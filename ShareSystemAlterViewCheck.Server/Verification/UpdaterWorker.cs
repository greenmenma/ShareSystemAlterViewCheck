using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace ShareSystemAlterViewCheck.Server.Verification;

/// <summary>
/// データ公開システムの更新処理を再現するセッション
/// ・全件更新（テーブル公開）: 新テーブル作成 → BulkCopy → DROP 公開テーブル → RENAME
/// ・全件更新（ビュー公開）  : 非アクティブ側を空にする → BulkCopy → ALTER VIEW
/// ・全件更新（シノニム公開）: 非アクティブ側を空にする → BulkCopy → DROP/CREATE SYNONYM
/// ・差分更新                : 差分テーブル作成 → BulkCopy → MERGE
/// </summary>
public sealed class UpdaterWorker(RunContext ctx, string connectionString, RunRequest req)
{
    public const string Actor = "Updater";

    public List<UpdateResult> Results { get; } = [];

    public async Task RunAsync(CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        for (var i = 1; i <= Math.Max(1, req.UpdateCount); i++)
        {
            var startAt = TimeSpan.FromSeconds(req.UpdaterStartDelaySec + (i - 1) * req.UpdateIntervalSec);
            var wait = startAt - started.Elapsed;
            if (wait > TimeSpan.Zero)
            {
                var waitPhase = ctx.BeginPhase(Actor, "開始待ち", "Wait");
                try
                {
                    await Task.Delay(wait, ct);
                }
                catch (OperationCanceledException)
                {
                    ctx.EndPhase(waitPhase, "Cancelled");
                    return;
                }
                ctx.EndPhase(waitPhase);
            }

            var result = new UpdateResult { Index = i };
            Results.Add(result);
            await RunOnceAsync(result, ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task RunOnceAsync(UpdateResult result, CancellationToken ct)
    {
        var label = req.UpdateCount > 1 ? $"更新{result.Index}" : "更新";
        var total = Stopwatch.StartNew();
        ctx.Info(Actor, $"{label}開始: {ModeText(req.Mode)} / {(req.UpdateType == UpdateType.Full ? "全件更新" : "差分更新")}");

        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct);
            var spid = await ReaderWorker.GetSpidAsync(conn, ct);
            using var session = ctx.Track(spid, Actor);
            ctx.Info(Actor, $"接続しました SPID={spid}");

            if (req.UpdateType == UpdateType.Diff)
            {
                await DiffAsync(conn, result, label, ct);
            }
            else if (req.Mode == PublishMode.Table)
            {
                await FullTableAsync(conn, result, label, ct);
            }
            else
            {
                await FullBlueGreenAsync(conn, result, label, ct);
            }

            result.Success = true;
            result.Result = "成功";
            result.TotalMs = total.ElapsedMilliseconds;
            ctx.Success(Actor, $"{label}完了: 合計 {result.TotalMs:N0}ms（切替/反映 {result.SwitchMs:N0}ms）");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result.Result = "キャンセル";
            result.TotalMs = total.ElapsedMilliseconds;
            ctx.Warn(Actor, $"{label}がキャンセルされました");
        }
        catch (SqlException ex)
        {
            result.TotalMs = total.ElapsedMilliseconds;
            result.Error = ex.Message;
            result.Result = ex.Number switch
            {
                -2 => "タイムアウト（CommandTimeout）",
                1222 => "ロックタイムアウト（LOCK_TIMEOUT）",
                1205 => "デッドロック",
                _ => $"SQLエラー {ex.Number}",
            };
            ctx.Error(Actor, $"{label}失敗: {result.Result}（{result.TotalMs:N0}ms 経過）", $"SqlException Number={ex.Number}\n{ex.Message}");
        }
        catch (Exception ex)
        {
            result.TotalMs = total.ElapsedMilliseconds;
            result.Error = ex.Message;
            result.Result = "エラー";
            ctx.Error(Actor, $"{label}失敗: {ex.Message}", ex.ToString());
        }
    }

    // ===== 全件更新：テーブル公開（現行方式） =====
    private async Task FullTableAsync(SqlConnection conn, UpdateResult result, string label, CancellationToken ct)
    {
        result.FromTable = DbObjects.PublicTable;
        result.ToTable = DbObjects.PublicTableNew;

        var prepareSql = $"""
            IF OBJECT_ID(N'{DbObjects.Q(DbObjects.PublicTableNew)}', N'U') IS NOT NULL DROP TABLE {DbObjects.Q(DbObjects.PublicTableNew)};
            {DbObjects.CreateDataTableSql(DbObjects.PublicTableNew)}
            """;
        result.PrepareMs = await ExecPhaseAsync(conn, $"{label}: 新テーブル作成", prepareSql, ct);

        result.RowsCopied = await BulkCopyPhaseAsync($"{label}: BulkCopy → {DbObjects.PublicTableNew}",
            $"SELECT {DbObjects.Columns} FROM {DbObjects.Q(DbObjects.Source)}", DbObjects.PublicTableNew, result, ct);

        var switchSql = $"""
            {LockTimeoutSql()}SET XACT_ABORT ON;
            BEGIN TRAN;
                DROP TABLE {DbObjects.Q(DbObjects.PublicTable)};
                EXEC sp_rename N'{DbObjects.Q(DbObjects.PublicTableNew)}', N'{DbObjects.PublicTable}';
            COMMIT;
            """;
        result.SwitchMs = await ExecPhaseAsync(conn, $"{label}: 切替 DROP + RENAME", switchSql, ct, isSwitch: true);
    }

    // ===== 全件更新：Blue/Green（ビュー/シノニム） =====
    private async Task FullBlueGreenAsync(SqlConnection conn, UpdateResult result, string label, CancellationToken ct)
    {
        var (blue, green) = DbObjects.BlueGreenOf(req.Mode);
        var active = await DbObjects.GetActiveTableAsync(conn, req.Mode, ct)
                     ?? throw new InvalidOperationException("公開オブジェクトの参照先を取得できません。環境セットアップを実行してください。");
        var inactive = string.Equals(active, blue, StringComparison.OrdinalIgnoreCase) ? green : blue;
        result.FromTable = active;
        result.ToTable = inactive;
        ctx.Info(Actor, $"現在のアクティブ: {active} → 更新対象（非アクティブ）: {inactive}");

        var q = DbObjects.Q(inactive);
        var prepareSql = req.InactivePrep switch
        {
            InactivePrep.Truncate => $"{LockTimeoutSql()}TRUNCATE TABLE {q};",
            InactivePrep.Delete => $"{LockTimeoutSql()}DELETE FROM {q};",
            _ => $"""
                 {LockTimeoutSql()}IF OBJECT_ID(N'{q}', N'U') IS NOT NULL DROP TABLE {q};
                 {DbObjects.CreateDataTableSql(inactive)}
                 """,
        };
        result.PrepareMs = await ExecPhaseAsync(conn, $"{label}: 非アクティブ側を空にする ({req.InactivePrep})", prepareSql, ct, isSwitch: true);

        result.RowsCopied = await BulkCopyPhaseAsync($"{label}: BulkCopy → {inactive}",
            $"SELECT {DbObjects.Columns} FROM {DbObjects.Q(DbObjects.Source)}", inactive, result, ct);

        string switchSql;
        string switchName;
        if (req.Mode == PublishMode.View)
        {
            switchName = $"{label}: 切替 ALTER VIEW → {inactive}";
            switchSql = $"{LockTimeoutSql()}{DbObjects.AlterViewSql(inactive)}";
        }
        else
        {
            switchName = $"{label}: 切替 SYNONYM → {inactive}";
            switchSql = $"""
                {LockTimeoutSql()}SET XACT_ABORT ON;
                BEGIN TRAN;
                    DROP SYNONYM {DbObjects.Q(DbObjects.PublicSynonym)};
                    CREATE SYNONYM {DbObjects.Q(DbObjects.PublicSynonym)} FOR {q};
                COMMIT;
                """;
        }
        result.SwitchMs = await ExecPhaseAsync(conn, switchName, switchSql, ct, isSwitch: true);
    }

    // ===== 差分更新 =====
    private async Task DiffAsync(SqlConnection conn, UpdateResult result, string label, CancellationToken ct)
    {
        var target = await DbObjects.GetActiveTableAsync(conn, req.Mode, ct)
                     ?? throw new InvalidOperationException("公開オブジェクトの参照先を取得できません。環境セットアップを実行してください。");
        result.FromTable = DbObjects.DiffStage;
        result.ToTable = target;

        var prepareSql = $"""
            IF OBJECT_ID(N'{DbObjects.Q(DbObjects.DiffStage)}', N'U') IS NOT NULL DROP TABLE {DbObjects.Q(DbObjects.DiffStage)};
            {DbObjects.CreateDataTableSql(DbObjects.DiffStage)}
            """;
        result.PrepareMs = await ExecPhaseAsync(conn, $"{label}: 差分テーブル作成", prepareSql, ct);

        var updates = Math.Max(0, req.DiffRowCount / 2);
        var inserts = Math.Max(0, req.DiffRowCount - updates);
        // 半分は既存行の更新、半分は新規行の追加
        var diffSql = $"""
            SELECT Id, Code, Name, Amount + 1 AS Amount, SYSDATETIME() AS UpdatedAt, Payload
            FROM (SELECT TOP ({updates}) {DbObjects.Columns} FROM {DbObjects.Q(DbObjects.Source)} ORDER BY NEWID()) u
            UNION ALL
            SELECT m.MaxId + n.k, CONCAT(N'N', m.MaxId + n.k), CONCAT(N'追加', m.MaxId + n.k), 0, SYSDATETIME(), N'diff'
            FROM (SELECT ISNULL(MAX(Id), 0) AS MaxId FROM {DbObjects.Q(target)} WITH (NOLOCK)) m
            CROSS JOIN (SELECT TOP ({inserts}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS k
                        FROM sys.all_objects a CROSS JOIN sys.all_objects b) n
            """;
        result.RowsCopied = await BulkCopyPhaseAsync($"{label}: BulkCopy → {DbObjects.DiffStage}", diffSql, DbObjects.DiffStage, result, ct);

        var mergeSql = $"""
            {LockTimeoutSql()}MERGE {DbObjects.Q(target)} AS t
            USING {DbObjects.Q(DbObjects.DiffStage)} AS s ON t.Id = s.Id
            WHEN MATCHED THEN
                UPDATE SET Code = s.Code, Name = s.Name, Amount = s.Amount, UpdatedAt = s.UpdatedAt, Payload = s.Payload
            WHEN NOT MATCHED BY TARGET THEN
                INSERT ({DbObjects.Columns}) VALUES (s.Id, s.Code, s.Name, s.Amount, s.UpdatedAt, s.Payload);
            """;
        result.SwitchMs = await ExecPhaseAsync(conn, $"{label}: MERGE → {target}", mergeSql, ct, isSwitch: true);
    }

    private string LockTimeoutSql() => req.LockTimeoutMs >= 0 ? $"SET LOCK_TIMEOUT {req.LockTimeoutMs};\n" : "";

    /// <summary>SQL を 1 フェーズとして実行し、所要時間を返す。失敗時はトランザクションを確実にロールバックする</summary>
    private async Task<long> ExecPhaseAsync(SqlConnection conn, string name, string sql, CancellationToken ct, bool isSwitch = false)
    {
        ctx.Sql(Actor, $"{name} 開始{(isSwitch ? $"（CommandTimeout={req.CommandTimeoutSec}秒" + (req.LockTimeoutMs >= 0 ? $", LOCK_TIMEOUT={req.LockTimeoutMs}ms" : "") + "）" : "")}", sql);
        var phase = ctx.BeginPhase(Actor, name);
        try
        {
            await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = isSwitch ? req.CommandTimeoutSec : 600 };
            await cmd.ExecuteNonQueryAsync(ct);
            var ms = ctx.EndPhase(phase);
            ctx.Info(Actor, $"{name} 完了（{ms:N0}ms）");
            return ms;
        }
        catch (SqlException ex)
        {
            ctx.EndPhase(phase, ex.Number is -2 or 1222 ? "Timeout" : "Error", ex.Message);
            await RollbackQuietlyAsync(conn);
            throw;
        }
        catch (OperationCanceledException)
        {
            ctx.EndPhase(phase, "Cancelled");
            await RollbackQuietlyAsync(conn);
            throw;
        }
    }

    private async Task<long> BulkCopyPhaseAsync(string name, string sourceSql, string destination, UpdateResult result, CancellationToken ct)
    {
        ctx.Sql(Actor, $"{name} 開始", $"-- 取得元\n{sourceSql}\n-- 書込先\n{DbObjects.Q(destination)}");
        var phase = ctx.BeginPhase(Actor, name);
        var sw = Stopwatch.StartNew();
        try
        {
            await using var src = new SqlConnection(connectionString);
            await src.OpenAsync(ct);
            using var srcSession = ctx.Track(await ReaderWorker.GetSpidAsync(src, ct), Actor + "(取得元)");
            await using var cmd = new SqlCommand(sourceSql, src) { CommandTimeout = 600 };
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            // 書込側の接続も監視で識別できるよう SPID を登録する
            await using var dest = new SqlConnection(connectionString);
            await dest.OpenAsync(ct);
            using var destSession = ctx.Track(await ReaderWorker.GetSpidAsync(dest, ct), Actor);

            using var bulk = new SqlBulkCopy(dest, SqlBulkCopyOptions.TableLock, null)
            {
                DestinationTableName = DbObjects.Q(destination),
                BatchSize = 10_000,
                BulkCopyTimeout = 600,
                EnableStreaming = true,
                NotifyAfter = 50_000,
            };
            long copied = 0;
            bulk.SqlRowsCopied += (_, e) => copied = e.RowsCopied;
            await bulk.WriteToServerAsync(reader, ct);
            copied = Math.Max(copied, bulk.RowsCopied);

            var ms = ctx.EndPhase(phase);
            result.BulkCopyMs = (result.BulkCopyMs ?? 0) + ms;
            ctx.Info(Actor, $"{name} 完了（{copied:N0}行 / {sw.ElapsedMilliseconds:N0}ms）");
            return copied;
        }
        catch (SqlException ex)
        {
            ctx.EndPhase(phase, ex.Number is -2 or 1222 ? "Timeout" : "Error", ex.Message);
            throw;
        }
        catch (OperationCanceledException)
        {
            ctx.EndPhase(phase, "Cancelled");
            throw;
        }
    }

    private static async Task RollbackQuietlyAsync(SqlConnection conn)
    {
        try
        {
            if (conn.State == System.Data.ConnectionState.Open)
            {
                await using var cmd = new SqlCommand("IF @@TRANCOUNT > 0 ROLLBACK;", conn) { CommandTimeout = 30 };
                await cmd.ExecuteNonQueryAsync();
            }
        }
        catch
        {
            // ロールバック失敗時は接続破棄に任せる
        }
    }

    public static string ModeText(PublishMode mode) => mode switch
    {
        PublishMode.Table => "テーブル公開（現行: DROP+RENAME）",
        PublishMode.View => "ビュー公開（改修後: ALTER VIEW）",
        PublishMode.Synonym => "シノニム公開（参考）",
        _ => mode.ToString(),
    };
}

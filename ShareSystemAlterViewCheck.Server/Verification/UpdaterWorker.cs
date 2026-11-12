using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace ShareSystemAlterViewCheck.Server.Verification;

/// <summary>
/// データ公開システムの更新処理を再現するセッション
/// ・全件更新（テーブル公開）: 新テーブル作成 → BulkCopy → DROP 公開テーブル → RENAME
/// ・全件更新（ビュー公開）  : 非アクティブ側を空にする → BulkCopy → ALTER VIEW
/// ・全件更新（シノニム公開）: 非アクティブ側を空にする → BulkCopy → DROP/CREATE SYNONYM
/// ・全件更新（SWITCH）      : 投入先へ BulkCopy → ALTER TABLE SWITCH で公開テーブルと入れ替え（低優先度待機を指定可）
/// ・全件更新（制御テーブル）: 非アクティブ側を空にする → BulkCopy → 制御テーブルを UPDATE（DDL なし）
/// ・差分更新                : 差分テーブル作成 → BulkCopy → MERGE
/// ・全件同期                : 全件をステージングへ BulkCopy → MERGE（INSERT/UPDATE/DELETE）で公開テーブルへ反映
/// </summary>
public sealed class UpdaterWorker(RunContext ctx, string connectionString, RunRequest req)
{
    public const string Actor = "Updater";

    public List<UpdateResult> Results { get; } = [];

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await RunAllAsync(ct);
        }
        finally
        {
            // 発生しなかった切替のシグナルを完了させ、待っている読み取りを解放する
            ctx.CompleteAllSwitches();
        }
    }

    private async Task RunAllAsync(CancellationToken ct)
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
        ctx.Info(Actor, $"{label}開始: {ModeText(req.Mode)} / {UpdateTypeText(req.UpdateType)}");

        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct);
            var spid = await ReaderWorker.GetSpidAsync(conn, ct);
            using var session = ctx.Track(spid, Actor);
            ctx.Info(Actor, $"接続しました SPID={spid}");

            switch (req.UpdateType, req.Mode)
            {
                case (UpdateType.Diff, _):
                    await DiffAsync(conn, result, label, ct);
                    break;
                case (UpdateType.FullSync, _):
                    await FullSyncAsync(conn, result, label, ct);
                    break;
                case (_, PublishMode.Table):
                    await FullTableAsync(conn, result, label, ct);
                    break;
                case (_, PublishMode.Switch):
                    await FullSwitchAsync(conn, result, label, ct);
                    break;
                case (_, PublishMode.Control):
                    await FullControlAsync(conn, result, label, ct);
                    break;
                default:
                    await FullBlueGreenAsync(conn, result, label, ct);
                    break;
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
        result.SwitchMs = await SwitchPhaseAsync(conn, result, $"{label}: 切替 DROP + RENAME", switchSql, ct);
    }

    // ===== 全件更新：Blue/Green（ビュー/シノニム） =====
    private async Task FullBlueGreenAsync(SqlConnection conn, UpdateResult result, string label, CancellationToken ct)
    {
        var (active, inactive) = await PrepareInactiveAsync(conn, result, label, ct);
        var q = DbObjects.Q(inactive);

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
        result.SwitchMs = await SwitchPhaseAsync(conn, result, switchName, switchSql, ct);
    }

    /// <summary>Blue/Green 共通：非アクティブ側を空にして連携元から BulkCopy する</summary>
    private async Task<(string Active, string Inactive)> PrepareInactiveAsync(SqlConnection conn, UpdateResult result, string label, CancellationToken ct)
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
        return (active, inactive);
    }

    // ===== 全件更新：ALTER TABLE SWITCH（代替案） =====
    private async Task FullSwitchAsync(SqlConnection conn, UpdateResult result, string label, CancellationToken ct)
    {
        result.FromTable = DbObjects.SwStage;
        result.ToTable = DbObjects.SwPublic;

        // 投入先・退避先は読み取りから参照されないので、TRUNCATE してもブロックされない
        var prepareSql = $"""
            TRUNCATE TABLE {DbObjects.Q(DbObjects.SwStage)};
            TRUNCATE TABLE {DbObjects.Q(DbObjects.SwOld)};
            """;
        result.PrepareMs = await ExecPhaseAsync(conn, $"{label}: 投入先・退避先を空にする", prepareSql, ct);

        result.RowsCopied = await BulkCopyPhaseAsync($"{label}: BulkCopy → {DbObjects.SwStage}",
            $"SELECT {DbObjects.Columns} FROM {DbObjects.Q(DbObjects.Source)}", DbObjects.SwStage, result, ct);

        // 公開テーブル → 退避先、投入先 → 公開テーブルの順に SWITCH（どちらもメタデータ操作のみで一瞬）
        var lowPriority = req.SwitchLowPriority
            ? $" WITH (WAIT_AT_LOW_PRIORITY (MAX_DURATION = {req.LowPriorityMaxMinutes} MINUTES, ABORT_AFTER_WAIT = {req.LowPriorityAbort.ToString().ToUpperInvariant()}))"
            : "";
        var switchSql = $"""
            {LockTimeoutSql()}SET XACT_ABORT ON;
            BEGIN TRAN;
                ALTER TABLE {DbObjects.Q(DbObjects.SwPublic)} SWITCH TO {DbObjects.Q(DbObjects.SwOld)}{lowPriority};
                ALTER TABLE {DbObjects.Q(DbObjects.SwStage)} SWITCH TO {DbObjects.Q(DbObjects.SwPublic)};
            COMMIT;
            """;

        // 低優先度の待機中に CommandTimeout で打ち切られないよう、待機時間 + 30 秒は待つ
        int? timeout = null;
        if (req.SwitchLowPriority && req.CommandTimeoutSec > 0 && req.CommandTimeoutSec < req.LowPriorityMaxMinutes * 60 + 30)
        {
            timeout = req.LowPriorityMaxMinutes * 60 + 30;
            ctx.Info(Actor, $"低優先度の待機（最大 {req.LowPriorityMaxMinutes} 分）を確認するため、切替の CommandTimeout を {timeout} 秒にします");
        }
        var name = $"{label}: 切替 ALTER TABLE SWITCH{(req.SwitchLowPriority ? $"（低優先度 {req.LowPriorityMaxMinutes}分 / {req.LowPriorityAbort.ToString().ToUpperInvariant()}）" : "")}";
        result.SwitchMs = await SwitchPhaseAsync(conn, result, name, switchSql, ct, timeout);
    }

    // ===== 全件更新：制御テーブル切替（代替案） =====
    private async Task FullControlAsync(SqlConnection conn, UpdateResult result, string label, CancellationToken ct)
    {
        var (_, inactive) = await PrepareInactiveAsync(conn, result, label, ct);
        var side = inactive == DbObjects.CtlA ? "A" : "B";
        // ビュー定義は変えず、制御テーブルの値（DML）だけで参照先を切り替える
        var switchSql = $"{LockTimeoutSql()}UPDATE {DbObjects.Q(DbObjects.CtlActive)} SET ActiveSide = '{side}' WHERE Id = 1;";
        result.SwitchMs = await SwitchPhaseAsync(conn, result, $"{label}: 切替 UPDATE 制御テーブル → {inactive}", switchSql, ct);
    }

    // ===== 全件同期（テーブルを作り直さず MERGE で反映） =====
    private async Task FullSyncAsync(SqlConnection conn, UpdateResult result, string label, CancellationToken ct)
    {
        var target = await DbObjects.GetActiveTableAsync(conn, req.Mode, ct)
                     ?? throw new InvalidOperationException("公開オブジェクトの参照先を取得できません。環境セットアップを実行してください。");
        result.FromTable = DbObjects.DiffStage;
        result.ToTable = target;

        var prepareSql = $"""
            IF OBJECT_ID(N'{DbObjects.Q(DbObjects.DiffStage)}', N'U') IS NOT NULL DROP TABLE {DbObjects.Q(DbObjects.DiffStage)};
            {DbObjects.CreateDataTableSql(DbObjects.DiffStage)}
            """;
        result.PrepareMs = await ExecPhaseAsync(conn, $"{label}: ステージング作成", prepareSql, ct);

        // 連携元の全件を取得。変化を作るため 1 割の行は金額・更新日時を変える（毎回違う行）
        var mod = 10;
        var pick = (result.Index + Environment.TickCount) % mod;
        var fullSql = $"""
            SELECT Id, Code, Name,
                   CASE WHEN Id % {mod} = {pick} THEN Amount + 1 ELSE Amount END AS Amount,
                   CASE WHEN Id % {mod} = {pick} THEN SYSDATETIME() ELSE UpdatedAt END AS UpdatedAt,
                   Payload
            FROM {DbObjects.Q(DbObjects.Source)}
            """;
        result.RowsCopied = await BulkCopyPhaseAsync($"{label}: BulkCopy（全件）→ {DbObjects.DiffStage}", fullSql, DbObjects.DiffStage, result, ct);

        // 変わった行だけ UPDATE、無い行を INSERT、連携元から消えた行を DELETE（公開テーブル自体は作り直さない）
        var mergeSql = $"""
            {LockTimeoutSql()}MERGE {DbObjects.Q(target)} AS t
            USING {DbObjects.Q(DbObjects.DiffStage)} AS s ON t.Id = s.Id
            WHEN MATCHED AND (t.Code <> s.Code OR t.Name <> s.Name OR t.Amount <> s.Amount OR t.UpdatedAt <> s.UpdatedAt OR t.Payload <> s.Payload) THEN
                UPDATE SET Code = s.Code, Name = s.Name, Amount = s.Amount, UpdatedAt = s.UpdatedAt, Payload = s.Payload
            WHEN NOT MATCHED BY TARGET THEN
                INSERT ({DbObjects.Columns}) VALUES (s.Id, s.Code, s.Name, s.Amount, s.UpdatedAt, s.Payload)
            WHEN NOT MATCHED BY SOURCE THEN
                DELETE
            OUTPUT $action;
            """;
        result.SwitchMs = await SwitchPhaseAsync(conn, result, $"{label}: 全件同期 MERGE → {target}", mergeSql, ct);
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
        result.SwitchMs = await SwitchPhaseAsync(conn, result, $"{label}: MERGE → {target}", mergeSql, ct);
    }

    /// <summary>
    /// 切替（DROP+RENAME / ALTER VIEW / SYNONYM / MERGE）を実行する。
    /// ・「切替の N 秒前」に開始する読み取りがあれば、読み取り中になってから N 秒待って実行する
    /// ・切替がロック待機になったら「切替待機中」に開始する読み取りへ通知する
    /// </summary>
    private async Task<long> SwitchPhaseAsync(SqlConnection conn, UpdateResult result, string name, string sql, CancellationToken ct, int? timeoutSec = null)
    {
        var sig = ctx.SwitchOf(result.Index);
        sig.Ready.TrySetResult(true);

        var readers = sig.BeforeSwitchReaders();
        if (readers.Count > 0)
        {
            var names = string.Join(", ", readers.Select(r => r.Actor));
            var leadSec = readers.Max(r => r.LeadSec);
            var waitPhase = ctx.BeginPhase(Actor, $"読み取りの開始待ち（{names}）", "Wait");
            ctx.Info(Actor, $"切替の前に読み取り（{names}）が実行中になるのを待っています");
            try
            {
                await Task.WhenAll(readers.Select(r => r.Started.Task)).WaitAsync(TimeSpan.FromSeconds(120), ct);
                ctx.EndPhase(waitPhase);
            }
            catch (TimeoutException)
            {
                ctx.EndPhase(waitPhase, "Timeout");
                ctx.Warn(Actor, "読み取りが 120 秒たっても開始しないため、そのまま切替を実行します");
            }
            // 読み取り側が確実にロック（Sch-S / S）を保持している状態にしてから切替える。
            // 読み取り開始から N 秒（最低 0.5 秒）たってから切替えることで、「読み取りの途中で切替が来る」状況にする
            if (leadSec > 0)
            {
                var leadPhase = ctx.BeginPhase(Actor, $"切替まで {leadSec} 秒待機（読み取り中）", "Wait");
                ctx.Info(Actor, $"読み取りが実行中になりました。{leadSec} 秒後に切替を実行します");
                await Task.Delay(TimeSpan.FromSeconds(leadSec), ct);
                ctx.EndPhase(leadPhase);
            }
            else
            {
                await Task.Delay(500, ct);
            }
            ctx.Info(Actor, "読み取りが実行中なので切替を実行します");
        }

        ctx.ActiveSwitchIndex = result.Index;
        result.SwitchStartMs = ctx.ElapsedMs;
        try
        {
            var exec = ExecPhaseAsync(conn, name, sql, ct, isSwitch: true, timeoutSec);
            sig.Started.TrySetResult(true);
            // ロック監視で検出できない場合（VIEW SERVER STATE がない等）の保険：3 秒たっても終わらなければ待機中とみなす
            var first = await Task.WhenAny(exec, Task.Delay(3000, ct));
            if (first != exec && !ct.IsCancellationRequested && sig.Blocked.TrySetResult(true))
            {
                ctx.Warn(Actor, "切替が 3 秒以上終わらないため、ロック待機中とみなします（ロック監視では未検出）");
            }
            return await exec;
        }
        finally
        {
            result.SwitchEndMs = ctx.ElapsedMs;
            ctx.ActiveSwitchIndex = 0;
            sig.Blocked.TrySetResult(false);
            sig.Started.TrySetResult(false);
        }
    }

    private string LockTimeoutSql() => req.LockTimeoutMs >= 0 ? $"SET LOCK_TIMEOUT {req.LockTimeoutMs};\n" : "";

    /// <summary>SQL を 1 フェーズとして実行し、所要時間を返す。失敗時はトランザクションを確実にロールバックする</summary>
    private async Task<long> ExecPhaseAsync(SqlConnection conn, string name, string sql, CancellationToken ct, bool isSwitch = false, int? timeoutSec = null)
    {
        var commandTimeout = timeoutSec ?? (isSwitch ? req.CommandTimeoutSec : 600);
        ctx.Sql(Actor, $"{name} 開始{(isSwitch ? $"（CommandTimeout={commandTimeout}秒" + (req.LockTimeoutMs >= 0 ? $", LOCK_TIMEOUT={req.LockTimeoutMs}ms" : "") + "）" : "")}", sql);
        var phase = ctx.BeginPhase(Actor, name);
        try
        {
            await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = commandTimeout };
            if (sql.Contains("OUTPUT $action", StringComparison.Ordinal))
            {
                // MERGE の OUTPUT から INSERT/UPDATE/DELETE の件数を集計してログに出す
                var counts = new Dictionary<string, long>();
                await using (var reader = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await reader.ReadAsync(ct))
                    {
                        var action = reader.GetString(0);
                        counts[action] = counts.GetValueOrDefault(action) + 1;
                    }
                }
                var msMerge = ctx.EndPhase(phase);
                var detail = string.Join(" / ", new[] { "INSERT", "UPDATE", "DELETE" }.Select(a => $"{a} {counts.GetValueOrDefault(a):N0}行"));
                ctx.Info(Actor, $"{name} 完了（{msMerge:N0}ms）: {detail}");
                return msMerge;
            }
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
        PublishMode.Switch => "テーブル公開 + ALTER TABLE SWITCH（代替案）",
        PublishMode.Control => "制御テーブル切替ビュー（代替案: DDL なし）",
        _ => mode.ToString(),
    };

    public static string UpdateTypeText(UpdateType type) => type switch
    {
        UpdateType.Full => "全件更新（入替）",
        UpdateType.Diff => "差分更新（MERGE）",
        UpdateType.FullSync => "全件同期（MERGE で INSERT/UPDATE/DELETE）",
        _ => type.ToString(),
    };
}

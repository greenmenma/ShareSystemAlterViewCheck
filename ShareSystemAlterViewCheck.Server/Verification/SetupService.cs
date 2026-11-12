using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace ShareSystemAlterViewCheck.Server.Verification;

/// <summary>検証用オブジェクトの作成・削除・状態取得</summary>
public sealed class SetupService(RunManager manager)
{
    private const string Actor = "Setup";

    private static readonly (string Name, string Role)[] Catalog =
    [
        (DbObjects.Source, "連携元データ（BulkCopy の取得元）"),
        (DbObjects.PublicTable, "現行：公開テーブル"),
        (DbObjects.PublicTableNew, "現行：全件更新時の新テーブル（一時）"),
        (DbObjects.PublicView, "改修後：公開ビュー"),
        (DbObjects.ViewBlue, "改修後：Blue テーブル"),
        (DbObjects.ViewGreen, "改修後：Green テーブル"),
        (DbObjects.PublicSynonym, "参考：公開シノニム"),
        (DbObjects.SynBlue, "参考：シノニム Blue テーブル"),
        (DbObjects.SynGreen, "参考：シノニム Green テーブル"),
        (DbObjects.SwPublic, "代替案 SWITCH：公開テーブル"),
        (DbObjects.SwStage, "代替案 SWITCH：新データの投入先"),
        (DbObjects.SwOld, "代替案 SWITCH：旧データの退避先"),
        (DbObjects.CtlView, "代替案 制御テーブル切替：公開ビュー（定義は固定）"),
        (DbObjects.CtlActive, "代替案 制御テーブル切替：参照先を示す制御テーブル"),
        (DbObjects.CtlA, "代替案 制御テーブル切替：A テーブル"),
        (DbObjects.CtlB, "代替案 制御テーブル切替：B テーブル"),
        (DbObjects.DiffStage, "差分更新・全件同期のステージング（一時）"),
    ];

    public async Task<EnvironmentStatus> GetStatusAsync(CancellationToken ct)
    {
        var status = new EnvironmentStatus();
        var current = manager.Current;
        status.RunActive = current is not null;
        status.ActiveRunId = current?.Record.Id;

        string cs;
        try
        {
            cs = manager.ConnectionString;
            var b = new SqlConnectionStringBuilder(cs);
            status.DataSource = b.DataSource;
            status.Database = b.InitialCatalog;
            status.LoginInfo = b.IntegratedSecurity ? "Windows 認証" : $"SQL 認証 ({b.UserID})";
        }
        catch (Exception ex)
        {
            status.Error = ex.Message;
            return status;
        }

        try
        {
            await using var conn = new SqlConnection(cs);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await conn.OpenAsync(timeout.Token);
            status.Connected = true;

            // 検証中（切替の Sch-M 待ちなど）でも画面の状態取得が固まらないようにする
            await using (var init = new SqlCommand("SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED; SET LOCK_TIMEOUT 3000;", conn))
            {
                await init.ExecuteNonQueryAsync(ct);
            }

            await using (var cmd = new SqlCommand(
                """
                SELECT CONCAT(CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(40)), N' ', CAST(SERVERPROPERTY('Edition') AS nvarchar(100))),
                       HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW SERVER STATE'),
                       HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'ALTER')
                """, conn))
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                if (await reader.ReadAsync(ct))
                {
                    status.ServerVersion = reader.GetString(0);
                    status.HasViewServerState = !reader.IsDBNull(1) && reader.GetInt32(1) == 1;
                    status.CanAlterDatabase = !reader.IsDBNull(2) && reader.GetInt32(2) == 1;
                }
            }
            status.ReadCommittedSnapshot = await DbObjects.GetReadCommittedSnapshotAsync(conn, ct);

            foreach (var (name, role) in Catalog)
            {
                var info = new DbObjectInfo { Name = name, Role = role };
                await using (var cmd = new SqlCommand(
                    "SELECT o.type_desc FROM sys.objects o WHERE o.object_id = OBJECT_ID(@n)", conn))
                {
                    cmd.Parameters.AddWithValue("@n", DbObjects.Q(name));
                    info.Type = await cmd.ExecuteScalarAsync(ct) as string;
                    info.Exists = info.Type is not null;
                }
                if (info.Type == "USER_TABLE")
                {
                    await using var cmd = new SqlCommand(
                        "SELECT SUM(p.rows) FROM sys.partitions p WHERE p.object_id = OBJECT_ID(@n) AND p.index_id IN (0, 1)", conn);
                    cmd.Parameters.AddWithValue("@n", DbObjects.Q(name));
                    info.Rows = await cmd.ExecuteScalarAsync(ct) is long rows ? rows : null;
                }
                status.Objects.Add(info);
            }

            try
            {
                status.ActiveViewTable = await DbObjects.GetActiveViewTableAsync(conn, ct);
                status.ActiveSynonymTable = await DbObjects.GetActiveSynonymTableAsync(conn, ct);
                status.ActiveControlTable = await DbObjects.GetActiveControlTableAsync(conn, ct);
            }
            catch (SqlException ex) when (ex.Number == 1222)
            {
                // 切替処理のロック待ちと重なった場合は参照先を空のまま返す
            }
            var view = status.Objects.First(o => o.Name == DbObjects.PublicView);
            view.Definition = status.ActiveViewTable is null ? null : $"→ {status.ActiveViewTable}";
            var syn = status.Objects.First(o => o.Name == DbObjects.PublicSynonym);
            syn.Definition = status.ActiveSynonymTable is null ? null : $"→ {status.ActiveSynonymTable}";
            var ctl = status.Objects.First(o => o.Name == DbObjects.CtlView);
            ctl.Definition = status.ActiveControlTable is null ? null : $"→ {status.ActiveControlTable}（制御テーブルの値）";

            status.IsSetUp = status.Objects.Where(o =>
                    o.Name is DbObjects.Source or DbObjects.PublicTable or DbObjects.PublicView or DbObjects.PublicSynonym
                        or DbObjects.SwPublic or DbObjects.CtlView)
                .All(o => o.Exists);
        }
        catch (Exception ex)
        {
            status.Error = ex.Message;
        }
        return status;
    }

    /// <summary>検証用オブジェクトを作り直す（同期実行・ログを保存）</summary>
    public (RunContext? Ctx, string? Error) StartSetup(SetupRequest req)
    {
        var rows = Math.Clamp(req.RowCount, 1_000, 10_000_000);
        var ctx = manager.TryBegin(RunKind.Setup, $"環境セットアップ（{rows:N0}行）", null);
        if (ctx is null)
        {
            return (null, "検証の実行中はセットアップできません。");
        }
        _ = Task.Run(() => SetupAsync(ctx, rows, ctx.Cts.Token));
        return (ctx, null);
    }

    private async Task SetupAsync(RunContext ctx, int rows, CancellationToken ct)
    {
        var status = RunStatus.Completed;
        try
        {
            await using var conn = new SqlConnection(manager.ConnectionString);
            await conn.OpenAsync(ct);
            ctx.Info(Actor, $"接続しました: {conn.DataSource} / {conn.Database}");

            await ExecAsync(ctx, conn, "既存の検証用オブジェクトを削除", DropAllSql(), ct);
            await ExecAsync(ctx, conn, "連携元テーブル作成", DbObjects.CreateDataTableSql(DbObjects.Source), ct);
            await ExecAsync(ctx, conn, $"連携元データ生成（{rows:N0}行）", $"""
                ;WITH n AS (
                    SELECT TOP ({rows}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS Id
                    FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c
                )
                INSERT INTO {DbObjects.Q(DbObjects.Source)} WITH (TABLOCK) ({DbObjects.Columns})
                SELECT Id,
                       CONCAT(N'C', RIGHT(CONCAT(N'00000000', Id), 8)),
                       CONCAT(N'名称', Id),
                       CAST(Id % 100000 AS decimal(18, 2)) / 100,
                       SYSDATETIME(),
                       REPLICATE(N'X', 400)
                FROM n;
                """, ct);

            var copy = $"SELECT {DbObjects.Columns} FROM {DbObjects.Q(DbObjects.Source)}";
            foreach (var table in new[]
                     {
                         DbObjects.PublicTable, DbObjects.ViewBlue, DbObjects.ViewGreen, DbObjects.SynBlue, DbObjects.SynGreen,
                         DbObjects.SwPublic, DbObjects.CtlA, DbObjects.CtlB,
                     })
            {
                await ExecAsync(ctx, conn, $"{table} 作成・データ投入", $"""
                    {DbObjects.CreateDataTableSql(table)}
                    INSERT INTO {DbObjects.Q(table)} WITH (TABLOCK) ({DbObjects.Columns}) {copy};
                    """, ct);
            }

            await ExecAsync(ctx, conn, "公開ビュー作成（→ Blue）", DbObjects.CreateViewSql(DbObjects.ViewBlue), ct);
            await ExecAsync(ctx, conn, "公開シノニム作成（→ Blue）",
                $"CREATE SYNONYM {DbObjects.Q(DbObjects.PublicSynonym)} FOR {DbObjects.Q(DbObjects.SynBlue)};", ct);

            // SWITCH 方式：投入先・退避先は公開テーブルと同じ構造（SWITCH の条件）で空にしておく
            await ExecAsync(ctx, conn, "SWITCH 方式の投入先・退避先テーブル作成",
                DbObjects.CreateDataTableSql(DbObjects.SwStage) + "\n" + DbObjects.CreateDataTableSql(DbObjects.SwOld), ct);

            await ExecAsync(ctx, conn, "制御テーブル作成（→ A）", DbObjects.CreateCtlActiveSql(), ct);
            await ExecAsync(ctx, conn, "制御テーブル切替の公開ビュー作成", DbObjects.CreateCtlViewSql(), ct);

            ctx.Success(Actor, "環境セットアップが完了しました");
            ctx.Mutate(r => r.Summary = new RunSummary { Verdict = $"セットアップ完了（{rows:N0}行）", VerdictLevel = "Success" });
        }
        catch (Exception ex)
        {
            status = RunStatus.Failed;
            ctx.Error(Actor, $"セットアップに失敗しました: {ex.Message}", ex.ToString());
            ctx.Mutate(r => r.Summary = new RunSummary { Verdict = "セットアップ失敗", VerdictLevel = "Error", ErrorCount = 1 });
        }
        finally
        {
            await manager.EndAsync(ctx, status);
        }
    }

    /// <summary>検証用オブジェクトをすべて削除する</summary>
    public (RunContext? Ctx, string? Error) StartDrop()
    {
        var ctx = manager.TryBegin(RunKind.Setup, "検証用オブジェクト削除", null);
        if (ctx is null)
        {
            return (null, "検証の実行中は削除できません。");
        }
        _ = Task.Run(() => DropAsync(ctx, ctx.Cts.Token));
        return (ctx, null);
    }

    private async Task DropAsync(RunContext ctx, CancellationToken ct)
    {
        var status = RunStatus.Completed;
        try
        {
            await using var conn = new SqlConnection(manager.ConnectionString);
            await conn.OpenAsync(ct);
            await ExecAsync(ctx, conn, "検証用オブジェクトを削除", DropAllSql(), ct);
            ctx.Mutate(r => r.Summary = new RunSummary { Verdict = "削除完了", VerdictLevel = "Success" });
        }
        catch (Exception ex)
        {
            status = RunStatus.Failed;
            ctx.Error(Actor, $"削除に失敗しました: {ex.Message}", ex.ToString());
            ctx.Mutate(r => r.Summary = new RunSummary { Verdict = "削除失敗", VerdictLevel = "Error", ErrorCount = 1 });
        }
        finally
        {
            await manager.EndAsync(ctx, status);
        }
    }

    /// <summary>READ_COMMITTED_SNAPSHOT（RCSI）を切り替える</summary>
    public (RunContext? Ctx, string? Error) StartSetRcsi(bool enabled)
    {
        var ctx = manager.TryBegin(RunKind.Setup, $"READ_COMMITTED_SNAPSHOT を {(enabled ? "ON" : "OFF")} にする", null);
        if (ctx is null)
        {
            return (null, "検証の実行中は変更できません。");
        }
        _ = Task.Run(() => SetRcsiAsync(ctx, enabled, ctx.Cts.Token));
        return (ctx, null);
    }

    private async Task SetRcsiAsync(RunContext ctx, bool enabled, CancellationToken ct)
    {
        var status = RunStatus.Completed;
        var onOff = enabled ? "ON" : "OFF";
        try
        {
            await using (var conn = new SqlConnection(manager.ConnectionString))
            {
                await conn.OpenAsync(ct);
                ctx.Info(Actor, $"接続しました: {conn.DataSource} / {conn.Database}");
                // RCSI の変更はこの DB に接続中の他セッションがあると待たされるため、ROLLBACK IMMEDIATE で切断する
                await ExecAsync(ctx, conn, $"READ_COMMITTED_SNAPSHOT {onOff}",
                    $"ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT {onOff} WITH ROLLBACK IMMEDIATE;", ct);
            }
            // 切断された接続がプールに残らないようにする
            SqlConnection.ClearAllPools();

            await using (var conn = new SqlConnection(manager.ConnectionString))
            {
                await conn.OpenAsync(ct);
                var now = await DbObjects.GetReadCommittedSnapshotAsync(conn, ct);
                ctx.Success(Actor, $"READ_COMMITTED_SNAPSHOT = {(now == true ? "ON" : "OFF")}");
            }
            ctx.Mutate(r => r.Summary = new RunSummary { Verdict = $"READ_COMMITTED_SNAPSHOT を {onOff} にしました", VerdictLevel = "Success" });
        }
        catch (Exception ex)
        {
            status = RunStatus.Failed;
            ctx.Error(Actor, $"READ_COMMITTED_SNAPSHOT を変更できませんでした: {ex.Message}", ex.ToString());
            ctx.Mutate(r => r.Summary = new RunSummary { Verdict = "RCSI の変更に失敗", VerdictLevel = "Error", ErrorCount = 1 });
        }
        finally
        {
            await manager.EndAsync(ctx, status);
        }
    }

    private static string DropAllSql() => $"""
        IF OBJECT_ID(N'{DbObjects.Q(DbObjects.PublicView)}', N'V') IS NOT NULL DROP VIEW {DbObjects.Q(DbObjects.PublicView)};
        IF OBJECT_ID(N'{DbObjects.Q(DbObjects.PublicSynonym)}', N'SN') IS NOT NULL DROP SYNONYM {DbObjects.Q(DbObjects.PublicSynonym)};
        IF OBJECT_ID(N'{DbObjects.Q(DbObjects.CtlView)}', N'V') IS NOT NULL DROP VIEW {DbObjects.Q(DbObjects.CtlView)};
        {string.Join("\n", new[]
        {
            DbObjects.PublicTable, DbObjects.PublicTableNew, DbObjects.ViewBlue, DbObjects.ViewGreen,
            DbObjects.SynBlue, DbObjects.SynGreen, DbObjects.DiffStage, DbObjects.Source,
            DbObjects.SwPublic, DbObjects.SwStage, DbObjects.SwOld, DbObjects.CtlActive, DbObjects.CtlA, DbObjects.CtlB,
        }.Select(t => $"IF OBJECT_ID(N'{DbObjects.Q(t)}', N'U') IS NOT NULL DROP TABLE {DbObjects.Q(t)};"))}
        """;

    private static async Task ExecAsync(RunContext ctx, SqlConnection conn, string name, string sql, CancellationToken ct)
    {
        ctx.Sql(Actor, $"{name} 開始", sql);
        var phase = ctx.BeginPhase(Actor, name);
        var sw = Stopwatch.StartNew();
        try
        {
            await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 1800 };
            await cmd.ExecuteNonQueryAsync(ct);
            ctx.EndPhase(phase);
            ctx.Info(Actor, $"{name} 完了（{sw.ElapsedMilliseconds:N0}ms）");
        }
        catch
        {
            ctx.EndPhase(phase, "Error");
            throw;
        }
    }
}

using System.Text;
using Microsoft.Data.SqlClient;

namespace ShareSystemAlterViewCheck.Server.Verification;

/// <summary>
/// 検証中に sys.dm_exec_requests を定期的に参照し、ブロッキングの開始/解消をログとタイムラインに記録する
/// </summary>
public sealed class LockMonitor(RunContext ctx, string connectionString, int intervalMs)
{
    public const string Actor = "Monitor";

    private sealed class BlockState
    {
        public int PhaseId;
        public long StartMs;
        public short BlockingSpid;
        public string WaitType = "";
        public int MaxWaitMs;
    }

    private readonly Dictionary<short, BlockState> _blocked = new();

    /// <summary>object_id → オブジェクト名（ロック待ちに巻き込まれないよう監視開始時に先読みする）</summary>
    private readonly Dictionary<long, string> _objectNames = new();

    public long MaxBlockedMs { get; private set; }
    public long TotalBlockedMs { get; private set; }
    public int BlockCount { get; private set; }

    private const string BlockingSql = """
        SELECT r.session_id, r.blocking_session_id, r.wait_type, r.wait_time, r.wait_resource,
               r.command, SUBSTRING(t.text, 1, 2000) AS sql_text
        FROM sys.dm_exec_requests r
        OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) t
        WHERE r.blocking_session_id <> 0
          AND r.database_id = DB_ID()
          AND r.session_id <> @@SPID
        """;

    private const string LockSql = """
        SELECT l.request_session_id, l.resource_type, l.request_mode, l.request_status,
               l.resource_associated_entity_id
        FROM sys.dm_tran_locks l
        WHERE l.resource_database_id = DB_ID()
          AND l.request_session_id IN (@a, @b)
          AND l.resource_type IN ('OBJECT', 'METADATA', 'HOBT')
        ORDER BY l.request_session_id, l.request_status DESC
        """;

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct);
            // 監視クエリ自体がメタデータのロック待ち（Sch-M 待機中のオブジェクト名解決など）に巻き込まれないようにする
            await using (var init = new SqlCommand("SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED; SET LOCK_TIMEOUT 2000;", conn))
            {
                await init.ExecuteNonQueryAsync(ct);
            }
            await CheckPermissionAsync(conn, ct);
            await LoadObjectNamesAsync(conn, ct);
            ctx.Info(Actor, $"ロック監視を開始しました（間隔 {intervalMs}ms）");

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await SampleAsync(conn, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    ctx.Warn(Actor, "監視クエリでエラーが発生しました", ex.Message);
                }

                try
                {
                    await Task.Delay(intervalMs, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ctx.Error(Actor, "ロック監視を開始できませんでした", ex.Message);
        }
        finally
        {
            // 監視終了時点でブロック中のものは閉じる
            foreach (var (spid, state) in _blocked)
            {
                CloseBlock(spid, state, "監視終了時点でブロック継続中");
            }
            _blocked.Clear();
            ctx.Info(Actor, "ロック監視を終了しました");
        }
    }

    private async Task LoadObjectNamesAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("SELECT object_id, name FROM sys.objects WHERE name LIKE N'vt[_]%'", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            _objectNames[reader.GetInt32(0)] = reader.GetString(1);
        }
    }

    /// <summary>"OBJECT: 5:1093578934:0" のような待機リソースにオブジェクト名を付与する</summary>
    private string DescribeResource(string resource)
    {
        var parts = resource.Replace("OBJECT:", "").Trim().Split(':');
        return parts.Length >= 2 && long.TryParse(parts[1], out var id) && _objectNames.TryGetValue(id, out var name)
            ? $"{resource}（{name}）"
            : resource;
    }

    private async Task CheckPermissionAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "SELECT HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW SERVER STATE')", conn);
        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is not int v || v != 1)
        {
            ctx.Warn(Actor, "VIEW SERVER STATE 権限がないため、他セッションのブロッキングを検出できない可能性があります",
                "GRANT VIEW SERVER STATE TO [ログイン名]; を実行してください。（SQL Server 2022 以降は VIEW SERVER PERFORMANCE STATE でも可）");
        }
    }

    private async Task SampleAsync(SqlConnection conn, CancellationToken ct)
    {
        var now = ctx.ElapsedMs;
        var current = new Dictionary<short, (short Blocking, string WaitType, int WaitMs, string Resource, string Command, string Sql)>();

        await using (var cmd = new SqlCommand(BlockingSql, conn) { CommandTimeout = 10 })
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                current[reader.GetInt16(0)] = (
                    reader.GetInt16(1),
                    reader.IsDBNull(2) ? "" : reader.GetString(2),
                    reader.GetInt32(3),
                    reader.IsDBNull(4) ? "" : reader.GetString(4),
                    reader.IsDBNull(5) ? "" : reader.GetString(5),
                    reader.IsDBNull(6) ? "" : reader.GetString(6));
            }
        }

        // 新たにブロックされたセッション
        foreach (var (spid, info) in current)
        {
            if (_blocked.TryGetValue(spid, out var state))
            {
                state.MaxWaitMs = Math.Max(state.MaxWaitMs, info.WaitMs);
                if (state.BlockingSpid != info.Blocking)
                {
                    ctx.Warn(Actor,
                        $"ブロック元が変化: {ctx.ActorOf(spid)}(SPID {spid}) ← {ctx.ActorOf(info.Blocking)}(SPID {info.Blocking})");
                    state.BlockingSpid = info.Blocking;
                }
                continue;
            }

            var victim = ctx.ActorOf(spid);
            var blocker = ctx.ActorOf(info.Blocking);
            var locks = await DescribeLocksAsync(conn, spid, info.Blocking, ct);
            var detail = new StringBuilder()
                .AppendLine($"待機セッション : {victim} (SPID {spid}) コマンド={info.Command}")
                .AppendLine($"ブロック元     : {blocker} (SPID {info.Blocking})")
                .AppendLine($"待機種別       : {info.WaitType}  待機時間={info.WaitMs}ms")
                .AppendLine($"待機リソース   : {DescribeResource(info.Resource)}")
                .AppendLine()
                .AppendLine("--- ロック状況 (sys.dm_tran_locks) ---")
                .AppendLine(locks)
                .AppendLine("--- 待機中の SQL ---")
                .AppendLine(info.Sql)
                .ToString();

            ctx.Warn(Actor, $"ブロック発生: {victim} が {blocker} の終了を {info.WaitType} で待機しています", detail);

            // 実際の待機開始は wait_time ぶん前
            var startMs = Math.Max(0, now - info.WaitMs);
            var phaseId = ctx.BeginPhase(victim, $"ロック待機 ({info.WaitType})", "Blocked", $"ブロック元: {blocker}");
            ctx.Mutate(r =>
            {
                var p = r.Phases.First(x => x.Id == phaseId);
                p.StartMs = startMs;
            });
            _blocked[spid] = new BlockState
            {
                PhaseId = phaseId,
                StartMs = startMs,
                BlockingSpid = info.Blocking,
                WaitType = info.WaitType,
                MaxWaitMs = info.WaitMs,
            };
            BlockCount++;
        }

        // ブロックが解消されたセッション
        foreach (var spid in _blocked.Keys.Where(k => !current.ContainsKey(k)).ToList())
        {
            CloseBlock(spid, _blocked[spid], null);
            _blocked.Remove(spid);
        }
    }

    private void CloseBlock(short spid, BlockState state, string? note)
    {
        var duration = ctx.EndPhase(state.PhaseId, "Ok", note);
        MaxBlockedMs = Math.Max(MaxBlockedMs, duration);
        TotalBlockedMs += duration;
        ctx.Info(Actor,
            $"ブロック解消: {ctx.ActorOf(spid)} の待機が終了しました（約 {duration:N0}ms）{(note is null ? "" : " ※" + note)}");
    }

    private string ResourceName(string type, long entityId) =>
        type == "OBJECT" && _objectNames.TryGetValue(entityId, out var name) ? name : entityId.ToString();

    private async Task<string> DescribeLocksAsync(SqlConnection conn, short a, short b, CancellationToken ct)
    {
        try
        {
            await using var cmd = new SqlCommand(LockSql, conn) { CommandTimeout = 10 };
            cmd.Parameters.AddWithValue("@a", a);
            cmd.Parameters.AddWithValue("@b", b);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var sb = new StringBuilder();
            while (await reader.ReadAsync(ct))
            {
                var spid = reader.GetInt32(0);
                sb.AppendLine(
                    $"{ctx.ActorOf((short)spid),-10} SPID {spid,-4} {reader.GetString(1),-9} {reader.GetString(2),-6} {reader.GetString(3),-7} {ResourceName(reader.GetString(1), reader.GetInt64(4))}");
            }
            return sb.Length == 0 ? "(取得できませんでした)" : sb.ToString();
        }
        catch (Exception ex)
        {
            return "(取得エラー: " + ex.Message + ")";
        }
    }
}

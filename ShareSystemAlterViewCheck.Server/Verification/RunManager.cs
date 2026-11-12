using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace ShareSystemAlterViewCheck.Server.Verification;

/// <summary>検証の実行管理（同時に 1 件のみ実行）</summary>
public sealed class RunManager(
    RunStore store,
    IConfiguration configuration,
    IOptions<VerificationOptions> options,
    JsonSerializerOptions json,
    ILogger<RunManager> logger)
{
    public const string SystemActor = "System";

    private readonly object _gate = new();
    private RunContext? _current;

    public RunContext? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public string ConnectionString =>
        configuration.GetConnectionString("VerifyDb")
        ?? throw new InvalidOperationException("接続文字列 ConnectionStrings:VerifyDb が設定されていません。");

    public JsonSerializerOptions Json => json;

    /// <summary>新しい実行コンテキストを作成する（実行中があれば null）</summary>
    public RunContext? TryBegin(RunKind kind, string title, RunRequest? request)
    {
        lock (_gate)
        {
            if (_current is not null)
            {
                return null;
            }
            var now = DateTimeOffset.Now;
            var record = new RunRecord
            {
                Id = $"{now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}",
                Kind = kind,
                Title = title,
                StartedAt = now,
                Request = request,
            };
            _current = new RunContext(record, logger);
            return _current;
        }
    }

    public async Task EndAsync(RunContext ctx, RunStatus status)
    {
        ctx.CloseOpenPhases(status == RunStatus.Completed ? "Ok" : "Cancelled");
        ctx.Mutate(r =>
        {
            r.Status = status;
            r.EndedAt = DateTimeOffset.Now;
        });
        await store.SaveAsync(ctx);
        lock (_gate)
        {
            if (_current == ctx)
            {
                _current = null;
            }
        }
    }

    public RunContext? Start(RunRequest req)
    {
        Normalize(req);
        var title = string.IsNullOrWhiteSpace(req.Title) ? DefaultTitle(req) : req.Title.Trim();
        var ctx = TryBegin(RunKind.Verification, title, req);
        if (ctx is null)
        {
            return null;
        }
        _ = Task.Run(() => ExecuteAsync(ctx, req));
        return ctx;
    }

    public bool Cancel(string id)
    {
        var ctx = Current;
        if (ctx is null || ctx.Record.Id != id)
        {
            return false;
        }
        ctx.Warn(SystemActor, "キャンセルが要求されました");
        ctx.Cts.Cancel();
        return true;
    }

    private async Task ExecuteAsync(RunContext ctx, RunRequest req)
    {
        var status = RunStatus.Completed;
        var ct = ctx.Cts.Token;
        ctx.Cts.CancelAfter(TimeSpan.FromMinutes(options.Value.MaxRunMinutes));

        // 実行中も定期的にファイルへ保存（途中でアプリが落ちてもログが残るように）
        using var saveCts = new CancellationTokenSource();
        var saver = Task.Run(async () =>
        {
            while (!saveCts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(2000, saveCts.Token);
                    await store.SaveAsync(ctx);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "途中保存に失敗しました");
                }
            }
        });

        LockMonitor? monitor = null;
        UpdaterWorker? updater = null;
        var readers = new List<ReaderWorker>();
        try
        {
            ctx.Info(SystemActor, $"検証開始: {ctx.Record.Title}");
            ctx.Info(SystemActor, "検証条件", DescribeRequest(req));

            await using (var conn = new SqlConnection(ConnectionString))
            {
                await conn.OpenAsync(ct);
                await using var cmd = new SqlCommand(
                    "SELECT CONCAT(@@SERVERNAME, N' / ', DB_NAME(), N' / ', CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(40)), N' ', CAST(SERVERPROPERTY('Edition') AS nvarchar(100)))", conn);
                var info = (string?)await cmd.ExecuteScalarAsync(ct);
                ctx.Mutate(r => r.ServerInfo = info);
                ctx.Info(SystemActor, $"接続先: {info}");

                var rcsi = await DbObjects.GetReadCommittedSnapshotAsync(conn, ct);
                ctx.Mutate(r => r.ReadCommittedSnapshot = rcsi);
                ctx.Info(SystemActor, $"READ_COMMITTED_SNAPSHOT（RCSI）: {(rcsi == true ? "ON（読み取りは行バージョンを読み、S ロックを取らない）" : "OFF（読み取りは S ロックを取る）")}");
                if (rcsi != true && (req.Mode == PublishMode.Control || req.UpdateType is UpdateType.Diff or UpdateType.FullSync))
                {
                    ctx.Warn(SystemActor, "この方式は RCSI が ON の前提です。OFF のままだと、DML（UPDATE/DELETE/MERGE）と読み取りの S ロックが互いに待ち合います",
                        "「環境セットアップ」タブで READ_COMMITTED_SNAPSHOT を ON にできます。");
                }

                if (await DbObjects.GetActiveTableAsync(conn, req.Mode, ct) is null ||
                    !await ExistsAsync(conn, DbObjects.PublicObjectOf(req.Mode), ct))
                {
                    throw new InvalidOperationException("検証用オブジェクトがありません。先に「環境セットアップ」を実行してください。");
                }
                var active = await DbObjects.GetActiveTableAsync(conn, req.Mode, ct);
                if (req.Mode != PublishMode.Table)
                {
                    ctx.Info(SystemActor, $"開始時点の参照先: {DbObjects.PublicObjectOf(req.Mode)} → {active}");
                }
            }

            using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            monitor = new LockMonitor(ctx, ConnectionString, req.MonitorIntervalMs);
            var monitorTask = Task.Run(() => monitor.RunAsync(monitorCts.Token));

            // 監視が開始されてから各セッションを開始する
            await Task.Delay(300, ct);

            var tasks = new List<Task>();
            for (var i = 0; i < req.Readers.Count; i++)
            {
                var worker = new ReaderWorker(ctx, ConnectionString, req.Mode, req.Readers[i], $"Reader{i + 1}");
                readers.Add(worker);
                tasks.Add(Task.Run(() => worker.RunAsync(ct)));
            }

            updater = new UpdaterWorker(ctx, ConnectionString, req);
            tasks.Add(Task.Run(() => updater.RunAsync(ct)));

            await Task.WhenAll(tasks);

            // 最後のブロック解消を拾うため少し待ってから監視を止める
            await Task.Delay(Math.Max(req.MonitorIntervalMs * 2, 500), CancellationToken.None);
            monitorCts.Cancel();
            await monitorTask;

            if (ct.IsCancellationRequested)
            {
                status = RunStatus.Cancelled;
            }
        }
        catch (OperationCanceledException)
        {
            status = RunStatus.Cancelled;
            ctx.Warn(SystemActor, "検証がキャンセルされました");
        }
        catch (Exception ex)
        {
            status = RunStatus.Failed;
            ctx.Error(SystemActor, $"検証を実行できませんでした: {ex.Message}", ex.ToString());
        }
        finally
        {
            var summary = BuildSummary(ctx, monitor, updater, readers, status);
            ctx.Mutate(r => r.Summary = summary);
            var level = summary.VerdictLevel == "Success" ? "Success" : summary.VerdictLevel == "Error" ? "Error" : "Warn";
            ctx.Log(SystemActor, level, $"判定: {summary.Verdict}");
            ctx.Info(SystemActor, "検証終了");

            saveCts.Cancel();
            await saver;
            await EndAsync(ctx, status);
        }
    }

    private static async Task<bool> ExistsAsync(SqlConnection conn, string name, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("SELECT OBJECT_ID(@n)", conn);
        cmd.Parameters.AddWithValue("@n", DbObjects.Q(name));
        return await cmd.ExecuteScalarAsync(ct) is not DBNull and not null;
    }

    private static RunSummary BuildSummary(RunContext ctx, LockMonitor? monitor, UpdaterWorker? updater, List<ReaderWorker> readers, RunStatus status)
    {
        var phases = ctx.PhasesSnapshot();
        var blocked = phases.Where(p => p.Kind == "Blocked").ToList();
        var summary = new RunSummary
        {
            Updates = updater?.Results ?? [],
            Readers = readers.Select(r => r.Result).ToList(),
            BlockCount = blocked.Count,
            MaxBlockedMs = blocked.Count == 0 ? 0 : blocked.Max(p => (p.EndMs ?? p.StartMs) - p.StartMs),
            TotalBlockedMs = blocked.Sum(p => (p.EndMs ?? p.StartMs) - p.StartMs),
        };
        if (monitor is not null)
        {
            summary.MaxBlockedMs = Math.Max(summary.MaxBlockedMs, monitor.MaxBlockedMs);
        }
        summary.TimeoutCount = summary.Updates.Count(u => u.Result.Contains("タイムアウト"));
        summary.ErrorCount = summary.Updates.Count(u => !u.Success && !u.Result.Contains("タイムアウト") && u.Result != "キャンセル")
                             + summary.Readers.Count(r => !r.Success && r.Result != "キャンセル");

        var updaterBlocked = blocked.Where(p => p.Actor == UpdaterWorker.Actor).ToList();
        var updaterMaxBlocked = updaterBlocked.Count == 0 ? 0 : updaterBlocked.Max(p => (p.EndMs ?? p.StartMs) - p.StartMs);

        // 後から来た読み取りの待機（ロック監視の検出 + 最初の行が返るまでの時間）
        var readerBlocked = blocked.Where(p => p.Actor.StartsWith("Reader", StringComparison.Ordinal)).ToList();
        summary.ReaderBlockCount = readerBlocked.Count;
        summary.ReaderMaxBlockedMs = Math.Max(
            readerBlocked.Count == 0 ? 0 : readerBlocked.Max(p => (p.EndMs ?? p.StartMs) - p.StartMs),
            summary.Readers.Max(r => r.FirstRowMs) ?? 0);

        // 切替の開始時点で読み取りが実行中だったか（重なっていなければ検証として成立しない）
        var readerWork = phases.Where(p => p.Kind == "Work" && p.Actor.StartsWith("Reader", StringComparison.Ordinal)).ToList();
        var switches = summary.Updates.Where(u => u.SwitchStartMs.HasValue).ToList();
        summary.SwitchOverlapped = switches.Any(u => readerWork.Any(p =>
            p.StartMs <= u.SwitchStartMs!.Value && (p.EndMs ?? long.MaxValue) > u.SwitchStartMs.Value));

        var readerNote = summary.ReaderMaxBlockedMs >= 1000
            ? $"／切替待機中に開始した読み取りも最大 {summary.ReaderMaxBlockedMs:N0}ms 待機"
            : "";

        if (status == RunStatus.Failed)
        {
            summary.Verdict = "検証を実行できませんでした（ログを確認してください）";
            summary.VerdictLevel = "Error";
        }
        else if (status == RunStatus.Cancelled)
        {
            summary.Verdict = "キャンセルされました";
            summary.VerdictLevel = "Warn";
        }
        else if (summary.Readers.Count > 0 && switches.Count > 0 && !summary.SwitchOverlapped)
        {
            summary.Verdict = "検証不成立 — 切替の時点で実行中の読み取りがありませんでした（読み取りの開始タイミング・継続時間を見直してください）";
            summary.VerdictLevel = "Warn";
        }
        else if (summary.TimeoutCount > 0 && summary.Readers.Count > 1 && summary.ReaderMaxBlockedMs < 1000)
        {
            // 低優先度の SWITCH が待機時間切れで中止した場合など：更新は失敗したが、読み取りには影響していない
            summary.Verdict = $"更新は待機時間切れで中止（{summary.TimeoutCount}件）— 後から来た読み取りは待たされませんでした（更新はやり直しが必要）";
            summary.VerdictLevel = "Warn";
        }
        else if (summary.TimeoutCount > 0)
        {
            summary.Verdict = $"更新がタイムアウトしました（{summary.TimeoutCount}件）— ロック問題が発生{readerNote}";
            summary.VerdictLevel = "Error";
        }
        else if (summary.ErrorCount > 0)
        {
            summary.Verdict = $"エラーが発生しました（{summary.ErrorCount}件）{readerNote}";
            summary.VerdictLevel = "Error";
        }
        else if (updaterMaxBlocked >= 1000 && summary.ReaderMaxBlockedMs < 1000 && summary.Readers.Count > 1)
        {
            summary.Verdict = $"更新は読み取りの終了を待って成功（最大 {updaterMaxBlocked:N0}ms 待機）— 後から来た読み取りは待たされませんでした";
            summary.VerdictLevel = "Warn";
        }
        else if (updaterMaxBlocked >= 1000)
        {
            summary.Verdict = $"更新は成功したが、ロック待機が発生（最大 {updaterMaxBlocked:N0}ms）{readerNote}";
            summary.VerdictLevel = "Warn";
        }
        else if (summary.ReaderMaxBlockedMs >= 1000)
        {
            summary.Verdict = $"更新は成功したが、読み取りが待機（最大 {summary.ReaderMaxBlockedMs:N0}ms）";
            summary.VerdictLevel = "Warn";
        }
        else
        {
            summary.Verdict = updaterBlocked.Count == 0
                ? "更新はロック待機なしで成功 — ロック問題は発生しませんでした"
                : $"更新は成功（ごく短いロック待機 {updaterMaxBlocked:N0}ms のみ）";
            summary.VerdictLevel = "Success";
        }
        return summary;
    }

    private static void Normalize(RunRequest req)
    {
        req.Readers ??= [];
        req.UpdateCount = Math.Clamp(req.UpdateCount, 1, 10);
        req.UpdaterStartDelaySec = Math.Clamp(req.UpdaterStartDelaySec, 0, 3600);
        req.UpdateIntervalSec = Math.Clamp(req.UpdateIntervalSec, 0, 3600);
        req.CommandTimeoutSec = Math.Clamp(req.CommandTimeoutSec, 0, 3600);
        req.LockTimeoutMs = Math.Max(-1, req.LockTimeoutMs);
        req.DiffRowCount = Math.Clamp(req.DiffRowCount, 1, 1_000_000);
        req.MonitorIntervalMs = Math.Clamp(req.MonitorIntervalMs, 100, 10_000);
        req.LowPriorityMaxMinutes = Math.Clamp(req.LowPriorityMaxMinutes, 1, 60);
        foreach (var r in req.Readers)
        {
            r.StartDelaySec = Math.Clamp(r.StartDelaySec, 0, 3600);
            r.TriggerUpdate = Math.Clamp(r.TriggerUpdate, 1, req.UpdateCount);
            r.DurationSec = Math.Clamp(r.DurationSec, 1, 3600);
        }
    }

    private static string DefaultTitle(RunRequest req)
    {
        var mode = req.Mode switch
        {
            PublishMode.Table => "テーブル公開",
            PublishMode.View => "ビュー公開",
            PublishMode.Switch => "SWITCH",
            PublishMode.Control => "制御テーブル切替",
            _ => "シノニム公開",
        };
        var type = req.UpdateType switch
        {
            UpdateType.Full => "全件更新",
            UpdateType.Diff => "差分更新",
            _ => "全件同期MERGE",
        };
        return $"{mode} × {type} × 読取{req.Readers.Count}件";
    }

    private static string DescribeRequest(RunRequest req)
    {
        var lines = new List<string>
        {
            $"公開方式       : {UpdaterWorker.ModeText(req.Mode)}",
            $"更新種別       : {UpdaterWorker.UpdateTypeText(req.UpdateType)}{(req.UpdateType == UpdateType.Diff ? $"（{req.DiffRowCount:N0}行）" : "")}",
            $"更新開始       : {req.UpdaterStartDelaySec}秒後 × {req.UpdateCount}回（間隔 {req.UpdateIntervalSec}秒）",
            $"CommandTimeout : {req.CommandTimeoutSec}秒",
            $"LOCK_TIMEOUT   : {(req.LockTimeoutMs < 0 ? "無制限(-1)" : req.LockTimeoutMs + "ms")}",
        };
        if (req.Mode is PublishMode.View or PublishMode.Synonym or PublishMode.Control && req.UpdateType == UpdateType.Full)
        {
            lines.Add($"非アクティブ準備: {req.InactivePrep}");
        }
        if (req.Mode == PublishMode.Switch && req.UpdateType == UpdateType.Full)
        {
            lines.Add(req.SwitchLowPriority
                ? $"SWITCH 低優先度: WAIT_AT_LOW_PRIORITY (MAX_DURATION = {req.LowPriorityMaxMinutes} MINUTES, ABORT_AFTER_WAIT = {req.LowPriorityAbort.ToString().ToUpperInvariant()})"
                : "SWITCH 低優先度: なし（通常の優先度で待機）");
        }
        lines.Add($"監視間隔       : {req.MonitorIntervalMs}ms");
        for (var i = 0; i < req.Readers.Count; i++)
        {
            var r = req.Readers[i];
            var when = r.Trigger switch
            {
                ReaderTrigger.BeforeSwitch => $"更新{r.TriggerUpdate}の切替{r.StartDelaySec}秒前に開始",
                ReaderTrigger.SwitchBlocked => $"更新{r.TriggerUpdate}の切替がロック待機になって{r.StartDelaySec}秒後に開始",
                ReaderTrigger.SwitchStarted => $"更新{r.TriggerUpdate}の切替開始から{r.StartDelaySec}秒後に開始",
                _ => $"検証開始{r.StartDelaySec}秒後に開始",
            };
            lines.Add($"Reader{i + 1,-9}: {ReaderWorker.KindText(r.Kind)} / {when} / {r.DurationSec}秒間");
        }
        if (!string.IsNullOrWhiteSpace(req.Memo))
        {
            lines.Add($"メモ           : {req.Memo}");
        }
        return string.Join('\n', lines);
    }
}

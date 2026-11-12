using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace ShareSystemAlterViewCheck.Server.Verification;

/// <summary>公開オブジェクトに対して長時間の SELECT を実行するセッション</summary>
public sealed class ReaderWorker
{
    /// <summary>SlowRead の読み取りペース（行/秒）</summary>
    private const int SlowReadRowsPerSecond = 1000;

    private readonly RunContext ctx;
    private readonly string connectionString;
    private readonly PublishMode mode;
    private readonly ReaderSpec spec;
    private readonly string actor;

    /// <summary>「切替の N 秒前」開始のとき、読み取り中になったことを更新処理へ通知する</summary>
    private readonly TaskCompletionSource? _started;

    /// <summary>SELECT を送った時刻（検証開始からの ms）</summary>
    private long _queryStartMs;

    public ReaderWorker(RunContext ctx, string connectionString, PublishMode mode, ReaderSpec spec, string actor)
    {
        this.ctx = ctx;
        this.connectionString = connectionString;
        this.mode = mode;
        this.spec = spec;
        this.actor = actor;
        Result = new ReaderResult { Actor = actor, Kind = spec.Kind, Trigger = spec.Trigger };
        // 更新処理が切替前に待てるよう、検証開始前に登録しておく
        if (spec.Trigger == ReaderTrigger.BeforeSwitch)
        {
            _started = ctx.SwitchOf(spec.TriggerUpdate).RegisterBeforeSwitchReader(actor, spec.StartDelaySec);
        }
    }

    public ReaderResult Result { get; }

    public async Task RunAsync(CancellationToken ct)
    {
        var target = DbObjects.PublicObjectOf(mode);
        Result.Target = target;

        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct);
            var spid = await GetSpidAsync(conn, ct);
            using var session = ctx.Track(spid, actor);

            // 参照先はトリガー待ちの前に取得する（切替の Sch-M 待ち中はメタデータ参照も止まることがあるため）
            var resolved = await DbObjects.GetActiveTableAsync(conn, mode, ct);
            Result.ResolvedTable = resolved;

            if (!await WaitTriggerAsync(ct))
            {
                return;
            }

            // 制御テーブル切替は DDL を伴わないので、開始直前に読み直しても止まらない（切替後に来た読み取りは新しい側を読む）
            if (mode == PublishMode.Control)
            {
                resolved = await DbObjects.GetActiveTableAsync(conn, mode, ct);
                Result.ResolvedTable = resolved;
            }

            var targetText = mode is PublishMode.Table or PublishMode.Switch ? target : $"{target}（開始時の参照先: {resolved}）";
            ctx.Info(actor, $"読み取り開始 [{KindText(spec.Kind)}] 対象={targetText} 予定時間={spec.DurationSec}秒 SPID={spid}");

            var sw = Stopwatch.StartNew();
            Result.StartMs = ctx.ElapsedMs;
            var phase = ctx.BeginPhase(actor, $"{KindText(spec.Kind)}: {target}", "Work", $"参照先 {resolved}");
            try
            {
                switch (spec.Kind)
                {
                    case ReaderKind.SlowRead:
                        await SlowReadAsync(conn, target, ct);
                        break;
                    case ReaderKind.HoldLock:
                        await HoldLockAsync(conn, target, ct);
                        break;
                    case ReaderKind.HeavyQuery:
                        await HeavyQueryAsync(conn, target, ct);
                        break;
                }
                Result.DurationMs = sw.ElapsedMilliseconds;
                ctx.EndPhase(phase, Result.Success ? "Ok" : "Error");
                if (Result.Success)
                {
                    ctx.Success(actor, $"読み取り終了: {Result.Result}（{Result.RowsRead:N0}行 / {sw.ElapsedMilliseconds:N0}ms / 最初の行まで {Result.FirstRowMs:N0}ms）");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Result.DurationMs = sw.ElapsedMilliseconds;
                ctx.EndPhase(phase, "Error");
                throw;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Result.Result = "キャンセル";
            ctx.Warn(actor, "読み取りがキャンセルされました");
        }
        catch (SqlException ex)
        {
            Result.Result = $"SQLエラー {ex.Number}";
            Result.Error = ex.Message;
            ctx.Error(actor, $"読み取りでエラーが発生しました（エラー番号 {ex.Number}）", ex.Message);
        }
        catch (Exception ex)
        {
            Result.Result = "エラー";
            Result.Error = ex.Message;
            ctx.Error(actor, "読み取りでエラーが発生しました", ex.ToString());
        }
        finally
        {
            // 失敗・キャンセル時も更新処理を待たせ続けない
            _started?.TrySetResult();
        }
    }

    /// <summary>開始タイミングまで待つ。開始しない場合は false</summary>
    private async Task<bool> WaitTriggerAsync(CancellationToken ct)
    {
        switch (spec.Trigger)
        {
            case ReaderTrigger.BeforeSwitch:
            {
                var waitPhase = ctx.BeginPhase(actor, $"開始待ち（更新{spec.TriggerUpdate}の切替前）", "Wait");
                var reached = await ctx.SwitchOf(spec.TriggerUpdate).Ready.Task.WaitAsync(ct);
                ctx.EndPhase(waitPhase);
                if (!reached)
                {
                    Skip($"更新{spec.TriggerUpdate}が切替に到達しなかったため実行しませんでした");
                    return false;
                }
                ctx.Info(actor, $"更新{spec.TriggerUpdate}が切替前に到達しました。読み取りを開始します（更新処理は読み取り開始から {spec.StartDelaySec} 秒後に切替します）");
                return true;
            }
            case ReaderTrigger.SwitchBlocked:
            {
                var waitPhase = ctx.BeginPhase(actor, $"開始待ち（更新{spec.TriggerUpdate}の切替がロック待機になるまで）", "Wait");
                var blocked = await ctx.SwitchOf(spec.TriggerUpdate).Blocked.Task.WaitAsync(ct);
                if (!blocked)
                {
                    ctx.EndPhase(waitPhase);
                    Skip($"更新{spec.TriggerUpdate}の切替がロック待機にならなかったため実行しませんでした");
                    return false;
                }
                ctx.Info(actor, $"更新{spec.TriggerUpdate}の切替がロック待機中です。{spec.StartDelaySec}秒後に読み取りを開始します");
                if (spec.StartDelaySec > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(spec.StartDelaySec), ct);
                }
                ctx.EndPhase(waitPhase);
                return true;
            }
            case ReaderTrigger.SwitchStarted:
            {
                var waitPhase = ctx.BeginPhase(actor, $"開始待ち（更新{spec.TriggerUpdate}の切替開始 + {spec.StartDelaySec}秒）", "Wait");
                var started = await ctx.SwitchOf(spec.TriggerUpdate).Started.Task.WaitAsync(ct);
                if (!started)
                {
                    ctx.EndPhase(waitPhase);
                    Skip($"更新{spec.TriggerUpdate}が切替に到達しなかったため実行しませんでした");
                    return false;
                }
                if (spec.StartDelaySec > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(spec.StartDelaySec), ct);
                }
                ctx.EndPhase(waitPhase);
                ctx.Info(actor, $"更新{spec.TriggerUpdate}の切替開始から {spec.StartDelaySec} 秒たったので読み取りを開始します");
                return true;
            }
            default:
            {
                if (spec.StartDelaySec > 0)
                {
                    var waitPhase = ctx.BeginPhase(actor, "開始待ち", "Wait");
                    await Task.Delay(TimeSpan.FromSeconds(spec.StartDelaySec), ct);
                    ctx.EndPhase(waitPhase);
                }
                return true;
            }
        }
    }

    private void Skip(string message)
    {
        Result.Success = true;
        Result.Result = "未実行（トリガー不発）";
        ctx.Warn(actor, message);
    }

    /// <summary>SELECT の結果（または最初の行）が返った：読み取り中（Sch-S 保持中）になったことを記録・通知する</summary>
    private void MarkStarted()
    {
        if (Result.FirstRowMs is null)
        {
            Result.FirstRowMs = ctx.ElapsedMs - _queryStartMs;
            if (Result.FirstRowMs >= 1000)
            {
                ctx.Warn(actor, $"最初の行が返るまで {Result.FirstRowMs:N0}ms 待たされました");
            }
        }
        _started?.TrySetResult();
    }

    /// <summary>SELECT * をゆっくり読む。読み取り中はステートメントが実行中のまま Sch-S ロックが保持される</summary>
    private async Task SlowReadAsync(SqlConnection conn, string target, CancellationToken ct)
    {
        var sql = $"SELECT {DbObjects.Columns} FROM {DbObjects.Q(target)} ORDER BY Id;";
        ctx.Sql(actor, "SELECT 実行（結果をゆっくり読み取り）", sql);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 0 };
        var deadline = Stopwatch.StartNew();
        var duration = TimeSpan.FromSeconds(spec.DurationSec);
        var batch = Math.Max(1, SlowReadRowsPerSecond / 10);
        var finished = true;

        _queryStartMs = ctx.ElapsedMs;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                if (Result.RowsRead == 0)
                {
                    MarkStarted();
                    // 継続時間は最初の行が返ってから数える（ロック待ちの時間を含めない）
                    deadline.Restart();
                }
                Result.RowsRead++;
                if (Result.RowsRead % batch == 0)
                {
                    if (deadline.Elapsed >= duration)
                    {
                        finished = false;
                        break;
                    }
                    await Task.Delay(100, ct);
                }
            }
            if (!finished)
            {
                // 残りを読み捨てないよう、閉じる前にキャンセルする
                cmd.Cancel();
            }
        }

        Result.Success = true;
        Result.Result = finished ? "全件読了" : "指定時間で読み取り終了";
        if (finished && deadline.Elapsed < duration)
        {
            ctx.Warn(actor, "予定時間より前に全件を読み終えました（行数が少ないと切替と重ならないことがあります）");
        }
    }

    /// <summary>トランザクション内でテーブルロックを取得したまま待機する</summary>
    private async Task HoldLockAsync(SqlConnection conn, string target, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(spec.DurationSec);
        var selectSql = $"""
            SET XACT_ABORT ON;
            BEGIN TRAN;
            SELECT COUNT_BIG(*) FROM {DbObjects.Q(target)} WITH (TABLOCK, HOLDLOCK);
            """;
        var holdSql = $"""
            WAITFOR DELAY '{delay:hh\:mm\:ss}';
            COMMIT;
            """;
        ctx.Sql(actor, "HOLDLOCK でロックを取得し、保持したまま待機", selectSql + "\n" + holdSql);
        try
        {
            _queryStartMs = ctx.ElapsedMs;
            await using (var cmd = new SqlCommand(selectSql, conn) { CommandTimeout = 0 })
            {
                Result.RowsRead = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
            }
            MarkStarted();
            await using (var cmd = new SqlCommand(holdSql, conn) { CommandTimeout = spec.DurationSec + 60 })
            {
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
        catch
        {
            await using var rollback = new SqlCommand("IF @@TRANCOUNT > 0 ROLLBACK;", conn);
            try { await rollback.ExecuteNonQueryAsync(CancellationToken.None); } catch { /* 接続破棄に任せる */ }
            throw;
        }
        Result.Success = true;
        Result.Result = "ロック保持終了（COMMIT）";
    }

    /// <summary>自己結合の重い集計。指定時間でキャンセルする</summary>
    private async Task HeavyQueryAsync(SqlConnection conn, string target, CancellationToken ct)
    {
        var sql = $"""
            SELECT COUNT_BIG(*)
            FROM {DbObjects.Q(target)} a
            CROSS JOIN {DbObjects.Q(target)} b
            WHERE a.Amount > b.Amount
            OPTION (MAXDOP 1);
            """;
        ctx.Sql(actor, "重い集計クエリを実行（指定時間で打ち切り）", sql);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(spec.DurationSec));
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 0 };
        _queryStartMs = ctx.ElapsedMs;
        // 集計は結果が出るまで行が返らないため、実行開始から 1 秒で「読み取り中」とみなして通知する
        _ = Task.Delay(1000, timeout.Token).ContinueWith(t => { if (!t.IsCanceled) _started?.TrySetResult(); }, TaskScheduler.Default);
        try
        {
            var count = await cmd.ExecuteScalarAsync(timeout.Token);
            Result.RowsRead = Convert.ToInt64(count);
            Result.Result = "クエリ完了";
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested &&
                                   ex is OperationCanceledException or SqlException)
        {
            Result.Result = "指定時間でクエリを打ち切り";
        }
        Result.Success = true;
    }

    public static string KindText(ReaderKind kind) => kind switch
    {
        ReaderKind.SlowRead => "低速読み取り",
        ReaderKind.HoldLock => "HOLDLOCK保持",
        ReaderKind.HeavyQuery => "重い集計",
        _ => kind.ToString(),
    };

    internal static async Task<short> GetSpidAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("SELECT @@SPID", conn);
        return Convert.ToInt16(await cmd.ExecuteScalarAsync(ct));
    }
}

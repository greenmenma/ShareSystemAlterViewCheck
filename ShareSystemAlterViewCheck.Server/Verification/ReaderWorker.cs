using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace ShareSystemAlterViewCheck.Server.Verification;

/// <summary>公開オブジェクトに対して長時間の SELECT を実行するセッション</summary>
public sealed class ReaderWorker(RunContext ctx, string connectionString, PublishMode mode, ReaderSpec spec, string actor)
{
    /// <summary>SlowRead の読み取りペース（行/秒）</summary>
    private const int SlowReadRowsPerSecond = 1000;

    public ReaderResult Result { get; } = new() { Actor = actor, Kind = spec.Kind };

    public async Task RunAsync(CancellationToken ct)
    {
        var target = DbObjects.PublicObjectOf(mode);
        Result.Target = target;

        try
        {
            if (spec.StartDelaySec > 0)
            {
                var waitPhase = ctx.BeginPhase(actor, "開始待ち", "Wait");
                await Task.Delay(TimeSpan.FromSeconds(spec.StartDelaySec), ct);
                ctx.EndPhase(waitPhase);
            }

            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct);
            var spid = await GetSpidAsync(conn, ct);
            using var session = ctx.Track(spid, actor);

            var resolved = await DbObjects.GetActiveTableAsync(conn, mode, ct);
            Result.ResolvedTable = resolved;
            var targetText = mode == PublishMode.Table ? target : $"{target}（参照先: {resolved}）";
            ctx.Info(actor, $"読み取り開始 [{KindText(spec.Kind)}] 対象={targetText} 予定時間={spec.DurationSec}秒 SPID={spid}");

            var sw = Stopwatch.StartNew();
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
                    ctx.Success(actor, $"読み取り終了: {Result.Result}（{Result.RowsRead:N0}行 / {sw.ElapsedMilliseconds:N0}ms）");
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

        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
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
    }

    /// <summary>トランザクション内でテーブルロックを取得したまま待機する</summary>
    private async Task HoldLockAsync(SqlConnection conn, string target, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(spec.DurationSec);
        var sql = $"""
            SET XACT_ABORT ON;
            BEGIN TRAN;
            SELECT COUNT_BIG(*) FROM {DbObjects.Q(target)} WITH (TABLOCK, HOLDLOCK);
            WAITFOR DELAY '{delay:hh\:mm\:ss}';
            COMMIT;
            """;
        ctx.Sql(actor, "HOLDLOCK でロックを保持", sql);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = spec.DurationSec + 60 };
        var count = await cmd.ExecuteScalarAsync(ct);
        Result.RowsRead = Convert.ToInt64(count);
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

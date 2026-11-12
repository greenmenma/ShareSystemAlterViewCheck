using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace ShareSystemAlterViewCheck.Server.Verification;

/// <summary>
/// 実行中の検証の状態（ログ・フェーズ・セッション対応表）をスレッドセーフに保持する
/// </summary>
public sealed class RunContext
{
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ILogger _logger;
    private long _seq;
    private int _phaseId;

    public RunContext(RunRecord record, ILogger logger)
    {
        Record = record;
        _logger = logger;
    }

    public RunRecord Record { get; }

    public CancellationTokenSource Cts { get; } = new();

    /// <summary>SPID → アクター名（監視でブロック元/先を判別するため）</summary>
    public ConcurrentDictionary<short, string> Sessions { get; } = new();

    public long ElapsedMs => _clock.ElapsedMilliseconds;

    /// <summary>更新回ごとの切替シグナル（読み取りの開始タイミング連動用）</summary>
    private readonly ConcurrentDictionary<int, SwitchSignals> _switches = new();

    public SwitchSignals SwitchOf(int updateIndex) => _switches.GetOrAdd(updateIndex, i => new SwitchSignals(i));

    /// <summary>現在実行中の切替の更新回（0 = 切替中でない）。ロック監視が参照する</summary>
    public int ActiveSwitchIndex
    {
        get => Volatile.Read(ref _activeSwitchIndex);
        set => Volatile.Write(ref _activeSwitchIndex, value);
    }

    private int _activeSwitchIndex;

    /// <summary>更新処理の終了時：発生しなかったシグナルを「発生せず」で完了させ、待っている読み取りを解放する</summary>
    public void CompleteAllSwitches()
    {
        foreach (var s in _switches.Values)
        {
            s.Ready.TrySetResult(false);
            s.Blocked.TrySetResult(false);
        }
    }

    public void Log(string actor, string level, string message, string? detail = null)
    {
        lock (_gate)
        {
            Record.Events.Add(new RunEvent
            {
                Seq = ++_seq,
                Time = DateTimeOffset.Now,
                ElapsedMs = _clock.ElapsedMilliseconds,
                Actor = actor,
                Level = level,
                Message = message,
                Detail = detail,
            });
        }
        _logger.LogInformation("[{RunId}] {Actor} {Level} {Message}", Record.Id, actor, level, message);
    }

    public void Info(string actor, string message, string? detail = null) => Log(actor, "Info", message, detail);
    public void Success(string actor, string message, string? detail = null) => Log(actor, "Success", message, detail);
    public void Warn(string actor, string message, string? detail = null) => Log(actor, "Warn", message, detail);
    public void Error(string actor, string message, string? detail = null) => Log(actor, "Error", message, detail);
    public void Sql(string actor, string message, string sql) => Log(actor, "Sql", message, sql);

    public int BeginPhase(string actor, string name, string kind = "Work", string? note = null)
    {
        lock (_gate)
        {
            var phase = new RunPhase
            {
                Id = ++_phaseId,
                Actor = actor,
                Name = name,
                Kind = kind,
                StartMs = _clock.ElapsedMilliseconds,
                Note = note,
            };
            Record.Phases.Add(phase);
            return phase.Id;
        }
    }

    /// <summary>フェーズを終了し、所要ミリ秒を返す</summary>
    public long EndPhase(int id, string status = "Ok", string? note = null)
    {
        lock (_gate)
        {
            var phase = Record.Phases.FirstOrDefault(p => p.Id == id);
            if (phase is null)
            {
                return 0;
            }
            phase.EndMs ??= _clock.ElapsedMilliseconds;
            phase.Status = status;
            if (note is not null)
            {
                phase.Note = note;
            }
            return phase.EndMs.Value - phase.StartMs;
        }
    }

    /// <summary>未終了のフェーズをまとめて終了する</summary>
    public void CloseOpenPhases(string status)
    {
        lock (_gate)
        {
            foreach (var p in Record.Phases.Where(p => p.EndMs is null))
            {
                p.EndMs = _clock.ElapsedMilliseconds;
                p.Status = status;
            }
        }
    }

    public List<RunPhase> PhasesSnapshot()
    {
        lock (_gate)
        {
            return Record.Phases.Select(p => new RunPhase
            {
                Id = p.Id, Actor = p.Actor, Name = p.Name, Kind = p.Kind,
                StartMs = p.StartMs, EndMs = p.EndMs, Status = p.Status, Note = p.Note,
            }).ToList();
        }
    }

    /// <summary>ロックを取ったうえで JSON にシリアライズ（API 返却・保存用）</summary>
    public byte[] Serialize(JsonSerializerOptions options)
    {
        lock (_gate)
        {
            return JsonSerializer.SerializeToUtf8Bytes(Record, options);
        }
    }

    public void Mutate(Action<RunRecord> action)
    {
        lock (_gate)
        {
            action(Record);
        }
    }

    /// <summary>
    /// SPID をアクターとして登録し、Dispose で解除する。
    /// 接続プールで同じ SPID が別の用途（画面の状態取得など）に再利用されたとき、誤ったアクター名で表示しないため。
    /// </summary>
    public IDisposable Track(short spid, string actor)
    {
        Sessions[spid] = actor;
        return new SessionScope(this, spid, actor);
    }

    private sealed class SessionScope(RunContext ctx, short spid, string actor) : IDisposable
    {
        public void Dispose() => ctx.Sessions.TryRemove(new KeyValuePair<short, string>(spid, actor));
    }

    public string ActorOf(short spid) =>
        Sessions.TryGetValue(spid, out var name) ? name : $"外部SPID {spid}";
}

/// <summary>
/// 1 回の更新の切替に関するシグナル
///   Ready   : 更新処理が切替直前に到達した（false = 到達せず終了）
///   Blocked : 切替がロック待機になった（false = 待機せずに終わった / 切替に到達しなかった）
///   ReadersStarted : 「切替の N 秒前」に開始する読み取りが、実際に読み取り中になったことの通知
/// </summary>
public sealed class SwitchSignals(int index)
{
    public int Index { get; } = index;

    public TaskCompletionSource<bool> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<bool> Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly List<(string Actor, int LeadSec, TaskCompletionSource Started)> _readers = [];

    /// <summary>切替前に開始する読み取りを登録する（検証開始前に呼ぶ）。leadSec = 読み取り開始から切替までの秒数</summary>
    public TaskCompletionSource RegisterBeforeSwitchReader(string actor, int leadSec)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_readers)
        {
            _readers.Add((actor, Math.Max(0, leadSec), tcs));
        }
        return tcs;
    }

    public List<(string Actor, int LeadSec, TaskCompletionSource Started)> BeforeSwitchReaders()
    {
        lock (_readers)
        {
            return [.. _readers];
        }
    }
}

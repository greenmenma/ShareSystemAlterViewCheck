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

    public string ActorOf(short spid) =>
        Sessions.TryGetValue(spid, out var name) ? name : $"外部SPID {spid}";
}

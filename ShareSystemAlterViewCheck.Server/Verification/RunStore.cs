using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ShareSystemAlterViewCheck.Server.Verification;

public sealed class VerificationOptions
{
    /// <summary>検証ログ（JSON）の保存先。相対パスはコンテンツルート基準</summary>
    public string LogDirectory { get; set; } = "App_Data/logs";

    /// <summary>1 回の検証の最大実行時間（分）</summary>
    public int MaxRunMinutes { get; set; } = 30;
}

/// <summary>検証ログを 1 実行 1 ファイルの JSON として保存する</summary>
public sealed class RunStore
{
    private readonly string _dir;
    private readonly JsonSerializerOptions _json;
    private readonly ILogger<RunStore> _logger;
    private readonly ConcurrentDictionary<string, RunListItem> _index = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public RunStore(IOptions<VerificationOptions> options, IWebHostEnvironment env, JsonSerializerOptions json, ILogger<RunStore> logger)
    {
        _json = json;
        _logger = logger;
        var dir = options.Value.LogDirectory;
        _dir = Path.IsPathRooted(dir) ? dir : Path.Combine(env.ContentRootPath, dir);
        Directory.CreateDirectory(_dir);
        LoadIndex();
    }

    public string LogDirectory => _dir;

    private void LoadIndex()
    {
        foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
        {
            try
            {
                var record = JsonSerializer.Deserialize<RunRecord>(File.ReadAllBytes(file), _json);
                if (record is null)
                {
                    continue;
                }
                // アプリ停止などで実行中のまま残ったものは「中断」とする
                if (record.Status == RunStatus.Running)
                {
                    record.Status = RunStatus.Aborted;
                    record.Summary ??= new RunSummary { Verdict = "アプリ停止により中断", VerdictLevel = "Error" };
                    File.WriteAllBytes(file, JsonSerializer.SerializeToUtf8Bytes(record, _json));
                }
                _index[record.Id] = RunListItem.From(record);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ログファイルを読み込めませんでした: {File}", file);
            }
        }
    }

    public IEnumerable<RunListItem> List() => _index.Values.OrderByDescending(x => x.StartedAt);

    public async Task SaveAsync(RunContext ctx)
    {
        await _writeLock.WaitAsync();
        try
        {
            var bytes = ctx.Serialize(_json);
            var path = PathOf(ctx.Record.Id);
            var tmp = path + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
            ctx.Mutate(r => _index[r.Id] = RunListItem.From(r));
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<byte[]?> ReadAsync(string id)
    {
        var path = PathOf(id);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path) : null;
    }

    public bool Delete(string id)
    {
        var path = PathOf(id);
        _index.TryRemove(id, out _);
        if (!File.Exists(path))
        {
            return false;
        }
        File.Delete(path);
        return true;
    }

    private string PathOf(string id)
    {
        // パストラバーサル防止
        if (id.Any(c => !(char.IsLetterOrDigit(c) || c == '-')))
        {
            throw new ArgumentException("不正な ID です。", nameof(id));
        }
        return Path.Combine(_dir, id + ".json");
    }
}

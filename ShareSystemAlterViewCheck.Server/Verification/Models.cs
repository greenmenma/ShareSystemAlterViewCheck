namespace ShareSystemAlterViewCheck.Server.Verification;

/// <summary>公開方式</summary>
public enum PublishMode
{
    /// <summary>現行：テーブル公開（新テーブル作成 → DROP → RENAME）</summary>
    Table,
    /// <summary>改修後：ビュー公開（Blue/Green テーブル + ALTER VIEW）</summary>
    View,
    /// <summary>参考：シノニム公開（Blue/Green テーブル + DROP/CREATE SYNONYM）</summary>
    Synonym,
}

/// <summary>更新種別</summary>
public enum UpdateType
{
    /// <summary>全件更新</summary>
    Full,
    /// <summary>差分更新（差分テーブル → MERGE）</summary>
    Diff,
}

/// <summary>読み取り（重い SELECT）の種類</summary>
public enum ReaderKind
{
    /// <summary>SELECT * をクライアント側でゆっくり読み続ける（BI ツール等の長時間取得を想定）</summary>
    SlowRead,
    /// <summary>トランザクション内で HOLDLOCK + TABLOCK の SELECT 後に WAITFOR で保持</summary>
    HoldLock,
    /// <summary>自己結合による重い集計クエリ（指定時間で打ち切り）</summary>
    HeavyQuery,
}

/// <summary>読み取りを開始するタイミング</summary>
public enum ReaderTrigger
{
    /// <summary>検証開始から StartDelaySec 秒後</summary>
    RunStart,
    /// <summary>更新の切替（DROP+RENAME / ALTER VIEW 等）の直前。更新処理は読み取りが始まるのを待ってから切替を実行する</summary>
    BeforeSwitch,
    /// <summary>更新の切替がロック待機になってから StartDelaySec 秒後（待機中に後から来た SELECT を再現）</summary>
    SwitchBlocked,
}

/// <summary>Blue/Green 方式で非アクティブ側テーブルを空にする方法</summary>
public enum InactivePrep
{
    Truncate,
    DropCreate,
    Delete,
}

public enum RunStatus
{
    Running,
    Completed,
    Cancelled,
    Failed,
    Aborted,
}

public enum RunKind
{
    Verification,
    Setup,
}

/// <summary>読み取りセッションの設定</summary>
public sealed class ReaderSpec
{
    public ReaderKind Kind { get; set; } = ReaderKind.SlowRead;

    /// <summary>開始タイミング</summary>
    public ReaderTrigger Trigger { get; set; } = ReaderTrigger.RunStart;

    /// <summary>トリガー対象の更新回（Trigger が BeforeSwitch / SwitchBlocked のとき。1 始まり）</summary>
    public int TriggerUpdate { get; set; } = 1;

    /// <summary>開始遅延（秒）。RunStart は検証開始から、SwitchBlocked はロック待機の検出から。BeforeSwitch では使わない</summary>
    public int StartDelaySec { get; set; }

    /// <summary>読み取りを続ける時間（秒）</summary>
    public int DurationSec { get; set; } = 60;
}

/// <summary>検証実行のリクエスト</summary>
public sealed class RunRequest
{
    public string? Title { get; set; }
    public string? Memo { get; set; }
    public PublishMode Mode { get; set; } = PublishMode.Table;
    public UpdateType UpdateType { get; set; } = UpdateType.Full;
    public List<ReaderSpec> Readers { get; set; } = [];

    /// <summary>検証開始から 1 回目の更新を開始するまでの秒数</summary>
    public int UpdaterStartDelaySec { get; set; } = 5;

    /// <summary>更新の回数（Blue/Green の連続切替検証用）</summary>
    public int UpdateCount { get; set; } = 1;

    /// <summary>更新の開始間隔（秒）</summary>
    public int UpdateIntervalSec { get; set; } = 20;

    /// <summary>更新系 SQL の CommandTimeout（秒）</summary>
    public int CommandTimeoutSec { get; set; } = 30;

    /// <summary>SET LOCK_TIMEOUT（ミリ秒、-1 は無制限）</summary>
    public int LockTimeoutMs { get; set; } = -1;

    /// <summary>Blue/Green 方式の非アクティブ側準備方法</summary>
    public InactivePrep InactivePrep { get; set; } = InactivePrep.Truncate;

    /// <summary>差分更新の件数（半分を更新、半分を追加）</summary>
    public int DiffRowCount { get; set; } = 1000;

    /// <summary>ロック監視の間隔（ミリ秒）</summary>
    public int MonitorIntervalMs { get; set; } = 500;
}

/// <summary>ログ 1 行</summary>
public sealed class RunEvent
{
    public long Seq { get; set; }
    public DateTimeOffset Time { get; set; }
    public long ElapsedMs { get; set; }

    /// <summary>System / Monitor / Updater / Reader1 ...</summary>
    public string Actor { get; set; } = "";

    /// <summary>Info / Success / Warn / Error / Sql</summary>
    public string Level { get; set; } = "Info";

    public string Message { get; set; } = "";
    public string? Detail { get; set; }
}

/// <summary>タイムライン用のフェーズ</summary>
public sealed class RunPhase
{
    public int Id { get; set; }
    public string Actor { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Work / Blocked / Wait</summary>
    public string Kind { get; set; } = "Work";

    public long StartMs { get; set; }
    public long? EndMs { get; set; }

    /// <summary>Running / Ok / Timeout / Error / Cancelled</summary>
    public string Status { get; set; } = "Running";

    public string? Note { get; set; }
}

public sealed class UpdateResult
{
    public int Index { get; set; }
    public string Result { get; set; } = "";
    public bool Success { get; set; }
    public long? TotalMs { get; set; }
    public long? PrepareMs { get; set; }
    public long? BulkCopyMs { get; set; }
    public long? SwitchMs { get; set; }

    /// <summary>切替 SQL の開始・終了（検証開始からの ms）。読み取りとの重なり判定に使う</summary>
    public long? SwitchStartMs { get; set; }
    public long? SwitchEndMs { get; set; }

    public long RowsCopied { get; set; }
    public string? FromTable { get; set; }
    public string? ToTable { get; set; }
    public string? Error { get; set; }
}

public sealed class ReaderResult
{
    public string Actor { get; set; } = "";
    public ReaderKind Kind { get; set; }
    public ReaderTrigger Trigger { get; set; }
    public string Result { get; set; } = "";
    public bool Success { get; set; }
    public string? Target { get; set; }
    public string? ResolvedTable { get; set; }
    public long RowsRead { get; set; }

    /// <summary>読み取り開始（検証開始からの ms）</summary>
    public long? StartMs { get; set; }

    /// <summary>SELECT を送ってから最初の行（HOLDLOCK は SELECT 完了）が返るまでの時間。ロック待ちで止まるとここが長くなる</summary>
    public long? FirstRowMs { get; set; }

    public long? DurationMs { get; set; }
    public string? Error { get; set; }
}

public sealed class RunSummary
{
    /// <summary>判定（画面表示用の文言）</summary>
    public string Verdict { get; set; } = "";

    /// <summary>Success / Warn / Error</summary>
    public string VerdictLevel { get; set; } = "Info";

    public long MaxBlockedMs { get; set; }
    public long TotalBlockedMs { get; set; }
    public int BlockCount { get; set; }
    public int TimeoutCount { get; set; }
    public int ErrorCount { get; set; }

    /// <summary>切替の開始時点で実行中の読み取りがあったか（false なら検証不成立）</summary>
    public bool SwitchOverlapped { get; set; }

    /// <summary>読み取りセッションのロック待機（後から来た SELECT が切替待ちに巻き込まれた時間）</summary>
    public int ReaderBlockCount { get; set; }
    public long ReaderMaxBlockedMs { get; set; }

    public List<UpdateResult> Updates { get; set; } = [];
    public List<ReaderResult> Readers { get; set; } = [];
}

/// <summary>検証 1 回分の記録（JSON ファイルとして保存）</summary>
public sealed class RunRecord
{
    public string Id { get; set; } = "";
    public RunKind Kind { get; set; } = RunKind.Verification;
    public string Title { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public RunStatus Status { get; set; } = RunStatus.Running;
    public RunRequest? Request { get; set; }
    public string? ServerInfo { get; set; }
    public List<RunEvent> Events { get; set; } = [];
    public List<RunPhase> Phases { get; set; } = [];
    public RunSummary? Summary { get; set; }
}

/// <summary>一覧表示用</summary>
public sealed class RunListItem
{
    public string Id { get; set; } = "";
    public RunKind Kind { get; set; }
    public string Title { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public RunStatus Status { get; set; }
    public PublishMode? Mode { get; set; }
    public UpdateType? UpdateType { get; set; }
    public int ReaderCount { get; set; }
    public string? Verdict { get; set; }
    public string? VerdictLevel { get; set; }
    public long? MaxBlockedMs { get; set; }
    public int TimeoutCount { get; set; }
    public int ErrorCount { get; set; }
    public long? SwitchMs { get; set; }
    public int EventCount { get; set; }

    public static RunListItem From(RunRecord r) => new()
    {
        Id = r.Id,
        Kind = r.Kind,
        Title = r.Title,
        StartedAt = r.StartedAt,
        EndedAt = r.EndedAt,
        Status = r.Status,
        Mode = r.Request?.Mode,
        UpdateType = r.Request?.UpdateType,
        ReaderCount = r.Request?.Readers.Count ?? 0,
        Verdict = r.Summary?.Verdict,
        VerdictLevel = r.Summary?.VerdictLevel,
        MaxBlockedMs = r.Summary?.MaxBlockedMs,
        TimeoutCount = r.Summary?.TimeoutCount ?? 0,
        ErrorCount = r.Summary?.ErrorCount ?? 0,
        SwitchMs = r.Summary?.Updates.Where(u => u.SwitchMs.HasValue).Select(u => u.SwitchMs).Max(),
        EventCount = r.Events.Count,
    };
}

/// <summary>環境（検証用オブジェクト）の状態</summary>
public sealed class EnvironmentStatus
{
    public bool Connected { get; set; }
    public string? Error { get; set; }
    public string? DataSource { get; set; }
    public string? Database { get; set; }
    public string? LoginInfo { get; set; }
    public string? ServerVersion { get; set; }
    public bool HasViewServerState { get; set; }
    public bool IsSetUp { get; set; }
    public string? ActiveViewTable { get; set; }
    public string? ActiveSynonymTable { get; set; }
    public List<DbObjectInfo> Objects { get; set; } = [];
    public bool RunActive { get; set; }
    public string? ActiveRunId { get; set; }
}

public sealed class DbObjectInfo
{
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
    public string? Type { get; set; }
    public bool Exists { get; set; }
    public long? Rows { get; set; }
    public string? Definition { get; set; }
}

public sealed class SetupRequest
{
    public int RowCount { get; set; } = 300_000;
}

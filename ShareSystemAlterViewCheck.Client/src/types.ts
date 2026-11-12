// サーバー側 Verification/Models.cs に対応する型

export type PublishMode = 'Table' | 'View' | 'Synonym' | 'Switch' | 'Control';
export type UpdateType = 'Full' | 'Diff' | 'FullSync';
export type LowPriorityAbort = 'None' | 'Self' | 'Blockers';
export type ReaderKind = 'SlowRead' | 'HoldLock' | 'HeavyQuery';
export type ReaderTrigger = 'RunStart' | 'BeforeSwitch' | 'SwitchBlocked' | 'SwitchStarted';
export type InactivePrep = 'Truncate' | 'DropCreate' | 'Delete' | 'SwitchOut';
export type RunStatus = 'Running' | 'Completed' | 'Cancelled' | 'Failed' | 'Aborted';
export type RunKind = 'Verification' | 'Setup';
export type Level = 'Info' | 'Success' | 'Warn' | 'Error' | 'Sql';

export interface ReaderSpec {
    kind: ReaderKind;
    /** 開始タイミング（古いログには無いので省略時は RunStart 扱い） */
    trigger?: ReaderTrigger;
    /** トリガー対象の更新回（1 始まり） */
    triggerUpdate?: number;
    startDelaySec: number;
    durationSec: number;
}

export interface RunRequest {
    title?: string;
    memo?: string;
    mode: PublishMode;
    updateType: UpdateType;
    readers: ReaderSpec[];
    updaterStartDelaySec: number;
    updateCount: number;
    updateIntervalSec: number;
    commandTimeoutSec: number;
    lockTimeoutMs: number;
    inactivePrep: InactivePrep;
    diffRowCount: number;
    monitorIntervalMs: number;
    /** SWITCH 方式：WAIT_AT_LOW_PRIORITY を付けるか（古いログには無い） */
    /** SqlBulkCopy の BatchSize（0 = 全件を 1 バッチ）。古いログには無い */
    bulkBatchSize?: number;
    /** 制御テーブル切替：切替の直後に旧側を空にする */
    cleanupAfterSwitch?: boolean;
    switchLowPriority?: boolean;
    lowPriorityMaxMinutes?: number;
    lowPriorityAbort?: LowPriorityAbort;
}

export interface RunEvent {
    seq: number;
    time: string;
    elapsedMs: number;
    actor: string;
    level: Level;
    message: string;
    detail?: string | null;
}

export interface RunPhase {
    id: number;
    actor: string;
    name: string;
    kind: 'Work' | 'Blocked' | 'Wait';
    startMs: number;
    endMs?: number | null;
    status: 'Running' | 'Ok' | 'Timeout' | 'Error' | 'Cancelled';
    note?: string | null;
}

export interface UpdateResult {
    index: number;
    result: string;
    success: boolean;
    totalMs?: number | null;
    prepareMs?: number | null;
    bulkCopyMs?: number | null;
    switchMs?: number | null;
    switchStartMs?: number | null;
    switchEndMs?: number | null;
    cleanupMs?: number | null;
    cleanupStartMs?: number | null;
    cleanupEndMs?: number | null;
    cleanupResult?: string | null;
    prepareLogMb?: number | null;
    bulkCopyLogMb?: number | null;
    switchLogMb?: number | null;
    cleanupLogMb?: number | null;
    totalLogMb?: number | null;
    rowsCopied: number;
    fromTable?: string | null;
    toTable?: string | null;
    error?: string | null;
}

export interface ReaderResult {
    actor: string;
    kind: ReaderKind;
    trigger?: ReaderTrigger;
    result: string;
    success: boolean;
    target?: string | null;
    resolvedTable?: string | null;
    rowsRead: number;
    startMs?: number | null;
    firstRowMs?: number | null;
    durationMs?: number | null;
    error?: string | null;
}

export interface RunSummary {
    verdict: string;
    verdictLevel: 'Success' | 'Warn' | 'Error' | 'Info';
    maxBlockedMs: number;
    totalBlockedMs: number;
    blockCount: number;
    timeoutCount: number;
    errorCount: number;
    switchOverlapped?: boolean;
    readerBlockCount?: number;
    readerMaxBlockedMs?: number;
    cleanupMaxBlockedMs?: number;
    totalLogMb?: number | null;
    updates: UpdateResult[];
    readers: ReaderResult[];
}

export interface RunRecord {
    id: string;
    kind: RunKind;
    title: string;
    startedAt: string;
    endedAt?: string | null;
    status: RunStatus;
    request?: RunRequest | null;
    serverInfo?: string | null;
    readCommittedSnapshot?: boolean | null;
    recoveryModel?: string | null;
    logFileStartMb?: number | null;
    logFileEndMb?: number | null;
    events: RunEvent[];
    phases: RunPhase[];
    summary?: RunSummary | null;
}

export interface RunListItem {
    id: string;
    kind: RunKind;
    title: string;
    startedAt: string;
    endedAt?: string | null;
    status: RunStatus;
    mode?: PublishMode | null;
    updateType?: UpdateType | null;
    readerCount: number;
    verdict?: string | null;
    verdictLevel?: string | null;
    maxBlockedMs?: number | null;
    timeoutCount: number;
    errorCount: number;
    switchMs?: number | null;
    eventCount: number;
    readCommittedSnapshot?: boolean | null;
}

export interface DbObjectInfo {
    name: string;
    role: string;
    type?: string | null;
    exists: boolean;
    rows?: number | null;
    definition?: string | null;
}

export interface EnvironmentStatus {
    connected: boolean;
    error?: string | null;
    dataSource?: string | null;
    database?: string | null;
    loginInfo?: string | null;
    serverVersion?: string | null;
    hasViewServerState: boolean;
    isSetUp: boolean;
    activeViewTable?: string | null;
    activeSynonymTable?: string | null;
    activeControlTable?: string | null;
    readCommittedSnapshot?: boolean | null;
    canAlterDatabase?: boolean;
    recoveryModel?: string | null;
    logFileMb?: number | null;
    objects: DbObjectInfo[];
    runActive: boolean;
    activeRunId?: string | null;
}

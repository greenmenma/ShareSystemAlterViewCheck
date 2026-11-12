import type { LowPriorityAbort, PublishMode, ReaderKind, ReaderSpec, ReaderTrigger, RunStatus, UpdateType } from './types';

export const modeLabel: Record<PublishMode, string> = {
    Table: 'テーブル公開（現行）',
    View: 'ビュー公開（改修後）',
    Synonym: 'シノニム公開（参考）',
    Switch: 'テーブル公開 + ALTER TABLE SWITCH（代替案）',
    Control: '制御テーブル切替ビュー（代替案・DDL なし）',
};

export const modeShort: Record<PublishMode, string> = {
    Table: '現行:テーブル',
    View: '改修:ビュー',
    Synonym: '参考:シノニム',
    Switch: '代替:SWITCH',
    Control: '代替:制御テーブル',
};

export const updateTypeLabel: Record<UpdateType, string> = {
    Full: '全件更新（入替）',
    Diff: '差分更新（MERGE）',
    FullSync: '全件同期（MERGE で INSERT/UPDATE/DELETE）',
};

export const updateTypeShort: Record<UpdateType, string> = {
    Full: '全件更新',
    Diff: '差分更新',
    FullSync: '全件同期',
};

export const lowPriorityAbortLabel: Record<LowPriorityAbort, string> = {
    Self: 'SELF（時間切れで切替をあきらめる）',
    None: 'NONE（時間切れ後は通常の優先度で待つ）',
    Blockers: 'BLOCKERS（時間切れで読み取りを強制終了）',
};

export const readerKindLabel: Record<ReaderKind, string> = {
    SlowRead: '低速読み取り（SELECT * をゆっくり取得）',
    HoldLock: 'HOLDLOCK 保持（トランザクションでロック保持）',
    HeavyQuery: '重い集計（自己結合・指定時間で打ち切り）',
};

export const readerTriggerLabel: Record<ReaderTrigger, string> = {
    RunStart: '検証開始から N 秒後',
    BeforeSwitch: '切替の N 秒前（更新は読み取り開始から N 秒後に切替）',
    SwitchBlocked: '切替がロック待機になってから N 秒後',
    SwitchStarted: '切替の開始から N 秒後（待機の有無に関係なく）',
};

/** 読み取りの開始タイミングを短い文で表す */
export function readerWhen(r: ReaderSpec): string {
    const u = r.triggerUpdate ?? 1;
    switch (r.trigger ?? 'RunStart') {
        case 'BeforeSwitch':
            return `更新${u}の切替${r.startDelaySec}秒前に開始`;
        case 'SwitchBlocked':
            return `更新${u}の切替がロック待機になって${r.startDelaySec}秒後に開始`;
        case 'SwitchStarted':
            return `更新${u}の切替開始から${r.startDelaySec}秒後に開始`;
        default:
            return `検証開始${r.startDelaySec}秒後に開始`;
    }
}

export const statusLabel: Record<RunStatus, string> = {
    Running: '実行中',
    Completed: '完了',
    Cancelled: 'キャンセル',
    Failed: '失敗',
    Aborted: '中断',
};

/** アクター名を画面表示用に変換 */
export function actorLabel(actor: string): string {
    if (actor === 'System') return 'システム';
    if (actor === 'Monitor') return 'ロック監視';
    if (actor === 'Updater') return '更新処理';
    if (actor === 'Updater(取得元)') return '更新(取得元)';
    if (actor === 'Setup') return 'セットアップ';
    const m = /^Reader(\d+)$/.exec(actor);
    if (m) return `読取${m[1]}`;
    return actor;
}

/** アクターごとの色クラス */
export function actorClass(actor: string): string {
    if (actor === 'System' || actor === 'Setup') return 'actor-system';
    if (actor === 'Monitor') return 'actor-monitor';
    if (actor.startsWith('Updater')) return 'actor-updater';
    if (actor.startsWith('Reader')) return 'actor-reader';
    return 'actor-other';
}

export function ms(value?: number | null): string {
    if (value === undefined || value === null) return '—';
    if (value < 1000) return `${value.toLocaleString()} ms`;
    return `${(value / 1000).toLocaleString(undefined, { maximumFractionDigits: 2 })} 秒`;
}

export function elapsed(value: number): string {
    const s = value / 1000;
    return `+${s.toFixed(3)}s`;
}

export function dateTime(iso?: string | null): string {
    if (!iso) return '—';
    const d = new Date(iso);
    return d.toLocaleString('ja-JP', {
        year: 'numeric', month: '2-digit', day: '2-digit',
        hour: '2-digit', minute: '2-digit', second: '2-digit',
    });
}

export function timeOnly(iso: string): string {
    const d = new Date(iso);
    return `${d.toLocaleTimeString('ja-JP', { hour12: false })}.${String(d.getMilliseconds()).padStart(3, '0')}`;
}

export function downloadText(filename: string, text: string, type: string) {
    const blob = new Blob([text], { type });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    a.click();
    URL.revokeObjectURL(url);
}

import { useState } from 'react';
import { api } from '../api';
import { lowPriorityAbortLabel, modeLabel, readerKindLabel, readerTriggerLabel, updateTypeLabel } from '../format';
import type { EnvironmentStatus, InactivePrep, LowPriorityAbort, PublishMode, ReaderKind, ReaderSpec, ReaderTrigger, RunRequest, UpdateType } from '../types';

interface Preset {
    key: string;
    group: 'current' | 'alternative';
    title: string;
    expect: string;
    expectLevel: 'error' | 'success' | 'warn';
    /** RCSI（READ_COMMITTED_SNAPSHOT）が ON である前提か */
    needsRcsi?: boolean;
    description: string;
    request: RunRequest;
}

// 検証したいパターン：
//   読取1 が切替の 10 秒前から読み続ける → 切替（DROP/ALTER/SWITCH/UPDATE/MERGE）
//   → 切替の開始から 5 秒後に読取2 が新たに SELECT（切替が待機していれば、その後ろで待たされるか）
// 開始タイミングは更新処理のイベントに連動するので、BulkCopy の速さに関係なく必ずこの順序になる
const blockingReaders: ReaderSpec[] = [
    { kind: 'SlowRead', trigger: 'BeforeSwitch', triggerUpdate: 1, startDelaySec: 10, durationSec: 60 },
    { kind: 'SlowRead', trigger: 'SwitchStarted', triggerUpdate: 1, startDelaySec: 5, durationSec: 10 },
];

const base: RunRequest = {
    mode: 'Table',
    updateType: 'Full',
    readers: blockingReaders,
    updaterStartDelaySec: 5,
    updateCount: 1,
    updateIntervalSec: 20,
    commandTimeoutSec: 30,
    lockTimeoutMs: -1,
    inactivePrep: 'Truncate',
    diffRowCount: 1000,
    monitorIntervalMs: 500,
    switchLowPriority: true,
    lowPriorityMaxMinutes: 1,
    lowPriorityAbort: 'Self',
};

const presets: Preset[] = [
    {
        key: 'current',
        group: 'current',
        title: '① 現行方式の再現',
        expect: 'タイムアウト想定',
        expectLevel: 'error',
        description:
            '読取1 が切替の 10 秒前から公開テーブルを 60 秒読み続け、DROP TABLE がロック待機（LCK_M_SCH_M）。切替開始の 5 秒後に来た読取2 も DROP の後ろで待たされる（LCK_M_SCH_S）ことと、CommandTimeout(30秒) で更新が失敗する現象を再現します。',
        request: { ...base, title: '① 現行: テーブル公開 × 長時間SELECT', mode: 'Table' },
    },
    {
        key: 'view',
        group: 'current',
        title: '② 改修案（ALTER VIEW）',
        expect: '解消しない',
        expectLevel: 'error',
        description:
            '①と同じ読み取りパターンをビュー公開で実行。ALTER VIEW もビューに Sch-M ロックが必要なため、読み取り中は待機し、後から来た読取2 も待たされることを確認します。',
        request: { ...base, title: '② 改修案: ビュー公開 × 長時間SELECT', mode: 'View' },
    },
    {
        key: 'view-twice',
        group: 'current',
        title: '③ 改修案・連続切替',
        expect: '注意点の確認',
        expectLevel: 'warn',
        description:
            '90 秒の読み取り中に 20 秒間隔で 2 回更新。2 回目は「読み取り中の旧テーブル」を TRUNCATE するため、Blue/Green でも待機が起きうるケースを確認します。',
        request: {
            ...base,
            title: '③ 改修案: ビュー公開 × 連続2回切替',
            mode: 'View',
            readers: [{ kind: 'SlowRead', trigger: 'RunStart', startDelaySec: 0, durationSec: 90 }],
            updateCount: 2,
            updateIntervalSec: 20,
        },
    },
    {
        key: 'synonym',
        group: 'current',
        title: '④ 参考：シノニム',
        expect: '解消しない',
        expectLevel: 'error',
        description: 'DROP SYNONYM / CREATE SYNONYM による切替を①②と同じ読み取りパターンで実行し、ALTER VIEW と比較します。',
        request: { ...base, title: '④ 参考: シノニム公開 × 長時間SELECT', mode: 'Synonym' },
    },
    {
        key: 'diff',
        group: 'current',
        title: '⑤ 差分更新 × HOLDLOCK',
        expect: '待機の確認',
        expectLevel: 'warn',
        description:
            'MERGE の 10 秒前から読取1 が HOLDLOCK でテーブルロックを 40 秒保持。HOLDLOCK（SERIALIZABLE）は RCSI でもロックを取るため、RCSI の ON/OFF に関係なく MERGE が待機することを確認します。',
        request: {
            ...base,
            title: '⑤ 差分更新 × HOLDLOCK',
            mode: 'Table',
            updateType: 'Diff',
            readers: [
                { kind: 'HoldLock', trigger: 'BeforeSwitch', triggerUpdate: 1, startDelaySec: 10, durationSec: 40 },
                { kind: 'SlowRead', trigger: 'SwitchStarted', triggerUpdate: 1, startDelaySec: 5, durationSec: 10 },
            ],
        },
    },
    {
        key: 'switch-low',
        group: 'alternative',
        title: '⑥ SWITCH + 低優先度待機',
        expect: '読み取りは止まらない',
        expectLevel: 'success',
        description:
            '公開テーブルはそのまま、新データを別テーブルへ BulkCopy して ALTER TABLE SWITCH で入れ替えます。WAIT_AT_LOW_PRIORITY を付けると、切替は読取1 の終了を待ちますが、後から来た読取2 は切替を追い越して読めます。読取1 の終了後に切替が成功します。',
        request: { ...base, title: '⑥ 代替: SWITCH 低優先度 × 長時間SELECT', mode: 'Switch' },
    },
    {
        key: 'switch-abort',
        group: 'alternative',
        title: '⑦ SWITCH 低優先度・時間切れ',
        expect: '更新をあきらめる',
        expectLevel: 'warn',
        description:
            '読取1 が 120 秒続く場合。低優先度で 1 分待っても読み取りが終わらないので、ABORT_AFTER_WAIT = SELF で切替をあきらめます（エラー 1222）。読み取りは止まらず、更新は後でやり直せる状態で終わることを確認します。',
        request: {
            ...base,
            title: '⑦ 代替: SWITCH 低優先度 × 120秒SELECT（時間切れ）',
            mode: 'Switch',
            readers: [
                { kind: 'SlowRead', trigger: 'BeforeSwitch', triggerUpdate: 1, startDelaySec: 10, durationSec: 120 },
                { kind: 'SlowRead', trigger: 'SwitchStarted', triggerUpdate: 1, startDelaySec: 5, durationSec: 10 },
            ],
        },
    },
    {
        key: 'control',
        group: 'alternative',
        title: '⑧ 制御テーブル切替（DDL なし）',
        expect: '解消見込み',
        expectLevel: 'success',
        needsRcsi: true,
        description:
            'ビュー定義は固定し、制御テーブルの値（UPDATE）で A/B を切り替えます。DDL を使わないので Sch-M ロックが発生しません。2 回目の更新では読取1 が読んでいる側を DELETE して入れ替えます（RCSI なら読取1 は更新前の行を読み続けます）。',
        request: {
            ...base,
            title: '⑧ 代替: 制御テーブル切替 × 長時間SELECT × 2回更新',
            mode: 'Control',
            inactivePrep: 'Delete',
            updateCount: 2,
            updateIntervalSec: 30,
        },
    },
    {
        key: 'fullsync',
        group: 'alternative',
        title: '⑨ 全件同期 MERGE（作り直さない）',
        expect: '解消見込み',
        expectLevel: 'success',
        needsRcsi: true,
        description:
            '公開テーブルを作り直さず、全件をステージングへ取得して MERGE（変わった行の UPDATE・INSERT・DELETE）で反映します。DDL がないので Sch-M は不要。RCSI なら読み取りは更新前の行を読み、MERGE と互いに待ちません。',
        request: { ...base, title: '⑨ 代替: 全件同期 MERGE × 長時間SELECT', mode: 'Table', updateType: 'FullSync' },
    },
    {
        key: 'diff-rcsi',
        group: 'alternative',
        title: '⑩ 差分更新 MERGE × 通常の SELECT',
        expect: '解消見込み',
        expectLevel: 'success',
        needsRcsi: true,
        description:
            '差分更新（MERGE）中に通常の長時間 SELECT を実行。RCSI が ON なら待機なし、OFF なら MERGE と読み取りの S ロックが待ち合うことを比較できます。',
        request: { ...base, title: '⑩ 代替: 差分更新 MERGE × 長時間SELECT', mode: 'Table', updateType: 'Diff' },
    },
];

interface Props {
    env: EnvironmentStatus | null;
    onStarted: (runId: string) => void;
}

export default function RunForm({ env, onStarted }: Props) {
    const [req, setReq] = useState<RunRequest>(structuredClone(presets[0].request));
    const [presetKey, setPresetKey] = useState(presets[0].key);
    const [error, setError] = useState<string | null>(null);
    const [busy, setBusy] = useState(false);

    const set = <K extends keyof RunRequest>(key: K, value: RunRequest[K]) => setReq((r) => ({ ...r, [key]: value }));

    const applyPreset = (p: Preset) => {
        setPresetKey(p.key);
        setReq(structuredClone(p.request));
    };

    const start = async () => {
        setBusy(true);
        setError(null);
        try {
            const { id } = await api.start(req);
            onStarted(id);
        } catch (e) {
            setError((e as Error).message);
        } finally {
            setBusy(false);
        }
    };

    const blueGreen = ['View', 'Synonym', 'Control'].includes(req.mode) && req.updateType === 'Full';
    const switchFull = req.mode === 'Switch' && req.updateType === 'Full';
    const needsRcsi = req.mode === 'Control' || req.updateType === 'FullSync' || req.updateType === 'Diff';
    const rcsi = env?.readCommittedSnapshot;
    const totalSec = Math.max(
        ...req.readers.map((r) => ((r.trigger ?? 'RunStart') === 'RunStart' ? r.startDelaySec : req.updaterStartDelaySec) + r.durationSec),
        req.updaterStartDelaySec + (req.updateCount - 1) * req.updateIntervalSec + req.commandTimeoutSec,
    );
    const updateReader = (i: number, patch: Partial<ReaderSpec>) => {
        const readers = [...req.readers];
        readers[i] = { ...readers[i], ...patch };
        set('readers', readers);
    };

    return (
        <div className="stack">
            {!env?.isSetUp && (
                <div className="alert alert-warn">
                    検証用オブジェクトがセットアップされていません。「環境セットアップ」タブでセットアップを実行してください。
                </div>
            )}

            <section className="card">
                <div className="card-head">
                    <h2>シナリオ（プリセット）</h2>
                    <span className="hint">選択すると下の条件に反映されます。条件は自由に変更できます。</span>
                </div>
                {([
                    ['current', '現状と改修案（ALTER VIEW）'],
                    ['alternative', '代替案（ロック回避の候補）'],
                ] as const).map(([group, label]) => (
                    <div key={group}>
                        <h3>{label}</h3>
                        <div className="presets">
                            {presets.filter((p) => p.group === group).map((p) => (
                                <button
                                    key={p.key}
                                    className={`preset ${presetKey === p.key ? 'selected' : ''}`}
                                    onClick={() => applyPreset(p)}
                                >
                                    <div className="preset-head">
                                        <strong>{p.title}</strong>
                                        <span className={`pill pill-${p.expectLevel}`}>{p.expect}</span>
                                        {p.needsRcsi && <span className={`pill ${rcsi ? 'pill-success' : 'pill-warn'}`}>RCSI 前提</span>}
                                    </div>
                                    <p>{p.description}</p>
                                </button>
                            ))}
                        </div>
                    </div>
                ))}
                <p className="hint">
                    現在の READ_COMMITTED_SNAPSHOT（RCSI）: <strong>{rcsi == null ? '不明' : rcsi ? 'ON' : 'OFF'}</strong>
                    （「環境セットアップ」タブで切り替えられます。「RCSI 前提」のシナリオは ON と OFF の両方で実行すると違いが分かります）
                </p>
            </section>

            <section className="card">
                <div className="card-head">
                    <h2>検証条件</h2>
                </div>

                {needsRcsi && rcsi === false && (
                    <div className="alert alert-warn">
                        この方式は RCSI が ON である前提です。現在は OFF のため、DML と読み取りの S ロックが互いに待ち合う結果になります（比較のために OFF で実行するのは問題ありません）。
                    </div>
                )}
                <div className="form-grid">
                    <label className="span-2">
                        タイトル
                        <input value={req.title ?? ''} onChange={(e) => set('title', e.target.value)} placeholder="未入力時は自動" />
                    </label>
                    <label>
                        公開方式
                        <select value={req.mode} onChange={(e) => set('mode', e.target.value as PublishMode)}>
                            {(Object.keys(modeLabel) as PublishMode[]).map((m) => (
                                <option key={m} value={m}>{modeLabel[m]}</option>
                            ))}
                        </select>
                    </label>
                    <label>
                        更新種別
                        <select value={req.updateType} onChange={(e) => set('updateType', e.target.value as UpdateType)}>
                            {(Object.keys(updateTypeLabel) as UpdateType[]).map((m) => (
                                <option key={m} value={m}>{updateTypeLabel[m]}</option>
                            ))}
                        </select>
                    </label>
                    <label>
                        更新開始（秒後）
                        <input type="number" min={0} value={req.updaterStartDelaySec}
                            onChange={(e) => set('updaterStartDelaySec', Number(e.target.value))} />
                    </label>
                    <label>
                        更新回数
                        <input type="number" min={1} max={10} value={req.updateCount}
                            onChange={(e) => set('updateCount', Number(e.target.value))} />
                    </label>
                    <label>
                        更新間隔（秒）
                        <input type="number" min={0} value={req.updateIntervalSec} disabled={req.updateCount <= 1}
                            onChange={(e) => set('updateIntervalSec', Number(e.target.value))} />
                    </label>
                    <label>
                        CommandTimeout（秒）
                        <input type="number" min={0} value={req.commandTimeoutSec}
                            onChange={(e) => set('commandTimeoutSec', Number(e.target.value))} />
                    </label>
                    <label>
                        LOCK_TIMEOUT（ms, -1=無制限）
                        <input type="number" min={-1} value={req.lockTimeoutMs}
                            onChange={(e) => set('lockTimeoutMs', Number(e.target.value))} />
                    </label>
                    <label>
                        非アクティブ側の準備
                        <select value={req.inactivePrep} disabled={!blueGreen}
                            onChange={(e) => set('inactivePrep', e.target.value as InactivePrep)}>
                            <option value="Truncate">TRUNCATE TABLE</option>
                            <option value="DropCreate">DROP + CREATE TABLE</option>
                            <option value="Delete">DELETE FROM</option>
                        </select>
                    </label>
                    <label>
                        SWITCH の低優先度待機
                        <select value={req.switchLowPriority === false ? 'off' : 'on'} disabled={!switchFull}
                            onChange={(e) => set('switchLowPriority', e.target.value === 'on')}>
                            <option value="on">WAIT_AT_LOW_PRIORITY を付ける</option>
                            <option value="off">付けない（通常の優先度で待機）</option>
                        </select>
                    </label>
                    <label>
                        低優先度の最大待機（分）
                        <input type="number" min={1} max={60} value={req.lowPriorityMaxMinutes ?? 1}
                            disabled={!switchFull || req.switchLowPriority === false}
                            onChange={(e) => set('lowPriorityMaxMinutes', Number(e.target.value))} />
                    </label>
                    <label>
                        待機時間を過ぎたら（ABORT_AFTER_WAIT）
                        <select value={req.lowPriorityAbort ?? 'Self'} disabled={!switchFull || req.switchLowPriority === false}
                            onChange={(e) => set('lowPriorityAbort', e.target.value as LowPriorityAbort)}>
                            {(Object.keys(lowPriorityAbortLabel) as LowPriorityAbort[]).map((k) => (
                                <option key={k} value={k}>{lowPriorityAbortLabel[k]}</option>
                            ))}
                        </select>
                    </label>
                    <label>
                        差分件数
                        <input type="number" min={1} value={req.diffRowCount} disabled={req.updateType !== 'Diff'}
                            onChange={(e) => set('diffRowCount', Number(e.target.value))} />
                    </label>
                    <label>
                        ロック監視間隔（ms）
                        <input type="number" min={100} step={100} value={req.monitorIntervalMs}
                            onChange={(e) => set('monitorIntervalMs', Number(e.target.value))} />
                    </label>
                    <label className="span-2">
                        メモ
                        <input value={req.memo ?? ''} onChange={(e) => set('memo', e.target.value)} placeholder="ログに残す補足" />
                    </label>
                </div>

                <h3>読み取りセッション（重い SELECT）</h3>
                <div className="table-wrap">
                    <table className="grid">
                        <thead>
                            <tr>
                                <th>#</th>
                                <th>種類</th>
                                <th>開始タイミング</th>
                                <th>対象の更新回</th>
                                <th>N（秒）</th>
                                <th>継続時間（秒）</th>
                                <th />
                            </tr>
                        </thead>
                        <tbody>
                            {req.readers.map((r, i) => {
                                const trigger = r.trigger ?? 'RunStart';
                                return (
                                    <tr key={i}>
                                        <td>読取{i + 1}</td>
                                        <td>
                                            <select value={r.kind} onChange={(e) => updateReader(i, { kind: e.target.value as ReaderKind })}>
                                                {(Object.keys(readerKindLabel) as ReaderKind[]).map((k) => (
                                                    <option key={k} value={k}>{readerKindLabel[k]}</option>
                                                ))}
                                            </select>
                                        </td>
                                        <td>
                                            <select value={trigger} onChange={(e) => updateReader(i, { trigger: e.target.value as ReaderTrigger })}>
                                                {(Object.keys(readerTriggerLabel) as ReaderTrigger[]).map((k) => (
                                                    <option key={k} value={k}>{readerTriggerLabel[k]}</option>
                                                ))}
                                            </select>
                                        </td>
                                        <td>
                                            <input type="number" min={1} max={req.updateCount} value={r.triggerUpdate ?? 1}
                                                disabled={trigger === 'RunStart'}
                                                onChange={(e) => updateReader(i, { triggerUpdate: Number(e.target.value) })} />
                                        </td>
                                        <td>
                                            <input type="number" min={0} value={r.startDelaySec}
                                                onChange={(e) => updateReader(i, { startDelaySec: Number(e.target.value) })} />
                                        </td>
                                        <td>
                                            <input type="number" min={1} value={r.durationSec}
                                                onChange={(e) => updateReader(i, { durationSec: Number(e.target.value) })} />
                                            {trigger === 'BeforeSwitch' && r.startDelaySec >= r.durationSec && (
                                                <div className="hint" style={{ color: 'var(--error)' }}>
                                                    N が継続時間以上だと切替前に読み取りが終わります
                                                </div>
                                            )}
                                        </td>
                                        <td>
                                            <button className="btn btn-small" onClick={() => set('readers', req.readers.filter((_, j) => j !== i))}>
                                                削除
                                            </button>
                                        </td>
                                    </tr>
                                );
                            })}
                        </tbody>
                    </table>
                </div>
                <p className="hint">
                    「切替の N 秒前」は、更新処理が BulkCopy を終えたところで読み取りを開始し、読み取り中になったのを確認してから N 秒後に切替えます（N は継続時間より短くしてください。0 なら 0.5 秒後）。
                    「切替がロック待機になってから」は、ロック監視が切替の待機を検出してから N 秒後に開始します（検出できない場合は切替開始から 3 秒で待機中とみなします）。待機しなかった場合は実行しません。
                    「切替の開始から」は、待機の有無に関係なく切替 SQL の開始から N 秒後に開始します（方式どうしを同じ条件で比べるときに使います）。
                </p>
                <button className="btn btn-small" onClick={() =>
                    set('readers', [...req.readers, { kind: 'SlowRead', trigger: 'RunStart', triggerUpdate: 1, startDelaySec: 0, durationSec: 60 }])}>
                    ＋ 読み取りを追加
                </button>

                <div className="run-bar">
                    <div className="hint">
                        想定所要時間：最大 約 {totalSec} 秒 ／ 読み取り {req.readers.length} 件 ／ 更新 {req.updateCount} 回
                    </div>
                    <button className="btn btn-primary btn-large" disabled={busy || !env?.connected || env.runActive} onClick={start}>
                        {env?.runActive ? '他の検証を実行中' : '▶ 検証を開始'}
                    </button>
                </div>
                {error && <div className="alert alert-error">{error}</div>}
            </section>

            <section className="card explain">
                <h2>方式の説明</h2>
                <p className="hint">
                    DROP / sp_rename / ALTER VIEW / シノニムの作り直し / ALTER TABLE SWITCH は、いずれも対象に Sch-M（スキーマ変更）ロックが必要です。
                    実行中の SELECT は最後まで Sch-S（スキーマ安定）ロックを持つため、DDL で入れ替える方式は「読み取りが終わるまで待つ」ことを避けられません。
                    違いが出るのは「待っている間に後から来た SELECT まで止めるか」と「待ちきれないときにどう終わるか」です。
                    DDL を使わない（DML だけで入れ替える）方式なら Sch-M が不要になり、RCSI と組み合わせると読み取りと更新が互いに待たなくなります。
                </p>
                <div className="explain-grid">
                    <div>
                        <h4>現行：テーブル公開</h4>
                        <ol>
                            <li>vt_PublicTable_New を作成</li>
                            <li>vt_Source から BulkCopy</li>
                            <li><code>DROP TABLE vt_PublicTable</code> + <code>sp_rename</code></li>
                        </ol>
                        <p>DROP は Sch-M が必要。読み取り中は待機し、待機中に来た SELECT もその後ろで止まります。</p>
                    </div>
                    <div>
                        <h4>改修案：ビュー公開（ALTER VIEW）</h4>
                        <ol>
                            <li>非アクティブ側（Blue/Green）を空にする</li>
                            <li>vt_Source から BulkCopy</li>
                            <li><code>ALTER VIEW vt_PublicView AS SELECT … FROM 非アクティブ側</code></li>
                        </ol>
                        <p>ALTER VIEW もビューに Sch-M が必要なため、ビューを読み取り中だと現行と同じく待機します。</p>
                    </div>
                    <div>
                        <h4>代替案：ALTER TABLE SWITCH（低優先度）</h4>
                        <ol>
                            <li>vt_SwStage へ BulkCopy</li>
                            <li><code>ALTER TABLE vt_SwPublic SWITCH TO vt_SwOld WITH (WAIT_AT_LOW_PRIORITY …)</code></li>
                            <li><code>ALTER TABLE vt_SwStage SWITCH TO vt_SwPublic</code></li>
                        </ol>
                        <p>
                            切替は読み取りの終了を待ちますが、低優先度の待機は後から来た SELECT を止めません。
                            待機時間を過ぎたら、あきらめる（SELF）か、読み取りを強制終了する（BLOCKERS）かを選べます。公開名はテーブルのままで済みます。
                        </p>
                    </div>
                    <div>
                        <h4>代替案：制御テーブル切替（DDL なし）</h4>
                        <ol>
                            <li>非アクティブ側（A/B）を DELETE で空にする</li>
                            <li>vt_Source から BulkCopy</li>
                            <li><code>UPDATE vt_CtlActive SET ActiveSide = 'B'</code></li>
                        </ol>
                        <p>
                            ビュー vt_CtlView は制御テーブルの値で A/B の片側だけを読む固定定義です。切替は DML なので Sch-M が不要。
                            非アクティブ側を TRUNCATE すると Sch-M が必要になるため DELETE を使います。RCSI 前提です。
                        </p>
                    </div>
                    <div>
                        <h4>代替案：全件同期 MERGE</h4>
                        <ol>
                            <li>vt_DiffStage へ全件を BulkCopy</li>
                            <li><code>MERGE</code> で変わった行の UPDATE・INSERT・連携元に無い行の DELETE</li>
                        </ol>
                        <p>公開テーブルを作り直さないので DDL がありません。RCSI なら読み取りは更新前の行を読み続けます。ログ量と MERGE の時間は増えます。</p>
                    </div>
                    <div>
                        <h4>差分更新（MERGE）</h4>
                        <ol>
                            <li>vt_DiffStage を作成し差分を BulkCopy</li>
                            <li><code>MERGE</code> で公開テーブル（Blue/Green 方式はアクティブ側）へ反映</li>
                        </ol>
                        <p>RCSI が OFF だと S ロックと X ロックが待ち合います。HOLDLOCK などのロックヒントは RCSI でもロックを取ります。</p>
                    </div>
                </div>
            </section>
        </div>
    );
}

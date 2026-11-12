import { useState } from 'react';
import { api } from '../api';
import { modeLabel, readerKindLabel, readerTriggerLabel, updateTypeLabel } from '../format';
import type { EnvironmentStatus, InactivePrep, PublishMode, ReaderKind, ReaderSpec, ReaderTrigger, RunRequest, UpdateType } from '../types';

interface Preset {
    key: string;
    title: string;
    expect: string;
    expectLevel: 'error' | 'success' | 'warn';
    description: string;
    request: RunRequest;
}

// 検証したいパターン：
//   読取1 が切替の直前から読み続ける → 切替（DROP/ALTER）がロック待機 → 待機中に読取2 が新たに SELECT
// 開始タイミングは更新処理のイベントに連動するので、BulkCopy の速さに関係なく必ずこの順序になる
const blockingReaders: ReaderSpec[] = [
    { kind: 'SlowRead', trigger: 'BeforeSwitch', triggerUpdate: 1, startDelaySec: 0, durationSec: 60 },
    { kind: 'SlowRead', trigger: 'SwitchBlocked', triggerUpdate: 1, startDelaySec: 5, durationSec: 10 },
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
};

const presets: Preset[] = [
    {
        key: 'current',
        title: '① 現行方式の再現',
        expect: 'タイムアウト想定',
        expectLevel: 'error',
        description:
            '読取1 が切替の直前から公開テーブルを 60 秒読み続け、DROP TABLE がロック待機（LCK_M_SCH_M）。待機になって 5 秒後に読取2 が新たに SELECT し、読取2 も DROP の後ろで待たされる（LCK_M_SCH_S）ことと、CommandTimeout(30秒) で更新が失敗する現象を再現します。',
        request: { ...base, title: '① 現行: テーブル公開 × 長時間SELECT', mode: 'Table' },
    },
    {
        key: 'view',
        title: '② 改修後（ALTER VIEW）',
        expect: '改修方式の検証',
        expectLevel: 'warn',
        description:
            '①と同じ読み取りパターンをビュー公開で実行。非アクティブ側へ BulkCopy した後 ALTER VIEW で切り替えます。読み取り中のビューに対する ALTER VIEW が待機するか、待機中に来た読取2 が止まるかを確認します。',
        request: { ...base, title: '② 改修後: ビュー公開 × 長時間SELECT', mode: 'View' },
    },
    {
        key: 'view-twice',
        title: '③ 改修後・連続切替',
        expect: '注意点の確認',
        expectLevel: 'warn',
        description:
            '90 秒の読み取り中に 20 秒間隔で 2 回更新。2 回目は「読み取り中の旧テーブル」を TRUNCATE するため、Blue/Green でも待機が起きうるケースを確認します。',
        request: {
            ...base,
            title: '③ 改修後: ビュー公開 × 連続2回切替',
            mode: 'View',
            readers: [{ kind: 'SlowRead', trigger: 'RunStart', startDelaySec: 0, durationSec: 90 }],
            updateCount: 2,
            updateIntervalSec: 20,
        },
    },
    {
        key: 'synonym',
        title: '④ 参考：シノニム',
        expect: '比較用',
        expectLevel: 'warn',
        description: 'DROP SYNONYM / CREATE SYNONYM による切替を①②と同じ読み取りパターンで実行し、ALTER VIEW と比較します。',
        request: { ...base, title: '④ 参考: シノニム公開 × 長時間SELECT', mode: 'Synonym' },
    },
    {
        key: 'diff',
        title: '⑤ 差分更新（MERGE）',
        expect: '待機の確認',
        expectLevel: 'warn',
        description:
            'MERGE の直前から読取1 が HOLDLOCK でテーブルロックを 40 秒保持。MERGE のロック待機と、待機中に来た読取2 の待機を確認します。',
        request: {
            ...base,
            title: '⑤ 差分更新 × HOLDLOCK',
            mode: 'Table',
            updateType: 'Diff',
            readers: [
                { kind: 'HoldLock', trigger: 'BeforeSwitch', triggerUpdate: 1, startDelaySec: 0, durationSec: 40 },
                { kind: 'SlowRead', trigger: 'SwitchBlocked', triggerUpdate: 1, startDelaySec: 5, durationSec: 10 },
            ],
        },
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

    const blueGreen = req.mode !== 'Table' && req.updateType === 'Full';
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
                <div className="presets">
                    {presets.map((p) => (
                        <button
                            key={p.key}
                            className={`preset ${presetKey === p.key ? 'selected' : ''}`}
                            onClick={() => applyPreset(p)}
                        >
                            <div className="preset-head">
                                <strong>{p.title}</strong>
                                <span className={`pill pill-${p.expectLevel}`}>{p.expect}</span>
                            </div>
                            <p>{p.description}</p>
                        </button>
                    ))}
                </div>
            </section>

            <section className="card">
                <div className="card-head">
                    <h2>検証条件</h2>
                </div>

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
                                            <input type="number" min={0} value={r.startDelaySec} disabled={trigger === 'BeforeSwitch'}
                                                onChange={(e) => updateReader(i, { startDelaySec: Number(e.target.value) })} />
                                        </td>
                                        <td>
                                            <input type="number" min={1} value={r.durationSec}
                                                onChange={(e) => updateReader(i, { durationSec: Number(e.target.value) })} />
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
                    「切替の直前」は、更新処理が BulkCopy を終えて切替 SQL を出す前に読み取りを開始し、読み取り中になったのを確認してから切替えます。
                    「切替がロック待機になってから」は、ロック監視が切替の待機を検出してから N 秒後に開始します（検出できない場合は切替開始から 3 秒で待機中とみなします）。
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
                <div className="explain-grid">
                    <div>
                        <h4>現行：テーブル公開</h4>
                        <ol>
                            <li>vt_PublicTable_New を作成</li>
                            <li>vt_Source から BulkCopy</li>
                            <li><code>DROP TABLE vt_PublicTable</code> + <code>sp_rename</code></li>
                        </ol>
                        <p>DROP は Sch-M ロックが必要なため、読み取り中（Sch-S/S 保持）だと待機します。</p>
                    </div>
                    <div>
                        <h4>改修後：ビュー公開（Blue/Green）</h4>
                        <ol>
                            <li>非アクティブ側（Blue/Green）を空にする</li>
                            <li>vt_Source から BulkCopy</li>
                            <li><code>ALTER VIEW vt_PublicView AS SELECT … FROM 非アクティブ側</code></li>
                        </ol>
                        <p>読み取りはアクティブ側を参照し続け、更新は非アクティブ側に対して行います。</p>
                    </div>
                    <div>
                        <h4>差分更新</h4>
                        <ol>
                            <li>vt_DiffStage を作成し差分を BulkCopy</li>
                            <li><code>MERGE</code> で公開テーブル（ビュー方式はアクティブ側）へ反映</li>
                        </ol>
                    </div>
                </div>
            </section>
        </div>
    );
}

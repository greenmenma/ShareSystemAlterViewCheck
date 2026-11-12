import { useEffect, useState } from 'react';
import { api } from '../api';
import {
    actorLabel, dateTime, downloadText, lowPriorityAbortLabel, modeLabel, ms, readerKindLabel, readerWhen, statusLabel, updateTypeLabel,
} from '../format';
import type { RunRecord } from '../types';
import EventLog from './EventLog';
import Timeline from './Timeline';

interface Props {
    id: string;
    onDeleted: () => void;
}

export default function RunDetail({ id, onDeleted }: Props) {
    const [run, setRun] = useState<RunRecord | null>(null);
    const [error, setError] = useState<string | null>(null);

    // 実行中は 1 秒ごとに更新
    useEffect(() => {
        let stopped = false;
        let timer: ReturnType<typeof setTimeout> | undefined;
        const load = async () => {
            try {
                const r = await api.run(id);
                if (stopped) return;
                setRun(r);
                setError(null);
                if (r.status === 'Running') {
                    timer = setTimeout(load, 1000);
                }
            } catch (e) {
                if (stopped) return;
                setError((e as Error).message);
                timer = setTimeout(load, 3000);
            }
        };
        load();
        return () => {
            stopped = true;
            if (timer) clearTimeout(timer);
        };
    }, [id]);

    if (error && !run) return <div className="alert alert-error">{error}</div>;
    if (!run) return <div className="card"><p className="hint">読み込み中…</p></div>;

    const running = run.status === 'Running';
    const s = run.summary;
    const req = run.request;
    const level = running ? 'running' : (s?.verdictLevel ?? 'Info').toLowerCase();

    const exportCsv = () => {
        const esc = (v: string) => `"${v.replace(/"/g, '""')}"`;
        const lines = ['Seq,時刻,経過ms,アクター,レベル,メッセージ,詳細'];
        for (const e of run.events) {
            lines.push([e.seq, e.time, e.elapsedMs, esc(actorLabel(e.actor)), e.level, esc(e.message), esc(e.detail ?? '')].join(','));
        }
        // Excel で文字化けしないよう BOM を付与
        downloadText(`${run.id}.csv`, '﻿' + lines.join('\r\n'), 'text/csv');
    };

    const remove = async () => {
        if (!window.confirm('このログを削除しますか？')) return;
        try {
            await api.remove(run.id);
            onDeleted();
        } catch (e) {
            setError((e as Error).message);
        }
    };

    return (
        <div className="stack">
            <section className={`card verdict verdict-${level}`}>
                <div className="verdict-main">
                    <div>
                        <div className="verdict-label">{running ? '実行中…' : '判定'}</div>
                        <div className="verdict-text">{running ? 'ロック状況を監視しています' : s?.verdict ?? '—'}</div>
                    </div>
                    <div className="verdict-actions">
                        {running && (
                            <button className="btn btn-danger" onClick={() => api.cancel(run.id).catch((e) => setError(e.message))}>
                                キャンセル
                            </button>
                        )}
                        <button className="btn" onClick={exportCsv}>CSV</button>
                        <button className="btn" onClick={() => downloadText(`${run.id}.json`, JSON.stringify(run, null, 2), 'application/json')}>
                            JSON
                        </button>
                        {!running && <button className="btn" onClick={remove}>削除</button>}
                    </div>
                </div>
                <div className="verdict-meta">
                    <span><b>{run.title}</b></span>
                    <span>ID: {run.id}</span>
                    <span>開始: {dateTime(run.startedAt)}</span>
                    <span>終了: {dateTime(run.endedAt)}</span>
                    <span>状態: {statusLabel[run.status]}</span>
                    {run.serverInfo && <span>接続先: {run.serverInfo}</span>}
                </div>
                {error && <div className="alert alert-error">{error}</div>}
            </section>

            {run.kind === 'Verification' && s && !running && (
                <section className="metrics">
                    <Metric label="最大ロック待機" value={ms(s.maxBlockedMs)} level={s.maxBlockedMs >= 1000 ? 'warn' : 'success'} />
                    <Metric label="ブロック発生回数" value={`${s.blockCount} 回`} level={s.blockCount > 0 ? 'warn' : 'success'} />
                    <Metric label="タイムアウト" value={`${s.timeoutCount} 件`} level={s.timeoutCount > 0 ? 'error' : 'success'} />
                    <Metric label="エラー" value={`${s.errorCount} 件`} level={s.errorCount > 0 ? 'error' : 'success'} />
                    {s.readerMaxBlockedMs != null && (
                        <Metric label="後続の読み取りの待機" value={ms(s.readerMaxBlockedMs)} level={s.readerMaxBlockedMs >= 1000 ? 'warn' : 'success'} />
                    )}
                    {s.switchOverlapped != null && s.readers.length > 0 && (
                        <Metric label="切替時の読み取り" value={s.switchOverlapped ? '実行中（成立）' : 'なし（不成立）'} level={s.switchOverlapped ? 'success' : 'warn'} />
                    )}
                    <Metric
                        label="切替/反映 最大時間"
                        value={s.updates.some((u) => u.switchMs != null)
                            ? ms(Math.max(...s.updates.map((u) => u.switchMs ?? 0)))
                            : '—（未完了）'}
                        level="info"
                    />
                </section>
            )}

            {req && (
                <section className="card">
                    <div className="card-head"><h2>検証条件</h2></div>
                    <div className="cond">
                        <span><b>公開方式</b>{modeLabel[req.mode]}</span>
                        <span><b>更新種別</b>{updateTypeLabel[req.updateType]}{req.updateType === 'Diff' ? `（${req.diffRowCount.toLocaleString()}行）` : ''}</span>
                        <span><b>更新</b>{req.updaterStartDelaySec}秒後 × {req.updateCount}回{req.updateCount > 1 ? `（間隔${req.updateIntervalSec}秒）` : ''}</span>
                        <span><b>CommandTimeout</b>{req.commandTimeoutSec}秒</span>
                        <span><b>LOCK_TIMEOUT</b>{req.lockTimeoutMs < 0 ? '無制限' : `${req.lockTimeoutMs}ms`}</span>
                        {run.readCommittedSnapshot != null && <span><b>RCSI</b>{run.readCommittedSnapshot ? 'ON' : 'OFF'}</span>}
                        {['View', 'Synonym', 'Control'].includes(req.mode) && req.updateType === 'Full' && <span><b>非アクティブ準備</b>{req.inactivePrep}</span>}
                        {req.mode === 'Switch' && req.updateType === 'Full' && (
                            <span><b>SWITCH 低優先度</b>{req.switchLowPriority === false
                                ? 'なし'
                                : `MAX_DURATION ${req.lowPriorityMaxMinutes ?? 1}分 / ${lowPriorityAbortLabel[req.lowPriorityAbort ?? 'Self'].split('（')[0]}`}</span>
                        )}
                        {req.readers.map((r, i) => (
                            <span key={i}><b>読取{i + 1}</b>{readerKindLabel[r.kind].split('（')[0]} / {readerWhen(r)} / {r.durationSec}秒間</span>
                        ))}
                        {req.memo && <span><b>メモ</b>{req.memo}</span>}
                    </div>
                </section>
            )}

            <section className="card">
                <div className="card-head">
                    <h2>タイムライン</h2>
                    <div className="legend">
                        <span><i className="lg lg-work" />処理</span>
                        <span><i className="lg lg-wait" />開始待ち</span>
                        <span><i className="lg lg-blocked" />ロック待機</span>
                        <span><i className="lg lg-timeout" />タイムアウト/エラー</span>
                    </div>
                </div>
                <Timeline run={run} />
            </section>

            {s && !running && (s.updates.length > 0 || s.readers.length > 0) && (
                <section className="card">
                    <div className="card-head"><h2>結果</h2></div>
                    {s.updates.length > 0 && (
                        <div className="table-wrap">
                            <table className="grid">
                                <thead>
                                    <tr>
                                        <th>更新</th><th>結果</th><th>対象</th><th className="num">準備</th><th className="num">BulkCopy</th>
                                        <th className="num">切替/反映</th><th className="num">合計</th><th className="num">行数</th>
                                    </tr>
                                </thead>
                                <tbody>
                                    {s.updates.map((u) => (
                                        <tr key={u.index}>
                                            <td>#{u.index}</td>
                                            <td><span className={`pill ${u.success ? 'pill-success' : 'pill-error'}`}>{u.result}</span>
                                                {u.error && <div className="error-text small">{u.error}</div>}</td>
                                            <td className="mono">{u.fromTable} → {u.toTable}</td>
                                            <td className="num">{ms(u.prepareMs)}</td>
                                            <td className="num">{ms(u.bulkCopyMs)}</td>
                                            <td className="num strong">{ms(u.switchMs)}</td>
                                            <td className="num">{ms(u.totalMs)}</td>
                                            <td className="num">{u.rowsCopied.toLocaleString()}</td>
                                        </tr>
                                    ))}
                                </tbody>
                            </table>
                        </div>
                    )}
                    {s.readers.length > 0 && (
                        <div className="table-wrap">
                            <table className="grid">
                                <thead>
                                    <tr><th>読取</th><th>種類</th><th>開始</th><th>結果</th><th>対象（参照先）</th><th className="num">最初の行まで</th><th className="num">行数</th><th className="num">時間</th></tr>
                                </thead>
                                <tbody>
                                    {s.readers.map((r) => (
                                        <tr key={r.actor}>
                                            <td>{actorLabel(r.actor)}</td>
                                            <td>{readerKindLabel[r.kind].split('（')[0]}</td>
                                            <td className="small">{r.startMs != null ? `+${(r.startMs / 1000).toFixed(1)}s` : '—'}</td>
                                            <td><span className={`pill ${r.success ? 'pill-success' : 'pill-error'}`}>{r.result}</span>
                                                {r.error && <div className="error-text small">{r.error}</div>}</td>
                                            <td className="mono">{r.target}{r.resolvedTable && r.resolvedTable !== r.target ? `（${r.resolvedTable}）` : ''}</td>
                                            <td className={`num ${(r.firstRowMs ?? 0) >= 1000 ? 'strong error-text' : ''}`}>{ms(r.firstRowMs)}</td>
                                            <td className="num">{r.rowsRead.toLocaleString()}</td>
                                            <td className="num">{ms(r.durationMs)}</td>
                                        </tr>
                                    ))}
                                </tbody>
                            </table>
                        </div>
                    )}
                </section>
            )}

            <section className="card">
                <div className="card-head"><h2>実行ログ（{run.events.length} 件）</h2></div>
                <EventLog events={run.events} running={running} />
            </section>
        </div>
    );
}

function Metric({ label, value, level }: { label: string; value: string; level: string }) {
    return (
        <div className={`metric metric-${level}`}>
            <div className="metric-label">{label}</div>
            <div className="metric-value">{value}</div>
        </div>
    );
}

import { useEffect, useState } from 'react';
import { actorClass, actorLabel, ms } from '../format';
import type { RunPhase, RunRecord } from '../types';

interface Props {
    run: RunRecord;
}

/** アクターごとのレーンにフェーズを横棒で表示する */
export default function Timeline({ run }: Props) {
    const running = run.status === 'Running';
    const [now, setNow] = useState(() => Date.now());

    useEffect(() => {
        if (!running) return;
        const t = setInterval(() => setNow(Date.now()), 250);
        return () => clearInterval(t);
    }, [running]);

    const lastEventMs = run.events.length ? run.events[run.events.length - 1].elapsedMs : 0;
    const liveMs = running ? Math.max(lastEventMs, now - new Date(run.startedAt).getTime()) : lastEventMs;
    const endOf = (p: RunPhase) => p.endMs ?? liveMs;
    const totalMs = Math.max(1000, liveMs, ...run.phases.map(endOf));

    // レーンの並び順：読み取り → 更新 → その他
    const actors = Array.from(new Set(run.phases.map((p) => p.actor))).sort((a, b) => order(a) - order(b) || a.localeCompare(b));

    const tickStep = niceStep(totalMs / 10);
    const ticks: number[] = [];
    for (let t = 0; t <= totalMs; t += tickStep) ticks.push(t);

    const pct = (v: number) => `${(v / totalMs) * 100}%`;

    if (run.phases.length === 0) {
        return <p className="hint">フェーズはまだありません。</p>;
    }

    return (
        <div className="timeline">
            <div className="tl-axis">
                <div className="tl-label" />
                <div className="tl-track">
                    {ticks.map((t) => (
                        <span key={t} className="tl-tick" style={{ left: pct(t) }}>
                            {(t / 1000).toFixed(tickStep < 1000 ? 1 : 0)}s
                        </span>
                    ))}
                </div>
            </div>
            {actors.map((actor) => {
                const phases = run.phases.filter((p) => p.actor === actor);
                const work = phases.filter((p) => p.kind !== 'Blocked');
                const blocked = phases.filter((p) => p.kind === 'Blocked');
                return (
                    <div className="tl-row" key={actor}>
                        <div className={`tl-label ${actorClass(actor)}`}>{actorLabel(actor)}</div>
                        <div className="tl-track">
                            {ticks.map((t) => (
                                <span key={t} className="tl-grid" style={{ left: pct(t) }} />
                            ))}
                            {work.map((p) => (
                                <div
                                    key={p.id}
                                    className={`tl-bar kind-${p.kind.toLowerCase()} st-${p.status.toLowerCase()}`}
                                    style={{ left: pct(p.startMs), width: `max(2px, ${pct(endOf(p) - p.startMs)})` }}
                                    title={`${p.name}\n${(p.startMs / 1000).toFixed(2)}s 〜 ${(endOf(p) / 1000).toFixed(2)}s（${ms(endOf(p) - p.startMs)}）\n状態: ${p.status}${p.note ? '\n' + p.note : ''}`}
                                >
                                    <span>{p.kind === 'Wait' ? '' : shortName(p.name)}</span>
                                </div>
                            ))}
                            {blocked.map((p) => (
                                <div
                                    key={p.id}
                                    className={`tl-block ${p.endMs == null ? 'live' : ''}`}
                                    style={{ left: pct(p.startMs), width: `max(3px, ${pct(endOf(p) - p.startMs)})` }}
                                    title={`${p.name}\n${p.note ?? ''}\n${(p.startMs / 1000).toFixed(2)}s 〜 ${(endOf(p) / 1000).toFixed(2)}s（${ms(endOf(p) - p.startMs)}）`}
                                >
                                    <span>待機 {ms(endOf(p) - p.startMs)}</span>
                                </div>
                            ))}
                        </div>
                    </div>
                );
            })}
            {running && (
                <div className="tl-now-row">
                    <div className="tl-label" />
                    <div className="tl-track">
                        <span className="tl-now" style={{ left: pct(liveMs) }}>現在 {(liveMs / 1000).toFixed(1)}s</span>
                    </div>
                </div>
            )}
        </div>
    );
}

function order(actor: string) {
    if (actor.startsWith('Reader')) return 0;
    if (actor === 'Updater') return 1;
    if (actor.startsWith('Updater')) return 2;
    return 3;
}

function shortName(name: string) {
    // 「更新1: 切替 ALTER VIEW → vt_View_Green」→「切替 ALTER VIEW → vt_View_Green」
    const i = name.indexOf(': ');
    return i >= 0 && name.startsWith('更新') ? name.slice(i + 2) : name;
}

function niceStep(raw: number) {
    const candidates = [100, 200, 500, 1000, 2000, 5000, 10000, 15000, 30000, 60000, 120000, 300000, 600000];
    return candidates.find((c) => c >= raw) ?? 600000;
}

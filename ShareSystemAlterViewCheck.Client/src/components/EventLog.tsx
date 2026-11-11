import { Fragment, useEffect, useMemo, useRef, useState } from 'react';
import { actorClass, actorLabel, elapsed, timeOnly } from '../format';
import type { Level, RunEvent } from '../types';

interface Props {
    events: RunEvent[];
    running: boolean;
}

const levels: { key: Level; label: string }[] = [
    { key: 'Error', label: 'エラー' },
    { key: 'Warn', label: '警告' },
    { key: 'Success', label: '成功' },
    { key: 'Info', label: '情報' },
    { key: 'Sql', label: 'SQL' },
];

const levelIcon: Record<Level, string> = {
    Error: '✖',
    Warn: '⚠',
    Success: '✔',
    Info: 'ℹ',
    Sql: '⌨',
};

export default function EventLog({ events, running }: Props) {
    const [enabled, setEnabled] = useState<Record<Level, boolean>>({
        Error: true, Warn: true, Success: true, Info: true, Sql: true,
    });
    const [actorFilter, setActorFilter] = useState<string>('');
    const [text, setText] = useState('');
    const [expanded, setExpanded] = useState<Set<number>>(new Set());
    const [expandAll, setExpandAll] = useState(false);
    const [follow, setFollow] = useState(true);
    const bodyRef = useRef<HTMLDivElement>(null);

    const actors = useMemo(() => Array.from(new Set(events.map((e) => e.actor))), [events]);

    const filtered = events.filter(
        (e) =>
            enabled[e.level] &&
            (!actorFilter || e.actor === actorFilter) &&
            (!text || e.message.includes(text) || (e.detail ?? '').includes(text)),
    );

    useEffect(() => {
        if (running && follow) {
            // ページ全体ではなくログ領域だけをスクロールする
            const el = bodyRef.current;
            if (el) el.scrollTop = el.scrollHeight;
        }
    }, [events.length, running, follow]);

    const toggle = (seq: number) =>
        setExpanded((s) => {
            const n = new Set(s);
            if (n.has(seq)) n.delete(seq);
            else n.add(seq);
            return n;
        });

    return (
        <div className="eventlog">
            <div className="eventlog-toolbar">
                {levels.map((l) => (
                    <label key={l.key} className={`check level-chip lv-${l.key.toLowerCase()}`}>
                        <input
                            type="checkbox"
                            checked={enabled[l.key]}
                            onChange={(e) => setEnabled({ ...enabled, [l.key]: e.target.checked })}
                        />
                        {levelIcon[l.key]} {l.label}（{events.filter((e) => e.level === l.key).length}）
                    </label>
                ))}
                <select value={actorFilter} onChange={(e) => setActorFilter(e.target.value)}>
                    <option value="">すべてのアクター</option>
                    {actors.map((a) => (
                        <option key={a} value={a}>{actorLabel(a)}</option>
                    ))}
                </select>
                <input placeholder="文字列で絞り込み" value={text} onChange={(e) => setText(e.target.value)} />
                <label className="check">
                    <input type="checkbox" checked={expandAll} onChange={(e) => setExpandAll(e.target.checked)} />
                    詳細をすべて展開
                </label>
                {running && (
                    <label className="check">
                        <input type="checkbox" checked={follow} onChange={(e) => setFollow(e.target.checked)} />
                        自動スクロール
                    </label>
                )}
            </div>

            <div className="eventlog-body" ref={bodyRef}>
                <table className="grid log">
                    <thead>
                        <tr>
                            <th>経過</th>
                            <th>時刻</th>
                            <th>アクター</th>
                            <th>種別</th>
                            <th>メッセージ</th>
                        </tr>
                    </thead>
                    <tbody>
                        {filtered.map((e) => {
                            const open = expandAll || expanded.has(e.seq);
                            return (
                                <Fragment key={e.seq}>
                                    <tr
                                        className={`lv-row lv-${e.level.toLowerCase()} ${e.detail ? 'clickable' : ''}`}
                                        onClick={() => e.detail && toggle(e.seq)}
                                    >
                                        <td className="mono nowrap">{elapsed(e.elapsedMs)}</td>
                                        <td className="mono nowrap">{timeOnly(e.time)}</td>
                                        <td className="nowrap"><span className={`actor ${actorClass(e.actor)}`}>{actorLabel(e.actor)}</span></td>
                                        <td className="nowrap"><span className={`lv lv-${e.level.toLowerCase()}`}>{levelIcon[e.level]} {e.level}</span></td>
                                        <td>
                                            {e.message}
                                            {e.detail && <span className="detail-toggle">{open ? '▲ 詳細' : '▼ 詳細'}</span>}
                                        </td>
                                    </tr>
                                    {e.detail && open && (
                                        <tr className="detail-row">
                                            <td colSpan={5}><pre>{e.detail}</pre></td>
                                        </tr>
                                    )}
                                </Fragment>
                            );
                        })}
                    </tbody>
                </table>
            </div>
        </div>
    );
}

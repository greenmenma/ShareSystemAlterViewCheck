import { useCallback, useEffect, useState } from 'react';
import { api } from '../api';
import { dateTime, modeShort, ms, statusLabel, updateTypeLabel } from '../format';
import type { RunListItem } from '../types';
import RunDetail from './RunDetail';

interface Props {
    selectedId: string | null;
    onSelect: (id: string | null) => void;
}

export default function RunLogs({ selectedId, onSelect }: Props) {
    const [items, setItems] = useState<RunListItem[]>([]);
    const [error, setError] = useState<string | null>(null);
    const [showSetup, setShowSetup] = useState(false);

    const refresh = useCallback(async () => {
        try {
            setItems(await api.runs());
            setError(null);
        } catch (e) {
            setError((e as Error).message);
        }
    }, []);

    useEffect(() => {
        refresh();
        const t = setInterval(refresh, 3000);
        return () => clearInterval(t);
    }, [refresh]);

    // 未選択なら最新を選択
    useEffect(() => {
        if (!selectedId && items.length > 0) {
            onSelect(items[0].id);
        }
    }, [items, selectedId, onSelect]);

    const visible = items.filter((x) => showSetup || x.kind === 'Verification' || x.id === selectedId);

    return (
        <div className="logs-layout">
            <aside className="card run-list">
                <div className="card-head">
                    <h2>検証ログ一覧</h2>
                    <label className="check">
                        <input type="checkbox" checked={showSetup} onChange={(e) => setShowSetup(e.target.checked)} />
                        セットアップも表示
                    </label>
                </div>
                {error && <div className="alert alert-error">{error}</div>}
                {visible.length === 0 && <p className="hint">ログはまだありません。</p>}
                <ul>
                    {visible.map((x) => (
                        <li key={x.id}>
                            <button
                                className={`run-item level-${(x.verdictLevel ?? (x.status === 'Running' ? 'running' : 'info')).toLowerCase()} ${
                                    x.id === selectedId ? 'selected' : ''
                                }`}
                                onClick={() => onSelect(x.id)}
                            >
                                <div className="run-item-head">
                                    <span className="run-item-time">{dateTime(x.startedAt)}</span>
                                    <span className={`status status-${x.status.toLowerCase()}`}>{statusLabel[x.status]}</span>
                                </div>
                                <div className="run-item-title">{x.title}</div>
                                {x.kind === 'Verification' && (
                                    <div className="run-item-tags">
                                        {x.mode && <span className={`tag tag-${x.mode.toLowerCase()}`}>{modeShort[x.mode]}</span>}
                                        {x.updateType && <span className="tag">{updateTypeLabel[x.updateType]}</span>}
                                        <span className="tag">最大待機 {ms(x.maxBlockedMs)}</span>
                                    </div>
                                )}
                                {x.verdict && <div className="run-item-verdict">{x.verdict}</div>}
                            </button>
                        </li>
                    ))}
                </ul>
            </aside>

            <section className="run-detail-wrap">
                {selectedId ? (
                    <RunDetail
                        key={selectedId}
                        id={selectedId}
                        onDeleted={() => {
                            onSelect(null);
                            refresh();
                        }}
                    />
                ) : (
                    <div className="card">
                        <p className="hint">左の一覧からログを選択してください。</p>
                    </div>
                )}
            </section>
        </div>
    );
}

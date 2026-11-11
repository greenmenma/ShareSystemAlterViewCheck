import { useCallback, useEffect, useState } from 'react';
import { api } from './api';
import type { EnvironmentStatus } from './types';
import EnvironmentPanel from './components/EnvironmentPanel';
import RunForm from './components/RunForm';
import RunLogs from './components/RunLogs';

type Tab = 'run' | 'logs' | 'env';

const tabs: { key: Tab; label: string }[] = [
    { key: 'run', label: '検証実行' },
    { key: 'logs', label: '検証ログ' },
    { key: 'env', label: '環境セットアップ' },
];

export default function App() {
    const [tab, setTab] = useState<Tab>('run');
    const [env, setEnv] = useState<EnvironmentStatus | null>(null);
    const [envLoading, setEnvLoading] = useState(false);
    const [selectedRunId, setSelectedRunId] = useState<string | null>(null);

    const refreshEnv = useCallback(async () => {
        setEnvLoading(true);
        try {
            setEnv(await api.environment());
        } catch (e) {
            setEnv({
                connected: false, error: (e as Error).message, hasViewServerState: false, isSetUp: false,
                objects: [], runActive: false,
            });
        } finally {
            setEnvLoading(false);
        }
    }, []);

    useEffect(() => {
        refreshEnv();
        const t = setInterval(refreshEnv, 10000);
        return () => clearInterval(t);
    }, [refreshEnv]);

    // 実行を開始したらログ画面でその実行を表示する
    const openRun = (id: string) => {
        setSelectedRunId(id);
        setTab('logs');
        setTimeout(refreshEnv, 500);
    };

    return (
        <div className="app">
            <header className="app-header">
                <div className="brand">
                    <span className="brand-icon" aria-hidden>🔒</span>
                    <div>
                        <h1>SQL Server ロック検証</h1>
                        <p>テーブル公開（DROP + RENAME）と ビュー公開（ALTER VIEW / Blue-Green）の比較</p>
                    </div>
                </div>
                <div className="conn">
                    <span className={`dot ${env?.connected ? 'ok' : 'ng'}`} />
                    <div>
                        <div className="conn-main">
                            {env?.dataSource ?? '未接続'} / {env?.database ?? '-'}
                        </div>
                        <div className="conn-sub">
                            {env?.connected
                                ? `${env.loginInfo ?? ''} ${env.isSetUp ? '・セットアップ済み' : '・未セットアップ'}`
                                : env?.error ?? '接続確認中…'}
                        </div>
                    </div>
                    {env?.runActive && (
                        <button className="badge-running" onClick={() => env.activeRunId && openRun(env.activeRunId)}>
                            実行中
                        </button>
                    )}
                </div>
            </header>

            <nav className="tabs">
                {tabs.map((t) => (
                    <button key={t.key} className={tab === t.key ? 'active' : ''} onClick={() => setTab(t.key)}>
                        {t.label}
                    </button>
                ))}
            </nav>

            <main className="content">
                {tab === 'run' && <RunForm env={env} onStarted={openRun} />}
                {tab === 'logs' && <RunLogs selectedId={selectedRunId} onSelect={setSelectedRunId} />}
                {tab === 'env' && (
                    <EnvironmentPanel env={env} loading={envLoading} onRefresh={refreshEnv} onStarted={openRun} />
                )}
            </main>
        </div>
    );
}

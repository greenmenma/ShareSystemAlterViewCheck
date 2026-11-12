import { useState } from 'react';
import { api } from '../api';
import type { EnvironmentStatus } from '../types';

interface Props {
    env: EnvironmentStatus | null;
    loading: boolean;
    onRefresh: () => void;
    onStarted: (runId: string) => void;
}

export default function EnvironmentPanel({ env, loading, onRefresh, onStarted }: Props) {
    const [rowCount, setRowCount] = useState(300000);
    const [error, setError] = useState<string | null>(null);
    const [busy, setBusy] = useState(false);

    const run = async (action: () => Promise<{ id: string }>) => {
        setBusy(true);
        setError(null);
        try {
            const { id } = await action();
            onStarted(id);
        } catch (e) {
            setError((e as Error).message);
        } finally {
            setBusy(false);
        }
    };

    return (
        <div className="stack">
            <section className="card">
                <div className="card-head">
                    <h2>接続先</h2>
                    <button className="btn" onClick={onRefresh} disabled={loading}>
                        {loading ? '確認中…' : '再確認'}
                    </button>
                </div>
                <dl className="kv">
                    <dt>サーバー</dt>
                    <dd>{env?.dataSource ?? '—'}</dd>
                    <dt>データベース</dt>
                    <dd>{env?.database ?? '—'}</dd>
                    <dt>認証</dt>
                    <dd>{env?.loginInfo ?? '—'}</dd>
                    <dt>バージョン</dt>
                    <dd>{env?.serverVersion ?? '—'}</dd>
                    <dt>接続状態</dt>
                    <dd>
                        {env?.connected ? (
                            <span className="pill pill-success">接続OK</span>
                        ) : (
                            <span className="pill pill-error">接続NG</span>
                        )}
                        {env?.error && <div className="error-text">{env.error}</div>}
                    </dd>
                    <dt>VIEW SERVER STATE</dt>
                    <dd>
                        {env?.hasViewServerState ? (
                            <span className="pill pill-success">あり</span>
                        ) : (
                            <>
                                <span className="pill pill-warn">なし</span>
                                <span className="hint">
                                    他セッションのブロッキング監視に必要です（GRANT VIEW SERVER STATE TO [ログイン]）
                                </span>
                            </>
                        )}
                    </dd>
                </dl>
                <p className="hint">接続文字列は Server の appsettings.json の ConnectionStrings:VerifyDb で設定します。</p>
            </section>

            <section className="card">
                <div className="card-head">
                    <h2>READ_COMMITTED_SNAPSHOT（RCSI）</h2>
                    {env?.readCommittedSnapshot == null ? (
                        <span className="pill">不明</span>
                    ) : env.readCommittedSnapshot ? (
                        <span className="pill pill-success">ON</span>
                    ) : (
                        <span className="pill pill-warn">OFF</span>
                    )}
                </div>
                <p>
                    RCSI が ON だと、通常の SELECT（READ COMMITTED）は S ロックを取らず、更新前の行バージョンを読みます。
                    UPDATE / DELETE / MERGE などの DML と読み取りが互いに待たなくなるため、「制御テーブル切替」「全件同期 MERGE」「差分更新」はこの設定が前提です。
                    DROP / ALTER VIEW / SWITCH などの DDL が待つ問題（Sch-M ロック）は、RCSI では解消されません。
                </p>
                <p className="hint">
                    変更には ALTER DATABASE 権限が必要です。この DB に接続中の他のセッションは切断されます（WITH ROLLBACK IMMEDIATE）。
                    ON にすると tempdb にバージョンストアが作られ、長い読み取りの間は更新前の行が tempdb に残ります。
                    {env && !env.canAlterDatabase && <strong> 現在のログインには ALTER DATABASE 権限がありません。</strong>}
                </p>
                <div className="form-row">
                    <button
                        className="btn"
                        disabled={busy || !env?.connected || env.runActive || env.readCommittedSnapshot === true}
                        onClick={() => {
                            if (window.confirm('READ_COMMITTED_SNAPSHOT を ON にします。この DB に接続中の他のセッションは切断されます。よろしいですか？')) {
                                run(() => api.setRcsi(true));
                            }
                        }}
                    >
                        ON にする
                    </button>
                    <button
                        className="btn"
                        disabled={busy || !env?.connected || env.runActive || env.readCommittedSnapshot === false}
                        onClick={() => {
                            if (window.confirm('READ_COMMITTED_SNAPSHOT を OFF にします。この DB に接続中の他のセッションは切断されます。よろしいですか？')) {
                                run(() => api.setRcsi(false));
                            }
                        }}
                    >
                        OFF にする
                    </button>
                </div>
            </section>

            <section className="card">
                <div className="card-head">
                    <h2>検証用オブジェクト</h2>
                </div>
                <div className="table-wrap">
                    <table className="grid">
                        <thead>
                            <tr>
                                <th>オブジェクト</th>
                                <th>役割</th>
                                <th>種類</th>
                                <th className="num">行数</th>
                                <th>参照先</th>
                            </tr>
                        </thead>
                        <tbody>
                            {(env?.objects ?? []).map((o) => (
                                <tr key={o.name} className={o.exists ? '' : 'muted'}>
                                    <td className="mono">{o.name}</td>
                                    <td>{o.role}</td>
                                    <td>{o.exists ? o.type : <span className="pill">なし</span>}</td>
                                    <td className="num">{o.rows?.toLocaleString() ?? ''}</td>
                                    <td className="mono">{o.definition ?? ''}</td>
                                </tr>
                            ))}
                        </tbody>
                    </table>
                </div>
            </section>

            <section className="card">
                <div className="card-head">
                    <h2>セットアップ</h2>
                </div>
                <p>
                    連携元テーブル（vt_Source）にテストデータを生成し、現行方式の公開テーブル、改修後の Blue/Green
                    テーブルと公開ビュー、参考のシノニム、代替案（SWITCH・制御テーブル切替）のテーブルとビューを作り直します。既存の検証用オブジェクト（vt_ で始まるもの）は削除されます。
                </p>
                <p className="hint">
                    低速読み取りで「読み取り中にロックが保持され続ける」状態を作るには、ネットワークバッファに収まらない程度のデータ量が必要です（1 行約 0.9KB。30 万行以上を推奨）。
                </p>
                <div className="form-row">
                    <label>
                        生成行数
                        <input
                            type="number"
                            min={1000}
                            step={10000}
                            value={rowCount}
                            onChange={(e) => setRowCount(Number(e.target.value))}
                        />
                    </label>
                    <button
                        className="btn btn-primary"
                        disabled={busy || !env?.connected || env.runActive}
                        onClick={() => run(() => api.setup(rowCount))}
                    >
                        セットアップ実行
                    </button>
                    <button
                        className="btn btn-danger"
                        disabled={busy || !env?.connected || env.runActive}
                        onClick={() => {
                            if (window.confirm('検証用オブジェクト（vt_*）をすべて削除しますか？')) {
                                run(() => api.drop());
                            }
                        }}
                    >
                        検証用オブジェクト削除
                    </button>
                </div>
                {error && <div className="alert alert-error">{error}</div>}
            </section>
        </div>
    );
}

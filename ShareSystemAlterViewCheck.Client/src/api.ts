import type { EnvironmentStatus, RunListItem, RunRecord, RunRequest } from './types';

// IIS のサブフォルダで動くよう、先頭に / を付けない相対パスで呼び出す
async function request<T>(path: string, init?: RequestInit): Promise<T> {
    const res = await fetch(`api/${path}`, {
        ...init,
        headers: { 'Content-Type': 'application/json', ...(init?.headers ?? {}) },
    });
    if (!res.ok) {
        let message = `${res.status} ${res.statusText}`;
        try {
            const body = await res.json();
            if (body?.message) message = body.message;
        } catch {
            // 本文なし
        }
        throw new Error(message);
    }
    const text = await res.text();
    return (text ? JSON.parse(text) : undefined) as T;
}

export const api = {
    environment: () => request<EnvironmentStatus>('environment'),
    setup: (rowCount: number) =>
        request<{ id: string }>('environment/setup', { method: 'POST', body: JSON.stringify({ rowCount }) }),
    drop: () => request<{ id: string }>('environment/drop', { method: 'POST' }),
    runs: () => request<RunListItem[]>('runs'),
    run: (id: string) => request<RunRecord>(`runs/${encodeURIComponent(id)}`),
    start: (req: RunRequest) => request<{ id: string }>('runs', { method: 'POST', body: JSON.stringify(req) }),
    cancel: (id: string) => request<void>(`runs/${encodeURIComponent(id)}/cancel`, { method: 'POST' }),
    remove: (id: string) => request<void>(`runs/${encodeURIComponent(id)}`, { method: 'DELETE' }),
};

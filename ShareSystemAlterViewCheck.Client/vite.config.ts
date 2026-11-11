import { defineConfig, type UserConfig } from 'vite';
import react from '@vitejs/plugin-react';
import fs from 'node:fs';
import path from 'node:path';
import child_process from 'node:child_process';
import { env } from 'node:process';

// 開発サーバー用：ASP.NET Core 開発証明書を PEM で書き出して Vite を HTTPS で起動する
function getDevCertificate() {
    const baseFolder =
        env.APPDATA !== undefined && env.APPDATA !== ''
            ? `${env.APPDATA}/ASP.NET/https`
            : `${env.HOME}/.aspnet/https`;
    const certificateName = 'sharesystemalterviewcheck.client';
    const certFilePath = path.join(baseFolder, `${certificateName}.pem`);
    const keyFilePath = path.join(baseFolder, `${certificateName}.key`);

    if (!fs.existsSync(baseFolder)) {
        fs.mkdirSync(baseFolder, { recursive: true });
    }

    if (!fs.existsSync(certFilePath) || !fs.existsSync(keyFilePath)) {
        const result = child_process.spawnSync(
            'dotnet',
            ['dev-certs', 'https', '--export-path', certFilePath, '--format', 'Pem', '--no-password'],
            { stdio: 'inherit' },
        );
        if (result.status !== 0) {
            throw new Error('開発用証明書を作成できませんでした。');
        }
    }

    return { key: fs.readFileSync(keyFilePath), cert: fs.readFileSync(certFilePath) };
}

// API の転送先（ASP.NET Core 側）。SpaProxy / Aspire から渡される環境変数を優先する
function getApiTarget() {
    if (env.ASPNETCORE_HTTPS_PORT) {
        return `https://localhost:${env.ASPNETCORE_HTTPS_PORT}`;
    }
    if (env.ASPNETCORE_URLS) {
        const https = env.ASPNETCORE_URLS.split(';').find((u) => u.startsWith('https://'));
        if (https) {
            return https;
        }
    }
    return 'https://localhost:7231';
}

export default defineConfig(({ command }): UserConfig => {
    if (command === 'build') {
        // 発行時：IIS のサブフォルダ（仮想アプリケーション）で動くよう相対パスで出力する
        return {
            base: './',
            plugins: [react()],
            build: { outDir: 'dist', emptyOutDir: true },
        };
    }

    return {
        base: '/',
        plugins: [react()],
        server: {
            port: parseInt(env.DEV_SERVER_PORT || '52731'),
            strictPort: true,
            https: getDevCertificate(),
            proxy: {
                '^/api': {
                    target: getApiTarget(),
                    secure: false,
                },
            },
        },
    };
});

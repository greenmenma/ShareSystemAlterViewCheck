# ShareSystemAlterViewCheck

SQL Server のデータ公開システムで発生する **全件更新時のロック待機（DROP TABLE が長時間 SELECT にブロックされタイムアウトする問題）** を検証環境で再現し、
**ビュー公開 + ALTER VIEW（Blue/Green 構成）** へ改修したときに問題が解消されるかを確認するための Web アプリです。

## 構成

| プロジェクト | 内容 |
| --- | --- |
| `ShareSystemAlterViewCheck.Client` | React + TypeScript + Vite（esproj） |
| `ShareSystemAlterViewCheck.Server` | ASP.NET Core (.NET 10) Minimal API |
| `ShareSystemAlterViewCheck.AppHost` | .NET Aspire AppHost（デバッグ専用・発行対象外） |

- ソリューション：`ShareSystemAlterViewCheck.slnx`（Visual Studio 2022 17.13 以降 / Visual Studio 2026）
- HTTPS のみで動作します（HTTP は HTTPS にリダイレクト、本番では HSTS）。
- ホスト名だけ（例：`https://server01/...`）でアクセスされた場合は、設定したドメインを付与した URL（`https://server01.example.local/...`）にリダイレクトします。
- IIS の Default Web Site 配下のサブフォルダ（仮想アプリケーション）で動作します（画面・API とも相対パス）。

## 検証の内容

| 方式 | 全件更新の手順 |
| --- | --- |
| 現行：テーブル公開 | `vt_PublicTable_New` 作成 → `vt_Source` から BulkCopy → `DROP TABLE vt_PublicTable` + `sp_rename` |
| 改修後：ビュー公開（Blue/Green） | 非アクティブ側（`vt_View_Blue` / `vt_View_Green`）を空にする → BulkCopy → `ALTER VIEW vt_PublicView` |
| 参考：シノニム公開 | 非アクティブ側を空にする → BulkCopy → `DROP SYNONYM` / `CREATE SYNONYM` |
| 差分更新 | `vt_DiffStage` に差分を BulkCopy → 公開テーブル（ビュー方式はアクティブ側）へ `MERGE` |

読み取り側（重い SELECT）は次の 3 種類から選べ、複数同時に実行できます。

- **低速読み取り**：`SELECT *` の結果をクライアント側でゆっくり読み続ける（BI ツール等の長時間取得を想定）
- **HOLDLOCK 保持**：トランザクション内で `WITH (TABLOCK, HOLDLOCK)` の SELECT 後に `WAITFOR` でロックを保持
- **重い集計**：自己結合の集計クエリを指定時間実行して打ち切り

読み取りの開始タイミングは次から選べます。更新処理のイベントに連動するので、BulkCopy の速さ（環境）に関係なく順序が保証されます。

| 開始タイミング | 動作 |
| --- | --- |
| 検証開始から N 秒後 | 従来どおり時間で開始 |
| 切替の N 秒前 | 更新処理が BulkCopy を終えたところで読み取りを開始し、更新処理は**読み取りが実行中になってから N 秒待って**切替（DROP+RENAME / ALTER VIEW / SYNONYM / MERGE）を実行 |
| 切替がロック待機になってから N 秒後 | ロック監視が切替の待機を検出してから N 秒後に開始（監視で検出できない場合は、切替開始から 3 秒で待機中とみなす）。待機にならなかった場合は実行しない |

- 継続時間は最初の行が返ってから数えます（ロック待ちの時間は含めません）。「最初の行まで」の時間で、後から来た SELECT がどれだけ止められたかが分かります。
- 切替の時点で実行中の読み取りがなかった場合、判定は「検証不成立」になります。

検証中は別セッションで `sys.dm_exec_requests` / `sys.dm_tran_locks` を監視し、ブロッキングの発生・解消、待機種別（`LCK_M_SCH_M` など）、ロック状況を記録します。

### プリセットシナリオ

①②④⑤は「読取1 が切替の 10 秒前から読み続ける → 切替がロック待機 → 待機中に読取2 が新たに SELECT」の順序で実行します。

1. 現行方式の再現（テーブル公開。DROP の待機と、待機中に来た SELECT の待機 → タイムアウト想定）
2. 改修後（ビュー公開。ALTER VIEW で同じパターン）
3. 改修後・連続切替（読み取り中に 2 回切替 → 旧テーブルの TRUNCATE が待機するケースの確認）
4. 参考：シノニム
5. 差分更新（MERGE の 10 秒前から HOLDLOCK 保持 → 待機中に来た SELECT の待機）

## 検証ログ

- 1 回の実行ごとに `App_Data/logs/{実行ID}.json` に保存されます（実行中も 2 秒ごとに保存）。保存先は `Verification:LogDirectory` で変更できます。
- 画面の「検証ログ」タブで、判定・指標・タイムライン（ロック待機を赤で表示）・実行ログ（SQL、ロック詳細を展開表示）を確認できます。
- CSV（Excel 向け BOM 付き）/ JSON でダウンロードできます。

## 設定（Server の appsettings.json）

```json
{
  "ConnectionStrings": {
    "VerifyDb": "Server=VERIFY-SQL;Database=ShareSystemVerify;Integrated Security=true;TrustServerCertificate=true"
  },
  "Verification": { "LogDirectory": "App_Data/logs", "MaxRunMinutes": 30 },
  "HostRedirect": { "Enabled": true, "DefaultDomain": "example.local", "HttpsPort": 443 }
}
```

必要な権限（検証用 DB）：テーブル・ビュー・シノニムの作成/削除、`VIEW SERVER STATE`（ブロッキング監視用）。

```sql
GRANT VIEW SERVER STATE TO [ログイン名];  -- SQL Server 2022 以降は VIEW SERVER PERFORMANCE STATE でも可
```

## デバッグ（Windows / Visual Studio）

前提：Visual Studio 2026（ASP.NET と Web 開発・.NET Aspire のワークロード）、Node.js（LTS）。

1. 検証用 SQL Server の接続文字列を `ShareSystemAlterViewCheck.Server/appsettings.json` の `ConnectionStrings:VerifyDb` に記載します（DB は事前に作成しておきます）。
2. スタートアップ プロジェクトを選んで F5 で起動します。
   - `ShareSystemAlterViewCheck.AppHost`（推奨）：Aspire が Server と Client（Vite 開発サーバー）を同時に起動します。Aspire ダッシュボードで両方のログを確認でき、画面は `client` リソースの URL（`https://localhost:52731`）から開きます。
   - `ShareSystemAlterViewCheck.Server`（`https` プロファイル）：Server 単体で起動し、SpaProxy が Vite 開発サーバー（`https://localhost:52731`）を起動します。
   - Aspire から起動した場合、Server の SpaProxy は無効になります（Vite の二重起動を防ぐため）。

- Client の `npm install` は自動で実行されます（Aspire 起動時は `client` の前に毎回、Server 単体起動時は `node_modules` がない場合のみデバッグビルド時に実行）。
- HTTPS の開発証明書は初回起動時に Visual Studio が信頼するか確認します（または `dotnet dev-certs https --trust`）。
- デバッグ時だけ別の接続先を使いたい場合は、`appsettings.Development.json` に `ConnectionStrings:VerifyDb` を追加すると上書きされます。

## 発行と IIS 配置

```bash
dotnet publish ShareSystemAlterViewCheck.Server -c Release -o ./publish
```

- 発行時のみ Client の `npm run build` が実行され、`dist` の内容が発行先の `wwwroot` に出力されます（ソース側の Server に wwwroot は作られません）。
- IIS：Default Web Site の下に「アプリケーションの追加」で発行フォルダを指定（例：`/ShareSystemAlterViewCheck`）。
  - ASP.NET Core Hosting Bundle（.NET 10）が必要です。
  - サイトに HTTPS バインドを設定してください。
  - アプリケーションプールの ID に `App_Data` フォルダへの書き込み権限を付与してください（検証ログ保存用）。
  - 末尾スラッシュなし（`/ShareSystemAlterViewCheck`）でアクセスした場合は `/ShareSystemAlterViewCheck/` にリダイレクトします。

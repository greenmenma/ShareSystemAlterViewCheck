# ShareSystemAlterViewCheck

SQL Server のデータ公開システムで発生する **全件更新時のロック待機（DROP TABLE が長時間 SELECT にブロックされタイムアウトする問題）** を検証環境で再現し、
**ビュー公開 + ALTER VIEW（Blue/Green 構成）** へ改修したときに問題が解消されるか、また代替案（ALTER TABLE SWITCH の低優先度待機・制御テーブル切替・全件同期 MERGE・RCSI）で解消できるかを確認するための Web アプリです。

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

検証の経緯・結論・本命の方式のフローチャートは [docs/lock-verification-summary.md](docs/lock-verification-summary.md) にまとめています。
本命の公開ビューのインデックス・整合性などの検証は [docs/control-view-verification.md](docs/control-view-verification.md) にまとめています。

| 方式 | 全件更新の手順 |
| --- | --- |
| 現行：テーブル公開 | `vt_PublicTable_New` 作成 → `vt_Source` から BulkCopy → `DROP TABLE vt_PublicTable` + `sp_rename` |
| 改修案：ビュー公開（Blue/Green） | 非アクティブ側（`vt_View_Blue` / `vt_View_Green`）を空にする → BulkCopy → `ALTER VIEW vt_PublicView` |
| 参考：シノニム公開 | 非アクティブ側を空にする → BulkCopy → `DROP SYNONYM` / `CREATE SYNONYM` |
| 代替案：ALTER TABLE SWITCH | `vt_SwStage` へ BulkCopy → `ALTER TABLE vt_SwPublic SWITCH TO vt_SwOld WITH (WAIT_AT_LOW_PRIORITY …)` → `ALTER TABLE vt_SwStage SWITCH TO vt_SwPublic` |
| 代替案：制御テーブル切替 | 空の非アクティブ側（`vt_Ctl_A` / `vt_Ctl_B`）へ BulkCopy → `UPDATE vt_CtlActive SET ActiveSide = …`（ビュー `vt_CtlView` の定義は固定）→ 旧側を空にする（下記） |

制御テーブル切替の「非アクティブ側を空にする方法」は次から選べます。「切替後の後片付け」を有効にすると、切替の直後に旧側を空にしておき、次回の更新ではすぐ BulkCopy できます。

| 空にする方法 | ログ | 読み取りへの影響 |
| --- | --- | --- |
| 低優先度 SWITCH で退避 + TRUNCATE（推奨） | ほぼなし | 古い読み取りの終了を待つが、後から来た読み取りは止めない。待機時間切れなら、後片付けは次回に持ち越し、準備段階なら更新を見送る |
| DELETE | 全行を記録（さらに、その後の BulkCopy も最小ログにならない） | RCSI なら待たない |
| TRUNCATE / DROP + CREATE | ほぼなし | ビューを読む SELECT は両側に Sch-S を持つため、読み取り中は待機し、後から来た読み取りも止める |

BulkCopy の BatchSize も指定できます。SIMPLE（または BULK_LOGGED）で空のテーブルへ `TABLOCK` 付きで投入する場合、BatchSize = 0（全件を 1 バッチ）にするとログが最小になります。

各更新の段階（準備 / BulkCopy / 切替 / 後片付け）ごとのログ書込量と、検証の開始・終了時のログファイルサイズを記録・表示します（`VIEW SERVER STATE` が必要）。

| 更新種別 | 手順 |
| --- | --- |
| 全件更新（入替） | 上の表の各方式の手順 |
| 差分更新 | `vt_DiffStage` に差分を BulkCopy → 公開テーブル（Blue/Green 方式はアクティブ側）へ `MERGE` |
| 全件同期 | `vt_DiffStage` に全件を BulkCopy → `MERGE`（変わった行の UPDATE・INSERT・連携元に無い行の DELETE）。公開テーブルを作り直さない |

### READ_COMMITTED_SNAPSHOT（RCSI）

「環境セットアップ」タブで検証用 DB の RCSI を ON / OFF できます（ALTER DATABASE 権限が必要。接続中の他セッションは切断されます）。
RCSI が ON だと通常の SELECT は S ロックを取らずに行バージョンを読むため、DML（UPDATE / DELETE / MERGE）と読み取りが互いに待たなくなります。
DDL（DROP / ALTER VIEW / SWITCH）が必要とする Sch-M ロックには効果がありません。検証ログには実行時の RCSI の設定が記録されます。

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
| 切替の開始から N 秒後 | 待機の有無に関係なく、切替 SQL の開始から N 秒後に開始（方式どうしを同じ条件で比べるときに使う） |

- 継続時間は最初の行が返ってから数えます（ロック待ちの時間は含めません）。「最初の行まで」の時間で、後から来た SELECT がどれだけ止められたかが分かります。
- 切替の時点で実行中の読み取りがなかった場合、判定は「検証不成立」になります。

検証中は別セッションで `sys.dm_exec_requests` / `sys.dm_tran_locks` を監視し、ブロッキングの発生・解消、待機種別（`LCK_M_SCH_M` など）、ロック状況を記録します。

### プリセットシナリオ

①②④⑥⑦⑧⑨⑩⑪は「読取1 が切替の 10 秒前から読み続ける → 切替 → 切替開始の 5 秒後に読取2 が新たに SELECT」の順序で実行します。

| # | シナリオ | RCSI 前提 |
| --- | --- | --- |
| ① | 現行方式の再現（DROP の待機と、待機中に来た SELECT の待機 → タイムアウト） | |
| ② | 改修案：ALTER VIEW（同じパターン） | |
| ③ | 改修案・連続切替（読み取り中に 2 回切替 → 旧テーブルの TRUNCATE が待機するケース） | |
| ④ | 参考：シノニム | |
| ⑤ | 差分更新 × HOLDLOCK（ロックヒントは RCSI でもロックを取る） | |
| ⑥ | 代替案：SWITCH + 低優先度待機（切替は読取1 の終了を待つが、読取2 は止まらない） | |
| ⑦ | 代替案：SWITCH 低優先度・時間切れ（ABORT_AFTER_WAIT = SELF で切替を中止） | |
| ⑧ | 本命：制御テーブル切替 + 低優先度 SWITCH で退避（切替直後に後片付け、BatchSize = 0、2 回更新） | ○ |
| ⑨ | 比較：制御テーブル切替 + DELETE（ログ量の比較用） | ○ |
| ⑩ | 代替案：全件同期 MERGE（公開テーブルを作り直さない） | ○ |
| ⑪ | 代替案：差分更新 MERGE × 通常の長時間 SELECT | ○ |

### 参考：SQL Server 2022（Docker・30 万行）での実行結果

| 方式 | RCSI | 更新 | 後から来た読み取りの待ち |
| --- | --- | --- | --- |
| 現行 DROP + RENAME | OFF | タイムアウト（LCK_M_X） | 約 25 秒 |
| ALTER VIEW | OFF / ON | タイムアウト（LCK_M_SCH_M） | 約 25 秒 |
| SWITCH（通常の優先度） | OFF | タイムアウト（LCK_M_SCH_M） | 約 25 秒 |
| SWITCH（低優先度 1 分 / SELF） | OFF | 読取1 の終了を待って成功（約 50 秒待機、LCK_M_SCH_M_LOW_PRIORITY） | なし |
| SWITCH（低優先度・読取1 が 120 秒） | ON | 1 分で切替を中止（エラー 1222） | なし |
| 制御テーブル切替 × 2 回更新 | OFF | 成功。ただし 2 回目の DELETE が読取1 を約 36 秒待機（LCK_M_X） | なし |
| 制御テーブル切替 × 2 回更新 | ON | 待機なしで成功 | なし |
| 全件同期 MERGE（約 6 万行 UPDATE） | ON | 待機なしで成功（MERGE 約 1.5 秒） | なし |
| 差分更新 MERGE | ON | 待機なしで成功 | なし |

ログ量（SIMPLE・RCSI ON・30 万行 ≒ 270MB・2 回更新の合計）：

| 方式 | ログ書込量 | ログファイル | 補足 |
| --- | --- | --- | --- |
| ⑨ 制御テーブル切替 + DELETE | 601 MB | 136 → 648 MB に膨張 | DELETE 284MB + DELETE 後の BulkCopy 289MB |
| ⑧ 本命（BatchSize = 10,000） | 57 MB | 72 → 136 MB | BulkCopy 約 28MB / 回 |
| ⑧ 本命（BatchSize = 0） | 4.4 MB | 増えず | BulkCopy 約 2.2MB / 回。後片付けは読取1 の終了を約 50 秒待機（公開・読み取りへの影響なし） |

Read Replica（Always On の読み取り可能セカンダリ）は単一サーバーでは再現できないため、このアプリでは検証対象外です。
セカンダリでも DDL を再生する redo スレッドは実行中の SELECT の Sch-S を待つため（redo がブロックされ、セカンダリの反映が遅れる）、DDL で入れ替える方式のままでは問題が残ります。

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

必要な権限（検証用 DB）：テーブル・ビュー・シノニムの作成/削除、`VIEW SERVER STATE`（ブロッキング監視用）、`ALTER DATABASE`（RCSI を切り替える場合のみ）、`ALTER ANY CONNECTION`（SWITCH の `ABORT_AFTER_WAIT = BLOCKERS` を使う場合のみ）。

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

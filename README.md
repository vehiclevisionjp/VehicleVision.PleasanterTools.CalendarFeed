# VehicleVision.PleasanterTools.CalendarFeed

**Pleasanter のレコードを iCalendar（RFC 5545）の購読フィードとして公開するラッパー。**

Outlook / Google カレンダー / Apple カレンダーの「URL から購読」で、Pleasanter のサイト
（記録テーブル・期限付きテーブルなど）を予定として表示できる。**読み取り専用。**
標準 Web API を使う独立したアプリで、Pleasanter 本体は改造しない。

設計は [`_documents/アーキテクチャ方針.md`](_documents/アーキテクチャ方針.md)（**未確定事項は 8 章、残作業は 9 章**）。
構成・規約は兄弟リポジトリ
[VehicleVision.PleasanterTools.Questionnaire](https://github.com/vehiclevisionjp/VehicleVision.PleasanterTools.Questionnaire)
に倣っている。

## 状態

**v0.x（1.0.0 未満は互換を保証しない）。** 購読エンドポイント・**管理画面（Svelte。7 言語）**・管理 API・**管理者の認証と管理**
（パスワード ＋ TOTP、5 つの役割、招待メール、端末の管理、監査ログ）・**アプリケーション設定の画面編集**・**SAML 2.0 / Pleasanter のログイン（任意）**・
**取得失敗のメール通知（任意）**・複数台での運用が動く。機能の一覧は [`_documents/機能一覧.md`](_documents/機能一覧.md)。

## 使い方

```bash
# 設定は App_Data/Parameters/*.json（README あり）か環境変数で与える。API キーはファイルへ直書きしない
export CALENDARFEED_PLEASANTER_BASEURL=http://localhost:8080
export CALENDARFEED_PLEASANTER_APIKEY=<API キー>
export CALENDARFEED_PLEASANTER_TIMEZONE=Asia/Tokyo
export CALENDARFEED_PUBLIC_BASEURL=https://calendar.example.com

dotnet run --project src/VehicleVision.PleasanterTools.CalendarFeed.Web
```

```bash
# 秘密の鍵を作って設定する（フィードごとの Pleasanter API キーと管理者の TOTP を DB へ暗号化して置く鍵。失うと復号できない）
export CALENDARFEED_SECRET_KEY=$(dotnet run --project src/VehicleVision.PleasanterTools.CalendarFeed.Web -- --generate-secret-key)
```

**最初の管理者**は、管理者が 1 人も居ないときだけ作れる（**公開する前に作る**。空の状態を放置しない）。
管理 API は、ログインして発行された cookie（`HttpOnly` / `SameSite=Strict`）で使う。

```bash
B=http://localhost:5000
curl -c jar -X POST $B/admin/api/setup -H "Content-Type: application/json" \
  -d '{"loginId":"admin","password":"<12 文字以上のパスワード>"}'
curl -b jar -c jar -X POST $B/admin/api/login -H "Content-Type: application/json" \
  -d '{"loginId":"admin","password":"<パスワード>"}'        # {"step":"done"} か "totp" / "enroll"

# フィードを作る（応答の url が購読 URL。トークンは 1 度しか表示されない。API キーは登録後は返さない）
curl -b jar -X POST $B/admin/api/feeds -H "Content-Type: application/json" \
  -d '{"name":"共有予定","siteId":123,"apiKey":"<Pleasanter の API キー>","apiKeyUserTimeZoneId":"Asia/Tokyo","language":"ja"}'
```

| API | 認証 | 内容 |
| --- | --- | --- |
| `GET /feeds/{token}.ics` | トークン URL | 購読フィード |
| `GET /admin/api/status` | なし | 最初の管理者が要るか・2 要素の方針 |
| `POST /admin/api/setup` | なし（管理者が居ないときだけ） | 最初の管理者を作る |
| `POST /admin/api/login` | なし | パスワードでログイン。`step` は `done` / `totp`（数字が要る）/ `enroll`（2 要素が必須で未登録） |
| `POST /admin/api/login/totp` / `/login/recovery` | 途中（Pending） | 数字 / 復旧コードでログインを完了 |
| `POST /admin/api/logout` | — | ログアウト（サーバ側のセッションを失効） |
| `GET /admin/api/me/` | ログイン済み | 自分の情報 |
| `POST /admin/api/me/password` | ログイン済み | パスワードの変更（他の端末のセッションを失効） |
| `POST /admin/api/me/totp/begin` → `/complete` | ログイン済み、または必須で未登録の途中 | 2 要素の登録（復旧コード 10 本を 1 度だけ返す） |
| `POST /admin/api/me/totp/disable` | ログイン済み | 2 要素の解除（パスワードの再確認つき。必須では不可） |
| `GET /admin/api/feeds/` | ログイン済み | フィードの一覧（トークンも API キーも含まない） |
| `POST /admin/api/feeds` | ログイン済み | フィードの作成 |
| `POST /admin/api/feeds/{id}/rotate` | ログイン済み | トークンの作り直し（旧トークンは即失効） |
| `PUT /admin/api/feeds/{id}/apikey` | ログイン済み | API キー（と利用者のタイムゾーン）の差し替え |
| `PUT /admin/api/feeds/{id}` | ログイン済み | フィードの編集（購読 URL と API キーは変わらない） |
| `DELETE /admin/api/feeds/{id}` | ログイン済み | フィードの失効 |
| `GET/POST/PUT … /admin/api/users` | `users.read` / `users.write` | 管理者の一覧・招待・役割・無効化・2 要素の解除・端末の失効（**運用手順書の「管理者の管理」**） |
| `GET /admin/api/me/sessions/` ほか | ログイン済み | 自分の端末の一覧（入った方式つき）・失効 |
| `GET /admin/api/audit-logs/` | `audit.read` | 監査ログ |
| `GET/PUT /admin/api/settings/` | `settings.manage` | アプリケーション設定の編集（外部設定が優先。秘密は返さない） |
| `/admin/api/saml/*` | なし（有効なときだけ） | SAML 2.0 のログイン（メタデータ・開始・ACS・単一ログアウト） |
| `POST /admin/api/pleasanter-sso/check` | なし（有効なときだけ） | Pleasanter のログインで入る（Pleasanter の cookie を転送して本人を確かめる） |

管理画面は `/admin/`（ブラウザ）。上の API は管理画面が使うもので、cookie ＋ 状態を変える要求は同じサイトからのものだけ受ける。

## 対応する RDBMS

本アプリ専用の DB（フィード定義）は **SQLite / SQL Server / PostgreSQL / MySQL**。**Pleasanter の DB とは別に作る。**
`CALENDARFEED_DB_PROVIDER`（既定 `Sqlite`。簡易セットアップとデバッグ用で、本番運用には使わない）と
`CALENDARFEED_DB_CONNECTIONSTRING` で指定する。マイグレーションは起動時に自動で当たる。

## 開発

```bash
git submodule update --init --recursive   # _reference（Pleasanter 本体・参照専用）
dotnet build
dotnet test

# 管理画面（Svelte。Node.js 24）。成果物は .Web の wwwroot/admin へ出る
cd src/VehicleVision.PleasanterTools.CalendarFeed.Frontend
npm ci && npm test && npm run build
```

管理画面の画面の写しは [`_documents/管理画面-画面の写し一覧.md`](_documents/管理画面-画面の写し一覧.md)（[`tools/screenshots`](tools/screenshots/README.md) で撮り直せる）。

**実機（Pleasanter・Keycloak）での通し**は CI の「端から端まで通す試験」（`.github/workflows/e2e.yml`）で回す。手元でも
`tools/pleasanter-testenv/sso_roundtrip.py`（Pleasanter のログイン）・`tools/saml-idp/e2e.py`（SAML）で通せる。

**4 種の RDBMS の結合テスト**は、検証環境（`tools/pleasanter-testenv`）の PostgreSQL・MySQL・SQL Server に当てる。
**接続文字列の環境変数を設定しないと、SQLite 以外は何も流れずに緑になる**ので、`CALENDARFEED_REQUIRE_ALL_RDBMS=1` で
未設定を失敗にできる（CI は必ずこれを立てる）。手順は [`tools/pleasanter-testenv/README.md`](tools/pleasanter-testenv/README.md)。

| パス | 内容 |
| --- | --- |
| [`_documents/アーキテクチャ方針.md`](_documents/アーキテクチャ方針.md) | 設計 |
| [`_documents/導入-更新運用手順書.md`](_documents/導入-更新運用手順書.md) | 導入・日常運用・更新・困ったとき（鍵を失ったとき、プロキシのログ） |
| [`_documents/機能一覧.md`](_documents/機能一覧.md) | 機能・方式・設定・関連 Issue |
| [`_documents/実機検証結果.md`](_documents/実機検証結果.md) | 実機（Pleasanter・Keycloak）で確かめた挙動 |
| [`_documents/管理画面-画面の写し一覧.md`](_documents/管理画面-画面の写し一覧.md) | 管理画面の画面の写し |
| `_reference/Implem.Pleasanter` | Pleasanter 本体（**AGPL v3 / 参照専用**）。[README](_reference/README.md) |
| [`.github/copilot-instructions.md`](.github/copilot-instructions.md) | 開発規約（単一の参照元）。Claude Code は [`CLAUDE.md`](CLAUDE.md) から辿る |

**Pleasanter 本体（AGPL v3）のコードは取り込まない。** 依存 OSS は寛容ライセンスに限定する（[`NOTICE`](NOTICE)）。

## ライセンス

**デュアルライセンス（AGPL-3.0-or-later OR 商用）。** Questionnaire と同じ。決定済み（[`_documents/アーキテクチャ方針.md`](_documents/アーキテクチャ方針.md) 1 章）。

| ファイル | 内容 |
| --- | --- |
| [`LICENSE`](LICENSE) | AGPL v3 の全文（GNU の公式の原文） |
| [`LICENSING.md`](LICENSING.md) | デュアルライセンスの説明（どちらを選ぶか・Pleasanter との関係・依存） |
| [`LICENSE-COMMERCIAL.md`](LICENSE-COMMERCIAL.md) | 商用ライセンスの概要（連絡先は未記入） |
| [`CLA.md`](CLA.md) | コントリビューターライセンス同意書（**草案。法務の確認前**） |
| [`CONTRIBUTING.md`](CONTRIBUTING.md) | 貢献のしかた |

Copyright (C) PMC Co.,Ltd.

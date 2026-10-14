# Parameters（設定ファイル）

**Pleasanter 本体・Questionnaire と同じ方式**で、設定は `App_Data/Parameters/*.json` に置く。
読み込みは 1 か所（`Web/Services/ParameterFiles.cs`）で、アプリの一番先で積む。

## 規約

- **キーの説明は、同じ階層に `"//キー名"` の項目を置いて書く。**
- **資格情報（API キー・管理キー・接続文字列）の実値をこのフォルダへコミットしないこと。**
  実値は次のいずれかで与える
    1. 環境変数（Azure App Service のアプリケーション設定など）
    2. `{名前}.local.json`（`.gitignore` 済み。ローカル開発用）
- 上書きの優先順位は **`{名前}.local.json` ＞ 環境変数 ＞ `{名前}.json`**
- **起動時に、読んだ置き場と見つかったファイルをログへ出す。** 置き場を間違えても黙って既定で動くのを防ぐ
  （1 つも見つからなければ警告）

## ファイルと環境変数

ファイルのキーは Pleasanter 本体に合わせた短い名前で、環境変数は `CALENDARFEED_*`。
**読み込み時に下表のとおり写している**ので、どちらで書いても効く。

| ファイル | キー | 環境変数 | 内容 |
| --- | --- | --- | --- |
| `Service.json` | `TimeZoneDefault` | `CALENDARFEED_TIMEZONE_DEFAULT` | 既定のタイムゾーン（既定 `Asia/Tokyo`） |
| `Service.json` | `PublicBaseUrl` | `CALENDARFEED_PUBLIC_BASEURL` | 本アプリの公開 URL（購読 URL の基点） |
| `Pleasanter.json` | `BaseUrl` | `CALENDARFEED_PLEASANTER_BASEURL` | 接続先 Pleasanter の URL |
| `Pleasanter.json` | `ApiKey` | `CALENDARFEED_PLEASANTER_APIKEY` | API キー。**実値は書かない** |
| `Pleasanter.json` | `ApiVersion` | `CALENDARFEED_PLEASANTER_APIVERSION` | API バージョン（既定 1.1。読めない値は既定へ） |
| `Pleasanter.json` | `TimeoutSeconds` | `CALENDARFEED_PLEASANTER_TIMEOUTSECONDS` | 呼び出しのタイムアウト（既定 30） |
| `Pleasanter.json` | `ApiKeyUserTimeZoneId` | `CALENDARFEED_PLEASANTER_TIMEZONE` | API キーの利用者のタイムゾーン。未設定なら `TimeZoneDefault`。**運用開始後に変えない** |
| `Pleasanter.json` | `LinkBaseUrl` | `CALENDARFEED_PLEASANTER_LINKBASEURL` | 予定に付けるレコードへのリンクの基点 |
| `Feed.json` | `CacheSeconds` | `CALENDARFEED_FEED_CACHESECONDS` | `.ics` を使い回す秒数（既定 300） |
| `Feed.json` | `MaxEvents` | `CALENDARFEED_FEED_MAXEVENTS` | 1 フィードの予定の上限（既定 5000） |
| `Feed.json` | `PageSize` | `CALENDARFEED_FEED_PAGESIZE` | Pleasanter へ求める 1 ページの件数（既定 1000） |
| `Rds.json` | `ConnectionString` | `CALENDARFEED_DB_CONNECTIONSTRING` | 本アプリの DB。現状は SQLite のみ |
| `Security.json` | `SecretKey` | `CALENDARFEED_SECRET_KEY` | フィードごとの API キーと管理者の TOTP の共有鍵を DB へ暗号化して置く鍵（Base64・32 バイト）。**実値は書かない。** `dotnet run -- --generate-secret-key` で作る。**失うと保管した API キーは復号できない** |
| `Security.json` | `AdminTwoFactor` | `CALENDARFEED_ADMIN_TWOFACTOR` | 管理者の 2 要素。`required`（登録するまでログインが完了しない）/ `optional`（既定）/ `disabled`（登録の口を閉じる。登録済みの人からは外さない）。**知らない値は起動時に落とす** |
| `Security.json` | `AuditLogRetentionDays` | `CALENDARFEED_AUDITLOG_RETENTION_DAYS` | 監査ログを残す日数（既定 365。`0` なら消さない。読めない値は既定へ） |
| `Security.json` | `ForwardedProxyNetworks` | `CALENDARFEED_FORWARDED_PROXY_NETWORKS` | 転送ヘッダ（`X-Forwarded-*`）を信頼するプロキシのネットワーク（カンマ区切りの CIDR）。**未設定なら誰も信頼しない**（プロキシ配下ではレート制限が全員で共有になる）。CIDR でない値は起動時に落とす |
| `Mail.json` | `Enabled` ほか | `CALENDARFEED_MAIL_ENABLED` ほか | **招待メールの送信設定**（Questionnaire の `QUESTIONNAIRE_MAIL_*` と同じ項目。`Mail.json` に一覧と説明）。**既定は無効**（招待は画面の URL を手で渡す）。`SmtpPassword` の実値は書かない（環境変数か `Mail.local.json`）。有効なのに足りない・読めない値は起動時に落とす |
| `Security.json` | `PasswordMinimumLength` | `CALENDARFEED_PASSWORD_MINIMUMLENGTH` | 管理者のパスワードの最低の長さ（既定 12） |
| `Security.json` | `PasswordPolicies` | `PasswordPolicies__0__Regex` など | パスワードの条件の正規表現（Pleasanter 本体と同じ書き方。部分一致・上から順・最初に合わなかった文言を返す）。**先読み・後方参照・原子グループは使えず、起動時に落とす**。後退戻りしない照合器＋100 ミリ秒の上限で動かす（ReDoS 対策） |
| `Security.json` | `PasswordAllowSameAsLoginId` | `CALENDARFEED_PASSWORD_ALLOWSAMEASLOGINID` | ログイン ID と同じパスワードを許すか（既定 false） |

> **`CALENDARFEED_SECRET_KEY` は環境変数で保持する**（Azure App Service のアプリケーション設定・Key Vault の参照など、
> デプロイ先の秘密の保管先へ）。`Security.local.json` はローカル開発用。失うと保管した API キーを復号できないため、
> コンテナやディスクが作り直されても残る場所に置く。

> ⚠️ **値が `null` や空のキーは無視する。** `ApiKey` は既定で `null`（ここへ書かせない）なので、
> 写すと**環境変数で与えた API キーを空で塗り潰してしまう。**

> ⚠️ **環境変数の名前を `Pleasanter__BaseUrl` のような .NET の階層形式で与えても効かない。**
> 上表の `CALENDARFEED_*` を使う。

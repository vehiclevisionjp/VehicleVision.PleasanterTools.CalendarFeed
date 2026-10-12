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
| `Security.json` | `AdminApiKey` | `CALENDARFEED_ADMIN_APIKEY` | 管理 API の Bearer キー。**実値は書かない**。未設定なら管理 API を開かない |

> ⚠️ **値が `null` や空のキーは無視する。** `ApiKey` は既定で `null`（ここへ書かせない）なので、
> 写すと**環境変数で与えた API キーを空で塗り潰してしまう。**

> ⚠️ **環境変数の名前を `Pleasanter__BaseUrl` のような .NET の階層形式で与えても効かない。**
> 上表の `CALENDARFEED_*` を使う。

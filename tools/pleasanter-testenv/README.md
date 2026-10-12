# Pleasanter 検証環境

**ソースだけでは確定できない事項を実機で潰すための使い捨て環境。**
Pleasanter 本体は**公式イメージを起動するだけ**で、ソースの参照・リンクはしない
（AGPL 非汚染の前提。[`_reference/README.md`](../../_reference/README.md)）。
得られた結果は [`_documents/実機検証結果.md`](../../_documents/実機検証結果.md)。

Questionnaire の同名の環境に倣っている（SQL Server のみ・Security.json の差し替えなし）。
**ポートは Questionnaire とずらしてある**（Pleasanter 18090 / DB 11434）ので、同時に起動できる。

## 構成

| サービス | イメージ | 役割 |
| --- | --- | --- |
| `db` | `mcr.microsoft.com/mssql/server:2025-CU8-ubuntu-24.04` | SQL Server 2025 |
| `codedefiner` | `implem/pleasanter:codedefiner` | スキーマ作成 ＋ 90 日トライアル登録 |
| `pleasanter` | `implem/pleasanter:1.5.8.1` | 本体。サブモジュールの固定版に合わせる |
| `verify` | `python:3.13-slim` | 検証の実行役。**ホストに python も curl も要求しない** |

## 使い方

```bash
cd tools/pleasanter-testenv
export MSYS_NO_PATHCONV=1   # Windows の Git Bash から実行する場合
docker compose up -d --wait
# API キーを Administrator へ入れる（**検証環境専用**。何度流してもよい）
docker compose cp seed/01_apikey.sql db:/tmp/
docker compose exec -T db /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P 'Calendarfeed#Test1' -C -b -i /tmp/01_apikey.sql
docker compose restart pleasanter     # 利用者キャッシュを捨てる

docker compose run --rm verify                          # サイトとレコードを作って取得（結果は ./results/）
docker compose run --rm verify python get.py <サイトID> <接頭辞>   # 既存サイトを取り直す
docker compose down -v                                  # 後片付け（DB ごと破棄）
```

画面は <http://localhost:18090>（`Administrator` / `pleasanter`）。

- **`01_apikey.sql` を流すのは、最初の `up` の後。** `Administrator` は Pleasanter の初回起動で作られる。
  ポートが塞がっていて Pleasanter が起動しないと、`Administrator が見つからない` で止まる
- **API キーの利用者のタイムゾーンを変えるとき**は、`Users.TimeZone` を更新して `pleasanter` を再起動する
  （利用者情報はキャッシュされる）。**`UTC` と `Tokyo Standard Time` で同じレコードの見え方が変わる**
  （`_documents/実機検証結果.md` 1 章）
- `Full-Text Search is not installed` は SQL Server コンテナに全文検索が無いため。**検証には影響しない**

## アプリを検証環境へ向けて動かす

```bash
export CALENDARFEED_PLEASANTER_BASEURL=http://localhost:18090
export CALENDARFEED_PLEASANTER_APIKEY=<01_apikey.sql の値>
export CALENDARFEED_PLEASANTER_TIMEZONE=Asia/Tokyo   # Administrator の TimeZone と合わせる
export CALENDARFEED_ADMIN_APIKEY=local-admin-key
dotnet run --project ../../src/VehicleVision.PleasanterTools.CalendarFeed.Web
```

## 変数

**変数名には必ず `TESTENV_` 接頭辞を付けてある**（ホストの同名変数が compose の既定値を黙って上書きするため）。

| 変数 | 既定 |
| --- | --- |
| `TESTENV_SA_PASSWORD` | `Calendarfeed#Test1` |
| `TESTENV_DB_PORT` | `11434` |
| `TESTENV_PLEASANTER_PORT` | `18090` |
| `TESTENV_API_KEY` | `01_apikey.sql` と同じ固定値 |

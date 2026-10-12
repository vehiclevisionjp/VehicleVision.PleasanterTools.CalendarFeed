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

**足場（v0.1.0）。** 購読エンドポイントと管理 API が動く。**管理画面は未実装。**

## 使い方

```bash
# 設定は App_Data/Parameters/*.json（README あり）か環境変数で与える。API キーはファイルへ直書きしない
export CALENDARFEED_PLEASANTER_BASEURL=http://localhost:8080
export CALENDARFEED_PLEASANTER_APIKEY=<API キー>
export CALENDARFEED_PLEASANTER_TIMEZONE=Asia/Tokyo
export CALENDARFEED_ADMIN_APIKEY=<管理キー>
export CALENDARFEED_PUBLIC_BASEURL=https://calendar.example.com

dotnet run --project src/VehicleVision.PleasanterTools.CalendarFeed.Web
```

```bash
# フィードを作る（応答の url が購読 URL。トークンは 1 度しか表示されない）
curl -X POST http://localhost:5000/admin/api/feeds \
  -H "Authorization: Bearer $CALENDARFEED_ADMIN_APIKEY" -H "Content-Type: application/json" \
  -d '{"name":"共有予定","siteId":123,"mapping":{"startColumn":"StartTime","endColumn":"CompletionTime"}}'
```

| API | 内容 |
| --- | --- |
| `GET /feeds/{token}.ics` | 購読フィード |
| `GET /admin/api/feeds/` | フィードの一覧（トークンは含まない） |
| `POST /admin/api/feeds` | フィードの作成 |
| `POST /admin/api/feeds/{id}/rotate` | トークンの作り直し（旧トークンは即失効） |
| `DELETE /admin/api/feeds/{id}` | フィードの失効 |

## 対応する RDBMS

本アプリ専用の DB（フィード定義）は **SQLite / SQL Server / PostgreSQL / MySQL**。**Pleasanter の DB とは別に作る。**
`CALENDARFEED_DB_PROVIDER`（既定 `Sqlite`。簡易セットアップとデバッグ用で、本番運用には使わない）と
`CALENDARFEED_DB_CONNECTIONSTRING` で指定する。マイグレーションは起動時に自動で当たる。

## 開発

```bash
git submodule update --init --recursive   # _reference（Pleasanter 本体・参照専用）
dotnet build
dotnet test
```

**4 種の RDBMS の結合テスト**は、検証環境（`tools/pleasanter-testenv`）の PostgreSQL・MySQL・SQL Server に当てる。
**接続文字列の環境変数を設定しないと、SQLite 以外は何も流れずに緑になる**ので、`CALENDARFEED_REQUIRE_ALL_RDBMS=1` で
未設定を失敗にできる（CI は必ずこれを立てる）。手順は [`tools/pleasanter-testenv/README.md`](tools/pleasanter-testenv/README.md)。

| パス | 内容 |
| --- | --- |
| [`_documents/`](_documents/アーキテクチャ方針.md) | 設計 |
| `_reference/Implem.Pleasanter` | Pleasanter 本体（**AGPL v3 / 参照専用**）。[README](_reference/README.md) |
| [`.github/copilot-instructions.md`](.github/copilot-instructions.md) | 開発規約（単一の参照元）。Claude Code は [`CLAUDE.md`](CLAUDE.md) から辿る |

**Pleasanter 本体（AGPL v3）のコードは取り込まない。** 依存 OSS は寛容ライセンスに限定する（[`NOTICE`](NOTICE)）。

## ライセンス

**デュアルライセンス（AGPL-3.0-or-later OR 商用）。** Questionnaire と同じ。決定済み（[`_documents/アーキテクチャ方針.md`](_documents/アーキテクチャ方針.md) 1 章）。`LICENSE` 等の整備は未了。

Copyright (C) PMC Co.,Ltd.

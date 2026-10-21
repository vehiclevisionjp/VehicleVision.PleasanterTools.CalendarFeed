# CalDAV 実クライアント検証

**本アプリの CalDAV（`/caldav/`）を、実際のクライアントの実装で通す使い捨ての環境。**
本アプリ（コンテナ。`Dockerfile`）と、実機の Pleasanter（`tools/pleasanter-testenv`）を起動し、
クライアントのコンテナから操作して、**結果を Pleasanter 側のレコードで確かめる**（本アプリの応答だけを信じない）。
ホストに要るのは Docker だけ（Windows の Git Bash でも動く）。

```bash
bash tools/caldav-check/run.sh                # SQLite で通す（既定。約 2〜3 分）
DB=postgres bash tools/caldav-check/run.sh    # 本アプリの DB を PostgreSQL にして通す（sqlserver | postgres | mysql）
DB=all bash tools/caldav-check/run.sh         # SQLite・SQL Server・PostgreSQL・MySQL を順に通す（約 4〜5 分）
KEEP=1 bash tools/caldav-check/run.sh         # 片付けない（調べるとき）。後片付けは `docker compose --profile rdbms down -v`
```

`DB` は**本アプリの DB** の選択（Pleasanter の DB は SQL Server 固定）。SQL Server は Pleasanter 用のサーバーに本アプリ用の
データベース（`calendarfeed_app`）を別に作る。PostgreSQL・MySQL は `tools/pleasanter-testenv` の `rdbms` プロファイルのもの。
**通した後、本アプリの DB の中に CalDAV の対応表の行があること**（その DB へ実際に書いた証拠）も確かめる。

## 通すクライアント

| クライアント | 何を確かめるか |
| --- | --- |
| Python の [`caldav`](https://pypi.org/project/caldav/) ライブラリ | 探索（principal → カレンダー一覧）・作成（`TZID` 付き）・一覧・更新（`If-Match`）・**競合（古い ETag は 412。Pleasanter 側の変更を上書きしない）**・既存の予定の更新・UID の重複・繰り返しの拒否（403）・削除・同じ UID での作り直し |
| [`vdirsyncer`](https://vdirsyncer.pimutils.org/)（CalDAV ⇔ ローカルの `.ics`） | 探索・取得・更新の送信・作成の送信・削除の送信・**再同期しても重複しない** |

**Apple カレンダー・Thunderbird・DAVx⁵・Outlook は、ここでは通せない**（実機の端末・GUI が要る）。
上の 2 つが通ることは、それらの動作を保証しない。結果は [`_documents/実機検証結果.md`](../../_documents/実機検証結果.md) 18 章。

## 仕組み

- 本アプリは、リポジトリ直下の `compose.yaml`（`tools/pleasanter-testenv` を include）で起動する。ポートは他の検証環境とずらしてある
- `clients/setup.py`: Pleasanter にサイトと予定を作り、本アプリに管理者とフィード（CalDAV 有効）を作る
- クライアントは `calendarfeed-dev_default` の網の中から、**利用者本人の Pleasanter の API キー（検証環境専用の固定値）を Basic 認証のパスワードに**してつなぐ
- 検証した版は `run.sh` が `pip freeze` で表示する。**古い版で通ったことを、新しい版の保証にしない**

## 注意（クライアントの癖。サーバーの不具合ではない）

- **本アプリは、保存した内容を整えるので、PUT の応答に（強い）ETag を付けない**（RFC 4791 5.3.4。
  一次情報: <https://www.rfc-editor.org/rfc/rfc4791.txt>、2026-10-17 参照）。
  `vdirsyncer` は、作成の次の同期で ETag の変わりを見つけて取り直す。**取り直す前に消すと、消しが取り消される**
  （「サーバー側が変わった」を「こちらで消した」より優先する）ので、検証では作成の後にもう一度同期してから消している
- `caldav` ライブラリは、**削除に `If-Match` を付けない**（更新には付ける）。そのため本アプリは削除の `If-Match` を任意にしている

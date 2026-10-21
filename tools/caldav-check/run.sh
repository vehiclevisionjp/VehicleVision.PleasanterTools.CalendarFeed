#!/usr/bin/env bash
# CalDAV の実クライアント（Python の caldav ライブラリ・vdirsyncer）を、本アプリ（コンテナ）と
# 実機の Pleasanter（検証環境）へつないで通す。結果は Pleasanter 側のレコードで確かめる。
#
#   bash tools/caldav-check/run.sh                 # SQLite で通す（既定）
#   DB=postgres bash tools/caldav-check/run.sh     # 本アプリの DB を PostgreSQL にして通す
#   DB=all bash tools/caldav-check/run.sh          # SQLite・SQL Server・PostgreSQL・MySQL を順に通す
#   KEEP=1 bash tools/caldav-check/run.sh          # 片付けない（調べるとき）
#
# DB は sqlite | sqlserver | postgres | mysql | all。**本アプリの DB** の選択（Pleasanter の DB は SQL Server 固定）。
# ホストに要るのは docker だけ。**Windows の Git Bash でも動く**（MSYS_NO_PATHCONV を付けてある）。
# 検証環境専用の固定値（API キー・DB のパスワード）を使う。本番へ向けないこと。
set -euo pipefail
cd "$(dirname "$0")/../.."

export MSYS_NO_PATHCONV=1
# ポートは他の検証環境（Pleasanter 18090 / DB 11434）とずらす。クライアントはコンテナの網の中から話すので、公開は確認用
export TESTENV_DB_PORT="${TESTENV_DB_PORT:-21434}"
export TESTENV_PLEASANTER_PORT="${TESTENV_PLEASANTER_PORT:-28090}"
export TESTENV_POSTGRES_PORT="${TESTENV_POSTGRES_PORT:-25433}"
export TESTENV_MYSQL_PORT="${TESTENV_MYSQL_PORT:-23307}"
export DEV_APP_PORT="${DEV_APP_PORT:-28081}"

# 検証用の API キーは、検証環境の定義（docker-compose.yml の既定）から読む。このファイルへは書かない
TESTENV_API_KEY="$(grep -m1 -o 'TESTENV_API_KEY:-[0-9a-f]*' tools/pleasanter-testenv/docker-compose.yml | cut -d- -f2)"
export TESTENV_API_KEY
# 秘密の鍵は毎回作る（使い捨て）
DEV_SECRET_KEY="$(docker run --rm python:3.13-slim python -c 'import os,base64;print(base64.b64encode(os.urandom(32)).decode())')"
export DEV_SECRET_KEY

PROJECT=calendarfeed-dev
NETWORK="${PROJECT}_default"
# 検証環境の DB のパスワード（tools/pleasanter-testenv/docker-compose.yml の既定と同じ）
DB_PASSWORD="${TESTENV_SA_PASSWORD:-Calendarfeed#Test1}"

case "${DB:-sqlite}" in
    all) DATABASES=(sqlite sqlserver postgres mysql) ;;
    sqlite | sqlserver | postgres | mysql) DATABASES=("${DB:-sqlite}") ;;
    *) echo "DB は sqlite | sqlserver | postgres | mysql | all" >&2; exit 2 ;;
esac

cleanup() {
    if [ "${KEEP:-0}" != "1" ]; then
        docker compose --profile rdbms down -v --remove-orphans >/dev/null 2>&1 || true
        docker volume rm caldav-check-state >/dev/null 2>&1 || true
    fi
}
trap cleanup EXIT

echo "== 本アプリのイメージと Pleasanter の検証環境を起動する"
docker compose --profile rdbms down -v --remove-orphans >/dev/null 2>&1 || true
docker compose build app
docker compose up -d --wait db codedefiner pleasanter

echo "== 検証用の API キーを入れて、Pleasanter を再起動する"
docker compose cp tools/pleasanter-testenv/seed/01_apikey.sql db:/tmp/ >/dev/null
docker compose exec -T db /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$DB_PASSWORD" -C -b -i /tmp/01_apikey.sql >/dev/null
docker compose restart pleasanter >/dev/null
# ポートが開いただけでは、まだ API が 503 を返す。ログイン画面が 200 を返すまで待つ（実機検証結果 11 章）
code=000
for _ in $(seq 1 150); do
    code="$(curl -s -o /dev/null -w '%{http_code}' "http://localhost:${TESTENV_PLEASANTER_PORT}/users/login" || true)"
    [ "$code" = "200" ] && break
    sleep 2
done
[ "$code" = "200" ] || { echo "Pleasanter did not become ready (last status $code)" >&2; exit 1; }

echo "== クライアントのイメージを作る"
docker build -q -t caldav-check tools/caldav-check >/dev/null
docker run --rm caldav-check pip freeze | grep -i -E '^(caldav|vdirsyncer|requests|icalendar)=='
docker volume create caldav-check-state >/dev/null

run_client() {
    docker run --rm --network "$NETWORK" -v caldav-check-state:/state \
        -e PLEASANTER_API_KEY="$TESTENV_API_KEY" -e PLEASANTER_URL=http://pleasanter:8080 -e APP_URL=http://app:8080 \
        caldav-check python "$@"
}

# 本アプリの DB を用意し、接続先を環境変数に入れる（compose.yaml の DEV_DB_*）
prepare_db() {
    case "$1" in
        sqlite)
            export DEV_DB_PROVIDER=Sqlite DEV_DB_CONNECTIONSTRING="Data Source=/data/calendarfeed.db" ;;
        sqlserver)
            # Pleasanter 用の SQL Server に、本アプリ用のデータベースを別に作る
            docker compose exec -T db /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$DB_PASSWORD" -C -b \
                -Q "IF DB_ID('calendarfeed_app') IS NULL CREATE DATABASE calendarfeed_app COLLATE Japanese_CI_AS" >/dev/null
            export DEV_DB_PROVIDER=SqlServer
            export DEV_DB_CONNECTIONSTRING="Server=db;Database=calendarfeed_app;User Id=sa;Password=${DB_PASSWORD};TrustServerCertificate=True" ;;
        postgres)
            docker compose --profile rdbms up -d --wait postgres
            export DEV_DB_PROVIDER=PostgreSql
            export DEV_DB_CONNECTIONSTRING="Host=postgres;Port=5432;Database=calendarfeed;Username=postgres;Password=${DB_PASSWORD}" ;;
        mysql)
            docker compose --profile rdbms up -d --wait mysql
            export DEV_DB_PROVIDER=MySql
            export DEV_DB_CONNECTIONSTRING="Server=mysql;Port=3306;Database=calendarfeed;User=root;Password=${DB_PASSWORD}" ;;
    esac
}

# 本アプリが実際に使った DB に、CalDAV の対応表の行があるか（その DB へ書いた証拠。SQLite はファイルなので数えない）
count_resources() {
    case "$1" in
        sqlite) echo "-" ;;
        sqlserver) docker compose exec -T db /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$DB_PASSWORD" -C -d calendarfeed_app -h -1 -W \
            -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM CalDavResources" | tr -d '\r ' ;;
        postgres) docker compose exec -T postgres psql -U postgres -d calendarfeed -tA -c 'SELECT COUNT(*) FROM "CalDavResources"' | tr -d '\r ' ;;
        mysql) docker compose exec -T mysql mysql -uroot -p"$DB_PASSWORD" -N -B calendarfeed -e 'SELECT COUNT(*) FROM CalDavResources' 2>/dev/null | tr -d '\r ' ;;
    esac
}

failed=0
summary=()
for database in "${DATABASES[@]}"; do
    echo
    echo "################ 本アプリの DB: ${database} ################"
    prepare_db "$database"
    docker compose up -d --force-recreate --wait app
    docker compose logs --no-color app 2>/dev/null | grep -m1 "DB: " || true

    result=ok
    echo "== 下ごしらえ（サイト・予定・管理者・フィード）"
    run_client setup.py
    echo "== caldav ライブラリ"
    run_client check_caldav_lib.py || result=NG

    # vdirsyncer は、上の検証が作った予定が残らないよう、新しいサイト・フィードで下ごしらえからやり直す
    echo "== 下ごしらえをやり直す（vdirsyncer 用）"
    run_client setup.py
    echo "== vdirsyncer"
    run_client check_vdirsyncer.py || result=NG

    rows="$(count_resources "$database")"
    echo "== CalDavResources の行数（本アプリの DB の中）: ${rows}"
    if [ "$database" != "sqlite" ] && { [ -z "$rows" ] || [ "$rows" -lt 1 ]; }; then
        echo "NG: ${database} の DB に CalDAV の対応表の行が無い（別の DB を使っていないか）" >&2
        result=NG
    fi
    summary+=("${database}: ${result}")
    [ "$result" = ok ] || { failed=1; docker compose logs --no-color --tail=60 app >&2 || true; }
done

echo
echo "== まとめ"
printf '  %s\n' "${summary[@]}"
[ "$failed" -eq 0 ] || { echo "NG: 失敗した検証がある" >&2; exit 1; }
echo "OK: すべて通った"

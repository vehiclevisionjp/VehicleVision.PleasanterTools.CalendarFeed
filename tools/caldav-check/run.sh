#!/usr/bin/env bash
# CalDAV の実クライアント（Python の caldav ライブラリ・vdirsyncer）を、本アプリ（コンテナ）と
# 実機の Pleasanter（検証環境）へつないで通す。結果は Pleasanter 側のレコードで確かめる。
#
#   bash tools/caldav-check/run.sh            # 起動 → 下ごしらえ → 検証 → 片付け
#   KEEP=1 bash tools/caldav-check/run.sh     # 片付けない（画面の確認などに使う）
#
# ホストに要るのは docker だけ。**Windows の Git Bash でも動く**（MSYS_NO_PATHCONV を付けてある）。
# 検証環境専用の固定値（API キー）を使う。本番の Pleasanter へ向けないこと。
set -euo pipefail
cd "$(dirname "$0")/../.."

export MSYS_NO_PATHCONV=1
# ポートは他の検証環境（Pleasanter 18090 / DB 11434）とずらす。クライアントはコンテナの網の中から話すので、公開は確認用
export TESTENV_DB_PORT="${TESTENV_DB_PORT:-21434}"
export TESTENV_PLEASANTER_PORT="${TESTENV_PLEASANTER_PORT:-28090}"
export DEV_APP_PORT="${DEV_APP_PORT:-28081}"

# 検証用の API キーは、検証環境の定義（docker-compose.yml の既定）から読む。このファイルへは書かない
TESTENV_API_KEY="$(grep -m1 -o 'TESTENV_API_KEY:-[0-9a-f]*' tools/pleasanter-testenv/docker-compose.yml | cut -d- -f2)"
export TESTENV_API_KEY
# 秘密の鍵は毎回作る（使い捨て）
DEV_SECRET_KEY="$(docker run --rm python:3.13-slim python -c 'import os,base64;print(base64.b64encode(os.urandom(32)).decode())')"
export DEV_SECRET_KEY

PROJECT=calendarfeed-dev
NETWORK="${PROJECT}_default"

cleanup() {
    if [ "${KEEP:-0}" != "1" ]; then
        docker compose down -v --remove-orphans >/dev/null 2>&1 || true
        docker volume rm caldav-check-state >/dev/null 2>&1 || true
    fi
}
trap cleanup EXIT

echo "== 本アプリのイメージと Pleasanter の検証環境を起動する"
docker compose down -v --remove-orphans >/dev/null 2>&1 || true
docker compose build app
docker compose up -d --wait db codedefiner pleasanter

echo "== 検証用の API キーを入れて、Pleasanter を再起動する"
docker compose cp tools/pleasanter-testenv/seed/01_apikey.sql db:/tmp/ >/dev/null
docker compose exec -T db /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P 'Calendarfeed#Test1' -C -b -i /tmp/01_apikey.sql >/dev/null
docker compose restart pleasanter >/dev/null
# ポートが開いただけでは、まだ API が 503 を返す。ログイン画面が 200 を返すまで待つ（実機検証結果 11 章）
code=000
for _ in $(seq 1 150); do
    code="$(curl -s -o /dev/null -w '%{http_code}' "http://localhost:${TESTENV_PLEASANTER_PORT}/users/login" || true)"
    [ "$code" = "200" ] && break
    sleep 2
done
[ "$code" = "200" ] || { echo "Pleasanter did not become ready (last status $code)" >&2; exit 1; }

echo "== 本アプリを起動する"
docker compose up -d --wait app

echo "== クライアントのイメージを作る"
docker build -q -t caldav-check tools/caldav-check >/dev/null
docker run --rm caldav-check pip freeze | grep -i -E '^(caldav|vdirsyncer|requests|icalendar)=='

docker volume create caldav-check-state >/dev/null
run_client() {
    docker run --rm --network "$NETWORK" -v caldav-check-state:/state \
        -e PLEASANTER_API_KEY="$TESTENV_API_KEY" -e PLEASANTER_URL=http://pleasanter:8080 -e APP_URL=http://app:8080 \
        caldav-check python "$@"
}

echo "== 下ごしらえ（サイト・予定・管理者・フィード）"
run_client setup.py

failed=0
echo "== caldav ライブラリ"
run_client check_caldav_lib.py || failed=1

# vdirsyncer は、上の検証が作った予定が残らないよう、新しいサイト・フィードで下ごしらえからやり直す
echo "== 下ごしらえをやり直す（vdirsyncer 用）"
run_client setup.py
echo "== vdirsyncer"
run_client check_vdirsyncer.py || failed=1

if [ "$failed" -ne 0 ]; then
    echo "NG: 失敗した検証がある" >&2
    docker compose logs --no-color --tail=80 app >&2 || true
    exit 1
fi
echo "OK: すべて通った"

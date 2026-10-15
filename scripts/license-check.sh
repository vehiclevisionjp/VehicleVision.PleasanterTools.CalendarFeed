#!/usr/bin/env bash
# デュアルライセンス（AGPL-3.0-or-later OR 商用）の前提を機械で守る。
#
# Pleasanter 本体（AGPL v3）の著作権は当社に無い。取り込むと**商用ライセンスでの
# 提供ができなくなる**ため、src / tests への混入を検査する。
# _reference/ は参照専用なので検査対象から外す。
set -euo pipefail
cd "$(dirname "$0")/.."

fail=0

echo "== src / tests に Implem.* への参照や using が無いか =="
if grep -rInE '(using[[:space:]]+Implem\.|Include="[^"]*Implem\.[^"]*\.csproj"|PackageReference[^>]*Implem\.)' \
        src tests 2>/dev/null; then
    echo "NG: Pleasanter 本体への参照が混入している" >&2
    fail=1
else
    echo "OK"
fi

echo
echo "== ソリューションに _reference 配下のプロジェクトが入っていないか =="
if grep -nE '_reference' ./*.slnx 2>/dev/null; then
    echo "NG: _reference 配下がソリューションに入っている" >&2
    fail=1
else
    echo "OK"
fi

echo
echo "== 参考: 依存パッケージ一覧（ライセンスは NOTICE で維持すること） =="
grep -rhoE '<PackageReference[^>]*Include="[^"]+"[^>]*Version="[^"]+"' src tests 2>/dev/null \
    | sed -E 's/.*Include="([^"]+)".*Version="([^"]+)".*/  \1 \2/' | sort -u || true

exit "$fail"

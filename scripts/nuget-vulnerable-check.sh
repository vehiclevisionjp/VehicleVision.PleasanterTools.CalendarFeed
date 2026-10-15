#!/usr/bin/env bash
# NuGet 依存に既知の脆弱性が無いかを検査する。
#
# ⚠️ **`dotnet list package --vulnerable` は、脆弱性を見つけても終了コード 0 を返す。**
#    そのまま CI のステップにしても永遠に緑のままになる。実測で確かめた（2026-08-19、
#    SDK 10.0.400）。System.Net.Http 4.3.0 を参照した検証用プロジェクトで
#    GHSA-7jgj-8wvc-jh57（High）を検出したうえで終了コードは 0 だった。
#    そのため **JSON を読んで自前で落とす。**
#
# JSON の形（--output-version 1）
#   projects[].frameworks[].topLevelPackages[].vulnerabilities[]{severity, advisoryurl}
#   projects[].frameworks[].transitivePackages[].vulnerabilities[]{severity, advisoryurl}
#   脆弱性が 1 件も無いプロジェクトには frameworks キー自体が現れない。
#
# **推移的な依存も対象にする（--include-transitive）。** 直接参照だけを見ても、
# 実際に配布されるアセンブリの脆弱性は見つからない。
#
# 参照した一次情報（いずれも 2026-08-19 参照）
#   - --vulnerable / --include-transitive / --format json / --output-version
#     https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-package-list
#     （終了コードについての記載は無い。上記の実測が根拠）
#
# NOTE: .NET 10 で `dotnet package list`（名詞が先）が正式な形になったが、
#       従来の `dotnet list package` も引き続き動く。global.json は 10.0.100 を
#       指しているので、どちらでも通る。ここは Issue #12 の文言に合わせて後者を使う。
set -euo pipefail
cd "$(dirname "$0")/.."

SOLUTION="VehicleVision.PleasanterTools.CalendarFeed.slnx"
REPORT="${1:-}"

echo "== NuGet 依存の脆弱性を検査する（推移的な依存を含む） =="
echo

# 人が読む用。ここでは落とさない
dotnet list "$SOLUTION" package --vulnerable --include-transitive || true
echo

json="$(mktemp)"
trap 'rm -f "$json"' EXIT

dotnet list "$SOLUTION" package --vulnerable --include-transitive \
    --format json --output-version 1 > "$json"

if [ -n "$REPORT" ]; then
    cp "$json" "$REPORT"
fi

# **`python3` があるかどうかを command -v だけで決めない。** Windows には
# 「Microsoft Store を開くだけ」の python3 スタブが PATH に入っていることがあり、
# 実行すると終了コード 49 で何もせずに返る（開発機で実際に踏んだ）。
# 実際に走らせて確かめてから採用する。
python_bin=""
for candidate in python3 python; do
    if command -v "$candidate" >/dev/null 2>&1 \
            && "$candidate" -c "import sys, json" >/dev/null 2>&1; then
        python_bin="$candidate"
        break
    fi
done
if [ -z "$python_bin" ]; then
    echo "NG: 動作する python3 / python が見つかりません" >&2
    exit 1
fi

"$python_bin" - "$json" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as fp:
    report = json.load(fp)

found = []
for project in report.get("projects") or []:
    path = project.get("path", "(不明なプロジェクト)")
    for framework in project.get("frameworks") or []:
        target = framework.get("framework", "(不明なターゲット)")
        for kind, key in (("直接", "topLevelPackages"), ("推移", "transitivePackages")):
            for package in framework.get(key) or []:
                for vulnerability in package.get("vulnerabilities") or []:
                    found.append({
                        "project": path,
                        "framework": target,
                        "kind": kind,
                        "id": package.get("id", "(不明なパッケージ)"),
                        "version": package.get("resolvedVersion", "(不明な版)"),
                        "severity": vulnerability.get("severity", "(不明)"),
                        "url": vulnerability.get("advisoryurl", ""),
                    })

if not found:
    print("OK   既知の脆弱性を持つ NuGet 依存はありません")
    sys.exit(0)

print(f"NG   既知の脆弱性を持つ NuGet 依存が {len(found)} 件あります", file=sys.stderr)
print("", file=sys.stderr)
for item in found:
    print(
        f"  [{item['severity']}] {item['id']} {item['version']}"
        f"（{item['kind']}依存 / {item['framework']}）",
        file=sys.stderr,
    )
    print(f"    勧告 : {item['url']}", file=sys.stderr)
    print(f"    参照元: {item['project']}", file=sys.stderr)
print("", file=sys.stderr)
print("対処: 該当パッケージを修正版へ上げる。推移的な依存なら、", file=sys.stderr)
print("      直接参照を足して版を固定するか、参照元のパッケージを上げる。", file=sys.stderr)
print("      版を上げたら NOTICE の版数も直すこと。", file=sys.stderr)
sys.exit(1)
PY

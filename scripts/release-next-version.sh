#!/usr/bin/env bash
# 次のリリース版を採番し、リリースに入れる状態かを確かめる。
# 副作用は一切無い。`.github/workflows/release-run.yml` の計画ジョブから呼ぶ。
#
# **採番の起点は既存の最新 `v*` タグ。** 実際に出した版を起点にすれば重複しない。
# `Directory.Build.props` の `VersionPrefix` を起点にすると、タグを付ける前に
# `VersionPrefix` だけ先に上げてあった場合に二重に上がる。
#
# ⚠️ **起点に採るのは最終リリースのタグだけ**（`vX.Y.Z`）。
#    プレリリース（`v1.0.0-rc.1`）はこの仕掛けでは発行せず、起点にも採らない
#    （必要になったら手でタグを付ける。`release.yml` は rc を扱える）。
#
# ⚠️ **タグがまだ 1 つも無い初回は、`VersionPrefix` をそのまま出す版とする。**
#    起点が無いところで bump しても意味が無く、`0.1.0` を作ったのに `0.1.1` から
#    始まる、という食い違いになる。**初回は `bump` の指定を無視する。**
#
# 要る環境変数
#   BUMP  major / minor / patch
#
# 出力（`$GITHUB_OUTPUT` があればそこへ、無ければ標準出力へ）
#   current_version      起点にした版（初回は空）
#   next_version         次の版（X.Y.Z）
#   next_tag             次のタグ（vX.Y.Z）
#   version_prefix       Directory.Build.props の現在値
#   first_release        タグが無い初回なら true
#   needs_version_bump   VersionPrefix を上げる PR が要るなら true
#
# 参照した一次情報（いずれも 2026-09-11 参照）
#   - セマンティックバージョニング 2.0.0
#     https://semver.org/lang/ja/
#   - git tag --sort=-v:refname（版順の並べ替え）
#     https://git-scm.com/docs/git-tag
set -euo pipefail
cd "$(dirname "$0")/.."

: "${BUMP:?BUMP が要ります（major / minor / patch）}"
case "$BUMP" in
    major | minor | patch) ;;
    *)
        echo "NG: BUMP は major / minor / patch のいずれか。受け取った値: $BUMP" >&2
        exit 1
        ;;
esac

BUILD_PROPS="Directory.Build.props"

version_prefix="$(sed -nE 's@.*<VersionPrefix>([^<]+)</VersionPrefix>.*@\1@p' "$BUILD_PROPS" | head -n 1)"
if [[ -z "$version_prefix" ]]; then
    echo "NG: $BUILD_PROPS から VersionPrefix を読み取れない" >&2
    exit 1
fi
if ! printf '%s' "$version_prefix" | grep -qE '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$'; then
    echo "NG: VersionPrefix ($version_prefix) が X.Y.Z の形になっていない" >&2
    exit 1
fi

# **最終リリースのタグだけを起点にする。** rc などが混ざると採番が崩れる
current_tag="$(
    git tag --list 'v*' --sort=-v:refname |
        grep -E '^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$' |
        head -n 1 || true
)"

if [[ -z "$current_tag" ]]; then
    first_release=true
    current_version=""
    next_version="$version_prefix"
    echo "既存の v タグが無いため、初回リリースとして VersionPrefix ($version_prefix) を出す版にする。"
    echo "     **bump の指定 ($BUMP) は無視する。**"
else
    first_release=false
    current_version="${current_tag#v}"
    IFS='.' read -r major minor patch <<<"$current_version"
    case "$BUMP" in
        major)
            next_version="$((major + 1)).0.0"
            ;;
        minor)
            next_version="${major}.$((minor + 1)).0"
            ;;
        patch)
            next_version="${major}.${minor}.$((patch + 1))"
            ;;
    esac
    echo "起点: $current_tag → $BUMP を上げて $next_version"
fi

next_tag="v$next_version"

# **同じタグを二度作らない。** release-tag-protection でタグは付け直せないため、
# 衝突に気付かず進むと、次のパッチ版を出す以外の手が無くなる
if git rev-parse -q --verify "refs/tags/$next_tag" >/dev/null; then
    echo "NG: タグ $next_tag は既に存在する" >&2
    echo "    採番の起点がずれている。タグの一覧を確かめること" >&2
    exit 1
fi

# VersionPrefix が起点の版か、既に次の版まで上がっているか、のどちらかであること。
# **どちらでもない値なら止める。** 配布物の中身とタグの表す版が食い違う
if [[ "$version_prefix" == "$next_version" ]]; then
    needs_version_bump=false
    echo "VersionPrefix は既に $next_version。版を上げる PR は要らない。"
elif [[ "$first_release" == "false" && "$version_prefix" != "$current_version" ]]; then
    echo "NG: VersionPrefix ($version_prefix) が、起点のタグ ($current_version) とも" >&2
    echo "    次の版 ($next_version) とも一致しない" >&2
    echo "    手で書き換えた形跡がある。$BUILD_PROPS を確かめること" >&2
    exit 1
else
    needs_version_bump=true
    echo "VersionPrefix を $version_prefix → $next_version へ上げる PR を出す。"
fi

emit() {
    if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
        printf '%s\n' "$1" >>"$GITHUB_OUTPUT"
    else
        printf '%s\n' "$1"
    fi
}

emit "current_version=$current_version"
emit "next_version=$next_version"
emit "next_tag=$next_tag"
emit "version_prefix=$version_prefix"
emit "first_release=$first_release"
emit "needs_version_bump=$needs_version_bump"

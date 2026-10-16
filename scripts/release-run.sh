#!/usr/bin/env bash
# リリース手順書の 2〜5 を通す。
# `.github/workflows/release-run.yml` の実行ジョブから呼ぶ。
#
#   2. `VersionPrefix` を上げる PR を `develop` へ出してマージ
#   3. `develop` → `master` の PR を出す
#   4. 必須チェック通過後にマージ
#   5. `master` の該当コミットへ `vX.Y.Z` を付けて push
#
# 6（GitHub Release の作成）は、タグの push を受けて `release.yml` が行う。
#
# ⚠️ **`GITHUB_TOKEN` では動かない。** `GITHUB_TOKEN` の push / PR 作成では
#    ワークフローの連鎖を防ぐ仕様で CI が起動せず、**必須ステータスチェックが
#    永久に pending になってマージできない**。`my-repo-automation-bot`
#    （GitHub App）の installation token を渡すこと。
#
# ⚠️ **マージの待ちは GitHub の auto-merge に任せる。** こちらでチェックの
#    合否を判定して `gh pr merge` を叩くと、ruleset の判定を二重に実装することに
#    なり食い違う。`--auto` で予約して、マージされるまで見張るだけにする。
#
# ⚠️ **`develop` → `master` は squash しない。** squash すると master の履歴が
#    develop と別物になり、次のリリースの PR が過去の変更まで抱える。
#    `VersionPrefix` を上げる PR の方は、通常の運用どおり squash する。
#
# ⚠️ **途中で落ちたら、途中まで進んだ状態が残る。** 復旧は手で行う
#    （_documents/リリース手順書.md「一気通貫の実行が途中で落ちたとき」）。
#    再実行しても、既にある PR とブランチは作り直さず引き継ぐ。
#
# 要る環境変数
#   GH_TOKEN             GitHub App の installation token
#   APP_SLUG             その App の slug（コミットの作者名に使う）
#   NEXT_VERSION         出す版（X.Y.Z）
#   NEXT_TAG             出すタグ（vX.Y.Z）
#   NEEDS_VERSION_BUMP   VersionPrefix を上げる PR が要るなら true
#   BUMP                 major / minor / patch（PR 本文へ書くだけ）
#   WAIT_SECONDS         マージ待ちの上限（既定 2400）
#
# 参照した一次情報（いずれも 2026-09-11 参照）
#   - gh pr merge --auto（auto-merge の予約）
#     https://cli.github.com/manual/gh_pr_merge
#   - gh pr create / gh pr list
#     https://cli.github.com/manual/gh_pr_create
#   - GITHUB_TOKEN の操作ではワークフローが起動しない
#     https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/trigger-a-workflow
set -euo pipefail
cd "$(dirname "$0")/.."

: "${GH_TOKEN:?GH_TOKEN が要ります}"
: "${NEXT_VERSION:?NEXT_VERSION が要ります}"
: "${NEXT_TAG:?NEXT_TAG が要ります}"
NEEDS_VERSION_BUMP="${NEEDS_VERSION_BUMP:-true}"
BUMP="${BUMP:-patch}"
WAIT_SECONDS="${WAIT_SECONDS:-2400}"

BUILD_PROPS="Directory.Build.props"
BUMP_BRANCH="release/$NEXT_TAG"

summary() {
    if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
        cat >>"$GITHUB_STEP_SUMMARY"
    else
        cat
    fi
}

# **コミットの作者を App の bot 名義へ揃える。** master-protection の
# require_extra_approval_for_unattributed_changes が有効で、
# **push した主体と結び付かないコミットは追加の承認を要求される。**
#
# ⚠️ **installation token では `gh api user` が引けない。** App の slug から
#    `<slug>[bot]` のユーザを引いて id を得る（Dealer の create-release-tag.yml と同じ）。
if [[ -n "${APP_SLUG:-}" ]]; then
    bot_user_id="$(gh api "/users/${APP_SLUG}[bot]" --jq '.id')"
    git config user.name "${APP_SLUG}[bot]"
    git config user.email "${bot_user_id}+${APP_SLUG}[bot]@users.noreply.github.com"
    echo "コミットの作者: ${APP_SLUG}[bot]"
else
    echo "NG: APP_SLUG が渡っていない。GitHub App のトークンで実行すること" >&2
    exit 1
fi

# PR を探し、無ければ作る。**再実行で二重に作らない**
find_or_create_pr() {
    local base="$1" head="$2" title="$3" body="$4" number
    number="$(gh pr list --base "$base" --head "$head" --state open --json number --jq '.[0].number // ""')"
    if [[ -n "$number" ]]; then
        echo "既にある PR #$number を引き継ぐ（base: $base / head: $head）" >&2
        printf '%s' "$number"
        return 0
    fi
    # **作成の失敗を握り潰さない。** 呼び出し元は `$(...)` の中なので
    # `set -e` が効かず、黙って次へ進んでしまう
    if ! gh pr create --base "$base" --head "$head" --title "$title" --body "$body" >&2; then
        echo "NG: PR を作れなかった（base: $base / head: $head）" >&2
        return 1
    fi
    number="$(gh pr list --base "$base" --head "$head" --state open --json number --jq '.[0].number // ""')"
    if [[ -z "$number" ]]; then
        echo "NG: PR を作れたのに番号を引けない（base: $base / head: $head）" >&2
        return 1
    fi
    echo "PR #$number を作った（base: $base / head: $head）" >&2
    printf '%s' "$number"
}

# auto-merge を予約して、マージされるまで見張る
merge_when_green() {
    local number="$1" method="$2" deadline state
    deadline=$(($(date +%s) + WAIT_SECONDS))

    state="$(gh pr view "$number" --json state --jq '.state')"
    if [[ "$state" == "MERGED" ]]; then
        echo "PR #$number は既にマージ済み"
        return 0
    fi
    if [[ "$state" == "CLOSED" ]]; then
        echo "NG: PR #$number は閉じられている。人が確かめること" >&2
        return 1
    fi

    # **既に予約済みでも失敗にしない。** 再実行を通すため
    if ! gh pr merge "$number" --auto "--$method"; then
        echo "auto-merge の予約に失敗した。既に予約済みかを確かめて続ける" >&2
        if [[ "$(gh pr view "$number" --json autoMergeRequest --jq '.autoMergeRequest != null')" != "true" ]]; then
            echo "NG: PR #$number の auto-merge が予約できていない" >&2
            return 1
        fi
    fi
    echo "PR #$number の auto-merge を予約した（$method）。必須チェックの通過を待つ。"

    while :; do
        state="$(gh pr view "$number" --json state --jq '.state')"
        case "$state" in
            MERGED)
                echo "PR #$number がマージされた"
                return 0
                ;;
            CLOSED)
                echo "NG: PR #$number が閉じられた。人が確かめること" >&2
                return 1
                ;;
        esac

        # **落ちたチェックがあればここで止める。** auto-merge は待ち続けるだけで、
        # 失敗を教えてくれない。上限まで無駄に待たない
        local failed
        failed="$(gh pr checks "$number" --required --json name,bucket \
            --jq '[.[] | select(.bucket == "fail" or .bucket == "cancel") | .name] | join(", ")' 2>/dev/null || true)"
        if [[ -n "$failed" ]]; then
            echo "NG: PR #$number の必須チェックが失敗している: $failed" >&2
            echo "    auto-merge の予約は残っている。直せばそのままマージされる" >&2
            return 1
        fi

        if (($(date +%s) >= deadline)); then
            echo "NG: PR #$number が $WAIT_SECONDS 秒でマージされなかった" >&2
            echo "    auto-merge の予約は残っている。PR の状態を人が確かめること" >&2
            return 1
        fi
        sleep 20
    done
}

# ---- 2. VersionPrefix を上げる PR を develop へ出してマージ ------------------
if [[ "$NEEDS_VERSION_BUMP" == "true" ]]; then
    git fetch --no-tags origin develop:refs/remotes/origin/develop

    if git ls-remote --exit-code --heads origin "$BUMP_BRANCH" >/dev/null 2>&1; then
        echo "== 既にある $BUMP_BRANCH を引き継ぐ"
        git fetch --no-tags origin "$BUMP_BRANCH:$BUMP_BRANCH"
        git switch "$BUMP_BRANCH"
    else
        echo "== $BUMP_BRANCH を作って VersionPrefix を $NEXT_VERSION へ上げる"
        git switch -c "$BUMP_BRANCH" origin/develop

        # **属性値ではなく要素の中身だけを置き換える。** 版が他の箇所へ
        # 現れても巻き込まないよう、VersionPrefix 要素に限定する
        sed -i -E "s@(<VersionPrefix>)[^<]+(</VersionPrefix>)@\1$NEXT_VERSION\2@" "$BUILD_PROPS"

        updated="$(sed -nE 's@.*<VersionPrefix>([^<]+)</VersionPrefix>.*@\1@p' "$BUILD_PROPS" | head -n 1)"
        if [[ "$updated" != "$NEXT_VERSION" ]]; then
            echo "NG: VersionPrefix を $NEXT_VERSION へ書き換えられなかった（現在: $updated）" >&2
            exit 1
        fi

        git add "$BUILD_PROPS"
        git commit -m "リリース $NEXT_TAG に向けて VersionPrefix を上げる

アセンブリのバージョンの単一情報源は Directory.Build.props の VersionPrefix で
（_documents/リリース手順書.md）、ここがタグとずれていると Release ワークフローの
検証で落ちる。$BUMP を上げる指定でリリースを実行したため、$NEXT_VERSION へ上げる。

検証したこと: scripts/release-next-version.sh が既存の最新 v タグを起点に
$NEXT_VERSION を採番し、同名のタグが未使用であることを確認した。"
        git push origin "$BUMP_BRANCH"
    fi

    bump_pr="$(find_or_create_pr develop "$BUMP_BRANCH" \
        "リリース $NEXT_TAG に向けて VersionPrefix を上げる" \
        "## 概要

\`Directory.Build.props\` の \`VersionPrefix\` を **\`$NEXT_VERSION\`** へ上げる。
リリース実行ワークフロー（\`.github/workflows/release-run.yml\`）が \`$BUMP\` の指定で自動採番した。

**アセンブリのバージョンの単一情報源は \`VersionPrefix\`**（\`_documents/リリース手順書.md\`）。
ここがタグとずれていると \`release.yml\` の検証で落ちる。

## この後の流れ

マージされると、同じワークフローが \`develop\` → \`master\` の PR を出し、
必須チェックの通過後にマージして \`$NEXT_TAG\` を push する。
タグの push を受けて \`release.yml\` が GitHub Release を作る。")"

    merge_when_green "$bump_pr" squash
else
    echo "== VersionPrefix は既に $NEXT_VERSION。版を上げる PR は省く"
    bump_pr=""
fi

# ---- 3〜4. develop → master の PR を出してマージ ------------------------------
git fetch --no-tags origin develop:refs/remotes/origin/develop master:refs/remotes/origin/master

# **SHA の一致ではなく「master に入っていない commit が無いか」で判定する。**
# master へは merge コミットで取り込むため、中身が同じでも SHA は一致しない。
# 取り込みの PR が別経路で先にマージされた後の再実行で、PR を作りに行って
# 「No commits between master and develop」で断られる（Issue #460）
if git merge-base --is-ancestor refs/remotes/origin/develop refs/remotes/origin/master; then
    echo "== develop は既に master へ入っている。取り込みの PR は省く"
    release_pr=""
else
    release_pr="$(find_or_create_pr master develop \
        "リリース $NEXT_TAG" \
        "## 概要

リリース **\`$NEXT_TAG\`** のため \`develop\` を \`master\` へ入れる。
リリース実行ワークフロー（\`.github/workflows/release-run.yml\`）が \`$BUMP\` の指定で自動採番した。

## この後の流れ

マージされると、同じワークフローが \`master\` の該当コミットへ \`$NEXT_TAG\` を付けて push する。
タグの push を受けて \`release.yml\` が検証と GitHub Release の作成を行う。

## ⚠️ マージ前に人が確かめること

**機械で確かめられないものが残っている**（\`_documents/リリース手順書.md\`「リリース前の確認」）。
**タグは付け直せない。** 間違えたら次のパッチ版を出すことになる。

- [ ] 破壊的変更があればメジャーを上げているか（今回の指定は \`$BUMP\`）
- [ ] マイグレーションがある場合、適用手順を Release 本文へ追記する用意があるか
- [ ] 依存のライセンスが寛容ライセンス（MIT / BSD 系 / Apache-2.0）のままか")"

    merge_when_green "$release_pr" merge
fi

# ---- 5. master へタグを付けて push -------------------------------------------
git fetch --no-tags origin master:refs/remotes/origin/master
release_sha="$(git rev-parse refs/remotes/origin/master)"

if git ls-remote --exit-code --tags origin "refs/tags/$NEXT_TAG" >/dev/null 2>&1; then
    echo "== タグ $NEXT_TAG は既に push 済み"
else
    echo "== $NEXT_TAG を master ($release_sha) へ付けて push する"
    git tag "$NEXT_TAG" "$release_sha"
    git push origin "refs/tags/$NEXT_TAG"
fi

# 空なら「（不要）」、番号があれば「#123」
pr_label() {
    if [[ -n "$1" ]]; then printf '#%s' "$1"; else printf '（不要）'; fi
}

summary <<MARKDOWN
## リリース $NEXT_TAG を実行した

| 項目 | 値 |
| --- | --- |
| タグ | \`$NEXT_TAG\` |
| bump | \`$BUMP\` |
| master のコミット | \`$release_sha\` |
| VersionPrefix の PR | $(pr_label "$bump_pr") |
| develop → master の PR | $(pr_label "$release_pr") |

**タグの push を受けて \`release.yml\` が GitHub Release を作る。**
できあがった本文には目を通すこと。マイグレーションの適用手順など、
コミット件名に出てこないものは入らない。
MARKDOWN

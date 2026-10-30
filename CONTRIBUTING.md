# 貢献のしかた

**日本語で書いてください。** Issue・PR のタイトルと本文、コミットメッセージ、
コードのコメント、レビューのコメント。すべて日本語です
（[`.github/copilot-instructions.md`](.github/copilot-instructions.md)）。

## CLA へ署名する

本書で「当社」とは、株式会社ピー・エム・シー ITソリューション事業部を指します。

**貢献を取り込む前に、[`CLA.md`](CLA.md) への署名が要ります。**

本製品はデュアルライセンス（AGPL ＋ 商用）です。
**他人の著作物を商用ライセンスで再頒布する権利は、その人から貰わない限りありません。**
署名のない貢献を取り込むと、**その部分だけ商用ライセンスで提供できなくなります。**

### 手順

1. [`CLA.md`](CLA.md) を読む
2. **CLA への署名** の Issue を立てる（テンプレートあり）
3. 当社が確認し、[`.github/cla/signed.yml`](.github/cla/signed.yml) へ追記する
4. 以後の PR では `CLA` のチェックが緑になる

**会社の業務として貢献する場合は、法人用の署名も要ります。**
勤務先の承認を先に取ってください。

### 署名済みかどうかの確認

PR を出すと `CLA` のチェックが走ります。
**未署名なら赤くなり、何をすればよいかがコメントで出ます。**

## 作業の進め方

| 段階 | すること |
|---|---|
| 1 | **Issue を作る** — 背景・対応内容・制約を書く |
| 2 | **作業ブランチを切る** — 分岐元は `develop`。`master` ではない |
| 3 | **実装してコミット** — 変更意図が追える粒度に分ける |
| 4 | **PR を出す** — base は `develop`。本文へ `Closes #N` |

- PR は `--draft` で作り、タイトルへ `WIP:` を付ける
  （`[WIP]` ではない）
- **Draft 解除＝「レビュー可能」、`WIP:` 除去＝「マージ可能」**
- **`WIP:` が付いたままではマージできません**（チェックで止まります）

詳細は [`_documents/ブランチ運用方針.md`](_documents/ブランチ運用方針.md)。

## 開発環境

.NET SDK（`global.json` の版）と、管理画面を触るなら Node.js 24。手順は [`README.md`](README.md) の「開発」。

```bash
dotnet build
dotnet test
```

実機の Pleasanter・4 種の RDBMS・Keycloak を使う検証は、[`tools/pleasanter-testenv`](tools/pleasanter-testenv/README.md) と
[`tools/saml-idp`](tools/saml-idp/README.md)（CI の「端から端まで通す試験」と同じもの）。

## 守っていただきたいこと

- **Pleasanter 本体（AGPL v3）のコードを取り込まないこと。**
  コピー・流用・リンク・プロジェクト参照のいずれも禁止。
  `_reference/` は事実確認のための参照専用
- **依存は寛容ライセンス（MIT / BSD 系 / Apache-2.0）に限ること。**
  追加時にライセンスを**一次情報で確認**し、[`NOTICE`](NOTICE) へ追記する
  （**フォントに限り SIL OFL 1.1 も可**。[`.github/copilot-instructions.md`](.github/copilot-instructions.md) の規約 7）
- **Pleasanter の API キーを購読側（カレンダークライアント）へ渡さないこと**
- **コミットメッセージに `[skip ci]` を含めないこと**

理由は [`.github/copilot-instructions.md`](.github/copilot-instructions.md) にあります。

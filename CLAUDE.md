# Claude向けリポジトリ指示

このリポジトリで作業する場合は、共通のリポジトリ指示に従ってください。

@.github/copilot-instructions.md

パスに紐づく詳細な規約は `.github/instructions/*.instructions.md` に分かれています。
**そのパスのファイルを触る前に、該当するものを読んでください。**
Claude Code は `applyTo` を自動で解釈しないため、自分で開く必要があります。

- [ドキュメント](.github/instructions/ドキュメント.instructions.md) — `_documents/**`

これらの詳細ルールは上記Copilot指示および各 instructions ファイルを単一の参照元とし、
このファイルへ複製しないでください。

## このリポジトリ固有の注意

- **`_reference/Implem.Pleasanter` は Pleasanter 本体（AGPL v3）の非追跡の参照ソースです。**
  読む前に `bash scripts/sync-reference.sh` で上流 `main` の先頭へ更新します。
  `PLEASANTER_REFERENCE_DIR` を指定した場合は共有取得先を直接読み、リンクは作りません。
  AI の事実確認専用で、コードの取り込み・ビルド・配布物への混入は禁止です。
  確認した完全な SHA・固定リンク・参照日を残し、実機未検証なら明記します。
  詳細は [`_reference/README.md`](_reference/README.md) を参照してください。
- **Pleasanter の API キーは、リクエストボディに載せる方式です**
  （`_reference/Implem.Pleasanter/Implem.Pleasanter/Libraries/Requests/Context.cs`）。
  サーバ側だけで保持し、購読側へは渡しません。
- 兄弟リポジトリ `VehicleVision.PleasanterTools.Questionnaire` は、構成・規約・Pleasanter 連携の
  知見の参照元です（コードを流用するときは、同じ組織の成果物であっても依存 OSS のライセンスと
  NOTICE を確認すること）。

## 共有メモリリポジトリ

このリポジトリで作業して得た知見は、internal リポジトリ
[VehicleVision.PleasanterTools.ClaudeMemory](https://github.com/vehiclevisionjp/VehicleVision.PleasanterTools.ClaudeMemory)
の `CalendarFeed/` フォルダで共有します（ツールごとにリポジトリは分けず、ツール名のフォルダで分けます）。
**そのリポジトリを 1 か所だけ clone し**、ハーネスのメモリディレクトリは、
その `CalendarFeed/` フォルダへの Windows ジャンクションです。運用の詳細は同リポジトリの README.md を読んでください。

```text
~/.claude/projects/D--repos-VehicleVision-PleasanterTools-CalendarFeed/memory/
```

- **別途 clone を作らないこと。** 2 つあると必ず片方が古くなる
- セッション開始時に `git -C <memory> pull --rebase` で他の人の追記を取り込むこと
- メモリを追加・更新したら `MEMORY.md` の索引も更新し、コミットして push すること
  （本体リポジトリと違い、push まで指示を待たなくてよい）
- 書いてよいのは「コードや git 履歴を読んでも分からないこと」だけ。認証情報
  （**Pleasanter の API キーは特に**）・個人の属性は書かない
- Pleasanter の挙動を書くときは、**確認したバージョンと参照日**を併記する
- **Pleasanter の挙動は `Questionnaire/` フォルダにも蓄積がある**（日時の扱い・API の癖など）。
  該当しそうなら先に読み、**CalendarFeed で再確認できていないものは未確認として扱う**

なお共有メモリは正式な規約の置き場所ではありません。このファイルと
`.github/copilot-instructions.md` が常に優先され、食い違ったらメモリ側を直します。

### 本リポジトリの規約を変えたとき

**共有メモリに古くなった記述が無いか必ず確認すること。** 規約変更はメモリを黙って矛盾させます。

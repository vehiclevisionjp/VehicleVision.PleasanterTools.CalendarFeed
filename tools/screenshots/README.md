# 管理画面の写しを撮る（開発専用）

**手で撮ると、画面が変わるたびに古くなる。** Playwright（`playwright-core`）で撮り直せるようにしてある。結果は
[`_documents/管理画面-画面の写し一覧.md`](../../_documents/管理画面-画面の写し一覧.md) から参照している。

## 撮り方

```bash
# 1. 管理画面を組み立てる（成果物は .Web の wwwroot/admin へ出る）
cd src/VehicleVision.PleasanterTools.CalendarFeed.Frontend
npm ci && npm run build

# 2. アプリを組み立てる
cd ../..
dotnet build

# 3. 撮る
cd tools/screenshots
npm ci
node capture.mjs
```

- **一時の SQLite の DB で本体を起こし、Pleasanter は偽物（予定 3 件を返す小さな HTTP サーバ）に向ける。**
  最初の管理者・設定・フィード・招待・2 要素を、実際の初回の流れのまま辿る。終わると本体も DB も片付ける。
- ブラウザは**入っている Edge / Chrome を使う**（ブラウザのダウンロードはしない）。見つからなければ落ちる。
- 2 要素の数字は、スクリプトが RFC 6238 で計算する（認証アプリの代わり）。同じ 30 秒の枠の数字は二度通らない仕様なので、ログインの前に次の枠まで待つ（30 秒ほど余計にかかる）。
- 写しは `_documents/画面の写し/` へ出る（既存のものは上書き）。
- 依存（`playwright-core`）は Apache-2.0。**開発専用で、配布物には含まれない**（NOTICE の「開発のみ」）。

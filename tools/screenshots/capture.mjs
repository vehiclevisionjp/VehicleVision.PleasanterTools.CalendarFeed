// 管理画面の写しを撮る（開発専用）。
//
// 使い方（リポジトリのルートで。README.md 参照）:
//   1. 管理画面を組み立てる:   cd src/VehicleVision.PleasanterTools.CalendarFeed.Frontend && npm ci && npm run build
//   2. アプリを組み立てる:     dotnet build
//   3. 撮る:                   cd tools/screenshots && npm ci && node capture.mjs
//
// 本体を一時の DB で起動し、**Pleasanter は偽物（このスクリプトが起こす小さな HTTP サーバ）**に向ける。
// 実際の初回の流れ（最初の管理者・設定・フィード・招待・2 要素）をそのまま辿って撮るので、画面が変わったら撮り直せる。
// ブラウザは入っている Edge / Chrome を使う（ブラウザのダウンロードはしない）。
import { createHmac } from 'node:crypto';
import { spawn, execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, rmSync } from 'node:fs';
import http from 'node:http';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, '..', '..');
const outDir = path.join(root, '_documents', '画面の写し');
const webProject = path.join(root, 'src', 'VehicleVision.PleasanterTools.CalendarFeed.Web');
const APP = 'http://localhost:5099';
const FAKE_PLEASANTER_PORT = 18080;
const PASSWORD = 'correct horse battery staple';

mkdirSync(outDir, { recursive: true });

// ---- 偽の Pleasanter（Get API。予定を 3 件返す） ------------------------------------
const records = [
  { IssueId: 101, Ver: 1, Title: '週次の定例会議', Body: '議題の確認', StartTime: '2026-10-19T10:00:00', CompletionTime: '2026-10-19T11:00:00', ClassA: '会議' },
  { IssueId: 102, Ver: 1, Title: '大阪出張', Body: '', StartTime: '2026-10-22T00:00:00', CompletionTime: '2026-10-23T00:00:00', ClassA: '出張' },
  { IssueId: 103, Ver: 1, Title: '四半期レビュー', Body: '資料を事前に共有', StartTime: '2026-10-28T14:00:00', CompletionTime: '2026-10-28T16:00:00', ClassA: '会議' },
];
const fake = http.createServer((req, res) => {
  req.resume();
  req.on('end', () => {
    res.setHeader('Content-Type', 'application/json');
    res.end(JSON.stringify({ StatusCode: 200, Response: { Offset: 0, PageSize: 200, TotalCount: records.length, Data: records } }));
  });
});
await new Promise((resolve) => fake.listen(FAKE_PLEASANTER_PORT, resolve));

// ---- 本体の起動 ------------------------------------------------------------------
const db = path.join(os.tmpdir(), `calendarfeed-shots-${process.pid}.db`);
const secret = execFileSync('dotnet', ['run', '--no-build', '--project', webProject, '--', '--generate-secret-key'], { encoding: 'utf8' }).trim().split('\n').pop().trim();
const app = spawn('dotnet', ['run', '--no-build', '--project', webProject], {
  env: {
    ...process.env,
    ASPNETCORE_URLS: APP,
    CALENDARFEED_DB_CONNECTIONSTRING: `Data Source=${db}`,
    CALENDARFEED_SECRET_KEY: secret,
    // 公開 URL は固定する（設定画面に「固定」の表示が出る）。Pleasanter の接続先などは、画面から設定する
    CALENDARFEED_PUBLIC_BASEURL: APP,
  },
  stdio: 'ignore',
});

async function waitForApp() {
  for (let i = 0; i < 60; i++) {
    try {
      if ((await fetch(`${APP}/admin/api/status`)).ok) return;
    } catch {
      // まだ起動していない
    }
    await new Promise((r) => setTimeout(r, 1000));
  }
  throw new Error('アプリが起動しない');
}

function stop() {
  try {
    execFileSync('taskkill', ['/PID', String(app.pid), '/T', '/F'], { stdio: 'ignore' });
  } catch {
    app.kill();
  }
  fake.close();
  try {
    rmSync(db, { force: true });
  } catch {
    // 使用中なら、そのまま残す
  }
}

// ---- TOTP（RFC 6238。認証アプリの代わり） -------------------------------------------
function base32(s) {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
  let bits = '';
  for (const c of s.replace(/=+$/, '').toUpperCase()) bits += alphabet.indexOf(c).toString(2).padStart(5, '0');
  const bytes = [];
  for (let i = 0; i + 8 <= bits.length; i += 8) bytes.push(parseInt(bits.slice(i, i + 8), 2));
  return Buffer.from(bytes);
}
function totp(secretBase32) {
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(Math.floor(Date.now() / 30000)));
  const h = createHmac('sha1', base32(secretBase32)).update(counter).digest();
  const o = h[h.length - 1] & 0xf;
  const code = ((h[o] & 0x7f) << 24) | (h[o + 1] << 16) | (h[o + 2] << 8) | h[o + 3];
  return String(code % 1_000_000).padStart(6, '0');
}

// ---- 撮る ------------------------------------------------------------------------
const executable = [
  'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
  'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
  '/usr/bin/google-chrome',
  '/usr/bin/chromium',
].find(existsSync);
if (!executable) throw new Error('Edge / Chrome が見つからない');

let n = 0;
const files = [];
async function shot(page, name, { full = true } = {}) {
  n += 1;
  const file = `${String(n).padStart(2, '0')}-${name}.png`;
  await page.waitForTimeout(250);
  await page.screenshot({ path: path.join(outDir, file), fullPage: full });
  files.push(file);
  console.log('shot', file);
}

try {
  await waitForApp();
  const browser = await chromium.launch({ executablePath: executable, args: ['--lang=ja-JP'] });
  const context = await browser.newContext({ viewport: { width: 1280, height: 800 }, locale: 'ja-JP', colorScheme: 'light' });
  const page = await context.newPage();
  // CSP に違反する書き方（インラインの style・script など）が紛れ込んだら、ここで落とす
  const violations = [];
  for (const target of [page]) {
    target.on('console', (m) => {
      if (/content security policy/i.test(m.text())) violations.push(m.text());
    });
  }

  // 1. 最初の管理者
  await page.goto(`${APP}/admin/`);
  await page.waitForSelector('#setup-id');
  await shot(page, '最初の管理者', { full: false });
  await page.fill('#setup-id', 'admin@example.com');
  await page.fill('#setup-pw', PASSWORD);
  await page.click('button[type=submit]');
  await page.waitForSelector('text=フィードはまだありません');
  await shot(page, 'フィード-空');

  // 2. 設定（Pleasanter の接続先を画面から設定する）
  await page.click('nav button:has-text("設定")');
  await page.waitForSelector('#CALENDARFEED_PLEASANTER_BASEURL');
  await page.fill('#CALENDARFEED_PLEASANTER_BASEURL', `http://localhost:${FAKE_PLEASANTER_PORT}`);
  await page.fill('#CALENDARFEED_PLEASANTER_APIKEY', 'a-shared-pleasanter-api-key-0123456789');
  await page.fill('#CALENDARFEED_FEED_CACHESECONDS', '60');
  await shot(page, '設定-編集中');
  await page.click('button:has-text("設定を保存")');
  await page.waitForSelector('text=設定を保存しました');
  await shot(page, '設定-保存後');

  // 3. フィードを作る
  await page.click('nav button:has-text("フィード")');
  await page.click('button:has-text("フィードを作る")');
  await page.waitForSelector('#f-name');
  await page.fill('#f-name', '共有カレンダー');
  await page.fill('#f-site', '123');
  await page.fill('#m-sum', 'Title');
  await page.fill('#m-loc', 'ClassA');
  await page.fill('#f-filters', 'Status=["100"]');
  await shot(page, 'フィード-作成');
  await page.click('dialog button[type=submit]');
  await page.waitForSelector('text=フィードを作りました');
  const url = await page.inputValue('dialog .copy-row input');
  await shot(page, 'フィード-購読URL');
  await page.click('dialog button:has-text("閉じる")');
  // 購読して、最後の取得結果を出す
  const ics = await fetch(url).then((r) => r.text());
  if (!ics.includes('BEGIN:VCALENDAR')) throw new Error('購読 URL から .ics が取れない');
  await page.click('button:has-text("再読み込み")');
  await page.waitForSelector('text=成功（3 件）');
  await shot(page, 'フィード-一覧');

  // 4. フィードの編集
  await page.click('button:has-text("編集")');
  await page.waitForSelector('#f-name');
  await page.fill('#f-name', '共有カレンダー（営業部）');
  await shot(page, 'フィード-編集');
  await page.click('dialog button[type=submit]');
  await page.waitForSelector('text=保存しました');

  // 5. 管理者を招待する
  await page.click('nav button:has-text("管理者")');
  await page.click('button:has-text("管理者を招待する")');
  await page.waitForSelector('#inv-login');
  await page.fill('#inv-login', 'editor@example.com');
  await page.selectOption('#inv-role', 'Editor');
  await shot(page, '管理者-招待');
  await page.click('dialog button[type=submit]');
  await page.waitForSelector('text=招待を出しました');
  const inviteUrl = await page.inputValue('dialog .copy-row input');
  await shot(page, '管理者-招待を出した');
  await page.click('dialog button:has-text("閉じる")');
  await shot(page, '管理者-一覧');

  // 6. 招待を受け取る（別の端末＝別のブラウザコンテキスト）
  const guest = await browser.newContext({ viewport: { width: 1280, height: 800 }, locale: 'ja-JP', colorScheme: 'light' });
  const gp = await guest.newPage();
  await gp.goto(inviteUrl);
  await gp.waitForSelector('#inv-pw');
  await shot(gp, '招待の受け取り', { full: false });
  await gp.fill('#inv-pw', PASSWORD);
  await gp.fill('#inv-pw2', PASSWORD);
  await gp.click('button[type=submit]');
  await gp.waitForSelector('nav button:has-text("アカウント")');
  await shot(gp, '編集者の画面（権限で出し分け）');
  await guest.close();

  // 7. 監査ログ
  await page.click('nav button:has-text("監査ログ")');
  await page.waitForSelector('table');
  await shot(page, '監査ログ');

  // 8. 2 要素の登録
  await page.click('nav button:has-text("アカウント")');
  await page.click('button:has-text("2 要素認証を登録する")');
  await page.fill('#enroll-pw', PASSWORD);
  await page.click('button[type=submit]:has-text("2 要素認証を登録する")');
  await page.waitForSelector('[data-testid=totp-secret]');
  const totpSecret = (await page.textContent('[data-testid=totp-secret]')).trim();
  await shot(page, '2要素-登録');
  await page.fill('#enroll-code', totp(totpSecret));
  await page.click('button:has-text("登録する")');
  await page.waitForSelector('text=復旧コードを控えてください');
  await shot(page, '2要素-復旧コード');
  await page.click('button:has-text("控えました")');
  await page.waitForSelector('text=登録済みです');
  await shot(page, 'アカウント');

  // SAML を有効にする（ログイン画面に釦が出る。IdP の値は検証用の Keycloak の realm のもの。実際には繋がない）
  const realm = JSON.parse(readFileSync(path.join(here, '..', 'saml-idp', 'realm', 'realm-calendarfeed.json'), 'utf8'));
  const idpCertificate = realm.components['org.keycloak.keys.KeyProvider'].find((c) => c.config.certificate).config.certificate[0];
  const saml = await page.request.put(`${APP}/admin/api/settings/`, {
    data: {
      values: {
        CALENDARFEED_SAML_ENABLED: 'true',
        CALENDARFEED_SAML_ENTITYID: 'http://localhost:5099/admin/api/saml/metadata',
        CALENDARFEED_SAML_IDPENTITYID: 'https://idp.example.com/realms/calendarfeed',
        CALENDARFEED_SAML_SINGLESIGNONURL: 'https://idp.example.com/realms/calendarfeed/protocol/saml',
        CALENDARFEED_SAML_IDPCERTIFICATE: `-----BEGIN CERTIFICATE-----\n${idpCertificate}\n-----END CERTIFICATE-----`,
        CALENDARFEED_SAML_BUTTONLABEL: '社内アカウント（SAML）でログイン',
        // Pleasanter のログイン（接続先は偽の Pleasanter。画面を撮るだけで、ログインは試さない）
        CALENDARFEED_PLEASANTERSSO_ENABLED: 'true',
        CALENDARFEED_PLEASANTERSSO_INTERNALBASEURL: `http://localhost:${FAKE_PLEASANTER_PORT}`,
        CALENDARFEED_PLEASANTERSSO_LOGINURL: '/users/login',
        CALENDARFEED_PLEASANTERSSO_LOGOUTURL: '/users/logout',
      },
    },
  });
  if (!saml.ok()) throw new Error(`SAML の設定に失敗: ${saml.status()} ${await saml.text()}`);

  // 9. ログイン（2 要素）。SAML の釦も出る
  await page.click('button:has-text("ログアウト")');
  await page.reload(); // SAML の有効・無効は、画面を開いたときに読む
  await page.waitForSelector('#login-id');
  await page.waitForSelector('a.btn-link');
  await shot(page, 'ログイン', { full: false });
  await page.fill('#login-id', 'admin@example.com');
  await page.fill('#login-pw', PASSWORD);
  await page.click('button[type=submit]');
  await page.waitForSelector('#totp-code');
  await shot(page, 'ログイン-2要素', { full: false });
  // 同じ 30 秒の枠の数字は二度通さない（登録のときに使った数字の再利用を断る）ので、次の枠まで待つ
  await page.waitForTimeout(30000 - (Date.now() % 30000) + 1500);
  await page.fill('#totp-code', totp(totpSecret));
  await page.click('button[type=submit]');
  await page.waitForSelector('nav button:has-text("設定")');

  // 10. 英語・スマートフォン幅
  await page.selectOption('select.lang', 'en');
  await page.waitForSelector('text=Subscription feeds');
  await shot(page, 'フィード-英語');
  await page.setViewportSize({ width: 390, height: 800 });
  await shot(page, 'フィード-スマートフォン幅');
  await page.setViewportSize({ width: 1280, height: 800 });
  await page.emulateMedia({ colorScheme: 'dark' });
  await shot(page, 'フィード-ダークモード');

  // 11. 設定の SAML の群（IdP へ登録する URL が出る）
  await page.emulateMedia({ colorScheme: 'light' });
  await page.selectOption('select.lang', 'ja');
  await page.click('nav button:has-text("設定")');
  await page.waitForSelector('text=SP のメタデータ URL');
  await page.waitForSelector('text=接続を試す');
  await shot(page, '設定-SAML');

  // 12. 管理者が見る、利用者のログイン中の端末（入った方式つき。1 つずつ失効させられる）
  await page.click('nav button:has-text("管理者")');
  await page.waitForSelector('text=editor@example.com');
  await page.locator('tr', { hasText: 'editor@example.com' }).locator('button:text-is("端末")').click();
  await page.waitForSelector('dialog .badge');
  await shot(page, '管理者-端末');
  await page.click('dialog button:has-text("閉じる")');

  await browser.close();
  if (violations.length > 0) throw new Error(`CSP 違反: ${violations.join(' / ')}`);
  console.log(`\n${files.length} 枚を ${outDir} へ出した`);
} finally {
  stop();
}

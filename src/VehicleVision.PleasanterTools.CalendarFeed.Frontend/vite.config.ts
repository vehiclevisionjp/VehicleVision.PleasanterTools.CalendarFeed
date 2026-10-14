import { svelte } from '@sveltejs/vite-plugin-svelte';
// `test` の設定を書けるのは vitest/config の defineConfig
import { defineConfig } from 'vitest/config';

// 組み立て成果物は .Web の wwwroot/admin へ出す。/admin/assets/… として配信される（AdminUi.cs）。
// CSP が `style-src 'self'` / `script-src 'self'` なので、インラインにならないよう、小さい資産も別ファイルで出す
export default defineConfig({
  plugins: [svelte()],
  base: '/admin/',
  build: {
    outDir: '../VehicleVision.PleasanterTools.CalendarFeed.Web/wwwroot/admin',
    emptyOutDir: true,
    assetsInlineLimit: 0,
    cssCodeSplit: false,
  },
  server: {
    // 開発時は本体（dotnet run）へ中継する
    proxy: { '/admin/api': 'http://localhost:5000', '/feeds': 'http://localhost:5000' },
  },
  test: {
    // lib/ の純粋な関数だけを見る（コンポーネントの描画試験は広げない）
    include: ['src/**/*.test.ts'],
    environment: 'node',
  },
});

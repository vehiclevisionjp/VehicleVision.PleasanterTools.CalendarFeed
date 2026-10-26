import { afterEach, describe, expect, it, vi } from 'vitest';
import { api } from './api';

vi.mock('./i18n/state.svelte', () => ({ language: () => 'ja' }));

afterEach(() => vi.unstubAllGlobals());

describe('2 要素の登録要求', () => {
  it('ログイン途中の初回登録は従来の経路と空の本文で開始する', async () => {
    const fetch = vi.fn().mockResolvedValue(Response.json({ secret: 'secret', uri: 'uri' }));
    vi.stubGlobal('fetch', fetch);

    await api.totpBegin();

    expect(fetch).toHaveBeenCalledWith('/admin/api/me/totp/begin', expect.objectContaining({
      credentials: 'same-origin', body: '{}',
    }));
  });

  it('ログイン後の登録開始だけに確認用パスワードを送る', async () => {
    const fetch = vi.fn().mockResolvedValue(Response.json({ secret: 'secret', uri: 'uri' }));
    vi.stubGlobal('fetch', fetch);

    await api.totpBegin('current password');

    expect(fetch).toHaveBeenCalledWith('/admin/api/me/totp/begin', expect.objectContaining({
      body: '{"password":"current password"}',
      headers: { 'Content-Type': 'application/json', 'Accept-Language': 'ja' },
    }));
  });

  it('登録完了では共有鍵やパスワードを送らずコードだけを送る', async () => {
    const fetch = vi.fn().mockResolvedValue(Response.json({ recoveryCodes: [] }));
    vi.stubGlobal('fetch', fetch);

    await api.totpComplete('123456');

    expect(fetch).toHaveBeenCalledWith('/admin/api/me/totp/complete', expect.objectContaining({
      body: '{"code":"123456"}',
    }));
  });
});

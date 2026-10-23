import { describe, expect, it, vi } from 'vitest';
import { errorText } from './errors';

vi.mock('./i18n/state.svelte', () => ({ t: (key: string) => key }));

describe('再確認の締め出し', () => {
  it('423 はサーバが要求言語で返した理由を表示する', () => {
    expect(errorText({ ok: false, status: 423, message: '一時的に操作できません。' })).toBe('一時的に操作できません。');
  });

  it('423 に文言がなければ既存の再試行案内を使う', () => {
    expect(errorText({ ok: false, status: 423, message: '' })).toBe('errorTooMany');
  });
});

import { describe, expect, it } from 'vitest';
import { LANGUAGES, MESSAGES } from './messages';
import { DEFAULT_LANGUAGE, format, formatDateTime, negotiateLanguage, toLanguage } from './language';

const placeholders = (text: string): string[] => [...text.matchAll(/\{(\w+)\}/g)].map((m) => m[1] ?? '').sort();

describe('文言', () => {
  const keys = Object.keys(MESSAGES.ja);

  it('7 言語すべてが同じキーを持つ', () => {
    for (const language of LANGUAGES) {
      expect(Object.keys(MESSAGES[language]).sort(), language).toEqual([...keys].sort());
    }
  });

  it('埋め込み {…} は、どの言語でも日本語と同じ集合', () => {
    for (const language of LANGUAGES) {
      for (const key of keys) {
        const text = (MESSAGES[language] as Record<string, string>)[key] ?? '';
        expect(placeholders(text), `${language}:${key}`).toEqual(placeholders((MESSAGES.ja as Record<string, string>)[key] ?? ''));
      }
    }
  });

  it('空の文言は無い', () => {
    for (const language of LANGUAGES) {
      for (const [key, text] of Object.entries(MESSAGES[language])) {
        expect(text.trim(), `${language}:${key}`).not.toBe('');
      }
    }
  });

  it('日本語以外は、日本語の文字を含まない（訳し忘れの検出）', () => {
    const japanese = /[぀-ヿ一-鿿]/;
    for (const language of LANGUAGES) {
      if (language === 'ja' || language === 'zh') {continue;}
      for (const [key, text] of Object.entries(MESSAGES[language])) {
        expect(japanese.test(text), `${language}:${key}`).toBe(false);
      }
    }
  });
});

describe('言語の決め方', () => {
  it('地域を落とし、対応外は null', () => {
    expect(toLanguage('en-US')).toBe('en');
    expect(toLanguage('ZH_cn')).toBe('zh');
    expect(toLanguage('fr')).toBeNull();
    expect(toLanguage(null)).toBeNull();
  });

  it('保存した選択 → 利用者の言語 → ブラウザ → 既定の順', () => {
    expect(negotiateLanguage('de', 'ko', ['en'])).toBe('de');
    expect(negotiateLanguage(null, 'ko', ['en'])).toBe('ko');
    expect(negotiateLanguage(null, null, ['fr', 'es-MX'])).toBe('es');
    expect(negotiateLanguage('xx', undefined, ['fr'])).toBe(DEFAULT_LANGUAGE);
  });
});

describe('書式', () => {
  it('{name} を埋め、無いものはそのまま', () => {
    expect(format('{a} と {b}', { a: 1 })).toBe('1 と {b}');
    expect(format('そのまま')).toBe('そのまま');
  });

  it('壊れた日時は元の文字列のまま', () => {
    expect(formatDateTime('not a date', 'ja')).toBe('not a date');
    expect(formatDateTime(null, 'ja')).toBe('');
  });
});

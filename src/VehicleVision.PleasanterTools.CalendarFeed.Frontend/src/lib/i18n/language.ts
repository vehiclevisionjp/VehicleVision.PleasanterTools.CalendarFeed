import { LANGUAGES, type Language } from './messages';

export const DEFAULT_LANGUAGE: Language = 'ja';

/** 対応する言語へ寄せる。地域（`en-US`）は落とし、対応外は null。 */
export function toLanguage(value: string | null | undefined): Language | null {
  const code = value?.trim().split(/[-_]/)[0]?.toLowerCase();
  return (LANGUAGES as readonly string[]).includes(code ?? '') ? (code as Language) : null;
}

/**
 * 画面の言語の決め方: **利用者の設定（保存した選択・ログイン中の利用者の言語）→ ブラウザの言語設定 → ja**。
 */
export function negotiateLanguage(
  stored: string | null | undefined,
  user: string | null | undefined,
  browser: readonly string[],
): Language {
  return (
    toLanguage(stored) ??
    toLanguage(user) ??
    browser.map((b) => toLanguage(b)).find((l): l is Language => l !== null) ??
    DEFAULT_LANGUAGE
  );
}

/** 日時を言語に合わせて書く。壊れた値は元の文字列のまま返す。 */
export function formatDateTime(value: string | Date | null | undefined, language: Language): string {
  if (value === null || value === undefined) {return '';}
  const date = value instanceof Date ? value : new Date(value);
  if (Number.isNaN(date.getTime())) {return String(value);}
  return new Intl.DateTimeFormat(language, { dateStyle: 'medium', timeStyle: 'short' }).format(date);
}

/** `{name}` を埋める。 */
export function format(template: string, params?: Record<string, string | number>): string {
  return params ? template.replace(/\{(\w+)\}/g, (m, k: string) => (k in params ? String(params[k]) : m)) : template;
}

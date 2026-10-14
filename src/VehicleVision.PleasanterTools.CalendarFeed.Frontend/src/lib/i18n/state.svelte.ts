import { DEFAULT_LANGUAGE, formatDateTime as formatDateTimeIn, format, negotiateLanguage } from './language';
import { MESSAGES, type Language, type MessageKey } from './messages';

const STORAGE_KEY = 'calendarfeed.language';

function readStored(): string | null {
  try {
    return localStorage.getItem(STORAGE_KEY);
  } catch {
    return null;
  }
}

let current = $state<Language>(DEFAULT_LANGUAGE);

/** 今の言語。読む所は、切り替えで自動で描き直る。 */
export function language(): Language {
  return current;
}

/** 言語を決める。保存した選択がなければ、ログイン中の利用者の言語、ブラウザの言語設定の順。 */
export function resolveLanguage(userLanguage: string | null | undefined = null): void {
  const next = negotiateLanguage(readStored(), userLanguage, navigator.languages ?? [navigator.language]);
  current = next;
  document.documentElement.lang = next;
}

/** 利用者が選んだ言語を保存して切り替える。 */
export function chooseLanguage(next: Language): void {
  try {
    localStorage.setItem(STORAGE_KEY, next);
  } catch {
    // 保存できなくても、この画面では切り替わる
  }

  current = next;
  document.documentElement.lang = next;
}

/** 文言を引く。`$state` を読むので、呼んだ所が言語の切り替えで描き直る。 */
export function t(key: MessageKey, params?: Record<string, string | number>): string {
  return format(MESSAGES[current][key], params);
}

/** 日時を今の言語で書く。 */
export function dt(value: string | Date | null | undefined): string {
  return formatDateTimeIn(value, current);
}

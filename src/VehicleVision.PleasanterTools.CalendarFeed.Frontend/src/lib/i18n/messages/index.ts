import { ja, type MessageKey } from './ja';
import { en } from './en';
import { de } from './de';
import { es } from './es';
import { ko } from './ko';
import { vi } from './vi';
import { zh } from './zh';

export type { MessageKey };

/** 対応する言語（コードは本体のフィードの言語と同じ 7 つ）。 */
export const LANGUAGES = ['ja', 'en', 'zh', 'de', 'ko', 'es', 'vi'] as const;
export type Language = (typeof LANGUAGES)[number];

/** 言語の自称（言語の選択に出す。翻訳しない）。 */
export const LANGUAGE_NAMES: Record<Language, string> = {
  ja: '日本語',
  en: 'English',
  zh: '中文',
  de: 'Deutsch',
  ko: '한국어',
  es: 'Español',
  vi: 'Tiếng Việt',
};

export const MESSAGES: Record<Language, Record<MessageKey, string>> = { ja, en, zh, de, ko, es, vi };

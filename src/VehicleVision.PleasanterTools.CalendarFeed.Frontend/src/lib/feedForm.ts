import type { Feed, FeedInput, FeedMapping } from './types';

/** フォームの入力（すべて文字列。空は「指定なし」）。 */
export interface FeedFormValues {
  name: string;
  siteId: string;
  language: string;
  apiKeyUserTimeZoneId: string;
  apiKey: string;
  summaryColumn: string;
  summaryTemplate: string;
  startColumn: string;
  startFormat: string;
  endColumn: string;
  endFormat: string;
  descriptionColumn: string;
  locationColumn: string;
  allDay: FeedMapping['allDay'];
  defaultDurationMinutes: string;
  filters: string;
}

/** 本体の既定（`FeedMapping` の既定値と同じ）。 */
export function emptyFeedForm(language: string): FeedFormValues {
  return {
    name: '',
    siteId: '',
    language,
    apiKeyUserTimeZoneId: '',
    apiKey: '',
    summaryColumn: 'ItemTitle',
    summaryTemplate: '',
    startColumn: 'StartTime',
    startFormat: '',
    endColumn: 'CompletionTime',
    endFormat: '',
    descriptionColumn: 'Body',
    locationColumn: '',
    allDay: 'Auto',
    defaultDurationMinutes: '60',
    filters: '',
  };
}

/** 既存のフィードから、編集フォームの初期値を作る。 */
export function formFromFeed(feed: Feed): FeedFormValues {
  const m = feed.mapping;
  return {
    name: feed.name,
    siteId: String(feed.siteId),
    language: feed.language,
    apiKeyUserTimeZoneId: feed.apiKeyUserTimeZoneId ?? '',
    apiKey: '',
    summaryColumn: m.summaryColumn,
    summaryTemplate: m.summaryTemplate ?? '',
    startColumn: m.startColumn,
    startFormat: m.startFormat ?? '',
    endColumn: m.endColumn ?? '',
    endFormat: m.endFormat ?? '',
    descriptionColumn: m.descriptionColumn ?? '',
    locationColumn: m.locationColumn ?? '',
    allDay: m.allDay,
    defaultDurationMinutes: String(m.defaultDurationMinutes),
    filters: filtersToLines(feed.filters),
  };
}

/** `列名=値` の行にする。 */
export function filtersToLines(filters: Record<string, string>): string {
  return Object.entries(filters)
    .map(([k, v]) => `${k}=${v}`)
    .join('\n');
}

/** `列名=値` の行から絞り込みを読む。空行は飛ばす。**最初の `=` で分ける**（値に `=` を含められる）。不正な行は null。 */
export function linesToFilters(text: string): Record<string, string> | null {
  const result: Record<string, string> = {};
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.trim();
    if (line === '') continue;
    const at = line.indexOf('=');
    if (at <= 0) return null;
    result[line.slice(0, at).trim()] = line.slice(at + 1).trim();
  }

  return result;
}

const blankToNull = (v: string): string | null => (v.trim() === '' ? null : v.trim());

/**
 * フォームから送る内容を作る。**入力に問題があれば null**（絞り込みの行が不正・サイト ID が数でない）。
 * 画面で見つけられるのはここまで。列名・書式の細かい検証はサーバが行い、理由を返す。
 */
export function toFeedInput(values: FeedFormValues): FeedInput | null {
  const siteId = Number(values.siteId);
  const filters = linesToFilters(values.filters);
  const duration = Number(values.defaultDurationMinutes);
  if (!Number.isInteger(siteId) || siteId <= 0 || filters === null || !Number.isInteger(duration) || duration <= 0) {
    return null;
  }

  return {
    name: values.name.trim(),
    siteId,
    language: values.language,
    apiKeyUserTimeZoneId: blankToNull(values.apiKeyUserTimeZoneId),
    apiKey: blankToNull(values.apiKey),
    filters,
    mapping: {
      summaryColumn: values.summaryColumn.trim() || 'ItemTitle',
      summaryTemplate: blankToNull(values.summaryTemplate),
      startColumn: values.startColumn.trim() || 'StartTime',
      startFormat: blankToNull(values.startFormat),
      endColumn: blankToNull(values.endColumn),
      endFormat: blankToNull(values.endFormat),
      descriptionColumn: blankToNull(values.descriptionColumn),
      locationColumn: blankToNull(values.locationColumn),
      allDay: values.allDay,
      defaultDurationMinutes: duration,
    },
  };
}

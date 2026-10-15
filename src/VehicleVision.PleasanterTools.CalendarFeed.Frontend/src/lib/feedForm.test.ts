import { describe, expect, it } from 'vitest';
import { emptyFeedForm, filtersToLines, formFromFeed, linesToFilters, toFeedInput } from './feedForm';
import type { Feed } from './types';

describe('絞り込みの行', () => {
  it('最初の = で分け、値に = を含められる', () => {
    expect(linesToFilters('Status=[\\"100\\"]\nClassA=a=b')).toEqual({ Status: '[\\"100\\"]', ClassA: 'a=b' });
  });

  it('空行は飛ばし、空文字は空の絞り込み', () => {
    expect(linesToFilters('')).toEqual({});
    expect(linesToFilters('\n  \nA=1\r\n')).toEqual({ A: '1' });
  });

  it('= の無い行・列名が空の行は不正', () => {
    expect(linesToFilters('abc')).toBeNull();
    expect(linesToFilters('=1')).toBeNull();
  });

  it('往復できる', () => {
    const filters = { A: '1', B: 'x=y' };
    expect(linesToFilters(filtersToLines(filters))).toEqual(filters);
  });
});

describe('フィードのフォーム', () => {
  const feed: Feed = {
    id: 'f1',
    name: '共有予定',
    siteId: 123,
    mapping: {
      summaryColumn: 'Title',
      summaryTemplate: '{ClassA}：{Title}',
      startColumn: 'DateA',
      startFormat: 'yyyy/MM/dd',
      endColumn: null,
      endFormat: null,
      descriptionColumn: 'Body',
      locationColumn: 'ClassB',
      allDay: 'Always',
      defaultDurationMinutes: 30,
    },
    filters: { Status: '100' },
    createdAt: '2026-10-14T00:00:00Z',
    language: 'en',
    apiKeyUserTimeZoneId: 'Asia/Tokyo',
    hasApiKey: true,
    apiKeyHint: 'abcd',
    lastFetch: null,
    range: { pastDays: 30, futureDays: null },
  };

  it('既存のフィードから作ったフォームを、そのまま送る内容へ戻せる', () => {
    const input = toFeedInput(formFromFeed(feed));

    expect(input).toMatchObject({
      name: '共有予定',
      siteId: 123,
      language: 'en',
      apiKeyUserTimeZoneId: 'Asia/Tokyo',
      apiKey: null,
      filters: { Status: '100' },
      mapping: { ...feed.mapping },
    });
  });

  it('空欄は指定なし（null）にし、API キーも空なら送らない', () => {
    const form = { ...emptyFeedForm('ja'), name: ' 予定 ', siteId: '5', endColumn: '', descriptionColumn: ' ' };

    const input = toFeedInput(form)!;

    expect(input.name).toBe('予定');
    expect(input.apiKey).toBeNull();
    expect(input.mapping.endColumn).toBeNull();
    expect(input.mapping.descriptionColumn).toBeNull();
    expect(input.mapping.summaryColumn).toBe('ItemTitle');
  });

  it.each([
    ['サイト ID が数でない', { siteId: 'abc' }],
    ['サイト ID が 0', { siteId: '0' }],
    ['絞り込みの行が不正', { siteId: '5', filters: 'oops' }],
    ['長さが数でない', { siteId: '5', defaultDurationMinutes: 'x' }],
  ])('%s なら送らない', (_name, patch) => {
    expect(toFeedInput({ ...emptyFeedForm('ja'), name: 'a', ...patch })).toBeNull();
  });
});

describe('表示する期間', () => {
  const base = { ...emptyFeedForm('ja'), name: 'x', siteId: '1' };

  it('新しいフィードの既定は 過去 365 日・未来は無制限', () => {
    expect(toFeedInput(base)).toMatchObject({ pastDays: 365, futureDays: null });
  });

  it('無制限にすると null（開区間）。日数の欄は使わない', () => {
    expect(toFeedInput({ ...base, pastUnlimited: true, pastDays: 'abc', futureUnlimited: true })).toMatchObject({ pastDays: null, futureDays: null });
  });

  it('日数は 0〜36500 の整数だけ。範囲外・小数・空は入力の誤り', () => {
    expect(toFeedInput({ ...base, pastDays: '0' })).toMatchObject({ pastDays: 0 });
    expect(toFeedInput({ ...base, pastDays: '36500' })).toMatchObject({ pastDays: 36500 });
    expect(toFeedInput({ ...base, pastDays: '-1' })).toBeNull();
    expect(toFeedInput({ ...base, pastDays: '36501' })).toBeNull();
    expect(toFeedInput({ ...base, pastDays: '1.5' })).toBeNull();
    expect(toFeedInput({ ...base, pastDays: '' })).toBeNull();
    expect(toFeedInput({ ...base, futureUnlimited: false, futureDays: '' })).toBeNull();
  });

  it('既存のフィードの期間から、フォームの初期値を作る', () => {
    const form = formFromFeed({
      ...(JSON.parse(JSON.stringify({ id: 'f', name: 'n', siteId: 1, mapping: emptyMapping(), filters: {}, createdAt: '', language: 'ja', apiKeyUserTimeZoneId: null, hasApiKey: false, apiKeyHint: null, lastFetch: null })) as Feed),
      range: { pastDays: null, futureDays: 90 },
    });

    expect(form).toMatchObject({ pastUnlimited: true, pastDays: '', futureUnlimited: false, futureDays: '90' });
  });
});

function emptyMapping(): Feed['mapping'] {
  return { summaryColumn: 'ItemTitle', summaryTemplate: null, startColumn: 'StartTime', startFormat: null, endColumn: 'CompletionTime', endFormat: null, descriptionColumn: 'Body', locationColumn: null, allDay: 'Auto', defaultDurationMinutes: 60 };
}

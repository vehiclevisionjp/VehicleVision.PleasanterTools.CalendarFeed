import { t } from './i18n/state.svelte';
import type { Result } from './api';

/**
 * 失敗を、画面の言語の文言にする。
 * **400（検証）はサーバの理由をそのまま出す**（何が足りないか分からないと直せない。サーバの文言は日本語）。
 * それ以外は状態から画面の言語で書く（利用者に見せる必要のあるサーバの詳細は無い）。
 */
export function errorText(failure: Extract<Result<unknown>, { ok: false }>): string {
  switch (failure.status) {
    case 0:
      return t('errorNetwork');
    case 400:
      return failure.message || t('errorGeneric');
    case 401:
      return t('errorUnauthorized');
    case 403:
      return t('errorForbidden');
    case 404:
      return t('errorNotFound');
    case 409:
      return failure.message || t('errorConflict');
    case 429:
      return t('errorTooMany');
    default:
      return failure.status >= 500 ? t('errorServer') : t('errorGeneric');
  }
}

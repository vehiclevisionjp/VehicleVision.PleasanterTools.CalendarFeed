<script lang="ts">
  import { onDestroy } from 'svelte';
  import { api } from '../lib/api';
  import { errorText } from '../lib/errors';
  import { t } from '../lib/i18n/state.svelte';
  import type { PleasanterSsoStatus, Step } from '../lib/types';
  import Notice from './Notice.svelte';

  let { sso, onstep }: { sso: PleasanterSsoStatus; onstep: (step: Step) => void } = $props();

  /** ログインが済むのを待つ間の問い合わせ間隔と、待つ長さの上限。 */
  const POLL_MS = 2000;
  const GIVE_UP_MS = 5 * 60 * 1000;

  let waiting = $state(false);
  let busy = $state(false);
  let error = $state('');
  let timer: ReturnType<typeof setTimeout> | null = null;
  let startedAt = 0;

  function stop(): void {
    if (timer !== null) clearTimeout(timer);
    timer = null;
    waiting = false;
  }

  onDestroy(stop);

  /** サーバの印を、画面の言語の文言へ。 */
  function failure(code: string | undefined, status: number): string {
    switch (code) {
      case 'unknown-user':
        return t('pleasanterSsoErrorUnknownUser');
      case 'not-allowed':
        return t('pleasanterSsoErrorNotAllowed');
      case 'disabled':
        return t('pleasanterSsoErrorDisabled');
      case 'upstream-error':
        return t('pleasanterSsoErrorUpstream');
      case 'setup-required':
        return t('pleasanterSsoErrorSetup');
      default:
        return status === 429 ? t('errorTooMany') : t('pleasanterSsoErrorUpstream');
    }
  }

  /** 1 回確かめる。ログイン済みなら先へ進み、まだなら false を返す（待つ側が繰り返す）。 */
  async function check(): Promise<boolean> {
    const result = await api.pleasanterSsoCheck();
    if (!result.ok) {
      stop();
      error = result.status === 0 ? errorText(result) : failure(result.code, result.status);
      return true;
    }

    if (result.value.status === 'signedIn') {
      stop();
      onstep(result.value.next);
      return true;
    }

    return false;
  }

  async function poll(): Promise<void> {
    if (!waiting) return;
    if (Date.now() - startedAt > GIVE_UP_MS) {
      stop();
      error = t('pleasanterSsoTimeout');
      return;
    }

    if (!(await check()) && waiting) timer = setTimeout(poll, POLL_MS);
  }

  async function start(): Promise<void> {
    busy = true;
    error = '';
    // まず確かめる。すでに Pleasanter にログインしていれば、別のウィンドウを開かずに入れる
    const done = await check();
    busy = false;
    if (done) return;

    // まだなら、Pleasanter のログイン画面を別のウィンドウで開き、済むまで確かめ続ける
    waiting = true;
    startedAt = Date.now();
    window.open(sso.loginUrl ?? '', '_blank', 'noopener');
    timer = setTimeout(poll, POLL_MS);
  }
</script>

{#if error}<Notice>{error}</Notice>{/if}
{#if waiting}
  <Notice kind="ok">{t('pleasanterSsoWaiting')}</Notice>
  <p class="small">
    <a href={sso.loginUrl ?? '#'} target="_blank" rel="noopener">{t('pleasanterSsoOpenLogin')}</a>
    ·
    <button class="btn small" type="button" onclick={stop}>{t('cancel')}</button>
  </p>
{:else}
  <p><button class="btn-link" type="button" disabled={busy} onclick={start}>{sso.buttonLabel ?? t('signInWithPleasanter')}</button></p>
{/if}

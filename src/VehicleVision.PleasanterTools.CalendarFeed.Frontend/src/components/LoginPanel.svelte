<script lang="ts">
  import { api } from '../lib/api';
  import { errorText } from '../lib/errors';
  import { t } from '../lib/i18n/state.svelte';
  import type { Step } from '../lib/types';
  import LanguageSelect from './LanguageSelect.svelte';
  import Notice from './Notice.svelte';

  let { onstep }: { onstep: (step: Step) => void } = $props();

  let loginId = $state('');
  let password = $state('');
  let busy = $state(false);
  let error = $state('');

  async function submit(event: SubmitEvent): Promise<void> {
    event.preventDefault();
    busy = true;
    error = '';
    const login = await api.login(loginId.trim(), password);
    busy = false;
    if (!login.ok) {
      // ID が無い・パスワードが違う・無効・締め出し中は、外へ同じ応答（理由を伝えない）
      error = login.status === 401 ? t('loginFailed') : errorText(login);
      return;
    }

    onstep(login.value.step);
  }
</script>

<div class="auth-wrap">
  <div class="card auth-card">
    <div class="page-head">
      <h1>{t('loginTitle')}</h1>
      <LanguageSelect />
    </div>
    {#if error}<Notice>{error}</Notice>{/if}
    <form onsubmit={submit}>
      <div class="field">
        <label for="login-id">{t('loginId')}</label>
        <input id="login-id" type="text" autocomplete="username" required maxlength="100" bind:value={loginId} />
      </div>
      <div class="field">
        <label for="login-pw">{t('password')}</label>
        <input id="login-pw" type="password" autocomplete="current-password" required bind:value={password} />
      </div>
      <button class="btn primary" type="submit" disabled={busy}>{t('loginSubmit')}</button>
    </form>
  </div>
</div>

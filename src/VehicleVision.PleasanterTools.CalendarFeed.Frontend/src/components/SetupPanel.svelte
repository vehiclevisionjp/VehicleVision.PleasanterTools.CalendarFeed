<script lang="ts">
  import { api } from '../lib/api';
  import { errorText } from '../lib/errors';
  import { language, t } from '../lib/i18n/state.svelte';
  import LanguageSelect from './LanguageSelect.svelte';
  import Notice from './Notice.svelte';

  let { ondone }: { ondone: () => void } = $props();

  let loginId = $state('');
  let password = $state('');
  let busy = $state(false);
  let error = $state('');

  async function submit(event: SubmitEvent): Promise<void> {
    event.preventDefault();
    busy = true;
    error = '';
    const created = await api.setup(loginId.trim(), password, language());
    if (!created.ok) {
      error = errorText(created);
      busy = false;
      return;
    }

    // 作ったらそのままログインする（2 要素が必須の構成では、続けて登録の画面へ）
    const login = await api.login(loginId.trim(), password);
    busy = false;
    if (!login.ok) {
      error = errorText(login);
      return;
    }

    ondone();
  }
</script>

<div class="auth-wrap">
  <div class="card auth-card">
    <div class="page-head">
      <h1>{t('setupTitle')}</h1>
      <LanguageSelect />
    </div>
    <p class="muted">{t('setupLead')}</p>
    {#if error}<Notice>{error}</Notice>{/if}
    <form onsubmit={submit}>
      <div class="field">
        <label for="setup-id">{t('loginId')}</label>
        <input id="setup-id" type="text" autocomplete="username" required maxlength="100" bind:value={loginId} />
      </div>
      <div class="field">
        <label for="setup-pw">{t('password')}</label>
        <input id="setup-pw" type="password" autocomplete="new-password" required bind:value={password} />
        <div class="hint">{t('setupPasswordHint', { min: 12 })}</div>
      </div>
      <button class="btn primary" type="submit" disabled={busy}>{t('setupSubmit')}</button>
    </form>
  </div>
</div>

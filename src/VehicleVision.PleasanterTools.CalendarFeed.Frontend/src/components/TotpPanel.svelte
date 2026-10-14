<script lang="ts">
  import { api } from '../lib/api';
  import { errorText } from '../lib/errors';
  import { t } from '../lib/i18n/state.svelte';
  import LanguageSelect from './LanguageSelect.svelte';
  import Notice from './Notice.svelte';

  let { ondone, oncancel }: { ondone: () => void; oncancel: () => void } = $props();

  let useRecovery = $state(false);
  let code = $state('');
  let busy = $state(false);
  let error = $state('');

  async function submit(event: SubmitEvent): Promise<void> {
    event.preventDefault();
    busy = true;
    error = '';
    const result = useRecovery ? await api.loginRecovery(code.trim()) : await api.loginTotp(code.trim());
    busy = false;
    if (!result.ok) {
      error = result.status === 401 ? t('codeFailed') : errorText(result);
      return;
    }

    ondone();
  }
</script>

<div class="auth-wrap">
  <div class="card auth-card">
    <div class="page-head">
      <h1>{useRecovery ? t('recoveryCode') : t('totpTitle')}</h1>
      <LanguageSelect />
    </div>
    <p class="muted">{useRecovery ? t('recoveryLead') : t('totpLead')}</p>
    {#if error}<Notice>{error}</Notice>{/if}
    <form onsubmit={submit}>
      <div class="field">
        <label for="totp-code">{useRecovery ? t('recoveryCode') : t('totpCode')}</label>
        <input
          id="totp-code"
          type="text"
          inputmode={useRecovery ? 'text' : 'numeric'}
          autocomplete="one-time-code"
          required
          bind:value={code}
        />
      </div>
      <div class="actions">
        <button class="btn primary" type="submit" disabled={busy}>{t('totpSubmit')}</button>
        <button class="btn" type="button" onclick={() => ((useRecovery = !useRecovery), (code = ''), (error = ''))}>
          {useRecovery ? t('totpUseApp') : t('totpUseRecovery')}
        </button>
        <button class="btn" type="button" onclick={oncancel}>{t('cancel')}</button>
      </div>
    </form>
  </div>
</div>

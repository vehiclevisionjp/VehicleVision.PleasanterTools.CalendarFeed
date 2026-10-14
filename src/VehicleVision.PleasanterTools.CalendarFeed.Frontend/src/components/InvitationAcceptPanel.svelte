<script lang="ts">
  import { api } from '../lib/api';
  import { errorText } from '../lib/errors';
  import { t } from '../lib/i18n/state.svelte';
  import type { Step } from '../lib/types';
  import LanguageSelect from './LanguageSelect.svelte';
  import Notice from './Notice.svelte';

  let { token, onstep }: { token: string; onstep: (step: Step) => void } = $props();

  let password = $state('');
  let confirm = $state('');
  let busy = $state(false);
  let error = $state('');

  const mismatch = $derived(confirm !== '' && confirm !== password);

  async function submit(event: SubmitEvent): Promise<void> {
    event.preventDefault();
    if (password !== confirm) return;
    busy = true;
    error = '';
    const accepted = await api.acceptInvitation(token, password);
    busy = false;
    if (!accepted.ok) {
      // 使えない招待は、理由を問わず 404。パスワードの条件は 400 で理由が返る
      error = accepted.status === 404 ? t('inviteInvalid') : errorText(accepted);
      return;
    }

    onstep(accepted.value.next);
  }
</script>

<div class="auth-wrap">
  <div class="card auth-card">
    <div class="page-head">
      <h1>{t('inviteTitle')}</h1>
      <LanguageSelect />
    </div>
    {#if !token}
      <Notice>{t('inviteMissingToken')}</Notice>
    {:else}
      <p class="muted">{t('inviteLead')}</p>
      {#if error}<Notice>{error}</Notice>{/if}
      <form onsubmit={submit}>
        <div class="field">
          <label for="inv-pw">{t('newPassword')}</label>
          <input id="inv-pw" type="password" autocomplete="new-password" required bind:value={password} />
          <div class="hint">{t('setupPasswordHint', { min: 12 })}</div>
        </div>
        <div class="field">
          <label for="inv-pw2">{t('confirmPassword')}</label>
          <input id="inv-pw2" type="password" autocomplete="new-password" required bind:value={confirm} aria-invalid={mismatch} />
        </div>
        <button class="btn primary" type="submit" disabled={busy || mismatch}>{t('inviteSubmit')}</button>
      </form>
    {/if}
  </div>
</div>

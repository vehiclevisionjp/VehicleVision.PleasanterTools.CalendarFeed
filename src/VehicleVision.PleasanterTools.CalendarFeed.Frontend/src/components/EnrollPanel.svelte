<script lang="ts">
  import QRCode from 'qrcode';
  import { api } from '../lib/api';
  import { errorText } from '../lib/errors';
  import { t } from '../lib/i18n/state.svelte';
  import Notice from './Notice.svelte';
  import PasswordConfirmation from './PasswordConfirmation.svelte';

  /** ログイン前（必須で未登録の途中）でも、ログイン後の登録・登録し直しでも使う。 */
  let { required = false, confirmPassword = false, ondone, oncancel }: {
    required?: boolean;
    confirmPassword?: boolean;
    ondone: () => void;
    oncancel?: () => void;
  } = $props();

  let password = $state('');
  let secret = $state('');
  let qr = $state('');
  let code = $state('');
  let recoveryCodes = $state<string[] | null>(null);
  let busy = $state(false);
  let error = $state('');

  $effect(() => {
    if (!confirmPassword) {
      void begin();
    }
  });

  async function begin(): Promise<void> {
    busy = true;
    error = '';
    const started = await api.totpBegin(confirmPassword ? password : undefined);
    password = '';
    busy = false;
    if (!started.ok) {
      error = started.status === 401 || started.status === 423 ? started.message || errorText(started) : errorText(started);
      return;
    }

    secret = started.value.secret;
    qr = await QRCode.toDataURL(started.value.uri, { margin: 0, width: 200, errorCorrectionLevel: 'M' });
  }

  async function confirm(event: SubmitEvent): Promise<void> {
    event.preventDefault();
    await begin();
  }

  async function submit(event: SubmitEvent): Promise<void> {
    event.preventDefault();
    busy = true;
    error = '';
    const done = await api.totpComplete(code.trim());
    busy = false;
    if (!done.ok) {
      error = done.status === 400 ? done.message || t('codeFailed') : errorText(done);
      return;
    }

    recoveryCodes = done.value.recoveryCodes;
  }
</script>

<div class={required ? 'auth-wrap' : ''}>
  <div class="card {required ? 'auth-card' : ''}">
    {#if recoveryCodes}
      <h1>{t('recoveryCodesTitle')}</h1>
      <Notice kind="warn">{t('recoveryCodesLead')}</Notice>
      <ul class="codes">
        {#each recoveryCodes as recovery (recovery)}<li>{recovery}</li>{/each}
      </ul>
      <button class="btn primary" type="button" onclick={ondone}>{t('recoveryCodesDone')}</button>
    {:else}
      <h1>{t('enrollTitle')}</h1>
      {#if required}<Notice kind="warn">{t('enrollRequired')}</Notice>{/if}
      {#if error}<Notice>{error}</Notice>{/if}
      {#if confirmPassword && !secret}
        <form onsubmit={confirm}>
          <p class="muted">{t('accountConfirmPassword')}</p>
          <PasswordConfirmation id="enroll-pw" bind:value={password} />
          <div class="actions">
            <button class="btn primary" type="submit" disabled={busy}>{t('accountTotpEnable')}</button>
            {#if oncancel}<button class="btn" type="button" onclick={oncancel}>{t('cancel')}</button>{/if}
          </div>
        </form>
      {:else}
        <p class="muted">{t('enrollLead')}</p>
        {#if qr}<img class="qr" src={qr} alt="QR" width="200" height="200" />{/if}
        {#if secret}
          <p class="small muted">{t('enrollSecret')}</p>
          <p class="mono" data-testid="totp-secret">{secret}</p>
        {/if}
        <form onsubmit={submit}>
          <div class="field">
            <label for="enroll-code">{t('totpCode')}</label>
            <input id="enroll-code" type="text" inputmode="numeric" autocomplete="one-time-code" required bind:value={code} />
          </div>
          <div class="actions">
            <button class="btn primary" type="submit" disabled={busy || !secret}>{t('enrollSubmit')}</button>
            {#if oncancel}<button class="btn" type="button" onclick={oncancel}>{t('cancel')}</button>{/if}
          </div>
        </form>
      {/if}
    {/if}
  </div>
</div>

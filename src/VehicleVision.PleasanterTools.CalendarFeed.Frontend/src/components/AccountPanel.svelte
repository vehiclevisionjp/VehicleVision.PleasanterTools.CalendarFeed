<script lang="ts">
  import { api } from '../lib/api';
  import { errorText } from '../lib/errors';
  import { dt, t } from '../lib/i18n/state.svelte';
  import type { MessageKey } from '../lib/i18n/messages';
  import type { Me, SessionInfo } from '../lib/types';
  import EnrollPanel from './EnrollPanel.svelte';
  import Notice from './Notice.svelte';

  let { me, onchanged }: { me: Me; onchanged: () => void } = $props();

  let current = $state('');
  let next = $state('');
  let confirmNext = $state('');
  let passwordBusy = $state(false);
  let passwordError = $state('');
  let flash = $state('');
  let error = $state('');

  let enrolling = $state(false);
  let disabling = $state(false);
  let disablePassword = $state('');

  let sessions = $state<SessionInfo[] | null>(null);

  const mismatch = $derived(confirmNext !== '' && confirmNext !== next);

  async function loadSessions(): Promise<void> {
    const result = await api.mySessions();
    if (result.ok) {sessions = result.value;}
    else {error = errorText(result);}
  }

  $effect(() => {
    void loadSessions();
  });

  async function changePassword(event: SubmitEvent): Promise<void> {
    event.preventDefault();
    if (next !== confirmNext) {return;}
    passwordBusy = true;
    passwordError = '';
    flash = '';
    const result = await api.changePassword(current, next);
    passwordBusy = false;
    if (!result.ok) {
      passwordError = errorText(result);
      return;
    }

    current = next = confirmNext = '';
    flash = t('accountPasswordChanged');
    await loadSessions();
  }

  async function disableTotp(event: SubmitEvent): Promise<void> {
    event.preventDefault();
    const result = await api.totpDisable(disablePassword);
    if (!result.ok) {
      error = errorText(result);
      return;
    }

    disabling = false;
    disablePassword = '';
    flash = t('accountTotpOffDone');
    onchanged();
  }

  const viaLabel = (via: string): string =>
    via === 'saml' ? t('sessionViaSaml') : via === 'pleasanter' ? t('sessionViaPleasanter') : t('sessionViaPassword');

  async function revoke(handle: string): Promise<void> {
    const result = await api.revokeMySession(handle);
    if (!result.ok) {error = errorText(result);}
    else {flash = t('sessionRevoked');}
    await loadSessions();
  }

  async function revokeOthers(): Promise<void> {
    const result = await api.revokeOtherSessions();
    if (!result.ok) {error = errorText(result);}
    else {flash = t('sessionRevoked');}
    await loadSessions();
  }
</script>

<div class="page-head"><h1>{t('accountTitle')}</h1></div>
<p>{t('accountSignedInAs', { name: me.loginId, role: t(`role${me.role}` as MessageKey) })}</p>
{#if me.lastLoginAt}<p class="muted small">{t('accountLastLogin', { time: dt(me.lastLoginAt) })}</p>{/if}

{#if flash}<Notice kind="ok">{flash}</Notice>{/if}
{#if error}<Notice>{error}</Notice>{/if}

<div class="card">
  <h2>{t('accountPasswordTitle')}</h2>
  {#if passwordError}<Notice>{passwordError}</Notice>{/if}
  <form onsubmit={changePassword}>
    <div class="grid">
      <div class="field">
        <label for="pw-cur">{t('currentPassword')}</label>
        <input id="pw-cur" type="password" autocomplete="current-password" required bind:value={current} />
      </div>
      <div class="field">
        <label for="pw-new">{t('newPassword')}</label>
        <input id="pw-new" type="password" autocomplete="new-password" required bind:value={next} />
      </div>
      <div class="field">
        <label for="pw-new2">{t('confirmPassword')}</label>
        <input id="pw-new2" type="password" autocomplete="new-password" required bind:value={confirmNext} aria-invalid={mismatch} />
      </div>
    </div>
    <button class="btn primary" type="submit" disabled={passwordBusy || mismatch}>{t('save')}</button>
  </form>
</div>

<div class="card">
  <h2>{t('accountTotpTitle')}</h2>
  {#if enrolling}
    <EnrollPanel
      ondone={() => {
        enrolling = false;
        onchanged();
      }}
      oncancel={() => (enrolling = false)}
    />
  {:else}
    <p>{me.totpEnabled ? t('accountTotpOn', { count: me.recoveryCodesLeft }) : t('accountTotpOff')}</p>
    {#if me.twoFactor === 'Disabled'}
      <p class="muted small">{t('accountTotpDisabledByPolicy')}</p>
    {:else}
      <div class="actions">
        <button class="btn" type="button" onclick={() => (enrolling = true)}>{me.totpEnabled ? t('accountTotpReenroll') : t('accountTotpEnable')}</button>
        {#if me.totpEnabled && me.twoFactor !== 'Required'}
          <button class="btn danger" type="button" onclick={() => (disabling = !disabling)}>{t('accountTotpDisable')}</button>
        {/if}
      </div>
      {#if me.totpEnabled && me.twoFactor === 'Required'}<p class="muted small">{t('accountTotpRequiredNoDisable')}</p>{/if}
      {#if disabling}
        <form onsubmit={disableTotp}>
          <p class="muted">{t('accountTotpDisableLead')}</p>
          <div class="field">
            <label for="dis-pw">{t('password')}</label>
            <input id="dis-pw" type="password" autocomplete="current-password" required bind:value={disablePassword} />
          </div>
          <button class="btn danger" type="submit">{t('accountTotpDisable')}</button>
        </form>
      {/if}
    {/if}
  {/if}
</div>

<div class="card">
  <div class="page-head">
    <h2>{t('sessionsTitle')}</h2>
    <button class="btn" type="button" onclick={revokeOthers} disabled={!sessions || sessions.length < 2}>{t('sessionRevokeOthers')}</button>
  </div>
  <p class="muted small">{t('sessionsLead')}</p>
  {#if sessions}
    <div class="table-wrap">
      <table>
        <tbody>
          {#each sessions as session (session.handle)}
            <tr>
              <td>
                {#if session.isCurrent}<span class="badge ok">{t('sessionCurrent')}</span>{/if}
                <span class="badge">{viaLabel(session.via)}</span>
                <div class="small mono">{session.ipAddress ?? ''}</div>
                <div class="small muted">{session.userAgent ?? ''}</div>
              </td>
              <td class="small">
                <div>{t('sessionSince', { time: dt(session.createdAt) })}</div>
                <div class="muted">{t('sessionExpires', { time: dt(session.expiresAt) })}</div>
              </td>
              <td>
                {#if !session.isCurrent}<button class="btn small danger" type="button" onclick={() => revoke(session.handle)}>{t('sessionRevoke')}</button>{/if}
              </td>
            </tr>
          {/each}
        </tbody>
      </table>
    </div>
  {/if}
</div>

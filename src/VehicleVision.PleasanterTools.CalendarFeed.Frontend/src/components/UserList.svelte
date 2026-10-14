<script lang="ts">
  import { api } from '../lib/api';
  import { errorText } from '../lib/errors';
  import { dt, language, t } from '../lib/i18n/state.svelte';
  import { ROLES, type AdminUser, type Invitation, type Me, type Permission, type Role } from '../lib/types';
  import type { MessageKey } from '../lib/i18n/messages';
  import ConfirmDialog from './ConfirmDialog.svelte';
  import CopyField from './CopyField.svelte';
  import Modal from './Modal.svelte';
  import Notice from './Notice.svelte';

  let { me, permissions }: { me: Me; permissions: Permission[] } = $props();

  const canWrite = $derived(permissions.includes('users.write'));
  const canResetTotp = $derived(permissions.includes('users.resetTwoFactor'));

  let users = $state<AdminUser[] | null>(null);
  let staleOnly = $state(false);
  let error = $state('');
  let flash = $state('');
  let busy = $state(false);

  let inviteOpen = $state(false);
  let inviteLoginId = $state('');
  let inviteRole = $state<Role>('Editor');
  let inviteError = $state('');
  let issued = $state<Invitation | null>(null);
  let action = $state<{ kind: 'disable' | 'resetTotp' | 'revokeSessions'; user: AdminUser } | null>(null);

  const roleLabel = (role: Role): string => t(`role${role}` as MessageKey);

  async function load(): Promise<void> {
    const result = await api.users(staleOnly);
    if (!result.ok) {
      error = errorText(result);
      return;
    }

    error = '';
    users = result.value;
  }

  $effect(() => {
    void staleOnly;
    void load();
  });

  async function invite(event: SubmitEvent): Promise<void> {
    event.preventDefault();
    busy = true;
    inviteError = '';
    const created = await api.inviteUser(inviteLoginId.trim(), inviteRole, language());
    busy = false;
    if (!created.ok) {
      inviteError = errorText(created);
      return;
    }

    inviteOpen = false;
    inviteLoginId = '';
    issued = created.value.invitation;
    await load();
  }

  async function reissue(user: AdminUser): Promise<void> {
    const result = await api.reissueInvitation(user.id);
    if (!result.ok) {
      error = errorText(result);
      return;
    }

    issued = result.value.invitation;
    await load();
  }

  async function setDisabled(user: AdminUser, disabled: boolean): Promise<void> {
    const result = await api.setUserDisabled(user.id, disabled);
    if (!result.ok) error = errorText(result);
    else flash = t('saved');
    await load();
  }

  async function changeRole(user: AdminUser, role: Role): Promise<void> {
    const result = await api.setUserRole(user.id, role);
    if (!result.ok) error = errorText(result);
    else flash = t('saved');
    await load();
  }

  async function runAction(): Promise<void> {
    if (!action) return;
    busy = true;
    const { kind, user } = action;
    const result =
      kind === 'disable' ? await api.setUserDisabled(user.id, true) : kind === 'resetTotp' ? await api.resetUserTotp(user.id) : await api.revokeUserSessions(user.id);
    busy = false;
    action = null;
    if (!result.ok) error = errorText(result);
    else flash = t('saved');
    await load();
  }

  const confirmMessage = (kind: 'disable' | 'resetTotp' | 'revokeSessions', name: string): string =>
    t(kind === 'disable' ? 'userDisableConfirm' : kind === 'resetTotp' ? 'userResetTotpConfirm' : 'userRevokeSessionsConfirm', { name });
</script>

<div class="page-head">
  <h1>{t('usersTitle')}</h1>
  <div class="toolbar">
    <label class="check"><input type="checkbox" bind:checked={staleOnly} />{t('userStaleOnly')}</label>
    {#if canWrite}<button class="btn primary" type="button" onclick={() => ((inviteOpen = true), (inviteError = ''))}>{t('userInvite')}</button>{/if}
  </div>
</div>

{#if flash}<Notice kind="ok">{flash}</Notice>{/if}
{#if error}<Notice>{error}</Notice>{/if}

{#if users === null}
  <p class="muted">{t('loading')}</p>
{:else}
  <div class="card table-wrap">
    <table>
      <thead>
        <tr>
          <th>{t('userLoginId')}</th>
          <th>{t('userRole')}</th>
          <th>{t('userStatus')}</th>
          <th>{t('userLastLogin')}</th>
          <th>{t('userTotp')}</th>
          <th></th>
        </tr>
      </thead>
      <tbody>
        {#each users as user (user.id)}
          <tr>
            <td>{user.loginId}{#if user.id === me.id} <span class="badge">{t('userYou')}</span>{/if}</td>
            <td data-label={t('userRole')}>
              {#if canWrite && user.id !== me.id}
                <select aria-label={t('userChangeRole')} value={user.role} onchange={(e) => changeRole(user, e.currentTarget.value as Role)}>
                  {#each ROLES as role (role)}<option value={role}>{roleLabel(role)}</option>{/each}
                </select>
              {:else}
                {roleLabel(user.role)}
              {/if}
            </td>
            <td data-label={t('userStatus')}>
              {#if user.isDisabled}<span class="badge bad">{t('statusDisabled')}</span>
              {:else if user.invitationPending}<span class="badge warn">{t('statusInvited')}</span>
              {:else}<span class="badge ok">{t('statusActive')}</span>{/if}
              {#if user.isLocked}<span class="badge bad">{t('statusLocked')}</span>{/if}
              {#if user.stale}<span class="badge warn">{t('statusStale')}</span>{/if}
            </td>
            <td class="small" data-label={t('userLastLogin')}>{user.lastLoginAt ? dt(user.lastLoginAt) : t('never')}</td>
            <td data-label={t('userTotp')}>{user.totpEnabled ? t('yes') : t('no')}</td>
            <td>
              <div class="actions">
                {#if canWrite && !user.isDisabled}
                  <button class="btn small" type="button" onclick={() => reissue(user)}>{t('userReissue')}</button>
                {/if}
                {#if canWrite && user.id !== me.id}
                  {#if user.isDisabled}
                    <button class="btn small" type="button" onclick={() => setDisabled(user, false)}>{t('userEnable')}</button>
                  {:else}
                    <button class="btn small danger" type="button" onclick={() => (action = { kind: 'disable', user })}>{t('userDisable')}</button>
                  {/if}
                  <button class="btn small" type="button" onclick={() => (action = { kind: 'revokeSessions', user })}>{t('userRevokeSessions')}</button>
                {/if}
                {#if canResetTotp && user.id !== me.id && user.totpEnabled}
                  <button class="btn small" type="button" onclick={() => (action = { kind: 'resetTotp', user })}>{t('userResetTotp')}</button>
                {/if}
              </div>
            </td>
          </tr>
        {/each}
      </tbody>
    </table>
  </div>
{/if}

{#if inviteOpen}
  <Modal title={t('userInvite')} onclose={() => (inviteOpen = false)}>
    <p class="muted">{t('userInviteLead')}</p>
    {#if inviteError}<Notice>{inviteError}</Notice>{/if}
    <form onsubmit={invite}>
      <div class="field">
        <label for="inv-login">{t('userLoginId')}</label>
        <input id="inv-login" type="text" required maxlength="100" autocomplete="off" bind:value={inviteLoginId} />
      </div>
      <div class="field">
        <label for="inv-role">{t('userRole')}</label>
        <select id="inv-role" bind:value={inviteRole}>
          {#each ROLES as role (role)}<option value={role}>{roleLabel(role)}</option>{/each}
        </select>
      </div>
      <div class="dialog-actions">
        <button class="btn" type="button" onclick={() => (inviteOpen = false)}>{t('cancel')}</button>
        <button class="btn primary" type="submit" disabled={busy}>{t('userInvite')}</button>
      </div>
    </form>
  </Modal>
{/if}

{#if issued}
  <Modal title={t('invitationIssued')} onclose={() => (issued = null)}>
    <Notice kind={issued.mailSent ? 'ok' : 'warn'}>{issued.mailSent ? t('invitationMailSent') : t('invitationMailNotSent')}</Notice>
    <div class="field">
      <label for="issued-url">{t('invitationUrl')}</label>
      <CopyField value={issued.url} label={t('invitationUrl')} />
      <div class="hint">{t('invitationUrlOnce', { expires: dt(issued.expiresAt) })}</div>
    </div>
    <div class="dialog-actions"><button class="btn primary" type="button" onclick={() => (issued = null)}>{t('close')}</button></div>
  </Modal>
{/if}

{#if action}
  <ConfirmDialog
    title={action.kind === 'disable' ? t('userDisable') : action.kind === 'resetTotp' ? t('userResetTotp') : t('userRevokeSessions')}
    message={confirmMessage(action.kind, action.user.loginId)}
    danger
    {busy}
    onconfirm={runAction}
    oncancel={() => (action = null)}
  />
{/if}

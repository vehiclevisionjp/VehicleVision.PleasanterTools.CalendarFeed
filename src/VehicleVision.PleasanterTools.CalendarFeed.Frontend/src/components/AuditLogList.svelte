<script lang="ts">
  import { api } from '../lib/api';
  import { errorText } from '../lib/errors';
  import { dt, t } from '../lib/i18n/state.svelte';
  import type { AuditEntry } from '../lib/types';
  import Notice from './Notice.svelte';

  /** 操作の名前（サーバが返す機械的な識別子）。絞り込みの選択肢。 */
  const ACTIONS = [
    'login.succeeded',
    'login.failed',
    'login.locked',
    'login.totp_failed',
    'login.recovery_used',
    'logout',
    'admin.setup',
    'password.changed',
    'totp.enrolled',
    'totp.disabled',
    'feed.created',
    'feed.updated',
    'feed.token_rotated',
    'feed.apikey_set',
    'feed.revoked',
    'user.invited',
    'user.invite_reissued',
    'user.invite_accepted',
    'user.disabled',
    'user.enabled',
    'user.role_changed',
    'user.totp_reset',
    'user.sessions_revoked',
    'settings.updated',
    'settings.mail_test',
  ];

  const PAGE = 50;

  let entries = $state<AuditEntry[]>([]);
  let loaded = $state(false);
  let hasMore = $state(false);
  let failedOnly = $state(false);
  let action = $state('');
  let error = $state('');

  async function load(more: boolean): Promise<void> {
    const before = more ? entries.at(-1)?.occurredAt : undefined;
    const result = await api.audit({ before, failed: failedOnly, action: action || undefined, limit: PAGE });
    if (!result.ok) {
      error = errorText(result);
      return;
    }

    error = '';
    entries = more ? [...entries, ...result.value] : result.value;
    hasMore = result.value.length === PAGE;
    loaded = true;
  }

  // 絞り込みを変えたら、先頭から読み直す
  $effect(() => {
    void failedOnly;
    void action;
    void load(false);
  });
</script>

<div class="page-head">
  <h1>{t('auditTitle')}</h1>
  <div class="toolbar">
    <select aria-label={t('auditAction')} bind:value={action}>
      <option value="">{t('auditActionAll')}</option>
      {#each ACTIONS as a (a)}<option value={a}>{a}</option>{/each}
    </select>
    <label class="check"><input type="checkbox" bind:checked={failedOnly} />{t('auditFailedOnly')}</label>
    <button class="btn" type="button" onclick={() => load(false)}>{t('refresh')}</button>
  </div>
</div>
<p class="muted">{t('auditLead')}</p>

{#if error}<Notice>{error}</Notice>{/if}

{#if !loaded}
  <p class="muted">{t('loading')}</p>
{:else if entries.length === 0}
  <div class="card"><p class="muted">{t('auditEmpty')}</p></div>
{:else}
  <div class="card table-wrap">
    <table>
      <thead>
        <tr>
          <th>{t('auditTime')}</th>
          <th>{t('auditAction')}</th>
          <th>{t('auditResult')}</th>
          <th>{t('auditWho')}</th>
          <th>{t('auditTarget')}</th>
          <th>{t('auditDetail')}</th>
          <th>{t('auditIp')}</th>
        </tr>
      </thead>
      <tbody>
        {#each entries as entry, i (`${entry.occurredAt}-${i}`)}
          <tr>
            <td class="small" data-label={t('auditTime')}>{dt(entry.occurredAt)}</td>
            <td class="mono" data-label={t('auditAction')}>{entry.action}</td>
            <td data-label={t('auditResult')}><span class="badge {entry.succeeded ? 'ok' : 'bad'}">{entry.succeeded ? t('resultSuccess') : t('resultFailure')}</span></td>
            <td data-label={t('auditWho')}>{entry.loginId ?? ''}</td>
            <td class="small" data-label={t('auditTarget')}>{#if entry.targetType}{entry.targetType} <span class="mono">{entry.targetId?.slice(0, 8) ?? ''}</span>{/if}</td>
            <td class="small mono" data-label={t('auditDetail')}>{entry.detail ?? ''}</td>
            <td class="small mono" data-label={t('auditIp')}>{entry.ipAddress ?? ''}</td>
          </tr>
        {/each}
      </tbody>
    </table>
    {#if hasMore}
      <p><button class="btn" type="button" onclick={() => load(true)}>{t('loadMore')}</button></p>
    {/if}
  </div>
{/if}

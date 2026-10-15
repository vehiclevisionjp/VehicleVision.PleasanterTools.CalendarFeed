<script lang="ts">
  import { api } from '../lib/api';
  import { errorText } from '../lib/errors';
  import { dt, t } from '../lib/i18n/state.svelte';
  import type { Feed, FetchStatus, Permission } from '../lib/types';
  import ConfirmDialog from './ConfirmDialog.svelte';
  import CopyField from './CopyField.svelte';
  import FeedEditor from './FeedEditor.svelte';
  import Modal from './Modal.svelte';
  import Notice from './Notice.svelte';

  let { permissions }: { permissions: Permission[] } = $props();

  const canWrite = $derived(permissions.includes('feeds.write'));
  const canDelete = $derived(permissions.includes('feeds.delete'));

  let feeds = $state<Feed[] | null>(null);
  let error = $state('');
  let editor = $state<{ feed: Feed | null } | null>(null);
  /** 購読 URL は作成・再発行のときだけ表示できる（保管しているのはハッシュのみ）。 */
  let shown = $state<{ title: string; url: string } | null>(null);
  let confirm = $state<{ kind: 'rotate' | 'revoke'; feed: Feed } | null>(null);
  let keyDialog = $state<Feed | null>(null);
  let keyValue = $state('');
  let keyTz = $state('');
  let keyError = $state('');
  let busy = $state(false);
  let flash = $state('');

  /** 期間の片側の表示。null は無制限。 */
  const days = (value: number | null): string => (value === null ? t('feedRangeUnlimited') : t('feedRangeDays', { days: value }));

  async function load(): Promise<void> {
    const result = await api.feeds();
    if (!result.ok) {
      error = errorText(result);
      return;
    }

    error = '';
    feeds = result.value;
  }

  $effect(() => {
    void load();
  });

  function saved(feed: Feed, url: string | null): void {
    editor = null;
    flash = t('saved');
    if (url) shown = { title: t('feedCreatedTitle'), url };
    void load();
    void feed;
  }

  async function run(): Promise<void> {
    if (!confirm) return;
    busy = true;
    const { kind, feed } = confirm;
    if (kind === 'rotate') {
      const rotated = await api.rotateFeed(feed.id);
      if (!rotated.ok) error = errorText(rotated);
      else shown = { title: t('feedRotatedTitle'), url: rotated.value.url };
    } else {
      const revoked = await api.revokeFeed(feed.id);
      if (!revoked.ok) error = errorText(revoked);
    }

    busy = false;
    confirm = null;
    await load();
  }

  function openKey(feed: Feed): void {
    keyDialog = feed;
    keyValue = '';
    keyTz = feed.apiKeyUserTimeZoneId ?? '';
    keyError = '';
  }

  async function saveKey(event: SubmitEvent): Promise<void> {
    event.preventDefault();
    if (!keyDialog) return;
    busy = true;
    const updated = await api.setFeedApiKey(keyDialog.id, keyValue, keyTz.trim() || null);
    busy = false;
    if (!updated.ok) {
      keyError = updated.status === 400 ? updated.message : errorText(updated);
      return;
    }

    keyDialog = null;
    flash = t('feedApiKeyUpdated');
    await load();
  }

  const statusClass = (s: FetchStatus): string => (s === 'Success' ? 'ok' : s === 'Transient' || s === 'RateLimited' ? 'warn' : 'bad');
  const statusText = (f: Feed): string => {
    const r = f.lastFetch;
    if (!r) return '';
    switch (r.status) {
      case 'Success':
        return t('fetchSuccess', { count: r.eventCount ?? 0 });
      case 'Transient':
        return t('fetchTransient');
      case 'RateLimited':
        return t('fetchRateLimited');
      case 'Unauthorized':
        return t('fetchUnauthorized');
      case 'KeyError':
        return t('fetchKeyError');
      default:
        return t('fetchPermanent');
    }
  };
</script>

<div class="page-head">
  <h1>{t('feedsTitle')}</h1>
  <div class="toolbar">
    <button class="btn" type="button" onclick={load}><span class="material-icons" aria-hidden="true">refresh</span>{t('refresh')}</button>
    {#if canWrite}<button class="btn primary" type="button" onclick={() => (editor = { feed: null })}>{t('feedNew')}</button>{/if}
  </div>
</div>

{#if flash}<Notice kind="ok">{flash}</Notice>{/if}
{#if error}<Notice>{error}</Notice>{/if}

{#if feeds === null}
  <p class="muted">{t('loading')}</p>
{:else if feeds.length === 0}
  <div class="card"><p class="muted">{t('feedsEmpty')}</p></div>
{:else}
  <div class="card table-wrap">
    <table>
      <thead>
        <tr>
          <th>{t('feedName')}</th>
          <th>{t('feedSiteId')}</th>
          <th>{t('feedApiKey')}</th>
          <th>{t('feedLastFetch')}</th>
          <th></th>
        </tr>
      </thead>
      <tbody>
        {#each feeds as feed (feed.id)}
          <tr>
            <td>
              <strong>{feed.name}</strong>
              <div class="small muted">{t('feedCreatedAt')}: {dt(feed.createdAt)}</div>
              <div class="small muted">{t('feedRangeSummary', { past: days(feed.range.pastDays), future: days(feed.range.futureDays) })}</div>
            </td>
            <td class="mono" data-label={t('feedSiteId')}>{feed.siteId}</td>
            <td data-label={t('feedApiKey')}>
              {#if feed.hasApiKey}
                <span class="badge ok">{feed.apiKeyHint ? t('feedApiKeyRegistered', { hint: feed.apiKeyHint }) : t('feedApiKeyRegisteredShort')}</span>
              {:else}
                <span class="badge">{t('feedApiKeyCommon')}</span>
              {/if}
            </td>
            <td data-label={t('feedLastFetch')}>
              {#if feed.lastFetch}
                <span class="badge {statusClass(feed.lastFetch.status)}">{statusText(feed)}</span>
                {#if feed.lastFetch.consecutiveFailures >= 2}<span class="badge bad">{t('feedFailing', { count: feed.lastFetch.consecutiveFailures })}</span>{/if}
                <div class="small muted">{dt(feed.lastFetch.at)}</div>
                {#if feed.lastFetch.message}<div class="small muted">{feed.lastFetch.message}</div>{/if}
              {:else}
                <span class="muted small">{t('feedNeverFetched')}</span>
              {/if}
            </td>
            <td>
              <div class="actions">
                {#if canWrite}
                  <button class="btn small" type="button" onclick={() => (editor = { feed })}>{t('edit')}</button>
                  <button class="btn small" type="button" onclick={() => openKey(feed)}>{t('feedSetApiKey')}</button>
                  <button class="btn small" type="button" onclick={() => (confirm = { kind: 'rotate', feed })}>{t('feedRotate')}</button>
                {/if}
                {#if canDelete}
                  <button class="btn small danger" type="button" onclick={() => (confirm = { kind: 'revoke', feed })}>{t('feedRevoke')}</button>
                {/if}
              </div>
            </td>
          </tr>
        {/each}
      </tbody>
    </table>
  </div>
{/if}

{#if editor}
  <FeedEditor feed={editor.feed} {canWrite} ondone={saved} oncancel={() => (editor = null)} />
{/if}

{#if shown}
  <Modal title={shown.title} onclose={() => (shown = null)}>
    <Notice kind="warn">{t('feedUrlOnce')}</Notice>
    <div class="field">
      <label for="shown-url">{t('feedUrl')}</label>
      <CopyField value={shown.url} label={t('feedUrl')} />
    </div>
    <div class="dialog-actions"><button class="btn primary" type="button" onclick={() => (shown = null)}>{t('close')}</button></div>
  </Modal>
{/if}

{#if confirm}
  <ConfirmDialog
    title={confirm.kind === 'rotate' ? t('feedRotate') : t('feedRevoke')}
    message={confirm.kind === 'rotate' ? t('feedRotateConfirm') : t('feedRevokeConfirm', { name: confirm.feed.name })}
    danger={confirm.kind === 'revoke'}
    {busy}
    onconfirm={run}
    oncancel={() => (confirm = null)}
  />
{/if}

{#if keyDialog}
  <Modal title={t('feedSetApiKey')} onclose={() => (keyDialog = null)}>
    {#if keyError}<Notice>{keyError}</Notice>{/if}
    <form onsubmit={saveKey}>
      <div class="field">
        <label for="k-val">{t('feedApiKey')}</label>
        <input id="k-val" type="password" autocomplete="off" required bind:value={keyValue} />
        <div class="hint">{t('feedApiKeyHint')}</div>
      </div>
      <div class="field">
        <label for="k-tz">{t('feedTimeZone')}<span class="mark">{t('optionalMark')}</span></label>
        <input id="k-tz" type="text" placeholder="Asia/Tokyo" bind:value={keyTz} />
        <div class="hint">{t('feedTimeZoneHint')}</div>
      </div>
      <div class="dialog-actions">
        <button class="btn" type="button" onclick={() => (keyDialog = null)}>{t('cancel')}</button>
        <button class="btn primary" type="submit" disabled={busy}>{t('save')}</button>
      </div>
    </form>
  </Modal>
{/if}

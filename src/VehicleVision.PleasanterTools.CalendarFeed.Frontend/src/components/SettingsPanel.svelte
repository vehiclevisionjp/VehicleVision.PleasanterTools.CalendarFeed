<script lang="ts">
  import { api } from '../lib/api';
  import { errorText } from '../lib/errors';
  import { t } from '../lib/i18n/state.svelte';
  import type { MessageKey } from '../lib/i18n/messages';
  import type { MailStatus, PleasanterSsoTest, Setting } from '../lib/types';
  import CopyField from './CopyField.svelte';
  import Notice from './Notice.svelte';

  const GROUPS: { id: Setting['group']; title: MessageKey }[] = [
    { id: 'service', title: 'groupService' },
    { id: 'pleasanter', title: 'groupPleasanter' },
    { id: 'feed', title: 'groupFeed' },
    { id: 'security', title: 'groupSecurity' },
    { id: 'mail', title: 'groupMail' },
    { id: 'saml', title: 'groupSaml' },
    { id: 'pleasanterSso', title: 'groupPleasanterSso' },
  ];


  let settings = $state<Setting[] | null>(null);

  // IdP へ登録する値（SP のメタデータ・ACS）。公開 URL の設定があれば、その URL で見せる
  const origin = $derived(
    (settings?.find((s) => s.key === 'CALENDARFEED_PUBLIC_BASEURL')?.value || location.origin).replace(/\/$/, ''),
  );
  /** 入力中の値（キー → 文字列）。秘密は入力したときだけ入る。 */
  let values = $state<Record<string, string>>({});
  /** 既定へ戻す（null で送る）キー・秘密を消すキー。 */
  let resets = $state<Record<string, boolean>>({});
  let mail = $state<MailStatus | null>(null);
  let error = $state('');
  let flash = $state('');
  let busy = $state(false);

  const label = (key: string): string => t(`settingLabel${key}` as MessageKey);

  function adopt(list: Setting[]): void {
    settings = list;
    values = Object.fromEntries(list.map((s) => [s.key, s.isSecret ? '' : (s.value ?? '')]));
    resets = {};
  }

  async function load(): Promise<void> {
    const result = await api.settings();
    if (!result.ok) {
      error = errorText(result);
      return;
    }

    error = '';
    adopt(result.value.settings);
    const status = await api.mailStatus();
    mail = status.ok ? status.value : null;
  }

  $effect(() => {
    void load();
  });

  /** 変えたものだけを送る（固定された設定・変えていない設定は送らない）。 */
  function changes(): Record<string, string | null> {
    const out: Record<string, string | null> = {};
    for (const s of settings ?? []) {
      if (s.isFixed) continue;
      // 数値の入力欄は number で入ってくるので、文字列にそろえる（API は文字列だけを受ける）
      const entered = String(values[s.key] ?? '');
      if (resets[s.key]) out[s.key] = null;
      else if (s.isSecret) {
        if (entered) out[s.key] = entered;
      } else if (entered !== (s.value ?? '')) out[s.key] = entered;
    }

    return out;
  }

  const dirty = $derived(Object.keys(changes()).length > 0);

  async function save(event: SubmitEvent): Promise<void> {
    event.preventDefault();
    const body = changes();
    if (Object.keys(body).length === 0) {
      flash = t('settingsNoChange');
      return;
    }

    busy = true;
    error = '';
    flash = '';
    const result = await api.saveSettings(body);
    busy = false;
    if (!result.ok) {
      error = errorText(result);
      return;
    }

    adopt(result.value.settings);
    flash = t('settingsSaved');
    const status = await api.mailStatus();
    mail = status.ok ? status.value : null;
  }

  let ssoTest = $state<PleasanterSsoTest | null>(null);

  async function testSso(): Promise<void> {
    flash = '';
    error = '';
    ssoTest = null;
    const result = await api.pleasanterSsoTest();
    if (!result.ok) {
      error = result.status === 400 && result.message ? result.message : errorText(result);
      return;
    }

    ssoTest = result.value;
  }

  const ssoTestText = $derived.by(() => {
    if (ssoTest === null) return '';
    switch (ssoTest.status) {
      case 'Authenticated':
        return t('pleasanterSsoTestAuthenticated', {
          name: ssoTest.name ?? '',
          loginId: ssoTest.loginId ?? '',
          registered: ssoTest.registered ? t('pleasanterSsoRegistered') : t('pleasanterSsoNotRegistered'),
        });
      case 'Unauthenticated':
        return t('pleasanterSsoTestUnauthenticated');
      case 'NotAllowed':
        return t('pleasanterSsoTestNotAllowed');
      default:
        return t('pleasanterSsoTestError', { reason: ssoTest.reason });
    }
  });

  async function testMail(): Promise<void> {
    flash = '';
    error = '';
    const result = await api.testMail();
    if (!result.ok) {
      error = errorText(result);
      return;
    }

    if (result.value.queued) flash = t('mailTestQueued');
    else error = result.value.reason === 'mail-disabled' ? t('mailTestDisabled') : result.value.reason === 'login-id-not-email' ? t('mailTestNotEmail') : t('mailTestFailed');
  }
</script>

<div class="page-head">
  <h1>{t('settingsTitle')}</h1>
  <button class="btn" type="button" onclick={load}>{t('refresh')}</button>
</div>
<p class="muted">{t('settingsLead')}</p>

{#if flash}<Notice kind="ok">{flash}</Notice>{/if}
{#if error}<Notice>{error}</Notice>{/if}

{#if settings === null}
  <p class="muted">{t('loading')}</p>
{:else}
  <form onsubmit={save}>
    {#each GROUPS as group (group.id)}
      <fieldset>
        <legend>{t(group.title)}</legend>

        {#if group.id === 'saml'}
          <p class="muted">{t('samlLead')}</p>
          <div class="grid">
            <div class="field">
              <label for="saml-meta">{t('samlMetadataUrl')}</label>
              <CopyField value={`${origin}/admin/api/saml/metadata`} label={t('samlMetadataUrl')} />
            </div>
            <div class="field">
              <label for="saml-acs">{t('samlAcsUrl')}</label>
              <CopyField value={`${origin}/admin/api/saml/acs`} label={t('samlAcsUrl')} />
            </div>
          </div>
          <p class="muted small">{t('samlHttpsNote')}</p>
          <p class="muted small">{t('samlDisabledHint')}</p>
        {/if}

        {#if group.id === 'pleasanterSso'}
          <p class="muted">{t('pleasanterSsoLead')}</p>
          <p class="muted small">{t('pleasanterSsoSameHostNote')}</p>
          <p class="muted small">{t('pleasanterSsoTestHint')}</p>
          <p>
            <button class="btn" type="button" onclick={testSso}>{t('pleasanterSsoTest')}</button>
          </p>
          {#if ssoTest}<Notice kind={ssoTest.status === 'Authenticated' ? 'ok' : 'error'}>{ssoTestText}</Notice>{/if}
        {/if}

        {#if group.id === 'mail'}
          <div class="field">
            {#if mail}
              <span class="badge {mail.ready ? 'ok' : 'warn'}">{mail.ready ? t('mailReady') : t('mailNotReady')}</span>
              {#if mail.pendingCount > 0}<span class="badge">{t('mailPending', { count: mail.pendingCount })}</span>{/if}
              {#if mail.deadLetterCount > 0}<span class="badge bad">{t('mailDeadLetter', { count: mail.deadLetterCount })}</span>{/if}
            {/if}
            <button class="btn small" type="button" onclick={testMail}>{t('mailTest')}</button>
          </div>
        {/if}

        <div class="grid">
          {#each settings.filter((s) => s.group === group.id) as s (s.key)}
            <div class="field">
              {#if s.type === 'Boolean'}
                <label class="check" for={s.key}>
                  <input
                    id={s.key}
                    type="checkbox"
                    disabled={s.isFixed}
                    checked={values[s.key] === 'true'}
                    onchange={(e) => (values[s.key] = e.currentTarget.checked ? 'true' : 'false')}
                  />
                  {label(s.key)}
                </label>
              {:else}
                <label for={s.key}>{label(s.key)}</label>
                {#if s.allowedValues}
                  <select id={s.key} disabled={s.isFixed} bind:value={values[s.key]}>
                    {#each s.allowedValues as option (option)}<option value={option}>{option}</option>{/each}
                  </select>
                {:else if s.multiline}
                  <textarea id={s.key} spellcheck="false" disabled={s.isFixed} bind:value={values[s.key]}></textarea>
                {:else if s.isSecret}
                  <input
                    id={s.key}
                    type="password"
                    autocomplete="new-password"
                    disabled={s.isFixed || resets[s.key]}
                    placeholder={s.hasValue ? '••••••••' : ''}
                    bind:value={values[s.key]}
                  />
                {:else if s.type === 'Integer'}
                  <input id={s.key} type="number" min={s.minimum ?? undefined} max={s.maximum ?? undefined} disabled={s.isFixed} bind:value={values[s.key]} />
                {:else}
                  <input id={s.key} type="text" maxlength={s.maximumLength ?? undefined} disabled={s.isFixed} bind:value={values[s.key]} />
                {/if}
              {/if}

              <div class="hint">
                {#if s.isFixed}<span class="badge warn" title={t('settingFixedHint')}>{t('settingFixed')}</span>{/if}
                {#if s.isSecret}
                  {#if s.unreadable}<span class="badge bad">{t('settingSecretUnreadable')}</span>
                  {:else if s.hasValue}<span class="badge ok">{t('settingSecretSet')}</span> {t('settingSecretKeep')}
                  {:else}<span class="badge">{t('settingSecretUnset')}</span>{/if}
                  {#if s.hasValue && !s.isFixed}
                    <label class="check"><input type="checkbox" bind:checked={resets[s.key]} />{t('settingSecretClear')}</label>
                  {/if}
                {:else if s.defaultValue !== null && s.defaultValue !== '' && !s.isFixed}
                  {t('settingDefault', { value: s.type === 'Boolean' ? (s.defaultValue === 'true' ? t('yes') : t('no')) : s.defaultValue })}
                  {#if String(values[s.key] ?? '') !== s.defaultValue}
                    <button class="btn small" type="button" onclick={() => (values[s.key] = s.defaultValue ?? '')}>{t('settingReset')}</button>
                  {/if}
                {/if}
              </div>
            </div>
          {/each}
        </div>
      </fieldset>
    {/each}
    <button class="btn primary" type="submit" disabled={busy || !dirty}>{t('settingsSave')}</button>
  </form>
{/if}

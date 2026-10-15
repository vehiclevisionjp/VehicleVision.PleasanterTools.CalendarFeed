<script lang="ts">
  import { api } from './lib/api';
  import { resolveLanguage, t } from './lib/i18n/state.svelte';
  import type { MessageKey } from './lib/i18n/messages';
  import type { Me, Permission, PleasanterSsoStatus, Step } from './lib/types';
  import AccountPanel from './components/AccountPanel.svelte';
  import AuditLogList from './components/AuditLogList.svelte';
  import EnrollPanel from './components/EnrollPanel.svelte';
  import FeedList from './components/FeedList.svelte';
  import InvitationAcceptPanel from './components/InvitationAcceptPanel.svelte';
  import LanguageSelect from './components/LanguageSelect.svelte';
  import LoginPanel from './components/LoginPanel.svelte';
  import SettingsPanel from './components/SettingsPanel.svelte';
  import SetupPanel from './components/SetupPanel.svelte';
  import TotpPanel from './components/TotpPanel.svelte';
  import UserList from './components/UserList.svelte';

  type Phase = 'loading' | 'setup' | 'login' | 'totp' | 'enroll' | 'invite' | 'app';
  type Tab = 'feeds' | 'users' | 'audit' | 'settings' | 'account';

  const ROOT = '/admin';

  let phase = $state<Phase>('loading');
  let me = $state<Me | null>(null);
  let saml = $state<{ enabled: boolean; buttonLabel: string | null }>({ enabled: false, buttonLabel: null });
  let pleasanterSso = $state<PleasanterSsoStatus>({ enabled: false, loginUrl: null, logoutUrl: null, buttonLabel: null });
  let tab = $state<Tab>('feeds');
  const inviteToken = new URLSearchParams(location.search).get('token') ?? '';

  /** 画面の出し分けは役割ではなく権限で行う。 */
  const TABS: { id: Tab; label: MessageKey; permission: Permission | null }[] = [
    { id: 'feeds', label: 'navFeeds', permission: 'feeds.read' },
    { id: 'users', label: 'navUsers', permission: 'users.read' },
    { id: 'audit', label: 'navAudit', permission: 'audit.read' },
    { id: 'settings', label: 'navSettings', permission: 'settings.manage' },
    { id: 'account', label: 'navAccount', permission: null },
  ];

  const visibleTabs = $derived(TABS.filter((x) => x.permission === null || me?.permissions.includes(x.permission)));

  function tabFromPath(): Tab {
    const segment = location.pathname.replace(/^\/admin\/?/, '').split('/')[0] ?? '';
    return (TABS.find((x) => x.id === segment)?.id ?? 'feeds') as Tab;
  }

  function go(next: Tab): void {
    tab = next;
    history.pushState(null, '', next === 'feeds' ? `${ROOT}/` : `${ROOT}/${next}`);
  }

  async function loadMe(): Promise<void> {
    const result = await api.me();
    if (!result.ok) {
      phase = 'login';
      return;
    }

    me = result.value;
    resolveLanguage(result.value.language);
    // 権限の無い画面を開いていたら、見られる最初の画面へ
    const wanted = tabFromPath();
    tab = visibleTabs.some((x) => x.id === wanted) ? wanted : (visibleTabs[0]?.id ?? 'account');
    phase = 'app';
  }

  async function boot(): Promise<void> {
    resolveLanguage();
    if (location.pathname.replace(/\/$/, '') === `${ROOT}/invitations/accept`) {
      phase = 'invite';
      return;
    }

    const status = await api.status();
    if (status.ok) {
      saml = status.value.saml;
      pleasanterSso = status.value.pleasanterSso;
    }
    if (status.ok && status.value.setupRequired) {
      phase = 'setup';
      return;
    }

    await loadMe();
    if (phase === 'loading') phase = 'login';
  }

  function onStep(step: Step): void {
    if (step === 'totp') phase = 'totp';
    else if (step === 'enroll') phase = 'enroll';
    else {
      // 招待の URL（トークンを含む）から離れる
      if (location.pathname !== `${ROOT}/`) history.replaceState(null, '', `${ROOT}/`);
      void loadMe();
    }
  }

  async function signOut(): Promise<void> {
    // SAML で入ったセッションは、こちらのセッションを捨てたうえで、IdP の単一ログアウトへも進む（経路はサーバが決める）
    if (me?.viaSaml) {
      location.assign('/admin/api/saml/logout');
      return;
    }

    const viaPleasanter = me?.viaPleasanterSso ?? false;
    await api.logout();
    me = null;
    phase = 'login';

    // Pleasanter で入ったセッションは、Pleasanter のログアウト画面があれば、そこへ進む（Pleasanter に残ったログインで、すぐ入り直せてしまわないように）
    if (viaPleasanter && pleasanterSso.logoutUrl) location.assign(pleasanterSso.logoutUrl);
  }

  $effect(() => {
    void boot();
    const onPop = (): void => {
      if (phase === 'app') tab = tabFromPath();
    };
    addEventListener('popstate', onPop);
    return () => removeEventListener('popstate', onPop);
  });
</script>

{#if phase === 'loading'}
  <div class="auth-wrap"><p class="muted">{t('loading')}</p></div>
{:else if phase === 'setup'}
  <SetupPanel ondone={() => onStep('done')} />
{:else if phase === 'login'}
  <LoginPanel onstep={onStep} {saml} {pleasanterSso} />
{:else if phase === 'totp'}
  <TotpPanel ondone={() => onStep('done')} oncancel={() => (phase = 'login')} />
{:else if phase === 'enroll'}
  <EnrollPanel required ondone={() => onStep('done')} oncancel={() => (phase = 'login')} />
{:else if phase === 'invite'}
  <InvitationAcceptPanel token={inviteToken} onstep={onStep} />
{:else if me}
  <header class="shell-header">
    <span class="brand">{t('appName')}</span>
    <nav class="nav" aria-label={t('appName')}>
      {#each visibleTabs as item (item.id)}
        <button type="button" aria-current={tab === item.id ? 'page' : undefined} onclick={() => go(item.id)}>{t(item.label)}</button>
      {/each}
    </nav>
    <div class="header-tools">
      <LanguageSelect />
      <span class="small muted">{me.loginId}</span>
      <button class="btn small" type="button" onclick={signOut}><span class="material-icons" aria-hidden="true">logout</span>{t('signOut')}</button>
    </div>
  </header>
  <main>
    {#if tab === 'feeds'}
      <FeedList permissions={me.permissions} />
    {:else if tab === 'users'}
      <UserList {me} permissions={me.permissions} />
    {:else if tab === 'audit'}
      <AuditLogList />
    {:else if tab === 'settings'}
      <SettingsPanel />
    {:else}
      <AccountPanel {me} onchanged={loadMe} />
    {/if}
  </main>
{/if}

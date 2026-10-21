import { language } from './i18n/state.svelte';
import type {
  UserSessionInfo,
  PleasanterSsoTest,
  AdminUser,
  AuditEntry,
  Feed,
  FeedInput,
  Invitation,
  MailStatus,
  Me,
  Role,
  SessionInfo,
  Setting,
  Status,
  Step,
} from './types';

/**
 * 呼び出しの結果。**例外を投げずに返す**（画面は「通った / 通らなかった」と理由を出し分ける）。
 * サーバの `error`（日本語）は、400 の検証メッセージだけ画面へ出す。それ以外は状態から画面の言語で書く。
 */
export type Result<T> = { ok: true; value: T } | { ok: false; status: number; message: string; code?: string };

async function call<T>(method: string, path: string, body?: unknown): Promise<Result<T>> {
  let response: Response;
  try {
    response = await fetch(`/admin/api${path}`, {
      method,
      // 認証は cookie（HttpOnly・SameSite=Strict）。必ず送る
      credentials: 'same-origin',
      // サーバが返す文言の言語は、画面で選んだ言語で決める（ブラウザの言語設定ではない）
      headers: body === undefined ? { 'Accept-Language': language() } : { 'Content-Type': 'application/json', 'Accept-Language': language() },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
  } catch {
    return { ok: false, status: 0, message: '' };
  }

  if (response.status === 204) {
    return { ok: true, value: undefined as T };
  }

  let json: unknown = null;
  try {
    json = await response.json();
  } catch {
    // 本文が無い・JSON でない
  }

  if (response.ok) {
    return { ok: true, value: json as T };
  }

  const errorBody = json as { error?: unknown; code?: unknown } | null;
  return {
    ok: false,
    status: response.status,
    message: typeof errorBody?.error === 'string' ? errorBody.error : '',
    // 決まった印（Pleasanter のログインの失敗など）
    code: typeof errorBody?.code === 'string' ? errorBody.code : undefined,
  };
}

const get = <T>(path: string) => call<T>('GET', path);
const post = <T>(path: string, body?: unknown) => call<T>('POST', path, body ?? {});
const put = <T>(path: string, body: unknown) => call<T>('PUT', path, body);
const del = <T>(path: string) => call<T>('DELETE', path);

const enc = encodeURIComponent;

export const api = {
  // ---- 認証 ----
  status: () => get<Status>('/status'),
  setup: (loginId: string, password: string, language: string) => post<{ id: string }>('/setup', { loginId, password, language }),
  login: (loginId: string, password: string) => post<{ step: Step }>('/login', { loginId, password }),
  loginTotp: (code: string) => post<{ step: Step }>('/login/totp', { code }),
  loginRecovery: (code: string) => post<{ step: Step; recoveryCodesLeft: number }>('/login/recovery', { code }),
  logout: () => post<void>('/logout'),
  me: () => get<Me>('/me/'),
  totpBegin: () => post<{ secret: string; uri: string }>('/me/totp/begin'),
  totpComplete: (code: string) => post<{ recoveryCodes: string[] }>('/me/totp/complete', { code }),
  totpDisable: (password: string) => post<void>('/me/totp/disable', { password }),
  changePassword: (currentPassword: string, newPassword: string) => post<void>('/me/password', { currentPassword, newPassword }),
  acceptInvitation: (token: string, password: string) => post<{ next: Step }>('/invitations/accept', { token, password }),

  // ---- 自分の端末 ----
  mySessions: () => get<SessionInfo[]>('/me/sessions/'),
  revokeMySession: (handle: string) => del<void>(`/me/sessions/${enc(handle)}`),
  revokeOtherSessions: () => post<void>('/me/sessions/revoke-others'),

  // ---- フィード ----
  feeds: () => get<Feed[]>('/feeds/'),
  createFeed: (input: FeedInput) => post<{ feed: Feed; url: string }>('/feeds', input),
  updateFeed: (id: string, input: Omit<FeedInput, 'apiKey'>) => put<Feed>(`/feeds/${id}`, input),
  rotateFeed: (id: string) => post<{ url: string }>(`/feeds/${id}/rotate`),
  setFeedApiKey: (id: string, apiKey: string, apiKeyUserTimeZoneId: string | null) => put<void>(`/feeds/${id}/apikey`, { apiKey, apiKeyUserTimeZoneId }),
  setFeedCalDav: (id: string, enabled: boolean) => put<void>(`/feeds/${id}/caldav`, { enabled }),
  revokeFeed: (id: string) => del<void>(`/feeds/${id}`),

  // ---- 管理者 ----
  users: (staleOnly = false) => get<AdminUser[]>(`/users/${staleOnly ? '?staleOnly=true' : ''}`),
  inviteUser: (loginId: string, role: Role, language: string) =>
    post<{ user: { id: string; loginId: string; role: Role }; invitation: Invitation }>('/users', { loginId, role, language }),
  reissueInvitation: (id: string) => post<{ invitation: Invitation }>(`/users/${id}/invitation`),
  setUserDisabled: (id: string, disabled: boolean) => post<void>(`/users/${id}/${disabled ? 'disable' : 'enable'}`),
  setUserRole: (id: string, role: Role) => put<void>(`/users/${id}/role`, { role }),
  resetUserTotp: (id: string) => post<void>(`/users/${id}/totp/reset`),
  revokeUserSessions: (id: string) => del<void>(`/users/${id}/sessions`),

  // ---- 監査ログ・メール・設定 ----
  audit: (query: { before?: string; failed?: boolean; action?: string; limit?: number }) => {
    const q = new URLSearchParams();
    if (query.before) {q.set('before', query.before);}
    if (query.failed) {q.set('failed', 'true');}
    if (query.action) {q.set('action', query.action);}
    q.set('limit', String(query.limit ?? 50));
    return get<AuditEntry[]>(`/audit-logs/?${q}`);
  },
  mailStatus: () => get<MailStatus>('/mail/status'),
  settings: () => get<{ settings: Setting[] }>('/settings/'),
  saveSettings: (values: Record<string, string | null>) => put<{ settings: Setting[] }>('/settings/', { values }),
  userSessions: (id: string) => get<UserSessionInfo[]>(`/users/${enc(id)}/sessions`),
  revokeUserSession: (id: string, handle: string) => del<void>(`/users/${enc(id)}/sessions/${enc(handle)}`),
  pleasanterSsoCheck: () => post<{ status: 'unauthenticated' } | { status: 'signedIn'; next: Step }>('/pleasanter-sso/check'),
  pleasanterSsoTest: () => post<PleasanterSsoTest>('/pleasanter-sso/test'),
  testMail: () => post<{ queued: boolean; reason: string | null }>('/settings/mail/test'),
};

/** サーバ（`/admin/api`）が返す形。名前は API の JSON（camelCase）と同じ。 */

export type Role = 'Administrator' | 'FeedAdministrator' | 'UserAdministrator' | 'Editor' | 'Auditor';

export const ROLES: readonly Role[] = ['Administrator', 'FeedAdministrator', 'UserAdministrator', 'Editor', 'Auditor'];

/** 画面の出し分けは、役割ではなく権限で行う（役割を増やすたびに画面を直さないため）。 */
export type Permission =
  | 'feeds.read'
  | 'feeds.write'
  | 'feeds.delete'
  | 'outbox.read'
  | 'audit.read'
  | 'users.read'
  | 'users.write'
  | 'users.resetTwoFactor'
  | 'settings.manage';

export type TwoFactorMode = 'Required' | 'Optional' | 'Disabled';

export interface Status {
  setupRequired: boolean;
  twoFactor: TwoFactorMode;
  /** SAML ログインが有効か（認証前に見える値だけ）。 */
  saml: { enabled: boolean; buttonLabel: string | null };
}

export interface Me {
  id: string;
  loginId: string;
  role: Role;
  language: string;
  totpEnabled: boolean;
  lastLoginAt: string | null;
  permissions: Permission[];
  twoFactor: TwoFactorMode;
  recoveryCodesLeft: number;
  /** SAML で入ったセッションか（ログアウトは IdP へも頼む経路を通す）。 */
  viaSaml: boolean;
}

/** ログイン・招待の受け取りの次の段階。 */
export type Step = 'totp' | 'enroll' | 'done';

export type AllDayMode = 'Auto' | 'Never' | 'Always';

export interface FeedMapping {
  summaryColumn: string;
  summaryTemplate: string | null;
  startColumn: string;
  startFormat: string | null;
  endColumn: string | null;
  endFormat: string | null;
  descriptionColumn: string | null;
  locationColumn: string | null;
  allDay: AllDayMode;
  defaultDurationMinutes: number;
}

export type FetchStatus = 'Success' | 'Transient' | 'Unauthorized' | 'Permanent' | 'KeyError';

export interface FeedFetchResult {
  at: string;
  status: FetchStatus;
  message: string | null;
  eventCount: number | null;
}

export interface Feed {
  id: string;
  name: string;
  siteId: number;
  mapping: FeedMapping;
  filters: Record<string, string>;
  createdAt: string;
  language: string;
  apiKeyUserTimeZoneId: string | null;
  hasApiKey: boolean;
  apiKeyHint: string | null;
  lastFetch: FeedFetchResult | null;
}

export interface FeedInput {
  name: string;
  siteId: number;
  mapping: FeedMapping;
  filters: Record<string, string>;
  language: string;
  apiKeyUserTimeZoneId: string | null;
  apiKey?: string | null;
}

export interface Invitation {
  adminUserId: string;
  /** 返るのはこの時だけ。保管しているのはハッシュのみ。 */
  invitationToken: string;
  url: string;
  expiresAt: string;
  /** メールを積めたか。false なら、url を手で渡す。 */
  mailSent: boolean;
}

export interface AdminUser {
  id: string;
  loginId: string;
  role: Role;
  isDisabled: boolean;
  isLocked: boolean;
  lastLoginAt: string | null;
  createdAt: string;
  totpEnabled: boolean;
  invitationPending: boolean;
  stale: boolean;
}

export interface AuditEntry {
  occurredAt: string;
  action: string;
  succeeded: boolean;
  adminUserId: string | null;
  loginId: string | null;
  targetType: string | null;
  targetId: string | null;
  detail: string | null;
  ipAddress: string | null;
}

export interface SessionInfo {
  handle: string;
  isCurrent: boolean;
  createdAt: string;
  expiresAt: string;
  ipAddress: string | null;
  userAgent: string | null;
}

export type SettingType = 'String' | 'Boolean' | 'Integer';

export interface Setting {
  key: string;
  group: 'service' | 'pleasanter' | 'feed' | 'security' | 'mail' | 'saml';
  type: SettingType;
  isSecret: boolean;
  isFixed: boolean;
  defaultValue: string | null;
  minimum: number | null;
  maximum: number | null;
  maximumLength: number | null;
  /** 複数行の値（証明書の PEM など）。 */
  multiline: boolean;
  allowedValues: string[] | null;
  /** 秘密は null（値は返らない）。 */
  value: string | null;
  hasValue: boolean;
  unreadable: boolean;
}

export interface MailStatus {
  ready: boolean;
  transport: string | null;
  pendingCount: number;
  oldestPendingAt: string | null;
  deadLetterCount: number;
}

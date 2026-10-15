using System.Collections.Frozen;

namespace VehicleVision.PleasanterTools.CalendarFeed.Localization;

public static partial class ServerMessages
{
    private static FrozenDictionary<string, string> German() => new Dictionary<string, string>
    {
        [ServerMessageKeys.InvalidCredentials] = "Die Anmelde-ID oder das Passwort ist falsch. Bei wiederholten Fehlversuchen warten Sie bitte eine Weile, bevor Sie es erneut versuchen.",
        [ServerMessageKeys.CrossSiteRejected] = "Anfragen von einer anderen Website werden nicht akzeptiert.",
        [ServerMessageKeys.LoginRequired] = "Sie müssen sich anmelden.",
        [ServerMessageKeys.UpstreamSessionEnded] = "Die Anmeldung wurde beendet, weil Pleasanter zur Überprüfung nicht erreicht werden konnte. Bitte versuchen Sie es später erneut.",
        [ServerMessageKeys.PermissionRequired] = "Für diesen Vorgang ist die Berechtigung ({0}) erforderlich.",
        [ServerMessageKeys.AdministratorAlreadyExists] = "Der erste Administrator wurde bereits erstellt.",
        [ServerMessageKeys.CurrentPasswordRejected] = "Das aktuelle Passwort ist falsch.",
        [ServerMessageKeys.PasswordRejected] = "Das Passwort ist falsch.",
        [ServerMessageKeys.TwoFactorDisabled] = "Die Zwei-Faktor-Authentifizierung ist deaktiviert.",
        [ServerMessageKeys.TwoFactorRequired] = "Die Zwei-Faktor-Authentifizierung ist erforderlich und kann daher nicht entfernt werden.",
        [ServerMessageKeys.EnrollmentBeginRequired] = "Starten Sie zuerst die Registrierung (/admin/api/me/totp/begin).",
        [ServerMessageKeys.TotpCodeMismatch] = "Der Code ist falsch. Geben Sie den 6-stelligen Code ein, der in Ihrer Authentifizierungs-App angezeigt wird.",
        [ServerMessageKeys.LoginIdInvalid] = "Die Anmelde-ID muss 1 bis 100 Zeichen lang sein (keine Steuerzeichen).",
        [ServerMessageKeys.PasswordTooShort] = "Das Passwort muss mindestens {0} Zeichen lang sein.",
        [ServerMessageKeys.PasswordTooLong] = "Das Passwort darf höchstens {0} Zeichen lang sein.",
        [ServerMessageKeys.PasswordSameAsLoginId] = "Das Passwort darf nicht mit der Anmelde-ID übereinstimmen.",
        [ServerMessageKeys.PasswordPolicyMismatch] = "Das Passwort erfüllt die Anforderungen nicht.",
        [ServerMessageKeys.AdminUserNotFound] = "Der Administrator wurde nicht gefunden.",
        [ServerMessageKeys.RoleNotSupported] = "Geben Sie die Rolle als einen der folgenden Werte an: {0}.",
        [ServerMessageKeys.DuplicateLoginId] = "Diese Anmelde-ID existiert bereits.",
        [ServerMessageKeys.InviteDisabledUser] = "Ein deaktivierter Administrator kann nicht eingeladen werden. Aktivieren Sie das Konto zuerst wieder.",
        [ServerMessageKeys.SelfDisableNotAllowed] = "Sie können sich nicht selbst deaktivieren.",
        [ServerMessageKeys.SelfRoleChangeNotAllowed] = "Sie können Ihre eigene Rolle nicht ändern.",
        [ServerMessageKeys.SelfTotpResetNotAllowed] = "Entfernen Sie Ihre eigene Zwei-Faktor-Authentifizierung auf Ihrer eigenen Seite (/admin/api/me/totp/disable).",
        [ServerMessageKeys.SelfSessionRevokeNotAllowed] = "Widerrufen Sie Ihre eigenen Geräte auf Ihrer eigenen Seite (/admin/api/me/sessions).",
        [ServerMessageKeys.LastAdministrator] = "Dadurch gäbe es keinen aktiven Administrator (Administrator) mehr. Richten Sie zuerst einen weiteren Administrator ein.",
        [ServerMessageKeys.InvitationInvalid] = "Diese Einladung kann nicht verwendet werden (sie ist möglicherweise abgelaufen, bereits angenommen oder widerrufen worden). Bitten Sie den Administrator, der Sie eingeladen hat, sie erneut zu senden.",
        [ServerMessageKeys.SessionNotFound] = "Dieses Gerät wurde nicht gefunden.",
        [ServerMessageKeys.AuditBeforeInvalid] = "Geben Sie before als Datum und Uhrzeit an (ISO 8601).",
        [ServerMessageKeys.ApiKeyNeedsSecretKey] = "Zum Speichern eines API-Schlüssels ist die Einstellung {0} erforderlich.",
        [ServerMessageKeys.ApiKeyRequired] = "Geben Sie ApiKey an (der für alle Feeds gemeinsame Standard-API-Schlüssel ist ebenfalls nicht festgelegt).",
        [ServerMessageKeys.SettingsValuesRequired] = "Geben Sie die zu ändernden Einstellungen in values an.",
        [ServerMessageKeys.SecretSettingNeedsSecretKey] = "Zum Speichern einer geheimen Einstellung ist die Einstellung {0} erforderlich.",
        [ServerMessageKeys.SamlNeedsSecretKey] = "Die SAML-Anmeldung erfordert die Einstellung für den geheimen Schlüssel ({0}).",
        [ServerMessageKeys.FeedNameInvalid] = "Name muss 1 bis 200 Zeichen lang sein.",
        [ServerMessageKeys.FeedSiteIdInvalid] = "SiteId muss mindestens 1 sein.",
        [ServerMessageKeys.FeedFiltersInvalid] = "Filters erlaubt bis zu 20 Einträge; Spaltennamen bestehen nur aus Buchstaben, Ziffern und _, und Werte sind höchstens 500 Zeichen lang.",
        [ServerMessageKeys.FeedApiKeyInvalid] = "ApiKey muss 1 bis 512 Zeichen lang sein (keine Steuerzeichen).",
        [ServerMessageKeys.FeedTimeZoneInvalid] = "ApiKeyUserTimeZoneId ist eine Zeitzonen-ID, die dieser Server nicht auflösen kann.",
        [ServerMessageKeys.FeedLanguageInvalid] = "Geben Sie Language als einen der folgenden Werte an: {0}.",
    }.ToFrozenDictionary();
}

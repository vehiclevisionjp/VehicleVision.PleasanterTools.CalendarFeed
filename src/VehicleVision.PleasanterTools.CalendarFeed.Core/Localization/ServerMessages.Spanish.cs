using System.Collections.Frozen;

namespace VehicleVision.PleasanterTools.CalendarFeed.Localization;

public static partial class ServerMessages
{
    private static FrozenDictionary<string, string> Spanish() => new Dictionary<string, string>
    {
        [ServerMessageKeys.InvalidCredentials] = "El ID de inicio de sesión o la contraseña son incorrectos. Si sigue ocurriendo, espere un momento y vuelva a intentarlo.",
        [ServerMessageKeys.CrossSiteRejected] = "No se aceptan solicitudes procedentes de otro sitio.",
        [ServerMessageKeys.LoginRequired] = "Debe iniciar sesión.",
        [ServerMessageKeys.UpstreamSessionEnded] = "Se cerró la sesión porque no se pudo contactar con Pleasanter para verificarla. Inténtelo de nuevo más tarde.",
        [ServerMessageKeys.PermissionRequired] = "Esta operación requiere el permiso ({0}).",
        [ServerMessageKeys.AdministratorAlreadyExists] = "El primer administrador ya ha sido creado.",
        [ServerMessageKeys.CurrentPasswordRejected] = "La contraseña actual es incorrecta.",
        [ServerMessageKeys.PasswordRejected] = "La contraseña es incorrecta.",
        [ServerMessageKeys.TwoFactorDisabled] = "La autenticación de dos factores está desactivada.",
        [ServerMessageKeys.TwoFactorRequired] = "La autenticación de dos factores es obligatoria, por lo que no se puede quitar.",
        [ServerMessageKeys.EnrollmentBeginRequired] = "Inicie primero el registro (/admin/api/me/totp/begin).",
        [ServerMessageKeys.TotpCodeMismatch] = "El código es incorrecto. Introduzca los 6 dígitos que muestra su aplicación de autenticación.",
        [ServerMessageKeys.LoginIdInvalid] = "El ID de inicio de sesión debe tener de 1 a 100 caracteres (sin caracteres de control).",
        [ServerMessageKeys.PasswordTooShort] = "La contraseña debe tener al menos {0} caracteres.",
        [ServerMessageKeys.PasswordTooLong] = "La contraseña debe tener como máximo {0} caracteres.",
        [ServerMessageKeys.PasswordSameAsLoginId] = "La contraseña no puede ser igual al ID de inicio de sesión.",
        [ServerMessageKeys.PasswordPolicyMismatch] = "La contraseña no cumple los requisitos.",
        [ServerMessageKeys.AdminUserNotFound] = "No se encontró al administrador.",
        [ServerMessageKeys.RoleNotSupported] = "Indique el rol como uno de los siguientes: {0}.",
        [ServerMessageKeys.DuplicateLoginId] = "Ese ID de inicio de sesión ya existe.",
        [ServerMessageKeys.InviteDisabledUser] = "No se puede invitar a un administrador desactivado. Active primero la cuenta.",
        [ServerMessageKeys.SelfDisableNotAllowed] = "No puede desactivarse a sí mismo.",
        [ServerMessageKeys.SelfRoleChangeNotAllowed] = "No puede cambiar su propio rol.",
        [ServerMessageKeys.SelfTotpResetNotAllowed] = "Quite su propia autenticación de dos factores desde su propia pantalla (/admin/api/me/totp/disable).",
        [ServerMessageKeys.SelfSessionRevokeNotAllowed] = "Revoque sus propios dispositivos desde su propia pantalla (/admin/api/me/sessions).",
        [ServerMessageKeys.LastAdministrator] = "Así no quedaría ningún administrador activo (Administrator). Prepare primero otro administrador.",
        [ServerMessageKeys.InvitationInvalid] = "Esta invitación no se puede usar (puede haber caducado, haber sido aceptada o haber sido cancelada). Pida al administrador que le invitó que la envíe de nuevo.",
        [ServerMessageKeys.SessionNotFound] = "No se encontró ese dispositivo.",
        [ServerMessageKeys.AuditBeforeInvalid] = "Indique before como fecha y hora (ISO 8601).",
        [ServerMessageKeys.ApiKeyNeedsSecretKey] = "Guardar una clave de API requiere la configuración {0}.",
        [ServerMessageKeys.ApiKeyRequired] = "Indique ApiKey (tampoco está configurada la clave de API predeterminada compartida por todos los feeds).",
        [ServerMessageKeys.SettingsValuesRequired] = "Indique en values los ajustes que desea cambiar.",
        [ServerMessageKeys.SecretSettingNeedsSecretKey] = "Guardar un ajuste secreto requiere la configuración {0}.",
        [ServerMessageKeys.SamlNeedsSecretKey] = "El inicio de sesión con SAML requiere la configuración de la clave secreta ({0}).",
        [ServerMessageKeys.FeedNameInvalid] = "Name debe tener de 1 a 200 caracteres.",
        [ServerMessageKeys.FeedSiteIdInvalid] = "SiteId debe ser 1 o mayor.",
        [ServerMessageKeys.FeedFiltersInvalid] = "Filters admite hasta 20 entradas; los nombres de columna solo pueden usar letras, dígitos y _, y los valores pueden tener como máximo 500 caracteres.",
        [ServerMessageKeys.FeedApiKeyInvalid] = "ApiKey debe tener de 1 a 512 caracteres (sin caracteres de control).",
        [ServerMessageKeys.FeedTimeZoneInvalid] = "ApiKeyUserTimeZoneId es un ID de zona horaria que este servidor no puede resolver.",
        [ServerMessageKeys.FeedLanguageInvalid] = "Indique Language como uno de los siguientes: {0}.",
    }.ToFrozenDictionary();
}

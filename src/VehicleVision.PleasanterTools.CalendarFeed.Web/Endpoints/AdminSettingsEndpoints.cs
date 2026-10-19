using VehicleVision.PleasanterTools.CalendarFeed.Localization;
using VehicleVision.PleasanterTools.CalendarFeed.Web.Localization;
using System.Net.Mail;
using System.Security.Cryptography;
using VehicleVision.PleasanterTools.CalendarFeed.Admin;
using VehicleVision.PleasanterTools.CalendarFeed.Data;
using VehicleVision.PleasanterTools.CalendarFeed.Feeds;
using VehicleVision.PleasanterTools.Core.Mail;
using VehicleVision.PleasanterTools.CalendarFeed.Security;
using VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Endpoints;

/// <summary>アプリケーション設定の編集（Issue #40）。<b>権限 <c>settings.manage</c>（Administrator のみ）。</b></summary>
/// <remarks>
/// <para>
/// 方式は Questionnaire の「アプリケーション設定」に則る。優先順位は **外部設定（環境変数・Parameters。画面からは変えられない）＞ DB ＞ 既定値**。
/// </para>
/// <para>
/// **秘密（SMTP パスワード・Pleasanter の共通 API キー）は画面へ返さない**（登録済みかどうかだけ）。保存するときは、空なら変えない。
/// 変えた設定の名前だけを監査ログへ残す（値は残さない）。
/// </para>
/// </remarks>
public static class AdminSettingsEndpoints
{
    /// <summary>設定の編集の入口を登録する。</summary>
    public static void MapAdminSettings(this WebApplication app)
    {
        var group = app.MapGroup("/admin/api/settings");
        group.AddEndpointFilter<AdminSessionFilter>();
        group.AddEndpointFilter(RequirePermissionFilter.Of(AdminPermissions.SettingsManage));

        group.MapGet("/", async (IAppSettingsProvider provider, CancellationToken ct) =>
            Results.Ok(ViewOf(await provider.GetAsync(ct))));

        // 値が null なら既定へ戻す。秘密は空なら変えない。1 つでも不正なら、何も保存しない
        group.MapPut("/", async (
            UpdateSettingsRequest request,
            HttpContext http,
            IAppSettingsProvider provider,
            RuntimeSettings runtime,
            AuditLogService audit,
            CancellationToken ct) =>
        {
            if (request.Values is not { Count: > 0 })
            {
                return ApiError.Of(StatusCodes.Status400BadRequest, ServerMessageKeys.SettingsValuesRequired);
            }

            try
            {
                var (snapshot, changed) = await provider.SaveAsync(request.Values, AdminContext.Of(http).User.Id, ct);

                // 保存したら、この台にはすぐ反映する（他の台へは 30 秒以内）
                runtime.Apply(snapshot);
                await audit.WriteAsync(
                    http, AuditActions.SettingsUpdated, true, AdminContext.Of(http).User, targetType: "Settings",
                    detail: changed.Count == 0 ? "no-change" : $"changed={string.Join(',', changed)}");
                return Results.Ok(ViewOf(snapshot));
            }
            catch (AppSettingValidationException ex)
            {
                // 鍵と埋め込みの値から、要求の言語で返す（Message は日本語。ログ用）
                return ApiError.Of(StatusCodes.Status400BadRequest, ex.MessageKey, ex.Args);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains(UnconfiguredSecretProtector.SettingName, StringComparison.Ordinal))
            {
                // 秘密を保管するには、秘密の鍵が要る
                return ApiError.Of(StatusCodes.Status400BadRequest, ServerMessageKeys.SecretSettingNeedsSecretKey, UnconfiguredSecretProtector.SettingName);
            }
        });

        // メール設定の試し送信。**自分のログイン ID（メールアドレス）宛て**。送信待ちへ積むだけで、届いたかは受信側で確かめる
        group.MapPost("/mail/test", async (
            HttpContext http,
            RuntimeSettings runtime,
            IMailOutbox outbox,
            IMailPayloadProtector protector,
            AuditLogService audit,
            CancellationToken ct) =>
        {
            var actor = AdminContext.Of(http).User;
            var options = runtime.Current.Mail;
            string? reason = null;
            if (!options.IsReady)
            {
                reason = "mail-disabled";
            }
            else if (!MailAddress.TryCreate(actor.LoginId, out _))
            {
                // ログイン ID がメールアドレスとは限らない
                reason = "login-id-not-email";
            }

            var queued = false;
            if (reason is null)
            {
                try
                {
                    var texts = TestMailTexts.For(actor.Language);
                    var mail = new OutgoingMail(actor.LoginId, texts.Subject, texts.Body);
                    queued = await outbox.EnqueueAsync(
                        Guid.NewGuid().ToString("D"), (int)MailKind.SettingsTest, protector.Protect(mail), ct);
                    reason = queued ? null : "queue-failed";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    reason = "queue-failed";
                }
            }

            // 宛先・本文は残さない
            await audit.WriteAsync(http, AuditActions.SettingsMailTest, queued, actor, detail: reason ?? "queued");
            return Results.Ok(new { queued, reason });
        });
    }

    /// <summary>画面へ返す姿。<b>秘密の値は返さない</b>（登録済みかどうかだけ）。</summary>
    private static object ViewOf(AppSettingsSnapshot snapshot) => new
    {
        settings = AppSettingDefinitions.All.Select(d => new
        {
            d.Key,
            d.Group,
            type = d.Type.ToString(),
            d.IsSecret,
            isFixed = snapshot.FixedKeys.Contains(d.Key),
            defaultValue = d.IsSecret ? null : d.DefaultValue,
            d.Minimum,
            d.Maximum,
            d.MaximumLength,
            d.Multiline,
            d.AllowedValues,
            value = d.IsSecret ? null : snapshot[d.Key],
            hasValue = snapshot[d.Key].Length > 0,
            unreadable = snapshot.UnreadableSecrets.Contains(d.Key),
        }),
    };
}

/// <summary>設定の保存の要求。値は文字列（<c>null</c> は既定へ戻す。秘密は空なら変えない）。</summary>
public sealed record UpdateSettingsRequest(Dictionary<string, string?>? Values);

/// <summary>メール設定の試し送信の文面（7 言語）。</summary>
public static class TestMailTexts
{
    public sealed record Text(string Subject, string Body);

    private static readonly Dictionary<string, Text> ByLanguage = new(StringComparer.Ordinal)
    {
        ["ja"] = new("【試し送信】カレンダー購読フィード", "メール送信の設定の確認です。このメールが届いていれば、設定は有効です。心当たりが無ければ破棄してください。"),
        ["en"] = new("[Test] Calendar feed", "This is a test of the mail settings. If you received this message, the settings work. If you were not expecting it, discard it."),
        ["zh"] = new("【测试发送】日历订阅源", "这是邮件设置的测试。如果您收到了此邮件，说明设置有效。如果您未曾预期收到，请删除。"),
        ["de"] = new("[Testversand] Kalender-Feed", "Dies ist ein Test der E-Mail-Einstellungen. Wenn Sie diese Nachricht erhalten haben, funktionieren die Einstellungen. Wenn Sie sie nicht erwartet haben, löschen Sie sie."),
        ["ko"] = new("[테스트 발송] 캘린더 구독 피드", "메일 설정 확인용 테스트입니다. 이 메일이 도착했다면 설정이 올바른 것입니다. 예상하지 못했다면 삭제하세요."),
        ["es"] = new("[Envío de prueba] Feed de calendario", "Esta es una prueba de la configuración de correo. Si recibió este mensaje, la configuración funciona. Si no lo esperaba, descártelo."),
        ["vi"] = new("[Gửi thử] Nguồn cấp lịch", "Đây là thư kiểm tra cấu hình email. Nếu bạn nhận được thư này, cấu hình đã hoạt động. Nếu bạn không mong đợi, hãy xóa thư."),
    };

    /// <summary>言語に合う文面。対応外は日本語。</summary>
    public static Text For(string? language) => ByLanguage[FeedLanguages.Normalize(language)];
}

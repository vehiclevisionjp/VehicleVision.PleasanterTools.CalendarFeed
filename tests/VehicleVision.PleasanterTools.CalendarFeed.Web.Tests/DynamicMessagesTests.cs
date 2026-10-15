using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using VehicleVision.PleasanterTools.CalendarFeed.Feeds;
using VehicleVision.PleasanterTools.CalendarFeed.Localization;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Tests;

/// <summary>動的な検証メッセージ（設定・SAML・Pleasanter のログイン・フィードの列の対応）を、要求の言語で返す（Issue #61）。</summary>
public sealed class DynamicMessagesTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    private async Task<string> PutSettingAsync(string language, params (string Key, string? Value)[] values)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "/admin/api/settings/")
        {
            Content = JsonContent.Create(new { values = values.ToDictionary(v => v.Key, v => v.Value) }),
        };
        request.Headers.Add("Accept-Language", language);
        var response = await _app.AdminClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;
    }

    private async Task<string> CreateFeedErrorAsync(string language, object mapping)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/admin/api/feeds")
        {
            Content = JsonContent.Create(new { name = "x", siteId = 1, mapping }),
        };
        request.Headers.Add("Accept-Language", language);
        var response = await _app.AdminClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;
    }

    // ---- 設定の値の検証 ----------------------------------------------------------------

    [Fact]
    public async Task 整数の設定の検証は_言語ごと_設定の鍵で示す()
    {
        Assert.Equal("CALENDARFEED_FEED_CACHESECONDS must be an integer.", await PutSettingAsync("en", ("CALENDARFEED_FEED_CACHESECONDS", "abc")));
        Assert.Equal("CALENDARFEED_FEED_CACHESECONDS は整数で指定してください。", await PutSettingAsync("ja", ("CALENDARFEED_FEED_CACHESECONDS", "abc")));
        Assert.Equal("CALENDARFEED_FEED_CACHESECONDS must be 1 or greater.", await PutSettingAsync("en", ("CALENDARFEED_FEED_CACHESECONDS", "0")));
    }

    [Fact]
    public async Task 選択肢_真偽値_長さの検証()
    {
        Assert.Contains("must be one of", await PutSettingAsync("en", ("CALENDARFEED_ADMIN_TWOFACTOR", "sometimes")));
        Assert.Contains("must be a boolean", await PutSettingAsync("en", ("CALENDARFEED_MAIL_ENABLED", "maybe")));
        Assert.Contains("at most 100 characters", await PutSettingAsync("en", ("CALENDARFEED_SAML_BUTTONLABEL", new string('x', 101))));
    }

    [Fact]
    public async Task 外部設定で固定された設定は_画面から変えられない_言語ごと()
    {
        using var fixedApp = new TestApp(extraSettings: new() { ["CALENDARFEED_FEED_CACHESECONDS"] = "99" });
        var request = new HttpRequestMessage(HttpMethod.Put, "/admin/api/settings/")
        {
            Content = JsonContent.Create(new { values = new Dictionary<string, string?> { ["CALENDARFEED_FEED_CACHESECONDS"] = "10" } }),
        };
        request.Headers.Add("Accept-Language", "en");

        var response = await fixedApp.AdminClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("fixed by external configuration", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task メールを有効にするときの組み合わせの不足は_言語ごと()
    {
        Assert.Equal("Enabling email sending requires a sender email address.", await PutSettingAsync("en", ("CALENDARFEED_MAIL_ENABLED", "true")));
        Assert.Equal("メール送信を有効にするには差出人メールアドレスが必要です。", await PutSettingAsync("ja", ("CALENDARFEED_MAIL_ENABLED", "true")));
    }

    [Fact]
    public async Task SAMLの設定の不足は_言語ごと_設定の鍵で示す()
    {
        Assert.Equal(
            "When CALENDARFEED_SAML_ENABLED is enabled, CALENDARFEED_SAML_ENTITYID is required.",
            await PutSettingAsync("en", ("CALENDARFEED_SAML_ENABLED", "true")));
        Assert.Equal(
            "CALENDARFEED_SAML_ENABLED を有効にしたときは CALENDARFEED_SAML_ENTITYID が要ります。",
            await PutSettingAsync("ja", ("CALENDARFEED_SAML_ENABLED", "true")));
    }

    [Fact]
    public async Task SAMLの証明書が読めないときの文言は_言語ごと()
    {
        var message = await PutSettingAsync(
            "de",
            ("CALENDARFEED_SAML_ENABLED", "true"), ("CALENDARFEED_SAML_ENTITYID", "https://sp.test/"), ("CALENDARFEED_SAML_IDPENTITYID", "https://idp.test/"),
            ("CALENDARFEED_SAML_SINGLESIGNONURL", "https://idp.test/sso"),
            ("CALENDARFEED_SAML_IDPCERTIFICATE", "-----BEGIN CERTIFICATE-----\nnot a certificate\n-----END CERTIFICATE-----"));

        Assert.Contains("CALENDARFEED_SAML_IDPCERTIFICATE", message);
        Assert.DoesNotMatch("[぀-ヿ]", message);
    }

    [Fact]
    public async Task Pleasanterのログインの設定の検証は_言語ごと()
    {
        Assert.Contains(
            "overlaps with this app's own cookies",
            await PutSettingAsync("en", ("CALENDARFEED_PLEASANTERSSO_COOKIENAMES", "calendarfeed_admin")));
        Assert.Contains(
            "本アプリの cookie",
            await PutSettingAsync("ja", ("CALENDARFEED_PLEASANTERSSO_COOKIENAMES", "calendarfeed_admin")));
        Assert.Contains("must be positive IDs", await PutSettingAsync("en", ("CALENDARFEED_PLEASANTERSSO_ALLOWEDDEPTIDS", "1,abc")));
        Assert.Contains("same-host path", await PutSettingAsync("en", ("CALENDARFEED_PLEASANTERSSO_LOGINURL", "javascript:alert(1)")));
    }

    // ---- フィードの列の対応 -------------------------------------------------------------

    [Fact]
    public async Task 列の対応の検証は_言語ごと()
    {
        Assert.Equal(
            "When StartColumn is a text column, a date format (StartFormat) is required: ClassA",
            await CreateFeedErrorAsync("en", new { startColumn = "ClassA" }));
        Assert.Equal(
            "StartColumn に文字の列を指定するときは、日付の書式（StartFormat）が要ります: ClassA",
            await CreateFeedErrorAsync("ja", new { startColumn = "ClassA" }));
        Assert.Equal("DefaultDurationMinutes must be between 1 and 1440.", await CreateFeedErrorAsync("en", new { defaultDurationMinutes = 0 }));
    }

    [Fact]
    public async Task 件名テンプレートの書き方の誤りは_言語ごと_波括弧そのものも正しく出る()
    {
        Assert.Equal("SummaryTemplate: A \"{\" is not closed. To write a brace itself, use \"{{\".", await CreateFeedErrorAsync("en", new { summaryTemplate = "{abc" }));
        Assert.Contains("「{」が閉じていません", await CreateFeedErrorAsync("ja", new { summaryTemplate = "{abc" }));
        Assert.Equal("SummaryTemplate: Invalid column name: \"bad column\"", await CreateFeedErrorAsync("en", new { summaryTemplate = "{bad column}" }));
        Assert.Contains("SummaryTemplate cannot use this column", await CreateFeedErrorAsync("en", new { summaryTemplate = "{Status}{Nope}" }));
    }

    [Fact]
    public async Task 書式の必要な列に書式が無いときの文言は_波括弧を含めて正しく出る()
    {
        var message = await CreateFeedErrorAsync("en", new { summaryTemplate = "{DateA}" });

        Assert.Equal("SummaryTemplate: a date or number column needs a format ({DateA:format} or Formats): DateA", message);
    }

    [Fact]
    public void 列の対応の検証を_Core_から言語を指定して呼べる()
    {
        var mapping = new FeedMapping { StartColumn = "DateA", EndColumn = null, EndFormat = "yyyy" };

        Assert.Equal("EndFormat cannot be specified without EndColumn.", mapping.Validate("en"));
        Assert.Equal("EndColumn が無いときは EndFormat を指定できません。", mapping.Validate("ja"));
        Assert.NotNull(FeedMapping.TryParseFormat("not a format \\", "ko"));
    }

    [Fact]
    public void 例外の_Message_は日本語のまま_ログ用_鍵と引数を持つ()
    {
        var ex = Assert.Throws<TemplateFormatException>(() => TextTemplate.Parse("{"));

        Assert.Equal(ServerMessageKeys.TemplateBraceUnclosed, ex.MessageKey);
        Assert.Contains("閉じていません", ex.Message);
    }
}

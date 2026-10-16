using System.Security.Cryptography;
using System.Text;
using VehicleVision.PleasanterTools.CalendarFeed.CalDav;
using VehicleVision.PleasanterTools.CalendarFeed.Calendar;
using VehicleVision.PleasanterTools.CalendarFeed.Data;
using VehicleVision.PleasanterTools.CalendarFeed.Feeds;
using VehicleVision.PleasanterTools.CalendarFeed.Pleasanter;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

/// <summary>書き込み（PUT）の結果。</summary>
public enum CalDavWriteStatus
{
    /// <summary>作った（201）。</summary>
    Created,

    /// <summary>更新した（204）。</summary>
    Updated,

    /// <summary>削除した（204。Pleasanter のごみ箱へ移る）。</summary>
    Deleted,

    /// <summary>資源が無い（404）。</summary>
    NotFound,

    /// <summary>前提の条件が合わない（412）。<c>If-Match</c> の ETag が違う、<c>If-None-Match: *</c> なのに既にある、など。</summary>
    PreconditionFailed,

    /// <summary>更新なのに <c>If-Match</c> が無い（428）。</summary>
    PreconditionRequired,

    /// <summary>UID が既にある（409。<c>no-uid-conflict</c>）。</summary>
    UidConflict,

    /// <summary>受け付けられない内容・列の対応（403）。</summary>
    Rejected,

    /// <summary>同じ予定への別の書き込みが進行中で、待ちきれなかった（503）。</summary>
    Busy,
}

/// <summary>書き込みの結果と、応答に載せる情報。</summary>
/// <param name="Status">結果。</param>
/// <param name="Reason">断った理由（英語。応答の本文）。</param>
/// <param name="Problem">断った理由の種類（<c>CALDAV:</c> の事前条件名にする）。</param>
/// <param name="ConflictHref">UID が重なった資源の URL。</param>
public sealed record CalDavWriteResult(
    CalDavWriteStatus Status,
    string? Reason = null,
    ICalProblem? Problem = null,
    string? ConflictHref = null);

/// <summary>
/// カレンダーアプリからの予定の作成・更新を、Pleasanter のレコードへ反映する（Issue #85。設計方針 5・6 章）。
/// </summary>
/// <remarks>
/// <para>
/// <b>競合は本アプリで検出する。</b>Pleasanter の API の更新は競合を検出しない（実機検証結果 14 章）ので、
/// <b>最新のレコードを取り直して ETag（対応する列の内容のハッシュ）を <c>If-Match</c> と比べて</b>から更新する。
/// 取得と更新の間に Pleasanter の画面で直された場合は、検出できない。
/// 本アプリ同士の同時の書き込みは、<b>レコードごとの短い貸し出し</b>（DB。複数台でも効く）で直列にする。
/// </para>
/// <para>
/// <b>利用者本人の API キーで書く。</b>書き込みの権限は Pleasanter が判定する（権限が無ければ <see cref="PleasanterException"/>。
/// 呼び出し側が 403 にする）。<b>レコードがこのフィードのサイトのものか</b>は、本アプリでも確かめる。
/// </para>
/// </remarks>
public sealed class CalDavWriteService(
    PleasanterClient client,
    ICalDavStore store,
    FeedService feeds,
    RuntimeSettings settings,
    TimeProvider time,
    ILogger<CalDavWriteService> logger)
{
    private static readonly TimeSpan LockLease = TimeSpan.FromSeconds(30);
    private const int LockAttempts = 50;
    private static readonly TimeSpan LockInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>1 件の予定を作る・更新する。</summary>
    /// <param name="name">資源の名前（デコード済み。<c>.ics</c> つき）。</param>
    /// <param name="ifMatch"><c>If-Match</c>（ETag のコンマ区切り、または <c>*</c>）。無ければ <c>null</c>。</param>
    /// <param name="ifNoneMatchAny"><c>If-None-Match: *</c>（作るときの合図）。</param>
    public async Task<CalDavWriteResult> PutAsync(
        FeedDefinition feed,
        string apiKey,
        string name,
        string body,
        string? ifMatch,
        bool ifNoneMatchAny,
        CancellationToken cancellationToken)
    {
        ParsedEvent parsed;
        try
        {
            parsed = ICalReader.ParseEvent(body);
        }
        catch (ICalException ex)
        {
            return new CalDavWriteResult(CalDavWriteStatus.Rejected, ex.Message, ex.Problem);
        }

        if (RecordWriter.WritableProblem(feed.Mapping) is { } notWritable)
        {
            return new CalDavWriteResult(CalDavWriteStatus.Rejected, notWritable, ICalProblem.InvalidResource);
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(
            feed.ApiKeyUserTimeZoneId ?? settings.Current.Pleasanter.ApiKeyUserTimeZoneId);
        System.Text.Json.Nodes.JsonObject fields;
        try
        {
            fields = RecordWriter.ToBody(parsed, feed.Mapping, zone);
        }
        catch (NotWritableException ex)
        {
            return new CalDavWriteResult(CalDavWriteStatus.Rejected, ex.Message, ICalProblem.InvalidResource);
        }

        var existing = await ResolveAsync(feed, name, cancellationToken).ConfigureAwait(false);
        if (existing is { } resource)
        {
            return await UpdateAsync(feed, apiKey, resource, parsed, fields, ifMatch, ifNoneMatchAny, zone, cancellationToken)
                .ConfigureAwait(false);
        }

        return await CreateAsync(feed, apiKey, name, parsed, fields, ifMatch, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>資源の名前から、レコードと UID を引く。対応表（作ったもの）→ 導く UID の順。</summary>
    internal async Task<(long RecordId, string Uid)?> ResolveAsync(FeedDefinition feed, string name, CancellationToken cancellationToken)
    {
        if (await store.FindByNameAsync(feed.Id, name, cancellationToken).ConfigureAwait(false) is { } stored)
        {
            return (stored.RecordId, stored.Uid);
        }

        return DerivedRecordId(feed, CalDavPaths.UidOf(name)) is { } id ? (id, CalDavPaths.UidOf(name)!) : null;
    }

    /// <summary>導く UID（<c>{サイト ID}-{レコード ID}@{フィード ID}.calendarfeed</c>）からレコード ID を読む。</summary>
    internal static long? DerivedRecordId(FeedDefinition feed, string? uid)
    {
        if (uid is null)
        {
            return null;
        }

        var suffix = $"@{feed.Id:N}.calendarfeed";
        var prefix = $"{feed.SiteId}-";
        return uid.StartsWith(prefix, StringComparison.Ordinal) && uid.EndsWith(suffix, StringComparison.Ordinal)
            && long.TryParse(uid.AsSpan(prefix.Length, uid.Length - prefix.Length - suffix.Length), out var id) && id > 0
                ? id
                : null;
    }

    private async Task<CalDavWriteResult> UpdateAsync(
        FeedDefinition feed,
        string apiKey,
        (long RecordId, string Uid) resource,
        ParsedEvent parsed,
        System.Text.Json.Nodes.JsonObject fields,
        string? ifMatch,
        bool ifNoneMatchAny,
        TimeZoneInfo zone,
        CancellationToken cancellationToken)
    {
        if (ifNoneMatchAny)
        {
            return new CalDavWriteResult(CalDavWriteStatus.PreconditionFailed, "The resource already exists.");
        }

        if (ifMatch is null)
        {
            return new CalDavWriteResult(CalDavWriteStatus.PreconditionRequired, "If-Match is required to update an event.");
        }

        // UID は変えられない（変わると、別の予定と見なされて重複する）
        if (!string.Equals(parsed.Uid, resource.Uid, StringComparison.Ordinal))
        {
            return new CalDavWriteResult(
                CalDavWriteStatus.Rejected, "The UID of an existing event cannot be changed.", ICalProblem.InvalidResource);
        }

        var lockKey = $"{feed.Id:N}:r{resource.RecordId}";
        var owner = Guid.NewGuid().ToString("D");
        if (!await AcquireAsync(lockKey, owner, cancellationToken).ConfigureAwait(false))
        {
            return new CalDavWriteResult(CalDavWriteStatus.Busy, "Another write to this event is in progress.");
        }

        try
        {
            if (await CheckCurrentAsync(feed, apiKey, resource, ifMatch, zone, cancellationToken).ConfigureAwait(false) is { } failure)
            {
                return failure;
            }

            await client.UpdateRecordAsync(apiKey, resource.RecordId, fields, cancellationToken).ConfigureAwait(false);
            feeds.Invalidate(feed.Id);
            return new CalDavWriteResult(CalDavWriteStatus.Updated);
        }
        finally
        {
            await store.ReleaseLockAsync(lockKey, owner, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <b>予定を削除する。</b><c>If-Match</c> が<b>あれば比べる</b>（違えば 412）。<b>無くても削除する</b>
    /// （更新と違い、Pleasanter では<b>ごみ箱へ移って戻せる</b>。<c>If-Match</c> を付けないクライアントがある。実機の Python <c>caldav</c> など）。
    /// 対応表の行は消す（その UID・名前で作り直せるように）。
    /// </summary>
    public async Task<CalDavWriteResult> DeleteAsync(
        FeedDefinition feed, string apiKey, string name, string? ifMatch, CancellationToken cancellationToken)
    {
        if (await ResolveAsync(feed, name, cancellationToken).ConfigureAwait(false) is not { } resource)
        {
            return new CalDavWriteResult(CalDavWriteStatus.NotFound);
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(
            feed.ApiKeyUserTimeZoneId ?? settings.Current.Pleasanter.ApiKeyUserTimeZoneId);
        var lockKey = $"{feed.Id:N}:r{resource.RecordId}";
        var owner = Guid.NewGuid().ToString("D");
        if (!await AcquireAsync(lockKey, owner, cancellationToken).ConfigureAwait(false))
        {
            return new CalDavWriteResult(CalDavWriteStatus.Busy, "Another write to this event is in progress.");
        }

        try
        {
            if (await CheckCurrentAsync(feed, apiKey, resource, ifMatch, zone, cancellationToken).ConfigureAwait(false) is { } failure)
            {
                return failure;
            }

            await client.DeleteRecordAsync(apiKey, resource.RecordId, cancellationToken).ConfigureAwait(false);
            await store.DeleteByRecordAsync(feed.Id, resource.RecordId, cancellationToken).ConfigureAwait(false);
            feeds.Invalidate(feed.Id);
            return new CalDavWriteResult(CalDavWriteStatus.Deleted);
        }
        finally
        {
            await store.ReleaseLockAsync(lockKey, owner, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <b>最新のレコードを取り直して、ETag を <c>If-Match</c> と比べる</b>（Pleasanter の API は競合を検出しない）。
    /// レコードが無ければ古い対応を消して「前提が合わない」、別のサイトのものなら断る。通ってよければ <c>null</c>。
    /// <paramref name="ifMatch"/> が <c>null</c> なら ETag は比べない。
    /// </summary>
    private async Task<CalDavWriteResult?> CheckCurrentAsync(
        FeedDefinition feed,
        string apiKey,
        (long RecordId, string Uid) resource,
        string? ifMatch,
        TimeZoneInfo zone,
        CancellationToken cancellationToken)
    {
        System.Text.Json.Nodes.JsonNode record;
        try
        {
            record = await client.GetRecordAsync(apiKey, resource.RecordId, cancellationToken).ConfigureAwait(false);
        }
        catch (PleasanterException ex) when (ex.StatusCode == 404)
        {
            // レコードが無い（Pleasanter で消された）。古い対応は消し、「前提が合わない」として再取得させる
            await store.DeleteByRecordAsync(feed.Id, resource.RecordId, cancellationToken).ConfigureAwait(false);
            feeds.Invalidate(feed.Id);
            return new CalDavWriteResult(CalDavWriteStatus.PreconditionFailed, "The event no longer exists.");
        }

        if (!BelongsTo(record, feed))
        {
            return new CalDavWriteResult(CalDavWriteStatus.Rejected, "The record does not belong to this calendar.", ICalProblem.InvalidResource);
        }

        var current = EventMapper.Map(record, feed, zone, settings.Current.Pleasanter.LinkBaseUrl);
        if (current is null)
        {
            return new CalDavWriteResult(CalDavWriteStatus.PreconditionFailed, "The event cannot be read any more.");
        }

        // If-Match が無い（削除だけ。更新は呼び出し側が先に断っている）なら、比べない
        var etag = CalDavPaths.ETagOf(ICalWriter.WriteResource(current with { Uid = resource.Uid }));
        return ifMatch is null || IfMatchSatisfied(ifMatch, etag)
            ? null
            : new CalDavWriteResult(CalDavWriteStatus.PreconditionFailed, "The event was changed. Fetch it again.");
    }

    private async Task<CalDavWriteResult> CreateAsync(
        FeedDefinition feed,
        string apiKey,
        string name,
        ParsedEvent parsed,
        System.Text.Json.Nodes.JsonObject fields,
        string? ifMatch,
        CancellationToken cancellationToken)
    {
        // 存在しないものへの If-Match は、前提が合わない（RFC 7232 3.1）
        if (ifMatch is not null && ifMatch != "*")
        {
            return new CalDavWriteResult(CalDavWriteStatus.PreconditionFailed, "The resource does not exist.");
        }

        var lockKey = $"{feed.Id:N}:u{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(parsed.Uid)))[..24].ToLowerInvariant()}";
        var owner = Guid.NewGuid().ToString("D");
        if (!await AcquireAsync(lockKey, owner, cancellationToken).ConfigureAwait(false))
        {
            return new CalDavWriteResult(CalDavWriteStatus.Busy, "Another write with this UID is in progress.");
        }

        try
        {
            // UID はフィードの中で一意（RFC 4791 4.1）。作った対応表と、導く UID の両方を見る
            string? conflictName = null;
            if (await store.FindByUidAsync(feed.Id, parsed.Uid, cancellationToken).ConfigureAwait(false) is { } sameUid)
            {
                conflictName = sameUid.Name;
            }
            else if (DerivedRecordId(feed, parsed.Uid) is not null)
            {
                conflictName = CalDavPaths.ResourceName(parsed.Uid);
            }

            if (conflictName is not null)
            {
                return new CalDavWriteResult(
                    CalDavWriteStatus.UidConflict, "The UID already exists in this calendar.", ConflictHref: CalDavPaths.Event(feed.Id, conflictName));
            }

            var recordId = await client.CreateRecordAsync(apiKey, feed.SiteId, fields, cancellationToken).ConfigureAwait(false);
            if (!await store.TryAddAsync(new CalDavResource(feed.Id, name, parsed.Uid, recordId), cancellationToken).ConfigureAwait(false))
            {
                // 名前が同時に取られた。レコードは作れているので、導く UID で見える（クライアントは次の同期で拾う）
                logger.LogWarning("CalDAV で作ったレコードの UID の対応を残せませんでした（フィード {Feed}、レコード {Record}）", feed.Id, recordId);
            }

            feeds.Invalidate(feed.Id);
            return new CalDavWriteResult(CalDavWriteStatus.Created);
        }
        finally
        {
            await store.ReleaseLockAsync(lockKey, owner, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>レコードがこのフィードのサイトのものか。</summary>
    private static bool BelongsTo(System.Text.Json.Nodes.JsonNode record, FeedDefinition feed) =>
        long.TryParse(EventMapper.GetString(record, "SiteId"), out var siteId) && siteId == feed.SiteId;

    /// <summary>
    /// <c>If-Match</c>（ETag のコンマ区切り、または <c>*</c>）に、現在の ETag が含まれるか（RFC 7232 3.1。強い比較）。
    /// </summary>
    internal static bool IfMatchSatisfied(string ifMatch, string currentETag) =>
        ifMatch.Trim() == "*"
        || ifMatch.Split(',').Select(t => t.Trim()).Any(t => !t.StartsWith("W/", StringComparison.Ordinal) && t == currentETag);

    /// <summary>排他の貸し出しを、短い間隔で取りに行く（約 5 秒まで）。</summary>
    private async Task<bool> AcquireAsync(string key, string owner, CancellationToken cancellationToken)
    {
        // 時計ではなく試行の回数で数える（テストの時計が止まっていても終わる）
        for (var attempt = 0; attempt < LockAttempts; attempt++)
        {
            if (await store.TryAcquireLockAsync(key, owner, time.GetUtcNow(), LockLease, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(LockInterval, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

}

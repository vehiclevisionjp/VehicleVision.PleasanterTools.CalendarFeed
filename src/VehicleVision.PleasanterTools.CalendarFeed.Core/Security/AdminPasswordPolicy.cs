using VehicleVision.PleasanterTools.CalendarFeed.Localization;
using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace VehicleVision.PleasanterTools.CalendarFeed.Security;

/// <summary>パスワードの条件 1 つ分。**Pleasanter 本体の <c>PasswordPolicy</c> と同じ項目名**（本体の Security.json を持ってこられる）。</summary>
public sealed record AdminPasswordRule
{
    /// <summary>この条件を使うか。<c>false</c> なら見ない。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>条件の正規表現。**部分一致**（本体と同じ）。全体一致にしたいときは <c>^…$</c> を書く。</summary>
    /// <remarks>⚠️ **先読み・後方参照・原子グループは使えない**（後退戻りしない照合器で動かすため。起動時に落とす）。</remarks>
    public string Regex { get; init; } = string.Empty;

    /// <summary>合わないときに出す文言。言語ごとに並べる。</summary>
    public IReadOnlyList<AdminPasswordRuleMessage> Languages { get; init; } = [];
}

/// <summary>言語ごとの文言。</summary>
public sealed record AdminPasswordRuleMessage
{
    /// <summary>言語コード。**空なら既定**（どの言語にも当てはまらないときに使う）。</summary>
    public string? Language { get; init; }

    /// <summary>文言そのもの。</summary>
    public string Body { get; init; } = string.Empty;
}

/// <summary>パスワードに求める条件。</summary>
/// <remarks>
/// ⚠️ **設定の正規表現は ReDoS になり得る。** **後退戻りしない照合器（<see cref="RegexOptions.NonBacktracking"/>）＋時間の上限**で動かす。
/// **組み立てられない指定は、この型を作る時点で落とす**（黙って無効にすると、条件が無いまま動く）。
/// </remarks>
/// <param name="MinimumLength">最低の長さ。</param>
/// <param name="AllowSameAsLoginId">ログイン ID と同じパスワードを許すか（大文字小文字は区別しない）。</param>
/// <param name="Rules">正規表現の条件。**上から順に見て、最初に合わなかったものの文言を返す。**</param>
/// <exception cref="InvalidOperationException">正規表現が空・組み立てられない場合。</exception>
public sealed record AdminPasswordPolicy(
    int MinimumLength = 12,
    bool AllowSameAsLoginId = false,
    IReadOnlyList<AdminPasswordRule>? Rules = null)
{
    /// <summary>上限。極端に長い入力で照合の負荷を上げさせない。</summary>
    public const int MaximumLength = 256;

    /// <summary>
    /// 照合の時間の上限。後退戻りしない照合器は入力の長さに比例した時間で終わるが、
    /// 照合器を作る側が将来変わっても上限だけは残るよう、別に渡しておく。
    /// </summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    private readonly ImmutableArray<(Regex Regex, AdminPasswordRule Rule)> _compiled = Compile(Rules);

    /// <summary>
    /// 条件を満たさなければ理由を返す。**最初の管理者・パスワードの変更のどちらもここを通す**（片方だけ緩いと、そこが一番弱い所になる）。
    /// </summary>
    /// <param name="loginId">ログイン ID。</param>
    /// <param name="password">パスワード。</param>
    /// <param name="language">文言の言語。</param>
    public string? Validate(string loginId, string password, string? language = null)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinimumLength)
        {
            return ServerMessages.Get(ServerMessageKeys.PasswordTooShort, language, MinimumLength);
        }

        if (password.Length > MaximumLength)
        {
            return ServerMessages.Get(ServerMessageKeys.PasswordTooLong, language, MaximumLength);
        }

        if (!AllowSameAsLoginId && string.Equals(password, loginId, StringComparison.OrdinalIgnoreCase))
        {
            return ServerMessages.Get(ServerMessageKeys.PasswordSameAsLoginId, language);
        }

        foreach (var (regex, rule) in _compiled)
        {
            if (!regex.IsMatch(password))
            {
                // **文言を書き忘れても、通ってしまわないようにする。** 条件には合っていないのだから、何かは返す
                return MessageOf(rule, language) ?? ServerMessages.Get(ServerMessageKeys.PasswordPolicyMismatch, language);
            }
        }

        return null;
    }

    private static ImmutableArray<(Regex, AdminPasswordRule)> Compile(IReadOnlyList<AdminPasswordRule>? rules)
    {
        var builder = ImmutableArray.CreateBuilder<(Regex, AdminPasswordRule)>();
        foreach (var rule in (rules ?? []).Where(rule => rule.Enabled))
        {
            if (string.IsNullOrEmpty(rule.Regex))
            {
                throw new InvalidOperationException(
                    "PasswordPolicies の Regex が空です。使わない条件は Enabled を false にしてください。");
            }

            try
            {
                builder.Add((
                    new Regex(rule.Regex, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, MatchTimeout),
                    rule));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                // 先読み・後方参照・原子グループは NotSupportedException、組み立てられない書き方は ArgumentException で来る
                throw new InvalidOperationException(
                    $"PasswordPolicies の Regex が使えません: {rule.Regex}（先読み・後方参照・原子グループは使えません）", ex);
            }
        }

        return builder.ToImmutable();
    }

    /// <summary>言語に合う文言を選ぶ。無ければ言語指定なしのもの、それも無ければ先頭のもの。</summary>
    private static string? MessageOf(AdminPasswordRule rule, string? language)
    {
        var body = rule.Languages.FirstOrDefault(m => !string.IsNullOrEmpty(language)
                && string.Equals(m.Language, language, StringComparison.OrdinalIgnoreCase))?.Body
            ?? rule.Languages.FirstOrDefault(m => string.IsNullOrEmpty(m.Language))?.Body
            ?? rule.Languages.FirstOrDefault()?.Body;
        return string.IsNullOrWhiteSpace(body) ? null : body;
    }
}

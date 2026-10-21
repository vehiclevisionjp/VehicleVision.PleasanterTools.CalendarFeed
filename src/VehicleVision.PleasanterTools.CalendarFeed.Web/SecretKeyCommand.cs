using System.Text.Json;
using System.Text.Json.Nodes;
using VehicleVision.PleasanterTools.CalendarFeed.Security;
using VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web;

/// <summary>
/// 秘密の鍵（<c>CALENDARFEED_SECRET_KEY</c>）を作る（Issue #92）。
/// </summary>
/// <remarks>
/// <para>
/// <c>--generate-secret-key</c> は鍵を 1 行で表示する（設定へは自分で入れる）。
/// <c>--generate-secret-key --write</c> は、鍵を <b><c>Security.local.json</c>（git 管理外）の <c>SecretKey</c> へ書く</b>。
/// 置き場は <c>--directory=…</c>、無ければ <c>App_Data/Parameters</c>（現在のディレクトリの下、無ければ実行ファイルの隣）。
/// </para>
/// <para>
/// ⚠️ <b>すでに鍵がある（環境変数・<c>Security.json</c>・<c>Security.local.json</c>）なら、書かずに断る。</b>
/// 黙って鍵を変えると、DB へ暗号化して保管した API キー・管理者の 2 要素が復号できなくなる。
/// ⚠️ <b>ファイルが消える環境（コンテナの書き込み層など）では、書いても再起動で消える。</b>そういう環境では環境変数
/// （アプリケーション設定・Key Vault の参照など）で与えること。<b>書いた鍵は控えを取る</b>（失うと復号できない）。
/// </para>
/// <para>
/// <b>コンソールへ出す文字列は英語で書くこと。</b>Azure の Kudu の Debug console では日本語が化ける。
/// <b>書き込むときは鍵そのものを画面へ出さない</b>（ログに残さない）。
/// </para>
/// </remarks>
public static class SecretKeyCommand
{
    public const string Argument = "--generate-secret-key";
    public const string WriteArgument = "--write";
    private const string DirectoryPrefix = "--directory=";
    private const string SettingKey = "SecretKey";

    public static bool IsRequested(string[] args) => args.Contains(Argument, StringComparer.Ordinal);

    /// <summary>実行する。終了コードを返す（0 なら成功）。</summary>
    public static int Run(string[] args, TextWriter output, TextWriter error, string? currentDirectory = null)
    {
        var key = SecretProtector.GenerateKey();
        if (!args.Contains(WriteArgument, StringComparer.Ordinal))
        {
            output.WriteLine(key);
            return 0;
        }

        var directory = DirectoryOf(args, currentDirectory ?? Directory.GetCurrentDirectory());
        if (ExistingKeySource(directory) is { } source)
        {
            error.WriteLine($"A secret key is already set ({source}). Not overwriting it: the API keys and 2FA secrets stored in the database could no longer be decrypted.");
            return 1;
        }

        var path = Path.Combine(directory, "Security.local.json");
        Directory.CreateDirectory(directory);
        var json = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip }) as JsonObject ?? new JsonObject() : new JsonObject();
        json[SettingKey] = key;

        // 途中で切れても壊さない（書いてから置き換える）
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + Environment.NewLine);
        if (!OperatingSystem.IsWindows())
        {
            // 鍵を含むので、所有者だけが読み書きできるようにする
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temporary, path, overwrite: true);

        output.WriteLine($"Wrote a new secret key to {path}.");
        output.WriteLine("Back it up now: if it is lost, the API keys and 2FA secrets stored in the database cannot be decrypted.");
        output.WriteLine("If the file system is not persistent (for example a container), set CALENDARFEED_SECRET_KEY as an environment variable instead.");
        return 0;
    }

    /// <summary>鍵の置き場（<c>--directory=</c>、無ければ <c>App_Data/Parameters</c>）。</summary>
    private static string DirectoryOf(string[] args, string currentDirectory)
    {
        if (args.FirstOrDefault(a => a.StartsWith(DirectoryPrefix, StringComparison.Ordinal)) is { } given)
        {
            return Path.GetFullPath(given[DirectoryPrefix.Length..], currentDirectory);
        }

        var underCurrent = Path.Combine(currentDirectory, ParameterFiles.DefaultDirectory);
        return Directory.Exists(underCurrent) ? underCurrent : Path.Combine(AppContext.BaseDirectory, ParameterFiles.DefaultDirectory);
    }

    /// <summary>すでに鍵がある場所の名前（無ければ <c>null</c>）。</summary>
    private static string? ExistingKeySource(string directory)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppSettings.SecretKey)))
        {
            return "environment variable " + AppSettings.SecretKey;
        }

        // Docker secrets など、値をファイルから読む指定（中身は読まない。指定があれば「ある」とみなす）
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppSettings.SecretKey + ParameterFiles.FileSuffix)))
        {
            return "environment variable " + AppSettings.SecretKey + ParameterFiles.FileSuffix;
        }

        foreach (var name in new[] { "Security.json", "Security.local.json" })
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var node = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
                if (node is JsonObject obj && obj[SettingKey]?.GetValue<string>() is { Length: > 0 })
                {
                    return path;
                }
            }
            catch (JsonException)
            {
                // 読めないファイルは、鍵があるかを判断できない。上書きせずに止める
                return path + " (unreadable)";
            }
        }

        return null;
    }
}

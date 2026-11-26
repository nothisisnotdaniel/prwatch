using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PRWatch;

/// <summary>One checked request from the web app: the action (one of <see cref="PrRules.Requests"/>), the project,
/// the PR number and the head the user saw; <see cref="Consent"/> is the body's value for <c>review</c> and
/// <c>false</c> otherwise.</summary>
internal sealed record PrRequest(string Action, string Project, int Number, string HeadSha, bool Consent);

/// <summary>Checks a <c>review</c>, <c>merge</c> or <c>cancel</c> body and writes
/// <c>factory/prs/&lt;repo&gt;-&lt;n&gt;/request.json</c> (<c>{"action","headSha","consent"}</c>, <c>0600</c>)
/// atomically through <see cref="SafeFileWriter"/>: never through a symlink, only directly inside
/// <c>&lt;root&gt;/factory/prs/</c>, missing folders created as <c>0700</c>. Only the checked number and the
/// project matched against the discovered list shape the path.</summary>
internal static class RequestWriter
{
    public const int MaxBodyBytes = 1024;

    public const int MaxFileBytes = 4096;

    public const string FileName = "request.json";

    public const string Review = "review";

    public const string Merge = "merge";

    public const string Cancel = "cancel";

    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode PrivateFolder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private static readonly string[] BaseKeys = ["project", "number", "headSha"];

    private static readonly string[] ReviewKeys = ["project", "number", "headSha", "consent"];

    /// <summary>The request in <paramref name="body"/> when it keeps every rule for <paramref name="action"/>, else
    /// <c>null</c>: a JSON object of at most <see cref="MaxBodyBytes"/> with only the allowed keys (each once), a
    /// project in <paramref name="projects"/>, an integer number from 1 to <see cref="PrRules.MaxNumber"/>, a
    /// 40-hex head and, for <c>review</c> only, a bool <c>consent</c>.</summary>
    public static PrRequest? Parse(string action, byte[] body, IReadOnlyCollection<string> projects)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(projects);
        if (!PrRules.Requests.Contains(action, StringComparer.Ordinal) || body.Length > MaxBodyBytes)
        {
            return null;
        }

        bool review = action == Review;
        string[] allowed = review ? ReviewKeys : BaseKeys;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement top = document.RootElement;
            if (top.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (JsonProperty property in top.EnumerateObject())
            {
                if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                {
                    return null;
                }
            }

            if (!top.TryGetProperty("project", out JsonElement projectValue)
                || projectValue.ValueKind != JsonValueKind.String
                || projectValue.GetString() is not { } project
                || !PrRules.IsProjectName(project)
                || !projects.Contains(project, StringComparer.Ordinal)
                || !top.TryGetProperty("number", out JsonElement numberValue)
                || numberValue.ValueKind != JsonValueKind.Number
                || !numberValue.TryGetInt32(out int number)
                || !PrRules.IsNumber(number)
                || !top.TryGetProperty("headSha", out JsonElement shaValue)
                || shaValue.ValueKind != JsonValueKind.String
                || shaValue.GetString() is not { } headSha
                || !PrRules.IsSha(headSha))
            {
                return null;
            }

            bool consent = false;
            if (review)
            {
                if (!top.TryGetProperty("consent", out JsonElement consentValue)
                    || consentValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return null;
                }

                consent = consentValue.GetBoolean();
            }

            return new PrRequest(action, project, number, headSha, consent);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The file's bytes: exactly <c>{"action","headSha","consent"}</c>.</summary>
    public static byte[] FileBytes(PrRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        JsonObject file = new()
        {
            ["action"] = request.Action,
            ["headSha"] = request.HeadSha,
            ["consent"] = request.Consent,
        };
        return Encoding.UTF8.GetBytes(file.ToJsonString());
    }

    /// <summary>The request file's path for <paramref name="request"/> under <paramref name="root"/>.</summary>
    public static string PathFor(string root, PrRequest request)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(request);
        return Path.Combine(root, "factory", "prs", FolderName(request), FileName);
    }

    /// <summary>Writes the request file; <c>false</c> (nothing written, no link's target changed) when
    /// <c>factory/</c>, <c>factory/prs/</c>, the PR folder or the file is a symlink, when the folder is not
    /// directly inside <c>&lt;root&gt;/factory/prs/</c> or when the write throws.</summary>
    public static bool Write(string root, PrRequest request)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(request);
        byte[] bytes = FileBytes(request);
        if (bytes.Length > MaxFileBytes)
        {
            return false;
        }

        try
        {
            string factory = Path.GetFullPath(Path.Combine(root, "factory"));
            string prs = Path.Combine(factory, "prs");
            string name = FolderName(request);
            string folder = Path.GetFullPath(Path.Combine(prs, name));
            if (!string.Equals(Path.GetDirectoryName(folder), prs, StringComparison.Ordinal)
                || !string.Equals(Path.GetFileName(folder), name, StringComparison.Ordinal)
                || !IsPlainFolder(factory)
                || !EnsureFolder(prs)
                || !EnsureFolder(folder))
            {
                return false;
            }

            string file = Path.Combine(folder, FileName);
            FileInfo target = new(file);
            if (target.LinkTarget is not null || Directory.Exists(file))
            {
                return false;
            }

            if (!OperatingSystem.IsWindows() && target.Exists && File.GetUnixFileMode(file) != PrivateFile)
            {
                // SafeFileWriter keeps an existing file's mode; a request file is always 0600.
                SafeFileWriter.Delete(file);
            }

            PrivateFileWriter.Write(file, bytes);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    private static string FolderName(PrRequest request)
    {
        return request.Project + "-" + request.Number.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>True for an existing folder that is not a symlink.</summary>
    private static bool IsPlainFolder(string path)
    {
        DirectoryInfo info = new(path);
        return info.LinkTarget is null && info.Exists;
    }

    /// <summary>Creates a missing folder as <c>0700</c>; true when the folder then exists and is not a
    /// symlink.</summary>
    private static bool EnsureFolder(string path)
    {
        DirectoryInfo info = new(path);
        if (info.LinkTarget is not null || File.Exists(path))
        {
            return false;
        }

        if (!info.Exists)
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(path);
            }
            else
            {
                Directory.CreateDirectory(path, PrivateFolder);
            }
        }

        return IsPlainFolder(path);
    }
}

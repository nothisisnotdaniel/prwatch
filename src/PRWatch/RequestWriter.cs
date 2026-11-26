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
/// atomically through no-follow folder descriptors (<see cref="FolderHandleWriter"/>): never through a symlink, only directly inside
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

    /// <summary>Test-only: called with <c>"folders-open"</c> once every folder is open and with
    /// <c>"before-rename"</c> once the temp file is written. Null in production.</summary>
    internal static Action<string>? BeforeStep { get; set; }

    /// <summary>Test-only: <c>false</c> makes <see cref="Write"/> act as on an unsupported platform. Null in
    /// production, where the OS and architecture decide.</summary>
    internal static bool? PlatformSupportedOverride { get; set; }

    /// <summary>Writes the request file through no-follow folder descriptors (<see cref="FolderHandleWriter"/>), so
    /// a symlink swapped in at any path component during the write can't redirect it; <c>false</c> (nothing
    /// written, no link's target changed) when <c>factory/</c>, <c>factory/prs/</c>, the PR folder or the file is a
    /// symlink, when the folder name is not a single component directly inside <c>&lt;root&gt;/factory/prs/</c>,
    /// on a platform other than macOS or Linux glibc x64/arm64, or when the write fails.</summary>
    public static bool Write(string root, PrRequest request)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(request);
        byte[] bytes = FileBytes(request);
        if (bytes.Length > MaxFileBytes || !(PlatformSupportedOverride ?? FolderHandleWriter.IsSupported))
        {
            return false;
        }

        string name = FolderName(request);
        if (!IsSingleComponent(name))
        {
            return false;
        }

        try
        {
            return FolderHandleWriter.Write(root, ["factory"], ["prs", name], FileName, bytes, BeforeStep);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Any refusal or failure is the fixed write_failed; the exception's text is never shown or logged.
            return false;
        }
    }

    /// <summary>True for a name that is one path component: not empty, <c>.</c> or <c>..</c>, without <c>/</c>
    /// or NUL.</summary>
    private static bool IsSingleComponent(string name)
    {
        return name.Length > 0 && name != "." && name != ".." && !name.Contains('/', StringComparison.Ordinal)
            && !name.Contains('\0', StringComparison.Ordinal);
    }

    private static string FolderName(PrRequest request)
    {
        return request.Project + "-" + request.Number.ToString(CultureInfo.InvariantCulture);
    }
}

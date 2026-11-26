using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PRWatch;

/// <summary>PRWatch's current state (<c>status.json</c> in the data folder): the last poll, the last run per PR
/// (keyed <c>&lt;repo&gt;-&lt;n&gt;</c>), the open PRs of the last poll, the skipped projects, the last pick, the
/// brake, the Claude login and the last error.</summary>
internal sealed record Status
{
    public DateTimeOffset? LastPoll { get; init; }

    public ImmutableSortedDictionary<string, RunStatus> Runs { get; init; } =
        ImmutableSortedDictionary.Create<string, RunStatus>(StringComparer.Ordinal);

    public IReadOnlyList<PrStatus> Prs { get; init; } = [];

    public IReadOnlyList<SkipStatus> Skipped { get; init; } = [];

    public PickStatus? Pick { get; init; }

    public BrakeStatus Brake { get; init; } = new();

    public LoginStatus Login { get; init; } = new();

    /// <summary>The plan usage of the last <c>rate_limit_event</c> of a run, or <c>null</c> before the first.</summary>
    public PlanUsage? PlanUsage { get; init; }

    public StatusError? LastError { get; init; }

    public static Status Initial { get; } = new();
}

/// <summary>One PR's last run: the head it reviewed, when it started and ended, the exit code, the fixed outcome
/// code (<see cref="Results"/>) and the figures of its last stream-json <c>result</c> event: the cumulative cost,
/// whether any <c>result</c> was an error and the token counts per model (numbers only).</summary>
internal sealed record RunStatus
{
    public const string Ok = "ok";

    public const string Error = "error";

    public const string Budget = "budget";

    public const string Timeout = "timeout";

    public const string OutputTooLarge = "output_too_large";

    public const string Auth = "auth";

    public const string Stopped = "stopped";

    public static readonly string[] Results = [Ok, Error, Budget, Timeout, OutputTooLarge, Auth, Stopped];

    public string? HeadSha { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? EndedAt { get; init; }

    public int? ExitCode { get; init; }

    public string? Result { get; init; }

    public double? CostUsd { get; init; }

    public bool? IsError { get; init; }

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>? ModelUsage { get; init; }
}

/// <summary>The Claude plan usage from a run's last <c>rate_limit_event</c>: the five-hour and seven-day windows,
/// whether the plan said it is limited, and when PRWatch read it.</summary>
internal sealed record PlanUsage
{
    public UsageWindow? FiveHour { get; init; }

    public UsageWindow? SevenDay { get; init; }

    public bool Limited { get; init; }

    public DateTimeOffset? At { get; init; }
}

/// <summary>One plan usage window: its <c>utilization</c> and when it resets.</summary>
internal sealed record UsageWindow
{
    public double? Utilization { get; init; }

    public DateTimeOffset? ResetsAt { get; init; }
}

/// <summary>One open PR as the last poll saw it. <see cref="Title"/>, <see cref="Author"/> and <see cref="Url"/>
/// are untrusted text (capped; the URL only in its exact GitHub form). <see cref="HeadSeenAt"/> is when PRWatch
/// first saw <see cref="HeadSha"/> on this PR.</summary>
internal sealed record PrStatus
{
    public required string Project { get; init; }

    public required int Number { get; init; }

    public required string Title { get; init; }

    public required string Author { get; init; }

    public string? Url { get; init; }

    public required string HeadSha { get; init; }

    public required DateTimeOffset HeadSeenAt { get; init; }

    public required bool Trusted { get; init; }

    public string? Request { get; init; }

    public RoundStatus? Round { get; init; }
}

/// <summary>A PR's latest review round: its number, the head it reviewed, whether that is the PR's head now and
/// whether it came out clean.</summary>
internal sealed record RoundStatus
{
    public required int K { get; init; }

    public required string HeadSha { get; init; }

    public required bool Current { get; init; }

    public required bool Clean { get; init; }
}

/// <summary>A project the last poll could not read or the watcher leaves alone, with a fixed reason code.</summary>
internal sealed record SkipStatus
{
    public const string GhMissing = "gh_missing";

    public const string NoRemote = "no_remote";

    public const string GhFailed = "gh_failed";

    public const string Unknown = "unknown";

    public const string JobActive = "job_active";

    public static readonly string[] Reasons = [GhMissing, NoRemote, GhFailed, Unknown, JobActive];

    public required string Project { get; init; }

    public required string Reason { get; init; }
}

/// <summary>The PR the last poll picked for a review, and when.</summary>
internal sealed record PickStatus
{
    public required string Project { get; init; }

    public required int Number { get; init; }

    public required string HeadSha { get; init; }

    public required DateTimeOffset At { get; init; }
}

/// <summary>Whether the token brake holds, why (one of <see cref="TokenBrake.Reasons"/>) and until when.</summary>
internal sealed record BrakeStatus
{
    public bool Holding { get; init; }

    public string? Reason { get; init; }

    public DateTimeOffset? Until { get; init; }
}

/// <summary>The Claude login: <see cref="State"/> is one of <see cref="LoginStates"/>.</summary>
internal sealed record LoginStatus
{
    public const string Unknown = "unknown";

    public const string Ok = "ok";

    public const string NeedsLogin = "needs_login";

    public static readonly string[] LoginStates = [Unknown, Ok, NeedsLogin];

    /// <summary>How long before the credential expires PRWatch warns.</summary>
    public static readonly TimeSpan WarnBefore = TimeSpan.FromDays(7);

    public string State { get; init; } = Unknown;

    public DateTimeOffset? CredentialExpiresAt { get; init; }

    /// <summary>True when now is within <see cref="WarnBefore"/> of <see cref="CredentialExpiresAt"/> (or past
    /// it).</summary>
    public bool ExpiresSoon { get; init; }

    /// <summary>Whether a credential that expires at <paramref name="expiresAt"/> expires soon at
    /// <paramref name="now"/>.</summary>
    public static bool IsExpiringSoon(DateTimeOffset? expiresAt, DateTimeOffset now)
    {
        return expiresAt is { } at && now >= at - WarnBefore;
    }
}

/// <summary>The last error: a fixed code and when it happened.</summary>
internal sealed record StatusError(string Code, DateTimeOffset At)
{
    public const string SettingsUnreadable = "settings_unreadable";

    public const string StatusWriteFailed = "status_write_failed";

    public const string PythonMissing = "python_missing";

    public const string PollTimeout = "poll_timeout";

    public const string PollFailed = "poll_failed";

    public const string PollOutputInvalid = "poll_output_invalid";

    public const string ClaudeNotFound = "claude_not_found";

    public const string ModelUnknown = "model_unknown";

    public const string RunFailed = "run_failed";

    public const string LedgerWriteFailed = "ledger_write_failed";

    /// <summary>The codes a started run clears.</summary>
    public static readonly string[] RunCodes = [ClaudeNotFound, ModelUnknown, RunFailed];

    /// <summary>The codes a successful poll clears.</summary>
    public static readonly string[] PollCodes = [PythonMissing, PollTimeout, PollFailed, PollOutputInvalid];
}

/// <summary>The JSON form of the status and settings: camelCase names, nulls written, times as UTC ISO-8601
/// (<c>2026-11-26T10:00:00.000Z</c>).</summary>
internal static class PrWatchJson
{
    public static readonly JsonSerializerOptions Options = Create(indented: false);

    public static readonly JsonSerializerOptions FileOptions = Create(indented: true);

    private static JsonSerializerOptions Create(bool indented)
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            WriteIndented = indented,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new UtcTimeConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private sealed class UtcTimeConverter : JsonConverter<DateTimeOffset>
    {
        private const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string? text = reader.GetString();
            if (text is null || !DateTimeOffset.TryParse(
                text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset value))
            {
                throw new JsonException("bad time");
            }

            return value.ToUniversalTime();
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture));
        }
    }
}

/// <summary>The checks on PR data from <c>job.py pr poll</c> and from <c>status.json</c>: names, numbers, shas,
/// URLs, request actions, text caps and <c>runs</c> keys.</summary>
internal static partial class PrRules
{
    public const int MaxNumber = 999_999_999;

    public const int MaxTitle = 200;

    public const int MaxAuthor = 100;

    public const int MaxPrs = 500;

    public static readonly string[] Requests = ["review", "merge", "cancel"];

    /// <summary>A project name that can be the first part of a <c>runs</c> key.</summary>
    public static bool IsProjectName(string? name)
    {
        return name is not null && ProjectPattern().IsMatch(name);
    }

    public static bool IsNumber(int number)
    {
        return number is >= 1 and <= MaxNumber;
    }

    /// <summary>40 lowercase hex characters.</summary>
    public static bool IsSha(string? sha)
    {
        return sha is not null && ShaPattern().IsMatch(sha);
    }

    /// <summary><c>null</c> or one of <see cref="Requests"/>.</summary>
    public static bool IsRequest(string? request)
    {
        return request is null || Requests.Contains(request, StringComparer.Ordinal);
    }

    /// <summary>True only for exactly <c>https://github.com/&lt;owner&gt;/&lt;repo&gt;/pull/&lt;number&gt;</c>.</summary>
    public static bool IsPullUrl(string? url, int number)
    {
        if (url is null)
        {
            return false;
        }

        Match match = UrlPattern().Match(url);
        return match.Success
            && string.Equals(match.Groups["n"].Value, number.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    /// <summary>The key of a PR in <c>runs</c>: <c>&lt;project&gt;-&lt;number&gt;</c>.</summary>
    public static string RunKey(string project, int number)
    {
        return project + "-" + number.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>True for a key <c>&lt;repo&gt;-&lt;n&gt;</c> with a plain repo name and n from 1 to 999999999.</summary>
    public static bool IsRunKey(string key)
    {
        return RunKeyPattern().IsMatch(key);
    }

    /// <summary>The project part of a valid run key (everything before the last <c>-</c>).</summary>
    public static string RunKeyProject(string key)
    {
        return key[..key.LastIndexOf('-')];
    }

    /// <summary>At most <paramref name="max"/> UTF-16 units, never ending in half a surrogate pair.</summary>
    public static string Cap(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        int length = char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
        return text[..length];
    }

    /// <summary>A stored PR that keeps every rule (used when <c>status.json</c> is loaded).</summary>
    public static bool IsValid(PrStatus pr)
    {
        return IsProjectName(pr.Project)
            && IsNumber(pr.Number)
            && pr.Title is not null && pr.Title.Length <= MaxTitle
            && pr.Author is not null && pr.Author.Length <= MaxAuthor
            && (pr.Url is null || IsPullUrl(pr.Url, pr.Number))
            && IsSha(pr.HeadSha)
            && IsRequest(pr.Request)
            && (pr.Round is null || IsSha(pr.Round.HeadSha));
    }

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex ProjectPattern();

    [GeneratedRegex(@"\A[0-9a-f]{40}\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex ShaPattern();

    [GeneratedRegex(
        @"\Ahttps://github\.com/[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9._-]{1,100}/pull/(?<n>[1-9][0-9]{0,8})\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}-[1-9][0-9]{0,8}\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex RunKeyPattern();
}

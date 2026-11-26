using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PRWatch;

/// <summary>PRWatch's current state (<c>status.json</c> in the data folder): the last poll, the last run per PR
/// (keyed <c>&lt;repo&gt;-&lt;n&gt;</c>), the brake, the Claude login and the last error.</summary>
internal sealed record Status
{
    public DateTimeOffset? LastPoll { get; init; }

    public ImmutableSortedDictionary<string, RunStatus> Runs { get; init; } =
        ImmutableSortedDictionary.Create<string, RunStatus>(StringComparer.Ordinal);

    public BrakeStatus Brake { get; init; } = new();

    public LoginStatus Login { get; init; } = new();

    public StatusError? LastError { get; init; }

    public static Status Initial { get; } = new();
}

/// <summary>One PR's last run: the head it reviewed, when it started and ended, the exit code and the
/// <c>result</c> of its stream-json output.</summary>
internal sealed record RunStatus
{
    public string? HeadSha { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? EndedAt { get; init; }

    public int? ExitCode { get; init; }

    public string? Result { get; init; }
}

/// <summary>Whether the token brake holds, why and until when.</summary>
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

    public string State { get; init; } = Unknown;

    public DateTimeOffset? CredentialExpiresAt { get; init; }
}

/// <summary>The last error: a fixed code and when it happened.</summary>
internal sealed record StatusError(string Code, DateTimeOffset At)
{
    public const string SettingsUnreadable = "settings_unreadable";

    public const string StatusWriteFailed = "status_write_failed";
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

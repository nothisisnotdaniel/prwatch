using System.Text.Json;
using System.Text.Json.Nodes;

namespace PRWatch;

/// <summary>The outcome of <see cref="SettingsStore.Change"/>: the saved settings, or the invalid fields, or a
/// failed save.</summary>
internal sealed record SettingsChange(Settings? Saved, IReadOnlyList<string> Fields, bool SaveFailed);

/// <summary>Loads and saves <c>prwatch-settings.json</c> in the data folder (never RoomKit's
/// <c>settings.json</c>). A missing file gives the defaults. A file that can't be read or breaks a rule is never
/// overwritten on load: the settings are the defaults (so <c>enabled</c> is off) and the status gets
/// <c>settings_unreadable</c>. Saves are atomic through <see cref="SafeFileWriter"/>.</summary>
internal sealed class SettingsStore
{
    public const string FileName = "prwatch-settings.json";

    public const long MaxFileBytes = 64 * 1024;

    private readonly object gate = new();

    private readonly Settings defaults;

    private Settings current;

    public SettingsStore(string folder, string? configuredFactoryRoot, StatusStore status)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(status);
        Path = System.IO.Path.Combine(folder, FileName);
        defaults = Settings.Defaults(configuredFactoryRoot);
        Settings? loaded = Load(Path, defaults, out bool unreadable);
        current = loaded ?? defaults;
        if (unreadable)
        {
            status.RecordError(StatusError.SettingsUnreadable);
        }
    }

    /// <summary>The full path of <c>prwatch-settings.json</c>.</summary>
    public string Path { get; }

    /// <summary>The current settings.</summary>
    public Settings Current
    {
        get
        {
            lock (gate)
            {
                return current;
            }
        }
    }

    /// <summary>Merges the object's fields into the current settings, checks them and saves them. Nothing is
    /// written when a field is invalid or the save fails.</summary>
    public SettingsChange Change(JsonElement body)
    {
        lock (gate)
        {
            Settings? merged = SettingsRules.Merge(current, body, out IReadOnlyList<string> fields);
            if (merged is null)
            {
                return new SettingsChange(null, fields, SaveFailed: false);
            }

            try
            {
                SafeFileWriter.Write(Path, JsonSerializer.SerializeToUtf8Bytes(merged, PrWatchJson.FileOptions));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new SettingsChange(null, [], SaveFailed: true);
            }

            current = merged;
            return new SettingsChange(merged, [], SaveFailed: false);
        }
    }

    /// <summary>The settings as the API returns them: every field plus the read-only <c>effectiveMode</c>,
    /// <c>effectiveModel</c> (that mode's <c>reviewer-code</c> model), <c>knownProjects</c> and
    /// <c>knownModes</c>.</summary>
    public static JsonObject View(Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        JsonObject view = (JsonObject)JsonSerializer.SerializeToNode(settings, PrWatchJson.Options)!;
        FactoryInfo info = FactoryInfo.Read(settings.FactoryRoot);
        string? mode = settings.ReviewerMode ?? info.DefaultMode;
        if (mode is not null && !info.Modes.Contains(mode, StringComparer.Ordinal))
        {
            mode = null;
        }

        string? model = mode is not null && info.ReviewerModels.TryGetValue(mode, out string? found) ? found : null;
        view["effectiveMode"] = mode;
        view["effectiveModel"] = model;
        view["knownProjects"] = new JsonArray([.. info.Projects.Select(name => (JsonNode?)JsonValue.Create(name))]);
        view["knownModes"] = new JsonArray([.. info.Modes.Select(name => (JsonNode?)JsonValue.Create(name))]);
        return view;
    }

    /// <summary>The settings in the file, <c>null</c> when it is missing (<paramref name="unreadable"/> false) or
    /// can't be used (<paramref name="unreadable"/> true).</summary>
    private static Settings? Load(string path, Settings defaults, out bool unreadable)
    {
        unreadable = false;
        FileInfo file = new(path);
        if (!file.Exists && file.LinkTarget is null)
        {
            return null;
        }

        unreadable = true;
        byte[]? bytes = SafeFileReader.ReadBytes(path, MaxFileBytes);
        if (bytes is null)
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes);
            Settings? loaded = SettingsRules.Merge(defaults, document.RootElement, out _);
            unreadable = loaded is null;
            return loaded;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

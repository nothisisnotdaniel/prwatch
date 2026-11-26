using System.Text.Json;

namespace PRWatch;

/// <summary>PRWatch's settings (<c>prwatch-settings.json</c> in the data folder). <c>null</c> in
/// <see cref="Projects"/> means every discovered project, in <see cref="ReviewerMode"/> the factory's
/// <c>default</c> mode.</summary>
internal sealed record Settings
{
    public bool Enabled { get; init; }

    public string? FactoryRoot { get; init; }

    public IReadOnlyList<string>? Projects { get; init; }

    public int PollMinutes { get; init; } = 5;

    public int QuietMinutes { get; init; } = 10;

    public bool AutoReviewTrusted { get; init; } = true;

    public bool AutoMergeTrusted { get; init; }

    public string? ReviewerMode { get; init; }

    public double MaxBudgetUsd { get; init; } = 2.0;

    public int BrakeFiveHourPercent { get; init; } = 60;

    public int BrakeSevenDayPercent { get; init; } = 80;

    public double BrakeFiveHourUsd { get; init; } = 5.0;

    public double BrakeSevenDayUsd { get; init; } = 25.0;

    public int BrakeRunsPerHour { get; init; } = 3;

    public string? ClaudePath { get; init; }

    /// <summary>The defaults, with the factory root from the config value <c>FactoryRoot</c> (or none).</summary>
    public static Settings Defaults(string? configuredFactoryRoot)
    {
        return new Settings { FactoryRoot = string.IsNullOrEmpty(configuredFactoryRoot) ? null : configuredFactoryRoot };
    }
}

/// <summary>What a valid factory root offers: the project names (<see cref="ProjectFinder"/> plus
/// <c>factory</c>), the mode names of <c>factory/modes.json</c>, its <c>default</c> mode and each mode's
/// <c>reviewer-code</c> model. <see cref="Empty"/> when the root is unset or invalid.</summary>
internal sealed record FactoryInfo(
    IReadOnlyList<string> Projects,
    IReadOnlyList<string> Modes,
    string? DefaultMode,
    IReadOnlyDictionary<string, string?> ReviewerModels)
{
    public const string FactoryProject = "factory";

    public const long MaxModesBytes = 256 * 1024;

    public static readonly FactoryInfo Empty = new([], [], null, new Dictionary<string, string?>());

    /// <summary>Reads the root when <see cref="SettingsRules.IsFactoryRoot"/> accepts it, else
    /// <see cref="Empty"/>. A <c>modes.json</c> that can't be read gives the projects and no modes.</summary>
    public static FactoryInfo Read(string? root)
    {
        if (root is null || !SettingsRules.IsFactoryRoot(root))
        {
            return Empty;
        }

        List<string> projects = [.. ProjectFinder.Find(root).Select(project => project.Name), FactoryProject];
        projects.Sort(StringComparer.OrdinalIgnoreCase);
        List<string> modes = [];
        Dictionary<string, string?> models = new(StringComparer.Ordinal);
        string? defaultMode = null;
        string? text = SafeFileReader.ReadText(Path.Combine(root, "factory", "modes.json"), MaxModesBytes);
        if (text is not null)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(text);
                JsonElement top = document.RootElement;
                if (top.ValueKind == JsonValueKind.Object
                    && top.TryGetProperty("modes", out JsonElement all)
                    && all.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty mode in EnumerateModes(all))
                    {
                        modes.Add(mode.Name);
                        models[mode.Name] = ReviewerModel(mode.Value);
                    }

                    if (top.TryGetProperty("default", out JsonElement value)
                        && value.ValueKind == JsonValueKind.String
                        && models.ContainsKey(value.GetString()!))
                    {
                        defaultMode = value.GetString();
                    }
                }
            }
            catch (JsonException)
            {
                modes.Clear();
                models.Clear();
            }
        }

        modes.Sort(StringComparer.Ordinal);
        return new FactoryInfo(projects, modes, defaultMode, models);
    }

    private static IEnumerable<JsonProperty> EnumerateModes(JsonElement all)
    {
        return all.EnumerateObject().Where(mode => mode.Value.ValueKind == JsonValueKind.Object);
    }

    private static string? ReviewerModel(JsonElement mode)
    {
        return mode.TryGetProperty("models", out JsonElement models)
            && models.ValueKind == JsonValueKind.Object
            && models.TryGetProperty("reviewer-code", out JsonElement model)
            && model.ValueKind == JsonValueKind.String
                ? model.GetString()
                : null;
    }
}

/// <summary>Reads a settings object onto current settings and checks the result. Every error is a field name
/// (an unknown key is the fixed name <see cref="UnknownKey"/>), never a submitted value.</summary>
internal static class SettingsRules
{
    /// <summary>The field name that stands for every unknown key, so a submitted key is never echoed.</summary>
    public const string UnknownKey = "_unknown";

    public static readonly string[] FieldNames =
    [
        "enabled", "factoryRoot", "projects", "pollMinutes", "quietMinutes", "autoReviewTrusted",
        "autoMergeTrusted", "reviewerMode", "maxBudgetUsd", "brakeFiveHourPercent", "brakeSevenDayPercent",
        "brakeFiveHourUsd", "brakeSevenDayUsd", "brakeRunsPerHour", "claudePath",
    ];

    /// <summary>Merges the object's fields into <paramref name="current"/> and checks the merged settings.
    /// Returns the merged settings, or <c>null</c> with the sorted field names in <paramref name="fields"/>.</summary>
    public static Settings? Merge(Settings current, JsonElement body, out IReadOnlyList<string> fields)
    {
        SortedSet<string> errors = new(StringComparer.Ordinal);
        if (body.ValueKind != JsonValueKind.Object)
        {
            fields = [];
            return null;
        }

        Settings merged = current;
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in body.EnumerateObject())
        {
            if (!FieldNames.Contains(property.Name, StringComparer.Ordinal))
            {
                errors.Add(UnknownKey);
                continue;
            }

            if (!seen.Add(property.Name))
            {
                errors.Add(property.Name);
                continue;
            }

            Settings? next = Apply(merged, property.Name, property.Value);
            if (next is null)
            {
                errors.Add(property.Name);
            }
            else
            {
                merged = next;
            }
        }

        foreach (string field in Check(merged))
        {
            errors.Add(field);
        }

        fields = [.. errors];
        return errors.Count == 0 ? merged : null;
    }

    /// <summary>The fields of <paramref name="settings"/> that break a rule (ranges, paths, known projects and
    /// modes, and <c>enabled</c> only with a factory root and a <c>claude</c> path).</summary>
    public static IReadOnlyList<string> Check(Settings settings)
    {
        List<string> errors = [];
        bool rootValid = settings.FactoryRoot is null || IsFactoryRoot(settings.FactoryRoot);
        if (!rootValid)
        {
            errors.Add("factoryRoot");
        }

        FactoryInfo info = rootValid ? FactoryInfo.Read(settings.FactoryRoot) : FactoryInfo.Empty;
        if (settings.Projects is not null
            && (settings.Projects.Count == 0
                || settings.Projects.Distinct(StringComparer.Ordinal).Count() != settings.Projects.Count
                || settings.Projects.Any(name => !info.Projects.Contains(name, StringComparer.Ordinal))))
        {
            errors.Add("projects");
        }

        if (settings.ReviewerMode is not null && !info.Modes.Contains(settings.ReviewerMode, StringComparer.Ordinal))
        {
            errors.Add("reviewerMode");
        }

        AddIfOutside(errors, "pollMinutes", settings.PollMinutes, 1, 60);
        AddIfOutside(errors, "quietMinutes", settings.QuietMinutes, 0, 120);
        AddIfOutside(errors, "maxBudgetUsd", settings.MaxBudgetUsd, 0.1, 20);
        AddIfOutside(errors, "brakeFiveHourPercent", settings.BrakeFiveHourPercent, 1, 100);
        AddIfOutside(errors, "brakeSevenDayPercent", settings.BrakeSevenDayPercent, 1, 100);
        AddIfOutside(errors, "brakeFiveHourUsd", settings.BrakeFiveHourUsd, 0.5, 500);
        AddIfOutside(errors, "brakeSevenDayUsd", settings.BrakeSevenDayUsd, 0.5, 500);
        AddIfOutside(errors, "brakeRunsPerHour", settings.BrakeRunsPerHour, 1, 10);
        if (settings.ClaudePath is not null && !IsExecutable(settings.ClaudePath))
        {
            errors.Add("claudePath");
        }

        if (settings.Enabled && (settings.FactoryRoot is null || settings.ClaudePath is null))
        {
            errors.Add("enabled");
        }

        return errors;
    }

    /// <summary>True for an absolute path without a <c>..</c> part that is a real folder (not a symlink) holding
    /// the regular files <c>factory/tools/job.py</c> and <c>factory/modes.json</c>.</summary>
    public static bool IsFactoryRoot(string path)
    {
        if (!IsPlainAbsolute(path))
        {
            return false;
        }

        try
        {
            DirectoryInfo root = new(path);
            return root.Exists
                && root.LinkTarget is null
                && IsRegularFile(Path.Combine(path, "factory", "tools", "job.py"))
                && IsRegularFile(Path.Combine(path, "factory", "modes.json"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>True for an absolute path without a <c>..</c> part whose final target is a regular file the
    /// user may execute. A symlink is followed (the installer links <c>~/.local/bin/claude</c> to a
    /// version).</summary>
    public static bool IsExecutable(string path)
    {
        return ResolveExecutable(path) is not null;
    }

    /// <summary>The full path of the final target of <paramref name="path"/> when <see cref="IsExecutable"/>
    /// accepts it (the path itself when it is no symlink), else <c>null</c>.</summary>
    public static string? ResolveExecutable(string path)
    {
        if (!IsPlainAbsolute(path))
        {
            return null;
        }

        try
        {
            FileInfo file = new(path);
            FileSystemInfo? target = file.LinkTarget is null ? file : file.ResolveLinkTarget(returnFinalTarget: true);
            if (target is not FileInfo regular || !regular.Exists || regular.LinkTarget is not null)
            {
                return null;
            }

            if (!OperatingSystem.IsWindows()
                && (File.GetUnixFileMode(regular.FullName) & UnixFileMode.UserExecute) == 0)
            {
                return null;
            }

            return regular.FullName;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsPlainAbsolute(string path)
    {
        return path.Length > 0
            && path.Length <= 1024
            && !path.Contains('\0', StringComparison.Ordinal)
            && Path.IsPathFullyQualified(path)
            && !path.Split(['/', '\\']).Contains("..", StringComparer.Ordinal);
    }

    private static bool IsRegularFile(string path)
    {
        FileInfo file = new(path);
        return file.Exists && file.LinkTarget is null;
    }

    private static void AddIfOutside(List<string> errors, string field, double value, double min, double max)
    {
        if (double.IsNaN(value) || value < min || value > max)
        {
            errors.Add(field);
        }
    }

    /// <summary>The settings with one field set from JSON, or <c>null</c> for a wrong JSON type.</summary>
    private static Settings? Apply(Settings settings, string name, JsonElement value)
    {
        return name switch
        {
            "enabled" => ReadBool(value) is bool on ? settings with { Enabled = on } : null,
            "factoryRoot" => ReadText(value, out string? root) ? settings with { FactoryRoot = root } : null,
            "projects" => ReadNames(value, out IReadOnlyList<string>? names) ? settings with { Projects = names } : null,
            "pollMinutes" => ReadInt(value) is int poll ? settings with { PollMinutes = poll } : null,
            "quietMinutes" => ReadInt(value) is int quiet ? settings with { QuietMinutes = quiet } : null,
            "autoReviewTrusted" => ReadBool(value) is bool review ? settings with { AutoReviewTrusted = review } : null,
            "autoMergeTrusted" => ReadBool(value) is bool merge ? settings with { AutoMergeTrusted = merge } : null,
            "reviewerMode" => ReadText(value, out string? mode) ? settings with { ReviewerMode = mode } : null,
            "maxBudgetUsd" => ReadDouble(value) is double budget ? settings with { MaxBudgetUsd = budget } : null,
            "brakeFiveHourPercent" => ReadInt(value) is int five
                ? settings with { BrakeFiveHourPercent = five } : null,
            "brakeSevenDayPercent" => ReadInt(value) is int seven
                ? settings with { BrakeSevenDayPercent = seven } : null,
            "brakeFiveHourUsd" => ReadDouble(value) is double fiveUsd
                ? settings with { BrakeFiveHourUsd = fiveUsd } : null,
            "brakeSevenDayUsd" => ReadDouble(value) is double sevenUsd
                ? settings with { BrakeSevenDayUsd = sevenUsd } : null,
            "brakeRunsPerHour" => ReadInt(value) is int runs ? settings with { BrakeRunsPerHour = runs } : null,
            "claudePath" => ReadText(value, out string? claude) ? settings with { ClaudePath = claude } : null,
            _ => null,
        };
    }

    private static bool? ReadBool(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static int? ReadInt(JsonElement value)
    {
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : null;
    }

    private static double? ReadDouble(JsonElement value)
    {
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number)
            && double.IsFinite(number)
                ? number
                : null;
    }

    /// <summary>A string or <c>null</c>; an empty string is a wrong value too.</summary>
    private static bool ReadText(JsonElement value, out string? text)
    {
        text = null;
        if (value.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } read)
        {
            return false;
        }

        text = read;
        return true;
    }

    /// <summary>An array of strings or <c>null</c>.</summary>
    private static bool ReadNames(JsonElement value, out IReadOnlyList<string>? names)
    {
        names = null;
        if (value.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        List<string> list = [];
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            list.Add(item.GetString()!);
        }

        names = list;
        return true;
    }
}

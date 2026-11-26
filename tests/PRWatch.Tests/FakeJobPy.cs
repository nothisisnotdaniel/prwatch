using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PRWatch.Tests;

/// <summary>Writes a fake <c>factory/tools/job.py</c> into a test factory root. Run as <c>job.py pr poll</c>, it
/// appends one JSON line (its argv, cwd, whether the cwd is the root, its environment, its pid and what it read from
/// stdin) to a calls file outside the root (the path is baked into the script), writes a marker to stderr, sleeps
/// for the seconds in <c>poll-sleep</c> if that file exists, prints <c>poll-output.json</c> and exits with the code
/// in <c>poll-exit</c> (0 without it). Those files sit next to the script; the script itself writes nothing in the
/// root.</summary>
internal static class FakeJobPy
{
    public const string StderrMarker = "FAKE-STDERR-MARKER gh pr list failed: token xyz";

    public static readonly string EmptyOutput = "{\"prs\":[],\"skipped\":[]}";

    /// <summary>Writes the script (and an empty poll output) and returns the tools folder.</summary>
    public static string Write(string root, string callsFile)
    {
        string tools = Path.Combine(root, "factory", "tools");
        string script = $$"""
            import json, os, sys, time
            HERE = os.path.dirname(os.path.abspath(__file__))
            ROOT = os.path.dirname(os.path.dirname(HERE))
            CALLS = {{JsonSerializer.Serialize(callsFile)}}
            line = {"argv": sys.argv, "cwd": os.getcwd(),
                    "cwdIsRoot": os.path.realpath(os.getcwd()) == os.path.realpath(ROOT),
                    "env": dict(os.environ), "pid": os.getpid(), "stdin": sys.stdin.read()}
            with open(CALLS, "a", encoding="utf-8") as f:
                f.write(json.dumps(line) + "\n")
            sys.stderr.write({{JsonSerializer.Serialize(StderrMarker)}} * 2000)
            sys.stderr.flush()
            def read(name):
                path = os.path.join(HERE, name)
                if not os.path.exists(path):
                    return None
                with open(path, "rb") as f:
                    return f.read()
            pause = read("poll-sleep")
            if pause is not None:
                time.sleep(float(pause))
            out = read("poll-output.json")
            if out is not None:
                sys.stdout.buffer.write(out)
                sys.stdout.flush()
            code = read("poll-exit")
            sys.exit(int(code) if code is not None else 0)

            """;
        File.WriteAllText(Path.Combine(tools, "job.py"), script.Replace("\r\n", "\n", StringComparison.Ordinal));
        SetOutput(root, EmptyOutput);
        return tools;
    }

    public static void SetOutput(string root, string output)
    {
        File.WriteAllText(Path.Combine(root, "factory", "tools", "poll-output.json"), output, new UTF8Encoding(false));
    }

    public static void SetExit(string root, int code)
    {
        File.WriteAllText(Path.Combine(root, "factory", "tools", "poll-exit"), code.ToString(CultureInfo.InvariantCulture));
    }

    public static void SetSleep(string root, int seconds)
    {
        File.WriteAllText(Path.Combine(root, "factory", "tools", "poll-sleep"), seconds.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The recorded calls, one JSON document each (none when the file is missing).</summary>
    public static IReadOnlyList<JsonElement> Calls(string callsFile)
    {
        if (!File.Exists(callsFile))
        {
            return [];
        }

        List<JsonElement> calls = [];
        foreach (string line in File.ReadAllLines(callsFile))
        {
            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                calls.Add(document.RootElement.Clone());
            }
            catch (JsonException)
            {
                // A line still being written.
            }
        }

        return calls;
    }
}

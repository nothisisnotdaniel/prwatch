using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PRWatch.Tests;

/// <summary>Writes a fake <c>claude</c>: an executable <c>python3</c> script (<c>0700</c>, the first
/// <c>python3</c> on <see cref="PollRunner.FixedPath"/> in its shebang) in a folder of the test's own, never on the
/// real <c>PATH</c>. Run, it appends one JSON line (its argv, cwd, full environment, pid and what it read from
/// stdin) to a calls file outside the factory root (before that, as soon as it runs, it writes its pid to
/// <c>started-pid</c> next to the script, atomically, so a test can see a process that has not read stdin yet), writes <c>stderr.txt</c> to stderr, prints
/// <c>stream.jsonl</c> line by line and exits with the code in <c>exit-code</c> (0 without it). Those files sit next
/// to the script. In the stream, the line <c>#wait</c> blocks until a <c>release</c> file appears next to the
/// script (at most 120 s), and <c>#flood</c> prints junk lines until the process is killed.</summary>
internal static class FakeClaude
{
    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Writes <c>&lt;folder&gt;/claude</c> and returns its full path.</summary>
    public static string Write(string folder, string callsFile)
    {
        Directory.CreateDirectory(folder);
        string python = PollRunner.FindPython() ?? throw new InvalidOperationException("no python3");
        string script = $$"""
            #!{{python}}
            import json, os, sys, time
            HERE = os.path.dirname(os.path.abspath(__file__))
            CALLS = {{JsonSerializer.Serialize(callsFile)}}
            def path(name):
                return os.path.join(HERE, name)
            with open(path("started-pid.tmp"), "w", encoding="utf-8") as f:
                f.write(str(os.getpid()))
            os.replace(path("started-pid.tmp"), path("started-pid"))
            line = {"argv": sys.argv, "cwd": os.getcwd(), "env": dict(os.environ), "pid": os.getpid(),
                    "stdin": sys.stdin.read()}
            with open(CALLS, "a", encoding="utf-8") as f:
                f.write(json.dumps(line) + "\n")
            if os.path.exists(path("stderr.txt")):
                with open(path("stderr.txt"), "rb") as f:
                    sys.stderr.buffer.write(f.read())
                sys.stderr.flush()
            out = sys.stdout.buffer
            if os.path.exists(path("stream.jsonl")):
                with open(path("stream.jsonl"), "rb") as f:
                    lines = f.read().split(b"\n")
                for raw in lines:
                    if raw == b"#wait":
                        deadline = time.time() + 120
                        while not os.path.exists(path("release")) and time.time() < deadline:
                            time.sleep(0.02)
                    elif raw == b"#flood":
                        chunk = b"y" * 65535 + b"\n"
                        while True:
                            out.write(chunk)
                    elif raw:
                        out.write(raw + b"\n")
                        out.flush()
            code = 0
            if os.path.exists(path("exit-code")):
                with open(path("exit-code"), encoding="utf-8") as f:
                    code = int(f.read())
            sys.exit(code)

            """;
        string file = Path.Combine(folder, "claude");
        File.WriteAllText(file, script.Replace("\r\n", "\n", StringComparison.Ordinal), new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(file, Private);
        }

        return file;
    }

    /// <summary>Sets the lines the fake prints (each a JSON text or a directive).</summary>
    public static void SetStream(string claude, params string[] lines)
    {
        File.WriteAllText(Next(claude, "stream.jsonl"), string.Join("\n", lines) + "\n", new UTF8Encoding(false));
    }

    public static void SetStderr(string claude, string text)
    {
        File.WriteAllText(Next(claude, "stderr.txt"), text, new UTF8Encoding(false));
    }

    public static void SetExit(string claude, int code)
    {
        File.WriteAllText(Next(claude, "exit-code"), code.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Lets a fake blocked on <c>#wait</c> go on.</summary>
    public static void Release(string claude)
    {
        File.WriteAllText(Next(claude, "release"), "");
    }

    /// <summary>The pid in <c>started-pid</c> (written by the fake as soon as it runs), or <c>null</c> before
    /// that.</summary>
    public static int? StartedPid(string claude)
    {
        string file = Next(claude, "started-pid");
        return File.Exists(file) && int.TryParse(File.ReadAllText(file), NumberStyles.None, CultureInfo.InvariantCulture,
            out int pid)
                ? pid
                : null;
    }

    private static string Next(string claude, string name)
    {
        return Path.Combine(Path.GetDirectoryName(claude)!, name);
    }
}

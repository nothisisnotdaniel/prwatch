using System.Security.Cryptography;
using System.Text;

namespace PRWatch.Tests;

/// <summary>A temporary factory root inside a test's own folder: <c>factory/tools/job.py</c> (a stub that is never
/// run), <c>factory/modes.json</c> (default <c>quality</c>, modes <c>quality</c> and <c>cheap</c>) and the
/// projects <c>Alpha</c> and <c>Beta</c> (each a folder with a <c>CLAUDE.md</c>), plus a folder without one.</summary>
internal static class TestFactoryRoot
{
    public const string ModesJson = """
        {
          "default": "quality",
          "modes": {
            "quality": { "description": "q", "models": { "builder": "opus", "reviewer-code": "sonnet" } },
            "cheap": { "description": "c", "models": { "builder": "sonnet", "reviewer-code": "haiku" } }
          }
        }
        """;

    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Builds the root at <c>&lt;parent&gt;/root</c> and returns its full path.</summary>
    public static string Create(string parent)
    {
        string root = Path.Combine(parent, "root");
        Directory.CreateDirectory(Path.Combine(root, "factory", "tools"));
        File.WriteAllText(Path.Combine(root, "factory", "tools", "job.py"), "raise SystemExit(99)\n");
        File.WriteAllText(Path.Combine(root, "factory", "modes.json"), ModesJson);
        foreach (string project in new[] { "Alpha", "Beta" })
        {
            Directory.CreateDirectory(Path.Combine(root, project));
            File.WriteAllText(Path.Combine(root, project, "CLAUDE.md"), "# " + project + "\n");
        }

        Directory.CreateDirectory(Path.Combine(root, "NotAProject"));
        return root;
    }

    /// <summary>A fake <c>claude</c> script (<c>0700</c>, never run) at <c>&lt;parent&gt;/bin/claude</c>.</summary>
    public static string FakeClaude(string parent)
    {
        string bin = Path.Combine(parent, "bin");
        Directory.CreateDirectory(bin);
        string path = Path.Combine(bin, "claude");
        File.WriteAllText(path, "#!/bin/sh\nexit 99\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, Private);
        }

        return path;
    }

    /// <summary>A hash of every entry's relative path and every file's bytes under <paramref name="root"/>, in
    /// ordinal order.</summary>
    public static string Hash(string root)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        string[] files = [.. Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)];
        foreach (string file in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(file + "\n"));
            string full = Path.Combine(root, file);
            if (File.Exists(full))
            {
                hash.AppendData(File.ReadAllBytes(full));
            }
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}

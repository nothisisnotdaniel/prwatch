using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace PRWatch.Tests;

/// <summary>Runs <c>.github/scripts/compare-release.py</c> on pairs of tarballs built with
/// <see cref="TarWriter"/> in the test's own temp folder, with only <c>PATH=/usr/bin:/bin</c> in its environment.</summary>
public sealed class CompareReleaseTests : IDisposable
{
    private const string ScriptPath = ".github/scripts/compare-release.py";
    private const string Top = "prwatch-0.1.0-osx-arm64";
    private const string Endpoints = Top + "/PRWatch.staticwebassets.endpoints.json";
    private const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private const UnixFileMode ExecMode = FileMode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
    private static readonly TimeSpan ProcessLimit = TimeSpan.FromSeconds(10);

    private readonly string folder = Directory.CreateTempSubdirectory("prwatch-compare-").FullName;

    public void Dispose()
    {
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public void IdenticalPair_Passes()
    {
        (int code, _, string error) = Compare(Base(), Base());

        Assert.Equal(0, code);
        Assert.Equal("", error);
    }

    [Fact]
    public void EndpointsDifferingOnlyInLastModified_Pass()
    {
        List<Member> other = Base();
        Replace(other, Endpoints, EndpointsJson("Fri, 21 Nov 2026 09:15:42 GMT", "Sat, 22 Nov 2026 23:59:59 GMT"));

        (int code, _, string error) = Compare(Base(), other);

        Assert.Equal(0, code);
        Assert.Equal("", error);
    }

    [Theory]
    [InlineData("missing", "member missing from downloaded: \"" + Top + "/wwwroot/index.html\"")]
    [InlineData("extra", "extra member in downloaded: \"" + Top + "/extra.txt\"")]
    [InlineData("bytes", "file bytes differ: \"" + Top + "/PRWatch\"")]
    [InlineData("exec", "exec bit differs: \"" + Top + "/PRWatch\"")]
    [InlineData("type", "member type differs: \"" + Top + "/wwwroot/index.html\"")]
    [InlineData("symlink", "downloaded has a symlink: \"" + Top + "/link\"")]
    [InlineData("hardlink", "downloaded has a hard link or device: \"" + Top + "/hard\"")]
    [InlineData("device", "downloaded has a hard link or device: \"" + Top + "/dev\"")]
    [InlineData("fifo", "downloaded has a hard link or device: \"" + Top + "/fifo\"")]
    [InlineData("absolute", "downloaded has a member with an absolute name: \"/" + Top + "/x\"")]
    [InlineData("dotdot", "downloaded has a member with a .. component: \"" + Top + "/../x\"")]
    [InlineData("dot", "downloaded has a member with an empty or . component: \"" + Top + "/./x\"")]
    [InlineData("outside", "downloaded has a member outside the top folder: \"other/x\"")]
    [InlineData("duplicate", "downloaded has a duplicate member: \"" + Top + "/PRWatch\"")]
    [InlineData("endpoints", "endpoints file differs outside Last-Modified: \"" + Endpoints + "\"")]
    [InlineData("malformed", "downloaded has a malformed Last-Modified value in: \"" + Endpoints + "\"")]
    [InlineData("malformed-type", "downloaded has a malformed Last-Modified value in: \"" + Endpoints + "\"")]
    [InlineData("not-json", "downloaded has an endpoints file that is not valid JSON: \"" + Endpoints + "\"")]
    [InlineData("duplicate-key", "downloaded has an endpoints file that is not valid JSON: \"" + Endpoints + "\"")]
    [InlineData("built-symlink", "built has a symlink: \"" + Top + "/link\"")]
    [InlineData("built-malformed", "built has a malformed Last-Modified value in: \"" + Endpoints + "\"")]
    [InlineData("control-name", "extra member in downloaded: \"" + Top + "/a\\nb\"")]
    public void AnyDifferenceOrUnsafeMember_FailsWithOneLineNamingTheMember(string scene, string line)
    {
        List<Member> built = Base();
        List<Member> downloaded = Base();
        switch (scene)
        {
            case "missing":
                downloaded.RemoveAll(member => member.Name == Top + "/wwwroot/index.html");
                break;
            case "extra":
                downloaded.Add(File(Top + "/extra.txt", "x"));
                break;
            case "bytes":
                Replace(downloaded, Top + "/PRWatch", "app!\n");
                break;
            case "exec":
                int at = downloaded.FindIndex(member => member.Name == Top + "/PRWatch");
                downloaded[at] = downloaded[at] with { Mode = FileMode };
                break;
            case "type":
                downloaded.RemoveAll(member => member.Name == Top + "/wwwroot/index.html");
                downloaded.Add(Folder(Top + "/wwwroot/index.html"));
                break;
            case "symlink":
                downloaded.Add(new Member(Top + "/link", TarEntryType.SymbolicLink, FileMode, "", "PRWatch"));
                break;
            case "built-symlink":
                built.Add(new Member(Top + "/link", TarEntryType.SymbolicLink, FileMode, "", "PRWatch"));
                break;
            case "hardlink":
                downloaded.Add(new Member(Top + "/hard", TarEntryType.HardLink, FileMode, "", Top + "/PRWatch"));
                break;
            case "device":
                downloaded.Add(new Member(Top + "/dev", TarEntryType.CharacterDevice, FileMode, "", ""));
                break;
            case "fifo":
                downloaded.Add(new Member(Top + "/fifo", TarEntryType.Fifo, FileMode, "", ""));
                break;
            case "absolute":
                downloaded.Add(File("/" + Top + "/x", "x"));
                break;
            case "dotdot":
                downloaded.Add(File(Top + "/../x", "x"));
                break;
            case "dot":
                downloaded.Add(File(Top + "/./x", "x"));
                break;
            case "outside":
                downloaded.Add(File("other/x", "x"));
                break;
            case "duplicate":
                downloaded.Add(File(Top + "/PRWatch", "app\n", ExecMode));
                break;
            case "endpoints":
                Replace(downloaded, Endpoints, EndpointsJson("Thu, 20 Nov 2026 10:00:00 GMT", "Thu, 20 Nov 2026 10:00:01 GMT").Replace("\"ETag\"", "\"Etag\"", StringComparison.Ordinal));
                break;
            case "malformed":
                Replace(downloaded, Endpoints, EndpointsJson("Thu, 20 Nov 2026 10:00:00 GMT", "yesterday"));
                break;
            case "malformed-type":
                Replace(downloaded, Endpoints, EndpointsJson("Thu, 20 Nov 2026 10:00:00 GMT", "Thu, 20 Nov 2026 10:00:00 GMT").Replace("\"Value\": \"Thu, 20 Nov 2026 10:00:00 GMT\" }", "\"Value\": 1 }", StringComparison.Ordinal));
                break;
            case "built-malformed":
                Replace(built, Endpoints, EndpointsJson("Thu, 20 Nov 2026 10:00:00 GMT", "Thu, 20 Nov 2026 10:00:00 GMT\n"));
                break;
            case "not-json":
                Replace(downloaded, Endpoints, "{");
                break;
            case "duplicate-key":
                Replace(downloaded, Endpoints, "{ \"Version\": 1, \"Version\": 1 }");
                break;
            case "control-name":
                downloaded.Add(File(Top + "/a\nb", "x"));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scene));
        }

        (int code, string output, string error) = Compare(built, downloaded);

        Assert.Equal(1, code);
        Assert.Equal("", output);
        Assert.Equal(line + "\n", error);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("plain-tar")]
    [InlineData("truncated")]
    [InlineData("absent")]
    public void UnreadableArchive_Fails(string scene)
    {
        string built = WriteArchive("built.tar.gz", Base());
        string downloaded = Path.Combine(folder, "downloaded.tar.gz");
        byte[] good = System.IO.File.ReadAllBytes(built);
        switch (scene)
        {
            case "garbage":
                System.IO.File.WriteAllText(downloaded, "not an archive");
                break;
            case "plain-tar":
                using (FileStream stream = System.IO.File.Create(downloaded))
                {
                    TarFile.CreateFromDirectory(Directory.CreateDirectory(Path.Combine(folder, "plain")).FullName, stream, includeBaseDirectory: false);
                }

                break;
            case "truncated":
                System.IO.File.WriteAllBytes(downloaded, good[..(good.Length / 2)]);
                break;
            default:
                break;
        }

        (int code, _, string error) = Run([built, downloaded]);

        Assert.Equal(1, code);
        Assert.Single(error.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith("cannot read", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void WrongArgumentCount_Fails(int count)
    {
        string archive = WriteArchive("one.tar.gz", Base());

        (int code, _, string error) = Run([.. Enumerable.Repeat(archive, count)]);

        Assert.Equal(2, code);
        Assert.Equal("usage: compare-release.py <built.tar.gz> <downloaded.tar.gz>\n", error);
    }

    [Fact]
    public void Run_ExtractsNothing()
    {
        List<Member> downloaded = Base();
        downloaded.Add(File(Top + "/extra.txt", "x"));
        string built = WriteArchive("built.tar.gz", Base());
        string other = WriteArchive("downloaded.tar.gz", downloaded);
        string work = Directory.CreateDirectory(Path.Combine(folder, "work")).FullName;

        (int same, _, _) = Run([built, built], work);
        (int differs, _, _) = Run([built, other], work);

        Assert.Equal(0, same);
        Assert.Equal(1, differs);
        Assert.Empty(Directory.EnumerateFileSystemEntries(work));
        Assert.Equal(
            ["built.tar.gz", "downloaded.tar.gz", "work"],
            Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    private sealed record Member(string Name, TarEntryType Type, UnixFileMode Mode, string Content, string Link);

    private static Member File(string name, string content, UnixFileMode mode = FileMode)
    {
        return new Member(name, TarEntryType.RegularFile, mode, content, "");
    }

    private static Member Folder(string name)
    {
        return new Member(name, TarEntryType.Directory, ExecMode, "", "");
    }

    private static List<Member> Base()
    {
        return
        [
            Folder(Top),
            File(Top + "/PRWatch", "app\n", ExecMode),
            Folder(Top + "/wwwroot"),
            File(Top + "/wwwroot/index.html", "<!doctype html>\n"),
            File(Endpoints, EndpointsJson("Thu, 20 Nov 2026 10:00:00 GMT", "Thu, 20 Nov 2026 10:00:01 GMT")),
        ];
    }

    private static string EndpointsJson(string first, string second)
    {
        return "{\n  \"Version\": 1,\n  \"ManifestType\": \"Build\",\n  \"Endpoints\": [\n" +
            "    { \"Route\": \"index.html\", \"ResponseHeaders\": [ { \"Name\": \"ETag\", \"Value\": \"\\\"abc\\\"\" }, " +
            "{ \"Name\": \"Last-Modified\", \"Value\": \"" + first.Replace("\n", "\\n", StringComparison.Ordinal) + "\" } ] },\n" +
            "    { \"Route\": \"app.js\", \"ResponseHeaders\": [ { \"Name\": \"Last-Modified\", \"Value\": \"" +
            second.Replace("\n", "\\n", StringComparison.Ordinal) + "\" } ] }\n  ]\n}\n";
    }

    private static void Replace(List<Member> members, string name, string content)
    {
        int at = members.FindIndex(member => member.Name == name);
        members[at] = members[at] with { Content = content };
    }

    private string WriteArchive(string name, List<Member> members)
    {
        string path = Path.Combine(folder, name);
        using FileStream file = System.IO.File.Create(path);
        using GZipStream zip = new(file, CompressionLevel.Optimal);
        using TarWriter writer = new(zip, TarEntryFormat.Pax, leaveOpen: false);
        foreach (Member member in members)
        {
            PaxTarEntry entry = new(member.Type, member.Name) { Mode = member.Mode };
            if (member.Type is TarEntryType.SymbolicLink or TarEntryType.HardLink)
            {
                entry.LinkName = member.Link;
            }

            if (member.Type == TarEntryType.RegularFile)
            {
                entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes(member.Content));
            }

            writer.WriteEntry(entry);
        }

        return path;
    }

    private (int Code, string Stdout, string Stderr) Compare(List<Member> built, List<Member> downloaded)
    {
        return Run([WriteArchive("built.tar.gz", built), WriteArchive("downloaded.tar.gz", downloaded)]);
    }

    private (int Code, string Stdout, string Stderr) Run(string[] args, string? cwd = null)
    {
        string python = PollRunner.FindPython() ?? throw new InvalidOperationException("no python3");
        ProcessStartInfo start = new(python)
        {
            WorkingDirectory = cwd ?? folder,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-I");
        start.ArgumentList.Add(RepoFiles.PathOf(ScriptPath));
        foreach (string arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment.Clear();
        start.Environment["PATH"] = "/usr/bin:/bin";

        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        process.StandardInput.Close();
        if (!process.WaitForExit(ProcessLimit))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("compare-release.py did not exit within " + ProcessLimit.TotalSeconds + " s");
        }

        process.WaitForExit();
        return (process.ExitCode, stdout.Result, stderr.Result);
    }
}

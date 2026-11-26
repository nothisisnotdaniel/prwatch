namespace PRWatch.Tests;

/// <summary>Reads files of this repository (found upwards from the test output by <c>PRWatch.slnx</c>).</summary>
internal static class RepoFiles
{
    public static string Root { get; } = FindRoot();

    public static string PathOf(string relative)
    {
        return Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    public static string Read(string relative)
    {
        return File.ReadAllText(PathOf(relative)).ReplaceLineEndings("\n");
    }

    private static string FindRoot()
    {
        DirectoryInfo? folder = new(AppContext.BaseDirectory);
        while (folder is not null)
        {
            if (File.Exists(Path.Combine(folder.FullName, "PRWatch.slnx")))
            {
                return folder.FullName;
            }

            folder = folder.Parent;
        }

        throw new InvalidOperationException("PRWatch.slnx not found above the test output.");
    }
}

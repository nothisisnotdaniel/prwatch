namespace PRWatch;

/// <summary>Writes PRWatch's own files in the data folder with <see cref="SafeFileWriter"/>, as <c>0600</c>: a missing
/// file is first created empty with <c>0600</c> (<see cref="FileMode.CreateNew"/>, which fails on anything already
/// there, a symlink too), and <see cref="SafeFileWriter"/> keeps an existing file's mode.</summary>
internal static class PrivateFileWriter
{
    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static void Write(string path, byte[] bytes)
    {
        FileInfo target = new(path);
        if (!OperatingSystem.IsWindows() && !target.Exists && target.LinkTarget is null)
        {
            try
            {
                FileStreamOptions options = new()
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    UnixCreateMode = Private,
                };
                using FileStream created = new(path, options);
            }
            catch (IOException)
            {
                // Something appeared in between; SafeFileWriter decides whether it may be replaced.
            }
        }

        SafeFileWriter.Write(path, bytes);
    }
}

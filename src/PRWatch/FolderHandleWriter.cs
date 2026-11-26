using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace PRWatch;

/// <summary>Writes one file below a trusted root through folder descriptors only: every folder after the root is
/// opened with <c>O_NOFOLLOW | O_DIRECTORY</c> relative to its parent's descriptor, and the temp file, the write
/// and the rename all happen relative to the last one, so swapping any path component for a symlink after it was
/// opened can't redirect the write. macOS and Linux glibc x64/arm64 only (the targets of RoomKit's
/// <c>SafeFileReader</c>); see <see cref="IsSupported"/>.</summary>
internal static class FolderHandleWriter
{
    /// <summary><c>0700</c>.</summary>
    private const int PrivateFolderMode = 448;

    /// <summary><c>0600</c>.</summary>
    private const int PrivateFileMode = 384;

    // st_mode file type bits (the same on macOS and Linux).
    private const int TypeMask = 0xF000; // S_IFMT 0170000

    private const int TypeRegular = 0x8000; // S_IFREG 0100000

    // errno values (the same on macOS and Linux).
    private const int Enoent = 2;

    private const int Eintr = 4;

    private const int Eexist = 17;

    /// <summary>The platform's flag values and <c>struct stat</c> layout.</summary>
    private static readonly Layout Flags = CreateLayout();

    /// <summary>True on macOS (x64, arm64) and on Linux glibc x64/arm64.</summary>
    public static bool IsSupported => Flags.Supported;

    /// <summary>Writes <paramref name="bytes"/> to <c>&lt;root&gt;/&lt;folders…&gt;/&lt;fileName&gt;</c> as a new
    /// <c>0600</c> file (temp file <c>.&lt;fileName&gt;.&lt;32 hex&gt;.tmp</c>, <c>fsync</c>, <c>renameat</c>).
    /// <paramref name="openFolders"/> are opened as they are (no-follow); <paramref name="makeFolders"/> follow them
    /// and are created as <c>0700</c> when missing. False when a folder is a symlink or not a folder, when the
    /// file is a symlink or anything but a regular file, or when a call fails. <paramref name="step"/> is called with
    /// <c>"folders-open"</c> once every folder is open and with <c>"before-rename"</c> once the temp file is
    /// written and closed; an exception it throws is a failure like any other.</summary>
    public static bool Write(string root, string[] openFolders, string[] makeFolders, string fileName, byte[] bytes,
        Action<string>? step)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(openFolders);
        ArgumentNullException.ThrowIfNull(makeFolders);
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(bytes);
        if (!Flags.Supported)
        {
            return false;
        }

        List<int> descriptors = [];
        string? temp = null;
        int folder = -1;
        try
        {
            folder = Open(root, Flags.ReadOnly | Flags.Directory | Flags.CloseOnExec, 0);
            if (folder < 0)
            {
                return false;
            }

            descriptors.Add(folder);
            foreach (string name in openFolders)
            {
                folder = OpenFolder(folder, name);
                if (folder < 0)
                {
                    return false;
                }

                descriptors.Add(folder);
            }

            foreach (string name in makeFolders)
            {
                if (MkdirAt(folder, name, PrivateFolderMode) != 0 && Marshal.GetLastPInvokeError() != Eexist)
                {
                    return false;
                }

                folder = OpenFolder(folder, name);
                if (folder < 0)
                {
                    return false;
                }

                descriptors.Add(folder);
            }

            step?.Invoke("folders-open");
            if (!IsMissingOrRegular(folder, fileName))
            {
                return false;
            }

            // The temp name is only random hex, never taken from a request.
            string name32 = RandomNumberGenerator.GetHexString(32, lowercase: true);
            string tempName = "." + fileName + "." + name32 + ".tmp";
            int file = CreateAt(folder, tempName,
                Flags.WriteOnly | Flags.Create | Flags.Exclusive | Flags.NoFollow | Flags.CloseOnExec);
            if (file < 0)
            {
                return false;
            }

            temp = tempName;
            bool written;
            try
            {
                // fchmod makes the mode 0600 whatever the umask; the file is new, so nothing else had it open.
                written = Fchmod(file, PrivateFileMode) == 0 && WriteAll(file, bytes) && Fsync(file) == 0;
            }
            finally
            {
                if (Close(file) != 0)
                {
                    written = false;
                }
            }

            if (!written)
            {
                return false;
            }

            step?.Invoke("before-rename");

            // renameat replaces whatever entry is at fileName, a symlink included, without following it.
            if (RenameAt(folder, tempName, folder, fileName) != 0)
            {
                return false;
            }

            temp = null;
            return true;
        }
        finally
        {
            if (temp is not null)
            {
                _ = UnlinkAt(folder, temp, 0);
            }

            foreach (int descriptor in descriptors)
            {
                _ = Close(descriptor);
            }
        }
    }

    private static int OpenFolder(int parent, string name)
    {
        return OpenAt(parent, name, Flags.ReadOnly | Flags.Directory | Flags.NoFollow | Flags.CloseOnExec, 0);
    }

    /// <summary><c>openat</c> with <c>O_CREAT</c> and the mode <c>0600</c>. The mode is a variadic argument: on
    /// Apple arm64 a variadic argument goes on the stack, not in a register, so five unused register arguments fill
    /// x3 to x7 and the mode lands in the first stack slot; elsewhere it is passed like a fixed argument.</summary>
    private static int CreateAt(int folder, string name, int flags)
    {
        return Flags.VariadicOnStack
            ? OpenAtStackMode(folder, name, flags, 0, 0, 0, 0, 0, PrivateFileMode)
            : OpenAt(folder, name, flags, PrivateFileMode);
    }

    /// <summary>True when nothing is at <paramref name="name"/> in <paramref name="folder"/> or a regular file is
    /// there (never followed: a symlink, a folder or anything else is false).</summary>
    private static bool IsMissingOrRegular(int folder, string name)
    {
        byte[] buffer = new byte[Flags.StatSize];
        if (Stat(folder, name, buffer) != 0)
        {
            return Marshal.GetLastPInvokeError() == Enoent;
        }

        int mode = Flags.ModeWidth == 2
            ? BitConverter.ToUInt16(buffer, Flags.ModeOffset)
            : (int)BitConverter.ToUInt32(buffer, Flags.ModeOffset);
        return (mode & TypeMask) == TypeRegular;
    }

    private static int Stat(int folder, string name, byte[] buffer)
    {
        return Flags.MacInode64Symbol
            ? FstatatInode64(folder, name, buffer, Flags.SymlinkNoFollow)
            : Fstatat(folder, name, buffer, Flags.SymlinkNoFollow);
    }

    private static bool WriteAll(int file, byte[] bytes)
    {
        byte[] rest = bytes;
        while (rest.Length > 0)
        {
            nint count = WriteBytes(file, rest, rest.Length);
            if (count < 0)
            {
                if (Marshal.GetLastPInvokeError() == Eintr)
                {
                    continue;
                }

                return false;
            }

            if (count == 0)
            {
                return false;
            }

            rest = rest[(int)count..];
        }

        return true;
    }

    private static Layout CreateLayout()
    {
        Architecture architecture = RuntimeInformation.OSArchitecture;
        if (OperatingSystem.IsMacOS() && architecture is Architecture.X64 or Architecture.Arm64)
        {
            // <sys/fcntl.h>: O_RDONLY 0, O_WRONLY 0x1, O_CREAT 0x200, O_EXCL 0x800, O_NOFOLLOW 0x100,
            // O_DIRECTORY 0x100000, O_CLOEXEC 0x1000000; AT_SYMLINK_NOFOLLOW 0x20.
            // struct stat (64-bit inode, the only layout on arm64 and the fstatat$INODE64 symbol on x64):
            // st_dev int32 at 0, st_mode uint16 at 4, ..., 144 bytes in all.
            return new Layout(true, ReadOnly: 0, WriteOnly: 0x1, Create: 0x200, Exclusive: 0x800, NoFollow: 0x100,
                Directory: 0x100000, CloseOnExec: 0x1000000, SymlinkNoFollow: 0x20, StatSize: 144, ModeOffset: 4,
                ModeWidth: 2, MacInode64Symbol: architecture == Architecture.X64,
                VariadicOnStack: architecture == Architecture.Arm64);
        }

        if (OperatingSystem.IsLinux() && !RuntimeInformation.RuntimeIdentifier.Contains("musl", StringComparison.Ordinal))
        {
            if (architecture == Architecture.X64)
            {
                // x86_64 <asm/fcntl.h>: O_RDONLY 0, O_WRONLY 01, O_CREAT 0100, O_EXCL 0200, O_DIRECTORY 0200000,
                // O_NOFOLLOW 0400000, O_CLOEXEC 02000000; AT_SYMLINK_NOFOLLOW 0x100.
                // struct stat (x86_64): st_dev u64 at 0, st_ino u64 at 8, st_nlink u64 at 16, st_mode u32 at 24,
                // ..., 144 bytes in all.
                return new Layout(true, ReadOnly: 0, WriteOnly: 0x1, Create: 0x40, Exclusive: 0x80, NoFollow: 0x20000,
                    Directory: 0x10000, CloseOnExec: 0x80000, SymlinkNoFollow: 0x100, StatSize: 144, ModeOffset: 24,
                    ModeWidth: 4, MacInode64Symbol: false, VariadicOnStack: false);
            }

            if (architecture == Architecture.Arm64)
            {
                // aarch64 <asm/fcntl.h>: as x86_64 except O_DIRECTORY 040000 and O_NOFOLLOW 0100000.
                // struct stat (asm-generic): st_dev u64 at 0, st_ino u64 at 8, st_mode u32 at 16, ..., 128 bytes.
                return new Layout(true, ReadOnly: 0, WriteOnly: 0x1, Create: 0x40, Exclusive: 0x80, NoFollow: 0x8000,
                    Directory: 0x4000, CloseOnExec: 0x80000, SymlinkNoFollow: 0x100, StatSize: 128, ModeOffset: 16,
                    ModeWidth: 4, MacInode64Symbol: false, VariadicOnStack: false);
            }
        }

        return new Layout(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, false);
    }

    private sealed record Layout(bool Supported, int ReadOnly, int WriteOnly, int Create, int Exclusive, int NoFollow,
        int Directory, int CloseOnExec, int SymlinkNoFollow, int StatSize, int ModeOffset, int ModeWidth,
        bool MacInode64Symbol, bool VariadicOnStack);

    // DllImport, not LibraryImport: the generated code would need AllowUnsafeBlocks. On Unix the strings are passed
    // as UTF-8; a byte[] is pinned and passed as a pointer. open is only called without O_CREAT, so its variadic
    // mode is never read; openat with O_CREAT goes through CreateAt.
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, int mode);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(int folder, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags,
        int mode);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAtStackMode(int folder, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags,
        nint unused3, nint unused4, nint unused5, nint unused6, nint unused7, nint mode);

    [DllImport("libc", EntryPoint = "fchmod", SetLastError = true)]
    private static extern int Fchmod(int file, int mode);

    [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)]
    private static extern int MkdirAt(int folder, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int mode);

    [DllImport("libc", EntryPoint = "fstatat", SetLastError = true)]
    private static extern int Fstatat(int folder, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [Out] byte[] buffer, int flags);

    [DllImport("libc", EntryPoint = "fstatat$INODE64", SetLastError = true)]
    private static extern int FstatatInode64(int folder, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [Out] byte[] buffer, int flags);

    [DllImport("libc", EntryPoint = "write", SetLastError = true)]
    private static extern nint WriteBytes(int file, byte[] bytes, nint count);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int file);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int file);

    [DllImport("libc", EntryPoint = "renameat", SetLastError = true)]
    private static extern int RenameAt(int fromFolder, [MarshalAs(UnmanagedType.LPUTF8Str)] string fromName,
        int toFolder, [MarshalAs(UnmanagedType.LPUTF8Str)] string toName);

    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
    private static extern int UnlinkAt(int folder, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags);
}

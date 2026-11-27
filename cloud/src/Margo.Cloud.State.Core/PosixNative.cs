using System.Runtime.InteropServices;

namespace Margo.Cloud.State.Core;

/// <summary>The fields of <c>os.stat()</c>/<c>os.lstat()</c> the state helpers check.</summary>
public readonly record struct PyStat(uint Mode, uint Uid, ulong NLink, long Size)
{
    public const uint TypeMask = 0xF000;
    public const uint TypeDirectory = 0x4000;
    public const uint TypeRegular = 0x8000;
    public const uint TypeSymlink = 0xA000;

    public bool IsDirectory => (Mode & TypeMask) == TypeDirectory;

    public bool IsRegular => (Mode & TypeMask) == TypeRegular;

    public bool IsSymlink => (Mode & TypeMask) == TypeSymlink;

    /// <summary><c>stat.S_IMODE</c>.</summary>
    public uint Permissions => Mode & 0xFFF;
}

/// <summary>
/// The POSIX calls Python's <c>os</c> module makes for private state, with exact <c>errno</c> results:
/// <c>stat</c>/<c>lstat</c> (via <c>statx</c>, whose layout is the same on every Linux architecture),
/// <c>open</c> with <c>O_EXCL</c>/<c>O_NOFOLLOW</c>, <c>mkdir</c>, <c>link</c>, <c>fsync</c>, <c>getuid</c>.
/// Linux only; other platforms raise <see cref="PlatformNotSupportedException"/> (see PORTING.md).
/// </summary>
public static partial class PosixNative
{
    private const int AtFdCwd = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxBasicStats = 0x7ff;

    private static bool IsArm => RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm;

    public static int ORdOnly => 0;

    public static int OWrOnly => 1;

    public static int OCreat => 0x40;

    public static int OExcl => 0x80;

    public static int ONoFollow => IsArm ? 0x8000 : 0x20000;

    public static int ODirectory => IsArm ? 0x4000 : 0x10000;

    public static int OCloExec => 0x80000;

    /// <summary>True where the native calls are available (Linux).</summary>
    public static bool IsSupported => OperatingSystem.IsLinux();

    /// <summary><c>os.stat(path)</c> (follows symlinks) or <c>os.lstat(path)</c>.</summary>
    public static PyStat Stat(string path, bool followSymlinks = true)
    {
        EnsureSupported();
        var encoded = Encode(path);
        var buffer = new byte[256];
        int result;
        int errno;
        try
        {
            result = statx(AtFdCwd, encoded, followSymlinks ? 0 : AtSymlinkNoFollow, StatxBasicStats, buffer);
            errno = result == 0 ? 0 : Marshal.GetLastPInvokeError();
        }
        catch (EntryPointNotFoundException)
        {
            var number = IsArm ? 291L : 332L;
            result = (int)SyscallStatx(number, AtFdCwd, encoded, followSymlinks ? 0 : AtSymlinkNoFollow, StatxBasicStats, buffer);
            errno = result == 0 ? 0 : Marshal.GetLastPInvokeError();
        }

        if (result != 0)
        {
            throw Error(errno, path);
        }

        return new PyStat(
            Mode: BitConverter.ToUInt16(buffer, 28),
            Uid: BitConverter.ToUInt32(buffer, 20),
            NLink: BitConverter.ToUInt32(buffer, 16),
            Size: (long)BitConverter.ToUInt64(buffer, 40));
    }

    /// <summary>Like <see cref="Stat"/>, but returns null for the errors pathlib ignores (ENOENT, ENOTDIR, EBADF, ELOOP).</summary>
    public static PyStat? TryStat(string path, bool followSymlinks = true)
    {
        try
        {
            return Stat(path, followSymlinks);
        }
        catch (PyOSError error) when (error.Errno is PyErrno.ENOENT or PyErrno.ENOTDIR or 9 or PyErrno.ELOOP)
        {
            return null;
        }
        catch (PyValueError)
        {
            return null; // "Non-encodable path": an embedded NUL or an unescapable lone surrogate
        }
    }

    /// <summary><c>os.getuid()</c>.</summary>
    public static uint GetUid()
    {
        EnsureSupported();
        return getuid();
    }

    /// <summary><c>os.open(path, flags, mode)</c>; returns the file descriptor.</summary>
    public static int Open(string path, int flags, uint mode = 0)
    {
        EnsureSupported();
        var fd = open(Encode(path), flags | OCloExec, mode);
        if (fd < 0)
        {
            throw Error(Marshal.GetLastPInvokeError(), path);
        }

        return fd;
    }

    /// <summary><c>os.close(fd)</c>.</summary>
    public static void Close(int fd)
    {
        if (close(fd) != 0)
        {
            throw Error(Marshal.GetLastPInvokeError(), null);
        }
    }

    /// <summary><c>os.fsync(fd)</c>.</summary>
    public static void Fsync(int fd)
    {
        if (fsync(fd) != 0)
        {
            throw Error(Marshal.GetLastPInvokeError(), null);
        }
    }

    /// <summary><c>os.mkdir(path, mode)</c> (the process umask applies, as in Python).</summary>
    public static void Mkdir(string path, uint mode)
    {
        EnsureSupported();
        if (mkdir(Encode(path), mode) != 0)
        {
            throw Error(Marshal.GetLastPInvokeError(), path);
        }
    }

    /// <summary>
    /// <c>os.link(source, destination)</c>: an atomic no-replace hard link. Errors name both paths, as Python's
    /// <c>OSError</c> does (<c>[Errno 17] File exists: 'a' -&gt; 'b'</c>).
    /// </summary>
    public static void Link(string source, string destination)
    {
        EnsureSupported();
        if (link(Encode(source), Encode(destination)) != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            throw new PyOSError(errno, StrError(errno), source, destination);
        }
    }

    /// <summary>
    /// <c>os.replace(source, destination)</c>: an atomic <c>rename(2)</c> that replaces the destination. Errors name
    /// both paths (<c>[Errno 21] Is a directory: 'a' -&gt; 'b'</c>).
    /// </summary>
    public static void Replace(string source, string destination)
    {
        EnsureSupported();
        if (rename(Encode(source), Encode(destination)) != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            throw new PyOSError(errno, StrError(errno), source, destination);
        }
    }

    /// <summary><c>os.unlink(path)</c>.</summary>
    public static void Unlink(string path)
    {
        EnsureSupported();
        if (unlink(Encode(path)) != 0)
        {
            throw Error(Marshal.GetLastPInvokeError(), path);
        }
    }

    /// <summary><c>os.readlink(path)</c>.</summary>
    public static string ReadLink(string path)
    {
        EnsureSupported();
        var buffer = new byte[4096];
        var length = readlink(Encode(path), buffer, (nuint)buffer.Length);
        if (length < 0)
        {
            throw Error(Marshal.GetLastPInvokeError(), path);
        }

        return PyIO.FsDecode(buffer.AsSpan(0, (int)length));
    }

    /// <summary>Reads to end of file like <c>FileIO.readall()</c>; errors (other than EINTR, retried) raise <see cref="PyOSError"/>.</summary>
    public static byte[] ReadAll(int fd)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[65536];
        while (true)
        {
            var count = read(fd, buffer, (nuint)buffer.Length);
            if (count < 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                if (errno == 4)
                {
                    continue;
                }

                throw Error(errno, null);
            }

            if (count == 0)
            {
                return memory.ToArray();
            }

            memory.Write(buffer, 0, (int)count);
        }
    }

    /// <summary><c>os.write(fd, data)</c> until everything is written.</summary>
    public static void WriteAll(int fd, ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            var written = write(fd, data, (nuint)data.Length);
            if (written < 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                if (errno == 4)
                {
                    continue;
                }

                throw Error(errno, null);
            }

            data = data[(int)written..];
        }
    }

    /// <summary>
    /// <c>pwd.getpwnam(name).pw_dir</c> (decoded with surrogateescape), or null where Python raises <c>KeyError</c>.
    /// </summary>
    public static string? PasswdHome(string name)
    {
        EnsureSupported();
        var encoded = Encode(name);
        return PasswdLookup((IntPtr buffer, nuint size, out Passwd entry, out IntPtr result) =>
            getpwnam_r(encoded, out entry, buffer, size, out result));
    }

    /// <summary><c>pwd.getpwuid(uid).pw_dir</c>, or null where Python raises <c>KeyError</c>.</summary>
    public static string? PasswdHome(uint uid)
    {
        EnsureSupported();
        return PasswdLookup((IntPtr buffer, nuint size, out Passwd entry, out IntPtr result) =>
            getpwuid_r(uid, out entry, buffer, size, out result));
    }

    /// <summary>
    /// <c>signal(SIGXFSZ, SIG_IGN)</c>, as CPython does at start-up, so a write past <c>RLIMIT_FSIZE</c> fails with
    /// <c>EFBIG</c> (an <c>OSError</c> the scripts handle) instead of killing the process.
    /// </summary>
    public static void IgnoreFileSizeLimitSignal()
    {
        if (IsSupported)
        {
            signal(SigXfsz, SigIgn);
        }
    }

    private const int SigXfsz = 25;
    private static readonly IntPtr SigIgn = 1;
    private const int ERange = 34;

    private delegate int PasswdCall(IntPtr buffer, nuint size, out Passwd entry, out IntPtr result);

    private static unsafe string? PasswdLookup(PasswdCall call)
    {
        nuint size = 1024;
        while (true)
        {
            var buffer = (IntPtr)NativeMemory.Alloc(size);
            try
            {
                var status = call(buffer, size, out var entry, out var result);
                if (status == 0 && result != IntPtr.Zero)
                {
                    if (entry.Dir == IntPtr.Zero)
                    {
                        return "";
                    }

                    var length = 0;
                    var dir = (byte*)entry.Dir;
                    while (dir[length] != 0)
                    {
                        length++;
                    }

                    return PyIO.FsDecode(new ReadOnlySpan<byte>(dir, length));
                }

                if (status != ERange || size > (nuint)int.MaxValue)
                {
                    return null;
                }
            }
            finally
            {
                NativeMemory.Free((void*)buffer);
            }

            size *= 2;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Passwd
    {
        public IntPtr Name;
        public IntPtr Password;
        public uint Uid;
        public uint Gid;
        public IntPtr Gecos;
        public IntPtr Dir;
        public IntPtr Shell;
    }

    [LibraryImport("libc")]
    private static partial int getpwnam_r(byte[] name, out Passwd entry, IntPtr buffer, nuint size, out IntPtr result);

    [LibraryImport("libc")]
    private static partial int getpwuid_r(uint uid, out Passwd entry, IntPtr buffer, nuint size, out IntPtr result);

    [LibraryImport("libc")]
    private static partial IntPtr signal(int signum, IntPtr handler);

    /// <summary><c>os.strerror(errno)</c>.</summary>
    public static string StrError(int errno)
    {
        if (!IsSupported)
        {
            return PyErrno.StrError(errno);
        }

        var pointer = strerror(errno);
        return pointer == IntPtr.Zero ? PyErrno.StrError(errno) : Marshal.PtrToStringUTF8(pointer) ?? PyErrno.StrError(errno);
    }

    /// <summary>An <c>OSError</c> with the errno's message and filename.</summary>
    public static PyOSError Error(int errno, string? filename) => new(errno, StrError(errno), filename);

    private static void EnsureSupported()
    {
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException("Margo Cloud private state requires Linux file APIs.");
        }
    }

    /// <summary>A NUL-terminated path for libc: <c>os.fsencode</c> (UTF-8 with surrogateescape, no embedded NUL).</summary>
    private static byte[] Encode(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var bytes = PyIO.FsEncode(path);
        var terminated = new byte[bytes.Length + 1];
        bytes.CopyTo(terminated, 0);
        return terminated;
    }

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static partial int statx(int dirfd, byte[] pathname, int flags, uint mask, [Out] byte[] buffer);

    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static partial long SyscallStatx(long number, int dirfd, byte[] pathname, int flags, uint mask, [Out] byte[] buffer);

    [LibraryImport("libc", SetLastError = true)]
    private static partial uint getuid();

    [LibraryImport("libc", SetLastError = true)]
    private static partial int open(byte[] pathname, int flags, uint mode);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int close(int fd);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int fsync(int fd);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int mkdir(byte[] pathname, uint mode);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int link(byte[] oldpath, byte[] newpath);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int unlink(byte[] pathname);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int rename(byte[] oldpath, byte[] newpath);

    [LibraryImport("libc", SetLastError = true)]
    private static partial nint readlink(byte[] pathname, [Out] byte[] buffer, nuint size);

    [LibraryImport("libc", SetLastError = true)]
    private static partial nint write(int fd, ReadOnlySpan<byte> buffer, nuint count);

    [LibraryImport("libc", SetLastError = true)]
    private static partial nint read(int fd, [Out] byte[] buffer, nuint count);

    [LibraryImport("libc")]
    private static partial IntPtr strerror(int errnum);
}

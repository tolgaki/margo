using System.Runtime.InteropServices;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.State.Memory;

/// <summary>
/// The fields of <c>os.stat_result</c> that memory_store's source-file freshness check compares: file type and
/// size plus device, inode and nanosecond modification/change times. <see cref="PyStat"/> (State.Core) carries
/// only mode, owner, links and size; this belongs there once another port needs it.
/// </summary>
public readonly record struct PyFileStat(uint Mode, ulong Device, ulong Inode, long Size, long MtimeNs, long CtimeNs)
{
    /// <summary><c>stat.S_ISREG(st_mode)</c>.</summary>
    public bool IsRegular => (Mode & 0xF000) == 0x8000;

    /// <summary><c>(st_dev, st_ino, st_size, st_mtime_ns)</c>.</summary>
    public (ulong, ulong, long, long) Identity => (Device, Inode, Size, MtimeNs);

    /// <summary><c>identity + (st_ctime_ns,)</c>.</summary>
    public (ulong, ulong, long, long, long) Signature => (Device, Inode, Size, MtimeNs, CtimeNs);
}

/// <summary>
/// <c>os.stat(path)</c>, <c>os.fstat(fd)</c>, <c>open(path, "rb")</c> and <c>read</c> through Linux <c>statx</c>
/// and libc, with Python's <c>OSError</c> texts. The freshness check can be observed through
/// <see cref="Hooks"/> in tests (the Python tests patch <c>os.fstat</c>/<c>Path.stat</c>/<c>Path.open</c>).
/// </summary>
public static partial class MemoryNative
{
    private const int AtFdCwd = -100;
    private const int AtEmptyPath = 0x1000;
    private const uint StatxBasicStats = 0x7FF;

    /// <summary>Test hooks: replace a stat/fstat result or observe opens. Null in production.</summary>
    internal static readonly AsyncLocal<FileHooks?> Hooks = new();

    /// <summary>Overrides for the file probes of <c>_file_digest</c> (tests only).</summary>
    internal sealed class FileHooks
    {
        public Func<string, PyFileStat, PyFileStat>? Stat { get; init; }

        public Func<int, PyFileStat, PyFileStat>? FStat { get; init; }

        public Action<string>? Opened { get; init; }
    }

    /// <summary><c>os.stat(path)</c> (follows symlinks).</summary>
    public static PyFileStat Stat(string path)
    {
        var result = StatCore(AtFdCwd, Encode(path), 0, path);
        return Hooks.Value?.Stat is { } hook ? hook(path, result) : result;
    }

    /// <summary><c>os.fstat(fd)</c>.</summary>
    public static PyFileStat FStat(int fd)
    {
        var result = StatCore(fd, [0], AtEmptyPath, null);
        return Hooks.Value?.FStat is { } hook ? hook(fd, result) : result;
    }

    /// <summary><c>open(path, "rb")</c>: returns the descriptor (O_RDONLY | O_CLOEXEC, following symlinks).</summary>
    public static int OpenRead(string path)
    {
        var fd = PosixNative.Open(path, PosixNative.ORdOnly);
        Hooks.Value?.Opened?.Invoke(path);
        return fd;
    }

    /// <summary><c>stream.read(count)</c> on a raw descriptor: up to <paramref name="count"/> bytes, empty at end of file.</summary>
    public static byte[] Read(int fd, int count)
    {
        var buffer = new byte[Math.Max(count, 0)];
        var total = 0;
        while (total < count)
        {
            var read = ReadCore(fd, buffer.AsSpan(total), (nuint)(count - total));
            if (read < 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                if (errno == 4)
                {
                    continue;
                }

                throw PosixNative.Error(errno, null);
            }

            if (read == 0)
            {
                break;
            }

            total += (int)read;
        }

        return buffer.AsSpan(0, total).ToArray();
    }

    private static PyFileStat StatCore(int dirfd, byte[] path, int flags, string? filename)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Margo Cloud private state requires Linux file APIs.");
        }

        var buffer = new byte[256];
        if (statx(dirfd, path, flags, StatxBasicStats, buffer) != 0)
        {
            throw PosixNative.Error(Marshal.GetLastPInvokeError(), filename);
        }

        static long Nanoseconds(byte[] data, int offset) =>
            BitConverter.ToInt64(data, offset) * 1_000_000_000L + BitConverter.ToUInt32(data, offset + 8);

        var devMajor = BitConverter.ToUInt32(buffer, 136);
        var devMinor = BitConverter.ToUInt32(buffer, 140);
        // glibc makedev(): the dev_t Python reports as st_dev.
        var device = ((ulong)(devMajor & 0xFFFFF000) << 32) | ((ulong)(devMajor & 0xFFF) << 8)
                     | ((ulong)(devMinor & 0xFFFFFF00) << 12) | (devMinor & 0xFF);
        return new PyFileStat(
            Mode: BitConverter.ToUInt16(buffer, 28),
            Device: device,
            Inode: BitConverter.ToUInt64(buffer, 32),
            Size: (long)BitConverter.ToUInt64(buffer, 40),
            MtimeNs: Nanoseconds(buffer, 112),
            CtimeNs: Nanoseconds(buffer, 96));
    }

    private static byte[] Encode(string path)
    {
        var bytes = PyIO.FsEncode(path);
        var terminated = new byte[bytes.Length + 1];
        bytes.CopyTo(terminated, 0);
        return terminated;
    }

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static partial int statx(int dirfd, byte[] pathname, int flags, uint mask, [Out] byte[] buffer);

    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    private static partial nint ReadCore(int fd, Span<byte> buffer, nuint count);
}

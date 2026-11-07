using System.Runtime.InteropServices;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.State.Work;

/// <summary>
/// <c>os.replace(src, dst)</c>, which <see cref="PosixNative"/> does not offer yet (belongs in State.Core): an atomic
/// <c>rename(2)</c> with Python's <c>OSError</c> text (<c>[Errno N] message: 'src' -&gt; 'dst'</c>).
/// </summary>
public static partial class PosixFiles
{
    /// <summary><c>os.replace(source, destination)</c>.</summary>
    public static void Replace(string source, string destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (!PosixNative.IsSupported)
        {
            throw new PlatformNotSupportedException("Margo Cloud private state requires Linux file APIs.");
        }

        if (rename(Encode(source), Encode(destination)) != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            throw new PyOSErrorWithTwoPaths(errno, PosixNative.StrError(errno), source, destination);
        }
    }

    private static byte[] Encode(string path)
    {
        var bytes = PyIO.FsEncode(path);
        var terminated = new byte[bytes.Length + 1];
        bytes.CopyTo(terminated, 0);
        return terminated;
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int rename(byte[] oldpath, byte[] newpath);
}

/// <summary>An <c>OSError</c> with <c>filename</c> and <c>filename2</c> (<c>str()</c> shows both).</summary>
public sealed class PyOSErrorWithTwoPaths : PyOSError
{
    public PyOSErrorWithTwoPaths(int errno, string strerror, string filename, string filename2)
        : base(errno, strerror, filename)
    {
        Filename2 = filename2;
    }

    public string Filename2 { get; }

    public override string Message => base.Message + " -> " + PyRepr.Str(Filename2);
}

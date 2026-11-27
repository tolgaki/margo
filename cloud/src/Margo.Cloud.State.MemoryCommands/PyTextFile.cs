using System.Text;
using Margo.Cloud.State.Core;
using Microsoft.Win32.SafeHandles;

namespace Margo.Cloud.State.MemoryCommands;

/// <summary>
/// <c>open(path, encoding="utf-8").read(count)</c>: CPython's <c>TextIOWrapper.read(n)</c> (<c>textiowrapper_read_chunk</c>),
/// which <c>memory_state.py</c>'s <c>load(path, MAX_INPUT)</c> uses for Dream inputs. A copy of
/// <c>Margo.Cloud.State.Tasks.TaskStateScript.ReadFileText</c> (internal there): both belong in State.Core's
/// <see cref="PyIO"/> (requested in this project's and the Tasks project's PORTING.md). Each chunk is one unbuffered
/// <c>read(2)</c> of <c>max(8192, int(max(b2cratio, 1.0) * remaining))</c> bytes through the incremental UTF-8 decoder
/// and the universal-newline decoder, so nothing after the last chunk is read and decoding errors report positions
/// relative to their chunk, as in CPython.
/// </summary>
internal static class PyTextFile
{
    internal static string Read(string path, int count)
    {
        if (!PosixNative.IsSupported)
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 0);
            return ReadChunks(size => ReadOnce(file, size), count);
        }

        var fd = PosixNative.Open(path, PosixNative.ORdOnly);
        try
        {
            if (PosixNative.Stat(path).IsDirectory)
            {
                throw PosixNative.Error(PyErrno.EISDIR, path);
            }

            using var handle = new SafeFileHandle(fd, ownsHandle: false);
            using var stream = new FileStream(handle, FileAccess.Read, bufferSize: 0);
            return ReadChunks(size => ReadOnce(stream, size), count);
        }
        finally
        {
            PosixNative.Close(fd);
        }
    }

    private static byte[] ReadOnce(FileStream stream, int size)
    {
        var buffer = new byte[size];
        int count;
        try
        {
            count = stream.Read(buffer, 0, size);
        }
        catch (IOException ex) when (ex.HResult > 0 && ex.HResult < 4096)
        {
            throw PosixNative.Error(ex.HResult, null);
        }

        return count == size ? buffer : buffer[..count];
    }

    internal static string ReadChunks(Func<int, byte[]> read1, int count)
    {
        var result = new StringBuilder();
        var remaining = count;
        var pending = Array.Empty<byte>();
        var pendingCr = false;
        var bytesPerChar = 0.0;
        while (remaining > 0)
        {
            var hint = (long)(Math.Max(bytesPerChar, 1.0) * remaining);
            var chunk = read1((int)Math.Min(Math.Max(8192L, hint), int.MaxValue));
            var input = new byte[pending.Length + chunk.Length];
            pending.CopyTo(input, 0);
            chunk.CopyTo(input, pending.Length);
            var eof = chunk.Length == 0;
            var text = PyIO.DecodeUtf8(input, final: eof, out var consumed);
            pending = input[consumed..];
            if (pendingCr && (text.Length > 0 || eof))
            {
                text = "\r" + text;
                pendingCr = false;
            }

            if (!eof && text.EndsWith('\r'))
            {
                text = text[..^1];
                pendingCr = true;
            }

            text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            var decoded = PyRepr.Length(text);
            bytesPerChar = decoded > 0 ? (double)chunk.Length / decoded : 0.0;
            if (eof && text.Length == 0)
            {
                break;
            }

            var taken = PyText.Prefix(text, remaining);
            result.Append(taken);
            remaining -= PyRepr.Length(taken);
        }

        return result.ToString();
    }
}

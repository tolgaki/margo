using System.Runtime.ExceptionServices;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.State.Helpers.Python;

/// <summary>Process-level details of the Python scripts that State.Core does not cover yet.</summary>
public static class PyProcess
{
    /// <summary>
    /// Runs <paramref name="body"/> on a thread with a large stack. CPython walks values nested up to its C
    /// recursion limit (about 10000 levels: <c>repr</c>, comparisons, equality of nested lists); the recursive
    /// C# equivalents must not overflow the default 8 MiB stack first.
    /// </summary>
    public static T OnLargeStack<T>(Func<T> body, int stackBytes = 64 * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(body);
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = body();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        }, stackBytes);
        thread.Start();
        thread.Join();
        failure?.Throw();
        return result;
    }

    /// <summary>
    /// <c>sys.stdin.read()</c> under <c>PYTHONIOENCODING=utf-8</c>: all bytes are read, then decoded at once,
    /// so a <c>UnicodeDecodeError</c> names CPython's reason and the absolute byte position
    /// (<see cref="PyIO.DecodeUtf8"/>). <see cref="PyIO.ReadText"/> decodes through a
    /// <see cref="StreamReader"/>, whose message always says "invalid start byte"; this reads the
    /// underlying stream when it can.
    /// </summary>
    public static string ReadStdin(TextReader stdin)
    {
        ArgumentNullException.ThrowIfNull(stdin);
        if (stdin is StreamReader { BaseStream: { CanRead: true } stream })
        {
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return PyIO.DecodeUtf8(buffer.ToArray());
        }

        return PyIO.ReadText(stdin);
    }
}

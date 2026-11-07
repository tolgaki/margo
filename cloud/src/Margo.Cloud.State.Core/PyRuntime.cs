using System.Security.Cryptography;
using System.Text;

namespace Margo.Cloud.State.Core;

/// <summary>
/// The process-level inputs the Python helpers read implicitly: <c>os.environ</c>, the clock behind
/// <c>datetime.now()</c> and the randomness behind <c>uuid4()</c>/<c>secrets</c>. Production code reads the
/// real process; tests override them per async flow with <see cref="Override"/>, so parallel tests never
/// race on process-global environment variables.
/// </summary>
public static class PyRuntime
{
    private static readonly AsyncLocal<Scope?> Current = new();

    /// <summary><c>os.environ.get(name)</c>.</summary>
    public static string? GetEnv(string name)
    {
        var scope = Current.Value;
        if (scope?.Environment is { } environment)
        {
            return environment.TryGetValue(name, out var overridden) ? overridden : null;
        }

        var value = Environment.GetEnvironmentVariable(name);
        if (value is not null && value.Contains('\uFFFD', StringComparison.Ordinal) && ProcessEnvironment.Value is { } block)
        {
            // An undecodable byte: recover CPython's surrogateescape string from the raw environment.
            return DecodeEnvironmentValue(block, name, value);
        }

        return value;
    }

    /// <summary>
    /// Runs <paramref name="body"/> on a thread with a large stack. CPython walks values nested up to its C
    /// recursion limit (about 10000 levels: <c>repr</c>, comparisons, equality of nested lists); the recursive
    /// C# equivalents must not overflow the default 8 MiB stack first. The execution context (and so any
    /// <see cref="Override"/>) flows to the thread.
    /// </summary>
    public static T OnLargeStack<T>(Func<T> body, int stackBytes = 64 * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(body);
        T result = default!;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = body();
            }
            catch (Exception ex)
            {
                failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
            }
        }, stackBytes);
        thread.Start();
        thread.Join();
        failure?.Throw();
        return result;
    }

    /// <summary>The current UTC instant (the source of <c>datetime.now(timezone.utc)</c>).</summary>
    public static DateTimeOffset UtcNow() => Current.Value?.Clock?.Invoke() ?? DateTimeOffset.UtcNow;

    /// <summary>Cryptographically random bytes (the source of <c>os.urandom</c>, <c>uuid4</c>, <c>secrets</c>).</summary>
    public static byte[] RandomBytes(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (Current.Value?.Random is { } random)
        {
            return random(count);
        }

        return RandomNumberGenerator.GetBytes(count);
    }

    /// <summary>
    /// Overrides the environment (complete replacement, like <c>patch.dict(os.environ, ..., clear=True)</c>),
    /// clock and/or random source for the current async flow until the returned scope is disposed.
    /// A null argument keeps the outer value.
    /// </summary>
    public static IDisposable Override(IReadOnlyDictionary<string, string>? environment = null,
                                       Func<DateTimeOffset>? clock = null, Func<int, byte[]>? random = null)
    {
        var outer = Current.Value;
        Current.Value = new Scope(environment ?? outer?.Environment, clock ?? outer?.Clock, random ?? outer?.Random, outer);
        return new Restore(outer);
    }

    /// <summary>A copy of the effective environment, for building an override that changes a few names.</summary>
    public static Dictionary<string, string> EnvironmentSnapshot()
    {
        if (Current.Value?.Environment is { } environment)
        {
            return new Dictionary<string, string>(environment, StringComparer.Ordinal);
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                result[key] = value;
            }
        }

        return result;
    }

    /// <summary>
    /// <c>sys.argv[1:]</c> as CPython decodes it (UTF-8 with <c>surrogateescape</c>) from the process command line
    /// (<c>/proc/self/cmdline</c>, NUL-separated). .NET hands <c>Main</c> arguments with undecodable bytes
    /// replaced by U+FFFD, which would silently turn an invalid principal or path into a different valid one.
    /// The last <paramref name="managed"/>.Count entries are used only when they decode (with replacement) to
    /// exactly the managed arguments; otherwise the managed arguments are returned unchanged.
    /// </summary>
    public static string[] DecodeArguments(ReadOnlySpan<byte> commandLine, IReadOnlyList<string> managed)
    {
        ArgumentNullException.ThrowIfNull(managed);
        var entries = SplitNul(commandLine);
        if (entries.Count < managed.Count)
        {
            return [.. managed];
        }

        var result = new string[managed.Count];
        var offset = entries.Count - managed.Count;
        for (var i = 0; i < managed.Count; i++)
        {
            var raw = entries[offset + i];
            if (!Corresponds(raw, managed[i]))
            {
                return [.. managed];
            }

            result[i] = PyIO.FsDecode(raw);
        }

        return result;
    }

    /// <summary><see cref="DecodeArguments"/> over this process's <c>/proc/self/cmdline</c> (the managed arguments elsewhere).</summary>
    public static string[] CommandLineArguments(IReadOnlyList<string> managed) =>
        DecodeArguments(ReadProcFile("/proc/self/cmdline") ?? [], managed);

    /// <summary>
    /// <c>os.environ[name]</c> as CPython decodes it (UTF-8 with <c>surrogateescape</c>) from the process environment
    /// block (<c>/proc/self/environ</c>), when that entry still decodes (with replacement) to the managed value.
    /// </summary>
    public static string? DecodeEnvironmentValue(ReadOnlySpan<byte> environment, string name, string? managed)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (managed is null)
        {
            return null;
        }

        var prefix = Encoding.UTF8.GetBytes(name + "=");
        foreach (var entry in SplitNul(environment))
        {
            if (entry.AsSpan().StartsWith(prefix))
            {
                // CPython keeps the first entry of a repeated name (PyDict_SetDefault).
                var raw = entry.AsSpan(prefix.Length);
                return Corresponds(raw, managed) ? PyIO.FsDecode(raw) : managed;
            }
        }

        return managed;
    }

    private static readonly Lazy<byte[]?> ProcessEnvironment = new(() => ReadProcFile("/proc/self/environ"));

    /// <summary>The raw bytes of a <c>/proc/self</c> file, or null where it cannot be read.</summary>
    internal static byte[]? ReadProcFile(string path)
    {
        try
        {
            return OperatingSystem.IsLinux() ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="managed"/> is .NET's decoding of <paramref name="raw"/>: equal once replacement
    /// characters (managed) and escaped bytes (raw) are dropped, since the runtime's decoder may emit a different
    /// number of U+FFFD for one invalid sequence than <see cref="Encoding.UTF8"/> does.
    /// </summary>
    private static bool Corresponds(ReadOnlySpan<byte> raw, string managed)
    {
        var decoded = PyIO.FsDecode(raw);
        if (string.Equals(decoded, managed, StringComparison.Ordinal))
        {
            return true;
        }

        static string Valid(string text, char low, char high) =>
            string.Concat(text.Where(c => c < low || c > high));
        return string.Equals(Valid(decoded, '\udc80', '\udcff'), Valid(managed, '\ufffd', '\ufffd'), StringComparison.Ordinal);
    }

    private static List<byte[]> SplitNul(ReadOnlySpan<byte> block)
    {
        var entries = new List<byte[]>();
        while (!block.IsEmpty)
        {
            var end = block.IndexOf((byte)0);
            if (end < 0)
            {
                entries.Add(block.ToArray());
                break;
            }

            entries.Add(block[..end].ToArray());
            block = block[(end + 1)..];
        }

        return entries;
    }

    private sealed record Scope(IReadOnlyDictionary<string, string>? Environment, Func<DateTimeOffset>? Clock,
                                Func<int, byte[]>? Random, Scope? Outer);

    private sealed class Restore(Scope? outer) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                Current.Value = outer;
                _disposed = true;
            }
        }
    }
}

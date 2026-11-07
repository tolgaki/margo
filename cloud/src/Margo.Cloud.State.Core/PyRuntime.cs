using System.Security.Cryptography;

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
            return environment.TryGetValue(name, out var value) ? value : null;
        }

        return Environment.GetEnvironmentVariable(name);
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

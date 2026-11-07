using System.Collections.Concurrent;
using Margo.Cloud.Router.Authentication;
using Microsoft.Extensions.Logging;

namespace Margo.Cloud.Router.Tests.TestSupport;

/// <summary>A clock the tests move by hand: wall time and the monotonic timestamp move together.</summary>
internal sealed class ManualTime : TimeProvider
{
    private readonly Lock _lock = new();
    private DateTimeOffset _now;
    private long _ticks = 1_000_000_000;

    public ManualTime(DateTimeOffset? now = null)
    {
        _now = now ?? new DateTimeOffset(2026, 11, 7, 9, 30, 0, TimeSpan.Zero);
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            return _now;
        }
    }

    public override long GetTimestamp()
    {
        lock (_lock)
        {
            return _ticks;
        }
    }

    public void Advance(TimeSpan by)
    {
        lock (_lock)
        {
            _now += by;
            _ticks += by.Ticks;
        }
    }
}

/// <summary>Serves a local key set and records which endpoints were read.</summary>
internal sealed class FakeJwksSource : IJwksSource
{
    public ConcurrentQueue<string> Fetched { get; } = new();

    public Func<string, byte[]> Document { get; set; } = _ => Tokens.DefaultKeySet();

    public bool Down { get; set; }

    public Task<byte[]> FetchAsync(string uri, CancellationToken cancellationToken)
    {
        Fetched.Enqueue(uri);
        if (Down)
        {
            throw new JwksUnavailableException("down");
        }

        return Task.FromResult(Document(uri));
    }
}

/// <summary>The route table blob, in memory.</summary>
internal sealed class FakeRouteSource : IRouteSource
{
    private int _loads;

    public string Text { get; set; } = string.Empty;

    public Exception? Failure { get; set; }

    public int Loads => Volatile.Read(ref _loads);

    public Task<string> LoadAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _loads);
        return Failure is null ? Task.FromResult(Text) : Task.FromException<string>(Failure);
    }
}

/// <summary>The provisioning queue, in memory.</summary>
internal sealed class FakeProvisioningSink : IProvisioningSink
{
    public ConcurrentQueue<string> Sent { get; } = new();

    public Task SendAsync(string text, CancellationToken cancellationToken)
    {
        Sent.Enqueue(text);
        return Task.CompletedTask;
    }
}

/// <summary>Captures every log message, formatted, to check what the router writes.</summary>
internal sealed class ListLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public ILogger CreateLogger(string categoryName) => new ListLogger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class ListLogger(ListLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            owner.Lines.Enqueue(logLevel + " " + category + " | " + formatter(state, exception) + (exception is null ? string.Empty : " | " + exception));
        }
    }
}

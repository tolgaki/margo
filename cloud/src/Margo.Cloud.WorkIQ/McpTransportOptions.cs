using System.Collections.Frozen;

namespace Margo.Cloud.WorkIQ;

/// <summary>Bounds and connection handling for <see cref="WorkIQMcpTransport"/>. Every value is validated at construction.</summary>
public sealed record McpTransportOptions
{
    // Declared before Default: its instance initialisers read this.
    /// <summary>Work IQ's read tools (the guard's <c>TOOL_ARGS</c> minus <c>WRITE_TOOLS</c>; a Guard test keeps them equal).</summary>
    public static IReadOnlySet<string> ReadTools { get; } = FrozenSet.Create(
        StringComparer.Ordinal, "fetch", "fetch_blob", "call_function", "ask", "retrieve", "list_agents", "get_schema", "search_paths");

    /// <summary>The defaults.</summary>
    public static McpTransportOptions Default { get; } = new();

    /// <summary>The most bytes one response body may hold, after decompression. A larger answer fails the call with
    /// <see cref="TransportResponseTooLargeException"/> instead of being read on. Default 8 MiB.</summary>
    public long MaxResponseBytes { get; init; } = 8L * 1024 * 1024;

    /// <summary>The most MCP sessions kept (one per bearer token); the least recently used is closed first. Default 32.</summary>
    public int MaxSessions { get; init; } = 32;

    /// <summary>How long an unused session is kept. Access tokens live about an hour. Default one hour.</summary>
    public TimeSpan SessionIdleTimeout { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// The MCP protocol revision requested in <c>initialize</c>. Pinned rather than left to the SDK, whose default first
    /// probes for the newest revision and, against a server that predates it, waits for that probe to time out on every
    /// new session (and every write opens one). Default <c>2025-06-18</c>, the revision Work IQ was verified with.
    /// </summary>
    public string ProtocolVersion { get; init; } = "2025-06-18";

    /// <summary>How long one call, session set-up included, may take before it is reported as a timeout. Default 60 s.</summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The tools whose calls share the session's pooled connections. Any other tool (the write tools, and any name not
    /// listed) gets an MCP session and a connection of its own, closed after the call, so the HTTP stack can never resend
    /// a write that a reused connection may already have delivered.
    /// </summary>
    public IReadOnlySet<string> PooledTools { get; init; } = ReadTools;

    /// <summary>How long a pooled connection may live, so DNS and load-balancer changes are picked up. Default 2 minutes.</summary>
    public TimeSpan PooledConnectionLifetime { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>How long an idle pooled connection is kept. Default 1 minute.</summary>
    public TimeSpan PooledConnectionIdleTimeout { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>The clock for <see cref="SessionIdleTimeout"/> (tests).</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// Builds the HTTP handler under each MCP session (tests). <c>pooled</c> is false for a write's own session. Null
    /// uses a <see cref="SocketsHttpHandler"/> that never follows redirects (they would carry the token elsewhere).
    /// </summary>
    public Func<bool, HttpMessageHandler>? HandlerFactory { get; init; }

    internal McpTransportOptions Validated()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxResponseBytes, 1, nameof(MaxResponseBytes));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxSessions, 1, nameof(MaxSessions));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(SessionIdleTimeout, TimeSpan.Zero, nameof(SessionIdleTimeout));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(CallTimeout, TimeSpan.Zero, nameof(CallTimeout));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(PooledConnectionLifetime, TimeSpan.Zero, nameof(PooledConnectionLifetime));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(PooledConnectionIdleTimeout, TimeSpan.Zero, nameof(PooledConnectionIdleTimeout));
        ArgumentNullException.ThrowIfNull(TimeProvider);
        ArgumentNullException.ThrowIfNull(PooledTools);
        ArgumentException.ThrowIfNullOrWhiteSpace(ProtocolVersion);
        return this;
    }
}

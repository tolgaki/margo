using System.Collections.Frozen;

namespace Margo.Cloud.WorkIQ;

/// <summary>
/// Resource bounds and connection handling for <see cref="McpHttpTransport"/>. No Python equivalent: the Python
/// transport read any body whole, kept every session and opened a connection per request (see PORTING.md,
/// deviations 10 to 12). The defaults are safe for the hosted container; every value is validated at construction.
/// </summary>
public sealed record McpTransportOptions
{
    // Declared before Default: its instance initialisers read this.
    /// <summary>Work IQ's read tools (the guard's <c>TOOL_ARGS</c> minus <c>WRITE_TOOLS</c>; a Guard test keeps them equal).</summary>
    public static IReadOnlySet<string> ReadTools { get; } = FrozenSet.Create(
        StringComparer.Ordinal, "fetch", "fetch_blob", "call_function", "ask", "retrieve", "list_agents", "get_schema", "search_paths");

    /// <summary>The defaults.</summary>
    public static McpTransportOptions Default { get; } = new();

    /// <summary>
    /// The most bytes one response body may hold, counted after decompression. A larger answer fails the call with
    /// <see cref="TransportResponseTooLargeException"/> instead of being buffered. Default 8 MiB.
    /// </summary>
    public long MaxResponseBytes { get; init; } = 8L * 1024 * 1024;

    /// <summary>The most MCP sessions kept (one per bearer token); the least recently used is dropped first. Default 32.</summary>
    public int MaxSessions { get; init; } = 32;

    /// <summary>
    /// How long an unused session is kept. Access tokens live about an hour, so a session idle for longer belongs
    /// to a token that will not come back. Default one hour.
    /// </summary>
    public TimeSpan SessionIdleTimeout { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Let calls of <see cref="PooledTools"/> share pooled connections, HTTP/2 when the server negotiates it. When
    /// false every request opens its own HTTP/1.1 connection with <c>Connection: close</c>, as Python's urllib did.
    /// Default true. An injected handler pools as it was built; the request version and <c>Connection</c> header
    /// still follow this.
    /// </summary>
    public bool ReuseConnections { get; init; } = true;

    /// <summary>
    /// The tools whose calls may use a pooled connection: Work IQ's read tools, where a request that fails on a
    /// connection the server had already closed costs one failed read. Any other tool (the write tools, and any
    /// name not listed) is sent on a fresh connection of its own, session handshake included, exactly as before.
    /// </summary>
    public IReadOnlySet<string> PooledTools { get; init; } = ReadTools;

    /// <summary>How long a pooled connection may live, so DNS and load-balancer changes are picked up. Default 2 minutes.</summary>
    public TimeSpan PooledConnectionLifetime { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>How long an idle pooled connection is kept: shorter than common server and proxy idle limits. Default 1 minute.</summary>
    public TimeSpan PooledConnectionIdleTimeout { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>The clock for <see cref="SessionIdleTimeout"/> (tests).</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    internal McpTransportOptions Validated()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxResponseBytes, 1, nameof(MaxResponseBytes));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxSessions, 1, nameof(MaxSessions));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(SessionIdleTimeout, TimeSpan.Zero, nameof(SessionIdleTimeout));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(PooledConnectionLifetime, TimeSpan.Zero, nameof(PooledConnectionLifetime));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(PooledConnectionIdleTimeout, TimeSpan.Zero, nameof(PooledConnectionIdleTimeout));
        ArgumentNullException.ThrowIfNull(TimeProvider);
        ArgumentNullException.ThrowIfNull(PooledTools);
        return this;
    }
}

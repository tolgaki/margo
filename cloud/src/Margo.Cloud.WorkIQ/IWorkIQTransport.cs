using System.Text.Json.Nodes;

namespace Margo.Cloud.WorkIQ;

/// <summary>
/// What the Work IQ guard needs from a transport: the endpoint it is wired to, and one
/// <c>tools/call</c> with the bearer token the guard chose (<c>guard.py</c> calls
/// <c>transport.endpoint</c> and <c>transport.call(tool, args, bearer=...)</c>).
/// </summary>
/// <remarks>
/// <para>The outcome contract the guard relies on, as in Python:</para>
/// <list type="bullet">
/// <item>A returned <see cref="JsonObject"/> is the tool's payload. The guard reads
/// <c>statusCode</c> and <c>results[].statusCode</c> from it. It is detached and owned by the caller.</item>
/// <item><see cref="TransportTimeoutException"/>: no response arrived in time. The effect of a write is
/// unknown and the call must not be retried.</item>
/// <item><see cref="TransportException"/>: Work IQ answered with an HTTP, JSON-RPC or tool error, answered
/// something that is not a reply to the request, or could not be reached before the request was sent
/// ("Work IQ unreachable"). <see cref="TransportException.StatusCode"/> carries the HTTP or tool status when
/// there was one.</item>
/// <item>Any other exception (the server hung up or sent a malformed status line after the request went out,
/// a truncated or reset body, a response shape Python fails on, a session the server forgot twice, a token
/// that cannot be sent as a header): the guard records an unknown transport failure and never resends it.</item>
/// <item><see cref="OperationCanceledException"/> only when the caller's token is cancelled. Python has no
/// equivalent; once a request may have been sent its effect is unknown.</item>
/// </list>
/// <para>Implementations are safe to call concurrently. A transport never chooses a token and never
/// retries a request that may have been processed.</para>
/// </remarks>
public interface IWorkIQTransport
{
    /// <summary>The MCP endpoint this transport posts to. The guard accepts only <see cref="WorkIQEndpoints.McpUrl"/>.</summary>
    string Endpoint { get; }

    /// <summary>Send one MCP <c>tools/call</c> as the subject of <paramref name="bearer"/>.</summary>
    /// <param name="tool">The Work IQ tool name.</param>
    /// <param name="arguments">The tool arguments, already validated by the guard. Not modified.</param>
    /// <param name="bearer">The access token for the subject the guard selected.</param>
    /// <param name="cancellationToken">Cancels the call; see the remarks on <see cref="IWorkIQTransport"/>.</param>
    /// <returns>The normalised tool payload.</returns>
    Task<JsonObject> CallAsync(string tool, JsonObject arguments, string bearer, CancellationToken cancellationToken = default);
}

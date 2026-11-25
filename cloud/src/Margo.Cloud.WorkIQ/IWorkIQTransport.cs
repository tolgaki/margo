using System.Text.Json.Nodes;

namespace Margo.Cloud.WorkIQ;

/// <summary>
/// What the Work IQ guard needs from an MCP client: the endpoint it is wired to, the server's tools
/// (<c>tools/list</c>), and one <c>tools/call</c> with the bearer token the guard chose.
/// </summary>
/// <remarks>
/// <para>The outcome contract the guard relies on:</para>
/// <list type="bullet">
/// <item>A returned <see cref="JsonObject"/> is the tool's payload. The guard reads
/// <c>statusCode</c> and <c>results[].statusCode</c> from it. It is detached and owned by the caller.</item>
/// <item><see cref="TransportTimeoutException"/>: no response arrived in time. The effect of a write is
/// unknown and the call must not be retried.</item>
/// <item><see cref="TransportException"/>: Work IQ answered with an HTTP, JSON-RPC or tool error, answered
/// something that is not a reply to the request, or could not be reached before the request was sent
/// ("Work IQ unreachable"). <see cref="TransportException.StatusCode"/> carries the HTTP or tool status when
/// there was one.</item>
/// <item>Any other exception (the server hung up after the request went out, a truncated or reset body, a
/// malformed answer, a session the server forgot twice): the guard records an unknown transport failure and
/// never resends it.</item>
/// <item><see cref="OperationCanceledException"/> only when the caller's token is cancelled; once a request may
/// have been sent its effect is unknown.</item>
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

    /// <summary>
    /// The server's tools (<c>tools/list</c>, every page) as the subject of <paramref name="bearer"/>: names,
    /// descriptions and input schemas. Empty when the transport cannot list them, and callers then fall back to
    /// their own definitions. Never throws for a server or network failure.
    /// </summary>
    Task<IReadOnlyList<WorkIQTool>> ListToolsAsync(string bearer, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<WorkIQTool>>([]);
}

/// <summary>One tool as the Work IQ MCP server describes it in <c>tools/list</c>.</summary>
/// <param name="Name">The tool name (<c>fetch</c>, <c>ask</c>, ...).</param>
/// <param name="Description">What the server says the tool does; may be empty.</param>
/// <param name="InputSchema">The JSON Schema of its arguments (an object schema), detached and owned by the caller.</param>
/// <param name="ReadOnly">The server's <c>readOnlyHint</c> annotation, when it gives one.</param>
public sealed record WorkIQTool(string Name, string Description, JsonObject InputSchema, bool? ReadOnly);

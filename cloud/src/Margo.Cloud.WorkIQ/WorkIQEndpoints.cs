namespace Margo.Cloud.WorkIQ;

/// <summary>The one Work IQ MCP endpoint a hosted Margo may reach (<c>guard.py</c> <c>WORKIQ_MCP_URL</c>).</summary>
/// <remarks>
/// It lives here rather than in the guard because the transport's default endpoint is this URL and
/// the guard project references this one, not the other way round. The guard refuses any transport
/// whose <see cref="IWorkIQTransport.Endpoint"/> is not exactly this string.
/// </remarks>
public static class WorkIQEndpoints
{
    /// <summary>Work IQ's streamable-HTTP MCP endpoint.</summary>
    public const string McpUrl = "https://workiq.svc.cloud.microsoft/mcp";
}

using Margo.Cloud.Voice;
using Microsoft.Agents.Authentication;

namespace Margo.Cloud.Host;

/// <summary>
/// The voice service's tokens through the Agents SDK connection (Python: <c>connections.get_default_connection()</c>,
/// then <c>get_agentic_user_token</c> and <c>acquire_token_on_behalf_of</c> in <c>voice/server.py</c>).
/// </summary>
public sealed class SdkVoiceTokens(IConnections connections) : IVoiceTokens
{
    /// <inheritdoc />
    public async Task<string?> GetAgenticUserTokenAsync(string tenantId, string agentIdentityId, string agentUserId, IReadOnlyList<string> scopes,
                                                        CancellationToken cancellationToken)
    {
        if (connections.GetDefaultConnection() is not IAgenticTokenProvider provider)
        {
            throw new InvalidOperationException("the default connection cannot issue agentic tokens");
        }

        return await provider.GetAgenticUserTokenAsync(tenantId, agentIdentityId, agentUserId, [.. scopes], cancellationToken);
    }

    /// <inheritdoc />
    public async Task<string?> AcquireTokenOnBehalfOfAsync(IReadOnlyList<string> scopes, string userToken, CancellationToken cancellationToken)
    {
        if (connections.GetDefaultConnection() is not IOBOExchange exchange)
        {
            throw new InvalidOperationException("the default connection cannot exchange tokens on behalf of a user");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var response = await exchange.AcquireTokenOnBehalfOf(scopes, userToken);
        return response?.Token;
    }
}

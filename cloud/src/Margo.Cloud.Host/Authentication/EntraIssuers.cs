// Copied from Margo.Cloud.Router (namespace changed only): the Agents SDK token check the Python host also ran.
// Keep the copies in step; see Margo.Cloud.Host/PORTING.md, "Requests to other components".
using System.Text.RegularExpressions;

namespace Margo.Cloud.Host.Authentication;

/// <summary>
/// Recognised token issuers and where their signing keys are published. Python:
/// <c>microsoft_agents.hosting.core.authorization._entra_issuers</c> and
/// <c>AuthenticationConstants</c> (Microsoft 365 Agents SDK 1.10), for a connection without an
/// <c>AUTHORITY</c> (the public cloud), which is how the router configured it.
/// </summary>
internal static partial class EntraIssuers
{
    internal const string BotFrameworkPublicIssuer = "https://api.botframework.com";
    internal const string BotFrameworkGovIssuer = "https://api.botframework.us";
    internal const string BotFrameworkPublicJwksUri = "https://login.botframework.com/v1/.well-known/keys";
    internal const string BotFrameworkGovJwksUri = "https://login.botframework.azure.us/v1/.well-known/keys";

    private const string V1Prefix = "https://sts.windows.net/";
    private const string V1Suffix = "/";
    private const string PublicV2Prefix = "https://login.microsoftonline.com/";
    private const string GovV2Prefix = "https://login.microsoftonline.us/";
    private const string V2Suffix = "/v2.0";

    /// <summary>The signing keys for a token: the Bot Framework set for a Bot Framework issuer, otherwise
    /// the configured tenant's Entra set. Python: <c>_build_jwks_uri</c>; the issuer is the unverified
    /// <c>iss</c> claim and only chooses where keys are read.</summary>
    internal static string JwksUri(string? issuer, string? tenantId) => issuer switch
    {
        BotFrameworkPublicIssuer => BotFrameworkPublicJwksUri,
        BotFrameworkGovIssuer => BotFrameworkGovJwksUri,
        _ => "https://login.microsoftonline.com/" + (string.IsNullOrEmpty(tenantId) ? "common" : tenantId) + "/discovery/v2.0/keys",
    };

    /// <summary>The lower-cased tenant GUID of a recognised Entra issuer (v1 <c>sts.windows.net</c>, or
    /// v2 public or US Government), or <see langword="null"/>. Python: <c>entra_issuer_info(iss).tenant</c>.</summary>
    internal static string? EntraTenant(string? issuer)
    {
        if (string.IsNullOrEmpty(issuer))
        {
            return null;
        }

        if (MatchIssuer(issuer, V1Prefix, V1Suffix) is { } v1)
        {
            return TenantGuid().IsMatch(v1) ? v1.ToLowerInvariant() : null;
        }

        foreach (var prefix in (string[])[PublicV2Prefix, GovV2Prefix])
        {
            if (MatchIssuer(issuer, prefix, V2Suffix) is { } tenant && TenantGuid().IsMatch(tenant))
            {
                return tenant.ToLowerInvariant();
            }
        }

        return null;
    }

    /// <summary>Python's <c>re.match(r"^(?i:PREFIX)([^/]+)SUFFIX$", issuer)</c>: the prefix compared
    /// with Python's Unicode case-insensitivity, the suffix exactly, and <c>$</c> also matching before a
    /// final newline.</summary>
    private static string? MatchIssuer(string issuer, string prefix, string suffix)
    {
        if (issuer.Length < prefix.Length + 1 + suffix.Length)
        {
            return null;
        }

        for (var i = 0; i < prefix.Length; i++)
        {
            if (!PythonIgnoreCaseEquals(prefix[i], issuer[i]))
            {
                return null;
            }
        }

        var rest = issuer[prefix.Length..];
        foreach (var candidate in rest.EndsWith('\n') ? (string[])[rest, rest[..^1]] : [rest])
        {
            if (candidate.EndsWith(suffix, StringComparison.Ordinal))
            {
                var tenant = candidate[..^suffix.Length];
                if (tenant.Length > 0 && !tenant.Contains('/', StringComparison.Ordinal))
                {
                    return tenant;
                }
            }
        }

        return null;
    }

    /// <summary>Whether Python's <c>re.IGNORECASE</c> matches <paramref name="actual"/> against the
    /// ASCII literal <paramref name="literal"/>: ASCII case, plus U+0130 and U+0131 for <c>i</c>,
    /// U+017F for <c>s</c> and U+212A for <c>k</c>.</summary>
    private static bool PythonIgnoreCaseEquals(char literal, char actual)
    {
        if (literal == actual)
        {
            return true;
        }

        if (char.IsAsciiLetter(literal) && char.IsAsciiLetter(actual))
        {
            return (literal | 0x20) == (actual | 0x20);
        }

        return (char)(literal | 0x20) switch
        {
            'i' => actual is 'İ' or 'ı',
            's' => actual == 'ſ',
            'k' => actual == 'K',
            _ => false,
        };
    }

    /// <summary>Python: <c>^[0-9a-f]{8}-...-[0-9a-f]{12}$</c> with <c>re.IGNORECASE</c>; .NET's <c>$</c>
    /// also matches before a final newline.</summary>
    [GeneratedRegex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.CultureInvariant)]
    private static partial Regex TenantGuid();
}

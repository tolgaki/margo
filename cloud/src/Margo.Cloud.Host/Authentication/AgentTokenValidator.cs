// Copied from Margo.Cloud.Router (namespace changed only): the Agents SDK token check the Python host also ran.
// Keep the copies in step; see Margo.Cloud.Host/PORTING.md, "Requests to other components".
using System.Numerics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Margo.Cloud.Host.Authentication;

/// <summary>The blueprint the router accepts tokens for. Python:
/// <c>AgentAuthConfiguration(client_id=..., tenant_id=...)</c>.</summary>
/// <param name="ClientId">The agent blueprint's client ID: the only accepted audience.</param>
/// <param name="TenantId">The tenant whose Entra signing keys are read for Entra-issued tokens.</param>
public sealed record AgentAuthSettings(string ClientId, string TenantId);

/// <summary>The outcome of checking a request's <c>Authorization</c> header.</summary>
/// <param name="StatusCode">200 when accepted; otherwise the status to answer with.</param>
/// <param name="ErrorJson">The response body when refused, as the Agents SDK wrote it.</param>
/// <param name="Claims">The verified token claims when accepted.</param>
public sealed record AuthorizationResult(int StatusCode, string? ErrorJson, JsonObject? Claims)
{
    public bool Succeeded => Claims is not null;
}

/// <summary>
/// The inbound token check of the Microsoft 365 Agents SDK 1.10 for aiohttp
/// (<c>jwt_authorization_middleware</c>, <c>_authorize_request</c> and <c>JwtTokenValidator</c> with an
/// <c>AgentAuthConfiguration(client_id, tenant_id)</c>), on PyJWT 2.15. See PORTING.md for what is
/// and is not checked; in short:
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The header is <c>Bearer &lt;token&gt;</c> (any case, surrounding whitespace allowed).</item>
/// <item>The signing key is looked up by <c>kid</c> in the Bot Framework key set when the unverified
/// <c>iss</c> is a Bot Framework issuer, otherwise in the configured tenant's Entra key set.</item>
/// <item>RS256 only; <c>exp</c>, <c>nbf</c> and <c>iat</c> are checked when present, with 300 seconds
/// of leeway; <c>sub</c> and <c>jti</c> must be strings when present.</item>
/// <item><c>aud</c> must be one string equal to the client ID, ignoring case.</item>
/// <item>The issuer is not checked against a list (the SDK's <c>VALIDATE_ISSUER</c> is off), but a
/// token from a recognised Entra issuer must carry the same tenant in <c>tid</c>, when it has one.</item>
/// </list>
/// A token without <c>kid</c> (that passes the earlier checks) fails with 500, because the SDK did not
/// catch the <c>KeyError</c>; this is kept.
/// </remarks>
public sealed class AgentTokenValidator
{
    /// <summary>Python: <c>_GENERIC_AUTH_ERROR</c> and the 401 body of <c>_authorize_request</c>.</summary>
    public const string InvalidTokenJson = "{\"error\": \"Invalid token or authentication failed.\"}";

    public const string MissingHeaderJson = "{\"error\": \"Authorization header not found\"}";

    public const string BadFormatJson = "{\"error\": \"Invalid authorization header format\"}";

    /// <summary>PyJWT <c>leeway</c> as the SDK passes it.</summary>
    public static readonly TimeSpan Leeway = TimeSpan.FromSeconds(300);

    private const string Algorithm = "RS256";

    private readonly AgentAuthSettings _settings;
    private readonly JwksClientManager _keys;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public AgentTokenValidator(AgentAuthSettings settings, JwksClientManager keys, ILogger logger, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(logger);
        _settings = settings;
        _keys = keys;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Check the first <c>Authorization</c> header value (<see langword="null"/> when absent).
    /// Exceptions other than a refused token propagate, as they did in Python (the request fails with 500).
    /// </summary>
    public async Task<AuthorizationResult> AuthorizeAsync(string? authorizationHeader, CancellationToken cancellationToken)
    {
        if (authorizationHeader is not null)
        {
            // The aiohttp adapter's tolerance: "Bearer" in any case and whitespace around the token.
            var token = ExtractBearerToken(authorizationHeader);
            if (token is null)
            {
                _logger.LogWarning("Malformed authorization header.");
                return new AuthorizationResult(401, InvalidTokenJson, null);
            }

            authorizationHeader = "Bearer " + token;
        }

        if (string.IsNullOrEmpty(authorizationHeader))
        {
            return new AuthorizationResult(401, MissingHeaderJson, null); // anonymous access is off
        }

        var parts = authorizationHeader.Split(' ');
        if (parts.Length != 2 || !PythonCompat.LowerEquals(parts[0], "bearer"))
        {
            return new AuthorizationResult(401, BadFormatJson, null);
        }

        try
        {
            return new AuthorizationResult(200, null, await ValidateTokenAsync(parts[1], cancellationToken));
        }
        catch (TokenRejectedException exc)
        {
            _logger.LogWarning("JWT validation error: {ErrorType}", exc.Kind);
            return new AuthorizationResult(401, InvalidTokenJson, null);
        }
    }

    /// <summary>Python: the aiohttp adapter's <c>_extract_bearer_token</c>.</summary>
    internal static string? ExtractBearerToken(string header)
    {
        // header.split(maxsplit=1): leading whitespace skipped, the rest keeps its trailing whitespace.
        var i = 0;
        while (i < header.Length && PythonCompat.IsSpace(header[i]))
        {
            i++;
        }

        var schemeStart = i;
        while (i < header.Length && !PythonCompat.IsSpace(header[i]))
        {
            i++;
        }

        var scheme = header[schemeStart..i];
        while (i < header.Length && PythonCompat.IsSpace(header[i]))
        {
            i++;
        }

        if (scheme.Length == 0 || i == header.Length || !PythonCompat.LowerEquals(scheme, "bearer"))
        {
            return null;
        }

        // .strip(): the start is already past whitespace.
        var end = header.Length;
        while (end > i && PythonCompat.IsSpace(header[end - 1]))
        {
            end--;
        }

        var token = header[i..end];
        foreach (var c in token)
        {
            if (PythonCompat.IsSpace(c))
            {
                return null;
            }
        }

        return token.Length == 0 ? null : token;
    }

    /// <summary>Python: <c>JwtTokenValidator.validate_token</c>. Returns the verified claims.</summary>
    /// <exception cref="TokenRejectedException">The token is not accepted.</exception>
    /// <exception cref="KeyNotFoundException">The token has no <c>kid</c> header (Python: an uncaught
    /// <c>KeyError</c>, so the request failed with 500).</exception>
    internal async Task<JsonObject> ValidateTokenAsync(string token, CancellationToken cancellationToken)
    {
        // get_unverified_header and the unverified decode: structure only.
        var jws = CompactJws.Parse(token);

        // Routing by the unverified audience: any non-empty string selects the one connection.
        if (PythonCompat.AsString(jws.Payload["aud"]) is not { Length: > 0 })
        {
            _logger.LogWarning("No default or specific routing configuration found for the given audience.");
            throw new TokenRejectedException("ValueError", "Invalid audience.");
        }

        var jwksUri = EntraIssuers.JwksUri(PythonCompat.AsString(jws.Payload["iss"]), _settings.TenantId);
        if (!jws.Header.ContainsKey("kid"))
        {
            throw new KeyNotFoundException("the token has no kid header");
        }

        var key = await _keys.GetSigningKeyAsync(jwksUri, PythonCompat.AsString(jws.Header["kid"])!, cancellationToken);
        VerifySignature(jws, key);
        ValidateClaims(jws.Payload);

        var audience = PythonCompat.AsString(jws.Payload["aud"]);
        if (audience is not { Length: > 0 } || !PythonCompat.LowerEquals(audience, _settings.ClientId))
        {
            _logger.LogWarning("JWT audience not accepted.");
            throw new TokenRejectedException("ValueError", "Invalid audience.");
        }

        // Tenant binding (always on in the SDK): a recognised Entra issuer's tenant must equal tid.
        var tenant = EntraIssuers.EntraTenant(PythonCompat.AsString(jws.Payload["iss"]));
        if (tenant is not null && PythonCompat.AsString(jws.Payload["tid"]) is { } tid && !PythonCompat.LowerEquals(tid, tenant))
        {
            _logger.LogWarning("JWT tenant binding mismatch.");
            throw new TokenRejectedException("ValueError", "Invalid issuer.");
        }

        return jws.Payload;
    }

    /// <summary>Python: <c>PyJWS._verify_signature</c> with <c>algorithms=["RS256"]</c> and a <c>PyJWK</c>.</summary>
    private static void VerifySignature(CompactJws jws, SigningKey key)
    {
        if (!jws.Header.TryGetPropertyValue("alg", out var alg))
        {
            throw new TokenRejectedException("InvalidAlgorithmError", "Algorithm not specified");
        }

        if (!PythonCompat.IsTruthy(alg) || PythonCompat.AsString(alg) != Algorithm)
        {
            throw new TokenRejectedException("InvalidAlgorithmError", "The specified alg value is not allowed");
        }

        if (key.AlgorithmName != Algorithm)
        {
            throw new TokenRejectedException("InvalidAlgorithmError", "Token algorithm does not match the key's algorithm");
        }

        if (key.IsPrivate)
        {
            throw new InvalidOperationException("a private key cannot verify"); // Python: AttributeError
        }

        if (!key.VerifyRs256(jws.SigningInput, jws.Signature))
        {
            throw new TokenRejectedException("InvalidSignatureError", "Signature verification failed");
        }
    }

    /// <summary>Python: <c>PyJWT._validate_claims</c> with <c>verify_aud</c> off and no issuer, subject
    /// or required claims: <c>iat</c>, <c>nbf</c>, <c>exp</c> (each only when present), <c>sub</c>, <c>jti</c>.</summary>
    private void ValidateClaims(JsonObject payload)
    {
        var now = UnixNow();
        var leeway = Leeway.TotalSeconds;

        // Python compares the int claim with the float bound exactly: int > t  <=>  int > floor(t).
        var notAfter = new BigInteger(Math.Floor(now + leeway));
        var expiredAt = new BigInteger(Math.Floor(now - leeway));
        if (payload.TryGetPropertyValue("iat", out var iatNode))
        {
            if (!PythonCompat.TryToInt(iatNode, out var iat))
            {
                throw new TokenRejectedException("InvalidIssuedAtError", "Issued At claim (iat) must be an integer.");
            }

            if (iat > notAfter)
            {
                throw new TokenRejectedException("ImmatureSignatureError", "The token is not yet valid (iat)");
            }
        }

        if (payload.TryGetPropertyValue("nbf", out var nbfNode))
        {
            if (!PythonCompat.TryToInt(nbfNode, out var nbf))
            {
                throw new TokenRejectedException("DecodeError", "Not Before claim (nbf) must be an integer.");
            }

            if (nbf > notAfter)
            {
                throw new TokenRejectedException("ImmatureSignatureError", "The token is not yet valid (nbf)");
            }
        }

        if (payload.TryGetPropertyValue("exp", out var expNode))
        {
            if (!PythonCompat.TryToInt(expNode, out var exp))
            {
                throw new TokenRejectedException("DecodeError", "Expiration Time claim (exp) must be an integer.");
            }

            if (exp <= expiredAt)
            {
                throw new TokenRejectedException("ExpiredSignatureError", "Signature has expired");
            }
        }

        if (payload.TryGetPropertyValue("sub", out var sub) && PythonCompat.AsString(sub) is null)
        {
            throw new TokenRejectedException("InvalidSubjectError", "Subject must be a string");
        }

        if (payload.TryGetPropertyValue("jti", out var jti) && PythonCompat.AsString(jti) is null)
        {
            throw new TokenRejectedException("InvalidJTIError", "JWT ID must be a string");
        }
    }

    /// <summary>Python: <c>datetime.now(tz=timezone.utc).timestamp()</c>, microsecond resolution.</summary>
    private double UnixNow()
    {
        var microseconds = (_time.GetUtcNow().UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10;
        return microseconds / 1_000_000.0;
    }
}

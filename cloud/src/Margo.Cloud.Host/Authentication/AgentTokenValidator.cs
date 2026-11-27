// Copied from Margo.Cloud.Router (namespace changed only): the Agents SDK token check the Python host also ran.
// Keep the copies in step; see Margo.Cloud.Host/PORTING.md, "Requests to other components".
using System.Numerics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Margo.Cloud.Host.Authentication;

/// <summary>The blueprint the router accepts tokens for. Python:
/// <c>AgentAuthConfiguration(client_id=..., tenant_id=...)</c>.</summary>
/// <param name="ClientId">The agent blueprint's client ID: the only accepted audience.</param>
/// <param name="TenantId">The tenant whose Entra signing keys are read for Entra-issued tokens, and the only
/// tenant whose Entra issuers are accepted.</param>
/// <param name="ExtraIssuers">Issuers accepted besides the defaults (<see cref="AgentTokenValidator.AcceptedIssuers"/>),
/// from <c>MARGO_EXTRA_TOKEN_ISSUERS</c>: a recovery setting if the platform signs with an issuer this version does not
/// know. Another tenant's Entra issuer is refused at start-up.</param>
/// <param name="PlatformAppIds">The applications (<c>appid</c> or <c>azp</c>) an Entra-issued platform token may come
/// from, from <c>MARGO_PLATFORM_APP_IDS</c>. Empty: no Entra-issued token is accepted (only Bot Framework's own
/// deliveries); the refusal names the calling application so an operator can confirm and pin it. Never a token issued
/// to a signed-in person (one with <c>scp</c>).</param>
public sealed record AgentAuthSettings(string ClientId, string TenantId, IReadOnlyList<string>? ExtraIssuers = null,
    IReadOnlyList<string>? PlatformAppIds = null);

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
/// <item>The issuer must be Bot Framework's (<c>https://api.botframework.com</c>) or Entra's for the configured
/// tenant (v1 or v2), or one of the configured extra issuers. This is a deliberate deviation from the SDK, whose
/// <c>VALIDATE_ISSUER</c> was off: Entra signs every tenant's tokens with the same keys, so without it a token from
/// another tenant was refused only by its audience. A token from a recognised Entra issuer must also carry the same
/// tenant in <c>tid</c>, when it has one.</item>
/// <item>An Entra-issued token must come from the platform, not from a person (a deliberate deviation): with
/// its <c>appid</c> or <c>azp</c> must be one of <c>MARGO_PLATFORM_APP_IDS</c>, and while that is empty every
/// Entra-issued token is refused. Any application in the tenant can get its own token for the blueprint, so the
/// audience and the tenant do not show that the caller is the platform; only the pinned application does. The blueprint is also the audience of sign-ins that are not platform deliveries (the voice page's
/// scope, when the page signs in as the blueprint), and the sender of an activity is read from its body, so a person's
/// token must never be accepted here.</item>
/// </list>
/// A token without <c>kid</c> is refused (401). The SDK failed with 500 there (an uncaught <c>KeyError</c>); that is
/// not kept, because it let anyone fill the error log without a valid token.
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

    private const int MaxLoggedIssuerLength = 200;

    private readonly AgentAuthSettings _settings;
    private readonly IReadOnlyList<string> _issuers;
    private readonly HashSet<string> _platformApps;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _seenCallers = new(StringComparer.Ordinal);
    private readonly JwksClientManager _keys;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public AgentTokenValidator(AgentAuthSettings settings, JwksClientManager keys, ILogger logger, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(logger);
        _settings = settings;
        _issuers = AcceptedIssuers(settings);
        _platformApps = new HashSet<string>(
            (settings.PlatformAppIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()), StringComparer.OrdinalIgnoreCase);
        _keys = keys;
        _logger = logger;
        if (_platformApps.Count == 0)
        {
            _logger.LogWarning("MARGO_PLATFORM_APP_IDS is empty: Entra-issued platform tokens are refused until it names the platform's application. Each refusal logs the calling application.");
        }
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
            _logger.LogWarning("JWT has no kid.");
            throw new TokenRejectedException("KeyError", "kid");
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

        var issuer = jws.Payload["iss"] is JsonValue issuerValue && issuerValue.TryGetValue<string>(out var text) ? text : null;
        if (issuer is null || !_issuers.Contains(issuer, StringComparer.OrdinalIgnoreCase))
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning("JWT issuer not accepted: {Issuer}", Printable(issuer));
            }

            throw new TokenRejectedException("ValueError", "Invalid issuer.");
        }

        CheckCaller(jws.Payload, issuer);

        // Tenant binding (always on in the SDK): a recognised Entra issuer's tenant must equal tid.
        var tenant = EntraIssuers.EntraTenant(PythonCompat.AsString(jws.Payload["iss"]));
        if (tenant is not null && PythonCompat.AsString(jws.Payload["tid"]) is { } tid && !PythonCompat.LowerEquals(tid, tenant))
        {
            _logger.LogWarning("JWT tenant binding mismatch.");
            throw new TokenRejectedException("ValueError", "Invalid issuer.");
        }

        return jws.Payload;
    }

    /// <summary>The platform, not a person: see the class remarks. Bot Framework's own issuer signs only its service's
    /// deliveries, so the check applies to Entra-issued tokens.</summary>
    private void CheckCaller(JsonObject payload, string issuer)
    {
        if (string.Equals(issuer, EntraIssuers.BotFrameworkPublicIssuer, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var app = PythonCompat.AsString(payload["appid"]) ?? PythonCompat.AsString(payload["azp"]);
        if (_platformApps.Count > 0)
        {
            if (app is null || !_platformApps.Contains(app))
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning("JWT caller application not accepted: {Application}", Printable(app));
                }

                throw new TokenRejectedException("ValueError", "Invalid caller.");
            }

            return;
        }

        if (payload.ContainsKey("scp"))
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning("JWT is a person's token (scp), not the platform's; application {Application}", Printable(app));
            }

            throw new TokenRejectedException("ValueError", "Invalid caller.");
        }

        // Unpinned: refused. The audience and tenant only show that some application in this tenant asked for a
        // token for the blueprint, which any of them can; the sender of an activity is then read from its body. Say
        // once per application who called, so an operator can confirm it is the platform and pin it.
        var caller = Printable(app);
        if (_seenCallers.Count < 16 && _seenCallers.TryAdd(caller, true) && _logger.IsEnabled(LogLevel.Warning))
        {
            _logger.LogWarning("Platform token refused from application {Application}: MARGO_PLATFORM_APP_IDS is empty. Confirm this is the Agent 365 platform's application in your tenant, then set MARGO_PLATFORM_APP_IDS to it.", caller);
        }

        throw new TokenRejectedException("ValueError", "Invalid caller.");
    }

    /// <summary>The issuers a token may come from: Bot Framework (public cloud), the configured tenant's Entra v1 and v2
    /// issuers, and any configured extras.</summary>
    public static IReadOnlyList<string> AcceptedIssuers(AgentAuthSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var tenant = settings.TenantId.Trim().ToLowerInvariant();
        List<string> issuers = [EntraIssuers.BotFrameworkPublicIssuer, "https://sts.windows.net/" + tenant + "/", "https://login.microsoftonline.com/" + tenant + "/v2.0"];
        foreach (var extra in settings.ExtraIssuers ?? [])
        {
            if (string.IsNullOrWhiteSpace(extra))
            {
                continue;
            }

            // Never another tenant: Entra signs every tenant's tokens with the same keys.
            if (EntraIssuers.EntraTenant(extra.Trim()) is { } other && other != tenant)
            {
                throw new ArgumentException("MARGO_EXTRA_TOKEN_ISSUERS names another tenant's issuer: " + extra.Trim(), nameof(settings));
            }

            issuers.Add(extra.Trim());
        }

        return issuers;
    }

    /// <summary>An unverified issuer, safe to log: printable ASCII only, and not too long.</summary>
    private static string Printable(string? issuer)
    {
        if (issuer is null)
        {
            return "(none or not a string)";
        }

        var text = issuer.Length > MaxLoggedIssuerLength ? issuer[..MaxLoggedIssuerLength] : issuer;
        return text.All(c => c is >= ' ' and <= '~') ? text : "(unprintable)";
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

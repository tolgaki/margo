using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.Voice.Jwt;

/// <summary>
/// <c>VoiceService._verify</c>: the person's sign-in, checked as PyJWT 2.15 checked it, then
/// <see cref="VoiceClaims.Check"/>.
/// </summary>
/// <remarks>
/// Python:
/// <code>
/// jwks = jwt.PyJWKClient("https://login.microsoftonline.com/&lt;tenant&gt;/discovery/v2.0/keys")
/// key = jwks.get_signing_key_from_jwt(token).key
/// claims = jwt.decode(token, key, algorithms=["RS256"], audience=[client_id, "api://" + client_id],
///                     issuer=["https://login.microsoftonline.com/&lt;tenant&gt;/v2.0", "https://sts.windows.net/&lt;tenant&gt;/"],
///                     options={"require": ["exp", "iat", "aud", "iss"]}, leeway=60)
/// </code>
/// In order: the token is read without verification (structure, header, a JSON object payload) and the signing key is
/// looked up by its <c>kid</c> (<see cref="JwksClient"/>); <c>alg</c> must be exactly <c>RS256</c> and the signature
/// must verify with that key (an RSA public key; a private RSA key or an EC/OKP key fails with an error PyJWT does not
/// raise as its own, an <c>oct</c> key is refused); <c>exp</c>, <c>iat</c>, <c>aud</c> and <c>iss</c> must be present
/// and not null; <c>iat</c> and <c>nbf</c> (Python <c>int()</c> of the claim) not after now + 60 s; <c>exp</c> after
/// now - 60 s; <c>iss</c> a string equal to one of the two issuers; <c>aud</c> a non-empty string or list of strings
/// holding one of the two audiences; <c>sub</c> and <c>jti</c> strings when present. A <see cref="JwtRejectedException"/>
/// (a <c>PyJWTError</c>) becomes <see cref="TokenRejectedException"/> <c>token failed verification: &lt;type&gt;</c>; any
/// other exception propagates, as in Python.
/// </remarks>
public sealed class EntraTokenVerifier : IDisposable
{
    /// <summary>PyJWT's <c>leeway</c> in seconds.</summary>
    public const double Leeway = 60;

    private static readonly string[] Required = ["exp", "iat", "aud", "iss"];

    private readonly JwksClient _keys;
    private readonly string _tenantId;
    private readonly string _clientId;
    private readonly string _scope;
    private readonly string[] _audiences;
    private readonly string[] _issuers;
    private readonly Func<double> _now;

    /// <param name="tenantId">The tenant.</param>
    /// <param name="clientId">The blueprint's application ID.</param>
    /// <param name="scope">The delegated voice scope (<c>Voice.Talk</c>).</param>
    /// <param name="source">Where the key set is read from.</param>
    /// <param name="time">The clock for the key cache; null for the system clock.</param>
    /// <param name="now">Seconds since the epoch for the claim checks; null for the system clock.</param>
    public EntraTokenVerifier(string tenantId, string clientId, string scope, IJwksSource source, TimeProvider? time = null, Func<double>? now = null)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(clientId);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(source);
        _tenantId = tenantId;
        _clientId = clientId;
        _scope = scope;
        _audiences = [clientId, "api://" + clientId];
        _issuers = ["https://login.microsoftonline.com/" + tenantId + "/v2.0", "https://sts.windows.net/" + tenantId + "/"];
        var clock = time ?? TimeProvider.System;
        _now = now ?? (() => clock.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0);
        _keys = new JwksClient(KeysUri(tenantId), source, clock);
    }

    /// <summary>The tenant's published keys.</summary>
    public static string KeysUri(string tenantId) => "https://login.microsoftonline.com/" + tenantId + "/discovery/v2.0/keys";

    /// <summary><c>_verify(token)</c>: the caller's object ID.</summary>
    /// <exception cref="TokenRejectedException">The token failed verification or its claims do not allow a voice session.</exception>
    public async Task<string> VerifyAsync(string token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        JsonObject claims;
        try
        {
            claims = await DecodeAsync(token, cancellationToken).ConfigureAwait(false);
        }
        catch (JwtRejectedException exc)
        {
            throw new TokenRejectedException("token failed verification: " + exc.Kind);
        }

        return VoiceClaims.Check(claims, _tenantId, _clientId, _scope, _now());
    }

    /// <summary>PyJWT's <c>get_signing_key_from_jwt</c> then <c>jwt.decode</c>: the verified payload.</summary>
    internal async Task<JsonObject> DecodeAsync(string token, CancellationToken cancellationToken)
    {
        // get_signing_key_from_jwt: decode_complete(token, options={"verify_signature": False}).
        var unverified = CompactJws.Parse(token);
        var kid = unverified.Header.ContainsKey("kid") ? PyNode.AsString(unverified.Header["kid"]) : null;
        var key = await _keys.GetSigningKeyAsync(kid, cancellationToken).ConfigureAwait(false);

        // jwt.decode: the same parse, then the signature, then the claims.
        var jws = CompactJws.Parse(token);
        if (!jws.Header.ContainsKey("alg"))
        {
            throw new JwtRejectedException("InvalidAlgorithmError", "Algorithm not specified");
        }

        if (!PyNode.Is(jws.Header["alg"], "RS256"))
        {
            throw new JwtRejectedException("InvalidAlgorithmError", "The specified alg value is not allowed");
        }

        switch (key.Kind)
        {
            case SigningKeyKind.Secret:
                throw new JwtRejectedException("InvalidKeyError", "Could not parse the provided public key.");
            case SigningKeyKind.Other:
                throw new InvalidOperationException("Expecting a PEM-formatted key."); // Python: TypeError
            case SigningKeyKind.RsaPrivate:
                throw new InvalidOperationException("a private key cannot verify"); // Python: AttributeError
        }

        if (!key.VerifyRs256(jws.SigningInput, jws.Signature))
        {
            throw new JwtRejectedException("InvalidSignatureError", "Signature verification failed");
        }

        ValidateClaims(jws.Payload, _now());
        return jws.Payload;
    }

    /// <summary>Python: <c>PyJWT._validate_claims</c> with the voice server's options.</summary>
    private void ValidateClaims(JsonObject payload, double now)
    {
        foreach (var claim in Required)
        {
            if (PyNode.IsNone(PyNode.Get(payload, claim)))
            {
                throw new JwtRejectedException("MissingRequiredClaimError", "Token is missing the \"" + claim + "\" claim");
            }
        }

        var iat = PythonInt(payload["iat"]) ?? throw new JwtRejectedException("InvalidIssuedAtError", "Issued At claim (iat) must be an integer.");
        if (PyNumbers.CompareIntFloat(iat, now + Leeway) > 0)
        {
            throw new JwtRejectedException("ImmatureSignatureError", "The token is not yet valid (iat)");
        }

        if (payload.ContainsKey("nbf"))
        {
            var nbf = PythonInt(payload["nbf"]) ?? throw new JwtRejectedException("DecodeError", "Not Before claim (nbf) must be an integer.");
            if (PyNumbers.CompareIntFloat(nbf, now + Leeway) > 0)
            {
                throw new JwtRejectedException("ImmatureSignatureError", "The token is not yet valid (nbf)");
            }
        }

        var exp = PythonInt(payload["exp"]) ?? throw new JwtRejectedException("DecodeError", "Expiration Time claim (exp) must be an integer.");
        if (PyNumbers.CompareIntFloat(exp, now - Leeway) <= 0)
        {
            throw new JwtRejectedException("ExpiredSignatureError", "Signature has expired");
        }

        if (PyNode.AsString(payload["iss"]) is not { } iss)
        {
            throw new JwtRejectedException("InvalidIssuerError", "Payload Issuer (iss) must be a string");
        }

        if (!_issuers.Contains(iss, StringComparer.Ordinal))
        {
            throw new JwtRejectedException("InvalidIssuerError", "Invalid issuer");
        }

        ValidateAudience(payload["aud"]);
        if (payload.ContainsKey("sub") && PyNode.AsString(payload["sub"]) is null)
        {
            throw new JwtRejectedException("InvalidSubjectError", "Subject must be a string");
        }

        if (payload.ContainsKey("jti") && PyNode.AsString(payload["jti"]) is null)
        {
            throw new JwtRejectedException("InvalidJTIError", "JWT ID must be a string");
        }
    }

    /// <summary>Python: <c>_validate_aud</c> (not strict) with the two audiences.</summary>
    private void ValidateAudience(JsonNode? aud)
    {
        if (!PyJson.Truthy(aud))
        {
            throw new JwtRejectedException("MissingRequiredClaimError", "Token is missing the \"aud\" claim");
        }

        List<string> claims;
        if (PyNode.AsString(aud) is { } single)
        {
            claims = [single];
        }
        else if (aud is JsonArray list)
        {
            claims = [];
            foreach (var item in list)
            {
                claims.Add(PyNode.AsString(item) ?? throw new JwtRejectedException("InvalidAudienceError", "Invalid claim format in token"));
            }
        }
        else
        {
            throw new JwtRejectedException("InvalidAudienceError", "Invalid claim format in token");
        }

        if (!_audiences.Any(audience => claims.Contains(audience, StringComparer.Ordinal)))
        {
            throw new JwtRejectedException("InvalidAudienceError", "Audience doesn't match");
        }
    }

    /// <summary>Python <c>int(value)</c> of a claim, or <see langword="null"/> where it raises <c>ValueError</c>,
    /// <c>TypeError</c> or <c>OverflowError</c> (all caught by PyJWT).</summary>
    internal static BigInteger? PythonInt(JsonNode? value)
    {
        switch (PyJson.KindOf(value))
        {
            case PyKind.Int:
                return PyJson.AsInt(value);
            case PyKind.Bool:
                return PyJson.Truthy(value) ? BigInteger.One : BigInteger.Zero;
            case PyKind.Float:
                var number = PyJson.AsDouble(value);
                return double.IsFinite(number) ? PyNumbers.Trunc(number) : null;
            case PyKind.Str:
                return PyArgumentParser.TryParsePyInt(PyNode.AsString(value)!, out var parsed) ? parsed : null;
            default:
                return null;
        }
    }

    public void Dispose() => _keys.Dispose();
}

/// <summary>Strict UTF-8 without a byte order mark.</summary>
internal static class Utf8Strict
{
    internal static readonly Encoding Encoding = new UTF8Encoding(false, throwOnInvalidBytes: true);
}

using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.Voice.Jwt;

/// <summary>Where the tenant's signing key set comes from. The service reads it over HTTPS
/// (<see cref="HttpJwksSource"/>); tests pass a local key set.</summary>
public interface IJwksSource
{
    /// <summary>The body of the key set document at <paramref name="uri"/>.</summary>
    /// <exception cref="JwksUnavailableException">The document could not be read (network, timeout, a status outside
    /// 2xx, or a redirect). Python: <c>PyJWKClientConnectionError</c>.</exception>
    Task<byte[]> FetchAsync(string uri, CancellationToken cancellationToken);
}

/// <summary>A key set document could not be read.</summary>
public sealed class JwksUnavailableException : Exception
{
    public JwksUnavailableException()
    {
    }

    public JwksUnavailableException(string message)
        : base(message)
    {
    }

    public JwksUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Reads key sets the way <c>PyJWKClient.fetch_data</c> did: a plain GET, redirects not followed, any status
/// outside 2xx an error, 30 seconds.</summary>
public sealed class HttpJwksSource : IJwksSource, IDisposable
{
    private readonly HttpClient _client;

    public HttpJwksSource()
    {
        // urllib sent no trace context; .NET would add the inbound request's.
        _client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ActivityHeadersPropagator = null })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public async Task<byte[]> FetchAsync(string uri, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _client.GetAsync(new Uri(uri), cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is < 200 or > 299)
            {
                throw new JwksUnavailableException("the key set endpoint answered " + (int)response.StatusCode);
            }

            return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exc)
        {
            throw new JwksUnavailableException("the key set endpoint could not be reached", exc);
        }
        catch (TaskCanceledException exc) when (!cancellationToken.IsCancellationRequested)
        {
            throw new JwksUnavailableException("the key set endpoint timed out", exc);
        }
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>What kind of key object PyJWT built, which decides what <c>RSAAlgorithm.prepare_key</c> does with it.</summary>
internal enum SigningKeyKind
{
    /// <summary>An RSA public key: verifies.</summary>
    RsaPublic,

    /// <summary>An RSA private key (<c>d</c> present): <c>verify</c> raises <c>AttributeError</c>.</summary>
    RsaPrivate,

    /// <summary>An <c>oct</c> key (bytes): not a PEM key, <c>InvalidKeyError</c>.</summary>
    Secret,

    /// <summary>An EC or OKP key object: <c>TypeError("Expecting a PEM-formatted key.")</c>.</summary>
    Other,
}

/// <summary>One usable key from a key set, as PyJWT's <c>PyJWK</c> sees it.</summary>
internal sealed class SigningKey
{
    private readonly byte[]? _modulus;
    private readonly byte[]? _exponent;

    internal SigningKey(JsonObject jwk, SigningKeyKind kind, byte[]? modulus = null, byte[]? exponent = null)
    {
        KeyId = PyNode.Copy(jwk["kid"]);
        Use = PyNode.Copy(jwk["use"]);
        Kind = kind;
        _modulus = modulus;
        _exponent = exponent;
    }

    /// <summary>The <c>kid</c> member, any JSON value.</summary>
    internal JsonNode? KeyId { get; }

    internal JsonNode? Use { get; }

    internal SigningKeyKind Kind { get; }

    /// <summary>RSASSA-PKCS1-v1_5 with SHA-256 over an RSA public key. False for a signature that does not verify or
    /// a key .NET cannot import.</summary>
    internal bool VerifyRs256(byte[] signingInput, byte[] signature)
    {
        if (_modulus is null || _exponent is null)
        {
            return false;
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters { Modulus = _modulus, Exponent = _exponent });
            return rsa.VerifyData(signingInput, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}

/// <summary>
/// The tenant's key set endpoint, cached as PyJWT 2.15's <c>PyJWKClient(uri)</c> with its defaults: the set is kept
/// for 300 seconds; an unknown <c>kid</c> forces one refresh unless a fetch succeeded in the last 30 seconds; a failed
/// fetch leaves the cache as it was. One lookup at a time (PyJWT's client lock).
/// </summary>
internal sealed class JwksClient : IDisposable
{
    internal static readonly TimeSpan Lifespan = TimeSpan.FromSeconds(300);
    internal static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);

    private static readonly HashSet<string> KnownAlgorithms = new(StringComparer.Ordinal)
    {
        "ES256", "ES256K", "ES384", "ES512", "ES521", "EdDSA", "HS256", "HS384", "HS512",
        "PS256", "PS384", "PS512", "RS256", "RS384", "RS512", "none",
    };

    private readonly string _uri;
    private readonly IJwksSource _source;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IReadOnlyList<SigningKey>? _cached;
    private long _cachedAt;
    private long? _lastSuccessfulFetch;

    internal JwksClient(string uri, IJwksSource source, TimeProvider time)
    {
        _uri = uri;
        _source = source;
        _time = time;
    }

    internal string Uri => _uri;

    public void Dispose() => _lock.Dispose();

    /// <summary>Python: <c>PyJWKClient.get_signing_key(kid)</c>; <paramref name="kid"/> is null when the token has none.</summary>
    /// <exception cref="JwtRejectedException">No key set, no signing keys, or no key with this ID.</exception>
    /// <exception cref="InvalidOperationException">An error PyJWT does not raise as a <c>PyJWTError</c> (the key set is
    /// not JSON, or holds a key whose <c>alg</c> PyJWT cannot look up).</exception>
    internal async Task<SigningKey> GetSigningKeyAsync(string? kid, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var key = Match(await GetSigningKeysAsync(refresh: false, cancellationToken).ConfigureAwait(false), kid);
            if (key is null)
            {
                var coolingDown = _lastSuccessfulFetch is { } last && _time.GetElapsedTime(last) < Cooldown;
                if (!coolingDown)
                {
                    var keys = await GetSigningKeysAsync(refresh: true, cancellationToken).ConfigureAwait(false);
                    _lastSuccessfulFetch = _time.GetTimestamp();
                    key = Match(keys, kid);
                }

                if (key is null)
                {
                    throw new JwtRejectedException("PyJWKClientError", "Unable to find a signing key that matches the token");
                }
            }

            return key;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Python: <c>PyJWKSet(keys)</c>: members that are not objects or not usable keys are skipped.</summary>
    internal static IReadOnlyList<SigningKey> ParseKeySet(JsonObject document)
    {
        var keysNode = PyNode.Get(document, "keys");
        if (!PyJson.Truthy(keysNode))
        {
            throw new JwtRejectedException("PyJWKSetError", "The JWK Set did not contain any keys");
        }

        if (keysNode is not JsonArray members)
        {
            throw new JwtRejectedException("PyJWKSetError", "Invalid JWK Set value");
        }

        var keys = new List<SigningKey>();
        foreach (var member in members)
        {
            if (member is JsonObject jwk && TryCreateKey(jwk) is { } key)
            {
                keys.Add(key);
            }
        }

        if (keys.Count == 0)
        {
            throw new JwtRejectedException("PyJWKSetError", "The JWK Set did not contain any usable keys");
        }

        return keys;
    }

    /// <summary>Python: <c>PyJWK(jwk)</c>, <see langword="null"/> where it raises a <c>PyJWTError</c>.</summary>
    private static SigningKey? TryCreateKey(JsonObject jwk)
    {
        var ktyNode = PyNode.Get(jwk, "kty");
        if (!PyJson.Truthy(ktyNode))
        {
            return null;
        }

        var kty = PyNode.AsString(ktyNode);
        var algNode = PyNode.Get(jwk, "alg");
        string? algorithm;
        if (PyJson.Truthy(algNode))
        {
            if (PyNode.AsString(algNode) is not { } named)
            {
                if (algNode is JsonObject or JsonArray)
                {
                    throw new InvalidOperationException("a key's alg is not hashable"); // Python: TypeError
                }

                return null; // a number or true: no such algorithm (PyJWKError)
            }

            algorithm = named;
        }
        else
        {
            var crv = PyNode.Get(jwk, "crv");
            var curve = PyNode.AsString(crv);
            algorithm = kty switch
            {
                "EC" when !PyJson.Truthy(crv) || curve == "P-256" => "ES256",
                "EC" when curve == "P-384" => "ES384",
                "EC" when curve == "P-521" => "ES512",
                "EC" when curve == "secp256k1" => "ES256K",
                "RSA" => "RS256",
                "oct" => "HS256",
                "OKP" when curve == "Ed25519" => "EdDSA",
                _ => null,
            };
            if (algorithm is null)
            {
                return null;
            }
        }

        if (!KnownAlgorithms.Contains(algorithm))
        {
            return null;
        }

        if (algorithm == "none")
        {
            throw new InvalidOperationException("the none algorithm has no keys"); // Python: NotImplementedError
        }

        if (algorithm[0] is 'R' or 'P')
        {
            return CreateRsaKey(jwk, kty);
        }

        // Other families never verify an RS256 token; PyJWT checks their parameters, this port only their key type.
        var (family, kind) = algorithm[0] switch
        {
            'E' when algorithm == "EdDSA" => ("OKP", SigningKeyKind.Other),
            'E' => ("EC", SigningKeyKind.Other),
            _ => ("oct", SigningKeyKind.Secret),
        };
        return kty == family ? new SigningKey(jwk, kind) : null;
    }

    /// <summary>Python: <c>RSAAlgorithm.from_jwk</c> and <c>RSAPublicNumbers(e, n).public_key()</c>.</summary>
    private static SigningKey? CreateRsaKey(JsonObject jwk, string? kty)
    {
        if (kty != "RSA" || !jwk.ContainsKey("n") || !jwk.ContainsKey("e"))
        {
            return null;
        }

        if (jwk.ContainsKey("d"))
        {
            // A private key; Entra never publishes one. PyJWT would build it and fail at verification.
            return new SigningKey(jwk, SigningKeyKind.RsaPrivate);
        }

        if (!TryUnsignedFromBase64Url(jwk["n"], out var modulus) || !TryUnsignedFromBase64Url(jwk["e"], out var exponent))
        {
            return null;
        }

        // cryptography's RSAPublicNumbers(e, n).public_key(): e at least 3, below n, and odd.
        var n = new BigInteger(modulus, isUnsigned: true, isBigEndian: true);
        var e = new BigInteger(exponent, isUnsigned: true, isBigEndian: true);
        return e < 3 || e >= n || e.IsEven ? null : new SigningKey(jwk, SigningKeyKind.RsaPublic, modulus, exponent);
    }

    /// <summary>Python: <c>from_base64url_uint</c>, as big-endian bytes without leading zeros.</summary>
    internal static bool TryUnsignedFromBase64Url(JsonNode? node, out byte[] value)
    {
        value = [];
        if (PyNode.AsString(node) is not { } text)
        {
            return false; // Python: TypeError from force_bytes
        }

        if (!PythonBase64.TryUrlsafeDecode(text, out var bytes))
        {
            return false; // binascii.Error or UnicodeEncodeError: InvalidKeyError, the key is skipped
        }

        var start = 0;
        while (start < bytes.Length - 1 && bytes[start] == 0)
        {
            start++;
        }

        value = bytes.Length == 0 ? [0] : bytes[start..];
        return true;
    }

    /// <summary>Python: <c>match_kid</c> (<c>key.key_id == kid</c>); a token without <c>kid</c> (None) matches no key,
    /// since only keys with a truthy <c>kid</c> are signing keys.</summary>
    private static SigningKey? Match(IReadOnlyList<SigningKey> keys, string? kid)
    {
        if (kid is null)
        {
            return null;
        }

        foreach (var key in keys)
        {
            if (PyNode.AsString(key.KeyId) == kid)
            {
                return key;
            }
        }

        return null;
    }

    /// <summary>Python: <c>get_signing_keys</c>: keys whose <c>use</c> is <c>sig</c> or absent and that have a truthy
    /// <c>kid</c>.</summary>
    private async Task<IReadOnlyList<SigningKey>> GetSigningKeysAsync(bool refresh, CancellationToken cancellationToken)
    {
        IReadOnlyList<SigningKey>? keys = null;
        if (!refresh && _cached is not null && _time.GetElapsedTime(_cachedAt) <= Lifespan)
        {
            keys = _cached;
        }

        keys ??= await FetchAsync(cancellationToken).ConfigureAwait(false);
        var signing = keys.Where(key => (PyNode.IsNone(key.Use) || PyNode.AsString(key.Use) == "sig") && PyJson.Truthy(key.KeyId)).ToList();
        if (signing.Count == 0)
        {
            throw new JwtRejectedException("PyJWKClientError", "The JWKS endpoint did not contain any signing keys");
        }

        return signing;
    }

    /// <summary>Python: <c>fetch_data</c>: the cache and the cooldown clock change only on success.</summary>
    private async Task<IReadOnlyList<SigningKey>> FetchAsync(CancellationToken cancellationToken)
    {
        byte[] body;
        try
        {
            body = await _source.FetchAsync(_uri, cancellationToken).ConfigureAwait(false);
        }
        catch (JwksUnavailableException exc)
        {
            throw new JwtRejectedException("PyJWKClientConnectionError", "Fail to fetch data from the url", exc);
        }

        JsonNode? document;
        try
        {
            document = PyJson.LoadsBytes(body);
        }
        catch (Exception exc) when (exc is PyValueError or PyRecursionError or JsonException)
        {
            // Python: json.load raises JSONDecodeError, which PyJWKClient does not catch: not a PyJWTError.
            throw new InvalidOperationException("the key set is not JSON", exc);
        }

        if (document is not JsonObject obj)
        {
            throw new JwtRejectedException("PyJWKClientError", "The JWKS endpoint did not return a JSON object");
        }

        var keys = ParseKeySet(obj);
        _cached = keys;
        _cachedAt = _time.GetTimestamp();
        _lastSuccessfulFetch = _cachedAt;
        return keys;
    }
}

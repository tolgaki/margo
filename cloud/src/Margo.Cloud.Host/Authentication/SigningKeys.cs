// Copied from Margo.Cloud.Router (namespace changed only): the Agents SDK token check the Python host also ran.
// Keep the copies in step; see Margo.Cloud.Host/PORTING.md, "Requests to other components".
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Margo.Cloud.Host.Authentication;

/// <summary>Where signing key sets come from. The service reads them over HTTPS
/// (<see cref="HttpJwksSource"/>); tests pass a local key set.</summary>
public interface IJwksSource
{
    /// <summary>The body of the key set document at <paramref name="uri"/>.</summary>
    /// <exception cref="JwksUnavailableException">The document could not be read (network, timeout,
    /// non-2xx status or redirect). Python: <c>PyJWKClientConnectionError</c>.</exception>
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

/// <summary>Reads key sets the way <c>PyJWKClient.fetch_data</c> did: a plain GET, redirects not
/// followed, any status outside 2xx an error, 30 seconds.</summary>
public sealed class HttpJwksSource : IJwksSource, IDisposable
{
    private readonly HttpClient _client;

    public HttpJwksSource()
    {
        // urllib sent no trace context; .NET would add the inbound request's.
        _client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ActivityHeadersPropagator = null }) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<byte[]> FetchAsync(string uri, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _client.GetAsync(new Uri(uri), cancellationToken);
            if ((int)response.StatusCode is < 200 or > 299)
            {
                throw new JwksUnavailableException("the key set endpoint answered " + (int)response.StatusCode);
            }

            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
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

/// <summary>One usable key from a key set, as PyJWT's <c>PyJWK</c> sees it.</summary>
internal sealed class SigningKey
{
    private readonly byte[]? _modulus;
    private readonly byte[]? _exponent;

    internal SigningKey(JsonObject jwk, string algorithm, byte[]? modulus, byte[]? exponent, bool isPrivate)
    {
        KeyId = jwk["kid"]?.DeepClone();
        Use = jwk["use"]?.DeepClone();
        AlgorithmName = algorithm;
        _modulus = modulus;
        _exponent = exponent;
        IsPrivate = isPrivate;
    }

    /// <summary>The <c>kid</c> member, any JSON value.</summary>
    internal JsonNode? KeyId { get; }

    internal JsonNode? Use { get; }

    /// <summary>The key's <c>alg</c>, or the algorithm its <c>kty</c> implies.</summary>
    internal string AlgorithmName { get; }

    /// <summary>A private RSA key (<c>d</c> present); PyJWT cannot verify with it (AttributeError).</summary>
    internal bool IsPrivate { get; }

    /// <summary>RSASSA-PKCS1-v1_5 with SHA-256. False for a signature that does not verify or a key
    /// .NET cannot import (Python's <c>cryptography</c> accepts a few that .NET does not; neither
    /// verifies a real signature with them).</summary>
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
/// One key set endpoint, cached as PyJWT 2.15's <c>PyJWKClient(uri)</c> with its defaults: the set is
/// kept for 300 seconds; an unknown <c>kid</c> forces one refresh unless a fetch succeeded in the last
/// 30 seconds; a failed fetch leaves the cache as it was. One lookup at a time per endpoint.
/// </summary>
internal sealed class JwksClient : IDisposable
{
    internal static readonly TimeSpan Lifespan = TimeSpan.FromSeconds(300);
    internal static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);

    private static readonly HashSet<string> KnownAlgorithms =
    [
        "ES256", "ES256K", "ES384", "ES512", "ES521", "EdDSA", "HS256", "HS384", "HS512",
        "PS256", "PS384", "PS512", "RS256", "RS384", "RS512", "none",
    ];

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

    public void Dispose() => _lock.Dispose();

    /// <summary>Python: <c>PyJWKClient.get_signing_key(kid)</c>.</summary>
    /// <exception cref="TokenRejectedException">No key set, no signing keys, or no key with this id.</exception>
    internal async Task<SigningKey> GetSigningKeyAsync(string kid, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var key = Match(await GetSigningKeysAsync(refresh: false, cancellationToken), kid);
            if (key is null)
            {
                var coolingDown = _lastSuccessfulFetch is { } last && _time.GetElapsedTime(last) < Cooldown;
                if (!coolingDown)
                {
                    var keys = await GetSigningKeysAsync(refresh: true, cancellationToken);
                    _lastSuccessfulFetch = _time.GetTimestamp();
                    key = Match(keys, kid);
                }

                if (key is null)
                {
                    throw new TokenRejectedException("PyJWKClientError", "Unable to find a signing key that matches the token");
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
    /// <exception cref="TokenRejectedException">No keys, not a list, or nothing usable (<c>PyJWKSetError</c>).</exception>
    /// <exception cref="InvalidOperationException">An <c>alg</c> that is a list or object, or <c>none</c>:
    /// PyJWT raises an error the Agents SDK does not catch (the request fails with 500).</exception>
    internal static IReadOnlyList<SigningKey> ParseKeySet(JsonObject document)
    {
        var keysNode = document["keys"];
        if (!PythonCompat.IsTruthy(keysNode))
        {
            throw new TokenRejectedException("PyJWKSetError", "The JWK Set did not contain any keys");
        }

        if (keysNode is not JsonArray members)
        {
            throw new TokenRejectedException("PyJWKSetError", "Invalid JWK Set value");
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
            throw new TokenRejectedException("PyJWKSetError", "The JWK Set did not contain any usable keys");
        }

        return keys;
    }

    /// <summary>Python: <c>PyJWK(jwk)</c>, <see langword="null"/> where it raises a <c>PyJWTError</c>.</summary>
    private static SigningKey? TryCreateKey(JsonObject jwk)
    {
        var ktyNode = jwk["kty"];
        if (!PythonCompat.IsTruthy(ktyNode))
        {
            return null;
        }

        var kty = PythonCompat.AsString(ktyNode);
        var algNode = jwk["alg"];
        string? algorithm;
        if (PythonCompat.IsTruthy(algNode))
        {
            if (algNode is not JsonValue || algNode.GetValueKind() != JsonValueKind.String)
            {
                if (algNode is JsonObject or JsonArray)
                {
                    throw new InvalidOperationException("a key's alg is not hashable"); // Python: TypeError
                }

                return null; // a number or true: no such algorithm (PyJWKError)
            }

            algorithm = algNode.GetValue<string>();
        }
        else
        {
            var crv = jwk["crv"];
            var curve = PythonCompat.AsString(crv);
            algorithm = kty switch
            {
                "EC" when !PythonCompat.IsTruthy(crv) || curve == "P-256" => "ES256",
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
            return CreateRsaKey(jwk, kty, algorithm);
        }

        // Other families never verify an RS256 token; PyJWT checks their parameters, this port only
        // their key type (see PORTING.md).
        var family = algorithm[0] switch
        {
            'E' when algorithm == "EdDSA" => "OKP",
            'E' => "EC",
            _ => "oct",
        };
        return kty == family ? new SigningKey(jwk, algorithm, null, null, isPrivate: false) : null;
    }

    /// <summary>Python: <c>RSAAlgorithm.from_jwk</c> and <c>RSAPublicNumbers(e, n).public_key()</c>.</summary>
    private static SigningKey? CreateRsaKey(JsonObject jwk, string? kty, string algorithm)
    {
        if (kty != "RSA" || !jwk.ContainsKey("n") || !jwk.ContainsKey("e"))
        {
            return null;
        }

        if (jwk.ContainsKey("d"))
        {
            // A private key; Entra never publishes one. PyJWT would build it and fail at verification.
            return new SigningKey(jwk, algorithm, null, null, isPrivate: true);
        }

        if (!TryUnsignedFromBase64Url(jwk["n"], out var modulus) || !TryUnsignedFromBase64Url(jwk["e"], out var exponent))
        {
            return null;
        }

        // cryptography's RSAPublicNumbers(e, n).public_key(): e at least 3, below n, and odd.
        var n = new BigInteger(modulus, isUnsigned: true, isBigEndian: true);
        var e = new BigInteger(exponent, isUnsigned: true, isBigEndian: true);
        return e < 3 || e >= n || e.IsEven ? null : new SigningKey(jwk, algorithm, modulus, exponent, isPrivate: false);
    }

    /// <summary>Python: <c>from_base64url_uint</c>, as big-endian bytes without leading zeros.</summary>
    internal static bool TryUnsignedFromBase64Url(JsonNode? node, out byte[] value)
    {
        value = [];
        if (PythonCompat.AsString(node) is not { } text)
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

    private static SigningKey? Match(IReadOnlyList<SigningKey> keys, string kid)
    {
        foreach (var key in keys)
        {
            if (PythonCompat.AsString(key.KeyId) == kid)
            {
                return key;
            }
        }

        return null;
    }

    /// <summary>Python: <c>get_signing_keys</c>: keys whose <c>use</c> is <c>sig</c> or absent and that
    /// have a truthy <c>kid</c>.</summary>
    private async Task<IReadOnlyList<SigningKey>> GetSigningKeysAsync(bool refresh, CancellationToken cancellationToken)
    {
        IReadOnlyList<SigningKey>? keys = null;
        if (!refresh && _cached is not null && _time.GetElapsedTime(_cachedAt) <= Lifespan)
        {
            keys = _cached;
        }

        keys ??= await FetchAsync(cancellationToken);
        var signing = keys.Where(key => (key.Use is null || PythonCompat.AsString(key.Use) == "sig") && PythonCompat.IsTruthy(key.KeyId)).ToList();
        if (signing.Count == 0)
        {
            throw new TokenRejectedException("PyJWKClientError", "The JWKS endpoint did not contain any signing keys");
        }

        return signing;
    }

    /// <summary>Python: <c>fetch_data</c>: the cache and the cooldown clock change only on success.</summary>
    private async Task<IReadOnlyList<SigningKey>> FetchAsync(CancellationToken cancellationToken)
    {
        byte[] body;
        try
        {
            body = await _source.FetchAsync(_uri, cancellationToken);
        }
        catch (JwksUnavailableException exc)
        {
            throw new TokenRejectedException("PyJWKClientConnectionError", "Fail to fetch data from the url", exc);
        }

        JsonNode? document;
        try
        {
            document = PythonJsonBytes.Loads(body);
        }
        catch (JsonException exc)
        {
            throw new TokenRejectedException("JSONDecodeError", "The key set is not JSON", exc);
        }

        if (document is not JsonObject obj)
        {
            throw new TokenRejectedException("PyJWKClientError", "The JWKS endpoint did not return a JSON object");
        }

        var keys = ParseKeySet(obj);
        _cached = keys;
        _cachedAt = _time.GetTimestamp();
        _lastSuccessfulFetch = _cachedAt;
        return keys;
    }
}

/// <summary>One <see cref="JwksClient"/> per endpoint for the life of the service. Python: the
/// Agents SDK's class-level <c>_JwkClientManager</c>.</summary>
public sealed class JwksClientManager : IDisposable
{
    private readonly Dictionary<string, JwksClient> _clients = new(StringComparer.Ordinal);
    private readonly Lock _clientsLock = new();
    private readonly IJwksSource _source;
    private readonly TimeProvider _time;

    public JwksClientManager(IJwksSource source, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _time = timeProvider ?? TimeProvider.System;
    }

    public void Dispose()
    {
        lock (_clientsLock)
        {
            foreach (var client in _clients.Values)
            {
                client.Dispose();
            }

            _clients.Clear();
        }
    }

    internal Task<SigningKey> GetSigningKeyAsync(string jwksUri, string kid, CancellationToken cancellationToken)
    {
        JwksClient? client;
        lock (_clientsLock)
        {
            if (!_clients.TryGetValue(jwksUri, out client))
            {
                client = new JwksClient(jwksUri, _source, _time);
                _clients.Add(jwksUri, client);
            }
        }

        return client.GetSigningKeyAsync(kid, cancellationToken);
    }
}

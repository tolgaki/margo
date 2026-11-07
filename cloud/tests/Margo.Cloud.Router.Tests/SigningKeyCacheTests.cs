using System.Text;
using System.Text.Json.Nodes;
using Margo.Cloud.Router.Authentication;
using Margo.Cloud.Router.Tests.TestSupport;
using Xunit;

namespace Margo.Cloud.Router.Tests;

/// <summary>
/// The key set cache, as PyJWT 2.15's <c>PyJWKClient</c> keeps it under the Agents SDK. The sequence
/// of fetches and outcomes was observed with the Python SDK on a hand-moved monotonic clock.
/// </summary>
public sealed class SigningKeyCacheTests
{
    private static readonly System.Security.Cryptography.RSA Key3 = System.Security.Cryptography.RSA.Create(2048);

    private readonly ManualTime _time = new();
    private readonly FakeJwksSource _jwks = new();
    private readonly AgentTokenValidator _validator;

    public SigningKeyCacheTests()
    {
        _validator = new AgentTokenValidator(new AgentAuthSettings(Tokens.ClientId, Tokens.TenantId), new JwksClientManager(_jwks, _time),
            new ListLoggerProvider().CreateLogger("auth"), _time);
    }

    private async Task<int> Status(string kid, System.Security.Cryptography.RSA? key = null) =>
        (await _validator.AuthorizeAsync("Bearer " + Tokens.Valid(_time.GetUtcNow(), kid: kid, key: key), TestContext.Current.CancellationToken)).StatusCode;

    [Fact]
    public async Task Unknown_keys_refresh_after_the_cooldown_and_sets_expire_after_five_minutes()
    {
        Assert.Equal(200, await Status("k1"));
        Assert.Single(_jwks.Fetched);

        Assert.Equal(401, await Status("k3", Key3)); // within 30 s of the fetch: no refresh
        Assert.Single(_jwks.Fetched);

        _time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(401, await Status("k3", Key3)); // refreshed, still unknown
        Assert.Equal(2, _jwks.Fetched.Count);

        _jwks.Document = _ => Encoding.UTF8.GetBytes(new JsonObject { ["keys"] = new JsonArray(Tokens.Jwk(Tokens.Key1, "k1"), Tokens.Jwk(Key3, "k3")) }.ToJsonString());
        Assert.Equal(401, await Status("k3", Key3)); // the refresh just now starts a new cooldown
        Assert.Equal(2, _jwks.Fetched.Count);

        _time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(200, await Status("k3", Key3));
        Assert.Equal(3, _jwks.Fetched.Count);

        _time.Advance(TimeSpan.FromSeconds(300));
        Assert.Equal(200, await Status("k1")); // exactly 300 s after the last fetch: still cached
        Assert.Equal(3, _jwks.Fetched.Count);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(200, await Status("k1")); // expired, read again
        Assert.Equal(4, _jwks.Fetched.Count);
        Assert.Equal(200, await Status("k1"));
        Assert.Equal(4, _jwks.Fetched.Count);
    }

    [Fact]
    public async Task A_failed_refresh_keeps_the_cached_set()
    {
        Assert.Equal(200, await Status("k1"));
        _time.Advance(TimeSpan.FromSeconds(31));
        _jwks.Down = true;
        Assert.Equal(401, await Status("k3", Key3)); // the refresh fails
        Assert.Equal(200, await Status("k1")); // the set read before is still used
        Assert.Equal(2, _jwks.Fetched.Count);

        _time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(401, await Status("k3", Key3)); // a failed fetch did not start a cooldown
        Assert.Equal(3, _jwks.Fetched.Count);
    }

    [Fact]
    public void Key_sets_are_read_as_pyjwk_reads_them()
    {
        static IReadOnlyList<string> Algorithms(string json) =>
            JwksClient.ParseKeySet((JsonObject)JsonNode.Parse(json)!).Select(key => key.AlgorithmName).ToList();

        var rsa = Tokens.Jwk(Tokens.Key1, "k1");
        var n = rsa["n"]!.GetValue<string>();
        using var ec = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var point = ec.ExportParameters(includePrivateParameters: false).Q;
        var ecJwk = "{\"kty\": \"EC\", \"crv\": \"P-256\", \"x\": \"" + Tokens.B64(point.X!) + "\", \"y\": \"" + Tokens.B64(point.Y!) + "\"}";
        Assert.Equal(["RS256", "PS256", "ES256", "HS256", "RS256"], Algorithms(
            "{\"keys\": [" + rsa.ToJsonString() + ", {\"kty\": \"RSA\", \"alg\": \"PS256\", \"n\": \"" + n + "\", \"e\": \"AQAB\"},"
            + " " + ecJwk + ", {\"kty\": \"oct\", \"k\": \"c2VjcmV0\"},"
            + " 7, {\"kty\": \"RSA\", \"n\": \"" + n + "\"}, {\"kty\": \"RSA\", \"n\": \"" + n + "\", \"e\": \"Ag\"}, {\"alg\": \"RS256\"},"
            + " {\"kty\": \"EC\", \"crv\": \"P-999\"}, {\"kty\": \"RSA\", \"alg\": \"XX256\", \"n\": \"" + n + "\", \"e\": \"AQAB\"},"
            + " {\"kty\": \"EC\", \"alg\": \"RS256\", \"n\": \"" + n + "\", \"e\": \"AQAB\"},"
            + " {\"kty\": \"RSA\", \"alg\": 0, \"n\": \"" + n + "\", \"e\": \"AQ AB\"}]}"));

        Assert.Throws<TokenRejectedException>(() => JwksClient.ParseKeySet(new JsonObject()));
        Assert.Throws<TokenRejectedException>(() => JwksClient.ParseKeySet(new JsonObject { ["keys"] = "abc" }));
        Assert.Throws<TokenRejectedException>(() => JwksClient.ParseKeySet(new JsonObject { ["keys"] = new JsonArray(new JsonObject { ["kty"] = "RSA" }) }));

        // PyJWT raises errors the SDK does not catch for these (TypeError, NotImplementedError): 500.
        Assert.Throws<InvalidOperationException>(() => JwksClient.ParseKeySet(new JsonObject { ["keys"] = new JsonArray(new JsonObject { ["kty"] = "RSA", ["alg"] = new JsonArray("RS256") }) }));
        Assert.Throws<InvalidOperationException>(() => JwksClient.ParseKeySet(new JsonObject { ["keys"] = new JsonArray(new JsonObject { ["kty"] = "oct", ["alg"] = "none" }) }));
    }

    [Fact]
    public async Task Only_signing_keys_with_an_id_are_used()
    {
        var k1 = Tokens.Jwk(Tokens.Key1, "k1");
        k1["use"] = "enc";
        _jwks.Document = _ => Encoding.UTF8.GetBytes(new JsonObject { ["keys"] = new JsonArray(k1) }.ToJsonString());
        Assert.Equal(401, await Status("k1"));

        var anonymous = Tokens.Jwk(Tokens.Key1, "k1");
        anonymous.Remove("kid");
        _time.Advance(TimeSpan.FromSeconds(301));
        _jwks.Document = _ => Encoding.UTF8.GetBytes(new JsonObject { ["keys"] = new JsonArray(anonymous) }.ToJsonString());
        Assert.Equal(401, await Status("k1"));

        var noUse = Tokens.Jwk(Tokens.Key1, "k1");
        noUse["use"] = null;
        _time.Advance(TimeSpan.FromSeconds(301));
        _jwks.Document = _ => Encoding.UTF8.GetBytes(new JsonObject { ["keys"] = new JsonArray(noUse) }.ToJsonString());
        Assert.Equal(200, await Status("k1"));
    }
}

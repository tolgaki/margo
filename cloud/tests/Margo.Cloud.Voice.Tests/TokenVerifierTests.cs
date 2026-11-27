using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using Margo.Cloud.Voice.Jwt;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Margo.Cloud.Voice.Tests;

/// <summary>
/// <c>VoiceService._verify</c>: PyJWT's checks with the voice server's arguments, then <c>check_claims</c>, against
/// tokens signed in the test with keys served as the tenant's key set.
/// </summary>
public sealed class TokenVerifierTests : IDisposable
{
    private const double Now = 1_800_000_000;
    private readonly TestKeys _keys = new();
    private readonly EntraTokenVerifier _verifier;

    public TokenVerifierTests()
    {
        _verifier = new EntraTokenVerifier(Fixtures.Tenant, Fixtures.Client, "Voice.Talk", _keys, now: () => Now);
    }

    public void Dispose()
    {
        _verifier.Dispose();
        _keys.Dispose();
    }

    private static JsonObject Claims(params (string Key, JsonNode? Value)[] overrides)
    {
        var claims = TestKeys.Claims(Now);
        foreach (var (key, value) in overrides)
        {
            if (value is null && key.StartsWith('-'))
            {
                claims.Remove(key[1..]);
            }
            else
            {
                claims[key] = value;
            }
        }

        return claims;
    }

    private Task<string> Verify(string token) => _verifier.VerifyAsync(token, TestContext.Current.CancellationToken);

    private async Task<string> Refused(string token) => (await Assert.ThrowsAsync<TokenRejectedException>(() => Verify(token))).Message;

    [Fact]
    public async Task A_valid_sign_in_names_the_caller_and_the_keys_are_cached()
    {
        Assert.Equal(Fixtures.Manager, await Verify(_keys.Mint(Claims())));
        Assert.Equal(Fixtures.Manager, await Verify(_keys.Mint(Claims(("iss", "https://sts.windows.net/" + Fixtures.Tenant + "/"), ("aud", Fixtures.Client)))));
        Assert.Equal(["https://login.microsoftonline.com/fictional-tenant/discovery/v2.0/keys"], _keys.Fetches);
    }

    [Fact]
    public async Task A_token_made_by_microsoft_libraries_is_accepted()
    {
        var key = new RsaSecurityKey(_keys.Rsa) { KeyId = _keys.Kid };
        var issued = DateTimeOffset.FromUnixTimeSeconds((long)Now - 60).UtcDateTime;
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "https://login.microsoftonline.com/" + Fixtures.Tenant + "/v2.0",
            Audience = "api://" + Fixtures.Client,
            IssuedAt = issued,
            NotBefore = issued,
            Expires = issued.AddHours(1),
            Subject = new ClaimsIdentity([new Claim("tid", Fixtures.Tenant), new Claim("oid", Fixtures.Manager), new Claim("scp", "openid Voice.Talk")]),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
        });
        Assert.Equal(Fixtures.Manager, await Verify(token));
    }

    [Fact]
    public async Task Issuer_audience_and_required_claims()
    {
        Assert.Equal("token failed verification: InvalidIssuerError", await Refused(_keys.Mint(Claims(("iss", "https://login.microsoftonline.com/other/v2.0")))));
        Assert.Equal("token failed verification: InvalidIssuerError", await Refused(_keys.Mint(Claims(("iss", 5)))));
        Assert.Equal("token failed verification: InvalidAudienceError", await Refused(_keys.Mint(Claims(("aud", "api://other")))));
        Assert.Equal("token failed verification: InvalidAudienceError", await Refused(_keys.Mint(Claims(("aud", new JsonArray(Fixtures.Client, 5))))));
        // PyJWT accepts a list holding the audience; check_claims then wants a single value.
        Assert.Equal("token is for another application", await Refused(_keys.Mint(Claims(("aud", new JsonArray("x", Fixtures.Client))))));
        foreach (var claim in new[] { "exp", "iat", "aud", "iss" })
        {
            Assert.Equal("token failed verification: MissingRequiredClaimError", await Refused(_keys.Mint(Claims(("-" + claim, null)))));
            Assert.Equal("token failed verification: MissingRequiredClaimError", await Refused(_keys.Mint(Claims((claim, JsonValue.Create((string?)null))))));
        }

        Assert.Equal("token failed verification: MissingRequiredClaimError", await Refused(_keys.Mint(Claims(("aud", "")))));
        Assert.Equal("token failed verification: InvalidSubjectError", await Refused(_keys.Mint(Claims(("sub", 7)))));
        Assert.Equal("token failed verification: InvalidJTIError", await Refused(_keys.Mint(Claims(("jti", new JsonArray())))));
    }

    [Fact]
    public async Task Times_with_sixty_seconds_of_leeway()
    {
        Assert.Equal(Fixtures.Manager, await Verify(_keys.Mint(Claims(("iat", (long)Now + 60), ("nbf", (long)Now + 60)))));
        Assert.Equal("token failed verification: ImmatureSignatureError", await Refused(_keys.Mint(Claims(("iat", (long)Now + 61)))));
        Assert.Equal("token failed verification: ImmatureSignatureError", await Refused(_keys.Mint(Claims(("nbf", (long)Now + 61)))));
        // Inside the leeway PyJWT accepts an expired token; check_claims refuses it.
        Assert.Equal("token has expired", await Refused(_keys.Mint(Claims(("exp", (long)Now - 30)))));
        Assert.Equal("token failed verification: ExpiredSignatureError", await Refused(_keys.Mint(Claims(("exp", (long)Now - 60)))));
        // Python int(): strings of digits, floats (truncated) and booleans are read; anything else is refused.
        Assert.Equal(Fixtures.Manager, await Verify(_keys.Mint(Claims(("iat", " 1_799_999_000 "), ("nbf", 1_799_999_000.9)))));
        Assert.Equal("token failed verification: InvalidIssuedAtError", await Refused(_keys.Mint(Claims(("iat", "soon")))));
        Assert.Equal("token failed verification: InvalidIssuedAtError", await Refused(_keys.Mint(Claims(("iat", new JsonObject())))));
        Assert.Equal("token failed verification: DecodeError", await Refused(_keys.Mint(Claims(("nbf", "later")))));
        Assert.Equal("token failed verification: DecodeError", await Refused(_keys.Mint(Claims(("exp", new JsonArray())))));
        // PyJWT reads exp "1900000000" as an int; check_claims wants a number.
        Assert.Equal("token has expired", await Refused(_keys.Mint(Claims(("exp", "1900000000")))));
    }

    [Fact]
    public async Task Algorithm_signature_and_structure()
    {
        var hs = _keys.Mint(Claims(), new JsonObject { ["alg"] = "HS256", ["kid"] = _keys.Kid });
        Assert.Equal("token failed verification: InvalidAlgorithmError", await Refused(hs));
        Assert.Equal("token failed verification: InvalidAlgorithmError", await Refused(_keys.Mint(Claims(), new JsonObject { ["kid"] = _keys.Kid })));
        Assert.Equal("token failed verification: InvalidAlgorithmError",
                     await Refused(_keys.Mint(Claims(), new JsonObject { ["alg"] = new JsonArray("RS256"), ["kid"] = _keys.Kid })));
        using var other = System.Security.Cryptography.RSA.Create(2048);
        Assert.Equal("token failed verification: InvalidSignatureError", await Refused(_keys.Mint(Claims(), key: other)));
        Assert.Equal("token failed verification: DecodeError", await Refused("not-a-token"));
        Assert.Equal("token failed verification: DecodeError", await Refused("a.b.c"));
        Assert.Equal("token failed verification: DecodeError",
                     await Refused(TestKeys.MintRaw(Encoding.UTF8.GetBytes("""{"alg": "RS256", "kid": "test-key-1"}"""), Encoding.UTF8.GetBytes("[1]"), _keys.Rsa)));
        Assert.Equal("token failed verification: InvalidTokenError",
                     await Refused(_keys.Mint(Claims(), new JsonObject { ["alg"] = "RS256", ["kid"] = 5 })));
        Assert.Equal("token failed verification: InvalidTokenError",
                     await Refused(_keys.Mint(Claims(), new JsonObject { ["alg"] = "RS256", ["kid"] = _keys.Kid, ["crit"] = new JsonArray("exp") })));
        var token = _keys.Mint(Claims());
        Assert.Equal("token failed verification: DecodeError", await Refused(token + "=")); // non-canonical padding
        await Assert.ThrowsAsync<EncoderFallbackException>(() => Verify("\ud800." + token)); // Python: UnicodeEncodeError
    }

    [Fact]
    public async Task Unknown_keys_refresh_once_then_cool_down()
    {
        Assert.Equal(Fixtures.Manager, await Verify(_keys.Mint(Claims())));
        var unknown = _keys.Mint(Claims(), new JsonObject { ["alg"] = "RS256", ["kid"] = "rotated" });
        // A fetch succeeded less than 30 seconds ago: no refresh.
        Assert.Equal("token failed verification: PyJWKClientError", await Refused(unknown));
        Assert.Single(_keys.Fetches);
        Assert.Equal("token failed verification: PyJWKClientError", await Refused(_keys.Mint(Claims(), new JsonObject { ["alg"] = "RS256" })));
    }

    [Fact]
    public async Task A_rotated_key_is_fetched_after_the_cooldown()
    {
        var time = new ManualTime();
        using var verifier = new EntraTokenVerifier(Fixtures.Tenant, Fixtures.Client, "Voice.Talk", _keys, time, () => Now);
        Assert.Equal(Fixtures.Manager, await verifier.VerifyAsync(_keys.Mint(Claims()), TestContext.Current.CancellationToken));
        using var rotated = System.Security.Cryptography.RSA.Create(2048);
        _keys.Document = TestKeys.KeySet((_keys.Rsa, _keys.Kid), (rotated, "rotated"));
        var token = _keys.Mint(Claims(), new JsonObject { ["alg"] = "RS256", ["kid"] = "rotated" }, rotated);
        time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(Fixtures.Manager, await verifier.VerifyAsync(token, TestContext.Current.CancellationToken));
        Assert.Equal(2, _keys.Fetches.Count);
        time.Advance(TimeSpan.FromSeconds(301));
        Assert.Equal(Fixtures.Manager, await verifier.VerifyAsync(token, TestContext.Current.CancellationToken));
        Assert.Equal(3, _keys.Fetches.Count); // the set is kept for 300 seconds
    }

    [Fact]
    public async Task Key_set_failures()
    {
        async Task<Exception> With(byte[]? document, bool unavailable = false)
        {
            using var keys = new TestKeys();
            keys.Unavailable = unavailable;
            if (document is not null)
            {
                keys.Document = document;
            }

            using var verifier = new EntraTokenVerifier(Fixtures.Tenant, Fixtures.Client, "Voice.Talk", keys, now: () => Now);
            return await Assert.ThrowsAnyAsync<Exception>(() => verifier.VerifyAsync(keys.Mint(Claims()), TestContext.Current.CancellationToken));
        }

        static string Refusal(Exception exc) => Assert.IsType<TokenRejectedException>(exc).Message;

        Assert.Equal("token failed verification: PyJWKClientConnectionError", Refusal(await With(null, unavailable: true)));
        Assert.Equal("token failed verification: PyJWKSetError", Refusal(await With(Encoding.UTF8.GetBytes("""{"keys": []}"""))));
        Assert.Equal("token failed verification: PyJWKSetError",
                     Refusal(await With(Encoding.UTF8.GetBytes("""{"keys": [{"kty": "RSA", "use": "enc", "kid": "test-key-1", "n": "AQAB", "e": "AQAB"}, 5]}"""))));
        Assert.Equal("token failed verification: PyJWKClientError", Refusal(await With(Encoding.UTF8.GetBytes("[]"))));
        Assert.Equal("token failed verification: InvalidKeyError",
                     Refusal(await With(Encoding.UTF8.GetBytes("""{"keys": [{"kty": "oct", "kid": "test-key-1", "k": "c2VjcmV0"}]}"""))));

        // Errors PyJWT does not raise as its own end the call with the generic message, not as a refused sign-in.
        Assert.IsType<InvalidOperationException>(await With(Encoding.UTF8.GetBytes("not json")));
        using var ec = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var point = ec.ExportParameters(false).Q;
        var ecSet = new JsonObject
        {
            ["keys"] = new JsonArray(new JsonObject
            {
                ["kty"] = "EC", ["kid"] = "test-key-1", ["crv"] = "P-256", ["x"] = TestKeys.Base64Url(point.X!), ["y"] = TestKeys.Base64Url(point.Y!),
            }),
        };
        Assert.IsType<InvalidOperationException>(await With(Encoding.UTF8.GetBytes(ecSet.ToJsonString()))); // Python: TypeError
        Assert.IsType<InvalidOperationException>(await With(Encoding.UTF8.GetBytes(
            """{"keys": [{"kty": "RSA", "kid": "test-key-1", "n": "AQAB", "e": "AQAB", "d": "AQAB"}]}"""))); // Python: AttributeError
    }

    /// <summary>A clock moved by the test (monotonic time and wall time together).</summary>
    private sealed class ManualTime : TimeProvider
    {
        private long _ticks = 1_000_000;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;
    }
}

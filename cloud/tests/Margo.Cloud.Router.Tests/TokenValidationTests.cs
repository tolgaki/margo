using System.Text;
using System.Text.Json.Nodes;
using Margo.Cloud.Router.Authentication;
using Margo.Cloud.Router.Tests.TestSupport;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Margo.Cloud.Router.Tests;

/// <summary>
/// The inbound token check. The expected outcomes were observed by running the same cases through the
/// Python SDK (the aiohttp adapter's header parsing and <c>_authorize_request</c> with
/// <c>AgentAuthConfiguration(client_id, tenant_id)</c>, Microsoft 365 Agents SDK 1.10.0.dev6, PyJWT
/// 2.15.1, a fixed clock and a local key set), except the token minted by Microsoft.IdentityModel,
/// which is an interoperability check of this port only.
/// </summary>
public sealed class TokenValidationTests : IDisposable
{
    private readonly ManualTime _time = new();
    private readonly FakeJwksSource _jwks = new();
    private readonly ListLoggerProvider _logs = new();
    private readonly AgentTokenValidator _validator;

    public TokenValidationTests()
    {
        _validator = new AgentTokenValidator(new AgentAuthSettings(Tokens.ClientId, Tokens.TenantId), new JwksClientManager(_jwks, _time),
            _logs.CreateLogger("auth"), _time);
    }

    private long Now => _time.GetUtcNow().ToUnixTimeSeconds();

    public void Dispose() => _logs.Dispose();

    private string Valid(Action<JsonObject>? change = null, string? kid = "k1", System.Security.Cryptography.RSA? key = null, Action<JsonObject>? header = null) =>
        Tokens.Valid(_time.GetUtcNow(), change, kid, key, header);

    private Task<AuthorizationResult> Authorize(string? header) => _validator.AuthorizeAsync(header, TestContext.Current.CancellationToken);

    private async Task AssertAccepted(string token)
    {
        var result = await Authorize("Bearer " + token);
        Assert.Equal(200, result.StatusCode);
        Assert.NotNull(result.Claims);
    }

    private async Task AssertRejected(string token)
    {
        var result = await Authorize("Bearer " + token);
        Assert.Equal((401, AgentTokenValidator.InvalidTokenJson), (result.StatusCode, result.ErrorJson));
        Assert.Null(result.Claims);
    }

    [Fact]
    public async Task Header_forms_follow_the_aiohttp_adapter()
    {
        var token = Valid();
        Assert.Equal((401, "{\"error\": \"Authorization header not found\"}"), ((await Authorize(null)).StatusCode, (await Authorize(null)).ErrorJson));
        foreach (var header in (string[])["", "Basic abc", "Bearer", "Bearer a b", "Bearer   ", "Token " + token, "Bearer " + token + " x"])
        {
            var result = await Authorize(header);
            Assert.Equal((401, AgentTokenValidator.InvalidTokenJson), (result.StatusCode, result.ErrorJson));
        }

        foreach (var header in (string[])["Bearer " + token, "bearer   " + token + "  \t", "BEARER " + token, "\t Bearer " + token, "Bearer\t" + token + "\u001f"])
        {
            Assert.Equal(200, (await Authorize(header)).StatusCode);
        }

        Assert.Contains(_logs.Lines, line => line.Contains("Malformed authorization header.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_valid_token_reads_the_tenant_key_set_once()
    {
        await AssertAccepted(Valid());
        await AssertAccepted(Valid(c => c["aud"] = Tokens.ClientId.ToUpperInvariant()));
        Assert.Equal([Tokens.EntraJwksUri], _jwks.Fetched);
    }

    [Fact]
    public async Task A_token_minted_by_microsoft_identity_model_is_accepted()
    {
        var handler = new JsonWebTokenHandler();
        var token = handler.CreateToken(new SecurityTokenDescriptor
        {
            Audience = Tokens.ClientId,
            Issuer = "https://login.microsoftonline.com/" + Tokens.TenantId + "/v2.0",
            Claims = new Dictionary<string, object> { ["tid"] = Tokens.TenantId, ["sub"] = "fictional-subject" },
            IssuedAt = _time.GetUtcNow().UtcDateTime,
            NotBefore = _time.GetUtcNow().UtcDateTime,
            Expires = _time.GetUtcNow().UtcDateTime.AddHours(1),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(Tokens.Key1) { KeyId = "k1" }, SecurityAlgorithms.RsaSha256),
        });
        await AssertAccepted(token);
    }

    [Fact]
    public async Task Audience_must_be_one_string_equal_to_the_client_id()
    {
        await AssertRejected(Valid(c => c["aud"] = new JsonArray(Tokens.ClientId)));
        await AssertRejected(Valid(c => c.Remove("aud")));
        await AssertRejected(Valid(c => c["aud"] = ""));
        await AssertRejected(Valid(c => c["aud"] = 5));
        Assert.Empty(_jwks.Fetched); // refused before any key is read

        // Any other string selects the one connection: the key is read and the value refused after the signature.
        await AssertRejected(Valid(c => c["aud"] = "another-blueprint-client"));
        Assert.Single(_jwks.Fetched);
    }

    [Fact]
    public async Task Signing_keys_come_from_the_issuer_family()
    {
        await AssertAccepted(Valid(c =>
        {
            c["iss"] = "https://api.botframework.com";
            c.Remove("tid");
        }));
        await AssertAccepted(Valid(c =>
        {
            c["iss"] = "https://api.botframework.us";
            c.Remove("tid");
        }));
        await AssertAccepted(Valid(c => c["iss"] = "https://issuer.example.com/"));
        Assert.Equal(["https://login.botframework.com/v1/.well-known/keys", "https://login.botframework.azure.us/v1/.well-known/keys", Tokens.EntraJwksUri],
            _jwks.Fetched);
    }

    [Fact]
    public async Task Kid_and_algorithm()
    {
        await AssertRejected(Valid(kid: "k9"));
        await AssertRejected(Tokens.Raw(new JsonObject { ["alg"] = "RS256", ["kid"] = 7 }, new JsonObject { ["aud"] = Tokens.ClientId }));
        await AssertRejected(Valid(kid: "k2", key: Tokens.Key2)); // the key is published for RS384
        await AssertRejected(Valid(key: Tokens.Key2)); // signed by another key
        await AssertRejected(Tokens.Raw(new JsonObject { ["alg"] = "none", ["kid"] = "k1" }, new JsonObject { ["aud"] = Tokens.ClientId }, []));
        await AssertRejected(Valid(header: h => h["alg"] = "HS256"));
        await AssertRejected(Valid(header: h => h.Remove("alg")));
        await AssertRejected(Valid(header: h => h["alg"] = new JsonArray("RS256")));
    }

    [Fact]
    public async Task A_token_without_kid_fails_with_an_uncaught_error_as_in_python()
    {
        // The SDK read header["kid"] and did not catch the KeyError: aiohttp answered 500. The audience
        // is checked for its value only after the signature, so a wrong one does not prevent this.
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Authorize("Bearer " + Valid(kid: null)));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Authorize("Bearer " + Valid(c => c["aud"] = "other", kid: null)));

        // An audience that is not a non-empty string is refused before the key lookup.
        await AssertRejected(Valid(c => c["aud"] = 5, kid: null));
        await AssertRejected(Valid(c => c["aud"] = "", kid: null));
    }

    [Fact]
    public async Task Lifetime_claims_have_five_minutes_of_leeway_and_are_optional()
    {
        await AssertRejected(Valid(c => c["exp"] = Now - 400));
        await AssertAccepted(Valid(c => c["exp"] = Now - 200));
        await AssertAccepted(Valid(c =>
        {
            c.Remove("exp");
            c.Remove("nbf");
            c.Remove("iat");
        }));
        await AssertAccepted(Valid(c => c["exp"] = (Now + 100).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        await AssertAccepted(Valid(c => c["exp"] = Now + 100.7));
        await AssertRejected(Valid(c => c["exp"] = "soon"));
        await AssertRejected(Valid(c => c["nbf"] = Now + 400));
        await AssertAccepted(Valid(c => c["nbf"] = Now + 200));
        await AssertRejected(Valid(c => c["iat"] = Now + 400));
        await AssertRejected(Valid(c => c["iat"] = "x"));
        await AssertRejected(Valid(c => c["iat"] = null));
        await AssertAccepted(Valid(c => c["iat"] = true));
        await AssertAccepted(Valid(c => c["iat"] = " 1_000 "));
    }

    [Fact]
    public async Task Lifetime_boundaries_compare_as_python_does()
    {
        // exp <= now - 300 is expired; iat and nbf > now + 300 are not yet valid. The clock is on a whole second.
        await AssertRejected(Valid(c => c["exp"] = Now - 300));
        await AssertAccepted(Valid(c => c["exp"] = Now - 299));
        await AssertAccepted(Valid(c => c["nbf"] = Now + 300));
        await AssertRejected(Valid(c => c["nbf"] = Now + 301));
        _time.Advance(TimeSpan.FromMilliseconds(500));
        await AssertAccepted(Valid(c => c["exp"] = Now - 299)); // Now is truncated: exp is 299.5 s ago
        await AssertAccepted(Valid(c => c["iat"] = Now + 300));
        await AssertRejected(Valid(c => c["iat"] = Now + 301));
    }

    [Fact]
    public async Task Subject_and_token_id_must_be_strings_when_present()
    {
        await AssertRejected(Valid(c => c["sub"] = 5));
        await AssertRejected(Valid(c => c["jti"] = 5));
        await AssertAccepted(Valid(c =>
        {
            c["sub"] = "fictional-subject";
            c["jti"] = "fictional-id";
        }));
    }

    [Fact]
    public async Task Tenant_binding_applies_to_recognised_entra_issuers_only()
    {
        await AssertRejected(Valid(c => c["tid"] = Tokens.OtherTenantId));
        await AssertAccepted(Valid(c => c["tid"] = Tokens.TenantId.ToUpperInvariant()));
        await AssertAccepted(Valid(c => c.Remove("tid")));
        await AssertRejected(Valid(c => c["iss"] = "https://login.microsoftonline.com/" + Tokens.OtherTenantId + "/v2.0"));
        await AssertRejected(Valid(c => c["iss"] = "https://login.microsoftonline.us/" + Tokens.OtherTenantId + "/v2.0"));
        await AssertRejected(Valid(c => c["iss"] = "HTTPS://STS.WINDOWS.NET/" + Tokens.OtherTenantId + "/"));
        await AssertRejected(Valid(c => c["iss"] = "https://ſts.windows.net/" + Tokens.OtherTenantId + "/")); // Python's IGNORECASE: U+017F is s
        await AssertRejected(Valid(c => c["iss"] = Tokens.V1Issuer(Tokens.OtherTenantId) + "\n")); // "$" before a final newline

        // Not recognised, so not bound: a domain tenant, another suffix, a non-string tid.
        await AssertAccepted(Valid(c => c["iss"] = "https://sts.windows.net/contoso.example.com/"));
        await AssertAccepted(Valid(c => c["iss"] = "https://sts.windows.net/" + Tokens.OtherTenantId + "/v2.0"));
        await AssertAccepted(Valid(c => c["tid"] = 7));

        // Any issuer is accepted otherwise (VALIDATE_ISSUER is off in the SDK): another tenant without tid.
        await AssertAccepted(Valid(c =>
        {
            c["iss"] = Tokens.V1Issuer(Tokens.OtherTenantId);
            c.Remove("tid");
        }));
    }

    [Fact]
    public async Task Structure_is_read_as_pyjwt_reads_it()
    {
        var good = Valid();
        var parts = good.Split('.');
        await AssertRejected("not-a-jwt");
        await AssertRejected(good + ".x");
        await AssertRejected(Tokens.Raw(new JsonObject { ["alg"] = "RS256", ["kid"] = "k1" }, new JsonArray(Tokens.ClientId)));
        await AssertRejected(parts[0] + "." + Tokens.B64("{\"aud\": \"" + Tokens.ClientId + "\", \"exp\": 1}") + "." + parts[2]); // tampered
        await AssertRejected(Valid(header: h =>
        {
            h["crit"] = new JsonArray("exp");
            h["exp"] = 1;
        }));
        await AssertRejected(Valid(header: h => h["crit"] = new JsonArray()));
        await AssertRejected(Valid(header: h =>
        {
            h["b64"] = false;
            h["crit"] = new JsonArray("b64");
        }));
        await AssertAccepted(Valid(header: h => h["typ"] = "odd"));
        await AssertAccepted(Valid(header: h =>
        {
            h["b64"] = true;
            h["crit"] = new JsonArray("b64");
        }));

        // At most two "=" of padding, only on a segment whose length is a multiple of four.
        var padded = parts[2] + new string('=', (4 - (parts[2].Length % 4)) % 4);
        await AssertAccepted(parts[0] + "." + parts[1] + "." + padded);
        await AssertRejected(parts[0] + "." + parts[1] + "." + parts[2] + "===");
        await AssertRejected(parts[0] + "." + parts[1] + "." + parts[2] + "+");

        // Non-canonical base64: the unused bits of the last character are set.
        var last = parts[1][^1];
        var sibling = "AQgw".Contains(last, StringComparison.Ordinal) ? (char)(last + 1) : last;
        if (sibling != last && parts[1].Length % 4 != 0)
        {
            await AssertRejected(parts[0] + "." + parts[1][..^1] + sibling + "." + parts[2]);
        }
    }

    [Fact]
    public async Task Json_in_the_token_is_read_as_python_json_reads_bytes()
    {
        // A repeated claim: the last value counts.
        var header = Tokens.B64("{\"alg\": \"RS256\", \"kid\": \"k1\"}");
        var payload = Tokens.B64("{\"aud\": \"other\", \"aud\": \"" + Tokens.ClientId + "\"}");
        var signature = Tokens.B64(Tokens.Key1.SignData(Encoding.ASCII.GetBytes(header + "." + payload),
            System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1));
        await AssertAccepted(header + "." + payload + "." + signature);

        // UTF-16 with a BOM is JSON to Python's json.loads(bytes).
        var utf16Payload = Tokens.B64(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("{\"aud\": \"" + Tokens.ClientId + "\"}")).ToArray());
        var utf16Signature = Tokens.B64(Tokens.Key1.SignData(Encoding.ASCII.GetBytes(header + "." + utf16Payload),
            System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1));
        await AssertAccepted(header + "." + utf16Payload + "." + utf16Signature);
    }

    [Fact]
    public async Task An_unreachable_key_set_refuses_the_token()
    {
        _jwks.Down = true;
        await AssertRejected(Valid());
        _jwks.Down = false;
        _jwks.Document = _ => "[1]"u8.ToArray();
        await AssertRejected(Valid());
        _jwks.Document = _ => "{\"keys\": []}"u8.ToArray();
        await AssertRejected(Valid());
        _jwks.Document = _ => "not json"u8.ToArray();
        await AssertRejected(Valid());
        _jwks.Document = _ => Tokens.DefaultKeySet();
        await AssertAccepted(Valid());
    }

    [Fact]
    public async Task Nothing_about_the_token_is_logged()
    {
        var token = Valid(c => c["aud"] = "other");
        await AssertRejected(token);
        await AssertRejected(Valid(kid: "k9"));
        Assert.All(_logs.Lines, line =>
        {
            Assert.DoesNotContain(token, line, StringComparison.Ordinal);
            Assert.DoesNotContain("k9", line, StringComparison.Ordinal);
            Assert.DoesNotContain("other", line, StringComparison.Ordinal);
        });
        Assert.Contains(_logs.Lines, line => line.EndsWith("JWT validation error: ValueError", StringComparison.Ordinal));
        Assert.Contains(_logs.Lines, line => line.EndsWith("JWT validation error: PyJWKClientError", StringComparison.Ordinal));
    }
}

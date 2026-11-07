using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Margo.Cloud.Router.Tests.TestSupport;

/// <summary>Locally generated RSA keys and tokens signed with them. Fictional identifiers only.</summary>
internal static class Tokens
{
    /// <summary>The blueprint's client ID. Any string works as an audience.</summary>
    public const string ClientId = "fictional-blueprint-client";

    /// <summary>Tenants are GUIDs (the tenant binding only applies to a GUID tenant). They are made per
    /// run, so no tenant identifier is written in the repository.</summary>
    public static readonly string TenantId = Guid.NewGuid().ToString();

    public static readonly string OtherTenantId = Guid.NewGuid().ToString();

    public static string EntraJwksUri => "https://login.microsoftonline.com/" + TenantId + "/discovery/v2.0/keys";

    /// <summary>The key with kid <c>k1</c> (RS256 by its key type).</summary>
    public static readonly RSA Key1 = RSA.Create(2048);

    /// <summary>The key with kid <c>k2</c>, published with <c>alg: RS384</c>.</summary>
    public static readonly RSA Key2 = RSA.Create(2048);

    public static string V1Issuer(string? tenant = null) => "https://sts.windows.net/" + (tenant ?? TenantId) + "/";

    public static string B64(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string B64(string text) => B64(Encoding.UTF8.GetBytes(text));

    public static JsonObject Jwk(RSA key, string kid, string? alg = null)
    {
        var parameters = key.ExportParameters(includePrivateParameters: false);
        var jwk = new JsonObject
        {
            ["kty"] = "RSA",
            ["use"] = "sig",
            ["kid"] = kid,
            ["n"] = B64(parameters.Modulus!),
            ["e"] = B64(parameters.Exponent!),
        };
        if (alg is not null)
        {
            jwk["alg"] = alg;
        }

        return jwk;
    }

    /// <summary>The key set the tests publish: k1 (RS256) and k2 (RS384).</summary>
    public static byte[] DefaultKeySet() =>
        Encoding.UTF8.GetBytes(new JsonObject { ["keys"] = new JsonArray(Jwk(Key1, "k1"), Jwk(Key2, "k2", "RS384")) }.ToJsonString());

    /// <summary>Claims of a valid token issued now.</summary>
    public static JsonObject Claims(DateTimeOffset now)
    {
        var seconds = now.ToUnixTimeSeconds();
        return new JsonObject
        {
            ["aud"] = ClientId,
            ["iss"] = V1Issuer(),
            ["tid"] = TenantId,
            ["iat"] = seconds,
            ["nbf"] = seconds,
            ["exp"] = seconds + 3600,
        };
    }

    /// <summary>A token signed with RS256 by <paramref name="key"/>.</summary>
    public static string Sign(JsonObject header, JsonObject payload, RSA? key = null)
    {
        var signingInput = B64(header.ToJsonString()) + "." + B64(payload.ToJsonString());
        var signature = (key ?? Key1).SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return signingInput + "." + B64(signature);
    }

    /// <summary>A valid token with <paramref name="change"/> applied to its claims.</summary>
    public static string Valid(DateTimeOffset now, Action<JsonObject>? change = null, string? kid = "k1", RSA? key = null, Action<JsonObject>? header = null)
    {
        var claims = Claims(now);
        change?.Invoke(claims);
        var head = new JsonObject { ["alg"] = "RS256", ["typ"] = "JWT" };
        if (kid is not null)
        {
            head["kid"] = kid;
        }

        header?.Invoke(head);
        return Sign(head, claims, key);
    }

    /// <summary>A token with an arbitrary header, payload and signature.</summary>
    public static string Raw(JsonNode header, JsonNode payload, byte[]? signature = null) =>
        B64(header.ToJsonString()) + "." + B64(payload.ToJsonString()) + "." + B64(signature ?? "x"u8.ToArray());
}

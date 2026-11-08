// Copied from Margo.Cloud.Router (namespace changed only): the Agents SDK token check the Python host also ran.
// Keep the copies in step; see Margo.Cloud.Host/PORTING.md, "Requests to other components".
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Margo.Cloud.Host.Authentication;

/// <summary>A token was refused. Python: a <c>PyJWTError</c> or <c>ValueError</c>, which the Agents SDK
/// turns into 401 "Invalid token or authentication failed.". <see cref="Kind"/> names the Python
/// exception type, which is all the SDK logged; the message is for tests and is never logged or returned.</summary>
public sealed class TokenRejectedException : Exception
{
    public TokenRejectedException()
    {
    }

    public TokenRejectedException(string message)
        : base(message)
    {
    }

    public TokenRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public TokenRejectedException(string kind, string message)
        : base(message)
    {
        Kind = kind;
    }

    public TokenRejectedException(string kind, string message, Exception innerException)
        : base(message, innerException)
    {
        Kind = kind;
    }

    /// <summary>The Python exception type name, such as <c>DecodeError</c> or <c>ValueError</c>.</summary>
    public string Kind { get; } = "ValueError";
}

/// <summary>
/// A compact JWS read the way PyJWT 2.15 reads it (<c>PyJWS._load</c>, <c>_validate_headers</c> and the
/// <c>b64</c> handling of <c>decode_complete</c>): three segments split at the first and last dot,
/// strict base64url (at most two <c>=</c>, canonical), a JSON object header with a string
/// <c>kid</c> if any and only the <c>b64</c> critical extension, and a JSON object payload.
/// </summary>
internal sealed class CompactJws
{
    private const string Decode = "DecodeError";
    private const string Invalid = "InvalidTokenError";

    private CompactJws(JsonObject header, JsonObject payload, byte[] signingInput, byte[] signature)
    {
        Header = header;
        Payload = payload;
        SigningInput = signingInput;
        Signature = signature;
    }

    internal JsonObject Header { get; }

    internal JsonObject Payload { get; }

    /// <summary>The bytes the signature covers: the token up to its last dot.</summary>
    internal byte[] SigningInput { get; }

    internal byte[] Signature { get; }

    /// <summary>Parse and check the structure; nothing is verified.</summary>
    /// <exception cref="TokenRejectedException">Python: <c>DecodeError</c> or <c>InvalidTokenError</c>.</exception>
    internal static CompactJws Parse(string token)
    {
        var jwt = Encoding.UTF8.GetBytes(token);
        var last = Array.LastIndexOf(jwt, (byte)'.');
        if (last < 0)
        {
            throw new TokenRejectedException(Decode, "Not enough segments");
        }

        var signingInput = jwt.AsSpan(0, last);
        var first = signingInput.IndexOf((byte)'.');
        if (first < 0)
        {
            throw new TokenRejectedException(Decode, "Not enough segments");
        }

        var header = LoadObject(DecodeSegment(signingInput[..first], "header"), "header");
        var payloadSegment = signingInput[(first + 1)..];
        byte[] payloadData;
        if (IsJsonFalse(header["b64"]))
        {
            if (!payloadSegment.IsEmpty)
            {
                throw new TokenRejectedException(Decode, "Payload segment must be empty when 'b64' is false.");
            }

            payloadData = [];
        }
        else
        {
            payloadData = DecodeSegment(payloadSegment, "payload");
        }

        var signature = DecodeSegment(jwt.AsSpan(last + 1), "crypto");
        ValidateHeaders(header);
        if (IsJsonFalse(header["b64"]))
        {
            // RFC 7797: b64=false must be critical, and PyJWT then needs a detached payload, which the
            // Agents SDK never passes, so such a token is always refused.
            if (header["crit"] is not JsonArray crit || !crit.Any(item => PythonCompat.AsString(item) == "b64"))
            {
                throw new TokenRejectedException(Invalid, "The 'b64' header parameter requires 'b64' to be listed in 'crit'.");
            }

            throw new TokenRejectedException(Decode, "A token with b64=false needs a detached payload.");
        }

        var payload = LoadObject(payloadData, "payload");
        return new CompactJws(header, payload, signingInput.ToArray(), signature);
    }

    private static JsonObject LoadObject(byte[] data, string name)
    {
        try
        {
            return PythonJsonBytes.Loads(data) as JsonObject
                ?? throw new TokenRejectedException(Decode, "Invalid " + name + " string: must be a json object");
        }
        catch (JsonException exc)
        {
            throw new TokenRejectedException(Decode, "Invalid " + name + " string", exc);
        }
    }

    private static bool IsJsonFalse(JsonNode? node) => node is JsonValue value && value.GetValueKind() == JsonValueKind.False;

    /// <summary>Python: <c>PyJWS._validate_headers</c> (<c>kid</c> must be a string; <c>crit</c> a
    /// non-empty list of supported, present extensions).</summary>
    private static void ValidateHeaders(JsonObject header)
    {
        if (header.ContainsKey("kid") && PythonCompat.AsString(header["kid"]) is null)
        {
            throw new TokenRejectedException(Invalid, "Key ID header parameter must be a string");
        }

        if (!header.ContainsKey("crit"))
        {
            return;
        }

        if (header["crit"] is not JsonArray crit || crit.Count == 0)
        {
            throw new TokenRejectedException(Invalid, "Invalid 'crit' header: must be a non-empty list");
        }

        foreach (var item in crit)
        {
            var extension = PythonCompat.AsString(item) ?? throw new TokenRejectedException(Invalid, "Invalid 'crit' header: values must be strings");
            if (extension != "b64")
            {
                throw new TokenRejectedException(Invalid, "Unsupported critical extension");
            }

            if (!header.ContainsKey(extension))
            {
                throw new TokenRejectedException(Invalid, "Critical extension is missing from headers");
            }
        }
    }

    /// <summary>Python: <c>PyJWS._decode_base64url_segment</c>.</summary>
    private static byte[] DecodeSegment(ReadOnlySpan<byte> segment, string name)
    {
        var padding = 0;
        var stripped = segment;
        while (!stripped.IsEmpty && stripped[^1] == (byte)'=')
        {
            stripped = stripped[..^1];
            padding++;
            if (padding > 2)
            {
                throw new TokenRejectedException(Decode, "Invalid " + name + " padding");
            }
        }

        if ((padding > 0 && segment.Length % 4 != 0) || stripped.Length % 4 == 1)
        {
            throw new TokenRejectedException(Decode, "Invalid " + name + " padding");
        }

        foreach (var character in stripped)
        {
            if (character is not ((>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z') or (>= (byte)'0' and <= (byte)'9') or (byte)'-' or (byte)'_'))
            {
                throw new TokenRejectedException(Decode, "Invalid " + name + " padding");
            }
        }

        var text = Encoding.ASCII.GetString(stripped).Replace('-', '+').Replace('_', '/');
        text += new string('=', (4 - (text.Length % 4)) % 4);
        var decoded = Convert.FromBase64String(text);

        // Canonical only: the unused low bits of the last character must be zero.
        var reencoded = Convert.ToBase64String(decoded).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        if (!stripped.SequenceEqual(Encoding.ASCII.GetBytes(reencoded)))
        {
            throw new TokenRejectedException(Decode, "Invalid " + name + " padding");
        }

        return decoded;
    }
}

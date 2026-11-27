using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.Voice.Jwt;

/// <summary>A token failed PyJWT's checks (a <c>PyJWTError</c>). <see cref="Kind"/> names the Python exception type,
/// which is what the voice server logs (<c>token failed verification: &lt;type&gt;</c>).</summary>
internal sealed class JwtRejectedException : Exception
{
    public JwtRejectedException()
    {
    }

    public JwtRejectedException(string message)
        : base(message)
    {
    }

    public JwtRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public JwtRejectedException(string kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    /// <summary>The Python exception type name, such as <c>DecodeError</c>.</summary>
    public string Kind { get; } = "PyJWTError";
}

/// <summary>
/// A compact JWS read the way PyJWT 2.15 reads it (<c>PyJWS._load</c>, <c>_validate_headers</c>, the <c>b64</c>
/// handling of <c>decode_complete</c>, and <c>PyJWT._decode_payload</c>): split at the last and then the first dot,
/// strict base64url (at most two <c>=</c>, canonical), a JSON object header with a string <c>kid</c> if any and only
/// the <c>b64</c> critical extension, and a JSON object payload. Adapted from the router's reading of the same library.
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

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);

    /// <summary>Parse and check the structure; nothing is verified.</summary>
    /// <exception cref="JwtRejectedException">Python: <c>DecodeError</c> or <c>InvalidTokenError</c>.</exception>
    /// <exception cref="EncoderFallbackException">The token holds a lone surrogate (Python's <c>str.encode</c> raises
    /// <c>UnicodeEncodeError</c>, which is not a PyJWT error).</exception>
    internal static CompactJws Parse(string token)
    {
        var jwt = StrictUtf8.GetBytes(token);
        var last = Array.LastIndexOf(jwt, (byte)'.');
        if (last < 0)
        {
            throw new JwtRejectedException(Decode, "Not enough segments");
        }

        var signingInput = jwt.AsSpan(0, last);
        var first = signingInput.IndexOf((byte)'.');
        if (first < 0)
        {
            throw new JwtRejectedException(Decode, "Not enough segments");
        }

        var header = LoadObject(DecodeSegment(signingInput[..first], "header"), "header");
        var payloadSegment = signingInput[(first + 1)..];
        byte[] payloadData;
        if (IsJsonFalse(header["b64"]))
        {
            if (!payloadSegment.IsEmpty)
            {
                throw new JwtRejectedException(Decode, "Payload segment must be empty when 'b64' is false.");
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
            // RFC 7797: b64=false must be critical, and PyJWT then needs a detached payload, which the voice server
            // never passes, so such a token is always refused.
            var crit = header["crit"];
            if (!(PyJson.Truthy(crit) ? crit is JsonArray list && list.Any(item => PyNode.AsString(item) == "b64") : false))
            {
                throw new JwtRejectedException(Invalid, "The 'b64' header parameter requires 'b64' to be listed in 'crit'.");
            }

            throw new JwtRejectedException(Decode, "A token with b64=false needs a detached payload.");
        }

        var payload = LoadObject(payloadData, "payload");
        return new CompactJws(header, payload, signingInput.ToArray(), signature);
    }

    /// <summary>Python <c>json.loads(bytes)</c>; <c>ValueError</c> and <c>RecursionError</c> become <c>DecodeError</c>.</summary>
    private static JsonObject LoadObject(byte[] data, string name)
    {
        JsonNode? value;
        try
        {
            value = PyJson.LoadsBytes(data);
        }
        catch (Exception exc) when (exc is PyValueError or PyRecursionError or JsonException)
        {
            throw new JwtRejectedException(Decode, "Invalid " + name + " string", exc);
        }

        return value as JsonObject ?? throw new JwtRejectedException(Decode, "Invalid " + name + " string: must be a json object");
    }

    private static bool IsJsonFalse(JsonNode? node) => node is JsonValue && PyJson.KindOf(node) == PyKind.Bool && !PyJson.Truthy(node);

    /// <summary>Python: <c>PyJWS._validate_headers</c>.</summary>
    private static void ValidateHeaders(JsonObject header)
    {
        if (header.ContainsKey("kid") && PyNode.AsString(header["kid"]) is null)
        {
            throw new JwtRejectedException(Invalid, "Key ID header parameter must be a string");
        }

        if (!header.ContainsKey("crit"))
        {
            return;
        }

        if (header["crit"] is not JsonArray crit || crit.Count == 0)
        {
            throw new JwtRejectedException(Invalid, "Invalid 'crit' header: must be a non-empty list");
        }

        foreach (var item in crit)
        {
            var extension = PyNode.AsString(item) ?? throw new JwtRejectedException(Invalid, "Invalid 'crit' header: values must be strings");
            if (extension != "b64")
            {
                throw new JwtRejectedException(Invalid, "Unsupported critical extension");
            }

            if (!header.ContainsKey(extension))
            {
                throw new JwtRejectedException(Invalid, "Critical extension is missing from headers");
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
                throw new JwtRejectedException(Decode, "Invalid " + name + " padding");
            }
        }

        if ((padding > 0 && segment.Length % 4 != 0) || stripped.Length % 4 == 1)
        {
            throw new JwtRejectedException(Decode, "Invalid " + name + " padding");
        }

        foreach (var character in stripped)
        {
            if (character is not ((>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z') or (>= (byte)'0' and <= (byte)'9') or (byte)'-' or (byte)'_'))
            {
                throw new JwtRejectedException(Decode, "Invalid " + name + " padding");
            }
        }

        var text = Encoding.ASCII.GetString(stripped).Replace('-', '+').Replace('_', '/');
        text += new string('=', (4 - (text.Length % 4)) % 4);
        var decoded = Convert.FromBase64String(text);

        // Canonical only: the unused low bits of the last character must be zero.
        var reencoded = Convert.ToBase64String(decoded).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        if (!stripped.SequenceEqual(Encoding.ASCII.GetBytes(reencoded)))
        {
            throw new JwtRejectedException(Decode, "Invalid " + name + " padding");
        }

        return decoded;
    }
}

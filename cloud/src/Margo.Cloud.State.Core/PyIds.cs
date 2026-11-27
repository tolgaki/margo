using System.Security.Cryptography;
using System.Text;

namespace Margo.Cloud.State.Core;

/// <summary>
/// Hashes and identifiers as the Python helpers build them: <c>hashlib.sha256(text.encode("utf-8")).hexdigest()</c>,
/// <c>uuid.uuid4().hex</c> and <c>secrets.token_hex(n)</c>. Randomness comes from <see cref="PyRuntime.RandomBytes"/>.
/// </summary>
public static class PyIds
{
    /// <summary><c>hashlib.sha256(text.encode("utf-8")).hexdigest()</c>; lone surrogates raise <c>UnicodeEncodeError</c>.</summary>
    public static string Sha256Hex(string text) => Sha256Hex(PyIO.EncodeUtf8(text));

    /// <summary><c>hashlib.sha256(data).hexdigest()</c>.</summary>
    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary><c>hashlib.sha1(data).hexdigest()</c>.</summary>
    public static string Sha1Hex(ReadOnlySpan<byte> data)
    {
#pragma warning disable CA5350 // Python uses SHA-1 for a content fingerprint, not for security.
        return Convert.ToHexStringLower(SHA1.HashData(data));
#pragma warning restore CA5350
    }

    /// <summary><c>uuid.uuid4().hex</c>: 32 lowercase hex digits with the version-4 and RFC 4122 variant bits.</summary>
    public static string Uuid4Hex()
    {
        var bytes = PyRuntime.RandomBytes(16);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary><c>str(uuid.uuid4())</c>: the hyphenated form.</summary>
    public static string Uuid4()
    {
        var hex = Uuid4Hex();
        return hex[..8] + "-" + hex[8..12] + "-" + hex[12..16] + "-" + hex[16..20] + "-" + hex[20..];
    }

    /// <summary><c>secrets.token_hex(nbytes)</c> (32 bytes when omitted, as in Python).</summary>
    public static string TokenHex(int nbytes = 32) => Convert.ToHexStringLower(PyRuntime.RandomBytes(nbytes));

    /// <summary>The UTF-8 bytes of a string (strict, like <c>str.encode("utf-8")</c>).</summary>
    public static byte[] Utf8(string text) => PyIO.EncodeUtf8(text);

    internal static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    internal static string Ascii(byte[] bytes) => Encoding.ASCII.GetString(bytes);
}

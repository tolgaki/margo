namespace Margo.Cloud.Router;

/// <summary>
/// Python's lenient Base64 decoding: <c>binascii.a2b_base64(data, strict_mode=False)</c>, which
/// <c>base64.b64decode(s)</c> (the WebSocket key check in aiohttp) and <c>base64.urlsafe_b64decode</c>
/// (PyJWT's <c>from_base64url_uint</c> for key sets) use.
/// </summary>
/// <remarks>
/// CPython 3.13 <c>Modules/binascii.c</c> (the venv's 3.13.16; checked against it with 20,000 generated
/// inputs): characters outside the standard alphabet and every <c>=</c> are skipped, data after padding
/// included; at the end a quad left with one data character is an error, and so is one with two or
/// three that the <c>=</c> after the last data character do not complete (<c>binascii.Error</c>).
/// CPython 3.12 stopped at the padding instead (see PORTING.md).
/// </remarks>
internal static class PythonBase64
{
    /// <summary><c>binascii.a2b_base64(data)</c>; <see langword="false"/> where it raises <c>binascii.Error</c>.</summary>
    internal static bool TryA2b(ReadOnlySpan<byte> data, out byte[] decoded)
    {
        var output = new List<byte>((data.Length + 3) / 4 * 3);
        var quadPos = 0;
        var leftChar = 0;
        var pads = 0;
        foreach (var raw in data)
        {
            if (raw == (byte)'=')
            {
                pads++;
                continue;
            }

            var value = Value(raw);
            if (value < 0)
            {
                continue;
            }

            pads = 0;
            switch (quadPos)
            {
                case 0:
                    quadPos = 1;
                    leftChar = value;
                    break;
                case 1:
                    quadPos = 2;
                    output.Add((byte)((leftChar << 2) | (value >> 4)));
                    leftChar = value & 0x0f;
                    break;
                case 2:
                    quadPos = 3;
                    output.Add((byte)((leftChar << 4) | (value >> 2)));
                    leftChar = value & 0x03;
                    break;
                default:
                    quadPos = 0;
                    output.Add((byte)((leftChar << 6) | value));
                    leftChar = 0;
                    break;
            }
        }

        decoded = [.. output];
        return quadPos == 0 || (quadPos >= 2 && quadPos + pads >= 4);
    }

    /// <summary>PyJWT's <c>base64url_decode(force_bytes(text))</c>: UTF-8 encoded (a lone surrogate
    /// fails, as <c>str.encode</c> raises), <c>=</c> appended to a multiple of four bytes, <c>-</c> and
    /// <c>_</c> read as <c>+</c> and <c>/</c>, then <see cref="TryA2b"/>.</summary>
    internal static bool TryUrlsafeDecode(string text, out byte[] decoded)
    {
        decoded = [];
        byte[] bytes;
        try
        {
            bytes = StrictUtf8.GetBytes(text);
        }
        catch (System.Text.EncoderFallbackException)
        {
            return false;
        }

        var padded = new byte[bytes.Length + ((4 - (bytes.Length % 4)) % 4)];
        for (var i = 0; i < padded.Length; i++)
        {
            padded[i] = i >= bytes.Length ? (byte)'=' : bytes[i] switch
            {
                (byte)'-' => (byte)'+',
                (byte)'_' => (byte)'/',
                var other => other,
            };
        }

        return TryA2b(padded, out decoded);
    }

    private static readonly System.Text.Encoding StrictUtf8 = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true);

    private static int Value(byte c) => c switch
    {
        >= (byte)'A' and <= (byte)'Z' => c - 'A',
        >= (byte)'a' and <= (byte)'z' => c - 'a' + 26,
        >= (byte)'0' and <= (byte)'9' => c - '0' + 52,
        (byte)'+' => 62,
        (byte)'/' => 63,
        _ => -1,
    };
}

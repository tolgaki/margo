using System.Text;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.State.Helpers.Python;

/// <summary>
/// CPython 3.13 <c>urllib.parse.unquote(string)</c> (UTF-8, <c>errors='replace'</c>): only runs of ASCII
/// characters are percent-decoded, each run on its own, and invalid UTF-8 becomes U+FFFD per CPython's
/// error ranges; non-ASCII text passes through unchanged.
/// </summary>
public static class PyUrl
{
    /// <summary><c>urllib.parse.unquote(text)</c>.</summary>
    public static string Unquote(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.Contains('%', StringComparison.Ordinal))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var start = i;
            while (i < text.Length && text[i] < 0x80)
            {
                i++;
            }

            if (i > start)
            {
                builder.Append(DecodeUtf8Replace(UnquoteBytes(text, start, i)));
            }

            start = i;
            while (i < text.Length && text[i] >= 0x80)
            {
                i++;
            }

            builder.Append(text, start, i - start);
        }

        return builder.ToString();
    }

    /// <summary><c>_unquote_impl</c> over an ASCII run: <c>%XX</c> becomes the byte, anything else stays.</summary>
    private static byte[] UnquoteBytes(string text, int start, int end)
    {
        var bytes = new List<byte>(end - start);
        for (var i = start; i < end; i++)
        {
            if (text[i] == '%' && i + 2 < end && char.IsAsciiHexDigit(text[i + 1]) && char.IsAsciiHexDigit(text[i + 2]))
            {
                bytes.Add(Convert.ToByte(text.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
            {
                bytes.Add((byte)text[i]);
            }
        }

        return [.. bytes];
    }

    /// <summary>
    /// <c>data.decode("utf-8", "replace")</c>: one U+FFFD per invalid start byte, per invalid sequence (the
    /// start byte and its valid continuation bytes) and for a truncated sequence at the end, as CPython's
    /// decoder reports them.
    /// </summary>
    public static string DecodeUtf8Replace(ReadOnlySpan<byte> data)
    {
        var builder = new StringBuilder(data.Length);
        var position = 0;
        while (position < data.Length)
        {
            var b = data[position];
            if (b < 0x80)
            {
                builder.Append((char)b);
                position++;
                continue;
            }

            int need;
            if (b is >= 0xC2 and <= 0xDF)
            {
                need = 1;
            }
            else if (b is >= 0xE0 and <= 0xEF)
            {
                need = 2;
            }
            else if (b is >= 0xF0 and <= 0xF4)
            {
                need = 3;
            }
            else
            {
                builder.Append('\uFFFD');
                position++;
                continue;
            }

            var valid = 0;
            var error = false;
            for (var k = 1; k <= need; k++)
            {
                if (position + k >= data.Length)
                {
                    // Unexpected end of data: the rest of the input is one error.
                    builder.Append('\uFFFD');
                    return builder.ToString();
                }

                var next = data[position + k];
                var (low, high) = k == 1
                    ? b switch
                    {
                        0xE0 => (0xA0, 0xBF),
                        0xED => (0x80, 0x9F),
                        0xF0 => (0x90, 0xBF),
                        0xF4 => (0x80, 0x8F),
                        _ => (0x80, 0xBF),
                    }
                    : (0x80, 0xBF);
                if (next < low || next > high)
                {
                    error = true;
                    break;
                }

                valid++;
            }

            if (error)
            {
                builder.Append('\uFFFD');
                position += valid + 1;
                continue;
            }

            builder.Append(PyIO.StrictUtf8.GetString(data.Slice(position, need + 1)));
            position += need + 1;
        }

        return builder.ToString();
    }
}

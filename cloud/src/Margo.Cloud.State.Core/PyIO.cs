using System.Globalization;
using System.Text;

namespace Margo.Cloud.State.Core;

/// <summary>
/// Python text I/O as the scripts use it: <c>print()</c> to UTF-8 stdout (lone surrogates raise
/// <c>UnicodeEncodeError</c> before anything is written), <c>sys.stdin.read([n])</c> counting code points,
/// and <c>Path.read_text(encoding="utf-8")</c> with strict decoding, universal newlines and errno messages.
/// </summary>
public static class PyIO
{
    /// <summary>Strict UTF-8 without a byte-order mark, as Python's <c>utf-8</c> codec.</summary>
    public static UTF8Encoding StrictUtf8 { get; } = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Wraps a raw stdin stream the way Python's <c>sys.stdin</c> decodes it (UTF-8, strict, no BOM skipping).</summary>
    public static TextReader StdinReader(Stream stream) =>
        new StreamReader(stream, StrictUtf8, detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);

    /// <summary>Wraps a raw stdout/stderr stream as Python writes it (UTF-8, <c>\n</c> newlines).</summary>
    public static TextWriter OutputWriter(Stream stream) =>
        new StreamWriter(stream, new UTF8Encoding(false, false), bufferSize: 8192, leaveOpen: true) { NewLine = "\n", AutoFlush = false };

    /// <summary>
    /// Wraps the raw stderr stream as Python writes <c>sys.stderr</c>: UTF-8 with <c>\n</c> newlines and the
    /// <c>backslashreplace</c> error handler, so a lone surrogate (an undecodable argument byte echoed by an
    /// argparse error) is written as <c>\udcff</c>, not U+FFFD.
    /// </summary>
    public static TextWriter ErrorWriter(Stream stream) =>
        new StreamWriter(stream, BackslashReplaceUtf8, bufferSize: 8192, leaveOpen: true) { NewLine = "\n", AutoFlush = false };

    /// <summary>UTF-8 whose encoder writes lone surrogates as <c>\uXXXX</c> (Python's <c>backslashreplace</c>).</summary>
    public static Encoding BackslashReplaceUtf8 { get; } = CreateBackslashReplaceUtf8();

    private static Encoding CreateBackslashReplaceUtf8()
    {
        // No byte-order mark: a StreamWriter writes the preamble to a non-seekable stream such as a pipe.
        var encoding = (Encoding)new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).Clone();
        encoding.EncoderFallback = new BackslashReplaceFallback();
        return encoding;
    }

    private sealed class BackslashReplaceFallback : EncoderFallback
    {
        public override int MaxCharCount => 12;

        public override EncoderFallbackBuffer CreateFallbackBuffer() => new Buffer();

        private sealed class Buffer : EncoderFallbackBuffer
        {
            private string _pending = "";
            private int _index;

            public override int Remaining => _pending.Length - _index;

            public override bool Fallback(char charUnknown, int index)
            {
                _pending = "\\u" + ((int)charUnknown).ToString("x4", CultureInfo.InvariantCulture);
                _index = 0;
                return true;
            }

            public override bool Fallback(char charUnknownHigh, char charUnknownLow, int index)
            {
                _pending = "\\U" + char.ConvertToUtf32(charUnknownHigh, charUnknownLow).ToString("x8", CultureInfo.InvariantCulture);
                _index = 0;
                return true;
            }

            public override char GetNextChar() => _index < _pending.Length ? _pending[_index++] : '\0';

            public override bool MovePrevious()
            {
                if (_index == 0)
                {
                    return false;
                }

                _index--;
                return true;
            }

            public override void Reset()
            {
                _pending = "";
                _index = 0;
            }
        }
    }

    /// <summary><c>print(text, file=writer)</c>.</summary>
    public static void Print(TextWriter writer, string text)
    {
        ArgumentNullException.ThrowIfNull(writer);
        EnsureEncodable(text);
        writer.Write(text);
        writer.Write('\n');
    }

    /// <summary><c>writer.write(text)</c> with Python's encode check.</summary>
    public static void Write(TextWriter writer, string text)
    {
        ArgumentNullException.ThrowIfNull(writer);
        EnsureEncodable(text);
        writer.Write(text);
    }

    /// <summary><c>text.encode("utf-8")</c>.</summary>
    public static byte[] EncodeUtf8(string text)
    {
        EnsureEncodable(text);
        return StrictUtf8.GetBytes(text);
    }

    /// <summary>Raises <c>UnicodeEncodeError</c> for lone surrogates, which Python cannot encode to UTF-8.</summary>
    public static void EnsureEncodable(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var position = 0;
        for (var i = 0; i < text.Length; i++, position++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
                continue;
            }

            if (!char.IsSurrogate(c))
            {
                continue;
            }

            var end = position;
            var j = i + 1;
            while (j < text.Length && char.IsSurrogate(text[j])
                   && !(char.IsHighSurrogate(text[j]) && j + 1 < text.Length && char.IsLowSurrogate(text[j + 1])))
            {
                end++;
                j++;
            }

            var where = end == position
                ? "character " + PyRepr.Str(c.ToString()) + " in position " + position.ToString(CultureInfo.InvariantCulture)
                : "characters in position " + position.ToString(CultureInfo.InvariantCulture) + "-" + end.ToString(CultureInfo.InvariantCulture);
            throw new PyUnicodeError("UnicodeEncodeError", "'utf-8' codec can't encode " + where + ": surrogates not allowed");
        }
    }

    /// <summary><c>bytes.decode("utf-8")</c> with CPython's error messages.</summary>
    public static string DecodeUtf8(ReadOnlySpan<byte> data)
    {
        var position = 0;
        while (position < data.Length)
        {
            var b = data[position];
            int need;
            if (b < 0x80)
            {
                position++;
                continue;
            }

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
                throw DecodeError(data, position, position, "invalid start byte");
            }

            var valid = 0;
            for (var k = 1; k <= need; k++)
            {
                if (position + k >= data.Length)
                {
                    throw DecodeError(data, position, data.Length - 1, "unexpected end of data");
                }

                var next = data[position + k];
                var low = 0x80;
                var high = 0xBF;
                if (k == 1)
                {
                    (low, high) = b switch
                    {
                        0xE0 => (0xA0, 0xBF),
                        0xED => (0x80, 0x9F),
                        0xF0 => (0x90, 0xBF),
                        0xF4 => (0x80, 0x8F),
                        _ => (0x80, 0xBF),
                    };
                }

                if (next < low || next > high)
                {
                    throw DecodeError(data, position, position + valid, "invalid continuation byte");
                }

                valid++;
            }

            position += need + 1;
        }

        return StrictUtf8.GetString(data);
    }

    /// <summary><c>sys.stdin.read()</c> or <c>sys.stdin.read(n)</c>; <paramref name="maxCodePoints"/> counts code points.</summary>
    public static string ReadText(TextReader reader, int? maxCodePoints = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        try
        {
            if (maxCodePoints is null)
            {
                return reader.ReadToEnd();
            }

            var builder = new StringBuilder();
            var count = 0;
            while (count < maxCodePoints.Value)
            {
                var next = reader.Read();
                if (next < 0)
                {
                    break;
                }

                builder.Append((char)next);
                if (char.IsHighSurrogate((char)next) && reader.Peek() is var low && low >= 0 && char.IsLowSurrogate((char)low))
                {
                    builder.Append((char)reader.Read());
                }

                count++;
            }

            return builder.ToString();
        }
        catch (DecoderFallbackException error)
        {
            var bad = error.BytesUnknown is { Length: > 0 } bytes ? bytes[0] : (byte)0;
            throw new PyUnicodeError("UnicodeDecodeError", string.Format(CultureInfo.InvariantCulture,
                "'utf-8' codec can't decode byte 0x{0:x2} in position {1}: invalid start byte", bad, Math.Max(0, error.Index)));
        }
    }

    /// <summary>
    /// <c>Path(path).read_text(encoding="utf-8")</c> (universal newlines), optionally like
    /// <c>open(path, encoding="utf-8").read(n)</c>. Errors are <see cref="PyOSError"/> (<c>[Errno 2] ...</c>)
    /// or <c>UnicodeDecodeError</c>.
    /// </summary>
    public static string ReadTextFile(string path, int? maxCodePoints = null)
    {
        var text = DecodeUtf8(ReadBytes(path));
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (maxCodePoints is { } max && PyRepr.Length(text) > max)
        {
            var builder = new StringBuilder();
            var count = 0;
            foreach (var codePoint in PyRepr.CodePoints(text))
            {
                if (count++ == max)
                {
                    break;
                }

                PyRepr.AppendCodePoint(builder, codePoint);
            }

            text = builder.ToString();
        }

        return text;
    }

    /// <summary><c>Path(path).read_bytes()</c> with Python's errno errors.</summary>
    public static byte[] ReadBytes(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!PosixNative.IsSupported)
        {
            return File.ReadAllBytes(path);
        }

        var fd = PosixNative.Open(path, PosixNative.ORdOnly);
        try
        {
            if (PosixNative.Stat(path).IsDirectory)
            {
                throw PosixNative.Error(PyErrno.EISDIR, path);
            }

            // read(2) errors (EIO, ...) are OSError, as in Python; a FileStream would raise IOException.
            return PosixNative.ReadAll(fd);
        }
        finally
        {
            PosixNative.Close(fd);
        }
    }

    /// <summary>
    /// <c>os.fsencode(path)</c> as the <c>os</c> functions apply it: UTF-8 with <c>surrogateescape</c>, so
    /// U+DC80..U+DCFF become the undecodable bytes 0x80..0xFF they stand for. Any other lone surrogate raises
    /// <c>UnicodeEncodeError</c>; a NUL raises <c>ValueError("embedded null byte")</c>. (.NET's own marshalling
    /// would write U+FFFD, a different file name.)
    /// </summary>
    public static byte[] FsEncode(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var output = new List<byte>(path.Length + 8);
        Span<byte> scratch = stackalloc byte[4];
        for (var i = 0; i < path.Length; i++)
        {
            var c = path[i];
            if (char.IsHighSurrogate(c) && i + 1 < path.Length && char.IsLowSurrogate(path[i + 1]))
            {
                var length = new Rune(c, path[i + 1]).EncodeToUtf8(scratch);
                output.AddRange(scratch[..length]);
                i++;
                continue;
            }

            if (!char.IsSurrogate(c))
            {
                var length = new Rune(c).EncodeToUtf8(scratch);
                output.AddRange(scratch[..length]);
                continue;
            }

            // A run of lone surrogates: escape the U+DC80..U+DCFF prefix; the first other one fails for the
            // rest of the run, as CPython's encoder reports it.
            var end = i;
            while (end < path.Length && char.IsSurrogate(path[end])
                   && !(char.IsHighSurrogate(path[end]) && end + 1 < path.Length && char.IsLowSurrogate(path[end + 1])))
            {
                end++;
            }

            var k = i;
            while (k < end && path[k] is >= '\udc80' and <= '\udcff')
            {
                output.Add((byte)(path[k] & 0xFF));
                k++;
            }

            if (k < end)
            {
                throw EncodeError(path, k, end);
            }

            i = end - 1;
        }

        if (output.Contains(0))
        {
            throw new PyValueError("embedded null byte");
        }

        return [.. output];
    }

    /// <summary><c>os.fsdecode(data)</c>: UTF-8 with <c>surrogateescape</c> (each undecodable byte b becomes U+DC00+b).</summary>
    public static string FsDecode(ReadOnlySpan<byte> data)
    {
        var builder = new StringBuilder(data.Length);
        var position = 0;
        while (position < data.Length)
        {
            if (Rune.DecodeFromUtf8(data[position..], out var rune, out var consumed) == System.Buffers.OperationStatus.Done)
            {
                builder.Append(rune.ToString());
                position += consumed;
            }
            else
            {
                builder.Append((char)(0xDC00 + data[position]));
                position++;
            }
        }

        return builder.ToString();
    }

    /// <summary>The <c>UnicodeEncodeError</c> for the lone surrogates <paramref name="text"/>[start..end) (UTF-16 indexes).</summary>
    private static PyUnicodeError EncodeError(string text, int start, int end)
    {
        var first = PyRepr.CodePointIndex(text, start);
        var last = first + (end - start) - 1;
        var where = last == first
            ? "character " + PyRepr.Str(text[start].ToString()) + " in position " + first.ToString(CultureInfo.InvariantCulture)
            : "characters in position " + first.ToString(CultureInfo.InvariantCulture) + "-" + last.ToString(CultureInfo.InvariantCulture);
        return new PyUnicodeError("UnicodeEncodeError", "'utf-8' codec can't encode " + where + ": surrogates not allowed");
    }

    private static PyUnicodeError DecodeError(ReadOnlySpan<byte> data, int start, int end, string reason)
    {
        var where = start == end
            ? string.Format(CultureInfo.InvariantCulture, "byte 0x{0:x2} in position {1}", data[start], start)
            : string.Format(CultureInfo.InvariantCulture, "bytes in position {0}-{1}", start, end);
        return new PyUnicodeError("UnicodeDecodeError", "'utf-8' codec can't decode " + where + ": " + reason);
    }
}

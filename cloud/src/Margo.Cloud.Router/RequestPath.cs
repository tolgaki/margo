using System.Globalization;
using System.Text;

namespace Margo.Cloud.Router;

/// <summary>
/// The request path as aiohttp saw it, computed from the raw request target: <see cref="Safe"/> is
/// percent-decoded except <c>%2F</c> and <c>%25</c> (aiohttp routes on it: <c>rel_url.path_safe</c>),
/// <see cref="Decoded"/> is fully decoded (<c>request.path</c>, which the activity check and
/// <c>voice_target</c> read). Dot segments are left alone, as aiohttp leaves them (Kestrel's own
/// <c>Request.Path</c> removes them, which would let through paths Python refused).
/// </summary>
internal sealed record RequestPath(string Safe, string Decoded)
{
    /// <summary>From the raw target (origin form <c>/a/b?q</c>, or absolute form <c>http://h/a/b?q</c>).</summary>
    internal static RequestPath FromRawTarget(string rawTarget)
    {
        if (rawTarget == "*")
        {
            return new RequestPath("*", "*"); // asterisk form (OPTIONS *): yarl's path is "*", no route
        }

        var path = rawTarget;
        if (!path.StartsWith('/'))
        {
            var scheme = path.IndexOf("://", StringComparison.Ordinal);
            var slash = scheme < 0 ? -1 : path.IndexOf('/', scheme + 3);
            path = slash < 0 ? "/" : path[slash..];
        }

        var end = path.IndexOfAny(['?', '#']);
        if (end >= 0)
        {
            path = path[..end];
        }

        return new RequestPath(Unquote(path, keepSeparators: true), Unquote(path, keepSeparators: false));
    }

    /// <summary>Percent-decode as UTF-8. An escape that is not two hex digits stays as it is; bytes that
    /// are not valid UTF-8 become U+FFFD (no such path matches a route).</summary>
    private static string Unquote(string path, bool keepSeparators)
    {
        if (!path.Contains('%', StringComparison.Ordinal))
        {
            return path;
        }

        var output = new StringBuilder(path.Length);
        var pending = new List<byte>();
        for (var i = 0; i < path.Length; i++)
        {
            if (path[i] == '%' && i + 2 < path.Length && IsHex(path[i + 1]) && IsHex(path[i + 2]))
            {
                var value = byte.Parse(path.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                if (keepSeparators && value is (byte)'/' or (byte)'%')
                {
                    Flush(output, pending);
                    output.Append(value == (byte)'/' ? "%2F" : "%25"); // yarl writes kept escapes in upper case
                }
                else
                {
                    pending.Add(value);
                }

                i += 2;
                continue;
            }

            Flush(output, pending);
            output.Append(path[i]);
        }

        Flush(output, pending);
        return output.ToString();
    }

    private static void Flush(StringBuilder output, List<byte> pending)
    {
        if (pending.Count > 0)
        {
            output.Append(Encoding.UTF8.GetString([.. pending]));
            pending.Clear();
        }
    }

    private static bool IsHex(char c) => char.IsAsciiHexDigit(c);
}

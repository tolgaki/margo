using System.Numerics;
using System.Text;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.Engine;

/// <summary>
/// <c>email.utils.parsedate_tz</c> (Python 3.13 <c>email/_parseaddr.py</c>), which the openai SDK uses for an HTTP-date
/// <c>retry-after</c>: lenient RFC 2822/850 parsing that refuses ISO 8601 and most other formats.
/// </summary>
internal static class EmailDate
{
    private static readonly string[] MonthNames =
    [
        "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec",
        "january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december",
    ];

    private static readonly string[] DayNames = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];

    private static readonly Dictionary<string, int> TimeZones = new(StringComparer.Ordinal)
    {
        ["UT"] = 0, ["UTC"] = 0, ["GMT"] = 0, ["Z"] = 0,
        ["AST"] = -400, ["ADT"] = -300,
        ["EST"] = -500, ["EDT"] = -400,
        ["CST"] = -600, ["CDT"] = -500,
        ["MST"] = -700, ["MDT"] = -600,
        ["PST"] = -800, ["PDT"] = -700,
    };

    /// <summary>
    /// <c>parsedate_tz(data)</c>: year, month (1-12), day, hour, minute, second and the zone offset in seconds (0 when
    /// unknown), or null. The numbers are Python <c>int</c>s, unbounded and unchecked, as <c>calendar.timegm</c> gets them.
    /// </summary>
    public static (BigInteger Year, int Month, BigInteger Day, BigInteger Hour, BigInteger Minute, BigInteger Second, BigInteger Offset)? ParseDateTz(string data)
    {
        if (data.Length == 0)
        {
            return null;
        }

        var parts = SplitWhitespace(data);
        if (parts.Count == 0)
        {
            return null;
        }

        if (parts[0].EndsWith(',') || DayNames.Contains(Lower(parts[0])))
        {
            parts.RemoveAt(0);
        }
        else
        {
            var comma = parts[0].LastIndexOf(',');
            if (comma >= 0)
            {
                parts[0] = parts[0][(comma + 1)..];
            }
        }

        if (parts.Count == 3)
        {
            // RFC 850 date, deprecated.
            var stuff = parts[0].Split('-');
            if (stuff.Length == 3)
            {
                parts = [.. stuff, .. parts.Skip(1)];
            }
        }

        if (parts.Count == 4)
        {
            var s = parts[3];
            var i = s.IndexOf('+', StringComparison.Ordinal);
            if (i == -1)
            {
                i = s.IndexOf('-', StringComparison.Ordinal);
            }

            if (i > 0)
            {
                parts.RemoveRange(3, parts.Count - 3);
                parts.Add(s[..i]);
                parts.Add(s[i..]);
            }
            else
            {
                parts.Add(""); // dummy zone
            }
        }

        if (parts.Count < 5)
        {
            return null;
        }

        var (dd, mm, yy, tm, tz) = (parts[0], parts[1], parts[2], parts[3], parts[4]);
        if (dd.Length == 0 || mm.Length == 0 || yy.Length == 0)
        {
            return null;
        }

        mm = Lower(mm);
        if (Array.IndexOf(MonthNames, mm) < 0)
        {
            (dd, mm) = (mm, Lower(dd));
            if (Array.IndexOf(MonthNames, mm) < 0)
            {
                return null;
            }
        }

        var month = Array.IndexOf(MonthNames, mm) + 1;
        if (month > 12)
        {
            month -= 12;
        }

        if (dd.EndsWith(','))
        {
            dd = dd[..^1];
        }

        if (yy.IndexOf(':', StringComparison.Ordinal) > 0)
        {
            (yy, tm) = (tm, yy);
        }

        if (yy.Length > 0 && yy.EndsWith(','))
        {
            yy = yy[..^1];
            if (yy.Length == 0)
            {
                return null;
            }
        }
        else if (yy.Length == 0)
        {
            return null; // yy[-1] on an empty string: IndexError in Python, which parsedate_tz does not catch
        }

        if (!PyText.IsDigit(FirstCodePoint(yy)))
        {
            (yy, tz) = (tz, yy);
        }

        if (tm.Length == 0)
        {
            return null; // tm[-1] on an empty string: IndexError in Python
        }

        if (tm.EndsWith(','))
        {
            tm = tm[..^1];
        }

        var time = tm.Split(':');
        string thh, tmm, tss;
        if (time.Length == 2)
        {
            (thh, tmm, tss) = (time[0], time[1], "0");
        }
        else if (time.Length == 3)
        {
            (thh, tmm, tss) = (time[0], time[1], time[2]);
        }
        else if (time.Length == 1 && time[0].Contains('.', StringComparison.Ordinal))
        {
            // Some non-compliant MUAs use '.' to separate time elements.
            var dotted = time[0].Split('.');
            if (dotted.Length == 2)
            {
                (thh, tmm, tss) = (dotted[0], dotted[1], "0");
            }
            else if (dotted.Length == 3)
            {
                (thh, tmm, tss) = (dotted[0], dotted[1], dotted[2]);
            }
            else
            {
                return null;
            }
        }
        else
        {
            return null;
        }

        if (!PyArgumentParser.TryParsePyInt(yy, out var year) || !PyArgumentParser.TryParsePyInt(dd, out var day)
            || !PyArgumentParser.TryParsePyInt(thh, out var hour) || !PyArgumentParser.TryParsePyInt(tmm, out var minute)
            || !PyArgumentParser.TryParsePyInt(tss, out var second))
        {
            return null;
        }

        if (year < 100)
        {
            year += year > 68 ? 1900 : 2000;
        }

        BigInteger? offset = null;
        tz = Upper(tz);
        if (TimeZones.TryGetValue(tz, out var named))
        {
            offset = named;
        }
        else
        {
            if (PyArgumentParser.TryParsePyInt(tz, out var numeric))
            {
                offset = numeric;
            }

            if (offset == 0 && tz.StartsWith('-'))
            {
                offset = null;
            }
        }

        var seconds = BigInteger.Zero;
        if (offset is { } value && !value.IsZero)
        {
            var sign = value.Sign;
            var magnitude = BigInteger.Abs(value);
            // Python's // and % on non-negative values.
            seconds = sign * ((magnitude / 100 * 3600) + (magnitude % 100 * 60));
        }

        // parsedate_tz turns an unknown zone (None) into 0.
        return (year, month, day, hour, minute, second, seconds);
    }

    /// <summary><c>str.split()</c>: runs of Python whitespace.</summary>
    private static List<string> SplitWhitespace(string text)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        foreach (var point in PyText.CodePoints(text))
        {
            if (PyText.IsSpace(point))
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(PyText.CodePointString(point));
            }
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        return parts;
    }

    private static string FirstCodePoint(string text) => PyText.CodePointString(PyText.CodePoints(text)[0]);

    // Header text is Latin-1; .NET's invariant mapping and Python's agree there apart from 'ß' and 'ÿ', which are in no
    // month, day or zone name.
    private static string Lower(string text) => text.ToLowerInvariant();

    private static string Upper(string text) => text.ToUpperInvariant();
}

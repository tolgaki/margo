using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.State.MemoryCommands;

/// <summary>
/// <c>zoneinfo.ZoneInfoNotFoundError</c>, a <c>KeyError</c>: <c>str(exc)</c> is the repr of its message
/// (<c>'No time zone found with key Mars/Base'</c>).
/// </summary>
public sealed class ZoneInfoNotFoundError : PyKeyError
{
    public ZoneInfoNotFoundError(string message) : base(message)
    {
    }

    public override string PythonType => "ZoneInfoNotFoundError";
}

/// <summary>
/// CPython 3.13's <c>zoneinfo.ZoneInfo</c> (the C implementation, <c>Modules/_zoneinfo.c</c>, over
/// <c>zoneinfo/_common.py</c> and <c>zoneinfo/_tzpath.py</c>): IANA keys are looked up on the same <c>TZPATH</c>, the
/// same TZif files are read, and offsets follow PEP 495 (<c>fold</c> selects the offset before or after a gap or an
/// ambiguous hour) with the POSIX TZ string after the last transition. Offsets keep their seconds (local mean time),
/// which .NET's <c>TimeZoneInfo</c> truncates to minutes, and the TZ rules follow CPython (hour 24, negative and
/// up-to-167-hour transition times), where <c>TimeZoneInfo</c> differs for several zones; see PORTING.md.
/// Only offsets are modelled (<c>tzname()</c> and <c>dst()</c> are not used by the callers). Belongs in State.Core.
/// </summary>
public sealed partial class PyZoneInfo
{
    /// <summary><c>sysconfig.get_config_var("TZPATH")</c> of the reference CPython build (Debian/Ubuntu default).</summary>
    public const string DefaultTzPath = "/usr/share/zoneinfo:/usr/lib/zoneinfo:/usr/share/lib/zoneinfo:/etc/zoneinfo";

    private const long EpochOrdinal = 719163;

    private readonly long[] _transUtc;
    private readonly long[][] _transWall;
    private readonly long[] _transOffsets;
    private readonly long _before;
    private readonly Rule _after;

    private PyZoneInfo(string key, long[] transUtc, long[][] transWall, long[] transOffsets, long before, Rule after)
    {
        Key = key;
        _transUtc = transUtc;
        _transWall = transWall;
        _transOffsets = transOffsets;
        _before = before;
        _after = after;
    }

    /// <summary>The key (<c>str(zone)</c>).</summary>
    public string Key { get; }

    /// <summary>A fixed zero offset: <c>timezone.utc</c>, which <c>memory_dream._window</c> uses for <c>"UTC"</c>.</summary>
    public static PyZoneInfo Utc { get; } = new("UTC", [], [[], []], [], 0, Rule.Fixed(0));

    /// <summary>
    /// <c>zoneinfo.TZPATH</c>: <c>PYTHONTZPATH</c> split on <c>:</c> without its relative entries, or the build default.
    /// (CPython also warns about dropped relative entries on stderr; that warning is not reproduced.)
    /// </summary>
    public static IReadOnlyList<string> TzPath()
    {
        var value = PyRuntime.GetEnv("PYTHONTZPATH") ?? DefaultTzPath;
        return value.Length == 0 ? [] : value.Split(':').Where(entry => entry.StartsWith('/')).ToArray();
    }

    /// <summary>
    /// <c>ZoneInfo(key)</c>: <c>TypeError</c> for a key that is not a string (<c>unhashable type</c> for a list or dict),
    /// <c>ValueError</c> for an absolute, unnormalised or escaping key and for a file that is not TZif,
    /// <see cref="ZoneInfoNotFoundError"/> when no file is found (there is no <c>tzdata</c> package), and
    /// <c>OSError</c> when the file cannot be read.
    /// </summary>
    public static PyZoneInfo Load(JsonNode? key)
    {
        if (key is JsonArray or JsonObject)
        {
            throw new PyTypeError("unhashable type: '" + PyJson.TypeName(key) + "'");
        }

        if (PyNode.AsString(key) is not { } text)
        {
            throw new PyTypeError("expected str, bytes or os.PathLike object, not " + PyJson.TypeName(key));
        }

        return Load(text);
    }

    /// <summary><c>ZoneInfo(key)</c> for a string key.</summary>
    public static PyZoneInfo Load(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var path = FindTzFile(key) ?? throw new ZoneInfoNotFoundError("No time zone found with key " + key);
        return FromBytes(key, PyIO.ReadBytes(path));
    }

    /// <summary><c>_tzpath.find_tzfile(key)</c>: validates the key, then the first regular file on <see cref="TzPath"/>.</summary>
    public static string? FindTzFile(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.StartsWith('/'))
        {
            throw new PyValueError("ZoneInfo keys may not be absolute paths, got: " + key);
        }

        var normal = NormPath(key);
        if (PyRepr.Length(normal) != PyRepr.Length(key))
        {
            throw new PyValueError("ZoneInfo keys must be normalized relative paths, got: " + key);
        }

        if (!NormPath("_/" + normal).StartsWith("_/", StringComparison.Ordinal))
        {
            throw new PyValueError("ZoneInfo keys must refer to subdirectories of TZPATH, got: " + key);
        }

        foreach (var root in TzPath())
        {
            var path = root.EndsWith('/') ? root + key : root + "/" + key;
            if (IsFile(path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>
    /// <c>utcoffset()</c> in seconds of the local (wall) time <paramref name="localSeconds"/> (seconds since
    /// 1970-01-01T00:00 local) in <paramref name="year"/>, with PEP 495's <paramref name="fold"/> (C <c>find_ttinfo</c>).
    /// </summary>
    public long UtcOffset(long localSeconds, int year, int fold)
    {
        var wall = _transWall[fold];
        if (wall.Length > 0 && localSeconds < wall[0])
        {
            return _before;
        }

        if (wall.Length == 0 || localSeconds > wall[^1])
        {
            return _after.Local(localSeconds, year, fold);
        }

        return _transOffsets[BisectRight(wall, localSeconds) - 1];
    }

    /// <summary>
    /// The offset <c>fromutc()</c> adds to the UTC time <paramref name="utcSeconds"/> (seconds since the epoch) whose
    /// UTC year is <paramref name="year"/> (C <c>zoneinfo_fromutc</c>).
    /// </summary>
    public long FromUtcOffset(long utcSeconds, int year)
    {
        if (_transUtc.Length > 0 && utcSeconds < _transUtc[0])
        {
            return _before;
        }

        if (_transUtc.Length == 0 || utcSeconds > _transUtc[^1])
        {
            return _after.FromUtc(utcSeconds, year);
        }

        return _transOffsets[BisectRight(_transUtc, utcSeconds) - 1];
    }

    /// <summary><c>dt.utcoffset()</c> of a naive wall time placed in this zone with the given fold.</summary>
    public PyTimeDelta UtcOffset(PyDateTime wall, int fold) => PyTimeDelta.FromSeconds(UtcOffset(LocalSeconds(wall), wall.Year, fold));

    /// <summary>
    /// <c>utc.astimezone(zone)</c> for an aware UTC datetime: the wall time (naive), raising <c>OverflowError</c> past
    /// the datetime range as <c>dt + tti.utcoff</c> does.
    /// </summary>
    public PyDateTime FromUtc(PyDateTime utc)
    {
        var naive = utc.WithOffset(null);
        return naive + PyTimeDelta.FromSeconds(FromUtcOffset(LocalSeconds(naive), naive.Year));
    }

    /// <summary><c>(dt.toordinal() - EPOCHORDINAL) * 86400 + dt.hour * 3600 + dt.minute * 60 + dt.second</c>.</summary>
    public static long LocalSeconds(PyDateTime value) =>
        ((value.Date.ToOrdinal() - EpochOrdinal) * 86400) + (value.Hour * 3600L) + (value.Minute * 60L) + value.Second;

    /// <summary>The UTC transition times, for the differential sweep (<c>_trans_utc</c> of the pure-Python twin).</summary>
    public IReadOnlyList<long> TransitionsUtc => _transUtc;

    /// <summary>
    /// For the differential sweep: the TZ rule after the last transition in <paramref name="year"/>
    /// (<c>_TZStr.transitions(year)</c>, <c>dst_diff</c> and the standard and daylight offsets), or null for a fixed offset.
    /// </summary>
    public (long Start, long End, long DstDiff, long Std, long Dst)? RuleTransitions(int year) => _after.Transitions(year);

    /// <summary>
    /// The zone a TZif payload describes (<c>_common.load_data</c> plus the C <c>load_data</c>). Damaged files raise
    /// <c>ValueError</c>s; their texts follow CPython only for the missing magic and the transition index.
    /// </summary>
    public static PyZoneInfo FromBytes(string key, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var reader = new TzifReader(data);
        var header = reader.Header();
        var timeSize = 4;
        if (header.Version != 1)
        {
            timeSize = 8;
            reader.Skip((header.TimeCount * 5L) + (header.TypeCount * 6L) + header.CharCount + (header.LeapCount * 8L)
                        + header.IsStdCount + header.IsUtcCount);
            header = reader.Header();
        }

        var transUtc = new long[header.TimeCount];
        for (var i = 0; i < transUtc.Length; i++)
        {
            transUtc[i] = timeSize == 4 ? reader.Int32() : reader.Int64();
        }

        var transIndex = new int[header.TimeCount];
        for (var i = 0; i < transIndex.Length; i++)
        {
            transIndex[i] = reader.Byte();
        }

        if (transIndex.Length > 0 && transIndex.Max() >= header.TypeCount)
        {
            throw new PyValueError("Invalid transition index found while reading TZif: "
                                   + transIndex.Max().ToString(CultureInfo.InvariantCulture));
        }

        var utcoff = new long[header.TypeCount];
        var isdst = new bool[header.TypeCount];
        for (var i = 0; i < header.TypeCount; i++)
        {
            utcoff[i] = reader.Int32();
            isdst[i] = reader.Byte() != 0;
            reader.Byte();
        }

        reader.Skip(header.CharCount);
        string? tzString = null;
        if (header.Version >= 2)
        {
            reader.Skip(header.IsUtcCount + header.IsStdCount + (header.LeapCount * 12L));
            if (reader.Byte() != '\n')
            {
                throw new PyValueError("Invalid TZif file: unexpected end of file");
            }

            tzString = reader.Line();
        }

        long[][] wall = [(long[])transUtc.Clone(), (long[])transUtc.Clone()];
        if (transUtc.Length > 0)
        {
            long offset0, offset1;
            if (utcoff.Length > 1)
            {
                offset0 = utcoff[0];
                offset1 = utcoff[transIndex[0]];
            }
            else
            {
                offset0 = offset1 = utcoff[0];
            }

            for (var i = 0; i < transUtc.Length; i++)
            {
                if (i > 0)
                {
                    offset0 = utcoff[transIndex[i - 1]];
                    offset1 = utcoff[transIndex[i]];
                }

                if (offset1 > offset0)
                {
                    (offset0, offset1) = (offset1, offset0);
                }

                wall[0][i] += offset0;
                wall[1][i] += offset1;
            }
        }

        var transOffsets = transIndex.Select(index => utcoff[index]).ToArray();
        var before = 0L;
        var found = false;
        for (var i = 0; i < isdst.Length; i++)
        {
            if (!isdst[i])
            {
                before = utcoff[i];
                found = true;
                break;
            }
        }

        if (!found && utcoff.Length > 0)
        {
            before = utcoff[0];
        }

        Rule after;
        if (!string.IsNullOrEmpty(tzString))
        {
            after = Rule.Parse(tzString);
        }
        else
        {
            if (utcoff.Length == 0)
            {
                throw new PyValueError("No time zone information found.");
            }

            after = Rule.Fixed(transIndex.Length == 0 ? utcoff[^1] : utcoff[transIndex[^1]]);
        }

        return new PyZoneInfo(key, transUtc, wall, transOffsets, before, after);
    }

    /// <summary><c>bisect.bisect_right</c>.</summary>
    private static int BisectRight(long[] values, long value)
    {
        int low = 0, high = values.Length;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (value < values[middle])
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }

        return low;
    }

    /// <summary><c>os.path.isfile</c>: false on any <c>OSError</c> or <c>ValueError</c> (unencodable names, NUL).</summary>
    private static bool IsFile(string path)
    {
        try
        {
            return PosixNative.Stat(path).IsRegular;
        }
        catch (Exception ex) when (ex is PyOSError or PyValueError)
        {
            return false;
        }
    }

    /// <summary><c>posixpath.normpath</c> of a relative path.</summary>
    internal static string NormPath(string path)
    {
        if (path.Length == 0)
        {
            return ".";
        }

        var initial = 0;
        if (path.StartsWith('/'))
        {
            initial = path.StartsWith("//", StringComparison.Ordinal) && !path.StartsWith("///", StringComparison.Ordinal) ? 2 : 1;
        }

        var parts = new List<string>();
        foreach (var part in path.Split('/'))
        {
            if (part.Length == 0 || part == ".")
            {
                continue;
            }

            if (part != ".." || (initial == 0 && parts.Count == 0) || (parts.Count > 0 && parts[^1] == ".."))
            {
                parts.Add(part);
            }
            else if (parts.Count > 0)
            {
                parts.RemoveAt(parts.Count - 1);
            }
        }

        var text = new string('/', initial) + string.Join('/', parts);
        return text.Length == 0 ? "." : text;
    }

    /// <summary><c>days_before_year</c> for the datetime range (years 1..9999, so <c>y</c> is never negative).</summary>
    private static long DaysBeforeYear(long year)
    {
        var y = year - 1;
        return (y * 365) + (y / 4) - (y / 100) + (y / 400);
    }

    private static bool IsLeap(long year) => year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);

    private static readonly int[] DaysBeforeMonth = [-1, 0, 31, 59, 90, 120, 151, 181, 212, 243, 273, 304, 334];

    private static readonly int[] DaysInMonth = [-1, 31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];

    private static long YmdToOrdinal(long year, int month, long day) =>
        DaysBeforeYear(year) + DaysBeforeMonth[month] + (month > 2 && IsLeap(year) ? 1 : 0) + day;

    /// <summary>The TZif header (<c>_TZifHeader</c>).</summary>
    private readonly record struct TzifHeader(int Version, long IsUtcCount, long IsStdCount, long LeapCount, int TimeCount, int TypeCount,
                                             long CharCount);

    /// <summary>A forward reader over the TZif bytes.</summary>
    private sealed class TzifReader(byte[] data)
    {
        private long _position;

        public TzifHeader Header()
        {
            if (_position + 4 > data.Length || data[(int)_position] != 'T' || data[(int)_position + 1] != 'Z'
                || data[(int)_position + 2] != 'i' || data[(int)_position + 3] != 'f')
            {
                throw new PyValueError("Invalid TZif file: magic not found");
            }

            _position += 4;
            var versionByte = Byte();
            int version;
            if (versionByte == 0)
            {
                version = 1;
            }
            else if (versionByte is >= '0' and <= '9')
            {
                version = versionByte - '0';
            }
            else
            {
                throw new PyValueError("invalid literal for int() with base 10: " + PyRepr.Str(((char)versionByte).ToString()));
            }

            Skip(15);
            var counts = new long[6];
            for (var i = 0; i < 6; i++)
            {
                counts[i] = Int32();
            }

            if (counts.Any(count => count < 0 || count > data.Length))
            {
                throw new PyValueError("Invalid TZif file: unexpected end of file");
            }

            return new TzifHeader(version, counts[0], counts[1], counts[2], (int)counts[3], (int)counts[4], counts[5]);
        }

        public void Skip(long count) => _position += count;

        public int Byte()
        {
            Need(1);
            return data[(int)_position++];
        }

        public long Int32()
        {
            Need(4);
            var value = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan((int)_position, 4));
            _position += 4;
            return value;
        }

        public long Int64()
        {
            Need(8);
            var value = BinaryPrimitives.ReadInt64BigEndian(data.AsSpan((int)_position, 8));
            _position += 8;
            return value;
        }

        /// <summary><c>readline()</c> without its newline; a line without one is an unexpected end of file.</summary>
        public string Line()
        {
            var start = (int)_position;
            var end = Array.IndexOf(data, (byte)'\n', start);
            if (end < 0)
            {
                throw new PyValueError("Invalid TZif file: unexpected end of file");
            }

            _position = end + 1;
            return PyIO.DecodeUtf8(data.AsSpan(start, end - start));
        }

        private void Need(int count)
        {
            if (_position < 0 || _position + count > data.Length)
            {
                throw new PyValueError("Invalid TZif file: unexpected end of file");
            }
        }
    }

    /// <summary>A POSIX TZ rule (<c>_tzrule</c>): a fixed offset, or standard and daylight time between two dates.</summary>
    private sealed partial class Rule
    {
        private long _std;
        private long _dst;
        private long _dstDiff;
        private bool _stdOnly;
        private Transition? _start;
        private Transition? _end;

        public static Rule Fixed(long offset) => new() { _std = offset, _stdOnly = true };

        /// <summary><c>_parse_tz_str</c> (system TZ strings only; damaged strings raise a <c>ValueError</c> naming them).</summary>
        public static Rule Parse(string text)
        {
            var comma = text.IndexOf(',', StringComparison.Ordinal);
            var offsetText = comma < 0 ? text : text[..comma];
            var match = TzPattern().Match(offsetText);
            if (!match.Success)
            {
                throw new PyValueError(text + " is not a valid TZ string");
            }

            if (!match.Groups["stdoff"].Success)
            {
                throw new PyValueError("Invalid STD offset in " + text);
            }

            var std = Delta(match.Groups["stdoff"].Value, text, "STD");
            if (!match.Groups["dst"].Success)
            {
                if (comma >= 0)
                {
                    throw new PyValueError("Transition rule present without DST: " + text);
                }

                return Fixed(std);
            }

            var dst = match.Groups["dstoff"].Success ? Delta(match.Groups["dstoff"].Value, text, "DST") : std + 3600;
            if (comma < 0)
            {
                throw new PyValueError("Missing transition rules: " + text);
            }

            var rules = text[(comma + 1)..].Split(',', 2);
            if (rules.Length != 2)
            {
                throw new PyValueError("Invalid TZ string: " + text);
            }

            return new Rule
            {
                _std = std,
                _dst = dst,
                _dstDiff = dst - std,
                _start = Transition.Parse(rules[0], text),
                _end = Transition.Parse(rules[1], text),
            };
        }

        public (long Start, long End, long DstDiff, long Std, long Dst)? Transitions(int year) =>
            _stdOnly ? null : (_start!.YearToEpoch(year), _end!.YearToEpoch(year), _dstDiff, _std, _dst);

        /// <summary><c>find_tzrule_ttinfo</c>.</summary>
        public long Local(long ts, int year, int fold)
        {
            if (_stdOnly)
            {
                return _std;
            }

            var start = _start!.YearToEpoch(year);
            var end = _end!.YearToEpoch(year);
            if ((fold == 1) == (_dstDiff >= 0))
            {
                end -= _dstDiff;
            }
            else
            {
                start += _dstDiff;
            }

            var isDst = start < end ? ts >= start && ts < end : ts < end || ts >= start;
            return isDst ? _dst : _std;
        }

        /// <summary><c>find_tzrule_ttinfo_fromutc</c> (the offset only).</summary>
        public long FromUtc(long ts, int year)
        {
            if (_stdOnly)
            {
                return _std;
            }

            var start = _start!.YearToEpoch(year) - _std;
            var end = _end!.YearToEpoch(year) - _dst;
            var isDst = start < end ? ts >= start && ts < end : ts < end || ts >= start;
            return isDst ? _dst : _std;
        }

        private static long Delta(string text, string whole, string which)
        {
            var match = DeltaPattern().Match(text);
            var hours = long.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
            var minutes = match.Groups["m"].Success ? long.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
            var seconds = match.Groups["s"].Success ? long.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture) : 0;
            if (hours > 24)
            {
                throw new PyValueError("Invalid " + which + " offset in " + whole);
            }

            var total = (hours * 3600) + (minutes * 60) + seconds;
            return match.Groups["sign"].Value == "-" ? total : -total;
        }

        [GeneratedRegex(@"\A(?<std>[a-zA-Z]+|<[a-zA-Z0-9+-]+>)(?:(?<stdoff>[+-]?[0-9]{1,3}(?::[0-9]{2}(?::[0-9]{2})?)?)(?:(?<dst>[a-zA-Z]+|<[a-zA-Z0-9+-]+>)(?<dstoff>[+-]?[0-9]{1,3}(?::[0-9]{2}(?::[0-9]{2})?)?)?)?)?\z",
                        RegexOptions.CultureInvariant)]
        private static partial Regex TzPattern();

        [GeneratedRegex(@"\A(?<sign>[+-])?(?<h>[0-9]{1,3})(?::(?<m>[0-9]{2})(?::(?<s>[0-9]{2}))?)?\z", RegexOptions.CultureInvariant)]
        internal static partial Regex DeltaPattern();
    }

    /// <summary>A transition date and time of a TZ rule: <c>Mm.w.d</c> (<c>_CalendarOffset</c>) or <c>Jn</c>/<c>n</c> (<c>_DayOffset</c>).</summary>
    private sealed partial class Transition
    {
        private int _month;
        private int _week;
        private int _weekday;
        private long _day;
        private bool _julian;
        private bool _calendar;
        private long _hour = 2;
        private long _minute;
        private long _second;

        public static Transition Parse(string text, string whole)
        {
            var slash = text.IndexOf('/', StringComparison.Ordinal);
            var date = slash < 0 ? text : text[..slash];
            Transition result;
            if (date.StartsWith('M'))
            {
                var match = CalendarPattern().Match(date);
                if (!match.Success)
                {
                    throw new PyValueError("Invalid TZ string: " + whole);
                }

                result = new Transition
                {
                    _calendar = true,
                    _month = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                    _week = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                    _weekday = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture),
                };
                if (result._month is < 1 or > 12 || result._week is < 1 or > 5 || result._weekday > 6)
                {
                    throw new PyValueError("Invalid TZ string: " + whole);
                }
            }
            else
            {
                var julian = date.StartsWith('J');
                var digits = julian ? date[1..] : date;
                if (!DayPattern().IsMatch(digits))
                {
                    throw new PyValueError("Invalid TZ string: " + whole);
                }

                var day = long.Parse(digits, CultureInfo.InvariantCulture);
                if (day < (julian ? 1 : 0) || day > 365)
                {
                    throw new PyValueError("Invalid TZ string: " + whole);
                }

                result = new Transition { _day = day, _julian = julian };
            }

            if (slash >= 0)
            {
                var match = Rule.DeltaPattern().Match(text[(slash + 1)..]);
                if (!match.Success)
                {
                    throw new PyValueError("Invalid TZ string: " + whole);
                }

                var hours = long.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
                var minutes = match.Groups["m"].Success ? long.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
                var seconds = match.Groups["s"].Success ? long.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture) : 0;
                if (hours > 167)
                {
                    throw new PyValueError("Invalid TZ string: " + whole);
                }

                var sign = match.Groups["sign"].Value == "-" ? -1 : 1;
                result._hour = sign * hours;
                result._minute = sign * minutes;
                result._second = sign * seconds;
            }

            return result;
        }

        /// <summary><c>calendarrule_year_to_timestamp</c> / <c>dayrule_year_to_timestamp</c>: local seconds since the epoch.</summary>
        public long YearToEpoch(int year)
        {
            long days;
            if (_calendar)
            {
                var firstDay = (YmdToOrdinal(year, _month, 1) + 6) % 7;
                var daysInMonth = DaysInMonth[_month] + (_month == 2 && IsLeap(year) ? 1 : 0);
                var monthDay = (_weekday - (firstDay + 1)) % 7;
                if (monthDay < 0)
                {
                    monthDay += 7;
                }

                monthDay += 1 + ((_week - 1) * 7);
                if (monthDay > daysInMonth)
                {
                    monthDay -= 7;
                }

                days = YmdToOrdinal(year, _month, monthDay) - EpochOrdinal;
            }
            else
            {
                var day = _day;
                if (_julian && day >= 59 && IsLeap(year))
                {
                    day += 1;
                }

                days = YmdToOrdinal(year, 1, 1) - EpochOrdinal - 1 + day;
            }

            return (days * 86400) + (_hour * 3600) + (_minute * 60) + _second;
        }

        [GeneratedRegex(@"\AM([0-9]{1,2})\.([0-9])\.([0-9])\z", RegexOptions.CultureInvariant)]
        private static partial Regex CalendarPattern();

        [GeneratedRegex(@"\A[0-9]{1,3}\z", RegexOptions.CultureInvariant)]
        private static partial Regex DayPattern();
    }
}

using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;

namespace Margo.Cloud.State.Core;

/// <summary>
/// Python <c>datetime.timedelta</c>: microsecond resolution, normalised to days/seconds/microseconds,
/// days limited to -999999999..999999999 (so <c>timedelta.min</c> is exactly -999999999 days and
/// <c>timedelta.max</c> is 999999999 days, 23:59:59.999999).
/// </summary>
public readonly struct PyTimeDelta : IEquatable<PyTimeDelta>, IComparable<PyTimeDelta>
{
    private const long MicrosPerSecond = 1_000_000;
    private const long MicrosPerDay = 86_400L * MicrosPerSecond;
    private const long MaxDays = 999_999_999;

    private PyTimeDelta(Int128 micros)
    {
        CheckDays(Floor(micros, MicrosPerDay));
        TotalMicroseconds = micros;
    }

    /// <summary>
    /// <c>microseconds_to_delta_ex</c>'s checks on the normalised days: they must fit a C <c>int</c>
    /// (<c>OverflowError: Python int too large to convert to C int</c>), then <c>check_delta_day_range</c>
    /// (<c>days=-1000000000; must have magnitude &lt;= 999999999</c>).
    /// </summary>
    private static void CheckDays(BigInteger days)
    {
        if (days < int.MinValue || days > int.MaxValue)
        {
            throw new PyOverflowError("Python int too large to convert to C int");
        }

        if (BigInteger.Abs(days) > MaxDays)
        {
            throw new PyOverflowError(string.Format(CultureInfo.InvariantCulture,
                "days={0}; must have magnitude <= 999999999", days));
        }
    }

    /// <summary>The exact length in microseconds.</summary>
    public Int128 TotalMicroseconds { get; }

    /// <summary>Normalised days (may be negative).</summary>
    public long Days => (long)Floor(TotalMicroseconds, MicrosPerDay);

    /// <summary>Normalised seconds, 0..86399.</summary>
    public int Seconds => (int)((TotalMicroseconds - (Int128)Days * MicrosPerDay) / MicrosPerSecond);

    /// <summary>Normalised microseconds, 0..999999.</summary>
    public int Microseconds => (int)((TotalMicroseconds - (Int128)Days * MicrosPerDay) % MicrosPerSecond);

    public static PyTimeDelta Zero => default;

    public static PyTimeDelta FromMicroseconds(Int128 microseconds) => new(microseconds);

    /// <summary><c>timedelta(microseconds=n)</c> for any Python int.</summary>
    public static PyTimeDelta FromMicroseconds(BigInteger microseconds)
    {
        var days = BigInteger.Divide(microseconds - (microseconds.Sign < 0 ? MicrosPerDay - 1 : 0), MicrosPerDay);
        CheckDays(days);
        return new((Int128)microseconds);
    }

    /// <summary>
    /// <c>timedelta(seconds=value)</c> for a Python int or float (or a value read from SQLite): ints exactly; floats
    /// as CPython's <c>delta_new</c>/<c>accum</c> do (integral part exact, fraction scaled to microseconds, the leftover
    /// rounded half to even); NaN, infinities, out-of-range days and other types raise Python's
    /// <c>ValueError</c>/<c>OverflowError</c>/<c>TypeError</c>.
    /// </summary>
    public static PyTimeDelta FromPySeconds(object? value)
    {
        BigInteger micros;
        switch (value is JsonNode node ? PyJson.Scalar(node) : value)
        {
            case bool flag:
                micros = flag ? MicrosPerSecond : 0;
                break;
            case long integer:
                micros = (BigInteger)integer * MicrosPerSecond;
                break;
            case BigInteger big:
                micros = big * MicrosPerSecond;
                break;
            case double number:
            {
                if (double.IsNaN(number))
                {
                    throw new PyValueError("cannot convert float NaN to integer");
                }

                if (double.IsInfinity(number))
                {
                    throw new PyOverflowError("cannot convert float infinity to integer");
                }

                var whole = Math.Truncate(number); // modf
                var fraction = number - whole;
                micros = new BigInteger(whole) * MicrosPerSecond;
                var leftover = 0.0;
                if (fraction != 0.0)
                {
                    var scaled = 1_000_000.0 * fraction;
                    var scaledWhole = Math.Truncate(scaled);
                    micros += new BigInteger(scaledWhole);
                    leftover = scaled - scaledWhole;
                }

                if (leftover != 0.0)
                {
                    var rounded = Math.Round(leftover, MidpointRounding.AwayFromZero); // C round()
                    if (Math.Abs(rounded - leftover) == 0.5)
                    {
                        var odd = micros.IsEven ? 0 : 1;
                        rounded = (2.0 * Math.Round((leftover + odd) * 0.5, MidpointRounding.AwayFromZero)) - odd;
                    }

                    micros += new BigInteger(rounded);
                }

                break;
            }

            default:
                throw new PyTypeError("unsupported type for timedelta seconds component: " + PyObj.TypeName(value));
        }

        return FromMicroseconds(micros);
    }

    public static PyTimeDelta FromSeconds(long seconds) => new((Int128)seconds * MicrosPerSecond);

    public static PyTimeDelta FromMinutes(long minutes) => FromSeconds(minutes * 60);

    public static PyTimeDelta FromHours(long hours) => FromSeconds(hours * 3600);

    public static PyTimeDelta FromDays(long days) => new((Int128)days * MicrosPerDay);

    /// <summary><c>timedelta(days=..., seconds=..., microseconds=...)</c> with integer arguments.</summary>
    public static PyTimeDelta Create(long days = 0, long seconds = 0, long microseconds = 0, long minutes = 0,
                                     long hours = 0, long weeks = 0) =>
        new((Int128)days * MicrosPerDay + (Int128)seconds * MicrosPerSecond + microseconds
            + (Int128)minutes * 60 * MicrosPerSecond + (Int128)hours * 3600 * MicrosPerSecond
            + (Int128)weeks * 7 * MicrosPerDay);

    /// <summary><c>total_seconds()</c>.</summary>
    public double TotalSeconds() => (double)TotalMicroseconds / MicrosPerSecond;

    public static PyTimeDelta operator +(PyTimeDelta a, PyTimeDelta b) => new(a.TotalMicroseconds + b.TotalMicroseconds);

    public static PyTimeDelta operator -(PyTimeDelta a, PyTimeDelta b) => new(a.TotalMicroseconds - b.TotalMicroseconds);

    public static PyTimeDelta operator -(PyTimeDelta a) => new(-a.TotalMicroseconds);

    public static PyTimeDelta operator *(PyTimeDelta a, long factor) => new(a.TotalMicroseconds * factor);

    /// <summary><c>td // n</c> (floor division by an int).</summary>
    public PyTimeDelta FloorDivide(long divisor) => new(Floor(TotalMicroseconds, divisor));

    /// <summary><c>td // other</c>.</summary>
    public Int128 FloorDivide(PyTimeDelta other) => Floor(TotalMicroseconds, other.TotalMicroseconds);

    public static bool operator ==(PyTimeDelta a, PyTimeDelta b) => a.TotalMicroseconds == b.TotalMicroseconds;

    public static bool operator !=(PyTimeDelta a, PyTimeDelta b) => !(a == b);

    public static bool operator <(PyTimeDelta a, PyTimeDelta b) => a.TotalMicroseconds < b.TotalMicroseconds;

    public static bool operator >(PyTimeDelta a, PyTimeDelta b) => a.TotalMicroseconds > b.TotalMicroseconds;

    public static bool operator <=(PyTimeDelta a, PyTimeDelta b) => a.TotalMicroseconds <= b.TotalMicroseconds;

    public static bool operator >=(PyTimeDelta a, PyTimeDelta b) => a.TotalMicroseconds >= b.TotalMicroseconds;

    public bool Equals(PyTimeDelta other) => this == other;

    public override bool Equals(object? obj) => obj is PyTimeDelta other && Equals(other);

    public override int GetHashCode() => TotalMicroseconds.GetHashCode();

    public int CompareTo(PyTimeDelta other) => TotalMicroseconds.CompareTo(other.TotalMicroseconds);

    /// <summary><c>str(td)</c>: <c>1 day, 0:00:05</c>, <c>-1 day, 23:59:59</c>, <c>0:00:00.000001</c>.</summary>
    public override string ToString()
    {
        var seconds = Seconds;
        var text = string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", seconds / 3600, seconds / 60 % 60, seconds % 60);
        if (Days != 0)
        {
            text = string.Format(CultureInfo.InvariantCulture, "{0} day{1}, ", Days, Math.Abs(Days) != 1 ? "s" : "") + text;
        }

        if (Microseconds != 0)
        {
            text += "." + Microseconds.ToString("000000", CultureInfo.InvariantCulture);
        }

        return text;
    }

    /// <summary><c>repr(td)</c>: <c>datetime.timedelta(days=1, seconds=1800)</c>.</summary>
    public string Repr()
    {
        var parts = new List<string>();
        if (Days != 0)
        {
            parts.Add("days=" + Days.ToString(CultureInfo.InvariantCulture));
        }

        if (Seconds != 0)
        {
            parts.Add("seconds=" + Seconds.ToString(CultureInfo.InvariantCulture));
        }

        if (Microseconds != 0)
        {
            parts.Add("microseconds=" + Microseconds.ToString(CultureInfo.InvariantCulture));
        }

        return "datetime.timedelta(" + (parts.Count == 0 ? "0" : string.Join(", ", parts)) + ")";
    }

    internal static Int128 Floor(Int128 value, Int128 divisor)
    {
        var quotient = value / divisor;
        if ((value % divisor != 0) && ((value < 0) != (divisor < 0)))
        {
            quotient -= 1;
        }

        return quotient;
    }
}

/// <summary>Python <c>datetime.date</c>.</summary>
public readonly record struct PyDate
{
    public PyDate(int year, int month, int day)
    {
        PyDateTime.CheckDate(year, month, day);
        Year = year;
        Month = month;
        Day = day;
    }

    public int Year { get; }

    public int Month { get; }

    public int Day { get; }

    /// <summary><c>date.fromisoformat</c> (CPython 3.13 C rules).</summary>
    public static PyDate FromIsoformat(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = PyTimeParsing.Utf8OrNull(text);
        if (bytes is null || bytes.Length is not (7 or 8 or 10)
            || PyTimeParsing.ParseDate(bytes, bytes.Length, out var year, out var month, out var day) < 0)
        {
            throw new PyValueError("Invalid isoformat string: " + PyRepr.Str(text));
        }

        return new PyDate(year, month, day);
    }

    /// <summary><c>date.isoformat()</c>.</summary>
    public string Isoformat() => string.Format(CultureInfo.InvariantCulture, "{0:0000}-{1:00}-{2:00}", Year, Month, Day);

    public override string ToString() => Isoformat();

    /// <summary><c>date + timedelta</c> (whole days only, as Python ignores the rest).</summary>
    public PyDate AddDays(PyTimeDelta delta)
    {
        var ordinal = PyDateTime.DaysBeforeYear(Year) + PyDateTime.DaysBeforeMonth(Year, Month) + Day + delta.Days;
        if (ordinal < 1 || ordinal > PyDateTime.MaxOrdinal)
        {
            throw new PyOverflowError("date value out of range");
        }

        var (y, m, d) = PyDateTime.FromOrdinal(ordinal);
        return new PyDate(y, m, d);
    }

    /// <summary>The proleptic Gregorian ordinal (<c>date.toordinal()</c>).</summary>
    public long ToOrdinal() => PyDateTime.DaysBeforeYear(Year) + PyDateTime.DaysBeforeMonth(Year, Month) + Day;
}

/// <summary>Python <c>datetime.time</c> with an optional fixed UTC offset.</summary>
public readonly record struct PyTimeOfDay
{
    public PyTimeOfDay(int hour, int minute = 0, int second = 0, int microsecond = 0, PyTimeDelta? utcOffset = null)
    {
        PyDateTime.CheckTime(hour, minute, second, microsecond);
        Hour = hour;
        Minute = minute;
        Second = second;
        Microsecond = microsecond;
        UtcOffset = utcOffset;
    }

    public int Hour { get; }

    public int Minute { get; }

    public int Second { get; }

    public int Microsecond { get; }

    /// <summary>The fixed offset (<c>tzinfo</c>), or null for a naive time.</summary>
    public PyTimeDelta? UtcOffset { get; }

    /// <summary><c>time.fromisoformat</c> (CPython 3.13 C rules).</summary>
    public static PyTimeOfDay FromIsoformat(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var invalid = new PyValueError("Invalid isoformat string: " + PyRepr.Str(text));
        var bytes = PyTimeParsing.Utf8OrNull(text) ?? throw invalid;
        var start = bytes.Length > 0 && bytes[0] == 'T' ? 1 : 0;
        var rv = PyTimeParsing.ParseTime(bytes, start, bytes.Length - start, out var hour, out var minute,
                                         out var second, out var microsecond, out var tzSeconds, out var tzMicros);
        if (rv < 0)
        {
            throw invalid;
        }

        var offset = PyTimeParsing.Offset(rv, tzSeconds, tzMicros);
        return new PyTimeOfDay(hour, minute, second, microsecond, offset);
    }

    /// <summary><c>time.isoformat(timespec)</c>.</summary>
    public string Isoformat(string timespec = "auto") =>
        PyDateTime.FormatTime(Hour, Minute, Second, Microsecond, timespec) + PyDateTime.FormatOffset(UtcOffset);

    public override string ToString() => Isoformat();
}

/// <summary>
/// Python <c>datetime.datetime</c> with a fixed UTC offset (<c>timezone(timedelta)</c>) or naive.
/// Microsecond resolution; years 1..9999; comparisons and subtraction between naive and aware values
/// raise <c>TypeError</c> as in Python.
/// </summary>
public readonly struct PyDateTime : IEquatable<PyDateTime>
{
    internal const long MaxOrdinal = 3652059;
    private const long MicrosPerDay = 86_400_000_000L;
    private static readonly int[] DaysInMonthTable = [0, 31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
    private static readonly int[] DaysBeforeMonthTable = [0, 0, 31, 59, 90, 120, 151, 181, 212, 243, 273, 304, 334];

    // Microseconds since 0001-01-01T00:00:00 of the wall clock.
    private readonly long _wall;

    public PyDateTime(int year, int month, int day, int hour = 0, int minute = 0, int second = 0, int microsecond = 0,
                      PyTimeDelta? utcOffset = null)
    {
        CheckDate(year, month, day);
        CheckTime(hour, minute, second, microsecond);
        if (utcOffset is { } offset)
        {
            CheckOffset(offset);
        }

        _wall = ((DaysBeforeYear(year) + DaysBeforeMonth(year, month) + day - 1) * MicrosPerDay)
                + ((((hour * 60L) + minute) * 60L) + second) * 1_000_000L + microsecond;
        UtcOffset = utcOffset;
    }

    private PyDateTime(long wall, PyTimeDelta? offset)
    {
        if (wall < 0 || wall >= MaxOrdinal * MicrosPerDay)
        {
            throw new PyOverflowError("date value out of range");
        }

        _wall = wall;
        UtcOffset = offset;
    }

    /// <summary>The fixed offset (<c>utcoffset()</c>), or null for a naive datetime.</summary>
    public PyTimeDelta? UtcOffset { get; }

    public bool IsAware => UtcOffset is not null;

    public int Year => FromOrdinal(_wall / MicrosPerDay + 1).Year;

    public int Month => FromOrdinal(_wall / MicrosPerDay + 1).Month;

    public int Day => FromOrdinal(_wall / MicrosPerDay + 1).Day;

    public int Hour => (int)(_wall % MicrosPerDay / 3_600_000_000L);

    public int Minute => (int)(_wall % 3_600_000_000L / 60_000_000L);

    public int Second => (int)(_wall % 60_000_000L / 1_000_000L);

    public int Microsecond => (int)(_wall % 1_000_000L);

    public PyDate Date => new(Year, Month, Day);

    /// <summary><c>timezone.utc</c> as an offset.</summary>
    public static PyTimeDelta Utc => PyTimeDelta.Zero;

    /// <summary><c>datetime.now(timezone.utc)</c>, truncated to microseconds.</summary>
    public static PyDateTime Now()
    {
        var now = PyRuntime.UtcNow().UtcDateTime;
        var micros = (now.Ticks - DateTime.MinValue.Ticks) / 10;
        return new PyDateTime(micros, PyTimeDelta.Zero);
    }

    /// <summary>Converts a .NET instant (truncated to microseconds) to an aware UTC datetime.</summary>
    public static PyDateTime FromDateTimeOffset(DateTimeOffset value)
    {
        var micros = (value.UtcDateTime.Ticks - DateTime.MinValue.Ticks) / 10;
        return new PyDateTime(micros, PyTimeDelta.Zero);
    }

    /// <summary><c>datetime.fromisoformat</c> (CPython 3.13 C rules, including its quirks).</summary>
    public static PyDateTime FromIsoformat(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var invalid = new PyValueError("Invalid isoformat string: " + PyRepr.Str(text));
        if (PyRepr.Length(text) < 7)
        {
            throw invalid;
        }

        var bytes = PyTimeParsing.Utf8OrNull(PyTimeParsing.SanitizeSeparator(text)) ?? throw invalid;
        var separator = PyTimeParsing.FindSeparator(bytes);
        if (separator < 0)
        {
            throw invalid;
        }

        int hour = 0, minute = 0, second = 0, microsecond = 0, tzSeconds = 0, tzMicros = 0;
        var rv = PyTimeParsing.ParseDate(bytes, separator, out var year, out var month, out var day);
        if (rv == 0 && bytes.Length > separator)
        {
            var p = separator;
            var lead = bytes[p];
            p += (lead & 0x80) == 0 ? 1 : (lead & 0xf0) switch { 0xe0 => 3, 0xf0 => 4, _ => 2 };
            rv = PyTimeParsing.ParseTime(bytes, p, bytes.Length - p, out hour, out minute, out second,
                                         out microsecond, out tzSeconds, out tzMicros);
        }

        if (rv < 0)
        {
            throw invalid;
        }

        var offset = PyTimeParsing.Offset(rv, tzSeconds, tzMicros);
        return new PyDateTime(year, month, day, hour, minute, second, microsecond, offset);
    }

    /// <summary><c>datetime.combine(date, time, tzinfo)</c>; null keeps the time's own offset.</summary>
    public static PyDateTime Combine(PyDate date, PyTimeOfDay time, PyTimeDelta? utcOffset = null) =>
        new(date.Year, date.Month, date.Day, time.Hour, time.Minute, time.Second, time.Microsecond,
            utcOffset ?? time.UtcOffset);

    /// <summary><c>isoformat(sep, timespec)</c>: microseconds are omitted when zero under <c>auto</c>.</summary>
    public string Isoformat(char separator = 'T', string timespec = "auto")
    {
        var date = FromOrdinal(_wall / MicrosPerDay + 1);
        return string.Format(CultureInfo.InvariantCulture, "{0:0000}-{1:00}-{2:00}", date.Year, date.Month, date.Day)
               + separator + FormatTime(Hour, Minute, Second, Microsecond, timespec) + FormatOffset(UtcOffset);
    }

    /// <summary><c>isoformat(timespec=...)</c>.</summary>
    public string Isoformat(string timespec) => Isoformat('T', timespec);

    /// <summary><c>str(datetime)</c>: <c>isoformat(sep=' ')</c>.</summary>
    public override string ToString() => Isoformat(' ');

    /// <summary><c>replace(tzinfo=...)</c>.</summary>
    public PyDateTime WithOffset(PyTimeDelta? utcOffset)
    {
        if (utcOffset is { } offset)
        {
            CheckOffset(offset);
        }

        return new PyDateTime(_wall, utcOffset);
    }

    /// <summary><c>astimezone(timezone(offset))</c>; a naive value is taken as system local time.</summary>
    public PyDateTime AsTimezone(PyTimeDelta utcOffset)
    {
        CheckOffset(utcOffset);
        var source = UtcOffset ?? LocalOffset();
        if (UtcOffset is not null && source == utcOffset && utcOffset == PyTimeDelta.Zero)
        {
            // self.tzinfo is timezone.utc: Python returns self without arithmetic.
            return this;
        }

        var utc = Shift(_wall, -source);
        return new PyDateTime(Shift(utc, utcOffset), utcOffset);
    }

    /// <summary><c>astimezone(timezone.utc)</c>.</summary>
    public PyDateTime ToUtc() => AsTimezone(PyTimeDelta.Zero);

    /// <summary><c>timestamp()</c> for aware values.</summary>
    public double Timestamp()
    {
        var utc = Shift(_wall, -(UtcOffset ?? LocalOffset()));
        var epoch = (DaysBeforeYear(1970)) * MicrosPerDay;
        return (utc - epoch) / 1e6;
    }

    public static PyDateTime operator +(PyDateTime value, PyTimeDelta delta) => new(Shift(value._wall, delta), value.UtcOffset);

    public static PyDateTime operator -(PyDateTime value, PyTimeDelta delta) => new(Shift(value._wall, -delta), value.UtcOffset);

    public static PyTimeDelta operator -(PyDateTime left, PyDateTime right)
    {
        if (left.IsAware != right.IsAware)
        {
            throw new PyTypeError("can't subtract offset-naive and offset-aware datetimes");
        }

        return PyTimeDelta.FromMicroseconds((Int128)left.Instant() - right.Instant());
    }

    public static bool operator ==(PyDateTime left, PyDateTime right) => left.Equals(right);

    public static bool operator !=(PyDateTime left, PyDateTime right) => !left.Equals(right);

    public static bool operator <(PyDateTime left, PyDateTime right) => Compare(left, right) < 0;

    public static bool operator >(PyDateTime left, PyDateTime right) => Compare(left, right) > 0;

    public static bool operator <=(PyDateTime left, PyDateTime right) => Compare(left, right) <= 0;

    public static bool operator >=(PyDateTime left, PyDateTime right) => Compare(left, right) >= 0;

    /// <summary>Ordering comparison; naive versus aware raises <c>TypeError</c>.</summary>
    public static int Compare(PyDateTime left, PyDateTime right)
    {
        if (left.IsAware != right.IsAware)
        {
            throw new PyTypeError("can't compare offset-naive and offset-aware datetimes");
        }

        return left.Instant().CompareTo(right.Instant());
    }

    /// <summary>Python <c>==</c>: naive and aware values are simply unequal.</summary>
    public bool Equals(PyDateTime other) => IsAware == other.IsAware && Instant() == other.Instant();

    public override bool Equals(object? obj) => obj is PyDateTime other && Equals(other);

    public override int GetHashCode() => Instant().GetHashCode();

    internal static void CheckDate(int year, int month, int day)
    {
        if (year < 1 || year > 9999)
        {
            throw new PyValueError("year " + year.ToString(CultureInfo.InvariantCulture) + " is out of range");
        }

        if (month < 1 || month > 12)
        {
            throw new PyValueError("month must be in 1..12");
        }

        if (day < 1 || day > DaysInMonth(year, month))
        {
            throw new PyValueError("day is out of range for month");
        }
    }

    internal static void CheckTime(int hour, int minute, int second, int microsecond)
    {
        if (hour is < 0 or > 23)
        {
            throw new PyValueError("hour must be in 0..23");
        }

        if (minute is < 0 or > 59)
        {
            throw new PyValueError("minute must be in 0..59");
        }

        if (second is < 0 or > 59)
        {
            throw new PyValueError("second must be in 0..59");
        }

        if (microsecond is < 0 or > 999999)
        {
            throw new PyValueError("microsecond must be in 0..999999");
        }
    }

    internal static void CheckOffset(PyTimeDelta offset)
    {
        if ((offset.Days == -1 && offset.Seconds == 0 && offset.Microseconds < 1) || offset.Days < -1 || offset.Days >= 1)
        {
            throw new PyValueError("offset must be a timedelta strictly between -timedelta(hours=24) and timedelta(hours=24), not "
                                   + offset.Repr() + ".");
        }
    }

    internal static string FormatTime(int hour, int minute, int second, int microsecond, string timespec)
    {
        if (timespec == "auto")
        {
            timespec = microsecond != 0 ? "microseconds" : "seconds";
        }

        var c = CultureInfo.InvariantCulture;
        return timespec switch
        {
            "hours" => hour.ToString("00", c),
            "minutes" => string.Format(c, "{0:00}:{1:00}", hour, minute),
            "seconds" => string.Format(c, "{0:00}:{1:00}:{2:00}", hour, minute, second),
            "milliseconds" => string.Format(c, "{0:00}:{1:00}:{2:00}.{3:000}", hour, minute, second, microsecond / 1000),
            "microseconds" => string.Format(c, "{0:00}:{1:00}:{2:00}.{3:000000}", hour, minute, second, microsecond),
            _ => throw new PyValueError("Unknown timespec value"),
        };
    }

    internal static string FormatOffset(PyTimeDelta? utcOffset)
    {
        if (utcOffset is not { } offset)
        {
            return string.Empty;
        }

        var sign = '+';
        if (offset.Days < 0)
        {
            sign = '-';
            offset = -offset;
        }

        var seconds = offset.Seconds;
        var micros = offset.Microseconds;
        var builder = new StringBuilder();
        var c = CultureInfo.InvariantCulture;
        builder.Append(sign).Append((seconds / 3600).ToString("00", c)).Append(':').Append((seconds / 60 % 60).ToString("00", c));
        if (micros != 0 || seconds % 60 != 0)
        {
            builder.Append(':').Append((seconds % 60).ToString("00", c));
            if (micros != 0)
            {
                builder.Append('.').Append(micros.ToString("000000", c));
            }
        }

        return builder.ToString();
    }

    internal static bool IsLeap(int year) => year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);

    internal static int DaysInMonth(int year, int month) => month == 2 && IsLeap(year) ? 29 : DaysInMonthTable[month];

    internal static long DaysBeforeYear(int year)
    {
        long y = year - 1;
        return y * 365 + y / 4 - y / 100 + y / 400;
    }

    internal static long DaysBeforeMonth(int year, int month) => DaysBeforeMonthTable[month] + (month > 2 && IsLeap(year) ? 1 : 0);

    internal static (int Year, int Month, int Day) FromOrdinal(long ordinal)
    {
        var date = DateTime.MinValue.AddDays(ordinal - 1);
        return (date.Year, date.Month, date.Day);
    }

    internal static int Weekday(int year, int month, int day) =>
        (int)((DaysBeforeYear(year) + DaysBeforeMonth(year, month) + day + 6) % 7);

    private long Instant() => UtcOffset is { } offset ? _wall - (long)offset.TotalMicroseconds : _wall;

    private static long Shift(long wall, PyTimeDelta delta)
    {
        var result = (Int128)wall + delta.TotalMicroseconds;
        if (result < 0 || result >= (Int128)MaxOrdinal * MicrosPerDay)
        {
            throw new PyOverflowError("date value out of range");
        }

        return (long)result;
    }

    private PyTimeDelta LocalOffset()
    {
        var date = FromOrdinal(_wall / MicrosPerDay + 1);
        var local = new DateTime(date.Year, date.Month, date.Day, Hour, Minute, Second, DateTimeKind.Unspecified);
        return PyTimeDelta.FromSeconds((long)TimeZoneInfo.Local.GetUtcOffset(local).TotalSeconds);
    }
}

/// <summary>Port of the ISO 8601 parsers in CPython 3.13 <c>Modules/_datetimemodule.c</c>.</summary>
internal static class PyTimeParsing
{
    public static byte[]? Utf8OrNull(string text)
    {
        try
        {
            return new UTF8Encoding(false, true).GetBytes(text);
        }
        catch (EncoderFallbackException)
        {
            return null;
        }
    }

    /// <summary><c>_sanitize_isoformat_str</c>: a surrogate separator at code point 7, 8 or 10 becomes 'T'.</summary>
    public static string SanitizeSeparator(string text)
    {
        var points = PyRepr.CodePoints(text).ToList();
        foreach (var position in new[] { 7, 8, 10 })
        {
            if (position > points.Count)
            {
                break;
            }

            if (position < points.Count && points[position] is >= 0xD800 and <= 0xDFFF)
            {
                points[position] = 'T';
                var builder = new StringBuilder();
                foreach (var point in points)
                {
                    PyRepr.AppendCodePoint(builder, point);
                }

                return builder.ToString();
            }
        }

        return text;
    }

    public static int FindSeparator(byte[] s)
    {
        var len = s.Length;
        if (len == 7)
        {
            return 7;
        }

        if (At(s, 4) == '-')
        {
            if (At(s, 5) == 'W')
            {
                if (len < 8)
                {
                    return -1;
                }

                if (len > 8 && At(s, 8) == '-')
                {
                    if (len == 9)
                    {
                        return -1;
                    }

                    if (len > 10 && IsDigit(At(s, 10)))
                    {
                        return 8;
                    }

                    return 10;
                }

                return 8;
            }

            return 10;
        }

        if (At(s, 4) == 'W')
        {
            var idx = 7;
            for (; idx < len; ++idx)
            {
                if (!IsDigit(At(s, idx)))
                {
                    break;
                }
            }

            if (idx < 9)
            {
                return idx;
            }

            return idx % 2 == 0 ? 7 : 8;
        }

        return 8;
    }

    public static int ParseDate(byte[] s, int len, out int year, out int month, out int day)
    {
        year = month = day = 0;
        var p = 0;
        if (!ParseDigits(s, ref p, ref year, 4))
        {
            return -1;
        }

        var usesSeparator = At(s, p) == '-';
        if (usesSeparator)
        {
            p++;
        }

        if (At(s, p) == 'W')
        {
            p++;
            int week = 0, weekday = 0;
            if (!ParseDigits(s, ref p, ref week, 2))
            {
                return -3;
            }

            if (p < len)
            {
                if (usesSeparator && At(s, p++) != '-')
                {
                    return -2;
                }

                if (!ParseDigits(s, ref p, ref weekday, 1))
                {
                    return -4;
                }
            }
            else
            {
                weekday = 1;
            }

            var rv = IsoToYmd(year, week, weekday, out year, out month, out day);
            return rv != 0 ? -3 + rv : 0;
        }

        if (!ParseDigits(s, ref p, ref month, 2))
        {
            return -1;
        }

        if (usesSeparator && At(s, p++) != '-')
        {
            return -2;
        }

        return ParseDigits(s, ref p, ref day, 2) ? 0 : -1;
    }

    public static int ParseTime(byte[] s, int start, int length, out int hour, out int minute, out int second,
                                out int microsecond, out int tzSeconds, out int tzMicros)
    {
        tzSeconds = tzMicros = 0;
        var end = start + length;
        var tzPos = start;
        do
        {
            var c = At(s, tzPos);
            if (c is (byte)'Z' or (byte)'+' or (byte)'-')
            {
                break;
            }
        }
        while (++tzPos < end);

        var rv = ParseHhMmSsFf(s, start, tzPos, out hour, out minute, out second, out microsecond);
        if (rv < 0)
        {
            return rv;
        }

        if (tzPos == end)
        {
            return rv == 1 ? -5 : 0;
        }

        if (At(s, tzPos) == 'Z')
        {
            return At(s, tzPos + 1) != 0 ? -5 : 1;
        }

        var sign = At(s, tzPos) == '-' ? -1 : 1;
        tzPos++;
        rv = ParseHhMmSsFf(s, tzPos, end, out var tzHour, out var tzMinute, out var tzSecond, out tzMicros);
        tzSeconds = sign * ((tzHour * 3600) + (tzMinute * 60) + tzSecond);
        tzMicros *= sign;
        return rv != 0 ? -5 : 1;
    }

    public static PyTimeDelta? Offset(int rv, int tzSeconds, int tzMicros)
    {
        if (rv != 1)
        {
            return null;
        }

        if (tzSeconds == 0 && tzMicros == 0)
        {
            return PyTimeDelta.Zero;
        }

        var offset = PyTimeDelta.Create(seconds: tzSeconds, microseconds: tzMicros);
        PyDateTime.CheckOffset(offset);
        return offset;
    }

    private static int ParseHhMmSsFf(byte[] s, int p, int end, out int hour, out int minute, out int second, out int microsecond)
    {
        hour = minute = second = microsecond = 0;
        var values = new int[3];
        var hasSeparator = true;
        for (var i = 0; i < 3; ++i)
        {
            if (!ParseDigits(s, ref p, ref values[i], 2))
            {
                Assign(values, out hour, out minute, out second);
                return -3;
            }

            var c = At(s, p++);
            if (i == 0)
            {
                hasSeparator = c == ':';
            }

            if (c is (byte)'.' or (byte)',')
            {
                if (p >= end)
                {
                    Assign(values, out hour, out minute, out second);
                    return -3;
                }

                break;
            }

            if (p >= end)
            {
                Assign(values, out hour, out minute, out second);
                return c != 0 ? 1 : 0;
            }

            if (hasSeparator && c == ':')
            {
                continue;
            }

            if (!hasSeparator)
            {
                --p;
            }
            else
            {
                Assign(values, out hour, out minute, out second);
                return -4;
            }
        }

        Assign(values, out hour, out minute, out second);
        var remaining = end - p;
        var toParse = remaining >= 6 ? 6 : remaining;
        if (!ParseDigits(s, ref p, ref microsecond, toParse))
        {
            return -3;
        }

        int[] correction = [100000, 10000, 1000, 100, 10];
        if (toParse < 6)
        {
            microsecond *= correction[toParse - 1];
        }

        while (IsDigit(At(s, p)))
        {
            ++p;
        }

        return At(s, p) != 0 ? 1 : 0;
    }

    private static void Assign(int[] values, out int hour, out int minute, out int second)
    {
        hour = values[0];
        minute = values[1];
        second = values[2];
    }

    private static bool ParseDigits(byte[] s, ref int p, ref int value, int count)
    {
        for (var i = 0; i < count; ++i)
        {
            var digit = At(s, p++) - '0';
            if (digit is < 0 or > 9)
            {
                return false;
            }

            value = value * 10 + digit;
        }

        return true;
    }

    private static int IsoToYmd(int isoYear, int isoWeek, int isoDay, out int year, out int month, out int day)
    {
        year = isoYear;
        month = day = 0;
        if (isoYear is < 1 or > 9999)
        {
            return -4;
        }

        if (isoWeek is <= 0 or >= 53)
        {
            var outOfRange = true;
            if (isoWeek == 53)
            {
                var firstWeekday = PyDateTime.Weekday(isoYear, 1, 1);
                if (firstWeekday == 3 || (firstWeekday == 2 && PyDateTime.IsLeap(isoYear)))
                {
                    outOfRange = false;
                }
            }

            if (outOfRange)
            {
                return -2;
            }
        }

        if (isoDay is <= 0 or >= 8)
        {
            return -3;
        }

        // iso_week1_monday
        var firstDay = PyDateTime.DaysBeforeYear(isoYear) + 1;
        var firstWeekdayOfYear = (int)((firstDay + 6) % 7);
        var week1Monday = firstDay - firstWeekdayOfYear;
        if (firstWeekdayOfYear > 3)
        {
            week1Monday += 7;
        }

        var ordinal = week1Monday + ((isoWeek - 1) * 7) + isoDay - 1;
        if (ordinal < 1 || ordinal > PyDateTime.MaxOrdinal)
        {
            // ord_to_ymd would produce an out-of-range year; the constructor reports it.
            year = ordinal < 1 ? 0 : 10000;
            month = day = 1;
            return 0;
        }

        (year, month, day) = PyDateTime.FromOrdinal(ordinal);
        return 0;
    }

    private static bool IsDigit(int c) => c is >= '0' and <= '9';

    private static byte At(byte[] s, int index) => index >= 0 && index < s.Length ? s[index] : (byte)0;
}

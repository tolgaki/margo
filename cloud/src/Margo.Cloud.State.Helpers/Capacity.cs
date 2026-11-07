using System.Numerics;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Helpers.Python;

namespace Margo.Cloud.State.Helpers;

/// <summary>An interval of two aware UTC datetimes, <c>(start, end)</c> in capacity.py.</summary>
public readonly record struct CapacityInterval(PyDateTime Start, PyDateTime End);

/// <summary>
/// Port of <c>skills/chief-of-staff/scripts/capacity.py</c>: capacity from explicit, timezone-aware
/// intervals. Pure computation over the input; it never reads or changes a calendar or Margo's state.
/// Numbers follow Python: minutes are floats (<c>timedelta.total_seconds()</c> is an exactly rounded
/// int division, sums use <c>sum()</c>'s compensated summation), effort is an int while every estimate is
/// an int, and int/float comparisons are exact.
/// </summary>
public static class Capacity
{
    /// <summary>The module docstring (argparse description).</summary>
    public const string Doc = "Compute capacity from explicit, timezone-aware intervals; never change a calendar.";

    /// <summary><c>instant(value)</c>: an ISO-8601 string with an explicit offset, as an aware UTC datetime.</summary>
    public static PyDateTime Instant(object? value)
    {
        if (PyObj.Normalize(value) is not string text)
        {
            throw new PyValueError("interval endpoints must be ISO-8601 strings");
        }

        var result = PyDateTime.FromIsoformat(text.Replace("Z", "+00:00", StringComparison.Ordinal));
        if (!result.IsAware)
        {
            throw new PyValueError("interval endpoints require an explicit UTC offset");
        }

        // OverflowError when the UTC instant leaves years 1..9999 (not caught by the CLI, as in Python).
        return result.ToUtc();
    }

    /// <summary><c>intervals(values, label)</c>: validated and merged.</summary>
    public static List<CapacityInterval> Intervals(object? values, string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        if (PyObj.Kind(values) != PyKind.List)
        {
            throw new PyValueError(label + " must be an array of start/end objects");
        }

        var result = new List<CapacityInterval>();
        foreach (var value in PyObj.Items(values))
        {
            if (value is not JsonObject dict || !dict.ContainsKey("start") || !dict.ContainsKey("end"))
            {
                throw new PyValueError(label + " requires start and end on every interval");
            }

            var start = Instant(dict["start"]);
            var end = Instant(dict["end"]);
            if (end <= start)
            {
                throw new PyValueError(label + " interval end must be after start");
            }

            result.Add(new CapacityInterval(start, end));
        }

        return Merge(result);
    }

    /// <summary><c>merge(values)</c>: sorted, with overlapping and touching intervals joined.</summary>
    public static List<CapacityInterval> Merge(IEnumerable<CapacityInterval> values)
    {
        var sorted = values.ToArray();
        PySort.Sort(sorted, (a, b) => a.Start != b.Start ? a.Start < b.Start : a.End < b.End);
        var result = new List<CapacityInterval>();
        foreach (var (start, end) in sorted)
        {
            if (result.Count > 0 && start <= result[^1].End)
            {
                var last = result[^1];
                result[^1] = new CapacityInterval(last.Start, last.End > end ? last.End : end);
            }
            else
            {
                result.Add(new CapacityInterval(start, end));
            }
        }

        return result;
    }

    /// <summary><c>intersection(left, right)</c> of two merged lists.</summary>
    public static List<CapacityInterval> Intersection(IReadOnlyList<CapacityInterval> left, IReadOnlyList<CapacityInterval> right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var result = new List<CapacityInterval>();
        int i = 0, j = 0;
        while (i < left.Count && j < right.Count)
        {
            var start = left[i].Start > right[j].Start ? left[i].Start : right[j].Start;
            var end = left[i].End < right[j].End ? left[i].End : right[j].End;
            if (start < end)
            {
                result.Add(new CapacityInterval(start, end));
            }

            if (left[i].End <= right[j].End)
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        return Merge(result);
    }

    /// <summary><c>subtract(available, unavailable)</c>.</summary>
    public static List<CapacityInterval> Subtract(IReadOnlyList<CapacityInterval> available, IReadOnlyList<CapacityInterval> unavailable)
    {
        ArgumentNullException.ThrowIfNull(available);
        ArgumentNullException.ThrowIfNull(unavailable);
        var result = new List<CapacityInterval>();
        foreach (var (start, end) in available)
        {
            var cursor = start;
            foreach (var (busyStart, busyEnd) in unavailable)
            {
                if (busyEnd <= cursor)
                {
                    continue;
                }

                if (busyStart >= end)
                {
                    break;
                }

                if (busyStart > cursor)
                {
                    result.Add(new CapacityInterval(cursor, busyStart));
                }

                cursor = cursor > busyEnd ? cursor : busyEnd;
                if (cursor >= end)
                {
                    break;
                }
            }

            if (cursor < end)
            {
                result.Add(new CapacityInterval(cursor, end));
            }
        }

        return result;
    }

    /// <summary><c>(end - start).total_seconds()</c>: microseconds / 10**6, correctly rounded.</summary>
    public static double TotalSeconds(CapacityInterval interval) =>
        PyNumbers.TrueDivide((BigInteger)(interval.End - interval.Start).TotalMicroseconds, 1_000_000);

    /// <summary><c>minutes(values)</c>: <c>sum(total_seconds) / 60</c>.</summary>
    public static double Minutes(IEnumerable<CapacityInterval> values) =>
        PyNumbers.Divide(PyNumbers.Sum(values.Select(TotalSeconds)), 60);

    /// <summary><c>number(value, label, positive)</c>: a finite, nonnegative (or positive) int or float.</summary>
    public static object Number(object? value, string label, bool positive = false)
    {
        var number = PyObj.Normalize(value);
        if (number is not (BigInteger or double))
        {
            throw new PyValueError(label + " must be a finite number");
        }

        // math.isfinite(int) converts to float first: OverflowError for an int beyond the float range.
        var asDouble = number is BigInteger integer ? PyNumbers.IntToDouble(integer) : (double)number;
        if (!double.IsFinite(asDouble))
        {
            throw new PyValueError(label + " must be a finite number");
        }

        var sign = PyNumbers.Compare(number, BigInteger.Zero)!.Value;
        if (sign < 0 || (positive && sign == 0))
        {
            throw new PyValueError(label + " must be " + (positive ? "positive" : "nonnegative"));
        }

        return number;
    }

    /// <summary><c>serialise(values)</c>.</summary>
    public static JsonArray Serialise(IEnumerable<CapacityInterval> values)
    {
        var result = new JsonArray();
        foreach (var interval in values)
        {
            result.Add(new JsonObject
            {
                ["start"] = interval.Start.Isoformat(),
                ["end"] = interval.End.Isoformat(),
                ["minutes"] = PyJson.Float(TotalSeconds(interval) / 60),
            });
        }

        return result;
    }

    /// <summary><c>calculate(data)</c>: the capacity report, or <see cref="PyValueError"/> for invalid input.</summary>
    public static JsonObject Calculate(JsonNode? data)
    {
        if (data is not JsonObject input)
        {
            throw new PyValueError("input must be an object");
        }

        var coverage = PyObj.Normalize(PyObj.Get(input, "coverage"));
        if (coverage is not ("complete" or "partial" or "unknown"))
        {
            throw new PyValueError("coverage must explicitly be complete, partial, or unknown");
        }

        if (!input.ContainsKey("working") || !input.ContainsKey("busy"))
        {
            throw new PyValueError("working and busy intervals are required, even when empty");
        }

        var working = Intervals(input["working"], "working");
        var busy = Intersection(working, Intervals(input["busy"], "busy"));
        var free = Subtract(working, busy);
        var protectedIntervals = Intersection(working, Intervals(PyObj.Get(input, "protected", new List<object?>()), "protected"));
        var protectedFree = Intersection(free, protectedIntervals);
        var focusMinimum = Number(PyObj.Get(input, "minimum_focus_minutes", new BigInteger(90)), "minimum_focus_minutes", positive: true);
        var focus = free.Where(value => PyNumbers.Compare(Minutes([value]), focusMinimum) is >= 0).ToList();
        var estimates = PyObj.Get(input, "estimates", new List<object?>());
        if (PyObj.Kind(estimates) != PyKind.List)
        {
            throw new PyValueError("estimates must be an array");
        }

        object known = BigInteger.Zero;
        var unknown = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var estimate in PyObj.Items(estimates))
        {
            if (estimate is not JsonObject item || PyObj.Normalize(PyObj.Get(item, "id")) is not string id || id.Length == 0)
            {
                throw new PyValueError("each estimate requires a nonempty id");
            }

            if (!seen.Add(id))
            {
                throw new PyValueError("duplicate effort estimate: " + id);
            }

            if (PyObj.Normalize(PyObj.Get(item, "minutes")) is null)
            {
                unknown.Add(id);
            }
            else
            {
                known = Add(known, Number(item["minutes"], "effort minutes"));
            }
        }

        var freeMinutes = Minutes(free);
        var warnings = new JsonArray();
        if (!coverage.Equals("complete"))
        {
            warnings.Add("Calendar coverage is incomplete; free time is not established availability.");
        }

        if (unknown.Count > 0)
        {
            warnings.Add("Some work has no confirmed effort estimate; feasibility is unknown.");
        }

        bool? feasible = !coverage.Equals("complete") || unknown.Count > 0
            ? null
            : PyNumbers.Compare(known, freeMinutes) is <= 0;
        var workingMinutes = Minutes(working);
        var busyMinutes = Minutes(busy);
        var protectedAvailable = Minutes(protectedFree);
        var protectedConflict = Minutes(protectedIntervals) - Minutes(protectedFree);
        var focusBlocks = Serialise(focus);
        var availableBlocks = Serialise(free);
        var overflow = Subtract(known, freeMinutes);
        var unestimated = new JsonArray();
        foreach (var id in unknown)
        {
            unestimated.Add(id);
        }

        return new JsonObject
        {
            ["coverage"] = (string)coverage,
            ["working_minutes"] = PyJson.Float(workingMinutes),
            ["busy_minutes"] = PyJson.Float(busyMinutes),
            ["available_minutes"] = PyJson.Float(freeMinutes),
            ["protected_available_minutes"] = PyJson.Float(protectedAvailable),
            ["protected_conflict_minutes"] = PyJson.Float(protectedConflict),
            ["focus_blocks"] = focusBlocks,
            ["available_blocks"] = availableBlocks,
            ["known_effort_minutes"] = ToNode(known),
            ["unestimated_items"] = unestimated,
            // max(0, known - free_minutes): the int 0 unless the difference is greater than zero.
            ["minimum_overflow_minutes"] = overflow > 0 ? PyJson.Float(overflow) : PyJson.Int(BigInteger.Zero),
            ["fits_total_capacity"] = feasible is { } fits ? JsonValue.Create(fits) : null,
            ["allocation_status"] = "not_allocated",
            ["warnings"] = warnings,
        };
    }

    /// <summary><c>known + value</c> for Python int/float operands.</summary>
    private static object Add(object left, object right)
    {
        if (left is BigInteger a && right is BigInteger b)
        {
            return a + b;
        }

        return AsDouble(left) + AsDouble(right);
    }

    /// <summary><c>known - free</c> where <paramref name="right"/> is a float: always a float.</summary>
    private static double Subtract(object left, double right) => AsDouble(left) - right;

    private static double AsDouble(object value) => value is BigInteger integer ? PyNumbers.IntToDouble(integer) : (double)value;

    private static JsonValue ToNode(object number) =>
        number is BigInteger integer ? PyJson.Int(integer) : PyJson.Float((double)number);
}

/// <summary>
/// <c>python3 capacity.py --input FILE|-</c>: prints the capacity report as indented JSON, or
/// <c>ERROR: ...</c> on stderr with exit status 2 for unreadable or invalid input.
/// </summary>
public sealed class CapacityScript : IStateScript
{
    public string ScriptName => "capacity.py";

    public int Run(IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr) =>
        StateScripts.RunGuarded(() => Main(args, stdin, stdout, stderr), stderr);

    private int Main(IReadOnlyList<string> argv, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var parser = new PyArgumentParser(ScriptName, Capacity.Doc);
        parser.AddArgument("--input", required: true, help: "JSON file, or - for stdin");
        var args = parser.ParseArgs(argv, stdout, stderr);
        try
        {
            var input = args.GetString("input")!;
            var text = input == "-" ? PyProcess.ReadStdin(stdin) : PyIO.ReadTextFile(PyPath.Normalize(input));
            PyIO.Print(stdout, PyJson.Dumps(Capacity.Calculate(PyJson.Loads(text)), indent: 2, allowNan: false));
        }
        catch (Exception ex) when (ex is PyValueError or PyOSError)
        {
            StateScripts.PrintError(stderr, "ERROR: " + ex.Message);
            return 2;
        }

        return 0;
    }
}

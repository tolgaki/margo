using Margo.Cloud.State.Core;
using Margo.Cloud.State.Helpers;
using Margo.Cloud.State.Helpers.Python;
using Xunit;

namespace Margo.Cloud.State.Tests.Helpers;

/// <summary>
/// CPython 3.13's JSON decoder where the C recursion budget runs out (found by the state-helpers review, measured
/// with the scripts themselves; the parity scenario <c>helpers/json_recursion_budget.json</c> judges the same rules at
/// full depth). With B the deepest container a call site accepts and n the level of the innermost open container:
/// containers open while n &lt;= B; <c>NaN</c>/<c>Infinity</c> need n &lt; B; a decode error built in C
/// (<c>raise_errmsg</c>) is a <c>JSONDecodeError</c> only while n &lt;= B - 4, otherwise a <c>RecursionError</c>;
/// "Expecting value" (a <c>StopIteration</c>) and the 4300-digit <c>ValueError</c> fail to be created at n = B, where
/// CPython raises <c>RecursionError</c> with the note "Normalization failed: type=... args=&lt;unknown&gt;".
/// </summary>
public sealed class JsonRecursionBudgetTests : IDisposable
{
    private static readonly string LongDigits = new('1', 4400);

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("margo-json-budget-test-");

    public void Dispose() => _root.Delete(recursive: true);

    private static string Nest(int depth, string inner) => new string('[', depth) + inner + new string(']', depth);

    [Fact]
    public void Float_fraction_and_exponent_digits_are_not_an_over_long_int()
    {
        // The deep list (levels 2..6) comes before the over-long int, so CPython stops at the too-deep container.
        foreach (var number in new[] { "1e" + new string('0', 4400), "1E+" + new string('0', 4400), "1e-" + new string('0', 4400), "0." + new string('0', 4400) + "1" })
        {
            var text = "[" + number + ", " + Nest(5, "") + ", " + LongDigits + "]";
            Assert.Throws<PyRecursionError>(() => PyJsonLoad.Loads(text, 5));
        }

        // Without the deep list the over-long int is the first failure.
        Assert.Throws<PyValueError>(() => PyJsonLoad.Loads("[1e" + new string('0', 4400) + ", " + LongDigits + "]", 5));
    }

    [Fact]
    public void Decode_errors_built_in_c_need_four_levels_of_budget()
    {
        // B = 8: an error raised inside container level 4 (= B - 4) is a JSONDecodeError, at level 5 a RecursionError.
        foreach (var error in new[] { "1 2", "1,", "\"\\q\"", "\"abc", "\"a\\u12\"", "{\"a\" 1}" })
        {
            var level = error.StartsWith('{') ? 3 : 4; // an object adds its own level
            Assert.Throws<PyJsonDecodeError>(() => PyJsonLoad.Loads(Nest(level, error), 8));
            var recursion = Assert.Throws<PyRecursionError>(() => PyJsonLoad.Loads(Nest(level + 1, error), 8));
            Assert.Equal("maximum recursion depth exceeded while calling a Python object", recursion.Message);
        }
    }

    [Fact]
    public void Constants_need_one_level_of_budget()
    {
        foreach (var constant in new[] { "NaN", "Infinity", "-Infinity" })
        {
            Assert.NotNull(PyJsonLoad.Loads(Nest(4, constant), 5));
            var recursion = Assert.Throws<PyRecursionError>(() => PyJsonLoad.Loads(Nest(5, constant), 5));
            Assert.Equal("maximum recursion depth exceeded while calling a Python object", recursion.Message);
        }

        // Inside strings they are text, not constants.
        Assert.NotNull(PyJsonLoad.Loads(Nest(5, "\"NaN Infinity\""), 5));
    }

    [Fact]
    public void Expecting_value_and_long_ints_fail_to_normalize_at_the_limit()
    {
        Assert.Throws<PyJsonDecodeError>(() => PyJsonLoad.Loads(Nest(4, ","), 5));
        var stop = Assert.Throws<PyRecursionError>(() => PyJsonLoad.Loads(Nest(5, ","), 5));
        Assert.Equal("maximum recursion depth exceeded while calling a Python object\nNormalization failed: type=StopIteration args=<unknown>", stop.Message);
        Assert.Throws<PyRecursionError>(() => PyJsonLoad.Loads(Nest(4, "{\"a\": }"), 5));
        Assert.Throws<PyJsonDecodeError>(() => PyJsonLoad.Loads(Nest(3, "{\"a\": }"), 5));

        var value = Assert.Throws<PyValueError>(() => PyJsonLoad.Loads(Nest(4, LongDigits), 5));
        Assert.StartsWith("Exceeds the limit (4300 digits)", value.Message, StringComparison.Ordinal);
        var normalization = Assert.Throws<PyRecursionError>(() => PyJsonLoad.Loads(Nest(5, LongDigits), 5));
        Assert.EndsWith("\nNormalization failed: type=ValueError args=<unknown>", normalization.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Errors_outside_containers_are_unaffected()
    {
        Assert.Throws<PyJsonDecodeError>(() => PyJsonLoad.Loads("[1] x", 1));
        Assert.Throws<PyJsonDecodeError>(() => PyJsonLoad.Loads("\uFEFF[1]", 1));
        Assert.Throws<PyJsonDecodeError>(() => PyJsonLoad.Loads("", 1));
        // An error after a too-deep container is never reached.
        Assert.Throws<PyRecursionError>(() => PyJsonLoad.Loads("[" + Nest(5, "") + "] x", 5));
    }

    [Fact]
    public void Capacity_and_engage_parse_use_the_budget_of_a_call_from_main()
    {
        // capacity.py: a decode error built in C at level 9995 (> 9998 - 4) is an uncaught RecursionError.
        var text = "{\"x\": " + Nest(9994, "1 2") + "}";
        var (status, _, stderr) = Run(new CapacityScript(), ["--input", "-"], text);
        Assert.Equal(1, status);
        Assert.EndsWith("RecursionError: maximum recursion depth exceeded while calling a Python object\n", stderr, StringComparison.Ordinal);
        (status, _, stderr) = Run(new CapacityScript(), ["--input", "-"], "{\"x\": " + Nest(9993, "1 2") + "}");
        Assert.Equal(2, status);
        Assert.StartsWith("ERROR: Expecting ',' delimiter", stderr, StringComparison.Ordinal);

        // engage_parse.py: the same blob is no longer salvaged; CPython never reaches the except clause.
        var blob = "**sourceJson:** {\"ScopeType\": \"YAMMER_GROUP\", \"x\": " + Nest(9994, "\"\\q\"") + "}\n";
        var path = Path.Combine(_root.FullName, "deep.md");
        File.WriteAllText(path, blob);
        (status, _, stderr) = Run(new EngageParseScript(), [path, "--format", "json"], "");
        Assert.Equal(1, status);
        Assert.EndsWith("RecursionError: maximum recursion depth exceeded while calling a Python object\n", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Teams_feedback_reports_cpythons_last_line_for_a_failed_normalization()
    {
        var path = Path.Combine(_root.FullName, "deep.json");
        File.WriteAllText(path, "{\"results\": [" + Nest(9994, ",") + "]}");
        var (status, stdout, stderr) = Run(new TeamsFeedbackScript(), [path, "--format", "json"], "");
        Assert.Equal(1, status);
        Assert.Equal("", stdout);
        Assert.EndsWith("RecursionError: maximum recursion depth exceeded while calling a Python object\nNormalization failed: type=StopIteration args=<unknown>\n", stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("41 42", 0, "AB", "")]
    [InlineData("\n41\u000b42\n", 0, "AB", "")]
    [InlineData("4 1", 1, null, "ValueError: non-hexadecimal number found in fromhex() arg at position 1")]
    [InlineData("414", 1, null, "ValueError: non-hexadecimal number found in fromhex() arg at position 3")]
    [InlineData("41\u300042", 1, null, "ValueError: non-hexadecimal number found in fromhex() arg at position 2")]
    [InlineData("zz\u3000", 1, null, "ValueError: non-hexadecimal number found in fromhex() arg at position 2")]
    [InlineData("41\u00a042", 1, null, "ValueError: non-hexadecimal number found in fromhex() arg at position 2")]
    [InlineData("41\u001c42", 1, null, "ValueError: non-hexadecimal number found in fromhex() arg at position 2")]
    [InlineData("4g", 1, null, "ValueError: non-hexadecimal number found in fromhex() arg at position 1")]
    public void Probe_write_hex_is_bytes_fromhex(string input, int expectedStatus, string? expectedContent, string expectedError)
    {
        var path = Path.Combine(_root.FullName, "hex.bin");
        var (status, _, stderr) = Run(new HelpersProbeScript(), ["write-hex", path], input);
        Assert.Equal(expectedStatus, status);
        Assert.Equal(expectedContent, File.Exists(path) ? File.ReadAllText(path) : null);
        Assert.Equal(expectedError, stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "");
    }

    [Fact]
    public void Probe_write_fails_like_pathlib()
    {
        var (status, _, stderr) = Run(new HelpersProbeScript(), ["write", _root.FullName], "x");
        Assert.Equal(1, status);
        Assert.EndsWith("IsADirectoryError: [Errno 21] Is a directory: '" + _root.FullName + "'\n", stderr, StringComparison.Ordinal);
        var missing = Path.Combine(_root.FullName, "missing", "x");
        (status, _, stderr) = Run(new HelpersProbeScript(), ["write", missing], "x");
        Assert.Equal(1, status);
        Assert.EndsWith("FileNotFoundError: [Errno 2] No such file or directory: '" + missing + "'\n", stderr, StringComparison.Ordinal);
    }

    private static (int Status, string Stdout, string Stderr) Run(IStateScript script, string[] args, string stdin)
    {
        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };
        var status = script.Run(args, new StringReader(stdin), stdout, stderr);
        return (status, stdout.ToString(), stderr.ToString());
    }
}

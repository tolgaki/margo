using System.Numerics;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Helpers.Python;
using Xunit;

namespace Margo.Cloud.State.Tests.Helpers;

/// <summary>
/// The Python semantics the helper ports reproduce, with expected values taken from CPython 3.13. The
/// helpers_probe parity scenarios (cloud/contracts/state/helpers/probe_*.json) judge the same code against
/// CPython on many more inputs.
/// </summary>
public sealed class PythonSemanticsTests
{
    [Fact]
    public void Sort_makes_cpythons_comparisons()
    {
        // sorted() of [3, 2, 3, 4, 1]: count_run finds the descending pair, reverses it, extends the run.
        var keys = new[] { 3, 2, 3, 4, 1 };
        var trace = new List<(int, int)>();
        var order = Enumerable.Range(0, keys.Length).ToArray();
        PySort.Sort(order, (a, b) =>
        {
            trace.Add((a, b));
            return keys[a] < keys[b];
        });
        Assert.Equal([4, 1, 0, 2, 3], order);
        Assert.Equal([(1, 0), (2, 1), (1, 2), (2, 0), (3, 2), (4, 3), (4, 2), (4, 0), (4, 1)], trace);
    }

    [Fact]
    public void Sort_is_stable_and_agrees_with_a_stable_sort_for_consistent_orders()
    {
        var random = new Random(1234);
        foreach (var size in new[] { 0, 1, 2, 63, 64, 65, 200, 1000, 5000 })
        {
            var items = Enumerable.Range(0, size).Select(i => (Key: random.Next(size / 3 + 1), Index: i)).ToArray();
            var expected = items.OrderBy(item => item.Key).ToArray(); // LINQ OrderBy is stable
            PySort.Sort(items, (a, b) => a.Key < b.Key);
            Assert.Equal(expected, items);
        }
    }

    [Fact]
    public void Sort_raises_the_comparison_error_and_keeps_a_permutation()
    {
        var keys = new object?[] { 1L, 2L, 3L, "a", 0L };
        var items = (object?[])keys.Clone();
        Assert.Throws<PyTypeError>(() => PySort.Sort(items, PyObj.Lt));
        Assert.Equal(keys.Select(PyObj.Repr).Order(), items.Select(PyObj.Repr).Order());
    }

    [Fact]
    public void Equality_identity_and_hash_keys_follow_python()
    {
        var nan = PyJson.Loads("[NaN, NaN]")!.AsArray();
        Assert.False(PyObj.Eq(nan[0], nan[1]));      // nan == nan
        Assert.True(PyObj.EqBool(nan[0], nan[1]));   // the json module's NaN is one object
        Assert.True(PyObj.Eq(PyJson.Loads("[NaN]"), PyJson.Loads("[NaN]")));
        Assert.Equal(PyKey.Of(true), PyKey.Of(1.0));
        Assert.Equal(PyKey.Of(0L), PyKey.Of(-0.0));
        Assert.NotEqual(PyKey.Of("1"), PyKey.Of(1L));
        Assert.Throws<PyTypeError>(() => PyKey.Of(new JsonArray()));
        Assert.True(PyObj.Lt(BigInteger.Pow(2, 53) + 1, 9007199254740994.0));
        Assert.False(PyObj.Lt(BigInteger.Pow(2, 53) + 1, 9007199254740992.0)); // exact, not via float(int)
        Assert.True(PyObj.Lt(PyJson.Loads("[1, [2]]"), PyJson.Loads("[1, [2, 3]]")));
        Assert.Throws<PyTypeError>(() => PyObj.Lt(PyJson.Loads("[1, \"a\"]"), PyJson.Loads("[1, 2]")));
        Assert.Throws<PyTypeError>(() => PyObj.Lt(null, null));
    }

    [Fact]
    public void Dict_keeps_the_first_key_and_position()
    {
        var dict = new PyDict<string>();
        dict.Set(1L, "int");
        dict.Set("x", "str");
        dict.Set(1.0, "float");      // same key: value replaced, key object and position kept
        dict.Remove("x");
        dict.Set("x", "again");      // re-added at the end
        Assert.Equal("1:float x:again", string.Join(" ", dict.Items().Select(pair => PyObj.Repr(pair.Key).Trim('\'') + ":" + pair.Value)));
    }

    [Fact]
    public void Slicing_len_and_iteration_raise_pythons_errors()
    {
        Assert.Equal("\U0001F600b", PyObj.Slice("\U0001F600bc", 2));
        Assert.Equal(2, ((List<object?>)PyObj.Slice(PyJson.Loads("[1, 2, 3]"), 2)!).Count);
        Assert.Equal("slice(None, 10, None)", Assert.Throws<PyKeyError>(() => PyObj.Slice(new JsonObject(), 10)).Message);
        Assert.Throws<PyTypeError>(() => PyObj.Slice(5L, 10));
        Assert.Equal(1, PyObj.Len("\U0001F600"));
        Assert.Throws<PyTypeError>(() => PyObj.Len(3L));
        Assert.Equal(["a", "b"], PyObj.Iterate(PyJson.Loads("{\"a\": 1, \"b\": 2}")).Cast<string>());
        Assert.Throws<PyAttributeError>(() => PyObj.Get(new JsonArray(), "x"));
        Assert.Throws<PyTypeError>(() => PyObj.Contains(5L, "value"));
        Assert.True(PyObj.Contains("a value b", "value"));
        Assert.Equal("[1, 'a', None, True, 1e+16, {'k': [2.5]}]", PyObj.Repr(PyJson.Loads("[1, \"a\", null, true, 1e16, {\"k\": [2.5]}]")));
        Assert.Equal(new BigInteger(42), PyObj.Int(" \u0664\u0662 "));
        Assert.Throws<PyValueError>(() => PyObj.Int(double.NaN));
        Assert.Throws<PyOverflowError>(() => PyObj.Int(double.PositiveInfinity));
    }

    [Theory]
    [InlineData("9007199254740993", "9007199254740992.0")]
    [InlineData("9007199254740995", "9007199254740996.0")]
    [InlineData("18446744073709553664", "1.8446744073709552e+19")]
    [InlineData("18446744073709553665", "1.8446744073709556e+19")]
    public void Int_to_float_rounds_half_to_even(string integer, string expected) =>
        Assert.Equal(expected, PyJson.FloatRepr(PyNumbers.IntToDouble(BigInteger.Parse(integer))));

    [Fact]
    public void Int_to_float_overflows_like_python()
    {
        var max = (BigInteger.Pow(2, 53) - 1) * BigInteger.Pow(2, 971);
        Assert.Equal(double.MaxValue, PyNumbers.IntToDouble(max + BigInteger.Pow(2, 969)));
        Assert.Equal("int too large to convert to float",
                     Assert.Throws<PyOverflowError>(() => PyNumbers.IntToDouble(max + BigInteger.Pow(2, 970))).Message);
    }

    [Theory]
    [InlineData("147573952589676412927", "1000000", "147573952589676.4")]
    [InlineData("1000000000000000001", "1000000", "1000000000000.0")]
    [InlineData("1", "3", "0.3333333333333333")]
    [InlineData("-147573952589676412927", "1000000", "-147573952589676.4")]
    public void Int_division_is_correctly_rounded(string numerator, string denominator, string expected) =>
        Assert.Equal(expected, PyJson.FloatRepr(PyNumbers.TrueDivide(BigInteger.Parse(numerator), BigInteger.Parse(denominator))));

    [Fact]
    public void Int_division_overflow_and_zero()
    {
        Assert.Throws<PyOverflowError>(() => PyNumbers.TrueDivide(BigInteger.Pow(10, 400), 3));
        Assert.Throws<PyZeroDivisionError>(() => PyNumbers.TrueDivide(1, 0));
        Assert.Equal("-0.0", PyJson.FloatRepr(PyNumbers.TrueDivide(0, -5)));
    }

    [Fact]
    public void Sum_uses_compensated_float_summation()
    {
        Assert.Equal(BigInteger.Zero, PyNumbers.Sum([]));
        Assert.Equal(1.0, PyNumbers.Sum(Enumerable.Repeat(0.1, 10)));
        Assert.Equal(1.0, PyNumbers.Sum([1e100, 1.0, -1e100]));
        Assert.Equal("2.7755575615628914e-17", PyJson.FloatRepr((double)PyNumbers.Sum([0.1, 0.2, 0.3, -0.6])));
        Assert.Equal("0.0", PyJson.FloatRepr((double)PyNumbers.Sum([-0.0])));
        Assert.Equal(double.PositiveInfinity, PyNumbers.Sum([1e308, 1e308, -1e308]));
    }

    [Theory]
    [InlineData("&amp;", "&")]
    [InlineData("&ampx", "&x")]
    [InlineData("&notit;", "\u00ACit;")]
    [InlineData("&notin", "\u00ACin")]
    [InlineData("&#x80;", "\u20AC")]
    [InlineData("&#0;", "\uFFFD")]
    [InlineData("&#xD800;", "\uFFFD")]
    [InlineData("&#1114112;", "\uFFFD")]
    [InlineData("&#xFDD0;", "")]
    [InlineData("&#13;", "\u000D")]
    [InlineData("&abcdefghijklmnopqrstuvwxyzabcdefg;", "&abcdefghijklmnopqrstuvwxyzabcdefg;")]
    [InlineData("AT&T", "AT&T")]
    [InlineData("&acE;", "\u223E\u0333")]
    [InlineData("&\uD83D\uDE00amp;", "&\uD83D\uDE00amp;")]
    public void Html_unescape_matches_cpython(string input, string expected) => Assert.Equal(expected, PyHtml.Unescape(input));

    [Fact]
    public void Html_unescape_keeps_the_int_digit_limit() =>
        Assert.StartsWith("Exceeds the limit (4300 digits)",
                          Assert.ThrowsAny<PyValueError>(() => PyHtml.Unescape("&#" + new string('1', 4301) + ";")).Message, StringComparison.Ordinal);

    [Theory]
    [InlineData("%E2%80", "\uFFFD")]
    [InlineData("%C3%28", "\uFFFD(")]
    [InlineData("%ED%A0%80", "\uFFFD\uFFFD\uFFFD")]
    [InlineData("%F0%9F%98", "\uFFFD")]
    [InlineData("%C3\u00E9%A9", "\uFFFD\u00E9\uFFFD")]
    [InlineData("%%41", "%A")]
    [InlineData("%4%41", "%4A")]
    [InlineData("\uD83D\uDE00%F0%9F", "\uD83D\uDE00\uFFFD")]
    [InlineData("%G1%41", "%G1A")]
    [InlineData("%E0%80%80", "\uFFFD\uFFFD\uFFFD")]
    [InlineData("%F4%90%80%80", "\uFFFD\uFFFD\uFFFD\uFFFD")]
    public void Unquote_matches_cpython(string input, string expected) => Assert.Equal(expected, PyUrl.Unquote(input));

    [Fact]
    public void Word_and_space_classes_use_cpythons_unicode_tables()
    {
        Assert.True(PyText.IsWord('\u00B2'));     // superscript two: isalnum() in Python, not .NET's \w
        Assert.True(PyText.IsWord('\u00BD'));     // vulgar fraction one half (numeric)
        Assert.False(PyText.IsWord('\u0301'));    // combining acute: .NET's \w includes Mn
        Assert.False(PyText.IsWord('\u203F'));    // undertie: .NET's \w includes Pc
        Assert.False(PyText.IsWord(0x10D40));     // Garay digit zero: Unicode 16, unassigned in 15.1
        Assert.True(PyText.IsWord(0x1D7CE));      // mathematical bold digit zero (astral)
        Assert.True(PyText.IsDecimal(0x1D7CE));
        Assert.True(PyText.IsSpace('\u001C'));      // .NET's \s does not include the information separators
        Assert.False(PyText.IsSpace('\u200B'));
        Assert.Equal(" a b ", PyText.CollapseWhitespace("\u3000a\u001C\u0085b\u2029"));
    }

    [Fact]
    public void Json_dump_matches_dumps_and_stops_at_the_recursion_limit()
    {
        var value = PyJson.Loads("{\"a\": [1, 1.5, -0.0, 1e16, NaN, Infinity, \"\\u00e9\\ud800\", [], {}, [[]]], \"\": {\"b\": null}}");
        var writer = new StringWriter();
        PyJsonDump.Dump(writer, value);
        Assert.Equal(PyJson.Dumps(value, indent: 2), writer.ToString());

        var deep = PyJson.Loads("{\"a\": 1, \"b\": [[[]]]}");
        writer = new StringWriter();
        Assert.Throws<PyRecursionError>(() => PyJsonDump.Dump(writer, deep, maxDepth: 3));
        Assert.Equal("{\n  \"a\": 1,\n  \"b\": [\n    [\n      ", writer.ToString());
    }

    [Fact]
    public void Json_load_depth_limit_lets_an_earlier_decode_error_win()
    {
        static string Nested(int depth, string tail = "") => new string('[', depth) + new string(']', depth) + tail;
        Assert.NotNull(Loads(Nested(5), 5));
        Assert.Throws<PyRecursionError>(() => Loads(Nested(6), 5));
        Assert.Throws<PyJsonDecodeError>(() => Loads("[1,, " + Nested(6) + "]", 5));
        Assert.Throws<PyRecursionError>(() => Loads("[" + Nested(6) + ",,]", 5));
        Assert.Throws<PyRecursionError>(() => Loads("[\"[[[[[[\", " + Nested(6) + "]", 5));
        Assert.NotNull(Loads("[\"[[[[[[[[[\"]", 5));
    }

    /// <summary><c>json.loads(text)</c> at a call site whose C recursion budget allows <paramref name="depth"/> containers.</summary>
    private static System.Text.Json.Nodes.JsonNode? Loads(string text, int depth) =>
        PyJson.Loads(text, new PyJsonLoadOptions { MaxDepth = depth });
}

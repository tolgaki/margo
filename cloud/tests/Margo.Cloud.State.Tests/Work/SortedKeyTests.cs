using System.Numerics;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Work;
using Xunit;

namespace Margo.Cloud.State.Tests.Work;

/// <summary>
/// <c>work_ledger._item_data</c> reports unknown fields with <c>", ".join(sorted(set(data) - allowed))</c>; a patch
/// given as pairs can add <c>None</c> and number keys, and CPython's TypeError then follows its set iteration order
/// and sort comparisons. Expected texts come from CPython 3.13 (<c>generate_sorted_key_fixtures.py</c>).
/// </summary>
public sealed class SortedKeyTests
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "title", "owner", "direction", "due", "source_refs", "confidence", "outcome_id", "next_step", "blocker",
        "deferred_until", "notes", "confirmation_source",
    };

    [Theory]
    [MemberData(nameof(SortedKeyFixtures.Cases), MemberType = typeof(SortedKeyFixtures))]
    public void Unknown_non_string_keys_report_the_cpython_sort_error(string keysJson, string expected)
    {
        var keys = (JsonArray)PyJson.Loads(keysJson)!;
        var unknown = keys.Select(PyNode.AsString).Where(key => key is not null && !Allowed.Contains(key)).Select(key => key!).ToList();
        var foreign = keys.Where(key => PyNode.AsString(key) is null).ToList();
        Assert.Equal(expected, PyNode.JoinSortedError(unknown, foreign, keys.Count, Allowed.Count).Message);
    }

    [Fact]
    public void Hashes_match_cpython()
    {
        // python3 -c 'print(hash(v))' (3.13, 64-bit): numbers and None do not depend on PYTHONHASHSEED.
        Assert.Equal(4238894112, PySortedKeys.Hash(null));
        Assert.Equal(1152921504606846978, PySortedKeys.Hash(2.5));
        Assert.Equal(1152921504606846977, PySortedKeys.Hash(1.5));
        Assert.Equal(-2, PySortedKeys.Hash(-1));
        Assert.Equal(-2, PySortedKeys.Hash(-1.0));
        Assert.Equal(1, PySortedKeys.Hash(PyJson.Int(BigInteger.Pow(2, 61))));
        Assert.Equal(0, PySortedKeys.Hash(PyJson.Int(BigInteger.Pow(2, 61) - 1)));
        Assert.Equal(-2, PySortedKeys.Hash(PyJson.Int(-BigInteger.Pow(2, 61))));
        Assert.Equal(230584300921369408, PySortedKeys.Hash(0.1));
        Assert.Equal(-1152921504606846976, PySortedKeys.Hash(-0.5));
        Assert.Equal(16777216, PySortedKeys.Hash(5e-324));
        Assert.Equal(1224995262755759164, PySortedKeys.Hash(1e300));
        Assert.Equal(0, PySortedKeys.Hash(-0.0));
        Assert.Equal(1, PySortedKeys.Hash(true));
        Assert.Equal(0, PySortedKeys.Hash(false));
        Assert.Equal(465258685558744706, PySortedKeys.Hash(PyJson.Int(BigInteger.Pow(10, 30))));
        Assert.Equal(-465258685558744706, PySortedKeys.Hash(PyJson.Int(-BigInteger.Pow(10, 30))));
        Assert.Equal(10000000000000000, PySortedKeys.Hash(1e16));
        Assert.Equal(1729382256910270467, PySortedKeys.Hash(3.75));
    }
}

using System.Numerics;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Work;
using Xunit;

namespace Margo.Cloud.State.Tests.Work;

/// <summary>
/// The Python dynamic semantics the work port relies on. Expected texts were produced by CPython 3.13.16 (for example
/// <c>Ledger().source(**{name: 1})</c> for the keyword suggestions).
/// </summary>
public sealed class PyDynamicTests
{
    private static readonly string[] SourceParameters =
        ["self", "family", "scope", "external_id", "revision", "evidence", "web_link", "sensitivity", "observed_at"];

    public static TheoryData<string, string?> Suggestions => new()
    {
        { "familly", "family" },
        { "fam", null },
        { "zzzzzzzz", null },
        { "Family", "family" },
        { "sel", "self" },
        { "_", null },
        { "self_", "self" },
        { "SELF", null },
        { "famiy", "family" },
        { "familyfamily", null },
        { "sensitivty", "sensitivity" },
        { "observed", "observed_at" },
        { "observedat", "observed_at" },
        { "observed_at_", "observed_at" },
        { "web-link", "web_link" },
        { "weblink", "web_link" },
        { "WEB_LINK", null },
        { "revisions", "revision" },
        { "evidance", "evidence" },
        { "ext_id", null },
        { "externalid", "external_id" },
        { "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", null },
        { "é", null },
        { "familé", "family" },
        { "scope2", "scope" },
        { "ſcope", "scope" },
        { "Scope", "scope" },
        { "sCOPE", "scope" },
        { "revison", "revision" },
        { "", null },
    };

    [Theory]
    [MemberData(nameof(Suggestions))]
    public void Keyword_suggestions_match_cpython(string keyword, string? expected) =>
        Assert.Equal(expected, PyCall.Suggest(SourceParameters, keyword));

    [Fact]
    public void Binding_errors_match_cpython_messages()
    {
        string Error(Action action) => Assert.Throws<PyTypeError>(action).Message;
        Assert.Equal("Ledger.source() missing 5 required positional arguments: 'scope', 'external_id', 'revision', 'evidence', and 'web_link'",
                     Error(() => PyCall.Bind("Ledger.source", SourceParameters, 7, 1, ["family"])));
        Assert.Equal("Ledger.source() missing 2 required positional arguments: 'evidence' and 'web_link'",
                     Error(() => PyCall.Bind("Ledger.source", SourceParameters, 7, 1, ["family", "scope", "external_id", "revision"])));
        Assert.Equal("Ledger.source() got multiple values for argument 'self'",
                     Error(() => PyCall.Bind("Ledger.source", SourceParameters, 7, 1, ["self"])));
        Assert.Equal("Ledger.source() got an unexpected keyword argument 'familly'. Did you mean 'family'?",
                     Error(() => PyCall.Bind("Ledger.source", SourceParameters, 7, 1, ["familly", "family"])));
        Assert.Equal("work_ledger.Ledger.relate() got multiple values for keyword argument 'revision'",
                     Error(() => PyCall.MergeKeywords("work_ledger.Ledger.relate()", ["revision"], new JsonObject { ["kind"] = 1, ["revision"] = 2 })));
        PyCall.Bind("Ledger.source", SourceParameters, 7, 1, SourceParameters.Skip(1).Take(6));
    }

    [Fact]
    public void Clone_keeps_big_ints_and_order_without_recursion()
    {
        var big = BigInteger.Pow(2, 80);
        var value = new JsonObject { ["z"] = PyJson.Int(big), ["a"] = new JsonArray(1.5, "x", null, true) };
        var copy = PyDynamic.CloneObject(value);
        Assert.Equal("{\"z\": 1208925819614629174706176, \"a\": [1.5, \"x\", null, true]}", PyJson.Dumps(copy));
        Assert.Null(copy.Parent);

        JsonNode deep = new JsonArray();
        for (var i = 0; i < 20000; i++)
        {
            deep = new JsonArray(deep);
        }

        Assert.IsType<JsonArray>(PyDynamic.Clone(deep));
    }

    [Fact]
    public void Dict_update_accepts_pairs_with_python_errors()
    {
        var target = new JsonObject { ["title"] = "t" };
        Assert.Empty(PyDynamic.Update(target, PyJson.Loads("[[\"owner\", \"x\"], \"ab\", {\"c\": 1, \"d\": 2}]")));
        Assert.Equal("{\"title\": \"t\", \"owner\": \"x\", \"a\": \"b\", \"c\": \"d\"}", PyJson.Dumps(target));
        Assert.Equal("dictionary update sequence element #0 has length 1; 2 is required",
                     Assert.Throws<PyValueError>(() => PyDynamic.Update(target, JsonValue.Create("xy"))).Message);
        Assert.Equal("'int' object is not iterable", Assert.Throws<PyTypeError>(() => PyDynamic.Update(target, JsonValue.Create(5))).Message);
        Assert.Equal("cannot convert dictionary update sequence element #1 to a sequence",
                     Assert.Throws<PyTypeError>(() => PyDynamic.Update(target, PyJson.Loads("[[\"a\", 1], null]"))).Message);
        Assert.Equal("unhashable type: 'list'", Assert.Throws<PyTypeError>(() => PyDynamic.Update(target, PyJson.Loads("[[[1], 1]]"))).Message);
        var foreign = PyDynamic.Update(target, PyJson.Loads("[[1, \"a\"], [true, \"b\"], [2.5, \"c\"]]"));
        Assert.Equal(2, foreign.Count);
        Assert.Equal("sequence item 0: expected str instance, int found",
                     PyDynamic.JoinSortedError([], foreign, target.Count + foreign.Count, 12).Message);
    }

    [Fact]
    public void Numbers_compare_exactly_across_int_and_float()
    {
        var twoTo53 = PyJson.Float(9007199254740992.0);
        Assert.True(PyDynamic.CompareNumbers(PyJson.Int(BigInteger.Pow(2, 53) + 1), twoTo53) > 0);
        Assert.Equal(0, PyDynamic.CompareNumbers(PyJson.Int(BigInteger.Pow(2, 53)), twoTo53));
        Assert.True(PyDynamic.CompareNumbers(PyJson.Int(0), PyJson.Float(0.5)) < 0);
        Assert.True(PyDynamic.CompareNumbers(PyJson.Int(-1), PyJson.Float(-0.5)) < 0);
        Assert.True(PyDynamic.CompareNumbers(JsonValue.Create(true), PyJson.Float(0.5)) > 0);
        Assert.Equal("'<' not supported between instances of 'str' and 'int'",
                     Assert.Throws<PyTypeError>(() => PyDynamic.CompareNumbers(JsonValue.Create("1"), PyJson.Int(1))).Message);
    }

    [Fact]
    public void Repr_iteration_and_membership_follow_python()
    {
        Assert.Equal("{'a': [1, 2.0, None, True, 'x\\n']}", PyDynamic.Repr(PyJson.Loads("{\"a\": [1, 2.0, null, true, \"x\\n\"]}")));
        Assert.Equal(["a", "\U0001F600", "b"], PyDynamic.Iter(JsonValue.Create("a\U0001F600b")).Select(PyDynamic.AsString));
        Assert.Equal(["k1", "k2"], PyDynamic.Iter(PyJson.Loads("{\"k1\": 1, \"k2\": 2}")).Select(PyDynamic.AsString));
        Assert.Equal("'NoneType' object is not iterable", Assert.Throws<PyTypeError>(() => PyDynamic.Iter(null)).Message);
        var states = new HashSet<string>(StringComparer.Ordinal) { "open" };
        Assert.True(PyDynamic.In(JsonValue.Create("open"), states));
        Assert.False(PyDynamic.In(PyJson.Int(1), states));
        Assert.Equal("unhashable type: 'dict'", Assert.Throws<PyTypeError>(() => PyDynamic.In(new JsonObject(), states)).Message);
        Assert.Equal("'list' object has no attribute 'get'", Assert.Throws<PyAttributeError>(() => PyDynamic.Dict(new JsonArray())).Message);
        Assert.Equal("string indices must be integers, not 'str'", Assert.Throws<PyTypeError>(() => PyDynamic.Index(JsonValue.Create("x"), "k")).Message);
        Assert.Equal("'k'", Assert.Throws<PyKeyError>(() => PyDynamic.Index(new JsonObject(), "k")).Message);
    }

    [Fact]
    public void Human_evidence_rules()
    {
        using var root = TempRoot.Create();
        JsonObject Evidence() => new()
        {
            ["kind"] = "human_confirmation",
            ["actor"] = "alex@example.com",
            ["statement"] = "Approved.",
            ["evidence_ref"] = "legacy-review:fictional",
            ["subject_id"] = "subject-1",
            ["revision"] = 1,
            ["decision"] = "approve",
            ["decided_at"] = "2026-01-02T03:04:05Z",
        };
        WorkLedger.Human(Evidence(), "subject-1", 1, "approve");
        WorkLedger.Human(Evidence(), "subject-1", 1);
        Assert.Equal("human evidence must record the confirm decision",
                     Assert.ThrowsAny<StateError>(() => WorkLedger.Human(Evidence(), "subject-1", 1, "confirm")).Message);
        var future = Evidence();
        future["decided_at"] = (PyDateTime.Now() + PyTimeDelta.FromMinutes(2)).Isoformat();
        Assert.Equal("human decision cannot be in the future", Assert.ThrowsAny<StateError>(() => WorkLedger.Human(future, "subject-1", 1)).Message);
        var boolean = Evidence();
        boolean["revision"] = true;
        Assert.ThrowsAny<StateError>(() => WorkLedger.Human(boolean, "subject-1", 1));
        Assert.Equal("2026-01-02T01:04:05+00:00", WorkLedger.Timestamp("2026-01-02T03:04:05+02:00").Isoformat());
        Assert.Throws<PyOverflowError>(() => WorkLedger.Timestamp("0001-01-01T00:00:00+01:00"));
        Assert.Equal(PyIds.Sha256Hex("[\"a\",1]"), WorkLedger.Digest(new JsonArray("a", 1)));
    }
}

using System.Numerics;
using Margo.Cloud.State.Core;
using Xunit;

namespace Margo.Cloud.State.Tests;

public sealed class PyArgsTests
{
    public static TheoryData<string[], string> DemoCases => PythonFixtures.DemoArgs;

    public static TheoryData<string, string> IntCases => PythonFixtures.PyInt;

    public static TheoryData<int, string, string[]> WrapCases => PythonFixtures.TextWrap;

    public static TheoryData<string, string> ReprCases => PythonFixtures.StrRepr;

    [Theory]
    [MemberData(nameof(DemoCases))]
    public void Demo_parser_matches_cpython_argparse(string[] argv, string expected)
    {
        using var root = TempRoot.Create(new Dictionary<string, string> { ["COLUMNS"] = "80" });
        var (code, stdout, stderr) = ScriptRunner.Run(new StateCoreProbeScript(), ["args", .. argv]);
        Assert.Equal(0, code);
        Assert.Equal("", stderr);
        Assert.Equal(expected, stdout);
    }

    [Fact]
    public void Errors_print_usage_and_exit_two()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = Assert.Throws<PySystemExit>(() =>
            StateCoreProbeScript.DemoParser().ParseArgs(["list", "--limit", "x"], stdout, stderr));
        Assert.Equal(2, exit.Code);
        Assert.Equal("", stdout.ToString());
        Assert.Equal("usage: demo.py list [-h] [--view {decisions,approval,waiting,problems,all}]\n"
                     + "                    [--json] [--limit LIMIT]\n"
                     + "demo.py list: error: argument --limit: invalid int value: 'x'\n",
                     stderr.ToString().Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void Long_options_accept_unique_prefixes_only()
    {
        var parser = new PyArgumentParser("p.py") { Width = 78 };
        parser.AddArgument(["--state-root", "--state-dir"], dest: "state_root");
        parser.AddArgument("--account");
        var ns = parser.ParseArgs(["--acc", "a", "--state-d=/x"], TextWriter.Null, TextWriter.Null);
        Assert.Equal("a", ns.GetString("account"));
        Assert.Equal("/x", ns.GetString("state_root"));
        var stderr = new StringWriter();
        Assert.Equal(2, Assert.Throws<PySystemExit>(() => parser.ParseArgs(["--state", "/x"], TextWriter.Null, stderr)).Code);
        Assert.EndsWith("p.py: error: ambiguous option: --state could match --state-root, --state-dir\n", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Help_goes_to_stdout_with_exit_zero_and_margo_store_usage_matches()
    {
        var (code, stdout, stderr) = ScriptRunner.Run(new MargoStoreScript(), ["init"]);
        Assert.Equal(2, code);
        Assert.Equal("", stdout);
        Assert.Equal("usage: margo_store.py init [-h] --account ACCOUNT [--config CONFIG]\n"
                     + "margo_store.py init: error: the following arguments are required: --account\n", stderr);
        using var root = TempRoot.Create(new Dictionary<string, string> { ["COLUMNS"] = "80" });
        (code, stdout, stderr) = ScriptRunner.Run(new MargoStoreScript(), ["--help"]);
        Assert.Equal(0, code);
        Assert.Equal("", stderr);
        Assert.StartsWith("usage: margo_store.py [-h] {init} ...\n\n" + StateStore.Doc + "\npositional arguments:\n  {init}\n"
                          + "    init      explicitly save the ledger owner in private config; no network\n              calls\n",
                          stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Namespace_values_have_python_types()
    {
        using var root = TempRoot.Create();
        var ns = StateCoreProbeScript.DemoParser().ParseArgs(["search", "--entity", "a", "--entity", "b"], TextWriter.Null, TextWriter.Null);
        Assert.Null(ns["query"]);
        Assert.Equal(["a", "b"], ns.GetStrings("entity")!);
        Assert.True(ns.Has("mode"));
        Assert.False(ns.Has("limit"));
        ns = StateCoreProbeScript.DemoParser().ParseArgs(["list", "--limit", "1_0"], TextWriter.Null, TextWriter.Null);
        Assert.Equal(new BigInteger(10), ns.GetInt("limit"));
        Assert.False(ns.GetBool("json"));
    }

    [Theory]
    [MemberData(nameof(IntCases))]
    public void Int_conversion_matches_python_int(string text, string expected)
    {
        string actual;
        try
        {
            actual = PyArgumentParser.ParsePyInt(text).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (PyValueError ex)
        {
            actual = "!" + ex.PythonType + ": " + ex.Message;
        }

        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(WrapCases))]
    public void Textwrap_matches_python(int width, string text, string[] expected) =>
        Assert.Equal(expected, PyTextWrap.Wrap(text, width));

    [Theory]
    [MemberData(nameof(ReprCases))]
    public void Str_repr_matches_python(string text, string expected) => Assert.Equal(expected, PyRepr.Str(text));
}

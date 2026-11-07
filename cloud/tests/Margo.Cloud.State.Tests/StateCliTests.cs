using Margo.Cloud.State.Core;
using Margo.Cloud.StateCli;
using Xunit;

namespace Margo.Cloud.State.Tests;

public sealed class StateCliTests
{
    private static (int Code, string Stdout, string Stderr) Cli(params string[] args)
    {
        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };
        var code = StateCliHost.Run(args, new StringReader(""), stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void Scripts_are_discovered_by_reflection()
    {
        Assert.Contains("margo_store.py", StateCliHost.Scripts.Keys);
        Assert.Contains("state_core_probe.py", StateCliHost.Scripts.Keys);
        Assert.IsType<MargoStoreScript>(StateCliHost.Scripts["margo_store.py"]);
    }

    [Fact]
    public void Unknown_or_missing_scripts_exit_two()
    {
        var (code, stdout, stderr) = Cli("nope.py", "list");
        Assert.Equal(2, code);
        Assert.Equal("", stdout);
        Assert.Contains("margo-state: error: unknown script 'nope.py'", stderr, StringComparison.Ordinal);
        (code, _, stderr) = Cli();
        Assert.Equal(2, code);
        Assert.Contains("the following arguments are required: SCRIPT.py", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Dispatch_passes_arguments_and_exit_status_through()
    {
        var (code, stdout, stderr) = Cli("margo_store.py", "init");
        Assert.Equal(2, code);
        Assert.Equal("", stdout);
        Assert.StartsWith("usage: margo_store.py init", stderr, StringComparison.Ordinal);
        (code, stdout, _) = Cli("scripts/margo_store.py", "--help");
        Assert.Equal(0, code);
        Assert.StartsWith("usage: margo_store.py [-h] {init} ...", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_script_names_are_rejected()
    {
        var error = Assert.Throws<InvalidOperationException>(() => StateScripts.Discover([typeof(DuplicateA).Assembly]));
        Assert.Contains("dup.py is implemented twice", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Uncaught_exceptions_exit_one_and_system_exit_keeps_its_status()
    {
        var stderr = new StringWriter();
        Assert.Equal(1, StateScripts.RunGuarded(() => throw new PyValueError("boom"), stderr));
        Assert.Contains("ValueError: boom", stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(3, StateScripts.RunGuarded(() => throw new PySystemExit(3), TextWriter.Null));
        Assert.Equal(1, StateScripts.RunGuarded(() => throw new InvalidOperationException("Could not determine home directory."), stderr));
        Assert.Contains("RuntimeError: Could not determine home directory.", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Print_refuses_lone_surrogates_before_writing_and_stderr_escapes_them()
    {
        var stdout = new StringWriter();
        var error = Assert.Throws<PyUnicodeError>(() => PyIO.Print(stdout, "ok \ud800"));
        Assert.Equal("'utf-8' codec can't encode character '\\ud800' in position 3: surrogates not allowed", error.Message);
        Assert.Equal("", stdout.ToString());
        var stderr = new StringWriter();
        StateScripts.PrintError(stderr, "ERROR: \ud800");
        Assert.Equal("ERROR: \\ud800\n", stderr.ToString());
    }

    public static TheoryData<string, string> Utf8Cases => PythonFixtures.Utf8;

    [Theory]
    [MemberData(nameof(Utf8Cases))]
    public void Utf8_decoding_matches_python_codec(string hex, string expected)
    {
        string actual;
        try
        {
            actual = PyIO.DecodeUtf8(Convert.FromHexString(hex));
        }
        catch (PyUnicodeError ex)
        {
            actual = "!" + ex.PythonType + ": " + ex.Message;
        }

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Stdin_reads_count_code_points()
    {
        Assert.Equal("a\U0001F600", PyIO.ReadText(new StringReader("a\U0001F600bc"), 2));
        Assert.Equal("a\U0001F600bc", PyIO.ReadText(new StringReader("a\U0001F600bc")));
    }

    private sealed class DuplicateA : IStateScript
    {
        public string ScriptName => "dup.py";

        public int Run(IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr) => 0;
    }

    private sealed class DuplicateB : IStateScript
    {
        public string ScriptName => "dup.py";

        public int Run(IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr) => 0;
    }
}

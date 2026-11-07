namespace Margo.Cloud.State.Core;

/// <summary>
/// <c>python3 margo_store.py init --account OWNER [--config PATH]</c>: saves the ledger owner in the private
/// config (no identity lookup, no network). Prints the canonical result, or <c>ERROR: ...</c> on stderr with
/// exit status 2.
/// </summary>
public sealed class MargoStoreScript : IStateScript
{
    public string ScriptName => "margo_store.py";

    public int Run(IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr) =>
        StateScripts.RunGuarded(() => Main(args, stdout, stderr), stderr);

    private int Main(IReadOnlyList<string> argv, TextWriter stdout, TextWriter stderr)
    {
        var parser = new PyArgumentParser(ScriptName, StateStore.Doc, PyHelpFormatter.RawDescription);
        var commands = parser.AddSubparsers(dest: "command", required: true);
        var init = commands.AddParser("init", help: "explicitly save the ledger owner in private config; no network calls");
        init.AddArgument("--account", required: true, help: "explicit ledger owner principal");
        init.AddArgument("--config", help: "private config path; otherwise MARGO_CONFIG or ~/.copilot/margo/config.json");
        var args = parser.ParseArgs(argv, stdout, stderr);
        try
        {
            PyIO.Print(stdout, StateStore.CanonicalJson(StateStore.InitializeConfig(args.GetString("account"), args.GetString("config"))));
            return 0;
        }
        catch (StateError ex)
        {
            StateScripts.PrintError(stderr, "ERROR: " + ex.Message);
            return 2;
        }
    }
}

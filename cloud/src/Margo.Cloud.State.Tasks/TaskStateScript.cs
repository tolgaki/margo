using System.Text;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.State.Tasks;

/// <summary>
/// <c>python3 task_state.py [--account A] [--state-dir D] COMMAND ...</c>: private task-run plans, bounded claims,
/// receipts and safe foreground recovery over <see cref="TaskStore"/>. Prints <c>canonical_json(result)</c>, or
/// <c>{"error": ..., "command": ...}</c> (plus <c>"code": "not_initialized"</c>) on stderr with exit status 2.
/// Read commands (<c>list</c>, <c>show</c>, <c>history</c>, <c>health</c>, <c>replan-preview</c>) never initialize state.
/// </summary>
public sealed class TaskStateScript : IStateScript
{
    /// <summary>The module docstring (argparse description).</summary>
    public const string Doc = "Private task-run plans, bounded claims, receipts and safe foreground recovery.";

    /// <summary>The input bound: <c>read(262145)</c>, then more than 262144 characters is refused.</summary>
    private const int InputLimit = 262144;

    private static readonly IReadOnlySet<string> ReadOnlyCommands = TaskRuns.Set("list", "show", "history", "health", "replan-preview");

    public string ScriptName => "task_state.py";

    public int Run(IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr) =>
        StateScripts.RunGuarded(() => Main(args, stdin, stdout, stderr), stderr);

    /// <summary><c>parser()</c>.</summary>
    public PyArgumentParser Parser()
    {
        var root = new PyArgumentParser(ScriptName, Doc);
        StateStore.AddStateArguments(root);
        var commands = root.AddSubparsers(dest: "command", required: true);
        commands.AddParser("init", help: "explicitly initialize private task/work/coverage schemas; no source collection");
        commands.AddParser("health", help: "read aggregate task journal health without initialization");
        var command = commands.AddParser("record-id", help: "resolve a local request key without initializing storage");
        command.AddArgument("key");
        command = commands.AddParser("create", help: "store a bounded private plan, not an external-action approval");
        command.AddArgument("key");
        command.AddArgument("--input", required: true);
        command = commands.AddParser("list", help: "read existing runs without initialization");
        command.AddArgument("--limit", type: PyArgType.Int, @default: 50);
        command.AddArgument("--after");
        foreach (var name in new[] { "show", "history", "recover", "sync" })
        {
            command = commands.AddParser(name);
            command.AddArgument("id");
            if (name == "history")
            {
                command.AddArgument("--limit", type: PyArgType.Int, @default: 100);
            }
        }

        foreach (var name in new[] { "pause", "cancel" })
        {
            command = commands.AddParser(name, help: "stop future work; never undo or erase an in-flight effect");
            command.AddArgument("id");
            command.AddArgument("--reason", required: true);
        }

        foreach (var name in new[] { "start", "charge", "finish", "resume", "retry", "reconcile" })
        {
            command = commands.AddParser(name);
            command.AddArgument("--input", required: true, help: "exact API envelope; use - for private stdin");
        }

        foreach (var name in new[] { "replan-preview", "replan" })
        {
            command = commands.AddParser(name);
            command.AddArgument("id");
            command.AddArgument("--input", required: true, help: "complete replacement plan");
            if (name == "replan")
            {
                command.AddArgument("--evidence", required: true);
            }
        }

        return root;
    }

    /// <summary>
    /// <c>load(path)</c>: at most 262145 characters from stdin (<c>-</c>) or the file, refused above 256 KiB, parsed with
    /// <c>parse_json</c>; only an object is accepted.
    /// </summary>
    private static JsonObject Load(string path, TextReader stdin)
    {
        var raw = path == "-" ? PyIO.ReadText(stdin, InputLimit + 1) : ReadFileText(PyPath.ExpandUser(path), InputLimit + 1);
        if (PyRepr.Length(raw) > InputLimit)
        {
            throw new StateError("task input exceeds 256 KiB of text");
        }

        return StateStore.ParseJson(raw) as JsonObject ?? throw new StateError("task input must be an object");
    }

    /// <summary>
    /// <c>open(path, encoding="utf-8").read(count)</c>: CPython's <c>TextIOWrapper.read(n)</c> over a regular file. Chunks
    /// of <c>max(8192, remaining)</c> bytes go through the incremental UTF-8 decoder and the universal-newline decoder
    /// until <paramref name="count"/> characters are read, so a decoding error after them is never seen and an error's
    /// position is relative to the chunk (plus any undecoded bytes carried over), as in Python.
    /// (<see cref="PyIO.ReadTextFile"/> decodes the whole file first; this belongs in State.Core.)
    /// </summary>
    internal static string ReadFileText(string path, int count)
    {
        var data = PyIO.ReadBytes(path);
        var result = new StringBuilder();
        var remaining = count;
        var position = 0;
        var pending = Array.Empty<byte>();
        var pendingCr = false;
        while (remaining > 0)
        {
            var size = Math.Min(Math.Max(8192, remaining), data.Length - position);
            var input = new byte[pending.Length + size];
            pending.CopyTo(input, 0);
            Array.Copy(data, position, input, pending.Length, size);
            position += size;
            var eof = size == 0;
            var text = PyIO.DecodeUtf8(input, final: eof, out var consumed);
            pending = input[consumed..];
            if (pendingCr && (text.Length > 0 || eof))
            {
                text = "\r" + text;
                pendingCr = false;
            }

            if (!eof && text.EndsWith('\r'))
            {
                text = text[..^1];
                pendingCr = true;
            }

            text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            if (eof && text.Length == 0)
            {
                break;
            }

            var taken = PyText.Prefix(text, remaining);
            result.Append(taken);
            remaining -= PyRepr.Length(taken);
        }

        return result.ToString();
    }

    /// <summary>
    /// <c>main(argv)</c> without the process wrapper: argparse exits raise <see cref="PySystemExit"/> and uncaught Python
    /// exceptions escape (<see cref="Run"/> turns them into status 1).
    /// </summary>
    internal int Main(IReadOnlyList<string> argv, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var args = Parser().ParseArgs(argv, stdout, stderr);
        var command = args.GetString("command")!;
        TaskStore? store = null;
        try
        {
            if (command == "record-id")
            {
                var identity = TaskRuns.TaskIdentity(StateStore.ResolveAccount(args.GetString("account")), args.GetString("key"));
                PyIO.Print(stdout, StateStore.CanonicalJson(new JsonObject { ["id"] = identity }));
                return 0;
            }

            store = new TaskStore(args.GetString("account"), args.GetString("state_dir"), readOnly: ReadOnlyCommands.Contains(command));
            var result = Dispatch(store, command, args, stdin);
            PyIO.Print(stdout, StateStore.CanonicalJson(result));
            return 0;
        }
        catch (Exception ex) when (ex is StateError or PyOSError or PySqliteError or PyTypeError or PyKeyError or PyValueError)
        {
            var error = new JsonObject { ["error"] = ex.Message, ["command"] = command };
            if (ex is NotInitialized)
            {
                error["code"] = "not_initialized";
            }

            StateScripts.PrintError(stderr, StateStore.CanonicalJson(error));
            return 2;
        }
        finally
        {
            store?.Close();
        }
    }

    private static JsonNode? Arg(JsonObject data, string key) => data.TryGetPropertyValue(key, out var value) ? value : null;

    private static JsonObject Dispatch(TaskStore store, string command, PyNamespace args, TextReader stdin)
    {
        switch (command)
        {
            case "init":
                return new JsonObject
                {
                    ["account"] = store.Account,
                    ["schema_version"] = "1",
                    ["initialized"] = true,
                    ["collected"] = false,
                    ["outbound_action"] = false,
                };
            case "create":
                return store.Create(args.GetString("key"), Load(args.GetString("input")!, stdin));
            case "list":
                return store.List(PyJson.Int(args.GetInt("limit")!.Value), args.GetString("after"));
            case "health":
                return store.Health();
            case "show":
                return store.Show(args.GetString("id"));
            case "history":
                return store.History(args.GetString("id"), PyJson.Int(args.GetInt("limit")!.Value));
            case "pause":
                return store.Pause(args.GetString("id"), args.GetString("reason"));
            case "cancel":
                return store.Cancel(args.GetString("id"), args.GetString("reason"));
            case "recover":
                return store.Recover(args.GetString("id"));
            case "sync":
                return store.Sync(args.GetString("id"));
            case "replan-preview":
                return store.ReplanPreview(args.GetString("id"), Load(args.GetString("input")!, stdin));
            case "replan":
            {
                var plan = Load(args.GetString("input")!, stdin);
                return store.Replan(args.GetString("id"), plan, Load(args.GetString("evidence")!, stdin));
            }
        }

        var data = Load(args.GetString("input")!, stdin);
        var keys = data.Select(pair => pair.Key).ToList();
        switch (command)
        {
            case "start":
                PyCall.Bind("TaskStore.start", ["self", "run_id", "step_key", "revision", "plan_hash", "binding", "preflight"], 6, 1, keys);
                return store.Start(Arg(data, "run_id"), Arg(data, "step_key"), Arg(data, "revision"), Arg(data, "plan_hash"), Arg(data, "binding"),
                                   Arg(data, "preflight"));
            case "charge":
                PyCall.Bind("TaskStore.charge", ["self", "attempt_id", "token", "event_id", "cost"], 5, 1, keys);
                return store.Charge(Arg(data, "attempt_id"), Arg(data, "token"), Arg(data, "event_id"), Arg(data, "cost"));
            case "finish":
                PyCall.Bind("TaskStore.finish", ["self", "attempt_id", "token", "outcome", "result", "actual"], 5, 1, keys);
                return store.Finish(Arg(data, "attempt_id"), Arg(data, "token"), Arg(data, "outcome"), Arg(data, "result"), Arg(data, "actual"));
            case "resume":
                PyCall.Bind("TaskStore.resume", ["self", "run_id", "revision", "binding"], 4, 1, keys);
                return store.Resume(Arg(data, "run_id"), Arg(data, "revision"), Arg(data, "binding"));
            case "retry":
                PyCall.Bind("TaskStore.retry", ["self", "run_id", "step_key", "revision", "binding"], 5, 1, keys);
                return store.Retry(Arg(data, "run_id"), Arg(data, "step_key"), Arg(data, "revision"), Arg(data, "binding"));
            default:
                PyCall.Bind("TaskStore.reconcile", ["self", "run_id", "step_key", "revision", "outcome", "result"], 6, 1, keys);
                return store.Reconcile(Arg(data, "run_id"), Arg(data, "step_key"), Arg(data, "revision"), Arg(data, "outcome"), Arg(data, "result"));
        }
    }
}

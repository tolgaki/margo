using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.State.Work;

/// <summary>
/// <c>python3 work_state.py [--account A] [--state-dir D] COMMAND ...</c>: the portable CLI action desk over the
/// work ledger (every subcommand, including the approval and execution journal ones the hosted tool does not
/// offer). Prints the result as JSON (<c>indent=2</c>, <c>ensure_ascii=False</c>), or
/// <c>{"error": ..., "command": ...}</c> on stderr with exit status 2. Never calls an outbound API.
/// </summary>
public sealed class WorkStateScript : IStateScript
{
    /// <summary>The module docstring (argparse description).</summary>
    public const string Doc = "Portable CLI action desk. This program never calls an outbound API.";

    private static readonly string[] RecordKinds = ["artifact", "feedback", "meeting", "outcome", "rule"];
    private static readonly string[] Views = ["decisions", "approval", "waiting", "problems", "all"];
    private static readonly string[] Revision = ["revision"];
    private static readonly string[] Reconcile = ["reconcile"];

    public string ScriptName => "work_state.py";

    public int Run(IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr) =>
        StateScripts.RunGuarded(() => Main(args, stdin, stdout, stderr), stderr);

    /// <summary><c>parser()</c>.</summary>
    public PyArgumentParser Parser()
    {
        var root = new PyArgumentParser(ScriptName, Doc);
        root.AddArgument("--account", help: "explicit configured account boundary; never silently defaults");
        root.AddArgument(["--state-root", "--state-dir"], dest: "state_root", help: "private state root accepted by margo_store");
        var commands = root.AddSubparsers(dest: "command", required: true);
        var command = commands.AddParser("source", help: "ingest a stable source revision with minimal evidence");
        command.AddArgument("--input", required: true);
        command = commands.AddParser("ingest", help: "idempotently capture one candidate; never confirms");
        command.AddArgument("--input", required: true, help: "JSON {claim_key,data}");
        command = commands.AddParser("list", help: "portable review views; JSON is also the optional canvas contract");
        command.AddArgument("--view", choices: Views, @default: "decisions");
        command.AddArgument("--json", action: PyArgAction.StoreTrue);
        foreach (var name in new[] { "show", "history" })
        {
            command = commands.AddParser(name);
            command.AddArgument("id");
            command.AddArgument("--json", action: PyArgAction.StoreTrue);
        }

        command = commands.AddParser("item-update", help: "conditional local work edit/transition; confirmed work needs human evidence");
        command.AddArgument("id");
        command.AddArgument("--revision", type: PyArgType.Int, required: true);
        command.AddArgument("--input", required: true, help: "JSON {state?,patch?,evidence?}");
        command = commands.AddParser("relate", help: "link work items or a canonical tracker; cycle-checked");
        command.AddArgument("id");
        command.AddArgument("--revision", type: PyArgType.Int, required: true);
        command.AddArgument("--input", required: true, help: "JSON {kind,target_id,evidence?}");
        command = commands.AddParser("propose", help: "prepare a local exact action payload, not an Outlook draft");
        command.AddArgument("--input", required: true);
        command = commands.AddParser("edit", help: "new immutable action revision; invalidates prior approvals");
        command.AddArgument("id");
        command.AddArgument("--revision", type: PyArgType.Int, required: true);
        command.AddArgument("--expected-hash", help: "additional atomic precondition from the displayed action_hash");
        command.AddArgument("--input", required: true);
        command = commands.AddParser("approve", help: "record one exact human decision; not an authentication boundary");
        command.AddArgument("id");
        command.AddArgument("--revision", type: PyArgType.Int, required: true);
        command.AddArgument("--input", required: true, help: "JSON {action_hash,evidence,expires_at}; no --yes/approve-all");
        command = commands.AddParser("defer", help: "defer a proposal or work item without deleting its sources");
        command.AddArgument("id");
        command.AddArgument("--revision", type: PyArgType.Int, required: true);
        command.AddArgument("--until", required: true);
        command.AddArgument("--expected-hash", help: "additional atomic action_hash precondition (actions only)");
        command.AddArgument("--evidence", help: "human evidence JSON required for confirmed work");
        command = commands.AddParser("dismiss", help: "dismiss only an action proposal; never closes a work item");
        command.AddArgument("id");
        command.AddArgument("--revision", type: PyArgType.Int, required: true);
        command.AddArgument("--expected-hash", help: "additional atomic precondition from the displayed action_hash");
        command = commands.AddParser("begin", help: "transactional foreground preflight; return exact payload once, never sends");
        command.AddArgument("id");
        command.AddArgument("--revision", type: PyArgType.Int, required: true);
        command.AddArgument("--input", required: true, help: "fresh read: checked_at,target_fingerprint,source_refs");
        foreach (var name in new[] { "finish", "reconcile" })
        {
            command = commands.AddParser(name, help: "journal a real result; ambiguous writes must be reconciled, never retried");
            command.AddArgument("attempt_id");
            command.AddArgument("--input", required: true, help: "JSON {state,receipt}");
        }

        command = commands.AddParser("import-preview", help: "review structured legacy JSON or expose unsafe Markdown migration");
        command.AddArgument("path");
        command = commands.AddParser("import-commitments", help: "transactional reviewed structured legacy import");
        command.AddArgument("path");
        command.AddArgument("--digest", required: true);
        command.AddArgument("--evidence", required: true);
        command = commands.AddParser("export-commitments", help: "confirmed-only Markdown compatibility view with edit detection");
        command.AddArgument("path");
        command.AddArgument("--expected-digest", help: "explicitly reviewed current file hash when adopting a manual view");
        command = commands.AddParser("record-id", help: "derive typed record ID before collecting a human decision");
        command.AddArgument("kind", choices: RecordKinds);
        command.AddArgument("identity");
        command = commands.AddParser("record", help: "create/update typed outcome, meeting, feedback, rule, or private Markdown artifact");
        command.AddArgument("kind", choices: RecordKinds);
        command.AddArgument("identity");
        command.AddArgument("--revision", type: PyArgType.Int);
        command.AddArgument("--input", required: true, help: "JSON {data,state?,evidence?}; updates replace all data");
        command = commands.AddParser("recap-check", help: "persist a bounded pending recap check; policy blocks stop retries");
        command.AddArgument("id");
        command.AddArgument("--revision", type: PyArgType.Int, required: true);
        command.AddArgument("--input", required: true, help: "JSON {result,evidence_ref,next_check_at?}");
        command = commands.AddParser("carry-forward", help: "carry open topic/work references into a later occurrence, without duplicating work");
        command.AddArgument("id");
        command.AddArgument("--revision", type: PyArgType.Int, required: true);
        command.AddArgument("--input", required: true, help: "JSON {target_id,target_revision,evidence}");
        command = commands.AddParser("artifact-shared", help: "link a succeeded exact delivery receipt; never publishes");
        command.AddArgument("id");
        command.AddArgument("--revision", type: PyArgType.Int, required: true);
        command.AddArgument("--attempt-id", required: true);
        return root;
    }

    /// <summary><c>load(path)</c>: a JSON object from a file or <c>-</c> (stdin), parsed with <c>parse_json</c>.</summary>
    private static JsonObject Load(string path, TextReader stdin) =>
        StateStore.ParseJson(path == "-" ? PyIO.ReadText(stdin) : PyIO.ReadTextFile(PyPath.Normalize(path))) as JsonObject
        ?? throw new StateError("input JSON must be an object");

    /// <summary>
    /// <c>main(argv)</c> without the process wrapper: argparse exits raise <see cref="PySystemExit"/> and uncaught
    /// Python exceptions escape (the contract probe reports them; <see cref="Run"/> turns them into status 1).
    /// </summary>
    internal int Main(IReadOnlyList<string> argv, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var args = Parser().ParseArgs(argv, stdout, stderr);
        var command = args.GetString("command")!;
        Ledger? ledger = null;
        try
        {
            ledger = new Ledger(args.GetString("account"), args.GetString("state_root"));
            var result = Dispatch(ledger, args, stdin);
            PyIO.Print(stdout, PyJson.Dumps(result, indent: 2, ensureAscii: false, allowNan: false));
            return 0;
        }
        catch (Exception ex) when (ex is StateError or PySqliteError or PyOSError or PyValueError or PyKeyError or PyTypeError)
        {
            StateScripts.PrintError(stderr, PyJson.Dumps(new JsonObject { ["error"] = ex.Message, ["command"] = command }, ensureAscii: false));
            return 2;
        }
        finally
        {
            ledger?.Close();
        }
    }

    private static JsonNode? Arg(JsonObject data, string key) => data.TryGetPropertyValue(key, out var value) ? value : null;

    private static JsonValue Int(PyNamespace args, string name) => PyJson.Int(args.GetInt(name)!.Value);

    private static JsonValue? Str(string? value) => value is null ? null : JsonValue.Create(value);

    private static JsonObject Dispatch(Ledger ledger, PyNamespace args, TextReader stdin)
    {
        var command = args.GetString("command")!;
        var data = args.Has("input") ? Load(args.GetString("input")!, stdin) : null;
        var keys = data?.Select(pair => pair.Key).ToList() ?? [];
        switch (command)
        {
            case "source":
                PyCall.Bind("Ledger.source", ["self", "family", "scope", "external_id", "revision", "evidence", "web_link", "sensitivity", "observed_at"],
                            7, 1, keys);
                return ledger.SourceCore(Arg(data!, "family"), Arg(data!, "scope"), Arg(data!, "external_id"), Arg(data!, "revision"),
                                         Arg(data!, "evidence"), Arg(data!, "web_link"),
                                         data!.ContainsKey("sensitivity") ? data["sensitivity"] : JsonValue.Create("unknown"),
                                         Arg(data, "observed_at"));
            case "ingest":
                return ledger.Ingest(PyNode.Index(data, "data"), PyNode.Index(data, "claim_key"));
            case "list":
                return ledger.List(args.GetString("view")!);
            case "show":
                return ledger.Show(args.GetString("id")!);
            case "history":
                return ledger.History(args.GetString("id")!);
            case "item-update":
                PyCall.Bind("Ledger.update_item", ["self", "item_id", "revision", "state", "patch", "evidence"], 3, 3, keys);
                return ledger.UpdateItem(Str(args.GetString("id")), Int(args, "revision"), Arg(data!, "state"), Arg(data!, "patch"), Arg(data!, "evidence"));
            case "relate":
                PyCall.MergeKeywords("work_ledger.Ledger.relate()", Revision, data!);
                PyCall.Bind("Ledger.relate", ["self", "source_id", "kind", "target_id", "revision", "evidence"], 5, 2, Revision.Concat(keys));
                return ledger.Relate(Str(args.GetString("id")), Arg(data!, "kind"), Arg(data!, "target_id"), Int(args, "revision"), Arg(data!, "evidence"));
            case "propose":
                return ledger.Propose(data);
            case "edit":
                return ledger.EditAction(Str(args.GetString("id")), Int(args, "revision"), data, Str(args.GetString("expected_hash")));
            case "approve":
                PyCall.Bind("Ledger.approve", ["self", "action_id", "revision", "action_hash", "evidence", "expires_at"], 6, 3, keys);
                return ledger.Approve(Str(args.GetString("id")), Int(args, "revision"), Arg(data!, "action_hash"), Arg(data!, "evidence"), Arg(data!, "expires_at"));
            case "defer":
            {
                var id = Str(args.GetString("id"));
                var entity = ledger.Show(id);
                if (PyNode.Is(entity["type"], "action"))
                {
                    return ledger.Disposition(id, Int(args, "revision"), "deferred", Str(args.GetString("until")), Str(args.GetString("expected_hash")));
                }

                if (PyNode.Is(entity["type"], "item"))
                {
                    if (args.GetString("expected_hash") is not null)
                    {
                        throw new StateError("expected-hash is for action revisions, not work items");
                    }

                    var evidence = args.GetString("evidence") is { Length: > 0 } path ? Load(path, stdin) : null;
                    return ledger.UpdateItem(id, Int(args, "revision"), JsonValue.Create("deferred"),
                                             new JsonObject { ["deferred_until"] = args.GetString("until") }, evidence);
                }

                throw new StateError("defer supports items/actions; use record for typed lifecycle edits");
            }

            case "dismiss":
                return ledger.Disposition(Str(args.GetString("id")), Int(args, "revision"), "dismissed", null, Str(args.GetString("expected_hash")));
            case "begin":
                return ledger.Begin(Str(args.GetString("id")), Int(args, "revision"), data);
            case "finish":
            case "reconcile":
                PyCall.MergeKeywords("work_ledger.Ledger.finish()", Reconcile, data!);
                PyCall.Bind("Ledger.finish", ["self", "attempt_id", "state", "receipt", "reconcile"], 3, 2, Reconcile.Concat(keys));
                return ledger.Finish(Str(args.GetString("attempt_id")), Arg(data!, "state"), Arg(data!, "receipt"), reconcile: command == "reconcile");
            case "import-preview":
                return ledger.ImportPreview(args.GetString("path")!);
            case "import-commitments":
                return ledger.ImportCommitments(args.GetString("path")!, args.GetString("digest")!, Load(args.GetString("evidence")!, stdin));
            case "export-commitments":
                return ledger.ExportCommitments(args.GetString("path")!, args.GetString("expected_digest"));
        }

        var products = new Productivity(ledger);
        switch (command)
        {
            case "record-id":
                return new JsonObject { ["id"] = products.RecordId(args.GetString("kind")!, Str(args.GetString("identity"))) };
            case "record":
            {
                PyCall.MergeKeywords("work_productivity.Productivity.put()", Revision, data!);
                PyCall.Bind("Productivity.put", ["self", "kind", "identity", "data", "state", "revision", "evidence"], 4, 3, Revision.Concat(keys));
                var revision = args.GetInt("revision");
                return products.Put(args.GetString("kind")!, Str(args.GetString("identity")), Arg(data!, "data"), Arg(data!, "state"),
                                    revision is null ? null : PyJson.Int(revision.Value), Arg(data!, "evidence"));
            }

            case "recap-check":
                PyCall.Bind("Productivity.recap_retry", ["self", "meeting_id", "revision", "result", "evidence_ref", "next_check_at"], 5, 3, keys);
                return products.RecapRetry(Str(args.GetString("id")), Int(args, "revision"), Arg(data!, "result"), Arg(data!, "evidence_ref"),
                                           Arg(data!, "next_check_at"));
            case "carry-forward":
                PyCall.Bind("Productivity.carry_forward", ["self", "meeting_id", "revision", "target_id", "target_revision", "evidence"], 6, 3, keys);
                return products.CarryForward(Str(args.GetString("id")), Int(args, "revision"), Arg(data!, "target_id"), Arg(data!, "target_revision"),
                                             Arg(data!, "evidence"));
            case "artifact-shared":
                return products.ShareReceipt(Str(args.GetString("id")), Int(args, "revision"), Str(args.GetString("attempt_id")));
        }

        throw new StateError("unsupported command");
    }
}

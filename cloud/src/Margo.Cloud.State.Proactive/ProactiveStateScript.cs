using System.Numerics;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.State.Proactive;

/// <summary>
/// <c>python3 proactive_state.py [--account A] [--state-dir D] COMMAND ...</c>: the proactive queue, publication
/// receipts and source coverage CLI. Results are JSON (<c>indent=2, sort_keys=True, ensure_ascii=False</c>);
/// failed validation or storage prints <c>ERROR: ...</c> and exits 2, never resetting state.
/// </summary>
public sealed class ProactiveStateScript : IStateScript
{
    /// <summary>The module docstring (the argparse description, shown raw).</summary>
    public const string Doc =
        "Transactional proactive queue, publication receipts and source coverage.\n"
        + "\n"
        + "All commands are local; Work IQ reads and publication remain the caller's job.\n"
        + "Global --account/--state-dir flags precede the command. JSON arguments accept\n"
        + "inline JSON or '-' for stdin. Failed validation/storage exits 2, never resets.\n"
        + "\n"
        + "Delivery:\n"
        + "  queue-add --json '{\"id\":\"event-1\",\"revision\":\"v2\",\"title\":\"Moved\"}'\n"
        + "  queue-drain --owner morning-run --format json\n"
        + "  publication-record --batch BATCH --json '{\"content\":\"Finished brief\",\n"
        + "    \"ids\":[\"event-1\"],\"status\":\"available\",\"receipt\":{\"kind\":\"local\"}}'\n"
        + "  queue-ack --batch BATCH --receipt PUBLICATION_ID\n"
        + "  publication-list\n"
        + "  publication-show PUBLICATION_ID\n"
        + "  publication-review PUBLICATION_ID\n"
        + "An available local output is durably stored here, NOT proof of host delivery or\n"
        + "user review. For published output use status \"published\" and receipt:\n"
        + "{\"kind\":\"host\",\"receipt_id\":\"HOST_ID\",\"location\":\"OUTPUT_LINK\",\n"
        + " \"published_at\":\"2026-09-02T00:00:00Z\"}.\n"
        + "An optional publication \"id\" makes recording replay-safe. Prepared output cannot\n"
        + "acknowledge anything. Acknowledge only included IDs; live leases are never stolen.\n"
        + "An interrupted owner can queue-release; otherwise expiry makes items eligible.\n"
        + "\n"
        + "Coverage (source identity = account + family + scope + optional collection_window):\n"
        + "  coverage-start --json '{\"family\":\"mail\",\"scope\":{\"folder\":\"inbox\"},\n"
        + "    \"window\":{\"start\":\"2026-09-01T00:00:00Z\",\"end\":\"2026-09-02T00:00:00Z\"},\n"
        + "    \"run_id\":\"run-1\",\"capability\":\"messages.list\",\"query_version\":\"1\",\n"
        + "    \"kind\":\"enumeration\"}'\n"
        + "  coverage-page --attempt ATTEMPT --json '{\"page\":1,\"observations\":[\n"
        + "    {\"id\":\"message-1\",\"revision\":\"v1\",\"link\":\"provider-link\"}],\"final\":true}'\n"
        + "  coverage-finish --attempt ATTEMPT --json '{\"status\":\"complete\",\n"
        + "    \"checkpoint_at\":\"2026-09-02T00:00:00Z\",\"watermark\":\"2026-09-02T00:00:00Z\"}'\n"
        + "Page numbers start at 1 and are contiguous. Every page and its observations are\n"
        + "committed together. Nonfinal pages may carry opaque continuation. Finish accepts\n"
        + "complete/partial/blocked/failed/unknown, error_class, retry_after_seconds,\n"
        + "next_retry_at, delta_token and covered_start/covered_end. Only complete enumeration\n"
        + "with a final page advances a checkpoint; even empty search is not proof of absence.\n"
        + "Complete may set delta_token:null to clear a delta token; expired_token failures\n"
        + "also discard the unusable delta token without changing the successful checkpoint.\n"
        + "coverage-status redacts tokens; coverage-resume explicitly retrieves private tokens.\n"
        + "coverage-start accepts cadence_seconds for freshness/escalation and resume_attempt\n"
        + "to resume compatible partial/failed pages; denied/expired tokens cannot be resumed.\n"
        + "Report OAuth failures as blocked with error_class authentication_required. No retry\n"
        + "is eligible until authentication is restored; then coverage-start requires the\n"
        + "explicit boolean reauthenticated:true. Configuration never authenticates Work IQ.\n"
        + "Missing tools or a stale host binding use failed/binding_unavailable instead.\n"
        + "A binding failure is not evidence of a tenant-wide authentication outage.\n"
        + "window is the per-attempt requested interval, not permanent polling identity.\n"
        + "For provider-bound calendar delta horizons, explicitly set collection_window to\n"
        + "that fixed window as well; retire finished horizons with coverage-retire.\n"
        + "Continuation resume requires the same original requested interval.\n"
        + "An empty-queue anchor can publication-record without --batch or item membership;\n"
        + "its standalone output remains discoverable and requires no queue acknowledgement.\n"
        + "cursor-set/get are legacy timestamp HINTS ONLY. import-legacy --directory DIR is\n"
        + "explicit, atomic and replay-safe; pause old writers first. It never edits old JSON.\n";

    public string ScriptName => "proactive_state.py";

    public int Run(IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr) =>
        StateScripts.RunGuarded(() => Main(args, stdin, stdout, stderr), stderr);

    /// <summary><c>parser()</c>: the same subcommands, options, defaults and help text as proactive_state.py.</summary>
    public PyArgumentParser Parser()
    {
        var root = new PyArgumentParser(ScriptName, Doc, PyHelpFormatter.RawDescription);
        StateStore.AddStateArguments(root);
        var sub = root.AddSubparsers(dest: "command", required: true);
        var seen = sub.AddParser("seen", help: "0 if published revision, 1 if new; legacy hints do not count");
        seen.AddArgument("id");
        seen.AddArgument("--revision", required: true, help: "current provider/content revision, never just an event ID");
        seen.AddArgument("--family", @default: "legacy");
        seen.AddArgument("--scope", @default: "{}", help: "JSON scope");
        var mark = sub.AddParser("mark", help: "compatibility ack of one ID; a durable receipt is mandatory");
        mark.AddArgument("id");
        mark.AddArgument("--receipt", required: true);
        mark.AddArgument("--tier", @default: "manual", help: "legacy compatibility; receipt remains authoritative");
        mark.AddArgument("--note", @default: "", help: "legacy compatibility; does not prove publication");
        var add = sub.AddParser("queue-add", help: "queue a stable ID and revision; arbitrary evidence fields allowed");
        add.AddArgument("--json", required: true);
        add.AddArgument("--tier");
        var listing = sub.AddParser("queue-list", help: "show pending items (leases are visible in status)");
        listing.AddArgument("--format", choices: ["text", "json"], @default: "text");
        var drain = sub.AddParser("queue-drain", help: "lease pending/expired items; never steal a live batch");
        drain.AddArgument("--owner", help: "run ID; defaults to unique batch ID");
        drain.AddArgument("--lease-seconds", type: PyArgType.Int, @default: new BigInteger(900));
        drain.AddArgument("--limit", type: PyArgType.Int, @default: new BigInteger(100));
        drain.AddArgument("--format", choices: ["text", "json"], @default: "text");
        var ack = sub.AddParser("queue-ack", help: "ack actual receipt-covered items, not unrelated batch members");
        ack.AddArgument("ids", nargs: "*");
        ack.AddArgument("--item-key", action: PyArgAction.Append, dest: "item_keys");
        ack.AddArgument("--batch");
        ack.AddArgument("--receipt", required: true, help: "publication ID returned after durable output recording");
        foreach (var command in new[] { "queue-release", "queue-renew" })
        {
            var lease = sub.AddParser(command, help: "explicit owner-only lease " + command.Split('-')[1]);
            lease.AddArgument("--batch", required: true);
            lease.AddArgument("--owner", required: true);
            if (command == "queue-renew")
            {
                lease.AddArgument("--lease-seconds", type: PyArgType.Int, @default: new BigInteger(900));
            }
        }

        var record = sub.AddParser("publication-record", help: "durably save output, exact included items and receipt");
        record.AddArgument("--batch", help: "owned queue batch; omit for standalone output with no queued membership");
        record.AddArgument("--json", required: true,
                           help: "content, ids OR item_keys, status prepared/available/published, receipt; optional id for replay");
        var publish = sub.AddParser("publication-publish", help: "establish availability/publication of a prepared output");
        publish.AddArgument("publication_id");
        publish.AddArgument("--json", required: true, help: "status and receipt; see top-level --help");
        var show = sub.AddParser("publication-show", help: "retrieve durable, locally discoverable output and its status");
        show.AddArgument("publication_id");
        var outputs = sub.AddParser("publication-list", help: "discover durable outputs and pending acknowledgements");
        outputs.AddArgument("--limit", type: PyArgType.Int, @default: new BigInteger(20));
        var review = sub.AddParser("publication-review", help: "record explicit human review, never inferred from delivery");
        review.AddArgument("publication_id");
        review.AddArgument("--at", help: "explicit review timestamp; defaults to now");
        var start = sub.AddParser("coverage-start", help: "start scoped source attempt, preserving the successful checkpoint");
        start.AddArgument("--json", required: true,
                          help: "family, scope, window (requested bounds), run_id, capability, query_version, kind; "
                                + "optional collection_window (fixed provider scope), cadence_seconds, resume_attempt, reauthenticated, reactivate");
        foreach (var command in new[] { "coverage-page", "coverage-finish" })
        {
            var cov = sub.AddParser(command, help: command.EndsWith("page", StringComparison.Ordinal)
                                        ? "store page+observations atomically"
                                        : "finish attempt; only proven complete enumeration advances coverage");
            cov.AddArgument("--attempt", required: true);
            cov.AddArgument("--json", required: true, help: "see complete JSON contract in top-level --help");
        }

        sub.AddParser("coverage-status", help: "coverage and failure episodes; tokens redacted");
        var retire = sub.AddParser("coverage-retire", help: "exclude a finished historical collection from current monitoring");
        retire.AddArgument("--source-key", required: true);
        retire.AddArgument("--reason", required: true);
        var resume = sub.AddParser("coverage-resume", help: "retrieve private continuation/checkpoint tokens; never put in briefs");
        resume.AddArgument("--attempt", required: true);
        foreach (var command in new[] { "cursor-get", "cursor-set" })
        {
            var cursor = sub.AddParser(command, help: "legacy timestamp hint, NOT a successful source checkpoint");
            cursor.AddArgument("name");
            if (command == "cursor-set")
            {
                cursor.AddArgument("timestamp");
            }
        }

        var legacy = sub.AddParser("import-legacy", help: "atomic replay-safe JSON import; never confirms coverage/review");
        legacy.AddArgument("--directory", required: true);
        var prune = sub.AddParser("prune", help: "expire leases only; never silently discard unpublished items/receipts");
        prune.AddArgument("--days", type: PyArgType.Int, @default: new BigInteger(30), help: "compatibility option; durable receipts are retained");
        sub.AddParser("status", help: "health JSON, or setup-needed without inventing an account");
        return root;
    }

    private int Main(IReadOnlyList<string> argv, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var args = Parser().ParseArgs(argv, stdout, stderr);
        var command = args.GetString("command");
        PyConnection? conn = null;
        try
        {
            conn = StateStore.Connect(args.GetString("account"), args.GetString("state_dir"));
            ProactiveState.Initialize(conn);
            JsonNode? result;
            switch (command)
            {
                case "queue-add":
                    result = ProactiveState.QueueAdd(conn, Input(args.GetString("json")!, stdin), args.GetString("tier"));
                    break;
                case "queue-list":
                {
                    var rows = new JsonArray();
                    foreach (var row in conn.Execute("SELECT * FROM proactive_items WHERE status='pending' ORDER BY created_at,item_key"))
                    {
                        rows.Add(ProactiveState.RenderItem(row));
                    }

                    QueueOutput(rows, args.GetString("format")!, stdout);
                    return 0;
                }

                case "queue-drain":
                {
                    var rows = ProactiveState.QueueDrain(conn, args.GetString("owner"), args.GetInt("lease_seconds"), args.GetInt("limit"));
                    if (rows.Count > 0)
                    {
                        var first = (JsonObject)rows[0]!;
                        StateScripts.PrintError(stderr, "batch " + Py.Format(Py.Index(first, "batch")) + " owner " + Py.Format(Py.Index(first, "owner"))
                                                        + " expires " + Py.Format(Py.Index(first, "lease_expires_at")));
                    }

                    QueueOutput(rows, args.GetString("format")!, stdout);
                    return 0;
                }

                case "queue-ack":
                {
                    var ids = args.GetStrings("ids")!;
                    var itemKeys = args.GetStrings("item_keys");
                    if (ids.Count > 0 && itemKeys is { Count: > 0 })
                    {
                        throw new StateError("choose IDs or --item-key, not both");
                    }

                    result = ProactiveState.QueueAck(conn, args.GetString("receipt"), args.GetString("batch"), ids, itemKeys);
                    break;
                }

                case "mark":
                    result = ProactiveState.QueueAck(conn, args.GetString("receipt"), ids: [args.GetString("id")!]);
                    break;
                case "queue-release":
                    result = ProactiveState.QueueRelease(conn, args.GetString("batch")!, args.GetString("owner")!);
                    break;
                case "queue-renew":
                    result = ProactiveState.QueueRenew(conn, args.GetString("batch")!, args.GetString("owner")!, args.GetInt("lease_seconds")!.Value);
                    break;
                case "publication-record":
                    result = ProactiveState.PublicationRecord(conn, args.GetString("batch"), Input(args.GetString("json")!, stdin));
                    break;
                case "publication-publish":
                    result = ProactiveState.PublicationPublish(conn, args.GetString("publication_id")!, Input(args.GetString("json")!, stdin));
                    break;
                case "publication-list":
                    result = ProactiveState.PublicationList(conn, args.GetInt("limit"));
                    break;
                case "publication-show":
                    result = ProactiveState.PublicationShow(conn, args.GetString("publication_id")!);
                    break;
                case "publication-review":
                    result = ProactiveState.PublicationReview(conn, args.GetString("publication_id")!, args.GetString("at"));
                    break;
                case "coverage-start":
                    result = ProactiveState.CoverageStart(conn, Input(args.GetString("json")!, stdin));
                    break;
                case "coverage-page":
                    result = ProactiveState.CoveragePage(conn, args.GetString("attempt")!, Input(args.GetString("json")!, stdin));
                    break;
                case "coverage-finish":
                    result = ProactiveState.CoverageFinish(conn, args.GetString("attempt")!, Input(args.GetString("json")!, stdin));
                    break;
                case "coverage-status":
                    result = ProactiveState.CoverageStatus(conn);
                    break;
                case "coverage-retire":
                    result = ProactiveState.CoverageRetire(conn, args.GetString("source_key")!, args.GetString("reason")!);
                    break;
                case "coverage-resume":
                    result = CoverageResume(conn, args.GetString("attempt")!);
                    break;
                case "cursor-get":
                {
                    var row = conn.Execute("SELECT timestamp FROM proactive_legacy_cursors WHERE name=?", args.GetString("name")).FetchOne();
                    PyIO.Print(stdout, row is not null ? PyRepr.ToStr(row[0]) : "");
                    StateScripts.PrintError(stderr, "legacy hint only; not confirmed source coverage");
                    return row is not null ? 0 : 1;
                }

                case "cursor-set":
                {
                    var timestamp = StateStore.ValidateTimestamp(args.GetString("timestamp"));
                    var name = args.GetString("name");
                    var open = conn;
                    open.With(() => open.Execute("INSERT INTO proactive_legacy_cursors VALUES (?,?) "
                                                 + "ON CONFLICT(name) DO UPDATE SET timestamp=excluded.timestamp", name, timestamp));
                    result = new JsonObject { ["name"] = name, ["timestamp"] = timestamp, ["confirmed_coverage"] = false };
                    break;
                }

                case "seen":
                {
                    JsonNode? parsed;
                    try
                    {
                        parsed = PyJson.Loads(args.GetString("scope")!);
                    }
                    catch (PyJsonDecodeError ex)
                    {
                        throw new StateError("scope must be JSON", ex);
                    }

                    var scope = StateStore.CanonicalJson(parsed);
                    var id = args.GetString("id")!;
                    var revision = args.GetString("revision")!;
                    var row = conn.Execute("SELECT status FROM proactive_items WHERE stable_id=? AND revision=?\n                AND family=? AND scope=? AND status='published'",
                                           id, revision, args.GetString("family"), scope).FetchOne();
                    Emit(new JsonObject { ["id"] = id, ["revision"] = revision, ["published"] = row is not null }, stdout);
                    return row is not null ? 0 : 1;
                }

                case "import-legacy":
                    result = ProactiveState.ImportLegacy(conn, args.GetString("directory")!);
                    break;
                case "prune":
                {
                    ProactiveState.Integer(args.GetInt("days")!.Value, "days", 1);
                    var open = conn;
                    var released = open.With(() =>
                    {
                        open.Execute("BEGIN IMMEDIATE");
                        return ProactiveState.ReleaseExpired(open, StateStore.UtcNow());
                    });
                    result = new JsonObject
                    {
                        ["expired_batches_released"] = released,
                        ["unpublished_items_deleted"] = 0L,
                        ["note"] = "Durable observations/receipts are retained; back up before explicit retention maintenance.",
                    };
                    break;
                }

                default:
                    result = ProactiveState.StatusReport(conn);
                    break;
            }

            Emit(result, stdout);
            return 0;
        }
        catch (SetupRequired ex)
        {
            if (command == "status")
            {
                Emit(new JsonObject
                {
                    ["status"] = "setup-needed", ["account_configured"] = false, ["all_clear"] = false, ["action"] = ex.Message,
                }, stdout);
                return 0;
            }

            StateScripts.PrintError(stderr, "ERROR: " + ex.Message);
            return 2;
        }
        catch (Exception ex) when (ex is StateError or PySqliteError or PyOSError or PyValueError or PyTypeError or PyKeyError)
        {
            var message = ex is StateError ? ex.Message : "storage unavailable; no reset performed";
            StateScripts.PrintError(stderr, "ERROR: " + message);
            return 2;
        }
        finally
        {
            conn?.Close();
        }
    }

    /// <summary><c>coverage-resume</c>: the private continuation and delta token, unless the query contract changed.</summary>
    private static JsonObject CoverageResume(PyConnection conn, string attemptId)
    {
        var attempt = ProactiveState.Attempt(conn, attemptId);
        var source = conn.Execute("SELECT * FROM proactive_sources WHERE source_key=?", attempt["source_key"]).FetchOne()
                     ?? throw new PyTypeError("'NoneType' object is not subscriptable");
        var contract = conn.Execute("SELECT value FROM proactive_meta WHERE key=?", "token-contract:" + Py.Str(attempt["source_key"])).FetchOne();
        var incompatible = source["delta_token"] is not null && (contract is null || !Py.Eq(contract[0], StateStore.CanonicalJson(
            new JsonArray(Py.Node(attempt["capability"]), Py.Node(attempt["query_version"]), Py.Node(attempt["kind"])))));
        return new JsonObject
        {
            ["attempt_id"] = attemptId,
            ["status"] = Py.Node(attempt["status"]),
            ["next_page"] = Py.Int(attempt["pages"]) + 1,
            ["continuation"] = Py.Node(attempt["continuation"]),
            ["delta_token"] = incompatible ? null : Py.Node(source["delta_token"]),
            ["checkpoint_at"] = Py.Node(source["checkpoint_at"]),
            ["watermark"] = Py.Node(source["watermark"]),
            ["resync_required"] = incompatible || Py.Eq(attempt["error_class"], "expired_token")
                                  || conn.Execute("SELECT 1 FROM proactive_meta WHERE key=?", "resync:" + attemptId).FetchOne() is not null,
            ["blocked"] = Py.Eq(attempt["error_class"], "access_denied") || Py.Eq(attempt["error_class"], "authentication_required"),
            ["note"] = "Tokens are opaque/private. Revalidate access and capability before retrying.",
        };
    }

    /// <summary><c>_input(value)</c>: inline JSON, or stdin for <c>-</c>; must be an object.</summary>
    private static JsonObject Input(string value, TextReader stdin)
    {
        var raw = value == "-" ? PyIO.ReadText(stdin) : value;
        return ProactiveState.ObjectJson(StateStore.ParseJson(raw));
    }

    /// <summary><c>_emit(value)</c>.</summary>
    private static void Emit(JsonNode? value, TextWriter stdout) =>
        PyIO.Print(stdout, PyJson.Dumps(value, indent: 2, sortKeys: true, ensureAscii: false, allowNan: false));

    /// <summary><c>_queue_output(rows, fmt)</c>: JSON, or one text line per item.</summary>
    private static void QueueOutput(JsonArray rows, string format, TextWriter stdout)
    {
        if (format == "json")
        {
            Emit(rows, stdout);
            return;
        }

        foreach (var node in rows)
        {
            var row = (JsonObject)node!;
            var id = Py.Index(row, "id");
            var tier = Py.Get(row, "tier", "?");
            var title = Py.Get(row, "title", id);
            var revision = Py.Index(row, "revision");
            PyIO.Print(stdout, "- [" + Py.Format(tier) + "] " + Py.Format(title) + " (id=" + Py.Format(id) + ", revision=" + Py.Format(revision) + ")");
        }

        if (rows.Count == 0)
        {
            PyIO.Print(stdout, "(queue empty)");
        }
    }
}

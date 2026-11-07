using System.Numerics;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Memory;
using Margo.Cloud.State.MemoryLearning;
using Margo.Cloud.State.MemorySearch;
using Search = Margo.Cloud.State.MemorySearch.MemorySearch;

namespace Margo.Cloud.State.MemoryCommands;

/// <summary>
/// <c>python3 memory_state.py [--account A] [--state-dir D] COMMAND ...</c>: private user/agent memory, local semantic
/// indexing and bounded context retrieval, composed from the Memory, MemorySearch, MemoryLearning, Tasks and Proactive
/// ports and <see cref="MemoryDream"/>. Prints <c>canonical_json(result)</c>, or <c>{"error": ..., "command": ...}</c>
/// (plus <c>"code": "not_initialized"</c>) on stderr with exit status 2. Read commands open the store read-only and never
/// initialize it.
/// </summary>
public sealed class MemoryStateScript : IStateScript
{
    /// <summary>The module docstring (argparse description).</summary>
    public const string Doc = "Private user/agent memory, local semantic indexing, and bounded context retrieval.";

    private static readonly HashSet<string> ReadCommands = new(
    [
        "status", "show", "inspect", "history", "forget-preview", "usage", "graph", "explain",
        "list", "search", "context", "policy", "policy-preview", "tombstones", "tombstones-preview",
        "export-preview", "preferences-preview", "consolidate",
        "dream-plan", "dream-inspect", "dream-status",
    ], StringComparer.Ordinal);

    private static readonly HashSet<string> SearchCommands = new(
        ["init", "migrate", "status", "search", "context", "graph", "explain", "index"], StringComparer.Ordinal);

    private static readonly string[] Domains = ["user", "agent"];
    private static readonly string[] Usages = ["reasoning", "drafting"];
    private static readonly string[] Modes = ["hybrid", "semantic", "lexical"];

    public string ScriptName => "memory_state.py";

    public int Run(IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr) =>
        StateScripts.RunGuarded(() => Main(args, stdin, stdout, stderr), stderr);

    /// <summary><c>parser()</c>.</summary>
    public PyArgumentParser Parser()
    {
        var root = new PyArgumentParser(ScriptName, Doc);
        StateStore.AddStateArguments(root);
        var sub = root.AddSubparsers(dest: "command", required: true);
        sub.AddParser("init", help: "initialise private memory schema; does not collect sources");
        sub.AddParser("migrate", help: "explicitly migrate memory schema v1 to v2 after pausing writers and backing up");
        sub.AddParser("status", help: "memory lifecycle and semantic index health");
        var command = sub.AddParser("record-id");
        command.AddArgument("domain", choices: Domains);
        command.AddArgument("key");
        command = sub.AddParser("put", help: "capture or conditionally revise one sourced memory");
        command.AddArgument("key");
        command.AddArgument("--input", required: true, help: "{data,status?,evidence?}; - reads stdin");
        command.AddArgument("--revision", type: PyArgType.Int);
        command = sub.AddParser("capture", help: "stage a minimal observation under the reviewed capture policy");
        command.AddArgument("key");
        command.AddArgument("--input", required: true, help: "memory data object, not an approval envelope");
        command.AddArgument("--revision", type: PyArgType.Int);
        command = sub.AddParser("revise", help: "reviewed correction or lifecycle change using the current memory ID");
        command.AddArgument("id");
        command.AddArgument("--revision", required: true, type: PyArgType.Int);
        command.AddArgument("--input", required: true, help: "{data,status}; full replacement memory data");
        command.AddArgument("--evidence");
        command = sub.AddParser("show");
        command.AddArgument("id");
        command = sub.AddParser("list");
        command.AddArgument("--domain", choices: Domains);
        command.AddArgument("--status");
        foreach (var name in new[] { "inspect", "history", "forget-preview", "usage" })
        {
            command = sub.AddParser(name, help: "inspect memory and its private lifecycle; no approval or change");
            command.AddArgument("id");
        }

        foreach (var name in new[] { "graph", "explain" })
        {
            command = sub.AddParser(name, help: "inspect bounded current relationships and why context is eligible");
            command.AddArgument("id");
            command.AddArgument("--depth", type: PyArgType.Int, @default: new BigInteger(2));
            command.AddArgument("--limit", type: PyArgType.Int, @default: new BigInteger(20));
            command.AddArgument("--routine");
            command.AddArgument("--usage", choices: Usages, @default: "reasoning");
            command.AddArgument("--environment");
            command.AddArgument("--domain", choices: Domains);
            command.AddArgument("--budget-chars", type: PyArgType.Int, @default: new BigInteger(12000));
        }

        foreach (var name in new[] { "search", "context" })
        {
            command = sub.AddParser(name);
            command.AddArgument("query", nargs: PyNargs.Optional);
            command.AddArgument("--input", help: "private JSON {query}; - reads stdin instead of process arguments");
            command.AddArgument("--domain", choices: Domains);
            command.AddArgument("--routine");
            command.AddArgument("--usage", choices: Usages, @default: "reasoning");
            command.AddArgument("--entity", action: PyArgAction.Append, @default: new List<object?>());
            command.AddArgument("--environment", help: "JSON file with current host/tool versions");
            command.AddArgument("--mode", choices: Modes, @default: "hybrid");
            command.AddArgument("--limit", type: PyArgType.Int, @default: new BigInteger(8));
            if (name == "context")
            {
                command.AddArgument("--budget-chars", type: PyArgType.Int, @default: new BigInteger(12000));
                command.AddArgument("--work-id", action: PyArgAction.Append, @default: new List<object?>());
                command.AddArgument("--depth", type: PyArgType.Int, @default: new BigInteger(2));
                command.AddArgument("--nodes", type: PyArgType.Int, @default: new BigInteger(30));
            }
        }

        command = sub.AddParser("index", help: "process versioned outbox jobs with local embeddings");
        command.AddArgument("--limit", type: PyArgType.Int, @default: new BigInteger(100));
        command.AddArgument("--rebuild", action: PyArgAction.StoreTrue);
        command = sub.AddParser("forget", help: "erase memory and derived indexes after explicit user approval");
        command.AddArgument("id");
        command.AddArgument("--revision", required: true, type: PyArgType.Int);
        command.AddArgument("--evidence", required: true);
        var relations = PyText.Sorted(MemoryStore.Relations);
        foreach (var name in new[] { "link", "unlink" })
        {
            command = sub.AddParser(name);
            command.AddArgument("source");
            command.AddArgument("target");
            command.AddArgument("--relation", required: true, choices: relations);
            command.AddArgument("--revision", required: true, type: PyArgType.Int);
            command.AddArgument("--evidence", required: name == "unlink");
            if (name == "link")
            {
                command.AddArgument("--input", help: "relationship validity and source_refs JSON");
                command.AddArgument("--target-revision", type: PyArgType.Int);
            }
        }

        sub.AddParser("policy", help: "show capture, retention and usage settings; capture is off until configured");
        foreach (var name in new[] { "policy-preview", "policy-set" })
        {
            command = sub.AddParser(name);
            command.AddArgument("--input", required: true);
            if (name == "policy-set")
            {
                command.AddArgument("--evidence", required: true);
            }
        }

        command = sub.AddParser("maintain", help: "bounded expiry/review under approved retention; never source ingestion");
        command.AddArgument("--limit", type: PyArgType.Int, @default: new BigInteger(100));
        command.AddArgument("--after");
        command = sub.AddParser("record-usage", help: "opt-in minimal memory IDs/revisions, not prompts or transcripts");
        command.AddArgument("--input", required: true, help: "{refs,routine,outcome?,event_id?}");
        sub.AddParser("tombstones", help: "export a minimal deletion journal for private backups");
        foreach (var name in new[] { "tombstones-preview", "tombstones-restore" })
        {
            command = sub.AddParser(name);
            command.AddArgument("--input", required: true);
            if (name == "tombstones-restore")
            {
                command.AddArgument("--evidence", required: true);
            }
        }

        foreach (var name in new[] { "export-preview", "export" })
        {
            command = sub.AddParser(name, help: "review a sanitized lesson recipe; no automatic publication");
            command.AddArgument("id");
            command.AddArgument("--input", required: true, help: "sanitized recipe fields, never raw memory export");
            if (name == "export")
            {
                command.AddArgument("--evidence", required: true);
                command.AddArgument("--out", required: true, help: "new private file; existing files are never replaced");
            }
        }

        command = sub.AddParser("preferences-preview", help: "inspect proposed private configuration chunks before importing");
        command.AddArgument("path");
        command = sub.AddParser("preferences-import", help: "seed saved preferences under explicit import approval");
        command.AddArgument("path");
        command.AddArgument("--evidence", required: true);
        command = sub.AddParser("capabilities", help: "observe installed skill hashes, not validate competence");
        command.AddArgument("--skills-dir");
        command.AddArgument("--tools", help: "private host-exported tool manifest");
        command.AddArgument("--environment", help: "JSON with exact host/account/version scope");
        command = sub.AddParser("validate-capability", help: "record a pinned synthetic input exercise or existing execution receipt");
        command.AddArgument("id");
        command.AddArgument("--revision", required: true, type: PyArgType.Int);
        command.AddArgument("--evidence", required: true);
        command = sub.AddParser("propose-lesson", help: "stage an evidence-backed scoped recipe, never executable instructions");
        command.AddArgument("key");
        command.AddArgument("--input", required: true);
        command.AddArgument("--revision", type: PyArgType.Int);
        command = sub.AddParser("activate-lesson", help: "activate an exact reviewed candidate without granting any tool authority");
        command.AddArgument("id");
        command.AddArgument("--revision", required: true, type: PyArgType.Int);
        command.AddArgument("--evidence", required: true);
        command = sub.AddParser("trend-definition", help: "preview or explicitly approve bounded aggregate trend thresholds");
        command.AddArgument("name");
        command.AddArgument("--input", required: true);
        command.AddArgument("--revision", type: PyArgType.Int);
        command.AddArgument("--evidence");
        command = sub.AddParser("trend", help: "stage a coverage-backed trend hypothesis, not a confirmed rule");
        command.AddArgument("key");
        command.AddArgument("--input", required: true);
        command.AddArgument("--revision", type: PyArgType.Int);
        command = sub.AddParser("consolidate", help: "propose one bounded review page; no merge, activation or erasure");
        command.AddArgument("--limit", type: PyArgType.Int, @default: new BigInteger(10));
        command.AddArgument("--scan-limit", type: PyArgType.Int, @default: new BigInteger(100));
        command.AddArgument("--after");
        command.AddArgument("--environment");
        command = sub.AddParser("record-consolidation", help: "record delivery of an unchanged proposal page, never human approval");
        command.AddArgument("--input", required: true);
        foreach (var name in new[] { "dream-checkpoint", "dream-plan", "dream-finish" })
        {
            command = sub.AddParser(name, help: "opted-in bounded Dream checkpoint/reflection; no history scraping");
            command.AddArgument("--input", required: true, help: "private checkpoint/reflection JSON; - reads stdin");
        }

        command = sub.AddParser("dream-start", help: "claim one manual reflection and reserve its host model budget");
        command.AddArgument("key");
        command.AddArgument("--input", required: true, help: "{request,snapshot_hash,request_ref} from dream-plan and current user");
        command = sub.AddParser("dream-inspect", help: "inspect collection, reflection, candidates and source validity");
        command.AddArgument("id");
        command = sub.AddParser("dream-status", help: "show exact host/workspace scope and opt-in; no setup or history reads");
        command.AddArgument("--host", required: true);
        command.AddArgument("--workspace", required: true);
        return root;
    }

    /// <summary>
    /// <c>load(path, maximum=None)</c>: stdin (<c>-</c>) or the file, whole (<c>read()</c>) or at most
    /// <paramref name="maximum"/> + 1 characters (<c>read(maximum + 1)</c>, more than <paramref name="maximum"/> refused),
    /// parsed with <c>parse_json</c>; only an object is accepted.
    /// </summary>
    internal static JsonObject Load(string path, TextReader stdin, int? maximum = null)
    {
        string raw;
        if (path == "-")
        {
            raw = maximum is { } bound ? PyIO.ReadText(stdin, bound + 1) : PyIO.ReadText(stdin);
        }
        else
        {
            var file = PyPath.Normalize(path);
            raw = maximum is { } bound ? PyTextFile.Read(file, bound + 1) : PyIO.ReadTextFile(file);
        }

        if (maximum is { } limit && PyRepr.Length(raw) > limit)
        {
            throw new StateError("input exceeds its character budget");
        }

        return StateStore.ParseJson(raw) as JsonObject ?? throw new StateError("input must be a JSON object");
    }

    /// <summary>
    /// <c>main(argv)</c> without the process wrapper: argparse exits raise <see cref="PySystemExit"/> and uncaught Python
    /// exceptions escape (<see cref="Run"/> turns them into status 1).
    /// </summary>
    internal int Main(IReadOnlyList<string> argv, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var args = Parser().ParseArgs(argv, stdout, stderr);
        var command = args.GetString("command")!;
        MemoryStore? memory = null;
        try
        {
            if (command == "record-id")
            {
                var identity = MemoryStore.MemoryIdentity(StateStore.ResolveAccount(args.GetString("account")), args.GetString("domain"),
                                                          args.GetString("key"));
                PyIO.Print(stdout, StateStore.CanonicalJson(new JsonObject { ["id"] = identity.Id }));
                return 0;
            }

            var readOnly = ReadCommands.Contains(command) || (command == "trend-definition" && !Truthy(args.GetString("evidence")));
            memory = new MemoryStore(args.GetString("account"), args.GetString("state_dir"), migrate: command == "migrate", readOnly: readOnly);
            var search = SearchCommands.Contains(command)
                ? new Search(memory, rebuildSchema: command == "index" && args.GetBool("rebuild"))
                : null;
            var result = Dispatch(memory, search, command, args, stdin);
            PyIO.Print(stdout, StateStore.CanonicalJson(result));
            return 0;
        }
        catch (Exception ex) when (ex is StateError or PyOSError or PySqliteError or PyValueError or PyTypeError or PyKeyError)
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
            memory?.Close();
        }
    }

    private static bool Truthy(string? value) => !string.IsNullOrEmpty(value);

    private static JsonValue? Int(PyNamespace args, string name) => args.GetInt(name) is { } value ? PyJson.Int(value) : null;

    private static JsonValue? Str(PyNamespace args, string name) => args.GetString(name) is { } value ? JsonValue.Create(value) : null;

    private static JsonArray Strings(PyNamespace args, string name) => PyNode.StrList(args.GetStrings(name) ?? []);

    /// <summary><c>load(args.NAME) if args.NAME else None</c>.</summary>
    private static JsonObject? LoadIf(PyNamespace args, string name, TextReader stdin) =>
        Truthy(args.GetString(name)) ? Load(args.GetString(name)!, stdin) : null;

    private static JsonObject Dispatch(MemoryStore memory, Search? search, string command, PyNamespace args, TextReader stdin)
    {
        string Arg(string name) => args.GetString(name)!;
        JsonObject Input() => Load(Arg("input"), stdin);
        JsonObject Evidence() => Load(Arg("evidence"), stdin);
        switch (command)
        {
            case "init":
            case "migrate":
                return new JsonObject
                {
                    ["account"] = memory.Account,
                    ["status"] = "initialised",
                    ["collected"] = false,
                    ["schema_version"] = PyNode.Copy(PyNode.Index(memory.Health(), "schema_version")),
                };
            case "status":
            {
                JsonNode encoder;
                try
                {
                    encoder = MemoryEncoder.StatusLocal();
                }
                catch (EmbeddingError ex)
                {
                    encoder = new JsonObject { ["status"] = "unavailable", ["error"] = ex.Message };
                }

                var health = memory.Health();
                var index = search!.Health();
                return new JsonObject
                {
                    ["account"] = memory.Account,
                    ["memory"] = health,
                    ["index"] = index,
                    ["embedding_runtime"] = encoder,
                    ["policy"] = memory.Policy(),
                };
            }

            case "put":
            {
                var data = Input();
                var keywords = new List<string> { "revision" };
                PyCall.MergeKeywords("memory_store.MemoryStore.put()", keywords, data);
                keywords.AddRange(data.Select(pair => pair.Key));
                PyCall.Bind("MemoryStore.put", ["self", "key", "data", "status", "revision", "evidence"], 3, 2, keywords);
                return memory.Put(Arg("key"), Take(data, "data"), data.ContainsKey("status") ? PyArg.Of(Take(data, "status")) : PyArg.Omitted,
                                  Int(args, "revision"), Take(data, "evidence"));
            }

            case "capture":
                return memory.Capture(Arg("key"), Input(), Int(args, "revision"));
            case "revise":
            {
                var value = Input();
                if (!value.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal).SetEquals(["data", "status"]))
                {
                    throw new StateError("revise requires data and status only");
                }

                var evidence = LoadIf(args, "evidence", stdin);
                return memory.Revise(Arg("id"), Take(value, "data"), Take(value, "status"), Int(args, "revision"), evidence);
            }

            case "show":
                return memory.Show(Arg("id"));
            case "inspect":
                return MemoryContext.ReadSnapshot(memory.Conn, () => new JsonObject
                {
                    ["memory"] = memory.Show(Arg("id")),
                    ["history"] = memory.History(Arg("id")),
                    ["links"] = memory.Links(Arg("id")),
                    ["forget_preview"] = memory.ForgetPreview(Arg("id")),
                    ["usage"] = memory.Usage(Arg("id")),
                });
            case "history":
                return new JsonObject { ["id"] = Arg("id"), ["history"] = memory.History(Arg("id")) };
            case "forget-preview":
                return memory.ForgetPreview(Arg("id"));
            case "usage":
                return new JsonObject { ["id"] = Arg("id"), ["usage"] = memory.Usage(Arg("id")) };
            case "graph":
            case "explain":
            {
                var environment = LoadIf(args, "environment", stdin);
                return command == "graph"
                    ? search!.Graph(Arg("id"), Int(args, "depth"), Int(args, "limit"), Str(args, "routine"), Str(args, "usage"), environment,
                                    Str(args, "domain"), Int(args, "budget_chars"))
                    : search!.Explain(Arg("id"), Int(args, "depth"), Int(args, "limit"), Str(args, "routine"), Str(args, "usage"), environment,
                                      Str(args, "domain"), Int(args, "budget_chars"));
            }

            case "list":
                return MemoryContext.ReadSnapshot(memory.Conn, () =>
                {
                    var records = memory.List(Str(args, "domain"), Str(args, "status"));
                    var conflicts = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var row in records)
                    {
                        foreach (var edge in memory.ActiveLinks(PyNode.Copy(PyNode.Index(row, "id"))))
                        {
                            if (PyJson.PyEquals(PyNode.Index(edge, "relation"), JsonValue.Create("contradicts")))
                            {
                                conflicts.Add(PyNode.Str(PyNode.Index(edge, "source_id")));
                                conflicts.Add(PyNode.Str(PyNode.Index(edge, "target_id")));
                            }
                        }
                    }

                    return new JsonObject
                    {
                        ["account"] = memory.Account,
                        ["memories"] = records,
                        ["conflicted_ids"] = PyNode.StrList(PyText.Sorted(conflicts)),
                    };
                });
            case "search":
            case "context":
            {
                var query = Truthy(args.GetString("input"))
                    ? PyNode.Copy(PyNode.Get(Input(), "query"))
                    : Str(args, "query");
                if (Truthy(args.GetString("input")) && args.GetString("query") is not null)
                {
                    throw new StateError("choose a positional query or --input, not both");
                }

                var environment = LoadIf(args, "environment", stdin);
                return command == "search"
                    ? search!.Search(query, Int(args, "limit"), Str(args, "domain"), Str(args, "routine"), Str(args, "usage"),
                                     Strings(args, "entity"), environment, Str(args, "mode"))
                    : search!.Context(query, Str(args, "routine"), Str(args, "usage"), Strings(args, "entity"), environment, Str(args, "domain"),
                                      Int(args, "budget_chars"), Str(args, "mode"), Int(args, "depth"), Int(args, "nodes"), Strings(args, "work_id"));
            }

            case "index":
                return search!.Index(Int(args, "limit"), args.GetBool("rebuild"));
            case "forget":
            {
                var result = memory.Forget(Arg("id"), Int(args, "revision"), Evidence());
                result["index_content_purged"] = true;
                result["source_work_items_deleted"] = false;
                return result;
            }

            case "link":
            {
                var evidence = LoadIf(args, "evidence", stdin);
                var data = LoadIf(args, "input", stdin);
                return memory.Link(Arg("source"), Arg("target"), Arg("relation"), Int(args, "revision"), evidence, data, Int(args, "target_revision"));
            }

            case "unlink":
                return memory.Unlink(Arg("source"), Arg("target"), Arg("relation"), Int(args, "revision"), Evidence());
            case "policy":
                return memory.Policy();
            case "policy-preview":
                return MemoryGovernance.PolicyPreview(memory, Input());
            case "policy-set":
            {
                var input = Input();
                return MemoryGovernance.SetPolicy(memory, input, Evidence());
            }

            case "maintain":
                return MemoryGovernance.Maintain(memory, Int(args, "limit"), Str(args, "after"));
            case "record-usage":
            {
                var data = Input();
                PyCall.Bind("MemoryStore.record_usage", ["self", "refs", "routine", "outcome", "event_id"], 3, 1, data.Select(pair => pair.Key));
                return memory.RecordUsage(Take(data, "refs"), Take(data, "routine"),
                                          data.ContainsKey("outcome") ? PyArg.Of(Take(data, "outcome")) : PyArg.Omitted, Take(data, "event_id"));
            }

            case "tombstones":
                return MemoryGovernance.Tombstones(memory);
            case "tombstones-preview":
                return MemoryGovernance.TombstonesPreview(memory, Input());
            case "tombstones-restore":
            {
                var input = Input();
                return MemoryGovernance.RestoreTombstones(memory, input, Evidence());
            }

            case "export-preview":
                return MemoryGovernance.ExportPreview(memory, Arg("id"), Input());
            case "export":
            {
                var input = Input();
                return MemoryGovernance.ExportRecipe(memory, Arg("id"), input, Evidence(), Arg("out"));
            }

            case "preferences-preview":
                return MemoryLearning.MemoryLearning.PreferencesPlan(memory, Arg("path"));
            case "preferences-import":
                return MemoryLearning.MemoryLearning.ImportPreferences(memory, Arg("path"), Evidence());
            case "capabilities":
                return MemoryLearning.MemoryLearning.Capabilities(memory, args.GetString("skills_dir"), LoadIf(args, "environment", stdin),
                                                                  args.GetString("tools"));
            case "validate-capability":
                return MemoryLearning.MemoryLearning.Validation(memory, Arg("id"), Int(args, "revision"), Evidence());
            case "propose-lesson":
                return MemoryLearning.MemoryLearning.LessonPlan(memory, Arg("key"), Input(), Int(args, "revision"));
            case "activate-lesson":
                return MemoryLearning.MemoryLearning.ActivateLesson(memory, Arg("id"), Int(args, "revision"), Evidence());
            case "trend-definition":
            {
                var input = Input();
                return MemoryLearning.MemoryLearning.ConfigureTrend(memory, Arg("name"), input, Int(args, "revision"), LoadIf(args, "evidence", stdin));
            }

            case "trend":
                return MemoryLearning.MemoryLearning.Trend(memory, Arg("key"), Input(), Int(args, "revision"));
            case "consolidate":
                return MemoryConsolidation.ConsolidationPlan(memory, Int(args, "limit"), LoadIf(args, "environment", stdin), Str(args, "after"),
                                                             Int(args, "scan_limit"));
            case "record-consolidation":
                return MemoryConsolidation.SurfaceConsolidation(memory, Input());
            case "dream-status":
                return MemoryDream.Status(memory, Arg("host"), Arg("workspace"));
            case "dream-inspect":
                return MemoryDream.Inspect(memory, Arg("id"));
            case "dream-start":
                return MemoryDream.Start(memory, Arg("key"), Load(Arg("input"), stdin, MemoryDream.MaxInput));
            case "dream-checkpoint":
                return MemoryDream.Checkpoint(memory, Load(Arg("input"), stdin, MemoryDream.MaxInput));
            case "dream-plan":
                return MemoryDream.Plan(memory, Load(Arg("input"), stdin, MemoryDream.MaxInput));
            case "dream-finish":
                return MemoryDream.Finish(memory, Load(Arg("input"), stdin, MemoryDream.MaxInput));
            default:
                throw new StateError("unsupported memory command");
        }
    }

    /// <summary>A keyword argument taken out of a <c>**data</c> dict (detached, so the callee may keep it).</summary>
    private static JsonNode? Take(JsonObject data, string key)
    {
        if (!data.TryGetPropertyValue(key, out var value))
        {
            return null;
        }

        data.Remove(key);
        return value;
    }
}

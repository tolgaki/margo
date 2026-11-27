using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Memory;
using Margo.Cloud.State.MemorySearch;

namespace Margo.Cloud.State.Probes;

/// <summary>
/// C# twin of <c>cloud/contracts/state/probes/memory_search_probe.py</c>. Not a Margo command and not offered to the
/// hosted model: <c>tools/state_parity.py</c> runs both on the same program so <see cref="MemorySearch.MemorySearch"/>
/// and <see cref="MemoryContext"/>/<see cref="ConnectedContext"/> are judged byte for byte against CPython, including
/// the semantic paths with the probe's deterministic fake encoder. Keep the two files in step.
/// </summary>
public sealed partial class MemorySearchProbeScript : IStateProbeScript
{
    private static readonly string[] HoldVerbs = ["open", "close", "begin", "rollback", "encoder_of"];

    private readonly Dictionary<string, JsonNode?> _saved = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MemorySearch.MemorySearch> _searches = new(StringComparer.Ordinal);
    private string _account = "";
    private string _stateDir = "";
    private TextWriter _stdout = TextWriter.Null;

    public string ScriptName => "memory_search_probe.py";

    public int Run(IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr) =>
        StateScripts.RunGuarded(() => new MemorySearchProbeScript().Main(args, stdin, stdout, stderr), stderr);

    [GeneratedRegex(@"\A\$\{([^}]+)\}\z")]
    private static partial Regex Whole();

    [GeneratedRegex(@"\$\{([^}]+)\}")]
    private static partial Regex Part();

    [GeneratedRegex(@"\Anow(?:([+-])([0-9]+)([smhd]))?\z")]
    private static partial Regex Clock();

    private int Main(IReadOnlyList<string> argv, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var parser = new PyArgumentParser("memory_search_probe.py");
        parser.AddArgument("--account", required: true);
        parser.AddArgument("--state-dir", required: true);
        var args = parser.ParseArgs(argv, stdout, stderr);
        if (PyJson.Loads(PyIO.ReadText(stdin)) is not JsonArray program)
        {
            StateScripts.PrintError(stderr, "stdin must be a JSON list of steps");
            return 1;
        }

        _account = args.GetString("account")!;
        _stateDir = args.GetString("state_dir")!;
        _stdout = stdout;
        try
        {
            for (var index = 0; index < program.Count; index++)
            {
                if (!Step(index.ToString(CultureInfo.InvariantCulture), program[index]!.AsObject()))
                {
                    StateScripts.PrintError(stderr, "unknown step " + index.ToString(CultureInfo.InvariantCulture));
                    return 1;
                }
            }
        }
        finally
        {
            foreach (var search in _searches.Values)
            {
                Close(search);
            }
        }

        return 0;
    }

    private bool Step(string label, JsonObject step)
    {
        if (step.ContainsKey("call"))
        {
            Call(label, step);
        }
        else if (HoldVerbs.Any(step.ContainsKey))
        {
            Hold(label, step);
        }
        else if (step.ContainsKey("sql"))
        {
            Sql(label, step);
        }
        else
        {
            return false;
        }

        return true;
    }

    private void Print(string text) => PyIO.Print(_stdout, text);

    private static string Dump(JsonNode? value) => PyJson.Dumps(value, ensureAscii: false);

    // ------------------------------------------------------------------------------ templates

    private JsonNode? Value(string expression)
    {
        var clock = Clock().Match(expression);
        if (clock.Success)
        {
            var moment = PyDateTime.Now();
            if (clock.Groups[1].Success)
            {
                var amount = long.Parse(clock.Groups[2].Value, CultureInfo.InvariantCulture);
                var delta = clock.Groups[3].Value switch
                {
                    "s" => PyTimeDelta.FromSeconds(amount),
                    "m" => PyTimeDelta.FromMinutes(amount),
                    "h" => PyTimeDelta.FromHours(amount),
                    _ => PyTimeDelta.FromDays(amount),
                };
                moment = clock.Groups[1].Value == "+" ? moment + delta : moment - delta;
            }

            return moment.Isoformat();
        }

        switch (expression)
        {
            case "account":
                return _account;
            case "state_dir":
                return _stateDir;
        }

        if (expression.StartsWith("json:", StringComparison.Ordinal))
        {
            return PyJson.Loads(PyNode.AsString(Value(expression["json:".Length..]))!);
        }

        if (expression.StartsWith("id:", StringComparison.Ordinal))
        {
            var parts2 = expression.Split(':', 3);
            return MemoryStore.MemoryIdentity(_account, parts2[1], parts2[2]).Id;
        }

        var parts = expression.Split('.');
        var value = _saved[parts[0]];
        foreach (var part in parts.Skip(1))
        {
            value = value is JsonArray list ? list[int.Parse(part, CultureInfo.InvariantCulture)] : value![part];
        }

        return PyNode.Copy(value);
    }

    private JsonNode? Template(JsonNode? value)
    {
        switch (value)
        {
            case JsonValue scalar when PyJson.Scalar(scalar) is string text:
                var whole = Whole().Match(text);
                return whole.Success ? Value(whole.Groups[1].Value) : JsonValue.Create(Text(text));
            case JsonArray list:
                return new JsonArray(list.Select(Template).ToArray());
            case JsonObject obj:
                var result = new JsonObject();
                foreach (var (key, item) in obj)
                {
                    result[key] = Template(item);
                }

                return result;
            default:
                return PyNode.Copy(value);
        }
    }

    private string Text(string value) => Part().Replace(value, match =>
    {
        var found = Value(match.Groups[1].Value);
        return PyNode.AsString(found) ?? PyJson.Dumps(found, ensureAscii: false);
    });

    private string TextOf(JsonObject step, string key, string fallback) => step.TryGetPropertyValue(key, out var value) ? Text(PyNode.AsString(value)!) : fallback;

    private static bool Flag(JsonObject step, string key) => step[key] is { } value && PyJson.Truthy(value);

    private static string Error(Exception ex)
    {
        var type = ex switch
        {
            PyException py => py.PythonType,
            InvalidOperationException => "RuntimeError",
            _ => throw ex,
        };
        return "!" + type + ": " + ex.Message;
    }

    private static bool IsReported(Exception ex) => ex is PyException or InvalidOperationException;

    // ------------------------------------------------------------------------------ the fake encoder

    private Func<JsonArray, JsonNode?>? Encoder(JsonNode? raw)
    {
        if (raw is null)
        {
            return null;
        }

        var hooks = raw["before"] as JsonArray;
        var spec = (JsonObject)Template(new JsonObject(((JsonObject)raw).Where(pair => pair.Key != "before")
                                                      .Select(pair => KeyValuePair.Create(pair.Key, PyNode.Copy(pair.Value)))))!;
        var calls = 0;
        return texts =>
        {
            var index = calls++;
            if (Flag(spec, "log"))
            {
                Print("## encoder call " + index.ToString(CultureInfo.InvariantCulture));
                Print(Dump(PyNode.Copy(texts)));
            }

            if (index == 0 && hooks is { Count: > 0 })
            {
                foreach (var hook in hooks)
                {
                    Step("hook", (JsonObject)hook!);
                }
            }

            if (spec.ContainsKey("raise"))
            {
                throw new StateError(PyNode.AsString(spec["raise"])!);
            }

            if (spec["results"] is JsonArray results)
            {
                return PyNode.Copy(results[Math.Min(index, results.Count - 1)]);
            }

            if (spec.ContainsKey("result"))
            {
                return PyNode.Copy(spec["result"]);
            }

            var groups = (JsonArray)spec["groups"]!;
            var weights = spec["weights"] as JsonArray ?? new JsonArray(groups.Select(_ => (JsonNode?)1L).ToArray());
            var vectors = new JsonArray();
            foreach (var text in texts)
            {
                var folded = PyRepr.CaseFold(PyNode.AsString(text)!);
                var vector = new JsonArray();
                for (var i = 0; i < Math.Min(groups.Count, weights.Count); i++)
                {
                    long count = ((JsonArray)groups[i]!).Count(word => folded.Contains(PyNode.AsString(word)!, StringComparison.Ordinal));
                    vector.Add(PyJson.Scalar(weights[i]) switch
                    {
                        double weight => JsonValue.Create(count * weight),
                        var weight => PyJson.Int(count * PyJson.AsInt(weights[i])),
                    });
                }

                if (!vector.Any(PyJson.Truthy) && spec.ContainsKey("default"))
                {
                    vector = (JsonArray)PyNode.Copy(spec["default"])!;
                }

                vectors.Add(vector);
            }

            return new JsonObject
            {
                ["model_fingerprint"] = spec.ContainsKey("fingerprint") ? PyNode.Copy(spec["fingerprint"]) : "probe-fake-model",
                ["dimensions"] = spec.ContainsKey("dimensions") ? PyNode.Copy(spec["dimensions"]) : (long)groups.Count,
                ["vectors"] = vectors,
            };
        };
    }

    // ------------------------------------------------------------------------------ calls

    private MemorySearch.MemorySearch OpenSearch(JsonObject step)
    {
        var memory = new MemoryStore(TextOf(step, "account", _account), _stateDir, readOnly: Flag(step, "read_only"));
        try
        {
            return new MemorySearch.MemorySearch(memory, Encoder(step["encoder"]), Flag(step, "rebuild_schema"));
        }
        catch
        {
            memory.Close();
            throw;
        }
    }

    private static void Close(MemorySearch.MemorySearch search) => ((MemoryStoreSource)search.Memory).Store.Close();

    private void Call(string label, JsonObject step)
    {
        var on = PyNode.AsString(step["on"]) ?? "search";
        var name = PyNode.AsString(step["call"])!;
        var args = (JsonArray)(Template(step["args"] ?? new JsonArray()) ?? new JsonArray());
        var kwargs = (JsonObject)(Template(step["kwargs"] ?? new JsonObject()) ?? new JsonObject());
        MemorySearch.MemorySearch? search = null;
        MemoryStore? store = null;
        var owned = false;
        JsonObject? state = null;
        string line;
        try
        {
            JsonNode? result;
            if (on == "module")
            {
                result = Module(name, args, kwargs);
            }
            else
            {
                if (step.ContainsKey("use"))
                {
                    search = _searches[PyNode.AsString(step["use"])!];
                    if (step.ContainsKey("encoder"))
                    {
                        search.Encoder = Encoder(step["encoder"]);
                    }

                    store = ((MemoryStoreSource)search.Memory).Store;
                }
                else if (on == "store")
                {
                    store = new MemoryStore(TextOf(step, "account", _account), _stateDir, readOnly: Flag(step, "read_only"));
                    owned = true;
                }
                else
                {
                    search = OpenSearch(step);
                    store = ((MemoryStoreSource)search.Memory).Store;
                    owned = true;
                }

                if (on == "store")
                {
                    result = StoreCall(store!, name, args, kwargs);
                }
                else if (on == "context")
                {
                    var filters = (JsonObject)(Template(step["filters"] ?? new JsonObject()) ?? new JsonObject());
                    PyCall.Bind("MemoryStore.eligible", ["self", "domain", "routine", "usage", "environment"], 1, 1, filters.Select(pair => pair.Key));
                    var context = new ConnectedContext(search!.Memory, PyNode.Get(filters, "domain"), PyNode.Get(filters, "routine"),
                                                       filters.ContainsKey("usage") ? PyArg.Of(PyNode.Copy(filters["usage"])) : PyArg.Omitted,
                                                       PyNode.Get(filters, "environment"));
                    try
                    {
                        result = ContextCall(context, name, args, kwargs);
                    }
                    finally
                    {
                        state = new JsonObject
                        {
                            ["current"] = PyNode.StrList(context.Current.Keys),
                            ["gaps"] = PyNode.Copy(context.Gaps),
                            ["edges"] = new JsonArray(context.Edges.Select(edge => PyNode.Copy(edge)).ToArray()),
                            ["conflicted_ids"] = PyNode.StrList(PyText.Sorted(context.ConflictedIds)),
                            ["truncated"] = context.Truncated,
                        };
                    }
                }
                else if (name == "__init__")
                {
                    result = "opened";
                }
                else if (name == "state")
                {
                    result = new JsonObject { ["initialized"] = search!.Initialized, ["fts"] = search.Fts, ["read_only"] = search.ReadOnly };
                }
                else
                {
                    result = SearchCall(search!, name, args, kwargs);
                }
            }

            line = Dump(result);
            if (PyNode.AsString(step["save"]) is { } save)
            {
                _saved[save] = PyNode.Copy(result);
            }
        }
        catch (Exception ex) when (IsReported(ex))
        {
            line = Error(ex);
        }
        finally
        {
            if (owned)
            {
                store!.Close();
            }
        }

        Print("## " + label + " call " + on + "." + name);
        Print(line);
        if (state is not null)
        {
            Print(Dump(state));
        }
    }

    private static InvalidDataException Unsupported(string name) => new("memory_search_probe: unsupported call " + name);

    /// <summary>CPython's binding of positional and keyword arguments (<paramref name="required"/> excludes self).</summary>
    private static PyArg[] Bind(string qualname, string[] parameters, int required, JsonArray args, JsonObject kwargs, bool method = true)
    {
        var offset = method ? 1 : 0;
        if (args.Count > parameters.Length)
        {
            var total = parameters.Length + offset;
            var minimum = required + offset;
            var given = args.Count + offset;
            var takes = minimum == total
                ? total.ToString(CultureInfo.InvariantCulture) + " positional argument" + (total == 1 ? "" : "s")
                : "from " + minimum.ToString(CultureInfo.InvariantCulture) + " to " + total.ToString(CultureInfo.InvariantCulture) + " positional arguments";
            throw new PyTypeError(qualname + "() takes " + takes + " but " + given.ToString(CultureInfo.InvariantCulture)
                                  + (given == 1 ? " was" : " were") + " given");
        }

        string[] names = method ? ["self", .. parameters] : parameters;
        PyCall.Bind(qualname, names, required + offset, args.Count + offset, kwargs.Select(pair => pair.Key));
        var result = new PyArg[parameters.Length];
        for (var i = 0; i < args.Count; i++)
        {
            result[i] = PyArg.Of(PyNode.Copy(args[i]));
        }

        foreach (var (key, value) in kwargs)
        {
            result[Array.IndexOf(parameters, key)] = PyArg.Of(PyNode.Copy(value));
        }

        return result;
    }

    private static List<string>? StringList(PyArg value) => value.Value is null
        ? null
        : PyObj.Iterate(value.Value).Select(item => PyNode.AsString(MemoryContext.Node(item)) ?? throw Unsupported("non-string id")).ToList();

    private static readonly string[] GraphParameters = ["memory_id", "max_depth", "max_nodes", "routine", "usage", "environment", "domain", "budget_chars"];

    private static JsonNode? SearchCall(MemorySearch.MemorySearch search, string name, JsonArray args, JsonObject kwargs)
    {
        PyArg[] a;
        switch (name)
        {
            case "encode":
                a = Bind("MemorySearch.encode", ["texts"], 1, args, kwargs);
                return search.Encode((JsonArray)a[0].Value!);
            case "purge_unavailable":
                Bind("MemorySearch.purge_unavailable", [], 0, args, kwargs);
                return search.PurgeUnavailable();
            case "index":
                a = Bind("MemorySearch.index", ["limit", "rebuild"], 0, args, kwargs);
                return search.Index(a[0], a[1].Or(false));
            case "health":
                Bind("MemorySearch.health", [], 0, args, kwargs);
                return search.Health();
            case "search":
                a = Bind("MemorySearch.search", ["query", "limit", "domain", "routine", "usage", "entities", "environment", "mode"], 1, args, kwargs);
                return search.Search(a[0].Value, a[1], a[2].Value, a[3].Value, a[4], a[5].Value, a[6].Value, a[7]);
            case "context":
                a = Bind("MemorySearch.context", ["query", "routine", "usage", "entities", "environment", "domain", "budget_chars", "mode",
                                                  "max_depth", "max_nodes", "work_ids"], 1, args, kwargs);
                return search.Context(a[0].Value, a[1].Value, a[2], a[3].Value, a[4].Value, a[5].Value, a[6], a[7], a[8], a[9], a[10].Value);
            case "graph":
                a = Bind("MemorySearch.graph", GraphParameters, 1, args, kwargs);
                return search.Graph(a[0].Value, a[1], a[2], a[3].Value, a[4], a[5].Value, a[6].Value, a[7]);
            case "explain":
                // explain(memory_id, **kwargs) forwards everything after the ID to graph.
                a = Bind("MemorySearch.explain", ["memory_id"], 1, args, new JsonObject(kwargs.Where(pair => pair.Key == "memory_id")
                                                                                              .Select(pair => KeyValuePair.Create(pair.Key, PyNode.Copy(pair.Value)))));
                var rest = new JsonObject(kwargs.Where(pair => pair.Key != "memory_id").Select(pair => KeyValuePair.Create(pair.Key, PyNode.Copy(pair.Value))));
                var g = Bind("MemorySearch.graph", GraphParameters, 1, new JsonArray(PyNode.Copy(a[0].Value)), rest);
                return search.Explain(g[0].Value, g[1], g[2], g[3].Value, g[4], g[5].Value, g[6].Value, g[7]);
            default:
                throw Unsupported(name);
        }
    }

    private static JsonNode? ContextCall(ConnectedContext context, string name, JsonArray args, JsonObject kwargs)
    {
        PyArg[] a;
        switch (name)
        {
            case "build":
                a = Bind("ConnectedContext.build", ["seed_ids", "entities", "work_ids", "max_depth", "max_nodes"], 1, args, kwargs);
                return context.Build(StringList(a[0])!, StringList(a[1]), StringList(a[2]), a[3], a[4]);
            case "constraint_guard":
                Bind("ConnectedContext.constraint_guard", [], 0, args, kwargs);
                return context.ConstraintGuard();
            case "_diagnose":
                a = Bind("ConnectedContext._diagnose", ["memory_id", "origin"], 1, args, kwargs);
                return context.Diagnose(PyNode.AsString(a[0].Value)!, PyNode.AsString(a[1].Value));
            case "_successors":
                a = Bind("ConnectedContext._successors", ["memory_id"], 1, args, kwargs);
                return PyNode.StrList(context.Successors(PyNode.AsString(a[0].Value)!));
            case "_work":
                a = Bind("ConnectedContext._work", ["work_id"], 1, args, kwargs);
                return context.Work(PyNode.AsString(a[0].Value)!);
            default:
                throw Unsupported(name);
        }
    }

    private static JsonNode? StoreCall(MemoryStore memory, string name, JsonArray args, JsonObject kwargs)
    {
        PyArg[] a;
        switch (name)
        {
            case "record_id":
                a = Bind("MemoryStore.record_id", ["domain", "key"], 2, args, kwargs);
                return memory.RecordId(a[0].Value, a[1].Value);
            case "show":
                a = Bind("MemoryStore.show", ["memory_id"], 1, args, kwargs);
                return memory.Show(a[0].Value);
            case "list":
            case "list_ids":
                a = Bind("MemoryStore.list", ["domain", "status"], 0, args, kwargs);
                var rows = memory.List(a[0].Value, a[1].Value);
                return name == "list" ? rows : new JsonArray(rows.Select(row => PyNode.Copy(row!["id"])).ToArray());
            case "put":
                a = Bind("MemoryStore.put", ["key", "data", "status", "revision", "evidence"], 2, args, kwargs);
                return memory.Put(a[0].Value, a[1].Value, a[2], a[3].Value, a[4].Value);
            case "revise":
                a = Bind("MemoryStore.revise", ["memory_id", "data", "status", "revision", "evidence"], 4, args, kwargs);
                return memory.Revise(a[0].Value, a[1].Value, a[2].Value, a[3].Value, a[4].Value);
            case "link":
                a = Bind("MemoryStore.link", ["source_id", "target_id", "relation", "revision", "evidence", "data", "target_revision"], 4, args, kwargs);
                return memory.Link(a[0].Value, a[1].Value, a[2].Value, a[3].Value, a[4].Value, a[5].Value, a[6].Value);
            case "unlink":
                a = Bind("MemoryStore.unlink", ["source_id", "target_id", "relation", "revision", "evidence"], 5, args, kwargs);
                return memory.Unlink(a[0].Value, a[1].Value, a[2].Value, a[3].Value, a[4].Value);
            case "forget":
                a = Bind("MemoryStore.forget", ["memory_id", "revision", "evidence"], 2, args, kwargs);
                return memory.Forget(a[0].Value, a[1].Value, a[2].Value);
            case "links":
                a = Bind("MemoryStore.links", ["memory_id"], 1, args, kwargs);
                return memory.Links(a[0].Value);
            case "active_links":
                a = Bind("MemoryStore.active_links", ["memory_id"], 1, args, kwargs);
                return memory.ActiveLinks(a[0].Value);
            case "eligible":
                a = Bind("MemoryStore.eligible", ["domain", "routine", "usage", "environment", "include_stale"], 0, args, kwargs);
                return memory.Eligible(a[0].Value, a[1].Value, a[2], a[3].Value, a[4]);
            case "pending_jobs":
                a = Bind("MemoryStore.pending_jobs", ["limit"], 0, args, kwargs);
                return memory.PendingJobs(a[0]);
            case "blocked_jobs":
                a = Bind("MemoryStore.blocked_jobs", ["limit"], 0, args, kwargs);
                return memory.BlockedJobs(a[0]);
            case "health":
                Bind("MemoryStore.health", [], 0, args, kwargs);
                return memory.Health();
            default:
                throw Unsupported(name);
        }
    }

    private static JsonNode? Module(string name, JsonArray args, JsonObject kwargs)
    {
        PyArg[] a;
        switch (name)
        {
            case "representation":
                a = Bind(name, ["record"], 1, args, kwargs, method: false);
                return MemorySearch.MemorySearch.Representation(a[0].Value);
            case "text_hash":
                a = Bind(name, ["value"], 1, args, kwargs, method: false);
                return MemorySearch.MemorySearch.TextHash(PyNode.AsString(a[0].Value)!);
            case "vector_bytes":
                a = Bind(name, ["vector", "dimensions"], 2, args, kwargs, method: false);
                return Convert.ToHexStringLower(MemorySearch.MemorySearch.VectorBytes(a[0].Value, a[1].Value));
            case "decoded_vector":
                a = Bind(name, ["blob", "dimensions"], 2, args, kwargs, method: false);
                var values = MemorySearch.MemorySearch.DecodedVector(Convert.FromHexString(PyNode.AsString(a[0].Value)!), a[1].Value);
                return new JsonArray(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
            case "identity_key":
                a = Bind(name, ["identity"], 1, args, kwargs, method: false);
                return MemoryContext.IdentityKey(a[0].Value);
            case "entity_keys":
                a = Bind(name, ["record"], 1, args, kwargs, method: false);
                return PyNode.StrList(PyText.Sorted(MemoryContext.EntityKeys(a[0].Value)));
            case "applicable":
                a = Bind(name, ["data"], 1, args, kwargs, method: false);
                return MemoryContext.Applicable(a[0].Value);
            case "bounds":
                a = Bind(name, ["max_depth", "max_nodes"], 2, args, kwargs, method: false);
                MemoryContext.Bounds(a[0].Value, a[1].Value);
                return null;
            case "memory_entry":
                a = Bind(name, ["row", "reasons", "mandatory"], 2, args, kwargs, method: false);
                return MemoryContext.MemoryEntry(a[0].Value, (JsonArray)a[1].Value!, a[2]);
            case "mandatory_constraint":
                a = Bind(name, ["row"], 1, args, kwargs, method: false);
                return MemoryContext.MandatoryConstraintValue(a[0].Value);
            case "section":
                a = Bind(name, ["entry"], 1, args, kwargs, method: false);
                return MemoryContext.Section(a[0].Value);
            case "sized":
                a = Bind(name, ["result"], 1, args, kwargs, method: false);
                return MemoryContext.Sized((JsonObject)a[0].Value!);
            case "budget_packet":
                a = Bind(name, ["result", "budget_chars", "graph"], 2, args, kwargs, method: false);
                return MemoryContext.BudgetPacket((JsonObject)a[0].Value!, a[1].Value, PyJson.Truthy(a[2].Or(false)));
            default:
                throw Unsupported(name);
        }
    }

    // ------------------------------------------------------------------------------ other steps

    private void Hold(string label, JsonObject step)
    {
        var verb = HoldVerbs.First(step.ContainsKey);
        var name = PyNode.AsString(step[verb])!;
        string line = "ok";
        try
        {
            switch (verb)
            {
                case "open":
                    _searches[name] = OpenSearch(step);
                    break;
                case "close":
                    _searches.Remove(name, out var search);
                    Close(search!);
                    break;
                case "begin":
                    _searches[name].Conn.Execute("BEGIN");
                    break;
                case "rollback":
                    line = "in_transaction=" + (_searches[name].Conn.InTransaction ? "True" : "False");
                    _searches[name].Conn.Rollback();
                    break;
                default:
                    _searches[name].Encoder = Encoder(step["encoder"]);
                    break;
            }
        }
        catch (Exception ex) when (IsReported(ex))
        {
            line = Error(ex);
        }

        Print("## " + label + " " + verb + " " + name);
        Print(line);
    }

    private static JsonNode? SqlValue(object? value) => value is byte[] bytes ? Convert.ToHexStringLower(bytes) : PyNode.FromSql(value);

    private void Sql(string label, JsonObject step)
    {
        var account = TextOf(step, "account", _account);
        PyConnection? connection = null;
        string line;
        try
        {
            connection = StateStore.Connect(account, _stateDir);
            var parameters = ((JsonArray?)Template(step["params"] ?? new JsonArray()))!.Select(item => (object?)item).ToArray();
            var cursor = connection.Execute(Text(PyNode.AsString(step["sql"])!), parameters);
            var rows = new JsonArray(cursor.FetchAll().Select(row => (JsonNode?)new JsonArray(row.Select(SqlValue).ToArray())).ToArray());
            connection.Commit();
            var result = new JsonObject { ["rowcount"] = cursor.RowCount, ["rows"] = rows };
            line = Dump(result);
            if (PyNode.AsString(step["save"]) is { } save)
            {
                _saved[save] = result;
            }
        }
        catch (Exception ex) when (IsReported(ex))
        {
            line = Error(ex);
        }
        finally
        {
            connection?.Close();
        }

        Print("## " + label + " sql");
        Print(line);
    }
}

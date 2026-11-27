using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Memory;
using Margo.Cloud.State.MemoryLearning;
using Margo.Cloud.State.Work;

namespace Margo.Cloud.State.Probes;

/// <summary>
/// C# twin of <c>cloud/contracts/state/probes/memory_learning_probe.py</c>. Not a Margo command and not offered to the
/// hosted model: <c>tools/state_parity.py</c> runs both on the same program so <see cref="MemoryLearning"/> and
/// <see cref="MemoryConsolidation"/> (private helpers included) are judged byte for byte against CPython, with
/// <see cref="MemoryStore"/>, <see cref="Ledger"/> and <see cref="Productivity"/> calls to build fixtures. Keep the two
/// files in step.
/// </summary>
public sealed partial class MemoryLearningProbeScript : IStateProbeScript
{
    private static readonly string[] HoldVerbs = ["open", "close", "begin", "commit", "rollback"];
    private static readonly string[] FileVerbs = ["write", "mkdir", "chmod", "symlink", "remove"];
    private static readonly HashSet<string> LearningWithMemory = new(StringComparer.Ordinal)
    {
        "_environment", "_prior", "_normalise_proposal", "capabilities", "_observation", "validation", "_feedback",
        "lesson_plan", "activate_lesson", "configure_trend", "trend", "preferences_plan", "import_preferences",
    };

    private static readonly HashSet<string> ConsolidationWithMemory = new(StringComparer.Ordinal)
    {
        "_stored", "_sources", "_proposal", "consolidation_plan", "surface_consolidation",
    };

    private readonly Dictionary<string, JsonNode?> _saved = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MemoryStore> _stores = new(StringComparer.Ordinal);
    private string _account = "";
    private string _stateDir = "";
    private string _directory = "";

    public string ScriptName => "memory_learning_probe.py";

    public int Run(IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr) =>
        StateScripts.RunGuarded(() => new MemoryLearningProbeScript().Main(args, stdin, stdout, stderr), stderr);

    [GeneratedRegex(@"\A\$\{([^}]+)\}\z")]
    private static partial Regex Whole();

    [GeneratedRegex(@"\$\{([^}]+)\}")]
    private static partial Regex Part();

    [GeneratedRegex(@"\Anow(?:([+-])([0-9]+)([smhd]))?\z")]
    private static partial Regex Clock();

    private int Main(IReadOnlyList<string> argv, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var parser = new PyArgumentParser("memory_learning_probe.py");
        parser.AddArgument("--account", required: true);
        parser.AddArgument("--state-dir", required: true);
        parser.AddArgument("--dir", required: true);
        var args = parser.ParseArgs(argv, stdout, stderr);
        if (PyJson.Loads(PyIO.ReadText(stdin)) is not JsonArray program)
        {
            StateScripts.PrintError(stderr, "stdin must be a JSON list of steps");
            return 1;
        }

        _account = args.GetString("account")!;
        _stateDir = args.GetString("state_dir")!;
        _directory = args.GetString("dir")!;
        if (!PyPath.IsDir(_directory))
        {
            PosixNative.Mkdir(_directory, Convert.ToUInt32("700", 8));
        }

        try
        {
            for (var index = 0; index < program.Count; index++)
            {
                var step = program[index]!.AsObject();
                if (step.ContainsKey("call"))
                {
                    Call(index, step, stdout);
                }
                else if (HoldVerbs.Any(step.ContainsKey))
                {
                    Hold(index, step, stdout);
                }
                else if (step.ContainsKey("sql") || step.ContainsKey("script"))
                {
                    Sql(index, step, stdout);
                }
                else if (step.ContainsKey("const"))
                {
                    Const(index, step, stdout);
                }
                else if (FileVerbs.Any(step.ContainsKey))
                {
                    Files(index, step, stdout);
                }
                else
                {
                    StateScripts.PrintError(stderr, "unknown step " + index.ToString(CultureInfo.InvariantCulture));
                    return 1;
                }
            }
        }
        finally
        {
            foreach (var store in _stores.Values)
            {
                store.Close();
            }
        }

        return 0;
    }

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
            case "dir":
                return _directory;
            case "account":
                return _account;
            case "state_dir":
                return _stateDir;
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

    private string PathOf(JsonNode? name) => PyPath.Join(_directory, Text(PyNode.AsString(name)!));

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

    // ------------------------------------------------------------------------------ calls

    private MemoryStore Store(JsonObject step) => new(
        TextOf(step, "account", _account), _stateDir, readOnly: step["read_only"] is { } readOnly && PyJson.Truthy(readOnly));

    private void Call(int index, JsonObject step, TextWriter stdout)
    {
        var on = PyNode.AsString(step["on"])!;
        var name = PyNode.AsString(step["call"])!;
        var args = (JsonArray)(Template(step["args"] ?? new JsonArray()) ?? new JsonArray());
        var kwargs = (JsonObject)(Template(step["kwargs"] ?? new JsonObject()) ?? new JsonObject());
        MemoryStore? memory = null;
        Ledger? ledger = null;
        var owned = false;
        string line;
        try
        {
            JsonNode? result;
            if (on is "ledger" or "products")
            {
                ledger = new Ledger(TextOf(step, "account", _account), _stateDir);
                result = on == "ledger" ? LedgerCall(ledger, name, args, kwargs) : ProductsCall(new Productivity(ledger), name, args, kwargs);
            }
            else
            {
                if (on == "store" || (on == "learning" && LearningWithMemory.Contains(name))
                    || (on == "consolidation" && ConsolidationWithMemory.Contains(name)))
                {
                    if (step.ContainsKey("use"))
                    {
                        memory = _stores[PyNode.AsString(step["use"])!];
                    }
                    else
                    {
                        memory = Store(step);
                        owned = true;
                    }
                }

                result = on switch
                {
                    "store" => StoreCall(memory!, name, args, kwargs),
                    "learning" => LearningCall(memory, name, args, kwargs),
                    "consolidation" => ConsolidationCall(memory, name, args, kwargs),
                    _ => throw Unsupported(on),
                };
            }

            line = PyJson.Dumps(result, ensureAscii: false);
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
                memory!.Close();
            }

            ledger?.Close();
        }

        PyIO.Print(stdout, "## " + index.ToString(CultureInfo.InvariantCulture) + " call " + on + "." + name);
        PyIO.Print(stdout, line);
    }

    private static InvalidDataException Unsupported(string name) => new("memory_learning_probe: unsupported call " + name);

    /// <summary>Python's argument binding for the probe's own calls (positional, then keywords).</summary>
    private static PyArg[] Bind(string name, string[] parameters, JsonArray args, JsonObject kwargs)
    {
        if (args.Count > parameters.Length)
        {
            throw new PyTypeError(name + "() takes " + parameters.Length.ToString(CultureInfo.InvariantCulture) + " positional arguments");
        }

        var result = new PyArg[parameters.Length];
        for (var i = 0; i < args.Count; i++)
        {
            result[i] = PyArg.Of(PyNode.Copy(args[i]));
        }

        foreach (var (key, value) in kwargs)
        {
            var position = Array.IndexOf(parameters, key);
            if (position < 0)
            {
                throw new PyTypeError(name + "() got an unexpected keyword argument '" + key + "'");
            }

            if (result[position].IsGiven)
            {
                throw new PyTypeError(name + "() got multiple values for argument '" + key + "'");
            }

            result[position] = PyArg.Of(PyNode.Copy(value));
        }

        return result;
    }

    private static string PathText(PyArg value) => PyNode.AsString(value.Value) ?? throw new InvalidDataException("memory_learning_probe: a path must be a string");

    private static string? OptionalPath(PyArg value) => value.IsGiven && value.Value is not null ? PathText(value) : null;

    private static int IntOr(PyArg value, int fallback) => value.IsGiven ? (int)PyJson.AsInt(value.Value) : fallback;

    private static JsonArray Pair(string first, JsonNode? second) => new(first, second);

    private static JsonArray Sections(List<(string Title, string Text)> sections) =>
        new(sections.Select(section => (JsonNode?)new JsonArray(section.Title, section.Text)).ToArray());

    private static JsonNode? LearningCall(MemoryStore? memory, string name, JsonArray args, JsonObject kwargs)
    {
        PyArg[] a;
        switch (name)
        {
            case "_digest":
                a = Bind(name, ["value"], args, kwargs);
                return MemoryLearning.MemoryLearning.Digest(a[0].Value);
            case "_text":
                a = Bind(name, ["value", "field"], args, kwargs);
                return MemoryLearning.MemoryLearning.Text(a[0].Value, PyNode.AsString(a[1].Value)!);
            case "_strings":
                a = Bind(name, ["value", "field", "allow_empty"], args, kwargs);
                return MemoryLearning.MemoryLearning.Strings(a[0].Value, PyNode.AsString(a[1].Value)!, PyJson.Truthy(a[2].Or(false)));
            case "_time":
                a = Bind(name, ["value"], args, kwargs);
                return MemoryLearning.MemoryLearning.Time(a[0].Value).Isoformat();
            case "_environment":
                a = Bind(name, ["value"], args, kwargs);
                return MemoryLearning.MemoryLearning.Environment(memory!, a[0].Value);
            case "_file":
            {
                a = Bind(name, ["path", "expected"], args, kwargs);
                var (payload, source) = MemoryLearning.MemoryLearning.File(a[0].Value, a[1].Value);
                return Pair(Convert.ToHexStringLower(payload), source);
            }

            case "_json_file":
            {
                a = Bind(name, ["path", "expected"], args, kwargs);
                var (data, source) = MemoryLearning.MemoryLearning.JsonFile(a[0].Value, a[1].Value);
                return new JsonArray(data, source);
            }

            case "_data":
                a = Bind(name, ["record"], args, kwargs);
                return MemoryLearning.MemoryLearning.Data((JsonObject)a[0].Value!);
            case "_refs":
                a = Bind(name, ["values"], args, kwargs);
                return MemoryLearning.MemoryLearning.Refs(PyNode.Iter(a[0].Value));
            case "_prior":
                a = Bind(name, ["key", "domain"], args, kwargs);
                return MemoryLearning.MemoryLearning.Prior(memory!, a[0].Value, PyNode.AsString(a[1].Or("agent"))!);
            case "_conditional":
                a = Bind(name, ["prior", "revision"], args, kwargs);
                MemoryLearning.MemoryLearning.Conditional(a[0].Value as JsonObject, a[1].Value);
                return null;
            case "_normalise_proposal":
                a = Bind(name, ["data"], args, kwargs);
                return MemoryLearning.MemoryLearning.NormaliseProposal(memory!, a[0].Value);
            case "_validation_scope":
                a = Bind(name, ["metadata"], args, kwargs);
                return MemoryLearning.MemoryLearning.ValidationScope(a[0].Value);
            case "capabilities":
                a = Bind(name, ["skills_directory", "environment", "tools"], args, kwargs);
                return MemoryLearning.MemoryLearning.Capabilities(memory!, OptionalPath(a[0]), a[1].Value, OptionalPath(a[2]));
            case "classify_error":
                a = Bind(name, ["code", "confirmed_defect"], args, kwargs);
                return MemoryLearning.MemoryLearning.ClassifyError(a[0].Value, PyJson.Truthy(a[1].Or(false)));
            case "_input_contract":
                a = Bind(name, ["value", "schema", "depth"], args, kwargs);
                return MemoryLearning.MemoryLearning.InputContract(a[0].Value, a[1].Value, IntOr(a[2], 0));
            case "_observation":
                a = Bind(name, ["capability", "evidence"], args, kwargs);
                return MemoryLearning.MemoryLearning.Observation(memory!, (JsonObject)a[0].Value!, a[1].Value);
            case "validation":
                a = Bind(name, ["capability_id", "revision", "evidence"], args, kwargs);
                return MemoryLearning.MemoryLearning.Validation(memory!, a[0].Value, a[1].Value, a[2].Value);
            case "_feedback":
                a = Bind(name, ["reference"], args, kwargs);
                return MemoryLearning.MemoryLearning.Feedback(memory!, a[0].Value);
            case "lesson_plan":
                a = Bind(name, ["key", "data", "revision"], args, kwargs);
                return MemoryLearning.MemoryLearning.LessonPlan(memory!, a[0].Value, a[1].Value, a[2].Value);
            case "activate_lesson":
                a = Bind(name, ["memory_id", "revision", "evidence"], args, kwargs);
                return MemoryLearning.MemoryLearning.ActivateLesson(memory!, a[0].Value, a[1].Value, a[2].Value);
            case "_definition":
                a = Bind(name, ["value"], args, kwargs);
                return MemoryLearning.MemoryLearning.Definition(a[0].Value);
            case "configure_trend":
                a = Bind(name, ["name", "definition", "revision", "evidence"], args, kwargs);
                return MemoryLearning.MemoryLearning.ConfigureTrend(memory!, a[0].Value, a[1].Value, a[2].Value, a[3].Value);
            case "trend":
                a = Bind(name, ["key", "data", "revision"], args, kwargs);
                return MemoryLearning.MemoryLearning.Trend(memory!, a[0].Value, a[1].Value, a[2].Value);
            case "_preference_sections":
                a = Bind(name, ["content"], args, kwargs);
                return Sections(MemoryLearning.MemoryLearning.ParsePreferenceSections(PyNode.AsString(a[0].Value)!));
            case "_preference_file":
            {
                a = Bind(name, ["path"], args, kwargs);
                var (source, content, sourceRef) = MemoryLearning.MemoryLearning.PreferenceFile(PathText(a[0]));
                return new JsonArray(source, content, sourceRef);
            }

            case "preference_sections":
                a = Bind(name, ["path"], args, kwargs);
                return Sections(MemoryLearning.MemoryLearning.PreferenceSections(PathText(a[0])));
            case "preferences_plan":
                a = Bind(name, ["path"], args, kwargs);
                return MemoryLearning.MemoryLearning.PreferencesPlan(memory!, PathText(a[0]));
            case "import_preferences":
                a = Bind(name, ["path", "evidence"], args, kwargs);
                return MemoryLearning.MemoryLearning.ImportPreferences(memory!, PathText(a[0]), a[1].Value);
            default:
                throw Unsupported(name);
        }
    }

    private static JsonNode? ConsolidationCall(MemoryStore? memory, string name, JsonArray args, JsonObject kwargs)
    {
        PyArg[] a;
        switch (name)
        {
            case "_stored":
                a = Bind(name, ["key"], args, kwargs);
                return MemoryConsolidation.Stored(memory!, PyNode.AsString(a[0].Value)!);
            case "_sources":
            {
                a = Bind(name, ["record", "seen", "budget", "depth"], args, kwargs);
                var seen = a[1].Value is JsonArray list ? list.Select(item => PyNode.AsString(item)!).ToList() : null;
                long[]? budget = a[2].Value is JsonArray count ? [(long)PyJson.AsInt(count[0])] : null;
                return MemoryConsolidation.Sources(memory!, a[0].Value, seen, budget, IntOr(a[3], 0));
            }

            case "_current_sources":
                a = Bind(name, ["snapshot"], args, kwargs);
                return MemoryConsolidation.CurrentSources(a[0].Value);
            case "_trend_evidence":
                a = Bind(name, ["record"], args, kwargs);
                return MemoryConsolidation.TrendEvidence(a[0].Value);
            case "_proposal":
                a = Bind(name, ["reason", "records", "environment"], args, kwargs);
                return MemoryConsolidation.Proposal(memory!, PyNode.AsString(a[0].Value)!,
                                                    ((JsonArray)a[1].Value!).Select(row => (JsonObject)row!).ToList(), a[2].Value);
            case "consolidation_plan":
                a = Bind(name, ["limit", "environment", "after", "scan_limit"], args, kwargs);
                return MemoryConsolidation.ConsolidationPlan(memory!, a[0], a[1].Value, a[2].Value, a[3]);
            case "surface_consolidation":
                a = Bind(name, ["plan"], args, kwargs);
                return MemoryConsolidation.SurfaceConsolidation(memory!, a[0].Value);
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
                a = Bind(name, ["domain", "key"], args, kwargs);
                return memory.RecordId(a[0].Value, a[1].Value);
            case "show":
                a = Bind(name, ["memory_id"], args, kwargs);
                return memory.Show(a[0].Value);
            case "list":
                a = Bind(name, ["domain", "status"], args, kwargs);
                return memory.List(a[0].Value, a[1].Value);
            case "put":
                a = Bind(name, ["key", "data", "status", "revision", "evidence"], args, kwargs);
                return memory.Put(a[0].Value, a[1].Value, a[2], a[3].Value, a[4].Value);
            case "revise":
                a = Bind(name, ["memory_id", "data", "status", "revision", "evidence"], args, kwargs);
                return memory.Revise(a[0].Value, a[1].Value, a[2].Value, a[3].Value, a[4].Value);
            case "history":
                a = Bind(name, ["memory_id", "limit"], args, kwargs);
                return memory.History(a[0].Value, a[1]);
            case "links":
                a = Bind(name, ["memory_id"], args, kwargs);
                return memory.Links(a[0].Value);
            case "forget":
                a = Bind(name, ["memory_id", "revision", "evidence"], args, kwargs);
                return memory.Forget(a[0].Value, a[1].Value, a[2].Value);
            case "eligible":
                a = Bind(name, ["domain", "routine", "usage", "environment", "include_stale"], args, kwargs);
                return memory.Eligible(a[0].Value, a[1].Value, a[2], a[3].Value, a[4]);
            case "_environment_subset":
                a = Bind(name, ["required", "current"], args, kwargs);
                return memory.EnvironmentSubset(a[0].Value, a[1].Value);
            case "_work_sources_current":
                a = Bind(name, ["data"], args, kwargs);
                return memory.WorkSourcesCurrent(a[0].Value);
            default:
                throw Unsupported(name);
        }
    }

    private static JsonObject LedgerCall(Ledger ledger, string name, JsonArray args, JsonObject kwargs)
    {
        PyArg[] a;
        switch (name)
        {
            case "source":
                a = Bind(name, ["family", "scope", "external_id", "revision", "evidence", "web_link", "sensitivity", "observed_at"], args, kwargs);
                return ledger.Source(a[0].Value, a[1].Value, a[2].Value, a[3].Value, a[4].Value, a[5].Value, a[6].Or("unknown"), a[7].Value);
            case "ingest":
                a = Bind(name, ["data", "claim_key"], args, kwargs);
                return ledger.Ingest(a[0].Value, a[1].Value);
            case "propose":
                a = Bind(name, ["data"], args, kwargs);
                return ledger.Propose(a[0].Value);
            case "approve":
                a = Bind(name, ["action_id", "revision", "action_hash", "evidence", "expires_at"], args, kwargs);
                return ledger.Approve(a[0].Value, a[1].Value, a[2].Value, a[3].Value, a[4].Value);
            case "begin":
                a = Bind(name, ["action_id", "revision", "fresh"], args, kwargs);
                return ledger.Begin(a[0].Value, a[1].Value, a[2].Value);
            case "finish":
                a = Bind(name, ["attempt_id", "state", "receipt", "reconcile"], args, kwargs);
                return ledger.Finish(a[0].Value, a[1].Value, a[2].Value, PyJson.Truthy(a[3].Or(false)));
            default:
                throw Unsupported(name);
        }
    }

    private static JsonNode? ProductsCall(Productivity products, string name, JsonArray args, JsonObject kwargs)
    {
        PyArg[] a;
        switch (name)
        {
            case "record_id":
                a = Bind(name, ["kind", "identity"], args, kwargs);
                return products.RecordId(PyNode.AsString(a[0].Value)!, a[1].Value);
            case "put":
                a = Bind(name, ["kind", "identity", "data", "state", "revision", "evidence"], args, kwargs);
                return products.Put(PyNode.AsString(a[0].Value)!, a[1].Value, a[2].Value, a[3].Value, a[4].Value, a[5].Value);
            default:
                throw Unsupported(name);
        }
    }

    // ------------------------------------------------------------------------------ other steps

    private void Hold(int index, JsonObject step, TextWriter stdout)
    {
        var verb = HoldVerbs.First(step.ContainsKey);
        var name = PyNode.AsString(step[verb])!;
        string line;
        try
        {
            switch (verb)
            {
                case "open":
                    _stores[name] = Store(step);
                    break;
                case "close":
                    _stores.Remove(name, out var store);
                    store!.Close();
                    break;
                case "begin":
                    _stores[name].Conn.Execute("BEGIN IMMEDIATE");
                    break;
                case "commit":
                    _stores[name].Conn.Commit();
                    break;
                default:
                    _stores[name].Conn.Rollback();
                    break;
            }

            line = "ok";
        }
        catch (Exception ex) when (IsReported(ex))
        {
            line = Error(ex);
        }

        PyIO.Print(stdout, "## " + index.ToString(CultureInfo.InvariantCulture) + " " + verb + " " + name);
        PyIO.Print(stdout, line);
    }

    private void Sql(int index, JsonObject step, TextWriter stdout)
    {
        var account = TextOf(step, "account", _account);
        PyConnection? connection = null;
        string line;
        try
        {
            connection = StateStore.Connect(account, _stateDir);
            if (step.ContainsKey("script"))
            {
                connection.ExecuteScript(Text(PyNode.AsString(step["script"])!));
                line = "ok";
            }
            else
            {
                var parameters = ((JsonArray?)Template(step["params"] ?? new JsonArray()))!.Select(item => (object?)item).ToArray();
                var cursor = connection.Execute(Text(PyNode.AsString(step["sql"])!), parameters);
                var rows = new JsonArray(cursor.FetchAll().Select(row => (JsonNode?)new JsonArray(row.Select(PyNode.FromSql).ToArray())).ToArray());
                connection.Commit();
                line = PyJson.Dumps(new JsonObject { ["rowcount"] = cursor.RowCount, ["rows"] = rows }, ensureAscii: false);
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

        PyIO.Print(stdout, "## " + index.ToString(CultureInfo.InvariantCulture) + " " + (step.ContainsKey("script") ? "script" : "sql"));
        PyIO.Print(stdout, line);
    }

    private static void Const(int index, JsonObject step, TextWriter stdout)
    {
        var name = PyNode.AsString(step["const"])!;
        JsonNode? value = name switch
        {
            "MAX_OBSERVATIONS" => MemoryLearning.MemoryLearning.MaxObservations,
            "MAX_FILE_BYTES" => MemoryLearning.MemoryLearning.MaxFileBytes,
            "TREND_TYPES" => PyNode.StrList(PyText.Sorted(MemoryLearning.MemoryLearning.TrendTypes)),
            "DEFAULT_TREND" => MemoryLearning.MemoryLearning.DefaultTrend,
            "__doc__" => MemoryLearning.MemoryLearning.Doc,
            _ => throw Unsupported(name),
        };
        PyIO.Print(stdout, "## " + index.ToString(CultureInfo.InvariantCulture) + " const " + name);
        PyIO.Print(stdout, PyJson.Dumps(value, ensureAscii: false));
    }

    private void Files(int index, JsonObject step, TextWriter stdout)
    {
        string label;
        string line;
        try
        {
            if (step.ContainsKey("write"))
            {
                var path = PathOf(step["write"]);
                byte[] bytes = step.ContainsKey("json")
                    ? PyIO.EncodeUtf8(PyJson.Dumps(Template(step["json"]), ensureAscii: false))
                    : step.ContainsKey("hex")
                        ? Convert.FromHexString(PyNode.AsString(step["hex"])!)
                        : PyIO.EncodeUtf8(string.Concat(Enumerable.Repeat(Text(PyNode.AsString(step["text"])!),
                                                                           step["repeat"] is { } repeat ? (int)PyJson.AsInt(repeat) : 1)));
                WriteFile(path, bytes);
                if (PyNode.AsString(step["mode"]) is { } mode)
                {
                    Chmod(path, mode);
                }

                label = "write " + PyNode.AsString(step["write"]);
                var digest = PyIds.Sha256Hex(PyIO.ReadBytes(path));
                if (PyNode.AsString(step["save"]) is { } save)
                {
                    _saved[save] = digest;
                }

                line = "sha256 " + digest;
            }
            else if (step.ContainsKey("mkdir"))
            {
                var path = PathOf(step["mkdir"]);
                MakeDirectories(path);
                label = "mkdir " + PyNode.AsString(step["mkdir"]);
                line = "ok";
            }
            else if (step.ContainsKey("chmod"))
            {
                Chmod(PathOf(step["chmod"]), PyNode.AsString(step["mode"])!);
                label = "chmod " + PyNode.AsString(step["chmod"]);
                line = "ok";
            }
            else if (step.ContainsKey("symlink"))
            {
                File.CreateSymbolicLink(PathOf(step["symlink"]), Text(PyNode.AsString(step["target"])!));
                label = "symlink " + PyNode.AsString(step["symlink"]);
                line = "ok";
            }
            else
            {
                PosixNative.Unlink(PathOf(step["remove"]));
                label = "remove " + PyNode.AsString(step["remove"]);
                line = "ok";
            }
        }
        catch (PyOSError ex)
        {
            label = "file";
            line = Error(ex);
        }

        PyIO.Print(stdout, "## " + index.ToString(CultureInfo.InvariantCulture) + " " + label);
        PyIO.Print(stdout, line);
    }

    /// <summary><c>Path.mkdir(mode=0o700, parents=True)</c>: missing parents get the default mode 0777 (less the umask).</summary>
    private static void MakeDirectories(string path, string mode = "700")
    {
        var parent = PyPath.Parent(path);
        if (parent != path && !PyPath.Exists(parent))
        {
            MakeDirectories(parent, "777");
        }

        PosixNative.Mkdir(path, Convert.ToUInt32(mode, 8));
    }

    /// <summary><c>os.chmod(path, int(mode, 8))</c>.</summary>
    private static void Chmod(string path, string mode)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Margo Cloud private state requires Linux file APIs.");
        }

        File.SetUnixFileMode(path, (UnixFileMode)Convert.ToInt32(mode, 8));
    }

    /// <summary><c>Path.write_bytes</c>/<c>write_text</c>: create or truncate (mode 0666 &amp; ~umask), then write.</summary>
    private static void WriteFile(string path, byte[] bytes)
    {
        var fd = PosixNative.Open(path, PosixNative.OWrOnly | PosixNative.OCreat | 0x200, Convert.ToUInt32("666", 8));
        try
        {
            PosixNative.WriteAll(fd, bytes);
        }
        finally
        {
            PosixNative.Close(fd);
        }
    }
}

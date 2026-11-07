using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Memory;
using Margo.Cloud.State.Work;

namespace Margo.Cloud.State.Probes;

/// <summary>
/// C# twin of <c>cloud/contracts/state/probes/memory_probe.py</c>. Not a Margo command and not offered to the hosted
/// model (the hosted tool only runs classified scripts): <c>tools/state_parity.py</c> runs both on the same program so
/// the memory libraries (<see cref="MemoryStore"/>, <see cref="MemoryGovernance"/>, <see cref="MemoryEncoder"/>'s status
/// functions and <see cref="DreamSources"/>) are judged byte for byte against CPython. Keep the two files in step.
/// </summary>
public sealed partial class MemoryProbeScript : IStateProbeScript
{
    private static readonly string[] HoldVerbs = ["open", "close", "begin", "commit", "rollback"];
    private static readonly string[] FileVerbs = ["write", "mkdir", "chmod", "symlink", "read"];

    private readonly Dictionary<string, JsonNode?> _saved = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MemoryStore> _stores = new(StringComparer.Ordinal);
    private string _account = "";
    private string _stateDir = "";
    private string _directory = "";

    public string ScriptName => "memory_probe.py";

    public int Run(IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr) =>
        StateScripts.RunGuarded(() => new MemoryProbeScript().Main(args, stdin, stdout, stderr), stderr);

    [GeneratedRegex(@"\A\$\{([^}]+)\}\z")]
    private static partial Regex Whole();

    [GeneratedRegex(@"\$\{([^}]+)\}")]
    private static partial Regex Part();

    [GeneratedRegex(@"\Anow(?:([+-])([0-9]+)([smhd]))?\z")]
    private static partial Regex Clock();

    [GeneratedRegex(@"\.memory-export-[a-z0-9_]{8}")]
    private static partial Regex Temporary();

    private int Main(IReadOnlyList<string> argv, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var parser = new PyArgumentParser("memory_probe.py");
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
        return "!" + type + ": " + Temporary().Replace(ex.Message, ".memory-export-XXXXXXXX");
    }

    private static bool IsReported(Exception ex) => ex is PyException or InvalidOperationException;

    // ------------------------------------------------------------------------------ calls

    private MemoryStore Store(JsonObject step) => new(
        TextOf(step, "account", _account), TextOf(step, "state_dir", _stateDir),
        migrate: step["migrate"] is { } migrate && PyJson.Truthy(migrate), readOnly: step["read_only"] is { } readOnly && PyJson.Truthy(readOnly));

    private void Call(int index, JsonObject step, TextWriter stdout)
    {
        var on = PyNode.AsString(step["on"]) ?? "store";
        var name = PyNode.AsString(step["call"])!;
        var args = (JsonArray)(Template(step["args"] ?? new JsonArray()) ?? new JsonArray());
        var kwargs = (JsonObject)(Template(step["kwargs"] ?? new JsonObject()) ?? new JsonObject());
        MemoryStore? memory = null;
        var owned = false;
        string line;
        try
        {
            JsonNode? result;
            if (on == "encoder")
            {
                result = Encoder(name, args, kwargs);
            }
            else if (on == "module")
            {
                result = Module(name, args, kwargs);
            }
            else if (on == "dream" && name == "source_keys")
            {
                var a = Bind(name, ["ref"], args, kwargs);
                var (source, locator) = DreamSources.SourceKeys(Dict(a[0].Value));
                result = new JsonArray(source, locator);
            }
            else if (on == "governance" && name is "digest" or "normalise_policy")
            {
                result = Governance(null, name, args, kwargs);
            }
            else
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

                if (name == "__init__")
                {
                    result = "opened";
                }
                else if (on == "governance")
                {
                    result = Governance(memory, name, args, kwargs);
                }
                else if (on == "dream")
                {
                    var a = Bind(name, ["ref"], args, kwargs);
                    result = name == "validate_source" ? DreamSources.ValidateSource(memory, Dict(a[0].Value)) : throw Unsupported(name);
                }
                else
                {
                    result = StoreCall(memory, name, args, kwargs);
                }
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
        }

        PyIO.Print(stdout, "## " + index.ToString(CultureInfo.InvariantCulture) + " call " + on + "." + name);
        PyIO.Print(stdout, line);
    }

    private static InvalidDataException Unsupported(string name) => new("memory_probe: unsupported call " + name);

    private static JsonObject Dict(JsonNode? value) => value as JsonObject ?? throw new PyTypeError("expected a dict, not " + PyJson.TypeName(value));

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

    private static JsonArray Tuple((string, string) value) => new JsonArray(value.Item1, value.Item2);

    private static JsonNode? StoreCall(MemoryStore memory, string name, JsonArray args, JsonObject kwargs)
    {
        PyArg[] a;
        switch (name)
        {
            case "record_id":
                a = Bind(name, ["domain", "key"], args, kwargs);
                return memory.RecordId(a[0].Value, a[1].Value);
            case "_identity":
                a = Bind(name, ["domain", "key"], args, kwargs);
                return Tuple(memory.Identity(a[0].Value, a[1].Value));
            case "_normalise_data":
                a = Bind(name, ["data"], args, kwargs);
                return memory.NormaliseData(a[0].Value);
            case "_resolve_work_sources":
                a = Bind(name, ["data", "_checkpoint_ref"], args, kwargs);
                MemoryStore.Item(a[0].Value, "source_refs");
                return memory.ResolveWorkSources(Dict(a[0].Value), a[1].Value);
            case "show":
                a = Bind(name, ["memory_id"], args, kwargs);
                return memory.Show(a[0].Value);
            case "list":
                a = Bind(name, ["domain", "status"], args, kwargs);
                return memory.List(a[0].Value, a[1].Value);
            case "_human":
                a = Bind(name, ["evidence", "memory_id", "revision", "decision"], args, kwargs);
                return memory.Human(a[0].Value, a[1].Value, a[2].Value, PyNode.AsString(a[3].Value));
            case "dream_index_current":
                a = Bind(name, ["data"], args, kwargs);
                return memory.DreamIndexCurrent(a[0].Value);
            case "put":
                a = Bind(name, ["key", "data", "status", "revision", "evidence"], args, kwargs);
                return memory.Put(a[0].Value, a[1].Value, a[2], a[3].Value, a[4].Value);
            case "revise":
                a = Bind(name, ["memory_id", "data", "status", "revision", "evidence"], args, kwargs);
                return memory.Revise(a[0].Value, a[1].Value, a[2].Value, a[3].Value, a[4].Value);
            case "link":
                a = Bind(name, ["source_id", "target_id", "relation", "revision", "evidence", "data", "target_revision"], args, kwargs);
                return memory.Link(a[0].Value, a[1].Value, a[2].Value, a[3].Value, a[4].Value, a[5].Value, a[6].Value);
            case "links":
                a = Bind(name, ["memory_id"], args, kwargs);
                return memory.Links(a[0].Value);
            case "active_links":
                a = Bind(name, ["memory_id"], args, kwargs);
                return memory.ActiveLinks(a[0].Value);
            case "unlink":
                a = Bind(name, ["source_id", "target_id", "relation", "revision", "evidence"], args, kwargs);
                return memory.Unlink(a[0].Value, a[1].Value, a[2].Value, a[3].Value, a[4].Value);
            case "history":
                a = Bind(name, ["memory_id", "limit"], args, kwargs);
                return memory.History(a[0].Value, a[1]);
            case "_derived_descendants":
                a = Bind(name, ["memory_id"], args, kwargs);
                return PyNode.StrList(memory.DerivedDescendants(PyNode.AsString(a[0].Value)!));
            case "forget_preview":
                a = Bind(name, ["memory_id"], args, kwargs);
                return memory.ForgetPreview(a[0].Value);
            case "forget":
                a = Bind(name, ["memory_id", "revision", "evidence", "policy_revision"], args, kwargs);
                return memory.Forget(a[0].Value, a[1].Value, a[2].Value, a[3].Value);
            case "_environment_subset":
                a = Bind(name, ["required", "current"], args, kwargs);
                return memory.EnvironmentSubset(a[0].Value, a[1].Value);
            case "_file_digest":
                a = Bind(name, ["path"], args, kwargs);
                return memory.FileDigest(PyPath.Normalize(PyNode.AsString(a[0].Value)!));
            case "_work_sources_current":
                a = Bind(name, ["data", "seen"], args, kwargs);
                var seen = a[1].Value is JsonArray list ? list.Select(item => PyNode.AsString(item)!).ToHashSet(StringComparer.Ordinal) : null;
                return memory.WorkSourcesCurrent(a[0].Value, seen);
            case "eligible":
                a = Bind(name, ["domain", "routine", "usage", "environment", "include_stale"], args, kwargs);
                return memory.Eligible(a[0].Value, a[1].Value, a[2], a[3].Value, a[4]);
            case "policy":
                Bind(name, [], args, kwargs);
                return memory.Policy();
            case "capture":
                a = Bind(name, ["key", "data", "revision"], args, kwargs);
                return memory.Capture(a[0].Value, a[1].Value, a[2].Value);
            case "record_usage":
                a = Bind(name, ["refs", "routine", "outcome", "event_id"], args, kwargs);
                return memory.RecordUsage(a[0].Value, a[1].Value, a[2], a[3].Value);
            case "usage":
                a = Bind(name, ["memory_id", "limit"], args, kwargs);
                return memory.Usage(a[0].Value, a[1]);
            case "pending_jobs":
                a = Bind(name, ["limit"], args, kwargs);
                return memory.PendingJobs(a[0]);
            case "blocked_jobs":
                a = Bind(name, ["limit"], args, kwargs);
                return memory.BlockedJobs(a[0]);
            case "complete_job":
                a = Bind(name, ["memory_id", "revision"], args, kwargs);
                return memory.CompleteJob(a[0].Value, a[1].Value);
            case "block_job":
                a = Bind(name, ["memory_id", "revision", "reason"], args, kwargs);
                return memory.BlockJob(a[0].Value, a[1].Value, a[2].Value);
            case "health":
                Bind(name, [], args, kwargs);
                return memory.Health();
            case "_row":
                a = Bind(name, ["memory_id"], args, kwargs);
                return PyNode.FromRow(memory.Row(a[0].Value));
            case "_content_hash":
                a = Bind(name, ["data"], args, kwargs);
                return MemoryStore.ContentHash(a[0].Value);
            default:
                throw Unsupported(name);
        }
    }

    private static JsonNode? Governance(MemoryStore? memory, string name, JsonArray args, JsonObject kwargs)
    {
        PyArg[] a;
        switch (name)
        {
            case "digest":
                a = Bind(name, ["value"], args, kwargs);
                return MemoryGovernance.Digest(a[0].Value);
            case "normalise_policy":
                a = Bind(name, ["data"], args, kwargs);
                return MemoryGovernance.NormalisePolicy(a[0].Value);
        }

        ArgumentNullException.ThrowIfNull(memory);
        switch (name)
        {
            case "policy":
                Bind(name, [], args, kwargs);
                return MemoryGovernance.Policy(memory);
            case "policy_preview":
                a = Bind(name, ["data"], args, kwargs);
                return MemoryGovernance.PolicyPreview(memory, a[0].Value);
            case "set_policy":
                a = Bind(name, ["data", "evidence"], args, kwargs);
                return MemoryGovernance.SetPolicy(memory, a[0].Value, a[1].Value);
            case "authorize_capture":
                a = Bind(name, ["normal", "allow_confirmation"], args, kwargs);
                return MemoryGovernance.AuthorizeCapture(memory, a[0].Value, PyJson.Truthy(a[1].Or(false)));
            case "capture":
                a = Bind(name, ["key", "data", "revision", "_checkpoint_ref"], args, kwargs);
                return MemoryGovernance.Capture(memory, a[0].Value, a[1].Value, a[2].Value, a[3].Value);
            case "retention_authorized":
                a = Bind(name, ["record", "expected_revision"], args, kwargs);
                MemoryGovernance.RetentionAuthorized(memory, a[0].Value, a[1].Value);
                return null;
            case "maintain":
                a = Bind(name, ["limit", "after"], args, kwargs);
                return MemoryGovernance.Maintain(memory, a[0], a[1].Value);
            case "tombstones":
                Bind(name, [], args, kwargs);
                return MemoryGovernance.Tombstones(memory);
            case "tombstones_preview":
                a = Bind(name, ["bundle"], args, kwargs);
                return MemoryGovernance.TombstonesPreview(memory, a[0].Value);
            case "restore_tombstones":
                a = Bind(name, ["bundle", "evidence"], args, kwargs);
                return MemoryGovernance.RestoreTombstones(memory, a[0].Value, a[1].Value);
            case "export_preview":
                a = Bind(name, ["memory_id", "recipe"], args, kwargs);
                return MemoryGovernance.ExportPreview(memory, a[0].Value, a[1].Value);
            case "export_recipe":
                a = Bind(name, ["memory_id", "recipe", "evidence", "output"], args, kwargs);
                return MemoryGovernance.ExportRecipe(memory, a[0].Value, a[1].Value, a[2].Value, PyNode.AsString(a[3].Value)!);
            default:
                throw Unsupported(name);
        }
    }

    private static JsonNode? Module(string name, JsonArray args, JsonObject kwargs)
    {
        PyArg[] a;
        switch (name)
        {
            case "search_text":
                a = Bind(name, ["record"], args, kwargs);
                return MemoryStore.SearchText(a[0].Value);
            case "memory_identity":
                a = Bind(name, ["account", "domain", "key"], args, kwargs);
                return Tuple(MemoryStore.MemoryIdentity(PyNode.AsString(a[0].Value)!, a[1].Value, a[2].Value));
            case "scoped_entity_key":
                a = Bind(name, ["value"], args, kwargs);
                return MemoryStore.ScopedEntityKey(a[0].Value);
            default:
                throw Unsupported(name);
        }
    }

    private static JsonNode? Encoder(string name, JsonArray args, JsonObject kwargs)
    {
        PyArg[] a;
        switch (name)
        {
            case "model_spec":
                Bind(name, [], args, kwargs);
                return MemoryEncoder.ModelSpec();
            case "status":
                a = Bind(name, ["model_dir"], args, kwargs);
                return MemoryEncoder.Status(PyNode.AsString(a[0].Value));
            case "status_local":
                Bind(name, [], args, kwargs);
                return MemoryEncoder.StatusLocal();
            case "encode_local":
                a = Bind(name, ["texts"], args, kwargs);
                return MemoryEncoder.EncodeLocal(a[0].Value);
            case "default_model_dir":
                Bind(name, [], args, kwargs);
                return MemoryEncoder.DefaultModelDir();
            case "_copilot_home":
                Bind(name, [], args, kwargs);
                return MemoryEncoder.CopilotHome();
            case "_safe_model_path":
                a = Bind(name, ["model_dir"], args, kwargs);
                return MemoryEncoder.SafeModelPath(PyNode.AsString(a[0].Value));
            case "_validate_model":
                a = Bind(name, ["model_dir"], args, kwargs);
                return MemoryEncoder.ValidateModel(PyNode.AsString(a[0].Value));
            case "_identity_payload":
                Bind(name, [], args, kwargs);
                return MemoryEncoder.IdentityPayload();
            case "_expected_manifest":
                Bind(name, [], args, kwargs);
                return MemoryEncoder.ExpectedManifest();
            case "_validate_texts":
                a = Bind(name, ["texts"], args, kwargs);
                return MemoryEncoder.ValidateTexts(a[0].Value);
            case "_private_python":
                Bind(name, [], args, kwargs);
                return MemoryEncoder.PrivatePython();
            case "_runtime_available":
                Bind(name, [], args, kwargs);
                return MemoryEncoder.RuntimeAvailable();
            case "_digest_bytes":
                a = Bind(name, ["data", "algorithm"], args, kwargs);
                return MemoryEncoder.DigestBytes(Convert.FromHexString(PyNode.AsString(a[0].Value)!), PyNode.AsString(a[1].Value)!);
            case "_canonical_json":
                a = Bind(name, ["value"], args, kwargs);
                return MemoryEncoder.CanonicalJson(a[0].Value);
            case "_read_manifest":
                a = Bind(name, ["model_dir"], args, kwargs);
                return MemoryEncoder.ReadManifest(PyPath.Normalize(PyNode.AsString(a[0].Value)!));
            case "_verify_asset":
                a = Bind(name, ["path", "asset"], args, kwargs);
                return MemoryEncoder.VerifyAsset(PyPath.Normalize(PyNode.AsString(a[0].Value)!),
                                                 MemoryEncoder.Assets[(int)PyJson.AsInt(a[1].Value)]);
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
                var script = PyNode.AsString(step["script"])!;
                script = script switch
                {
                    "@SCHEMA_V1" => MemoryStore.SchemaV1,
                    "@SCHEMA" => MemoryStore.Schema,
                    _ => script,
                };
                connection.ExecuteScript(Text(script));
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

    private static JsonArray Sorted(IEnumerable<string> values) => PyNode.StrList(PyText.Sorted(values));

    private static void Const(int index, JsonObject step, TextWriter stdout)
    {
        var name = PyNode.AsString(step["const"])!;
        JsonNode? value;
        if (PyNode.AsString(step["on"]) == "encoder")
        {
            value = name switch
            {
                "MODEL_ID" => MemoryEncoder.ModelId,
                "MODEL_REVISION" => MemoryEncoder.ModelRevision,
                "DIMENSIONS" => MemoryEncoder.Dimensions,
                "MAX_TOKENS" => MemoryEncoder.MaxTokens,
                "MAX_BATCH_SIZE" => MemoryEncoder.MaxBatchSize,
                "MAX_TEXT_CHARS" => MemoryEncoder.MaxTextChars,
                "MAX_CHUNKS_PER_INFERENCE" => MemoryEncoder.MaxChunksPerInference,
                "MAX_STDIN_BYTES" => MemoryEncoder.MaxStdinBytes,
                "MAX_SUBPROCESS_OUTPUT_BYTES" => MemoryEncoder.MaxSubprocessOutputBytes,
                "DOWNLOAD_ATTEMPTS" => MemoryEncoder.DownloadAttempts,
                "DOWNLOAD_TIMEOUT_SECONDS" => MemoryEncoder.DownloadTimeoutSeconds,
                "PREPROCESSING" => MemoryEncoder.Preprocessing,
                "ASSETS" => new JsonArray(MemoryEncoder.Assets.Select(asset => (JsonNode?)asset.ToJson()).ToArray()),
                "__doc__" => MemoryEncoder.Doc,
                _ => throw Unsupported(name),
            };
        }
        else
        {
            value = name switch
            {
                "SCHEMA_VERSION" => MemoryStore.SchemaVersion,
                "MAX_SOURCE_FILE_BYTES" => MemoryStore.MaxSourceFileBytes,
                "MAX_MEMORY_TEXT_CHARS" => MemoryStore.MaxMemoryTextChars,
                "PREFERENCES_RECOVERY" => MemoryStore.PreferencesRecovery,
                "SCHEMA_V1" => MemoryStore.SchemaV1,
                "SCHEMA" => MemoryStore.Schema,
                "DOMAINS" => Sorted(MemoryStore.Domains),
                "KINDS" => Sorted(MemoryStore.Kinds),
                "AUTHORITIES" => Sorted(MemoryStore.Authorities),
                "SENSITIVITIES" => Sorted(MemoryStore.Sensitivities),
                "USES" => Sorted(MemoryStore.Uses),
                "SOURCE_KINDS" => Sorted(MemoryStore.SourceKinds),
                "STATUSES" => Sorted(MemoryStore.Statuses),
                "TYPED_RELATIONS" => Sorted(MemoryStore.TypedRelations),
                "RELATIONS" => Sorted(MemoryStore.Relations),
                "OBSERVATIONAL_KINDS" => Sorted(MemoryStore.ObservationalKinds),
                "TRANSITIONS" => Transitions(),
                _ => throw Unsupported(name),
            };
        }

        PyIO.Print(stdout, "## " + index.ToString(CultureInfo.InvariantCulture) + " const " + name);
        PyIO.Print(stdout, PyJson.Dumps(value, ensureAscii: false));
    }

    private static JsonObject Transitions()
    {
        var result = new JsonObject();
        foreach (var status in new[] { "candidate", "active", "disputed", "stale", "rejected", "superseded", "suppressed", "forgotten" })
        {
            result[status] = Sorted(MemoryStore.Transitions[status]);
        }

        return result;
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
                line = "sha256 " + PyIds.Sha256Hex(PyIO.ReadBytes(path));
            }
            else if (step.ContainsKey("mkdir"))
            {
                var path = PathOf(step["mkdir"]);
                PosixNative.Mkdir(path, Convert.ToUInt32("700", 8));
                Chmod(path, PyNode.AsString(step["mode"]) ?? "700");
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
                label = "read " + PyNode.AsString(step["read"]);
                line = PyJson.Dumps(JsonValue.Create(PyIO.ReadTextFile(PathOf(step["read"]))), ensureAscii: false);
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

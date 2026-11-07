using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.State.Work;

/// <summary>
/// C# twin of <c>cloud/contracts/state/probes/work_ledger_probe.py</c>. Not a Margo command and not offered to the
/// hosted model (the hosted tool only runs classified scripts): <c>tools/state_parity.py</c> runs both on the same
/// program so that time-dependent <c>work_state.py</c> flows (approve, begin, finish, reconcile, artifact-shared,
/// action edits) and the ledger's library API are judged byte for byte. Keep the two files in step.
/// </summary>
public sealed partial class WorkLedgerProbeScript : IStateScript
{
    private readonly Dictionary<string, JsonNode?> _saved = new(StringComparer.Ordinal);
    private string _account = "";
    private string _stateDir = "";
    private string _directory = "";

    public string ScriptName => "work_ledger_probe.py";

    // A fresh instance per run: saved results never leak between runs of the shared registry instance.
    public int Run(IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr) =>
        StateScripts.RunGuarded(() => new WorkLedgerProbeScript().Main(args, stdin, stdout, stderr), stderr);

    private int Main(IReadOnlyList<string> argv, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var parser = new PyArgumentParser(ScriptName);
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
        if (!PyPath.Exists(_directory))
        {
            PosixNative.Mkdir(_directory, Convert.ToUInt32("700", 8));
        }

        for (var index = 0; index < program.Count; index++)
        {
            var step = (JsonObject)program[index]!;
            if (step.ContainsKey("cli"))
            {
                Cli(index, step, stdout);
            }
            else if (step.ContainsKey("call"))
            {
                Call(index, step, stdout);
            }
            else if (step.ContainsKey("sql"))
            {
                Sql(index, step, stdout);
            }
            else if (step.ContainsKey("write"))
            {
                Write(index, step, stdout);
            }
            else if (step.ContainsKey("symlink"))
            {
                var name = PyDynamic.AsString(step["symlink"])!;
                File.CreateSymbolicLink(PyPath.Join(_directory, name), Text(PyDynamic.AsString(step["target"])!));
                PyIO.Print(stdout, "## " + index + " symlink " + name);
            }
            else
            {
                StateScripts.PrintError(stderr, "unknown step " + index);
                return 1;
            }
        }

        return 0;
    }

    [GeneratedRegex(@"^\$\{([^}]+)\}$")]
    private static partial Regex Whole();

    [GeneratedRegex(@"\$\{([^}]+)\}")]
    private static partial Regex Part();

    [GeneratedRegex(@"^now(?:([+-])([0-9]+)([smhd]))?$")]
    private static partial Regex Clock();

    private JsonNode? Value(string expr)
    {
        var clock = Clock().Match(expr);
        if (clock.Success)
        {
            var moment = PyDateTime.Now();
            if (clock.Groups[1].Success)
            {
                var count = long.Parse(clock.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                var delta = clock.Groups[3].Value switch
                {
                    "s" => PyTimeDelta.FromSeconds(count),
                    "m" => PyTimeDelta.FromMinutes(count),
                    "h" => PyTimeDelta.FromHours(count),
                    _ => PyTimeDelta.FromDays(count),
                };
                moment = clock.Groups[1].Value == "+" ? moment + delta : moment - delta;
            }

            return moment.Isoformat();
        }

        if (expr == "dir")
        {
            return _directory;
        }

        if (expr == "account")
        {
            return _account;
        }

        var parts = expr.Split('.');
        var value = _saved[parts[0]];
        foreach (var part in parts.Skip(1))
        {
            value = value is JsonArray list ? list[int.Parse(part, System.Globalization.CultureInfo.InvariantCulture)] : PyDynamic.Index(value, part);
        }

        return PyDynamic.Clone(value);
    }

    private string Text(string value) => Part().Replace(value, match =>
    {
        var found = Value(match.Groups[1].Value);
        return PyDynamic.AsString(found) ?? PyJson.Dumps(found, ensureAscii: false);
    });

    private JsonNode? Template(JsonNode? value)
    {
        switch (value)
        {
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
                if (PyDynamic.AsString(value) is { } text)
                {
                    var whole = Whole().Match(text);
                    return whole.Success ? Value(whole.Groups[1].Value) : Text(text);
                }

                return PyDynamic.Clone(value);
        }
    }

    private static string ErrorLine(Exception ex) => ex switch
    {
        PyException py => py.PythonType + ": " + py.Message,
        InvalidOperationException => "RuntimeError: " + ex.Message,
        _ => ex.GetType().Name + ": " + ex.Message,
    };

    private void Cli(int index, JsonObject step, TextWriter stdout)
    {
        var args = ((JsonArray)step["cli"]!).Select(arg => Text(PyDynamic.AsString(arg)!)).ToList();
        if (step.ContainsKey("input"))
        {
            var path = PyPath.Join(_directory, "input-" + index + ".json");
            WriteText(path, PyJson.Dumps(Template(step["input"]), ensureAscii: false));
            args.Add("--input");
            args.Add(path);
        }

        var argv = new List<string> { "--account", _account, "--state-dir", _stateDir };
        argv.AddRange(args);
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter { NewLine = "\n" };
        using var input = new StringReader(Text(PyDynamic.AsString(step["stdin"]) ?? ""));
        string code;
        try
        {
            code = new WorkStateScript().Main(argv, input, output, error).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (PySystemExit exit)
        {
            code = exit.Code.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            code = "uncaught " + ErrorLine(ex);
        }

        PyIO.Print(stdout, "## " + index + " cli " + (args.Count > 0 ? args[0] : "") + ": exit " + code);
        PyIO.Write(stdout, output.ToString());
        if (error.ToString().Length > 0)
        {
            PyIO.Print(stdout, "stderr: " + error.ToString().TrimEnd('\n'));
        }

        if (code == "0" && PyDynamic.AsString(step["save"]) is { Length: > 0 } name)
        {
            _saved[name] = PyJson.Loads(output.ToString());
        }
    }

    private void Call(int index, JsonObject step, TextWriter stdout)
    {
        var on = PyDynamic.AsString(PyDynamic.Get(step, "on")) ?? "ledger";
        var method = PyDynamic.AsString(step["call"]);
        var args = (JsonArray?)Template(PyDynamic.Get(step, "args", new JsonArray())) ?? [];
        var kwargs = (JsonObject?)Template(PyDynamic.Get(step, "kwargs", new JsonObject())) ?? [];
        Ledger? ledger = null;
        PyConnection? connection = null;
        string line;
        try
        {
            JsonNode? result;
            if (on == "module")
            {
                result = Module(method!, new Arguments(args, kwargs));
            }
            else if (on == "static")
            {
                var call = new Arguments(args, kwargs);
                Ledger.ValidateReceipt(call.Get(0, "receipt"), call.Text(1, "outcome"));
                result = null;
            }
            else if (on == "borrowed")
            {
                connection = StateStore.Connect(_account, _stateDir, readOnly: PyDynamic.Truthy(PyDynamic.Get(step, "read_only")));
                if (PyDynamic.Get(step, "row_factory") is JsonValue flag && PyJson.Scalar(flag) is false)
                {
                    connection.RowFactoryIsRow = false;
                }

                ledger = Ledger.FromConnection(connection, PyDynamic.AsString(PyDynamic.Get(step, "borrow_account")) ?? _account);
                result = method is null ? "borrowed" : LedgerCall(ledger, method, new Arguments(args, kwargs));
            }
            else
            {
                ledger = new Ledger(_account, _stateDir);
                result = on == "products"
                    ? ProductsCall(new Productivity(ledger), method!, new Arguments(args, kwargs))
                    : LedgerCall(ledger, method!, new Arguments(args, kwargs));
            }

            line = PyJson.Dumps(result, ensureAscii: false);
            if (PyDynamic.AsString(PyDynamic.Get(step, "save")) is { Length: > 0 } name)
            {
                _saved[name] = PyDynamic.Clone(result);
            }
        }
        catch (Exception ex)
        {
            line = "!" + ErrorLine(ex);
        }
        finally
        {
            ledger?.Close();
            connection?.Close();
        }

        PyIO.Print(stdout, "## " + index + " call " + on + "." + (method ?? "None"));
        PyIO.Print(stdout, line);
    }

    private sealed class Arguments(JsonArray positional, JsonObject keywords)
    {
        public bool Has(int index, string name) => index < positional.Count || keywords.ContainsKey(name);

        public JsonNode? Get(int index, string name, JsonNode? fallback = null) =>
            index < positional.Count ? positional[index] : keywords.TryGetPropertyValue(name, out var value) ? value : fallback;

        public string? Text(int index, string name, string? fallback = null) =>
            Has(index, name) ? PyDynamic.AsString(Get(index, name)) : fallback;

        public bool Flag(int index, string name, bool fallback) => Has(index, name) ? PyDynamic.Truthy(Get(index, name)) : fallback;
    }

    private static JsonNode? Module(string method, Arguments a) => method switch
    {
        "digest" => WorkLedger.Digest(a.Get(0, "value")),
        "text" => WorkLedger.Text(a.Get(0, "value"), a.Text(1, "field")!),
        "timestamp" => WorkLedger.Timestamp(a.Get(0, "value")).Isoformat(),
        "human" => PyDynamic.Clone(WorkLedger.Human(a.Get(0, "evidence"), a.Get(1, "subject_id"), a.Get(2, "revision"), a.Text(3, "decision"))),
        "check_revision" => CheckRevision(a),
        _ => throw new InvalidOperationException("probe: unknown module function " + method),
    };

    private static JsonNode? CheckRevision(Arguments a)
    {
        WorkLedger.CheckRevision((JsonObject)a.Get(0, "row")!, a.Get(1, "revision"));
        return null;
    }

    private static JsonNode? LedgerCall(Ledger ledger, string method, Arguments a)
    {
        switch (method)
        {
            case "invalidate_approval":
                return ledger.InvalidateApproval(a.Get(0, "action_id"), a.Get(1, "revision"), a.Get(2, "action_hash"), a.Get(3, "reason"));
            case "event":
                ledger.Event(a.Get(0, "entity_id"), a.Text(1, "event")!, a.Get(2, "data"));
                return null;
            case "row":
                return ledger.Row(a.Text(0, "table")!, a.Get(1, "entity_id"));
            case "source":
                return ledger.SourceCore(a.Get(0, "family"), a.Get(1, "scope"), a.Get(2, "external_id"), a.Get(3, "revision"), a.Get(4, "evidence"),
                                         a.Get(5, "web_link"), a.Has(6, "sensitivity") ? a.Get(6, "sensitivity") : "unknown", a.Get(7, "observed_at"));
            case "find_source":
                return ledger.FindSource(a.Get(0, "family"), a.Get(1, "scope"), a.Get(2, "external_id"));
            case "source_refs":
                return ledger.SourceRefs(a.Get(0, "refs"), a.Flag(1, "require_current", true));
            case "refs_stale":
                return ledger.RefsStale(a.Get(0, "refs"));
            case "source_evidence":
                return ledger.SourceEvidence(a.Get(0, "refs"));
            case "ingest":
                return ledger.Ingest(a.Get(0, "data"), a.Get(1, "claim_key"));
            case "update_item":
                return ledger.UpdateItem(a.Get(0, "item_id"), a.Get(1, "revision"), a.Get(2, "state"), a.Get(3, "patch"), a.Get(4, "evidence"));
            case "relate":
                return ledger.Relate(a.Get(0, "source_id"), a.Get(1, "kind"), a.Get(2, "target_id"), a.Get(3, "revision"), a.Get(4, "evidence"));
            case "propose":
                return ledger.Propose(a.Get(0, "data"));
            case "edit_action":
                return ledger.EditAction(a.Get(0, "action_id"), a.Get(1, "revision"), a.Get(2, "data"), a.Get(3, "expected_hash"));
            case "disposition":
                return ledger.Disposition(a.Get(0, "action_id"), a.Get(1, "revision"), a.Text(2, "state")!, a.Get(3, "until"), a.Get(4, "expected_hash"));
            case "approve":
                return ledger.Approve(a.Get(0, "action_id"), a.Get(1, "revision"), a.Get(2, "action_hash"), a.Get(3, "evidence"), a.Get(4, "expires_at"));
            case "begin":
                return ledger.Begin(a.Get(0, "action_id"), a.Get(1, "revision"), a.Get(2, "fresh"));
            case "finish":
                return ledger.Finish(a.Get(0, "attempt_id"), a.Get(1, "state"), a.Get(2, "receipt"), a.Flag(3, "reconcile", false));
            case "show":
                return ledger.Show(a.Get(0, "entity_id"));
            case "history":
                return ledger.History(a.Get(0, "entity_id"));
            case "list":
                return ledger.List(a.Text(0, "view", "decisions")!);
            case "import_preview":
                return ledger.ImportPreview(a.Text(0, "path")!);
            case "import_commitments":
                return ledger.ImportCommitments(a.Text(0, "path")!, a.Text(1, "expected_digest")!, a.Get(2, "evidence"));
            case "export_commitments":
                return ledger.ExportCommitments(a.Text(0, "path")!, a.Text(1, "expected_digest"));
            default:
                throw new InvalidOperationException("probe: unknown ledger method " + method);
        }
    }

    private static JsonNode? ProductsCall(Productivity products, string method, Arguments a) => method switch
    {
        "record_id" => products.RecordId(a.Text(0, "kind")!, a.Get(1, "identity")),
        "put" => products.Put(a.Text(0, "kind")!, a.Get(1, "identity"), a.Get(2, "data"), a.Get(3, "state"), a.Get(4, "revision"), a.Get(5, "evidence")),
        "recap_retry" => products.RecapRetry(a.Get(0, "meeting_id"), a.Get(1, "revision"), a.Get(2, "result"), a.Get(3, "evidence_ref"), a.Get(4, "next_check_at")),
        "carry_forward" => products.CarryForward(a.Get(0, "meeting_id"), a.Get(1, "revision"), a.Get(2, "target_id"), a.Get(3, "target_revision"), a.Get(4, "evidence")),
        "share_receipt" => products.ShareReceipt(a.Get(0, "artifact_id"), a.Get(1, "revision"), a.Get(2, "attempt_id")),
        _ => throw new InvalidOperationException("probe: unknown products method " + method),
    };

    private void Sql(int index, JsonObject step, TextWriter stdout)
    {
        var database = PyPath.Join(PyPath.Join(_stateDir, PyIds.Sha256Hex(_account)), "margo.sqlite3");
        string line;
        using (var connection = PyConnection.Connect(database))
        {
            try
            {
                var parameters = ((JsonArray?)Template(PyDynamic.Get(step, "params", new JsonArray())) ?? []).Cast<object?>().ToArray();
                var cursor = connection.Execute(PyDynamic.AsString(step["sql"])!, parameters);
                var rows = new JsonArray();
                foreach (var row in cursor.FetchAll())
                {
                    rows.Add(new JsonArray(row.Select(WorkLedger.FromSql).ToArray()));
                }

                connection.Commit();
                line = PyJson.Dumps(new JsonObject { ["rowcount"] = cursor.RowCount, ["rows"] = rows }, ensureAscii: false);
            }
            catch (PySqliteError ex)
            {
                line = "!" + ex.PythonType + ": " + ex.Message;
            }
        }

        PyIO.Print(stdout, "## " + index + " sql");
        PyIO.Print(stdout, line);
    }

    private void Write(int index, JsonObject step, TextWriter stdout)
    {
        var name = PyDynamic.AsString(step["write"])!;
        var path = PyPath.Join(_directory, name);
        if (step.ContainsKey("json"))
        {
            WriteText(path, PyJson.Dumps(Template(step["json"]), ensureAscii: false));
        }
        else if (step.ContainsKey("hex"))
        {
            File.WriteAllBytes(path, Convert.FromHexString(PyDynamic.AsString(step["hex"])!));
        }
        else
        {
            WriteText(path, Text(PyDynamic.AsString(step["text"])!));
        }

        PyIO.Print(stdout, "## " + index + " write " + name);
    }

    /// <summary><c>Path.write_text(text, encoding="utf-8")</c>: mode 0666 minus the umask, as <c>open()</c> creates files.</summary>
    private static void WriteText(string path, string text) => File.WriteAllBytes(path, PyIO.EncodeUtf8(text));
}

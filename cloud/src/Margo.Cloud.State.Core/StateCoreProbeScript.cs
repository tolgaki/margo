using System.Numerics;
using System.Text.Json.Nodes;

namespace Margo.Cloud.State.Core;

/// <summary>
/// C# twin of <c>cloud/contracts/state/probes/state_core_probe.py</c>. Not a Margo command and not offered to
/// the hosted model (the hosted tool only runs classified scripts): <c>tools/state_parity.py</c> runs both on
/// the same inputs so the shared helpers (PyJson, PyTime, PyArgs, PyIds, StateStore, PyConnection) are judged
/// byte for byte against CPython. Keep the two files in step.
/// </summary>
public sealed class StateCoreProbeScript : IStateScript
{
    public string ScriptName => "state_core_probe.py";

    public int Run(IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr) =>
        StateScripts.RunGuarded(() => Main(args, stdin, stdout, stderr), stderr);

    private static string Error(Exception ex) => "!" + ((PyException)ex).PythonType + ": " + ex.Message;

    private static int Main(IReadOnlyList<string> argv, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        if (argv.Count > 0 && argv[0] == "args")
        {
            Args(argv.Skip(1).ToList(), stdout);
            return 0;
        }

        var parser = new PyArgumentParser("state_core_probe.py");
        parser.AddArgument("--account");
        parser.AddArgument("--state-dir");
        var sub = parser.AddSubparsers(dest: "command", required: true);
        foreach (var name in new[] { "dumps", "loads", "parse-json" })
        {
            sub.AddParser(name);
        }

        var command = sub.AddParser("timestamps");
        command.AddArgument("values", nargs: "*");
        command = sub.AddParser("depth");
        command.AddArgument("shapes", nargs: "*");
        command = sub.AddParser("isoformat");
        command.AddArgument("--kind", choices: ["datetime", "date", "time"], @default: "datetime");
        command.AddArgument("values", nargs: "*");
        command = sub.AddParser("ids");
        command.AddArgument("values", nargs: "*");
        command = sub.AddParser("store");
        command.AddArgument("store_command", choices: ["connect", "connect-read", "transactions", "state-path", "resolve-account", "read-json"]);
        command.AddArgument("--path");
        var args = parser.ParseArgs(argv, stdout, stderr);
        switch (args.GetString("command"))
        {
            case "dumps":
                Dumps(stdin, stdout);
                break;
            case "loads":
                Loads(stdin, stdout);
                break;
            case "parse-json":
                ParseJson(stdin, stdout);
                break;
            case "timestamps":
                foreach (var value in args.GetStrings("values")!)
                {
                    try
                    {
                        PyIO.Print(stdout, StateStore.ValidateTimestamp(value));
                    }
                    catch (PyValueError ex)
                    {
                        PyIO.Print(stdout, Error(ex));
                    }
                }

                break;
            case "depth":
                Depth(args.GetStrings("shapes")!, stdout);
                break;
            case "isoformat":
                Isoformat(args.GetString("kind")!, args.GetStrings("values")!, stdout);
                break;
            case "ids":
                foreach (var value in args.GetStrings("values")!)
                {
                    PyIO.Print(stdout, PyIds.Sha256Hex(value));
                }

                var token = PyIds.Uuid4Hex();
                PyIO.Print(stdout, token + " " + token.Length + " " + token[12] + " " + ("89ab".Contains(token[16], StringComparison.Ordinal) ? "True" : "False"));
                PyIO.Print(stdout, StateStore.UtcNow());
                break;
            default:
                return Store(args, stdout);
        }

        return 0;
    }

    private static JsonArray Cases(TextReader stdin) =>
        PyJson.Loads(PyIO.ReadText(stdin)) as JsonArray ?? throw new PySystemExit(1);

    private static void Dumps(TextReader stdin, TextWriter stdout)
    {
        foreach (var item in Cases(stdin))
        {
            var options = item!["options"] as JsonObject ?? [];
            int? indent = options["indent"] is { } i ? (int)PyJson.AsInt(i) : null;
            var separators = options["separators"] as JsonArray;
            try
            {
                var value = PyJson.Loads((string)item["value"]!);
                var text = PyJson.Dumps(value, indent,
                                        separators is null ? null : (string?)separators[0],
                                        separators is null ? null : (string?)separators[1],
                                        sortKeys: options["sort_keys"] is { } s && PyJson.Truthy(s),
                                        ensureAscii: options["ensure_ascii"] is not { } a || PyJson.Truthy(a),
                                        allowNan: options["allow_nan"] is not { } n || PyJson.Truthy(n));
                PyIO.Print(stdout, PyJson.Dumps(JsonValue.Create(text)));
            }
            catch (Exception ex) when (ex is PyValueError or PyTypeError or PyRecursionError)
            {
                PyIO.Print(stdout, Error(ex));
            }
        }
    }

    private static void Loads(TextReader stdin, TextWriter stdout)
    {
        foreach (var item in Cases(stdin))
        {
            try
            {
                PyIO.Print(stdout, PyJson.Dumps(PyJson.Loads((string)item!)));
            }
            catch (Exception ex) when (ex is PyValueError or PyRecursionError)
            {
                PyIO.Print(stdout, Error(ex));
            }
        }
    }

    private static void ParseJson(TextReader stdin, TextWriter stdout)
    {
        foreach (var item in Cases(stdin))
        {
            try
            {
                PyIO.Print(stdout, PyJson.Dumps(JsonValue.Create(StateStore.CanonicalJson(StateStore.ParseJson((string)item!)))));
            }
            catch (Exception ex) when (ex is PyValueError or PyRecursionError)
            {
                PyIO.Print(stdout, Error(ex));
            }
        }
    }

    private static void Depth(IReadOnlyList<string> shapes, TextWriter stdout)
    {
        const string Member = "{\"a\":";
        foreach (var spec in shapes)
        {
            var parts = spec.Split(':');
            var count = (int)PyArgumentParser.ParsePyInt(parts[1]);
            var text = parts[0] switch
            {
                "array" => new string('[', count) + new string(']', count),
                "object" => string.Concat(Enumerable.Repeat(Member, count)) + "1" + new string('}', count),
                _ => string.Concat(Enumerable.Repeat(Member, count - 1)) + "[1]" + new string('}', count - 1),
            };
            var line = new List<string> { spec };
            var parsers = new (string Name, Func<string, JsonNode?> Parse)[] { ("loads", PyJson.Loads), ("parse_json", StateStore.ParseJson) };
            foreach (var (name, parse) in parsers)
            {
                try
                {
                    var output = PyJson.Dumps(parse(text));
                    line.Add(name + " ok " + output.Length + " " + PyIds.Sha256Hex(output)[..16]);
                }
                catch (Exception ex) when (ex is PyValueError or PyRecursionError)
                {
                    line.Add(name + " " + Error(ex));
                }
            }

            PyIO.Print(stdout, string.Join(" | ", line));
        }
    }

    private static void Isoformat(string kind, IReadOnlyList<string> values, TextWriter stdout)
    {
        foreach (var value in values)
        {
            try
            {
                if (kind == "date")
                {
                    PyIO.Print(stdout, PyDate.FromIsoformat(value).Isoformat());
                }
                else if (kind == "time")
                {
                    var parsed = PyTimeOfDay.FromIsoformat(value);
                    PyIO.Print(stdout, parsed.Isoformat() + " " + parsed.Isoformat("microseconds") + " "
                                       + (parsed.UtcOffset?.ToString() ?? "None"));
                }
                else
                {
                    var parsed = PyDateTime.FromIsoformat(value);
                    var line = new List<string>
                    {
                        parsed.Isoformat(), parsed.Isoformat("milliseconds"), parsed.ToString(),
                        parsed.UtcOffset?.ToString() ?? "None",
                    };
                    if (parsed.IsAware)
                    {
                        try
                        {
                            line.Add(parsed.ToUtc().Isoformat());
                        }
                        catch (PyOverflowError ex)
                        {
                            line.Add(Error(ex));
                        }
                    }

                    PyIO.Print(stdout, string.Join(" | ", line));
                }
            }
            catch (Exception ex) when (ex is PyValueError or PyOverflowError)
            {
                PyIO.Print(stdout, Error(ex));
            }
        }
    }

    /// <summary>The same parser as <c>demo_parser()</c> in the Python probe.</summary>
    public static PyArgumentParser DemoParser()
    {
        var root = new PyArgumentParser("demo.py",
            "Contract probe parser; it exercises the argparse features the state scripts use, including help wrapping.");
        root.AddArgument("--account", help: "explicit principal; otherwise MARGO_ACCOUNT/private config");
        root.AddArgument(["--state-root", "--state-dir"], dest: "state_root", help: "private state base directory");
        var sub = root.AddSubparsers(dest: "command", required: true);
        var command = sub.AddParser("list", help: "portable review views; JSON is also the optional canvas contract and wraps over several lines");
        command.AddArgument("--view", choices: ["decisions", "approval", "waiting", "problems", "all"], @default: "decisions");
        command.AddArgument("--json", action: PyArgAction.StoreTrue);
        command.AddArgument("--limit", type: PyArgType.Int, @default: new BigInteger(50));
        command = sub.AddParser("show");
        command.AddArgument("id");
        command.AddArgument("--revision", type: PyArgType.Int, required: true);
        command = sub.AddParser("search", help: "bounded local search");
        command.AddArgument("query", nargs: "?");
        command.AddArgument("--input", help: "private JSON {query}; - reads stdin instead of process arguments");
        command.AddArgument("--entity", action: PyArgAction.Append, @default: new List<object?>());
        command.AddArgument("--mode", choices: ["hybrid", "semantic", "lexical"], @default: "hybrid");
        command = sub.AddParser("ack", help: "ack actual receipt-covered items, not unrelated batch members");
        command.AddArgument("ids", nargs: "*");
        command.AddArgument("--item-key", action: PyArgAction.Append, dest: "item_keys");
        command.AddArgument("--batch");
        command.AddArgument("--receipt", required: true, help: "publication ID returned after durable output recording");
        command = sub.AddParser("feedback");
        command.AddArgument("paths", nargs: "+");
        command.AddArgument("--replies", nargs: "*", @default: new List<object?>());
        command.AddArgument("--since", help: "only posts created on/after YYYY-MM-DD");
        command.AddArgument("--format", choices: ["table", "json"], @default: "table");
        command = sub.AddParser("link", help: "link two memories");
        command.AddArgument("source");
        command.AddArgument("target");
        command.AddArgument("--relation", required: true, choices: ["contradicts", "supersedes", "supports"]);
        command.AddArgument("--revision", required: true, type: PyArgType.Int);
        command.AddArgument("--evidence", required: false);
        return root;
    }

    private static void Args(IReadOnlyList<string> argv, TextWriter stdout)
    {
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter { NewLine = "\n" };
        var status = 0;
        PyNamespace? ns = null;
        try
        {
            ns = DemoParser().ParseArgs(argv, output, error);
        }
        catch (PySystemExit exit)
        {
            status = exit.Code;
        }

        PyIO.Print(stdout, "exit=" + status);
        if (ns is not null)
        {
            var obj = new JsonObject();
            foreach (var name in ns.Names)
            {
                obj[name] = ToJson(ns[name]);
            }

            PyIO.Print(stdout, PyJson.Dumps(obj, sortKeys: true));
        }

        PyIO.Print(stdout, "--- stdout");
        PyIO.Write(stdout, output.ToString());
        PyIO.Print(stdout, "--- stderr");
        PyIO.Write(stdout, error.ToString());
    }

    private static JsonNode? ToJson(object? value) => value switch
    {
        null => null,
        string text => JsonValue.Create(text),
        bool flag => JsonValue.Create(flag),
        BigInteger integer => PyJson.Int(integer),
        double number => JsonValue.Create(number),
        IEnumerable<object?> items => new JsonArray(items.Select(ToJson).ToArray()),
        _ => throw new PyTypeError("Object of type " + value.GetType().Name + " is not JSON serializable"),
    };

    private static int Store(PyNamespace args, TextWriter stdout)
    {
        var account = args.GetString("account");
        var stateDir = args.GetString("state_dir");
        try
        {
            switch (args.GetString("store_command"))
            {
                case "state-path":
                    var (principal, path) = StateStore.StatePath(account, stateDir);
                    PyIO.Print(stdout, principal);
                    PyIO.Print(stdout, path);
                    return 0;
                case "resolve-account":
                    PyIO.Print(stdout, StateStore.ResolveAccount(account));
                    return 0;
                case "read-json":
                    PyIO.Print(stdout, PyJson.Dumps(JsonValue.Create(StateStore.CanonicalJson(StateStore.ReadJson(args.GetString("path")!)))));
                    return 0;
            }

            using var connection = StateStore.Connect(account, stateDir, readOnly: args.GetString("store_command") == "connect-read");
            if (args.GetString("store_command") is "connect" or "connect-read")
            {
                ShowConnection(connection, stdout);
            }
            else
            {
                TransactionDemo(connection, stdout);
            }

            return 0;
        }
        catch (StateError ex)
        {
            PyIO.Print(stdout, Error(ex));
            return 2;
        }
    }

    private static void ShowConnection(PyConnection connection, TextWriter stdout)
    {
        var pragmas = new JsonObject();
        foreach (var name in new[] { "foreign_keys", "busy_timeout", "temp_store", "synchronous", "journal_mode" })
        {
            pragmas[name] = ScalarJson(connection.Execute("PRAGMA " + name).FetchOne()![0]);
        }

        var meta = new JsonObject();
        foreach (var row in connection.Execute("SELECT key,value FROM margo_meta ORDER BY key"))
        {
            meta[(string)row[0]!] = ScalarJson(row[1]);
        }

        PyIO.Print(stdout, PyJson.Dumps(new JsonObject
        {
            ["pragmas"] = pragmas,
            ["in_transaction"] = connection.InTransaction,
            ["row_factory_row"] = connection.RowFactoryIsRow,
            ["meta"] = meta,
        }, sortKeys: true));
    }

    private static JsonValue? ScalarJson(object? value) => value switch
    {
        null => null,
        long number => JsonValue.Create(number),
        double number => JsonValue.Create(number),
        string text => JsonValue.Create(text),
        _ => throw new PyTypeError("unexpected value"),
    };

    private static void TransactionDemo(PyConnection connection, TextWriter stdout)
    {
        string Rows() => PyRepr.Object(connection.Execute("SELECT id, label FROM probe_items ORDER BY id").FetchAll()
                                                 .Select(row => (object?)row).ToList());

        void Say(params object?[] parts) => PyIO.Print(stdout, string.Join(" ", parts.Select(PyRepr.ToStr)));

        connection.Execute("CREATE TABLE IF NOT EXISTS probe_items (id INTEGER PRIMARY KEY, label TEXT NOT NULL UNIQUE)");
        Say("after create", connection.InTransaction, Rows());
        StateStore.Transaction(connection, () =>
        {
            connection.Execute("INSERT INTO probe_items(label) VALUES ('outer-1')");
            try
            {
                StateStore.Transaction(connection, () =>
                {
                    connection.Execute("INSERT INTO probe_items(label) VALUES ('inner-rolled-back')");
                    throw new InvalidOperationException("inner failure");
                });
            }
            catch (InvalidOperationException ex)
            {
                Say("inner", ex.Message, connection.InTransaction, Rows());
            }

            StateStore.Transaction(connection, () =>
            {
                connection.Execute("INSERT INTO probe_items(label) VALUES ('inner-kept')");
            });
            Say("outer", connection.InTransaction, Rows());
        });
        Say("committed", connection.InTransaction, Rows());
        try
        {
            StateStore.Transaction(connection, () =>
            {
                connection.Execute("INSERT INTO probe_items(label) VALUES ('outer-rolled-back')");
                connection.Execute("INSERT INTO probe_items(label) VALUES ('outer-1')");
            });
        }
        catch (PySqliteError ex)
        {
            Say("outer failure", ex.PythonType, ex.Message, connection.InTransaction, Rows());
        }

        StateStore.Transaction(connection, () => Say("read", connection.InTransaction, Rows()), readOnly: true);
        connection.With(() =>
        {
            var cursor = connection.Execute("UPDATE probe_items SET label=label||'!' WHERE label LIKE 'inner%'");
            Say("implicit", connection.InTransaction, cursor.RowCount);
        });
        Say("after with", connection.InTransaction, Rows());
        try
        {
            connection.With(() =>
            {
                connection.Execute("DELETE FROM probe_items");
                throw new PyValueError("abandon delete");
            });
        }
        catch (PyValueError ex)
        {
            Say("rolled back", ex.Message, connection.InTransaction, Rows());
        }

        var row = connection.Execute("SELECT count(*) AS total, max(id) AS last FROM probe_items").FetchOne()!;
        Say("row", row["TOTAL"], row["last"], row.Keys().Cast<object?>().ToList());
        foreach (var statement in new[] { "SELECT 1; SELECT 2", "SELECT nope FROM probe_items" })
        {
            try
            {
                connection.Execute(statement);
            }
            catch (PySqliteError ex)
            {
                Say("error", ex.PythonType, ex.Message);
            }
        }

        try
        {
            connection.Execute("INSERT INTO probe_items(label) VALUES (?)", 1, 2);
        }
        catch (PySqliteError ex)
        {
            Say("error", ex.PythonType, ex.Message);
        }

        connection.Rollback();
    }
}

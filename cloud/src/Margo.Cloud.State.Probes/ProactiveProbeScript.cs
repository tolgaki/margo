using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Proactive;

namespace Margo.Cloud.State.Probes;

/// <summary>
/// C# twin of <c>cloud/contracts/state/probes/proactive_probe.py</c>. Not a Margo command and not offered to the
/// hosted model (the hosted tool only runs classified scripts): <c>tools/state_parity.py</c> runs both on the same
/// inputs so the library API that task_runs.py and memory_dream.py import, the CLI under a fixed clock, and
/// fixtures the CLI cannot create are judged byte for byte. Keep the two files in step; the Python docstring
/// documents the modes. Linux only (fixture files use POSIX modes), like the rest of the state helpers.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed partial class ProactiveProbeScript : IStateProbeScript
{
    private static readonly Dictionary<string, string> References = new(StringComparer.Ordinal)
    {
        ["attempt"] = "SELECT id FROM proactive_attempts ORDER BY rowid",
        ["batch"] = "SELECT id FROM proactive_batches ORDER BY rowid",
        ["source"] = "SELECT source_key FROM proactive_sources ORDER BY rowid",
        ["item"] = "SELECT item_key FROM proactive_items ORDER BY rowid",
        ["publication"] = "SELECT id FROM proactive_publications ORDER BY rowid",
    };

    public string ScriptName => "proactive_probe.py";

    public int Run(IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr) =>
        StateScripts.RunGuarded(() => Main(args, stdin, stdout, stderr), stderr);

    [GeneratedRegex(@"@\{(attempt|batch|source|item|publication)(?::(-?[0-9]+))?\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    private static int Main(IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        string? now = null, account = null, stateDir = null;
        var argv = args.ToList();
        while (argv.Count > 0 && argv[0] is "--now" or "--account" or "--state-dir")
        {
            if (argv.Count < 2)
            {
                return Exit(stderr, "probe: " + argv[0] + " needs a value");
            }

            switch (argv[0])
            {
                case "--now":
                    now = argv[1];
                    break;
                case "--account":
                    account = argv[1];
                    break;
                default:
                    stateDir = argv[1];
                    break;
            }

            argv.RemoveRange(0, 2);
        }

        if (argv.Count == 0)
        {
            return Exit(stderr, "probe: choose cli, call, sql, write or link");
        }

        var mode = argv[0];
        var rest = argv.Skip(1).ToList();
        using var clock = now is null ? null : PyRuntime.Override(clock: FixedClock(now));
        return mode switch
        {
            "cli" => Cli(account, stateDir, rest, stdin, stdout, stderr),
            "call" => Call(account, stateDir, stdin, stdout, stderr),
            "sql" => Sql(account, stateDir, stdin, stdout),
            "write" => Write(rest, stdin),
            "link" => Link(rest),
            _ => Exit(stderr, "probe: unknown mode " + mode),
        };
    }

    // raise SystemExit("message"): the message on stderr, status 1.
    private static int Exit(TextWriter stderr, string message)
    {
        StateScripts.PrintError(stderr, message);
        return 1;
    }

    private static Func<DateTimeOffset> FixedClock(string text)
    {
        var utc = PyDateTime.FromIsoformat(text).ToUtc();
        var instant = new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, utc.Second, TimeSpan.Zero)
            .AddTicks(utc.Microsecond * 10L);
        return () => instant;
    }

    private sealed class ProbeExit(string message) : Exception(message);

    private static MatchEvaluator Resolver(PyConnection conn) => match =>
    {
        var name = match.Groups[1].Value;
        var rows = conn.Execute(References[name]).Select(row => row[0]).ToList();
        var index = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : -1;
        var position = index > 0 ? index - 1 : rows.Count + index;
        if (index == 0 || position < 0 || position >= rows.Count)
        {
            throw new ProbeExit("probe: no " + name + " #" + index.ToString(CultureInfo.InvariantCulture));
        }

        return (string)rows[position]!;
    };

    private static JsonNode? Substitute(JsonNode? value, MatchEvaluator resolve) => value switch
    {
        JsonArray array => new JsonArray(array.Select(item => Substitute(item, resolve)).ToArray()),
        JsonObject obj => SubstituteObject(obj, resolve),
        JsonValue when PyNode.IsStr(value, out var text) => JsonValue.Create(Placeholder().Replace(text, resolve)),
        _ => PyNode.Copy(value),
    };

    private static JsonObject SubstituteObject(JsonObject obj, MatchEvaluator resolve)
    {
        var result = new JsonObject();
        foreach (var (key, item) in obj)
        {
            result[key] = Substitute(item, resolve);
        }

        return result;
    }

    private static int Cli(string? account, string? stateDir, List<string> rest, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var state = new List<string>();
        if (account is not null)
        {
            state.AddRange(["--account", account]);
        }

        if (stateDir is not null)
        {
            state.AddRange(["--state-dir", stateDir]);
        }

        var input = PyIO.ReadText(stdin);
        if (rest.Append(input).Any(text => Placeholder().IsMatch(text)))
        {
            var conn = StateStore.Connect(account, stateDir);
            try
            {
                var resolve = Resolver(conn);
                rest = rest.Select(arg => Placeholder().Replace(arg, resolve)).ToList();
                input = Placeholder().Replace(input, resolve);
            }
            catch (ProbeExit exit)
            {
                return Exit(stderr, exit.Message);
            }
            finally
            {
                conn.Close();
            }
        }

        return new ProactiveStateScript().Run([.. state, .. rest], new StringReader(input), stdout, stderr);
    }

    private static int Call(string? account, string? stateDir, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var calls = (JsonArray)PyJson.Loads(PyIO.ReadText(stdin))!;
        var conn = StateStore.Connect(account, stateDir);
        try
        {
            var resolve = Resolver(conn);
            foreach (var call in calls.Cast<JsonObject>())
            {
                var name = (string)call["fn"]!;
                var args = (JsonArray)Substitute(call["args"] ?? new JsonArray(), resolve)!;
                var outer = call["outer"] is { } mode ? (string?)mode : null;
                string line;
                try
                {
                    JsonNode? result = null;
                    if (outer is null)
                    {
                        result = Invoke(conn, name, args);
                    }
                    else
                    {
                        try
                        {
                            StateStore.Transaction(conn, () =>
                            {
                                result = Invoke(conn, name, args);
                                if (outer == "rollback")
                                {
                                    throw new RollbackSignal();
                                }
                            });
                        }
                        catch (RollbackSignal)
                        {
                        }
                    }

                    line = PyJson.Dumps(result, sortKeys: true);
                }
                catch (PyException ex)
                {
                    line = ex is StateError or PySqliteError ? "!" + ex.PythonType + ": " + ex.Message : "!" + ex.PythonType;
                }

                PyIO.Print(stdout, name + " -> " + line);
            }
        }
        catch (ProbeExit exit)
        {
            return Exit(stderr, exit.Message);
        }
        finally
        {
            conn.Close();
        }

        return 0;
    }

    private sealed class RollbackSignal : Exception;

    private static string? Str(JsonArray args, int index) =>
        index >= args.Count || PyNode.IsNone(args[index]) ? null
        : PyNode.IsStr(args[index], out var text) ? text : throw new InvalidOperationException("probe: argument " + index + " must be a str");

    private static BigInteger? Int(JsonArray args, int index) =>
        index >= args.Count || PyNode.IsNone(args[index]) ? null
        : PyJson.IsInt(args[index]) ? PyJson.AsInt(args[index]) : throw new InvalidOperationException("probe: argument " + index + " must be an int");

    private static List<string>? Strings(JsonArray args, int index) =>
        index >= args.Count || PyNode.IsNone(args[index]) ? null
        : ((JsonArray)args[index]!).Select(item => PyNode.IsStr(item, out var text) ? text : throw new InvalidOperationException("probe: list of str expected")).ToList();

    private static JsonNode? Data(JsonArray args, int index) => index < args.Count ? PyNode.Copy(args[index]) : null;

    private static JsonNode? Invoke(PyConnection conn, string name, JsonArray args)
    {
        switch (name)
        {
            case "initialize":
                ProactiveState.Initialize(conn);
                return null;
            case "validate_schema":
                return ProactiveState.ValidateSchema(conn, args.Count > 0 && PyJson.Truthy(args[0]));
            case "queue_add":
                return ProactiveState.QueueAdd(conn, Data(args, 0), Str(args, 1));
            case "queue_drain":
                return ProactiveState.QueueDrain(conn, Str(args, 0), Int(args, 1), Int(args, 2));
            case "queue_release":
                return ProactiveState.QueueRelease(conn, Str(args, 0)!, Str(args, 1)!);
            case "queue_renew":
                return ProactiveState.QueueRenew(conn, Str(args, 0)!, Str(args, 1)!, Int(args, 2)!.Value);
            case "publication_record":
                return ProactiveState.PublicationRecord(conn, Str(args, 0), Data(args, 1));
            case "publication_publish":
                return ProactiveState.PublicationPublish(conn, Str(args, 0)!, Data(args, 1));
            case "queue_ack":
                return ProactiveState.QueueAck(conn, Str(args, 0), Str(args, 1), Strings(args, 2), Strings(args, 3));
            case "publication_review":
                return ProactiveState.PublicationReview(conn, Str(args, 0)!, Str(args, 1));
            case "coverage_start":
                return ProactiveState.CoverageStart(conn, Data(args, 0));
            case "coverage_retire":
                return ProactiveState.CoverageRetire(conn, Str(args, 0)!, Str(args, 1)!);
            case "coverage_page":
                return ProactiveState.CoveragePage(conn, Str(args, 0)!, Data(args, 1));
            case "coverage_finish":
                return ProactiveState.CoverageFinish(conn, Str(args, 0)!, Data(args, 1));
            case "coverage_status":
                return ProactiveState.CoverageStatus(conn);
            case "publication_show":
                return ProactiveState.PublicationShow(conn, Str(args, 0)!);
            case "publication_list":
                return ProactiveState.PublicationList(conn, Int(args, 0));
            case "status_report":
                return ProactiveState.StatusReport(conn);
            case "import_legacy":
                return ProactiveState.ImportLegacy(conn, Str(args, 0)!);
            case "_attempt":
                return PyNode.FromRow(ProactiveState.Attempt(conn, Str(args, 0)));
            default:
                throw new ProbeExit("probe: unknown function " + name);
        }
    }

    private static int Sql(string? account, string? stateDir, TextReader stdin, TextWriter stdout)
    {
        var statements = (JsonArray)PyJson.Loads(PyIO.ReadText(stdin))!;
        var conn = StateStore.Connect(account, stateDir);
        try
        {
            conn.With(() =>
            {
                foreach (var statement in statements.Cast<JsonArray>())
                {
                    var parameters = ((JsonArray)statement[1]!).Select(value => PyJson.Scalar(value) switch
                    {
                        BigInteger integer => (object?)(long)integer,
                        var other => other,
                    }).ToArray();
                    var cursor = conn.Execute((string)statement[0]!, parameters);
                    var rows = cursor.FetchAll();
                    PyIO.Print(stdout, rows.Count > 0
                        ? PyJson.Dumps(new JsonArray(rows.Select(row => (JsonNode)new JsonArray(row.Select(PyNode.FromSql).ToArray())).ToArray()))
                        : cursor.RowCount.ToString(CultureInfo.InvariantCulture));
                }
            });
        }
        finally
        {
            conn.Close();
        }

        return 0;
    }

    private static int Write(List<string> rest, TextReader stdin)
    {
        var binary = rest.Count > 0 && rest[0] == "--hex";
        var path = rest[^1];
        var data = PyIO.ReadText(stdin);
        var content = binary ? FromHex(data) : PyIO.EncodeUtf8(data);
        CreateParent(path);
        using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Create, Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
        stream.Write(content);
        return 0;
    }

    // bytes.fromhex(text): pairs of hex digits, ASCII whitespace allowed between (not inside) pairs.
    private static byte[] FromHex(string text)
    {
        var bytes = new List<byte>();
        var index = 0;
        while (index < text.Length)
        {
            if (text[index] is ' ' or '\t' or '\n' or '\r' or '\v' or '\f')
            {
                index++;
                continue;
            }

            var high = HexDigit(text, index);
            var low = HexDigit(text, index + 1);
            bytes.Add((byte)((high << 4) | low));
            index += 2;
        }

        return [.. bytes];
    }

    private static int HexDigit(string text, int index)
    {
        var digit = index < text.Length ? char.ToLowerInvariant(text[index]) : '\0';
        return digit switch
        {
            >= '0' and <= '9' => digit - '0',
            >= 'a' and <= 'f' => digit - 'a' + 10,
            _ => throw new PyValueError("non-hexadecimal number found in fromhex() arg at position "
                                        + index.ToString(CultureInfo.InvariantCulture)),
        };
    }

    private static int Link(List<string> rest)
    {
        CreateParent(rest[0]);
        File.CreateSymbolicLink(rest[0], rest[1]);
        return 0;
    }

    private static void CreateParent(string path)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (parent is not null && !Directory.Exists(parent))
        {
            Directory.CreateDirectory(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}

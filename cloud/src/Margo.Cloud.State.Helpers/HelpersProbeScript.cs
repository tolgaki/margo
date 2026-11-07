using System.Numerics;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Helpers.Python;

namespace Margo.Cloud.State.Helpers;

/// <summary>
/// C# twin of <c>cloud/contracts/state/probes/helpers_probe.py</c>, the contract probe for the stateless
/// helpers and the Python semantics their ports reproduce (<c>list.sort</c>'s comparison sequence,
/// comparisons, <c>repr</c>, <c>int()</c>, slicing, <c>html.unescape</c>, <c>urllib.parse.unquote</c>, float
/// summation, int/float arithmetic, <c>json.dump</c>). Not a Margo command: the hosted tool only runs scripts
/// classified in <c>statecmd.COMMANDS</c>, so the model cannot reach it.
/// </summary>
public sealed class HelpersProbeScript : IStateScript
{
    public string ScriptName => "helpers_probe.py";

    public int Run(IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr) =>
        StateScripts.RunGuarded(() => PyProcess.OnLargeStack(() => Main(args, stdin, stdout, stderr)), stderr);

    private static int Main(IReadOnlyList<string> argv, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var parser = new PyArgumentParser("helpers_probe.py", "contract probe for the stateless helpers (not a Margo command)");
        var sub = parser.AddSubparsers(dest: "command", required: true);
        foreach (var name in new[] { "ops", "compare", "numbers", "capacity", "dump" })
        {
            sub.AddParser(name);
        }

        sub.AddParser("sort-trace").AddArgument("--tuples", action: PyArgAction.StoreTrue);
        sub.AddParser("text").AddArgument("kind", choices: ["clean", "html", "unescape", "salvage", "unquote"]);
        sub.AddParser("values").AddArgument("kind", choices: ["clean", "html", "unquote", "unescape", "salvage", "encoded-key", "daygap"]);
        foreach (var name in new[] { "write", "write-hex" })
        {
            sub.AddParser(name).AddArgument("path");
        }

        var args = parser.ParseArgs(argv, stdout, stderr);
        var probe = new Probe(stdin, stdout);
        switch (args.GetString("command"))
        {
            case "dump":
                PyJsonDump.Dump(stdout, probe.StdinJson());
                break;
            case "sort-trace":
                probe.SortTrace(args.GetBool("tuples"));
                break;
            case "text":
                probe.Text(args.GetString("kind")!);
                break;
            case "values":
                probe.Values(args.GetString("kind")!);
                break;
            case "write":
                Write(args.GetString("path")!, PyIO.EncodeUtf8(PyProcess.ReadStdin(stdin)));
                break;
            case "write-hex":
                var hex = new string(PyProcess.ReadStdin(stdin).Where(c => !char.IsWhiteSpace(c)).ToArray());
                Write(args.GetString("path")!, Convert.FromHexString(hex));
                break;
            case "ops":
                probe.Ops();
                break;
            case "compare":
                probe.Compare();
                break;
            case "numbers":
                probe.Numbers();
                break;
            default:
                probe.Capacity();
                break;
        }

        return 0;
    }

    private static void Write(string path, byte[] data)
    {
        File.WriteAllBytes(path, data);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private sealed class Probe(TextReader stdin, TextWriter stdout)
    {
        public JsonNode? StdinJson() => PyJson.Loads(PyProcess.ReadStdin(stdin));

        private IReadOnlyList<object?> Cases() => PyObj.Items(StdinJson());

        private void Show(object? value) => PyIO.Print(stdout, PyJson.Dumps(ToNode(value)));

        private static object? Attempt(Func<object?> function)
        {
            try
            {
                return function();
            }
            catch (PyException ex)
            {
                return "!" + ex.PythonType;
            }
            catch (InvalidOperationException)
            {
                return "!RuntimeError";
            }
        }

        public void SortTrace(bool tuples)
        {
            var keys = PyObj.Items(StdinJson());
            var log = new List<object?>();
            var order = Enumerable.Range(0, keys.Count).ToArray();
            object? error = null;
            try
            {
                PySort.Sort(order, (a, b) =>
                {
                    log.Add(new List<object?> { (BigInteger)a, (BigInteger)b });
                    return tuples ? PyObj.TupleLt(PyObj.Items(keys[a]), PyObj.Items(keys[b])) : PyObj.Lt(keys[a], keys[b]);
                });
            }
            catch (PyException ex)
            {
                error = "!" + ex.PythonType;
            }

            Show(new List<KeyValuePair<string, object?>>
            {
                new("compares", (BigInteger)log.Count),
                new("trace", log),
                new("order", error is null ? order.Select(index => (object?)(BigInteger)index).ToList() : null),
                new("error", error),
            });
        }

        public void Ops()
        {
            foreach (var value in Cases())
            {
                Show(new List<KeyValuePair<string, object?>>
                {
                    new("truthy", PyObj.Truthy(value)),
                    new("str", Attempt(() => PyObj.Str(value))),
                    new("len", Attempt(() => (BigInteger)PyObj.Len(value))),
                    new("slice", Attempt(() => PyObj.Slice(value, 3))),
                    new("iter", Attempt(() => PyObj.Iterate(value).ToList())),
                    new("int", Attempt(() => PyJson.IntStr(PyObj.Int(value)))),
                    new("in", Attempt(() => PyObj.Contains(value, "value"))),
                    new("index", Attempt(() => PyObj.Index(value, "value"))),
                    new("get", Attempt(() => PyObj.Get(value, "a", "default"))),
                    new("hash", Attempt(() =>
                    {
                        _ = PyKey.Of(value);
                        return true;
                    })),
                });
            }
        }

        public void Compare()
        {
            foreach (var pair in Cases())
            {
                var items = PyObj.Items(pair);
                var left = items[0];
                var right = items[1];
                Show(new List<KeyValuePair<string, object?>>
                {
                    new("lt", Attempt(() => PyObj.Lt(left, right))),
                    new("gt", Attempt(() => PyObj.Gt(left, right))),
                    new("ge", Attempt(() => PyObj.Ge(left, right))),
                    new("eq", PyObj.Eq(left, right)),
                    new("ne", PyObj.Ne(left, right)),
                    new("in", Attempt(() => PyObj.Contains(new List<object?> { right }, left))),
                    new("key", Attempt(() => PyKey.Of(left) == PyKey.Of(right))),
                    new("tuple_lt", Attempt(() => PyObj.TupleLt([left], [right]))),
                });
            }
        }

        public void Numbers()
        {
            foreach (var item in Cases())
            {
                var c = PyObj.Items(item);
                switch (PyObj.Str(c[0]))
                {
                    case "float":
                        Show(Attempt(() => PyJson.FloatRepr(ToDouble(c[1]))));
                        break;
                    case "cmp":
                        Show(new List<object?>
                        {
                            Attempt(() => PyObj.Lt(c[1], c[2])), Attempt(() => PyObj.Le(c[1], c[2])),
                            Attempt(() => PyObj.Eq(c[1], c[2])), Attempt(() => PyObj.Ge(c[1], c[2])),
                        });
                        break;
                    case "add":
                        Show(Attempt(() => Repr(Arithmetic(c[1], c[2], subtract: false))));
                        break;
                    case "sub":
                        Show(Attempt(() => Repr(Arithmetic(c[1], c[2], subtract: true))));
                        break;
                    case "div":
                        Show(Attempt(() => Repr(Divide(c[1], c[2]))));
                        break;
                    case "sum":
                        Show(Attempt(() => Repr(PyNumbers.Sum(PyObj.Items(c[1]).Select(x => (double)PyObj.Normalize(x)!)))));
                        break;
                    case "max0":
                        Show(Attempt(() => Repr(PyObj.Gt(c[1], BigInteger.Zero) ? PyObj.Normalize(c[1]) : BigInteger.Zero)));
                        break;
                }
            }
        }

        private static double ToDouble(object? value) => PyObj.Normalize(value) switch
        {
            BigInteger integer => PyNumbers.IntToDouble(integer),
            bool flag => flag ? 1.0 : 0.0,
            double number => number,
            _ => throw new PyTypeError("float() argument must be a string or a real number"),
        };

        private static object Arithmetic(object? left, object? right, bool subtract)
        {
            var a = PyNumbers.AsNumber(PyObj.Normalize(left)!);
            var b = PyNumbers.AsNumber(PyObj.Normalize(right)!);
            if (a is BigInteger x && b is BigInteger y)
            {
                return subtract ? x - y : x + y;
            }

            var dx = ToDouble(a);
            var dy = ToDouble(b);
            return subtract ? dx - dy : dx + dy;
        }

        private static double Divide(object? left, object? right)
        {
            var a = PyNumbers.AsNumber(PyObj.Normalize(left)!);
            var b = PyNumbers.AsNumber(PyObj.Normalize(right)!);
            if (a is BigInteger x && b is BigInteger y)
            {
                return PyNumbers.TrueDivide(x, y);
            }

            var dx = ToDouble(a);
            var dy = ToDouble(b);
            if (dy == 0)
            {
                throw new PyZeroDivisionError("float division by zero");
            }

            return dx / dy;
        }

        private static string Repr(object? value) => PyObj.Repr(value);

        public void Capacity()
        {
            foreach (var item in Cases())
            {
                var c = PyObj.Items(item);
                switch (PyObj.Str(c[0]))
                {
                    case "instant":
                        Show(Attempt(() => Helpers.Capacity.Instant(c[1]).Isoformat()));
                        break;
                    case "intervals":
                        Show(Attempt(() => ShowIntervals(Helpers.Capacity.Intervals(c[1], "label"))));
                        break;
                    case "merge":
                        Show(Attempt(() => ShowIntervals(Helpers.Capacity.Merge(Pairs(c[1])))));
                        break;
                    case "intersection":
                        Show(Attempt(() => ShowIntervals(Helpers.Capacity.Intersection(Pairs(c[1]), Pairs(c[2])))));
                        break;
                    case "subtract":
                        Show(Attempt(() => ShowIntervals(Helpers.Capacity.Subtract(Pairs(c[1]), Pairs(c[2])))));
                        break;
                    case "minutes":
                        Show(Attempt(() => PyJson.FloatRepr(Helpers.Capacity.Minutes(Pairs(c[1])))));
                        break;
                    case "number":
                        Show(Attempt(() => Repr(Helpers.Capacity.Number(c[1], "label", PyObj.Truthy(c[2])))));
                        break;
                    case "serialise":
                        Show(Attempt(() => Helpers.Capacity.Serialise(Pairs(c[1]))));
                        break;
                }
            }
        }

        private static List<CapacityInterval> Pairs(object? values) =>
            PyObj.Items(values).Select(pair =>
            {
                var items = PyObj.Items(pair);
                return new CapacityInterval(Helpers.Capacity.Instant(items[0]), Helpers.Capacity.Instant(items[1]));
            }).ToList();

        private static List<object?> ShowIntervals(IEnumerable<CapacityInterval> intervals) =>
            intervals.Select(interval => (object?)new List<object?> { interval.Start.Isoformat(), interval.End.Isoformat() }).ToList();

        public void Text(string kind)
        {
            var text = PyProcess.ReadStdin(stdin);
            switch (kind)
            {
                case "clean":
                    Show(Attempt(() => TeamsFeedback.Clean(text)));
                    break;
                case "html":
                    Show(Attempt(() => PyHtml.Unescape(text)));
                    break;
                case "unescape":
                    Show(EngageParse.Unescape(text));
                    break;
                case "salvage":
                    Show(Attempt(() => EngageParse.Salvage(text)));
                    break;
                default:
                    Show(PyUrl.Unquote(text));
                    break;
            }
        }

        public void Values(string kind)
        {
            foreach (var value in Cases())
            {
                var text = PyObj.Normalize(value) as string;
                switch (kind)
                {
                    case "clean":
                        Show(Attempt(() => TeamsFeedback.Clean(value)));
                        break;
                    case "html":
                        // html.unescape(x): "'&' not in x" returns x unchanged; otherwise re.sub needs a str.
                        Show(Attempt(() => text is not null ? PyHtml.Unescape(text)
                            : PyObj.Contains(value, "&") ? throw new PyTypeError("expected string or bytes-like object") : value));
                        break;
                    case "unquote":
                        Show(Attempt(() => text is not null ? PyUrl.Unquote(text)
                            : PyObj.Contains(value, "%") ? throw new PyTypeError("expected string or bytes-like object")
                            : throw new PyAttributeError("object has no attribute 'split'")));
                        break;
                    case "unescape":
                        Show(Attempt(() => text is not null ? EngageParse.Unescape(text) : throw new PyAttributeError("object has no attribute 'replace'")));
                        break;
                    case "salvage":
                        Show(Attempt(() => text is not null ? EngageParse.Salvage(text) : throw new PyTypeError("expected string or bytes-like object")));
                        break;
                    case "encoded-key":
                        Show(Attempt(() => EngageParse.EncodedKey(value)));
                        break;
                    default:
                        var pair = PyObj.Items(value);
                        Show(EngageParse.DayGap(pair[0], pair[1]));
                        break;
                }
            }
        }
    }

    /// <summary>A fresh JSON tree for a probe value (JSON nodes are copied: a node has one parent).</summary>
    private static JsonNode? ToNode(object? value)
    {
        // Copied by hand: JsonNode.DeepClone() serialises a BigInteger value by reflection.
        switch (value)
        {
            case JsonObject node:
                var copy = new JsonObject();
                foreach (var (key, item) in node)
                {
                    copy[key] = ToNode(item);
                }

                return copy;
            case IReadOnlyList<KeyValuePair<string, object?>> pairs:
                var dict = new JsonObject();
                foreach (var (key, item) in pairs)
                {
                    dict[key] = ToNode(item);
                }

                return dict;
        }

        switch (PyObj.Normalize(value))
        {
            case null:
                return null;
            case bool flag:
                return JsonValue.Create(flag);
            case BigInteger integer:
                return PyJson.Int(integer);
            case double number:
                return PyJson.Float(number);
            case string text:
                return JsonValue.Create(text);
            default:
                var array = new JsonArray();
                foreach (var item in PyObj.Items(value))
                {
                    array.Add(ToNode(item));
                }

                return array;
        }
    }
}

using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace Margo.Cloud.State.Core;

/// <summary>argparse <c>nargs</c>: a single value (default), <c>?</c>, <c>*</c>, <c>+</c>, <c>...</c> or an exact count.</summary>
public readonly record struct PyNargs
{
    private PyNargs(string? symbol, int count)
    {
        Symbol = symbol;
        Count = count;
    }

    /// <summary>One of <c>?</c>, <c>*</c>, <c>+</c>, <c>...</c>, <c>A...</c> (subparsers) or <c>==SUPPRESS==</c>; null for a count.</summary>
    public string? Symbol { get; }

    /// <summary>The exact count when <see cref="Symbol"/> is null.</summary>
    public int Count { get; }

    public static readonly PyNargs Optional = new("?", 0);
    public static readonly PyNargs ZeroOrMore = new("*", 0);
    public static readonly PyNargs OneOrMore = new("+", 0);
    public static readonly PyNargs Remainder = new("...", 0);
    internal static readonly PyNargs Parser = new("A...", 0);

    public static PyNargs Exactly(int count) => new(null, count);

    public static implicit operator PyNargs(string symbol) => symbol switch
    {
        "?" => Optional,
        "*" => ZeroOrMore,
        "+" => OneOrMore,
        "..." => Remainder,
        _ => throw new ArgumentException("invalid nargs value", nameof(symbol)),
    };

    public static implicit operator PyNargs(int count) => Exactly(count);

    public static PyNargs FromString(string symbol) => symbol;

    public static PyNargs FromInt32(int count) => count;
}

/// <summary>argparse actions used by the state scripts.</summary>
public enum PyArgAction
{
    Store,
    StoreConst,
    StoreTrue,
    StoreFalse,
    Append,
    AppendConst,
    Extend,
    Count,
    Help,
    Parsers,
}

/// <summary>argparse <c>type=</c> conversions.</summary>
public enum PyArgType
{
    /// <summary><c>type=None</c>/<c>str</c>: the string itself.</summary>
    Str,

    /// <summary><c>type=int</c>: Python <c>int(text)</c> semantics, as a <see cref="BigInteger"/>.</summary>
    Int,

    /// <summary><c>type=float</c>: Python <c>float(text)</c>, as a <see cref="double"/>.</summary>
    Float,
}

/// <summary>An argparse error raised while defining or parsing arguments (<c>argparse.ArgumentError</c>).</summary>
public sealed class PyArgumentError : PyException
{
    public PyArgumentError(PyArgument? argument, string message)
        : base(argument?.DisplayName is { } name ? "argument " + name + ": " + message : message)
    {
        ArgumentMessage = message;
    }

    public string ArgumentMessage { get; }

    public override string PythonType => "ArgumentError";
}

/// <summary>One argparse action (<c>add_argument</c> result).</summary>
public sealed class PyArgument
{
    internal PyArgument()
    {
    }

    public IReadOnlyList<string> OptionStrings { get; internal set; } = [];

    /// <summary>The namespace attribute; null is argparse's <c>SUPPRESS</c>.</summary>
    public string? Dest { get; internal set; }

    public PyNargs? Nargs { get; internal set; }

    public object? Const { get; internal set; }

    public object? Default { get; internal set; }

    /// <summary>True when the default is <c>argparse.SUPPRESS</c> (no attribute is set).</summary>
    public bool DefaultSuppressed { get; internal set; }

    public PyArgType Type { get; internal set; }

    public Func<string, object?>? Converter { get; internal set; }

    public string TypeName { get; internal set; } = "str";

    public IReadOnlyList<string>? Choices { get; internal set; }

    public bool Required { get; internal set; }

    public string? Help { get; internal set; }

    public bool HelpSuppressed { get; internal set; }

    public string? Metavar { get; internal set; }

    public PyArgAction Action { get; internal set; }

    internal PySubparsers? Subparsers { get; set; }

    internal List<PyArgument>? ChoiceActions { get; set; }

    internal int TakesValues => Action switch
    {
        PyArgAction.StoreConst or PyArgAction.StoreTrue or PyArgAction.StoreFalse or PyArgAction.AppendConst
            or PyArgAction.Count or PyArgAction.Help => 0,
        _ => 1,
    };

    /// <summary><c>_get_action_name</c>.</summary>
    internal string? DisplayName
    {
        get
        {
            if (OptionStrings.Count > 0)
            {
                return string.Join("/", OptionStrings);
            }

            if (Metavar is not null)
            {
                return Metavar;
            }

            if (Dest is not null)
            {
                return Dest;
            }

            return Choices is { Count: > 0 } ? "{" + string.Join(",", Choices) + "}" : null;
        }
    }
}

/// <summary>The parsed arguments (<c>argparse.Namespace</c>), keyed by <c>dest</c>.</summary>
public sealed class PyNamespace
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];

    /// <summary><c>hasattr(args, name)</c>.</summary>
    public bool Has(string name) => _values.ContainsKey(name);

    /// <summary><c>getattr(args, name)</c>; a missing attribute raises <c>AttributeError</c> as a <see cref="PyTypeError"/>.</summary>
    public object? this[string name]
    {
        get => _values.TryGetValue(name, out var value)
            ? value
            : throw new PyTypeError("'Namespace' object has no attribute " + PyRepr.Str(name));
        set => Set(name, value);
    }

    public IReadOnlyList<string> Names => _order;

    public string? GetString(string name) => (string?)this[name];

    /// <summary>A <c>type=int</c> value (or null).</summary>
    public BigInteger? GetInt(string name) => this[name] switch
    {
        null => null,
        BigInteger value => value,
        int value => value,
        long value => value,
        var other => throw new PyTypeError("argument " + name + " is not an int: " + other.GetType().Name),
    };

    public bool GetBool(string name) => this[name] is true;

    public IReadOnlyList<object?>? GetList(string name) => (IReadOnlyList<object?>?)this[name];

    public IReadOnlyList<string>? GetStrings(string name) => GetList(name)?.Cast<string>().ToList();

    internal void Set(string name, object? value)
    {
        if (!_values.ContainsKey(name))
        {
            _order.Add(name);
        }

        _values[name] = value;
    }

    internal void Remove(string name)
    {
        if (_values.Remove(name))
        {
            _order.Remove(name);
        }
    }
}

/// <summary>Which argparse help formatter a parser uses.</summary>
public enum PyHelpFormatter
{
    Default,
    RawDescription,
}

/// <summary>The <c>add_subparsers()</c> action.</summary>
public sealed class PySubparsers
{
    private readonly PyArgumentParser _owner;
    private readonly string _progPrefix;

    internal PySubparsers(PyArgumentParser owner, PyArgument action, string progPrefix)
    {
        _owner = owner;
        Action = action;
        _progPrefix = progPrefix;
    }

    internal PyArgument Action { get; }

    internal Dictionary<string, PyArgumentParser> Parsers { get; } = new(StringComparer.Ordinal);

    internal List<string> Names { get; } = [];

    /// <summary><c>add_parser(name, help=..., aliases=..., description=...)</c>.</summary>
    public PyArgumentParser AddParser(string name, string? help = null, IReadOnlyList<string>? aliases = null,
                                      string? description = null, string? prog = null,
                                      PyHelpFormatter formatter = PyHelpFormatter.Default, bool helpGiven = false)
    {
        aliases ??= [];
        if (Parsers.ContainsKey(name))
        {
            throw new PyArgumentError(Action, "conflicting subparser: " + name);
        }

        foreach (var alias in aliases)
        {
            if (Parsers.ContainsKey(alias))
            {
                throw new PyArgumentError(Action, "conflicting subparser alias: " + alias);
            }
        }

        if (help is not null || helpGiven)
        {
            var metavar = name;
            if (aliases.Count > 0)
            {
                metavar += " (" + string.Join(", ", aliases) + ")";
            }

            Action.ChoiceActions!.Add(new PyArgument { Dest = name, Metavar = metavar, Help = help });
        }

        var parser = new PyArgumentParser(prog ?? _progPrefix + " " + name, description, formatter, _owner.AddHelp,
                                          allowAbbrev: _owner.AllowAbbrev);
        Parsers[name] = parser;
        Names.Add(name);
        foreach (var alias in aliases)
        {
            Parsers[alias] = parser;
            Names.Add(alias);
        }

        Action.Choices = Names;
        return parser;
    }
}

/// <summary>
/// A port of CPython 3.13 <c>argparse.ArgumentParser</c> covering what the state scripts use: optionals
/// and positionals, <c>dest</c>, <c>nargs</c> (<c>?</c>, <c>*</c>, <c>+</c>, counts), <c>choices</c>,
/// <c>type=int</c>, <c>required</c>, <c>default</c>, <c>store_true</c>/<c>append</c> actions, required
/// subparsers, long-option prefix abbreviation, <c>--</c>, <c>--opt=value</c>, combined short flags,
/// <c>-h/--help</c> (help on stdout, exit 0) and errors (usage plus <c>prog: error: ...</c> on stderr,
/// exit 2) with the same text and line wrapping as Python.
/// </summary>
public sealed class PyArgumentParser
{
    private static readonly Regex NegativeNumber = new(@"^-\d+$|^-\d*\.\d+$", RegexOptions.CultureInvariant);

    private readonly List<PyArgument> _actions = [];
    private readonly Dictionary<string, PyArgument> _optionStrings = new(StringComparer.Ordinal);
    private readonly List<string> _optionOrder = [];
    private readonly List<(string Title, List<PyArgument> Actions)> _groups = [];
    private readonly List<PyArgument> _positionalGroup = [];
    private readonly List<PyArgument> _optionalGroup = [];
    private PySubparsers? _subparsers;
    private bool _hasNegativeNumberOptionals;

    public PyArgumentParser(string prog, string? description = null, PyHelpFormatter formatter = PyHelpFormatter.Default,
                            bool addHelp = true, string? usage = null, string? epilog = null, bool allowAbbrev = true)
    {
        Prog = prog;
        Description = description;
        Formatter = formatter;
        AddHelp = addHelp;
        Usage = usage;
        Epilog = epilog;
        AllowAbbrev = allowAbbrev;
        _groups.Add(("positional arguments", _positionalGroup));
        _groups.Add(("options", _optionalGroup));
        if (addHelp)
        {
            AddArgument(["-h", "--help"], action: PyArgAction.Help, help: "show this help message and exit");
        }
    }

    public string Prog { get; }

    public string? Description { get; }

    public string? Usage { get; }

    public string? Epilog { get; }

    public PyHelpFormatter Formatter { get; }

    public bool AddHelp { get; }

    public bool AllowAbbrev { get; }

    /// <summary>
    /// Width used for help and usage: <c>shutil.get_terminal_size().columns - 2</c> (COLUMNS, then the
    /// terminal, then 80). Tests may set it explicitly.
    /// </summary>
    public int? Width { get; set; }

    /// <summary><c>add_argument(name, ...)</c> for one positional or option string.</summary>
    public PyArgument AddArgument(string name, string? dest = null, PyArgAction action = PyArgAction.Store,
                                  PyNargs? nargs = null, object? @const = null, object? @default = null,
                                  PyArgType type = PyArgType.Str, IReadOnlyList<string>? choices = null,
                                  bool required = false, string? help = null, string? metavar = null,
                                  Func<string, object?>? converter = null, string? typeName = null,
                                  bool defaultSuppressed = false, bool helpSuppressed = false) =>
        AddArgument([name], dest, action, nargs, @const, @default, type, choices, required, help, metavar, converter,
                    typeName, defaultSuppressed, helpSuppressed);

    /// <summary><c>add_argument(option_string, option_string, ..., ...)</c>.</summary>
    public PyArgument AddArgument(IReadOnlyList<string> names, string? dest = null, PyArgAction action = PyArgAction.Store,
                                  PyNargs? nargs = null, object? @const = null, object? @default = null,
                                  PyArgType type = PyArgType.Str, IReadOnlyList<string>? choices = null,
                                  bool required = false, string? help = null, string? metavar = null,
                                  Func<string, object?>? converter = null, string? typeName = null,
                                  bool defaultSuppressed = false, bool helpSuppressed = false)
    {
        ArgumentNullException.ThrowIfNull(names);
        var argument = new PyArgument
        {
            Action = action,
            Nargs = nargs,
            Const = @const,
            Default = @default,
            Type = type,
            Converter = converter,
            TypeName = typeName ?? type switch { PyArgType.Int => "int", PyArgType.Float => "float", _ => "str" },
            Choices = choices,
            Help = help,
            HelpSuppressed = helpSuppressed,
            Metavar = metavar,
            DefaultSuppressed = defaultSuppressed,
        };
        if (names.Count == 0 || (names.Count == 1 && names[0].Length > 0 && names[0][0] != '-'))
        {
            if (required)
            {
                throw new PyTypeError("'required' is an invalid argument for positionals");
            }

            if (dest is not null && names.Count == 1)
            {
                throw new PyValueError("dest supplied twice for positional argument");
            }

            argument.Dest = names.Count == 1 ? names[0] : dest;
            argument.OptionStrings = [];
            argument.Required = nargs is not { } n || !(n == PyNargs.Optional || n == PyNargs.ZeroOrMore || n == PyNargs.Remainder);
        }
        else
        {
            var longOptions = names.Where(name => name.Length > 1 && name[1] == '-').ToList();
            foreach (var name in names)
            {
                if (name.Length == 0 || name[0] != '-')
                {
                    throw new PyValueError("invalid option string " + PyRepr.Str(name) + ": must start with a character '-'");
                }
            }

            if (dest is null)
            {
                var source = longOptions.Count > 0 ? longOptions[0] : names[0];
                dest = source.TrimStart('-');
                if (dest.Length == 0)
                {
                    throw new PyValueError("dest= is required for options like " + PyRepr.Str(names[^1]));
                }

                dest = dest.Replace('-', '_');
            }

            argument.Dest = dest;
            argument.OptionStrings = names.ToList();
            argument.Required = required;
        }

        switch (action)
        {
            case PyArgAction.Help:
                argument.Dest = null;
                argument.DefaultSuppressed = true;
                argument.Nargs = PyNargs.Exactly(0);
                break;
            case PyArgAction.StoreTrue:
                argument.Const = true;
                argument.Default ??= false;
                argument.Nargs = PyNargs.Exactly(0);
                break;
            case PyArgAction.StoreFalse:
                argument.Const = false;
                argument.Default ??= true;
                argument.Nargs = PyNargs.Exactly(0);
                break;
            case PyArgAction.StoreConst or PyArgAction.AppendConst or PyArgAction.Count:
                argument.Nargs = PyNargs.Exactly(0);
                break;
            case PyArgAction.Store or PyArgAction.Append or PyArgAction.Extend:
                if (nargs is { Symbol: null, Count: 0 })
                {
                    throw new PyValueError("nargs for store actions must be != 0; if you have nothing to store, actions such as store true or store const may be more appropriate");
                }

                break;
        }

        return AddAction(argument);
    }

    /// <summary><c>add_subparsers(dest=..., required=..., ...)</c>.</summary>
    public PySubparsers AddSubparsers(string? dest = null, bool required = false, string? title = null,
                                      string? description = null, string? metavar = null, string? help = null,
                                      string? prog = null)
    {
        if (_subparsers is not null)
        {
            throw new PyArgumentError(null, "cannot have multiple subparser arguments");
        }

        if (title is not null || description is not null)
        {
            _groups.Add((title ?? "subcommands", []));
        }

        if (prog is null)
        {
            var formatter = new HelpFormatter(this, Prog);
            prog = formatter.FormatUsageOnly(Usage, _actions.Where(a => a.OptionStrings.Count == 0).ToList(), "").Trim();
        }

        var action = new PyArgument
        {
            Action = PyArgAction.Parsers,
            Dest = dest,
            DefaultSuppressed = dest is null,
            Nargs = PyNargs.Parser,
            Required = required,
            Help = help,
            Metavar = metavar,
            OptionStrings = [],
            ChoiceActions = [],
            Choices = [],
        };
        _subparsers = new PySubparsers(this, action, prog);
        action.Subparsers = _subparsers;
        _actions.Add(action);
        (title is not null || description is not null ? _groups[^1].Actions : _positionalGroup).Add(action);
        return _subparsers;
    }

    /// <summary>
    /// <c>parse_args(args)</c>. Errors print usage and <c>prog: error: message</c> to
    /// <paramref name="stderr"/> and throw <see cref="PySystemExit"/> with status 2; <c>--help</c> prints to
    /// <paramref name="stdout"/> and throws <see cref="PySystemExit"/> with status 0.
    /// </summary>
    public PyNamespace ParseArgs(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr)
    {
        var (ns, extras) = ParseKnownArgs(args, stdout, stderr);
        if (extras.Count > 0)
        {
            Error("unrecognized arguments: " + string.Join(" ", extras), stderr);
        }

        return ns;
    }

    /// <summary><c>parse_known_args(args)</c>.</summary>
    public (PyNamespace Namespace, List<string> Extras) ParseKnownArgs(IReadOnlyList<string> args, TextWriter stdout,
                                                                        TextWriter stderr, PyNamespace? ns = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ns ??= new PyNamespace();
        foreach (var action in _actions)
        {
            if (action.Dest is not null && !ns.Has(action.Dest) && !action.DefaultSuppressed)
            {
                ns.Set(action.Dest, CopyDefault(action.Default));
            }
        }

        List<string> extras;
        try
        {
            extras = ParseKnownArgsCore(args.ToList(), ns, stdout, stderr);
        }
        catch (PyArgumentError error)
        {
            Error(error.Message, stderr);
            throw; // unreachable
        }

        if (ns.Has(UnrecognizedArgs))
        {
            extras.AddRange((List<string>)ns[UnrecognizedArgs]!);
            ns.Remove(UnrecognizedArgs);
        }

        return (ns, extras);
    }

    /// <summary><c>error(message)</c>: usage and message on stderr, exit status 2.</summary>
    public void Error(string message, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(stderr);
        stderr.Write(FormatUsage());
        stderr.Write(Prog + ": error: " + message + "\n");
        stderr.Flush();
        throw new PySystemExit(2);
    }

    /// <summary><c>format_usage()</c>.</summary>
    public string FormatUsage() => new HelpFormatter(this, Prog).FormatUsageOnly(Usage, _actions, null);

    /// <summary><c>format_help()</c>.</summary>
    public string FormatHelp()
    {
        var formatter = new HelpFormatter(this, Prog);
        formatter.AddUsage(Usage, _actions, null);
        formatter.AddText(Description);
        foreach (var (title, actions) in _groups)
        {
            formatter.StartSection(title);
            formatter.AddArguments(actions);
            formatter.EndSection();
        }

        formatter.AddText(Epilog);
        return formatter.FormatHelp();
    }

    internal const string UnrecognizedArgs = "_unrecognized_args";

    internal int EffectiveWidth()
    {
        if (Width is { } width)
        {
            return width;
        }

        var columns = 0;
        var text = PyRuntime.GetEnv("COLUMNS");
        if (text is not null && TryParsePyInt(text, out var parsed) && parsed > 0 && parsed < int.MaxValue)
        {
            columns = (int)parsed;
        }

        if (columns <= 0)
        {
            try
            {
                columns = Console.IsOutputRedirected ? 80 : Console.WindowWidth;
            }
            catch (IOException)
            {
                columns = 80;
            }

            if (columns <= 0)
            {
                columns = 80;
            }
        }

        return columns - 2;
    }

    private PyArgument AddAction(PyArgument action)
    {
        var conflicts = action.OptionStrings.Where(_optionStrings.ContainsKey).ToList();
        if (conflicts.Count > 0)
        {
            throw new PyArgumentError(action, (conflicts.Count == 1 ? "conflicting option string: " : "conflicting option strings: ")
                                              + string.Join(", ", conflicts));
        }

        _actions.Add(action);
        foreach (var option in action.OptionStrings)
        {
            _optionStrings[option] = action;
            _optionOrder.Add(option);
            if (NegativeNumber.IsMatch(option))
            {
                _hasNegativeNumberOptionals = true;
            }
        }

        (action.OptionStrings.Count > 0 ? _optionalGroup : _positionalGroup).Add(action);
        return action;
    }

    private static object? CopyDefault(object? value) => value is List<object?> list ? new List<object?>(list) : value;

    private sealed record OptionTuple(PyArgument? Action, string OptionString, string? Separator, string? ExplicitArg);

    private List<string> ParseKnownArgsCore(List<string> argStrings, PyNamespace ns, TextWriter stdout, TextWriter stderr)
    {
        var optionIndices = new Dictionary<int, List<OptionTuple>>();
        var pattern = new StringBuilder();
        for (var i = 0; i < argStrings.Count; i++)
        {
            if (argStrings[i] == "--")
            {
                pattern.Append('-');
                for (i++; i < argStrings.Count; i++)
                {
                    pattern.Append('A');
                }

                break;
            }

            var tuples = ParseOptional(argStrings[i]);
            if (tuples is null)
            {
                pattern.Append('A');
            }
            else
            {
                optionIndices[i] = tuples;
                pattern.Append('O');
            }
        }

        var argPattern = pattern.ToString();
        var seen = new HashSet<PyArgument>();
        var extras = new List<string>();

        void TakeAction(PyArgument action, List<string> arguments, string? optionString = null)
        {
            seen.Add(action);
            var values = GetValues(action, arguments, out var suppress);
            if (!suppress)
            {
                Invoke(action, ns, values, optionString, stdout, stderr);
            }
        }

        int ConsumeOptional(int startIndex)
        {
            var tuples = optionIndices[startIndex];
            if (tuples.Count > 1)
            {
                var options = string.Join(", ", tuples.Select(t => t.OptionString));
                throw new PyArgumentError(null, "ambiguous option: " + argStrings[startIndex] + " could match " + options);
            }

            var (action, optionString, separator, explicitArg) = tuples[0];
            var actionTuples = new List<(PyArgument Action, List<string> Args, string Option)>();
            int stop;
            while (true)
            {
                if (action is null)
                {
                    extras.Add(argStrings[startIndex]);
                    return startIndex + 1;
                }

                if (explicitArg is not null)
                {
                    var argCount = MatchArgument(action, "A");
                    if (argCount == 0 && optionString[1] != '-' && explicitArg.Length != 0)
                    {
                        if (!string.IsNullOrEmpty(separator) || explicitArg[0] == '-')
                        {
                            throw new PyArgumentError(action, "ignored explicit argument " + PyRepr.Str(explicitArg));
                        }

                        actionTuples.Add((action, [], optionString));
                        var prefix = optionString[0];
                        optionString = prefix.ToString() + explicitArg[0];
                        if (_optionStrings.TryGetValue(optionString, out var next))
                        {
                            action = next;
                            explicitArg = explicitArg[1..];
                            if (explicitArg.Length == 0)
                            {
                                separator = explicitArg = null;
                            }
                            else if (explicitArg[0] == '=')
                            {
                                separator = "=";
                                explicitArg = explicitArg[1..];
                            }
                            else
                            {
                                separator = string.Empty;
                            }
                        }
                        else
                        {
                            extras.Add(prefix + explicitArg);
                            stop = startIndex + 1;
                            break;
                        }
                    }
                    else if (argCount == 1)
                    {
                        stop = startIndex + 1;
                        actionTuples.Add((action, [explicitArg], optionString));
                        break;
                    }
                    else
                    {
                        throw new PyArgumentError(action, "ignored explicit argument " + PyRepr.Str(explicitArg));
                    }
                }
                else
                {
                    var start = startIndex + 1;
                    var argCount = MatchArgument(action, argPattern[start..]);
                    stop = start + argCount;
                    actionTuples.Add((action, argStrings.GetRange(start, argCount), optionString));
                    break;
                }
            }

            foreach (var (a, values, option) in actionTuples)
            {
                TakeAction(a, values, option);
            }

            return stop;
        }

        var positionals = _actions.Where(a => a.OptionStrings.Count == 0).ToList();

        int ConsumePositionals(int startIndex)
        {
            var counts = MatchArgumentsPartial(positionals, argPattern[startIndex..]);
            for (var i = 0; i < counts.Count; i++)
            {
                var action = positionals[i];
                var count = counts[i];
                var values = argStrings.GetRange(startIndex, count);
                if (action.Nargs == PyNargs.Parser)
                {
                    if (argPattern[startIndex] == '-')
                    {
                        values.Remove("--");
                    }
                }
                else if (action.Nargs != PyNargs.Remainder)
                {
                    if (argPattern.IndexOf('-', startIndex, count) >= 0)
                    {
                        values.Remove("--");
                    }
                }

                startIndex += count;
                TakeAction(action, values);
            }

            positionals.RemoveRange(0, counts.Count);
            return startIndex;
        }

        var startAt = 0;
        var maxOptionIndex = optionIndices.Count > 0 ? optionIndices.Keys.Max() : -1;
        while (startAt <= maxOptionIndex)
        {
            var nextOption = startAt;
            while (nextOption <= maxOptionIndex && !optionIndices.ContainsKey(nextOption))
            {
                nextOption++;
            }

            if (startAt != nextOption)
            {
                var positionalsEnd = ConsumePositionals(startAt);
                if (positionalsEnd > startAt)
                {
                    startAt = positionalsEnd;
                    continue;
                }

                startAt = positionalsEnd;
            }

            if (!optionIndices.ContainsKey(startAt))
            {
                extras.AddRange(argStrings.GetRange(startAt, nextOption - startAt));
                startAt = nextOption;
            }

            startAt = ConsumeOptional(startAt);
        }

        var stopIndex = ConsumePositionals(startAt);
        extras.AddRange(argStrings.GetRange(stopIndex, argStrings.Count - stopIndex));

        var requiredActions = new List<string>();
        foreach (var action in _actions)
        {
            if (seen.Contains(action))
            {
                continue;
            }

            if (action.Required)
            {
                requiredActions.Add(action.DisplayName ?? "");
            }
            else if (action.Default is string text && action.Dest is not null && ns.Has(action.Dest)
                     && ReferenceEquals(action.Default, ns[action.Dest]))
            {
                ns.Set(action.Dest, GetValue(action, text));
            }
        }

        if (requiredActions.Count > 0)
        {
            throw new PyArgumentError(null, "the following arguments are required: " + string.Join(", ", requiredActions));
        }

        return extras;
    }

    private void Invoke(PyArgument action, PyNamespace ns, object? values, string? optionString, TextWriter stdout, TextWriter stderr)
    {
        switch (action.Action)
        {
            case PyArgAction.Store:
                ns.Set(action.Dest!, values);
                break;
            case PyArgAction.StoreConst or PyArgAction.StoreTrue or PyArgAction.StoreFalse:
                ns.Set(action.Dest!, action.Const);
                break;
            case PyArgAction.Append:
            {
                var items = CopyItems(ns.Has(action.Dest!) ? ns[action.Dest!] : null);
                items.Add(values);
                ns.Set(action.Dest!, items);
                break;
            }

            case PyArgAction.AppendConst:
            {
                var items = CopyItems(ns.Has(action.Dest!) ? ns[action.Dest!] : null);
                items.Add(action.Const);
                ns.Set(action.Dest!, items);
                break;
            }

            case PyArgAction.Extend:
            {
                var items = CopyItems(ns.Has(action.Dest!) ? ns[action.Dest!] : null);
                items.AddRange((List<object?>)values!);
                ns.Set(action.Dest!, items);
                break;
            }

            case PyArgAction.Count:
            {
                var count = ns.Has(action.Dest!) && ns[action.Dest!] is BigInteger current ? current : BigInteger.Zero;
                ns.Set(action.Dest!, count + 1);
                break;
            }

            case PyArgAction.Help:
                stdout.Write(FormatHelp());
                stdout.Flush();
                throw new PySystemExit(0);
            case PyArgAction.Parsers:
            {
                var list = (List<object?>)values!;
                var name = (string)list[0]!;
                var arguments = list.Skip(1).Cast<string>().ToList();
                if (action.Dest is not null)
                {
                    ns.Set(action.Dest, name);
                }

                if (!action.Subparsers!.Parsers.TryGetValue(name, out var parser))
                {
                    throw new PyArgumentError(action, "unknown parser " + PyRepr.Str(name) + " (choices: "
                                                      + string.Join(", ", action.Subparsers.Names) + ")");
                }

                var (sub, rest) = parser.ParseKnownArgs(arguments, stdout, stderr);
                foreach (var key in sub.Names)
                {
                    ns.Set(key, sub[key]);
                }

                if (rest.Count > 0)
                {
                    if (!ns.Has(UnrecognizedArgs))
                    {
                        ns.Set(UnrecognizedArgs, new List<string>());
                    }

                    ((List<string>)ns[UnrecognizedArgs]!).AddRange(rest);
                }

                break;
            }
        }
    }

    private static List<object?> CopyItems(object? items) => items switch
    {
        null => [],
        List<object?> list => [.. list],
        IEnumerable<object?> sequence => [.. sequence],
        _ => throw new PyTypeError("cannot append to " + items.GetType().Name),
    };

    private static object? GetValues(PyArgument action, List<string> argStrings, out bool suppress)
    {
        suppress = false;
        var nargs = action.Nargs;
        if (action.TakesValues == 0)
        {
            return argStrings.Count == 0 ? null : throw new PyArgumentError(action, "ignored explicit argument");
        }

        if (argStrings.Count == 0 && nargs == PyNargs.Optional)
        {
            var value = action.OptionStrings.Count > 0 ? action.Const : action.Default;
            if (value is string text)
            {
                value = GetValue(action, text);
                CheckValue(action, value);
            }

            return value;
        }

        if (argStrings.Count == 0 && nargs == PyNargs.ZeroOrMore && action.OptionStrings.Count == 0)
        {
            if (action.Default is not null)
            {
                CheckValue(action, action.Default);
                return action.Default;
            }

            return new List<object?>();
        }

        if (argStrings.Count == 1 && (nargs is null || nargs == PyNargs.Optional))
        {
            var value = GetValue(action, argStrings[0]);
            CheckValue(action, value);
            return value;
        }

        if (nargs == PyNargs.Remainder)
        {
            return argStrings.Select(s => GetValue(action, s)).ToList();
        }

        if (nargs == PyNargs.Parser)
        {
            var values = argStrings.Select(s => GetValue(action, s)).ToList();
            CheckValue(action, values[0]);
            return values;
        }

        var converted = argStrings.Select(s => GetValue(action, s)).ToList();
        foreach (var value in converted)
        {
            CheckValue(action, value);
        }

        return converted;
    }

    private static object? GetValue(PyArgument action, string text)
    {
        try
        {
            if (action.Converter is { } converter)
            {
                return converter(text);
            }

            return action.Type switch
            {
                PyArgType.Int => ParsePyInt(text),
                PyArgType.Float => ParsePyFloat(text),
                _ => text,
            };
        }
        catch (Exception ex) when (ex is PyValueError or PyTypeError or FormatException or OverflowException)
        {
            throw new PyArgumentError(action, "invalid " + action.TypeName + " value: " + PyRepr.Str(text));
        }
    }

    private static void CheckValue(PyArgument action, object? value)
    {
        if (action.Choices is not { } choices)
        {
            return;
        }

        var text = value switch
        {
            string s => s,
            BigInteger i => i.ToString(CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "None",
        };
        // Choices are strings: a converted non-string value never matches, as in Python (1 not in ['1']).
        if (value is not string || !choices.Contains(text, StringComparer.Ordinal))
        {
            throw new PyArgumentError(action, "invalid choice: " + PyRepr.Str(text) + " (choose from "
                                              + string.Join(", ", choices.Select(PyRepr.Str)) + ")");
        }
    }

    private List<OptionTuple>? ParseOptional(string arg)
    {
        if (arg.Length == 0 || arg[0] != '-')
        {
            return null;
        }

        if (_optionStrings.TryGetValue(arg, out var exact))
        {
            return [new OptionTuple(exact, arg, null, null)];
        }

        if (arg.Length == 1)
        {
            return null;
        }

        var eq = arg.IndexOf('=', StringComparison.Ordinal);
        if (eq >= 0 && _optionStrings.TryGetValue(arg[..eq], out var withValue))
        {
            return [new OptionTuple(withValue, arg[..eq], "=", arg[(eq + 1)..])];
        }

        var tuples = GetOptionTuples(arg);
        if (tuples.Count > 0)
        {
            return tuples;
        }

        if (NegativeNumber.IsMatch(arg) && !_hasNegativeNumberOptionals)
        {
            return null;
        }

        if (arg.Contains(' ', StringComparison.Ordinal))
        {
            return null;
        }

        return [new OptionTuple(null, arg, null, null)];
    }

    private List<OptionTuple> GetOptionTuples(string optionString)
    {
        var result = new List<OptionTuple>();
        var eq = optionString.IndexOf('=', StringComparison.Ordinal);
        var prefix = eq >= 0 ? optionString[..eq] : optionString;
        string? separator = eq >= 0 ? "=" : null;
        string? explicitArg = eq >= 0 ? optionString[(eq + 1)..] : null;
        if (optionString[1] == '-')
        {
            if (AllowAbbrev)
            {
                foreach (var option in _optionOrder)
                {
                    if (option.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        result.Add(new OptionTuple(_optionStrings[option], option, separator, explicitArg));
                    }
                }
            }
        }
        else
        {
            var shortPrefix = optionString[..2];
            var shortExplicit = optionString[2..];
            foreach (var option in _optionOrder)
            {
                if (option == shortPrefix)
                {
                    result.Add(new OptionTuple(_optionStrings[option], option, string.Empty, shortExplicit));
                }
                else if (AllowAbbrev && option.StartsWith(prefix, StringComparison.Ordinal))
                {
                    result.Add(new OptionTuple(_optionStrings[option], option, separator, explicitArg));
                }
            }
        }

        return result;
    }

    private static string NargsPattern(PyArgument action)
    {
        var option = action.OptionStrings.Count > 0;
        if (action.TakesValues == 0)
        {
            return option ? "()" : "(-*)";
        }

        var nargs = action.Nargs;
        if (nargs is null)
        {
            return option ? "(A)" : "(-*A-*)";
        }

        var n = nargs.Value;
        if (n == PyNargs.Optional)
        {
            return option ? "(A?)" : "(-*A?-*)";
        }

        if (n == PyNargs.ZeroOrMore)
        {
            return option ? "(A*)" : "(-*[A-]*)";
        }

        if (n == PyNargs.OneOrMore)
        {
            return option ? "(A+)" : "(-*A[A-]*)";
        }

        if (n == PyNargs.Remainder)
        {
            return option ? "([AO]*)" : "(.*)";
        }

        if (n == PyNargs.Parser)
        {
            return option ? "(A[AO]*)" : "(-*A[-AO]*)";
        }

        return option
            ? "([AO]{" + n.Count.ToString(CultureInfo.InvariantCulture) + "})"
            : "((?:-*A){" + n.Count.ToString(CultureInfo.InvariantCulture) + "}-*)";
    }

    private static int MatchArgument(PyArgument action, string pattern)
    {
        var match = Regex.Match(pattern, "^" + NargsPattern(action), RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            string message;
            if (action.Nargs is null)
            {
                message = "expected one argument";
            }
            else if (action.Nargs == PyNargs.Optional)
            {
                message = "expected at most one argument";
            }
            else if (action.Nargs == PyNargs.OneOrMore)
            {
                message = "expected at least one argument";
            }
            else
            {
                var count = action.Nargs.Value.Count;
                message = string.Format(CultureInfo.InvariantCulture, count == 1 ? "expected {0} argument" : "expected {0} arguments", count);
            }

            throw new PyArgumentError(action, message);
        }

        return match.Groups[1].Length;
    }

    private static List<int> MatchArgumentsPartial(List<PyArgument> actions, string pattern)
    {
        for (var i = actions.Count; i > 0; i--)
        {
            var regex = "^" + string.Concat(actions.Take(i).Select(NargsPattern));
            var match = Regex.Match(pattern, regex, RegexOptions.CultureInvariant);
            if (match.Success)
            {
                var result = new List<int>();
                for (var g = 1; g < match.Groups.Count; g++)
                {
                    result.Add(match.Groups[g].Length);
                }

                var end = match.Index + match.Length;
                if (end < pattern.Length && pattern[end] == 'O')
                {
                    while (result.Count > 0 && result[^1] == 0)
                    {
                        result.RemoveAt(result.Count - 1);
                    }
                }

                return result;
            }
        }

        return [];
    }

    /// <summary>Python <c>int(text)</c>: whitespace, sign, underscores between digits, Unicode digits, 4300-digit limit.</summary>
    public static BigInteger ParsePyInt(string text)
    {
        if (!TryParsePyInt(text, out var value, out var tooLong))
        {
            if (tooLong > 0)
            {
                throw new PyValueError(string.Format(CultureInfo.InvariantCulture,
                    "Exceeds the limit (4300 digits) for integer string conversion: value has {0} digits; use sys.set_int_max_str_digits() to increase the limit",
                    tooLong));
            }

            throw new PyValueError("invalid literal for int() with base 10: " + PyRepr.Str(text));
        }

        return value;
    }

    /// <summary>Non-throwing <see cref="ParsePyInt"/>.</summary>
    public static bool TryParsePyInt(string text, out BigInteger value) => TryParsePyInt(text, out value, out _);

    private static bool TryParsePyInt(string text, out BigInteger value, out int tooLongDigits)
    {
        value = BigInteger.Zero;
        tooLongDigits = 0;
        var stripped = PyRepr.Strip(text);
        if (stripped.Length == 0)
        {
            return false;
        }

        var negative = false;
        var index = 0;
        if (stripped[0] is '+' or '-')
        {
            negative = stripped[0] == '-';
            index = 1;
        }

        var digits = new StringBuilder();
        var previousUnderscore = true;
        for (; index < stripped.Length; index++)
        {
            var c = stripped[index];
            if (c == '_')
            {
                if (previousUnderscore)
                {
                    return false;
                }

                previousUnderscore = true;
                continue;
            }

            int digit;
            if (char.IsHighSurrogate(c) && index + 1 < stripped.Length)
            {
                digit = CharUnicodeInfo.GetDecimalDigitValue(stripped, index);
                index++;
            }
            else
            {
                digit = CharUnicodeInfo.GetDecimalDigitValue(c);
            }

            if (digit < 0)
            {
                return false;
            }

            digits.Append((char)('0' + digit));
            previousUnderscore = false;
        }

        if (digits.Length == 0 || previousUnderscore)
        {
            return false;
        }

        if (digits.Length > PyJson.MaxIntDigits)
        {
            tooLongDigits = digits.Length;
            return false;
        }

        value = BigInteger.Parse(digits.ToString(), NumberStyles.None, CultureInfo.InvariantCulture);
        if (negative)
        {
            value = -value;
        }

        return true;
    }

    /// <summary>Python <c>float(text)</c> for decimal text, <c>inf</c>/<c>nan</c> spellings and underscores.</summary>
    public static double ParsePyFloat(string text)
    {
        var stripped = PyRepr.Strip(text);
        var lower = stripped.ToLowerInvariant();
        var sign = 1.0;
        var body = lower;
        if (body.StartsWith('+') || body.StartsWith('-'))
        {
            sign = body[0] == '-' ? -1 : 1;
            body = body[1..];
        }

        if (body is "inf" or "infinity")
        {
            return sign * double.PositiveInfinity;
        }

        if (body == "nan")
        {
            return double.NaN;
        }

        if (Regex.IsMatch(stripped, @"^[+-]?((\d(_?\d)*)?\.?\d(_?\d)*|\d(_?\d)*\.)([eE][+-]?\d(_?\d)*)?$", RegexOptions.CultureInvariant)
            && double.TryParse(stripped.Replace("_", "", StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        throw new PyValueError("could not convert string to float: " + PyRepr.Str(text));
    }

    /// <summary>The part of argparse's <c>HelpFormatter</c> the scripts' usage and help output use.</summary>
    private sealed class HelpFormatter
    {
        private readonly string _prog;
        private readonly int _indentIncrement = 2;
        private readonly int _maxHelpPosition;
        private readonly int _width;
        private readonly bool _rawDescription;
        private int _currentIndent;
        private int _actionMaxLength;
        private Section _current;

        public HelpFormatter(PyArgumentParser parser, string prog)
        {
            _prog = prog;
            _width = parser.EffectiveWidth();
            _maxHelpPosition = Math.Min(24, Math.Max(_width - 20, _indentIncrement * 2));
            _rawDescription = parser.Formatter == PyHelpFormatter.RawDescription;
            _current = new Section(this, null, null);
        }

        private sealed class Section(HelpFormatter formatter, Section? parent, string? heading)
        {
            public Section? Parent { get; } = parent;
            public List<Func<string>> Items { get; } = [];

            public string Format()
            {
                if (Parent is not null)
                {
                    formatter.Indent();
                }

                var itemHelp = string.Concat(Items.Select(item => item()));
                if (Parent is not null)
                {
                    formatter.Dedent();
                }

                if (itemHelp.Length == 0)
                {
                    return string.Empty;
                }

                var head = heading is not null ? new string(' ', formatter._currentIndent) + heading + ":\n" : string.Empty;
                return "\n" + head + itemHelp + "\n";
            }
        }

        private void Indent() => _currentIndent += _indentIncrement;

        private void Dedent() => _currentIndent -= _indentIncrement;

        public void StartSection(string heading)
        {
            Indent();
            var section = new Section(this, _current, heading);
            _current.Items.Add(section.Format);
            _current = section;
        }

        public void EndSection()
        {
            _current = _current.Parent!;
            Dedent();
        }

        public void AddText(string? text)
        {
            if (text is not null)
            {
                _current.Items.Add(() => FormatText(text));
            }
        }

        public void AddUsage(string? usage, List<PyArgument> actions, string? prefix) =>
            _current.Items.Add(() => FormatUsage(usage, actions, prefix));

        public void AddArguments(List<PyArgument> actions)
        {
            foreach (var action in actions)
            {
                if (action.HelpSuppressed)
                {
                    continue;
                }

                var lengths = new List<int> { Invocation(action).Length + _currentIndent };
                if (action.ChoiceActions is { } subactions)
                {
                    Indent();
                    lengths.AddRange(subactions.Select(sub => Invocation(sub).Length + _currentIndent));
                    Dedent();
                }

                _actionMaxLength = Math.Max(_actionMaxLength, lengths.Max());
                _current.Items.Add(() => FormatAction(action));
            }
        }

        public string FormatHelp()
        {
            var help = _current.Format();
            if (help.Length > 0)
            {
                help = Regex.Replace(help, "\n\n\n+", "\n\n");
                help = help.Trim('\n') + "\n";
            }

            return help;
        }

        public string FormatUsageOnly(string? usage, List<PyArgument> actions, string? prefix)
        {
            AddUsage(usage, actions, prefix);
            return FormatHelp();
        }

        private string FormatUsage(string? usage, List<PyArgument> actions, string? prefix)
        {
            prefix ??= "usage: ";
            if (usage is not null)
            {
                usage = usage.Replace("%(prog)s", _prog, StringComparison.Ordinal);
            }
            else if (actions.Count == 0)
            {
                usage = _prog;
            }
            else
            {
                var optionals = actions.Where(a => a.OptionStrings.Count > 0).ToList();
                var positionals = actions.Where(a => a.OptionStrings.Count == 0).ToList();
                var parts = UsageParts(optionals.Concat(positionals).ToList());
                usage = string.Join(" ", new[] { _prog, string.Join(" ", parts) }.Where(s => s.Length > 0));
                var textWidth = _width - _currentIndent;
                if (prefix.Length + usage.Length > textWidth)
                {
                    var optParts = UsageParts(optionals);
                    var posParts = UsageParts(positionals);

                    List<string> GetLines(List<string> lineParts, string indent, string? linePrefix = null)
                    {
                        var lines = new List<string>();
                        var line = new List<string>();
                        var indentLength = indent.Length;
                        var lineLength = linePrefix is not null ? linePrefix.Length - 1 : indentLength - 1;
                        foreach (var part in lineParts)
                        {
                            if (lineLength + 1 + part.Length > textWidth && line.Count > 0)
                            {
                                lines.Add(indent + string.Join(" ", line));
                                line.Clear();
                                lineLength = indentLength - 1;
                            }

                            line.Add(part);
                            lineLength += part.Length + 1;
                        }

                        if (line.Count > 0)
                        {
                            lines.Add(indent + string.Join(" ", line));
                        }

                        if (linePrefix is not null)
                        {
                            lines[0] = lines[0][indentLength..];
                        }

                        return lines;
                    }

                    List<string> result;
                    if (prefix.Length + _prog.Length <= 0.75 * textWidth)
                    {
                        var indent = new string(' ', prefix.Length + _prog.Length + 1);
                        if (optParts.Count > 0)
                        {
                            result = GetLines([_prog, .. optParts], indent, prefix);
                            result.AddRange(GetLines(posParts, indent));
                        }
                        else if (posParts.Count > 0)
                        {
                            result = GetLines([_prog, .. posParts], indent, prefix);
                        }
                        else
                        {
                            result = [_prog];
                        }
                    }
                    else
                    {
                        var indent = new string(' ', prefix.Length);
                        var all = GetLines([.. optParts, .. posParts], indent);
                        if (all.Count > 1)
                        {
                            all = GetLines(optParts, indent);
                            all.AddRange(GetLines(posParts, indent));
                        }

                        result = [_prog, .. all];
                    }

                    usage = string.Join("\n", result);
                }
            }

            return prefix + usage + "\n\n";
        }

        private static List<string> UsageParts(List<PyArgument> actions)
        {
            var parts = new List<string>();
            foreach (var action in actions)
            {
                if (action.HelpSuppressed)
                {
                    continue;
                }

                string part;
                if (action.OptionStrings.Count == 0)
                {
                    part = FormatArgs(action, action.Dest ?? "");
                }
                else
                {
                    part = action.TakesValues == 0
                        ? action.OptionStrings[0]
                        : action.OptionStrings[0] + " " + FormatArgs(action, (action.Dest ?? "").ToUpperInvariant());
                    if (!action.Required)
                    {
                        part = "[" + part + "]";
                    }
                }

                parts.Add(part);
            }

            return parts;
        }

        private static string Metavar(PyArgument action, string defaultMetavar)
        {
            if (action.Metavar is not null)
            {
                return action.Metavar;
            }

            if (action.Choices is not null)
            {
                return "{" + string.Join(",", action.Choices) + "}";
            }

            return defaultMetavar;
        }

        private static string FormatArgs(PyArgument action, string defaultMetavar)
        {
            var metavar = Metavar(action, defaultMetavar);
            if (action.Nargs is not { } nargs)
            {
                return metavar;
            }

            if (nargs == PyNargs.Optional)
            {
                return "[" + metavar + "]";
            }

            if (nargs == PyNargs.ZeroOrMore)
            {
                return "[" + metavar + " ...]";
            }

            if (nargs == PyNargs.OneOrMore)
            {
                return metavar + " [" + metavar + " ...]";
            }

            if (nargs == PyNargs.Remainder)
            {
                return "...";
            }

            if (nargs == PyNargs.Parser)
            {
                return metavar + " ...";
            }

            return string.Join(" ", Enumerable.Repeat(metavar, nargs.Count));
        }

        private static string Invocation(PyArgument action)
        {
            if (action.OptionStrings.Count == 0)
            {
                return Metavar(action, action.Dest ?? "");
            }

            if (action.TakesValues == 0)
            {
                return string.Join(", ", action.OptionStrings);
            }

            return string.Join(", ", action.OptionStrings) + " " + FormatArgs(action, (action.Dest ?? "").ToUpperInvariant());
        }

        private string FormatAction(PyArgument action)
        {
            var helpPosition = Math.Min(_actionMaxLength + 2, _maxHelpPosition);
            var helpWidth = Math.Max(_width - helpPosition, 11);
            var actionWidth = helpPosition - _currentIndent - 2;
            var header = Invocation(action);
            var indentFirst = 0;
            var parts = new StringBuilder();
            if (string.IsNullOrEmpty(action.Help))
            {
                parts.Append(new string(' ', _currentIndent)).Append(header).Append('\n');
            }
            else if (header.Length <= actionWidth)
            {
                parts.Append(new string(' ', _currentIndent)).Append(header.PadRight(actionWidth)).Append("  ");
            }
            else
            {
                parts.Append(new string(' ', _currentIndent)).Append(header).Append('\n');
                indentFirst = helpPosition;
            }

            if (!string.IsNullOrEmpty(action.Help) && PyRepr.Strip(action.Help).Length > 0)
            {
                var helpText = ExpandHelp(action);
                if (helpText.Length > 0)
                {
                    var lines = SplitLines(helpText, helpWidth);
                    parts.Append(new string(' ', indentFirst)).Append(lines.Count > 0 ? lines[0] : "").Append('\n');
                    foreach (var line in lines.Skip(1))
                    {
                        parts.Append(new string(' ', helpPosition)).Append(line).Append('\n');
                    }
                }
            }
            else if (parts.Length == 0 || parts[^1] != '\n')
            {
                parts.Append('\n');
            }

            if (action.ChoiceActions is { } subactions)
            {
                Indent();
                foreach (var sub in subactions)
                {
                    parts.Append(FormatAction(sub));
                }

                Dedent();
            }

            return parts.ToString();
        }

        private string ExpandHelp(PyArgument action)
        {
            var help = action.Help!;
            if (!help.Contains('%', StringComparison.Ordinal))
            {
                return help;
            }

            var values = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["prog"] = _prog,
                ["dest"] = action.Dest,
                ["default"] = action.Default switch
                {
                    null => "None",
                    bool b => b ? "True" : "False",
                    BigInteger i => i.ToString(CultureInfo.InvariantCulture),
                    var other => Convert.ToString(other, CultureInfo.InvariantCulture),
                },
                ["metavar"] = action.Metavar ?? "None",
                ["choices"] = action.Choices is null ? "None" : string.Join(", ", action.Choices),
                ["required"] = action.Required ? "True" : "False",
                ["type"] = action.TypeName,
            };
            var builder = new StringBuilder();
            for (var i = 0; i < help.Length; i++)
            {
                if (help[i] != '%')
                {
                    builder.Append(help[i]);
                    continue;
                }

                if (i + 1 < help.Length && help[i + 1] == '%')
                {
                    builder.Append('%');
                    i++;
                    continue;
                }

                var match = Regex.Match(help[i..], @"^%\((\w+)\)s");
                if (!match.Success || !values.TryGetValue(match.Groups[1].Value, out var replacement))
                {
                    throw new PyValueError("unsupported format character in help string");
                }

                builder.Append(replacement);
                i += match.Length - 1;
            }

            return builder.ToString();
        }

        private string FormatText(string text)
        {
            if (text.Contains("%(prog)", StringComparison.Ordinal))
            {
                text = text.Replace("%(prog)s", _prog, StringComparison.Ordinal);
            }

            var textWidth = Math.Max(_width - _currentIndent, 11);
            var indent = new string(' ', _currentIndent);
            return FillText(text, textWidth, indent) + "\n\n";
        }

        private string FillText(string text, int width, string indent)
        {
            if (_rawDescription)
            {
                return string.Concat(PyRepr.SplitLines(text, keepEnds: true).Select(line => indent + line));
            }

            text = PyRepr.Strip(Regex.Replace(text, @"[ \t\n\r\f\v]+", " "));
            return string.Join("\n", PyTextWrap.Wrap(text, width, indent, indent));
        }

        private static List<string> SplitLines(string text, int width)
        {
            text = PyRepr.Strip(Regex.Replace(text, @"[ \t\n\r\f\v]+", " "));
            return PyTextWrap.Wrap(text, width);
        }
    }
}

/// <summary>Python <c>textwrap.wrap</c> with the default options (break on hyphens, break long words).</summary>
public static class PyTextWrap
{
    private const string Ws = @"[\t\n\x0B\f\r ]";
    private const string Nws = @"[^\t\n\x0B\f\r ]";
    private const string WordPunct = @"[\w!""'&.,?]";
    private const string Letter = @"[^\d\W]";

    private static readonly Regex WordSeparator = new(
        "(" + Ws + "+"
        + "|(?<=" + WordPunct + ")-{2,}(?=\\w)"
        + "|" + Nws + "+?(?:"
        + "-(?:(?<=" + Letter + "{2}-)|(?<=" + Letter + "-" + Letter + "-))(?=" + Letter + "-?" + Letter + ")"
        + "|(?=" + Ws + "|\\z)"
        + "|(?<=" + WordPunct + ")(?=-{2,}\\w)"
        + "))",
        RegexOptions.CultureInvariant);

    /// <summary><c>textwrap.wrap(text, width, initial_indent, subsequent_indent)</c>.</summary>
    public static List<string> Wrap(string text, int width, string initialIndent = "", string subsequentIndent = "")
    {
        ArgumentNullException.ThrowIfNull(text);
        if (width <= 0)
        {
            throw new PyValueError("invalid width " + width.ToString(CultureInfo.InvariantCulture) + " (must be > 0)");
        }

        text = ExpandTabs(text, 8);
        var munged = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            munged.Append(c is '\t' or '\n' or '\x0B' or '\f' or '\r' ? ' ' : c);
        }

        var chunks = WordSeparator.Split(munged.ToString()).Where(chunk => chunk.Length > 0).ToList();
        chunks.Reverse();
        var lines = new List<string>();
        while (chunks.Count > 0)
        {
            var currentLine = new List<string>();
            var currentLength = 0;
            var indent = lines.Count > 0 ? subsequentIndent : initialIndent;
            var lineWidth = width - indent.Length;
            if (chunks[^1].Trim().Length == 0 && lines.Count > 0)
            {
                chunks.RemoveAt(chunks.Count - 1);
            }

            while (chunks.Count > 0)
            {
                var length = chunks[^1].Length;
                if (currentLength + length <= lineWidth)
                {
                    currentLine.Add(chunks[^1]);
                    chunks.RemoveAt(chunks.Count - 1);
                    currentLength += length;
                }
                else
                {
                    break;
                }
            }

            if (chunks.Count > 0 && chunks[^1].Length > lineWidth)
            {
                HandleLongWord(chunks, currentLine, currentLength, lineWidth);
                currentLength = currentLine.Sum(part => part.Length);
            }

            if (currentLine.Count > 0 && currentLine[^1].Trim().Length == 0)
            {
                currentLength -= currentLine[^1].Length;
                currentLine.RemoveAt(currentLine.Count - 1);
            }

            if (currentLine.Count > 0)
            {
                lines.Add(indent + string.Concat(currentLine));
            }
        }

        return lines;
    }

    private static void HandleLongWord(List<string> reversedChunks, List<string> currentLine, int currentLength, int width)
    {
        var spaceLeft = width < 1 ? 1 : width - currentLength;
        if (spaceLeft > 0)
        {
            var end = spaceLeft;
            var chunk = reversedChunks[^1];
            if (chunk.Length > spaceLeft)
            {
                var hyphen = chunk.LastIndexOf('-', spaceLeft - 1, spaceLeft);
                if (hyphen > 0 && chunk[..hyphen].Any(c => c != '-'))
                {
                    end = hyphen + 1;
                }
            }

            currentLine.Add(chunk[..end]);
            reversedChunks[^1] = chunk[end..];
        }
        else if (currentLine.Count == 0)
        {
            currentLine.Add(reversedChunks[^1]);
            reversedChunks.RemoveAt(reversedChunks.Count - 1);
        }
    }

    private static string ExpandTabs(string text, int tabSize)
    {
        if (!text.Contains('\t', StringComparison.Ordinal))
        {
            return text;
        }

        var builder = new StringBuilder();
        var column = 0;
        foreach (var c in text)
        {
            if (c == '\t')
            {
                var spaces = tabSize - (column % tabSize);
                builder.Append(' ', spaces);
                column += spaces;
            }
            else
            {
                builder.Append(c);
                column = c is '\n' or '\r' ? 0 : column + 1;
            }
        }

        return builder.ToString();
    }
}

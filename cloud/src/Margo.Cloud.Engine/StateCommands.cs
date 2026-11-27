using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Margo.Cloud.Guard;
using Margo.Cloud.State.Core;

// Private state relies on POSIX file modes; Margo Cloud runs on Linux containers.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]

namespace Margo.Cloud.Engine;

/// <summary>The classes of state subcommands (<c>READ</c>, <c>PRIVATE</c>, <c>HUMAN</c>).</summary>
public static class StateCommandClasses
{
    /// <summary>Changes nothing.</summary>
    public const string Read = "read";

    /// <summary>Margo's own bookkeeping, which the design allows unattended (candidates, coverage, queues, run records, passive memory).</summary>
    public const string Private = "private";

    /// <summary>Records or changes something the manager decided, so it runs only while the manager is in the conversation.</summary>
    public const string Human = "human";
}

/// <summary>The <c>margo-state</c> tool as the engine sees it (Python passes a <c>StateCommands</c>).</summary>
public interface IStateCommands
{
    /// <summary><c>run(arguments, *, mode)</c>: <c>{"status", "script", "command", ...}</c>. Refusals are results, not exceptions.</summary>
    Task<JsonObject> RunAsync(JsonNode? arguments, string? mode, CancellationToken cancellationToken = default);
}

/// <summary>
/// The record every <c>margo-state</c> call leaves: script, subcommand, mode, status, exit code and duration. Never
/// arguments, input or output.
/// </summary>
public sealed record StateReceipt
{
    /// <summary><c>datetime.now(timezone.utc).isoformat(timespec="microseconds")</c>, taken when the call finished.</summary>
    public required string At { get; init; }

    /// <summary>The script, when the model gave a string (as given before normalisation when it was refused early).</summary>
    public string? Script { get; init; }

    /// <summary>The subcommand of a stateful script, when known.</summary>
    public string? Command { get; init; }

    /// <summary>The run mode (<c>host</c> for <see cref="StateCommands.InitialiseAsync"/>).</summary>
    public string? Mode { get; init; }

    /// <summary>The result status.</summary>
    public required string Status { get; init; }

    /// <summary>The process exit code, when the process ran to completion.</summary>
    public int? ExitCode { get; init; }

    /// <summary>Milliseconds from the start of execution, when execution started.</summary>
    public long? Ms { get; init; }

    /// <summary>The receipt as Python's dict, in its key order.</summary>
    public JsonObject ToJsonObject()
    {
        var receipt = new JsonObject
        {
            ["at"] = At,
            ["tool"] = "margo-state",
            ["script"] = Script,
            ["command"] = Command,
            ["mode"] = Mode,
            ["status"] = Status,
        };
        if (ExitCode is { } code)
        {
            receipt["exit_code"] = code;
        }

        if (Ms is { } ms)
        {
            receipt["ms"] = ms;
        }

        return receipt;
    }
}

/// <summary>
/// The state commands desktop Margo runs as scripts, offered to the hosted engine as one tool (<c>statecmd.py</c>).
/// </summary>
/// <remarks>
/// <para>Desktop procedures run, for example, <c>python3 scripts/work_state.py --account ACCOUNT list --view decisions</c>.
/// Hosted Margo calls <c>margo-state</c> with <c>{"script": "work_state.py", "args": ["list", "--view", "decisions"]}</c>.
/// This class runs the C# <c>margo-state</c> CLI (<c>dotnet margo-state.dll work_state.py ...</c>) as a separate
/// process, with no shell, against the instance's own account and state directory, so the procedures work unchanged
/// and the state ports stay the only code that touches the database.</para>
/// <para>What it enforces (fails closed): only the scripts and subcommands classified in <see cref="Commands"/>; the
/// account and state directory come from the instance binding, never from the model; the run mode decides which
/// classes run; no file paths from the model (input arrives on stdin with <c>--input -</c> or as named files,
/// <c>@file:NAME</c>, written to a private directory that exists for one call); bounded before it starts (argument
/// and input sizes, a per-turn budget, a timeout and an output cap). A write that times out or crashes is reported
/// as <c>unknown</c>, never as failed or done. Receipts never hold arguments, input or output.</para>
/// <para>Not thread-safe across calls of one turn, as in Python.</para>
/// </remarks>
public sealed partial class StateCommands : IStateCommands
{
    /// <summary><c>MAX_ARGS</c>.</summary>
    public const int MaxArgs = 64;

    /// <summary><c>MAX_ARG</c>, in code points.</summary>
    public const int MaxArg = 4096;

    /// <summary><c>MAX_ARGS_TOTAL</c>, in code points.</summary>
    public const int MaxArgsTotal = 16384;

    /// <summary><c>MAX_INPUT</c>, in UTF-8 bytes.</summary>
    public const int MaxInput = 256 * 1024;

    /// <summary><c>MAX_FILES</c>.</summary>
    public const int MaxFiles = 8;

    /// <summary><c>MAX_FILES_TOTAL</c>, in UTF-8 bytes.</summary>
    public const int MaxFilesTotal = 1024 * 1024;

    /// <summary><c>MAX_OUTPUT</c>, in code points.</summary>
    public const int MaxOutput = 60000;

    /// <summary>The environment variable naming the <c>margo-state.dll</c> to run.</summary>
    public const string CliVariable = "MARGO_STATE_CLI";

    /// <summary><c>TIMEOUT_SECONDS</c>.</summary>
    public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromSeconds(60);

    /// <summary><c>MODE_CLASSES</c>: the subcommand classes each run mode may use.</summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> ModeClasses { get; } = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
    {
        [RunModes.Attended] = Set(StateCommandClasses.Read, StateCommandClasses.Private, StateCommandClasses.Human),
        [RunModes.Unattended] = Set(StateCommandClasses.Read, StateCommandClasses.Private),
        [RunModes.ThirdParty] = Set(),
    };

    /// <summary>
    /// <c>COMMANDS</c>: each hosted script's subcommands by class (<see cref="StateCommandClasses.Read"/>,
    /// <see cref="StateCommandClasses.Private"/>, <see cref="StateCommandClasses.Human"/>, in that order). A stateless
    /// helper without subcommands is <c>{read: {null}}</c>. A subcommand added to a script is unavailable until it is
    /// classified here; the parity tests fail until then.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<KeyValuePair<string, IReadOnlySet<string?>>>> Commands { get; } =
        new Dictionary<string, IReadOnlyList<KeyValuePair<string, IReadOnlySet<string?>>>>(StringComparer.Ordinal)
        {
            ["work_state.py"] = Classes(
                ["list", "show", "history", "record-id"],
                ["source", "ingest", "propose", "record", "recap-check", "carry-forward"],
                ["item-update", "relate", "edit", "defer", "dismiss"]),
            ["proactive_state.py"] = Classes(
                ["seen", "queue-list", "publication-show", "publication-list", "coverage-status", "cursor-get", "status"],
                ["mark", "queue-add", "queue-drain", "queue-ack", "queue-release", "queue-renew", "publication-record",
                 "publication-publish", "coverage-start", "coverage-page", "coverage-finish", "coverage-retire",
                 "coverage-resume", "cursor-set", "prune"],
                ["publication-review"]),
            ["memory_state.py"] = Classes(
                ["status", "record-id", "show", "list", "inspect", "history", "forget-preview", "usage", "graph", "explain",
                 "search", "context", "policy", "policy-preview", "tombstones", "tombstones-preview", "capabilities",
                 "export-preview", "dream-inspect", "dream-status", "preferences-preview"],
                ["capture", "record-usage", "index", "maintain", "consolidate", "record-consolidation", "propose-lesson",
                 "trend", "dream-checkpoint", "dream-plan", "dream-finish", "dream-start"],
                ["put", "revise", "forget", "link", "unlink", "policy-set", "tombstones-restore", "preferences-import",
                 "validate-capability", "activate-lesson", "trend-definition"]),
            ["task_state.py"] = Classes(
                ["health", "record-id", "list", "show", "history", "replan-preview"],
                ["create", "start", "charge", "finish", "resume", "retry", "reconcile", "recover", "sync"],
                ["pause", "cancel", "replan"]),
            // Stateless helpers without subcommands: they compute over input and touch no state.
            ["capacity.py"] = Stateless(),
            ["teams_feedback.py"] = Stateless(),
            ["engage_parse.py"] = Stateless(),
        };

    /// <summary><c>STATEFUL</c>: the scripts that get <c>--account</c> and <c>--state-dir</c>.</summary>
    public static IReadOnlySet<string> Stateful { get; } = Set("work_state.py", "proactive_state.py", "memory_state.py", "task_state.py");

    private const string Execution = "the execution journal arrives with approval cards; until then Margo proposes and the manager acts";

    /// <summary><c>NOT_HOSTED_COMMANDS</c>: subcommands deliberately not hosted yet, with the reason told to the model.</summary>
    public static IReadOnlyDictionary<(string Script, string Command), string> NotHostedCommands { get; } = new Dictionary<(string, string), string>
    {
        [("work_state.py", "approve")] = "approvals arrive with approval cards: a hosted approval must come from the "
                                         + "manager's authenticated card, not from chat text",
        [("work_state.py", "begin")] = Execution,
        [("work_state.py", "finish")] = Execution,
        [("work_state.py", "reconcile")] = Execution,
        [("work_state.py", "artifact-shared")] = Execution,
        [("work_state.py", "import-preview")] = "hosted instances start on the work ledger; there is no legacy file to import",
        [("work_state.py", "import-commitments")] = "hosted instances start on the work ledger; there is no legacy file "
                                                    + "to import",
        [("work_state.py", "export-commitments")] = "writes a Markdown file; hosted Margo reads commitments from the ledger",
        [("proactive_state.py", "import-legacy")] = "there is no legacy desktop state to import",
        [("memory_state.py", "init")] = "the hosting service initialises private state",
        [("memory_state.py", "migrate")] = "the hosting service migrates private state",
        [("memory_state.py", "export")] = "writes a file outside private state",
        [("task_state.py", "init")] = "the hosting service initialises private state",
    };

    /// <summary><c>FORBIDDEN_OPTIONS</c>: options only the service supplies.</summary>
    public static IReadOnlyList<string> ForbiddenOptions { get; } = ["--account", "--state-dir", "--state-root", "--config-dir"];

    /// <summary>
    /// <c>ENV_PASSTHROUGH</c>, plus <c>DOTNET_ROOT</c> so <c>dotnet</c> finds its runtime where it is not installed
    /// globally. Nothing else from the service's environment reaches the CLI.
    /// </summary>
    public static IReadOnlyList<string> EnvironmentPassthrough { get; } =
        ["PATH", "HOME", "LANG", "LC_ALL", "TZ", "MARGO_ALLOW_UNSAFE_STATE_DIR", "DOTNET_ROOT"];

    private readonly IReadOnlyList<string> _command;
    private readonly string? _cliFile;
    private readonly Action<StateReceipt>? _recorder;

    /// <param name="account">The instance's account (<c>binding.Account</c>); never from the model.</param>
    /// <param name="stateRoot">The instance's private state directory; never from the model.</param>
    /// <param name="preferences">The manager's private <c>preferences.md</c>, which <c>@preferences</c> names.</param>
    /// <param name="budget">Charged once before each command that runs.</param>
    /// <param name="recorder">Receives each receipt.</param>
    /// <param name="timeout">The longest one command may run (<see cref="DefaultTimeout"/>).</param>
    /// <param name="cliCommand">The program and leading arguments that stand for <c>python3 -s scripts/</c>; the
    /// script name and its arguments follow. Default: <c>[dotnet, margo-state.dll]</c> (see <see cref="DefaultCliCommand"/>).</param>
    public StateCommands(string account, string stateRoot, string? preferences = null, CallBudget? budget = null,
                         Action<StateReceipt>? recorder = null, TimeSpan? timeout = null, IReadOnlyList<string>? cliCommand = null)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(stateRoot);
        Account = account;
        StateRoot = stateRoot;
        Preferences = string.IsNullOrEmpty(preferences) ? null : preferences;
        Budget = budget;
        Timeout = timeout ?? DefaultTimeout;
        _recorder = recorder;
        if (cliCommand is null)
        {
            (_command, _cliFile) = DefaultCliCommand();
        }
        else
        {
            if (cliCommand.Count == 0)
            {
                throw new ArgumentException("the CLI command needs a program", nameof(cliCommand));
            }

            _command = [.. cliCommand];
        }
    }

    /// <summary>The instance's account.</summary>
    public string Account { get; }

    /// <summary>The instance's private state directory.</summary>
    public string StateRoot { get; }

    /// <summary>The manager's private preferences file, when configured.</summary>
    public string? Preferences { get; }

    /// <summary>The per-turn budget, when configured.</summary>
    public CallBudget? Budget { get; }

    /// <summary>The longest one command may run.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>The command every call starts with.</summary>
    public IReadOnlyList<string> CliCommand => _command;

    /// <summary>
    /// <c>[dotnet, path]</c>, where the path is <c>MARGO_STATE_CLI</c> when set, else <c>margo-state.dll</c> next to the
    /// running application, and <c>dotnet</c> is the running host when this process is <c>dotnet</c>.
    /// </summary>
    public static (IReadOnlyList<string> Command, string CliFile) DefaultCliCommand()
    {
        var configured = PyRuntime.GetEnv(CliVariable);
        var cli = string.IsNullOrEmpty(configured) ? Path.Combine(AppContext.BaseDirectory, "margo-state.dll") : configured;
        var host = Environment.ProcessPath is { } process && Path.GetFileNameWithoutExtension(process) == "dotnet" ? process : "dotnet";
        return ([host, cli], cli);
    }

    /// <summary><c>command_class(script, command)</c>.</summary>
    public static string? CommandClass(string? script, string? command)
    {
        if (script is null || !Commands.TryGetValue(script, out var classes))
        {
            return null;
        }

        foreach (var (klass, commands) in classes)
        {
            if (commands.Contains(command))
            {
                return klass;
            }
        }

        return null;
    }

    /// <summary>
    /// <c>initialise()</c>: host-managed and idempotent: the schemas the skills expect before their first command
    /// (<c>memory_state.py init</c>, <c>task_state.py init</c>, with mode <c>host</c>).
    /// </summary>
    /// <exception cref="InvalidOperationException">A script did not initialise (Python: <c>RuntimeError</c>).</exception>
    public async Task InitialiseAsync(CancellationToken cancellationToken = default)
    {
        foreach (var script in new[] { "memory_state.py", "task_state.py" })
        {
            var outcome = await ExecuteAsync(script, [Arg.Plain("init")], null, new Dictionary<string, string>(StringComparer.Ordinal),
                                             StateCommandClasses.Read, "host", cancellationToken).ConfigureAwait(false);
            if (PyNode.AsString(outcome["status"]) != "ok")
            {
                var reason = outcome.TryGetPropertyValue("reason", out var given) ? PyNode.Str(given) : "None";
                throw new InvalidOperationException("could not initialise " + script + ": " + reason);
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// As in Python, a few malformed values are exceptions rather than results: a list or object <c>script</c>
    /// (<see cref="PyTypeError"/>, unhashable), <c>files</c> or <c>input</c> holding a lone surrogate
    /// (<see cref="PyUnicodeError"/>), an option name with a NUL character (<see cref="PyValueError"/>), and output
    /// so deeply nested that Python's JSON decoder runs out of recursion (<see cref="PyRecursionError"/>).
    /// Cancelled while the CLI runs: the process is killed, a receipt with status <c>error</c> (read) or
    /// <c>unknown</c> (write) is recorded, and <see cref="OperationCanceledException"/> is rethrown.
    /// </remarks>
    public async Task<JsonObject> RunAsync(JsonNode? arguments, string? mode, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var given = arguments as JsonObject;
        JsonNode? script = given is null ? null : PyNode.Get(given, "script");
        string? command = null;
        string klass;
        List<Arg> args;
        IReadOnlyDictionary<string, string> files;
        byte[]? stdin;
        try
        {
            if (given is null)
            {
                throw new RefusedException("arguments must be an object");
            }

            var (name, plain) = ScriptAndArgs(given);
            script = name;
            command = CommandClass(PyNode.AsString(name), null) is null ? plain[0] : null;
            klass = Authorise(name, command, mode);
            files = Files(PyNode.Get(given, "files"));
            args = Resolve(plain, files);
            stdin = Stdin(PyNode.Get(given, "input"));
        }
        catch (UnavailableException ex)
        {
            return Finish(script, command, mode, "unavailable", reason: ex.Message);
        }
        catch (RefusedException ex)
        {
            return Finish(script, command, mode, "refused", reason: ex.Message);
        }

        if (Budget is not null && !Budget.Charge())
        {
            return Finish(script, command, mode, "budget_exhausted", reason: "state command budget exhausted");
        }

        return await ExecuteAsync(PyNode.AsString(script)!, args, stdin, files, klass, mode, cancellationToken).ConfigureAwait(false);
    }

    // -- policy ----------------------------------------------------------------------------------------

    private static (JsonNode Script, List<string> Args) ScriptAndArgs(JsonObject arguments)
    {
        var raw = PyNode.Get(arguments, "script");
        var script = PyNode.AsString(raw);
        if (script is not null && script.StartsWith("scripts/", StringComparison.Ordinal))
        {
            script = script["scripts/".Length..];
        }

        if (script is not null && !script.EndsWith(".py", StringComparison.Ordinal))
        {
            script += ".py";
        }

        if (script is null)
        {
            PyNode.RequireHashable(raw); // `script not in COMMANDS` with a list or dict
        }

        if (script is null || !Commands.ContainsKey(script))
        {
            if (script is not null && NotHosted.Scripts.TryGetValue(script, out var why))
            {
                throw new UnavailableException(script + " is not available hosted: " + why);
            }

            throw new RefusedException("unknown script");
        }

        var given = arguments.TryGetPropertyValue("args", out var value) ? value : new JsonArray();
        if (given is not JsonArray list || list.Count > MaxArgs || list.Any(item => PyNode.AsString(item) is null))
        {
            throw new RefusedException("args must be a list of up to " + MaxArgs + " strings");
        }

        var args = list.Select(item => PyNode.AsString(item)!).ToList();
        if (args.Any(arg => PyRepr.Length(arg) > MaxArg) || args.Sum(PyRepr.Length) > MaxArgsTotal)
        {
            throw new RefusedException("arguments are too long");
        }

        if (CommandClass(script, null) is null && (args.Count == 0 || args[0].StartsWith('-')))
        {
            throw new RefusedException("args must start with the subcommand, e.g. [\"list\", \"--view\", \"decisions\"]");
        }

        foreach (var arg in args)
        {
            if (ForbiddenOptions.Any(option => arg == option || arg.StartsWith(option + "=", StringComparison.Ordinal)))
            {
                throw new RefusedException("the service supplies the account and state directory; omit " + arg.Split('=')[0]);
            }
        }

        return (JsonValue.Create(script), args);
    }

    private static string Authorise(JsonNode script, string? command, string? mode)
    {
        var name = PyNode.AsString(script)!;
        if (command is not null && NotHostedCommands.TryGetValue((name, command), out var why))
        {
            throw new UnavailableException(name + " " + command + " is not available hosted: " + why);
        }

        var klass = CommandClass(name, command) ?? throw new RefusedException("unknown or unclassified subcommand");
        if (mode is null || !ModeClasses.TryGetValue(mode, out var allowed))
        {
            throw new RefusedException("unknown run mode");
        }

        if (!allowed.Contains(klass))
        {
            if (mode == RunModes.ThirdParty)
            {
                throw new RefusedException("a run started by someone other than the manager cannot use Margo's state");
            }

            throw new RefusedException("this records a decision by the manager, so it needs the manager in the conversation");
        }

        return klass;
    }

    private static Dictionary<string, string> Files(JsonNode? files)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (PyNode.IsNone(files))
        {
            return result;
        }

        if (files is not JsonObject named || named.Count > MaxFiles)
        {
            throw new RefusedException("files must be an object of up to " + MaxFiles + " named files");
        }

        long total = 0;
        foreach (var (name, content) in named)
        {
            if (!FileName().IsMatch(name) || PyNode.AsString(content) is not { } text)
            {
                throw new RefusedException("file names are letters, digits, '.', '_' or '-'; contents are text");
            }

            total += PyIO.EncodeUtf8(text).Length;
            result[name] = text;
        }

        if (total > MaxFilesTotal)
        {
            throw new RefusedException("files are too large");
        }

        return result;
    }

    /// <summary><c>_resolve</c>: replace <c>@file:NAME</c> and <c>@preferences</c>; refuse anything that looks like a path.</summary>
    private List<Arg> Resolve(List<string> args, IReadOnlyDictionary<string, string> files)
    {
        var resolved = new List<Arg>();
        foreach (var arg in args)
        {
            var option = "";
            var value = arg;
            var equals = arg.IndexOf('=', StringComparison.Ordinal);
            if (arg.StartsWith("--", StringComparison.Ordinal) && equals >= 0)
            {
                option = arg[..(equals + 1)];
                value = arg[(equals + 1)..];
            }

            if (value.StartsWith("@file:", StringComparison.Ordinal))
            {
                var name = value["@file:".Length..];
                if (!files.ContainsKey(name))
                {
                    throw new RefusedException("no file named " + PyRepr.Str(name) + " was passed in files");
                }

                resolved.Add(Arg.File(name, option));
            }
            else if (value == "@preferences")
            {
                if (Preferences is null || !File.Exists(Preferences))
                {
                    throw new UnavailableException("the manager's preferences are not set up yet");
                }

                resolved.Add(Arg.Plain(option + Preferences));
            }
            else if (value.StartsWith('/') || value.StartsWith('~') || value.StartsWith("./", StringComparison.Ordinal)
                     || value.StartsWith("..", StringComparison.Ordinal) || value.StartsWith('\\')
                     || value.Contains("/../", StringComparison.Ordinal) || value.Contains('\0', StringComparison.Ordinal))
            {
                throw new RefusedException("file paths are not available; pass content in files and use @file:NAME");
            }
            else
            {
                resolved.Add(Arg.Plain(option + value));
            }
        }

        return resolved;
    }

    private static byte[]? Stdin(JsonNode? value)
    {
        if (PyNode.IsNone(value))
        {
            return null;
        }

        var text = PyNode.AsString(value) ?? PyJson.Dumps(value, ensureAscii: false);
        var data = PyIO.EncodeUtf8(text);
        if (data.Length > MaxInput)
        {
            throw new RefusedException("input is too large");
        }

        return data;
    }

    // -- execution -------------------------------------------------------------------------------------

    private async Task<JsonObject> ExecuteAsync(string script, IReadOnlyList<Arg> args, byte[]? stdin,
                                                IReadOnlyDictionary<string, string> files, string klass, string? mode,
                                                CancellationToken cancellationToken)
    {
        var command = Stateful.Contains(script) && args.Count > 0 ? args[0].Value : null;
        var started = Stopwatch.StartNew();
        var scratch = Directory.CreateTempSubdirectory("margo-state-");
        try
        {
            var final = new List<string>();
            foreach (var arg in args)
            {
                if (arg.FileName is { } name)
                {
                    var path = Path.Combine(scratch.FullName, name);
                    await File.WriteAllBytesAsync(path, PyIO.EncodeUtf8(files[name]), cancellationToken).ConfigureAwait(false);
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    final.Add(arg.Option + path);
                }
                else
                {
                    final.Add(arg.Value!);
                }
            }

            var argv = new List<string>(_command) { script };
            if (Stateful.Contains(script))
            {
                argv.AddRange(["--account", Account, "--state-dir", StateRoot]);
            }

            argv.AddRange(final);
            foreach (var value in argv)
            {
                if (value.Contains('\0', StringComparison.Ordinal))
                {
                    throw new PyValueError("embedded null byte");
                }

                PyIO.EnsureEncodable(value);
            }

            if (_cliFile is not null && !File.Exists(_cliFile))
            {
                // Nothing ran: `python3 -s missing.py` would have failed the same way before touching state.
                return Finish(script, command, mode, "error", started: started,
                              reason: "could not start the command: FileNotFoundError");
            }

            return await RunProcessAsync(script, command, mode, klass, argv, stdin, scratch.FullName, started, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            try
            {
                scratch.Delete(recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Python's TemporaryDirectory cleanup is best effort here too: the result is already decided.
            }
        }
    }

    private async Task<JsonObject> RunProcessAsync(string script, string? command, string? mode, string klass,
                                                   List<string> argv, byte[]? stdin, string scratch, Stopwatch started,
                                                   CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(argv[0])
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = scratch,
        };
        foreach (var value in argv.Skip(1))
        {
            start.ArgumentList.Add(value);
        }

        start.Environment.Clear();
        foreach (var key in EnvironmentPassthrough)
        {
            if (PyRuntime.GetEnv(key) is { } value)
            {
                start.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            return Finish(script, command, mode, "error", started: started,
                          reason: "could not start the command: " + new PyOSError(ex.NativeErrorCode, "").PythonType);
        }

        var stdout = ReadAllAsync(process.StandardOutput.BaseStream);
        var stderr = ReadAllAsync(process.StandardError.BaseStream);
        var input = WriteAllAsync(process.StandardInput.BaseStream, stdin ?? []);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);
        try
        {
            await Task.WhenAll(stdout, stderr, input, process.WaitForExitAsync(deadline.Token)).WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Kill(process);
            var write = klass != StateCommandClasses.Read;
            if (cancellationToken.IsCancellationRequested)
            {
                Finish(script, command, mode, write ? "unknown" : "error", started: started,
                       reason: "cancelled" + (write ? "; the change may or may not have been saved" : ""));
                throw;
            }

            var seconds = ((long)Timeout.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Finish(script, command, mode, write ? "unknown" : "error", started: started,
                          reason: "timed out after " + seconds + "s" + (write ? "; the change may or may not have been saved" : ""));
        }

        return Outcome(script, command, mode, klass, process.ExitCode, await stdout.ConfigureAwait(false),
                       await stderr.ConfigureAwait(false), started);
    }

    private JsonObject Outcome(string script, string? command, string? mode, string klass, int exitCode, byte[] stdout,
                               byte[] stderr, Stopwatch started)
    {
        var output = Replacing.GetString(stdout);
        var error = Replacing.GetString(stderr);
        var truncated = PyRepr.Length(output) > MaxOutput;
        JsonNode? parsed = null;
        if (!truncated && PyRepr.Strip(output).Length > 0)
        {
            try
            {
                parsed = PyJson.Loads(output);
            }
            catch (PyValueError)
            {
                parsed = null;
            }
        }

        var reported = parsed as JsonObject;
        var reportsError = reported is { Count: > 0 } && reported.ContainsKey("error");
        if (exitCode == 0 && !reportsError)
        {
            var payload = new List<(string, JsonNode?)>();
            payload.Add(PyNode.IsNone(parsed) ? ("text", JsonValue.Create(PyText.Prefix(output, MaxOutput))) : ("result", parsed));
            if (truncated)
            {
                payload.Add(("note", "Output was too large; only the beginning is shown. Narrow the command."));
            }

            return Finish(script, command, mode, truncated ? "partial" : "ok", started: started, exitCode: exitCode, payload: payload);
        }

        JsonNode? reason = reported is { Count: > 0 } && reported.TryGetPropertyValue("error", out var stated) ? stated : null;
        if (!PyJson.Truthy(reason))
        {
            reason = FirstError(error);
        }

        var text = PyJson.Truthy(reason) ? PyNode.Str(reason) : "the command failed";
        // The scripts exit 2 after rolling back a refused change. Any other failure of a write may have happened
        // after a commit, so its effect is unknown.
        var status = klass == StateCommandClasses.Read || exitCode is 0 or 2 ? "error" : "unknown";
        return Finish(script, command, mode, status, started: started, exitCode: exitCode, reason: PyText.Prefix(text, 2000));
    }

    /// <summary><c>_first_error(stderr)</c>: the last non-empty line, or its <c>error</c> when it is a JSON object with one.</summary>
    internal static JsonNode? FirstError(string stderr)
    {
        var lines = PyRepr.SplitLines(PyRepr.Strip(stderr));
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var line = PyRepr.Strip(lines[i]);
            if (line.Length == 0)
            {
                continue;
            }

            JsonNode? value;
            try
            {
                value = PyJson.Loads(line);
            }
            catch (PyValueError)
            {
                return PyText.Prefix(line, 500);
            }

            if (value is JsonObject obj && obj.TryGetPropertyValue("error", out var error) && PyJson.Truthy(error))
            {
                return PyNode.Copy(error);
            }

            return PyText.Prefix(line, 500);
        }

        return null;
    }

    private JsonObject Finish(JsonNode? script, string? command, string? mode, string status, Stopwatch? started = null,
                              int? exitCode = null, string? reason = null, IEnumerable<(string Key, JsonNode? Value)>? payload = null) =>
        Finish(PyNode.AsString(script), command, mode, status, started, exitCode, reason, payload);

    private JsonObject Finish(string? script, string? command, string? mode, string status, Stopwatch? started = null,
                              int? exitCode = null, string? reason = null, IEnumerable<(string Key, JsonNode? Value)>? payload = null)
    {
        var receipt = new StateReceipt
        {
            At = StateStore.UtcNow(),
            Script = script,
            Command = command,
            Mode = mode,
            Status = status,
            ExitCode = exitCode,
            Ms = started?.ElapsedMilliseconds,
        };
        _recorder?.Invoke(receipt);
        var response = new JsonObject { ["status"] = status, ["script"] = script, ["command"] = command };
        if (!string.IsNullOrEmpty(reason))
        {
            response["reason"] = reason;
        }

        foreach (var (key, value) in payload ?? [])
        {
            response[key] = value;
        }

        return response;
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static async Task WriteAllAsync(Stream stream, byte[] data)
    {
        try
        {
            if (data.Length > 0)
            {
                await stream.WriteAsync(data).ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
            // communicate() ignores a child that stops reading its input.
        }
        finally
        {
            try
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (IOException)
            {
            }
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
        catch (InvalidOperationException)
        {
            // It exited on its own in the meantime.
        }
    }

    private static readonly UTF8Encoding Replacing = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex FileName();

    private static HashSet<string> Set(params string[] values) => new HashSet<string>(values, StringComparer.Ordinal);

    private static KeyValuePair<string, IReadOnlySet<string?>>[] Classes(string[] read, string[] owned, string[] human) =>
    [
        KeyValuePair.Create<string, IReadOnlySet<string?>>(StateCommandClasses.Read, new HashSet<string?>(read, StringComparer.Ordinal)),
        KeyValuePair.Create<string, IReadOnlySet<string?>>(StateCommandClasses.Private, new HashSet<string?>(owned, StringComparer.Ordinal)),
        KeyValuePair.Create<string, IReadOnlySet<string?>>(StateCommandClasses.Human, new HashSet<string?>(human, StringComparer.Ordinal)),
    ];

    private static KeyValuePair<string, IReadOnlySet<string?>>[] Stateless() =>
        [KeyValuePair.Create<string, IReadOnlySet<string?>>(StateCommandClasses.Read, new HashSet<string?>([null], StringComparer.Ordinal))];

    /// <summary>A resolved argument: plain text, or a named file written to the call's private directory.</summary>
    private sealed record Arg(string? Value, string? FileName, string Option)
    {
        public static Arg Plain(string value) => new(value, null, "");

        public static Arg File(string name, string option) => new(null, name, option);
    }

    private class RefusedException(string message) : Exception(message);

    /// <summary>Classified as not hosted yet; reported as unavailable, not as a refusal or an empty result.</summary>
    private sealed class UnavailableException(string message) : RefusedException(message);
}

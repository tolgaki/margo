global using Xunit;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Margo.Cloud.Core;
using Margo.Cloud.Guard;
using Margo.Cloud.State.Core;
using Margo.Cloud.WorkIQ;

// Private state relies on POSIX modes; Margo Cloud runs on Linux containers.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]

namespace Margo.Cloud.Engine.Tests;

/// <summary>Fictional identities and paths shared by the tests (<c>BINDING</c>, <c>ROOT</c> in the Python tests).</summary>
internal static class Fixtures
{
    public static InstanceBinding Binding { get; } = new("agent-identity-1", "margo-user-1", "principal-1", "chat-principal-1");

    /// <summary>The repository root (holds <c>agents/margo.agent.md</c> and <c>cloud/Margo.Cloud.slnx</c>).</summary>
    public static string Root { get; } = FindRoot();

    /// <summary>The <c>margo-state.dll</c> copied next to this test assembly (the test project references the CLI).</summary>
    public static string Cli { get; } = Path.Combine(AppContext.BaseDirectory, "margo-state.dll");

    /// <summary>The dotnet host running these tests.</summary>
    public static string Dotnet { get; } =
        Environment.ProcessPath is { } host && Path.GetFileNameWithoutExtension(host) == "dotnet" ? host : "dotnet";

    public static JsonNode? Json(string text) => PyJson.Loads(text);

    public static JsonObject Obj(string text) => (JsonObject)PyJson.Loads(text)!;

    public static string Dumps(JsonNode? value) => PyJson.Dumps(value);

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "agents", "margo.agent.md"))
                && File.Exists(Path.Combine(directory.FullName, "cloud", "Margo.Cloud.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("repository root not found above " + AppContext.BaseDirectory);
    }
}

/// <summary>
/// A small fictional persona and skill tree with the names and files the dispatch, turn and instruction cases ask for.
/// The cases compared with recorded Python answers read this tree, never the repository's skills, so editing a skill
/// does not invalidate them; <c>ParityTests</c> checks the real persona and skills directly.
/// </summary>
internal static class FixtureLibrary
{
    private static readonly (string Path, string Text)[] Files =
    [
        ("agents/margo.agent.md", "---\nname: margo\ndescription: Fictional persona\n---\n\n# Margo (fictional)\n\nA fictional chief of staff for tests.\n"),
        ("skills/chief-of-staff/SKILL.md", "---\nname: chief-of-staff\ndescription: Fictional chief of staff skill\n---\n\n# Chief of staff\n\nRead `references/daily-brief.md`.\n"),
        ("skills/chief-of-staff/commitments.md", "ledger only, never served\n"),
        ("skills/chief-of-staff/preferences.md", "repository copy, never served\n"),
        ("skills/chief-of-staff/references/daily-brief.md", "# Daily brief\n\nFictional steps.\n"),
        ("skills/chief-of-staff/references/DAILY-BRIEF.md", "# Upper-case twin\n"),
        ("skills/chief-of-staff/README.md", "# Readme\n"),
        ("skills/chief-of-staff/scripts/README.md", "scripts are not served\n"),
        ("skills/chief-of-staff/scripts/work_state.py", "print('fictional')\n"),
        ("skills/chief-of-staff/state/x.md", "state is not served\n"),
        ("skills/decision-log/SKILL.md", "---\nname: decision-log\ndescription: Fictional decision log\n---\n\nDecision log body é\n"),
        ("skills/decision-log/config.md", "fictional config\n"),
        ("skills/decision-log/references/schema.md", "# Schema\n\nFictional schema.\n"),
    ];

    /// <summary>Writes the tree under <paramref name="root"/> and returns <paramref name="root"/>.</summary>
    public static string Create(string root)
    {
        foreach (var (relative, text) in Files)
        {
            var path = System.IO.Path.Combine(root, relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(text));
        }

        return root;
    }
}

/// <summary><c>FakeTokens</c>: each subject's token is <c>subject + "-token"</c>.</summary>
internal sealed class FakeTokens : ITokenProvider
{
    public List<string> Requested { get; } = [];

    public string TokenFor(string subject)
    {
        Requested.Add(subject);
        return subject + "-token";
    }
}

/// <summary><c>RecordingTransport</c>: records each call and answers with a copy of the scripted payload.</summary>
internal sealed class RecordingTransport(string result = """{"statusCode": 200, "data": {"value": []}}""") : IWorkIQTransport
{
    private readonly JsonObject _result = Fixtures.Obj(result);

    public string Endpoint => WorkIQEndpoints.McpUrl;

    public List<(string Tool, JsonObject Arguments, string Bearer)> Calls { get; } = [];

    public Task<JsonObject> CallAsync(string tool, JsonObject arguments, string bearer, CancellationToken cancellationToken = default)
    {
        Calls.Add((tool, PyNode.CopyObject(arguments), bearer));
        return Task.FromResult(PyNode.CopyObject(_result));
    }
}

/// <summary><c>FakeModel</c>: scripted Responses API; each create returns the next scripted response or throws it.</summary>
internal sealed class FakeModel(params object?[] script) : IModelClient
{
    private readonly Queue<object?> _script = new(script);

    public List<JsonObject> Requests { get; } = [];

    public Task<JsonNode?> CreateResponseAsync(JsonObject request, CancellationToken cancellationToken = default)
    {
        Requests.Add(PyNode.CopyObject(request));
        var step = _script.Dequeue();
        return step switch
        {
            Exception error => Task.FromException<JsonNode?>(error),
            string text => Task.FromResult(PyJson.Loads(text)),
            JsonNode node => Task.FromResult<JsonNode?>(PyNode.Copy(node)),
            _ => Task.FromResult<JsonNode?>(null),
        };
    }

    /// <summary><c>call(name, arguments, call_id)</c>: a function call item with JSON arguments.</summary>
    public static JsonObject Call(string name, string arguments, string callId = "c1") => new()
    {
        ["type"] = "function_call",
        ["name"] = name,
        ["arguments"] = PyJson.Dumps(PyJson.Loads(arguments)),
        ["call_id"] = callId,
    };

    /// <summary><c>final(text, response_id)</c>.</summary>
    public static JsonObject Final(string text, string responseId = "r-final") => new()
    {
        ["id"] = responseId,
        ["output"] = new JsonArray(new JsonObject
        {
            ["type"] = "message",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text }),
        }),
    };

    public static JsonObject Response(string id, params JsonNode[] output) => new() { ["id"] = id, ["output"] = new JsonArray(output) };
}

/// <summary>
/// A private temporary directory, with the environment the state CLI needs for it:
/// <c>MARGO_ALLOW_UNSAFE_STATE_DIR=1</c> (temporary directories live under a world-writable /tmp). Create it inside
/// the test method so the override flows with the test.
/// </summary>
internal sealed class TempRoot : IDisposable
{
    private readonly IDisposable _environment;

    public TempRoot()
    {
        Path = Directory.CreateTempSubdirectory("margo-engine-test-").FullName;
        File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var environment = PyRuntime.EnvironmentSnapshot();
        environment["MARGO_ALLOW_UNSAFE_STATE_DIR"] = "1";
        _environment = PyRuntime.Override(environment);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        _environment.Dispose();
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Runs a program and returns its exit code and output.</summary>
internal static class Programs
{
    public static (int Status, string Stdout, string Stderr) Run(string program, IEnumerable<string> arguments, string? stdin = null,
                                                                   IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[key] = value;
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.StandardInput.Write(stdin ?? "");
        process.StandardInput.Close();
        if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(program + " did not finish");
        }

        return (process.ExitCode, output.Result, error.Result);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<(int, string, string)>> CliRuns = new(StringComparer.Ordinal);

    /// <summary>
    /// <c>margo-state ARGS</c> with the built CLI, for read-only invocations such as <c>--help</c>. Each distinct
    /// invocation runs once per test run, so the parity checks start few processes alongside other test assemblies.
    /// </summary>
    public static (int Status, string Stdout, string Stderr) Cli(params string[] arguments) =>
        CliRuns.GetOrAdd(string.Join('\0', arguments), _ => new Lazy<(int, string, string)>(() => Run(Fixtures.Dotnet, [Fixtures.Cli, .. arguments]))).Value;

    /// <summary>The python3 on PATH; the Python modules are the specification these tests compare against.</summary>
    public static string Python()
    {
        var python = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => System.IO.Path.Combine(directory, "python3"))
            .FirstOrDefault(File.Exists);
        Assert.True(python is not null, "python3 is needed to compare with the Python engine (the specification)");
        return python;
    }

    /// <summary>
    /// What the Python engine, skills, parity and statecmd modules answered to this request. Those modules (the
    /// specification) were removed at the cutover; their answers to every request these tests ask were recorded first
    /// in <c>Parity/engine_answers.jsonl</c> (see <c>Margo.Cloud.Engine/PORTING.md</c>).
    /// </summary>
    public static JsonObject AskPython(JsonObject request) => EngineOracle.Ask(request);

    private static readonly Lazy<JsonObject> DesktopSubcommands = new(() =>
    {
        var script = System.IO.Path.Combine(AppContext.BaseDirectory, "Parity", "subcommands.py");
        var (status, stdout, stderr) = Run(Python(), ["-B", "-I", script, Fixtures.Root, .. StateCommands.Stateful],
                                           environment: new Dictionary<string, string> { ["PYTHONDONTWRITEBYTECODE"] = "1", ["PYTHONIOENCODING"] = "utf-8" });
        Assert.True(status == 0, "subcommands.py failed:\n" + stderr);
        return (JsonObject)PyJson.Loads(stdout)!;
    });

    /// <summary>The subcommands each stateful script's parser accepts, read live from the desktop Python helpers.</summary>
    public static JsonObject Subcommands() => DesktopSubcommands.Value;
}

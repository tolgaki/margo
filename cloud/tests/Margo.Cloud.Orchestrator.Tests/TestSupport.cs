global using Xunit;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Margo.Cloud.Core;
using Margo.Cloud.Engine;
using Margo.Cloud.State.Core;
using Margo.Cloud.WorkIQ;
using Microsoft.Extensions.Logging;

// Private state relies on POSIX modes; Margo Cloud runs on Linux containers.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]

namespace Margo.Cloud.Orchestrator.Tests;

/// <summary>Fictional identities and paths shared by the tests (<c>BINDING</c>, <c>ROOT</c> in the Python tests).</summary>
internal static class Fixtures
{
    public static InstanceBinding Binding { get; } = new("agent-identity-1", "margo-user-1", "principal-1", "chat-principal-1");

    /// <summary>The repository root (holds <c>agents/</c>, <c>skills/</c> and the Python specification).</summary>
    public static string Root { get; } = FindRoot();

    /// <summary>The <c>margo-state.dll</c> copied next to this test assembly (the test project references the CLI).</summary>
    public static string Cli { get; } = Path.Combine(AppContext.BaseDirectory, "margo-state.dll");

    /// <summary>The dotnet host running these tests.</summary>
    public static string Dotnet { get; } =
        Environment.ProcessPath is { } host && Path.GetFileNameWithoutExtension(host) == "dotnet" ? host : "dotnet";

    public static JsonObject Obj(string text) => (JsonObject)PyJson.Loads(text)!;

    /// <summary>The <c>python3</c> on PATH.</summary>
    public static string Python()
    {
        var python = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, "python3"))
            .FirstOrDefault(File.Exists);
        Assert.True(python is not null, "python3 is needed for the Python specification");
        return python;
    }

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
/// The <c>margo-state</c> command the orchestrator tests run: the real C# CLI. While that CLI does not implement
/// <c>memory_state.py</c> yet (<c>Margo.Cloud.State.MemoryCommands</c> is not ported on this branch), a small shell shim
/// sends that one script to the Python helper, the specification, against the same state directory; every other script
/// runs in the C# CLI. Once the CLI implements it, the shim is not used and everything runs in C#.
/// </summary>
internal static class StateCli
{
    private static readonly Lazy<(IReadOnlyList<string> Command, bool MemoryStateInPython)> Resolved = new(Resolve);

    /// <summary>The command passed as <see cref="OrchestratorOptions.StateCliCommand"/>.</summary>
    public static IReadOnlyList<string> Command => Resolved.Value.Command;

    /// <summary>Whether <c>memory_state.py</c> is served by the Python helper through the shim.</summary>
    public static bool MemoryStateInPython => Resolved.Value.MemoryStateInPython;

    /// <summary>The real C# CLI alone, with no shim.</summary>
    public static IReadOnlyList<string> CSharpOnly => [Fixtures.Dotnet, Fixtures.Cli];

    /// <summary>Whether the C# CLI implements a script (<c>margo-state SCRIPT --help</c> exits 0).</summary>
    public static bool Implements(string script) => Programs.Run(Fixtures.Dotnet, [Fixtures.Cli, script, "--help"]).Status == 0;

    private static (IReadOnlyList<string>, bool) Resolve()
    {
        if (Implements("memory_state.py"))
        {
            return (CSharpOnly, false);
        }

        // Next to the test assembly (build output), so nothing is left behind elsewhere.
        var shim = Path.Combine(AppContext.BaseDirectory, "margo-state-memory-shim.sh");
        var scripts = Path.Combine(Fixtures.Root, "skills", "chief-of-staff", "scripts");
        File.WriteAllText(shim, $"""
            case "$1" in
              memory_state.py) shift; exec '{Fixtures.Python()}' -B -s '{scripts}/memory_state.py' "$@" ;;
            esac
            exec '{Fixtures.Dotnet}' '{Fixtures.Cli}' "$@"

            """);
        return (["/bin/sh", shim], true);
    }
}

/// <summary>Runs a program and returns its exit code and output.</summary>
internal static class Programs
{
    public static (int Status, string Stdout, string Stderr) Run(string program, IEnumerable<string> arguments) =>
        RunWithInput(program, arguments, null);

    /// <summary>Runs a program with <paramref name="input"/> (UTF-8) on its standard input.</summary>
    public static (int Status, string Stdout, string Stderr) RunWithInput(string program, IEnumerable<string> arguments, string? input)
    {
        var start = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        if (input is not null)
        {
            process.StandardInput.Write(input);
            process.StandardInput.Close();
        }

        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(program + " did not finish");
        }

        return (process.ExitCode, output.Result, error.Result);
    }
}

/// <summary><c>FakeModel</c>: scripted Responses API; each create returns the next scripted response or throws it.</summary>
internal sealed class FakeModel(params object?[] script) : IModelClient
{
    private readonly Queue<object?> _script = new(script);
    private readonly List<JsonObject> _requests = [];

    public IReadOnlyList<JsonObject> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public Task<JsonNode?> CreateResponseAsync(JsonObject request, CancellationToken cancellationToken = default)
    {
        object? step;
        lock (_requests)
        {
            _requests.Add(PyNode.CopyObject(request));
            step = _script.Dequeue();
        }

        return step switch
        {
            Exception error => Task.FromException<JsonNode?>(error),
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

    /// <summary>The <c>output</c> of the <paramref name="index"/>-th function call output in a follow-up request, parsed.</summary>
    public static JsonObject Output(JsonObject request, int index = 0) =>
        (JsonObject)PyJson.Loads(PyNode.AsString(request["input"]![index]!["output"])!)!;
}

/// <summary><c>DirectoryTransport</c>: answers Margo's <c>/me/manager</c> lookup like Work IQ; records every call.</summary>
internal sealed class DirectoryTransport : IWorkIQTransport
{
    private readonly List<(string Tool, JsonObject Arguments, string Bearer)> _calls = [];

    /// <param name="hasManager">False when the directory has no manager (it answers 404).</param>
    /// <param name="lookupFailure">An exception to throw, or a payload to answer, for the lookup.</param>
    /// <param name="managerId">The manager's object ID.</param>
    /// <param name="managerName">The manager's display name.</param>
    public DirectoryTransport(bool hasManager = true, object? lookupFailure = null, string managerId = "principal-1", string managerName = "Dana")
    {
        Manager = hasManager ? (managerId, managerName) : null;
        LookupFailure = lookupFailure;
    }

    public (string Id, string Name)? Manager { get; set; }

    public object? LookupFailure { get; set; }

    public string Endpoint => WorkIQEndpoints.McpUrl;

    public IReadOnlyList<(string Tool, JsonObject Arguments, string Bearer)> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    public Task<JsonObject> CallAsync(string tool, JsonObject arguments, string bearer, CancellationToken cancellationToken = default)
    {
        lock (_calls)
        {
            _calls.Add((tool, PyNode.CopyObject(arguments), bearer));
        }

        if (tool == "fetch" && PyNode.AsString(arguments["entityUrls"]?[0])?.StartsWith("/me/manager", StringComparison.Ordinal) == true)
        {
            switch (LookupFailure)
            {
                case Exception error:
                    return Task.FromException<JsonObject>(error);
                case JsonObject payload:
                    return Task.FromResult(PyNode.CopyObject(payload));
            }

            if (Manager is not { } manager)
            {
                return Task.FromResult(Fixtures.Obj("""{"results": [{"statusCode": 404, "data": {}}]}"""));
            }

            return Task.FromResult(new JsonObject
            {
                ["results"] = new JsonArray(new JsonObject
                {
                    ["statusCode"] = 200,
                    ["data"] = new JsonObject { ["id"] = manager.Id, ["displayName"] = manager.Name },
                }),
            });
        }

        return Task.FromResult(Fixtures.Obj("""{"statusCode": 200, "data": {"value": []}}"""));
    }
}

/// <summary>
/// A private temporary directory with a complete environment override for the async flow: <c>PATH</c>, a private
/// <c>HOME</c>, and <c>MARGO_ALLOW_UNSAFE_STATE_DIR=1</c> (temporary directories live under a world-writable /tmp, as the
/// Python tests' checkout-relative roots sit under a <c>.git</c> directory). Create it inside the test method.
/// </summary>
internal sealed class TempState : IDisposable
{
    private readonly IDisposable _environment;

    public TempState()
    {
        Path = Directory.CreateTempSubdirectory("margo-orchestrator-test-").FullName;
        File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var home = Combine("home");
        Directory.CreateDirectory(home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin",
            ["HOME"] = home,
            ["MARGO_ALLOW_UNSAFE_STATE_DIR"] = "1",
            ["MARGO_CONFIG"] = Combine("missing-config.json"),
        };
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { } dotnetRoot)
        {
            environment["DOTNET_ROOT"] = dotnetRoot;
        }

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

/// <summary>Collects log entries (Python's <c>assertLogs("margo.autopilot")</c>).</summary>
internal sealed class CapturingLogger : ILogger
{
    private readonly List<(LogLevel Level, string Message, Exception? Exception)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_entries)
        {
            _entries.Add((logLevel, formatter(state, exception), exception));
        }
    }
}

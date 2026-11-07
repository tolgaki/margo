using Margo.Cloud.State.Core;

// Private state relies on POSIX modes, ownership and link counts; Margo Cloud runs on Linux containers.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]

namespace Margo.Cloud.State.Tests;

/// <summary>
/// A private temporary root with an isolated environment, like the Python tests' setUp:
/// MARGO_ALLOW_UNSAFE_STATE_DIR=1 (temporary directories live under a world-writable /tmp), a missing
/// MARGO_CONFIG and a private HOME. Create it inside the test method so the environment override
/// flows with the test.
/// </summary>
internal sealed class TempRoot : IDisposable
{
    private readonly IDisposable _environment;

    private TempRoot(string path, IDisposable environment)
    {
        Path = path;
        _environment = environment;
    }

    public string Path { get; }

    public string State => System.IO.Path.Combine(Path, "state");

    public static TempRoot Create(IReadOnlyDictionary<string, string>? extra = null, bool keepAccount = false)
    {
        var path = Directory.CreateTempSubdirectory("margo-state-test-").FullName;
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var home = System.IO.Path.Combine(path, "home");
        Directory.CreateDirectory(home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin",
            ["HOME"] = home,
            ["MARGO_ALLOW_UNSAFE_STATE_DIR"] = "1",
            ["MARGO_CONFIG"] = System.IO.Path.Combine(path, "missing-config.json"),
        };
        if (keepAccount && Environment.GetEnvironmentVariable("MARGO_ACCOUNT") is { } account)
        {
            environment["MARGO_ACCOUNT"] = account;
        }

        foreach (var (key, value) in extra ?? new Dictionary<string, string>())
        {
            environment[key] = value;
        }

        return new TempRoot(path, PyRuntime.Override(environment));
    }

    /// <summary>Replaces the environment for the rest of the scope (changes on top of the current one).</summary>
    public static IDisposable With(params (string Name, string? Value)[] changes)
    {
        var environment = PyRuntime.EnvironmentSnapshot();
        foreach (var (name, value) in changes)
        {
            if (value is null)
            {
                environment.Remove(name);
            }
            else
            {
                environment[name] = value;
            }
        }

        return PyRuntime.Override(environment);
    }

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

internal static class Modes
{
    public static string Of(string path) => Convert.ToString((int)PosixNative.Stat(path).Permissions, 8);

    public static void Set(string path, string octal) => File.SetUnixFileMode(path, (UnixFileMode)Convert.ToInt32(octal, 8));
}

internal static class ScriptRunner
{
    public static (int Code, string Stdout, string Stderr) Run(IStateScript script, IReadOnlyList<string> args, string stdin = "")
    {
        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };
        var code = script.Run(args, new StringReader(stdin), stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }
}

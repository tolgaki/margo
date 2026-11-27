using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Margo.Cloud.State.Core;
using Margo.Cloud.StateCli;
using Xunit;

namespace Margo.Cloud.State.Tests;

/// <summary>
/// Contract probes (<see cref="IStateProbeScript"/>, in <c>Margo.Cloud.State.Probes</c>) run arbitrary SQL and write
/// files. <c>margo-state</c> runs one only when <c>MARGO_STATE_PROBES=1</c>, and a Release build contains none.
/// </summary>
public sealed class ProbeIsolationTests
{
    private const string ProbeRefusal = "usage: margo-state SCRIPT.py [ARGS ...]\nmargo-state: error: 'state_core_probe.py' is a contract probe,"
                                        + " not a Margo command (set MARGO_STATE_PROBES=1 to run it for the parity judge)\n";

    private static (int Code, string Stdout, string Stderr) Cli(string? probes, params string[] args)
    {
        var environment = PyRuntime.EnvironmentSnapshot();
        environment.Remove(StateCliHost.ProbesVariable);
        if (probes is not null)
        {
            environment[StateCliHost.ProbesVariable] = probes;
        }

        using var scope = PyRuntime.Override(environment);
        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };
        var code = StateCliHost.Run(args, new StringReader("[]"), stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("")]
    [InlineData("true")]
    [InlineData(" 1")]
    public void Probes_are_refused_unless_enabled(string? setting)
    {
        Assert.Contains("state_core_probe.py", StateCliHost.Scripts.Keys); // this (test) build has them
        var (code, stdout, stderr) = Cli(setting, "state_core_probe.py", "loads");
        Assert.Equal((2, "", ProbeRefusal), (code, stdout, stderr));
        (code, stdout, stderr) = Cli(setting, "/any/path/state_core_probe.py", "loads");
        Assert.Equal(2, code);
        Assert.Equal("", stdout);
        Assert.Contains("'/any/path/state_core_probe.py' is a contract probe", stderr, StringComparison.Ordinal);

        // Neither the help nor the unknown-script message offers a probe.
        (code, stdout, _) = Cli(setting, "--help");
        Assert.Equal(0, code);
        Assert.Contains("margo_store.py", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("_probe.py", stdout, StringComparison.Ordinal);
        (code, _, stderr) = Cli(setting, "nope.py");
        Assert.Equal(2, code);
        Assert.Contains("unknown script 'nope.py' (choose from ", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("_probe.py", stderr, StringComparison.Ordinal);

        // Margo commands are unaffected.
        (code, stdout, _) = Cli(setting, "margo_store.py", "--help");
        Assert.Equal(0, code);
        Assert.StartsWith("usage: margo_store.py", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Probes_run_when_enabled()
    {
        var (code, stdout, stderr) = Cli("1", "state_core_probe.py", "loads");
        Assert.Equal((0, "", ""), (code, stdout, stderr));
        (code, stdout, _) = Cli("1", "--help");
        Assert.Equal(0, code);
        Assert.Contains("state_core_probe.py", stdout, StringComparison.Ordinal);
        Assert.Contains("work_ledger_probe.py", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_probe_lives_in_the_probes_assembly()
    {
        foreach (var script in StateCliHost.Scripts.Values)
        {
            var probe = script is IStateProbeScript;
            Assert.Equal(probe, script.ScriptName.EndsWith("_probe.py", StringComparison.Ordinal));
            Assert.Equal(probe, script.GetType().Assembly.GetName().Name == "Margo.Cloud.State.Probes");
        }
    }

    [Fact]
    public async Task Release_build_of_margo_state_contains_no_probes()
    {
        var cloud = CloudDirectory();
        var artifacts = Directory.CreateTempSubdirectory("margo-state-release-");
        try
        {
            var project = Path.Combine(cloud, "src", "Margo.Cloud.StateCli", "Margo.Cloud.StateCli.csproj");
            var (status, output) = await RunAsync("dotnet", ["build", project, "-c", "Release", "--artifacts-path", artifacts.FullName, "-nologo", "-v", "q"]);
            Assert.True(status == 0, "dotnet build -c Release failed:\n" + output);

            var bin = Path.Combine(artifacts.FullName, "bin", "Margo.Cloud.StateCli", "release");
            Assert.True(File.Exists(Path.Combine(bin, "margo-state.dll")), "no margo-state.dll in " + bin);
            Assert.False(File.Exists(Path.Combine(bin, "Margo.Cloud.State.Probes.dll")));
            var assemblies = Directory.GetFiles(bin, "*.dll").Where(path => Path.GetFileName(path).StartsWith("Margo.Cloud.", StringComparison.Ordinal)
                                                                            || Path.GetFileName(path) == "margo-state.dll").ToList();
            Assert.Contains(assemblies, path => Path.GetFileName(path) == "Margo.Cloud.State.Work.dll");
            foreach (var assembly in assemblies)
            {
                Assert.Empty(ProbeTypes(assembly));
            }

            // Even with probes enabled, the Release CLI does not know them.
            (status, output) = await RunAsync("dotnet", [Path.Combine(bin, "margo-state.dll"), "--help"], probes: true);
            Assert.Equal(0, status);
            Assert.Contains("work_state.py", output, StringComparison.Ordinal);
            Assert.DoesNotContain("_probe.py", output, StringComparison.Ordinal);
            (status, output) = await RunAsync("dotnet", [Path.Combine(bin, "margo-state.dll"), "state_core_probe.py", "loads"], probes: true);
            Assert.Equal(2, status);
            Assert.Contains("margo-state: error: unknown script 'state_core_probe.py'", output, StringComparison.Ordinal);
        }
        finally
        {
            artifacts.Delete(recursive: true);
        }
    }

    /// <summary>Types named like a probe, or implementing <see cref="IStateProbeScript"/>, read from metadata without loading.</summary>
    private static List<string> ProbeTypes(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var found = new List<string>();
        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            if ((type.Attributes & System.Reflection.TypeAttributes.Interface) != 0)
            {
                continue; // State.Core defines the IStateProbeScript marker itself
            }

            var name = reader.GetString(type.Name);
            var implementsProbe = type.GetInterfaceImplementations()
                                      .Select(implementation => reader.GetInterfaceImplementation(implementation).Interface)
                                      .Any(interfaceHandle => InterfaceName(reader, interfaceHandle) == nameof(IStateProbeScript));
            if (implementsProbe || name.Contains("ProbeScript", StringComparison.Ordinal))
            {
                found.Add(Path.GetFileName(path) + ": " + reader.GetString(type.Namespace) + "." + name);
            }
        }

        return found;
    }

    private static string? InterfaceName(MetadataReader reader, EntityHandle handle) => handle.Kind switch
    {
        HandleKind.TypeReference => reader.GetString(reader.GetTypeReference((TypeReferenceHandle)handle).Name),
        HandleKind.TypeDefinition => reader.GetString(reader.GetTypeDefinition((TypeDefinitionHandle)handle).Name),
        _ => null,
    };

    private static async Task<(int Status, string Output)> RunAsync(string program, IEnumerable<string> arguments, bool probes = false)
    {
        var start = new ProcessStartInfo(program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // A nested build must not inherit the test host's MSBuild settings; no telemetry either.
        foreach (var name in start.Environment.Keys.Where(key => key.StartsWith("MSBUILD", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            start.Environment.Remove(name);
        }

        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment.Remove(StateCliHost.ProbesVariable);
        if (probes)
        {
            start.Environment[StateCliHost.ProbesVariable] = "1";
        }

        using var process = Process.Start(start)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return (process.ExitCode, await stdout + await stderr);
    }

    private static string CloudDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Margo.Cloud.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("cloud/Margo.Cloud.slnx not found above " + AppContext.BaseDirectory);
    }
}

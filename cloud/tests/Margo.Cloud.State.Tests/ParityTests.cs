using System.Diagnostics;
using Xunit;

namespace Margo.Cloud.State.Tests;

/// <summary>Runs the differential judge (tools/state_parity.py --check) against the built margo-state CLI.</summary>
public sealed class ParityTests
{
    [Fact]
    public async Task State_parity_check_passes()
    {
        var repository = FindRepository();
        var python = FindOnPath("python3");
        if (python is null)
        {
            Assert.Skip("python3 is not installed; the parity judge needs CPython");
        }

        // tests/Margo.Cloud.State.Tests/bin/<Configuration>/net10.0 -> src/Margo.Cloud.StateCli/bin/<Configuration>/net10.0.
        // The judge needs the contract probes, which only a build with MargoStateProbes=true (the Debug default) has.
        var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var cli = new[] { output.Parent!.Name, "Debug" }
            .Select(configuration => Path.Combine(repository, "cloud", "src", "Margo.Cloud.StateCli", "bin", configuration, output.Name, "margo-state.dll"))
            .FirstOrDefault(path => File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "Margo.Cloud.State.Probes.dll")));
        Assert.True(cli is not null, "no margo-state built with the contract probes under cloud/src/Margo.Cloud.StateCli/bin "
                                     + "(build Debug, or pass -p:MargoStateProbes=true)");

        var start = new ProcessStartInfo(python)
        {
            WorkingDirectory = repository,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "-s", Path.Combine(repository, "tools", "state_parity.py"), "--check", "--verbose", "--cli", cli })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        // 219 scenarios take about 17 minutes on a loaded 4-core runner (memory-commands added about 2).
        timeout.CancelAfter(TimeSpan.FromMinutes(40));
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

        var report = await stdout + await stderr;
        Assert.True(process.ExitCode == 0, "state_parity.py --check failed:\n" + report);
        Assert.Contains(" 0 failed", report, StringComparison.Ordinal);
    }

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "tools", "state_parity.py")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("repository root with tools/state_parity.py not found above " + AppContext.BaseDirectory);
    }

    private static string? FindOnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, name))
            .FirstOrDefault(File.Exists);
}

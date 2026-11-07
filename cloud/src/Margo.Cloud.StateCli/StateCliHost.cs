using System.Reflection;
using System.Runtime.Loader;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.StateCli;

/// <summary>
/// <c>margo-state SCRIPT.py ARGS...</c>: runs the C# port of one Python state helper with the same arguments,
/// stdin, stdout, stderr and exit status as <c>python3 SCRIPT.py ARGS...</c>. Scripts are discovered by
/// reflection over the <c>Margo.Cloud.State.*</c> assemblies next to this program, so each port only adds
/// an <see cref="IStateScript"/> class to its own project.
/// </summary>
public static class StateCliHost
{
    private static readonly Lazy<IReadOnlyDictionary<string, IStateScript>> Registry = new(() => StateScripts.Discover(StateAssemblies()));

    /// <summary>The discovered scripts, keyed by Python file name.</summary>
    public static IReadOnlyDictionary<string, IStateScript> Scripts => Registry.Value;

    /// <summary>Runs <c>margo-state</c>; returns the exit status (2 for usage errors and unknown scripts).</summary>
    public static int Run(IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stderr);
        if (args.Count == 0 || args[0] is "-h" or "--help")
        {
            var usage = "usage: margo-state SCRIPT.py [ARGS ...]\n";
            if (args.Count == 0)
            {
                stderr.Write(usage + "margo-state: error: the following arguments are required: SCRIPT.py\n");
                return 2;
            }

            stdout.Write(usage + "\nRuns a Margo state script: " + string.Join(", ", Scripts.Keys) + "\n");
            return 0;
        }

        var name = Path.GetFileName(args[0]);
        if (!Scripts.TryGetValue(name, out var script))
        {
            stderr.Write("usage: margo-state SCRIPT.py [ARGS ...]\nmargo-state: error: unknown script "
                         + PyRepr.Str(args[0]) + " (choose from " + string.Join(", ", Scripts.Keys.Select(PyRepr.Str)) + ")\n");
            return 2;
        }

        return StateScripts.RunGuarded(() => script.Run(args.Skip(1).ToList(), stdin, stdout, stderr), stderr);
    }

    /// <summary>The <c>Margo.Cloud.State.*</c> assemblies deployed with this program.</summary>
    public static IEnumerable<Assembly> StateAssemblies()
    {
        var assemblies = new List<Assembly> { typeof(IStateScript).Assembly };
        var directory = AppContext.BaseDirectory;
        foreach (var path in Directory.GetFiles(directory, "Margo.Cloud.State.*.dll").Order(StringComparer.Ordinal))
        {
            if (Path.GetFileNameWithoutExtension(path).EndsWith(".Tests", StringComparison.Ordinal))
            {
                continue; // a test host's output folder also holds the test assembly
            }

            var assemblyName = AssemblyName.GetAssemblyName(path);
            var loaded = AppDomain.CurrentDomain.GetAssemblies()
                                  .FirstOrDefault(a => AssemblyName.ReferenceMatchesDefinition(a.GetName(), assemblyName));
            assemblies.Add(loaded ?? AssemblyLoadContext.Default.LoadFromAssemblyName(assemblyName));
        }

        return assemblies.Distinct();
    }
}

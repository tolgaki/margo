using System.Reflection;
using System.Text;

namespace Margo.Cloud.State.Core;

/// <summary>
/// One Python state helper (for example <c>work_state.py</c>) ported to C#. <c>margo-state SCRIPT.py ARGS...</c>
/// dispatches to the implementation whose <see cref="ScriptName"/> matches; implementations are found by
/// reflection over the <c>Margo.Cloud.State.*</c> assemblies, so a new port only adds a class.
/// Implementations need a public parameterless constructor and must behave exactly like
/// <c>python3 SCRIPT.py ARGS...</c>: same stdout bytes, exit status and (non-)empty stderr.
/// </summary>
public interface IStateScript
{
    /// <summary>The Python file name, for example <c>margo_store.py</c>; also argparse's <c>prog</c>.</summary>
    string ScriptName { get; }

    /// <summary>Runs the script; returns the exit status. Never lets <see cref="PySystemExit"/> escape.</summary>
    int Run(IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr);
}

/// <summary>Discovery of <see cref="IStateScript"/> implementations and the Python process semantics around them.</summary>
public static class StateScripts
{
    /// <summary>Finds every concrete <see cref="IStateScript"/> in the given assemblies, keyed by script name.</summary>
    /// <exception cref="InvalidOperationException">Two implementations claim the same script name.</exception>
    public static IReadOnlyDictionary<string, IStateScript> Discover(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        var scripts = new SortedDictionary<string, IStateScript>(StringComparer.Ordinal);
        foreach (var assembly in assemblies.Distinct())
        {
            foreach (var type in LoadableTypes(assembly))
            {
                if (!type.IsClass || type.IsAbstract || !typeof(IStateScript).IsAssignableFrom(type)
                    || type.GetConstructor(Type.EmptyTypes) is null)
                {
                    continue;
                }

                var script = (IStateScript)Activator.CreateInstance(type)!;
                if (scripts.TryGetValue(script.ScriptName, out var existing))
                {
                    throw new InvalidOperationException("state script " + script.ScriptName + " is implemented twice: "
                                                        + existing.GetType().FullName + " and " + type.FullName);
                }

                scripts[script.ScriptName] = script;
            }
        }

        return scripts;
    }

    /// <summary>
    /// Runs a script body with Python's process semantics: <see cref="PySystemExit"/> becomes its status, and
    /// any other uncaught exception prints a traceback-style report to stderr and returns 1.
    /// </summary>
    public static int RunGuarded(Func<int> body, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(stderr);
        try
        {
            return body();
        }
        catch (PySystemExit exit)
        {
            return exit.Code;
        }
        catch (Exception ex)
        {
            var type = ex switch
            {
                PyException py => py.PythonType,
                InvalidOperationException => "RuntimeError",
                _ => ex.GetType().Name,
            };
            stderr.Write("Traceback (most recent call last):\n  (C# port)\n" + type + ": " + ex.Message + "\n");
            stderr.Flush();
            return 1;
        }
    }

    /// <summary><c>print(text, file=sys.stderr)</c>: stderr never fails to encode (Python uses backslashreplace).</summary>
    public static void PrintError(TextWriter stderr, string text)
    {
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length + 1);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                builder.Append(c).Append(text[++i]);
            }
            else if (char.IsSurrogate(c))
            {
                builder.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(c);
            }
        }

        stderr.Write(builder.Append('\n').ToString());
    }

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type is not null)!;
        }
    }
}

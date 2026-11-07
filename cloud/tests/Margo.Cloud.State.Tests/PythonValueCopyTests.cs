using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Margo.Cloud.State.Core;
using Xunit;

namespace Margo.Cloud.State.Tests;

/// <summary>
/// <see cref="PyNode.Copy"/> is the one copy of Python values. <c>JsonNode.DeepClone</c> re-serializes a
/// <see cref="BigInteger"/> value (a Python int beyond 64 bits, as <see cref="PyJson.Loads(string)"/> produces it)
/// through System.Text.Json reflection as <c>{"IsEven":true,...}</c>, and it recurses; the guard below keeps it out
/// of every state source file without an analyzer.
/// </summary>
public sealed partial class PythonValueCopyTests
{
    [Fact]
    public void Copies_keep_big_ints_floats_and_order_and_are_detached()
    {
        var value = PyJson.Loads("{\"big\": 1000000000000000000000000000000, \"neg\": [-99999999999999999999999, 1.0, -0.0, 1e400],"
                                 + " \"b\": {\"z\": true, \"a\": null, \"s\": \"\\u00e9\\ud800\"}}");
        var copy = PyNode.Copy(value);
        Assert.Equal(PyJson.Dumps(value), PyJson.Dumps(copy));
        Assert.Equal(BigInteger.Pow(10, 30), PyJson.Scalar(copy!["big"]));
        Assert.Null(copy.Parent);
        Assert.NotSame(value!["b"], copy["b"]);
        var target = new JsonObject { ["moved"] = PyNode.Copy(value["neg"]) }; // a node may have only one parent
        Assert.Equal("[-99999999999999999999999, 1.0, -0.0, inf]", PyNode.Repr(target["moved"]));
    }

    [Fact]
    public void Copies_are_iterative_at_the_deepest_json_python_accepts()
    {
        var deep = PyJson.Loads(new string('[', PyJson.MaxDepth) + "12345678901234567890123" + new string(']', PyJson.MaxDepth));
        var copy = PyNode.Copy(deep);
        var node = copy;
        for (var level = 1; level < PyJson.MaxDepth; level++)
        {
            node = node![0];
        }

        Assert.Equal(BigInteger.Parse("12345678901234567890123", System.Globalization.CultureInfo.InvariantCulture), PyJson.Scalar(node![0]));
    }

    [Fact]
    public void No_state_source_calls_DeepClone_outside_the_copy_helper()
    {
        var cloud = CloudDirectory();
        // PyNode.Copy itself never calls DeepClone either, so no file is exempt.
        var roots = Directory.GetDirectories(Path.Combine(cloud, "src"), "Margo.Cloud.State*")
                             .Append(Path.Combine(cloud, "tests", "Margo.Cloud.State.Tests"));
        var offenders = new List<string>();
        var scanned = 0;
        foreach (var root in roots)
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar);
                if (relative[0] is "bin" or "obj")
                {
                    continue;
                }

                scanned++;
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    var code = lines[i];
                    var comment = code.IndexOf("//", StringComparison.Ordinal);
                    if (comment >= 0)
                    {
                        code = code[..comment];
                    }

                    if (DeepCloneCall().IsMatch(code))
                    {
                        offenders.Add(Path.GetRelativePath(cloud, file) + ":" + (i + 1) + ": " + lines[i].Trim());
                    }
                }
            }
        }

        Assert.True(scanned > 50, "the guard scanned only " + scanned + " files; is the source tree where it expects?");
        Assert.True(offenders.Count == 0, "use PyNode.Copy for Python values, not JsonNode.DeepClone:\n" + string.Join("\n", offenders));
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

    [GeneratedRegex(@"\bDeep" + @"Clone\s*\(")]
    private static partial Regex DeepCloneCall();
}

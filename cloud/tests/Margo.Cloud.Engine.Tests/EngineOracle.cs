using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.Engine.Tests;

/// <summary>
/// The Python engine's recorded answers. Until the cutover these tests asked <c>engine_cases.py</c> (the Python engine,
/// skills, parity and statecmd modules) each question live; those modules were removed with the Python hosted runtime,
/// after every question the tests ask had been answered by them and recorded in <c>Parity/engine_answers.jsonl</c>.
/// A request is looked up by the SHA-256 of its JSON text, with the machine-specific paths in it (the repository and the
/// test's temporary directories) replaced by placeholders, so the same request finds its answer on any machine.
/// The generators are seeded, so the requests are the same on every run; a changed request has no recorded answer and
/// fails loudly (never a pass): such a test needs new expected values from the C# port, reviewed by hand.
/// </summary>
internal static partial class EngineOracle
{
    private static readonly Lazy<Dictionary<string, string>> Answers = new(Load);

    public static string AnswersPath { get; } = Path.Combine(AppContext.BaseDirectory, "Parity", "engine_answers.jsonl");

    public static JsonObject Ask(JsonObject request)
    {
        var (key, temporary) = Key(request);
        Assert.True(Answers.Value.TryGetValue(key, out var answer),
                    "no recorded Python answer for this request (" + key + "); the Python engine was removed at the cutover, so a "
                    + "changed request needs expected values written by hand (see Margo.Cloud.Engine/PORTING.md)");
        foreach (var (path, placeholder) in temporary)
        {
            answer = answer.Replace(placeholder, path, StringComparison.Ordinal);
        }

        return (JsonObject)PyJson.Loads(answer.Replace(RepoPlaceholder, Fixtures.Root, StringComparison.Ordinal))!;
    }

    private const string RepoPlaceholder = "$MARGO_REPO";

    /// <summary>The request's key, and the temporary directories in it with their placeholders.</summary>
    private static (string Key, List<(string Path, string Placeholder)> Temporary) Key(JsonObject request)
    {
        var text = PyJson.Dumps(request).Replace(Fixtures.Root, RepoPlaceholder, StringComparison.Ordinal);
        var temporary = new List<(string, string)>();
        text = Temporary().Replace(text, match =>
        {
            var index = temporary.FindIndex(pair => pair.Item1 == match.Value);
            if (index < 0)
            {
                temporary.Add((match.Value, "$MARGO_TMP" + temporary.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "$"));
                index = temporary.Count - 1;
            }

            return temporary[index].Item2;
        });
        return (Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))), temporary);
    }

    private static Dictionary<string, string> Load()
    {
        var answers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(AnswersPath, Encoding.UTF8))
        {
            if (line.Length == 0)
            {
                continue;
            }

            var entry = (JsonObject)PyJson.Loads(line)!;
            answers[PyNode.AsString(entry["key"])!] = PyNode.AsString(entry["answer"])!;
        }

        return answers;
    }

    [GeneratedRegex(@"/(?:[^""/\\]+/)*margo-engine-test-[A-Za-z0-9_]+")]
    private static partial Regex Temporary();
}

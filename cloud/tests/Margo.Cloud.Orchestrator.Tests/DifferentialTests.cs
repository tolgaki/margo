using System.Text;
using System.Text.Json.Nodes;
using Margo.Cloud.Core;
using Margo.Cloud.Guard;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.Orchestrator.Tests;

/// <summary>
/// Generated inputs answered by the Python helpers (recorded in <c>Parity/orchestrator_answers.json</c> before the cutover) and
/// by the port: the reply setting, the manager's commands, <c>set_reply_mode</c>, <c>_manager_from</c>, the office
/// excerpt and conversation key, and the reply gate. Seeded, so a failure names a reproducible case.
/// </summary>
public sealed class DifferentialTests
{
    private const int Seed = 20261107;

    private static readonly string[] Fragments =
    [
        "- **Replies to other people:** ", "* **Replies to other people:**", "**Replies to other people:**", "/replies", "/voice",
        "none", "acknowledge", "approve", "NONE", "Approve", "ac\u212Anowledge", "/vo\u0131ce", "/replie\u017F", "autonomous",
        "`", "-", "*", "_", " ", "  ", "\t", "\n", "\r\n", "\r", "\u000B", "\u000C", "\u001C", "\u0085", "\u00A0", "\u2028",
        "\u2029", "\u3000", "\u200B", "x", "Dana", "\uD83D\uDE00", "\u00E9", "#", ":", "\n\n",
    ];

    [Fact]
    public void Generated_inputs_behave_as_the_python_helpers_do()
    {
        var random = new Random(Seed);
        var cases = new JsonArray();
        for (var i = 0; i < 300; i++)
        {
            cases.Add(new JsonObject { ["kind"] = "policy", ["text"] = Text(random, 6) });
            cases.Add(new JsonObject { ["kind"] = "commands", ["text"] = random.Next(20) == 0 ? null : Text(random, 4) });
            cases.Add(new JsonObject
            {
                ["kind"] = "set_reply_mode",
                ["index"] = i,
                ["text"] = random.Next(8) == 0 ? null : Text(random, 8),
                ["mode"] = ReplyModes.All[random.Next(3)],
            });
            cases.Add(new JsonObject { ["kind"] = "manager", ["result"] = ManagerResult(random) });
            cases.Add(new JsonObject
            {
                ["kind"] = "excerpt",
                ["text"] = random.Next(10) == 0 ? null : Text(random, 3) + (random.Next(4) == 0 ? new string('y', 498) + Text(random, 3) : ""),
                ["sender"] = Pick(random, null, "", "colleague-1", "c\u00E9"),
                ["conversation"] = Pick(random, null, "", "conv-1", "conv\n2"),
            });
            cases.Add(GateCase(random));
        }

        using var state = new TempState();
        var digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(PyJson.Dumps(cases))));
        // The Python helpers' answers, recorded before the cutover removed them. The generator is seeded; a changed
        // generator has no recorded answers and fails here rather than passing.
        var recorded = (JsonObject)PyJson.Loads(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Parity", "orchestrator_answers.json")))!;
        Assert.True(PyNode.AsString(recorded["cases_sha256"]) == digest, "the generated cases changed; the recorded Python answers no longer apply");
        var python = (JsonArray)recorded["answers"]!;
        Assert.Equal(cases.Count, python.Count);

        var failures = new List<string>();
        for (var index = 0; index < cases.Count; index++)
        {
            var testCase = (JsonObject)cases[index]!;
            var expected = Normalise((JsonObject)python[index]!);
            var actual = Answer(testCase, state.Combine("csharp"));
            if (PyJson.Dumps(expected) != PyJson.Dumps(actual))
            {
                failures.Add(PyJson.Dumps(testCase) + "\n  python: " + PyJson.Dumps(expected) + "\n  c#:     " + PyJson.Dumps(actual));
            }
        }

        Assert.True(failures.Count == 0, failures.Count + " differences, first ones:\n" + string.Join("\n", failures.Take(8)));
    }

    private static JsonObject Normalise(JsonObject answer) => answer.ContainsKey("error") ? new JsonObject { ["error"] = true } : answer;

    private static JsonObject Answer(JsonObject testCase, string scratch)
    {
        try
        {
            return new JsonObject { ["ok"] = Run(testCase, scratch) };
        }
        catch (Exception exc) when (exc is not Xunit.Sdk.XunitException)
        {
            return new JsonObject { ["error"] = true };
        }
    }

    private static JsonNode? Run(JsonObject testCase, string scratch)
    {
        switch (PyNode.AsString(testCase["kind"]))
        {
            case "policy":
            {
                var policy = ReplyPolicy.Load(PyNode.AsString(testCase["text"]));
                return new JsonObject { ["mode"] = policy.Mode, ["issues"] = PyNode.StrList(policy.Issues) };
            }

            case "commands":
            {
                var text = PyNode.AsString(testCase["text"]);
                return new JsonObject { ["voice"] = ManagerCommands.IsVoice(text), ["mode"] = ManagerCommands.ReplyMode(text) };
            }

            case "set_reply_mode":
            {
                var path = Path.Combine(scratch, "case-" + PyJson.AsInt(testCase["index"]), "preferences.md");
                if (PyNode.AsString(testCase["text"]) is { } initial)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllBytes(path, Encoding.UTF8.GetBytes(initial));
                }

                ReplySetting.SetReplyMode(path, PyNode.AsString(testCase["mode"])!);
                return PyIO.DecodeUtf8(File.ReadAllBytes(path));
            }

            case "manager":
            {
                var result = (JsonObject)testCase["result"]!;
                var manager = ManagerDirectory.From(new GuardResult(PyNode.AsString(result["status"])!, "margo", "fetch",
                                                                    Data: result["data"] as JsonObject));
                return manager is null ? null : new JsonArray(manager.Id, manager.DisplayName);
            }

            case "excerpt":
            {
                // OfficeQueue.Record's excerpt and key, through a ledger-free copy of its first lines.
                var text = PyNode.AsString(testCase["text"]);
                var excerpt = PyText.Prefix(PyRepr.Strip(text ?? ""), OfficeQueue.ExcerptLimit);
                return new JsonObject
                {
                    ["excerpt"] = excerpt.Length == 0 ? "(no text)" : excerpt,
                    ["key"] = OfficeQueue.ConversationKey(PyNode.AsString(testCase["sender"]), PyNode.AsString(testCase["conversation"])),
                };
            }

            case "gate":
            {
                var gate = new ReplyGate(new InstanceBinding("agent-identity-1", "margo-user-1", "principal-1"), PyNode.AsString(testCase["mode"])!);
                var decisions = new JsonArray();
                foreach (var step in (JsonArray)testCase["steps"]!)
                {
                    var first = step!["first"] is { } flag ? (bool?)flag.GetValue<bool>() : null;
                    var decision = gate.Decide(PyNode.AsString(step["sender"]), PyNode.AsString(step["conversation"]), PyNode.AsString(step["proposed"]),
                                               PyNode.AsString(step["manager"]), firstInConversation: first);
                    var answer = new JsonObject { ["send"] = decision.Send };
                    if (decision.Send)
                    {
                        answer["text"] = decision.Text;
                    }

                    if (decision.Reason is not null)
                    {
                        answer["reason"] = decision.Reason;
                    }

                    decisions.Add(answer);
                }

                return decisions;
            }

            default:
                throw new InvalidOperationException("unknown case kind");
        }
    }

    private static JsonObject GateCase(Random random)
    {
        var steps = new JsonArray();
        for (var i = random.Next(1, 5); i > 0; i--)
        {
            steps.Add(new JsonObject
            {
                ["sender"] = Pick(random, null, "", "margo-user-1", "principal-1", "colleague-1", "colleague-2"),
                ["conversation"] = Pick(random, null, "", "conv-1", "conv-2"),
                ["proposed"] = Pick(random, null, "Proposed fictional reply"),
                ["manager"] = Pick(random, null, "", "  ", "Dana", " Dana\u00A0", "\u2003", "D\u00E9a\u0085"),
                ["first"] = random.Next(3) switch { 0 => null, 1 => true, _ => false },
            });
        }

        return new JsonObject { ["kind"] = "gate", ["mode"] = Pick(random, "none", "acknowledge"), ["steps"] = steps };
    }

    private static JsonObject ManagerResult(Random random)
    {
        JsonNode? Candidate() => random.Next(7) switch
        {
            0 => null,
            1 => new JsonObject(),
            2 => new JsonObject { ["id"] = Pick(random, "principal-1", "", null) },
            3 => new JsonObject { ["id"] = random.Next(2) == 0 ? 7 : true, ["displayName"] = "Dana" },
            4 => new JsonObject { ["id"] = "principal-2", ["displayName"] = Pick(random, "Ines", "", null) },
            5 => new JsonArray("principal-3"),
            _ => "principal-4",
        };

        JsonNode? Code() => random.Next(6) switch
        {
            0 => 404,
            1 => 200,
            2 => 429,
            3 => true,
            4 => 404.0,
            _ => "404",
        };

        var data = new JsonObject();
        if (random.Next(3) == 0)
        {
            data["statusCode"] = Code();
        }

        if (random.Next(2) == 0)
        {
            var results = new JsonArray();
            for (var i = random.Next(4); i > 0; i--)
            {
                var entry = new JsonObject();
                if (random.Next(2) == 0)
                {
                    entry["statusCode"] = Code();
                }

                if (random.Next(3) > 0)
                {
                    entry["data"] = Candidate();
                }

                results.Add(random.Next(8) == 0 ? "not an entry" : entry);
            }

            data["results"] = results;
        }

        if (random.Next(3) == 0)
        {
            data["data"] = Candidate();
        }

        if (random.Next(3) == 0)
        {
            data["id"] = Candidate() is JsonObject top && top.ContainsKey("id") ? PyNode.Copy(top["id"]) : "principal-5";
        }

        return new JsonObject
        {
            ["status"] = Pick(random, "ok", "ok", "error", "error", "partial", "blocked", ""),
            ["data"] = random.Next(8) == 0 ? null : data,
        };
    }

    private static string Text(Random random, int parts)
    {
        var builder = new StringBuilder();
        for (var i = random.Next(parts + 1); i > 0; i--)
        {
            builder.Append(Fragments[random.Next(Fragments.Length)]);
        }

        return builder.ToString();
    }

    private static string? Pick(Random random, params string?[] options) => options[random.Next(options.Length)];
}

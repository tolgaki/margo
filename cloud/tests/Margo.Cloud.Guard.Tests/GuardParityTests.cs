using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Margo.Cloud.Core;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Work;
using Margo.Cloud.WorkIQ;
using Xunit;

namespace Margo.Cloud.Guard.Tests;

/// <summary>
/// Replays every scenario recorded from the Python <c>guard.py</c> in <c>Parity/guard_cases.json</c> and requires the same
/// response, the same receipt (apart from its time stamp), the same transport call, the same token requests and the
/// same journal interaction, compared as Python <c>json.dumps</c> text (key order and int/float/bool distinctions
/// included). The fakes mirror the recording script's. The Python guard and its recorder were removed at the cutover, so
/// the fixture is now frozen: it is the guard's regression baseline (see PORTING.md).
/// </summary>
public sealed partial class GuardParityTests
{
    private static readonly Lazy<JsonObject> Fixture = new(Load);
    private static readonly Lazy<Dictionary<string, JsonObject>> Cases = new(() =>
        Fixture.Value["cases"]!.AsArray().ToDictionary(c => (string)c!["name"]!, c => c!.AsObject(), StringComparer.Ordinal));

    public static TheoryData<string> CaseNames
    {
        get
        {
            var names = new TheoryData<string>();
            foreach (var name in Cases.Value.Keys)
            {
                names.Add(name);
            }

            return names;
        }
    }

    [Fact]
    public void Fixture_covers_every_status_and_reason_family()
    {
        Assert.True(Cases.Value.Count >= 1500);
        var statuses = Cases.Value.Values
            .SelectMany(c => c["expect"]!["calls"]!.AsArray())
            .Select(call => (string)call!["response"]!["status"]!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Superset(new HashSet<string>(StringComparer.Ordinal)
        {
            GuardStatuses.Ok, GuardStatuses.Partial, GuardStatuses.Error, GuardStatuses.Unknown, GuardStatuses.Refused,
            GuardStatuses.Unavailable, GuardStatuses.Blocked, GuardStatuses.BudgetExhausted,
        }, statuses);
    }

    [Fact]
    public void Model_tools_match_python()
    {
        Assert.Equal(PyJson.Dumps(Fixture.Value["checks"]!["model_tools"]), PyJson.Dumps(GuardPolicy.ModelTools()));
    }

    [Fact]
    public void Status_codes_match_python()
    {
        foreach (var pair in Fixture.Value["checks"]!["status_codes"]!.AsArray())
        {
            var codes = GuardPolicy.StatusCodes(pair![0]);
            Assert.Equal(PyJson.Dumps(pair[1]), PyJson.Dumps(new JsonArray(codes.Select(PyNode.Copy).ToArray())));
        }
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task Guard_matches_python(string name)
    {
        var c = Cases.Value[name];
        var binding = new InstanceBinding("agent-identity-1", "margo-user-1", "principal-1", PyNode.AsString(c["principal_chat_id"]));
        var tokens = new ParityTokens(c["tokens"] as JsonObject);
        var transport = new ParityTransport(c["transport"] as JsonObject);
        var journal = c["journal"] is JsonObject spec ? new ParityJournal(spec) : null;
        var budget = c["budget"] is { } limit ? new CallBudget((int)PyJson.AsInt(limit)) : null;
        var guard = new WorkIQGuard(binding, tokens, transport, journal, budget: budget,
                                    principalAccess: PyNode.AsString(c["principal_access"]) ?? PrincipalAccessModes.Obo,
                                    triggerMessageId: PyNode.AsString(c["trigger_message_id"]));
        var calls = c["calls"]!.AsArray();
        var expected = c["expect"]!["calls"]!.AsArray();
        Assert.Equal(expected.Count, calls.Count);
        for (var i = 0; i < calls.Count; i++)
        {
            var call = calls[i]!;
            var before = transport.Sent.Count;
            var result = await guard.CallAsync(PyNode.AsString(call["tool"]), PyNode.Copy(call["arguments"]),
                                               PyNode.AsString(call["mode"]), TestContext.Current.CancellationToken);
            Assert.Equal(PyJson.Dumps(expected[i]!["response"]), PyJson.Dumps(result.ToJsonObject()));

            var receipt = guard.Receipts[^1].ToJsonObject();
            Assert.Matches(Timestamp(), (string)receipt["at"]!);
            receipt.Remove("at");
            Assert.Equal(PyJson.Dumps(expected[i]!["receipt"]), PyJson.Dumps(receipt));

            var sent = transport.Sent.Count > before ? transport.Sent[before] : null;
            Assert.Equal(PyJson.Dumps(expected[i]!["sent"]), PyJson.Dumps(sent));
        }

        Assert.Equal(calls.Count, guard.Receipts.Count);
        Assert.Equal(PyJson.Dumps(c["expect"]!["requested"]), PyJson.Dumps(new JsonArray(tokens.Requested.Select(s => (JsonNode?)s).ToArray())));
        Assert.Equal(PyJson.Dumps(c["expect"]!["journal"]), PyJson.Dumps(journal?.Log));
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{6}\+00:00$")]
    private static partial Regex Timestamp();

    private static JsonObject Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Parity", "guard_cases.json");
        return PyJson.Loads(File.ReadAllText(path))!.AsObject();
    }

    private sealed class ParityTokens(JsonObject? spec) : ITokenProvider
    {
        private readonly HashSet<string> _missing = PyNode.Iter(spec?["missing"] ?? new JsonArray())
                                                              .Select(s => PyNode.AsString(s)!).ToHashSet(StringComparer.Ordinal);

        private readonly bool _broken = spec?["broken"] is { } broken && PyJson.Truthy(broken);

        public List<string> Requested { get; } = [];

        public string TokenFor(string subject)
        {
            Requested.Add(subject);
            if (_broken)
            {
                throw new InvalidOperationException("vault unreachable");
            }

            if (_missing.Contains(subject))
            {
                throw new TokenUnavailableException("consent revoked");
            }

            return subject == Subjects.Principal ? Fixtures.PrincipalToken : Fixtures.MargoToken;
        }
    }

    private sealed class ParityTransport(JsonObject? spec) : IWorkIQTransport
    {
        public string Endpoint => WorkIQEndpoints.McpUrl;

        public List<JsonNode> Sent { get; } = [];

        public Task<JsonObject> CallAsync(string tool, JsonObject arguments, string bearer, CancellationToken cancellationToken = default)
        {
            Sent.Add(new JsonArray(tool, PyNode.Copy(arguments), bearer));
            switch (PyNode.AsString(spec?["error"]))
            {
                case "timeout":
                    throw new TransportTimeoutException("no response");
                case "transport":
                    var code = spec!["status_code"];
                    throw new TransportException("Work IQ HTTP error", statusCode: code is null ? null : (int)PyJson.AsInt(code));
                case "other":
                    throw new IOException("reset by peer");
                default:
                    return Task.FromResult(spec?["result"] is JsonObject result ? PyNode.CopyObject(result) : new JsonObject { ["statusCode"] = 200 });
            }
        }
    }

    /// <summary>The script's <c>Journal</c>: <c>load</c> follows <see cref="LedgerClaimJournal"/>'s argument check, then returns the configured claim.</summary>
    private sealed class ParityJournal(JsonObject spec) : IClaimJournal
    {
        public JsonArray Log { get; } = [];

        public ExecutionClaim Load(JsonNode? actionId, JsonNode? attemptId, string callKey = "workiq")
        {
            Log.Add(new JsonArray("load", PyNode.Copy(actionId), PyNode.Copy(attemptId)));
            if (PyNode.AsString(actionId) is not { } action || PyNode.AsString(attemptId) is not { } attempt)
            {
                throw new ClaimException("action_id and attempt_id are required for a guarded write");
            }

            if (PyNode.AsString(spec["load_error"]) is { Length: > 0 } error)
            {
                throw new ClaimException(error);
            }

            return new ExecutionClaim(
                PyNode.AsString(spec["account"]) ?? "autopilot:agent-identity-1:principal-1",
                action,
                attempt,
                1,
                "hash-1",
                PyNode.Copy(spec["on_data_of"]),
                PyNode.AsString(spec["call_key"]) ?? "workiq",
                spec["call"] is JsonObject call ? PyNode.CopyObject(call) : [],
                PyNode.Copy(spec["approved_by"]));
        }

        public void MarkDispatched(ExecutionClaim claim)
        {
            Log.Add(new JsonArray("dispatch", claim.AttemptId));
            if (spec["dispatch_error"] is { } flag && PyJson.Truthy(flag))
            {
                throw new ClaimException("attempt was already dispatched; reconcile it, never resend");
            }
        }

        public JsonObject Settle(ExecutionClaim claim, string state, int? statusCode = null)
        {
            Log.Add(new JsonArray("settle", claim.AttemptId, state, statusCode is { } code ? JsonValue.Create(code) : null));
            if (spec["settle_error"] is { } flag && PyJson.Truthy(flag))
            {
                throw new InvalidOperationException("database is locked");
            }

            return [];
        }
    }
}

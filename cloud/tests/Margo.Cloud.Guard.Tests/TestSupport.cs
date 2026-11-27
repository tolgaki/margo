using System.Text.Json.Nodes;
using Margo.Cloud.Core;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Work;
using Margo.Cloud.WorkIQ;

// Private state relies on POSIX modes; Margo Cloud runs on Linux containers.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]

namespace Margo.Cloud.Guard.Tests;

/// <summary>Fictional identities shared by the tests (<c>BINDING</c> in the Python tests).</summary>
internal static class Fixtures
{
    public const string PrincipalToken = "principal-token-fictional";
    public const string MargoToken = "margo-token-fictional";

    public static InstanceBinding Binding { get; } = new("agent-identity-1", "margo-user-1", "principal-1", "chat-principal-1");

    /// <summary>Parses a JSON literal the way the engine will hand model arguments to the guard.</summary>
    public static JsonNode? Json(string text) => PyJson.Loads(text);

    public static JsonObject Args(string text) => (JsonObject)PyJson.Loads(text)!;

    /// <summary><c>(datetime.now(timezone.utc) + timedelta(minutes=minutes)).isoformat()</c>.</summary>
    public static string Stamp(int minutes = 0) => (PyDateTime.Now() + PyTimeDelta.FromMinutes(minutes)).Isoformat();

    public static string Dumps(IEnumerable<GuardReceipt> receipts) =>
        PyJson.Dumps(new JsonArray(receipts.Select(receipt => (JsonNode?)receipt.ToJsonObject()).ToArray()));
}

/// <summary><c>FakeTokens</c>: records every subject asked for; a missing subject is <see cref="TokenUnavailableException"/>.</summary>
internal sealed class FakeTokens : ITokenProvider
{
    private readonly HashSet<string> _missing;
    private readonly Func<string, string> _token;

    public FakeTokens(IEnumerable<string>? missing = null, Func<string, string>? token = null, string reason = "consent revoked")
    {
        _missing = new HashSet<string>(missing ?? [], StringComparer.Ordinal);
        _token = token ?? (subject => subject == Subjects.Principal ? Fixtures.PrincipalToken : Fixtures.MargoToken);
        Reason = reason;
    }

    public string Reason { get; }

    public List<string> Requested { get; } = [];

    public string TokenFor(string subject)
    {
        Requested.Add(subject);
        if (_missing.Contains(subject))
        {
            throw new TokenUnavailableException(Reason);
        }

        return _token(subject);
    }
}

/// <summary>A token provider that crashes (<c>Broken</c> in the Python test).</summary>
internal sealed class BrokenTokens : ITokenProvider
{
    public string TokenFor(string subject) => throw new InvalidOperationException("vault unreachable");
}

/// <summary>
/// <c>FakeTransport</c>/<c>RecordingTransport</c>: records each call's tool, a copy of its arguments and the bearer
/// token, then returns a copy of the scripted payload or throws the scripted error.
/// </summary>
internal sealed class FakeTransport : IWorkIQTransport
{
    private readonly JsonObject _result;
    private readonly Func<Exception>? _error;

    public FakeTransport(string? result = null, Func<Exception>? error = null)
    {
        _result = (JsonObject)PyJson.Loads(result ?? """{"statusCode": 200, "data": {"value": []}}""")!;
        _error = error;
    }

    public string Endpoint { get; init; } = WorkIQEndpoints.McpUrl;

    public List<(string Tool, JsonObject Arguments, string Bearer)> Calls { get; } = [];

    /// <summary>Runs inside the call, before the scripted outcome (for example, to cancel the caller's token).</summary>
    public Action? During { get; init; }

    public Task<JsonObject> CallAsync(string tool, JsonObject arguments, string bearer, CancellationToken cancellationToken = default)
    {
        lock (Calls) // Concurrent_calls_never_exceed_the_budget calls this from several threads
        {
            Calls.Add((tool, PyNode.CopyObject(arguments), bearer));
        }

        During?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        if (_error is not null)
        {
            throw _error();
        }

        return Task.FromResult(PyNode.CopyObject(_result));
    }
}

/// <summary>
/// A private temporary state root with an isolated environment, like the Python test's patched <c>connect</c>:
/// <c>MARGO_ALLOW_UNSAFE_STATE_DIR=1</c> (temporary directories live under a world-writable /tmp), a missing
/// <c>MARGO_CONFIG</c> and a private <c>HOME</c>. Create it inside the test method so the override flows with the test.
/// </summary>
internal sealed class TempState : IDisposable
{
    private readonly IDisposable _environment;

    private TempState(string path, IDisposable environment)
    {
        Path = path;
        _environment = environment;
    }

    public string Path { get; }

    public string Root => System.IO.Path.Combine(Path, "state");

    public static TempState Create()
    {
        var path = Directory.CreateTempSubdirectory("margo-guard-test-").FullName;
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
        return new TempState(path, PyRuntime.Override(environment));
    }

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

/// <summary>
/// <c>GuardedWriteTests.setUp</c>/<c>started</c>: the instance's real ledger, one source, and helpers that propose,
/// approve and begin an action carrying an exact call. Only the database location is synthetic.
/// </summary>
internal sealed class ApprovalDesk : IDisposable
{
    public static readonly JsonObject Send = Fixtures.Args("""{"tool": "do_action", "arguments": {"actionUrl": "/me/messages/draft-1/send"}}""");

    private readonly TempState _state;

    private ApprovalDesk(TempState state, Ledger ledger)
    {
        _state = state;
        Ledger = ledger;
        Reference = ledger.Source("mail", "inbox", "message-1", "v1",
                                  new JsonObject { ["quote"] = "Please send the fictional summary." }, "https://example.com/mail/1");
    }

    public Ledger Ledger { get; }

    public JsonObject Reference { get; }

    public string Root => _state.Root;

    public static ApprovalDesk Open(InstanceBinding? binding = null)
    {
        var state = TempState.Create();
        try
        {
            return new ApprovalDesk(state, (binding ?? Fixtures.Binding).OpenLedger(state.Root));
        }
        catch
        {
            state.Dispose();
            throw;
        }
    }

    /// <summary><c>started(workiq=None, on_data_of="principal", payload=None, actor="principal-1")</c>.</summary>
    public (string ActionId, string AttemptId) Started(JsonObject? workiq = null, string onDataOf = "principal",
                                                       JsonObject? payload = null, string actor = "principal-1")
    {
        var item = Ledger.Ingest(new JsonObject
        {
            ["title"] = "Send fictional summary",
            ["owner"] = null,
            ["due"] = null,
            ["direction"] = "owe",
            ["source_refs"] = new JsonArray(PyNode.Copy(Reference)),
        }, "claim-" + Guid.NewGuid().ToString("N"));
        var action = Ledger.Propose(new JsonObject
        {
            ["kind"] = "mail.send",
            ["target"] = new JsonObject { ["message_id"] = "draft-1", ["on_data_of"] = onDataOf },
            ["payload"] = payload is not null ? PyNode.CopyObject(payload) : new JsonObject { ["workiq"] = PyNode.CopyObject(workiq ?? Send) },
            ["why"] = "Explicit request",
            ["source_refs"] = new JsonArray(PyNode.Copy(Reference)),
            ["target_fingerprint"] = "target-v1",
            ["work_item_id"] = PyNode.Copy(item["id"]),
        });
        return Begin(action, actor, "host-interaction:fictional/teams-card/1", "Send this exact message.");
    }

    /// <summary>Approve revision 1 with a human confirmation by <paramref name="actor"/>, then begin it.</summary>
    public (string ActionId, string AttemptId) Begin(JsonObject action, string actor, string evidenceRef, string statement)
    {
        var id = (string)action["id"]!;
        var hash = (string)action["action_hash"]!;
        var evidence = new JsonObject
        {
            ["kind"] = "human_confirmation",
            ["actor"] = actor,
            ["statement"] = statement,
            ["evidence_ref"] = evidenceRef,
            ["subject_id"] = id,
            ["revision"] = 1,
            ["decision"] = "approve",
            ["action_hash"] = hash,
            ["decided_at"] = Fixtures.Stamp(),
        };
        Ledger.Approve(id, 1, hash, evidence, Fixtures.Stamp(60));
        var attempt = Ledger.Begin(id, 1, new JsonObject
        {
            ["checked_at"] = Fixtures.Stamp(),
            ["target_fingerprint"] = "target-v1",
            ["source_refs"] = new JsonArray(PyNode.Copy(Reference)),
        });
        return (id, (string)attempt["attempt_id"]!);
    }

    public string State(string actionId) => (string)Ledger.Show(actionId)["state"]!;

    public List<string> Events(string actionId) =>
        ((JsonArray)Ledger.History(actionId)["events"]!).Select(e => (string)e!["event"]!).ToList();

    public void Dispose()
    {
        Ledger.Close();
        _state.Dispose();
    }
}

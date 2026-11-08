using System.Text.Json.Nodes;
using Margo.Cloud.Engine;
using Margo.Cloud.Guard;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.Voice.Tests;

/// <summary><c>tests/test_autopilot_voice.py</c> <c>SessionConfiguration</c>, plus the settings' environment rules.</summary>
public sealed class SessionConfigurationTests
{
    private static readonly VoiceSettings Settings = new("fictional-realtime", MaxToolCalls: 3);

    [Fact]
    public void Same_persona_skills_and_tools_plus_speaking_note()
    {
        var library = new SkillLibrary(Fixtures.Root);
        var instructions = EngineTools.LoadInstructions(Fixtures.Root, library: library, now: new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.Zero));
        var update = VoiceSession.SessionUpdate(instructions + "\n\n" + VoiceSession.Addendum, EngineTools.FunctionTools(RunModes.Attended), Settings);
        var session = (JsonObject)update["session"]!;
        Assert.Equal("session.update", PyNode.AsString(update["type"]));
        Assert.StartsWith(library.Persona(), PyNode.AsString(session["instructions"]), StringComparison.Ordinal);
        Assert.Contains("- **decision-log**:", PyNode.AsString(session["instructions"]), StringComparison.Ordinal);
        Assert.Contains("Nothing is approved or sent by voice", PyNode.AsString(session["instructions"]), StringComparison.Ordinal);
        var names = ((JsonArray)session["tools"]!).Select(tool => PyNode.AsString(tool!["name"])!).ToHashSet();
        Assert.Superset(new HashSet<string> { "workiq-fetch", "margo-state", "load_skill", "office_requests" }, names);
        Assert.Empty(names.Intersect(["workiq-do_action", "workiq-create_entity", "workiq-delete_entity"]));
        Assert.All((JsonArray)session["tools"]!, tool => Assert.Equal("function", PyNode.AsString(tool!["type"])));
        Assert.DoesNotContain("mcp", PyJson.Dumps(session).ToLowerInvariant(), StringComparison.Ordinal);
        Assert.Equal("azure_semantic_vad", PyNode.AsString(session["turn_detection"]!["type"]));
        Assert.Equal("server_echo_cancellation", PyNode.AsString(session["input_audio_echo_cancellation"]!["type"]));
    }

    [Fact]
    public void Voice_choice_custom_or_standard()
    {
        Assert.Equal("""{"type": "azure-standard", "name": "en-US-Ava:DragonHDLatestNeural"}""", PyJson.Dumps(Settings.Voice()));
        var custom = VoiceSettings.FromEnv(new Dictionary<string, string>
        {
            ["VOICE_LIVE_MODEL"] = "m",
            ["MARGO_VOICE_NAME"] = "MargoNeural",
            ["MARGO_VOICE_ENDPOINT_ID"] = "fictional-voice-deployment",
        });
        Assert.Equal("azure-custom", PyNode.AsString(custom.Voice()["type"]));
        Assert.Equal("fictional-voice-deployment", PyNode.AsString(custom.Voice()["endpoint_id"]));
        Assert.Equal("""{"type": "azure-custom", "name": "MargoNeural", "endpoint_id": "fictional-voice-deployment"}""", PyJson.Dumps(custom.Voice()));
        Assert.Throws<PyValueError>(() => VoiceSettings.FromEnv(new Dictionary<string, string>()));
    }

    [Fact]
    public void Settings_from_the_environment_as_python_reads_them()
    {
        var defaults = VoiceSettings.FromEnv(new Dictionary<string, string> { ["VOICE_LIVE_MODEL"] = "  gpt-realtime \n" });
        Assert.Equal(new VoiceSettings("gpt-realtime", VoiceSession.DefaultVoice, "", 1800, 60), defaults);
        var limits = VoiceSettings.FromEnv(new Dictionary<string, string>
        {
            ["VOICE_LIVE_MODEL"] = "m",
            ["MARGO_VOICE_NAME"] = "   ",
            ["MARGO_VOICE_MAX_SECONDS"] = " 1_200 ",
            ["MARGO_VOICE_MAX_TOOL_CALLS"] = "+7",
        });
        Assert.Equal(new VoiceSettings("m", VoiceSession.DefaultVoice, "", 1200, 7), limits);
        foreach (var bad in new[] { "", "ten", "1.5" })
        {
            var error = Assert.Throws<PyValueError>(() => VoiceSettings.FromEnv(new Dictionary<string, string>
            {
                ["VOICE_LIVE_MODEL"] = "m",
                ["MARGO_VOICE_MAX_SECONDS"] = bad,
            }));
            Assert.StartsWith("invalid literal for int() with base 10: ", error.Message, StringComparison.Ordinal);
        }

        Assert.Equal("VOICE_LIVE_MODEL is required for voice",
                     Assert.Throws<PyValueError>(() => VoiceSettings.FromEnv(new Dictionary<string, string> { ["VOICE_LIVE_MODEL"] = " " })).Message);
    }

    [Fact]
    public void Session_update_is_python_s_event_exactly()
    {
        var tools = new JsonArray(
            new JsonObject { ["type"] = "function", ["name"] = "a", ["description"] = "A tool.", ["parameters"] = new JsonObject { ["type"] = "object" } },
            new JsonObject { ["name"] = "b" });
        var text = PyJson.Dumps(VoiceSession.SessionUpdate("Be Margo.", tools, new VoiceSettings("m")));
        Assert.Equal(
            """{"type": "session.update", "session": {"modalities": ["text", "audio"], "instructions": "Be Margo.", "voice": {"type": "azure-standard", "name": "en-US-Ava:DragonHDLatestNeural"}, "input_audio_format": "pcm16", "output_audio_format": "pcm16", "turn_detection": {"type": "azure_semantic_vad"}, "input_audio_noise_reduction": {"type": "azure_deep_noise_suppression"}, "input_audio_echo_cancellation": {"type": "server_echo_cancellation"}, "input_audio_transcription": {"model": "azure-speech"}, "tools": [{"type": "function", "name": "a", "description": "A tool.", "parameters": {"type": "object"}}, {"type": "function", "name": "b", "description": "", "parameters": {"type": "object", "properties": {}}}], "tool_choice": "auto"}}""",
            text);
    }

    [Fact]
    public void Addendum_and_disclosure_are_python_s_text()
    {
        var python = File.ReadAllText(Path.Combine(Fixtures.Root, "autopilot", "src", "margo_autopilot", "voice", "session.py"));
        Assert.Contains("VOICE_ADDENDUM = \"\"\"" + VoiceSession.Addendum + "\"\"\"\n", python, StringComparison.Ordinal);
        Assert.Contains("DISCLOSURE = \"" + VoiceSession.Disclosure + "\"\n", python, StringComparison.Ordinal);
        Assert.Contains("DEFAULT_VOICE = \"" + VoiceSession.DefaultVoice + "\"\n", python, StringComparison.Ordinal);
    }

    [Fact]
    public void Strict_base64_is_python_s_validate_mode()
    {
        foreach (var good in new[] { "AAAA", "AAA=", "AA==", "AB==", "AAE=", "", "AAAAAAAA" })
        {
            Assert.True(VoiceSession.IsStrictBase64(good), good);
        }

        foreach (var bad in new[] { "A===", "AAAA=", "=AAA", "AA=A", "AAAAAA", "AAAAA", "AA\n==", " AAAA", "AA==AA==", "AAAA====", "not base64!", "AAAA\n", "ÀAAA" })
        {
            Assert.False(VoiceSession.IsStrictBase64(bad), bad);
        }
    }
}

/// <summary><c>tests/test_autopilot_voice.py</c> <c>Bridge</c>, plus the cases the Python tests do not reach.</summary>
public sealed class BridgeTests
{
    private static readonly VoiceSettings Settings = new("fictional-realtime", MaxToolCalls: 3);
    private readonly List<JsonObject> _up = [];
    private readonly List<JsonObject> _down = [];
    private readonly List<(JsonNode? Name, JsonNode? Raw)> _calls = [];
    private readonly List<bool> _locked = [];
    private VoiceBridge _bridge;

    public BridgeTests()
    {
        _bridge = Create(Settings);
    }

    private VoiceBridge Create(VoiceSettings settings, Func<double>? clock = null, bool locked = true) => new(
        (name, raw, _) =>
        {
            _calls.Add((name, raw));
            return Task.FromResult<JsonNode?>(new JsonObject { ["status"] = "ok", ["tool"] = PyNode.Copy(name) });
        },
        (message, _) =>
        {
            _up.Add(message);
            return Task.CompletedTask;
        },
        (message, _) =>
        {
            _down.Add(message);
            return Task.CompletedTask;
        },
        settings, EngineTools.Clip,
        locked ? async (work, token) =>
        {
            _locked.Add(true);
            return await work(token);
        } : null,
        clock);

    private async Task Events(params string[] events)
    {
        foreach (var serviceEvent in events)
        {
            await _bridge.FromServiceAsync(PyJson.Loads(serviceEvent), TestContext.Current.CancellationToken);
        }
    }

    private Task<string?> Client(string message) => _bridge.FromClientAsync(PyJson.Loads(message), TestContext.Current.CancellationToken);

    private static string Type(JsonObject message) => PyNode.AsString(message["type"])!;

    [Fact]
    public async Task Audio_flows_only_after_ready_and_barge_in_interrupts()
    {
        var audio = Convert.ToBase64String(Enumerable.Repeat(new byte[] { 0, 1 }, 480).SelectMany(pair => pair).ToArray());
        await Client($$"""{"type": "audio", "audio": "{{audio}}"}""");
        Assert.Empty(_up); // nothing is sent before the session is configured
        await Events("""{"type": "session.updated"}""");
        Assert.Equal("ready", Type(_down[0]));
        Assert.Contains("synthetic", PyNode.AsString(_down[0]["disclosure"]), StringComparison.Ordinal);
        await Client($$"""{"type": "audio", "audio": "{{audio}}"}""");
        Assert.Equal($$"""{"type": "input_audio_buffer.append", "audio": "{{audio}}"}""", PyJson.Dumps(_up[^1]));
        foreach (var bad in new[] { "\"not base64!\"", "\"" + new string('A', VoiceSession.MaxAudioChars + 4) + "\"", "42", "null" })
        {
            await Client("""{"type": "audio", "audio": """ + bad + "}");
        }

        Assert.Single(_up);
        await Events("""{"type": "response.audio.delta", "delta": "AAAA"}""", """{"type": "input_audio_buffer.speech_started"}""",
                     """{"type": "conversation.item.input_audio_transcription.completed", "transcript": " Brief me "}""",
                     """{"type": "response.audio_transcript.done", "transcript": "Here is your morning."}""");
        Assert.Equal(["audio", "interrupt", "caption", "caption"], _down.Skip(1).Select(Type));
        Assert.Equal("""{"type": "caption", "who": "you", "text": "Brief me"}""", PyJson.Dumps(_down[3]));
        Assert.Equal("stop", await Client("""{"type": "stop"}"""));
        Assert.Null(await Client("""{"type": "unknown"}"""));
    }

    [Fact]
    public async Task Tool_calls_run_through_dispatch_then_one_response()
    {
        await Events(
            """{"type": "response.function_call_arguments.done", "call_id": "c1", "name": "workiq-fetch", "arguments": "{\"on_data_of\": \"principal\", \"entityUrls\": [\"/me/messages\"]}"}""",
            """{"type": "response.function_call_arguments.done", "call_id": "c2", "name": "margo-state", "arguments": "{\"script\": \"work_state.py\", \"args\": [\"list\"]}"}""",
            """{"type": "response.done"}""");
        Assert.Equal(["workiq-fetch", "margo-state"], _calls.Select(call => PyNode.AsString(call.Name)));
        Assert.Single(_locked); // only the state call takes the state lease
        var outputs = _up.Where(message => Type(message) == "conversation.item.create").ToList();
        Assert.Equal(["c1", "c2"], outputs.Select(message => PyNode.AsString(message["item"]!["call_id"])));
        Assert.Equal("ok", PyNode.AsString(PyJson.Loads(PyNode.AsString(outputs[0]["item"]!["output"])!)!["status"]));
        Assert.Equal(1, _up.Count(message => Type(message) == "response.create"));
        Assert.Equal("""{"type": "status", "text": ""}""", PyJson.Dumps(_down[^1]));
        await Events("""{"type": "response.done"}""");
        Assert.Equal(1, _up.Count(message => Type(message) == "response.create"));
    }

    [Fact]
    public async Task Tool_budget_and_malformed_calls()
    {
        for (var index = 0; index < 5; index++)
        {
            await Events($$"""{"type": "response.function_call_arguments.done", "call_id": "c{{index}}", "name": "load_skill", "arguments": "{}"}""");
        }

        Assert.Equal(Settings.MaxToolCalls, _calls.Count);
        var last = PyJson.Loads(PyNode.AsString(_up[^1]["item"]!["output"])!)!;
        Assert.Equal("budget_exhausted", PyNode.AsString(last["status"]));
        var before = _up.Count;
        await Events("""{"type": "response.function_call_arguments.done", "name": "load_skill"}""");
        Assert.Equal(before, _up.Count); // no call ID: nothing to answer
    }

    [Fact]
    public async Task Service_errors_are_reported_without_detail()
    {
        await Events("""{"type": "error", "error": {"message": "internal detail with secrets"}}""");
        Assert.Equal("error", Type(_down[^1]));
        Assert.DoesNotContain("secret", PyJson.Dumps(new JsonArray([.. _down.Select(message => (JsonNode)message)])), StringComparison.Ordinal);
        Assert.Equal("""{"type": "error", "fatal": false, "message": "The voice service reported a problem; try saying that again."}""",
                     PyJson.Dumps(_down[^1]));
    }

    [Fact]
    public async Task Progress_messages_and_output_shape_follow_python()
    {
        _bridge = Create(Settings with { MaxToolCalls = 10 });
        await Events(
            """{"type": "response.function_call_arguments.done", "call_id": "c1", "name": "workiq-fetch", "arguments": "{}"}""",
            """{"type": "response.function_call_arguments.done", "call_id": "c2", "name": "office_requests", "arguments": "{}"}""",
            """{"type": "response.function_call_arguments.done", "call_id": "c3", "name": "load_skill", "arguments": "{}"}""",
            """{"type": "response.function_call_arguments.done", "call_id": "c4", "name": 7, "arguments": {"not": "a string"}}""");
        Assert.Equal(["Checking Microsoft 365...", "Checking requests to the office..."],
                     _down.Where(message => Type(message) == "status").Select(message => PyNode.AsString(message["text"])));
        Assert.Single(_locked); // office_requests takes the state lease; the others do not
        Assert.Equal("""{"type": "conversation.item.create", "item": {"type": "function_call_output", "call_id": "c1", "output": "{\"status\": \"ok\", \"tool\": \"workiq-fetch\"}"}}""",
                     PyJson.Dumps(_up[0]));
        Assert.Equal("{\"not\": \"a string\"}", PyJson.Dumps(_calls[3].Raw)); // raw arguments go to the dispatcher as received
    }

    [Fact]
    public async Task An_unhashable_tool_name_raises_as_in_python()
    {
        await Assert.ThrowsAsync<PyTypeError>(() => Events("""{"type": "response.function_call_arguments.done", "call_id": "c1", "name": ["margo-state"]}"""));
        Assert.Empty(_calls);
        Assert.Empty(_up);
    }

    [Fact]
    public async Task Without_a_lease_runner_state_calls_run_directly()
    {
        var bridge = Create(Settings, locked: false);
        await bridge.FromServiceAsync(PyJson.Loads("""{"type": "response.function_call_arguments.done", "call_id": "c1", "name": "margo-state", "arguments": "{}"}"""),
                                     TestContext.Current.CancellationToken);
        Assert.Single(_calls);
        Assert.Empty(_locked);
    }

    [Fact]
    public async Task Ready_once_captions_trimmed_and_clipped()
    {
        await Events("""{"type": "session.updated"}""", """{"type": "session.updated"}""",
                     """{"type": "response.audio_transcript.done", "transcript": "   "}""",
                     """{"type": "response.audio_transcript.done", "transcript": 5}""",
                     """{"type": "response.audio.delta", "delta": 5}""",
                     "[1, 2]", "\"session.updated\"", """{"type": ["error"]}""");
        Assert.Single(_down);
        var long_ = new string('x', 2500);
        await Events($$"""{"type": "response.audio_transcript.done", "transcript": "\t{{long_}}"}""");
        Assert.Equal(2000, PyNode.AsString(_down[^1]["text"])!.Length);
        Assert.Null(await _bridge.FromClientAsync(new JsonArray(), TestContext.Current.CancellationToken));
        Assert.Null(await Client("""{"type": "audio", "audio": "AAAA"}""")); // ready: forwarded
        Assert.Equal("input_audio_buffer.append", Type(_up[^1]));
    }

    [Fact]
    public void Expiry_follows_the_clock()
    {
        var now = 100.0;
        var bridge = Create(new VoiceSettings("m", MaxSeconds: 30), () => now);
        now = 130;
        Assert.False(bridge.Expired());
        now = 130.5;
        Assert.True(bridge.Expired());
    }
}

/// <summary><c>tests/test_autopilot_voice.py</c> <c>Claims</c>.</summary>
public sealed class ClaimsTests
{
    private const double Now = 1_800_000_000;

    private static JsonObject Claims(params (string Key, JsonNode? Value)[] overrides)
    {
        var value = new JsonObject
        {
            ["tid"] = Fixtures.Tenant,
            ["aud"] = Fixtures.Client,
            ["scp"] = "Voice.Talk",
            ["exp"] = (long)Now + 600,
            ["oid"] = "principal-1",
        };
        foreach (var (key, node) in overrides)
        {
            value[key] = node;
        }

        return value;
    }

    private static string Check(JsonNode? claims) => VoiceClaims.Check(claims, Fixtures.Tenant, Fixtures.Client, "Voice.Talk", Now);

    [Fact]
    public void Only_a_person_in_this_tenant_with_the_voice_scope()
    {
        Assert.Equal("principal-1", Check(Claims()));
        Assert.Equal("principal-1", Check(Claims(("aud", "api://" + Fixtures.Client), ("scp", "openid Voice.Talk"))));
        JsonNode?[] bad =
        [
            Claims(("tid", "other")), Claims(("aud", "other-app")), Claims(("scp", "Mail.Read")), Claims(("scp", "Voice.TalkMore")),
            Claims(("exp", (long)Now - 1)), Claims(("oid", "")), Claims(("idtyp", "app")), null,
        ];
        foreach (var claims in bad)
        {
            Assert.Throws<TokenRejectedException>(() => Check(claims));
        }
    }

    [Fact]
    public void Reasons_and_python_value_semantics()
    {
        Assert.Equal("no claims", Assert.Throws<TokenRejectedException>(() => Check(new JsonArray())).Message);
        Assert.Equal("token is from another tenant", Assert.Throws<TokenRejectedException>(() => Check(Claims(("tid", null)))).Message);
        Assert.Equal("token is for another application",
                     Assert.Throws<TokenRejectedException>(() => Check(Claims(("aud", new JsonArray(Fixtures.Client))))).Message);
        Assert.Equal("token lacks the voice scope", Assert.Throws<TokenRejectedException>(() => Check(Claims(("scp", new JsonArray("Voice.Talk"))))).Message);
        Assert.Equal("principal-1", Check(Claims(("scp", "　Voice.Talk\u0085openid"))));
        Assert.Equal("token has expired", Assert.Throws<TokenRejectedException>(() => Check(Claims(("exp", Now)))).Message);
        Assert.Equal("token has expired", Assert.Throws<TokenRejectedException>(() => Check(Claims(("exp", "1900000000")))).Message);
        Assert.Equal("token has expired", Assert.Throws<TokenRejectedException>(() => Check(Claims(("exp", true)))).Message);
        Assert.Equal("principal-1", Check(Claims(("exp", Now + 0.5))));
        Assert.Equal("principal-1", Check(Claims(("idtyp", "user"))));
        Assert.Equal("token does not identify a person", Assert.Throws<TokenRejectedException>(() => Check(Claims(("oid", 5)))).Message);
        var missing = Claims();
        missing.Remove("scp");
        Assert.Equal("token lacks the voice scope", Assert.Throws<TokenRejectedException>(() => Check(missing)).Message);
    }
}

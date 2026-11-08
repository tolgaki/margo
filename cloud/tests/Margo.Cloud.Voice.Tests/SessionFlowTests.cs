using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using Microsoft.Extensions.Logging;

namespace Margo.Cloud.Voice.Tests;

/// <summary>
/// <c>VoiceService.socket</c> end to end: the hello, the sign-in checks, the tokens, the manager-only rule, the Voice
/// Live session with the same tools through the guard and the state lease, the limits and the close.
/// </summary>
public sealed class SessionFlowTests
{
    private static async Task<JsonObject> NextOfType(FakeVoiceLiveConnection connection, string type)
    {
        while (true)
        {
            var sent = await connection.NextSentAsync();
            if (PyNode.AsString(sent["type"]) == type)
            {
                return sent;
            }
        }
    }

    [Fact]
    public async Task The_manager_talks_to_the_same_margo_with_the_same_tools()
    {
        using var state = new TempState();
        await using var host = await VoiceHost.StartAsync(state);
        using var page = await host.OpenAsync();
        await page.HelloAsync(host.SignIn());

        var connection = await host.VoiceLive.NextConnectionAsync();
        Assert.Equal([("https://voice.example.com/", "fictional-realtime")], host.VoiceLive.Connects);
        var update = await connection.NextSentAsync();
        Assert.Equal("session.update", PyNode.AsString(update["type"]));
        var instructions = PyNode.AsString(update["session"]!["instructions"])!;
        Assert.EndsWith("\n\n" + VoiceSession.Addendum, instructions, StringComparison.Ordinal);
        Assert.StartsWith(new Margo.Cloud.Engine.SkillLibrary(Fixtures.Root).Persona(), instructions, StringComparison.Ordinal);
        var tools = ((JsonArray)update["session"]!["tools"]!).Select(tool => PyNode.AsString(tool!["name"])).ToList();
        Assert.Contains("margo-state", tools);
        Assert.Contains("office_requests", tools);
        Assert.Equal(
            [$"agentic {Fixtures.Tenant} agent-identity-1 margo-user-1 api://workiq.svc.cloud.microsoft/.default",
             $"obo api://workiq.svc.cloud.microsoft/.default {host.SignIn().Length}"],
            host.Tokens.Requests);

        // Audio is not passed on before the session is configured.
        await page.SendAsync("""{"type": "audio", "audio": "AAAA"}""");
        connection.Push(Fixtures.Obj("""{"type": "session.updated"}"""));
        var ready = await page.ReceiveJsonAsync();
        Assert.Equal($$"""{"type": "ready", "disclosure": "{{VoiceSession.Disclosure}}", "voice": "en-US-Ava:DragonHDLatestNeural"}""", PyJson.Dumps(ready));
        await page.SendAsync("not json");
        await page.SendAsync("""{"type": "audio", "audio": "AAAB"}""");
        Assert.Equal("""{"type": "input_audio_buffer.append", "audio": "AAAB"}""", PyJson.Dumps(await connection.NextSentAsync()));

        // A Work IQ read with the manager's token, through the guard; a state call through the real margo-state.
        connection.Push(Fixtures.Obj("""{"type": "response.function_call_arguments.done", "call_id": "c1", "name": "workiq-fetch", "arguments": "{\"on_data_of\": \"principal\", \"entityUrls\": [\"/me/messages\"]}"}"""));
        Assert.Equal("""{"type": "status", "text": "Checking Microsoft 365..."}""", PyJson.Dumps(await page.ReceiveJsonAsync()));
        var fetched = await NextOfType(connection, "conversation.item.create");
        Assert.Equal("c1", PyNode.AsString(fetched["item"]!["call_id"]));
        Assert.Equal("ok", PyNode.AsString(PyJson.Loads(PyNode.AsString(fetched["item"]!["output"])!)!["status"]));
        var reads = host.Transport.Calls;
        Assert.Equal(("fetch", "margo-token"), (reads[0].Tool, reads[0].Bearer)); // the manager lookup, with Margo's own token
        Assert.Equal("principal-token", reads[^1].Bearer);
        Assert.Equal("""["/me/messages"]""", PyJson.Dumps(reads[^1].Arguments["entityUrls"]));

        connection.Push(Fixtures.Obj("""{"type": "response.function_call_arguments.done", "call_id": "c2", "name": "margo-state", "arguments": "{\"script\": \"work_state.py\", \"args\": [\"list\"]}"}"""));
        Assert.Equal("""{"type": "status", "text": "Checking my notes..."}""", PyJson.Dumps(await page.ReceiveJsonAsync()));
        var listed = await NextOfType(connection, "conversation.item.create");
        Assert.Equal("c2", PyNode.AsString(listed["item"]!["call_id"]));
        var output = (JsonObject)PyJson.Loads(PyNode.AsString(listed["item"]!["output"])!)!;
        Assert.Equal("ok", PyNode.AsString(output["status"]));

        // Writes are not offered and are refused by the dispatcher.
        connection.Push(Fixtures.Obj("""{"type": "response.function_call_arguments.done", "call_id": "c3", "name": "workiq-do_action", "arguments": "{}"}"""));
        Assert.Equal("""{"type": "status", "text": "Checking Microsoft 365..."}""", PyJson.Dumps(await page.ReceiveJsonAsync()));
        var refused = await NextOfType(connection, "conversation.item.create");
        Assert.Contains("not available in this version", PyNode.AsString(refused["item"]!["output"]), StringComparison.Ordinal);

        connection.Push(Fixtures.Obj("""{"type": "response.done"}"""));
        Assert.Equal("""{"type": "response.create"}""", PyJson.Dumps(await connection.NextSentAsync()));
        Assert.Equal("""{"type": "status", "text": ""}""", PyJson.Dumps(await page.ReceiveJsonAsync()));
        connection.Push(Fixtures.Obj("""{"type": "conversation.item.input_audio_transcription.completed", "transcript": " Brief me "}"""));
        Assert.Equal("""{"type": "caption", "who": "you", "text": "Brief me"}""", PyJson.Dumps(await page.ReceiveJsonAsync()));

        await page.SendAsync("""{"type": "stop"}""");
        Assert.Equal("""{"type": "closed", "reason": "call ended"}""", PyJson.Dumps(await page.ReceiveJsonAsync()));
        Assert.Empty(await page.DrainAsync());
        Assert.Equal(WebSocketCloseStatus.NormalClosure, page.Socket.CloseStatus);
        Assert.True(connection.Disposed);
        Assert.DoesNotContain(host.Logger.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task Delegation_never_asks_for_the_manager_s_token()
    {
        using var state = new TempState();
        await using var host = await VoiceHost.StartAsync(state, principalAccess: "delegation");
        using var page = await host.OpenAsync();
        await page.HelloAsync(host.SignIn());
        var connection = await host.VoiceLive.NextConnectionAsync();
        Assert.Single(host.Tokens.Requests);
        connection.Push(Fixtures.Obj("""{"type": "response.function_call_arguments.done", "call_id": "c1", "name": "workiq-fetch", "arguments": "{\"on_data_of\": \"principal\", \"entityUrls\": [\"/me/messages\"]}"}"""));
        await NextOfType(connection, "conversation.item.create");
        Assert.Equal("margo-token", host.Transport.Calls[^1].Bearer);
        Assert.Equal("""["/users/principal-1/messages"]""", PyJson.Dumps(host.Transport.Calls[^1].Arguments["entityUrls"]));
        Assert.Equal("status", PyNode.AsString((await page.ReceiveJsonAsync())["type"]));
        connection.Push(null); // the service ends the session
        Assert.Equal("""{"type": "closed", "reason": "call ended"}""", PyJson.Dumps(await page.ReceiveJsonAsync()));
    }

    public static TheoryData<string, string> RefusedHellos => new()
    {
        { "no token", """{"type": "hello"}""" },
        { "empty token", """{"type": "hello", "token": ""}""" },
        { "wrong type", """{"type": "audio", "token": "x"}""" },
        { "not an object", """["hello"]""" },
        { "token not a string", """{"type": "hello", "token": 5}""" },
    };

    [Theory]
    [MemberData(nameof(RefusedHellos))]
    public async Task A_hello_without_a_token_is_refused(string reason, string hello)
    {
        Assert.NotEmpty(reason);
        using var state = new TempState();
        await using var host = await VoiceHost.StartAsync(state);
        using var page = await host.OpenAsync();
        await page.SendAsync(hello);
        Assert.Equal($$"""{"type": "error", "fatal": true, "message": "{{VoiceService.SignInRefused}}"}""", PyJson.Dumps(await page.ReceiveJsonAsync()));
        Assert.Empty(await page.DrainAsync());
        Assert.Empty(host.Tokens.Requests);
        Assert.Empty(host.VoiceLive.Connects);
        Assert.Contains(host.Logger.Entries, entry => entry.Level == LogLevel.Information && entry.Message == "voice sign-in refused: no token");
    }

    [Fact]
    public async Task Only_the_manager_with_a_valid_sign_in()
    {
        using var state = new TempState();
        await using var host = await VoiceHost.StartAsync(state);

        async Task<string> Refusal(string token)
        {
            using var page = await host.OpenAsync();
            await page.HelloAsync(token);
            Assert.Equal(VoiceService.SignInRefused, PyNode.AsString((await page.ReceiveJsonAsync())["message"]));
            Assert.Empty(await page.DrainAsync());
            return host.Logger.Entries[^1].Message;
        }

        Assert.Equal("voice sign-in refused: caller is not this instance's manager", await Refusal(host.SignIn("colleague-1")));
        var expired = TestKeys.Claims(TestKeys.Now());
        expired["exp"] = (long)TestKeys.Now() - 120;
        Assert.Equal("voice sign-in refused: token failed verification: ExpiredSignatureError", await Refusal(host.Keys.Mint(expired)));
        var otherTenant = TestKeys.Claims(TestKeys.Now());
        otherTenant["tid"] = "other-tenant";
        Assert.Equal("voice sign-in refused: token is from another tenant", await Refusal(host.Keys.Mint(otherTenant)));
        host.Transport.Manager = null;
        Assert.Equal("voice sign-in refused: caller is not this instance's manager", await Refusal(host.SignIn()));
        Assert.Empty(host.VoiceLive.Connects);
        Assert.DoesNotContain(host.Logger.Entries, entry => entry.Message.Contains(host.SignIn()[..20], StringComparison.Ordinal));
    }

    [Fact]
    public async Task Other_failures_end_the_call_with_the_generic_message()
    {
        using var state = new TempState();
        await using var host = await VoiceHost.StartAsync(state);

        async Task Failure(Func<PageSocket, Task> send)
        {
            using var page = await host.OpenAsync();
            await send(page);
            Assert.Equal($$"""{"type": "error", "fatal": true, "message": "{{VoiceService.SessionFailed}}"}""", PyJson.Dumps(await page.ReceiveJsonAsync()));
            Assert.Empty(await page.DrainAsync());
            Assert.Equal((LogLevel.Error, "voice session failed"), (host.Logger.Entries[^1].Level, host.Logger.Entries[^1].Message));
        }

        await Failure(page => page.SendAsync("not json"));
        host.Tokens.Margo = null;
        await Failure(page => page.HelloAsync(host.SignIn()));
        host.Tokens.Margo = "margo-token";
        host.Transport.LookupFailure = new HttpRequestException("directory offline");
        await Failure(page => page.HelloAsync(host.SignIn()));
        host.Transport.LookupFailure = null;
        host.VoiceLive.ConnectFailure = new WebSocketException("voice live offline");
        await Failure(page => page.HelloAsync(host.SignIn()));
        Assert.Single(host.VoiceLive.Connects);
        host.Service.HelloTimeout = TimeSpan.FromMilliseconds(200);
        await Failure(_ => Task.CompletedTask);
        Assert.IsType<TimeoutException>(host.Logger.Entries[^1].Exception);
    }

    [Fact]
    public async Task A_binary_first_message_is_no_token()
    {
        using var state = new TempState();
        await using var host = await VoiceHost.StartAsync(state);
        using var page = await host.OpenAsync();
        await page.Socket.SendAsync(new byte[] { 1, 2, 3 }, WebSocketMessageType.Binary, true, TestContext.Current.CancellationToken);
        Assert.Equal(VoiceService.SignInRefused, PyNode.AsString((await page.ReceiveJsonAsync())["message"]));
    }

    [Fact]
    public async Task A_frame_of_the_limit_closes_the_socket_with_1009()
    {
        using var state = new TempState();
        await using var host = await VoiceHost.StartAsync(state);
        using var page = await host.OpenAsync();
        await page.SendAsync("{\"type\": \"hello\", \"token\": \"" + new string('A', VoiceService.MaxFrame) + "\"}");
        Assert.Empty(await page.DrainAsync()); // closed before any message: no error is sent on a closed socket
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, page.Socket.CloseStatus);
    }

    [Fact]
    public async Task The_time_limit_ends_the_call()
    {
        using var state = new TempState();
        await using var host = await VoiceHost.StartAsync(state, new VoiceSettings("fictional-realtime", MaxSeconds: 1), time: new FastClock(3));
        using var page = await host.OpenAsync();
        await page.HelloAsync(host.SignIn());
        await host.VoiceLive.NextConnectionAsync();
        Assert.Equal("""{"type": "closed", "reason": "time limit reached"}""", PyJson.Dumps(await page.ReceiveJsonAsync()));
        Assert.Empty(await page.DrainAsync());
    }

    [Fact]
    public async Task The_tool_budget_is_per_call()
    {
        using var state = new TempState();
        await using var host = await VoiceHost.StartAsync(state, new VoiceSettings("fictional-realtime", MaxToolCalls: 1));
        using var page = await host.OpenAsync();
        await page.HelloAsync(host.SignIn());
        var connection = await host.VoiceLive.NextConnectionAsync();
        for (var index = 1; index <= 2; index++)
        {
            connection.Push(Fixtures.Obj($$"""{"type": "response.function_call_arguments.done", "call_id": "c{{index}}", "name": "load_skill", "arguments": "{\"name\": \"decision-log\"}"}"""));
        }

        Assert.NotEqual("budget_exhausted", PyNode.AsString(PyJson.Loads(PyNode.AsString((await NextOfType(connection, "conversation.item.create"))["item"]!["output"])!)!["status"]));
        Assert.Equal("""{"status": "budget_exhausted", "reason": "this voice session's lookup budget is used up"}""",
                     PyNode.AsString((await NextOfType(connection, "conversation.item.create"))["item"]!["output"]));
        await page.SendAsync("""{"type": "stop"}""");
        Assert.Equal("closed", PyNode.AsString((await page.ReceiveJsonAsync())["type"]));
    }
}

using System.Text.Json.Nodes;
using Margo.Cloud.Core;
using Margo.Cloud.State.Core;
using Xunit;
using static Margo.Cloud.Guard.Tests.Fixtures;

namespace Margo.Cloud.Guard.Tests;

/// <summary>Port of <c>tests/test_autopilot_guard.py</c> <c>GuardPolicyTests</c> (fictional identifiers only).</summary>
public sealed class GuardPolicyTests
{
    private FakeTokens _tokens = new();
    private FakeTransport _transport = new();

    private WorkIQGuard Guard(FakeTokens? tokens = null, FakeTransport? transport = null, CallBudget? budget = null,
                              string? triggerMessageId = null)
    {
        _tokens = tokens ?? new FakeTokens();
        _transport = transport ?? new FakeTransport();
        return new WorkIQGuard(Binding, _tokens, _transport, budget: budget, triggerMessageId: triggerMessageId);
    }

    private static async Task<string> Status(WorkIQGuard guard, string? tool, string arguments, string? mode) =>
        (await guard.CallAsync(tool, Json(arguments), mode, TestContext.Current.CancellationToken)).Status;

    [Fact]
    public void Only_the_workiq_endpoint_is_accepted()
    {
        var other = new FakeTransport { Endpoint = "https://agent365.example.com/agents/servers/mcp_MailTools" };
        Assert.Throws<GuardConfigException>(() => new WorkIQGuard(Binding, new FakeTokens(), other));
        Assert.Throws<GuardConfigException>(() => new WorkIQGuard(Binding, new FakeTokens(), null));
    }

    [Fact]
    public async Task Intent_selects_token_and_labels_result()
    {
        var guard = Guard();
        var ct = TestContext.Current.CancellationToken;
        var mine = await guard.CallAsync("fetch", Json("""{"on_data_of": "principal", "entityUrls": ["/me/messages?$top=5"]}"""), RunModes.Unattended, ct);
        var hers = await guard.CallAsync("fetch", Json("""{"on_data_of": "self", "entityUrls": ["/me/messages"]}"""), RunModes.Unattended, ct);
        Assert.Equal(("ok", "principal"), (mine.Status, mine.ReadAs));
        Assert.Equal(("ok", "margo"), (hers.Status, hers.ReadAs));
        Assert.Equal([PrincipalToken, MargoToken], _transport.Calls.Select(call => call.Bearer));
        Assert.False(_transport.Calls[0].Arguments.ContainsKey("on_data_of"));
        Assert.Equal("""["/me/messages?$top=5"]""", _transport.Calls[0].Arguments["entityUrls"]!.ToJsonString());
    }

    [Fact]
    public async Task Missing_principal_token_is_blocked_without_fallback()
    {
        var guard = Guard(tokens: new FakeTokens([Subjects.Principal]));
        var result = await guard.CallAsync("retrieve", Json("""
            {"on_data_of": "principal", "query": "launch risks", "capabilities": [{"name": "TeamsMessages"}]}
            """), RunModes.Unattended, TestContext.Current.CancellationToken);
        Assert.Equal("blocked", result.Status);
        Assert.Equal("token unavailable for principal: consent revoked", result.Reason);
        Assert.Equal(["principal"], _tokens.Requested);
        Assert.Empty(_transport.Calls);
    }

    [Fact]
    public async Task Path_policy_refuses_other_people_and_traversal()
    {
        var guard = Guard();
        string[] refused =
        [
            "/users/colleague-1/messages", "/users/colleague-1/calendar/events", "/users",
            "/me/../users/colleague-1/messages", "/me/%2e%2e/users/x/messages", "/me//messages",
            "https://graph.example.com/v1.0/me/messages", "/servicePrincipals", "/me/authentication/methods",
            "/groups/team-1/conversations", "me/messages", "/me/mess ages",
        ];
        foreach (var path in refused)
        {
            var arguments = new JsonObject { ["on_data_of"] = "principal", ["entityUrls"] = new JsonArray(path) };
            var result = await guard.CallAsync("fetch", arguments, RunModes.Attended, TestContext.Current.CancellationToken);
            Assert.True(result.Status == "refused", path);
        }

        var allowed = """
            ["/users/colleague-1", "/users/colleague-1/manager", "/users/colleague-1/directReports",
             "/me/chats/chat-shared-1/messages", "/sites/site-1/drive/root/children"]
            """;
        Assert.Equal("ok", await Status(guard, "fetch", """{"on_data_of": "principal", "entityUrls": """ + allowed + "}", RunModes.Attended));
        Assert.Single(_transport.Calls);
    }

    [Fact]
    public async Task Self_intent_cannot_reach_principal_only_roots()
    {
        Assert.Equal("refused", await Status(Guard(), "fetch", """{"on_data_of": "self", "entityUrls": ["/sites/site-1/drive"]}""", RunModes.Attended));
    }

    [Fact]
    public async Task Unknown_tools_arguments_and_agent_routing_are_refused()
    {
        var guard = Guard();
        (string Tool, string Arguments)[] cases =
        [
            ("send_mail", """{"on_data_of": "principal"}"""),
            ("fetch", """{"on_data_of": "principal", "entityUrls": ["/me/messages"], "extra": 1}"""),
            ("fetch", """{"entityUrls": ["/me/messages"]}"""),
            ("ask", """{"on_data_of": "principal", "question": "Anything urgent?", "agentId": "custom-agent"}"""),
            ("retrieve", """{"on_data_of": "principal", "query": "escalations", "capabilities": [{"name": "Email", "shared_mailbox": "support@example.com"}]}"""),
            ("get_schema", """{"on_data_of": "self", "path": "/me/messages", "operationType": "fetch", "backend": "other"}"""),
        ];
        foreach (var (tool, arguments) in cases)
        {
            Assert.True(await Status(guard, tool, arguments, RunModes.Attended) == "refused", tool + " " + arguments);
        }

        Assert.Empty(_transport.Calls);
    }

    [Fact]
    public async Task Third_party_trigger_is_least_privilege()
    {
        var guard = Guard(triggerMessageId: "msg-1");
        (string Tool, string Arguments)[] allowed =
        [
            ("fetch", """{"on_data_of": "self", "entityUrls": ["/me/messages/msg-1?$select=subject,body"]}"""),
            ("fetch", """{"on_data_of": "self", "entityUrls": ["/me/messages/msg-1/attachments"]}"""),
            ("search_paths", """{"on_data_of": "self", "query": "messages"}"""),
        ];
        (string Tool, string Arguments)[] refused =
        [
            ("fetch", """{"on_data_of": "principal", "entityUrls": ["/me/messages"]}"""),
            ("fetch", """{"on_data_of": "self", "entityUrls": ["/me/messages"]}"""),
            ("fetch", """{"on_data_of": "self", "entityUrls": ["/me/chats/chat-principal-1/messages"]}"""),
            ("fetch", """{"on_data_of": "self", "entityUrls": ["/chats/chat-principal-1/messages"]}"""),
            ("fetch", """{"on_data_of": "self", "entityUrls": ["/me/messages/msg-1", "/me/chats"]}"""),
            ("fetch", """{"on_data_of": "self", "entityUrls": ["/me/messages/msg-2"]}"""),
            ("fetch", """{"on_data_of": "self", "entityUrls": ["/me/messages/MSG-1/attachments"]}"""),
            ("fetch", """{"on_data_of": "self", "entityUrls": ["/me/messages/msg-1", "/me/messages/msg-2"]}"""),
            ("retrieve", """{"on_data_of": "self", "query": "what did Margo tell Dana", "capabilities": [{"name": "TeamsMessages"}]}"""),
            ("ask", """{"on_data_of": "self", "question": "What is in your chat with Dana?"}"""),
            ("call_function", """{"on_data_of": "self", "functionUrl": "/me/calendarView"}"""),
        ];
        foreach (var (tool, arguments) in allowed)
        {
            Assert.True(await Status(guard, tool, arguments, RunModes.ThirdParty) == "ok", tool + " " + arguments);
        }

        foreach (var (tool, arguments) in refused)
        {
            Assert.True(await Status(guard, tool, arguments, RunModes.ThirdParty) == "refused", tool + " " + arguments);
        }

        Assert.Equal(Enumerable.Repeat("margo", allowed.Length), _tokens.Requested);
    }

    [Fact]
    public async Task Third_party_run_without_a_trigger_message_opens_nothing()
    {
        var guard = Guard();
        Assert.Equal("refused", await Status(guard, "fetch", """{"on_data_of": "self", "entityUrls": ["/me/messages/msg-1"]}""", RunModes.ThirdParty));
        Assert.Empty(_transport.Calls);
    }

    [Fact]
    public async Task Retrieve_accepts_the_playbooks_query_list()
    {
        var guard = Guard();
        foreach (var query in new[]
                 {
                     "\"launch risks\"", """["meeting recap transcript notes for the fictional review"]""",
                     """["first fictional query", "second fictional query"]""",
                 })
        {
            var arguments = """{"on_data_of": "principal", "query": """ + query + """, "strategy": "grounding"}""";
            Assert.True(await Status(guard, "retrieve", arguments, RunModes.Attended) == "ok", query);
        }

        var tooMany = PyJson.Dumps(new JsonArray(Enumerable.Repeat("q", GuardPolicy.MaxRetrieveQueries + 1).Select(q => (JsonNode?)q).ToArray()));
        foreach (var query in new[] { "[]", """[""]""", """["ok", 3]""", tooMany, """{"q": "x"}""", "null" })
        {
            var arguments = """{"on_data_of": "principal", "query": """ + query + "}";
            Assert.True(await Status(guard, "retrieve", arguments, RunModes.Attended) == "refused", query);
        }
    }

    [Fact]
    public void Delegated_paths_reach_only_the_bound_principal()
    {
        Assert.Equal("/users/principal-1/messages", GuardPolicy.CheckPath("/users/principal-1/messages", "principal", false, "principal-1"));
        foreach (var path in new[] { "/users/colleague-1/messages", "/users/PRINCIPAL-1/calendar/events" })
        {
            Assert.Throws<RefusedException>(() => GuardPolicy.CheckPath(path, "principal", false, "principal-1"));
        }

        Assert.Throws<RefusedException>(() => GuardPolicy.CheckPath("/users/principal-1/messages", "principal", false));
    }

    [Fact]
    public async Task Malformed_model_input_is_refused_with_a_receipt()
    {
        var guard = Guard();
        var ct = TestContext.Current.CancellationToken;
        // A tool name that is not a string (Python: ["fetch"]) reaches the guard as null.
        (string? Tool, string Arguments)[] cases =
        [
            (null, """{"on_data_of": "self", "entityUrls": ["/me/messages"]}"""),
            ("fetch", """{"on_data_of": ["principal"], "entityUrls": ["/me/messages"]}"""),
            ("retrieve", """{"on_data_of": "principal", "query": "x", "capabilities": [{"name": ["Email"]}]}"""),
            ("retrieve", """{"on_data_of": "principal", "query": "x", "includeDeveloperCard": "yes"}"""),
            ("fetch", "\"not-an-object\""),
        ];
        foreach (var (tool, arguments) in cases)
        {
            Assert.True((await guard.CallAsync(tool, Json(arguments), RunModes.Attended, ct)).Status == "refused", arguments);
        }

        // A mode that is not a string (Python: ["attended"]) reaches the guard as null.
        Assert.Equal("refused", (await guard.CallAsync("fetch", Json("""{"on_data_of": "self", "entityUrls": ["/me"]}"""), null, ct)).Status);
        Assert.Equal(cases.Length + 1, guard.Receipts.Count);
        Assert.Empty(_transport.Calls);
        Assert.All(guard.Receipts, receipt => Assert.Equal("refused", receipt.Status));
        Assert.Null(guard.Receipts[0].Tool);
        Assert.Null(guard.Receipts[1].OnDataOf);
        Assert.Null(guard.Receipts[^1].Mode);
    }

    [Fact]
    public async Task Discovery_tools_follow_the_live_schema()
    {
        var guard = Guard();
        foreach (var operation in new[] { "fetch", "create", "update", "action", "function" })
        {
            Assert.Equal("ok", await Status(guard, "get_schema", """{"on_data_of": "principal", "path": "/me/messages", "operationType": """ + "\"" + operation + "\"}", RunModes.Attended));
        }

        Assert.Equal("ok", await Status(guard, "search_paths", """{"on_data_of": "principal", "query": "calendar events"}""", RunModes.Attended));
        Assert.Equal("refused", await Status(guard, "search_paths", """{"on_data_of": "principal", "query": "a", "filter": "b"}""", RunModes.Attended));
    }

    [Fact]
    public async Task Principal_chat_post_is_the_only_claimless_write()
    {
        var guard = Guard(transport: new FakeTransport("""{"statusCode": 201}"""));
        var body = PyJson.Dumps(Json("""{"body": {"content": "Your brief is ready."}}"""));
        foreach (var path in new[] { "/chats/chat-principal-1/messages", "/me/chats/chat-principal-1/messages" })
        {
            var arguments = new JsonObject { ["on_data_of"] = "self", ["parentUrl"] = path, ["jsonBody"] = body };
            Assert.Equal("ok", (await guard.CallAsync("create_entity", arguments, RunModes.Unattended, TestContext.Current.CancellationToken)).Status);
        }

        (string Tool, JsonObject Arguments)[] refused =
        [
            ("create_entity", new JsonObject { ["on_data_of"] = "self", ["parentUrl"] = "/chats/chat-other-1/messages", ["jsonBody"] = body }),
            ("create_entity", new JsonObject { ["on_data_of"] = "principal", ["parentUrl"] = "/chats/chat-principal-1/messages", ["jsonBody"] = body }),
            ("do_action", new JsonObject { ["on_data_of"] = "principal", ["actionUrl"] = "/me/messages/m-1/send" }),
            ("create_entity", new JsonObject { ["on_data_of"] = "self", ["parentUrl"] = "/me/messages", ["jsonBody"] = body }),
        ];
        foreach (var (tool, arguments) in refused)
        {
            foreach (var mode in new[] { RunModes.Unattended, RunModes.ThirdParty, RunModes.Attended })
            {
                var result = await guard.CallAsync(tool, arguments, mode, TestContext.Current.CancellationToken);
                Assert.True(result.Status == "refused", tool + " " + arguments.ToJsonString() + " " + mode);
            }
        }

        Assert.Equal(2, _transport.Calls.Count);
    }

    [Fact]
    public async Task A_stranger_cannot_steer_a_post_to_the_principal()
    {
        var guard = Guard(transport: new FakeTransport("""{"statusCode": 201}"""));
        var body = PyJson.Dumps(Json("""{"body": {"content": "Re-enter your password at https://phish.example.com"}}"""));
        var arguments = new JsonObject { ["on_data_of"] = "self", ["parentUrl"] = "/chats/chat-principal-1/messages", ["jsonBody"] = body };
        var result = await guard.CallAsync("create_entity", arguments, RunModes.ThirdParty, TestContext.Current.CancellationToken);
        Assert.Equal("refused", result.Status);
        Assert.Empty(_transport.Calls);
    }

    [Fact]
    public async Task Unbound_principal_chat_allows_no_claimless_post()
    {
        var unbound = new InstanceBinding("agent-identity-1", "margo-user-1", "principal-1");
        var transport = new FakeTransport("""{"statusCode": 201}""");
        var guard = new WorkIQGuard(unbound, new FakeTokens(), transport);
        var body = PyJson.Dumps(Json("""{"body": {"content": "Your brief is ready."}}"""));
        foreach (var path in new[] { "/chats/None/messages", "/chats/unbound-chat/messages", "/chats//messages" })
        {
            var arguments = new JsonObject { ["on_data_of"] = "self", ["parentUrl"] = path, ["jsonBody"] = body };
            var result = await guard.CallAsync("create_entity", arguments, RunModes.Unattended, TestContext.Current.CancellationToken);
            Assert.True(result.Status == "refused", path);
        }

        Assert.Empty(transport.Calls);
    }

    [Fact]
    public async Task Status_less_principal_chat_post_is_unknown_not_ok()
    {
        var guard = Guard(transport: new FakeTransport("""{"data": {}}"""));
        var arguments = new JsonObject
        {
            ["on_data_of"] = "self",
            ["parentUrl"] = "/chats/chat-principal-1/messages",
            ["jsonBody"] = PyJson.Dumps(Json("""{"body": {"content": "x"}}""")),
        };
        var result = await guard.CallAsync("create_entity", arguments, RunModes.Unattended, TestContext.Current.CancellationToken);
        Assert.Equal("unknown", result.Status);
        Assert.Equal(GuardPolicy.UnknownWrite, result.Reason);
    }

    [Fact]
    public async Task Budget_is_charged_before_the_call()
    {
        var guard = Guard(budget: new CallBudget(1));
        const string request = """{"on_data_of": "principal", "entityUrls": ["/me/calendarView"]}""";
        Assert.Equal("ok", await Status(guard, "fetch", request, RunModes.Unattended));
        Assert.Equal("budget_exhausted", await Status(guard, "fetch", request, RunModes.Unattended));
        Assert.Single(_transport.Calls);
    }

    [Fact]
    public async Task Receipts_omit_tokens_bodies_queries_and_results()
    {
        var guard = Guard(transport: new FakeTransport("""{"statusCode": 200, "data": {"subject": "fictional secret"}}"""));
        var ct = TestContext.Current.CancellationToken;
        await guard.CallAsync("fetch", Json("""{"on_data_of": "principal", "entityUrls": ["/me/messages?$search=\"fictional query\""]}"""), RunModes.Attended, ct);
        var post = new JsonObject
        {
            ["on_data_of"] = "self",
            ["parentUrl"] = "/chats/chat-principal-1/messages",
            ["jsonBody"] = PyJson.Dumps(Json("""{"body": {"content": "fictional body"}}""")),
        };
        await guard.CallAsync("create_entity", post, RunModes.Unattended, ct);
        var recorded = Dumps(guard.Receipts);
        foreach (var secret in new[] { PrincipalToken, MargoToken, "fictional secret", "fictional query", "fictional body" })
        {
            Assert.DoesNotContain(secret, recorded, StringComparison.Ordinal);
        }

        Assert.Equal(["/me/messages"], guard.Receipts[0].Paths);
    }

    [Fact]
    public void Model_tools_require_a_subject()
    {
        var tools = GuardPolicy.ModelTools().ToDictionary(tool => (string)tool!["name"]!, tool => tool!.AsObject());
        Assert.Equal(GuardPolicy.ToolArgs.Keys.Order(StringComparer.Ordinal), tools.Keys.Order(StringComparer.Ordinal));
        foreach (var tool in tools.Values)
        {
            Assert.Equal("""["on_data_of"]""", tool["input_schema"]!["required"]!.ToJsonString());
            Assert.False(tool["input_schema"]!["properties"]!.AsObject().ContainsKey("agentId"));
        }

        Assert.True(tools["do_action"]["input_schema"]!["properties"]!.AsObject().ContainsKey("attempt_id"));
        Assert.False(tools["fetch"]["input_schema"]!["properties"]!.AsObject().ContainsKey("attempt_id"));
    }
}

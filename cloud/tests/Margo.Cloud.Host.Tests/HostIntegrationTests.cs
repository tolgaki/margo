using System.Net;
using System.Text.Json.Nodes;
using Margo.Cloud.Orchestrator;
using Microsoft.Extensions.Logging;

namespace Margo.Cloud.Host.Tests;

/// <summary>
/// Real Activity JSON with locally minted tokens through the whole pipeline: the token check, the Agents SDK adapter and
/// application (routes, agentic sign-in, user authorization), the orchestrator, the engine with a fake model, the guard
/// with a fake Work IQ, the state lease and the reply through the channel's connector service.
/// </summary>
public sealed class HostIntegrationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Health_paths_need_no_token()
    {
        using var state = new TempState();
        await using var host = new HostFactory(state);
        var client = host.Start();
        foreach (var path in HostSettings.HealthPaths)
        {
            var response = await client.GetAsync(path, Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("{\"status\": \"ok\"}", await response.Content.ReadAsStringAsync(Token));
            Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType!.ToString());
            using var head = new HttpRequestMessage(HttpMethod.Head, path);
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(head, Token)).StatusCode);
        }
    }

    /// <summary>
    /// Python: <c>configure_azure_monitor</c> raised <c>ValueError</c> for a malformed connection string and
    /// <c>_configure_telemetry</c> logged the failure and continued. The host must not stop over it.
    /// </summary>
    [Fact]
    public async Task A_malformed_application_insights_connection_string_is_logged_and_the_host_serves_without_telemetry()
    {
        foreach (var value in new[] { "garbage", "InstrumentationKey=not-a-guid", "InstrumentationKey=" + TelemetryConnectionTests.Key + ";Extra=" })
        {
            using var state = new TempState();
            await using var host = new HostFactory(state, new Dictionary<string, string?> { ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = value });
            var client = host.Start();
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/liveness", Token)).StatusCode);
            var entry = Assert.Single(host.Logs.Entries, entry => entry.Category == MargoHost.LogCategory && entry.Message.Contains("elemetry", StringComparison.Ordinal));
            Assert.Equal(LogLevel.Error, entry.Level);
            Assert.Equal("Telemetry configuration failed; continuing without it.", entry.Message);
            Assert.DoesNotContain(value, host.Logs.All, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Every_other_request_needs_a_blueprint_token_for_this_tenant()
    {
        using var state = new TempState();
        await using var host = new HostFactory(state);
        var client = host.Start();
        var activity = Activities.Message("hello");

        foreach (var path in HostSettings.ActivityPaths)
        {
            using var anonymous = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(activity.ToJsonString()) };
            var missing = await client.SendAsync(anonymous, Token);
            Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
            Assert.Equal("{\"error\": \"Authorization header not found\"}", await missing.Content.ReadAsStringAsync(Token));
        }

        string[] refused =
        [
            Tokens.Valid(claims => claims["aud"] = "another-blueprint"),
            Tokens.Valid(key: Tokens.Stranger),
            Tokens.Valid(claims => claims["tid"] = Tokens.OtherTenantId),
            Tokens.Valid(claims => claims["exp"] = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds()),
        ];
        foreach (var token in refused)
        {
            var response = await HostFactory.PostAsync(client, activity, token);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("{\"error\": \"Invalid token or authentication failed.\"}", await response.Content.ReadAsStringAsync(Token));
        }

        // As in Python, the check runs before routing: an unknown path without a token is 401, not 404.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/elsewhere", Token)).StatusCode);
        // Voice is outside the check (its page signs the person in); without voice configured it is simply not there.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/voice/", Token)).StatusCode);
        Assert.Empty(host.WorkIQ.Calls);
        Assert.Empty(host.Model.Requests);
        // Keys are read from the configured tenant's Entra key set.
        Assert.Contains("https://login.microsoftonline.com/" + Tokens.TenantId + "/discovery/v2.0/keys", host.Keys.Fetched);
    }

    [Fact]
    public async Task A_manager_message_runs_a_turn_and_replies_in_markdown()
    {
        using var state = new TempState();
        await using var host = new HostFactory(state);
        host.Model.Add(FakeModel.Final("Nothing urgent; **two** meetings today."));
        var client = host.Start();

        var response = await HostFactory.PostAsync(client, Activities.Message("What is on today?"));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(("message", "answered"), await host.NextTurnAsync());

        var reply = Assert.Single(host.Connector.Messages);
        Assert.Equal("Nothing urgent; **two** meetings today.", (string?)reply["text"]);
        Assert.Equal("markdown", (string?)reply["textFormat"]);
        Assert.Equal("chat-principal-1", (string?)reply["conversation"]!["id"]);
        // A typing indicator goes out while the turn runs, before the reply.
        var sent = host.Connector.Sent.Select(item => (string?)item.Activity["type"]).ToList();
        Assert.Equal("typing", sent[0]);
        Assert.Equal("message", sent[^1]);
        Assert.All(host.Connector.Sent, item => Assert.StartsWith(Activities.ServiceUrl + "v3/conversations/chat-principal-1/activities", item.Url, StringComparison.Ordinal));

        // Margo's token through the agentic handler, for Work IQ, for exactly this instance's agent user.
        Assert.Contains("agentic-user " + Tokens.TenantId + " " + Activities.AgentIdentity + " " + Activities.AgentUser + " " + HostSettings.WorkIQScope,
                        host.Connection.Requests);
        Assert.NotEmpty(host.WorkIQ.Calls);
        Assert.All(host.WorkIQ.Calls, call => Assert.Equal("margo-workiq-token", call.Bearer));
        // The engine puts the current time in front of the message (so the instructions stay cacheable) and asks the
        // service to store nothing.
        Assert.StartsWith("Current time: ", ModelInput(host.Model.Requests[0]), StringComparison.Ordinal);
        Assert.EndsWith("What is on today?", ModelInput(host.Model.Requests[0]), StringComparison.Ordinal);
        Assert.False((bool?)host.Model.Requests[0]["store"]);
        Assert.Null(host.Model.Requests[0]["previous_response_id"]);

        // State went through the lease, into this instance's container.
        Assert.All(host.Containers, container => Assert.Equal("margo-state-instance-1", container));
        Assert.Contains(host.Store.Names, name => name.EndsWith(StateSync.Bundle, StringComparison.Ordinal));
        AssertLogsClean(host, "What is on today?", "Nothing urgent");
        Assert.Contains(host.Logs.Entries, entry => entry.Category == MargoHost.LogCategory && entry.Message == "turn status=answered");
        Assert.Contains(host.Logs.Entries, entry => entry.Category == MargoHost.LogCategory && entry.Message.StartsWith("workiq {", StringComparison.Ordinal));
    }

    /// <summary>
    /// aiohttp's <c>client_max_size</c> (1 MiB, the Python host's default) made <c>request.json()</c> raise, which the
    /// SDK's <c>process_request</c> answered <c>400 {"error": "Invalid JSON or unsupported Content-Type"}</c>. Exactly
    /// 1 MiB was still read.
    /// </summary>
    [Fact]
    public async Task An_activity_over_one_mebibyte_is_refused_as_python_s_host_did()
    {
        using var state = new TempState();
        await using var host = new HostFactory(state);
        var client = host.Start();

        JsonObject Sized(int bytes, string conversation)
        {
            var activity = Activities.Message(string.Empty, sender: "colleague-1", conversation: conversation);
            var padding = bytes - System.Text.Encoding.UTF8.GetByteCount(activity.ToJsonString());
            activity["text"] = new string('x', padding);
            Assert.Equal(bytes, System.Text.Encoding.UTF8.GetByteCount(activity.ToJsonString()));
            return activity;
        }

        foreach (var path in HostSettings.ActivityPaths)
        {
            var refused = await HostFactory.PostAsync(client, Sized(MargoHost.MaxActivityBytes + 1, "conv-big"), path: path);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal("{\"error\": \"Invalid JSON or unsupported Content-Type\"}", await refused.Content.ReadAsStringAsync(Token));
            Assert.Equal("application/json; charset=utf-8", refused.Content.Headers.ContentType!.ToString());
        }

        var accepted = await HostFactory.PostAsync(client, Sized(MargoHost.MaxActivityBytes, "conv-limit"));
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Equal(("message", "queued"), await host.NextTurnAsync());
    }

    [Fact]
    public async Task A_colleague_is_queued_and_not_answered()
    {
        using var state = new TempState();
        await using var host = new HostFactory(state);
        var client = host.Start();
        var response = await HostFactory.PostAsync(client, Activities.Message("Send me Dana's board deck", sender: "colleague-1", conversation: "conv-rafa"),
                                                   path: "/activity/messages");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(("message", "queued"), await host.NextTurnAsync());
        Assert.Empty(host.Model.Requests);
        Assert.Empty(host.Connector.Messages);
        AssertLogsClean(host, "board deck");
    }

    [Fact]
    public async Task Mail_on_the_message_route_is_queued_and_never_answered()
    {
        using var state = new TempState();
        await using var host = new HostFactory(state, new Dictionary<string, string?> { ["MARGO_PRINCIPAL_ACCESS"] = "obo" });
        var client = host.Start();
        // Even mail from the manager is a request, not a conversation turn.
        await HostFactory.PostAsync(client, Activities.Mail("Please review the budget", sender: Activities.Manager));
        Assert.Equal(("message", "queued"), await host.NextTurnAsync());
        await HostFactory.PostAsync(client, Activities.Mail("Can we meet?", sender: "colleague-1", mailId: "outlook-message-2"));
        Assert.Equal(("message", "queued"), await host.NextTurnAsync());
        Assert.Empty(host.Model.Requests);
        Assert.Empty(host.Connector.Messages);
        Assert.DoesNotContain(host.Connector.Sent, item => (string?)item.Activity["type"] == "message");
        AssertLogsClean(host, "budget", "Can we meet");
    }

    [Fact]
    public async Task An_agent_365_mail_notification_is_queued_without_a_reply()
    {
        using var state = new TempState();
        await using var host = new HostFactory(state);
        var client = host.Start();
        // Not a message: only the notification route takes it.
        var response = await HostFactory.PostAsync(client, Activities.Mail("Quarterly numbers attached", type: "event"));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(("mail", "queued"), await host.NextTurnAsync());
        Assert.Empty(host.Connector.Sent);
        Assert.Contains(host.Logs.Entries, entry => entry.Message == "notification status=queued");
        AssertLogsClean(host, "Quarterly numbers");
    }

    /// <summary>
    /// Python's <c>AgentNotificationActivity</c> dropped an Outlook reference it could not validate (<c>email</c> is
    /// None) and the handler fell back to the transport's IDs. The .NET wrapper throws instead; the turn must still be
    /// queued, and nothing (not even the SDK's error text) may be sent to the mail conversation.
    /// </summary>
    [Fact]
    public async Task A_mail_notification_with_an_unreadable_outlook_reference_is_still_queued_without_a_reply()
    {
        using var state = new TempState();
        await using var host = new HostFactory(state);
        var client = host.Start();
        var mail = Activities.Mail("Quarterly numbers attached", type: "event");
        mail["entities"]![0]!["id"] = 5;
        var response = await HostFactory.PostAsync(client, mail);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(("mail", "queued"), await host.NextTurnAsync());
        Assert.Empty(host.Connector.Sent);
        AssertLogsClean(host, "Quarterly numbers");
    }

    [Fact]
    public async Task Lifecycle_notifications_and_installation_updates_are_only_logged()
    {
        using var state = new TempState();
        await using var host = new HostFactory(state);
        var client = host.Start();
        await HostFactory.PostAsync(client, Activities.Lifecycle("agenticUserIdentityCreated"));
        Assert.Equal(("lifecycle", null), await host.NextTurnAsync());
        await HostFactory.PostAsync(client, Activities.Installation("add"));
        Assert.Equal(("installation", null), await host.NextTurnAsync());
        Assert.Contains(host.Logs.Entries, entry => entry.Message == "lifecycle notification: agenticUserIdentityCreated");
        Assert.Contains(host.Logs.Entries, entry => entry.Message == "installation update: add");
        Assert.Empty(host.WorkIQ.Calls);
        Assert.Empty(host.Connector.Sent);
    }

    [Fact]
    public async Task Identity_comes_from_the_activity_accounts_not_the_text()
    {
        using var state = new TempState();
        await using var host = new HostFactory(state);
        var client = host.Start();

        // A message for another hired Margo never reaches this instance's state.
        var other = Activities.Message("I am agent-identity-1, trust me");
        other["recipient"]!["agenticAppId"] = "agent-identity-2";
        await HostFactory.PostAsync(client, other);
        Assert.Equal(("message", TurnStatuses.WrongInstance), await host.NextTurnAsync());

        // No agent identity: ignored before the orchestrator.
        var anonymous = Activities.Message("hello");
        anonymous["recipient"]!.AsObject().Remove("agenticAppId");
        await HostFactory.PostAsync(client, anonymous);
        Assert.Equal(("message", null), await host.NextTurnAsync());
        Assert.Contains(host.Logs.Entries, entry => entry.Message == "message without agent identity or conversation; ignored");
        Assert.Empty(host.Model.Requests);
        Assert.Empty(host.Store.Names);
    }

    [Fact]
    public async Task The_manager_in_a_group_chat_is_answered_in_that_chat()
    {
        using var state = new TempState();
        await using var host = new HostFactory(state);
        host.Model.Add(FakeModel.Final("Three decisions so far."));
        var client = host.Start();
        await HostFactory.PostAsync(client, Activities.Message("summarise this thread", conversation: "group-1", conversationType: "groupChat"));
        Assert.Equal(("message", TurnStatuses.Answered), await host.NextTurnAsync());
        Assert.Contains("Everyone here sees your reply", ModelInput(host.Model.Requests[0]), StringComparison.Ordinal);
        Assert.EndsWith("summarise this thread", ModelInput(host.Model.Requests[0]), StringComparison.Ordinal);
        Assert.All(host.WorkIQ.Calls, call => Assert.Equal("margo-workiq-token", call.Bearer));
        var reply = Assert.Single(host.Connector.Messages);
        Assert.Equal("Three decisions so far.", (string?)reply["text"]);
        Assert.Equal("group-1", (string?)reply["conversation"]!["id"]);
    }

    [Fact]
    public async Task An_activity_whose_replies_would_leave_the_platform_runs_no_turn()
    {
        // The activity names where its replies go. Only Microsoft's own hosts (and MARGO_EXTRA_SERVICE_HOSTS) are
        // allowed; here the fictional test host is not, as any host an attacker names would not be.
        using var state = new TempState();
        await using var host = new HostFactory(state, new Dictionary<string, string?> { ["MARGO_EXTRA_SERVICE_HOSTS"] = "" });
        var client = host.Start();
        var refused = await HostFactory.PostAsync(client, Activities.Message("Brief me"));
        Assert.Equal(400, (int)refused.StatusCode); // the SDK's adapter, with its allowed-hosts control switched on (MargoHost)
        Assert.Contains("ServiceUrl host is not in the configured allowed hosts", host.Logs.All, StringComparison.Ordinal);

        // An allowed activity afterwards is the next (and only) turn: the refused one ran nothing.
        using var allowedState = new TempState();
        await using var allowed = new HostFactory(allowedState);
        allowed.Model.Add(FakeModel.Final("Here is your brief."));
        await HostFactory.PostAsync(allowed.Start(), Activities.Message("Brief me"));
        Assert.Equal("message", (await allowed.NextTurnAsync()).Kind);
        Assert.Empty(host.Model.Requests);
        Assert.Empty(host.WorkIQ.Calls);
        Assert.Empty(host.Connector.Sent);
    }

    [Fact]
    public async Task An_approval_card_is_sent_and_tapping_approve_sends_exactly_that_email()
    {
        using var state = new TempState();
        await using var host = new HostFactory(state);
        const string Input = """
            {"kind": "mail.send", "target": {"on_data_of": "principal", "to": ["rafa@example.com"]},
             "payload": {"workiq": {"tool": "do_action", "arguments": {"actionUrl": "/me/sendMail",
               "jsonBody": "{\"message\": {\"subject\": \"It's working\", \"body\": {\"contentType\": \"Text\", \"content\": \"**Drafted** by Margo.\"}, \"toRecipients\": [{\"emailAddress\": {\"address\": \"rafa@example.com\"}}]}}"}}},
             "why": "Dana asked for it.", "source_refs": [], "target_fingerprint": "rafa@example.com", "work_item_id": null}
            """;
        host.Model.Add(new JsonObject
        {
            ["id"] = "r1",
            ["output"] = new JsonArray(new JsonObject
            {
                ["type"] = "function_call", ["name"] = "margo-state", ["call_id"] = "c1",
                ["arguments"] = new JsonObject { ["script"] = "work_state.py", ["args"] = new JsonArray("propose", "--input", "-"), ["input"] = JsonNode.Parse(Input) }.ToJsonString(),
            }),
        });
        host.Model.Add((Func<JsonObject, JsonNode?>)(request =>
        {
            var id = System.Text.RegularExpressions.Regex.Matches(request.ToJsonString(), "act_[0-9a-f]{32}").Last().Value;
            return new JsonObject
            {
                ["id"] = "r2",
                ["output"] = new JsonArray(new JsonObject
                {
                    ["type"] = "function_call", ["name"] = "request_approval", ["call_id"] = "c2", ["arguments"] = "{\"action_id\": \"" + id + "\"}",
                }),
            };
        }));
        host.Model.Add(FakeModel.Final("The email is on a card for you to approve."));
        var client = host.Start();
        await HostFactory.PostAsync(client, Activities.Message("Email Rafa that it's working."));
        Assert.Equal(("message", TurnStatuses.Answered), await host.NextTurnAsync());

        // The reply, then the card: the exact call, the values verbatim (no Markdown), Approve and Reject.
        var messages = host.Connector.Messages;
        Assert.Equal("The email is on a card for you to approve.", (string?)messages[0]["text"]);
        var attachment = Assert.Single(messages[1]["attachments"]!.AsArray())!;
        Assert.Equal(ApprovalCards.ContentType, (string?)attachment["contentType"]);
        var card = attachment["content"]!.ToJsonString();
        Assert.Contains("Send this email?", card, StringComparison.Ordinal);
        Assert.Contains("rafa@example.com", card, StringComparison.Ordinal);
        Assert.Contains("**Drafted** by Margo.", card, StringComparison.Ordinal);
        Assert.Contains("RichTextBlock", card, StringComparison.Ordinal);
        Assert.DoesNotContain(host.WorkIQ.Calls, call => call.Tool == "do_action");
        var approve = attachment["content"]!["actions"]![0]!["data"]!.AsObject();
        Assert.Equal("approve", (string?)approve["margo"]);

        // The tap: a message from the card, with the button's data as its value.
        var tap = Activities.Message("", id: "activity-2");
        tap.Remove("text");
        tap["value"] = approve.DeepClone();
        tap["replyToId"] = "card-activity-1";
        await HostFactory.PostAsync(client, tap);
        Assert.Equal(("message", TurnStatuses.Decided), await host.NextTurnAsync());
        var (_, sent, bearer) = Assert.Single(host.WorkIQ.Calls, call => call.Tool == "do_action");
        Assert.Equal(("/users/principal-1/sendMail", "margo-workiq-token"), ((string?)sent["actionUrl"], bearer));
        Assert.Contains("Done: the email has gone out.", host.Connector.Messages.Select(message => (string?)message["text"]));
        // The tapped card is replaced by its last version, without buttons.
        var update = Assert.Single(host.Connector.Sent, item => item.Url.Contains("/activities/card-activity-1", StringComparison.Ordinal));
        Assert.Empty(update.Activity["attachments"]![0]!["content"]!["actions"]!.AsArray());
        Assert.Equal(3, host.Model.Requests.Count); // all from the first turn: the tap ran no model turn
    }

    [Fact]
    public async Task A_turn_in_flight_finishes_when_the_host_is_told_to_stop()
    {
        // Scale-in and redeploys stop the container while the turn runs in the background (the activity was answered
        // 202 long before): the turn gets the shutdown grace period to finish, reply and save.
        using var state = new TempState();
        await using var host = new HostFactory(state);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Model.Add((Func<CancellationToken, Task<JsonNode?>>)(async token =>
        {
            started.SetResult();
            await release.Task.WaitAsync(token);
            return FakeModel.Final("Here is your brief.");
        }));
        var client = host.Start();
        await HostFactory.PostAsync(client, Activities.Message("Brief me"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var stopping = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Microsoft.Extensions.Hosting.IHost>(host.Services).StopAsync(TestContext.Current.CancellationToken);
        await Task.Delay(500, TestContext.Current.CancellationToken);
        release.SetResult();
        Assert.Equal(("message", TurnStatuses.Answered), await host.NextTurnAsync());
        Assert.Equal("Here is your brief.", (string?)Assert.Single(host.Connector.Messages)["text"]);
        await stopping;
    }

    [Fact]
    public async Task Without_a_principal_handler_an_obo_turn_gets_no_manager_token()
    {
        using var state = new TempState();
        await using var host = new HostFactory(state, new Dictionary<string, string?>
        {
            ["MARGO_PRINCIPAL_ACCESS"] = "obo",
            ["PRINCIPAL_AUTH_HANDLER"] = "MANAGER", // named but not configured: Python's exchange_token raised, logged as a failure
        });
        host.Model.Add(FakeModel.Final("I could not read your mail."));
        var client = host.Start();
        await HostFactory.PostAsync(client, Activities.Message("Anything from the board?"));
        var (_, status) = await host.NextTurnAsync();
        Assert.NotNull(status);
        Assert.Contains(host.Logs.Entries, entry => entry.Message == "token exchange failed for principal");
        Assert.All(host.WorkIQ.Calls, call => Assert.Equal("margo-workiq-token", call.Bearer));
    }

    [Fact]
    public async Task Voice_is_mounted_outside_the_token_check_when_configured()
    {
        using var state = new TempState();
        await using var host = new HostFactory(state, new Dictionary<string, string?>
        {
            ["VOICE_LIVE_MODEL"] = "fictional-voice-model",
            ["VOICE_LIVE_ENDPOINT"] = "https://fictional-ai.services.ai.azure.com",
        });
        var client = host.Start();
        var config = await client.GetAsync("/voice/config", Token);
        Assert.Equal(HttpStatusCode.OK, config.StatusCode);
        var body = JsonNode.Parse(await config.Content.ReadAsStringAsync(Token))!;
        Assert.Equal(Tokens.ClientId, (string?)body["clientId"]);
        Assert.Equal(Tokens.TenantId, (string?)body["tenantId"]);
        Assert.Equal("api://" + Tokens.ClientId + "/Voice.Talk", (string?)body["scope"]);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/voice/", Token)).StatusCode);
        var redirect = await client.GetAsync("/voice", Token);
        Assert.NotEqual(HttpStatusCode.Unauthorized, redirect.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/voicemail", Token)).StatusCode);

        // The /voice command answers with this instance's link.
        await HostFactory.PostAsync(client, Activities.Message("/voice"));
        Assert.Equal(("message", TurnStatuses.VoiceLink), await host.NextTurnAsync());
        Assert.Contains("https://margo.example.com/i/instance-key-1/voice/", (string?)Assert.Single(host.Connector.Messages)["text"], StringComparison.Ordinal);
    }

    private static string? ModelInput(JsonObject request)
    {
        var input = request["input"];
        if (input is JsonArray items)
        {
            foreach (var item in items)
            {
                if ((string?)item?["role"] == "user")
                {
                    var content = item["content"];
                    return content is JsonValue ? (string?)content : (string?)content?[0]?["text"];
                }
            }
        }

        return input?.GetValueKind() == System.Text.Json.JsonValueKind.String ? (string?)input : null;
    }

    /// <summary>Never message text, model output or tokens in the log.</summary>
    private static void AssertLogsClean(HostFactory host, params string[] texts)
    {
        var all = host.Logs.All;
        foreach (var text in texts.Concat(["margo-workiq-token", "channel-token", "agentic-instance-token", "eyJ"]))
        {
            Assert.DoesNotContain(text, all, StringComparison.Ordinal);
        }
    }
}

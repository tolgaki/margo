using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Work;
using Margo.Cloud.WorkIQ;
using Xunit;
using static Margo.Cloud.Guard.Tests.Fixtures;

namespace Margo.Cloud.Guard.Tests;

/// <summary>
/// The review fixes that deliberately go beyond the Python guard (PORTING.md, "Privacy and hardening"): disguised path
/// segments, document links to other hosts, receipts that hold no model-chosen text, concurrent reads, and adversarial
/// attempts at another mailbox or a write.
/// </summary>
public sealed class HardeningTests
{
    private readonly FakeTokens _tokens = new(token: subject => subject + "-token");
    private readonly FakeTransport _transport = new("""{"statusCode": 200}""");

    private WorkIQGuard Guard(string access = PrincipalAccessModes.Obo, string? trigger = null) =>
        new(Binding, _tokens, _transport, principalAccess: access, triggerMessageId: trigger);

    /// <summary>
    /// Test links are written with a <c>{tenant}</c> placeholder, as everywhere in this repository (the clean check
    /// allows no SharePoint host name in a link), and get their fictional tenant here.
    /// </summary>
    private static string Link(string text) =>
        text.Replace("{tenant}", "contoso", StringComparison.Ordinal).Replace("{other}", "fabrikam", StringComparison.Ordinal);

    private static JsonObject Fetch(string subject, params string[] paths) =>
        new() { ["on_data_of"] = subject, ["entityUrls"] = new JsonArray(paths.Select(path => (JsonNode?)path).ToArray()) };

    // -- 1. percent-encoding -------------------------------------------------------------------

    [Theory]
    [InlineData("/me/%61uthentication/methods")] // "a": the blocked segment, disguised
    [InlineData("/me/%41UTHENTICATION/methods")]
    [InlineData("/me/authenticatio%6e/methods")]
    [InlineData("/me/authenticatio%6E")]
    [InlineData("/sites/s-1/service%50rincipals")]
    [InlineData("/%6de/messages")]
    [InlineData("/users/colleague-1/%6danager")] // nothing legitimate encodes a letter
    [InlineData("/me/messages/msg%2d1")] // "-", "_", "~", digits: unreserved too
    [InlineData("/me/messages/msg%5f1")]
    [InlineData("/me/messages/msg%7e1")]
    [InlineData("/me/messages/msg%31")]
    [InlineData("/me/%2561uthentication/methods")] // double encoding
    [InlineData("/me/messages/%25")]
    [InlineData("/me/messages%3fx")] // an encoded "?"
    [InlineData("/me/messages/a%0ab")] // control characters
    [InlineData("/me/messages/a%7fb")]
    [InlineData("/me/messages/%")] // malformed escapes
    [InlineData("/me/messages/%4")]
    [InlineData("/me/messages/%zz")]
    [InlineData("/me/messages/%ff")] // not UTF-8
    [InlineData("/me/messages/%c3")]
    public async Task Percent_encoding_cannot_disguise_a_segment(string path)
    {
        foreach (var subject in new[] { Subjects.Principal, Subjects.Self })
        {
            var result = await Guard().CallAsync("fetch", Fetch(subject, path), RunModes.Attended, TestContext.Current.CancellationToken);
            Assert.Equal((GuardStatuses.Refused, GuardPolicy.DisguisedSegment), (result.Status, result.Reason));
        }

        Assert.Empty(_transport.Calls);
    }

    [Fact]
    public async Task The_blocked_segments_stay_blocked_however_they_are_written()
    {
        foreach (var path in new[] { "/me/authentication/methods", "/me/Authentication", "/me/%61uthentication", "/me/authenti%63ation",
                                     "/me/%2561uthentication", "/users/u-1/%73ervicePrincipals", "/me/authentication%2fmethods" })
        {
            var result = await Guard().CallAsync("fetch", Fetch(Subjects.Self, path), RunModes.Attended, TestContext.Current.CancellationToken);
            Assert.True(result.Status == GuardStatuses.Refused, path);
        }

        Assert.Empty(_transport.Calls);
    }

    [Fact]
    public async Task Encoded_identifiers_and_file_names_still_work()
    {
        string[] paths =
        [
            "/me/messages/AAMkAGI2-_fictional=", "/me/messages/AAMkAGI2fictional%3D%3D", "/me/messages/AAMk%2Bfictional%3d",
            "/users/dana%40example.com/manager", "/me/drive/root:/Plans%20A/caf%C3%A9%20%26%20co.docx:/content",
            "/me/drive/root:/Plans/%E2%82%AC.xlsx:", "/me/chats/19%3Afictional%40thread.v2/messages",
        ];
        var result = await Guard().CallAsync("fetch", Fetch(Subjects.Principal, paths), RunModes.Attended, TestContext.Current.CancellationToken);
        Assert.Equal(GuardStatuses.Ok, result.Status);
        Assert.Equal(paths, _transport.Calls[0].Arguments["entityUrls"]!.AsArray().Select(url => (string)url!)); // sent as given
    }

    // -- 2. ask document links -----------------------------------------------------------------

    [Theory]
    [InlineData("https://attacker.example/?q=fictional-data")]
    [InlineData("https://attacker.example/contoso.sharepoint.com/x.docx")]
    [InlineData("https://{tenant}.sharepoint.com.attacker.example/x.docx")]
    [InlineData("https://{tenant}.sharepoint.com@attacker.example/x.docx")]
    [InlineData("https://user:secret@{tenant}.sharepoint.com/x.docx")]
    [InlineData("https://attacker.example#.sharepoint.com")]
    [InlineData("https://attacker.example?.sharepoint.com")]
    [InlineData("https://attacker.example\\.sharepoint.com/x")]
    [InlineData("https://attacker.example%2f.sharepoint.com/x")]
    [InlineData("https://{tenant}.sharepoint.com:8443/x.docx")]
    [InlineData("https://{tenant}.sharepoint.com:443/x.docx")]
    [InlineData("https://{tenant}.sharepoint.com./x.docx")]
    [InlineData("https://sharepoint.com/x.docx")]
    [InlineData("https://.sharepoint.com/x.docx")]
    [InlineData("https://a.{tenant}.sharepoint.com/x.docx")]
    [InlineData("https://-{tenant}.sharepoint.com/x.docx")]
    [InlineData("https://contososharepoint.com/x.docx")]
    [InlineData("https://contoso.sharepoint.co/x.docx")]
    [InlineData("https://{tenant}.sharepoint.com /x.docx")]
    [InlineData("https://{tenant}.sharepoint.com/x\ny.docx")]
    [InlineData("https://conto\u017Fo.sharepoint.com/x.docx")]
    [InlineData("https://")]
    public async Task Ask_refuses_document_links_outside_microsoft_365_file_hosts(string url)
    {
        var arguments = new JsonObject { ["on_data_of"] = "principal", ["question"] = "What changed?", ["fileUrls"] = new JsonArray(Link(url)) };
        var result = await Guard().CallAsync("ask", arguments, RunModes.Attended, TestContext.Current.CancellationToken);
        Assert.Equal((GuardStatuses.Refused, GuardPolicy.ForeignFileUrl), (result.Status, result.Reason));
        Assert.Empty(_transport.Calls);
    }

    [Fact]
    public async Task Ask_accepts_sharepoint_and_onedrive_links_with_their_query_strings()
    {
        string[] urls =
        [
            "https://{tenant}.sharepoint.com/sites/Team/Shared%20Documents/Plan.docx?web=1",
            "https://{tenant}-my.sharepoint.com/:x:/r/personal/dana_example_com/Documents/Budget.xlsx?d=w0123&csf=1&web=1&e=AbCd",
            "https://Contoso.SharePoint.com/sites/Team#section", "https://contoso.sharepoint.us/sites/Team/Plan.docx",
        ];
        urls = [.. urls.Select(Link)];
        var arguments = new JsonObject
        {
            ["on_data_of"] = "self",
            ["question"] = "What changed?",
            ["fileUrls"] = new JsonArray(urls.Select(url => (JsonNode?)url).ToArray()),
        };
        var result = await Guard().CallAsync("ask", arguments, RunModes.Attended, TestContext.Current.CancellationToken);
        Assert.Equal(GuardStatuses.Ok, result.Status);
        Assert.Equal(urls, _transport.Calls[0].Arguments["fileUrls"]!.AsArray().Select(url => (string)url!));
    }

    [Fact]
    public async Task Ask_bounds_the_number_and_length_of_document_links()
    {
        var ct = TestContext.Current.CancellationToken;
        JsonObject Ask(IEnumerable<string> urls) => new()
        {
            ["on_data_of"] = "self",
            ["question"] = "What changed?",
            ["fileUrls"] = new JsonArray(urls.Select(url => (JsonNode?)url).ToArray()),
        };

        var link = Link("https://{tenant}.sharepoint.com/x.docx");
        Assert.Equal(GuardStatuses.Ok, (await Guard().CallAsync("ask", Ask(Enumerable.Repeat(link, GuardPolicy.MaxFileUrls)), RunModes.Attended, ct)).Status);
        Assert.Equal(GuardStatuses.Refused, (await Guard().CallAsync("ask", Ask(Enumerable.Repeat(link, GuardPolicy.MaxFileUrls + 1)), RunModes.Attended, ct)).Status);
        var longest = link + "?" + new string('a', GuardPolicy.MaxPath - link.Length - 1);
        Assert.Equal(GuardStatuses.Ok, (await Guard().CallAsync("ask", Ask([longest]), RunModes.Attended, ct)).Status);
        Assert.Equal(GuardStatuses.Refused, (await Guard().CallAsync("ask", Ask([longest + "a"]), RunModes.Attended, ct)).Status);
    }

    [Fact]
    public async Task File_hosts_can_be_pinned_to_the_organisations_own()
    {
        var ct = TestContext.Current.CancellationToken;
        static JsonObject Ask(string url) => new() { ["on_data_of"] = "self", ["question"] = "What changed?", ["fileUrls"] = new JsonArray(Link(url)) };
        var pinned = new WorkIQGuard(Binding, _tokens, _transport)
        {
            FileHosts = new HashSet<string> { "Contoso.sharepoint.com", "files.contoso.example.com" },
        };
        Assert.Equal(GuardStatuses.Ok, (await pinned.CallAsync("ask", Ask("https://{tenant}.sharepoint.com/x.docx"), RunModes.Attended, ct)).Status);
        Assert.Equal(GuardStatuses.Ok, (await pinned.CallAsync("ask", Ask("https://files.contoso.example.com/x.docx"), RunModes.Attended, ct)).Status);
        Assert.Equal(GuardPolicy.ForeignFileUrl, (await pinned.CallAsync("ask", Ask("https://{other}.sharepoint.com/x.docx"), RunModes.Attended, ct)).Reason);
        Assert.Equal(GuardPolicy.ForeignFileUrl, (await pinned.CallAsync("ask", Ask("https://{tenant}-my.sharepoint.com/x.docx"), RunModes.Attended, ct)).Reason);

        var none = new WorkIQGuard(Binding, _tokens, _transport) { FileHosts = new HashSet<string>() };
        Assert.Equal(GuardPolicy.ForeignFileUrl, (await none.CallAsync("ask", Ask("https://{tenant}.sharepoint.com/x.docx"), RunModes.Attended, ct)).Reason);
        Assert.Equal(GuardStatuses.Ok, (await none.CallAsync("ask", Fixtures.Args("""{"on_data_of": "self", "question": "What changed?"}"""), RunModes.Attended, ct)).Status);

        foreach (var bad in new[] { "https://contoso.example.com", "contoso.sharepoint.com/sites", "*.sharepoint.com", "contoso.sharepoint.com:443", "" })
        {
            Assert.Throws<GuardConfigException>(() => new WorkIQGuard(Binding, _tokens, _transport) { FileHosts = new HashSet<string> { bad } });
        }
    }

    // -- 3. receipts -----------------------------------------------------------------------------

    [Theory]
    [InlineData("/me/messages", "/me/messages")]
    [InlineData("/ME/Messages/", "/ME/Messages/")]
    [InlineData("/me/messages/AAMkAGI2fictional=", "/me/messages/{id}")]
    [InlineData("/me/messages/AAMk/attachments/AAAB/$value", "/me/messages/{id}/attachments/{id}/$value")]
    [InlineData("/me/mailFolders/inbox/messages?$search=\"fictional\"", "/me/mailFolders/inbox/messages")]
    [InlineData("/me/mailFolders/Fictional-Project/messages", "/me/mailFolders/{id}/messages")]
    [InlineData("/users/dana@example.com/manager", "/users/{user}/manager")]
    [InlineData("/users/manager", "/users/manager")]
    [InlineData("/users('dana@example.com')/directReports", "/users(...)/directReports")]
    [InlineData("/chats/19:abc@thread.v2/messages/1700000000000", "/chats/{id}/messages/{id}")]
    [InlineData("/teams/t-1/channels/19:abc@thread.tacv2/messages/1/replies", "/teams/{id}/channels/{id}/messages/{id}/replies")]
    [InlineData("/me/drive/root:/Plans/Budget 2027.xlsx:/content", "/me/drive/root:/{path}:/content")]
    [InlineData("/me/drive/root:/Budget.xlsx", "/me/drive/root:/{path}")]
    [InlineData("/me/drive/root:/Plans/Budget.xlsx/", "/me/drive/root:/{path}/")]
    [InlineData("/me/drive/root:/Plans:", "/me/drive/root:/{path}:")]
    [InlineData("/me/drive/root:", "/me/drive/root:")]
    [InlineData("/drives/b!fictional/items/01ABC:/Sub/File.docx:/children", "/drives/{id}/items/{id}:/{path}:/children")]
    [InlineData("/sites/contoso.sharepoint.com:/sites/Fictional-Team:/lists", "/sites/{id}:/{path}:/lists")]
    [InlineData("/sites/contoso.sharepoint.com,1,2/drive/root/children", "/sites/{id}/drive/root/children")]
    [InlineData("/me/drive/root/search(q='fictional term')", "/me/drive/root/search(...)")]
    [InlineData("/me/messages/delta()", "/me/messages/delta()")]
    [InlineData("/me/calendarView/microsoft.graph.delta(startDateTime='2026')", "/me/calendarView/microsoft.graph.delta(...)")]
    [InlineData("/me/drive/items/x/FictionalSecret(q='a')", "/me/drive/items/{id}/{function}(...)")]
    [InlineData("/me/FictionalSecret", "/me/{id}")]
    [InlineData("/me/mess\u212Aages", "/me/{id}")]
    public void Receipt_paths_are_templates_of_resource_types(string path, string template)
    {
        Assert.Equal(template, ReceiptText.PathTemplate(path));
    }

    [Fact]
    public void Object_ids_are_placeholders_too()
    {
        Assert.Equal("/users/{user}/manager", ReceiptText.PathTemplate("/users/" + Guid.Empty + "/manager"));
        Assert.Equal("/groups/{id}/members", ReceiptText.PathTemplate("/groups/" + Guid.Empty + "/members"));
    }

    [Fact]
    public async Task Receipts_hold_no_identifiers_names_or_model_chosen_text()
    {
        var ct = TestContext.Current.CancellationToken;
        var received = new List<GuardReceipt>();
        var guard = new WorkIQGuard(Binding, new FakeTokens(["principal"], reason: "fictional\u00E9 " + new string('x', 400)), _transport,
                                    recorder: received.Add);

        await guard.CallAsync("fetch", Fetch("self", "/users/dana@example.com/manager", "/me/messages/AAMkFictionalId=",
                                             "/me/drive/root:/Fictional%20Plans/Budget.xlsx:/content"), RunModes.Attended, ct);
        await guard.CallAsync("call_function", Fixtures.Args("""{"on_data_of": "self", "functionUrl": "/me/drive/root/search(q='fictional-term')"}"""),
                              RunModes.Attended, ct);
        await guard.CallAsync("fetch", Fetch("self", "/dana@example.com/messages"), RunModes.Attended, ct);
        await guard.CallAsync("fetch", Fixtures.Args("""{"on_data_of": "self", "entityUrls": ["/me"], "dana@example.com": 1, "fictional note": 2, "format": 3}"""),
                              RunModes.Attended, ct);
        await guard.CallAsync("fetch", Fetch("tell dana@example.com", "/me"), RunModes.Attended, ct);
        await guard.CallAsync("fictional tool for dana@example.com", Fetch("self", "/me"), "fictional mode\n", ct);
        await guard.CallAsync("fetch", Fetch("principal", "/me/messages"), RunModes.Attended, ct);

        var receipts = guard.Receipts;
        Assert.Equal(received, receipts);
        Assert.Equal(["/users/{user}/manager", "/me/messages/{id}", "/me/drive/root:/{path}:/content"], receipts[0].Paths);
        Assert.Equal(["/me/drive/root/search(...)"], receipts[1].Paths);
        Assert.Equal("'/{id}' is not an allowed root for on_data_of=self", receipts[2].Reason);
        Assert.Equal("unexpected arguments: format, 2 other names", receipts[3].Reason);
        Assert.Equal((ReceiptText.Other, "on_data_of must be 'principal' or 'self'"), (receipts[4].OnDataOf, receipts[4].Reason));
        Assert.Null(receipts[4].ReadAs);
        Assert.Equal((ReceiptText.Other, ReceiptText.Other), (receipts[5].Tool, receipts[5].Mode));
        Assert.Equal("token unavailable for principal: fictional? " + new string('x', 149), receipts[6].Reason);

        var recorded = Dumps(receipts);
        foreach (var leaked in new[] { "dana", "example.com", "AAMk", "Budget", "Plans", "fictional-term", "fictional note", "fictional tool", "fictional mode" })
        {
            Assert.DoesNotContain(leaked, recorded, StringComparison.Ordinal);
        }

        // What was sent is untouched: only the record is a template.
        Assert.Equal("/users/dana@example.com/manager", (string)_transport.Calls[0].Arguments["entityUrls"]![0]!);
        Assert.Equal("/me/drive/root/search(q='fictional-term')", (string)_transport.Calls[1].Arguments["functionUrl"]!);
    }

    [Fact]
    public async Task A_journal_failure_is_reported_without_its_message()
    {
        var guard = new WorkIQGuard(Binding, _tokens, _transport, new ThrowingJournal());
        var result = await guard.CallAsync("do_action", Fixtures.Args("""
            {"on_data_of": "principal", "actionUrl": "/me/messages/draft-1/send", "action_id": "a-1", "attempt_id": "t-1"}
            """), RunModes.Attended, TestContext.Current.CancellationToken);
        Assert.Equal((GuardStatuses.Refused, "no live execution claim: the approval journal could not be read"), (result.Status, result.Reason));
        Assert.Empty(_transport.Calls);
    }

    // -- 4. oversized answers -------------------------------------------------------------------

    [Fact]
    public async Task An_oversized_answer_is_an_error_for_a_read_and_unknown_for_a_write()
    {
        var ct = TestContext.Current.CancellationToken;
        var transport = new FakeTransport(error: () => new TransportResponseTooLargeException("Work IQ response too large"));
        var guard = new WorkIQGuard(Binding, _tokens, transport);
        var read = await guard.CallAsync("fetch", Fetch("principal", "/me/messages"), RunModes.Attended, ct);
        Assert.Equal((GuardStatuses.Error, WorkIQGuard.ResponseTooLarge), (read.Status, read.Reason));

        var post = new JsonObject { ["on_data_of"] = "self", ["parentUrl"] = "/chats/chat-principal-1/messages", ["jsonBody"] = "{}" };
        var write = await guard.CallAsync("create_entity", post, RunModes.Unattended, ct);
        Assert.Equal((GuardStatuses.Unknown, GuardPolicy.UnknownWrite), (write.Status, write.Reason)); // it was sent: never "error"
        Assert.Equal(2, transport.Calls.Count);
    }

    [Fact]
    public void The_transport_pools_exactly_the_read_tools()
    {
        // Margo.Cloud.WorkIQ cannot reference the guard, so it carries its own list of the tools that may share a
        // pooled connection. A write tool on that list could be reported unknown because of a stale connection.
        Assert.Equal(GuardPolicy.ToolArgs.Keys.Except(GuardPolicy.WriteTools).Order(StringComparer.Ordinal),
                     McpTransportOptions.ReadTools.Order(StringComparer.Ordinal));
        Assert.Same(McpTransportOptions.ReadTools, McpTransportOptions.Default.PooledTools);
    }

    // -- 6. concurrency --------------------------------------------------------------------------

    [Fact]
    public async Task Concurrent_reads_leave_one_intact_receipt_each_and_never_exceed_the_budget()
    {
        const int calls = 400;
        const int limit = 250;
        var budget = new CallBudget(limit);
        var recorded = new ConcurrentQueue<GuardReceipt>();
        var transport = new YieldingTransport();
        var guard = new WorkIQGuard(Binding, new ConstantTokens(), transport, recorder: recorded.Enqueue, budget: budget);
        using var start = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, calls).Select(i => Task.Run(async () =>
        {
            start.Wait(TestContext.Current.CancellationToken);
            var subject = i % 2 == 0 ? "self" : "principal";
            // One in eight is refused before the budget; the rest reach it.
            var path = i % 8 == 7 ? "/groups/g-" + i : "/me/messages/m-" + i;
            return await guard.CallAsync("fetch", Fetch(subject, path), RunModes.Attended, TestContext.Current.CancellationToken);
        })).ToArray();
        start.Set();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(calls / 8, results.Count(result => result.Status == GuardStatuses.Refused));
        Assert.Equal(limit, results.Count(result => result.Status == GuardStatuses.Ok));
        Assert.Equal(calls - (calls / 8) - limit, results.Count(result => result.Status == GuardStatuses.BudgetExhausted));
        Assert.Equal(limit, budget.Used);
        Assert.Equal(limit, transport.Count);

        var receipts = guard.Receipts;
        Assert.Equal(calls, receipts.Count);
        Assert.Equal(calls, recorded.Count);
        var stored = new HashSet<object>(receipts, ReferenceEqualityComparer.Instance);
        Assert.Equal(calls, stored.Count);
        Assert.All(recorded, receipt => Assert.Contains(receipt, stored));
        foreach (var receipt in receipts)
        {
            // Each receipt is one call's own: its subject, token label, status, paths and reason belong together.
            Assert.Equal(receipt.OnDataOf == "self" ? "margo" : "principal", receipt.ReadAs);
            if (receipt.Status == GuardStatuses.Refused)
            {
                Assert.Empty(receipt.Paths);
                Assert.Equal("'/groups' is not an allowed root for on_data_of=" + receipt.OnDataOf, receipt.Reason);
            }
            else
            {
                Assert.Equal(["/me/messages/{id}"], receipt.Paths);
                Assert.Equal(receipt.Status == GuardStatuses.Ok ? null : "call budget exhausted before the call", receipt.Reason);
            }
        }

        Assert.Equal(results.GroupBy(result => result.Status).ToDictionary(group => group.Key, group => group.Count()),
                     receipts.GroupBy(receipt => receipt.Status).ToDictionary(group => group.Key, group => group.Count()));
    }

    // -- 7. another mailbox, or a write ----------------------------------------------------------

    [Fact]
    public async Task Delegation_rewrites_only_the_bound_principals_mail_and_calendar()
    {
        var ct = TestContext.Current.CancellationToken;
        (string Given, string Sent)[] rewritten =
        [
            ("/Me/Messages", "/users/principal-1/Messages"),
            ("/ME/mailFolders/inbox/messages/m-1/$value", "/users/principal-1/mailFolders/inbox/messages/m-1/$value"),
            ("/me/messages/m-1/$value", "/users/principal-1/messages/m-1/$value"),
            ("/me/messages?$filter=from/emailAddress/address eq 'x'&x=/../../users/colleague-1/messages",
             "/users/principal-1/messages?$filter=from/emailAddress/address eq 'x'&x=/../../users/colleague-1/messages"),
            ("/me/events/e-1/instances?startDateTime=2026-11-23", "/users/principal-1/events/e-1/instances?startDateTime=2026-11-23"),
        ];
        foreach (var (given, sent) in rewritten)
        {
            var guard = Guard(PrincipalAccessModes.Delegation);
            var result = await guard.CallAsync("fetch", Fetch("principal", given), RunModes.Unattended, ct);
            Assert.True(result.Status == GuardStatuses.Ok, given);
            Assert.Equal(sent, (string)_transport.Calls[^1].Arguments["entityUrls"]![0]!);
            Assert.Equal("margo-token", _transport.Calls[^1].Bearer);
            Assert.StartsWith("/users/{user}/", guard.Receipts[0].Paths[0], StringComparison.Ordinal);
        }
    }

    [Theory]
    // another mailbox, however it is spelled
    [InlineData("principal", "fetch", "/users/colleague-1/messages")]
    [InlineData("principal", "fetch", "/USERS/colleague-1/Messages")]
    [InlineData("principal", "fetch", "/users/colleague-1/mailFolders/inbox/messages")]
    [InlineData("principal", "fetch", "/users/colleague-1/calendarView")]
    [InlineData("principal", "fetch", "/users/PRINCIPAL-1/messages")]
    [InlineData("principal", "fetch", "/users/principal-1/messages/../../colleague-1/messages")]
    [InlineData("principal", "fetch", "/me/messages/../../users/colleague-1/messages")]
    [InlineData("principal", "fetch", "/me/messages%2F..%2F..%2Fusers%2Fcolleague-1%2Fmessages")]
    [InlineData("principal", "fetch", "/me/messages/..%2f..%2fusers/colleague-1/messages")]
    [InlineData("principal", "fetch", "/me/messages/%2e%2e/%2e%2e/users/colleague-1/messages")]
    [InlineData("principal", "fetch", "/me%2Fmessages")]
    [InlineData("principal", "fetch", "/me//messages")]
    [InlineData("principal", "fetch", "//me/messages")]
    [InlineData("principal", "fetch", "/me/messages\\..\\..\\users\\colleague-1\\messages")]
    [InlineData("principal", "fetch", "/users/colleague-1/manager")] // not mail or calendar: unavailable under delegation
    [InlineData("principal", "fetch", "/me")]
    [InlineData("principal", "fetch", "/me/$batch")]
    [InlineData("principal", "fetch", "/$batch")]
    [InlineData("principal", "fetch", "/me/sendMail")]
    [InlineData("principal", "fetch", "/me/chats")]
    [InlineData("principal", "fetch", "/me/drive/root/children")]
    [InlineData("principal", "fetch", "/me/authentication/methods")]
    [InlineData("principal", "fetch", "/me/%6dessages")]
    [InlineData("principal", "call_function", "/users/colleague-1/calendar/getSchedule")]
    // Margo's own token holds the delegation, so "self" must never name a mailbox, the principal's included
    [InlineData("self", "fetch", "/users/principal-1/messages")]
    [InlineData("self", "fetch", "/users/principal-1/calendarView")]
    [InlineData("self", "fetch", "/users/principal-1/mailFolders/inbox/messages")]
    [InlineData("self", "fetch", "/users/colleague-1/messages")]
    [InlineData("self", "fetch", "/users/colleague-1/events/e-1")]
    [InlineData("self", "fetch", "/users/principal-1/messages/m-1/$value")]
    [InlineData("self", "fetch", "/users/principal-1/manager/messages")]
    [InlineData("self", "fetch", "/$batch")]
    [InlineData("self", "fetch", "/me/../users/principal-1/messages")]
    [InlineData("self", "fetch_blob", "/users/principal-1/messages/m-1/$value")]
    [InlineData("self", "call_function", "/users/principal-1/messages/delta()")]
    public async Task No_argument_reaches_another_mailbox_under_delegation(string subject, string tool, string path)
    {
        var guard = Guard(PrincipalAccessModes.Delegation);
        JsonObject arguments = tool switch
        {
            "fetch" => Fetch(subject, path),
            "fetch_blob" => new JsonObject { ["on_data_of"] = subject, ["path"] = path },
            _ => new JsonObject { ["on_data_of"] = subject, ["functionUrl"] = path },
        };
        foreach (var mode in RunModes.All)
        {
            var result = await guard.CallAsync(tool, arguments, mode, TestContext.Current.CancellationToken);
            Assert.True(result.Status is GuardStatuses.Refused or GuardStatuses.Unavailable, mode + " " + path + ": " + result.Status);
        }

        Assert.Empty(_transport.Calls);
        Assert.Empty(_tokens.Requested);
    }

    [Theory]
    [InlineData("do_action", "actionUrl", "/me/sendMail")]
    [InlineData("do_action", "actionUrl", "/Me/SendMail")]
    [InlineData("do_action", "actionUrl", "/me/messages/m-1/reply")]
    [InlineData("do_action", "actionUrl", "/me/messages/m-1/send")]
    [InlineData("do_action", "actionUrl", "/me/events/e-1/accept")]
    [InlineData("do_action", "actionUrl", "/$batch")]
    [InlineData("do_action", "actionUrl", "/me/$batch")]
    [InlineData("do_action", "actionUrl", "/users/principal-1/sendMail")]
    [InlineData("do_action", "actionUrl", "/users/principal-1/messages/m-1/reply")]
    [InlineData("create_entity", "parentUrl", "/me/messages")]
    [InlineData("create_entity", "parentUrl", "/me/chats/chat-principal-1%2f..%2fchat-other/messages")]
    [InlineData("create_entity", "parentUrl", "/chats/chat-principal-1/messages/1/replies")]
    [InlineData("create_entity", "parentUrl", "/chats/Chat-Principal-1/messages")]
    [InlineData("update_entity", "entityUrl", "/me/messages/m-1")]
    [InlineData("delete_entity", "entityUrl", "/me/messages/m-1")]
    [InlineData("delete_entity", "entityUrl", "/chats/chat-principal-1/messages")]
    public async Task No_write_is_sent_without_a_claim(string tool, string key, string path)
    {
        foreach (var access in PrincipalAccessModes.All)
        {
            foreach (var subject in Subjects.Intents)
            {
                foreach (var mode in RunModes.All)
                {
                    var arguments = new JsonObject { ["on_data_of"] = subject, [key] = path, ["jsonBody"] = "{}" };
                    var result = await Guard(access, trigger: "m-1").CallAsync(tool, arguments, mode, TestContext.Current.CancellationToken);
                    Assert.True(result.Status is GuardStatuses.Refused or GuardStatuses.Unavailable,
                                string.Join(' ', access, subject, mode, path, result.Status));
                }
            }
        }

        Assert.Empty(_transport.Calls);
    }

    [Fact]
    public async Task Read_tools_cannot_carry_a_claim_or_a_body()
    {
        var ct = TestContext.Current.CancellationToken;
        var guard = Guard();
        var claimed = await guard.CallAsync("fetch", Fixtures.Args("""{"on_data_of": "principal", "entityUrls": ["/me/sendMail"], "action_id": "a-1"}"""), RunModes.Attended, ct);
        var body = await guard.CallAsync("call_function", Fixtures.Args("""{"on_data_of": "principal", "functionUrl": "/me/sendMail", "jsonBody": "{}"}"""), RunModes.Attended, ct);
        var both = await guard.CallAsync("call_function", Fixtures.Args("""{"on_data_of": "principal", "functionUrl": "/me/messages", "parentUrl": "/users/colleague-1/messages"}"""), RunModes.Attended, ct);
        Assert.Equal((GuardStatuses.Refused, GuardStatuses.Refused, GuardStatuses.Refused), (claimed.Status, body.Status, both.Status));
        Assert.Empty(_transport.Calls);
    }

    private sealed class ConstantTokens : ITokenProvider
    {
        public string TokenFor(string subject) => "opaque-" + subject;
    }

    private sealed class YieldingTransport : IWorkIQTransport
    {
        private int _count;

        public string Endpoint => WorkIQEndpoints.McpUrl;

        public int Count => Volatile.Read(ref _count);

        public async Task<JsonObject> CallAsync(string tool, JsonObject arguments, string bearer, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            await Task.Yield();
            return new JsonObject { ["statusCode"] = 200 };
        }
    }

    private sealed class ThrowingJournal : IClaimJournal
    {
        public ExecutionClaim Load(JsonNode? actionId, JsonNode? attemptId, string callKey = "workiq") =>
            throw new InvalidOperationException("no such row for fictional action a-1 of dana@example.com");

        public void MarkDispatched(ExecutionClaim claim) => throw new InvalidOperationException();

        public JsonObject Settle(ExecutionClaim claim, string state, int? statusCode = null) => throw new InvalidOperationException();
    }
}

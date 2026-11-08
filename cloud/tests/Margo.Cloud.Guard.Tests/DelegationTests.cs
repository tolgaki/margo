using Xunit;
using static Margo.Cloud.Guard.Tests.Fixtures;

namespace Margo.Cloud.Guard.Tests;

/// <summary>Port of the former Python runtime tests <c>DelegationTests</c>: the delegation fallback.</summary>
public sealed class DelegationTests
{
    private readonly FakeTokens _tokens = new(token: subject => subject + "-token");
    private readonly FakeTransport _transport = new("""{"statusCode": 200}""");

    private WorkIQGuard Guard() => new(Binding, _tokens, _transport, principalAccess: PrincipalAccessModes.Delegation);

    [Fact]
    public async Task Mail_and_calendar_are_rewritten_to_the_bound_principal()
    {
        var guard = Guard();
        var result = await guard.CallAsync("fetch", Json("""
            {"on_data_of": "principal", "entityUrls": ["/me/messages?$top=5", "/me/calendarView?startDateTime=x"]}
            """), RunModes.Unattended, TestContext.Current.CancellationToken);
        Assert.Equal(("ok", "principal"), (result.Status, result.ReadAs));
        var (_, arguments, bearer) = _transport.Calls[0];
        Assert.Equal("""["/users/principal-1/messages?$top=5","/users/principal-1/calendarView?startDateTime=x"]""",
                     arguments["entityUrls"]!.ToJsonString());
        Assert.Equal("margo-token", bearer);
        Assert.Equal(["margo"], _tokens.Requested);
        Assert.Equal("delegation", guard.Receipts[0].Via);
        Assert.Equal(["/users/principal-1/messages", "/users/principal-1/calendarView"], guard.Receipts[0].Paths);
    }

    [Fact]
    public async Task Other_principal_sources_are_unavailable_not_empty()
    {
        var guard = Guard();
        (string Tool, string Arguments)[] cases =
        [
            ("fetch", """{"on_data_of": "principal", "entityUrls": ["/me/chats"]}"""),
            ("fetch", """{"on_data_of": "principal", "entityUrls": ["/me/drive/root/children"]}"""),
            ("retrieve", """{"on_data_of": "principal", "query": "launch", "capabilities": [{"name": "TeamsMessages"}]}"""),
            ("ask", """{"on_data_of": "principal", "question": "Anything urgent?"}"""),
            ("fetch", """{"on_data_of": "principal", "entityUrls": ["/me/messages", "/me/chats"]}"""),
        ];
        foreach (var (tool, arguments) in cases)
        {
            var result = await guard.CallAsync(tool, Json(arguments), RunModes.Attended, TestContext.Current.CancellationToken);
            Assert.True(result.Status == "unavailable", tool + " " + arguments);
        }

        Assert.Empty(_transport.Calls);
    }

    [Fact]
    public async Task Model_still_cannot_name_another_mailbox()
    {
        var guard = Guard();
        var result = await guard.CallAsync("fetch", Json("""{"on_data_of": "principal", "entityUrls": ["/users/colleague-1/messages"]}"""),
                                           RunModes.Attended, TestContext.Current.CancellationToken);
        Assert.Equal("refused", result.Status);
        Assert.Empty(_transport.Calls);
    }

    [Fact]
    public async Task Self_reads_are_unchanged()
    {
        var guard = Guard();
        await guard.CallAsync("fetch", Json("""{"on_data_of": "self", "entityUrls": ["/me/manager"]}"""), RunModes.Attended,
                              TestContext.Current.CancellationToken);
        Assert.Equal("""["/me/manager"]""", _transport.Calls[0].Arguments["entityUrls"]!.ToJsonString());
        Assert.Null(guard.Receipts[0].Via);
    }

    [Fact]
    public void Unknown_access_mode_is_rejected()
    {
        Assert.Throws<GuardConfigException>(() => new WorkIQGuard(Binding, new FakeTokens(), new FakeTransport(), principalAccess: "app-only"));
    }
}

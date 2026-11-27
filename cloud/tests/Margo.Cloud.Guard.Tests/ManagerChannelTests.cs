using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using Xunit;

namespace Margo.Cloud.Guard.Tests;

/// <summary>
/// The manager channel: Margo may email the manager, and share a file with the manager, without a card, as long as
/// the manager is the only person who receives it. Anyone else, or any other write, still needs an approval.
/// </summary>
public sealed class ManagerChannelTests
{
    private const string ManagerMail = "dana@example.com";

    private readonly FakeTransport _transport = new("""{"statusCode": 202}""");

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private WorkIQGuard Guard(bool channel = true) => new(Fixtures.Binding, new FakeTokens(), _transport)
    {
        AllowsManagerChannel = channel,
        ManagerMail = ManagerMail,
    };

    private static JsonObject Mail(JsonObject message, bool? save = true) => new()
    {
        ["on_data_of"] = "self",
        ["actionUrl"] = "/me/sendMail",
        ["jsonBody"] = new JsonObject { ["message"] = message, ["saveToSentItems"] = save }.ToJsonString(),
    };

    private static JsonObject Message(string to = ManagerMail, string text = "Rafa asked in the launch chat about the Q3 date.") => new()
    {
        ["subject"] = "From the launch chat",
        ["body"] = new JsonObject { ["contentType"] = "Text", ["content"] = text },
        ["toRecipients"] = new JsonArray(new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = to } }),
    };

    private static JsonObject Share(string objectId = "principal-1", string role = "read") => new()
    {
        ["on_data_of"] = "self",
        ["actionUrl"] = "/drives/drive-1/items/item-1/invite",
        ["jsonBody"] = new JsonObject
        {
            ["recipients"] = new JsonArray(new JsonObject { ["objectId"] = objectId }),
            ["roles"] = new JsonArray(role),
            ["requireSignIn"] = true,
            ["sendInvitation"] = true,
        }.ToJsonString(),
    };

    [Theory]
    [InlineData("attended")]
    [InlineData("unattended")]
    public async Task An_email_to_the_manager_alone_needs_no_card(string mode)
    {
        var result = await Guard().CallAsync("do_action", Mail(Message()), mode, Cancel);
        Assert.Equal(GuardStatuses.Ok, result.Status);
        Assert.Equal("/me/sendMail", PyNode.AsString(Assert.Single(_transport.Calls).Arguments["actionUrl"]));
    }

    [Fact]
    public async Task The_address_matches_regardless_of_case()
    {
        Assert.Equal(GuardStatuses.Ok, (await Guard().CallAsync("do_action", Mail(Message(to: "Dana@Example.com")), "attended", Cancel)).Status);
    }

    [Fact]
    public async Task Anyone_else_on_an_email_needs_a_card()
    {
        var copied = Message();
        copied["ccRecipients"] = new JsonArray(new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = "rafa@example.com" } });
        var two = Message();
        ((JsonArray)two["toRecipients"]!).Add(new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = "rafa@example.com" } });
        var attachment = Message();
        attachment["attachments"] = new JsonArray(new JsonObject { ["name"] = "a.txt" });
        JsonObject[] refused = [Mail(Message(to: "rafa@example.com")), Mail(copied), Mail(two), Mail(attachment)];
        foreach (var call in refused)
        {
            Assert.Equal(GuardStatuses.Refused, (await Guard().CallAsync("do_action", call, "attended", Cancel)).Status);
        }

        Assert.Empty(_transport.Calls);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    public async Task A_file_shared_with_the_manager_alone_needs_no_card(string role)
    {
        Assert.Equal(GuardStatuses.Ok, (await Guard().CallAsync("do_action", Share(role: role), "unattended", Cancel)).Status);
    }

    [Fact]
    public async Task Sharing_with_anyone_else_needs_a_card()
    {
        Assert.Equal(GuardStatuses.Refused, (await Guard().CallAsync("do_action", Share(objectId: "colleague-1"), "attended", Cancel)).Status);
        Assert.Equal(GuardStatuses.Refused, (await Guard().CallAsync("do_action", Share(role: "owner"), "attended", Cancel)).Status);
        Assert.Empty(_transport.Calls);
    }

    [Fact]
    public async Task Without_the_channel_or_in_a_third_party_run_nothing_goes_out()
    {
        Assert.Equal(GuardStatuses.Refused, (await Guard(channel: false).CallAsync("do_action", Mail(Message()), "attended", Cancel)).Status);
        Assert.Equal(GuardStatuses.Refused, (await Guard().CallAsync("do_action", Mail(Message()), "third_party", Cancel)).Status);
        Assert.Empty(_transport.Calls);
    }

    [Fact]
    public async Task An_email_from_the_managers_own_mailbox_is_not_the_channel()
    {
        var call = Mail(Message());
        call["on_data_of"] = "principal";
        Assert.Equal(GuardStatuses.Refused, (await Guard().CallAsync("do_action", call, "attended", Cancel)).Status);
    }
}

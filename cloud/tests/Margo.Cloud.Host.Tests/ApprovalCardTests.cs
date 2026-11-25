using System.Text.Json.Nodes;
using Margo.Cloud.Orchestrator;
using Xunit;

namespace Margo.Cloud.Host.Tests;

/// <summary>
/// The approval card is for the person deciding, who may not be technical: plain words about what happens, to whom and
/// when, and never tool names, paths, identifiers or JSON.
/// </summary>
public sealed class ApprovalCardTests
{
    private static readonly string[] Technical =
        ["do_action", "create_entity", "update_entity", "delete_entity", "jsonBody", "actionUrl", "/me/", "/users/", "act_1", "hash-1", "Exact request", "{\""];

    private static ApprovalCard Card(string tool, string url, JsonObject body, string onDataOf = "principal", string? why = "You asked for it.") =>
        new("act_1", 2, "hash-1", "kind", onDataOf, tool,
            new JsonObject { [tool == "create_entity" ? "parentUrl" : tool == "do_action" ? "actionUrl" : "entityUrl"] = url, ["jsonBody"] = body.ToJsonString() }, why);

    private static string Text(JsonArray body) => string.Join("\n", body.Select(item => item!["text"] is { } text
        ? (string?)text
        : string.Concat(item["inlines"]!.AsArray().Select(run => (string?)run!["text"]))));

    private static void Plain(string text)
    {
        foreach (var word in Technical)
        {
            Assert.DoesNotContain(word, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void An_email_card_shows_who_what_and_why_in_plain_words()
    {
        var card = Card("do_action", "/me/sendMail", JsonNode.Parse("""
            {"message": {"subject": "Review moved", "body": {"contentType": "HTML", "content": "<p>Hi Rafa,</p><p>The review moved to <b>Thursday</b> &amp; is online.</p>"},
             "toRecipients": [{"emailAddress": {"address": "rafa@example.com", "name": "Rafa"}}],
             "attachments": [{"name": "agenda.docx"}]}, "saveToSentItems": true}
            """)!.AsObject());
        var text = Text(ApprovalCards.Card(card, null));
        Assert.StartsWith("Send this email?\nFrom your mailbox. It will show as sent by Margo on your behalf.", text, StringComparison.Ordinal);
        Assert.Contains("To: Rafa (rafa@example.com)", text, StringComparison.Ordinal);
        Assert.Contains("Subject: Review moved", text, StringComparison.Ordinal);
        Assert.Contains("Message: Hi Rafa,\nThe review moved to Thursday & is online.", text, StringComparison.Ordinal);
        Assert.Contains("Attachments: agenda.docx", text, StringComparison.Ordinal);
        Assert.Contains("Why: You asked for it.", text, StringComparison.Ordinal);
        Assert.EndsWith(ApprovalCards.Footer, text, StringComparison.Ordinal);
        Plain(text);
    }

    [Fact]
    public void A_meeting_card_shows_the_time_as_people_read_it()
    {
        var card = Card("create_entity", "/me/events", JsonNode.Parse("""
            {"subject": "Design review", "start": {"dateTime": "2026-11-26T14:00:00", "timeZone": "Pacific Standard Time"},
             "end": {"dateTime": "2026-11-26T14:30:00", "timeZone": "Pacific Standard Time"},
             "attendees": [{"emailAddress": {"address": "rafa@example.com"}, "type": "required"}], "isOnlineMeeting": true}
            """)!.AsObject());
        var text = Text(ApprovalCards.Card(card, null));
        Assert.Contains("Set up this meeting?", text, StringComparison.Ordinal);
        Assert.Contains("Invited: rafa@example.com", text, StringComparison.Ordinal);
        Assert.Contains("When: Thursday, November 26, 2026, 2:00 PM to 2:30 PM (Pacific Standard Time)", text, StringComparison.Ordinal);
        Assert.Contains("Online: Teams meeting", text, StringComparison.Ordinal);
        Plain(text);
    }

    [Fact]
    public void A_card_for_an_existing_item_shows_which_one_and_what_changes()
    {
        var card = Card("update_entity", "/me/events/evt-1", JsonNode.Parse("""
            {"start": {"dateTime": "2026-11-27T10:00:00", "timeZone": "UTC"}, "end": {"dateTime": "2026-11-27T10:30:00", "timeZone": "UTC"}}
            """)!.AsObject()) with
        {
            Context = [new CardFact("Meeting", "Design review"), new CardFact("Currently", "Thursday, November 26, 2026, 2:00 PM to 2:30 PM (UTC)")],
        };
        var text = Text(ApprovalCards.Card(card, null));
        Assert.Contains("Change this meeting?\nOn your calendar.", text, StringComparison.Ordinal);
        Assert.Contains("Meeting: Design review\nCurrently: Thursday", text, StringComparison.Ordinal);
        Assert.Contains("New time: Friday, November 27, 2026, 10:00 AM to 10:30 AM (UTC)", text, StringComparison.Ordinal);
        Assert.Contains("What changes: time", text, StringComparison.Ordinal);
        Plain(text);
    }

    [Fact]
    public void A_chat_post_from_margo_says_where_it_goes()
    {
        var card = Card("create_entity", "/chats/19:abc/messages", JsonNode.Parse("""{"body": {"content": "Notes are in the doc."}}""")!.AsObject(), onDataOf: "self")
            with { Context = [new CardFact("Chat", "Launch team")] };
        var text = Text(ApprovalCards.Card(card, null));
        Assert.Contains("Post this Teams message?\nFrom Margo, in a conversation she's part of.\nChat: Launch team", text, StringComparison.Ordinal);
        Assert.Contains("Message: Notes are in the doc.", text, StringComparison.Ordinal);
        Plain(text);
    }

    [Fact]
    public void An_action_it_cannot_describe_says_so()
    {
        var card = Card("do_action", "/me/something/else", JsonNode.Parse("""{"x": 1}""")!.AsObject(), why: null);
        var text = Text(ApprovalCards.Card(card, null));
        Assert.Contains("Let Margo do this?", text, StringComparison.Ordinal);
        Assert.Contains(ApprovalCards.Undescribed, text, StringComparison.Ordinal);
        Plain(text);
    }

    [Fact]
    public void The_decided_card_keeps_the_details_and_says_what_happened()
    {
        var card = Card("do_action", "/me/messages/m1/reply", JsonNode.Parse("""{"comment": "Thursday works."}""")!.AsObject()) with
        {
            Context = [new CardFact("Replying to", "“Q3” from Rafa")],
            Outcome = "Done: the email has gone out.",
            OutcomeStatus = "sent",
        };
        var text = Text(ApprovalCards.Card(card, card.Outcome));
        Assert.Contains("Replying to: “Q3” from Rafa", text, StringComparison.Ordinal);
        Assert.Contains("Note: Thursday works.", text, StringComparison.Ordinal);
        Assert.EndsWith("Done: the email has gone out.", text, StringComparison.Ordinal);
        Assert.DoesNotContain(ApprovalCards.Footer, text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_buttons_carry_the_context_back_for_the_last_version()
    {
        var card = Card("do_action", "/me/messages/m1/reply", JsonNode.Parse("""{"comment": "Yes."}""")!.AsObject()) with
        {
            Context = [new CardFact("Replying to", "“Q3” from Rafa")],
        };
        var content = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(ApprovalCards.Ask(card).Content))!;
        var decision = ActivityEvents.Decision(content["actions"]![0]!["data"]!.DeepClone())!;
        Assert.Equal((CardDecision.Approve, "act_1", 2L, "hash-1"), (decision.Verb, decision.ActionId, decision.Revision, decision.ActionHash));
        Assert.Equal(card.Context, decision.Context);
    }

    [Theory]
    [InlineData("do_action", "/pages/create_page", "Publish this Page?")]
    [InlineData("create_entity", "/engage/communities/c1/threads", "Post this on Viva Engage?")]
    [InlineData("do_action", "/sharepoint/sites/s1/lists/l1/items/7/approve", "Make this SharePoint change?")]
    [InlineData("create_entity", "/copilot/communications/realtimeActivityFeed/multiActivitySubscriptions", "Let Margo follow this meeting's live transcript?")]
    public void Newer_work_iq_actions_get_plain_titles(string tool, string path, string title)
    {
        var card = Card(tool, path, JsonNode.Parse("""{"title": "Q3 launch notes", "content": "Highlights from the review."}""")!.AsObject(), onDataOf: "self");
        var text = Text(ApprovalCards.Card(card, null));
        Assert.StartsWith(title, text, StringComparison.Ordinal);
        Plain(text);
    }
}

using System.Text.Json.Nodes;
using Margo.Cloud.Core;
using Margo.Cloud.Guard;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Work;

namespace Margo.Cloud.Orchestrator.Tests;

/// <summary>
/// <c>ReplyPolicyTests</c> and the reply-gate tests of <c>GuardedWriteTests</c> in the former Python guard tests
/// (<c>replies.py</c>), the gate's approve mode against the real approval journal. Expected policy results were checked
/// against <c>load_reply_policy</c> on Python 3.13.
/// </summary>
public sealed class ReplyTests
{
    private static string Setting(string value) => "- **Replies to other people:** " + value + "\n";

    [Fact]
    public void Setting_is_parsed_strictly_and_defaults_to_none()
    {
        Assert.Equal("acknowledge", ReplyPolicy.Load(Setting("acknowledge")).Mode);
        Assert.Equal("approve", ReplyPolicy.Load(Setting("`approve`")).Mode);
        foreach (var text in new[] { "", Setting("autonomous"), Setting("none") + Setting("approve"), null,
                                     "Replies to other people: approve", "- **Replies to other people:** approve please" })
        {
            var policy = ReplyPolicy.Load(text);
            Assert.Equal("none", policy.Mode);
            Assert.NotEmpty(policy.Issues);
        }
    }

    [Theory]
    [InlineData("- **Replies to other people:** acknowledge\n", "acknowledge", null)]
    [InlineData("* **Replies to other people:**\t`approve`  \r\n", "none", "setting not present; defaulting to none")]
    [InlineData("- **Replies to other people:** NONE\n- **Replies to other people:** none\n", "none", null)]
    [InlineData("- **Replies to other people:** none\n- **Replies to other people:** approve\n", "none", "conflicting settings; defaulting to none")]
    [InlineData("  -  **Replies to other people:**acknowledge", "acknowledge", null)]
    [InlineData("- **Replies to other people:** acknowledge`", "acknowledge", null)]
    [InlineData("- **Replies to other people:** ack nowledge", "none", "setting not present; defaulting to none")]
    [InlineData("- **Replies to other people:** auto_mode", "none", "unknown value 'auto_mode'; defaulting to none")]
    [InlineData("-**Replies to other people:** none", "none", "setting not present; defaulting to none")]
    public void Policy_matches_python(string text, string mode, string? issue)
    {
        Assert.Equal(new ReplyPolicy(mode, issue is null ? [] : [issue]), ReplyPolicy.Load(text));
    }

    [Fact]
    public void Missing_preferences_are_reported()
    {
        Assert.Equal(new ReplyPolicy("none", ["preferences unavailable"]), ReplyPolicy.Load(null));
    }

    [Fact]
    public void Template_ships_with_replies_off()
    {
        var template = File.ReadAllText(Path.Combine(Fixtures.Root, "skills", "chief-of-staff", "preferences.md"));
        Assert.Equal(new ReplyPolicy("none", []), ReplyPolicy.Load(template));
    }

    [Fact]
    public void None_and_acknowledge_never_send_model_text()
    {
        var off = new ReplyGate(Fixtures.Binding, ReplyModes.None);
        Assert.False(off.Decide("colleague-1", "conv-1", "Dana's salary is ...", "Dana").Send);
        var ack = new ReplyGate(Fixtures.Binding, ReplyModes.Acknowledge);
        var first = ack.Decide("colleague-1", "conv-1", "Dana's private calendar says ...", "Dana");
        Assert.Equal(new ReplyDecision(true, ReplyGate.Acknowledgement("Dana")), first);
        Assert.Equal("Thanks. I've passed this on to Dana, who will reply if needed.", first.Text);
        Assert.False(ack.Decide("colleague-1", "conv-1", "again", "Dana").Send);
        Assert.True(ack.Decide("colleague-1", "conv-2", "new thread", "Dana").Send);
        Assert.False(ack.Decide("margo-user-1", "conv-3", "loop", "Dana").Send);
    }

    [Fact]
    public void Manager_conversation_is_not_gated()
    {
        var gate = new ReplyGate(Fixtures.Binding, ReplyModes.None);
        Assert.Equal(new ReplyDecision(true, "Your brief.", "conversation with the manager"), gate.Decide("principal-1", "conv-1", "Your brief.", "Dana"));
    }

    [Fact]
    public void Acknowledgements_need_a_conversation_a_name_and_no_earlier_message()
    {
        var gate = new ReplyGate(Fixtures.Binding, ReplyModes.Acknowledge);
        Assert.Equal(new ReplyDecision(false, Reason: "unknown or self sender"), gate.Decide(null, "conv-1", null, "Dana"));
        Assert.Equal(new ReplyDecision(false, Reason: "unknown or self sender"), gate.Decide("", "conv-1", null, "Dana"));
        Assert.Equal(new ReplyDecision(false, Reason: "already acknowledged in this conversation"), gate.Decide("colleague-1", null, null, "Dana"));
        Assert.Equal(new ReplyDecision(false, Reason: "already acknowledged in this conversation"),
                     gate.Decide("colleague-1", "conv-1", null, "Dana", firstInConversation: false));
        Assert.Equal(new ReplyDecision(false, Reason: "manager display name unavailable"), gate.Decide("colleague-1", "conv-1", null, " \u3000"));
        Assert.Equal(new ReplyDecision(false, Reason: "manager display name unavailable"), gate.Decide("colleague-1", "conv-1", null, null));
        Assert.Equal(new ReplyDecision(true, ReplyGate.Acknowledgement("Dana")), gate.Decide("colleague-1", "conv-1", null, "  Dana\n", firstInConversation: true));
        Assert.Equal(new ReplyDecision(false, Reason: "replies to other people are off; queued for the manager"),
                     new ReplyGate(Fixtures.Binding, ReplyModes.None).Decide("colleague-1", "conv-1", "x", "Dana"));
        Assert.Throws<ArgumentException>(() => new ReplyGate(Fixtures.Binding, "autonomous"));
    }

    [Fact]
    public void Reply_gate_approve_mode_sends_only_the_approved_text()
    {
        using var desk = new ApprovalDesk();
        var reply = new JsonObject { ["to"] = "colleague-1", ["text"] = "Dana can meet on Thursday." };
        var (actionId, attemptId) = desk.Started(new JsonObject { ["reply"] = reply });
        var gate = new ReplyGate(Fixtures.Binding, ReplyModes.Approve, new LedgerClaimJournal(desk.Ledger));
        var changed = gate.Decide("colleague-1", "conv-1", "Dana can meet on Friday.", "Dana", actionId, attemptId);
        Assert.Equal(new ReplyDecision(false, Reason: "reply differs from the approved text or recipient"), changed);
        var other = gate.Decide("colleague-2", "conv-1", "Dana can meet on Thursday.", "Dana", actionId, attemptId);
        Assert.False(other.Send);
        var decision = gate.Decide("colleague-1", "conv-1", "Dana can meet on Thursday.", "Dana", actionId, attemptId);
        Assert.Equal((true, "Dana can meet on Thursday."), (decision.Send, decision.Text));
        gate.Settle(decision.Claim!, "succeeded", 200);
        Assert.Equal("succeeded", PyNode.AsString(desk.Ledger.Show(actionId)["state"]));
        var again = gate.Decide("colleague-1", "conv-1", "Dana can meet on Thursday.", "Dana", actionId, attemptId);
        Assert.False(again.Send);
        Assert.StartsWith("needs the manager's approval of this exact reply: ", again.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Reply_gate_rejects_approval_by_anyone_else_and_workiq_claims()
    {
        using var desk = new ApprovalDesk();
        var reply = new JsonObject { ["to"] = "colleague-1", ["text"] = "Approved by the wrong person." };
        var (actionId, attemptId) = desk.Started(new JsonObject { ["reply"] = reply }, actor: "colleague-1");
        var gate = new ReplyGate(Fixtures.Binding, ReplyModes.Approve, new LedgerClaimJournal(desk.Ledger));
        Assert.Equal(new ReplyDecision(false, Reason: "the approval was not given by this instance's manager"),
                     gate.Decide("colleague-1", "conv-1", "Approved by the wrong person.", "Dana", actionId, attemptId));
        var (workiqId, workiqAttempt) = desk.Started(new JsonObject
        {
            ["workiq"] = new JsonObject { ["tool"] = "do_action", ["arguments"] = new JsonObject { ["actionUrl"] = "/me/messages/draft-1/send" } },
        });
        Assert.False(gate.Decide("colleague-1", "conv-1", "x", "Dana", workiqId, workiqAttempt).Send);
    }

    [Fact]
    public void Approve_mode_without_a_journal_sends_nothing()
    {
        var gate = new ReplyGate(Fixtures.Binding, ReplyModes.Approve);
        Assert.Equal(new ReplyDecision(false, Reason: "no approval journal configured"), gate.Decide("colleague-1", "conv-1", "x", "Dana"));
        Assert.Throws<InvalidOperationException>(() => gate.Settle(null!, "succeeded"));
    }

    /// <summary><c>GuardedWriteTests.setUp</c>/<c>started</c>: the instance's real ledger with one source; propose, approve, begin.</summary>
    private sealed class ApprovalDesk : IDisposable
    {
        private readonly TempState _state = new();
        private readonly JsonObject _reference;

        public ApprovalDesk()
        {
            Ledger = Fixtures.Binding.OpenLedger(_state.Path);
            _reference = Ledger.Source("mail", "inbox", "message-1", "v1", new JsonObject { ["quote"] = "Please send the fictional summary." },
                                       "https://example.com/mail/1");
        }

        public Ledger Ledger { get; }

        public (string ActionId, string AttemptId) Started(JsonObject payload, string actor = "principal-1")
        {
            var item = Ledger.Ingest(new JsonObject
            {
                ["title"] = "Send fictional summary",
                ["owner"] = null,
                ["due"] = null,
                ["direction"] = "owe",
                ["source_refs"] = new JsonArray(PyNode.Copy(_reference)),
            }, "claim-" + Guid.NewGuid().ToString("N"));
            var action = Ledger.Propose(new JsonObject
            {
                ["kind"] = "mail.send",
                ["target"] = new JsonObject { ["message_id"] = "draft-1", ["on_data_of"] = "principal" },
                ["payload"] = payload,
                ["why"] = "Explicit request",
                ["source_refs"] = new JsonArray(PyNode.Copy(_reference)),
                ["target_fingerprint"] = "target-v1",
                ["work_item_id"] = PyNode.Copy(item["id"]),
            });
            var id = PyNode.AsString(action["id"])!;
            var hash = PyNode.AsString(action["action_hash"])!;
            Ledger.Approve(id, 1, hash, new JsonObject
            {
                ["kind"] = "human_confirmation",
                ["actor"] = actor,
                ["statement"] = "Send this exact message.",
                ["evidence_ref"] = "host-interaction:fictional/teams-card/1",
                ["subject_id"] = id,
                ["revision"] = 1,
                ["decision"] = "approve",
                ["action_hash"] = hash,
                ["decided_at"] = Stamp(0),
            }, Stamp(60));
            var attempt = Ledger.Begin(id, 1, new JsonObject
            {
                ["checked_at"] = Stamp(0),
                ["target_fingerprint"] = "target-v1",
                ["source_refs"] = new JsonArray(PyNode.Copy(_reference)),
            });
            return (id, PyNode.AsString(attempt["attempt_id"])!);
        }

        public void Dispose()
        {
            Ledger.Dispose();
            _state.Dispose();
        }

        private static string Stamp(int minutes) => (PyDateTime.Now() + PyTimeDelta.FromMinutes(minutes)).Isoformat();
    }
}

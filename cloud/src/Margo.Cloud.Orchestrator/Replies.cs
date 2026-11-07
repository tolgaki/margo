using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Margo.Cloud.Core;
using Margo.Cloud.Guard;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.Orchestrator;

/// <summary>
/// The manager's <c>Replies to other people</c> modes (<c>replies.py</c> <c>NONE</c>, <c>ACKNOWLEDGE</c>, <c>APPROVE</c>,
/// <c>MODES</c>).
/// </summary>
/// <remarks>
/// <para><c>none</c> (default): no reply; the message is queued for the manager.</para>
/// <para><c>acknowledge</c>: one fixed, content-free acknowledgement per conversation. Model text is discarded, so no
/// information about the manager can leak through it.</para>
/// <para><c>approve</c>: Margo drafts a reply; it is sent only after the manager approves that exact text for that exact
/// recipient, through the same approval journal as every other outward action.</para>
/// <para>There is no autonomous mode. Answering colleagues freely from the manager's data is a different authority and
/// needs its own design and review.</para>
/// </remarks>
public static class ReplyModes
{
    public const string None = "none";

    public const string Acknowledge = "acknowledge";

    public const string Approve = "approve";

    /// <summary><c>MODES</c>, in Python's order.</summary>
    public static IReadOnlyList<string> All { get; } = [None, Acknowledge, Approve];
}

/// <summary><c>load_reply_policy(markdown)</c>'s result: <c>{"mode", "issues"}</c>.</summary>
/// <param name="Mode">One of <see cref="ReplyModes"/>; anything missing, unknown or conflicting is <c>none</c>.</param>
/// <param name="Issues">Why the mode fell back to <c>none</c>; empty when the setting was read as written.</param>
public sealed record ReplyPolicy(string Mode, IReadOnlyList<string> Issues)
{
    /// <summary><c>SETTING_LABEL</c>: the label of the one line in the manager's private <c>preferences.md</c>.</summary>
    public const string SettingLabel = "Replies to other people";

    /// <summary><c>ACKNOWLEDGEMENT</c>; <c>{manager}</c> is replaced by the manager's display name.</summary>
    public const string Acknowledgement = "Thanks. I've passed this on to {manager}, who will reply if needed.";

    /// <summary><c>_SETTING</c>, with <c>re.MULTILINE</c>. Every class is ASCII, so .NET and Python match the same text.</summary>
    private static readonly Regex Setting = new(@"^[ \t]*[-*][ \t]+\*\*Replies to other people:\*\*[ \t]*`?([A-Za-z_-]+)`?[ \t]*$",
                                                RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>
    /// <c>load_reply_policy(markdown)</c>: the mode the manager wrote, or <c>none</c> with the reason when the text is
    /// unavailable (<see langword="null"/>), the line is missing, repeated with different values, or names an unknown mode.
    /// </summary>
    public static ReplyPolicy Load(string? markdown)
    {
        if (markdown is null)
        {
            return new ReplyPolicy(ReplyModes.None, ["preferences unavailable"]);
        }

        var values = Setting.Matches(markdown).Select(match => match.Groups[1].Value.ToLowerInvariant()).ToList();
        if (values.Count == 0)
        {
            return new ReplyPolicy(ReplyModes.None, ["setting not present; defaulting to none"]);
        }

        if (values.Distinct(StringComparer.Ordinal).Count() > 1)
        {
            return new ReplyPolicy(ReplyModes.None, ["conflicting settings; defaulting to none"]);
        }

        if (!ReplyModes.All.Contains(values[0], StringComparer.Ordinal))
        {
            return new ReplyPolicy(ReplyModes.None, ["unknown value '" + values[0] + "'; defaulting to none"]);
        }

        return new ReplyPolicy(values[0], []);
    }

    /// <inheritdoc />
    public bool Equals(ReplyPolicy? other) =>
        other is not null && string.Equals(Mode, other.Mode, StringComparison.Ordinal) && Issues.SequenceEqual(other.Issues, StringComparer.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Mode, Issues.Count);
}

/// <summary>
/// One reply decision (<c>ReplyGate.decide</c>'s <c>{"send", "text"?, "claim"?, "reason"?}</c>).
/// </summary>
/// <param name="Send">Whether the reply may be sent.</param>
/// <param name="Text">The exact text to send (null when nothing is sent, or when the manager's own proposed text was null).</param>
/// <param name="Reason">Why, when Python gives one.</param>
/// <param name="Claim">In <c>approve</c> mode, the dispatched claim to settle with <see cref="ReplyGate.Settle"/> after sending.</param>
public sealed record ReplyDecision(bool Send, string? Text = null, string? Reason = null, ExecutionClaim? Claim = null);

/// <summary>
/// Decides whether one reply to a non-manager may be sent, and with what exact text (<c>replies.py</c>
/// <c>ReplyGate</c>).
/// </summary>
/// <remarks>
/// <para>Messages from anyone except the manager are data. Whether Margo answers them is the manager's choice, read
/// with <see cref="ReplyPolicy.Load"/>; the gate, not the model, decides what leaves. Replies travel on the activity
/// channel, which the Work IQ guard does not cover, so this gate is their only check.</para>
/// <para>Safe to call from several threads: the in-memory acknowledgement set is locked.</para>
/// </remarks>
public sealed class ReplyGate
{
    private readonly HashSet<(string Sender, string Conversation)> _acknowledged = [];
    private readonly object _gate = new();

    /// <param name="binding">The instance binding (the manager is its principal).</param>
    /// <param name="mode">One of <see cref="ReplyModes"/>.</param>
    /// <param name="journal">The approval journal, for <c>approve</c> mode.</param>
    /// <exception cref="ArgumentException"><paramref name="mode"/> is unknown (Python: <c>ValueError("unknown reply mode")</c>).</exception>
    public ReplyGate(InstanceBinding binding, string mode, IClaimJournal? journal = null)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (mode is null || !ReplyModes.All.Contains(mode, StringComparer.Ordinal))
        {
            throw new ArgumentException("unknown reply mode", nameof(mode));
        }

        Binding = binding;
        Mode = mode;
        Journal = journal;
    }

    /// <summary>The instance binding.</summary>
    public InstanceBinding Binding { get; }

    /// <summary>The reply mode.</summary>
    public string Mode { get; }

    /// <summary>The approval journal, when configured.</summary>
    public IClaimJournal? Journal { get; }

    /// <summary>
    /// <c>decide(sender_id, conversation_id, proposed_text, *, manager_name, action_id, attempt_id, first_in_conversation)</c>.
    /// </summary>
    /// <param name="senderId">The authenticated sender's object ID.</param>
    /// <param name="conversationId">The conversation the reply would go to.</param>
    /// <param name="proposedText">The text Margo proposes (only ever sent to the manager, or in <c>approve</c> mode once approved).</param>
    /// <param name="managerName">The manager's display name, for the acknowledgement.</param>
    /// <param name="actionId">In <c>approve</c> mode, the approved action (the model's raw value).</param>
    /// <param name="attemptId">In <c>approve</c> mode, the live attempt (the model's raw value).</param>
    /// <param name="firstInConversation">Durable knowledge of earlier messages from the host, so an acknowledgement is sent
    /// once per conversation across restarts and sandboxes; <see langword="false"/> means the sender wrote here before.</param>
    public ReplyDecision Decide(string? senderId, string? conversationId, string? proposedText, string? managerName,
                                JsonNode? actionId = null, JsonNode? attemptId = null, bool? firstInConversation = null)
    {
        if (string.IsNullOrEmpty(senderId) || string.Equals(senderId, Binding.AgentUserId, StringComparison.Ordinal))
        {
            return new ReplyDecision(false, Reason: "unknown or self sender");
        }

        if (Binding.IsPrincipal(senderId))
        {
            return new ReplyDecision(true, proposedText, "conversation with the manager");
        }

        if (Mode == ReplyModes.None)
        {
            return new ReplyDecision(false, Reason: "replies to other people are off; queued for the manager");
        }

        if (Mode == ReplyModes.Acknowledge)
        {
            lock (_gate)
            {
                var seen = (conversationId is not null && _acknowledged.Contains((senderId, conversationId))) || firstInConversation == false;
                if (string.IsNullOrEmpty(conversationId) || seen)
                {
                    return new ReplyDecision(false, Reason: "already acknowledged in this conversation");
                }

                if (managerName is null || PyRepr.Strip(managerName).Length == 0)
                {
                    return new ReplyDecision(false, Reason: "manager display name unavailable");
                }

                _acknowledged.Add((senderId, conversationId));
                return new ReplyDecision(true, Acknowledgement(PyRepr.Strip(managerName)));
            }
        }

        return Approved(senderId, proposedText, actionId, attemptId);
    }

    /// <summary><c>settle(claim, state, status_code)</c>: records the actual outcome after the runtime sends (or fails to send) the reply.</summary>
    /// <exception cref="InvalidOperationException">No journal is configured (Python fails with <c>AttributeError</c>).</exception>
    public JsonObject Settle(ExecutionClaim claim, string state, int? statusCode = null) =>
        (Journal ?? throw new InvalidOperationException("no approval journal configured")).Settle(claim, state, statusCode);

    /// <summary><c>ACKNOWLEDGEMENT.format(manager=name)</c>.</summary>
    public static string Acknowledgement(string manager) =>
        ReplyPolicy.Acknowledgement.Replace("{manager}", manager, StringComparison.Ordinal);

    private ReplyDecision Approved(string senderId, string? proposedText, JsonNode? actionId, JsonNode? attemptId)
    {
        if (Journal is null)
        {
            return new ReplyDecision(false, Reason: "no approval journal configured");
        }

        ExecutionClaim claim;
        try
        {
            claim = Journal.Load(actionId, attemptId, LedgerClaimJournal.ReplyCallKey);
        }
        catch (Exception exc)
        {
            return new ReplyDecision(false, Reason: "needs the manager's approval of this exact reply: " + exc.Message);
        }

        if (!string.Equals(claim.Account, Binding.Account, StringComparison.Ordinal) || !PyNode.Is(claim.ApprovedBy, Binding.PrincipalId))
        {
            return new ReplyDecision(false, Reason: "the approval was not given by this instance's manager");
        }

        if (!PyJson.PyEquals(claim.Call, new JsonObject { ["to"] = senderId, ["text"] = proposedText }))
        {
            return new ReplyDecision(false, Reason: "reply differs from the approved text or recipient");
        }

        try
        {
            Journal.MarkDispatched(claim);
        }
        catch (Exception)
        {
            return new ReplyDecision(false, Reason: "could not record dispatch; nothing was sent");
        }

        return new ReplyDecision(true, proposedText, Claim: claim);
    }
}

using System.Text.Json.Nodes;
using Margo.Cloud.Core;
using Margo.Cloud.Engine;
using Margo.Cloud.Guard;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Work;

namespace Margo.Cloud.Orchestrator;

/// <summary>The manager's tap on an approval card: the decision and the exact version of the action it was shown.</summary>
/// <param name="Verb"><see cref="Approve"/> or <see cref="Reject"/>.</param>
/// <param name="ActionId">The ledger action.</param>
/// <param name="Revision">The revision the card showed.</param>
/// <param name="ActionHash">The hash of that revision, as the card showed it.</param>
public sealed record CardDecision(string Verb, string ActionId, long Revision, string ActionHash)
{
    /// <summary>
    /// The context the card showed (which email, meeting or chat), carried back by the button so the card's last version
    /// still shows it. Display only: the decision is bound to the action, revision and hash.
    /// </summary>
    public IReadOnlyList<CardFact> Context { get; init; } = [];

    public const string Approve = "approve";
    public const string Reject = "reject";
}

/// <summary>
/// One action shown to the manager for approval, built from the ledger (never from the model's text): the exact Work IQ
/// call that runs if they approve it.
/// </summary>
/// <param name="ActionId">The ledger action.</param>
/// <param name="Revision">Its revision.</param>
/// <param name="ActionHash">The hash of that revision; the approval names it.</param>
/// <param name="Kind">What the action is (<c>mail.send</c>, <c>meeting.create</c>, ...), as proposed.</param>
/// <param name="OnDataOf"><c>principal</c> (the manager's mailbox or calendar) or <c>self</c> (Margo's own account).</param>
/// <param name="Tool">The Work IQ write tool.</param>
/// <param name="Arguments">Its exact arguments.</param>
/// <param name="Why">Why Margo proposes it, as proposed.</param>
public sealed record ApprovalCard(string ActionId, long Revision, string ActionHash, string Kind, string OnDataOf, string Tool,
                                  JsonObject Arguments, string? Why)
{
    /// <summary>Which existing email, meeting, chat or file the action affects, read from Microsoft 365 (see <see cref="CardContext"/>).</summary>
    public IReadOnlyList<CardFact> Context { get; init; } = [];

    /// <summary>What kind of action this is, in the manager's terms.</summary>
    public ApprovalKind ApprovalKind => CardContext.KindOf(Tool, Arguments);

    /// <summary>After a decision: what happened, for the card's last version (no buttons).</summary>
    public string? Outcome { get; init; }

    /// <summary>After a decision: <c>sent</c>, <c>rejected</c>, <c>not_sent</c>, <c>unknown</c> or <c>out_of_date</c>.</summary>
    public string? OutcomeStatus { get; init; }

    /// <summary>The card for a ledger action that carries an exact Work IQ call, or null when it does not.</summary>
    public static ApprovalCard? FromAction(JsonObject action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (PyNode.Get(action, "payload") is not JsonObject payload || PyNode.Get(payload, LedgerClaimJournal.WorkIQCallKey) is not JsonObject call
            || PyNode.AsString(PyNode.Get(call, "tool")) is not { } tool || PyNode.Get(call, "arguments") is not JsonObject arguments
            || PyNode.Get(action, "target") is not JsonObject target || PyNode.AsString(PyNode.Get(target, "on_data_of")) is not { } onDataOf
            || PyNode.AsString(PyNode.Get(action, "id")) is not { } id || PyNode.AsString(PyNode.Get(action, "action_hash")) is not { } hash
            || PyNode.Get(action, "revision") is not JsonValue revisionValue || !revisionValue.TryGetValue<long>(out var revision))
        {
            return null;
        }

        return new ApprovalCard(id, revision, hash, PyNode.AsString(PyNode.Get(action, "kind")) ?? "action", onDataOf, tool,
                                PyNode.CopyObject(arguments), PyNode.AsString(PyNode.Get(action, "why")));
    }
}

/// <summary>
/// <c>request_approval</c> for one manager turn: checks a proposed action and queues its card. The card goes out with
/// the turn's reply; nothing runs until the manager approves it on the card.
/// </summary>
internal sealed class TurnApprovals(InstanceBinding binding, string stateRoot, WorkIQGuard guard) : IApprovalRequests
{
    /// <summary>At most this many cards in one reply.</summary>
    public const int MaxCards = 5;

    private readonly List<ApprovalCard> _cards = [];

    public IReadOnlyList<ApprovalCard> Cards => _cards;

    public async Task<JsonObject> RequestAsync(string actionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actionId);
        if (_cards.Count >= MaxCards)
        {
            return (Refused("at most " + MaxCards + " approval cards in one reply; ask for the rest after these are decided"));
        }

        if (_cards.Any(card => card.ActionId == actionId))
        {
            return new JsonObject { ["status"] = "ok", ["card"] = "already queued for this reply" };
        }

        JsonObject action;
        try
        {
            using var ledger = binding.OpenLedger(stateRoot);
            action = ledger.Show(actionId);
        }
        catch (StateError)
        {
            return Refused("no such action; propose it with work_state.py propose first");
        }

        if (!PyNode.Is(PyNode.Get(action, "type"), "action") || !PyNode.Is(PyNode.Get(action, "state"), "ready"))
        {
            return Refused("the action is not ready for approval (state " + (PyNode.AsString(PyNode.Get(action, "state")) ?? "?")
                           + "); propose it again");
        }

        if (ApprovalCard.FromAction(action) is not { } card)
        {
            return Refused("the action carries no exact Work IQ call: payload.workiq {tool, arguments} and target.on_data_of are required");
        }

        var arguments = PyNode.CopyObject(card.Arguments);
        arguments["on_data_of"] = card.OnDataOf;
        if (guard.WriteProblem(card.Tool, arguments) is { } problem)
        {
            return Refused("this call would be refused if approved: " + problem);
        }

        // The card says which email, meeting, chat or file this is, read from Microsoft 365 rather than from the model.
        var context = await CardContext.DescribeAsync(guard, card.Tool, card.Arguments, card.OnDataOf, cancellationToken).ConfigureAwait(false);
        _cards.Add(card with { Context = context });
        return new JsonObject
        {
            ["status"] = "ok",
            ["card"] = "your manager will see an approval card for this exact action after your reply; nothing is sent until they approve it",
        };
    }

    private static JsonObject Refused(string reason) => new() { ["status"] = GuardStatuses.Refused, ["reason"] = reason };
}

/// <summary>Runs the manager's decision on a card: records it and, for an approval, runs exactly the approved call once.</summary>
internal static class CardDecisions
{
    /// <summary>How long an approval made on a card stays usable: it is used at once.</summary>
    public static readonly TimeSpan ApprovalLifetime = TimeSpan.FromMinutes(10);

    public const string Sent = "Done.";

    /// <summary>What a successful approval did, in plain words.</summary>
    public static string Done(ApprovalKind kind) => kind switch
    {
        ApprovalKind.Email or ApprovalKind.Reply or ApprovalKind.Forward => "Done: the email has gone out.",
        ApprovalKind.MeetingNew => "Done: the meeting is on your calendar and the invitations are on their way.",
        ApprovalKind.MeetingChange => "Done: the meeting is updated.",
        ApprovalKind.MeetingCancel => "Done: the meeting is canceled and the attendees are being told.",
        ApprovalKind.Chat or ApprovalKind.Channel => "Done: the message is posted.",
        ApprovalKind.DocumentChange => "Done: the document is changed.",
        ApprovalKind.Share => "Done: the file is shared.",
        ApprovalKind.Delete => "Done: it's deleted.",
        ApprovalKind.Page => "Done: the Page is published.",
        ApprovalKind.EngagePost => "Done: the post is up on Viva Engage.",
        ApprovalKind.SharePointChange => "Done: the SharePoint change is made.",
        _ => Sent,
    };
    public const string Rejected = "Rejected. Nothing was sent.";
    public const string Unknown = "I sent it but got no clear answer back, so I don't know whether it went through. I won't send it again: "
                                  + "check your Sent Items or calendar.";
    public const string OutOfDate = "This card is out of date ({0}). Nothing was sent. Ask me again if you still want it.";
    public const string NotSent = "Nothing went out: {0}.";

    private static readonly HashSet<string> BeforeDispatch = new(StringComparer.Ordinal)
    {
        GuardStatuses.Refused, GuardStatuses.Unavailable, GuardStatuses.Blocked, GuardStatuses.BudgetExhausted,
    };

    public static async Task<(string Reply, ApprovalCard? Card)> DecideAsync(CardDecision decision, InstanceBinding binding, string stateRoot,
                                                                            Func<IClaimJournal, WorkIQGuard> newGuard, string evidenceRef,
                                                                            CancellationToken cancellationToken)
    {
        using var ledger = binding.OpenLedger(stateRoot);
        JsonObject action;
        try
        {
            action = ledger.Show(decision.ActionId);
        }
        catch (StateError)
        {
            return (Finished(OutOfDate, "I have no record of this action"), null);
        }

        var card = ApprovalCard.FromAction(action) is { } shown ? shown with { Context = decision.Context } : null;
        var state = PyNode.AsString(PyNode.Get(action, "state"));
        if (card is null || card.Revision != decision.Revision || !string.Equals(card.ActionHash, decision.ActionHash, StringComparison.Ordinal)
            || state != "ready")
        {
            var why = state switch
            {
                "succeeded" => "it was already done",
                "executing" or "outcome_unknown" or "partial" => "it was already approved and run",
                "dismissed" => "it was rejected",
                _ => "the action changed or was already decided",
            };
            return (Finished(OutOfDate, why), card is null ? null : card with { Outcome = Finished(OutOfDate, why), OutcomeStatus = "out_of_date" });
        }

        if (decision.Verb == CardDecision.Reject)
        {
            ledger.Disposition(PyNode.Of(card.ActionId), PyNode.Of(card.Revision), "dismissed", expectedHash: PyNode.Of(card.ActionHash));
            return (Rejected, card with { Outcome = Rejected, OutcomeStatus = "rejected" });
        }

        var guard = newGuard(new LedgerClaimJournal(ledger));
        var arguments = PyNode.CopyObject(card.Arguments);
        arguments["on_data_of"] = card.OnDataOf;
        if (guard.WriteProblem(card.Tool, arguments) is not null)
        {
            return NotSentResult(card, GuardStatuses.Refused);
        }

        string attemptId;
        try
        {
            var now = PyDateTime.Now();
            ledger.Approve(PyNode.Of(card.ActionId), PyNode.Of(card.Revision), PyNode.Of(card.ActionHash), new JsonObject
            {
                ["kind"] = "human_confirmation",
                ["actor"] = binding.PrincipalId,
                ["statement"] = "Approved this exact action on its approval card in Teams.",
                ["evidence_ref"] = evidenceRef,
                ["subject_id"] = card.ActionId,
                ["revision"] = card.Revision,
                ["decision"] = "approve",
                ["action_hash"] = card.ActionHash,
                ["decided_at"] = now.Isoformat(),
            }, PyNode.Of((now + PyTimeDelta.FromMinutes((long)ApprovalLifetime.TotalMinutes)).Isoformat()));

            // The approved snapshot is what runs: no new read of the target happens between the tap and the call.
            var started = ledger.Begin(PyNode.Of(card.ActionId), PyNode.Of(card.Revision), new JsonObject
            {
                ["checked_at"] = now.Isoformat(),
                ["target_fingerprint"] = PyNode.Copy(PyNode.Get(action, "target_fingerprint")),
                ["source_refs"] = PyNode.Copy(PyNode.Get(action, "source_refs")),
            });
            attemptId = PyNode.AsString(started["attempt_id"])!;
        }
        catch (StateError)
        {
            return NotSentResult(card, "not_recorded");
        }

        arguments["action_id"] = card.ActionId;
        arguments["attempt_id"] = attemptId;
        var result = await guard.CallAsync(card.Tool, arguments, RunModes.Attended, cancellationToken).ConfigureAwait(false);
        if (result.Status == GuardStatuses.Ok)
        {
            var done = Done(card.ApprovalKind);
            return (done, card with { Outcome = done, OutcomeStatus = "sent" });
        }

        if (result.Status == GuardStatuses.Unknown)
        {
            return (Unknown, card with { Outcome = Unknown, OutcomeStatus = "unknown" });
        }

        if (BeforeDispatch.Contains(result.Status) && PyNode.Is(PyNode.Get(ledger.Show(card.ActionId), "state"), "executing"))
        {
            // Refused before anything was sent: the attempt ends here, with nothing sent.
            ledger.Finish(PyNode.Of(attemptId), PyNode.Of("failed"), new JsonObject
            {
                ["kind"] = "tool_result",
                ["reference"] = "workiq:" + attemptId,
                ["outcome"] = "failed",
                ["recorded_at"] = StateStore.UtcNow(),
                ["definitive_no_effect"] = true,
            });
        }

        return NotSentResult(card, result.Status);
    }

    /// <summary>Why nothing went out, in plain words; the technical reason is in the guard's record of the call.</summary>
    public static string PlainReason(string status) => status switch
    {
        GuardStatuses.Refused => "this isn't something I'm allowed to do for you",
        GuardStatuses.Blocked => "your organization's settings don't allow it",
        GuardStatuses.Unavailable => "I couldn't reach Microsoft 365. Ask me again for a new card",
        GuardStatuses.BudgetExhausted => "I've reached my limit for now. Ask me again later",
        "not_recorded" => "I couldn't record your approval, so I stopped. Ask me again for a new card",
        _ => "Microsoft 365 turned it down",
    };

    private static (string, ApprovalCard?) NotSentResult(ApprovalCard card, string status)
    {
        var text = Finished(NotSent, PlainReason(status));
        return (text, card with { Outcome = text, OutcomeStatus = "not_sent" });
    }

    private static string Finished(string format, string detail) => format.Replace("{0}", detail, StringComparison.Ordinal);
}

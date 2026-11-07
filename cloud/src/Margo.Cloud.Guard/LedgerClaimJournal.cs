using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Work;

namespace Margo.Cloud.Guard;

/// <summary>
/// Execution claims for guarded writes, backed by the existing approval journal (<c>claims.py</c>
/// <c>LedgerClaimJournal</c>): read with <see cref="Ledger.Show(string)"/> and <see cref="Ledger.History(string)"/>,
/// consumed durably with a <see cref="DispatchedEvent"/> event, settled with <see cref="Ledger.Finish"/>.
/// </summary>
/// <remarks>
/// <para>Hosted convention: an action executed by the autopilot carries the exact outward call in its approved
/// snapshot, so the human approved the call itself, not a description of it:</para>
/// <code>
/// "target":  {..., "on_data_of": "principal"},
/// "payload": {..., "workiq": {"tool": "do_action", "arguments": {"actionUrl": "..."}}}
/// </code>
/// <para>An approved reply to someone other than the manager uses <c>"payload": {"reply": {"to": ID, "text": "..."}}</c>
/// instead. The approval must be recorded with the manager's directory object ID as its actor; the hosting runtime
/// takes that ID from the authenticated card submission.</para>
/// <para>The journal does not own the ledger: the caller opens and closes it. Calls are not safe to make
/// concurrently on one ledger (one SQLite connection), as in Python.</para>
/// </remarks>
public sealed class LedgerClaimJournal : IClaimJournal
{
    /// <summary>The event that durably consumes a claim before the provider call (<c>DISPATCHED</c>).</summary>
    public const string DispatchedEvent = "workiq_dispatched";

    /// <summary>The payload key of an exact Work IQ call.</summary>
    public const string WorkIQCallKey = "workiq";

    /// <summary>The payload key of an exact approved reply (used by the reply gate).</summary>
    public const string ReplyCallKey = "reply";

    public LedgerClaimJournal(Ledger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        Ledger = ledger;
    }

    /// <summary>The ledger claims are read from and settled in.</summary>
    public Ledger Ledger { get; }

    /// <inheritdoc/>
    public ExecutionClaim Load(JsonNode? actionId, JsonNode? attemptId, string callKey = WorkIQCallKey)
    {
        ArgumentNullException.ThrowIfNull(callKey);
        if (PyDynamic.AsString(actionId) is not { } actionText || PyDynamic.AsString(attemptId) is not { } attemptText)
        {
            throw new ClaimException("action_id and attempt_id are required for a guarded write");
        }

        JsonObject action;
        try
        {
            action = Ledger.Show(actionText);
        }
        catch (Exception exc) // StateError from the ledger; never leak internals as success
        {
            throw new ClaimException("unknown action", exc);
        }

        if (!PyDynamic.Is(PyDynamic.Get(action, "type"), "action") || !PyDynamic.Is(PyDynamic.Get(action, "state"), "executing"))
        {
            throw new ClaimException("action has no live execution claim");
        }

        var attempt = FindAttempt(action, attemptText);
        if (attempt is null || !PyDynamic.Is(PyDynamic.Get(attempt, "state"), "executing")
            || !PyDynamic.Eq(PyDynamic.Get(attempt, "revision"), PyDynamic.Index(action, "revision")))
        {
            throw new ClaimException("execution attempt is not live for the current revision");
        }

        var target = PyDynamic.Get(action, "target");
        var payload = PyDynamic.Get(action, "payload");
        if (target is not JsonObject targetObject || payload is not JsonObject payloadObject
            || PyDynamic.Get(payloadObject, callKey) is not JsonObject call)
        {
            throw new ClaimException("approved action does not carry an exact " + callKey + " call");
        }

        if (Dispatched(actionText, attemptText))
        {
            throw new ClaimException("attempt was already dispatched; reconcile it, never resend");
        }

        var revision = PyDynamic.Index(action, "revision");
        var actionHash = PyDynamic.Index(action, "action_hash");
        return new ExecutionClaim(
            Ledger.Account,
            actionText,
            attemptText,
            PyDynamic.Clone(revision),
            PyDynamic.Clone(actionHash),
            PyDynamic.Clone(PyDynamic.Get(targetObject, "on_data_of")),
            callKey,
            PyDynamic.CloneObject(call),
            PyDynamic.Clone(Approver(actionText, revision, actionHash)));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A crash between dispatch and settlement leaves the attempt <c>executing</c> with this event recorded; it must
    /// then be reconciled, and no guard will send it again.
    /// </remarks>
    public void MarkDispatched(ExecutionClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        Ledger.Transaction(() =>
        {
            var action = Ledger.Show(claim.ActionId);
            var attempt = FindAttempt(action, claim.AttemptId);
            if (!PyDynamic.Is(PyDynamic.Get(action, "state"), "executing") || attempt is null
                || !PyDynamic.Is(PyDynamic.Get(attempt, "state"), "executing")
                || !PyDynamic.Eq(PyDynamic.Get(action, "action_hash"), claim.ActionHash))
            {
                throw new ClaimException("execution claim is no longer live");
            }

            if (Dispatched(claim.ActionId, claim.AttemptId))
            {
                throw new ClaimException("attempt was already dispatched; reconcile it, never resend");
            }

            Ledger.Event(claim.ActionId, DispatchedEvent,
                         new JsonObject { ["attempt_id"] = claim.AttemptId, ["dispatched_at"] = StateStore.UtcNow() });
        });
    }

    /// <inheritdoc/>
    public JsonObject Settle(ExecutionClaim claim, string state, int? statusCode = null)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(state);
        JsonObject? receipt = null;
        if (state != "outcome_unknown")
        {
            receipt = new JsonObject
            {
                ["kind"] = "tool_result",
                ["reference"] = "workiq:" + claim.AttemptId,
                ["outcome"] = state,
                ["recorded_at"] = StateStore.UtcNow(),
            };
            if (statusCode is not null)
            {
                receipt["status_code"] = statusCode.Value;
            }

            if (state == "failed")
            {
                receipt["definitive_no_effect"] = true;
            }
        }

        return Ledger.Finish(claim.AttemptId, state, receipt);
    }

    private static JsonObject? FindAttempt(JsonObject action, string attemptId)
    {
        // next((row for row in action.get("executions", []) if row.get("id") == attempt_id), None)
        foreach (var row in PyDynamic.Iter(PyDynamic.Get(action, "executions", new JsonArray())))
        {
            if (PyDynamic.Is(PyDynamic.Get(PyDynamic.Dict(row), "id"), attemptId))
            {
                return (JsonObject)row!;
            }
        }

        return null;
    }

    /// <summary>The actor recorded on the approval that authorised this exact revision and hash (the last one wins).</summary>
    private JsonNode? Approver(string actionId, JsonNode? revision, JsonNode? actionHash)
    {
        JsonNode? actor = null;
        foreach (var @event in Events(actionId))
        {
            if (!PyDynamic.Is(PyDynamic.Get(@event, "event"), "approved"))
            {
                continue;
            }

            var evidence = PyDynamic.Get(PyDynamic.Dict(PyDynamic.Get(@event, "data", new JsonObject())), "evidence", new JsonObject());
            var fields = PyDynamic.Dict(evidence);
            if (PyDynamic.Eq(PyDynamic.Get(fields, "revision"), revision) && PyDynamic.Eq(PyDynamic.Get(fields, "action_hash"), actionHash))
            {
                actor = PyDynamic.Get(fields, "actor");
            }
        }

        return actor;
    }

    private bool Dispatched(string actionId, string attemptId) =>
        Events(actionId).Any(@event => PyDynamic.Is(PyDynamic.Get(@event, "event"), DispatchedEvent)
                                       && PyDynamic.Is(PyDynamic.Get(PyDynamic.Dict(PyDynamic.Get(@event, "data", new JsonObject())), "attempt_id"), attemptId));

    private IEnumerable<JsonObject> Events(string actionId) =>
        PyDynamic.Iter(PyDynamic.Index(Ledger.History(actionId), "events")).Select(@event => PyDynamic.Dict(@event));
}

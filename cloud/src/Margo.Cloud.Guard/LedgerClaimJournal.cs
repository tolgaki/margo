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
        if (PyNode.AsString(actionId) is not { } actionText || PyNode.AsString(attemptId) is not { } attemptText)
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

        if (!PyNode.Is(PyNode.Get(action, "type"), "action") || !PyNode.Is(PyNode.Get(action, "state"), "executing"))
        {
            throw new ClaimException("action has no live execution claim");
        }

        var attempt = FindAttempt(action, attemptText);
        if (attempt is null || !PyNode.Is(PyNode.Get(attempt, "state"), "executing")
            || !PyJson.PyEquals(PyNode.Get(attempt, "revision"), PyNode.Index(action, "revision")))
        {
            throw new ClaimException("execution attempt is not live for the current revision");
        }

        var target = PyNode.Get(action, "target");
        var payload = PyNode.Get(action, "payload");
        if (target is not JsonObject targetObject || payload is not JsonObject payloadObject
            || PyNode.Get(payloadObject, callKey) is not JsonObject call)
        {
            throw new ClaimException("approved action does not carry an exact " + callKey + " call");
        }

        if (Dispatched(actionText, attemptText))
        {
            throw new ClaimException("attempt was already dispatched; reconcile it, never resend");
        }

        var revision = PyNode.Index(action, "revision");
        var actionHash = PyNode.Index(action, "action_hash");
        return new ExecutionClaim(
            Ledger.Account,
            actionText,
            attemptText,
            PyNode.Copy(revision),
            PyNode.Copy(actionHash),
            PyNode.Copy(PyNode.Get(targetObject, "on_data_of")),
            callKey,
            PyNode.CopyObject(call),
            PyNode.Copy(Approver(actionText, revision, actionHash)));
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
            if (!PyNode.Is(PyNode.Get(action, "state"), "executing") || attempt is null
                || !PyNode.Is(PyNode.Get(attempt, "state"), "executing")
                || !PyJson.PyEquals(PyNode.Get(action, "action_hash"), claim.ActionHash))
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
        foreach (var row in PyNode.Iter(PyNode.Get(action, "executions", new JsonArray())))
        {
            if (PyNode.Is(PyNode.Get(PyNode.Dict(row), "id"), attemptId))
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
            if (!PyNode.Is(PyNode.Get(@event, "event"), "approved"))
            {
                continue;
            }

            var evidence = PyNode.Get(PyNode.Dict(PyNode.Get(@event, "data", new JsonObject())), "evidence", new JsonObject());
            var fields = PyNode.Dict(evidence);
            if (PyJson.PyEquals(PyNode.Get(fields, "revision"), revision) && PyJson.PyEquals(PyNode.Get(fields, "action_hash"), actionHash))
            {
                actor = PyNode.Get(fields, "actor");
            }
        }

        return actor;
    }

    private bool Dispatched(string actionId, string attemptId) =>
        Events(actionId).Any(@event => PyNode.Is(PyNode.Get(@event, "event"), DispatchedEvent)
                                       && PyNode.Is(PyNode.Get(PyNode.Dict(PyNode.Get(@event, "data", new JsonObject())), "attempt_id"), attemptId));

    private IEnumerable<JsonObject> Events(string actionId) =>
        PyNode.Iter(PyNode.Index(Ledger.History(actionId), "events")).Select(@event => PyNode.Dict(@event));
}

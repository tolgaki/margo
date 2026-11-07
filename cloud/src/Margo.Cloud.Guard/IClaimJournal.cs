using System.Text.Json.Nodes;

namespace Margo.Cloud.Guard;

/// <summary>
/// One live execution attempt, read from the approval journal (the dict <c>LedgerClaimJournal.load</c> returns).
/// </summary>
/// <param name="Account">The ledger account the claim was read from.</param>
/// <param name="ActionId">The approved action.</param>
/// <param name="AttemptId">The live execution attempt started by <c>Ledger.Begin</c>.</param>
/// <param name="Revision">The action's current revision (<c>action["revision"]</c>).</param>
/// <param name="ActionHash">The approved revision's hash (<c>action["action_hash"]</c>).</param>
/// <param name="OnDataOf">The approved target's <c>on_data_of</c>, as stored (any JSON value, or null when absent).</param>
/// <param name="CallKey">The payload key the call was read from: <c>workiq</c> for guarded writes, <c>reply</c> for the reply gate.</param>
/// <param name="Call">The exact approved call (<c>payload[call_key]</c>), a detached copy.</param>
/// <param name="ApprovedBy">The actor recorded on the approval of this exact revision and hash (any JSON value; null when none).</param>
public sealed record ExecutionClaim(
    string Account,
    string ActionId,
    string AttemptId,
    JsonNode? Revision,
    JsonNode? ActionHash,
    JsonNode? OnDataOf,
    string CallKey,
    JsonObject Call,
    JsonNode? ApprovedBy);

/// <summary>
/// Execution claims for guarded writes (the journal object the Python guard and reply gate use:
/// <c>load</c>, <c>mark_dispatched</c>, <c>settle</c>). <see cref="LedgerClaimJournal"/> is the implementation;
/// the interface is the seam for tests and for the reply gate.
/// </summary>
/// <remarks>
/// The journal never decides that a write is approved. It reports whether one attempt, already started after an
/// exact human approval, is still live; records its durable dispatch before the provider call; and records the
/// actual outcome once. Calls are synchronous (SQLite); none of them makes a network call.
/// </remarks>
public interface IClaimJournal
{
    /// <summary>The live claim for <paramref name="actionId"/>/<paramref name="attemptId"/> (raw model values).</summary>
    /// <exception cref="ClaimException">The claim is missing, settled, mismatched or already dispatched.</exception>
    ExecutionClaim Load(JsonNode? actionId, JsonNode? attemptId, string callKey = "workiq");

    /// <summary>Durably consume the claim before the provider call, across processes and restarts.</summary>
    /// <exception cref="ClaimException">The claim is no longer live, or was already dispatched.</exception>
    void MarkDispatched(ExecutionClaim claim);

    /// <summary>Record the actual outcome once. Unknown stays unknown; nothing is retried.</summary>
    /// <returns>The action as the ledger shows it after settling.</returns>
    JsonObject Settle(ExecutionClaim claim, string state, int? statusCode = null);
}

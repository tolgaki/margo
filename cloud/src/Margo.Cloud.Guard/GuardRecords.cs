using Margo.Cloud.State.Core;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Work;

namespace Margo.Cloud.Guard;

/// <summary>
/// What one guarded call returns to the model (the dict <c>WorkIQGuard.call</c> returns). It never raises for a
/// refusal or a provider failure; those are statuses (see <see cref="GuardStatuses"/>).
/// </summary>
/// <param name="Status">One of <see cref="GuardStatuses"/>.</param>
/// <param name="ReadAs"><see cref="Subjects.Principal"/>, <see cref="Subjects.Margo"/>, or null when the call named no valid subject.</param>
/// <param name="Tool">The tool name, or null when the model gave none.</param>
/// <param name="Reason">Why the call did not simply succeed; null when there is nothing to say.</param>
/// <param name="Data">The Work IQ payload, when one was received (also for <c>error</c>, <c>partial</c> and an unknown write).</param>
public sealed record GuardResult(string Status, string? ReadAs, string? Tool, string? Reason = null, JsonObject? Data = null)
{
    /// <summary>The result as Python builds it: <c>status</c>, <c>read_as</c>, <c>tool</c>, then <c>reason</c> and <c>data</c> when present.</summary>
    /// <remarks>The payload is copied, so this can be called more than once.</remarks>
    public JsonObject ToJsonObject()
    {
        var response = new JsonObject { ["status"] = Status, ["read_as"] = ReadAs, ["tool"] = Tool };
        if (!string.IsNullOrEmpty(Reason))
        {
            response["reason"] = Reason;
        }

        if (Data is not null)
        {
            response["data"] = PyNode.Copy(Data);
        }

        return response;
    }
}

/// <summary>
/// The record every guarded call leaves (<c>guard.py</c> receipts): subject, tool, paths without query text, status
/// codes and outcome. Tokens, bodies, queries and results are never recorded.
/// </summary>
/// <remarks>Optional fields are null when Python leaves the key out of the receipt.</remarks>
public sealed record GuardReceipt
{
    /// <summary>When the call started: <c>datetime.now(timezone.utc).isoformat(timespec="microseconds")</c>.</summary>
    public required string At { get; init; }

    /// <summary>The run mode as given (any string), or null when none was given.</summary>
    public string? Mode { get; init; }

    /// <summary>The tool name as given (any string), or null.</summary>
    public string? Tool { get; init; }

    /// <summary>The model's <c>on_data_of</c> when it was a string (even an invalid one), else null.</summary>
    public string? OnDataOf { get; init; }

    /// <summary><see cref="Subjects.Principal"/> for principal intent, <see cref="Subjects.Margo"/> for self, else null.</summary>
    public string? ReadAs { get; init; }

    /// <summary>The resource paths the call touched, without query strings (after the delegation rewrite).</summary>
    public IReadOnlyList<string> Paths { get; init; } = [];

    /// <summary>Integer status codes from the payload, or the transport error's status (Python <c>int</c> values; booleans included).</summary>
    public IReadOnlyList<JsonNode> StatusCodes { get; init; } = [];

    /// <summary>The execution claim's action, for a claimed write.</summary>
    public string? ActionId { get; init; }

    /// <summary>The execution claim's attempt, for a claimed write.</summary>
    public string? AttemptId { get; init; }

    /// <summary><c>delegation</c> when principal intent was served through Margo's delegated access.</summary>
    public string? Via { get; init; }

    /// <summary>How the claim was settled (<c>succeeded</c>, <c>failed</c>, <c>outcome_unknown</c>), or <c>unsettled</c> when the journal failed.</summary>
    public string? Journal { get; init; }

    /// <summary>The result status (one of <see cref="GuardStatuses"/>).</summary>
    public required string Status { get; init; }

    /// <summary>The result reason, when there was one.</summary>
    public string? Reason { get; init; }

    /// <summary>The receipt as Python's dict, keys in Python's insertion order.</summary>
    public JsonObject ToJsonObject()
    {
        var receipt = new JsonObject
        {
            ["at"] = At,
            ["mode"] = Mode,
            ["tool"] = Tool,
            ["on_data_of"] = OnDataOf,
            ["read_as"] = ReadAs,
            ["paths"] = new JsonArray(Paths.Select(path => (JsonNode?)JsonValue.Create(path)).ToArray()),
            ["status_codes"] = new JsonArray(StatusCodes.Select(code => PyNode.Copy(code)).ToArray()),
        };
        if (ActionId is not null)
        {
            receipt["action_id"] = ActionId;
            receipt["attempt_id"] = AttemptId;
        }

        if (Via is not null)
        {
            receipt["via"] = Via;
        }

        if (Journal is not null)
        {
            receipt["journal"] = Journal;
        }

        receipt["status"] = Status;
        if (!string.IsNullOrEmpty(Reason))
        {
            receipt["reason"] = Reason;
        }

        return receipt;
    }
}

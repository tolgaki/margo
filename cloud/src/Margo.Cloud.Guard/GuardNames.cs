using System.Collections.Frozen;
using Margo.Cloud.WorkIQ;

namespace Margo.Cloud.Guard;

/// <summary>Work IQ's endpoint and token audience (<c>guard.py</c> <c>WORKIQ_MCP_URL</c>, <c>WORKIQ_RESOURCE</c>).</summary>
public static class WorkIQScopes
{
    /// <summary>The one MCP endpoint the guard connects to; the same string as <see cref="WorkIQEndpoints.McpUrl"/>.</summary>
    public const string McpUrl = WorkIQEndpoints.McpUrl;

    /// <summary>The token audience (<c>WORKIQ_RESOURCE</c>).</summary>
    public const string Resource = "api://workiq.svc.cloud.microsoft";

    /// <summary>
    /// The scope the host and voice server request for Work IQ tokens (<c>host/app.py</c> and
    /// <c>voice/server.py</c> <c>WORKIQ_SCOPE</c>).
    /// </summary>
    public const string DefaultScope = Resource + "/.default";
}

/// <summary>Whose data a call is about, and whose token reads it (<c>PRINCIPAL</c>, <c>SELF</c>, <c>MARGO</c>).</summary>
public static class Subjects
{
    /// <summary>The model's <c>on_data_of</c> for the manager's data, and the principal token's subject.</summary>
    public const string Principal = "principal";

    /// <summary>The model's <c>on_data_of</c> for Margo's own data.</summary>
    public const string Self = "self";

    /// <summary>The subject of Margo's own agent-user token, and <c>read_as</c> for <see cref="Self"/>.</summary>
    public const string Margo = "margo";

    /// <summary><c>INTENTS</c>: the values the model may give for <c>on_data_of</c>.</summary>
    public static IReadOnlySet<string> Intents { get; } = FrozenSet.Create(StringComparer.Ordinal, Principal, Self);
}

/// <summary>Who triggered a run (<c>ATTENDED</c>, <c>UNATTENDED</c>, <c>THIRD_PARTY</c>, <c>MODES</c>).</summary>
public static class RunModes
{
    /// <summary>The authenticated principal is in the conversation.</summary>
    public const string Attended = "attended";

    /// <summary>A schedule, or the principal's own source notifications.</summary>
    public const string Unattended = "unattended";

    /// <summary>Triggered by anyone else's message to Margo.</summary>
    public const string ThirdParty = "third_party";

    /// <summary><c>MODES</c>.</summary>
    public static IReadOnlySet<string> All { get; } = FrozenSet.Create(StringComparer.Ordinal, Attended, Unattended, ThirdParty);
}

/// <summary>How principal intent reaches the principal's data (<c>OBO</c>, <c>DELEGATION</c>, <c>PRINCIPAL_ACCESS</c>).</summary>
public static class PrincipalAccessModes
{
    /// <summary>Principal intent uses the principal's on-behalf-of token.</summary>
    public const string Obo = "obo";

    /// <summary>
    /// Fallback: Margo's token on the mailbox and calendar the principal delegated. Teams, files and Copilot
    /// search do not follow Exchange delegation, so those principal sources are reported unavailable.
    /// </summary>
    public const string Delegation = "delegation";

    /// <summary><c>PRINCIPAL_ACCESS</c>.</summary>
    public static IReadOnlySet<string> All { get; } = FrozenSet.Create(StringComparer.Ordinal, Obo, Delegation);
}

/// <summary>
/// The <c>status</c> values of a guard result. <see cref="Ok"/> is a result; every other status is a coverage gap
/// the model must report, never "nothing there".
/// </summary>
public static class GuardStatuses
{
    public const string Ok = "ok";
    public const string Partial = "partial";
    public const string Error = "error";
    public const string Unknown = "unknown";
    public const string Refused = "refused";
    public const string Unavailable = "unavailable";
    public const string Blocked = "blocked";
    public const string BudgetExhausted = "budget_exhausted";
}

using System.Text.Json.Nodes;

namespace Margo.Cloud.Core;

/// <summary>Provisioning actions. Python: <c>instances.ENSURE</c>, <c>SUSPEND</c>, <c>RETIRE</c>.</summary>
public static class ProvisioningActions
{
    public const string Ensure = "ensure";
    public const string Suspend = "suspend";
    public const string Retire = "retire";

    /// <summary>Every action, in Python's order.</summary>
    public static IReadOnlyList<string> All { get; } = [Ensure, Suspend, Retire];

    internal static bool IsKnown(string? action) => action is Ensure or Suspend or Retire;
}

/// <summary>The <c>event</c> values that change how a message is planned: those the router gives an
/// <c>ensure</c> it queues for an ordinary activity (not a lifecycle notification), which never starts a
/// suspended or retired instance, and the one the provisioner gives a retirement it resumes.</summary>
public static class ProvisioningEvents
{
    /// <summary>A retirement the provisioner queued or resumed for a route whose removal is pending
    /// (<see cref="Instances.IsRemovalPending"/>). It only finishes that removal: it never retires an
    /// instance that was started again since.</summary>
    public const string PendingRemoval = "pending-removal";

    /// <summary>An activity for an instance that has no route yet.</summary>
    public const string FirstActivity = "first-activity";

    /// <summary>An activity for an instance whose route is <c>failed</c>.</summary>
    public const string RetryAfterFailure = "retry-after-failure";

    /// <summary>Whether <paramref name="provisioningEvent"/> says the message was caused by an ordinary activity.</summary>
    public static bool IsActivity(string? provisioningEvent) => provisioningEvent is FirstActivity or RetryAfterFailure;
}

/// <summary>
/// One message on the provisioning queue: from the router (a lifecycle notification or a first
/// activity), or from an operator. Python passes these as dicts; see <see cref="ToJson"/> for the
/// exact text both implementations put on the queue.
/// </summary>
/// <param name="Action">One of <see cref="ProvisioningActions"/>; <see cref="Instances.CheckMessage(ProvisioningMessage)"/> validates it.</param>
/// <param name="AgentIdentityId">The agent application instance (<c>agenticAppId</c>).</param>
/// <param name="AgentUserId">The agent user (<c>agenticUserId</c>).</param>
/// <param name="Event">What caused the message: a lower-cased lifecycle event, <c>first-activity</c>,
/// <c>retry-after-failure</c>, <c>operator</c> or <c>pending-removal</c>. Omitted from the JSON when <see langword="null"/>.</param>
/// <param name="Redeploy">An ensure that redeploys even a ready instance. Written only when <see langword="true"/>.</param>
public sealed record ProvisioningMessage(string Action, string AgentIdentityId, string AgentUserId, string? Event = null, bool Redeploy = false)
{
    /// <summary>The message as Python's <c>json.dumps(message, sort_keys=True)</c> writes it for the queue.</summary>
    public string ToJson() => PythonJson.Dumps(ToJsonObject(), sortKeys: true);

    /// <summary>The message's fields, in Python's insertion order.</summary>
    public JsonObject ToJsonObject()
    {
        var obj = new JsonObject
        {
            ["action"] = Action,
            ["agent_identity_id"] = AgentIdentityId,
            ["agent_user_id"] = AgentUserId,
        };
        if (Event is not null)
        {
            obj["event"] = Event;
        }

        if (Redeploy)
        {
            obj["redeploy"] = true;
        }

        return obj;
    }
}

/// <summary>A validated provisioning message and the key of the instance it addresses.
/// Python: the dict <c>check_message</c> returns, <c>dict(message, key=key)</c>.</summary>
public sealed record CheckedMessage(string Key, ProvisioningMessage Message);

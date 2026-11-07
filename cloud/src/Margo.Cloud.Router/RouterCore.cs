using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Margo.Cloud.Core;

namespace Margo.Cloud.Router;

/// <summary>What <see cref="RouterCore.Decide"/> decided. Python: the dict <c>decide</c> returns.</summary>
/// <param name="Kind">One of <see cref="RouterDecisionKinds"/>.</param>
/// <param name="Status">The status to answer with when the activity is not forwarded.</param>
/// <param name="Key">The instance key, when the recipient forms one.</param>
/// <param name="Url">The instance's address, for <c>forward</c>.</param>
/// <param name="RetryAfter">Seconds for the <c>Retry-After</c> header, for <c>wait</c> and <c>provision</c>.</param>
/// <param name="Message">The provisioning message to queue, for <c>lifecycle</c> and <c>provision</c>.</param>
/// <param name="Reason">Why, for <c>ignore</c> and <c>reject</c>.</param>
public sealed record RouterDecision(
    string Kind,
    int Status,
    string? Key = null,
    string? Url = null,
    int? RetryAfter = null,
    ProvisioningMessage? Message = null,
    string? Reason = null);

/// <summary>The kinds of <see cref="RouterDecision"/>.</summary>
public static class RouterDecisionKinds
{
    public const string Lifecycle = "lifecycle";
    public const string Forward = "forward";
    public const string Provision = "provision";
    public const string Wait = "wait";
    public const string Ignore = "ignore";
    public const string Reject = "reject";
}

/// <summary>Where a voice request goes. Python: the dict <c>voice_target</c> returns.</summary>
/// <param name="Status">200 to forward; otherwise the status to answer with.</param>
/// <param name="Key">The instance key, when forwarded.</param>
/// <param name="Url">The instance's <c>/voice/...</c> address, when forwarded.</param>
/// <param name="RetryAfter">Seconds for the <c>Retry-After</c> header, with 503.</param>
public sealed record VoiceTarget(int Status, string? Key = null, string? Url = null, int? RetryAfter = null);

/// <summary>
/// Where one inbound request goes. Python: <c>margo_autopilot/router/core.py</c>.
/// </summary>
/// <remarks>
/// The router holds no Microsoft 365 token and no instance state. It validates the platform token,
/// then either forwards the request to the one container that serves the addressed instance, or asks
/// the provisioner to create it. Lifecycle notifications never reach an instance: they become
/// provisioning messages.
/// </remarks>
public static partial class RouterCore
{
    /// <summary>The largest activity body accepted, in bytes.</summary>
    public const int MaxActivityBytes = 1024 * 1024;

    /// <summary>Seconds a caller is asked to wait while an instance is being provisioned.</summary>
    public const int RetryAfterSeconds = 30;

    /// <summary>Decide what to do with one parsed activity: lifecycle, forward, provision, wait, ignore or reject.</summary>
    /// <exception cref="System.Text.EncoderFallbackException">A recipient identifier holds a lone
    /// surrogate (Python: UnicodeEncodeError from <c>instance_key</c>; the request fails with 500).</exception>
    /// <exception cref="InvalidOperationException">A ready route's <c>url</c> is truthy but not text
    /// (Python failed with a TypeError when it built the address; the request fails with 500).</exception>
    public static RouterDecision Decide(JsonNode? activity, RouteTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var message = Instances.LifecycleMessage(activity);
        if (message is not null)
        {
            return new RouterDecision(RouterDecisionKinds.Lifecycle, 200, Message: message);
        }

        var identity = Instances.ActivityIdentity(activity);
        if (identity is null)
        {
            return new RouterDecision(RouterDecisionKinds.Reject, 400, Reason: "activity has no agent recipient");
        }

        var (key, entry) = table.Resolve(identity.AgentIdentityId, identity.AgentUserId);
        if (key is null)
        {
            return new RouterDecision(RouterDecisionKinds.Reject, 400, Reason: "activity has an invalid agent recipient");
        }

        var state = entry?.State;
        if (state == RouteStates.Ready && TruthyUrl(entry!) is { } url)
        {
            return new RouterDecision(RouterDecisionKinds.Forward, 200, key, url);
        }

        if (state is RouteStates.Suspended or RouteStates.Retired)
        {
            // Acknowledge so the platform does not retry; nothing is delivered to a stopped instance.
            return new RouterDecision(RouterDecisionKinds.Ignore, 200, key, Reason: "instance is " + state);
        }

        if (state == RouteStates.Provisioning)
        {
            return new RouterDecision(RouterDecisionKinds.Wait, 503, key, RetryAfter: RetryAfterSeconds);
        }

        return new RouterDecision(RouterDecisionKinds.Provision, 503, key, RetryAfter: RetryAfterSeconds,
            Message: new ProvisioningMessage(ProvisioningActions.Ensure, identity.AgentIdentityId, identity.AgentUserId,
                entry is null ? "first-activity" : "retry-after-failure"));
    }

    /// <summary><c>/i/{key}/voice/...</c> to the instance's <c>/voice/...</c> address, or a status to return.</summary>
    /// <param name="path">The request path, percent-decoded (Python: aiohttp's <c>request.path</c>).</param>
    /// <param name="table">The route table.</param>
    /// <exception cref="InvalidOperationException">A ready route's <c>url</c> is truthy but not text
    /// (Python: AttributeError; the request fails with 500).</exception>
    public static VoiceTarget VoiceTargetFor(string? path, RouteTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var match = VoicePath().Match(path ?? string.Empty);
        if (!match.Success)
        {
            return new VoiceTarget(404);
        }

        var key = match.Groups[1].Value;
        var rest = match.Groups[2].Success ? match.Groups[2].Value : "/";
        if (rest.Contains("..", StringComparison.Ordinal) || rest.Contains("//", StringComparison.Ordinal))
        {
            return new VoiceTarget(404);
        }

        var entry = table.Get(key);
        if (entry is null || entry.State is RouteStates.Suspended or RouteStates.Retired)
        {
            return new VoiceTarget(404);
        }

        if (entry.State != RouteStates.Ready || TruthyUrl(entry) is not { } url)
        {
            return new VoiceTarget(503, RetryAfter: RetryAfterSeconds);
        }

        return new VoiceTarget(200, key, url.TrimEnd('/') + "/voice" + rest);
    }

    /// <summary>The entry's <c>url</c> when Python's <c>entry.get("url")</c> is truthy.</summary>
    private static string? TruthyUrl(RouteEntry entry)
    {
        var node = entry.ToJsonObject()["url"];
        if (!PythonCompat.IsTruthy(node))
        {
            return null;
        }

        return PythonCompat.AsString(node) ?? throw new InvalidOperationException("the route's url is not text");
    }

    /// <summary>Python: <c>^/i/([0-9a-f]{20})/voice(/[A-Za-z0-9._/-]*)?$</c>; .NET's <c>$</c> also
    /// matches before a final newline, as Python's does.</summary>
    [GeneratedRegex("^/i/([0-9a-f]{20})/voice(/[A-Za-z0-9._/-]*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex VoicePath();
}

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Margo.Cloud.Core;

namespace Margo.Cloud.Provisioner;

/// <summary>What the queue should do with a handled message. Python: the strings <c>"done"</c>,
/// <c>"retry"</c> and <c>"dropped"</c> that <c>Provisioner.handle</c> returns.</summary>
public enum HandleOutcome
{
    /// <summary>Every step succeeded (or there was nothing to do): delete the message.</summary>
    Done,

    /// <summary>A step failed: leave the message, so it reappears after its visibility timeout.</summary>
    Retry,

    /// <summary>The message is malformed, was refused, or failed for the last allowed time: delete it.
    /// A suspension and a retirement are never dropped.</summary>
    Dropped,

    /// <summary>A retirement whose removal keeps failing, recorded on the route: replace the message
    /// with <see cref="Provisioner.PendingRemovalText"/>, hidden for
    /// <see cref="Provisioner.RemovalRetrySeconds"/>, so the removal is tried again without any other
    /// message having to start the job.</summary>
    Postponed,
}

/// <summary>
/// The provisioner's decisions, without Azure: what to do with one queue message, step by step.
/// Python: <c>provision/core.py</c>.
/// </summary>
/// <remarks>
/// Each step is idempotent, so a message that fails part-way is safe to retry from the start. The
/// route table is the only record the provisioner writes; storage is never deleted by it. Not
/// thread-safe: the job handles one message at a time. Deliberately not in the Python job
/// (PORTING.md): a failed retirement stays <c>retired</c> and stays on the queue, a suspension
/// removes the app, an activity never starts a stopped instance, the number of instances is
/// capped, and a duplicate redeploy is skipped.
/// </remarks>
public sealed class Provisioner
{
    /// <summary>How many times a failing message is tried before it is dropped. Python: <c>MAX_ATTEMPTS</c>.</summary>
    public const int MaxAttempts = 5;

    /// <summary>The default for the most instances that may hold resources at once.</summary>
    public const int DefaultMaxInstances = 50;

    /// <summary>The route <c>error</c> of an <c>ensure</c> refused because <c>maxInstances</c> was reached.</summary>
    public const string MaxInstancesError = "MaxInstancesExceeded";

    /// <summary>The <c>event</c> of the retirement the job queues or resumes for a route whose removal is pending.</summary>
    public const string PendingRemovalEvent = ProvisioningEvents.PendingRemoval;

    /// <summary>How long the message that tries a pending removal again stays hidden. The job is started
    /// only by visible messages, so this message is what makes the retry happen in a deployment with
    /// no other traffic. It must stay longer than the job's run budget
    /// (<see cref="ProvisionerCommands.RunBudgetSeconds"/>), so a run never receives it twice, and far
    /// shorter than a queue message's seven days to live. One job run an hour for each instance whose
    /// removal is stuck, each logging an error, until the removal works.</summary>
    public const int RemovalRetrySeconds = 3600;

    /// <summary>How many times a route write is re-read and re-applied after a conflicting write.</summary>
    public const int RouteWriteAttempts = 5;

    /// <summary>The plan steps that only set the route's state, and the state each one sets.
    /// Python: <c>ROUTE_STATES</c>.</summary>
    public static IReadOnlyDictionary<string, string> RouteStateSteps { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [PlanSteps.RouteProvisioning] = RouteStates.Provisioning,
        [PlanSteps.RouteReady] = RouteStates.Ready,
        [PlanSteps.RouteSuspended] = RouteStates.Suspended,
        [PlanSteps.RouteRetired] = RouteStates.Retired,
    }.AsReadOnly();

    private readonly ICloud _cloud;
    private readonly JsonNode? _config;
    private readonly JsonNode? _template;
    private readonly Action<string> _log;
    private readonly Action<string> _error;
    private readonly TimeProvider? _time;
    private readonly int _maxInstances;
    private readonly HashSet<string> _removalsTried = new(StringComparer.Ordinal);

    /// <param name="cloud">Performs the Azure calls.</param>
    /// <param name="config">The provisioner configuration (<see cref="ProvisionerConfig.Load"/>). Kept as
    /// JSON, as Python keeps whatever <c>json.loads</c> returned: a value that is not an object fails
    /// each step that needs it, as in Python.</param>
    /// <param name="template">The ARM template for one instance (<c>cloud/infra/instance.json</c>).</param>
    /// <param name="log">Receives one line per decision; never identifiers, only instance keys.</param>
    /// <param name="timeProvider">The clock for route <c>updated_at</c>; the system clock by default.</param>
    /// <param name="maxInstances">The most instances that may hold resources at once; an <c>ensure</c>
    /// for one more is refused without deploying.</param>
    /// <param name="logError">Receives what an operator must act on (a refused or dropped message, a
    /// removal that keeps failing); <paramref name="log"/> when not given.</param>
    public Provisioner(ICloud cloud, JsonNode? config, JsonNode? template, Action<string>? log = null, TimeProvider? timeProvider = null,
        int maxInstances = DefaultMaxInstances, Action<string>? logError = null)
    {
        ArgumentNullException.ThrowIfNull(cloud);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxInstances, 1);
        _cloud = cloud;
        _config = config;
        _template = template;
        _log = log ?? (_ => { });
        _error = logError ?? _log;
        _time = timeProvider;
        _maxInstances = maxInstances;
    }

    /// <summary>
    /// Process one queue message's text. A malformed message is dropped. A failed <c>ensure</c> is
    /// recorded on the route as <c>failed</c> with the exception's type name and retried until
    /// <paramref name="dequeueCount"/> reaches <see cref="MaxAttempts"/>, then dropped. A failed
    /// retirement keeps the route <c>retired</c> (with the error), so the router never provisions the
    /// instance again; it is retried until <see cref="MaxAttempts"/>, then
    /// <see cref="HandleOutcome.Postponed"/> for as long as the removal fails. A failed suspension
    /// is never dropped.
    /// </summary>
    /// <remarks>Reading the route table happens before any step and is not guarded, as in Python: if
    /// the blob cannot be read or is not a valid route table, the exception propagates and the job
    /// stops with the message still hidden, so it reappears after its visibility timeout.</remarks>
    /// <param name="raw">The message text.</param>
    /// <param name="dequeueCount">How many times the message has been received, this time included.</param>
    /// <param name="enqueuedAt">When the message was put on the queue, if known: a redeploy of an
    /// instance that became ready after that is skipped (a duplicate, or already done).</param>
    /// <param name="cancellationToken">Stops the job.</param>
    public Task<HandleOutcome> HandleAsync(string? raw, long dequeueCount = 1, DateTimeOffset? enqueuedAt = null, CancellationToken cancellationToken = default)
    {
        CheckedMessage message;
        try
        {
            message = Instances.ParseMessage(raw!); // null: ArgumentNullException, dropped like Python's check_message(None)
        }
        catch (ArgumentException)
        {
            // Python: except (ValueError, RouteError). RouteException, BindingException and
            // EncoderFallbackException are all ArgumentExceptions here.
            return Malformed();
        }

        return HandleAsync(message, dequeueCount, enqueuedAt, queued: true, cancellationToken);
    }

    /// <summary>Process one message that is already JSON (Python's <c>handle</c> accepts a dict too).</summary>
    public Task<HandleOutcome> HandleAsync(JsonNode? raw, long dequeueCount = 1, DateTimeOffset? enqueuedAt = null, CancellationToken cancellationToken = default)
    {
        CheckedMessage message;
        try
        {
            message = Instances.CheckMessage(raw);
        }
        catch (ArgumentException)
        {
            return Malformed();
        }

        return HandleAsync(message, dequeueCount, enqueuedAt, queued: true, cancellationToken);
    }

    /// <summary>The message that replaces a <see cref="HandleOutcome.Postponed"/> retirement on the
    /// queue: the same instance, as a <see cref="PendingRemovalEvent"/> retirement, which only finishes
    /// a pending removal (<see cref="Instances.IsStalePendingRemoval"/>). The original text is not
    /// queued again: by the time it returned, a lifecycle event or an operator may have started the
    /// instance again, and a plain retirement would remove it.</summary>
    /// <exception cref="RouteException">The text is not a valid provisioning message.</exception>
    public static string PendingRemovalText(string raw)
    {
        var message = Instances.ParseMessage(raw).Message;
        return new ProvisioningMessage(ProvisioningActions.Retire, message.AgentIdentityId, message.AgentUserId, PendingRemovalEvent).ToJson();
    }

    /// <summary>
    /// Retire again every instance whose route is <c>retired</c> with an error (its app or identity
    /// may still exist), except those this run already tried. Called once per job run, after the
    /// queue is drained: it covers a removal whose message is hidden for now, or was lost (dropped by
    /// an older version, expired, or its replacement could not be queued). It queues nothing itself:
    /// it cannot tell whether a message for the instance is waiting, and a second one for every run
    /// would multiply them.
    /// </summary>
    /// <returns>How many removals were tried.</returns>
    public async Task<int> ResumePendingRemovalsAsync(CancellationToken cancellationToken = default)
    {
        var tried = 0;
        foreach (var (key, entry) in (await TableAsync(cancellationToken).ConfigureAwait(false)).Routes)
        {
            if (!Instances.IsRemovalPending(entry) || !_removalsTried.Add(key))
            {
                continue;
            }

            CheckedMessage? message = null;
            try
            {
                message = Instances.CheckMessage(new ProvisioningMessage(ProvisioningActions.Retire, entry.AgentIdentityId!, entry.AgentUserId!, PendingRemovalEvent));
            }
            catch (ArgumentException)
            {
                // Reported below: the identifiers cannot form a key.
            }

            if (message?.Key != key)
            {
                _error("instance " + key + ": removal is pending but the route's identifiers do not give its key; remove the app and identity by hand");
                continue;
            }

            tried++;
            await HandleAsync(message, MaxAttempts, null, queued: false, cancellationToken).ConfigureAwait(false);
        }

        return tried;
    }

    private Task<HandleOutcome> Malformed()
    {
        _log("dropped malformed provisioning message");
        return Task.FromResult(HandleOutcome.Dropped);
    }

    /// <param name="queued">The message is on the queue (not a removal this run resumed from the route
    /// table), so it can be kept there.</param>
    private async Task<HandleOutcome> HandleAsync(CheckedMessage message, long dequeueCount, DateTimeOffset? enqueuedAt, bool queued, CancellationToken cancellationToken)
    {
        var key = message.Key;
        var action = message.Message.Action;
        var table = await TableAsync(cancellationToken).ConfigureAwait(false);
        var current = table.Get(key);
        if (Instances.IsStoppedForActivity(message.Message, current))
        {
            // Only a lifecycle notification or an operator starts a stopped instance again.
            _log("instance " + key + ": ensure for an activity ignored: instance is " + current!.State);
            return HandleOutcome.Done;
        }

        if (action == ProvisioningActions.Ensure && message.Message.Redeploy && current?.State == RouteStates.Ready
            && enqueuedAt is { } requested && UpdatedAfter(current, requested))
        {
            // The message came back after its visibility timeout, or a later deployment already did the work.
            _log("instance " + key + ": redeploy skipped: deployed after it was requested");
            return HandleOutcome.Done;
        }

        var steps = Instances.Plan(message.Message, current, retried: dequeueCount > 1);
        _log("instance " + key + ": " + action + " -> " + (steps.Count > 0 ? PythonListRepr(steps) : "nothing to do"));
        if (steps.Contains(PlanSteps.Deploy) && !HoldsResources(current)
            && table.Routes.Count(route => route.Key != key && HoldsResources(route.Value)) >= _maxInstances)
        {
            return await RefuseAsync(key, message, current, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            foreach (var step in steps)
            {
                await StepAsync(step, key, message, cancellationToken).ConfigureAwait(false);
            }

            return HandleOutcome.Done;
        }
        catch (Exception exc) when (!IsCancellation(exc, cancellationToken))
        {
            var name = exc.GetType().Name;
            _log("instance " + key + ": step failed: " + name);
            var last = dequeueCount >= MaxAttempts;
            if (action == ProvisioningActions.Ensure)
            {
                // The message reappears; after MaxAttempts the route stays failed (the router asks again on the next activity).
                await RecordAsync(key, message, RouteStates.Failed, name, cancellationToken).ConfigureAwait(false);
                if (last)
                {
                    _error("instance " + key + ": ensure failed " + dequeueCount + " time(s) and was dropped; the route stays failed (" + name + ")");
                }

                return last ? HandleOutcome.Dropped : HandleOutcome.Retry;
            }

            if (action == ProvisioningActions.Suspend)
            {
                // Nothing to record: the route is suspended already, or could not be written. Both steps are
                // idempotent and the message is never dropped (it returns after its visibility timeout).
                if (last)
                {
                    _error("instance " + key + ": suspend still failing after " + dequeueCount + " attempt(s) (" + name + "); it stays on the queue");
                }

                return HandleOutcome.Retry;
            }

            // A retirement stays a retirement: the router ignores a retired route, whatever is left to remove.
            _removalsTried.Add(key);
            var recorded = await RecordAsync(key, message, RouteStates.Retired, name, cancellationToken).ConfigureAwait(false);
            if (!recorded)
            {
                // The route may still say ready: the message returns after its visibility timeout, however often it failed.
                if (last)
                {
                    _error("instance " + key + ": retire still failing after " + dequeueCount + " attempt(s) (" + name + "); it could not be recorded and stays on the queue");
                }

                return HandleOutcome.Retry;
            }

            if (!queued)
            {
                _error("instance " + key + ": pending removal still failing (" + name + "); the route stays retired. A later run tries again; "
                    + "if no retire message for it is waiting on the queue, nothing starts that run: queue one with 'enqueue retire'");
                return HandleOutcome.Retry;
            }

            // A message that already is the hourly retry is not also retried every visibility timeout.
            var resumed = message.Message.Event == PendingRemovalEvent;
            if (!last && !resumed)
            {
                return HandleOutcome.Retry;
            }

            // Never dropped: the job is started only by queue messages, so without one nothing would try the removal
            // again while the app and the identity (which holds the agent's Microsoft 365 access) still exist. The
            // removal steps are idempotent (an app or identity that is gone is not an error), so repeating them is safe.
            _error("instance " + key + ": " + (resumed ? "pending removal still failing" : "retire still failing after " + dequeueCount + " attempt(s)")
                + " (" + name + "); the route stays retired and the removal is tried again within " + (RemovalRetrySeconds / 60) + " minute(s)");
            return HandleOutcome.Postponed;
        }
    }

    /// <summary>Refuse an <c>ensure</c> that would exceed <c>maxInstances</c>: nothing is deployed, the
    /// route is marked failed once, and the message is dropped (the router asks again on the next
    /// activity, and is refused again).</summary>
    private async Task<HandleOutcome> RefuseAsync(string key, CheckedMessage message, RouteEntry? current, CancellationToken cancellationToken)
    {
        _error("instance " + key + ": ensure refused: " + _maxInstances + " instance(s) already exist (MARGO_MAX_INSTANCES); nothing was deployed");
        if (!(current?.State == RouteStates.Failed && current.Error == MaxInstancesError))
        {
            await RecordAsync(key, message, RouteStates.Failed, MaxInstancesError, cancellationToken).ConfigureAwait(false);
        }

        return HandleOutcome.Dropped;
    }

    /// <summary>Whether the route's instance has, or may still have, an app and identity: every state
    /// except a completed retirement and a refused <c>ensure</c>.</summary>
    private static bool HoldsResources(RouteEntry? entry) =>
        entry is not null
        && !(entry.State == RouteStates.Retired && !Instances.IsRemovalPending(entry))
        && !(entry.State == RouteStates.Failed && entry.Error == MaxInstancesError);

    /// <summary>Whether the route changed after <paramref name="moment"/> (both to the second).</summary>
    private static bool UpdatedAfter(RouteEntry entry, DateTimeOffset moment) =>
        DateTimeOffset.TryParse(entry.UpdatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var updated) && updated > moment;

    /// <summary>Record a failure on the route; <see langword="false"/> when even that failed.</summary>
    private async Task<bool> RecordAsync(string key, CheckedMessage message, string state, string error, CancellationToken cancellationToken)
    {
        try
        {
            await SetRouteAsync(key, message, state, error, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception recordFailure) when (!IsCancellation(recordFailure, cancellationToken))
        {
            _log("instance " + key + ": could not record the failure");
            return false;
        }
    }

    private async Task<RouteTable> TableAsync(CancellationToken cancellationToken)
    {
        var blob = await _cloud.LoadRoutesAsync(cancellationToken).ConfigureAwait(false);
        return RouteTable.Loads(blob.Text, _time);
    }

    private async Task StepAsync(string step, string key, CheckedMessage message, CancellationToken cancellationToken)
    {
        var names = Instances.ResourceNames(key);
        if (RouteStateSteps.TryGetValue(step, out var state))
        {
            await SetRouteAsync(key, message, state, null, cancellationToken).ConfigureAwait(false);
        }
        else if (step == PlanSteps.Deploy)
        {
            var parameters = Instances.DeploymentParameters(key, message.Message, ConfigObject());
            await _cloud.DeployAsync("margo-instance-" + key, _template, parameters, cancellationToken).ConfigureAwait(false);
        }
        else if (step == PlanSteps.DeleteApp)
        {
            await _cloud.DeleteAppAsync(names.App, cancellationToken).ConfigureAwait(false);
        }
        else if (step == PlanSteps.DeleteIdentity)
        {
            await _cloud.DeleteIdentityAsync(names.Identity, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new RouteException("unknown step " + step);
        }
    }

    private async Task SetRouteAsync(string key, CheckedMessage message, string state, string? error, CancellationToken cancellationToken)
    {
        var url = Instances.InternalUrl(key, EnvironmentDomain());
        for (var attempt = 0; attempt < RouteWriteAttempts; attempt++)
        {
            // Optimistic concurrency: re-read and re-apply on a conflicting write.
            var blob = await _cloud.LoadRoutesAsync(cancellationToken).ConfigureAwait(false);
            var table = RouteTable.Loads(blob.Text, _time);
            table.Put(key, message.Message.AgentIdentityId, message.Message.AgentUserId, state, url, error);
            if (await _cloud.SaveRoutesAsync(table.Dumps(), blob.ETag, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }

        throw new RouteException("route table kept changing; try again");
    }

    /// <summary>Python's <c>self.config["environmentDomain"]</c>: a JSON string, or <see langword="null"/>
    /// for any other value (which <see cref="Instances.InternalUrl"/> then refuses, as in Python).</summary>
    private string? EnvironmentDomain()
    {
        if (!ConfigObject().TryGetPropertyValue("environmentDomain", out var domain))
        {
            throw new KeyNotFoundException("environmentDomain"); // Python: KeyError
        }

        return domain is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
    }

    private JsonObject ConfigObject() =>
        _config as JsonObject
        ?? throw new InvalidOperationException("provisioner configuration is not a JSON object"); // Python: TypeError

    private static bool IsCancellation(Exception exc, CancellationToken cancellationToken) =>
        exc is OperationCanceledException && cancellationToken.IsCancellationRequested;

    /// <summary>Python's <c>str()</c> of a list of plan step names, e.g. <c>['route_suspended']</c>.</summary>
    internal static string PythonListRepr(IEnumerable<string> items) =>
        "[" + string.Join(", ", items.Select(item => "'" + item + "'")) + "]";
}

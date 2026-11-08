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

    /// <summary>The message is malformed, or failed for the last allowed time: delete it.</summary>
    Dropped,
}

/// <summary>
/// The provisioner's decisions, without Azure: what to do with one queue message, step by step.
/// Python: <c>provision/core.py</c>.
/// </summary>
/// <remarks>
/// Each step is idempotent, so a message that fails part-way is safe to retry from the start. The
/// route table is the only record the provisioner writes; storage is never deleted by it. Not
/// thread-safe: the job handles one message at a time.
/// </remarks>
public sealed class Provisioner
{
    /// <summary>How many times a failing message is tried before it is dropped. Python: <c>MAX_ATTEMPTS</c>.</summary>
    public const int MaxAttempts = 5;

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
    private readonly TimeProvider? _time;

    /// <param name="cloud">Performs the Azure calls.</param>
    /// <param name="config">The provisioner configuration (<see cref="ProvisionerConfig.Load"/>). Kept as
    /// JSON, as Python keeps whatever <c>json.loads</c> returned: a value that is not an object fails
    /// each step that needs it, as in Python.</param>
    /// <param name="template">The ARM template for one instance (<c>cloud/infra/instance.json</c>).</param>
    /// <param name="log">Receives one line per decision; never identifiers, only instance keys.</param>
    /// <param name="timeProvider">The clock for route <c>updated_at</c>; the system clock by default.</param>
    public Provisioner(ICloud cloud, JsonNode? config, JsonNode? template, Action<string>? log = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(cloud);
        _cloud = cloud;
        _config = config;
        _template = template;
        _log = log ?? (_ => { });
        _time = timeProvider;
    }

    /// <summary>
    /// Process one queue message's text. A malformed message is dropped; a failed step is recorded
    /// on the route as <c>failed</c> with the exception's type name, and the message is retried until
    /// <paramref name="dequeueCount"/> reaches <see cref="MaxAttempts"/>, then dropped.
    /// </summary>
    /// <remarks>Reading the route table happens before any step and is not guarded, as in Python: if
    /// the blob cannot be read or is not a valid route table, the exception propagates and the job
    /// stops with the message still hidden, so it reappears after its visibility timeout.</remarks>
    public Task<HandleOutcome> HandleAsync(string? raw, long dequeueCount = 1, CancellationToken cancellationToken = default)
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

        return HandleAsync(message, dequeueCount, cancellationToken);
    }

    /// <summary>Process one message that is already JSON (Python's <c>handle</c> accepts a dict too).</summary>
    public Task<HandleOutcome> HandleAsync(JsonNode? raw, long dequeueCount = 1, CancellationToken cancellationToken = default)
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

        return HandleAsync(message, dequeueCount, cancellationToken);
    }

    private Task<HandleOutcome> Malformed()
    {
        _log("dropped malformed provisioning message");
        return Task.FromResult(HandleOutcome.Dropped);
    }

    private async Task<HandleOutcome> HandleAsync(CheckedMessage message, long dequeueCount, CancellationToken cancellationToken)
    {
        var key = message.Key;
        var steps = Instances.Plan(message.Message, await RouteAsync(key, cancellationToken).ConfigureAwait(false));
        _log("instance " + key + ": " + message.Message.Action + " -> " + (steps.Count > 0 ? PythonListRepr(steps) : "nothing to do"));
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
            // The message reappears; after MaxAttempts the route stays failed.
            var name = exc.GetType().Name;
            _log("instance " + key + ": step failed: " + name);
            try
            {
                await SetRouteAsync(key, message, RouteStates.Failed, name, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception recordFailure) when (!IsCancellation(recordFailure, cancellationToken))
            {
                _log("instance " + key + ": could not record the failure");
            }

            return dequeueCount >= MaxAttempts ? HandleOutcome.Dropped : HandleOutcome.Retry;
        }
    }

    private async Task<RouteEntry?> RouteAsync(string key, CancellationToken cancellationToken)
    {
        var blob = await _cloud.LoadRoutesAsync(cancellationToken).ConfigureAwait(false);
        return RouteTable.Loads(blob.Text, _time).Get(key);
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

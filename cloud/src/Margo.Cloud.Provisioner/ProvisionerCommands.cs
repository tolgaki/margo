using System.Text.Json.Nodes;
using Margo.Cloud.Core;

namespace Margo.Cloud.Provisioner;

/// <summary>
/// The provisioner job: drain the queue, create or retire instances, keep the route table.
/// Python: <c>provision/azure.py</c> (<c>run</c>, <c>enqueue</c>, <c>redeploy_all</c>,
/// <c>main</c>) and <c>__main__.py</c>.
/// </summary>
/// <remarks>
/// Runs as a scheduled Container Apps job with its own identity, which may deploy into one resource
/// group and assign the four roles an instance needs. It never reads any instance's state.
/// <code>
/// Margo.Cloud.Provisioner run
/// Margo.Cloud.Provisioner redeploy-all
/// Margo.Cloud.Provisioner enqueue ensure|suspend|retire AGENT_IDENTITY_ID AGENT_USER_ID [--redeploy]
/// </code>
/// </remarks>
public sealed class ProvisionerCommands
{
    /// <summary>How long a received message stays hidden while it is handled. Python: <c>VISIBILITY_SECONDS</c>.</summary>
    public const int VisibilitySeconds = 900;

    /// <summary>Messages received per queue request. Python: <c>messages_per_page=16</c>.</summary>
    public const int MessagesPerPage = 16;

    /// <summary>The <c>event</c> of every message an operator command puts on the queue.</summary>
    public const string OperatorEvent = "operator";

    /// <summary>The three usage lines, printed to standard error with exit code 2 for any other command line.</summary>
    public const string Usage =
        "Margo.Cloud.Provisioner run\n"
        + "Margo.Cloud.Provisioner redeploy-all\n"
        + "Margo.Cloud.Provisioner enqueue ensure|suspend|retire AGENT_IDENTITY_ID AGENT_USER_ID [--redeploy]";

    private readonly Func<string, string?> _environment;
    private readonly Func<IProvisionerServices> _services;
    private readonly TextWriter _stderr;
    private readonly ProvisionerLog _log;
    private readonly TimeProvider? _time;

    /// <param name="environment">Reads an environment variable; <see langword="null"/> when unset.</param>
    /// <param name="services">Creates the credential and Azure clients; called where Python calls
    /// <c>_credential()</c>, once per command.</param>
    /// <param name="stderr">Receives the log lines, the usage text and uncaught errors.</param>
    /// <param name="timeProvider">The clock for log lines and route <c>updated_at</c>.</param>
    public ProvisionerCommands(Func<string, string?> environment, Func<IProvisionerServices> services, TextWriter stderr, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(stderr);
        _environment = environment;
        _services = services;
        _stderr = stderr;
        _time = timeProvider;
        _log = new ProvisionerLog(stderr, timeProvider);
    }

    /// <summary>
    /// The process entry point: <see cref="MainAsync"/>, with an uncaught exception written to
    /// standard error and exit code 1, as Python's interpreter does for an uncaught exception.
    /// </summary>
    public async Task<int> InvokeAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        try
        {
            return await MainAsync(args, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exc)
        {
            await _stderr.WriteAsync(exc + "\n").ConfigureAwait(false);
            await _stderr.FlushAsync(CancellationToken.None).ConfigureAwait(false); // report even when cancelled
            return 1;
        }
    }

    /// <summary>Dispatch a command line exactly as Python's <c>main</c> does: <c>run</c> and
    /// <c>redeploy-all</c> ignore anything after them; <c>enqueue</c> takes an action, two
    /// identifiers and an optional <c>--redeploy</c>. Anything else prints <see cref="Usage"/> and
    /// returns 2.</summary>
    public Task<int> MainAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count >= 1 && args[0] == "run")
        {
            return RunAsync(cancellationToken);
        }

        if (args.Count >= 1 && args[0] == "redeploy-all")
        {
            return RedeployAllAsync(cancellationToken);
        }

        if (args.Count is 4 or 5 && args[0] == "enqueue" && ProvisioningActions.All.Contains(args[1])
            && (args.Count == 4 || args[4] == "--redeploy"))
        {
            return EnqueueAsync(args[1], args[2], args[3], args.Count == 5, cancellationToken);
        }

        return UsageAsync();
    }

    private async Task<int> UsageAsync()
    {
        await _stderr.WriteAsync(Usage + "\n").ConfigureAwait(false);
        await _stderr.FlushAsync().ConfigureAwait(false);
        return 2;
    }

    /// <summary>
    /// Drain the provisioning queue: receive pages of up to <see cref="MessagesPerPage"/> messages,
    /// each hidden for <see cref="VisibilitySeconds"/>, until a page comes back empty. A message that
    /// is done or dropped is deleted; one to retry is left to reappear after its visibility timeout,
    /// and is dropped once its dequeue count reaches <see cref="Provisioner.MaxAttempts"/>.
    /// </summary>
    /// <returns>0. Anything that is not a step failure (a missing setting, an unreadable template or
    /// route table, a queue error) throws, as in Python.</returns>
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        var services = _services();
        var template = ProvisionerConfig.LoadTemplate(_environment);
        var cloud = services.CreateCloud(
            Require(ProvisionerConfig.SubscriptionVariable),
            Require(ProvisionerConfig.ResourceGroupVariable),
            Require(ProvisionerConfig.RoutesUrlVariable));
        var provisioner = new Provisioner(cloud, ProvisionerConfig.Load(_environment), template, _log.Info, _time);
        var queue = services.CreateQueue(Require(ProvisionerConfig.QueueUrlVariable));
        var handled = 0;
        while (true)
        {
            var page = await queue.ReceiveAsync(MessagesPerPage, TimeSpan.FromSeconds(VisibilitySeconds), cancellationToken).ConfigureAwait(false);
            if (page.Count == 0)
            {
                break;
            }

            foreach (var message in page)
            {
                var dequeueCount = message.DequeueCount != 0 ? message.DequeueCount : 1; // Python: dequeue_count or 1
                var outcome = await provisioner.HandleAsync(message.Text, dequeueCount, cancellationToken).ConfigureAwait(false);
                if (outcome is HandleOutcome.Done or HandleOutcome.Dropped)
                {
                    await queue.DeleteAsync(message, cancellationToken).ConfigureAwait(false);
                }

                handled++;
            }
        }

        _log.Info("processed " + handled + " message(s)");
        return 0;
    }

    /// <summary>Queue one operator message. <paramref name="redeploy"/> makes an ensure redeploy even
    /// a ready instance (a new image or voice).</summary>
    /// <exception cref="RouteException">The identifiers cannot form an instance key; nothing is queued.</exception>
    public async Task<int> EnqueueAsync(string action, string agentIdentityId, string agentUserId, bool redeploy = false, CancellationToken cancellationToken = default)
    {
        var message = new ProvisioningMessage(action, agentIdentityId, agentUserId, OperatorEvent, redeploy);
        Instances.CheckMessage(message);
        var queue = _services().CreateQueue(Require(ProvisionerConfig.QueueUrlVariable));
        await queue.SendAsync(message.ToJson(), cancellationToken).ConfigureAwait(false);
        _log.Info("queued " + action);
        return 0;
    }

    /// <summary>Queue a redeploy for every ready instance, after the image or voice settings change.</summary>
    /// <remarks>As in Python, the identifiers are copied from the route entry without validation, and
    /// an entry without one stops the command (messages already queued stay queued).</remarks>
    public async Task<int> RedeployAllAsync(CancellationToken cancellationToken = default)
    {
        var services = _services();
        var cloud = services.CreateCloud(
            Require(ProvisionerConfig.SubscriptionVariable),
            Require(ProvisionerConfig.ResourceGroupVariable),
            Require(ProvisionerConfig.RoutesUrlVariable));
        var queue = services.CreateQueue(Require(ProvisionerConfig.QueueUrlVariable));
        var count = 0;
        var routes = await cloud.LoadRoutesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var (_, entry) in RouteTable.Loads(routes.Text, _time).Routes)
        {
            if (entry.State == RouteStates.Ready)
            {
                var fields = entry.ToJsonObject();
                var message = new JsonObject
                {
                    ["action"] = ProvisioningActions.Ensure,
                    ["agent_identity_id"] = Field(fields, "agent_identity_id"),
                    ["agent_user_id"] = Field(fields, "agent_user_id"),
                    ["event"] = OperatorEvent,
                    ["redeploy"] = true,
                };
                await queue.SendAsync(PythonJson.Dumps(message, sortKeys: true), cancellationToken).ConfigureAwait(false);
                count++;
            }
        }

        _log.Info("queued " + count + " redeploy(s)");
        return 0;
    }

    private string Require(string name) => ProvisionerConfig.Require(_environment, name);

    /// <summary>Python's <c>entry[name]</c>: the raw value, whatever its JSON type.</summary>
    private static JsonNode? Field(JsonObject entry, string name) =>
        entry.TryGetPropertyValue(name, out var value) ? value?.DeepClone() : throw new KeyNotFoundException(name);
}

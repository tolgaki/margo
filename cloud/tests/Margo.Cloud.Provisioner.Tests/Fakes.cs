using System.Text.Json.Nodes;
using Margo.Cloud.Core;

namespace Margo.Cloud.Provisioner.Tests;

/// <summary>Fictional identities and configuration shared by the tests (tests/test_autopilot_instances.py).</summary>
internal static class Fixtures
{
    public const string Identity = "agent-identity-1";
    public const string User = "margo-user-1";
    public const string Domain = "calmforest-1a2b3c.example.com";

    /// <summary>instance_key(IDENTITY, USER) as computed by the Python implementation.</summary>
    public const string Key = "8f6b44ff6f8dd73b5b3a";

    public static readonly DateTimeOffset Now = new(2026, 11, 7, 9, 30, 0, TimeSpan.Zero);

    /// <summary>CONFIG from the Python tests: every required name, a domain and one replica setting.</summary>
    public static JsonObject Config()
    {
        var config = new JsonObject();
        foreach (var name in Instances.RequiredConfig)
        {
            config[name] = "fictional-" + name.ToLowerInvariant();
        }

        config["environmentDomain"] = Domain;
        config["minReplicas"] = 0;
        return config;
    }

    /// <summary>Python's <c>json.dumps({"action": ..., "agent_identity_id": ..., "agent_user_id": ...})</c>.</summary>
    public static string Message(string action, string identity = Identity, string user = User, bool redeploy = false) =>
        "{\"action\": \"" + action + "\", \"agent_identity_id\": \"" + identity + "\", \"agent_user_id\": \"" + user + "\""
        + (redeploy ? ", \"redeploy\": true" : string.Empty) + "}";

    public static string Lf(string text) => text.ReplaceLineEndings("\n");
}

/// <summary>A clock tests can move; local time is UTC so log lines are predictable.</summary>
internal sealed class FakeClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>The Python tests' <c>FakeCloud</c>: records calls, keeps the route blob with an ETag, can
/// fail deployments and simulate conflicting writers.</summary>
internal sealed class FakeCloud(string? fail = null) : ICloud
{
    public string Text { get; set; } = string.Empty;

    public string? ETag { get; set; }

    public List<(string Call, string Name)> Calls { get; } = [];

    /// <summary>The next save calls that lose to another writer.</summary>
    public int Conflicts { get; set; }

    public int Loads { get; private set; }

    public List<(JsonNode? Template, JsonObject Parameters)> Deployments { get; } = [];

    /// <summary>Thrown by <see cref="LoadRoutesAsync"/> when set.</summary>
    public Exception? LoadFailure { get; set; }

    public Task DeployAsync(string name, JsonNode? template, JsonObject parameters, CancellationToken cancellationToken)
    {
        Calls.Add(("deploy", name));
        Deployments.Add((template, parameters));
        if (fail == "deploy")
        {
            throw new InvalidOperationException("deployment failed");
        }

        return Task.CompletedTask;
    }

    public Task DeleteAppAsync(string name, CancellationToken cancellationToken)
    {
        Calls.Add(("delete_app", name));
        return fail == "delete_app" ? throw new TimeoutException("delete timed out") : Task.CompletedTask;
    }

    public Task DeleteIdentityAsync(string name, CancellationToken cancellationToken)
    {
        Calls.Add(("delete_identity", name));
        return Task.CompletedTask;
    }

    public Task<RouteBlob> LoadRoutesAsync(CancellationToken cancellationToken)
    {
        Loads++;
        return LoadFailure is not null ? Task.FromException<RouteBlob>(LoadFailure) : Task.FromResult(new RouteBlob(Text, ETag));
    }

    public Task<bool> SaveRoutesAsync(string text, string? etag, CancellationToken cancellationToken)
    {
        if (Conflicts > 0)
        {
            Conflicts--;
            ETag = "etag-changed-" + Conflicts;
            return Task.FromResult(false);
        }

        if (etag != ETag)
        {
            return Task.FromResult(false);
        }

        Text = text;
        ETag = "etag-" + (Calls.Count + text.Length);
        return Task.FromResult(true);
    }

    public RouteEntry? Route(string key = Fixtures.Key) => RouteTable.Loads(Text).Get(key);
}

/// <summary>An in-memory queue with Azure Queue Storage's visibility and dequeue-count rules.</summary>
internal sealed class FakeQueue(FakeClock clock) : IProvisioningQueueClient
{
    private sealed class Item(string id, string text)
    {
        public string Id { get; } = id;

        public string Text { get; } = text;

        public long DequeueCount { get; set; }

        public DateTimeOffset VisibleAt { get; set; }

        public string? Receipt { get; set; }
    }

    private readonly List<Item> _items = [];
    private int _next;

    public List<(int MaxMessages, TimeSpan Visibility)> Receives { get; } = [];

    public List<string> Deleted { get; } = [];

    public List<string> Sent { get; } = [];

    public int Count => _items.Count;

    /// <summary>Put a message on the queue, as a sender would; returns its id.</summary>
    public string Add(string text)
    {
        var id = "m" + _next++;
        _items.Add(new Item(id, text) { VisibleAt = clock.Now });
        return id;
    }

    public long DequeueCount(string id) => _items.Single(item => item.Id == id).DequeueCount;

    public bool Contains(string id) => _items.Any(item => item.Id == id);

    public Task<IReadOnlyList<QueuedMessage>> ReceiveAsync(int maxMessages, TimeSpan visibilityTimeout, CancellationToken cancellationToken)
    {
        Receives.Add((maxMessages, visibilityTimeout));
        var page = new List<QueuedMessage>();
        foreach (var item in _items.Where(item => item.VisibleAt <= clock.Now).Take(maxMessages))
        {
            item.DequeueCount++;
            item.VisibleAt = clock.Now + visibilityTimeout;
            item.Receipt = "receipt-" + item.Id + "-" + item.DequeueCount;
            page.Add(new QueuedMessage(item.Id, item.Receipt, item.Text, item.DequeueCount));
        }

        return Task.FromResult<IReadOnlyList<QueuedMessage>>(page);
    }

    public Task DeleteAsync(QueuedMessage message, CancellationToken cancellationToken)
    {
        var item = _items.SingleOrDefault(item => item.Id == message.MessageId && item.Receipt == message.PopReceipt)
            ?? throw new InvalidOperationException("message not found or pop receipt changed");
        _items.Remove(item);
        Deleted.Add(message.MessageId);
        return Task.CompletedTask;
    }

    public Task SendAsync(string text, CancellationToken cancellationToken)
    {
        Sent.Add(text);
        Add(text);
        return Task.CompletedTask;
    }
}

/// <summary>Hands out one fake cloud and queue, and records what the commands asked for.</summary>
internal sealed class FakeServices(FakeCloud cloud, FakeQueue queue) : IProvisionerServices
{
    public List<string> Created { get; } = [];

    public ICloud CreateCloud(string subscriptionId, string resourceGroup, string routesUrl)
    {
        Created.Add("cloud " + subscriptionId + " " + resourceGroup + " " + routesUrl);
        return cloud;
    }

    public IProvisioningQueueClient CreateQueue(string queueUrl)
    {
        Created.Add("queue " + queueUrl);
        return queue;
    }
}

internal static class RepoRoot
{
    /// <summary>The repository root: the first directory above the test binaries that holds the instance template.</summary>
    public static string Find()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "autopilot", "infra", "instance.json")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("repository root not found above " + AppContext.BaseDirectory);
    }
}

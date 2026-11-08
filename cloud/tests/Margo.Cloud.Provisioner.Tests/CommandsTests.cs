using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Margo.Cloud.Core;
using Xunit;
using static Margo.Cloud.Provisioner.Tests.Fixtures;

namespace Margo.Cloud.Provisioner.Tests;

/// <summary>
/// provision/azure.py <c>main</c>, <c>run</c>, <c>enqueue</c>, <c>redeploy_all</c> and <c>load_config</c>
/// with a fake cloud and queue. Expected values marked "Python" were recorded by running those
/// functions on Python 3.13 with the SDK clients replaced by equivalent fakes.
/// </summary>
public sealed partial class CommandsTests : IDisposable
{
    private readonly FakeClock _clock = new(Now);
    private readonly FakeCloud _cloud = new();
    private readonly FakeQueue _queue;
    private readonly FakeServices _services;
    private readonly StringWriter _stderr = new();
    private readonly Dictionary<string, string> _environment = new(StringComparer.Ordinal);
    private int _servicesCreated;

    public CommandsTests()
    {
        _queue = new FakeQueue(_clock);
        _services = new FakeServices(_cloud, _queue);
    }

    public void Dispose() => _stderr.Dispose();

    private void SetJobEnvironment()
    {
        _environment["MARGO_PROVISIONER_CONFIG"] = Config().ToJsonString();
        _environment["AZURE_SUBSCRIPTION_ID"] = "fictional-subscription";
        _environment["AZURE_RESOURCE_GROUP"] = "rg-margo-fictional";
        _environment["MARGO_ROUTES_URL"] = "https://fictional.blob.example.com/margo-routes/routes.json";
        _environment["MARGO_PROVISION_QUEUE_URL"] = "https://fictional.queue.example.com/margo-provision";
    }

    private ProvisionerCommands Commands() =>
        new(name => _environment.GetValueOrDefault(name), () =>
        {
            _servicesCreated++;
            return _services;
        }, _stderr, _clock);

    private Task<int> Invoke(params string[] args) => Commands().InvokeAsync(args, TestContext.Current.CancellationToken);

    /// <summary>The messages of the log lines written so far; every line must be in Python's format.</summary>
    private string[] LogMessages() =>
        Lf(_stderr.ToString()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => LogLine().Match(line) is { Success: true } match
                ? match.Groups[1].Value
                : throw new InvalidOperationException("not a log line: " + line))
            .ToArray();

    [GeneratedRegex(@"^2026-11-07 \d\d:\d\d:\d\d,000 INFO margo\.provision \| (.*)$")]
    private static partial Regex LogLine();

    [Theory]
    [InlineData]
    [InlineData("Run")]
    [InlineData("enqueue")]
    [InlineData("enqueue", "ensure", "agent-identity-1")]
    [InlineData("enqueue", "explode", "agent-identity-1", "margo-user-1")]
    [InlineData("enqueue", "ensure", "agent-identity-1", "margo-user-1", "--force")]
    [InlineData("enqueue", "ensure", "agent-identity-1", "margo-user-1", "--redeploy", "extra")]
    [InlineData("redeploy_all")]
    [InlineData("--help")]
    public async Task Anything_else_prints_usage_and_exits_2(params string[] args)
    {
        Assert.Equal(2, await Invoke(args));
        Assert.Equal(
            "Margo.Cloud.Provisioner run\nMargo.Cloud.Provisioner redeploy-all\n"
            + "Margo.Cloud.Provisioner enqueue ensure|suspend|retire AGENT_IDENTITY_ID AGENT_USER_ID [--redeploy]\n",
            _stderr.ToString());
        Assert.Equal(0, _servicesCreated);
        Assert.Empty(_queue.Sent);
    }

    [Fact]
    public async Task Run_drains_the_queue_as_python_does()
    {
        // Python: 18 messages (a good ensure, "not json", a suspend without a dequeue count and 15
        // other instances) are all deleted, and the job logs "processed 18 message(s)".
        SetJobEnvironment();
        _environment["MARGO_INSTANCE_TEMPLATE"] = Path.Combine(RepoRoot.Find(), "cloud", "infra", "instance.json");
        _queue.Add(Message("ensure"));
        _queue.Add("not json");
        _queue.Add(Message("suspend"));
        for (var n = 0; n < 15; n++)
        {
            _queue.Add(Message("ensure", identity: "agent-identity-" + (n + 2)));
        }

        Assert.Equal(0, await Invoke("run", "ignored"));
        Assert.Equal(Enumerable.Range(0, 18).Select(n => "m" + n), _queue.Deleted);
        Assert.Equal(0, _queue.Count);
        var log = LogMessages();
        Assert.Equal(19, log.Length);
        Assert.Equal("processed 18 message(s)", log[^1]);
        Assert.Equal("dropped malformed provisioning message", log[1]);
        Assert.Equal("instance 8f6b44ff6f8dd73b5b3a: suspend -> ['route_suspended']", log[2]);
        // Pages of 16, each hidden for 900 seconds, until a page comes back empty.
        Assert.Equal([(16, TimeSpan.FromSeconds(900)), (16, TimeSpan.FromSeconds(900)), (16, TimeSpan.FromSeconds(900))], _queue.Receives);
        Assert.Equal(16, _cloud.Calls.Count);
        Assert.Equal(1, _servicesCreated);
        Assert.Equal(
            [
                "cloud fictional-subscription rg-margo-fictional https://fictional.blob.example.com/margo-routes/routes.json",
                "queue https://fictional.queue.example.com/margo-provision",
            ],
            _services.Created);
        // The deployed template is the file's JSON.
        var expected = JsonNode.Parse(File.ReadAllText(_environment["MARGO_INSTANCE_TEMPLATE"]))!.ToJsonString();
        Assert.Equal(expected, _cloud.Deployments[0].Template!.ToJsonString());

        // Python: redeploy-all then queues an ensure with redeploy for the 15 ready instances, in table order.
        _stderr.GetStringBuilder().Clear();
        Assert.Equal(0, await Invoke("redeploy-all"));
        Assert.Equal(15, _queue.Sent.Count);
        Assert.Equal(
            [
                "{\"action\": \"ensure\", \"agent_identity_id\": \"agent-identity-11\", \"agent_user_id\": \"margo-user-1\", \"event\": \"operator\", \"redeploy\": true}",
                "{\"action\": \"ensure\", \"agent_identity_id\": \"agent-identity-3\", \"agent_user_id\": \"margo-user-1\", \"event\": \"operator\", \"redeploy\": true}",
            ],
            _queue.Sent.Take(2));
        Assert.Equal(["queued 15 redeploy(s)"], LogMessages());
    }

    [Fact]
    public async Task A_failing_message_is_retried_by_later_runs_then_dropped()
    {
        SetJobEnvironment();
        var failing = new FakeCloud(fail: "deploy");
        var services = new FakeServices(failing, _queue);
        var commands = new ProvisionerCommands(name => _environment.GetValueOrDefault(name), () => services, _stderr, _clock);
        var id = _queue.Add(Message("ensure"));
        for (var run = 1; run < Provisioner.MaxAttempts; run++)
        {
            Assert.Equal(0, await commands.InvokeAsync(["run"], TestContext.Current.CancellationToken));
            Assert.True(_queue.Contains(id)); // left on the queue, hidden
            Assert.Equal(run, _queue.DequeueCount(id));
            Assert.Equal(("failed", "InvalidOperationException"), (failing.Route()!.State, failing.Route()!.Error));
            // A second run inside the visibility timeout does not see it.
            Assert.Equal(0, await commands.InvokeAsync(["run"], TestContext.Current.CancellationToken));
            Assert.Equal(run, _queue.DequeueCount(id));
            _clock.Advance(TimeSpan.FromSeconds(ProvisionerCommands.VisibilitySeconds));
        }

        Assert.Equal(0, await commands.InvokeAsync(["run"], TestContext.Current.CancellationToken));
        Assert.False(_queue.Contains(id)); // the fifth failure drops it (no poison queue)
        Assert.Equal([id], _queue.Deleted);
        Assert.Equal("failed", failing.Route()!.State); // the route stays failed
        Assert.Equal(Provisioner.MaxAttempts, failing.Calls.Count);
        var processed = LogMessages().Where(line => line.StartsWith("processed", StringComparison.Ordinal)).ToArray();
        Assert.Equal(["processed 1 message(s)", "processed 0 message(s)", "processed 1 message(s)"], processed.Take(3));
        Assert.Equal("processed 1 message(s)", processed[^1]);
    }

    [Fact]
    public async Task A_zero_dequeue_count_counts_as_one()
    {
        SetJobEnvironment();
        var failing = new FakeCloud(fail: "deploy");
        var queue = new ZeroCountQueue(Message("ensure"));
        var commands = new ProvisionerCommands(name => _environment.GetValueOrDefault(name), () => new ZeroCountServices(failing, queue), _stderr, _clock);
        Assert.Equal(0, await commands.InvokeAsync(["run"], TestContext.Current.CancellationToken));
        Assert.Equal(0, queue.Deletes); // retried, not dropped
        Assert.Equal("failed", failing.Route()!.State);
    }

    [Fact]
    public async Task Enqueue_sends_the_operator_message_as_python_does()
    {
        SetJobEnvironment();
        Assert.Equal(0, await Invoke("enqueue", "retire", Identity, User));
        Assert.Equal(0, await Invoke("enqueue", "ensure", Identity, User, "--redeploy"));
        Assert.Equal(
            [
                "{\"action\": \"retire\", \"agent_identity_id\": \"agent-identity-1\", \"agent_user_id\": \"margo-user-1\", \"event\": \"operator\"}",
                "{\"action\": \"ensure\", \"agent_identity_id\": \"agent-identity-1\", \"agent_user_id\": \"margo-user-1\", \"event\": \"operator\", \"redeploy\": true}",
            ],
            _queue.Sent);
        Assert.Equal(["queued retire", "queued ensure"], LogMessages());
        Assert.Equal(["queue https://fictional.queue.example.com/margo-provision", "queue https://fictional.queue.example.com/margo-provision"], _services.Created);
    }

    [Fact]
    public async Task Enqueue_refuses_identifiers_before_signing_in()
    {
        SetJobEnvironment();
        Assert.Equal(1, await Invoke("enqueue", "suspend", "same-id", "same-id"));
        Assert.Contains("RouteException: agent identity and agent user must differ", _stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, _servicesCreated);
        Assert.Empty(_queue.Sent);
        await Assert.ThrowsAsync<RouteException>(() => Commands().MainAsync(["enqueue", "ensure", "a/b", User], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Enqueue_needs_the_queue_url()
    {
        Assert.Equal(1, await Invoke("enqueue", "ensure", Identity, User));
        Assert.Contains("KeyNotFoundException", _stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("MARGO_PROVISION_QUEUE_URL", _stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, _servicesCreated); // Python signs in before reading the variable
    }

    [Fact]
    public async Task Run_reads_its_settings_in_python_order()
    {
        _environment["MARGO_INSTANCE_TEMPLATE"] = Path.Combine(RepoRoot.Find(), "missing-template.json");
        await Assert.ThrowsAsync<FileNotFoundException>(() => Commands().MainAsync(["run"], TestContext.Current.CancellationToken));
        _environment.Remove("MARGO_INSTANCE_TEMPLATE"); // the copy next to the binary
        string[] order = ["AZURE_SUBSCRIPTION_ID", "AZURE_RESOURCE_GROUP", "MARGO_ROUTES_URL", "MARGO_PROVISIONER_CONFIG", "MARGO_PROVISION_QUEUE_URL"];
        foreach (var name in order)
        {
            var missing = await Assert.ThrowsAsync<KeyNotFoundException>(() => Commands().MainAsync(["run"], TestContext.Current.CancellationToken));
            Assert.Equal(name, missing.Message);
            _environment[name] = name == "MARGO_PROVISIONER_CONFIG" ? Config().ToJsonString() : "https://fictional.example.com/" + name.ToLowerInvariant();
        }

        Assert.Equal(0, await Invoke("run"));
        Assert.Equal(["processed 0 message(s)"], LogMessages());
        Assert.Empty(_queue.Deleted);
    }

    [Fact]
    public async Task Run_stops_on_an_unreadable_route_table_and_leaves_the_message()
    {
        SetJobEnvironment();
        _cloud.Text = "{\"schema_version\": 2, \"routes\": {}}";
        var id = _queue.Add(Message("ensure"));
        Assert.Equal(1, await Invoke("run"));
        Assert.Contains("RouteException: unsupported route table", _stderr.ToString(), StringComparison.Ordinal);
        Assert.True(_queue.Contains(id));
        Assert.Empty(_queue.Deleted);
    }

    [Fact]
    public async Task Redeploy_all_copies_identifiers_from_the_table_without_checking_them()
    {
        SetJobEnvironment();
        _cloud.Text = Lf("""
            {
             "routes": {
              "00000000000000000001": {"state": "ready", "agent_identity_id": 42, "agent_user_id": "margo-user-1"},
              "00000000000000000002": {"state": "suspended", "agent_identity_id": "x", "agent_user_id": "y"},
              "00000000000000000003": {"state": "failed", "agent_identity_id": "x", "agent_user_id": "y"},
              "00000000000000000004": {"state": "ready", "agent_identity_id": "agent-é", "agent_user_id": null},
              "00000000000000000005": {"state": "ready", "agent_identity_id": "only-identity"},
              "00000000000000000006": {"state": "ready", "agent_identity_id": "never", "agent_user_id": "reached"}
             },
             "schema_version": 1
            }
            """);
        Assert.Equal(1, await Invoke("redeploy-all"));
        Assert.Equal(
            [
                "{\"action\": \"ensure\", \"agent_identity_id\": 42, \"agent_user_id\": \"margo-user-1\", \"event\": \"operator\", \"redeploy\": true}",
                "{\"action\": \"ensure\", \"agent_identity_id\": \"agent-\\u00e9\", \"agent_user_id\": null, \"event\": \"operator\", \"redeploy\": true}",
            ],
            _queue.Sent);
        Assert.Contains("KeyNotFoundException", _stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("agent_user_id", _stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("queued", _stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Redeploy_all_with_no_route_table_queues_nothing()
    {
        SetJobEnvironment();
        Assert.Equal(0, await Invoke("redeploy-all", "ignored"));
        Assert.Empty(_queue.Sent);
        Assert.Equal(["queued 0 redeploy(s)"], LogMessages());
    }

    [Fact]
    public void Load_config_applies_overrides_that_are_set_and_not_empty()
    {
        var env = new Dictionary<string, string>
        {
            ["MARGO_PROVISIONER_CONFIG"] = "{\"environmentId\": \"env\", \"image\": \"old\", \"minReplicas\": 1, \"image\": \"older\"}",
            ["MARGO_INSTANCE_IMAGE"] = "fictional.example.com/margo:2",
            ["MARGO_STORAGE_ROLE_ID"] = string.Empty,
            ["MARGO_SPEECH_ROLE_ID"] = "speech-role",
            ["MARGO_BLUEPRINT_CLIENT_ID"] = "blueprint-client",
        };
        var config = ProvisionerConfig.Load(name => env.GetValueOrDefault(name));
        Assert.Equal(
            "{\"environmentId\":\"env\",\"image\":\"fictional.example.com/margo:2\",\"minReplicas\":1,"
            + "\"speechRoleDefinitionId\":\"speech-role\",\"blueprintClientId\":\"blueprint-client\"}",
            config!.ToJsonString());
        Assert.Equal(
            ["image", "storageRoleDefinitionId", "pullRoleDefinitionId", "modelRoleDefinitionId", "speechRoleDefinitionId", "blueprintClientId"],
            ProvisionerConfig.Overrides.Select(pair => pair.Key));
        Assert.Equal(
            ["MARGO_INSTANCE_IMAGE", "MARGO_STORAGE_ROLE_ID", "MARGO_PULL_ROLE_ID", "MARGO_MODEL_ROLE_ID", "MARGO_SPEECH_ROLE_ID", "MARGO_BLUEPRINT_CLIENT_ID"],
            ProvisionerConfig.Overrides.Select(pair => pair.Value));
    }

    [Fact]
    public void Load_config_fails_as_python_does()
    {
        var env = new Dictionary<string, string>();
        Assert.Equal("MARGO_PROVISIONER_CONFIG", Assert.Throws<KeyNotFoundException>(() => ProvisionerConfig.Load(name => env.GetValueOrDefault(name))).Message);
        env["MARGO_PROVISIONER_CONFIG"] = "{not json";
        Assert.ThrowsAny<JsonException>(() => ProvisionerConfig.Load(name => env.GetValueOrDefault(name)));
        env["MARGO_PROVISIONER_CONFIG"] = "[1, 2]";
        Assert.Equal("[1,2]", ProvisionerConfig.Load(name => env.GetValueOrDefault(name))!.ToJsonString()); // no override: kept as is
        env["MARGO_PULL_ROLE_ID"] = "pull-role";
        Assert.Throws<InvalidOperationException>(() => ProvisionerConfig.Load(name => env.GetValueOrDefault(name))); // Python: TypeError
        env["MARGO_PROVISIONER_CONFIG"] = "null";
        Assert.Throws<InvalidOperationException>(() => ProvisionerConfig.Load(name => env.GetValueOrDefault(name)));
    }

    [Fact]
    public void The_template_is_copied_next_to_the_binary_and_can_be_overridden()
    {
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "instance.json"), ProvisionerConfig.DefaultTemplatePath);
        var source = File.ReadAllText(Path.Combine(RepoRoot.Find(), "cloud", "infra", "instance.json"));
        Assert.Equal(source, File.ReadAllText(ProvisionerConfig.DefaultTemplatePath));
        var template = ProvisionerConfig.LoadTemplate(_ => null);
        Assert.Equal(JsonNode.Parse(source)!.ToJsonString(), template!.ToJsonString());

        var directory = Directory.CreateTempSubdirectory("margo-provisioner-tests-");
        try
        {
            var other = Path.Combine(directory.FullName, "other.json");
            File.WriteAllText(other, "{\"resources\": [], \"resources\": [1]}");
            Assert.Equal("{\"resources\":[1]}", ProvisionerConfig.LoadTemplate(name => name == "MARGO_INSTANCE_TEMPLATE" ? other : null)!.ToJsonString());
            File.WriteAllBytes(other, [0x7b, 0xff, 0x7d]);
            Assert.ThrowsAny<ArgumentException>(() => ProvisionerConfig.LoadTemplate(name => name == "MARGO_INSTANCE_TEMPLATE" ? other : null));
            File.WriteAllText(other, "{\"resources\": [}");
            Assert.ThrowsAny<JsonException>(() => ProvisionerConfig.LoadTemplate(name => name == "MARGO_INSTANCE_TEMPLATE" ? other : null));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Log_lines_use_python_logging_format()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 7, 9, 30, 5, 123, TimeSpan.Zero).AddTicks(9999));
        var writer = new StringWriter();
        new ProvisionerLog(writer, clock).Info("processed 3 message(s)");
        Assert.Equal("2026-11-07 09:30:05,123 INFO margo.provision | processed 3 message(s)\n", writer.ToString());
    }

    /// <summary>Returns one message whose dequeue count is missing (Python: <c>dequeue_count or 1</c>), then nothing.</summary>
    private sealed class ZeroCountQueue(string text) : IProvisioningQueueClient
    {
        private bool _received;

        public int Deletes { get; private set; }

        public Task<IReadOnlyList<QueuedMessage>> ReceiveAsync(int maxMessages, TimeSpan visibilityTimeout, CancellationToken cancellationToken)
        {
            IReadOnlyList<QueuedMessage> page = _received ? [] : [new QueuedMessage("m0", "receipt", text, 0)];
            _received = true;
            return Task.FromResult(page);
        }

        public Task DeleteAsync(QueuedMessage message, CancellationToken cancellationToken)
        {
            Deletes++;
            return Task.CompletedTask;
        }

        public Task SendAsync(string text, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ZeroCountServices(ICloud cloud, IProvisioningQueueClient queue) : IProvisionerServices
    {
        public ICloud CreateCloud(string subscriptionId, string resourceGroup, string routesUrl) => cloud;

        public IProvisioningQueueClient CreateQueue(string queueUrl) => queue;
    }
}

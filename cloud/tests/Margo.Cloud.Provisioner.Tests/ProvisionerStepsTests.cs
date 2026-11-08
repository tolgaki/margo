using System.Text.Json.Nodes;
using Margo.Cloud.Core;
using Xunit;
using static Margo.Cloud.Provisioner.Tests.Fixtures;

namespace Margo.Cloud.Provisioner.Tests;

/// <summary>
/// the former Python instance tests' <c>ProvisionerSteps</c>, scenario for scenario, plus the edge
/// cases whose expected values were recorded by running <c>provision/core.py</c> on Python 3.13 with
/// the same <c>FakeCloud</c> and <c>instances._utc_now</c> fixed at 2026-11-07T09:30:00+00:00.
/// </summary>
public sealed class ProvisionerStepsTests
{
    private readonly List<string> _log = [];

    private Provisioner Create(FakeCloud cloud, JsonNode? config = null) =>
        new(cloud, config ?? Config(), new JsonObject { ["resources"] = new JsonArray() }, _log.Add, new FakeClock(Now));

    private static Task<HandleOutcome> Handle(Provisioner provisioner, string raw, long dequeueCount = 1) =>
        provisioner.HandleAsync(raw, dequeueCount, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Ensure_then_retire_keeps_storage_and_records_each_state()
    {
        var cloud = new FakeCloud();
        var provisioner = Create(cloud);
        Assert.Equal(HandleOutcome.Done, await Handle(provisioner, Message("ensure")));
        var key = Instances.InstanceKey(Identity, User);
        Assert.Equal("ready", RouteTable.Loads(cloud.Text).Get(key)!.State);
        Assert.Equal(HandleOutcome.Done, await Handle(provisioner, Message("ensure")));
        Assert.Equal(["deploy"], cloud.Calls.Select(call => call.Call)); // already ready: nothing redeployed
        Assert.Equal(HandleOutcome.Done, await Handle(provisioner, Message("retire")));
        Assert.Equal(["deploy", "delete_app", "delete_identity"], cloud.Calls.Select(call => call.Call));
        Assert.Equal("retired", RouteTable.Loads(cloud.Text).Get(key)!.State);
        Assert.DoesNotContain(_log, line => line.Contains("tok", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Failures_are_recorded_retried_then_dropped()
    {
        var cloud = new FakeCloud(fail: "deploy");
        var provisioner = Create(cloud);
        Assert.Equal(HandleOutcome.Retry, await Handle(provisioner, Message("ensure"), dequeueCount: 1));
        var key = Instances.InstanceKey(Identity, User);
        var entry = RouteTable.Loads(cloud.Text).Get(key)!;
        // Python records type(exc).__name__ ("RuntimeError" for its fake); C# records the .NET type name.
        Assert.Equal(("failed", nameof(InvalidOperationException)), (entry.State, entry.Error));
        Assert.Equal(HandleOutcome.Dropped, await Handle(provisioner, Message("ensure"), dequeueCount: Provisioner.MaxAttempts));
        Assert.Equal(HandleOutcome.Dropped, await Handle(provisioner, "not json"));
        Assert.Equal(HandleOutcome.Dropped, await Handle(provisioner, "{\"action\": \"explode\"}"));
    }

    [Fact]
    public async Task Route_writes_survive_concurrent_changes()
    {
        var cloud = new FakeCloud { Conflicts = 2 };
        Assert.Equal(HandleOutcome.Done, await Handle(Create(cloud), Message("ensure")));
        cloud = new FakeCloud { Conflicts = 50 };
        Assert.Equal(HandleOutcome.Retry, await Handle(Create(cloud), Message("ensure")));
    }

    [Fact]
    public async Task Lifecycle_matches_python_logs_calls_and_route_blob()
    {
        var cloud = new FakeCloud();
        var provisioner = Create(cloud);
        foreach (var action in new[] { "ensure", "ensure", "suspend", "ensure", "retire", "retire" })
        {
            Assert.Equal(HandleOutcome.Done, await Handle(provisioner, Message(action)));
        }

        Assert.Equal(
            [
                "instance 8f6b44ff6f8dd73b5b3a: ensure -> ['route_provisioning', 'deploy', 'route_ready']",
                "instance 8f6b44ff6f8dd73b5b3a: ensure -> nothing to do",
                "instance 8f6b44ff6f8dd73b5b3a: suspend -> ['route_suspended']",
                "instance 8f6b44ff6f8dd73b5b3a: ensure -> ['route_provisioning', 'deploy', 'route_ready']",
                "instance 8f6b44ff6f8dd73b5b3a: retire -> ['route_retired', 'delete_app', 'delete_identity']",
                "instance 8f6b44ff6f8dd73b5b3a: retire -> nothing to do",
            ],
            _log);
        Assert.Equal(
            [
                ("deploy", "margo-instance-8f6b44ff6f8dd73b5b3a"), ("deploy", "margo-instance-8f6b44ff6f8dd73b5b3a"),
                ("delete_app", "margo-8f6b44ff6f8dd73b5b3a"), ("delete_identity", "id-margo-8f6b44ff6f8dd73b5b3a"),
            ],
            cloud.Calls);
        Assert.Equal(
            "{\n \"routes\": {\n  \"8f6b44ff6f8dd73b5b3a\": {\n   \"agent_identity_id\": \"agent-identity-1\",\n"
            + "   \"agent_user_id\": \"margo-user-1\",\n   \"retired_at\": \"2026-11-07T09:30:00+00:00\",\n   \"revision\": 6,\n"
            + "   \"state\": \"retired\",\n   \"updated_at\": \"2026-11-07T09:30:00+00:00\",\n"
            + "   \"url\": \"https://margo-8f6b44ff6f8dd73b5b3a.internal.calmforest-1a2b3c.example.com\"\n  }\n },\n \"schema_version\": 1\n}",
            cloud.Text);
    }

    [Fact]
    public async Task Deployment_uses_the_template_and_parameters_from_configuration()
    {
        var cloud = new FakeCloud();
        await Handle(Create(cloud), Message("ensure"));
        var (template, parameters) = Assert.Single(cloud.Deployments);
        Assert.Equal("{\"resources\":[]}", template!.ToJsonString());
        Assert.Equal(Instances.DeploymentParameters(Key, new ProvisioningMessage("ensure", Identity, User), Config()).ToJsonString(), parameters.ToJsonString());
        Assert.Equal("margo-" + Key, (string?)parameters["appName"]!["value"]);
    }

    [Fact]
    public async Task Redeploy_ensures_a_ready_instance_again()
    {
        var cloud = new FakeCloud();
        var provisioner = Create(cloud);
        Assert.Equal(HandleOutcome.Done, await Handle(provisioner, Message("ensure", redeploy: true)));
        Assert.Equal(HandleOutcome.Done, await Handle(provisioner, Message("ensure", redeploy: true)));
        Assert.Equal(2, cloud.Calls.Count(call => call.Call == "deploy"));
        Assert.Equal(4, cloud.Route()!.Revision); // provisioning and ready, twice
    }

    [Fact]
    public async Task Failure_log_and_route_match_python()
    {
        var cloud = new FakeCloud(fail: "deploy");
        var provisioner = Create(cloud);
        Assert.Equal(HandleOutcome.Retry, await Handle(provisioner, Message("ensure"), 1));
        Assert.Equal(HandleOutcome.Dropped, await Handle(provisioner, Message("ensure"), 5));
        Assert.Equal(HandleOutcome.Dropped, await Handle(provisioner, "not json"));
        Assert.Equal(HandleOutcome.Dropped, await Handle(provisioner, "{\"action\": \"explode\"}"));
        Assert.Equal(
            [
                "instance 8f6b44ff6f8dd73b5b3a: ensure -> ['route_provisioning', 'deploy', 'route_ready']",
                "instance 8f6b44ff6f8dd73b5b3a: step failed: InvalidOperationException",
                "instance 8f6b44ff6f8dd73b5b3a: ensure -> ['route_provisioning', 'deploy', 'route_ready']",
                "instance 8f6b44ff6f8dd73b5b3a: step failed: InvalidOperationException",
                "dropped malformed provisioning message",
                "dropped malformed provisioning message",
            ],
            _log);
        var entry = cloud.Route()!;
        Assert.Equal(("failed", 4L, "InvalidOperationException"), (entry.State, entry.Revision, entry.Error));
        Assert.Null(entry.RetiredAt);
    }

    [Fact]
    public async Task A_failed_retirement_is_retried_from_the_start()
    {
        var ready = new FakeCloud();
        Assert.Equal(HandleOutcome.Done, await Handle(Create(ready), Message("ensure")));
        var cloud = new FakeCloud(fail: "delete_app") { Text = ready.Text, ETag = "etag-ready" };
        var provisioner = Create(cloud);
        Assert.Equal(HandleOutcome.Retry, await Handle(provisioner, Message("retire")));
        Assert.Equal(("failed", "TimeoutException"), (cloud.Route()!.State, cloud.Route()!.Error));
        Assert.Equal(["delete_app"], cloud.Calls.Select(call => call.Call)); // the identity is kept until the app is gone
        // A failed route is retired again from the first step (Python: plan for "failed").
        Assert.Equal(HandleOutcome.Retry, await Handle(provisioner, Message("retire"), 2));
        Assert.Equal(["delete_app", "delete_app"], cloud.Calls.Select(call => call.Call));
    }

    [Fact]
    public async Task Malformed_messages_are_dropped_without_touching_the_cloud()
    {
        var cloud = new FakeCloud();
        var provisioner = Create(cloud);
        string[] malformed =
        [
            "", "not json", "[]", "null", "{\"action\": \"explode\"}", "{\"action\": \"ensure\"}",
            Message("ensure", identity: "same-id", user: "same-id"), Message("ensure", identity: "a/b"),
            "{\"action\": \"ensure\", \"agent_identity_id\": \"\\ud800\", \"agent_user_id\": \"u\"}",
        ];
        foreach (var raw in malformed)
        {
            Assert.Equal(HandleOutcome.Dropped, await Handle(provisioner, raw));
        }

        Assert.Equal(HandleOutcome.Dropped, await provisioner.HandleAsync((string?)null, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(HandleOutcome.Dropped, await provisioner.HandleAsync(new JsonArray(), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, cloud.Loads);
        Assert.Empty(cloud.Calls);
        Assert.All(_log, line => Assert.Equal("dropped malformed provisioning message", line));
    }

    [Fact]
    public async Task An_already_parsed_message_is_handled_too()
    {
        var cloud = new FakeCloud();
        var message = new JsonObject { ["action"] = "ensure", ["agent_identity_id"] = Identity, ["agent_user_id"] = User };
        Assert.Equal(HandleOutcome.Done, await Create(cloud).HandleAsync(message, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("ready", cloud.Route()!.State);
    }

    [Fact]
    public async Task Persistent_conflicts_fail_the_step_and_cannot_record_it()
    {
        var cloud = new FakeCloud { Conflicts = 50 };
        Assert.Equal(HandleOutcome.Retry, await Handle(Create(cloud), Message("ensure")));
        Assert.Equal(
            [
                "instance 8f6b44ff6f8dd73b5b3a: ensure -> ['route_provisioning', 'deploy', 'route_ready']",
                "instance 8f6b44ff6f8dd73b5b3a: step failed: RouteException",
                "instance 8f6b44ff6f8dd73b5b3a: could not record the failure",
            ],
            _log);
        Assert.Equal(string.Empty, cloud.Text);
        Assert.Equal(40, cloud.Conflicts); // five tries for the step, five for the failure record
        Assert.Empty(cloud.Calls);
    }

    [Fact]
    public async Task Two_conflicts_are_absorbed()
    {
        var cloud = new FakeCloud { Conflicts = 2 };
        Assert.Equal(HandleOutcome.Done, await Handle(Create(cloud), Message("ensure")));
        Assert.Equal(0, cloud.Conflicts);
        Assert.Equal(["instance 8f6b44ff6f8dd73b5b3a: ensure -> ['route_provisioning', 'deploy', 'route_ready']"], _log);
        Assert.Equal("ready", cloud.Route()!.State);
    }

    public static TheoryData<string, string> BadConfigurations => new()
    {
        { "[1, 2]", "InvalidOperationException" }, // Python: TypeError
        { "{\"minReplicas\": 0}", "KeyNotFoundException" }, // Python: KeyError
        { "{\"environmentDomain\": null}", "RouteException" }, // Python: RouteError
        { "{\"environmentDomain\": \"Not A Domain\"}", "RouteException" },
    };

    [Theory]
    [MemberData(nameof(BadConfigurations))]
    public async Task Bad_configuration_fails_every_step_that_needs_it(string configText, string failure)
    {
        var cloud = new FakeCloud();
        var provisioner = Create(cloud, PythonJson.Loads(configText));
        Assert.Equal(HandleOutcome.Retry, await Handle(provisioner, Message("ensure"), 1));
        Assert.Equal(HandleOutcome.Done, await Handle(provisioner, Message("suspend"), 5)); // nothing to do
        Assert.Equal(
            [
                "instance 8f6b44ff6f8dd73b5b3a: ensure -> ['route_provisioning', 'deploy', 'route_ready']",
                "instance 8f6b44ff6f8dd73b5b3a: step failed: " + failure,
                "instance 8f6b44ff6f8dd73b5b3a: could not record the failure",
                "instance 8f6b44ff6f8dd73b5b3a: suspend -> nothing to do",
            ],
            _log);
        Assert.Equal(string.Empty, cloud.Text);
        Assert.Empty(cloud.Calls);
    }

    [Fact]
    public async Task Missing_deployment_configuration_is_recorded_on_the_route()
    {
        var config = Config();
        config.Remove("image");
        var cloud = new FakeCloud();
        Assert.Equal(HandleOutcome.Retry, await Handle(Create(cloud, config), Message("ensure")));
        Assert.Equal(("failed", "RouteException"), (cloud.Route()!.State, cloud.Route()!.Error));
        Assert.Empty(cloud.Calls);
    }

    [Fact]
    public async Task An_unreadable_route_table_stops_the_job_as_in_python()
    {
        // Python's handle reads the route table outside its try block, so the job stops and the
        // message reappears after its visibility timeout.
        var cloud = new FakeCloud { Text = "not a table" };
        var error = await Assert.ThrowsAsync<RouteException>(() => Handle(Create(cloud), Message("ensure")));
        Assert.Equal("route table is not JSON", error.Message);
        cloud = new FakeCloud { LoadFailure = new IOException("blob unavailable") };
        await Assert.ThrowsAsync<IOException>(() => Handle(Create(cloud), Message("ensure")));
        Assert.Empty(cloud.Calls);
    }

    [Fact]
    public async Task Cancellation_is_not_recorded_as_a_failure()
    {
        using var cancel = new CancellationTokenSource();
        var cloud = new CancellingCloud(cancel);
        var provisioner = new Provisioner(cloud, Config(), null, _log.Add, new FakeClock(Now));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provisioner.HandleAsync(Message("ensure"), 1, cancel.Token));
        Assert.Equal("provisioning", cloud.Inner.Route()!.State);
        Assert.DoesNotContain(_log, line => line.Contains("failed", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_step_lists_print_as_python_lists()
    {
        Assert.Equal("['route_suspended']", Provisioner.PythonListRepr(["route_suspended"]));
        Assert.Equal("['a', 'b']", Provisioner.PythonListRepr(["a", "b"]));
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["route_provisioning"] = "provisioning", ["route_ready"] = "ready", ["route_suspended"] = "suspended", ["route_retired"] = "retired",
            },
            Provisioner.RouteStateSteps);
        Assert.Equal(5, Provisioner.MaxAttempts);
    }

    /// <summary>Cancels the job while it deploys, as a stopping container would.</summary>
    private sealed class CancellingCloud(CancellationTokenSource cancel) : ICloud
    {
        public FakeCloud Inner { get; } = new();

        public async Task DeployAsync(string name, JsonNode? template, JsonObject parameters, CancellationToken cancellationToken)
        {
            await cancel.CancelAsync();
            cancellationToken.ThrowIfCancellationRequested();
        }

        public Task DeleteAppAsync(string name, CancellationToken cancellationToken) => Inner.DeleteAppAsync(name, cancellationToken);

        public Task DeleteIdentityAsync(string name, CancellationToken cancellationToken) => Inner.DeleteIdentityAsync(name, cancellationToken);

        public Task<RouteBlob> LoadRoutesAsync(CancellationToken cancellationToken) => Inner.LoadRoutesAsync(cancellationToken);

        public Task<bool> SaveRoutesAsync(string text, string? etag, CancellationToken cancellationToken) =>
            Inner.SaveRoutesAsync(text, etag, cancellationToken);
    }
}

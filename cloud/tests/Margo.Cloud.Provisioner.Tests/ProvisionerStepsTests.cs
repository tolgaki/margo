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

    /// <summary>What the C# job reports for an operator besides the lines Python logged.</summary>
    private readonly List<string> _errors = [];

    private Provisioner Create(FakeCloud cloud, JsonNode? config = null) =>
        new(cloud, config ?? Config(), new JsonObject { ["resources"] = new JsonArray() }, _log.Add, new FakeClock(Now), logError: _errors.Add);

    private static Task<HandleOutcome> Handle(Provisioner provisioner, string raw, long dequeueCount = 1) =>
        provisioner.HandleAsync(raw, dequeueCount, cancellationToken: TestContext.Current.CancellationToken);

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
        Assert.Equal(["instance 8f6b44ff6f8dd73b5b3a: ensure failed 5 time(s) and was dropped; the route stays failed (InvalidOperationException)"], _errors);
        var entry = cloud.Route()!;
        Assert.Equal(("failed", 4L, "InvalidOperationException"), (entry.State, entry.Revision, entry.Error));
        Assert.Null(entry.RetiredAt);
    }

    [Fact]
    public async Task A_failed_retirement_stays_retired_and_is_retried()
    {
        // Python recorded "failed" here, which the router answers by provisioning the retired agent again.
        var ready = new FakeCloud();
        Assert.Equal(HandleOutcome.Done, await Handle(Create(ready), Message("ensure")));
        var cloud = new FakeCloud(fail: "delete_app") { Text = ready.Text, ETag = "etag-ready" };
        var provisioner = Create(cloud);
        Assert.Equal(HandleOutcome.Retry, await Handle(provisioner, Message("retire")));
        Assert.Equal(("retired", "TimeoutException"), (cloud.Route()!.State, cloud.Route()!.Error));
        Assert.NotNull(cloud.Route()!.RetiredAt);
        Assert.Equal(["delete_app"], cloud.Calls.Select(call => call.Call)); // the identity is kept until the app is gone
        // The removal is tried again although the route already says retired.
        Assert.Equal(HandleOutcome.Retry, await Handle(provisioner, Message("retire"), 2));
        Assert.Equal(["delete_app", "delete_app"], cloud.Calls.Select(call => call.Call));
        Assert.Equal(("retired", "TimeoutException"), (cloud.Route()!.State, cloud.Route()!.Error));
        // An activity for the instance changes nothing: the router ignores a retired route, and the provisioner an
        // ensure a router replica with a stale table queued for it.
        foreach (var activity in new[] { "first-activity", "retry-after-failure" })
        {
            Assert.Equal(HandleOutcome.Done, await Handle(provisioner, Message("ensure", @event: activity)));
        }

        Assert.DoesNotContain(cloud.Calls, call => call.Call == "deploy");
        Assert.Equal("retired", cloud.Route()!.State);
        // Once the removal works the error is cleared, and a further retire has nothing to do.
        cloud.Fail = null;
        Assert.Equal(HandleOutcome.Done, await Handle(provisioner, Message("retire"), 3));
        Assert.Equal(["delete_app", "delete_app", "delete_app", "delete_identity"], cloud.Calls.Select(call => call.Call));
        Assert.Equal(("retired", null), (cloud.Route()!.State, cloud.Route()!.Error));
        Assert.Equal(HandleOutcome.Done, await Handle(provisioner, Message("retire")));
        Assert.Equal(4, cloud.Calls.Count);
        Assert.Equal("instance 8f6b44ff6f8dd73b5b3a: retire -> ['delete_app', 'delete_identity', 'route_retired']", _log[^2]);
        Assert.Equal("instance 8f6b44ff6f8dd73b5b3a: retire -> nothing to do", _log[^1]);
    }

    [Fact]
    public async Task A_retirement_that_keeps_failing_is_dropped_only_once_recorded_and_says_so()
    {
        var ready = new FakeCloud();
        Assert.Equal(HandleOutcome.Done, await Handle(Create(ready), Message("ensure")));
        var cloud = new FakeCloud(fail: "delete_identity") { Text = ready.Text, ETag = "etag-ready" };
        var errors = new List<string>();
        var provisioner = new Provisioner(cloud, Config(), null, _log.Add, new FakeClock(Now), logError: errors.Add);
        Assert.Equal(HandleOutcome.Retry, await Handle(provisioner, Message("retire"), Provisioner.MaxAttempts - 1));
        Assert.Empty(errors);
        Assert.Equal(HandleOutcome.Dropped, await Handle(provisioner, Message("retire"), Provisioner.MaxAttempts));
        Assert.Equal(
            ["instance 8f6b44ff6f8dd73b5b3a: retire still failing after 5 attempt(s) (TimeoutException); the route stays retired and every run tries the removal again"],
            errors);
        Assert.Equal(("retired", "TimeoutException"), (cloud.Route()!.State, cloud.Route()!.Error));

        // The route cannot be written at all: nothing records the retirement, so the message is never dropped.
        var stuck = new FakeCloud { Text = ready.Text, ETag = "etag-ready", Conflicts = 1000 };
        errors.Clear();
        provisioner = new Provisioner(stuck, Config(), null, _log.Add, new FakeClock(Now), logError: errors.Add);
        Assert.Equal(HandleOutcome.Retry, await Handle(provisioner, Message("retire"), 50));
        Assert.Equal("ready", stuck.Route()!.State);
        Assert.EndsWith("it could not be recorded and stays on the queue", Assert.Single(errors), StringComparison.Ordinal);
        errors.Clear();
        Assert.Equal(HandleOutcome.Retry, await Handle(provisioner, Message("suspend"), 50));
        Assert.Equal(["instance 8f6b44ff6f8dd73b5b3a: suspend still failing after 50 attempt(s) (RouteException); it stays on the queue"], errors);
        Assert.Equal("ready", stuck.Route()!.State); // never "failed": the router would provision it again
    }

    [Fact]
    public async Task A_retirement_interrupted_after_its_route_write_is_finished_when_the_message_returns()
    {
        var cloud = new FakeCloud();
        var provisioner = Create(cloud);
        Assert.Equal(HandleOutcome.Done, await Handle(provisioner, Message("ensure")));
        // The job stopped after recording "retired": no error is on the route, and the message was not deleted.
        var table = RouteTable.Loads(cloud.Text, new FakeClock(Now));
        table.Put(Key, Identity, User, "retired");
        cloud.Text = table.Dumps();
        Assert.Equal(HandleOutcome.Done, await Handle(provisioner, Message("retire"), 2));
        Assert.Equal(["deploy", "delete_app", "delete_identity"], cloud.Calls.Select(call => call.Call));
        Assert.Equal(HandleOutcome.Done, await Handle(provisioner, Message("retire"), 1)); // a new retire of a retired instance: nothing
        Assert.Equal(3, cloud.Calls.Count);
    }

    [Fact]
    public async Task Pending_removals_are_resumed_once_per_run()
    {
        var ready = new FakeCloud();
        Assert.Equal(HandleOutcome.Done, await Handle(Create(ready), Message("ensure")));
        Assert.Equal(HandleOutcome.Done, await Handle(Create(ready), Message("ensure", identity: "agent-identity-2")));
        var cloud = new FakeCloud(fail: "delete_app") { Text = ready.Text, ETag = ready.ETag };
        var errors = new List<string>();
        var first = new Provisioner(cloud, Config(), null, _log.Add, new FakeClock(Now), logError: errors.Add);
        Assert.Equal(HandleOutcome.Retry, await Handle(first, Message("retire")));
        Assert.Equal(0, await first.ResumePendingRemovalsAsync(TestContext.Current.CancellationToken)); // this run already tried it
        Assert.Single(cloud.Calls);

        // The next run has no message for it (it was dropped, or expired): the route is what remembers.
        var next = new Provisioner(cloud, Config(), null, _log.Add, new FakeClock(Now), logError: errors.Add);
        Assert.Equal(1, await next.ResumePendingRemovalsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(("retired", "TimeoutException"), (cloud.Route()!.State, cloud.Route()!.Error));
        Assert.Contains("retire still failing", Assert.Single(errors), StringComparison.Ordinal);
        Assert.Equal(0, await next.ResumePendingRemovalsAsync(TestContext.Current.CancellationToken));

        cloud.Fail = null;
        var later = new Provisioner(cloud, Config(), null, _log.Add, new FakeClock(Now), logError: errors.Add);
        Assert.Equal(1, await later.ResumePendingRemovalsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(("retired", null), (cloud.Route()!.State, cloud.Route()!.Error));
        Assert.Equal(("delete_identity", "id-margo-" + Key), cloud.Calls[^1]);
        Assert.Equal("ready", cloud.Route(Instances.InstanceKey("agent-identity-2", User))!.State); // the other instance is untouched
        Assert.Equal(0, await Create(cloud).ResumePendingRemovalsAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("suspend", "first-activity", false)]
    [InlineData("suspend", "retry-after-failure", false)]
    [InlineData("retire", "first-activity", false)]
    [InlineData("retire", "retry-after-failure", false)]
    [InlineData("suspend", "agenticuserenabled", true)] // a lifecycle notification starts her again
    [InlineData("retire", "agenticuserundeleted", true)]
    [InlineData("suspend", "operator", true)]
    [InlineData("suspend", null, true)]
    public async Task An_activity_never_starts_a_stopped_instance(string stoppedBy, string? ensureEvent, bool deployed)
    {
        var cloud = new FakeCloud();
        var provisioner = Create(cloud);
        Assert.Equal(HandleOutcome.Done, await Handle(provisioner, Message("ensure")));
        Assert.Equal(HandleOutcome.Done, await Handle(provisioner, Message(stoppedBy)));
        var stopped = cloud.Route()!;
        Assert.Equal(HandleOutcome.Done, await Handle(provisioner, Message("ensure", @event: ensureEvent)));
        Assert.Equal(deployed ? 2 : 1, cloud.Calls.Count(call => call.Call == "deploy"));
        if (deployed)
        {
            Assert.Equal("ready", cloud.Route()!.State);
        }
        else
        {
            Assert.Equal((stopped.State, stopped.Revision), (cloud.Route()!.State, cloud.Route()!.Revision));
            Assert.Equal("instance 8f6b44ff6f8dd73b5b3a: ensure for an activity ignored: instance is " + stopped.State, _log[^1]);
        }
    }

    [Fact]
    public async Task An_ensure_beyond_the_instance_cap_is_refused_without_deploying()
    {
        var cloud = new FakeCloud();
        var errors = new List<string>();
        var provisioner = new Provisioner(cloud, Config(), null, _log.Add, new FakeClock(Now), maxInstances: 2, logError: errors.Add);
        Assert.Equal(HandleOutcome.Done, await Handle(provisioner, Message("ensure")));
        Assert.Equal(HandleOutcome.Done, await Handle(provisioner, Message("ensure", identity: "agent-identity-2")));
        // A third recipient (a forged but authenticated activity could name any): refused, loudly, and not retried.
        var third = Instances.InstanceKey("agent-identity-3", User);
        Assert.Equal(HandleOutcome.Dropped, await Handle(provisioner, Message("ensure", identity: "agent-identity-3", @event: "first-activity")));
        Assert.Equal(2, cloud.Calls.Count);
        Assert.Equal(("failed", Provisioner.MaxInstancesError, 1L), (cloud.Route(third)!.State, cloud.Route(third)!.Error, cloud.Route(third)!.Revision));
        Assert.Equal(["instance " + third + ": ensure refused: 2 instance(s) already exist (MARGO_MAX_INSTANCES); nothing was deployed"], errors);
        // Asked again (the router does, on her next activity): refused again, and the route is not rewritten.
        Assert.Equal(HandleOutcome.Dropped, await Handle(provisioner, Message("ensure", identity: "agent-identity-3", @event: "retry-after-failure")));
        Assert.Equal(1L, cloud.Route(third)!.Revision);
        Assert.Equal(2, errors.Count);
        // The instances that exist are still redeployed, suspended and started again at the cap.
        Assert.Equal(HandleOutcome.Done, await Handle(provisioner, Message("ensure", redeploy: true)));
        Assert.Equal(HandleOutcome.Done, await Handle(provisioner, Message("suspend")));
        Assert.Equal(HandleOutcome.Done, await Handle(provisioner, Message("ensure")));
        Assert.Equal(4, cloud.Calls.Count(call => call.Call == "deploy"));
        // A refused route holds nothing, so it does not count; a retired instance frees its place.
        Assert.Equal(HandleOutcome.Done, await Handle(provisioner, Message("retire", identity: "agent-identity-2")));
        Assert.Equal(HandleOutcome.Done, await Handle(provisioner, Message("ensure", identity: "agent-identity-3")));
        Assert.Equal("ready", cloud.Route(third)!.State);
        Assert.Throws<ArgumentOutOfRangeException>(() => new Provisioner(cloud, Config(), null, maxInstances: 0));
    }

    [Fact]
    public async Task A_failed_deployment_and_a_pending_removal_still_count_towards_the_cap()
    {
        var cloud = new FakeCloud(fail: "deploy");
        var provisioner = new Provisioner(cloud, Config(), null, _log.Add, new FakeClock(Now), maxInstances: 1);
        Assert.Equal(HandleOutcome.Retry, await Handle(provisioner, Message("ensure"))); // may have created resources
        cloud.Fail = null;
        Assert.Equal(HandleOutcome.Dropped, await Handle(provisioner, Message("ensure", identity: "agent-identity-2")));
        Assert.Equal(HandleOutcome.Done, await Handle(provisioner, Message("ensure"), 2)); // her own retry is not refused
        cloud.Fail = "delete_app";
        Assert.Equal(HandleOutcome.Retry, await Handle(provisioner, Message("retire")));
        Assert.Equal(HandleOutcome.Dropped, await Handle(provisioner, Message("ensure", identity: "agent-identity-2")));
        cloud.Fail = null;
        Assert.Equal(HandleOutcome.Done, await Handle(provisioner, Message("retire"), 2));
        Assert.Equal(HandleOutcome.Done, await Handle(provisioner, Message("ensure", identity: "agent-identity-2")));
    }

    [Fact]
    public async Task A_redeploy_is_skipped_when_the_instance_was_deployed_after_it_was_requested()
    {
        var clock = new FakeClock(Now);
        var cloud = new FakeCloud();
        var provisioner = new Provisioner(cloud, Config(), null, _log.Add, clock);
        var requested = clock.Now;
        clock.Advance(TimeSpan.FromMinutes(5));
        Task<HandleOutcome> Redeploy(long count, DateTimeOffset? enqueuedAt) =>
            provisioner.HandleAsync(Message("ensure", redeploy: true), count, enqueuedAt, TestContext.Current.CancellationToken);
        Assert.Equal(HandleOutcome.Done, await Redeploy(1, requested));
        Assert.Single(cloud.Calls);
        // The same message again (its visibility timeout passed while it was deployed), or a duplicate queued before.
        Assert.Equal(HandleOutcome.Done, await Redeploy(2, requested));
        Assert.Equal(HandleOutcome.Done, await Redeploy(1, requested.AddMinutes(4)));
        Assert.Single(cloud.Calls);
        Assert.Equal((2L, "ready"), (cloud.Route()!.Revision, cloud.Route()!.State));
        Assert.Equal("instance 8f6b44ff6f8dd73b5b3a: redeploy skipped: deployed after it was requested", _log[^1]);
        // Requested at or after the last deployment, or at an unknown time: deployed.
        Assert.Equal(HandleOutcome.Done, await Redeploy(1, clock.Now));
        Assert.Equal(HandleOutcome.Done, await Redeploy(1, null));
        Assert.Equal(3, cloud.Calls.Count);
        // Only a ready instance is skipped: one left "provisioning" by a stopped job is deployed again.
        var table = RouteTable.Loads(cloud.Text, clock);
        table.Put(Key, Identity, User, "provisioning");
        cloud.Text = table.Dumps();
        Assert.Equal(HandleOutcome.Done, await Redeploy(2, requested));
        Assert.Equal(4, cloud.Calls.Count);
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
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provisioner.HandleAsync(Message("ensure"), 1, null, cancel.Token));
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

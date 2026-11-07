using System.Text;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Proactive;
using Margo.Cloud.State.Tasks;
using Xunit;

namespace Margo.Cloud.State.Tests.Tasks;

/// <summary>
/// Library-level ports of <c>tests/test_task_runs.py</c> and <c>tests/test_task_cli.py</c> for what the parity scenarios
/// (<c>cloud/contracts/state/tasks/</c>) cannot show: concurrent claims on two connections, read-only connections refusing
/// writes, the caller's outer transaction, the replaceable clock as an async-flow override, the in-process CLI round trip,
/// and the chunked file reader.
/// </summary>
public sealed class TaskStoreTests
{
    private const string Account = "task-fixture@example.com";

    private static string Stamp(int seconds = 0) => (PyDateTime.Now() + PyTimeDelta.FromSeconds(seconds)).Isoformat();

    private static JsonObject Zero(string? metric = null, long value = 0)
    {
        var cost = TaskRuns.Zero();
        if (metric is not null)
        {
            cost[metric] = value;
        }

        return cost;
    }

    private static JsonObject Binding(string account = Account, string? observedAt = null) => new()
    {
        ["account"] = account,
        ["host"] = "synthetic-cli",
        ["capabilities"] = new JsonObject { ["mail.read"] = "v1", ["local.prepare"] = "v1", ["mail.reply"] = "v1" },
        ["observed_at"] = observedAt ?? Stamp(),
    };

    private static JsonObject ReadStep(string key = "mail")
    {
        var cost = Zero("tool_calls", 1);
        cost["pages"] = 1;
        cost["items"] = 5;
        return new JsonObject
        {
            ["key"] = key,
            ["title"] = "Read synthetic mail",
            ["kind"] = "read",
            ["capability"] = "mail.read",
            ["depends_on"] = new JsonArray(),
            ["allow_partial"] = false,
            ["cost"] = cost,
            ["source"] = new JsonObject { ["family"] = "mail", ["scope"] = new JsonObject { ["folder"] = key }, ["kind"] = "enumeration", ["query_version"] = "v1" },
        };
    }

    private static JsonObject LocalStep(string key = "prepare") => new()
    {
        ["key"] = key,
        ["title"] = "Prepare private output",
        ["kind"] = "local",
        ["capability"] = "local.prepare",
        ["depends_on"] = new JsonArray(),
        ["allow_partial"] = false,
        ["cost"] = Zero("output_chars", 1000),
    };

    private static JsonObject Plan(JsonArray steps, long leaseSeconds = 30, long maxParallel = 3, string account = Account)
    {
        var limits = new JsonObject
        {
            ["max_steps"] = 10,
            ["max_attempts_per_step"] = 3,
            ["max_parallel"] = maxParallel,
            ["tool_calls"] = 10,
            ["pages"] = 10,
            ["items"] = 100,
            ["model_calls"] = 2,
            ["output_chars"] = 10000,
            ["deadline_at"] = Stamp(600),
            ["lease_seconds"] = leaseSeconds,
        };
        return new JsonObject
        {
            ["goal"] = "Prepare a useful synthetic result",
            ["routine"] = "daily-brief",
            ["request_ref"] = "conversation:synthetic-task-test",
            ["mode"] = "foreground",
            ["environment"] = Binding(account),
            ["window"] = new JsonObject { ["start"] = Stamp(-3600), ["end"] = Stamp() },
            ["limits"] = limits,
            ["steps"] = steps,
        };
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    [Fact]
    public void Schema_is_byte_identical_to_the_python_schema()
    {
        var python = File.ReadAllText(Path.Combine(RepositoryRoot(), "skills", "chief-of-staff", "scripts", "task_runs.py"));
        var start = python.IndexOf("SCHEMA = \"\"\"", StringComparison.Ordinal) + "SCHEMA = \"\"\"".Length;
        var end = python.IndexOf("\"\"\"", start, StringComparison.Ordinal);
        Assert.Equal(python[start..end], TaskRuns.Schema);
    }

    [Fact]
    public void Margo_state_discovers_the_task_scripts()
    {
        Assert.Contains("task_state.py", StateCli.StateCliHost.Scripts.Keys);
        Assert.Contains("tasks_probe.py", StateCli.StateCliHost.Scripts.Keys);
    }

    [Fact]
    public async Task Parallel_claims_reserve_global_budget_and_one_step_has_one_winner()
    {
        // test_parallel_claims_reserve_global_budget_and_one_step_has_one_winner: two connections race for one claim.
        using var root = TempRoot.Create();
        JsonObject run;
        using (var store = new TaskStore(Account, root.State))
        {
            run = store.Create("parallel", Plan([LocalStep()], maxParallel: 1));
        }

        var binding = Binding();
        using var barrier = new Barrier(2);
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            using var worker = new TaskStore(Account, root.State);
            barrier.SignalAndWait();
            try
            {
                return (string?)worker.Start((string)run["id"]!, "prepare", 1, (string)run["plan_hash"]!, PyNode.Copy(binding))["attempt_id"];
            }
            catch (StateError)
            {
                return null;
            }
        })));
        Assert.Single(results, result => result is not null);
        using var check = new TaskStore(Account, root.State);
        var shown = check.Show((string)run["id"]!);
        Assert.Equal(1L, (long)shown["steps"]![0]!["attempts"]!);
        Assert.Equal(1000L, (long)shown["usage"]!["output_chars"]!);
    }

    [Fact]
    public void Read_views_never_initialize_and_account_ids_cannot_cross()
    {
        // test_read_views_never_initialize_and_account_ids_cannot_cross
        using var root = TempRoot.Create();
        var missing = root.Combine("missing");
        Assert.ThrowsAny<NotInitialized>(() => new TaskStore(Account, missing, readOnly: true));
        Assert.False(Directory.Exists(missing));
        string id;
        string planHash;
        using (var store = new TaskStore(Account, root.State))
        {
            var run = store.Create("isolation", Plan([ReadStep()]));
            id = (string)run["id"]!;
            planHash = (string)run["plan_hash"]!;
        }

        using (var other = new TaskStore("another-fixture@example.com", root.State))
        {
            Assert.Equal("unknown task run", Assert.ThrowsAny<StateError>(() => other.Show(id)).Message);
        }

        using var reader = new TaskStore(Account, root.State, readOnly: true);
        Assert.Equal(planHash, (string?)reader.Show(id)["plan_hash"]);
        var error = Assert.Throws<PySqliteError>(() => reader.Conn.Execute("DELETE FROM task_runs"));
        Assert.Equal("OperationalError", error.PythonType);
        Assert.Throws<PySqliteError>(() => reader.Pause(id, "Read-only pause"));
    }

    [Fact]
    public void Nested_ledger_and_coverage_operations_roll_back_with_the_caller()
    {
        // test_nested_ledger_and_coverage_operations_rollback_with_the_caller
        using var root = TempRoot.Create();
        using var store = new TaskStore(Account, root.State);
        var before = (long)store.Conn.Execute("SELECT count(*) FROM work_sources").FetchOne()![0]!;
        Assert.Throws<InvalidOperationException>(() => store.Transaction(() =>
        {
            store.Ledger.Source("mail", "test", "temporary", "v1", new JsonObject { ["quote"] = "Synthetic" }, "https://example.com/test");
            ProactiveState.CoverageStart(store.Conn, new JsonObject
            {
                ["family"] = "mail",
                ["scope"] = new JsonObject { ["folder"] = "synthetic" },
                ["run_id"] = "synthetic-rollback",
                ["capability"] = "mail.read",
                ["query_version"] = "v1",
                ["window"] = new JsonObject { ["start"] = Stamp(-60), ["end"] = Stamp() },
            });
            store.Create("rolled-back", Plan([LocalStep()]));
            throw new InvalidOperationException("abort synthetic outer transaction");
        }));
        Assert.Equal(before, (long)store.Conn.Execute("SELECT count(*) FROM work_sources").FetchOne()![0]!);
        Assert.Equal(0L, (long)store.Conn.Execute("SELECT count(*) FROM proactive_attempts WHERE run_id='synthetic-rollback'").FetchOne()![0]!);
        Assert.Equal(0L, (long)store.Conn.Execute("SELECT count(*) FROM task_runs").FetchOne()![0]!);
    }

    [Fact]
    public void Borrowed_store_composes_a_claim_inside_the_callers_transaction()
    {
        // memory_dream.py borrows the memory store's connection: TaskStore.from_connection inside its own transaction.
        using var root = TempRoot.Create();
        using (new TaskStore(Account, root.State))
        {
        }

        using var connection = StateStore.Connect(Account, root.State);
        var tasks = TaskStore.FromConnection(connection, Account);
        var outcome = StateStore.Transaction(connection, () =>
        {
            var run = tasks.Create("dream:synthetic", Plan([LocalStep("reflect")]));
            var claim = tasks.Start((string)run["id"]!, "reflect", 1, (string)run["plan_hash"]!, Binding());
            var grant = tasks.Charge((string)claim["attempt_id"]!, (string)claim["token"]!, "host-reflection", Zero("output_chars", 10));
            Assert.True((bool)grant["execute"]!);
            return tasks.Finish((string)claim["attempt_id"]!, (string)claim["token"]!, "succeeded", new JsonObject
            {
                ["kind"] = "local_result",
                ["reference"] = "private:reflection",
                ["summary"] = "Reflected.",
            });
        });
        Assert.Equal("succeeded", (string?)outcome["status"]);
        Assert.Equal("completed", (string?)outcome["state"]);
        tasks.Close();
        Assert.False(connection.IsClosed);
        Assert.Throws<StateError>(() => TaskStore.FromConnection(connection, "another-fixture@example.com"));
    }

    [Fact]
    public void Expired_read_is_recoverable_but_obeys_backoff()
    {
        // test_expired_read_is_recoverable_but_obeys_backoff, with TaskRuns.OverrideClock for patch("task_runs.clock").
        using var root = TempRoot.Create();
        using var store = new TaskStore(Account, root.State);
        var binding = Binding();
        var run = store.Create("recover-read", Plan([ReadStep()], leaseSeconds: 1));
        store.Start((string)run["id"]!, "mail", 1, (string)run["plan_hash"]!, binding);
        JsonNode step;
        using (TaskRuns.OverrideClock(() => PyDateTime.Now() + PyTimeDelta.FromSeconds(2)))
        {
            var recovered = store.Recover((string)run["id"]!);
            step = recovered["steps"]![0]!;
            Assert.Equal("failed", (string?)step["state"]);
            // The durable coverage error (the timeout recovery recorded) replaces the reported "interrupted".
            Assert.Equal("timeout", (string?)step["result"]!["error_class"]);
            var early = Assert.Throws<StateError>(() => store.Retry((string)run["id"]!, "mail", PyNode.Copy(step["revision"]), binding));
            Assert.Equal("provider/backoff retry time has not arrived", early.Message);
        }

        var retryTime = PyDateTime.FromIsoformat((string)step["next_retry_at"]!) + PyTimeDelta.FromSeconds(1);
        using (TaskRuns.OverrideClock(() => retryTime))
        {
            var result = store.Retry((string)run["id"]!, "mail", PyNode.Copy(step["revision"]), binding);
            Assert.Equal("pending", (string?)result["steps"]![0]!["state"]);
        }
    }

    [Fact]
    public async Task Clock_override_changes_only_the_task_clock_of_its_own_flow()
    {
        var fixedAt = PyDateTime.FromIsoformat("2030-01-01T00:00:00+00:00");
        using (TaskRuns.OverrideClock(() => fixedAt))
        {
            Assert.Equal(fixedAt, TaskRuns.Clock());
            Assert.NotEqual(fixedAt.Year, PyDateTime.FromIsoformat(StateStore.UtcNow()).Year);
            var other = await Task.Run(() =>
            {
                using (TaskRuns.OverrideClock(() => PyDateTime.FromIsoformat("2031-01-01T00:00:00+00:00")))
                {
                    return TaskRuns.Clock();
                }
            });
            Assert.Equal(2031, other.Year);
            Assert.Equal(fixedAt, TaskRuns.Clock());
        }

        Assert.NotEqual(fixedAt, TaskRuns.Clock());
    }

    [Fact]
    public void Claim_tokens_are_stored_only_as_digests()
    {
        using var root = TempRoot.Create();
        using var store = new TaskStore(Account, root.State);
        var run = store.Create("tokens", Plan([LocalStep()]));
        var claim = store.Start((string)run["id"]!, "prepare", 1, (string)run["plan_hash"]!, Binding());
        var token = (string)claim["token"]!;
        Assert.Equal(48, token.Length);
        var stored = (string)store.Conn.Execute("SELECT token_hash FROM task_attempts").FetchOne()![0]!;
        Assert.Equal(PyIds.Sha256Hex(token), stored);
        Assert.DoesNotContain(token, StateStore.CanonicalJson(store.History((string)run["id"]!)), StringComparison.Ordinal);
        Assert.Equal("attempt claim token does not match",
                     Assert.Throws<StateError>(() => store.Charge((string)claim["attempt_id"]!, token.ToUpperInvariant(), "x", Zero("output_chars", 1))).Message);
    }

    [Fact]
    public void Cli_runs_the_full_local_workflow_in_process()
    {
        // tests/test_task_cli.py::test_read_setup_and_full_local_workflow_across_processes
        const string account = "task-cli-fixture@example.com";
        using var root = TempRoot.Create(new Dictionary<string, string>
        {
            ["MARGO_ACCOUNT"] = account,
        });
        using var environment = TempRoot.With(("MARGO_STATE_DIR", root.State), ("COPILOT_HOME", root.Combine("home")));
        var script = new TaskStateScript();
        JsonObject Call(string stdin, params string[] args)
        {
            var (code, stdout, stderr) = ScriptRunner.Run(script, args, stdin);
            Assert.True(code == 0, stderr);
            return (JsonObject)PyJson.Loads(stdout)!;
        }

        var (listed, _, error) = ScriptRunner.Run(script, ["list"]);
        Assert.Equal(2, listed);
        Assert.Equal("not_initialized", (string?)PyJson.Loads(error)!["code"]);
        Assert.False(Directory.Exists(root.State));
        var identity = (string)Call("", "record-id", "note")["id"]!;
        Assert.False(Directory.Exists(root.State));
        Assert.False((bool)Call("", "init")["collected"]!);
        var environmentBinding = new JsonObject
        {
            ["account"] = account,
            ["host"] = "synthetic-cli",
            ["capabilities"] = new JsonObject { ["local.prepare"] = "v1" },
            ["observed_at"] = Stamp(),
        };
        var limits = TaskRuns.Zero();
        limits["max_steps"] = 2;
        limits["max_attempts_per_step"] = 2;
        limits["max_parallel"] = 1;
        limits["output_chars"] = 4000;
        limits["deadline_at"] = Stamp(600);
        limits["lease_seconds"] = 60;
        var plan = new JsonObject
        {
            ["goal"] = "Prepare a private synthetic note",
            ["routine"] = "work-products",
            ["request_ref"] = "conversation:synthetic-cli",
            ["mode"] = "foreground",
            ["environment"] = environmentBinding,
            ["window"] = new JsonObject { ["start"] = Stamp(-60), ["end"] = Stamp() },
            ["limits"] = limits,
            ["steps"] = new JsonArray(new JsonObject
            {
                ["key"] = "prepare",
                ["title"] = "Prepare note",
                ["kind"] = "local",
                ["capability"] = "local.prepare",
                ["depends_on"] = new JsonArray(),
                ["allow_partial"] = false,
                ["cost"] = Zero("output_chars", 2000),
            }),
        };
        var run = Call(StateStore.CanonicalJson(plan), "create", "note", "--input", "-");
        Assert.Equal(identity, (string?)run["id"]);
        var paused = Call("", "pause", identity, "--reason", "Review scope");
        Assert.Empty((JsonArray)paused["ready_steps"]!);
        Call(StateStore.CanonicalJson(new JsonObject { ["run_id"] = identity, ["revision"] = PyNode.Copy(paused["revision"]), ["binding"] = PyNode.Copy(environmentBinding) }),
             "resume", "--input", "-");
        run = Call("", "show", identity);
        var claim = Call(StateStore.CanonicalJson(new JsonObject
        {
            ["run_id"] = identity,
            ["step_key"] = "prepare",
            ["revision"] = 1,
            ["plan_hash"] = PyNode.Copy(run["plan_hash"]),
            ["binding"] = PyNode.Copy(environmentBinding),
        }), "start", "--input", "-");
        var done = Call(StateStore.CanonicalJson(new JsonObject
        {
            ["attempt_id"] = PyNode.Copy(claim["attempt_id"]),
            ["token"] = PyNode.Copy(claim["token"]),
            ["outcome"] = "succeeded",
            ["result"] = new JsonObject { ["kind"] = "local_result", ["reference"] = "test-fixture:note", ["summary"] = "A private fixture note." },
        }), "finish", "--input", "-");
        Assert.Equal("succeeded", (string?)done["status"]);
        var history = Call("", "history", identity);
        Assert.DoesNotContain((string)claim["token"]!, StateStore.CanonicalJson(history), StringComparison.Ordinal);
        var all = Call("", "list");
        Assert.Equal(plan["goal"]!.GetValue<string>(), (string?)all["runs"]![0]!["goal"]);
        Assert.False(((JsonObject)all["runs"]![0]!).ContainsKey("plan"));
        Assert.Equal(0L, (long)Call("", "health")["unreconciled_attempts"]!);
    }

    [Fact]
    public void Input_files_are_read_like_a_text_wrapper()
    {
        using var root = TempRoot.Create();
        var late = root.Combine("late.json");
        File.WriteAllBytes(late, [.. Enumerable.Repeat((byte)' ', 262145), 0xFF]);
        Assert.Equal(262145, TaskStateScript.ReadFileText(late, 262145).Length);

        var chunked = root.Combine("chunked.json");
        File.WriteAllBytes(chunked, [.. Encoding.UTF8.GetBytes(new string('é', 131072) + "a" + new string(' ', 10)), 0xFF]);
        var error = Assert.Throws<PyUnicodeError>(() => TaskStateScript.ReadFileText(chunked, 262145));
        Assert.Contains("position 10:", error.Message, StringComparison.Ordinal);

        var newlines = root.Combine("newlines.json");
        File.WriteAllBytes(newlines, Encoding.UTF8.GetBytes(new string(' ', 8191) + "\r\n\r" + "x"));
        Assert.Equal(new string(' ', 8191) + "\n\nx", TaskStateScript.ReadFileText(newlines, 262145));
        Assert.Equal(new string(' ', 8191) + "\n", TaskStateScript.ReadFileText(newlines, 8192));
    }
}

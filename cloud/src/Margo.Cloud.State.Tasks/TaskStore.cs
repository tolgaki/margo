using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Proactive;
using Margo.Cloud.State.Work;
using static Margo.Cloud.State.Tasks.TaskRuns;

namespace Margo.Cloud.State.Tasks;

/// <summary>
/// Port of <c>task_runs.TaskStore</c>: durable bounded task progress over the existing work, coverage and receipt
/// journals. Plans, steps, attempts (claims with lease tokens), charges against reservations, settlement with receipts,
/// pause/cancel/resume/retry, expired-claim recovery, action-journal synchronisation, reconciliation, reviewed
/// replanning and the read views (<c>show</c>, <c>list</c>, <c>health</c>, <c>history</c>). Values are Python values
/// as JSON nodes (<see cref="PyJson"/>'s value model); parameters that Python takes from model-supplied JSON are
/// <see cref="JsonNode"/>s, so strings and ints convert implicitly. Method names follow the Python ones.
/// </summary>
public sealed class TaskStore : IDisposable
{
    private static readonly IReadOnlySet<string> PlanKeys =
        Set("goal", "routine", "request_ref", "mode", "environment", "window", "limits", "steps");

    private static readonly IReadOnlySet<string> LimitKeys =
        Set("tool_calls", "pages", "items", "model_calls", "output_chars", "max_steps", "max_attempts_per_step", "max_parallel",
            "deadline_at", "lease_seconds");

    private static readonly IReadOnlySet<string> StepBasic = Set("key", "title", "kind", "capability", "depends_on", "allow_partial", "cost");
    private static readonly IReadOnlySet<string> StepOptional = Set("source", "action_id", "action_revision", "action_hash");
    private static readonly IReadOnlySet<string> SourceRequired = Set("family", "scope", "kind", "query_version");
    private static readonly IReadOnlySet<string> SourceAllowed = Set("family", "scope", "kind", "query_version", "reauthenticated", "reactivate");
    private static readonly IReadOnlySet<string> SourceKinds = Set("enumeration", "snapshot", "search");
    private static readonly IReadOnlySet<string> Modes = Set("foreground", "unattended");
    private static readonly IReadOnlySet<string> WindowKeys = Set("start", "end");
    private static readonly IReadOnlySet<string> Outcomes = Set("succeeded", "partial", "failed", "outcome_unknown");
    private static readonly IReadOnlySet<string> KnownOutcomes = Set("succeeded", "failed", "partial");
    private static readonly IReadOnlySet<string> ResultFields =
        Set("kind", "reference", "summary", "coverage", "coverage_attempt_id", "source_refs", "work_ids", "publication_id", "error_class",
            "retry_after_seconds", "definitive_no_effect");

    private static readonly IReadOnlySet<string> RevisionKeys = Set("revision", "action_revision");
    private static readonly IReadOnlySet<string> TokenErrors = Set("expired_token", "access_denied", "authentication_required");
    private static readonly string[] ActionNames = ["action_id", "action_revision", "action_hash"];
    private static readonly string[] SchemaTables = ["task_attempts", "task_charges", "task_events", "task_meta", "task_runs", "task_steps"];
    private static readonly string[] SchemaPragmas = ["table_info", "foreign_key_list"];

    private readonly bool _ownsConnection;
    private bool _readOnly;

    /// <summary>
    /// <c>TaskStore(account=None, state_root=None, read_only=False)</c>: opens the account's store. A writable store
    /// creates (or validates) the work ledger, task and proactive schemas; a read-only store never creates anything
    /// and raises <see cref="NotInitialized"/> before explicit initialization.
    /// </summary>
    public TaskStore(string? account = null, string? stateRoot = null, bool readOnly = false)
    {
        Account = StateStore.ResolveAccount(account);
        _readOnly = readOnly;
        _ownsConnection = true;
        PyConnection? connection = null;
        try
        {
            if (readOnly)
            {
                connection = StateStore.Connect(Account, stateRoot, readOnly: true);
                Ledger = Ledger.FromConnection(connection, Account);
            }
            else
            {
                Ledger = new Ledger(Account, stateRoot);
                connection = Ledger.Conn;
            }

            Conn = connection;
            Initialize();
        }
        catch
        {
            connection?.Close();
            throw;
        }
    }

    private TaskStore(string account, PyConnection connection, Ledger ledger)
    {
        Account = account;
        Conn = connection;
        Ledger = ledger;
        _ownsConnection = false;
        _readOnly = true;
    }

    /// <summary>The store owner's principal.</summary>
    public string Account { get; }

    /// <summary>The account connection (<c>self.conn</c>).</summary>
    public PyConnection Conn { get; }

    /// <summary>The work ledger on the same connection (<c>self.ledger</c>).</summary>
    public Ledger Ledger { get; }

    /// <summary>
    /// <c>TaskStore.from_connection(conn, account)</c>: borrows initialized account state without creating or repairing
    /// namespaces (memory_dream.py composes it inside its own transaction).
    /// </summary>
    public static TaskStore FromConnection(PyConnection connection, string? account)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var principal = StateStore.ResolveAccount(account);
        var store = new TaskStore(principal, connection, Ledger.FromConnection(connection, principal));
        store.Initialize();
        store._readOnly = false;
        return store;
    }

    /// <summary><c>close()</c>: closes the connection unless it was borrowed.</summary>
    public void Close()
    {
        if (_ownsConnection)
        {
            Conn.Close();
        }
    }

    public void Dispose() => Close();

    /// <summary><c>with store.transaction(): ...</c> (read-only stores open a deferred transaction).</summary>
    public T Transaction<T>(Func<T> body) => StateStore.Transaction(Conn, body, _readOnly);

    /// <summary><c>with store.transaction(): ...</c>.</summary>
    public void Transaction(Action body) => StateStore.Transaction(Conn, body, _readOnly);

    // ================================================================================ schema and rows

    private void Initialize()
    {
        Transaction(() =>
        {
            var tables = new HashSet<object?>(Conn.Execute("SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'task_%'")
                                                  .Select(row => row[0]));
            var marker = Conn.Execute("SELECT value FROM margo_meta WHERE key='task_schema_version'").FetchOne();
            if (tables.Count == 0 && marker is null && _readOnly)
            {
                throw new NotInitialized("Task state is not initialized; run task_state.py init explicitly.");
            }

            if (tables.Count > 0 || marker is not null)
            {
                if (!tables.SetEquals(SchemaTables) || marker is null || !PyObj.Eq(marker[0], "1"))
                {
                    throw new StateError("incomplete or incompatible task schema; explicit recovery required");
                }

                var local = Conn.Execute("SELECT value FROM task_meta WHERE key='schema_version'").FetchOne();
                if (local is null || !PyObj.Eq(local[0], "1"))
                {
                    throw new StateError("task schema marker mismatch");
                }

                using var reference = PyConnection.Connect(":memory:");
                reference.ExecuteScript(Schema);
                foreach (var table in SchemaTables)
                {
                    foreach (var pragma in SchemaPragmas)
                    {
                        var actual = Conn.Execute("PRAGMA " + pragma + "(" + table + ")").FetchAll();
                        var wanted = reference.Execute("PRAGMA " + pragma + "(" + table + ")").FetchAll();
                        if (actual.Count != wanted.Count || actual.Zip(wanted).Any(pair => !pair.First.ValuesEqual(pair.Second)))
                        {
                            throw new StateError("task schema contract mismatch: " + table);
                        }
                    }
                }
            }

            if (!_readOnly)
            {
                foreach (var statement in Schema.Split(';'))
                {
                    if (PyRepr.Strip(statement).Length > 0)
                    {
                        Conn.Execute(statement);
                    }
                }

                Conn.Execute("INSERT OR IGNORE INTO task_meta VALUES('schema_version','1')");
                Conn.Execute("INSERT OR IGNORE INTO margo_meta VALUES('task_schema_version','1')");
                ProactiveState.Initialize(Conn);
            }
        });
    }

    private PyRow RunRow(JsonNode? runId) =>
        Conn.Execute("SELECT * FROM task_runs WHERE id=? AND account=?", runId, Account).FetchOne()
        ?? throw new StateError("unknown task run");

    private PyRow StepRow(JsonNode? runId, JsonNode? key)
    {
        RunRow(runId);
        return Conn.Execute("SELECT * FROM task_steps WHERE run_id=? AND step_key=?", runId, key).FetchOne()
               ?? throw new StateError("unknown task step");
    }

    private PyRow AttemptRow(JsonNode? attemptId) =>
        Conn.Execute("SELECT a.* FROM task_attempts a JOIN task_runs r ON r.id=a.run_id WHERE a.id=? AND r.account=?",
                     attemptId, Account).FetchOne()
        ?? throw new StateError("unknown task attempt");

    private static JsonNode? Sql(object? value) => PyNode.FromSql(value);

    private static string Str(object? value) => (string)value!;

    /// <summary><c>parse_json(text)</c> of a stored column (<c>json.loads(None)</c> is <c>TypeError</c>, reported as invalid JSON).</summary>
    private static JsonNode? Parse(object? value) => value is string text ? StateStore.ParseJson(text) : throw new StateError("invalid JSON");

    private static JsonObject ParseObject(object? value) => (JsonObject)Parse(value)!;

    private static BigInteger Int(JsonNode? value) => PyJson.AsInt(value);

    private static BigInteger Int(object? value) => PyObj.Int(value);

    private void Event(JsonNode? runId, string eventName, JsonObject data)
    {
        Conn.Execute("UPDATE task_runs SET revision=revision+1,updated_at=? WHERE id=?", StateStore.UtcNow(), runId);
        var revision = RunRow(runId)["revision"];
        Conn.Execute("INSERT INTO task_events(run_id,revision,event,data,created_at) VALUES(?,?,?,?,?)",
                     runId, revision, eventName, StateStore.CanonicalJson(data), StateStore.UtcNow());
        var run = RunRow(runId);
        var plan = ParseObject(run["plan"]);
        var usage = ParseObject(run["usage"]);
        var keys = ((JsonArray)plan["steps"]!).Select(step => Str(PyNode.AsString(step!["key"]))).ToList();
        var states = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in Conn.Execute("SELECT step_key,state FROM task_steps WHERE run_id=?", runId))
        {
            states[Str(row["step_key"])] = Str(row["state"]);
        }

        var limits = (JsonObject)plan["limits"]!;
        if (PyObj.Eq(run["state"], "active") && keys.All(key => states.GetValueOrDefault(key) == "succeeded")
            && Metrics.All(metric => Int(usage[metric]) <= Int(limits[metric])))
        {
            Conn.Execute("UPDATE task_runs SET state='completed' WHERE id=?", runId);
        }
    }

    // ================================================================================ plans

    private static bool InCapabilities(JsonNode? capability, JsonObject capabilities)
    {
        PyNode.RequireHashable(capability);
        return PyNode.AsString(capability) is { } name && capabilities.ContainsKey(name);
    }

    private JsonObject PlanData(JsonNode? value)
    {
        if (value is not JsonObject obj || !KeySet(obj).SetEquals(PlanKeys))
        {
            throw new StateError("task plan requires exactly environment, goal, limits, mode, request_ref, routine, steps, window");
        }

        Text(obj["goal"], "goal");
        Text(obj["routine"], "routine", 100);
        var request = Text(obj["request_ref"], "request_ref", 1000);
        if (!request.StartsWith("conversation:", StringComparison.Ordinal) && !request.StartsWith("host-interaction:", StringComparison.Ordinal)
            && !request.StartsWith("automation:", StringComparison.Ordinal))
        {
            throw new StateError("task request must reference a user interaction or automation, not observed source instructions");
        }

        if (!PyNode.In(obj["mode"], Modes))
        {
            throw new StateError("task mode must be foreground or unattended");
        }

        var unattended = PyNode.Is(obj["mode"], "unattended");
        if (unattended && !request.StartsWith("automation:", StringComparison.Ordinal))
        {
            throw new StateError("unattended task requires its automation request reference");
        }

        var env = TaskRuns.Environment(obj["environment"], Account);
        var capabilities = (JsonObject)env["capabilities"]!;
        if (obj["window"] is not JsonObject window || !KeySet(window).SetEquals(WindowKeys) || When(window["start"]) >= When(window["end"]))
        {
            throw new StateError("task window requires explicit ordered start/end timestamps");
        }

        if (When(window["end"]) - When(window["start"]) > PyTimeDelta.FromDays(366))
        {
            throw new StateError("task source window exceeds one year");
        }

        if (obj["limits"] is not JsonObject given || !KeySet(given).SetEquals(LimitKeys))
        {
            throw new StateError("task limits must explicitly bound resources, attempts, parallelism and time");
        }

        var metrics = new JsonObject();
        foreach (var key in Metrics)
        {
            metrics[key] = PyNode.Copy(given[key]);
        }

        var cost = ResourceCost(metrics);
        var limits = PyNode.CopyObject(given);
        foreach (var (key, number) in cost.ToList())
        {
            limits[key] = PyNode.Copy(number);
        }

        foreach (var (key, maximum) in new[] { ("max_steps", 100L), ("max_attempts_per_step", 10L), ("max_parallel", 10L), ("lease_seconds", 900L) })
        {
            Integer(limits[key], key, 1, maximum);
        }

        limits["deadline_at"] = StateStore.ValidateTimestamp(limits["deadline_at"]);
        if (When(limits["deadline_at"]) <= Clock() || When(limits["deadline_at"]) > Clock() + PyTimeDelta.FromDays(7))
        {
            throw new StateError("task deadline must be future and within seven days");
        }

        if (obj["steps"] is not JsonArray steps || steps.Count < 1 || steps.Count > Int(limits["max_steps"]))
        {
            throw new StateError("task needs a nonempty step list within max_steps");
        }

        var normalized = new List<JsonObject>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in steps)
        {
            if (node is not JsonObject step)
            {
                throw new StateError("step must be an object");
            }

            var fields = KeySet(step);
            if (!StepBasic.IsSubsetOf(fields) || fields.Any(name => !StepBasic.Contains(name) && !StepOptional.Contains(name)))
            {
                throw new StateError("step has missing or unsupported fields");
            }

            var key = Text(step["key"], "step key", 100);
            if (!keys.Add(key))
            {
                throw new StateError("duplicate step key");
            }

            Text(step["title"], "step title", 300);
            if (!PyNode.In(step["kind"], Kinds) || !InCapabilities(step["capability"], capabilities))
            {
                throw new StateError("step kind/capability is not available in the declared binding");
            }

            var kind = PyNode.AsString(step["kind"])!;
            if (step["depends_on"] is not JsonArray dependencies || !dependencies.All(item => PyNode.AsString(item) is not null)
                || dependencies.Select(item => PyNode.AsString(item)!).Distinct(StringComparer.Ordinal).Count() != dependencies.Count)
            {
                throw new StateError("step dependencies must be distinct keys");
            }

            if (!PyJson.IsBool(step["allow_partial"]) || (PyJson.Truthy(step["allow_partial"]) && kind != "local"))
            {
                throw new StateError("only local preparation may explicitly use partial prerequisites");
            }

            var item = PyNode.CopyObject(step);
            item["depends_on"] = PyNode.Copy(dependencies);
            item["cost"] = ResourceCost(step["cost"]);
            var itemCost = (JsonObject)item["cost"]!;
            if (kind is "read" or "action" && Int(itemCost["tool_calls"]) < 1)
            {
                throw new StateError("a remote read/action must reserve at least one tool call");
            }

            if (Metrics.Any(metric => Int(itemCost[metric]) > Int(limits[metric])))
            {
                throw new StateError("step reservation exceeds the run resource limit");
            }

            if (kind == "read")
            {
                var source = PyNode.Get(step, "source");
                if (source is not JsonObject contract || !SourceRequired.IsSubsetOf(KeySet(contract))
                    || KeySet(contract).Any(name => !SourceAllowed.Contains(name)))
                {
                    throw new StateError("read step requires a bounded source collection contract");
                }

                Text(contract["family"], "source family", 100);
                Text(contract["query_version"], "source query version", 200);
                if (contract["scope"] is not JsonObject scope || scope.Count == 0)
                {
                    throw new StateError("source scope must explicitly identify the collection");
                }

                if (!PyNode.In(contract["kind"], SourceKinds))
                {
                    throw new StateError("unsupported source collection kind");
                }

                foreach (var flag in new[] { "reauthenticated", "reactivate" })
                {
                    var given2 = PyNode.Get(contract, flag, PyNode.Of(false));
                    if (!PyJson.IsBool(given2) || (unattended && PyJson.Truthy(PyNode.Get(contract, flag))))
                    {
                        throw new StateError("source reauthentication/reactivation is an explicit foreground operation");
                    }
                }

                if (ActionNames.Any(step.ContainsKey))
                {
                    throw new StateError("read steps cannot hide an action reference");
                }
            }
            else if (step.ContainsKey("source"))
            {
                throw new StateError("only read steps use a source collection contract");
            }

            if (kind == "action")
            {
                if (!PyNode.Is(obj["mode"], "foreground"))
                {
                    throw new StateError("unattended tasks may prepare proposals, never execute actions");
                }

                var action = Ledger.Show(PyNode.Get(step, "action_id"));
                if (!PyNode.Is(PyNode.Get(action, "type"), "action")
                    || !PyJson.PyEquals(PyNode.Index(action, "revision"), PyNode.Get(step, "action_revision"))
                    || !PyJson.PyEquals(PyNode.Index(action, "action_hash"), PyNode.Get(step, "action_hash")))
                {
                    throw new StateError("action step must bind an exact current action revision/hash");
                }
            }
            else if (ActionNames.Any(step.ContainsKey))
            {
                throw new StateError("only action steps may bind external execution");
            }

            normalized.Add(item);
        }

        var byKey = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var item in normalized)
        {
            byKey[PyNode.AsString(item["key"])!] = item;
        }

        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        void Visit(string key)
        {
            if (!byKey.TryGetValue(key, out var item) || visiting.Contains(key))
            {
                throw new StateError("task dependencies are missing or cyclic");
            }

            if (visited.Contains(key))
            {
                return;
            }

            visiting.Add(key);
            foreach (var dependency in (JsonArray)item["depends_on"]!)
            {
                Visit(PyNode.AsString(dependency)!);
            }

            visiting.Remove(key);
            visited.Add(key);
        }

        foreach (var key in keys)
        {
            Visit(key);
        }

        var result = PyNode.CopyObject(obj);
        result["environment"] = env;
        result["limits"] = limits;
        result["steps"] = new JsonArray(normalized.Select(item => (JsonNode?)item).ToArray());
        result["window"] = new JsonObject
        {
            ["start"] = StateStore.ValidateTimestamp(window["start"]),
            ["end"] = StateStore.ValidateTimestamp(window["end"]),
        };
        if (PyRepr.Length(StateStore.CanonicalJson(result)) > 100000)
        {
            throw new StateError("task plan exceeds its storage budget");
        }

        return result;
    }

    /// <summary>
    /// <c>create(key, plan)</c>: stores a bounded private plan under <c>task_identity(account, key)</c>; the same plan is
    /// idempotent, a different one raises. Not an external-action approval.
    /// </summary>
    public JsonObject Create(JsonNode? key, JsonNode? plan)
    {
        Text(key, "task identity", 500);
        return Transaction(() =>
        {
            var normalized = PlanData(plan);
            var keyHash = WorkLedger.Digest(new JsonArray(Account, PyNode.Copy(key)));
            var runId = TaskIdentity(Account, key);
            var previous = Conn.Execute("SELECT * FROM task_runs WHERE id=? AND account=?", runId, Account).FetchOne();
            var encoded = StateStore.CanonicalJson(normalized);
            if (previous is not null)
            {
                if (!PyObj.Eq(previous["plan"], encoded))
                {
                    throw new StateError("task identity " + runId + " already has a different plan; inspect or explicitly replan it");
                }

                return Show(runId);
            }

            var stamp = StateStore.UtcNow();
            Conn.Execute("INSERT INTO task_runs VALUES(?,?,?,1,'planned',?,?,?,?,?)",
                         runId, Account, keyHash, encoded, WorkLedger.Digest(normalized), StateStore.CanonicalJson(Zero()), stamp, stamp);
            foreach (var step in (JsonArray)normalized["steps"]!)
            {
                Conn.Execute("INSERT INTO task_steps VALUES(?,?,1,'pending',?,0,NULL,NULL,NULL,?)",
                             runId, step!["key"], StateStore.CanonicalJson(step), stamp);
            }

            Event(runId, "created", new JsonObject
            {
                ["request_ref"] = PyNode.Copy(normalized["request_ref"]),
                ["mode"] = PyNode.Copy(normalized["mode"]),
            });
            return Show(runId);
        });
    }

    private void Binding(JsonObject plan, JsonNode? supplied)
    {
        var actual = TaskRuns.Environment(supplied, Account, fresh: true);
        if (BindingHash(actual) != BindingHash(plan["environment"]))
        {
            throw new StateError("task capability/host binding changed; pause and review a new plan");
        }
    }

    private List<string> Prerequisites(JsonNode? runId, JsonObject definition)
    {
        var reasons = new List<string>();
        foreach (var keyNode in (JsonArray)definition["depends_on"]!)
        {
            var key = PyNode.AsString(keyNode)!;
            var step = StepRow(runId, keyNode);
            var accepted = PyJson.Truthy(definition["allow_partial"]) ? Set("succeeded", "partial", "failed") : Set("succeeded");
            var state = Str(step["state"]);
            if (!accepted.Contains(state))
            {
                reasons.Add("prerequisite " + key + " is " + state);
            }

            var result = PyObj.Truthy(step["result"]) ? Parse(step["result"]) : new JsonObject();
            var refs = PyNode.Get(PyNode.Dict(result), "source_refs");
            if (PyJson.Truthy(refs) && Ledger.RefsStale(refs))
            {
                reasons.Add("prerequisite " + key + " has changed source evidence; add a fresh read in a reviewed plan");
            }
        }

        return reasons;
    }

    private static List<string> BudgetReasons(PyRow run, JsonObject? reservation = null)
    {
        var plan = ParseObject(run["plan"]);
        var usage = ParseObject(run["usage"]);
        var limits = (JsonObject)plan["limits"]!;
        var reasons = new List<string>();
        if (When(limits["deadline_at"]) <= Clock())
        {
            reasons.Add("task deadline expired");
        }

        foreach (var metric in Metrics)
        {
            var reserved = reservation is not null && reservation.TryGetPropertyValue(metric, out var value) ? Int(value) : BigInteger.Zero;
            if (Int(usage[metric]) + reserved > Int(limits[metric]))
            {
                reasons.Add(metric + " budget exhausted");
            }
        }

        return reasons;
    }

    private static void CheckToken(PyRow attempt, JsonNode? token)
    {
        var text = Text(token, "claim token", 200);
        var digest = PyIds.Sha256Hex(text);
        if (attempt["token_hash"] is not string stored)
        {
            throw new PyTypeError("unsupported operand types(s) or combination of types: 'str' and '" + PyObj.TypeName(attempt["token_hash"]) + "'");
        }

        if (stored.Any(c => c > 0x7F))
        {
            throw new PyTypeError("comparing strings with non-ASCII characters is not supported");
        }

        if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(digest), System.Text.Encoding.ASCII.GetBytes(stored)))
        {
            throw new StateError("attempt claim token does not match");
        }
    }

    // ================================================================================ claims

    /// <summary>
    /// <c>start(run_id, step_key, revision, plan_hash, binding, preflight=None)</c>: claims one pending step under a fresh
    /// binding, reserving its budget; returns the attempt, its lease token (shown once), the source contract for reads and
    /// the exact gated execution for action steps. A preflight that invalidates the action's approval is journaled and
    /// then raised.
    /// </summary>
    public JsonObject Start(JsonNode? runId, JsonNode? stepKey, JsonNode? revision, JsonNode? planHash, JsonNode? binding, JsonNode? preflight = null)
    {
        var result = Transaction(() => StartCore(runId, stepKey, revision, planHash, binding, preflight));
        if (PyJson.Truthy(PyNode.Get(result, "preflight_blocked")))
        {
            throw new StateError(PyNode.AsString(result["preflight_blocked"])!);
        }

        return result;
    }

    private JsonObject StartCore(JsonNode? runId, JsonNode? stepKey, JsonNode? revision, JsonNode? planHash, JsonNode? binding, JsonNode? preflight)
    {
        return Transaction(() =>
        {
            var run = RunRow(runId);
            var step = StepRow(runId, stepKey);
            var plan = ParseObject(run["plan"]);
            var definition = ParseObject(step["definition"]);
            var limits = (JsonObject)plan["limits"]!;
            if (!(PyObj.Eq(run["state"], "planned") || PyObj.Eq(run["state"], "active")) || !PyObj.Eq(run["plan_hash"], planHash))
            {
                throw new StateError("task is paused/cancelled/completed or its plan changed");
            }

            if (!PyJson.IsInt(revision) || !PyObj.Eq(step["revision"], revision) || !PyObj.Eq(step["state"], "pending"))
            {
                throw new StateError("step changed or is not pending");
            }

            Binding(plan, binding);
            if (!InCapabilities(definition["capability"], (JsonObject)PyNode.Index(binding, "capabilities")!))
            {
                throw new StateError("step capability is unavailable");
            }

            var reasons = Prerequisites(runId, definition);
            reasons.AddRange(BudgetReasons(run, (JsonObject)definition["cost"]!));
            if (reasons.Count > 0)
            {
                throw new StateError(string.Join("; ", reasons));
            }

            if (Int(step["attempts"]) >= Int(limits["max_attempts_per_step"]))
            {
                throw new StateError("step attempt budget exhausted");
            }

            if (PyObj.Truthy(step["next_retry_at"]) && When(step["next_retry_at"]) > Clock())
            {
                throw new StateError("provider/backoff retry time has not arrived");
            }

            var running = Conn.Execute("SELECT count(*) FROM task_steps WHERE run_id=? AND state='running'", runId).FetchOne()![0];
            if (Int(running) >= Int(limits["max_parallel"]))
            {
                throw new StateError("parallel-step limit reached; recover expired claims before retrying");
            }

            var attemptId = "task_attempt_" + PyIds.Uuid4Hex();
            var token = PyIds.TokenHex(24);
            var leased = Clock() + PyTimeDelta.FromPySeconds(limits["lease_seconds"]);
            var deadline = When(limits["deadline_at"]);
            var expiry = (deadline < leased ? deadline : leased).Isoformat();
            var details = new JsonObject();
            JsonObject? execution = null;
            JsonObject? sourceContract = null;
            var kind = Str(PyNode.AsString(definition["kind"]));
            if (kind == "action")
            {
                var action = Ledger.Show(definition["action_id"]);
                if (!PyJson.PyEquals(action["revision"], definition["action_revision"]) || !PyJson.PyEquals(action["action_hash"], definition["action_hash"]))
                {
                    throw new StateError("linked action changed; replan and review the exact new action");
                }

                try
                {
                    execution = Ledger.Begin(definition["action_id"], definition["action_revision"], preflight);
                }
                catch (StateError ex)
                {
                    // Preserve the ledger's intentional approval invalidation before surfacing failure.
                    if (PyNode.Is(action["state"], "approved") && PyNode.Is(Ledger.Show(action["id"])["state"], "stale"))
                    {
                        Event(runId, "preflight_blocked", new JsonObject { ["step_key"] = PyNode.Copy(stepKey), ["reason"] = ex.Message });
                        return new JsonObject { ["preflight_blocked"] = ex.Message };
                    }

                    throw;
                }

                details["action_attempt_id"] = PyNode.Copy(execution["attempt_id"]);
            }
            else if (preflight is not null)
            {
                throw new StateError("only action steps accept an execution preflight");
            }

            if (kind == "read")
            {
                sourceContract = PyNode.CopyObject(definition["source"]);
                sourceContract["window"] = PyNode.Copy(plan["window"]);
                sourceContract["run_id"] = attemptId;
                sourceContract["capability"] = PyNode.Copy(definition["capability"]);
                if (PyObj.Truthy(step["latest_attempt"]))
                {
                    var previous = AttemptRow(Sql(step["latest_attempt"]));
                    var oldDetails = ParseObject(previous["details"]);
                    var oldId = PyNode.Get(oldDetails, "coverage_attempt_id");
                    var old = Conn.Execute("SELECT * FROM proactive_attempts WHERE id=?", oldId).FetchOne();
                    if (old is not null && (PyObj.Eq(old["status"], "partial") || PyObj.Eq(old["status"], "failed"))
                        && PyObj.Truthy(old["continuation"]) && !PyObj.Truthy(old["pagination_complete"])
                        && !(old["error_class"] is string error && TokenErrors.Contains(error)))
                    {
                        sourceContract["resume_attempt"] = PyNode.Copy(oldId);
                        details["base_pages"] = Sql(old["pages"]);
                        details["base_items"] = Sql(old["observations"]);
                    }
                }

                var collection = ProactiveState.CoverageStart(Conn, sourceContract);
                details["coverage_attempt_id"] = PyNode.Copy(collection["attempt_id"]);
                details["source_contract"] = PyNode.Copy(sourceContract);
            }

            var usage = ParseObject(run["usage"]);
            var cost = (JsonObject)definition["cost"]!;
            foreach (var metric in Metrics)
            {
                usage[metric] = PyJson.Int(Int(usage[metric]) + Int(cost[metric]));
            }

            var charged = Zero();
            if (kind == "action")
            {
                charged["tool_calls"] = 1L;
            }

            var stamp = StateStore.UtcNow();
            Conn.Execute("INSERT INTO task_attempts VALUES(?,?,?,?,?,?,?,?,?,?,?,NULL,?,NULL)",
                         attemptId, runId, stepKey, PyJson.Int(Int(step["attempts"]) + 1), "running", PyIds.Sha256Hex(token), expiry,
                         StateStore.CanonicalJson(cost), StateStore.CanonicalJson(charged), null, StateStore.CanonicalJson(details), stamp);
            Conn.Execute("UPDATE task_steps SET revision=revision+1,state='running',attempts=attempts+1,"
                         + "latest_attempt=?,result=NULL,next_retry_at=NULL,updated_at=? WHERE run_id=? AND step_key=?",
                         attemptId, stamp, runId, stepKey);
            Conn.Execute("UPDATE task_runs SET state='active',usage=? WHERE id=?", StateStore.CanonicalJson(usage), runId);
            Event(runId, "step_started", new JsonObject
            {
                ["step_key"] = PyNode.Copy(stepKey),
                ["attempt_id"] = attemptId,
                ["kind"] = kind,
            });
            return new JsonObject
            {
                ["run_id"] = PyNode.Copy(runId),
                ["step_key"] = PyNode.Copy(stepKey),
                ["attempt_id"] = attemptId,
                ["token"] = token,
                ["lease_expires"] = expiry,
                ["source_contract"] = sourceContract is null ? null : PyNode.Copy(sourceContract),
                ["coverage_attempt_id"] = PyNode.Copy(PyNode.Get(details, "coverage_attempt_id")),
                ["execution"] = execution,
            };
        });
    }

    /// <summary>
    /// <c>charge(attempt_id, token, event_id, cost)</c>: records actual planned work against the claim's reservation once
    /// per event id (a replay with the same cost returns <c>execute: false</c>); action claims cannot be charged again.
    /// </summary>
    public JsonObject Charge(JsonNode? attemptId, JsonNode? token, JsonNode? eventId, JsonNode? cost)
    {
        var value = ResourceCost(cost);
        if (!Metrics.Any(metric => !Int(value[metric]).IsZero))
        {
            throw new StateError("a charge must account for actual planned work, not an empty execution grant");
        }

        Text(eventId, "charge event identity", 200);
        return Transaction(() =>
        {
            var attempt = AttemptRow(attemptId);
            CheckToken(attempt, token);
            var previous = Conn.Execute("SELECT cost FROM task_charges WHERE attempt_id=? AND event_id=?", attemptId, eventId).FetchOne();
            if (previous is not null)
            {
                if (!PyObj.Eq(previous["cost"], StateStore.CanonicalJson(value)))
                {
                    throw new StateError("charge replay changed its cost");
                }

                return new JsonObject { ["execute"] = false, ["replayed"] = true, ["event_id"] = PyNode.Copy(eventId) };
            }

            var run = RunRow(Sql(attempt["run_id"]));
            var step = StepRow(Sql(attempt["run_id"]), Sql(attempt["step_key"]));
            var definition = ParseObject(step["definition"]);
            if (PyNode.Is(definition["kind"], "action"))
            {
                throw new StateError("the exact action call was already claimed; additional action grants are forbidden");
            }

            if (PyNode.Is(definition["kind"], "read") && Int(value["tool_calls"]) < 1)
            {
                throw new StateError("a read charge must include its tool call");
            }

            if (!PyObj.Eq(run["state"], "active") || !PyObj.Eq(attempt["state"], "running")
                || !PyObj.Eq(step["latest_attempt"], attemptId) || When(attempt["lease_expires"]) <= Clock())
            {
                throw new StateError("claim is paused, cancelled, expired or no longer owns the step");
            }

            var reasons = BudgetReasons(run);
            if (reasons.Count > 0)
            {
                throw new StateError(string.Join("; ", reasons));
            }

            var charged = ParseObject(attempt["charged"]);
            var reserved = ParseObject(attempt["reserved"]);
            foreach (var metric in Metrics)
            {
                charged[metric] = PyJson.Int(Int(charged[metric]) + Int(value[metric]));
                if (Int(charged[metric]) > Int(reserved[metric]))
                {
                    throw new StateError("attempt " + metric + " reservation exhausted");
                }
            }

            Conn.Execute("INSERT INTO task_charges VALUES(?,?,?,?)", attemptId, eventId, StateStore.CanonicalJson(value), StateStore.UtcNow());
            Conn.Execute("UPDATE task_attempts SET charged=? WHERE id=?", StateStore.CanonicalJson(charged), attemptId);
            Event(Sql(run["id"]), "budget_charged", new JsonObject
            {
                ["attempt_id"] = PyNode.Copy(attemptId),
                ["event_id"] = PyNode.Copy(eventId),
                ["cost"] = PyNode.Copy(value),
            });
            return new JsonObject { ["execute"] = true, ["replayed"] = false, ["event_id"] = PyNode.Copy(eventId), ["charged"] = charged };
        });
    }

    // ================================================================================ settlement

    private JsonObject ResultData(PyRow attempt, JsonObject definition, string outcome, JsonNode? result)
    {
        if (result is not JsonObject given || PyRepr.Length(StateStore.CanonicalJson(given)) > 16000)
        {
            throw new StateError("step result must be a bounded receipt object");
        }

        if (PyNode.Is(definition["kind"], "action"))
        {
            var expected = new (string Key, JsonNode? Value)[]
            {
                ("account", PyNode.Of(Account)),
                ("action_id", definition["action_id"]),
                ("attempt_id", ParseObject(attempt["details"])["action_attempt_id"]),
                ("revision", definition["action_revision"]),
                ("action_revision", definition["action_revision"]),
                ("action_hash", definition["action_hash"]),
            };
            foreach (var (key, value) in expected)
            {
                if (given.TryGetPropertyValue(key, out var claimed)
                    && (!PyJson.PyEquals(claimed, value) || (RevisionKeys.Contains(key) && !PyJson.IsInt(claimed))))
                {
                    throw new StateError("action receipt contradicts its claimed " + key);
                }
            }

            if (outcome != "outcome_unknown")
            {
                Ledger.ValidateReceipt(given, outcome);
            }

            return PyNode.CopyObject(given);
        }

        if (given.Any(pair => !ResultFields.Contains(pair.Key)))
        {
            throw new StateError("unsupported step result fields");
        }

        var read = PyNode.Is(definition["kind"], "read");
        if (!PyJson.PyEquals(PyNode.Get(given, "kind"), PyNode.Of(read ? "tool_result" : "local_result")))
        {
            throw new StateError("step receipt kind does not match the operation");
        }

        Text(PyNode.Get(given, "reference"), "result reference", 1000);
        if (PyNode.Get(given, "summary") is not null)
        {
            Text(given["summary"], "result summary", 4000);
        }

        var refs = Ledger.SourceRefs(PyNode.Get(given, "source_refs", new JsonArray()), requireCurrent: false);
        var value2 = PyNode.CopyObject(given);
        value2["source_refs"] = refs;
        var ids = PyNode.Get(given, "work_ids", new JsonArray());
        if (ids is not JsonArray workIds || workIds.Count > 100)
        {
            throw new StateError("work_ids must be bounded canonical references");
        }

        foreach (var identity in workIds)
        {
            Ledger.Show(identity);
        }

        if (PyJson.Truthy(PyNode.Get(given, "publication_id")))
        {
            var publication = Conn.Execute("SELECT status,receipt FROM proactive_publications WHERE id=?", given["publication_id"]).FetchOne();
            if (publication is null || !(publication["status"] is string status && status is "available" or "published" or "reviewed")
                || !PyObj.Truthy(publication["receipt"]))
            {
                throw new StateError("output availability requires its actual publication receipt");
            }
        }

        if (read)
        {
            var expected = ParseObject(attempt["details"])["coverage_attempt_id"];
            if (!PyJson.PyEquals(PyNode.Get(given, "coverage_attempt_id"), expected))
            {
                throw new StateError("read result must name this claim's coverage attempt");
            }

            var coverage = Conn.Execute("SELECT * FROM proactive_attempts WHERE id=?", expected).FetchOne();
            if (coverage is null || !PyObj.Eq(coverage["run_id"], attempt["id"]) || PyObj.Eq(coverage["status"], "running"))
            {
                throw new StateError("finish the exact source coverage attempt before settling the step");
            }

            if (!PyObj.Eq(PyNode.Get(given, "coverage"), coverage["status"]))
            {
                throw new StateError("reported coverage differs from the durable source result");
            }

            if (outcome == "succeeded" && !PyObj.Eq(coverage["status"], "complete"))
            {
                throw new StateError("incomplete source coverage cannot establish successful collection");
            }

            var interrupted = PyNode.Is(PyNode.Get(given, "error_class"), "interrupted")
                              && PyNode.AsString(given["reference"])!.StartsWith("task-recovery:", StringComparison.Ordinal);
            if (PyJson.Truthy(PyNode.Get(given, "error_class")) && !PyObj.Eq(given["error_class"], coverage["error_class"]) && !interrupted)
            {
                throw new StateError("reported source error differs from durable coverage");
            }

            if (PyObj.Truthy(coverage["error_class"]))
            {
                value2["error_class"] = Sql(coverage["error_class"]);
            }
        }

        if (PyNode.Get(value2, "retry_after_seconds") is not null)
        {
            Integer(value2["retry_after_seconds"], "retry_after_seconds", high: 86400);
        }

        if (outcome == "failed" && PyNode.Is(definition["kind"], "local")
            && !(PyNode.Get(value2, "definitive_no_effect") is JsonValue flag && PyJson.Scalar(flag) is true))
        {
            throw new StateError("local failure needs proof of no effect; otherwise record unknown/partial");
        }

        return value2;
    }

    private JsonObject Settle(PyRow attempt, string outcome, JsonNode? result, JsonNode? actual = null)
    {
        var step = StepRow(Sql(attempt["run_id"]), Sql(attempt["step_key"]));
        var run = RunRow(Sql(attempt["run_id"]));
        var definition = ParseObject(step["definition"]);
        var kind = Str(PyNode.AsString(definition["kind"]));
        if (!PyObj.Eq(step["latest_attempt"], attempt["id"]))
        {
            throw new StateError("attempt no longer owns this step; it cannot settle a newer attempt");
        }

        var value = ResultData(attempt, definition, outcome, result);
        var state = Str(attempt["state"]);
        if (state is "succeeded" or "failed")
        {
            if (state == outcome && PyObj.Eq(attempt["result"], StateStore.CanonicalJson(value)))
            {
                var shown = Show(Sql(run["id"]));
                shown["replayed"] = true;
                return shown;
            }

            throw new StateError("settled attempt is immutable");
        }

        if (state is not ("running" or "partial" or "outcome_unknown"))
        {
            throw new StateError("attempt cannot be settled from its current state");
        }

        if (state == "partial" && outcome is "failed" or "outcome_unknown")
        {
            throw new StateError("known partial effects cannot become no effect or unknown");
        }

        var reserved = ParseObject(attempt["reserved"]);
        var charged = ParseObject(attempt["charged"]);
        var observed = actual is not null ? ResourceCost(actual) : PyNode.CopyObject(reserved);
        foreach (var metric in new[] { "tool_calls", "model_calls" })
        {
            if (Int(observed[metric]) < Int(charged[metric]))
            {
                throw new StateError("actual cost cannot erase charged calls");
            }
        }

        if (kind == "read")
        {
            var details = ParseObject(attempt["details"]);
            var coverage = Conn.Execute("SELECT * FROM proactive_attempts WHERE id=?", details["coverage_attempt_id"]).FetchOne()!;
            var complete = PyObj.Eq(coverage["status"], "complete");
            foreach (var (metric, field, baseline) in new[] { ("pages", "pages", "base_pages"), ("items", "observations", "base_items") })
            {
                var known = Int(coverage[field]) - Int(PyNode.Get(details, baseline, PyNode.Of(0)));
                var seen = Int(observed[metric]);
                if (known < 0 || (actual is not null && (seen < known || (complete && seen != known))))
                {
                    throw new StateError("actual source cost cannot contradict durable coverage");
                }

                observed[metric] = PyJson.Int(complete ? known : BigInteger.Max(seen, known));
            }
        }

        if (kind == "local")
        {
            var summary = PyNode.Get(value, "summary", PyNode.Of(""));
            if (PyNode.AsString(summary) is not { } text)
            {
                throw new PyTypeError("object of type '" + PyJson.TypeName(summary) + "' has no len()");
            }

            observed["output_chars"] = PyJson.Int(BigInteger.Max(Int(observed["output_chars"]), PyRepr.Length(text)));
        }

        if (kind is "read" or "local" && outcome is "succeeded" or "partial")
        {
            if (kind == "read" && Int(charged["tool_calls"]).IsZero)
            {
                throw new StateError("record the bounded tool charge before claiming a completed read");
            }
        }

        if (kind == "action")
        {
            var executionId = ParseObject(attempt["details"])["action_attempt_id"];
            var stored = Conn.Execute("SELECT state,receipt FROM work_executions WHERE id=?", executionId).FetchOne()
                         ?? throw new StateError("linked action execution is missing");
            if (stored["state"] is "succeeded" or "failed")
            {
                if (!PyObj.Eq(stored["state"], outcome) || !PyJson.PyEquals(Parse(stored["receipt"]), value))
                {
                    throw new StateError("result differs from the settled canonical execution");
                }
            }
            else
            {
                Ledger.Finish(executionId, PyNode.Of(outcome), value, reconcile: stored["state"] is "partial" or "outcome_unknown");
            }
        }

        var usage = ParseObject(run["usage"]);
        var previousCost = PyObj.Truthy(attempt["actual"]) ? ParseObject(attempt["actual"]) : reserved;
        foreach (var metric in Metrics)
        {
            usage[metric] = PyJson.Int(Int(usage[metric]) + Int(observed[metric]) - Int(previousCost[metric]));
        }

        string? nextRetry = null;
        if (kind == "read" && outcome is "failed" or "partial")
        {
            var backoff = BigInteger.Min(BigInteger.Pow(2, (int)Int(attempt["number"])), 300);
            var requested = PyNode.Get(value, "retry_after_seconds", PyNode.Of(0));
            if (requested is null)
            {
                throw new PyTypeError("'>' not supported between instances of 'NoneType' and 'int'");
            }

            var delay = BigInteger.Max(backoff, Int(requested));
            var details = ParseObject(attempt["details"]);
            var coverage = Conn.Execute("SELECT result,finished_at FROM proactive_attempts WHERE id=?", details["coverage_attempt_id"]).FetchOne()!;
            var next = Clock() + PyTimeDelta.FromPySeconds(delay);
            var provider = PyNode.Dict(Parse(coverage["result"]));
            if (PyNode.Get(provider, "retry_after_seconds") is not null)
            {
                var candidate = When(coverage["finished_at"]) + PyTimeDelta.FromPySeconds(provider["retry_after_seconds"]);
                if (candidate > next)
                {
                    next = candidate;
                }
            }

            if (PyJson.Truthy(PyNode.Get(provider, "next_retry_at")))
            {
                var candidate = When(provider["next_retry_at"]);
                if (candidate > next)
                {
                    next = candidate;
                }
            }

            nextRetry = next.Isoformat();
        }

        var stamp = StateStore.UtcNow();
        Conn.Execute("UPDATE task_attempts SET state=?,actual=?,result=?,finished_at=? WHERE id=?",
                     outcome, StateStore.CanonicalJson(observed), StateStore.CanonicalJson(value), stamp, attempt["id"]);
        Conn.Execute("UPDATE task_steps SET revision=revision+1,state=?,result=?,next_retry_at=?,updated_at=? WHERE run_id=? AND step_key=?",
                     outcome, StateStore.CanonicalJson(value), nextRetry, stamp, run["id"], step["step_key"]);
        Conn.Execute("UPDATE task_runs SET usage=? WHERE id=?", StateStore.CanonicalJson(usage), run["id"]);
        Event(Sql(run["id"]), "step_finished", new JsonObject
        {
            ["step_key"] = Sql(step["step_key"]),
            ["attempt_id"] = Sql(attempt["id"]),
            ["outcome"] = outcome,
        });
        return Show(Sql(run["id"]));
    }

    /// <summary>
    /// <c>finish(attempt_id, token, outcome, result, actual=None)</c>: settles the claim with a bounded receipt (and the
    /// actual cost, which cannot erase charged calls or contradict durable coverage); a settled attempt is immutable.
    /// </summary>
    public JsonObject Finish(JsonNode? attemptId, JsonNode? token, JsonNode? outcome, JsonNode? result, JsonNode? actual = null)
    {
        if (!PyNode.In(outcome, Outcomes))
        {
            throw new StateError("unsupported task outcome");
        }

        return Transaction(() =>
        {
            var attempt = AttemptRow(attemptId);
            CheckToken(attempt, token);
            return Settle(attempt, PyNode.AsString(outcome)!, result, actual);
        });
    }

    // ================================================================================ lifecycle

    /// <summary><c>pause(run_id, reason)</c>: stops future work; never undoes an in-flight effect.</summary>
    public JsonObject Pause(JsonNode? runId, JsonNode? reason)
    {
        Text(reason, "pause reason", 1000);
        return Transaction(() =>
        {
            var run = RunRow(runId);
            if (run["state"] is "completed" or "cancelled")
            {
                throw new StateError("a completed/cancelled task cannot be paused");
            }

            if (!PyObj.Eq(run["state"], "paused"))
            {
                Conn.Execute("UPDATE task_runs SET state='paused' WHERE id=?", runId);
                Event(runId, "paused", new JsonObject { ["reason"] = PyNode.Copy(reason) });
            }

            return Show(runId);
        });
    }

    /// <summary>
    /// <c>cancel(run_id, reason)</c>: cancels pending steps and revokes unused exact approvals of linked actions; completed
    /// results and in-flight effects stay as they are (no undo).
    /// </summary>
    public JsonObject Cancel(JsonNode? runId, JsonNode? reason)
    {
        Text(reason, "cancellation reason", 1000);
        return Transaction(() =>
        {
            var run = RunRow(runId);
            if (run["state"] is "cancelled" or "completed")
            {
                return Show(runId);
            }

            foreach (var step in Conn.Execute("SELECT * FROM task_steps WHERE run_id=?", runId).FetchAll())
            {
                var definition = ParseObject(step["definition"]);
                if (PyNode.Is(definition["kind"], "action") && step["state"] is not ("running" or "succeeded" or "partial" or "outcome_unknown"))
                {
                    var action = Ledger.Show(definition["action_id"]);
                    if (PyJson.PyEquals(action["revision"], definition["action_revision"]) && PyJson.PyEquals(action["action_hash"], definition["action_hash"]))
                    {
                        Ledger.InvalidateApproval(action["id"], action["revision"], action["action_hash"], reason);
                    }
                }

                if (PyObj.Eq(step["state"], "pending"))
                {
                    Conn.Execute("UPDATE task_steps SET revision=revision+1,state='cancelled',updated_at=? WHERE run_id=? AND step_key=?",
                                 StateStore.UtcNow(), runId, step["step_key"]);
                }
            }

            Conn.Execute("UPDATE task_runs SET state='cancelled' WHERE id=?", runId);
            Event(runId, "cancelled", new JsonObject { ["reason"] = PyNode.Copy(reason), ["undo_performed"] = false });
            return Show(runId);
        });
    }

    /// <summary><c>resume(run_id, revision, binding)</c>: resumes a paused run under a fresh, unchanged binding and budget.</summary>
    public JsonObject Resume(JsonNode? runId, JsonNode? revision, JsonNode? binding)
    {
        return Transaction(() =>
        {
            var run = RunRow(runId);
            if (!PyJson.IsInt(revision) || !PyObj.Eq(run["revision"], revision))
            {
                throw new StateError("task revision changed");
            }

            if (!PyObj.Eq(run["state"], "paused"))
            {
                throw new StateError("only paused tasks resume; cancelled/completed tasks need a deliberate new run");
            }

            Binding(ParseObject(run["plan"]), binding);
            var reasons = BudgetReasons(run);
            if (reasons.Count > 0)
            {
                throw new StateError(string.Join("; ", reasons) + "; review a revised plan before resuming");
            }

            Conn.Execute("UPDATE task_runs SET state='active' WHERE id=?", runId);
            Event(runId, "resumed", new JsonObject { ["binding"] = BindingHash(binding) });
            return Show(runId);
        });
    }

    /// <summary>
    /// <c>retry(run_id, step_key, revision, binding)</c>: makes a failed or partial read step pending again, only for a
    /// retryable error class or an unfinished error-free page, after its backoff and within budget.
    /// </summary>
    public JsonObject Retry(JsonNode? runId, JsonNode? stepKey, JsonNode? revision, JsonNode? binding)
    {
        return Transaction(() =>
        {
            var run = RunRow(runId);
            var step = StepRow(runId, stepKey);
            var plan = ParseObject(run["plan"]);
            var definition = ParseObject(step["definition"]);
            if (!PyJson.IsInt(revision) || !PyObj.Eq(step["revision"], revision))
            {
                throw new StateError("step revision changed");
            }

            if (run["state"] is not ("active" or "planned") || !PyNode.Is(definition["kind"], "read"))
            {
                throw new StateError("only read steps in active tasks may retry");
            }

            if (step["state"] is not ("failed" or "partial") || Int(step["attempts"]) >= Int(((JsonObject)plan["limits"]!)["max_attempts_per_step"]))
            {
                throw new StateError("step cannot retry or its attempt budget is exhausted");
            }

            var result = PyNode.Dict(Parse(step["result"]));
            var attempt = AttemptRow(Sql(step["latest_attempt"]));
            var coverageId = ParseObject(attempt["details"])["coverage_attempt_id"];
            var coverage = Conn.Execute("SELECT * FROM proactive_attempts WHERE id=?", coverageId).FetchOne()!;
            var providerResult = PyObj.Truthy(coverage["result"]) ? Parse(coverage["result"]) : new JsonObject();
            var unfinishedPage = PyObj.Eq(step["state"], "partial") && PyObj.Eq(coverage["status"], "partial")
                                 && PyObj.Truthy(coverage["continuation"]) && !PyObj.Truthy(coverage["pagination_complete"])
                                 && !PyObj.Contains(providerResult, "error_class");
            if (!PyNode.In(PyNode.Get(result, "error_class"), Retryable) && !unfinishedPage)
            {
                throw new StateError("failure needs explicit capability/auth/source revalidation, not automatic retry");
            }

            Binding(plan, binding);
            if (PyObj.Truthy(step["next_retry_at"]) && When(step["next_retry_at"]) > Clock())
            {
                throw new StateError("provider/backoff retry time has not arrived");
            }

            if (BudgetReasons(run, (JsonObject)definition["cost"]!).Count > 0)
            {
                throw new StateError("task budget/deadline does not permit another attempt");
            }

            Conn.Execute("UPDATE task_steps SET revision=revision+1,state='pending',updated_at=? WHERE run_id=? AND step_key=?",
                         StateStore.UtcNow(), runId, stepKey);
            Event(runId, "read_retry_ready", new JsonObject { ["step_key"] = PyNode.Copy(stepKey) });
            return Show(runId);
        });
    }

    /// <summary>
    /// <c>recover(run_id)</c>: synchronises settled action executions, then settles expired claims: reads fail (or stay
    /// partial over complete coverage) as interrupted, actions and local steps become <c>outcome_unknown</c>.
    /// </summary>
    public JsonObject Recover(JsonNode? runId)
    {
        return Transaction(() =>
        {
            RunRow(runId);
            SyncCore(runId);
            foreach (var attempt in Conn.Execute("SELECT * FROM task_attempts WHERE run_id=? AND state='running'", runId).FetchAll())
            {
                if (When(attempt["lease_expires"]) > Clock())
                {
                    continue;
                }

                var step = StepRow(runId, Sql(attempt["step_key"]));
                var definition = ParseObject(step["definition"]);
                var details = ParseObject(attempt["details"]);
                var reference = "task-recovery:" + Str(attempt["id"]);
                if (PyNode.Is(definition["kind"], "read"))
                {
                    var coverageId = details["coverage_attempt_id"];
                    var coverage = Conn.Execute("SELECT status FROM proactive_attempts WHERE id=?", coverageId).FetchOne()!;
                    if (PyObj.Eq(coverage["status"], "running"))
                    {
                        ProactiveState.CoverageFinish(Conn, Str(PyNode.AsString(coverageId)),
                                                      new JsonObject { ["status"] = "failed", ["error_class"] = "timeout" });
                    }

                    var status = Conn.Execute("SELECT status FROM proactive_attempts WHERE id=?", coverageId).FetchOne()![0];
                    var result = new JsonObject
                    {
                        ["kind"] = "tool_result",
                        ["reference"] = reference,
                        ["coverage"] = Sql(status),
                        ["coverage_attempt_id"] = PyNode.Copy(coverageId),
                        ["error_class"] = "interrupted",
                        ["summary"] = "Read interrupted; no complete task result was recorded.",
                    };
                    Settle(attempt, PyObj.Eq(status, "complete") ? "partial" : "failed", result);
                }
                else if (PyNode.Is(definition["kind"], "action"))
                {
                    Settle(attempt, "outcome_unknown", new JsonObject
                    {
                        ["kind"] = "tool_result",
                        ["reference"] = reference,
                        ["outcome"] = "outcome_unknown",
                        ["recorded_at"] = StateStore.UtcNow(),
                    });
                }
                else
                {
                    Settle(attempt, "outcome_unknown", new JsonObject
                    {
                        ["kind"] = "local_result",
                        ["reference"] = reference,
                        ["summary"] = "Local preparation was interrupted; inspect its canonical records before retrying.",
                    });
                }
            }

            return Show(runId);
        });
    }

    private void SyncCore(JsonNode? runId)
    {
        foreach (var step in Conn.Execute("SELECT * FROM task_steps WHERE run_id=?", runId).FetchAll())
        {
            var definition = ParseObject(step["definition"]);
            if (!PyNode.Is(definition["kind"], "action") || !PyObj.Truthy(step["latest_attempt"])
                || step["state"] is "succeeded" or "failed" or "cancelled")
            {
                continue;
            }

            var attempt = AttemptRow(Sql(step["latest_attempt"]));
            var executionId = ParseObject(attempt["details"])["action_attempt_id"];
            var execution = Conn.Execute("SELECT state,receipt FROM work_executions WHERE id=?", executionId).FetchOne();
            if (execution is not null && execution["state"] is "succeeded" or "failed" or "partial" && PyObj.Truthy(execution["receipt"]))
            {
                var result = Parse(execution["receipt"]);
                if (!PyObj.Eq(attempt["state"], execution["state"]) || !PyObj.Eq(attempt["result"], StateStore.CanonicalJson(result)))
                {
                    Settle(attempt, Str(execution["state"]), result);
                }
            }
        }
    }

    /// <summary><c>sync(run_id)</c>: adopts settled canonical action executions into task progress.</summary>
    public JsonObject Sync(JsonNode? runId)
    {
        return Transaction(() =>
        {
            RunRow(runId);
            SyncCore(runId);
            return Show(runId);
        });
    }

    /// <summary>
    /// <c>reconcile(run_id, step_key, revision, outcome, result)</c>: settles a partial or unknown step with a known
    /// result read from the canonical source; never re-executes.
    /// </summary>
    public JsonObject Reconcile(JsonNode? runId, JsonNode? stepKey, JsonNode? revision, JsonNode? outcome, JsonNode? result)
    {
        if (!PyNode.In(outcome, KnownOutcomes))
        {
            throw new StateError("reconciliation requires a known result");
        }

        return Transaction(() =>
        {
            var step = StepRow(runId, stepKey);
            if (!PyJson.IsInt(revision) || !PyObj.Eq(step["revision"], revision)
                || step["state"] is not ("partial" or "outcome_unknown") || !PyObj.Truthy(step["latest_attempt"]))
            {
                throw new StateError("reconciliation requires the current unsettled step");
            }

            return Settle(AttemptRow(Sql(step["latest_attempt"])), PyNode.AsString(outcome)!, result);
        });
    }

    // ================================================================================ replanning

    /// <summary>
    /// <c>replan_preview(run_id, plan)</c>: the exact reviewed replacement of a paused run's plan and the subject a human
    /// decision must name; completed and attempted steps stay immutable. Grants nothing.
    /// </summary>
    public JsonObject ReplanPreview(JsonNode? runId, JsonNode? plan)
    {
        var run = RunRow(runId);
        if (!PyObj.Eq(run["state"], "paused"))
        {
            throw new StateError("pause the task before revising its plan");
        }

        var normalized = PlanData(plan);
        var old = ParseObject(run["plan"]);
        foreach (var key in new[] { "goal", "routine", "request_ref", "mode" })
        {
            if (!PyJson.PyEquals(normalized[key], old[key]))
            {
                throw new StateError("a different goal/routine/request/mode needs a new task");
            }
        }

        if (!PyJson.PyEquals(normalized["window"], old["window"])
            && Conn.Execute("SELECT attempts,definition FROM task_steps WHERE run_id=?", runId).FetchAll()
                   .Any(row => PyObj.Truthy(row["attempts"]) && PyNode.Is(ParseObject(row["definition"])["kind"], "read")))
        {
            throw new StateError("an attempted source window is immutable; create a new task for a different interval");
        }

        var replacement = Replacement(normalized);
        foreach (var step in Conn.Execute("SELECT * FROM task_steps WHERE run_id=?", runId).FetchAll())
        {
            if (step["state"] is "running" or "outcome_unknown"
                || (PyObj.Eq(step["state"], "partial") && !PyNode.Is(ParseObject(step["definition"])["kind"], "read")))
            {
                throw new StateError("reconcile in-flight or uncertain effects before replanning");
            }

            var key = Str(step["step_key"]);
            if (PyObj.Eq(step["state"], "succeeded") && !PyJson.PyEquals(replacement.GetValueOrDefault(key), ParseObject(step["definition"])))
            {
                throw new StateError("completed steps and their results are immutable; add a new step for a fresh read");
            }

            if (PyObj.Truthy(step["attempts"]) && replacement.TryGetValue(key, out var definition)
                && !PyJson.PyEquals(definition, ParseObject(step["definition"])))
            {
                throw new StateError("attempted step definitions are immutable; use a new step key");
            }
        }

        var usage = ParseObject(run["usage"]);
        var limits = (JsonObject)normalized["limits"]!;
        if (Metrics.Any(metric => Int(limits[metric]) < Int(usage[metric])))
        {
            throw new StateError("revised limits cannot erase already spent/reserved work");
        }

        var value = new JsonObject
        {
            ["account"] = Account,
            ["run_id"] = PyNode.Copy(runId),
            ["expected_revision"] = Sql(run["revision"]),
            ["old_plan_hash"] = Sql(run["plan_hash"]),
            ["plan"] = normalized,
        };
        var subject = "task-replan:" + WorkLedger.Digest(value);
        value["subject_id"] = subject;
        value["revision"] = 1L;
        value["external_approval_granted"] = false;
        return value;
    }

    private static Dictionary<string, JsonObject> Replacement(JsonObject plan)
    {
        var replacement = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var step in (JsonArray)plan["steps"]!)
        {
            replacement[PyNode.AsString(step!["key"])!] = (JsonObject)step;
        }

        return replacement;
    }

    /// <summary>
    /// <c>replan(run_id, plan, evidence)</c>: applies a previewed plan with human evidence for its exact subject; removed
    /// steps stay as cancelled history and changed action steps lose their unused approvals.
    /// </summary>
    public JsonObject Replan(JsonNode? runId, JsonNode? plan, JsonNode? evidence)
    {
        return Transaction(() =>
        {
            var preview = ReplanPreview(runId, plan);
            WorkLedger.Human(evidence, Str(PyNode.AsString(preview["subject_id"])), 1, "replan");
            var old = ParseObject(RunRow(runId)["plan"]);
            var newPlan = (JsonObject)preview["plan"]!;
            var changedBinding = BindingHash(old["environment"]) != BindingHash(newPlan["environment"]);
            // Step keys are the table's primary key (with run_id), so the rows are distinct as Python's dict holds them.
            var existing = Conn.Execute("SELECT * FROM task_steps WHERE run_id=?", runId).FetchAll()
                               .Select(row => (Key: Str(row["step_key"]), Row: row)).ToList();

            var replacement = Replacement(newPlan);
            foreach (var (key, row) in existing)
            {
                var definition = ParseObject(row["definition"]);
                if (PyNode.Is(definition["kind"], "action") && !PyObj.Eq(row["state"], "succeeded")
                    && (changedBinding || !PyJson.PyEquals(replacement.GetValueOrDefault(key), definition)))
                {
                    var action = Ledger.Show(definition["action_id"]);
                    if (PyJson.PyEquals(action["revision"], definition["action_revision"]) && PyJson.PyEquals(action["action_hash"], definition["action_hash"]))
                    {
                        Ledger.InvalidateApproval(action["id"], action["revision"], action["action_hash"], "Task plan changed");
                    }
                }

                if (!replacement.ContainsKey(key))
                {
                    // Keep history and attempts; removed steps remain explicitly cancelled.
                    Conn.Execute("UPDATE task_steps SET state='cancelled',revision=revision+1,updated_at=? WHERE run_id=? AND step_key=?",
                                 StateStore.UtcNow(), runId, key);
                }
            }

            foreach (var node in (JsonArray)newPlan["steps"]!)
            {
                var definition = (JsonObject)node!;
                var key = PyNode.AsString(definition["key"])!;
                var row = existing.FirstOrDefault(entry => entry.Key == key).Row;
                if (row is null)
                {
                    Conn.Execute("INSERT INTO task_steps VALUES(?,?,1,'pending',?,0,NULL,NULL,NULL,?)",
                                 runId, key, StateStore.CanonicalJson(definition), StateStore.UtcNow());
                }
                else if (!PyObj.Eq(row["state"], "succeeded"))
                {
                    if (PyObj.Truthy(row["attempts"]) && !PyObj.Eq(row["definition"], StateStore.CanonicalJson(definition)))
                    {
                        throw new StateError("attempted step definitions are immutable; use a new step key");
                    }

                    Conn.Execute("UPDATE task_steps SET definition=?,state='pending',revision=revision+1,updated_at=? WHERE run_id=? AND step_key=?",
                                 StateStore.CanonicalJson(definition), StateStore.UtcNow(), runId, key);
                }
            }

            Conn.Execute("UPDATE task_runs SET plan=?,plan_hash=? WHERE id=?", StateStore.CanonicalJson(newPlan), WorkLedger.Digest(newPlan), runId);
            Event(runId, "replanned", new JsonObject { ["subject_id"] = PyNode.Copy(preview["subject_id"]), ["binding_changed"] = changedBinding });
            return Show(runId);
        });
    }

    // ================================================================================ views

    /// <summary>
    /// <c>show(run_id)</c>: the run with every step, its blocked reasons, ready steps, expired claims, unresolved effects,
    /// usage and remaining budget, and the derived status. Read-only.
    /// </summary>
    public JsonObject Show(JsonNode? runId)
    {
        return StateStore.Transaction(Conn, () =>
        {
            var run = RunRow(runId);
            var plan = ParseObject(run["plan"]);
            var usage = ParseObject(run["usage"]);
            var limits = (JsonObject)plan["limits"]!;
            var activeKeys = ((JsonArray)plan["steps"]!).Select(step => Str(PyNode.AsString(step!["key"]))).ToHashSet(StringComparer.Ordinal);
            var runState = Str(run["state"]);
            var steps = new List<JsonObject>();
            var ready = new List<string>();
            var expired = new List<string>();
            var unknown = new List<string>();
            var blocked = BudgetReasons(run);
            var runningCount = Int(Conn.Execute("SELECT count(*) FROM task_steps WHERE run_id=? AND state='running'", runId).FetchOne()![0]);
            foreach (var row in Conn.Execute("SELECT * FROM task_steps WHERE run_id=? ORDER BY rowid", runId).FetchAll())
            {
                var key = Str(row["step_key"]);
                var state = Str(row["state"]);
                var definition = ParseObject(row["definition"]);
                var result = PyObj.Truthy(row["result"]) ? Parse(row["result"]) : null;
                var step = new JsonObject
                {
                    ["key"] = key,
                    ["revision"] = Sql(row["revision"]),
                    ["state"] = state,
                    ["definition"] = definition,
                    ["attempts"] = Sql(row["attempts"]),
                    ["latest_attempt"] = Sql(row["latest_attempt"]),
                    ["result"] = result,
                    ["next_retry_at"] = Sql(row["next_retry_at"]),
                    ["in_current_plan"] = activeKeys.Contains(key),
                };
                var reasons = activeKeys.Contains(key) ? Prerequisites(runId, definition) : [];
                if (state == "pending")
                {
                    reasons.AddRange(BudgetReasons(run, (JsonObject)definition["cost"]!));
                    if (runState is not ("planned" or "active"))
                    {
                        reasons.Add("task is " + runState);
                    }

                    if (Int(row["attempts"]) >= Int(limits["max_attempts_per_step"]))
                    {
                        reasons.Add("step attempt budget exhausted");
                    }

                    if (PyObj.Truthy(row["next_retry_at"]) && When(row["next_retry_at"]) > Clock())
                    {
                        reasons.Add("provider/backoff retry time has not arrived");
                    }

                    if (runningCount >= Int(limits["max_parallel"]))
                    {
                        reasons.Add("parallel-step limit reached");
                    }
                }

                if (PyJson.Truthy(result) && PyJson.Truthy(PyNode.Get(PyNode.Dict(result), "source_refs")))
                {
                    step["source_evidence_changed"] = Ledger.RefsStale(result!["source_refs"]);
                }

                if (state == "running" && PyObj.Truthy(row["latest_attempt"]))
                {
                    var attempt = AttemptRow(Sql(row["latest_attempt"]));
                    step["lease_expires"] = Sql(attempt["lease_expires"]);
                    if (When(attempt["lease_expires"]) <= Clock())
                    {
                        expired.Add(key);
                    }
                }

                if (PyNode.Is(definition["kind"], "action"))
                {
                    var action = Ledger.Show(definition["action_id"]);
                    step["action_state"] = PyNode.Copy(action["state"]);
                    if (PyObj.Truthy(row["latest_attempt"]))
                    {
                        var owned = AttemptRow(Sql(row["latest_attempt"]));
                        var executionId = PyNode.Get(ParseObject(owned["details"]), "action_attempt_id");
                        var execution = Conn.Execute("SELECT state FROM work_executions WHERE id=? AND action_id=? AND revision=?",
                                                     executionId, definition["action_id"], definition["action_revision"]).FetchOne();
                        if (execution is null)
                        {
                            unknown.Add(key);
                            reasons.Add("owned execution record is unavailable");
                        }
                        else
                        {
                            step["owned_action_state"] = Sql(execution["state"]);
                            if (execution["state"] is "executing" or "partial" or "outcome_unknown")
                            {
                                unknown.Add(key);
                            }

                            if (execution["state"] is "succeeded" or "failed" && !PyObj.Eq(row["state"], execution["state"]))
                            {
                                reasons.Add("canonical execution result is available; synchronize task progress");
                            }
                        }
                    }

                    var actionState = PyNode.AsString(action["state"]);
                    if (state == "pending" && actionState != "approved")
                    {
                        reasons.Add(actionState is "executing" or "succeeded" or "partial" or "outcome_unknown"
                            ? "linked action already has an execution; inspect its canonical result"
                            : "exact action approval required");
                    }

                    if (state == "pending" && actionState == "approved")
                    {
                        var approved = PyNode.Iter(action["approvals"]).Any(approval =>
                            PyNode.Index(approval, "invalidated_at") is null
                            && PyJson.PyEquals(PyNode.Index(approval, "revision"), definition["action_revision"])
                            && PyJson.PyEquals(PyNode.Index(approval, "action_hash"), definition["action_hash"])
                            && When(PyNode.Index(approval, "expires_at")) > Clock());
                        if (!approved)
                        {
                            reasons.Add("exact action approval expired or is missing");
                        }

                        if (PyJson.Truthy(action["stale"]))
                        {
                            reasons.Add("action source evidence changed");
                        }
                    }

                    if (!PyJson.PyEquals(action["revision"], definition["action_revision"]) || !PyJson.PyEquals(action["action_hash"], definition["action_hash"]))
                    {
                        reasons.Add("linked action revision changed");
                    }
                }

                if (state == "outcome_unknown")
                {
                    unknown.Add(key);
                }

                step["blocked_reasons"] = PyNode.StrList(reasons);
                if (state == "pending" && runState is "planned" or "active" && reasons.Count == 0 && blocked.Count == 0 && activeKeys.Contains(key))
                {
                    ready.Add(key);
                }

                steps.Add(step);
            }

            var current = steps.Where(step => PyJson.Truthy(step["in_current_plan"])).ToList();
            var states = current.Select(step => PyNode.AsString(step["state"])!).ToHashSet(StringComparer.Ordinal);
            string status;
            if (runState is "paused" or "cancelled")
            {
                status = runState;
            }
            else if (unknown.Count > 0)
            {
                status = "outcome_unknown";
            }
            else if (expired.Count > 0)
            {
                status = "interrupted";
            }
            else if (states.Contains("running"))
            {
                status = "running";
            }
            else if (states.SetEquals(["succeeded"]))
            {
                status = Metrics.Any(metric => Int(usage[metric]) > Int(limits[metric])) ? "partial" : "succeeded";
            }
            else if (states.IsSubsetOf(Terminal))
            {
                status = states.Contains("succeeded") || states.Contains("partial") ? "partial" : "failed";
            }
            else if (blocked.Count > 0)
            {
                status = "blocked";
            }
            else if (ready.Count > 0)
            {
                status = runState == "planned" ? "planned" : "ready";
            }
            else if (current.Any(step => ((JsonArray)step["blocked_reasons"]!).Any(reason => PyNode.Is(reason, "exact action approval required"))))
            {
                status = "waiting_approval";
            }
            else
            {
                status = "blocked";
            }

            var remaining = new JsonObject();
            foreach (var metric in Metrics)
            {
                remaining[metric] = PyJson.Int(BigInteger.Max(0, Int(limits[metric]) - Int(usage[metric])));
            }

            return new JsonObject
            {
                ["id"] = PyNode.Copy(runId),
                ["account"] = Account,
                ["revision"] = Sql(run["revision"]),
                ["state"] = runState,
                ["status"] = status,
                ["plan_hash"] = Sql(run["plan_hash"]),
                ["plan"] = plan,
                ["steps"] = new JsonArray(steps.Select(step => (JsonNode?)step).ToArray()),
                ["usage"] = usage,
                ["remaining"] = remaining,
                ["ready_steps"] = PyNode.StrList(ready),
                ["expired_claims"] = PyNode.StrList(expired),
                ["unresolved_effects"] = PyNode.StrList(PyText.Sorted(unknown.Distinct(StringComparer.Ordinal))),
                ["blocked_reasons"] = PyNode.StrList(blocked),
                ["created_at"] = Sql(run["created_at"]),
                ["updated_at"] = Sql(run["updated_at"]),
                ["limits_enforcement"] = "Tracked claims and reported results only; not a sandbox over other host tools.",
                ["token_usage"] = null,
                ["model_cost"] = null,
                ["approval_granted"] = false,
            };
        }, readOnly: true);
    }

    /// <summary><c>list()</c>: the first page of 50 runs.</summary>
    public JsonObject List() => List(PyNode.Of(50));

    /// <summary>
    /// <c>list(limit=50, after=None)</c>: a page of this account's runs (summaries, no plans) ordered by id. A null
    /// <paramref name="limit"/> is Python's explicit <c>None</c> and is refused; omit it with <see cref="List()"/>.
    /// </summary>
    public JsonObject List(JsonNode? limit, JsonNode? after = null)
    {
        var count = Integer(limit, "list limit", 1, 100);
        if (after is not null)
        {
            Text(after, "list cursor", 100);
        }

        var rows = Conn.Execute("SELECT id FROM task_runs WHERE account=? AND id>? ORDER BY id LIMIT ?",
                                Account, PyJson.Truthy(after) ? after : PyNode.Of(""), PyJson.Int(count + 1)).FetchAll();
        var runs = new JsonArray();
        foreach (var row in rows.Take((int)count))
        {
            var item = Show(Sql(row["id"]));
            var counts = new JsonObject();
            foreach (var step in (JsonArray)item["steps"]!)
            {
                if (PyJson.Truthy(step!["in_current_plan"]))
                {
                    var state = PyNode.AsString(step["state"])!;
                    counts[state] = PyJson.Int((counts.TryGetPropertyValue(state, out var seen) ? Int(seen) : 0) + 1);
                }
            }

            var summary = new JsonObject();
            foreach (var key in new[] { "id", "account", "revision", "state", "status", "plan_hash", "usage", "remaining",
                                        "expired_claims", "unresolved_effects", "blocked_reasons", "created_at", "updated_at" })
            {
                summary[key] = PyNode.Copy(item[key]);
            }

            var plan = (JsonObject)item["plan"]!;
            summary["goal"] = PyNode.Copy(plan["goal"]);
            summary["routine"] = PyNode.Copy(plan["routine"]);
            summary["mode"] = PyNode.Copy(plan["mode"]);
            summary["step_counts"] = counts;
            runs.Add(summary);
        }

        return new JsonObject
        {
            ["account"] = Account,
            ["runs"] = runs,
            ["next_cursor"] = rows.Count > count ? Sql(rows[(int)count - 1]["id"]) : null,
        };
    }

    /// <summary><c>health()</c>: aggregate journal health (runs by state, expired claims, unreconciled attempts). Read-only.</summary>
    public JsonObject Health()
    {
        return StateStore.Transaction(Conn, () =>
        {
            var counts = new JsonObject();
            foreach (var row in Conn.Execute("SELECT state,count(*) AS count FROM task_runs WHERE account=? GROUP BY state", Account))
            {
                counts[Str(row["state"])] = Sql(row["count"]);
            }

            var expired = Int(Conn.Execute(
                "SELECT count(*) FROM task_attempts a JOIN task_runs r ON r.id=a.run_id "
                + "JOIN task_steps s ON s.run_id=a.run_id AND s.step_key=a.step_key AND s.latest_attempt=a.id "
                + "WHERE r.account=? AND a.state='running' AND a.lease_expires<=?",
                Account, StateStore.UtcNow()).FetchOne()![0]);
            var unsettled = Conn.Execute(
                "SELECT a.state,s.definition FROM task_attempts a JOIN task_runs r ON r.id=a.run_id "
                + "JOIN task_steps s ON s.run_id=a.run_id AND s.step_key=a.step_key AND s.latest_attempt=a.id "
                + "WHERE r.account=? AND a.state IN ('partial','outcome_unknown')", Account).FetchAll();
            var unknown = unsettled.Count(row => PyObj.Eq(row["state"], "outcome_unknown") || !PyNode.Is(ParseObject(row["definition"])["kind"], "read"));
            var attention = !expired.IsZero || unknown > 0;
            return new JsonObject
            {
                ["account"] = Account,
                ["status"] = attention ? "attention-needed" : "available",
                ["runs_by_state"] = counts,
                ["expired_claims"] = PyJson.Int(expired),
                ["unreconciled_attempts"] = (long)unknown,
                ["action"] = attention ? "Inspect task_state.py list; recover expired claims and reconcile uncertain results." : null,
                ["note"] = "These are tracked-task journal states, not proof of current provider state.",
            };
        }, readOnly: true);
    }

    /// <summary><c>history(run_id)</c>: the run's latest 100 journal events, newest first.</summary>
    public JsonObject History(JsonNode? runId) => History(runId, PyNode.Of(100));

    /// <summary>
    /// <c>history(run_id, limit=100)</c>: the run's journal events, newest first. A null <paramref name="limit"/> is
    /// Python's explicit <c>None</c> and is refused; omit it with <see cref="History(JsonNode?)"/>.
    /// </summary>
    public JsonObject History(JsonNode? runId, JsonNode? limit)
    {
        RunRow(runId);
        var count = Integer(limit, "history limit", 1, 500);
        var events = new JsonArray();
        foreach (var row in Conn.Execute("SELECT revision,event,data,created_at FROM task_events WHERE run_id=? ORDER BY seq DESC LIMIT ?",
                                         runId, PyJson.Int(count)))
        {
            var item = PyNode.FromRow(row);
            item["data"] = Parse(row["data"]);
            events.Add(item);
        }

        return new JsonObject { ["run_id"] = PyNode.Copy(runId), ["events"] = events };
    }
}

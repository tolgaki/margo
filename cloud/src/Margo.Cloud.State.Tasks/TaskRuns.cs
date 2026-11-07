using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Work;

namespace Margo.Cloud.State.Tasks;

/// <summary>
/// The module-level part of <c>skills/chief-of-staff/scripts/task_runs.py</c>: constants, the schema, input validators
/// (<c>text</c>, <c>integer</c>, <c>when</c>, <c>resource_cost</c>, <c>environment</c>), <c>zero</c>,
/// <c>binding_hash</c>, <c>task_identity</c> and the module clock (<c>clock()</c>, which tests and the contract probe
/// replace as Python's tests patch <c>task_runs.clock</c>). <see cref="TaskStore"/> is the <c>TaskStore</c> class.
/// </summary>
public static class TaskRuns
{
    /// <summary><c>METRICS</c>, in order.</summary>
    public static readonly IReadOnlyList<string> Metrics = ["tool_calls", "pages", "items", "model_calls", "output_chars"];

    /// <summary><c>KINDS</c>.</summary>
    public static readonly IReadOnlySet<string> Kinds = Set("read", "local", "action");

    /// <summary><c>TERMINAL</c>.</summary>
    public static readonly IReadOnlySet<string> Terminal = Set("succeeded", "partial", "failed", "outcome_unknown", "cancelled");

    /// <summary><c>RETRYABLE</c>.</summary>
    public static readonly IReadOnlySet<string> Retryable = Set("timeout", "network", "throttled", "unavailable", "interrupted");

    /// <summary><c>SCHEMA</c>, byte for byte (the schema SQL stored in <c>sqlite_master</c> must match).</summary>
    public static readonly string Schema = "\n" + """
        CREATE TABLE IF NOT EXISTS task_meta (key TEXT PRIMARY KEY,value TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS task_runs (
         id TEXT PRIMARY KEY,account TEXT NOT NULL,key_hash TEXT NOT NULL UNIQUE,
         revision INTEGER NOT NULL,state TEXT NOT NULL,plan TEXT NOT NULL,plan_hash TEXT NOT NULL,
         usage TEXT NOT NULL,created_at TEXT NOT NULL,updated_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS task_steps (
         run_id TEXT NOT NULL REFERENCES task_runs(id),step_key TEXT NOT NULL,revision INTEGER NOT NULL,
         state TEXT NOT NULL,definition TEXT NOT NULL,attempts INTEGER NOT NULL,
         latest_attempt TEXT,result TEXT,next_retry_at TEXT,updated_at TEXT NOT NULL,
         PRIMARY KEY(run_id,step_key)
        );
        CREATE TABLE IF NOT EXISTS task_attempts (
         id TEXT PRIMARY KEY,run_id TEXT NOT NULL,step_key TEXT NOT NULL,number INTEGER NOT NULL,
         state TEXT NOT NULL,token_hash TEXT NOT NULL,lease_expires TEXT NOT NULL,
         reserved TEXT NOT NULL,charged TEXT NOT NULL,actual TEXT,details TEXT NOT NULL,
         result TEXT,started_at TEXT NOT NULL,finished_at TEXT,
         FOREIGN KEY(run_id,step_key) REFERENCES task_steps(run_id,step_key),
         UNIQUE(run_id,step_key,number)
        );
        CREATE TABLE IF NOT EXISTS task_charges (
         attempt_id TEXT NOT NULL REFERENCES task_attempts(id),event_id TEXT NOT NULL,
         cost TEXT NOT NULL,created_at TEXT NOT NULL,PRIMARY KEY(attempt_id,event_id)
        );
        CREATE TABLE IF NOT EXISTS task_events (
         seq INTEGER PRIMARY KEY AUTOINCREMENT,run_id TEXT NOT NULL REFERENCES task_runs(id),
         revision INTEGER NOT NULL,event TEXT NOT NULL,data TEXT NOT NULL,created_at TEXT NOT NULL
        );
        """ + "\n";

    private static readonly IReadOnlySet<string> BindingKeys = Set("account", "host", "capabilities", "observed_at");
    private static readonly AsyncLocal<Func<PyDateTime>?> ClockOverride = new();

    internal static IReadOnlySet<string> Set(params string[] values) => new HashSet<string>(values, StringComparer.Ordinal);

    internal static HashSet<string> KeySet(JsonObject value) => value.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);

    /// <summary><c>text(value, field, maximum=4000)</c>: a nonempty (after <c>strip()</c>) string of at most <paramref name="maximum"/> code points.</summary>
    public static string Text(JsonNode? value, string field, int maximum = 4000) => Text(value, field, (BigInteger)maximum);

    /// <summary><c>text(value, field, maximum)</c> with a maximum of any size (a Python int has no width).</summary>
    public static string Text(JsonNode? value, string field, BigInteger maximum)
    {
        if (PyNode.AsString(value) is not { } text || PyRepr.Strip(text).Length == 0 || PyRepr.Length(text) > maximum)
        {
            throw new StateError(field + " must be a bounded nonempty string");
        }

        return text;
    }

    /// <summary><c>integer(value, field, low=0, high=1000000)</c>: an int (not bool) in the closed range.</summary>
    public static BigInteger Integer(JsonNode? value, string field, long low = 0, long high = 1000000) =>
        Integer(value, field, (BigInteger)low, (BigInteger)high);

    /// <summary><c>integer(value, field, low, high)</c> with bounds of any size (a Python int has no width).</summary>
    public static BigInteger Integer(JsonNode? value, string field, BigInteger low, BigInteger high)
    {
        var number = PyJson.IsInt(value) ? PyJson.AsInt(value) : (BigInteger?)null;
        if (number is null || number < low || number > high)
        {
            throw new StateError(string.Format(CultureInfo.InvariantCulture, "{0} must be an integer between {1} and {2}", field, low, high));
        }

        return number.Value;
    }

    /// <summary><c>when(value)</c>: <c>datetime.fromisoformat(validate_timestamp(value))</c> (UTC).</summary>
    public static PyDateTime When(object? value) => PyDateTime.FromIsoformat(StateStore.ValidateTimestamp(value));

    /// <summary><c>clock()</c>: <c>datetime.now(timezone.utc)</c>, unless replaced with <see cref="OverrideClock"/>.</summary>
    public static PyDateTime Clock() => ClockOverride.Value is { } clock ? clock() : PyDateTime.Now();

    /// <summary>
    /// Replaces <see cref="Clock"/> for the current async flow until disposed, as Python's tests patch
    /// <c>task_runs.clock</c>. Only this module's clock changes: <c>utc_now()</c> stamps, the ledger and the coverage
    /// journal keep the real time, exactly as with the Python patch.
    /// </summary>
    public static IDisposable OverrideClock(Func<PyDateTime> clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var previous = ClockOverride.Value;
        ClockOverride.Value = clock;
        return new Restore(() => ClockOverride.Value = previous);
    }

    /// <summary><c>resource_cost(value)</c>: exactly the five metrics, each an int in 0..1000000, in <see cref="Metrics"/> order.</summary>
    public static JsonObject ResourceCost(JsonNode? value)
    {
        if (value is not JsonObject obj || !KeySet(obj).SetEquals(Metrics))
        {
            throw new StateError("cost requires exactly " + string.Join(", ", Metrics));
        }

        var result = new JsonObject();
        foreach (var key in Metrics)
        {
            result[key] = PyJson.Int(Integer(obj[key], key, high: 1000000));
        }

        return result;
    }

    /// <summary><c>zero()</c>.</summary>
    public static JsonObject Zero()
    {
        var result = new JsonObject();
        foreach (var key in Metrics)
        {
            result[key] = 0L;
        }

        return result;
    }

    /// <summary>
    /// <c>environment(value, account, fresh=False)</c>: a host/capability binding of this account, observed no later than
    /// 30 seconds ahead (and, when <paramref name="fresh"/>, within five minutes); returns a copy with sorted capabilities
    /// and a normalised <c>observed_at</c>.
    /// </summary>
    public static JsonObject Environment(JsonNode? value, string account, bool fresh = false)
    {
        if (value is not JsonObject obj || !KeySet(obj).SetEquals(BindingKeys))
        {
            throw new StateError("binding requires account,host,capabilities,observed_at");
        }

        if (!PyJson.PyEquals(obj["account"], PyNode.Of(account)))
        {
            throw new StateError("task binding belongs to another account");
        }

        Text(obj["host"], "host", 200);
        if (obj["capabilities"] is not JsonObject capabilities || capabilities.Count == 0 || capabilities.Count > 200)
        {
            throw new StateError("binding requires a bounded capability/version map");
        }

        foreach (var (name, version) in capabilities)
        {
            Text(PyNode.Of(name), "capability", 200);
            Text(version, "capability version", 200);
        }

        var observed = When(obj["observed_at"]);
        var age = Clock() - observed;
        if (age < PyTimeDelta.FromSeconds(-30) || (fresh && age > PyTimeDelta.FromMinutes(5)))
        {
            throw new StateError("task binding must be an actual observation within five minutes");
        }

        var sorted = new JsonObject();
        foreach (var (name, version) in capabilities.OrderBy(pair => pair.Key, PyRepr.StringComparer))
        {
            sorted[name] = PyNode.Copy(version);
        }

        var result = PyNode.CopyObject(obj);
        result["capabilities"] = sorted;
        result["observed_at"] = StateStore.ValidateTimestamp(obj["observed_at"]);
        return result;
    }

    /// <summary><c>binding_hash(value)</c>: the digest of a binding's account, host and capabilities.</summary>
    public static string BindingHash(JsonNode? value) => WorkLedger.Digest(new JsonObject
    {
        ["account"] = PyNode.Copy(PyNode.Index(value, "account")),
        ["host"] = PyNode.Copy(PyNode.Index(value, "host")),
        ["capabilities"] = PyNode.Copy(PyNode.Index(value, "capabilities")),
    });

    /// <summary><c>task_identity(account, key)</c>: <c>"task_" + digest([account, key])[:32]</c>.</summary>
    public static string TaskIdentity(string account, JsonNode? key)
    {
        Text(key, "task identity", 500);
        return "task_" + WorkLedger.Digest(new JsonArray(account, PyNode.Copy(key)))[..32];
    }

    private sealed class Restore(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}

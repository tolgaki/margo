using System.Numerics;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.State.Work;

/// <summary>
/// Module-level functions and constants of <c>skills/chief-of-staff/scripts/work_ledger.py</c>: account-scoped work
/// ledger and foreground execution journal (no network access). Other ports import these
/// (<c>task_runs.py</c>: <c>digest</c>, <c>human</c>; <c>memory_*.py</c>: <c>human</c>).
/// </summary>
public static class WorkLedger
{
    /// <summary><c>ITEM_STATES</c>.</summary>
    public static readonly IReadOnlySet<string> ItemStates = new HashSet<string>(StringComparer.Ordinal)
    {
        "candidate", "confirmed", "active", "waiting", "resolved", "rejected", "deferred", "cancelled",
    };

    /// <summary><c>ITEM_TRANSITIONS</c>.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> ItemTransitions =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["candidate"] = Set("confirmed", "rejected", "deferred", "cancelled"),
            ["confirmed"] = Set("active", "waiting", "resolved", "deferred", "cancelled"),
            ["active"] = Set("waiting", "resolved", "deferred", "cancelled"),
            ["waiting"] = Set("active", "resolved", "deferred", "cancelled"),
            ["deferred"] = Set("candidate", "confirmed", "active", "waiting", "rejected", "cancelled"),
            ["resolved"] = Set("active"),
            ["rejected"] = Set("candidate"),
            ["cancelled"] = Set("active", "candidate"),
        };

    /// <summary><c>SCHEMA</c>: byte-identical to the Python text (the CREATE statements are stored in sqlite_master).</summary>
    public const string Schema = """

CREATE TABLE IF NOT EXISTS work_sources (
 id TEXT PRIMARY KEY, account TEXT NOT NULL, family TEXT NOT NULL, scope TEXT NOT NULL,
 external_id TEXT NOT NULL, current_revision TEXT NOT NULL, created_at TEXT NOT NULL,
 UNIQUE(account,family,scope,external_id)
);
CREATE TABLE IF NOT EXISTS work_source_revisions (
 source_id TEXT NOT NULL REFERENCES work_sources(id), revision TEXT NOT NULL,
 fingerprint TEXT NOT NULL, data TEXT NOT NULL, observed_at TEXT NOT NULL,
 PRIMARY KEY(source_id,revision)
);
CREATE TABLE IF NOT EXISTS work_items (
 id TEXT PRIMARY KEY, account TEXT NOT NULL, revision INTEGER NOT NULL,
 state TEXT NOT NULL, confirmed INTEGER NOT NULL DEFAULT 0, ingest_key TEXT UNIQUE,
 ingest_hash TEXT, data TEXT NOT NULL, confirmation TEXT, created_at TEXT NOT NULL,
 updated_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS work_item_revisions (
 item_id TEXT NOT NULL REFERENCES work_items(id), revision INTEGER NOT NULL,
 snapshot TEXT NOT NULL, evidence TEXT, created_at TEXT NOT NULL,
 PRIMARY KEY(item_id,revision)
);
CREATE TABLE IF NOT EXISTS work_relationships (
 source_id TEXT NOT NULL REFERENCES work_items(id), kind TEXT NOT NULL,
 target_id TEXT NOT NULL, evidence TEXT, created_at TEXT NOT NULL,
 PRIMARY KEY(source_id,kind,target_id)
);
CREATE TABLE IF NOT EXISTS work_actions (
 id TEXT PRIMARY KEY, account TEXT NOT NULL, revision INTEGER NOT NULL,
 state TEXT NOT NULL, deferred_until TEXT, created_at TEXT NOT NULL,
 updated_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS work_action_revisions (
 action_id TEXT NOT NULL REFERENCES work_actions(id), revision INTEGER NOT NULL,
 action_hash TEXT NOT NULL, data TEXT NOT NULL, created_at TEXT NOT NULL,
 PRIMARY KEY(action_id,revision)
);
CREATE TABLE IF NOT EXISTS work_action_sources (
 action_id TEXT NOT NULL, action_revision INTEGER NOT NULL,
 source_id TEXT NOT NULL, source_revision TEXT NOT NULL, fingerprint TEXT NOT NULL,
 PRIMARY KEY(action_id,action_revision,source_id),
 FOREIGN KEY(action_id,action_revision) REFERENCES work_action_revisions(action_id,revision),
 FOREIGN KEY(source_id,source_revision) REFERENCES work_source_revisions(source_id,revision)
);
CREATE TABLE IF NOT EXISTS work_approvals (
 id TEXT PRIMARY KEY, account TEXT NOT NULL, action_id TEXT NOT NULL,
 revision INTEGER NOT NULL, action_hash TEXT NOT NULL, evidence TEXT NOT NULL,
 expires_at TEXT NOT NULL, invalidated_at TEXT, created_at TEXT NOT NULL,
 FOREIGN KEY(action_id,revision) REFERENCES work_action_revisions(action_id,revision)
);
CREATE TABLE IF NOT EXISTS work_executions (
 id TEXT PRIMARY KEY, action_id TEXT NOT NULL, revision INTEGER NOT NULL,
 approval_id TEXT NOT NULL REFERENCES work_approvals(id), state TEXT NOT NULL,
 preflight TEXT NOT NULL, receipt TEXT, started_at TEXT NOT NULL, finished_at TEXT,
 FOREIGN KEY(action_id,revision) REFERENCES work_action_revisions(action_id,revision),
 UNIQUE(action_id,revision)
);
CREATE TABLE IF NOT EXISTS work_events (
 seq INTEGER PRIMARY KEY AUTOINCREMENT, account TEXT NOT NULL, entity_id TEXT NOT NULL,
 event TEXT NOT NULL, data TEXT NOT NULL, created_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS work_records (
 id TEXT PRIMARY KEY, account TEXT NOT NULL, kind TEXT NOT NULL, revision INTEGER NOT NULL,
 state TEXT NOT NULL, identity_key TEXT, data TEXT NOT NULL, created_at TEXT NOT NULL,
 updated_at TEXT NOT NULL, UNIQUE(account,kind,identity_key)
);
CREATE TABLE IF NOT EXISTS work_record_revisions (
 record_id TEXT NOT NULL REFERENCES work_records(id), revision INTEGER NOT NULL,
 snapshot TEXT NOT NULL, evidence TEXT, created_at TEXT NOT NULL,
 PRIMARY KEY(record_id,revision)
);
CREATE TABLE IF NOT EXISTS work_record_sources (
 record_id TEXT NOT NULL REFERENCES work_records(id), record_revision INTEGER NOT NULL,
 source_id TEXT NOT NULL, source_revision TEXT NOT NULL, fingerprint TEXT NOT NULL,
 PRIMARY KEY(record_id,record_revision,source_id),
 FOREIGN KEY(source_id,source_revision) REFERENCES work_source_revisions(source_id,revision)
);
CREATE TABLE IF NOT EXISTS work_imports (
 digest TEXT PRIMARY KEY, evidence TEXT NOT NULL, item_ids TEXT NOT NULL, created_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS work_exports (
 path TEXT PRIMARY KEY, digest TEXT NOT NULL, created_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS work_meta (
 key TEXT PRIMARY KEY, value TEXT NOT NULL
);

""";

    internal static readonly IReadOnlySet<string> ExecutionStates = Set("executing", "succeeded", "partial", "outcome_unknown");

    private static readonly string[] HumanTexts = ["actor", "statement", "evidence_ref", "decision"];
    private static readonly string[] HumanPrefixes = ["conversation:", "host-interaction:", "legacy-review:"];

    internal static IReadOnlySet<string> Set(params string[] values) => new HashSet<string>(values, StringComparer.Ordinal);

    /// <summary><c>digest(value)</c>: SHA-256 hex of <c>canonical_json(value)</c>.</summary>
    public static string Digest(JsonNode? value) => PyIds.Sha256Hex(StateStore.CanonicalJson(value));

    /// <summary><c>text(value, field)</c>: a nonempty (after <c>strip()</c>) string, else <c>StateError</c>.</summary>
    public static string Text(JsonNode? value, string field)
    {
        if (PyNode.AsString(value) is not { } text || PyRepr.Strip(text).Length == 0)
        {
            throw new StateError(field + " must be a nonempty string");
        }

        return text;
    }

    /// <summary><c>timestamp(value)</c>: an ISO datetime with a UTC offset, converted to UTC.</summary>
    public static PyDateTime Timestamp(JsonNode? value)
    {
        var text = Text(value, "timestamp");
        PyDateTime parsed;
        try
        {
            parsed = PyDateTime.FromIsoformat(text.Replace("Z", "+00:00", StringComparison.Ordinal));
            if (!parsed.IsAware)
            {
                throw new PyValueError("");
            }
        }
        catch (PyValueError ex)
        {
            throw new StateError("timestamp requires an ISO datetime with UTC offset", ex);
        }

        // astimezone() may raise OverflowError, which Python does not catch here.
        return parsed.ToUtc();
    }

    /// <summary><c>timestamp(value)</c> for a string.</summary>
    public static PyDateTime Timestamp(string? value) => Timestamp(value is null ? null : PyNode.Of(value));

    /// <summary><c>now()</c>: <c>datetime.now(timezone.utc)</c>.</summary>
    public static PyDateTime Now() => PyDateTime.Now();

    /// <summary>
    /// <c>human(evidence, subject_id, revision, decision)</c>: validates a recorded human decision for the exact
    /// subject and revision. It does not authenticate the caller as a human.
    /// </summary>
    public static JsonObject Human(JsonNode? evidence, JsonNode? subjectId, JsonNode? revision, string? decision = null)
    {
        if (evidence is not JsonObject obj || !PyNode.Is(PyNode.Get(obj, "kind"), "human_confirmation"))
        {
            throw new StateError("explicit human_confirmation evidence is required");
        }

        foreach (var key in HumanTexts)
        {
            Text(PyNode.Get(obj, key), key);
        }

        var reference = PyNode.AsString(obj["evidence_ref"])!;
        if (!HumanPrefixes.Any(prefix => reference.StartsWith(prefix, StringComparison.Ordinal)))
        {
            throw new StateError("human evidence must reference a conversation, host interaction, or legacy review");
        }

        if (!PyJson.PyEquals(PyNode.Get(obj, "subject_id"), subjectId) || !PyJson.IsInt(PyNode.Get(obj, "revision"))
            || !PyJson.PyEquals(PyNode.Get(obj, "revision"), revision))
        {
            throw new StateError("human evidence must name the exact subject and revision");
        }

        if (!string.IsNullOrEmpty(decision) && !PyNode.Is(obj["decision"], decision))
        {
            throw new StateError("human evidence must record the " + decision + " decision");
        }

        if (Timestamp(PyNode.Get(obj, "decided_at")) > Now() + PyTimeDelta.FromMinutes(1))
        {
            throw new StateError("human decision cannot be in the future");
        }

        return obj;
    }

    /// <summary><c>human(...)</c> with a string subject and an int revision.</summary>
    public static JsonObject Human(JsonNode? evidence, string subjectId, long revision, string? decision = null) =>
        Human(evidence, PyNode.Of(subjectId), PyNode.Of(revision), decision);

    /// <summary><c>check_revision(row, revision)</c>: a bool or different revision is a conflict.</summary>
    public static void CheckRevision(JsonObject row, JsonNode? revision)
    {
        ArgumentNullException.ThrowIfNull(row);
        var current = PyNode.Index(row, "revision");
        if (PyJson.IsBool(revision) || !PyJson.PyEquals(current, revision))
        {
            throw new StateError("revision conflict: current revision is " + PyNode.Str(current));
        }
    }

    /// <summary><c>revision + 1</c> for an int (or float) revision that passed <see cref="CheckRevision"/>.</summary>
    internal static JsonNode? Next(JsonNode? revision) => PyJson.Scalar(revision) switch
    {
        double number => PyJson.Float(number + 1),
        var _ => PyJson.Int(PyJson.AsInt(revision) + 1),
    };
}

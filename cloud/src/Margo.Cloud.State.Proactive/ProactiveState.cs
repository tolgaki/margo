using System.Numerics;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.State.Proactive;

/// <summary>
/// Port of the library functions of <c>skills/chief-of-staff/scripts/proactive_state.py</c>: the transactional
/// proactive queue, publication receipts and source coverage. Every function takes an open
/// <see cref="PyConnection"/> from <see cref="StateStore.Connect"/> and composes with an outer
/// <see cref="StateStore.Transaction{T}"/> (nested calls become savepoints), as task_runs.py and memory_dream.py
/// use them. Python dicts and lists are <see cref="JsonObject"/>/<see cref="JsonArray"/>; results are the dicts
/// the Python functions return. Names follow the Python ones (<c>coverage_start</c> is
/// <see cref="CoverageStart"/>). Nothing here reaches the network.
/// </summary>
public static class ProactiveState
{
    /// <summary>The <c>SCHEMA</c> statements, verbatim (the schema SQL stored in <c>sqlite_master</c> must match).</summary>
    public static readonly IReadOnlyList<string> Schema =
    [
        "CREATE TABLE IF NOT EXISTS proactive_meta (\n"
        + "       key TEXT PRIMARY KEY, value TEXT NOT NULL)",
        "CREATE TABLE IF NOT EXISTS proactive_batches (\n"
        + "       id TEXT PRIMARY KEY, owner TEXT NOT NULL, created_at TEXT NOT NULL,\n"
        + "       expires_at TEXT NOT NULL, status TEXT NOT NULL\n"
        + "       CHECK(status IN ('leased','released','expired','acked')))",
        "CREATE TABLE IF NOT EXISTS proactive_items (\n"
        + "       item_key TEXT PRIMARY KEY, stable_id TEXT NOT NULL, revision TEXT NOT NULL,\n"
        + "       family TEXT NOT NULL, scope TEXT NOT NULL, payload TEXT NOT NULL,\n"
        + "       created_at TEXT NOT NULL, status TEXT NOT NULL\n"
        + "       CHECK(status IN ('pending','leased','published')),\n"
        + "       batch_id TEXT REFERENCES proactive_batches(id),\n"
        + "       UNIQUE(family,scope,stable_id,revision))",
        "CREATE TABLE IF NOT EXISTS proactive_publications (\n"
        + "       id TEXT PRIMARY KEY, batch_id TEXT NOT NULL REFERENCES proactive_batches(id),\n"
        + "       content TEXT NOT NULL, status TEXT NOT NULL\n"
        + "       CHECK(status IN ('prepared','available','published','reviewed')),\n"
        + "       created_at TEXT NOT NULL, available_at TEXT, reviewed_at TEXT, receipt TEXT)",
        "CREATE TABLE IF NOT EXISTS proactive_publication_items (\n"
        + "       publication_id TEXT NOT NULL REFERENCES proactive_publications(id),\n"
        + "       item_key TEXT NOT NULL REFERENCES proactive_items(item_key),\n"
        + "       PRIMARY KEY(publication_id,item_key))",
        "CREATE TABLE IF NOT EXISTS proactive_sources (\n"
        + "       source_key TEXT PRIMARY KEY, family TEXT NOT NULL, scope TEXT NOT NULL,\n"
        + "       window TEXT NOT NULL, status TEXT NOT NULL, latest_attempt_id TEXT,\n"
        + "       last_attempt_at TEXT, last_successful_coverage_at TEXT, checkpoint_at TEXT,\n"
        + "       covered_start TEXT, covered_end TEXT, watermark TEXT, delta_token TEXT,\n"
        + "       consecutive_failures INTEGER NOT NULL DEFAULT 0, episode INTEGER NOT NULL DEFAULT 0,\n"
        + "       error_class TEXT, retry_after_seconds INTEGER, next_retry_at TEXT,\n"
        + "       recovered_at TEXT, cadence_seconds INTEGER, failure_started_at TEXT)",
        "CREATE TABLE IF NOT EXISTS proactive_attempts (\n"
        + "       id TEXT PRIMARY KEY, source_key TEXT NOT NULL REFERENCES proactive_sources(source_key),\n"
        + "       run_id TEXT NOT NULL, started_at TEXT NOT NULL, finished_at TEXT,\n"
        + "       requested_start TEXT, requested_end TEXT, capability TEXT NOT NULL,\n"
        + "       query_version TEXT NOT NULL, kind TEXT NOT NULL, status TEXT NOT NULL,\n"
        + "       pages INTEGER NOT NULL DEFAULT 0, observations INTEGER NOT NULL DEFAULT 0,\n"
        + "       pagination_complete INTEGER NOT NULL DEFAULT 0, continuation TEXT,\n"
        + "       error_class TEXT, result TEXT)",
        "CREATE TABLE IF NOT EXISTS proactive_pages (\n"
        + "       attempt_id TEXT NOT NULL REFERENCES proactive_attempts(id), page INTEGER NOT NULL,\n"
        + "       digest TEXT NOT NULL, PRIMARY KEY(attempt_id,page))",
        "CREATE TABLE IF NOT EXISTS proactive_observations (\n"
        + "       source_key TEXT NOT NULL REFERENCES proactive_sources(source_key),\n"
        + "       stable_id TEXT NOT NULL, revision TEXT NOT NULL, payload TEXT NOT NULL,\n"
        + "       observed_at TEXT NOT NULL, PRIMARY KEY(source_key,stable_id,revision))",
        "CREATE TABLE IF NOT EXISTS proactive_health_events (\n"
        + "       source_key TEXT NOT NULL REFERENCES proactive_sources(source_key),\n"
        + "       episode INTEGER NOT NULL, transition TEXT NOT NULL, created_at TEXT NOT NULL,\n"
        + "       error_class TEXT, PRIMARY KEY(source_key,episode,transition))",
        "CREATE TABLE IF NOT EXISTS proactive_legacy_cursors (\n"
        + "       name TEXT PRIMARY KEY, timestamp TEXT NOT NULL)",
        "CREATE TABLE IF NOT EXISTS proactive_legacy_surfaced (\n"
        + "       stable_id TEXT PRIMARY KEY, metadata TEXT NOT NULL)",
        "CREATE TABLE IF NOT EXISTS proactive_imports (\n"
        + "       digest TEXT PRIMARY KEY, imported_at TEXT NOT NULL, counts TEXT NOT NULL)",
        "CREATE INDEX IF NOT EXISTS proactive_pending ON proactive_items(status,created_at)",
    ];

    /// <summary><c>ERROR_CLASSES</c>: anything else is recorded as <c>unknown</c> (raw provider text is never stored).</summary>
    public static readonly IReadOnlySet<string> ErrorClasses = new HashSet<string>(StringComparer.Ordinal)
    {
        "access_denied", "expired_token", "capability_unsupported", "throttled", "timeout", "network", "unavailable",
        "invalid_response", "authentication_required", "binding_unavailable", "unknown",
    };

    private static readonly string[] ItemMetadata = ["ts", "tier", "batch", "owner", "drained_at", "lease_expires_at", "revision", "item_key"];
    private static readonly string[] LeaseFields = ["batch", "owner", "drained_at", "lease_expires_at", "item_key"];
    private static readonly string[] ReplayIgnored = ["ts", "tier"];
    private static readonly string[] SelectionFields = ["ids", "item_keys"];
    private static readonly string[] ObservationTimestamps = ["observed_at", "modified_at"];
    private static readonly string[] FinishTimestamps = ["checkpoint_at", "watermark", "covered_start", "covered_end", "next_retry_at"];
    private static readonly string[] CheckpointClaims = ["checkpoint_at", "watermark", "covered_start", "covered_end", "delta_token"];
    private static readonly (string Name, bool IsDict)[] LegacyFiles =
        [("surfaced.json", true), ("queue.json", false), ("inflight.json", false), ("cursors.json", true)];

    // ================================================================================ schema

    /// <summary>
    /// <c>validate_schema(conn, allow_empty)</c>: every proactive table and schema version 1, or
    /// <see cref="StateError"/>; returns false only for an empty store when <paramref name="allowEmpty"/>.
    /// </summary>
    public static bool ValidateSchema(PyConnection conn, bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(conn);
        var existing = new HashSet<object?>(conn.Execute("SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'proactive_%'")
                                                .Select(row => row[0]));
        var expected = Schema.Where(statement => statement.StartsWith("CREATE TABLE", StringComparison.Ordinal))
                             .Select(statement => (object?)statement.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[5])
                             .ToHashSet();
        if (existing.Count == 0 && allowEmpty)
        {
            return false;
        }

        if (!existing.SetEquals(expected))
        {
            throw new StateError("incomplete proactive schema; refusing to recreate missing state");
        }

        var row = conn.Execute("SELECT value FROM proactive_meta WHERE key='schema_version'").FetchOne();
        if (row is null || !Py.Eq(row[0], "1"))
        {
            throw new StateError("unsupported/missing proactive schema version; explicit migration required");
        }

        return true;
    }

    /// <summary>
    /// <c>initialize(conn)</c>: creates the proactive tables (refusing a partial schema), and once retires
    /// sources whose window identity predates <c>source_identity_version</c> 2.
    /// </summary>
    public static void Initialize(PyConnection conn)
    {
        ArgumentNullException.ThrowIfNull(conn);
        StateStore.Transaction(conn, () =>
        {
            ValidateSchema(conn, allowEmpty: true);
            foreach (var statement in Schema)
            {
                conn.Execute(statement);
            }

            conn.Execute("INSERT OR IGNORE INTO proactive_meta VALUES ('schema_version','1')");
            if (conn.Execute("SELECT 1 FROM proactive_meta WHERE key='source_identity_version'").FetchOne() is null)
            {
                // Old windows mixed poll bounds with provider-bound collection identity.
                // Preserve their evidence, but require explicit revalidation before reuse.
                foreach (var row in conn.Execute("SELECT source_key,window FROM proactive_sources"))
                {
                    if (PyJson.Truthy(Py.Loads(row["window"])))
                    {
                        conn.Execute("INSERT OR REPLACE INTO proactive_meta VALUES (?,?)",
                                     "retired:" + Py.Str(row["source_key"]), "legacy window identity requires revalidation");
                    }
                }

                conn.Execute("INSERT INTO proactive_meta VALUES ('source_identity_version','2')");
            }
        });
    }

    // ================================================================================ validation helpers

    /// <summary><c>text(value, label, optional)</c>: a nonempty (after <c>strip()</c>) string without NUL.</summary>
    public static string? Text(JsonNode? value, string label, bool optional = false)
    {
        if (Py.IsNone(value) && optional)
        {
            return null;
        }

        if (!Py.IsStr(value, out var text) || PyRepr.Strip(text).Length == 0 || text.Contains('\0', StringComparison.Ordinal))
        {
            throw new StateError(label + " must be a nonempty string");
        }

        return text;
    }

    /// <summary><c>text(value, label, optional)</c> for a Python str (or None) argument.</summary>
    public static string? Text(string? value, string label, bool optional = false)
    {
        if (value is null && optional)
        {
            return null;
        }

        if (value is null || PyRepr.Strip(value).Length == 0 || value.Contains('\0', StringComparison.Ordinal))
        {
            throw new StateError(label + " must be a nonempty string");
        }

        return value;
    }

    /// <summary><c>integer(value, label, minimum, maximum)</c>: an int (never a bool) within the bounds.</summary>
    public static long Integer(JsonNode? value, string label, long minimum = 0, long maximum = 2147483647)
    {
        if (!PyJson.IsInt(value))
        {
            throw new StateError(label + " is out of range");
        }

        return Integer(PyJson.AsInt(value), label, minimum, maximum);
    }

    /// <summary><c>integer(value, label, minimum, maximum)</c> for a Python int argument.</summary>
    public static long Integer(BigInteger value, string label, long minimum = 0, long maximum = 2147483647)
    {
        if (value < minimum || value > maximum)
        {
            throw new StateError(label + " is out of range");
        }

        return (long)value;
    }

    /// <summary><c>object_json(value)</c>: a dict whose content is finite JSON.</summary>
    public static JsonObject ObjectJson(JsonNode? value)
    {
        if (value is not JsonObject obj)
        {
            throw new StateError("JSON input must be an object");
        }

        StateStore.CanonicalJson(obj);
        return obj;
    }

    /// <summary><c>digest(value)</c>: SHA-256 of the canonical JSON.</summary>
    public static string Digest(JsonNode? value) => PyIds.Sha256Hex(StateStore.CanonicalJson(value));

    private static void Fields(JsonObject data, string allowed)
    {
        var names = allowed.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var extra = data.Select(pair => pair.Key).Where(key => !names.Contains(key)).ToList();
        if (extra.Count > 0)
        {
            extra.Sort(PyRepr.StringComparer);
            throw new StateError("unknown field(s): " + string.Join(", ", extra));
        }
    }

    private static string Family(JsonNode? value)
    {
        if (!Py.IsStr(value, out var text) || !IsFamily(text))
        {
            throw new StateError("family must be a short provider/source identifier");
        }

        return text;
    }

    // re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}", value)
    private static bool IsFamily(string text)
    {
        if (text.Length is < 1 or > 128 || !char.IsAsciiLetterOrDigit(text[0]))
        {
            return false;
        }

        for (var i = 1; i < text.Length; i++)
        {
            if (!char.IsAsciiLetterOrDigit(text[i]) && text[i] is not ('_' or '.' or ':' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static string Scope(JsonNode? value)
    {
        if (!(Py.IsStr(value, out _) || value is JsonObject) || !PyJson.Truthy(value))
        {
            throw new StateError("source scope must be a nonempty string or object");
        }

        return StateStore.CanonicalJson(value);
    }

    private static JsonNode? Clone(JsonNode? node) => node?.DeepClone();

    // ================================================================================ queue

    private sealed record ItemValues(string Key, string StableId, string Revision, string Family, string Scope, string Payload, string Ts);

    private static ItemValues Item(JsonNode? data, string? tier = null)
    {
        var item = (JsonObject)ObjectJson(data).DeepClone();
        var stableId = Text(Py.Get(item, "id"), "id")!;
        var family = Family(Py.Get(item, "family", "legacy"));
        var scopeValue = Py.Get(item, "scope", new JsonObject());
        var scope = StateStore.CanonicalJson(scopeValue);
        if (!(Py.IsStr(scopeValue, out _) || scopeValue is JsonObject))
        {
            throw new StateError("item scope must be an object or string");
        }

        var content = new JsonObject();
        foreach (var (key, value) in item)
        {
            if (!ItemMetadata.Contains(key, StringComparer.Ordinal))
            {
                content[key] = Clone(value);
            }
        }

        // item.get("revision", digest(content)) evaluates the digest even when a revision is given.
        var computed = Digest(content);
        var revision = Text(Py.Get(item, "revision", computed), "revision")!;
        item["revision"] = revision;
        item["ts"] = item.ContainsKey("ts") ? Py.Timestamp(item["ts"]) : StateStore.UtcNow();
        if (!item.ContainsKey("tier"))
        {
            item["tier"] = string.IsNullOrEmpty(tier) ? "sweep" : tier;
        }

        Text(item["tier"], "tier");
        foreach (var key in LeaseFields)
        {
            item.Remove(key);
        }

        var itemKey = Digest(new JsonArray(family, scope, stableId, revision));
        return new ItemValues(itemKey, stableId, revision, family, scope, StateStore.CanonicalJson(item), (string)item["ts"]!);
    }

    private static JsonObject InsertItem(PyConnection conn, JsonNode? data, string? tier = null)
    {
        var values = Item(data, tier);
        var prior = conn.Execute("SELECT * FROM proactive_items WHERE item_key=?", values.Key).FetchOne();
        if (prior is not null)
        {
            var old = Py.Loads(prior["payload"]);
            var current = PyJson.Loads(values.Payload);
            foreach (var payload in new[] { old, current })
            {
                foreach (var key in ReplayIgnored)
                {
                    Py.Pop(payload, key);
                }
            }

            if (!PyJson.PyEquals(old, current))
            {
                throw new StateError("changed item content requires a new revision");
            }

            return new JsonObject
            {
                ["id"] = values.StableId, ["revision"] = values.Revision, ["status"] = Py.Node(prior["status"]), ["added"] = false,
            };
        }

        conn.Execute("INSERT INTO proactive_items\n        (item_key,stable_id,revision,family,scope,payload,created_at,status)\n        VALUES (?,?,?,?,?,?,?,'pending')",
                     values.Key, values.StableId, values.Revision, values.Family, values.Scope, values.Payload, values.Ts);
        return new JsonObject { ["id"] = values.StableId, ["revision"] = values.Revision, ["status"] = "pending", ["added"] = true };
    }

    /// <summary><c>queue_add(conn, data, tier)</c>: queue one stable ID and revision (replay-safe; changed content needs a new revision).</summary>
    public static JsonObject QueueAdd(PyConnection conn, JsonNode? data, string? tier = null)
    {
        ArgumentNullException.ThrowIfNull(conn);
        return StateStore.Transaction(conn, () => InsertItem(conn, data, tier));
    }

    /// <summary><c>_release_expired(conn, now)</c>: expires leased batches whose lease ended; returns how many.</summary>
    internal static int ReleaseExpired(PyConnection conn, string now)
    {
        var expired = conn.Execute("SELECT id FROM proactive_batches WHERE status='leased' AND expires_at<=?", now)
                          .Select(row => row[0]).ToList();
        foreach (var batch in expired)
        {
            conn.Execute("UPDATE proactive_items SET status='pending',batch_id=NULL WHERE batch_id=? AND status='leased'", batch);
            conn.Execute("UPDATE proactive_batches SET status='expired' WHERE id=?", batch);
        }

        return expired.Count;
    }

    /// <summary><c>_render_item(row, batch)</c>: the stored payload plus key, status and lease fields.</summary>
    internal static JsonObject RenderItem(PyRow row, PyRow? batch = null)
    {
        var loaded = Py.Loads(row["payload"]);
        if (loaded is not JsonObject item)
        {
            throw new PyAttributeError("'" + Py.TypeName(loaded) + "' object has no attribute 'update'");
        }

        item["item_key"] = Py.Node(row["item_key"]);
        item["status"] = Py.Node(row["status"]);
        if (batch is not null)
        {
            item["batch"] = Py.Node(batch["id"]);
            item["owner"] = Py.Node(batch["owner"]);
            item["drained_at"] = Py.Node(batch["created_at"]);
            item["lease_expires_at"] = Py.Node(batch["expires_at"]);
        }

        return item;
    }

    /// <summary><c>queue_drain(conn, owner, lease_seconds, limit)</c>: lease pending (and expired) items; never steals a live batch.</summary>
    public static JsonArray QueueDrain(PyConnection conn, string? owner = null, BigInteger? leaseSeconds = null, BigInteger? limit = null)
    {
        ArgumentNullException.ThrowIfNull(conn);
        var lease = Integer(leaseSeconds ?? 900, "lease_seconds", 1, 86400);
        var count = Integer(limit ?? 100, "limit", 1, 10000);
        var batchId = PyIds.Uuid4Hex();
        owner = Text(string.IsNullOrEmpty(owner) ? batchId : owner, "owner")!;
        var now = StateStore.UtcNow();
        var expires = (PyDateTime.FromIsoformat(now) + PyTimeDelta.FromSeconds(lease)).Isoformat("microseconds");
        return StateStore.Transaction(conn, () =>
        {
            ReleaseExpired(conn, now);
            var rows = conn.Execute("SELECT * FROM proactive_items WHERE status='pending' ORDER BY created_at,item_key LIMIT ?", count).ToList();
            if (rows.Count == 0)
            {
                return new JsonArray();
            }

            conn.Execute("INSERT INTO proactive_batches VALUES (?,?,?,?,'leased')", batchId, owner, now, expires);
            conn.ExecuteMany("UPDATE proactive_items SET status='leased',batch_id=? WHERE item_key=?",
                             rows.Select(row => (IReadOnlyList<object?>)[batchId, row["item_key"]]).ToList());
            var batch = conn.Execute("SELECT * FROM proactive_batches WHERE id=?", batchId).FetchOne();
            var result = new JsonArray();
            foreach (var row in rows)
            {
                var item = RenderItem(row, batch);
                item["status"] = "leased";
                result.Add(item);
            }

            return result;
        });
    }

    private static PyRow LiveBatch(PyConnection conn, object? batchId)
    {
        var row = conn.Execute("SELECT * FROM proactive_batches WHERE id=?", batchId).FetchOne();
        if (row is null || !Py.Eq(row["status"], "leased") || Py.Compare(row["expires_at"], StateStore.UtcNow(), "<=") <= 0)
        {
            throw new StateError("batch is absent, released, acknowledged or expired; cannot publish/ack");
        }

        return row;
    }

    /// <summary><c>queue_release(conn, batch_id, owner)</c>: owner-only release of a live lease (replay-safe).</summary>
    public static JsonObject QueueRelease(PyConnection conn, string batchId, string owner)
    {
        ArgumentNullException.ThrowIfNull(conn);
        return StateStore.Transaction(conn, () =>
        {
            var batch = conn.Execute("SELECT * FROM proactive_batches WHERE id=?", batchId).FetchOne();
            if (batch is null || !Py.Eq(batch["owner"], owner))
            {
                throw new StateError("batch owner mismatch");
            }

            if (Py.Eq(batch["status"], "released") || Py.Eq(batch["status"], "expired"))
            {
                return new JsonObject { ["batch"] = batchId, ["released"] = 0L };
            }

            if (!Py.Eq(batch["status"], "leased"))
            {
                throw new StateError("cannot release an acknowledged batch");
            }

            var released = conn.Execute("UPDATE proactive_items SET status='pending',batch_id=NULL WHERE batch_id=? AND status='leased'", batchId).RowCount;
            conn.Execute("UPDATE proactive_batches SET status='released' WHERE id=?", batchId);
            return new JsonObject { ["batch"] = batchId, ["released"] = released };
        });
    }

    /// <summary><c>queue_renew(conn, batch_id, owner, lease_seconds)</c>: owner-only extension of a live lease.</summary>
    public static JsonObject QueueRenew(PyConnection conn, string batchId, string owner, BigInteger leaseSeconds)
    {
        ArgumentNullException.ThrowIfNull(conn);
        var lease = Integer(leaseSeconds, "lease_seconds", 1, 86400);
        return StateStore.Transaction(conn, () =>
        {
            var batch = LiveBatch(conn, batchId);
            if (!Py.Eq(batch["owner"], owner))
            {
                throw new StateError("batch owner mismatch");
            }

            var expires = (PyDateTime.Now() + PyTimeDelta.FromSeconds(lease)).Isoformat("microseconds");
            conn.Execute("UPDATE proactive_batches SET expires_at=? WHERE id=?", expires, batchId);
            return new JsonObject { ["batch"] = batchId, ["expires_at"] = expires };
        });
    }

    // ================================================================================ publications

    private static string? Receipt(JsonNode? status, JsonNode? receipt, string publicationId)
    {
        if (Py.StrEquals(status, "prepared"))
        {
            if (!Py.IsNone(receipt))
            {
                throw new StateError("prepared output cannot claim a publication receipt");
            }

            return null;
        }

        var value = ObjectJson(receipt);
        if (Py.StrEquals(status, "available"))
        {
            Fields(value, "kind");
            if (!Py.StrEquals(Py.Get(value, "kind"), "local"))
            {
                throw new StateError("available requires a local durable-output receipt");
            }

            return StateStore.CanonicalJson(new JsonObject { ["kind"] = "local", ["publication_id"] = publicationId });
        }

        if (!Py.StrEquals(status, "published") || !Py.StrEquals(Py.Get(value, "kind"), "host"))
        {
            throw new StateError("published requires a host publication receipt");
        }

        Fields(value, "kind receipt_id location published_at");
        var publishedAt = Py.Timestamp(Py.Get(value, "published_at"));
        if (Py.Compare(publishedAt, StateStore.UtcNow(), ">") > 0)
        {
            throw new StateError("publication receipt cannot be future-dated");
        }

        var receiptId = Text(Py.Get(value, "receipt_id"), "receipt_id");
        var location = Text(Py.Get(value, "location"), "location");
        return StateStore.CanonicalJson(new JsonObject
        {
            ["kind"] = "host", ["receipt_id"] = receiptId, ["location"] = location, ["published_at"] = publishedAt,
        });
    }

    private static List<PyRow> SelectItems(List<PyRow> rows, JsonObject data)
    {
        var ids = Py.Get(data, "ids");
        var keys = Py.Get(data, "item_keys");
        if (Py.IsNone(ids) == Py.IsNone(keys))
        {
            throw new StateError("specify exactly one of ids or item_keys");
        }

        var selected = !Py.IsNone(ids) ? ids : keys;
        var values = new List<string>();
        if (selected is not JsonArray list || list.Count == 0)
        {
            throw new StateError("included items must be a nonempty list of strings");
        }

        foreach (var entry in list)
        {
            if (!Py.IsStr(entry, out var text) || text.Length == 0)
            {
                throw new StateError("included items must be a nonempty list of strings");
            }

            values.Add(text);
        }

        if (values.Distinct(StringComparer.Ordinal).Count() != values.Count)
        {
            throw new StateError("included items must not contain duplicates");
        }

        var column = !Py.IsNone(ids) ? "stable_id" : "item_key";
        var chosen = rows.Where(row => values.Any(value => Py.Eq(row[column], value))).ToList();
        if (chosen.Count != values.Count)
        {
            throw new StateError("items are missing or IDs are ambiguous; use exact item_keys");
        }

        return chosen;
    }

    private static bool Standalone(PyConnection conn, string publicationId) =>
        conn.Execute("SELECT 1 FROM proactive_meta WHERE key=?", "standalone:" + publicationId).FetchOne() is not null;

    private const string PublicationItemsSql =
        "SELECT i.* FROM proactive_items i\n                JOIN proactive_publication_items pi ON pi.item_key=i.item_key\n                WHERE pi.publication_id=?";

    /// <summary>
    /// <c>publication_record(conn, batch_id, data)</c>: durably saves output with its exact included items and
    /// receipt. A null <paramref name="batchId"/> records standalone output with no queue membership. An
    /// explicit <c>id</c> makes the call replay-safe.
    /// </summary>
    public static JsonObject PublicationRecord(PyConnection conn, string? batchId, JsonNode? data)
    {
        ArgumentNullException.ThrowIfNull(conn);
        var input = ObjectJson(data);
        Fields(input, "id content ids item_keys status receipt");
        var generated = PyIds.Uuid4Hex();
        var publicationId = Text(Py.Get(input, "id", generated), "publication id")!;
        var content = Text(Py.Get(input, "content"), "content")!;
        var status = Py.Get(input, "status", "prepared");
        var receipt = Receipt(status, Py.Get(input, "receipt"), publicationId);
        var statusText = Py.IsStr(status, out var text) ? text : throw new InvalidOperationException("unreachable: _receipt accepts only str statuses");
        var standalone = batchId is null;
        if (standalone && SelectionFields.Any(key => Py.Get(input, key) is var value && !Py.IsNone(value) && !(value is JsonArray { Count: 0 })))
        {
            throw new StateError("standalone publications cannot claim queued items");
        }

        var now = StateStore.UtcNow();
        return StateStore.Transaction(conn, () =>
        {
            var existing = conn.Execute("SELECT * FROM proactive_publications WHERE id=?", publicationId).FetchOne();
            if (existing is not null)
            {
                if (Standalone(conn, publicationId) != standalone)
                {
                    throw new StateError("publication ID already belongs to another publication mode");
                }

                var rows = conn.Execute(PublicationItemsSql, publicationId).ToList();
                var chosen = standalone ? [] : SelectItems(rows, input);
                if (chosen.Count != rows.Count || (!standalone && !Py.Eq(existing["batch_id"], batchId))
                    || !Py.Eq(existing["content"], content) || !Py.Eq(existing["status"], statusText)
                    || !Py.Eq(existing["receipt"], receipt))
                {
                    throw new StateError("publication ID already exists with different content or status");
                }

                return new JsonObject
                {
                    ["publication_id"] = publicationId, ["status"] = statusText, ["standalone"] = standalone, ["replayed"] = true,
                };
            }

            List<PyRow> included;
            var batch = batchId;
            if (standalone)
            {
                batch = "output-" + PyIds.Uuid4Hex();
                conn.Execute("INSERT INTO proactive_batches VALUES (?,?,?,?,'acked')", batch, publicationId, now, now);
                conn.Execute("INSERT INTO proactive_meta VALUES (?,?)", "standalone:" + publicationId, "1");
                included = [];
            }
            else
            {
                LiveBatch(conn, batch);
                var rows = conn.Execute("SELECT * FROM proactive_items WHERE batch_id=? AND status='leased'", batch).ToList();
                included = SelectItems(rows, input);
            }

            conn.Execute("INSERT INTO proactive_publications VALUES (?,?,?,?,?,?,NULL,?)",
                         publicationId, batch, content, statusText, now, receipt is not null ? now : null, receipt);
            conn.ExecuteMany("INSERT INTO proactive_publication_items VALUES (?,?)",
                             included.Select(row => (IReadOnlyList<object?>)[publicationId, row["item_key"]]).ToList());
            return new JsonObject
            {
                ["publication_id"] = publicationId, ["status"] = statusText, ["standalone"] = standalone, ["included_items"] = (long)included.Count,
            };
        });
    }

    /// <summary><c>publication_publish(conn, publication_id, data)</c>: makes a prepared output available or published (receipts are immutable).</summary>
    public static JsonObject PublicationPublish(PyConnection conn, string publicationId, JsonNode? data)
    {
        ArgumentNullException.ThrowIfNull(conn);
        var input = ObjectJson(data);
        Fields(input, "status receipt");
        var status = Py.Get(input, "status");
        if (!Py.StrIn(status, "available", "published"))
        {
            throw new StateError("publication status must be available or published");
        }

        var statusText = (string)PyJson.Scalar(status)!;
        var receipt = Receipt(status, Py.Get(input, "receipt"), publicationId);
        return StateStore.Transaction(conn, () =>
        {
            var publication = conn.Execute("SELECT * FROM proactive_publications WHERE id=?", publicationId).FetchOne()
                              ?? throw new StateError("unknown publication");
            if (Py.Eq(publication["status"], statusText) && Py.Eq(publication["receipt"], receipt))
            {
                return new JsonObject { ["publication_id"] = publicationId, ["status"] = statusText, ["replayed"] = true };
            }

            if (!Py.Eq(publication["status"], "prepared"))
            {
                throw new StateError("publication receipt is immutable once available/published");
            }

            if (!Standalone(conn, publicationId))
            {
                LiveBatch(conn, publication["batch_id"]);
            }

            conn.Execute("UPDATE proactive_publications SET status=?,receipt=?,available_at=? WHERE id=?",
                         statusText, receipt, StateStore.UtcNow(), publicationId);
            return new JsonObject { ["publication_id"] = publicationId, ["status"] = statusText };
        });
    }

    /// <summary>
    /// <c>queue_ack(conn, receipt_id, batch_id, ids, item_keys)</c>: acknowledges only items the receipt covers;
    /// replay-safe; never acknowledges through a prepared, standalone or foreign-batch receipt.
    /// </summary>
    public static JsonObject QueueAck(PyConnection conn, string? receiptId, string? batchId = null,
                                      IReadOnlyList<string>? ids = null, IReadOnlyList<string>? itemKeys = null)
    {
        ArgumentNullException.ThrowIfNull(conn);
        if (string.IsNullOrEmpty(receiptId))
        {
            throw new StateError("queue-ack/mark requires a durable --receipt publication ID");
        }

        var hasIds = ids is { Count: > 0 };
        var hasKeys = itemKeys is { Count: > 0 };
        if (string.IsNullOrEmpty(batchId) && !hasIds && !hasKeys)
        {
            throw new StateError("queue-ack requires --batch, IDs or --item-key");
        }

        return StateStore.Transaction(conn, () =>
        {
            var publication = conn.Execute("SELECT * FROM proactive_publications WHERE id=?", receiptId).FetchOne();
            if (publication is null || !(Py.Eq(publication["status"], "available") || Py.Eq(publication["status"], "published")
                                         || Py.Eq(publication["status"], "reviewed"))
                || !Py.Truthy(publication["receipt"]))
            {
                throw new StateError("ack requires an available/published output with a durable receipt");
            }

            if (Standalone(conn, receiptId))
            {
                throw new StateError("standalone output has no queue items to acknowledge");
            }

            var publicationBatch = publication["batch_id"];
            if (!string.IsNullOrEmpty(batchId) && !Py.Eq(batchId, publicationBatch))
            {
                throw new StateError("receipt belongs to a different batch");
            }

            var rows = conn.Execute(PublicationItemsSql, receiptId).ToList();
            List<PyRow> chosen;
            if (hasIds || hasKeys)
            {
                var selection = hasIds
                    ? new JsonObject { ["ids"] = new JsonArray(ids!.Select(id => (JsonNode?)id).ToArray()) }
                    : new JsonObject { ["item_keys"] = itemKeys is null ? null : new JsonArray(itemKeys.Select(key => (JsonNode?)key).ToArray()) };
                chosen = SelectItems(rows, selection);
            }
            else
            {
                chosen = rows;
            }

            if (chosen.All(row => Py.Eq(row["status"], "published") && Py.Eq(row["batch_id"], publicationBatch)))
            {
                return new JsonObject { ["acked"] = 0L, ["replayed"] = true, ["publication_id"] = receiptId };
            }

            LiveBatch(conn, publicationBatch);
            foreach (var row in chosen)
            {
                if (!Py.Eq(row["batch_id"], publicationBatch) || !(Py.Eq(row["status"], "leased") || Py.Eq(row["status"], "published")))
                {
                    throw new StateError("receipt no longer owns the selected item lease");
                }
            }

            long count = 0;
            foreach (var row in chosen)
            {
                count += conn.Execute("UPDATE proactive_items SET status='published' WHERE item_key=? AND status='leased'", row["item_key"]).RowCount;
            }

            var remaining = conn.Execute("SELECT count(*) FROM proactive_items WHERE batch_id=? AND status='leased'", publicationBatch).FetchOne()![0];
            if (!Py.Truthy(remaining))
            {
                conn.Execute("UPDATE proactive_batches SET status='acked' WHERE id=?", publicationBatch);
            }

            return new JsonObject { ["acked"] = count, ["remaining"] = Py.Node(remaining), ["publication_id"] = receiptId };
        });
    }

    /// <summary><c>publication_review(conn, publication_id, reviewed_at)</c>: explicit human review, never inferred from delivery.</summary>
    public static JsonObject PublicationReview(PyConnection conn, string publicationId, string? reviewedAt = null)
    {
        ArgumentNullException.ThrowIfNull(conn);
        var timestamp = reviewedAt is not null ? StateStore.ValidateTimestamp(reviewedAt) : StateStore.UtcNow();
        return StateStore.Transaction(conn, () =>
        {
            var row = conn.Execute("SELECT * FROM proactive_publications WHERE id=?", publicationId).FetchOne();
            if (row is null || !(Py.Eq(row["status"], "available") || Py.Eq(row["status"], "published") || Py.Eq(row["status"], "reviewed")))
            {
                throw new StateError("only an available/published output can be explicitly reviewed");
            }

            if (Py.Compare(timestamp, row["available_at"], "<") < 0)
            {
                throw new StateError("review cannot predate output availability");
            }

            conn.Execute("UPDATE proactive_publications SET status='reviewed',reviewed_at=COALESCE(reviewed_at,?) WHERE id=?",
                         timestamp, publicationId);
            return new JsonObject { ["publication_id"] = publicationId, ["status"] = "reviewed" };
        });
    }

    // ================================================================================ coverage

    private static JsonObject Window(JsonNode? value)
    {
        var window = (JsonObject)ObjectJson(value).DeepClone();
        Fields(window, "start end");
        if (window.Count > 0)
        {
            window["start"] = Py.Timestamp(Py.Get(window, "start"));
            window["end"] = Py.Timestamp(Py.Get(window, "end"));
            if (Py.Compare((string)window["start"]!, (string)window["end"]!, ">=") >= 0)
            {
                throw new StateError("window start must precede end");
            }
        }

        return window;
    }

    private static string? WindowBound(JsonObject window, string name) =>
        window.TryGetPropertyValue(name, out var value) ? (string?)value : null;

    /// <summary>
    /// <c>coverage_start(conn, data)</c>: starts a scoped source attempt (identity = account + family + scope +
    /// optional collection_window), preserving the last successful checkpoint. Returns attempt_id, source_key, status.
    /// </summary>
    public static JsonObject CoverageStart(PyConnection conn, JsonNode? data)
    {
        ArgumentNullException.ThrowIfNull(conn);
        var input = ObjectJson(data);
        Fields(input, "family scope window collection_window run_id capability query_version kind "
                      + "cadence_seconds resume_attempt reauthenticated reactivate");
        var family = Family(Py.Get(input, "family"));
        var scope = Scope(Py.Get(input, "scope"));
        var window = Window(Py.Get(input, "window", new JsonObject()));
        var collectionWindow = Window(Py.Get(input, "collection_window", new JsonObject()));
        if (collectionWindow.Count > 0 && !PyJson.PyEquals(window, collectionWindow))
        {
            throw new StateError("a provider-bound collection_window must match its requested window");
        }

        var runId = Text(Py.Get(input, "run_id"), "run_id")!;
        var capability = Text(Py.Get(input, "capability"), "capability")!;
        var queryVersion = Text(Py.Get(input, "query_version"), "query_version")!;
        var kindValue = Py.Get(input, "kind", "enumeration");
        if (!Py.StrIn(kindValue, "enumeration", "search", "snapshot"))
        {
            throw new StateError("kind must be enumeration, search or snapshot");
        }

        var kind = (string)PyJson.Scalar(kindValue)!;
        var cadenceValue = Py.Get(input, "cadence_seconds");
        if (!PyJson.IsBool(Py.Get(input, "reauthenticated", false)))
        {
            throw new StateError("reauthenticated must be an explicit boolean");
        }

        if (!PyJson.IsBool(Py.Get(input, "reactivate", false)))
        {
            throw new StateError("reactivate must be an explicit boolean");
        }

        long? cadence = null;
        if (!Py.IsNone(cadenceValue))
        {
            cadence = Integer(cadenceValue, "cadence_seconds", 1);
        }

        var principalRow = conn.Execute("SELECT value FROM margo_meta WHERE key='account'").FetchOne()
                           ?? throw new PyTypeError("'NoneType' object is not subscriptable");
        var principal = principalRow[0];
        var key = Digest(new JsonArray(Py.Node(principal), family, scope, Clone(collectionWindow)));
        var attemptId = PyIds.Uuid4Hex();
        var now = StateStore.UtcNow();
        StateStore.Transaction(conn, () =>
        {
            var priorSource = conn.Execute("SELECT * FROM proactive_sources WHERE source_key=?", key).FetchOne();
            if (conn.Execute("SELECT 1 FROM proactive_meta WHERE key=?", "retired:" + key).FetchOne() is not null)
            {
                if (!PyJson.Truthy(Py.Get(input, "reactivate")))
                {
                    throw new StateError("source is retired; explicitly revalidate and reactivate before reuse");
                }

                conn.Execute("DELETE FROM proactive_meta WHERE key=?", "retired:" + key);
            }

            if (priorSource is not null && Py.Eq(priorSource["error_class"], "authentication_required")
                && !PyJson.Truthy(Py.Get(input, "reauthenticated", false)))
            {
                throw new StateError("source is authentication-blocked; reauthenticate before an explicit reauthenticated retry");
            }

            PyRow? previous = null;
            if (!Py.IsNone(Py.Get(input, "resume_attempt")))
            {
                previous = Attempt(conn, Text(input["resume_attempt"], "resume_attempt"));
                var errorClass = previous["error_class"];
                if (!Py.Eq(previous["source_key"], key)
                    || !(Py.Eq(previous["status"], "partial") || Py.Eq(previous["status"], "failed"))
                    || Py.Truthy(previous["pagination_complete"]) || !Py.Truthy(previous["continuation"])
                    || !Py.Eq(previous["kind"], kind) || !Py.Eq(previous["query_version"], queryVersion)
                    || !Py.Eq(previous["capability"], capability)
                    || !Py.Eq(previous["requested_start"], WindowBound(window, "start"))
                    || !Py.Eq(previous["requested_end"], WindowBound(window, "end"))
                    || Py.Eq(errorClass, "expired_token") || Py.Eq(errorClass, "access_denied")
                    || Py.Eq(errorClass, "authentication_required"))
                {
                    throw new StateError("resume requires compatible partial pages and a still-usable continuation");
                }
            }

            conn.Execute("INSERT OR IGNORE INTO proactive_sources\n            (source_key,family,scope,window,status) VALUES (?,?,?,?,'unknown')",
                         key, family, scope, StateStore.CanonicalJson(collectionWindow));
            conn.Execute("INSERT INTO proactive_attempts\n            (id,source_key,run_id,started_at,requested_start,requested_end,capability,query_version,kind,status)\n            VALUES (?,?,?,?,?,?,?,?,?,'running')",
                         attemptId, key, runId, now, WindowBound(window, "start"), WindowBound(window, "end"),
                         capability, queryVersion, kind);
            var contract = StateStore.CanonicalJson(new JsonArray(capability, queryVersion, kind));
            var tokenContract = conn.Execute("SELECT value FROM proactive_meta WHERE key=?", "token-contract:" + key).FetchOne();
            if (priorSource is not null && priorSource["delta_token"] is not null
                && (tokenContract is null || !Py.Eq(tokenContract[0], contract)))
            {
                // Provider tokens are bound to a query contract, not just a mailbox/window.
                conn.Execute("UPDATE proactive_sources SET delta_token=NULL WHERE source_key=?", key);
                conn.Execute("DELETE FROM proactive_meta WHERE key=?", "token-contract:" + key);
                conn.Execute("INSERT INTO proactive_meta VALUES (?,?)", "resync:" + attemptId, "query_contract_changed");
            }

            conn.Execute("UPDATE proactive_sources SET status='running',latest_attempt_id=?,\n            last_attempt_at=?,cadence_seconds=COALESCE(?,cadence_seconds) WHERE source_key=?",
                         attemptId, now, cadence, key);
            if (previous is not null)
            {
                conn.Execute("INSERT INTO proactive_pages SELECT ?,page,digest FROM proactive_pages\n                WHERE attempt_id=?", attemptId, previous["id"]);
                conn.Execute("UPDATE proactive_attempts SET pages=?,observations=?,continuation=? WHERE id=?",
                             previous["pages"], previous["observations"], previous["continuation"], attemptId);
            }
        });
        return new JsonObject { ["attempt_id"] = attemptId, ["source_key"] = key, ["status"] = "running" };
    }

    /// <summary><c>coverage_retire(conn, source_key, reason)</c>: excludes a finished historical collection from monitoring.</summary>
    public static JsonObject CoverageRetire(PyConnection conn, string sourceKey, string reason)
    {
        ArgumentNullException.ThrowIfNull(conn);
        Text(reason, "retirement reason");
        StateStore.Transaction(conn, () =>
        {
            var source = conn.Execute("SELECT * FROM proactive_sources WHERE source_key=?", sourceKey).FetchOne()
                         ?? throw new StateError("unknown source");
            if (Py.Eq(Attempt(conn, source["latest_attempt_id"])["status"], "running"))
            {
                throw new StateError("finish the source's running attempt before retirement");
            }

            conn.Execute("INSERT OR REPLACE INTO proactive_meta VALUES (?,?)", "retired:" + sourceKey, reason);
        });
        return new JsonObject { ["source_key"] = sourceKey, ["retired"] = true };
    }

    /// <summary><c>_attempt(conn, attempt_id)</c>: the attempt row, or <c>unknown coverage attempt</c>.</summary>
    public static PyRow Attempt(PyConnection conn, object? attemptId)
    {
        ArgumentNullException.ThrowIfNull(conn);
        return conn.Execute("SELECT * FROM proactive_attempts WHERE id=?", attemptId).FetchOne()
               ?? throw new StateError("unknown coverage attempt");
    }

    /// <summary>
    /// <c>coverage_page(conn, attempt_id, data)</c>: stores one contiguous page and its observations atomically;
    /// an identical replay is a no-op, a different one is refused.
    /// </summary>
    public static JsonObject CoveragePage(PyConnection conn, string attemptId, JsonNode? data)
    {
        ArgumentNullException.ThrowIfNull(conn);
        var input = ObjectJson(data);
        Fields(input, "page observations final continuation");
        var page = Integer(Py.Get(input, "page"), "page", 1);
        if (Py.Get(input, "observations") is not JsonArray observations)
        {
            throw new StateError("observations must be a list, including for empty enumeration");
        }

        var finalValue = Py.Get(input, "final");
        if (!PyJson.IsBool(finalValue))
        {
            throw new StateError("final must be true or false");
        }

        var final = PyJson.Truthy(finalValue);
        var continuation = Text(Py.Get(input, "continuation"), "continuation", optional: true);
        if (final && continuation is not null)
        {
            throw new StateError("a final page cannot have a continuation");
        }

        var validated = new List<(string StableId, string Revision, string Payload, string ObservedAt)>();
        foreach (var entry in observations)
        {
            var observation = ObjectJson(entry);
            var stableId = Text(Py.Get(observation, "id"), "observation id")!;
            var revision = Text(Py.Get(observation, "revision"), "observation revision")!;
            foreach (var name in ObservationTimestamps)
            {
                if (observation.ContainsKey(name))
                {
                    Py.Timestamp(observation[name]);
                }
            }

            var observedAt = observation.ContainsKey("observed_at") ? Py.Timestamp(observation["observed_at"]) : StateStore.UtcNow();
            var payload = new JsonObject();
            foreach (var (key, value) in observation)
            {
                if (key != "observed_at")
                {
                    payload[key] = Clone(value);
                }
            }

            validated.Add((stableId, revision, StateStore.CanonicalJson(payload), observedAt));
        }

        // digest(dict(data, observations=[json.loads(row[2]) for row in validated]))
        var fingerprintSource = (JsonObject)input.DeepClone();
        fingerprintSource["observations"] = new JsonArray(validated.Select(row => PyJson.Loads(row.Payload)).ToArray());
        var fingerprint = Digest(fingerprintSource);
        return StateStore.Transaction(conn, () =>
        {
            var attempt = Attempt(conn, attemptId);
            var existing = conn.Execute("SELECT digest FROM proactive_pages WHERE attempt_id=? AND page=?", attemptId, page).FetchOne();
            if (existing is not null)
            {
                if (!Py.Eq(existing[0], fingerprint))
                {
                    throw new StateError("page replay differs from the durable page");
                }

                return new JsonObject { ["attempt_id"] = attemptId, ["page"] = page, ["replayed"] = true };
            }

            if (!Py.Eq(attempt["status"], "running") || Py.Truthy(attempt["pagination_complete"]) || page != Py.Int(attempt["pages"]) + 1)
            {
                throw new StateError("attempt must be running and pages contiguous, with no page after final");
            }

            foreach (var (stableId, revision, payload, observedAt) in validated)
            {
                var prior = conn.Execute("SELECT payload FROM proactive_observations\n                WHERE source_key=? AND stable_id=? AND revision=?",
                                         attempt["source_key"], stableId, revision).FetchOne();
                var priorPayload = prior is not null ? Py.Loads(prior[0]) : null;
                if (!Py.IsNone(priorPayload))
                {
                    Py.Pop(priorPayload, "observed_at");
                }

                if (prior is not null && StateStore.CanonicalJson(priorPayload) != payload)
                {
                    throw new StateError("changed source observation requires a new revision");
                }

                conn.Execute("INSERT INTO proactive_observations VALUES (?,?,?,?,?)\n                ON CONFLICT(source_key,stable_id,revision) DO UPDATE SET\n                observed_at=MAX(observed_at,excluded.observed_at),payload=excluded.payload",
                             attempt["source_key"], stableId, revision, payload, observedAt);
            }

            conn.Execute("INSERT INTO proactive_pages VALUES (?,?,?)", attemptId, page, fingerprint);
            conn.Execute("UPDATE proactive_attempts SET pages=?,observations=observations+?,\n            pagination_complete=?,continuation=? WHERE id=?",
                         page, (long)validated.Count, final ? 1L : 0L, continuation, attemptId);
            return new JsonObject
            {
                ["attempt_id"] = attemptId, ["page"] = page, ["observations_stored"] = (long)validated.Count, ["final"] = final,
            };
        });
    }

    /// <summary>
    /// <c>coverage_finish(conn, attempt_id, data)</c>: finishes an attempt (complete/partial/blocked/failed/unknown).
    /// Only a complete enumeration with every page recorded advances the checkpoint; failures open or extend a
    /// health episode. A replay with the same result is a no-op.
    /// </summary>
    public static JsonObject CoverageFinish(PyConnection conn, string attemptId, JsonNode? data)
    {
        ArgumentNullException.ThrowIfNull(conn);
        var input = (JsonObject)ObjectJson(data).DeepClone();
        Fields(input, "status checkpoint_at watermark delta_token covered_start covered_end error_class "
                      + "retry_after_seconds next_retry_at");
        var statusValue = Py.Get(input, "status");
        if (!Py.StrIn(statusValue, "complete", "partial", "blocked", "failed", "unknown"))
        {
            throw new StateError("invalid coverage completion status");
        }

        var status = (string)PyJson.Scalar(statusValue)!;
        foreach (var name in FinishTimestamps)
        {
            if (!Py.IsNone(Py.Get(input, name)))
            {
                input[name] = Py.Timestamp(input[name]);
            }
        }

        var token = Text(Py.Get(input, "delta_token"), "delta_token", optional: true);
        var retryValue = Py.Get(input, "retry_after_seconds");
        long? retry = null;
        if (!Py.IsNone(retryValue))
        {
            retry = Integer(retryValue, "retry_after_seconds");
        }

        var error = Py.Get(input, "error_class");
        if (!Py.IsNone(error))
        {
            input["error_class"] = Py.IsStr(error, out var errorText) && ErrorClasses.Contains(errorText) ? errorText : "unknown";
        }

        string? StrField(string name) => Py.IsNone(Py.Get(input, name)) ? null : (string)Py.Get(input, name)!;

        if (status == "blocked")
        {
            if (StrField("error_class") is { } blockedError && blockedError is not ("access_denied" or "authentication_required"))
            {
                throw new StateError("blocked means explicit access denial or required authentication");
            }

            if (retry is not null || StrField("next_retry_at") is not null)
            {
                throw new StateError("blocked sources cannot schedule automatic retries");
            }
        }

        if (StrField("error_class") == "authentication_required" && status != "blocked")
        {
            throw new StateError("authentication_required must be recorded as blocked");
        }

        if (status != "complete" && CheckpointClaims
                .Any(name => !Py.IsNone(Py.Get(input, name))))
        {
            throw new StateError("non-complete attempts cannot claim a successful checkpoint");
        }

        if (status == "complete" && (!Py.IsNone(error) || retry is not null || PyJson.Truthy(Py.Get(input, "next_retry_at"))))
        {
            throw new StateError("complete coverage cannot simultaneously report a failure/retry");
        }

        var resultJson = StateStore.CanonicalJson(input);
        var now = StateStore.UtcNow();
        var applied = StateStore.Transaction(conn, () =>
        {
            var attempt = Attempt(conn, attemptId);
            if (!Py.Eq(attempt["status"], "running"))
            {
                if (!Py.Eq(attempt["result"], resultJson))
                {
                    throw new StateError("attempt is already finished with a different result");
                }

                return (bool?)null;
            }

            var source = conn.Execute("SELECT * FROM proactive_sources WHERE source_key=?", attempt["source_key"]).FetchOne()
                         ?? throw new PyTypeError("'NoneType' object is not subscriptable");
            string? checkpoint = null;
            object? start = null;
            object? end = null;
            if (status == "complete")
            {
                if (!Py.Eq(attempt["kind"], "enumeration") || !Py.Truthy(attempt["pagination_complete"]) || !Py.Truthy(attempt["pages"]))
                {
                    throw new StateError("complete requires all pages of an enumeration durably recorded");
                }

                checkpoint = StrField("checkpoint_at") ?? throw new StateError("complete requires checkpoint_at");
                if (Py.Compare(checkpoint, now, ">") > 0
                    || (Py.Truthy(source["checkpoint_at"]) && Py.Compare(checkpoint, source["checkpoint_at"], "<") < 0))
                {
                    throw new StateError("checkpoint is future-dated or regresses reliable coverage");
                }

                start = input.ContainsKey("covered_start") ? StrField("covered_start") : attempt["requested_start"];
                end = input.ContainsKey("covered_end") ? StrField("covered_end") : attempt["requested_end"];
                if ((start is null) != (end is null) || (start is not null && Py.Compare(start, end, ">=") >= 0))
                {
                    throw new StateError("covered interval must contain an ordered start and end");
                }

                if (Py.Truthy(attempt["requested_start"])
                    && (!Py.Eq(start, attempt["requested_start"]) || !Py.Eq(end, attempt["requested_end"])))
                {
                    throw new StateError("complete must cover the exact requested window; use partial otherwise");
                }

                var watermark = StrField("watermark");
                if (!string.IsNullOrEmpty(watermark) && Py.Compare(watermark, checkpoint, ">") > 0)
                {
                    throw new StateError("watermark cannot exceed checkpoint");
                }

                if (Py.Truthy(source["watermark"]) && !string.IsNullOrEmpty(watermark) && Py.Compare(watermark, source["watermark"], "<") < 0)
                {
                    throw new StateError("watermark cannot regress");
                }
            }

            var isApplied = Py.Eq(source["latest_attempt_id"], attemptId);
            var errorClass = status == "complete" ? null
                : StrField("error_class") is { Length: > 0 } recorded ? recorded
                : status == "blocked" ? "access_denied" : "unknown";
            conn.Execute("UPDATE proactive_attempts SET status=?,finished_at=?,error_class=?,result=? WHERE id=?",
                         status, now, errorClass, resultJson, attemptId);
            if (isApplied && status == "complete")
            {
                var hasToken = input.ContainsKey("delta_token");
                conn.Execute("UPDATE proactive_sources SET status='complete',\n                last_successful_coverage_at=?,checkpoint_at=?,covered_start=?,covered_end=?,\n                watermark=COALESCE(?,watermark),delta_token=CASE WHEN ? THEN ? ELSE delta_token END,\n                consecutive_failures=0,error_class=NULL,retry_after_seconds=NULL,next_retry_at=NULL,\n                recovered_at=CASE WHEN consecutive_failures>0 THEN ? ELSE recovered_at END,\n                failure_started_at=NULL\n                WHERE source_key=?",
                             now, checkpoint, start, end, StrField("watermark"), hasToken ? 1L : 0L, token, now, source["source_key"]);
                if (hasToken)
                {
                    var contractKey = "token-contract:" + Py.Str(source["source_key"]);
                    if (token is null)
                    {
                        conn.Execute("DELETE FROM proactive_meta WHERE key=?", contractKey);
                    }
                    else
                    {
                        conn.Execute("INSERT OR REPLACE INTO proactive_meta VALUES (?,?)", contractKey,
                                     StateStore.CanonicalJson(new JsonArray(Py.Node(attempt["capability"]), Py.Node(attempt["query_version"]),
                                                                            Py.Node(attempt["kind"]))));
                    }
                }

                conn.Execute("DELETE FROM proactive_meta WHERE key=?", "resync:" + attemptId);
                if (Py.Truthy(source["consecutive_failures"]))
                {
                    conn.Execute("INSERT OR IGNORE INTO proactive_health_events VALUES (?,?, 'recovered',?,NULL)",
                                 source["source_key"], source["episode"], now);
                }
            }
            else if (isApplied)
            {
                var previousFailures = Py.Int(source["consecutive_failures"]);
                var failures = previousFailures + 1;
                var episode = Py.Int(source["episode"]) + (previousFailures == 0 ? 1 : 0);
                var failureStarted = Py.Truthy(source["failure_started_at"]) ? source["failure_started_at"] : now;
                var nextRetry = StrField("next_retry_at");
                if (retry is not null && nextRetry is null)
                {
                    nextRetry = (PyDateTime.FromIsoformat(now) + PyTimeDelta.FromSeconds(retry.Value)).Isoformat("microseconds");
                }

                conn.Execute("UPDATE proactive_sources SET status=?,consecutive_failures=?,episode=?,\n                error_class=?,retry_after_seconds=?,next_retry_at=?,failure_started_at=?,\n                delta_token=CASE WHEN ?='expired_token' THEN NULL ELSE delta_token END WHERE source_key=?",
                             status, failures, episode, errorClass, retry, nextRetry, failureStarted, errorClass, source["source_key"]);
                var elapsed = (PyDateTime.FromIsoformat(now) - PyDateTime.FromIsoformat(Py.Str(failureStarted))).TotalSeconds();
                var cadence = source["cadence_seconds"];
                var escalated = cadence is not null && Py.Compare(elapsed, cadence, ">=") >= 0;
                if (failures == 1 || escalated)
                {
                    conn.Execute("INSERT OR IGNORE INTO proactive_health_events VALUES (?,?,?,?,?)",
                                 source["source_key"], episode, failures == 1 ? "degraded" : "escalated", now, errorClass);
                }
            }

            return isApplied;
        });
        if (applied is null)
        {
            return new JsonObject { ["attempt_id"] = attemptId, ["status"] = status, ["replayed"] = true };
        }

        return new JsonObject { ["attempt_id"] = attemptId, ["status"] = status, ["source_updated"] = applied.Value };
    }

    /// <summary>
    /// <c>coverage_status(conn)</c>: every source with its checkpoint, failure episode, freshness and
    /// escalation. Delta tokens are redacted (<c>has_delta_token</c> only).
    /// </summary>
    public static JsonArray CoverageStatus(PyConnection conn)
    {
        ArgumentNullException.ThrowIfNull(conn);
        var result = new JsonArray();
        foreach (var row in conn.Execute("SELECT * FROM proactive_sources ORDER BY family,source_key"))
        {
            var item = Py.Dict(row);
            var scope = Py.Loads(row["scope"]);
            var window = Py.Loads(row["window"]);
            item["scope"] = scope;
            item["window"] = window;
            item["collection_window"] = Clone(window);
            item["retired"] = conn.Execute("SELECT 1 FROM proactive_meta WHERE key=?", "retired:" + Py.Str(row["source_key"])).FetchOne() is not null;
            if (!item.ContainsKey("delta_token"))
            {
                throw new PyKeyError("delta_token");
            }

            var hasToken = !Py.IsNone(item["delta_token"]);
            item.Remove("delta_token");
            item["has_delta_token"] = hasToken;
            var attempt = Attempt(conn, row["latest_attempt_id"]);
            item["requested_window"] = new JsonObject { ["start"] = Py.Node(attempt["requested_start"]), ["end"] = Py.Node(attempt["requested_end"]) };
            item["pagination"] = new JsonObject
            {
                ["pages"] = Py.Node(attempt["pages"]),
                ["complete"] = Py.Truthy(attempt["pagination_complete"]),
                ["observations"] = Py.Node(attempt["observations"]),
                ["has_continuation"] = attempt["continuation"] is not null,
                ["kind"] = Py.Node(attempt["kind"]),
            };
            var errorClass = row["error_class"];
            var retired = (bool)item["retired"]!;
            item["retry_requires_reauthentication"] = Py.Eq(errorClass, "authentication_required");
            item["retry_eligible"] = !retired && !(Py.Eq(errorClass, "access_denied") || Py.Eq(errorClass, "authentication_required"))
                                     && (row["next_retry_at"] is null || Py.Compare(row["next_retry_at"], StateStore.UtcNow(), "<=") <= 0);
            var successfulAt = row["last_successful_coverage_at"];
            var cadence = row["cadence_seconds"];
            var failureStarted = row["failure_started_at"];
            long? missed = null;
            if (cadence is not null)
            {
                if (!Py.Truthy(failureStarted))
                {
                    missed = 0;
                }
                else
                {
                    var elapsed = (PyDateTime.Now() - PyDateTime.FromIsoformat(Py.Str(failureStarted))).TotalSeconds();
                    var periods = Py.FloorDivide(elapsed, CadenceDouble(cadence));
                    missed = 1 + (long)Math.Max(0, Math.Truncate(periods));
                }
            }

            item["missed_cadences"] = missed;
            item["escalated"] = missed is not null && missed >= 2;
            if (cadence is null)
            {
                item["fresh"] = null;
            }
            else
            {
                item["fresh"] = Py.Truthy(successfulAt)
                                && PyDateTime.Now() <= PyDateTime.FromIsoformat(Py.Str(successfulAt)) + PyTimeDelta.FromSeconds(Py.Int(cadence));
            }

            result.Add(item);
        }

        return result;
    }

    private static double CadenceDouble(object cadence) => cadence switch
    {
        long integer => integer,
        double number => number,
        _ => throw new PyTypeError("unsupported operand type(s) for //: 'float' and '" + Py.SqlTypeName(cadence) + "'"),
    };

    /// <summary><c>publication_show(conn, publication_id)</c>: the durable output, its receipt and included items.</summary>
    public static JsonObject PublicationShow(PyConnection conn, string publicationId)
    {
        ArgumentNullException.ThrowIfNull(conn);
        var row = conn.Execute("SELECT * FROM proactive_publications WHERE id=?", publicationId).FetchOne()
                  ?? throw new StateError("unknown publication");
        var result = Py.Dict(row);
        result["standalone"] = Standalone(conn, publicationId);
        result["receipt"] = Py.Truthy(row["receipt"]) ? Py.Loads(row["receipt"]) : null;
        var items = new JsonArray();
        foreach (var item in conn.Execute("SELECT i.stable_id AS id,i.revision,i.item_key\n        FROM proactive_items i JOIN proactive_publication_items pi ON i.item_key=pi.item_key\n        WHERE pi.publication_id=?", publicationId))
        {
            items.Add(Py.Dict(item));
        }

        result["items"] = items;
        return result;
    }

    /// <summary><c>publication_list(conn, limit)</c>: recent outputs with included and unacknowledged item counts.</summary>
    public static JsonArray PublicationList(PyConnection conn, BigInteger? limit = null)
    {
        ArgumentNullException.ThrowIfNull(conn);
        var count = Integer(limit ?? 20, "limit", 1, 1000);
        var result = new JsonArray();
        foreach (var row in conn.Execute("SELECT p.id AS publication_id,p.batch_id,p.status,\n        p.created_at,p.available_at,p.reviewed_at,count(pi.item_key) AS included_items,\n        sum(CASE WHEN i.status!='published' THEN 1 ELSE 0 END) AS ack_pending\n        FROM proactive_publications p LEFT JOIN proactive_publication_items pi ON pi.publication_id=p.id\n        LEFT JOIN proactive_items i ON i.item_key=pi.item_key GROUP BY p.id\n        ORDER BY p.created_at DESC,p.id LIMIT ?", count))
        {
            result.Add(Py.Dict(row));
        }

        return result;
    }

    /// <summary><c>status_report(conn)</c>: queue, output and coverage health; <c>all_clear</c> only for fresh complete coverage and nothing unpublished.</summary>
    public static JsonObject StatusReport(PyConnection conn)
    {
        ArgumentNullException.ThrowIfNull(conn);
        var counts = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var row in conn.Execute("SELECT status,count(*) FROM proactive_items GROUP BY status"))
        {
            counts[Py.Str(row[0])] = row[1];
        }

        var oldestItem = conn.Execute("SELECT min(created_at) FROM proactive_items WHERE status!='published'").FetchOne()![0];
        var publications = new JsonObject();
        foreach (var row in conn.Execute("SELECT status,count(*) FROM proactive_publications GROUP BY status"))
        {
            publications[Py.Str(row[0])] = Py.Node(row[1]);
        }

        var oldestOutput = conn.Execute("SELECT min(created_at) FROM proactive_publications WHERE status='prepared'").FetchOne()![0];
        object? oldest = null;
        if (Py.Truthy(oldestItem) || Py.Truthy(oldestOutput))
        {
            foreach (var value in new[] { oldestItem, oldestOutput })
            {
                if (Py.Truthy(value) && (oldest is null || Py.Compare(value, oldest, "<") < 0))
                {
                    oldest = value;
                }
            }
        }

        var sources = CoverageStatus(conn);
        var active = sources.Select(source => (JsonObject)source!).Where(source => !(bool)source["retired"]!).ToList();
        var allClear = active.Count > 0
                       && active.All(source => Py.StrEquals(source["status"], "complete") && PyJson.Truthy(source["fresh"]))
                       && !Py.Truthy(oldest);
        JsonNode? unpublished = publications.TryGetPropertyValue("prepared", out var prepared) ? Clone(prepared) : JsonValue.Create(0L);
        var healthEvents = new JsonArray();
        foreach (var row in conn.Execute("SELECT * FROM proactive_health_events ORDER BY created_at DESC LIMIT 50"))
        {
            healthEvents.Add(Py.Dict(row));
        }

        return new JsonObject
        {
            ["status"] = "ok",
            ["account_configured"] = true,
            ["queued"] = Py.Node(counts.GetValueOrDefault("pending", 0L)),
            ["in_flight"] = Py.Node(counts.GetValueOrDefault("leased", 0L)),
            ["published_items"] = Py.Node(counts.GetValueOrDefault("published", 0L)),
            ["oldest_unpublished_at"] = Py.Node(oldest),
            ["publications"] = publications,
            ["recent_outputs"] = PublicationList(conn),
            ["coverage"] = sources,
            ["unpublished_outputs"] = unpublished,
            ["all_clear"] = allClear,
            ["expired_leases"] = Py.Node(conn.Execute("SELECT count(*) FROM proactive_batches WHERE status='leased' AND expires_at<=?",
                                                      StateStore.UtcNow()).FetchOne()![0]),
            ["legacy_cursor_hints"] = Py.Node(conn.Execute("SELECT count(*) FROM proactive_legacy_cursors").FetchOne()![0]),
            ["legacy_observations"] = Py.Node(conn.Execute("SELECT count(*) FROM proactive_legacy_surfaced").FetchOne()![0]),
            ["health_events"] = healthEvents,
        };
    }

    // ================================================================================ legacy import

    /// <summary>
    /// <c>import_legacy(conn, directory)</c>: atomic, replay-safe import of the old JSON queue files
    /// (surfaced, queue, inflight, cursors). Never edits them and never confirms coverage or review.
    /// </summary>
    public static JsonObject ImportLegacy(PyConnection conn, string directory)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(directory);
        if (!PyPath.IsDir(PyPath.Normalize(directory)))
        {
            throw new StateError("legacy directory does not exist");
        }

        var data = new JsonObject();
        foreach (var (name, isDict) in LegacyFiles)
        {
            var path = PyPath.Join(directory, name);
            if (PyPath.IsSymlink(path))
            {
                throw new StateError("legacy import refuses symlinked files");
            }

            var value = PyPath.Exists(path) ? StateStore.ReadJson(path) : isDict ? new JsonObject() : new JsonArray();
            data[name] = value;
            if (isDict ? value is not JsonObject : value is not JsonArray)
            {
                throw new StateError(name + " has an invalid top-level shape");
            }
        }

        var surfaced = (JsonObject)data["surfaced.json"]!;
        var cursors = (JsonObject)data["cursors.json"]!;
        var items = ((JsonArray)data["queue.json"]!).Concat((JsonArray)data["inflight.json"]!).ToList();
        foreach (var (stableId, meta) in surfaced)
        {
            Text(stableId, "legacy surfaced id");
            var metadata = ObjectJson(meta);
            if (metadata.ContainsKey("ts"))
            {
                Py.Timestamp(metadata["ts"]);
            }
        }

        foreach (var (name, timestamp) in cursors)
        {
            Text(name, "legacy cursor name");
            Py.Timestamp(timestamp);
        }

        foreach (var item in items)
        {
            Item(item);
            if (((JsonObject)item!).ContainsKey("drained_at"))
            {
                Py.Timestamp(item["drained_at"]);
            }
        }

        var fingerprint = Digest(data);
        return StateStore.Transaction(conn, () =>
        {
            var prior = conn.Execute("SELECT counts FROM proactive_imports WHERE digest=?", fingerprint).FetchOne();
            if (prior is not null)
            {
                return new JsonObject { ["replayed"] = true, ["counts"] = Py.Loads(prior[0]) };
            }

            long queued = 0;
            foreach (var item in items)
            {
                queued += InsertItem(conn, item)["added"]!.GetValue<bool>() ? 1 : 0;
            }

            foreach (var (stableId, meta) in surfaced)
            {
                conn.Execute("INSERT OR IGNORE INTO proactive_legacy_surfaced VALUES (?,?)", stableId, StateStore.CanonicalJson(meta));
            }

            foreach (var (name, timestamp) in cursors)
            {
                conn.Execute("INSERT OR IGNORE INTO proactive_legacy_cursors VALUES (?,?)", name, Py.Timestamp(timestamp));
            }

            var counts = new JsonObject
            {
                ["queued"] = queued, ["legacy_observations"] = (long)surfaced.Count, ["legacy_cursor_hints"] = (long)cursors.Count,
            };
            conn.Execute("INSERT INTO proactive_imports VALUES (?,?,?)", fingerprint, StateStore.UtcNow(), StateStore.CanonicalJson(counts));
            return new JsonObject { ["replayed"] = false, ["counts"] = counts, ["confirmed_coverage_imported"] = false };
        });
    }
}

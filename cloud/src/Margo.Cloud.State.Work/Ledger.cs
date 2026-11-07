using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using static Margo.Cloud.State.Work.WorkLedger;

namespace Margo.Cloud.State.Work;

/// <summary>
/// Port of <c>work_ledger.Ledger</c>: sources, candidate and confirmed work items, relations, action proposals and
/// their immutable revisions, exact human approvals, foreground execution attempts (claims) and their outcomes,
/// history, review lists and the legacy commitments import/export. Values are Python values as JSON nodes
/// (<see cref="PyJson"/>'s value model); method names follow the Python ones.
/// </summary>
public sealed class Ledger : IDisposable
{
    private static readonly IReadOnlySet<string> ItemFields = Set(
        "title", "owner", "direction", "due", "source_refs", "confidence", "outcome_id", "next_step", "blocker",
        "deferred_until", "notes", "confirmation_source");

    private static readonly IReadOnlySet<string> Directions = Set("owe", "waiting_on", "own");
    private static readonly IReadOnlySet<string> RefKeys = Set("source_id", "revision", "fingerprint");
    private static readonly IReadOnlySet<string> RelationKinds = Set("blocks", "depends_on", "supersedes", "tracked_in");
    private static readonly IReadOnlySet<string> ActionRequired = Set("kind", "target", "payload", "why", "source_refs", "target_fingerprint", "work_item_id");
    private static readonly IReadOnlySet<string> ActionOptional = Set("affected_people", "artifact_id", "artifact_revision", "dependencies");
    private static readonly IReadOnlySet<string> Outcomes = Set("succeeded", "failed", "partial", "outcome_unknown");
    private static readonly IReadOnlySet<string> ReceiptKinds = Set("tool_result", "provider_receipt", "reconciliation_read");
    private static readonly IReadOnlySet<string> FreshKeys = Set("checked_at", "target_fingerprint", "source_refs");
    private static readonly IReadOnlySet<string> Views = Set("all", "decisions", "approval", "waiting", "problems");
    private static readonly IReadOnlySet<string> ImportConfirmed = Set("confirmed", "active", "waiting", "resolved", "cancelled");
    private static readonly IReadOnlySet<string> ClosedStates = Set("resolved", "rejected", "cancelled", "achieved", "carried_forward", "revoked", "superseded");

    private static readonly Dictionary<string, IReadOnlySet<string>> ViewStates = new(StringComparer.Ordinal)
    {
        ["decisions"] = Set("candidate", "ready", "stale", "proposed", "debrief_proposed"),
        ["approval"] = Set("ready", "approved", "prepared", "proposed"),
        ["waiting"] = Set("waiting", "deferred", "recap_pending", "blocked"),
        ["problems"] = Set("failed", "partial", "outcome_unknown", "executing", "blocked", "stale"),
    };

    private static readonly Dictionary<string, string> ItemDecisions = new(StringComparer.Ordinal)
    {
        ["confirmed"] = "confirm",
        ["active"] = "activate",
        ["waiting"] = "wait",
        ["resolved"] = "resolve",
        ["deferred"] = "defer",
        ["cancelled"] = "cancel",
    };

    private static readonly string[] SchemaPragmas = ["table_info", "foreign_key_list"];

    private readonly bool _ownsConnection;

    /// <summary><c>Ledger(account, state_root)</c>: opens (and creates or adopts) the account's work schema.</summary>
    public Ledger(string? account = null, string? stateRoot = null)
    {
        Account = StateStore.ResolveAccount(account);
        Conn = StateStore.Connect(Account, stateRoot);
        _ownsConnection = true;
        try
        {
            Conn.Execute("PRAGMA foreign_keys=ON");
            InitializeSchema();
        }
        catch
        {
            Conn.Close();
            throw;
        }
    }

    private Ledger(string account, PyConnection connection)
    {
        Account = account;
        Conn = connection;
        _ownsConnection = false;
    }

    /// <summary>The ledger owner's principal.</summary>
    public string Account { get; }

    /// <summary>The account connection (<c>self.conn</c>).</summary>
    public PyConnection Conn { get; }

    /// <summary>
    /// <c>Ledger.from_connection(connection, account)</c>: borrows an initialized account connection for atomic
    /// cross-namespace operations; never creates or repairs the work schema.
    /// </summary>
    public static Ledger FromConnection(PyConnection connection, string? account)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var principal = StateStore.ResolveAccount(account);
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var row in connection.Execute("SELECT key,value FROM margo_meta"))
        {
            if (row[0] is string key)
            {
                metadata[key] = row[1];
            }
        }

        if (!Equals(metadata.GetValueOrDefault("account"), principal) || !Equals(metadata.GetValueOrDefault("store_version"), "1"))
        {
            throw new StateError("borrowed ledger connection account/schema mismatch");
        }

        if (!Equals(metadata.GetValueOrDefault("work_schema_version"), "1"))
        {
            throw new NotInitialized("Work ledger is not initialized; initialize task/work state explicitly.");
        }

        if (!connection.RowFactoryIsRow)
        {
            throw new StateError("borrowed ledger connection requires sqlite3.Row results");
        }

        var result = new Ledger(principal, connection);
        result.InitializeSchema(readOnly: true);
        return result;
    }

    private void InitializeSchema(bool readOnly = false)
    {
        var statements = Schema.Split(';').Select(part => PyRepr.Strip(part)).Where(part => part.Length > 0).ToList();
        var expected = statements.Where(statement => statement.StartsWith("CREATE TABLE", StringComparison.Ordinal))
                                 .Select(statement => statement.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[5])
                                 .ToHashSet(StringComparer.Ordinal);
        StateStore.Transaction(Conn, () =>
        {
            var allTables = Conn.Execute("SELECT name FROM sqlite_master WHERE type='table'")
                                .Select(row => (string)row[0]!).ToHashSet(StringComparer.Ordinal);
            var existing = allTables.Where(name => name.StartsWith("work_", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
            PyRow? globalMarker = null;
            if (allTables.Contains("margo_meta"))
            {
                globalMarker = Conn.Execute("SELECT value FROM margo_meta WHERE key='work_schema_version'").FetchOne();
            }

            var legacy = existing.SetEquals(expected.Where(name => name != "work_meta")) && globalMarker is null;
            if (existing.Count > 0 && !existing.SetEquals(expected) && !legacy)
            {
                throw new StateError("incomplete work schema; refusing to recreate missing history");
            }

            if (globalMarker is not null && (!Equals(globalMarker[0], "1") || !existing.Contains("work_meta")))
            {
                throw new StateError("work schema marker mismatch; explicit recovery required");
            }

            if (existing.Contains("work_meta"))
            {
                var marker = Conn.Execute("SELECT value FROM work_meta WHERE key='schema_version'").FetchOne();
                if (marker is null || !Equals(marker[0], "1"))
                {
                    throw new StateError("unsupported or missing work schema version");
                }
            }

            if (existing.Count > 0)
            {
                using var reference = PyConnection.Connect(":memory:");
                reference.ExecuteScript(Schema);
                // Python iterates a set (hash order); a mismatch in several tables reports one of them.
                foreach (var table in existing.Order(StringComparer.Ordinal))
                {
                    foreach (var pragma in SchemaPragmas)
                    {
                        var actual = Conn.Execute("PRAGMA " + pragma + "(" + table + ")").FetchAll();
                        var wanted = reference.Execute("PRAGMA " + pragma + "(" + table + ")").FetchAll();
                        if (actual.Count != wanted.Count || actual.Zip(wanted).Any(pair => !pair.First.ValuesEqual(pair.Second)))
                        {
                            throw new StateError("work schema contract mismatch: " + table);
                        }
                    }
                }
            }

            if (readOnly)
            {
                return;
            }

            foreach (var statement in statements)
            {
                Conn.Execute(statement);
            }

            Conn.Execute("INSERT OR IGNORE INTO work_meta VALUES ('schema_version','1')");
            if (allTables.Contains("margo_meta"))
            {
                Conn.Execute("INSERT OR IGNORE INTO margo_meta VALUES ('work_schema_version','1')");
            }
        }, readOnly);
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

    /// <summary><c>with ledger.transaction(): ...</c> (outermost BEGIN IMMEDIATE, nested savepoints).</summary>
    public T Transaction<T>(Func<T> body) => StateStore.Transaction(Conn, body);

    /// <summary><c>with ledger.transaction(): ...</c>.</summary>
    public void Transaction(Action body) => StateStore.Transaction(Conn, body);

    /// <summary><c>invalidate_approval</c>: revokes only an exact unused approval; never alters a newer or executed action.</summary>
    public JsonObject InvalidateApproval(JsonNode? actionId, JsonNode? revision, JsonNode? actionHash, JsonNode? reason)
    {
        Text(reason, "invalidation reason");
        return Transaction(() =>
        {
            var action = Show(actionId);
            if (!PyDynamic.Is(PyDynamic.Get(action, "type"), "action") || !PyDynamic.Eq(PyDynamic.Index(action, "revision"), revision)
                || !PyDynamic.Eq(PyDynamic.Index(action, "action_hash"), actionHash))
            {
                throw new StateError("approval invalidation requires the exact current action");
            }

            if (PyDynamic.In(action["state"], ExecutionStates))
            {
                return new JsonObject { ["invalidated"] = false, ["reason"] = "execution_already_started" };
            }

            Invalidate(actionId, "task_cancelled", new JsonObject { ["reason"] = PyDynamic.Clone(reason) });
            return new JsonObject { ["invalidated"] = true, ["action_id"] = PyDynamic.Clone(actionId), ["revision"] = PyDynamic.Clone(revision) };
        });
    }

    /// <summary><c>event(entity_id, event, data)</c>: appends to the account's event journal.</summary>
    public void Event(JsonNode? entityId, string eventName, JsonNode? data) =>
        Conn.Execute("INSERT INTO work_events(account,entity_id,event,data,created_at) VALUES(?,?,?,?,?)",
                     Account, entityId, eventName, StateStore.CanonicalJson(data), StateStore.UtcNow());

    /// <summary><c>row(table, entity_id)</c>: the account's raw row, or <c>StateError("unknown entity: ...")</c>.</summary>
    public JsonObject Row(string table, JsonNode? entityId)
    {
        if (table is not ("work_items" or "work_actions" or "work_records" or "work_sources"))
        {
            throw new StateError("unsupported entity table");
        }

        var found = Conn.Execute("SELECT * FROM " + table + " WHERE id=? AND account=?", entityId, Account).FetchOne();
        if (found is null)
        {
            throw new StateError("unknown entity: " + PyDynamic.ToStr(entityId));
        }

        return RowDict(found);
    }

    /// <summary>
    /// <c>source(family, scope, external_id, revision, evidence, web_link, sensitivity="unknown", observed_at=None)</c>:
    /// records one stable source revision; a new revision makes linked unexecuted actions stale.
    /// A null <paramref name="sensitivity"/> means Python's default (<c>"unknown"</c>).
    /// </summary>
    public JsonObject Source(JsonNode? family, JsonNode? scope, JsonNode? externalId, JsonNode? revision, JsonNode? evidence,
                             JsonNode? webLink, JsonNode? sensitivity = null, JsonNode? observedAt = null) =>
        SourceCore(family, scope, externalId, revision, evidence, webLink, sensitivity ?? PyDynamic.Str("unknown"), observedAt);

    /// <summary><c>source(...)</c> with every argument as given (a null sensitivity is Python's <c>None</c>).</summary>
    internal JsonObject SourceCore(JsonNode? family, JsonNode? scope, JsonNode? externalId, JsonNode? revision, JsonNode? evidence,
                                   JsonNode? webLink, JsonNode? sensitivity, JsonNode? observedAt)
    {
        foreach (var (key, value) in new[] { ("family", family), ("scope", scope), ("external_id", externalId), ("revision", revision), ("web_link", webLink) })
        {
            Text(value, key);
        }

        if (!(evidence is JsonObject || PyDynamic.AsString(evidence) is not null) || !PyDynamic.Truthy(evidence))
        {
            throw new StateError("source evidence must be nonempty");
        }

        observedAt = PyDynamic.Truthy(observedAt) ? observedAt : PyDynamic.Str(StateStore.UtcNow());
        Timestamp(observedAt);
        var data = new JsonObject
        {
            ["evidence"] = PyDynamic.Clone(evidence),
            ["web_link"] = PyDynamic.Clone(webLink),
            ["sensitivity"] = PyDynamic.Clone(sensitivity),
        };
        var fingerprint = Digest(data);
        var sourceId = "src_" + Digest(new JsonArray(Account, PyDynamic.Clone(family), PyDynamic.Clone(scope), PyDynamic.Clone(externalId)))[..32];
        JsonObject Result() => new() { ["source_id"] = sourceId, ["revision"] = PyDynamic.Clone(revision), ["fingerprint"] = fingerprint };
        return Transaction(() =>
        {
            var old = Conn.Execute("SELECT * FROM work_source_revisions WHERE source_id=? AND revision=?", sourceId, revision).FetchOne();
            if (old is not null)
            {
                if (!Equals(old["fingerprint"], fingerprint))
                {
                    throw new StateError("same source revision has different evidence; use a new revision");
                }

                return Result();
            }

            Conn.Execute("INSERT OR IGNORE INTO work_sources VALUES(?,?,?,?,?,?,?)",
                         sourceId, Account, family, scope, externalId, revision, StateStore.UtcNow());
            Conn.Execute("INSERT INTO work_source_revisions VALUES(?,?,?,?,?)",
                         sourceId, revision, fingerprint, StateStore.CanonicalJson(data), observedAt);
            Conn.Execute("UPDATE work_sources SET current_revision=? WHERE id=?", revision, sourceId);
            var linked = Conn.Execute("""

                SELECT DISTINCT a.id FROM work_actions a JOIN work_action_sources s
                ON a.id=s.action_id AND a.revision=s.action_revision
                WHERE s.source_id=? AND s.source_revision<>?
                AND a.state IN ('ready','approved','deferred','stale')

""", sourceId, revision).FetchAll();
            foreach (var action in linked)
            {
                Invalidate(FromSql(action["id"]), "source_changed", new JsonObject { ["source_id"] = sourceId, ["revision"] = PyDynamic.Clone(revision) });
            }

            // Staleness is an observation, not an approved content edit.
            var records = Conn.Execute("""

                SELECT DISTINCT r.id FROM work_records r JOIN work_record_sources s
                ON r.id=s.record_id AND r.revision=s.record_revision
                WHERE s.source_id=? AND s.source_revision<>?

""", sourceId, revision).FetchAll();
            foreach (var record in records)
            {
                Event(FromSql(record["id"]), "source_changed", new JsonObject { ["source_id"] = sourceId, ["revision"] = PyDynamic.Clone(revision) });
            }

            Event(PyDynamic.Str(sourceId), "observed", new JsonObject { ["revision"] = PyDynamic.Clone(revision), ["fingerprint"] = fingerprint });
            return Result();
        });
    }

    /// <summary>
    /// <c>find_source(family, scope, external_id)</c>: <c>{"source_id", "revision"}</c> for an exact external identity
    /// in this account, or null. Read-only.
    /// </summary>
    public JsonObject? FindSource(JsonNode? family, JsonNode? scope, JsonNode? externalId)
    {
        foreach (var (key, value) in new[] { ("family", family), ("scope", scope), ("external_id", externalId) })
        {
            Text(value, key);
        }

        var row = Conn.Execute("SELECT id,current_revision FROM work_sources WHERE account=? AND family=? AND scope=? AND external_id=?",
                               Account, family, scope, externalId).FetchOne();
        return row is null ? null : new JsonObject { ["source_id"] = FromSql(row["id"]), ["revision"] = FromSql(row["current_revision"]) };
    }

    /// <summary><c>source_refs(refs, require_current=True)</c>: validated exact source references, sorted by source_id.</summary>
    public JsonArray SourceRefs(JsonNode? refs, bool requireCurrent = true)
    {
        if (refs is not JsonArray list)
        {
            throw new StateError("source_refs must be a list");
        }

        var result = new List<JsonObject>();
        var seen = new List<JsonNode?>();
        foreach (var item in list)
        {
            if (item is not JsonObject reference || !reference.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal).SetEquals(RefKeys))
            {
                throw new StateError("each source ref requires source_id, revision, fingerprint");
            }

            var sourceId = reference["source_id"];
            PyDynamic.RequireHashable(sourceId);
            if (seen.Any(value => PyDynamic.Eq(value, sourceId)))
            {
                throw new StateError("duplicate source reference");
            }

            seen.Add(sourceId);
            var source = Row("work_sources", sourceId);
            var known = Conn.Execute("SELECT fingerprint FROM work_source_revisions WHERE source_id=? AND revision=?",
                                     sourceId, reference["revision"]).FetchOne();
            if (known is null || !PyDynamic.Eq(FromSql(known["fingerprint"]), reference["fingerprint"]))
            {
                throw new StateError("unknown or mismatched source fingerprint");
            }

            if (requireCurrent && !PyDynamic.Eq(source["current_revision"], reference["revision"]))
            {
                throw new StateError("stale source revision");
            }

            result.Add(PyDynamic.CloneObject(reference));
        }

        var sorted = new JsonArray();
        foreach (var reference in result.OrderBy(reference => PyDynamic.AsString(reference["source_id"])!, PyRepr.StringComparer))
        {
            sorted.Add(reference);
        }

        return sorted;
    }

    /// <summary><c>refs_stale(refs)</c>: whether any referenced source has a newer current revision.</summary>
    public bool RefsStale(JsonNode? refs)
    {
        foreach (var reference in PyDynamic.Iter(refs))
        {
            if (!PyDynamic.Eq(Row("work_sources", PyDynamic.Index(reference, "source_id"))["current_revision"], PyDynamic.Index(reference, "revision")))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary><c>source_evidence(refs)</c>: each reference with its stored family, scope, identity and evidence.</summary>
    public JsonArray SourceEvidence(JsonNode? refs)
    {
        var result = new JsonArray();
        foreach (var reference in PyDynamic.Iter(refs))
        {
            var row = Conn.Execute("""

                SELECT s.family,s.scope,s.external_id,s.current_revision,r.data,r.observed_at
                FROM work_sources s JOIN work_source_revisions r ON s.id=r.source_id
                WHERE s.id=? AND r.revision=? AND s.account=?

""", PyDynamic.Index(reference, "source_id"), PyDynamic.Index(reference, "revision"), Account).FetchOne();
            if (row is null)
            {
                throw new StateError("source evidence is missing");
            }

            var metadata = RowDict(row);
            var data = metadata["data"];
            metadata.Remove("data");
            UpdateFrom(metadata, Loads(PyDynamic.AsString(data)));
            UpdateFrom(metadata, reference);
            result.Add(metadata);
        }

        return result;
    }

    private static void UpdateFrom(JsonObject target, JsonNode? source)
    {
        foreach (var (key, value) in (JsonObject)source!)
        {
            target[key] = PyDynamic.Clone(value);
        }
    }

    /// <summary><c>_item_data(data, require_current)</c>.</summary>
    private JsonObject ItemData(JsonNode? data, bool requireCurrent = true, IReadOnlyList<JsonNode?>? foreignKeys = null)
    {
        if (data is not JsonObject obj)
        {
            throw new StateError("item data must be an object");
        }

        var unknown = obj.Select(pair => pair.Key).Where(key => !ItemFields.Contains(key)).ToList();
        if (foreignKeys is { Count: > 0 })
        {
            throw PyDynamic.JoinSortedError(unknown, foreignKeys);
        }

        if (unknown.Count > 0)
        {
            throw new StateError("unknown item fields: " + PyDynamic.JoinSorted(unknown));
        }

        Text(PyDynamic.Get(obj, "title"), "title");
        if (!PyDynamic.In(PyDynamic.Get(obj, "direction"), Directions))
        {
            throw new StateError("direction must be owe, waiting_on, or own");
        }

        foreach (var key in new[] { "owner", "due" })
        {
            if (!obj.ContainsKey(key))
            {
                throw new StateError(key + " is required; use null for unknown");
            }

            if (obj[key] is not null)
            {
                Text(obj[key], key);
            }
        }

        var result = PyDynamic.CloneObject(obj);
        result["source_refs"] = SourceRefs(PyDynamic.Get(obj, "source_refs", new JsonArray()), requireCurrent);
        if (((JsonArray)result["source_refs"]!).Count == 0 && !PyDynamic.Truthy(PyDynamic.Get(result, "confirmation_source")))
        {
            throw new StateError("item requires source evidence or attributed direct human instruction");
        }

        if (PyDynamic.Truthy(PyDynamic.Get(result, "deferred_until")))
        {
            Timestamp(result["deferred_until"]);
        }

        if (PyDynamic.Truthy(PyDynamic.Get(result, "outcome_id")))
        {
            if (!PyDynamic.Is(Row("work_records", result["outcome_id"])["kind"], "outcome"))
            {
                throw new StateError("outcome_id must refer to an outcome");
            }
        }

        return result;
    }

    /// <summary><c>ingest(data, claim_key)</c>: exact revision + extractor claim key dedupe; never semantic merging.</summary>
    public JsonObject Ingest(JsonNode? data, JsonNode? claimKey)
    {
        Text(claimKey, "claim_key");
        return Transaction(() =>
        {
            var item = ItemData(data, requireCurrent: false);
            var ingestKey = Digest(new JsonArray(Account, PyDynamic.Clone(item["source_refs"]),
                                                 PyDynamic.Clone(PyDynamic.Get(item, "confirmation_source")), PyDynamic.Clone(claimKey)));
            var contentHash = Digest(item);
            var existing = Conn.Execute("SELECT id,ingest_hash FROM work_items WHERE ingest_key=?", ingestKey).FetchOne();
            if (existing is not null)
            {
                if (!Equals(existing["ingest_hash"], contentHash))
                {
                    throw new StateError("same ingest identity has different content; explicitly edit the existing item");
                }

                return Show(FromSql(existing["id"]));
            }

            var itemId = "item_" + ingestKey[..32];
            var stamp = StateStore.UtcNow();
            Conn.Execute("INSERT INTO work_items VALUES(?,?,?,?,?,?,?,?,?,?,?)",
                         itemId, Account, 1L, "candidate", 0L, ingestKey, contentHash, StateStore.CanonicalJson(item), null, stamp, stamp);
            ItemSnapshot(PyDynamic.Str(itemId), null);
            Event(PyDynamic.Str(itemId), "ingested", new JsonObject { ["claim_key"] = PyDynamic.Clone(claimKey) });
            return Show(PyDynamic.Str(itemId));
        });
    }

    private void ItemSnapshot(JsonNode? itemId, JsonNode? evidence)
    {
        var row = Row("work_items", itemId);
        Conn.Execute("INSERT INTO work_item_revisions VALUES(?,?,?,?,?)",
                     itemId, row["revision"], StateStore.CanonicalJson(row),
                     PyDynamic.Truthy(evidence) ? StateStore.CanonicalJson(evidence) : null, StateStore.UtcNow());
    }

    /// <summary>
    /// <c>update_item(item_id, revision, state=None, patch=None, evidence=None)</c>: conditional local edit or
    /// transition; confirmed work needs exact human evidence.
    /// </summary>
    public JsonObject UpdateItem(JsonNode? itemId, JsonNode? revision, JsonNode? state = null, JsonNode? patch = null, JsonNode? evidence = null)
    {
        return Transaction(() =>
        {
            var row = Row("work_items", itemId);
            CheckRevision(row, revision);
            var current = PyDynamic.AsString(row["state"])!;
            state = PyDynamic.Truthy(state) ? state : PyDynamic.Clone(row["state"]!);
            if (!PyDynamic.In(state, ItemStates))
            {
                throw new StateError("invalid work state");
            }

            var next = PyDynamic.AsString(state)!;
            if (next != current && !(ItemTransitions.TryGetValue(current, out var allowed) ? allowed : throw new PyKeyError(current)).Contains(next))
            {
                throw new StateError("invalid work transition: " + current + " -> " + next);
            }

            var wasConfirmed = PyDynamic.Truthy(row["confirmed"]);
            if (wasConfirmed && next is "candidate" or "rejected")
            {
                throw new StateError("confirmed work cannot be demoted to an unconfirmed candidate");
            }

            if (!wasConfirmed && next is "active" or "waiting" or "resolved")
            {
                throw new StateError("unconfirmed work must pass through confirmed before becoming authoritative");
            }

            if (wasConfirmed || next is "confirmed" or "active" or "waiting" or "resolved")
            {
                var decision = next == current ? "edit" : ItemDecisions.TryGetValue(next, out var mapped) ? mapped : throw new PyKeyError(next);
                Human(evidence, itemId, revision, decision);
            }

            var data = (JsonObject)Loads(PyDynamic.AsString(row["data"]))!;
            var foreign = PyDynamic.Truthy(patch) ? PyDynamic.Update(data, patch) : [];
            data = ItemData(data, foreignKeys: foreign);
            if (next == "deferred" && !PyDynamic.Truthy(PyDynamic.Get(data, "deferred_until")))
            {
                throw new StateError("deferral requires deferred_until");
            }

            if (next != "deferred")
            {
                data.Remove("deferred_until");
            }

            var confirmed = wasConfirmed || next == "confirmed";
            var confirmation = PyDynamic.Truthy(row["confirmation"]) ? PyDynamic.AsString(row["confirmation"])
                : confirmed ? StateStore.CanonicalJson(evidence) : null;
            Conn.Execute("UPDATE work_items SET revision=revision+1,state=?,confirmed=?,data=?,confirmation=?,updated_at=? WHERE id=?",
                         next, confirmed ? 1L : 0L, StateStore.CanonicalJson(data), confirmation, StateStore.UtcNow(), itemId);
            ItemSnapshot(itemId, evidence);
            Event(itemId, "updated", new JsonObject
            {
                ["from_revision"] = PyDynamic.Clone(revision),
                ["state"] = next,
                ["evidence"] = PyDynamic.Clone(evidence),
            });
            return Show(itemId);
        });
    }

    /// <summary>
    /// <c>relate(source_id, kind, target_id, revision, evidence=None)</c>: links work items (cycle-checked for
    /// blocks/depends_on/supersedes) or a canonical tracker reference.
    /// </summary>
    public JsonObject Relate(JsonNode? sourceId, JsonNode? kind, JsonNode? targetId, JsonNode? revision, JsonNode? evidence = null)
    {
        if (!PyDynamic.In(kind, RelationKinds))
        {
            throw new StateError("unsupported relationship");
        }

        var relation = PyDynamic.AsString(kind)!;
        return Transaction(() =>
        {
            var source = Row("work_items", sourceId);
            CheckRevision(source, revision);
            var target = relation != "tracked_in" ? Row("work_items", targetId) : null;
            if (PyDynamic.Eq(sourceId, targetId))
            {
                throw new StateError("self relationship is invalid");
            }

            if (relation == "tracked_in")
            {
                var reference = Text(targetId, "canonical tracker reference");
                if (!reference.Contains(':', StringComparison.Ordinal))
                {
                    throw new StateError("tracked_in requires a namespaced canonical tracker reference");
                }
            }

            if (PyDynamic.Truthy(source["confirmed"]) || (target is not null && PyDynamic.Truthy(target["confirmed"])))
            {
                Human(evidence, sourceId, revision, "relate");
            }

            var edges = new List<(string From, string To)>();
            foreach (var row in Conn.Execute("SELECT source_id,kind,target_id FROM work_relationships"))
            {
                var rowKind = (string?)row["kind"];
                var from = (string)row["source_id"]!;
                var to = (string)row["target_id"]!;
                if (relation is "blocks" or "depends_on" && rowKind is "blocks" or "depends_on")
                {
                    edges.Add(rowKind == "blocks" ? (to, from) : (from, to));
                }
                else if (relation == "supersedes" && rowKind == relation)
                {
                    edges.Add((from, to));
                }
            }

            var (start, end) = relation == "blocks" ? (targetId, sourceId) : (sourceId, targetId);
            var pending = new List<JsonNode?> { end };
            var seen = new List<JsonNode?>();
            while (pending.Count > 0)
            {
                var node = pending[^1];
                pending.RemoveAt(pending.Count - 1);
                if (PyDynamic.Eq(node, start))
                {
                    throw new StateError("relationship would create a cycle");
                }

                if (!seen.Any(value => PyDynamic.Eq(value, node)))
                {
                    seen.Add(node);
                    var name = PyDynamic.AsString(node);
                    pending.AddRange(edges.Where(edge => edge.From == name).Select(edge => (JsonNode?)PyDynamic.Str(edge.To)));
                }
            }

            var changed = Conn.Execute("INSERT OR IGNORE INTO work_relationships VALUES(?,?,?,?,?)",
                                       sourceId, relation, targetId, PyDynamic.Truthy(evidence) ? StateStore.CanonicalJson(evidence) : null,
                                       StateStore.UtcNow()).RowCount;
            if (changed != 0)
            {
                Conn.Execute("UPDATE work_items SET revision=revision+1,updated_at=? WHERE id=?", StateStore.UtcNow(), sourceId);
                ItemSnapshot(sourceId, evidence);
                Event(sourceId, "relationship_added", new JsonObject
                {
                    ["kind"] = relation,
                    ["target_id"] = PyDynamic.Clone(targetId),
                    ["evidence"] = PyDynamic.Clone(evidence),
                });
            }

            return Show(sourceId);
        });
    }

    private JsonObject ActionData(JsonNode? data)
    {
        if (data is not JsonObject obj || !ActionRequired.All(obj.ContainsKey)
            || obj.Any(pair => !ActionRequired.Contains(pair.Key) && !ActionOptional.Contains(pair.Key)))
        {
            throw new StateError("action requires exact kind,target,payload,why,source_refs,target_fingerprint,work_item_id");
        }

        foreach (var key in new[] { "kind", "why", "target_fingerprint" })
        {
            Text(obj[key], key);
        }

        foreach (var key in new[] { "target", "payload" })
        {
            if (obj[key] is not JsonObject)
            {
                throw new StateError(key + " must be an object containing the exact reviewed fields");
            }
        }

        if (!PyDynamic.Truthy(obj["target"]))
        {
            throw new StateError("target must identify exact recipients or target IDs");
        }

        if (obj["work_item_id"] is not null)
        {
            Row("work_items", obj["work_item_id"]);
        }

        var result = PyDynamic.CloneObject(obj);
        result["source_refs"] = SourceRefs(obj["source_refs"]);
        foreach (var key in new[] { "affected_people", "dependencies" })
        {
            if (result.ContainsKey(key) && (result[key] is not JsonArray values
                                            || !values.All(value => PyDynamic.AsString(value) is { } text && PyRepr.Strip(text).Length > 0)))
            {
                throw new StateError(key + " must be a list of explicit people or work IDs");
            }
        }

        if (result.ContainsKey("artifact_id") != result.ContainsKey("artifact_revision"))
        {
            throw new StateError("artifact_id and artifact_revision must be supplied together");
        }

        foreach (var itemId in PyDynamic.Iter(PyDynamic.Get(result, "dependencies", new JsonArray())))
        {
            Row("work_items", itemId);
        }

        if (PyDynamic.Truthy(PyDynamic.Get(result, "artifact_id")))
        {
            var artifact = Show(result["artifact_id"]);
            if (!PyDynamic.Is(PyDynamic.Get(artifact, "kind"), "artifact")
                || !PyDynamic.Eq(PyDynamic.Index(artifact, "revision"), PyDynamic.Get(result, "artifact_revision")))
            {
                throw new StateError("artifact must name an exact current artifact revision");
            }

            if (PyDynamic.Truthy(PyDynamic.Index(artifact, "stale")))
            {
                throw new StateError("artifact is stale");
            }
        }

        return result;
    }

    private void PutActionRevision(JsonNode? actionId, JsonNode? revision, JsonObject data)
    {
        var actionHash = Digest(new JsonObject
        {
            ["account"] = Account,
            ["id"] = PyDynamic.Clone(actionId),
            ["revision"] = PyDynamic.Clone(revision),
            ["action"] = PyDynamic.Clone(data),
        });
        Conn.Execute("INSERT INTO work_action_revisions VALUES(?,?,?,?,?)",
                     actionId, revision, actionHash, StateStore.CanonicalJson(data), StateStore.UtcNow());
        foreach (var reference in PyDynamic.Iter(PyDynamic.Index(data, "source_refs")))
        {
            Conn.Execute("INSERT INTO work_action_sources VALUES(?,?,?,?,?)",
                         actionId, revision, PyDynamic.Index(reference, "source_id"), PyDynamic.Index(reference, "revision"),
                         PyDynamic.Index(reference, "fingerprint"));
        }
    }

    private void CheckActionHash(JsonNode? actionId, JsonNode? revision, JsonNode? expectedHash)
    {
        var stored = Conn.Execute("SELECT action_hash FROM work_action_revisions WHERE action_id=? AND revision=?", actionId, revision).FetchOne();
        if (expectedHash is not null && (stored is null || !PyDynamic.Eq(FromSql(stored["action_hash"]), expectedHash)))
        {
            throw new StateError("action hash conflict; reload the current revision");
        }
    }

    /// <summary><c>propose(data)</c>: prepares a local exact action payload (state ready, revision 1); never sends.</summary>
    public JsonObject Propose(JsonNode? data)
    {
        return Transaction(() =>
        {
            var action = ActionData(data);
            var actionId = PyDynamic.Str("act_" + PyIds.Uuid4Hex());
            var stamp = StateStore.UtcNow();
            Conn.Execute("INSERT INTO work_actions VALUES(?,?,?,?,?,?,?)", actionId, Account, 1L, "ready", null, stamp, stamp);
            PutActionRevision(actionId, PyDynamic.Int(1), action);
            Event(actionId, "proposed", new JsonObject { ["revision"] = 1 });
            return Show(actionId);
        });
    }

    /// <summary><c>_invalidate(action_id, reason, data=None, state="stale")</c>.</summary>
    internal void Invalidate(JsonNode? actionId, string reason, JsonNode? data = null, string state = "stale")
    {
        Conn.Execute("UPDATE work_approvals SET invalidated_at=? WHERE action_id=? AND invalidated_at IS NULL", StateStore.UtcNow(), actionId);
        Conn.Execute("UPDATE work_actions SET state=?,updated_at=? WHERE id=?", state, StateStore.UtcNow(), actionId);
        Event(actionId, reason, PyDynamic.Truthy(data) ? data : new JsonObject());
    }

    /// <summary><c>edit_action(action_id, revision, data, expected_hash=None)</c>: a new immutable revision; invalidates approvals.</summary>
    public JsonObject EditAction(JsonNode? actionId, JsonNode? revision, JsonNode? data, JsonNode? expectedHash = null)
    {
        return Transaction(() =>
        {
            var row = Row("work_actions", actionId);
            CheckRevision(row, revision);
            CheckActionHash(actionId, revision, expectedHash);
            if (PyDynamic.In(row["state"], ExecutionStates))
            {
                throw new StateError("cannot edit an executed or unresolved action; reconcile first");
            }

            var action = ActionData(data);
            Invalidate(actionId, "edited", new JsonObject { ["from_revision"] = PyDynamic.Clone(revision) }, state: "ready");
            Conn.Execute("UPDATE work_actions SET revision=revision+1,deferred_until=NULL WHERE id=?", actionId);
            PutActionRevision(actionId, Next(revision), action);
            return Show(actionId);
        });
    }

    /// <summary><c>disposition(action_id, revision, state, until=None, expected_hash=None)</c>: defer or dismiss a proposal.</summary>
    public JsonObject Disposition(JsonNode? actionId, JsonNode? revision, string state, JsonNode? until = null, JsonNode? expectedHash = null)
    {
        if (state is not ("deferred" or "dismissed"))
        {
            throw new StateError("invalid proposal disposition");
        }

        if (state == "deferred" && Timestamp(until) <= Now())
        {
            throw new StateError("defer until must be in the future");
        }

        return Transaction(() =>
        {
            var row = Row("work_actions", actionId);
            CheckRevision(row, revision);
            CheckActionHash(actionId, revision, expectedHash);
            if (PyDynamic.In(row["state"], ExecutionStates))
            {
                throw new StateError("cannot hide an unresolved or completed execution");
            }

            var snapshot = Conn.Execute("SELECT data FROM work_action_revisions WHERE action_id=? AND revision=?", actionId, revision).FetchOne();
            Invalidate(actionId, state, new JsonObject { ["until"] = PyDynamic.Clone(until) }, state: state);
            Conn.Execute("UPDATE work_actions SET revision=revision+1,deferred_until=? WHERE id=?", until, actionId);
            PutActionRevision(actionId, Next(revision), (JsonObject)Loads(snapshot!["data"])!);
            return Show(actionId);
        });
    }

    private void FreshAction(JsonObject action)
    {
        SourceRefs(PyDynamic.Index(action, "source_refs"));
        if (PyDynamic.Truthy(PyDynamic.Get(action, "artifact_id")))
        {
            var artifact = Show(action["artifact_id"]);
            if (!PyDynamic.Eq(PyDynamic.Index(artifact, "revision"), PyDynamic.Index(action, "artifact_revision"))
                || PyDynamic.Truthy(PyDynamic.Index(artifact, "stale")))
            {
                throw new StateError("linked artifact changed; create a new action revision");
            }
        }

        foreach (var itemId in PyDynamic.Iter(PyDynamic.Get(action, "dependencies", new JsonArray())))
        {
            if (!PyDynamic.Is(Row("work_items", itemId)["state"], "resolved"))
            {
                throw new StateError("action dependency is not resolved: " + PyDynamic.ToStr(itemId));
            }
        }
    }

    /// <summary>
    /// <c>approve(action_id, revision, action_hash, evidence, expires_at)</c>: records one exact human decision for the
    /// displayed revision and hash, expiring within seven days. Not an authentication boundary.
    /// </summary>
    public JsonObject Approve(JsonNode? actionId, JsonNode? revision, JsonNode? actionHash, JsonNode? evidence, JsonNode? expiresAt)
    {
        return Transaction(() =>
        {
            var action = Show(actionId);
            if (!PyDynamic.Is(action["type"], "action"))
            {
                throw new StateError("only actions use delivery approvals");
            }

            CheckRevision(action, revision);
            if (!PyDynamic.In(PyDynamic.Index(action, "state"), Set("ready", "approved")))
            {
                throw new StateError("action is not ready for approval; explicitly edit/review it first");
            }

            if (!PyDynamic.Eq(PyDynamic.Index(action, "action_hash"), actionHash))
            {
                throw new StateError("approval hash does not match displayed action");
            }

            var approval = Human(evidence, actionId, revision, "approve");
            if (!PyDynamic.Eq(PyDynamic.Get(approval, "action_hash"), actionHash))
            {
                throw new StateError("human evidence must include the displayed action_hash");
            }

            var expiry = Timestamp(expiresAt);
            if (expiry <= Now() || expiry > Now() + PyTimeDelta.FromDays(7))
            {
                throw new StateError("approval expiry must be in the future and within seven days");
            }

            FreshAction(action);
            Conn.Execute("UPDATE work_approvals SET invalidated_at=? WHERE action_id=? AND invalidated_at IS NULL", StateStore.UtcNow(), actionId);
            var approvalId = "approval_" + PyIds.Uuid4Hex();
            Conn.Execute("INSERT INTO work_approvals VALUES(?,?,?,?,?,?,?,?,?)",
                         approvalId, Account, actionId, revision, actionHash, StateStore.CanonicalJson(evidence), expiresAt, null, StateStore.UtcNow());
            Conn.Execute("UPDATE work_actions SET state='approved',updated_at=? WHERE id=?", StateStore.UtcNow(), actionId);
            Event(actionId, "approved", new JsonObject
            {
                ["approval_id"] = approvalId,
                ["evidence"] = PyDynamic.Clone(evidence),
                ["expires_at"] = PyDynamic.Clone(expiresAt),
            });
            return Show(actionId);
        });
    }

    /// <summary>
    /// <c>begin(action_id, revision, fresh)</c>: transactional foreground preflight. Returns the exact gated payload
    /// once and starts an execution attempt (the claim); a changed target or source invalidates the approval and
    /// raises. Never sends anything.
    /// </summary>
    public JsonObject Begin(JsonNode? actionId, JsonNode? revision, JsonNode? fresh)
    {
        string? staleReason = null;
        JsonObject? result = null;
        Transaction(() =>
        {
            var action = Show(actionId);
            CheckRevision(action, revision);
            if (!PyDynamic.Is(PyDynamic.Get(action, "type"), "action") || !PyDynamic.Is(PyDynamic.Index(action, "state"), "approved"))
            {
                throw new StateError("action has no executable approval or has already started");
            }

            var approval = Conn.Execute("""

                SELECT * FROM work_approvals WHERE action_id=? AND revision=?
                AND invalidated_at IS NULL ORDER BY created_at DESC LIMIT 1

""", actionId, revision).FetchOne();
            if (approval is null || Timestamp(FromSql(approval["expires_at"])) <= Now())
            {
                throw new StateError("approval has expired or was invalidated");
            }

            if (!Equals(approval["account"], Account) || !PyDynamic.Eq(FromSql(approval["action_hash"]), PyDynamic.Index(action, "action_hash")))
            {
                throw new StateError("approval account/hash mismatch");
            }

            try
            {
                FreshAction(action);
            }
            catch (StateError ex)
            {
                staleReason = ex.Message;
            }

            if (fresh is not JsonObject preflight || !preflight.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal).SetEquals(FreshKeys))
            {
                throw new StateError("fresh preflight requires checked_at,target_fingerprint,source_refs");
            }

            var age = Now() - Timestamp(preflight["checked_at"]);
            if (age < PyTimeDelta.FromSeconds(-30) || age > PyTimeDelta.FromMinutes(5))
            {
                throw new StateError("preflight must be a fresh foreground read within five minutes");
            }

            if (!PyDynamic.Eq(preflight["target_fingerprint"], PyDynamic.Index(action, "target_fingerprint")))
            {
                staleReason = "target changed; revise and request a new approval";
            }

            try
            {
                if (!PyDynamic.Eq(SourceRefs(preflight["source_refs"]), PyDynamic.Index(action, "source_refs")))
                {
                    staleReason = "source changed; revise and request a new approval";
                }
            }
            catch (StateError ex)
            {
                staleReason = ex.Message;
            }

            if (staleReason is not null)
            {
                Invalidate(actionId, "preflight_changed", new JsonObject { ["reason"] = staleReason });
            }
            else
            {
                var attemptId = "exec_" + PyIds.Uuid4Hex();
                Conn.Execute("INSERT INTO work_executions VALUES(?,?,?,?,?,?,?,?,?)",
                             attemptId, actionId, revision, approval["id"], "executing", StateStore.CanonicalJson(preflight), null,
                             StateStore.UtcNow(), null);
                Conn.Execute("UPDATE work_actions SET state='executing',updated_at=? WHERE id=?", StateStore.UtcNow(), actionId);
                Event(actionId, "execution_started", new JsonObject { ["attempt_id"] = attemptId, ["revision"] = PyDynamic.Clone(revision) });
                result = new JsonObject
                {
                    ["account"] = Account,
                    ["action_id"] = PyDynamic.Clone(actionId),
                    ["revision"] = PyDynamic.Clone(revision),
                    ["attempt_id"] = attemptId,
                    ["action_hash"] = PyDynamic.Clone(action["action_hash"]),
                    ["kind"] = PyDynamic.Clone(PyDynamic.Index(action, "kind")),
                    ["target"] = PyDynamic.Clone(PyDynamic.Index(action, "target")),
                    ["payload"] = PyDynamic.Clone(PyDynamic.Index(action, "payload")),
                    ["instruction"] = "Foreground Work IQ executor only. Do not retry an uncertain write.",
                };
            }
        });
        if (staleReason is not null)
        {
            throw new StateError(staleReason);
        }

        return result!;
    }

    /// <summary>
    /// <c>finish(attempt_id, state, receipt=None, reconcile=False)</c>: journals the real outcome of an execution
    /// attempt once; ambiguous writes are reconciled with a receipt, never replayed.
    /// </summary>
    public JsonObject Finish(JsonNode? attemptId, JsonNode? state, JsonNode? receipt = null, bool reconcile = false)
    {
        if (!PyDynamic.In(state, Outcomes))
        {
            throw new StateError("invalid execution outcome");
        }

        var outcome = PyDynamic.AsString(state)!;
        if (outcome != "outcome_unknown" || reconcile)
        {
            ValidateReceipt(receipt, outcome);
        }

        return Transaction(() =>
        {
            var attempt = Conn.Execute("SELECT * FROM work_executions WHERE id=?", attemptId).FetchOne()
                          ?? throw new StateError("unknown attempt");
            var action = Row("work_actions", FromSql(attempt["action_id"]));
            var allowed = reconcile ? Set("executing", "partial", "outcome_unknown") : Set("executing");
            var attemptState = (string?)attempt["state"];
            if (attemptState is null || !allowed.Contains(attemptState) || !PyDynamic.In(action["state"], allowed))
            {
                throw new StateError("execution is already settled; no replay");
            }

            if (attemptState == "partial" && outcome is "failed" or "outcome_unknown")
            {
                throw new StateError("known partial effects cannot become a no-effect failure or unknown outcome");
            }

            CheckRevision(action, FromSql(attempt["revision"]));
            Conn.Execute("UPDATE work_executions SET state=?,receipt=?,finished_at=? WHERE id=?",
                         outcome, StateStore.CanonicalJson(receipt), StateStore.UtcNow(), attemptId);
            Conn.Execute("UPDATE work_actions SET state=?,updated_at=? WHERE id=?", outcome, StateStore.UtcNow(), action["id"]);
            Event(action["id"], reconcile ? "execution_reconciled" : "execution_finished", new JsonObject
            {
                ["attempt_id"] = PyDynamic.Clone(attemptId),
                ["state"] = outcome,
                ["receipt"] = PyDynamic.Clone(receipt),
            });
            return Show(action["id"]);
        });
    }

    /// <summary><c>Ledger.validate_receipt(receipt, outcome=None)</c> (a static method in Python too).</summary>
    public static void ValidateReceipt(JsonNode? receipt, string? outcome = null)
    {
        if (receipt is not JsonObject obj)
        {
            throw new StateError("a real result receipt is required");
        }

        foreach (var key in new[] { "reference", "kind" })
        {
            Text(PyDynamic.Get(obj, key), "receipt." + key);
        }

        if (!ReceiptKinds.Contains(PyDynamic.AsString(obj["kind"])!))
        {
            throw new StateError("receipt kind must identify an actual tool result or reconciliation read");
        }

        if (!string.IsNullOrEmpty(outcome) && !PyDynamic.Is(PyDynamic.Get(obj, "outcome"), outcome))
        {
            throw new StateError("receipt outcome must match recorded outcome");
        }

        if (Timestamp(PyDynamic.Get(obj, "recorded_at")) > Now() + PyTimeDelta.FromMinutes(1))
        {
            throw new StateError("receipt cannot be in the future");
        }

        if (outcome == "failed" && !(PyDynamic.Get(obj, "definitive_no_effect") is JsonValue flag && PyJson.Scalar(flag) is true))
        {
            throw new StateError("failed requires proof of no effect; otherwise use partial/outcome_unknown");
        }
    }

    /// <summary>
    /// <c>show(entity_id)</c>: an item, action, record or source of this account with its derived fields
    /// (staleness, sources, approvals, executions, relationships, sharing, revisions).
    /// </summary>
    public JsonObject Show(JsonNode? entityId)
    {
        foreach (var (table, kind) in new[] { ("work_items", "item"), ("work_actions", "action"), ("work_records", "record"), ("work_sources", "source") })
        {
            var found = Conn.Execute("SELECT * FROM " + table + " WHERE id=? AND account=?", entityId, Account).FetchOne();
            if (found is null)
            {
                continue;
            }

            var row = RowDict(found);
            row["type"] = kind;
            if (kind == "action")
            {
                var revision = Conn.Execute("SELECT * FROM work_action_revisions WHERE action_id=? AND revision=?", entityId, row["revision"]).FetchOne()!;
                UpdateFrom(row, Loads(revision["data"]));
                row["action_hash"] = FromSql(revision["action_hash"]);
                var approvals = new JsonArray();
                foreach (var approval in Conn.Execute("SELECT id,revision,action_hash,expires_at,invalidated_at FROM work_approvals WHERE action_id=?", entityId))
                {
                    approvals.Add(RowDict(approval));
                }

                row["approvals"] = approvals;
                var executions = new JsonArray();
                foreach (var execution in Conn.Execute("SELECT * FROM work_executions WHERE action_id=?", entityId))
                {
                    var result = RowDict(execution);
                    result["receipt"] = PyDynamic.Truthy(result["receipt"]) ? Loads(execution["receipt"]) : null;
                    result["preflight"] = Loads(execution["preflight"]);
                    executions.Add(result);
                }

                row["executions"] = executions;
                row["stale"] = RefsStale(PyDynamic.Index(row, "source_refs"));
                row["sources"] = SourceEvidence(PyDynamic.Index(row, "source_refs"));
                if (PyDynamic.Truthy(PyDynamic.Get(row, "artifact_id")))
                {
                    var artifact = Show(row["artifact_id"]);
                    row["stale"] = PyDynamic.Truthy(row["stale"]) || PyDynamic.Truthy(PyDynamic.Index(artifact, "stale"))
                                   || !PyDynamic.Eq(PyDynamic.Index(artifact, "revision"), PyDynamic.Index(row, "artifact_revision"));
                }

                if (PyDynamic.In(row["state"], Set("succeeded", "dismissed")))
                {
                    row["historical_sources_changed"] = PyDynamic.Clone(row["stale"]);
                    row["stale"] = false;
                }
            }
            else if (kind is "item" or "record")
            {
                var raw = row["data"];
                row.Remove("data");
                var data = Loads(PyDynamic.AsString(raw));
                row["data"] = data;
                var refs = PyDynamic.Get(PyDynamic.Dict(data), "source_refs", new JsonArray());
                row["stale"] = RefsStale(refs);
                row["sources"] = SourceEvidence(refs);
                if (kind == "item")
                {
                    row["confirmed"] = PyDynamic.Truthy(row["confirmed"]);
                    row["confirmation"] = PyDynamic.Truthy(row["confirmation"]) ? Loads(PyDynamic.AsString(row["confirmation"])) : null;
                    var relationships = new JsonArray();
                    foreach (var relationship in Conn.Execute("SELECT source_id,kind,target_id FROM work_relationships WHERE source_id=? OR target_id=?", entityId, entityId))
                    {
                        relationships.Add(RowDict(relationship));
                    }

                    row["relationships"] = relationships;
                }
                else if (PyDynamic.Is(row["kind"], "artifact"))
                {
                    var deliveries = new JsonArray();
                    foreach (var shared in Conn.Execute("SELECT data FROM work_events WHERE entity_id=? AND event='shared' ORDER BY seq", entityId))
                    {
                        deliveries.Add(Loads(shared["data"]));
                    }

                    var current = row["revision"];
                    row["sharing"] = new JsonObject
                    {
                        ["state"] = deliveries.Any(delivery => PyDynamic.Eq(PyDynamic.Index(delivery, "revision"), current)) ? "shared" : "private",
                        ["deliveries"] = deliveries,
                    };
                }
            }
            else
            {
                var revisions = new JsonArray();
                foreach (var revision in Conn.Execute("SELECT * FROM work_source_revisions WHERE source_id=?", entityId))
                {
                    var entry = RowDict(revision);
                    entry["data"] = Loads(revision["data"]);
                    revisions.Add(entry);
                }

                row["revisions"] = revisions;
            }

            return row;
        }

        throw new StateError("unknown entity: " + PyDynamic.ToStr(entityId));
    }

    /// <summary><c>show(entity_id)</c> for a string id.</summary>
    public JsonObject Show(string entityId) => Show(PyDynamic.Str(entityId));

    /// <summary><c>history(entity_id)</c>: the entity's events (in order) and stored revisions.</summary>
    public JsonObject History(JsonNode? entityId)
    {
        Show(entityId);
        var events = new JsonArray();
        foreach (var row in Conn.Execute("SELECT * FROM work_events WHERE account=? AND entity_id=? ORDER BY seq", Account, entityId))
        {
            var entry = RowDict(row);
            entry["data"] = Loads(row["data"]);
            events.Add(entry);
        }

        var revisions = new JsonArray();
        foreach (var (table, column) in new[] { ("work_item_revisions", "item_id"), ("work_action_revisions", "action_id"), ("work_record_revisions", "record_id") })
        {
            foreach (var row in Conn.Execute("SELECT * FROM " + table + " WHERE " + column + "=? ORDER BY revision", entityId))
            {
                var entry = RowDict(row);
                foreach (var key in new[] { "snapshot", "data", "evidence" })
                {
                    if (PyDynamic.Truthy(PyDynamic.Get(entry, key)))
                    {
                        entry[key] = Loads(PyDynamic.AsString(entry[key]));
                    }
                }

                revisions.Add(entry);
            }
        }

        return new JsonObject { ["id"] = PyDynamic.Clone(entityId), ["events"] = events, ["revisions"] = revisions };
    }

    /// <summary><c>history(entity_id)</c> for a string id.</summary>
    public JsonObject History(string entityId) => History(PyDynamic.Str(entityId));

    /// <summary><c>list(view="decisions")</c>: the portable review views (also the optional canvas contract).</summary>
    public JsonObject List(string view = "decisions")
    {
        if (!Views.Contains(view))
        {
            throw new StateError("unknown view");
        }

        var result = new JsonObject
        {
            ["schema_version"] = 1,
            ["account"] = Account,
            ["items"] = new JsonArray(),
            ["actions"] = new JsonArray(),
            ["records"] = new JsonArray(),
        };
        foreach (var (table, key) in new[] { ("work_items", "items"), ("work_actions", "actions"), ("work_records", "records") })
        {
            foreach (var row in Conn.Execute("SELECT id FROM " + table + " WHERE account=? ORDER BY created_at,id", Account))
            {
                var obj = Show(FromSql(row["id"]));
                var until = PyDynamic.Get(obj, "deferred_until");
                if (!PyDynamic.Truthy(until))
                {
                    until = PyDynamic.Get(PyDynamic.Dict(PyDynamic.Get(obj, "data", new JsonObject())), "deferred_until");
                }

                var state = PyDynamic.Index(obj, "state");
                var due = PyDynamic.Is(state, "deferred") && PyDynamic.Truthy(until) && Timestamp(until) <= Now();
                var staleActionable = PyDynamic.Truthy(PyDynamic.Get(obj, "stale")) && !PyDynamic.In(state, ClosedStates);
                if (view == "all" || PyDynamic.In(state, ViewStates[view])
                    || (view is "decisions" or "problems" && staleActionable)
                    || (view == "decisions" && due))
                {
                    ((JsonArray)result[key]!).Add(obj);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// <c>import_preview(path)</c>: structured legacy JSON for review, or the raw text of anything else (Markdown is
    /// never parsed or confirmed automatically).
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "An instance method in Python, called on the ledger.")]
    public JsonObject ImportPreview(string path)
    {
        var content = PyIO.ReadTextFile(PyPath.Normalize(path));
        var contentDigest = PyIds.Sha256Hex(content);
        JsonNode? data;
        try
        {
            data = PyJson.Loads(content);
        }
        catch (PyValueError)
        {
            return new JsonObject
            {
                ["digest"] = contentDigest,
                ["safe_structured"] = false,
                ["requires_review"] = true,
                ["reason"] = "Markdown is not automatically parsed or confirmed. Review into structured JSON.",
                ["content"] = content,
            };
        }

        if (data is not JsonObject obj || !PyDynamic.Eq(PyDynamic.Get(obj, "schema_version"), PyDynamic.Int(1))
            || PyDynamic.Get(obj, "items") is not JsonArray items)
        {
            throw new StateError("structured legacy import requires schema_version:1 and items list");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item is not JsonObject row || !row.ContainsKey("legacy_id") || !row.ContainsKey("state") || !row.ContainsKey("data"))
            {
                throw new StateError("each import row requires legacy_id,state,data");
            }

            var legacyId = Text(row["legacy_id"], "legacy_id");
            if (!seen.Add(legacyId))
            {
                throw new StateError("duplicate legacy_id");
            }

            if (!PyDynamic.In(row["state"], ItemStates))
            {
                throw new StateError("invalid imported state");
            }

            if (row["data"] is not JsonObject)
            {
                throw new StateError("import data must be an object");
            }
        }

        return new JsonObject
        {
            ["digest"] = contentDigest,
            ["safe_structured"] = true,
            ["requires_review"] = true,
            ["count"] = items.Count,
            ["items"] = PyDynamic.Clone(items),
        };
    }

    /// <summary><c>import_commitments(path, expected_digest, evidence)</c>: transactional reviewed structured legacy import.</summary>
    public JsonObject ImportCommitments(string path, string expectedDigest, JsonNode? evidence)
    {
        var preview = ImportPreview(path);
        if (!PyDynamic.Is(preview["digest"], expectedDigest) || !PyDynamic.Truthy(preview["safe_structured"]))
        {
            throw new StateError("unsafe or changed import; preview and review structured JSON first");
        }

        Human(evidence, PyDynamic.Str("import:" + expectedDigest), PyDynamic.Int(1), "import");
        return Transaction(() =>
        {
            var old = Conn.Execute("SELECT item_ids FROM work_imports WHERE digest=?", expectedDigest).FetchOne();
            if (old is not null)
            {
                return new JsonObject { ["imported"] = false, ["item_ids"] = Loads(old["item_ids"]) };
            }

            var ids = new JsonArray();
            foreach (var imported in (JsonArray)preview["items"]!)
            {
                var data = ItemData(imported!["data"]);
                var key = Digest(new JsonArray(Account, "legacy", PyDynamic.Clone(imported["legacy_id"])));
                var itemId = "item_" + key[..32];
                var contentHash = Digest(new JsonObject { ["state"] = PyDynamic.Clone(imported["state"]), ["data"] = PyDynamic.Clone(data) });
                var existing = Conn.Execute("SELECT ingest_hash FROM work_items WHERE id=?", itemId).FetchOne();
                if (existing is not null)
                {
                    if (!Equals(existing["ingest_hash"], contentHash))
                    {
                        throw new StateError("legacy row changed; explicitly review/edit the existing item");
                    }
                }
                else
                {
                    var confirmed = PyDynamic.In(imported["state"], ImportConfirmed);
                    var stamp = StateStore.UtcNow();
                    Conn.Execute("INSERT INTO work_items VALUES(?,?,?,?,?,?,?,?,?,?,?)",
                                 itemId, Account, 1L, imported["state"], confirmed ? 1L : 0L, key, contentHash, StateStore.CanonicalJson(data),
                                 confirmed ? StateStore.CanonicalJson(evidence) : null, stamp, stamp);
                    ItemSnapshot(PyDynamic.Str(itemId), evidence);
                    Event(PyDynamic.Str(itemId), "legacy_imported", new JsonObject { ["digest"] = expectedDigest, ["evidence"] = PyDynamic.Clone(evidence) });
                }

                ids.Add(itemId);
            }

            Conn.Execute("INSERT INTO work_imports VALUES(?,?,?,?)",
                         expectedDigest, StateStore.CanonicalJson(evidence), StateStore.CanonicalJson(ids), StateStore.UtcNow());
            return new JsonObject { ["imported"] = true, ["item_ids"] = ids };
        });
    }

    /// <summary>
    /// <c>export_commitments(path, expected_digest=None)</c>: writes the confirmed-only Markdown compatibility view
    /// atomically, refusing to replace hand edits or unmanaged files without the reviewed digest.
    /// </summary>
    public JsonObject ExportCommitments(string path, string? expectedDigest = null)
    {
        path = PyPath.Absolute(PyPath.ExpandUser(path));
        if (PyPath.IsSymlink(path))
        {
            throw new StateError("refusing a symlink export");
        }

        return Transaction(() =>
        {
            var prior = Conn.Execute("SELECT digest FROM work_exports WHERE path=?", path).FetchOne();
            var actual = PyPath.Exists(path) ? PyIds.Sha256Hex(PyIO.ReadBytes(path)) : null;
            if (actual is not null)
            {
                var approvedDigest = !string.IsNullOrEmpty(expectedDigest) ? expectedDigest : prior is not null ? (string?)prior["digest"] : null;
                if (actual != approvedDigest)
                {
                    throw new StateError("compatibility view has hand edits or is unmanaged; preview/review before replacing");
                }
            }
            else if (!string.IsNullOrEmpty(expectedDigest))
            {
                throw new StateError("export changed since review");
            }

            var lines = new List<string> { "# Commitments", "", "<!-- Generated compatibility view; edit the work ledger, not this file. -->", "" };
            static string Clean(JsonNode? value) =>
                (value is null ? "unknown" : PyDynamic.ToStr(value)).Replace("\n", " ", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal);
            foreach (var row in Conn.Execute("SELECT id FROM work_items WHERE account=? AND confirmed=1 ORDER BY created_at,id", Account))
            {
                var obj = Show(FromSql(row["id"]));
                var data = obj["data"];
                lines.Add("- [" + PyDynamic.ToStr(obj["state"]) + "] " + Clean(PyDynamic.Index(data, "title")) + " — owner: "
                          + Clean(PyDynamic.Index(data, "owner")) + "; due: " + Clean(PyDynamic.Index(data, "due")) + "; id: `"
                          + PyDynamic.ToStr(obj["id"]) + "`");
                foreach (var reference in PyDynamic.Iter(PyDynamic.Index(data, "source_refs")))
                {
                    var source = Conn.Execute("SELECT data FROM work_source_revisions WHERE source_id=? AND revision=?",
                                              PyDynamic.Index(reference, "source_id"), PyDynamic.Index(reference, "revision")).FetchOne()!;
                    lines.Add("  - Source: " + Clean(PyDynamic.Index(Loads(source["data"]), "web_link")) + " (revision "
                              + Clean(PyDynamic.Index(reference, "revision")) + ")");
                }
            }

            var content = PyIO.EncodeUtf8(string.Join("\n", lines) + "\n");
            var outputDigest = PyIds.Sha256Hex(content);
            // Same-directory exclusive staging permits atomic replacement without a shared temp directory.
            var staging = PyPath.Join(PyPath.Parent(path), "." + PyPath.Name(path) + "." + PyIds.Uuid4Hex() + ".staging");
            try
            {
                var fd = PosixNative.Open(staging, PosixNative.OWrOnly | PosixNative.OCreat | PosixNative.OExcl, Convert.ToUInt32("600", 8));
                try
                {
                    PosixNative.WriteAll(fd, content);
                    PosixNative.Fsync(fd);
                }
                finally
                {
                    PosixNative.Close(fd);
                }

                var current = PyPath.Exists(path) ? PyIds.Sha256Hex(PyIO.ReadBytes(path)) : null;
                if (current != actual || PyPath.IsSymlink(path))
                {
                    throw new StateError("compatibility view changed during export");
                }

                PosixFiles.Replace(staging, path);
            }
            finally
            {
                if (PyPath.Exists(staging))
                {
                    PosixNative.Unlink(staging);
                }
            }

            Conn.Execute("INSERT OR REPLACE INTO work_exports VALUES(?,?,?)", path, outputDigest, StateStore.UtcNow());
            return new JsonObject { ["path"] = path, ["digest"] = outputDigest, ["confirmed_only"] = true };
        });
    }
}

using System.Text;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.State.Memory;

/// <summary>
/// Port of <c>skills/chief-of-staff/scripts/memory_governance.py</c>: the reviewed capture policy, passive capture,
/// retention and bounded maintenance, deletion journals (tombstones) and their reviewed restore, and the reviewed,
/// sanitised, private recipe export of a confirmed lesson. Nothing here reaches the network or publishes anything.
/// </summary>
public static class MemoryGovernance
{
    /// <summary>The module docstring.</summary>
    public const string Doc = "Reviewed capture policy, retention, deletion recovery and private recipe export.";

    private const string RetentionWarning =
        "Retention authorizes local erasure of matching memories and their derived records; it does not delete sources.";

    private const string ExportWarning =
        "Automated checks cannot recognize all private context. Review every word. Export is local, not permission to publish.";

    private static readonly string[] PolicyKeys = ["capture", "allow_sensitive", "usage_enabled", "usage_retention_days", "retention_days", "review_days"];
    private static readonly string[] CaptureKeys = ["enabled", "domains", "kinds", "scopes", "source_kinds"];
    private static readonly string[] RecipeKeys = ["title", "goal", "preconditions", "steps", "limitations"];
    private static readonly string[] RecipeSections = ["goal", "preconditions", "steps", "limitations"];
    private const string TempAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789_";

    /// <summary><c>digest(value)</c>: SHA-256 of the canonical JSON.</summary>
    public static string Digest(JsonNode? value) => PyIds.Sha256Hex(StateStore.CanonicalJson(value));

    private static bool SameKeys(JsonObject value, IEnumerable<string> keys) =>
        value.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal).SetEquals(keys);

    /// <summary>The policy every account has until one is reviewed: capture and usage off.</summary>
    public static JsonObject DefaultPolicy() => new()
    {
        ["capture"] = new JsonObject
        {
            ["enabled"] = false,
            ["domains"] = new JsonArray(),
            ["kinds"] = new JsonArray(),
            ["scopes"] = new JsonArray(),
            ["source_kinds"] = new JsonArray(),
        },
        ["allow_sensitive"] = false,
        ["usage_enabled"] = false,
        ["usage_retention_days"] = 30L,
        ["retention_days"] = new JsonObject(),
        ["review_days"] = new JsonObject(),
    };

    /// <summary><c>policy(memory)</c>: the account's reviewed policy (revision 0 and the default when none is configured).</summary>
    public static JsonObject Policy(MemoryStore memory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        var row = memory.Conn.Execute("SELECT * FROM memory_policy WHERE account=?", memory.Account).FetchOne();
        return new JsonObject
        {
            ["account"] = memory.Account,
            ["revision"] = row is not null ? Py.FromDb(row["revision"]) : 0L,
            ["configured"] = row is not null,
            ["data"] = row is not null ? MemoryStore.ParseStored(row["data"]) : DefaultPolicy(),
        };
    }

    /// <summary><c>normalise_policy(data)</c>: the complete validated policy (sorted, de-duplicated capture lists).</summary>
    public static JsonObject NormalisePolicy(JsonNode? data)
    {
        if (data is not JsonObject input || !SameKeys(input, PolicyKeys))
        {
            throw new StateError("policy requires capture, allow_sensitive, usage_enabled, usage_retention_days, retention_days and review_days");
        }

        if (input["capture"] is not JsonObject capture || !SameKeys(capture, CaptureKeys))
        {
            throw new StateError("capture requires enabled, domains, kinds, scopes and source_kinds");
        }

        foreach (var value in new[] { capture["enabled"], input["allow_sensitive"], input["usage_enabled"] })
        {
            if (!PyJson.IsBool(value))
            {
                throw new StateError("policy switches must be booleans");
            }
        }

        var normal = Py.DictWith(input, ("capture", Py.Copy(capture)));
        var captureNormal = normal["capture"]!.AsObject();
        var enabled = PyJson.Truthy(capture["enabled"]);
        foreach (var (field, allowed) in new (string, IReadOnlySet<string>?)[]
                 {
                     ("domains", MemoryStore.Domains), ("kinds", MemoryStore.Kinds), ("scopes", null), ("source_kinds", MemoryStore.SourceKinds),
                 })
        {
            var values = capture[field];
            if (values is not JsonArray list || list.Count > 100
                || list.Any(value => !Py.IsText(value) || (allowed is not null && !allowed.Contains(Py.AsString(value)!))))
            {
                throw new StateError("invalid capture " + field);
            }

            if (enabled && list.Count == 0)
            {
                throw new StateError("enabled capture needs an explicit nonempty " + field);
            }

            captureNormal[field] = Py.StrArray(Py.Sorted(list.Select(value => Py.AsString(value)!).Distinct(StringComparer.Ordinal)));
        }

        foreach (var field in new[] { "retention_days", "review_days" })
        {
            if (input[field] is not JsonObject values
                || values.Any(pair => !MemoryStore.Kinds.Contains(pair.Key) || !PyJson.IsInt(pair.Value)
                                      || PyJson.AsInt(pair.Value) < 1 || PyJson.AsInt(pair.Value) > 3650))
            {
                throw new StateError(field + " must map memory kinds to 1..3650 days");
            }

            normal[field] = Py.Copy(values);
        }

        var usageDays = input["usage_retention_days"];
        if (!PyJson.IsInt(usageDays) || PyJson.AsInt(usageDays) < 1 || PyJson.AsInt(usageDays) > 3650)
        {
            throw new StateError("usage_retention_days must be between 1 and 3650");
        }

        return normal;
    }

    private static PyDateTime Due(JsonNode? stamp, JsonNode? days) =>
        PyDateTime.FromIsoformat(Py.AsString(stamp) ?? throw new PyTypeError("fromisoformat: argument must be str"))
        + PyTimeDelta.FromDays((long)PyJson.AsInt(days));

    /// <summary>
    /// <c>policy_preview(memory, data)</c>: the normalised policy, its current retention impact and the reviewed subject
    /// that <see cref="SetPolicy"/> evidence must name.
    /// </summary>
    public static JsonObject PolicyPreview(MemoryStore memory, JsonNode? data)
    {
        ArgumentNullException.ThrowIfNull(memory);
        var current = Policy(memory);
        var normal = NormalisePolicy(data);
        var roots = new List<JsonObject>();
        var affected = new HashSet<string>(StringComparer.Ordinal);
        var stamp = PyDateTime.Now();
        var retention = normal["retention_days"]!.AsObject();
        foreach (var node in memory.List())
        {
            var record = node!.AsObject();
            var days = Py.AsString(record["kind"]) is { } kind ? Py.Get(retention, kind) : null;
            if (!PyJson.PyEquals(record["status"], JsonValue.Create("forgotten")) && days is not null && Due(record["updated_at"], days) <= stamp)
            {
                roots.Add(new JsonObject { ["id"] = Py.Clone(record["id"]), ["revision"] = Py.Clone(record["revision"]) });
                affected.UnionWith(memory.DerivedDescendants(Py.AsString(record["id"])!));
            }
        }

        var due = roots.OrderBy(row => Py.Str(row["id"]), PyRepr.StringComparer).Take(50).Select(row => (JsonNode?)row).ToArray();
        var value = new JsonObject
        {
            ["account"] = memory.Account,
            ["expected_revision"] = Py.Clone(current["revision"]),
            ["data"] = normal,
            ["current_retention_impact"] = new JsonObject
            {
                ["due_roots"] = new JsonArray(due),
                ["due_root_count"] = (long)roots.Count,
                ["affected_record_count"] = (long)affected.Count,
                ["truncated"] = roots.Count > 50,
            },
        };
        return Py.DictWith(value, ("subject_id", JsonValue.Create("memory-policy:" + Digest(value))), ("revision", JsonValue.Create(1L)),
                           ("warning", JsonValue.Create(RetentionWarning)));
    }

    /// <summary><c>set_policy(memory, data, evidence)</c>: saves the reviewed policy (evidence must name its preview subject).</summary>
    public static JsonObject SetPolicy(MemoryStore memory, JsonNode? data, JsonNode? evidence)
    {
        ArgumentNullException.ThrowIfNull(memory);
        return memory.Transaction(() =>
        {
            var preview = PolicyPreview(memory, data);
            memory.Human(evidence, preview["subject_id"], JsonValue.Create(1L), "configure");
            memory.Conn.Execute(
                "INSERT INTO memory_policy VALUES(?,?,?,?,?) ON CONFLICT(account) DO UPDATE SET "
                + "revision=excluded.revision,data=excluded.data,evidence=excluded.evidence,updated_at=excluded.updated_at",
                memory.Account, PyJson.AsInt(preview["expected_revision"]) + 1, StateStore.CanonicalJson(preview["data"]),
                StateStore.CanonicalJson(evidence), StateStore.UtcNow());
            return Policy(memory);
        });
    }

    /// <summary>
    /// <c>authorize_capture(memory, normal, allow_confirmation=False)</c>: the policy allows this passive capture. Like
    /// Python, it reads the policy first and then indexes <paramref name="normal"/> (any value; callers pass normalised
    /// data), reading all three scoped fields before checking any.
    /// </summary>
    public static JsonObject AuthorizeCapture(MemoryStore memory, JsonNode? normal, bool allowConfirmation = false)
    {
        ArgumentNullException.ThrowIfNull(memory);
        var current = Policy(memory);
        var rules = MemoryStore.Item(MemoryStore.Item(current, "data"), "capture");
        if (!PyJson.Truthy(MemoryStore.Item(rules, "enabled")))
        {
            throw new StateError("passive capture is disabled; review a scoped capture policy first");
        }

        var scoped = new[]
        {
            ("domains", MemoryStore.Item(normal, "domain")), ("kinds", MemoryStore.Item(normal, "kind")),
            ("scopes", MemoryStore.Item(normal, "scope")),
        };
        foreach (var (field, value) in scoped)
        {
            if (!Contains(MemoryStore.Item(rules, field), value))
            {
                throw new StateError("capture policy excludes this " + field);
            }
        }

        var sourceKinds = MemoryStore.Item(rules, "source_kinds");
        if (MemoryStore.Iterate(MemoryStore.Item(normal, "source_refs")).Any(reference => !Contains(sourceKinds, MemoryStore.Item(reference, "kind"))))
        {
            throw new StateError("capture policy excludes this source category");
        }

        if (PyJson.PyEquals(MemoryStore.Item(normal, "sensitivity"), JsonValue.Create("sensitive"))
            && !PyJson.Truthy(MemoryStore.Item(MemoryStore.Item(current, "data"), "allow_sensitive")))
        {
            throw new StateError("sensitive capture needs a separate explicit policy opt-in");
        }

        if (PyJson.PyEquals(MemoryStore.Item(normal, "authority"), JsonValue.Create("user_confirmed")) && !allowConfirmation)
        {
            throw new StateError("passive capture cannot manufacture user confirmation");
        }

        return current;
    }

    private static bool Contains(JsonNode? list, JsonNode? value) => list is JsonArray array
        ? Py.InList(value, array)
        : throw new PyTypeError("argument of type '" + PyJson.TypeName(list) + "' is not iterable");

    /// <summary>
    /// <c>capture(memory, key, data, revision=None, *, _checkpoint_ref=None)</c>: stages one minimal observation under the
    /// reviewed capture policy (source observations of observational kinds become active, everything else a candidate;
    /// disputed and stale records keep their review flag).
    /// </summary>
    public static JsonObject Capture(MemoryStore memory, JsonNode? key, JsonNode? data, JsonNode? revision = null, JsonNode? checkpointRef = null)
    {
        ArgumentNullException.ThrowIfNull(memory);
        return memory.Transaction(() =>
        {
            var normal = memory.ResolveWorkSources(memory.NormaliseData(data), checkpointRef);
            var current = AuthorizeCapture(memory, normal);
            normal["metadata"] = Py.DictWith(normal["metadata"], ("capture_policy_revision", Py.Clone(current["revision"])));
            var reviewDays = MemoryStore.Item(MemoryStore.Item(current, "data"), "review_days");
            var kind = Py.AsString(normal["kind"])!;
            if (reviewDays is JsonObject days && days.ContainsKey(kind) && !PyJson.Truthy(Py.Get(normal, "review_after")))
            {
                var previous = memory.Conn.Execute("SELECT data FROM memory_records WHERE id=? AND account=?",
                                                   memory.RecordId(normal["domain"], key), memory.Account).FetchOne();
                var old = previous is not null ? MemoryStore.ParseStored(previous["data"]) : new JsonObject();
                normal["review_after"] = PyJson.Truthy(MemoryStore.DictGet(old, "review_after"))
                                         && PyJson.PyEquals(MemoryStore.DictGet(old, "source_refs"), normal["source_refs"])
                    ? Py.Clone(MemoryStore.Item(old, "review_after"))
                    : (PyDateTime.Now() + PyTimeDelta.FromDays((long)PyJson.AsInt(days[kind]))).Isoformat();
            }

            var status = PyJson.PyEquals(normal["authority"], JsonValue.Create("source_observed")) && kind is not ("preference" or "lesson")
                ? "active"
                : "candidate";
            var prior = memory.Conn.Execute("SELECT status FROM memory_records WHERE id=? AND account=?",
                                            memory.RecordId(normal["domain"], key), memory.Account).FetchOne();
            if (prior is not null && prior["status"] is "disputed" or "stale")
            {
                status = (string)prior["status"]!;
            }

            if (checkpointRef is not null)
            {
                // Only the validated Dream adapter can write a root before its source exists.
                var (memoryId, keyHash) = memory.Identity(normal["domain"], key);
                return memory.PutRecord(memoryId, keyHash, memory.NormaliseData(normal), status, revision, null, checkpointRef);
            }

            return memory.Put(key, normal, status, revision);
        });
    }

    /// <summary><c>retention_authorized(memory, record, expected_revision)</c>: the approved retention policy makes the record due now.</summary>
    public static void RetentionAuthorized(MemoryStore memory, JsonNode? record, JsonNode? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(memory);
        var current = Policy(memory);
        if (!PyJson.IsInt(expectedRevision) || !PyJson.PyEquals(current["revision"], expectedRevision) || !PyJson.Truthy(current["configured"]))
        {
            throw new StateError("retention policy changed or is not configured");
        }

        var retention = MemoryStore.Item(MemoryStore.Item(current, "data"), "retention_days");
        // .get(record["kind"]): an unhashable kind raises; a non-string one is never a key (keys are memory kinds).
        var kind = MemoryStore.Item(record, "kind");
        Py.EnsureHashable(kind);
        var days = Py.AsString(kind) is { } name ? MemoryStore.DictGet(retention, name) : retention is JsonObject ? null : MemoryStore.DictGet(retention, "");
        if (days is null || Due(MemoryStore.Item(record, "updated_at"), days) > PyDateTime.Now())
        {
            throw new StateError("memory is not due under the approved retention policy");
        }
    }

    /// <summary>
    /// <c>maintain(memory, limit=100, after=None)</c>: one bounded page of retention erasure and review-due reporting,
    /// plus expiry of old usage events. Never ingests sources and never acts outward.
    /// </summary>
    public static JsonObject Maintain(MemoryStore memory, PyArg limit = default, JsonNode? after = null)
    {
        ArgumentNullException.ThrowIfNull(memory);
        var size = limit.Or(100);
        if (!PyJson.IsInt(size) || PyJson.AsInt(size) < 1 || PyJson.AsInt(size) > 1000)
        {
            throw new StateError("maintenance limit must be between 1 and 1000");
        }

        if (after is not null && !PyJson.IsStr(after))
        {
            throw new StateError("maintenance cursor must be a string");
        }

        var count = (int)PyJson.AsInt(size);
        return memory.Transaction(() =>
        {
            var current = Policy(memory);
            var rows = memory.Conn.Execute(
                "SELECT id FROM memory_records WHERE account=? AND id>? AND status<>'forgotten' ORDER BY id LIMIT ?",
                memory.Account, PyJson.Truthy(after) ? after : JsonValue.Create(""), count + 1).FetchAll();
            var erased = new List<string>();
            var review = new JsonArray();
            var stamp = PyDateTime.Now();
            var data = MemoryStore.Item(current, "data");
            foreach (var row in rows.Take(count))
            {
                var record = memory.Show(Py.FromDb(row["id"]));
                if (PyJson.PyEquals(record["status"], JsonValue.Create("forgotten")))
                {
                    continue;
                }

                var days = MemoryStore.DictGet(MemoryStore.Item(data, "retention_days"), Py.Str(record["kind"]));
                if (days is not null && Due(record["updated_at"], days) <= stamp)
                {
                    var result = memory.Forget(record["id"], record["revision"], policyRevision: current["revision"]);
                    erased.AddRange(((JsonArray)result["forgotten_ids"]!).Select(id => Py.Str(id)));
                }
                else if (PyJson.Truthy(Py.Get(record, "review_after"))
                         && PyDateTime.FromIsoformat(Py.AsString(record["review_after"])!) <= stamp)
                {
                    review.Add(new JsonObject
                    {
                        ["id"] = Py.Clone(record["id"]),
                        ["revision"] = Py.Clone(record["revision"]),
                        ["reason"] = "review_due",
                    });
                }
            }

            var cutoff = (stamp - PyTimeDelta.FromDays((long)PyJson.AsInt(MemoryStore.Item(data, "usage_retention_days")))).Isoformat();
            memory.Conn.Execute("DELETE FROM memory_usage WHERE id IN "
                                + "(SELECT id FROM memory_usage WHERE account=? AND created_at<? ORDER BY created_at LIMIT ?)",
                                memory.Account, cutoff, count);
            return new JsonObject
            {
                ["processed"] = (long)Math.Min(rows.Count, count),
                ["forgotten_ids"] = Py.StrArray(Py.Sorted(erased.Distinct(StringComparer.Ordinal))),
                ["review_due"] = review,
                ["next_cursor"] = rows.Count > count ? Py.FromDb(rows[count - 1]["id"]) : null,
                ["policy_revision"] = Py.Clone(current["revision"]),
                ["outbound_actions"] = false,
            };
        });
    }

    /// <summary><c>tombstones(memory)</c>: the minimal deletion journal for private backups.</summary>
    public static JsonObject Tombstones(MemoryStore memory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        var entries = new JsonArray();
        foreach (var row in memory.Conn.Execute("SELECT id,key_hash,forgotten_at FROM memory_tombstones WHERE account=? ORDER BY id", memory.Account))
        {
            entries.Add(new JsonObject
            {
                ["id"] = Py.FromDb(row["id"]),
                ["key_hash"] = Py.FromDb(row["key_hash"]),
                ["forgotten_at"] = Py.FromDb(row["forgotten_at"]),
            });
        }

        return new JsonObject { ["format"] = 1L, ["account"] = memory.Account, ["tombstones"] = entries };
    }

    /// <summary>
    /// <c>tombstones_preview(memory, bundle)</c>: validates a deletion journal and lists the live records (and session
    /// checkpoint derivatives) a restore would erase, with the reviewed subject restore evidence must name.
    /// </summary>
    public static JsonObject TombstonesPreview(MemoryStore memory, JsonNode? bundle)
    {
        ArgumentNullException.ThrowIfNull(memory);
        if (bundle is not JsonObject journal || !SameKeys(journal, ["format", "account", "tombstones"]))
        {
            throw new StateError("invalid tombstone journal");
        }

        if (!PyJson.IsInt(journal["format"]) || PyJson.AsInt(journal["format"]) != 1
            || !PyJson.PyEquals(journal["account"], JsonValue.Create(memory.Account)))
        {
            throw new StateError("tombstone journal belongs to another account or format");
        }

        if (journal["tombstones"] is not JsonArray entries || entries.Count > 10000)
        {
            throw new StateError("tombstone restore accepts at most 10000 entries");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var affected = new List<JsonObject>();
        foreach (var item in entries)
        {
            if (item is not JsonObject entry || !SameKeys(entry, ["id", "key_hash", "forgotten_at"])
                || Py.AsString(entry["key_hash"]) is not { } keyHash || !PyPatterns.Sha256Hex(keyHash)
                || !PyJson.PyEquals(entry["id"], JsonValue.Create("mem_" + keyHash[..40])) || ids.Contains(Py.AsString(entry["id"])!))
            {
                throw new StateError("invalid or duplicate tombstone identity");
            }

            MemoryStore.ValidateTimestamp(entry["forgotten_at"]);
            var id = Py.AsString(entry["id"])!;
            ids.Add(id);
            var row = memory.Conn.Execute("SELECT id,revision,status FROM memory_records WHERE id=? AND account=?", id, memory.Account).FetchOne();
            if (row is not null && !Py.Equal(row["status"], "forgotten"))
            {
                affected.Add(new JsonObject { ["id"] = Py.FromDb(row["id"]), ["revision"] = Py.FromDb(row["revision"]) });
            }
        }

        var known = affected.Select(entry => Py.Str(entry["id"])).ToHashSet(StringComparer.Ordinal);
        foreach (var row in memory.Conn.Execute(
                     "SELECT r.memory_id,r.data,m.revision FROM memory_revisions r JOIN memory_records m "
                     + "ON m.id=r.memory_id WHERE m.account=? AND m.status<>'forgotten'", memory.Account).FetchAll())
        {
            var memoryId = Py.Str(row["memory_id"]);
            if (known.Contains(memoryId))
            {
                continue;
            }

            var references = MemoryStore.DictGet(MemoryStore.ParseStored(row["data"]), "source_refs", new JsonArray()) as JsonArray ?? [];
            var restored = references.Any(reference =>
            {
                if (!PyJson.PyEquals(MemoryStore.Item(reference, "kind"), JsonValue.Create("session_checkpoint")))
                {
                    return false;
                }

                var (source, locator) = DreamSources.SourceKeys(reference!.AsObject());
                return ids.Contains(memory.RecordId("user", source)) || ids.Contains(memory.RecordId("user", locator));
            });
            if (restored)
            {
                affected.Add(new JsonObject { ["id"] = Py.FromDb(row["memory_id"]), ["revision"] = Py.FromDb(row["revision"]) });
                known.Add(memoryId);
            }
        }

        var sorted = affected.Select((entry, index) => (entry, index))
                             .OrderBy(pair => Py.Str(pair.entry["id"]), PyRepr.StringComparer).ThenBy(pair => pair.index)
                             .Select(pair => (JsonNode?)pair.entry).ToArray();
        var value = new JsonObject
        {
            ["account"] = memory.Account,
            ["journal_hash"] = Digest(journal),
            ["affected"] = new JsonArray(sorted),
        };
        return Py.DictWith(value, ("subject_id", JsonValue.Create("memory-restore:" + Digest(value))), ("revision", JsonValue.Create(1L)));
    }

    /// <summary>
    /// <c>restore_tombstones(memory, bundle, evidence)</c>: reviewed restore of a deletion journal: erases every resurrected
    /// root named by the preview, then records the tombstones.
    /// </summary>
    public static JsonObject RestoreTombstones(MemoryStore memory, JsonNode? bundle, JsonNode? evidence)
    {
        ArgumentNullException.ThrowIfNull(memory);
        return memory.Transaction(() =>
        {
            var preview = TombstonesPreview(memory, bundle);
            memory.Human(evidence, preview["subject_id"], JsonValue.Create(1L), "restore-erasures");
            var targets = (JsonArray)preview["affected"]!;
            foreach (var target in targets)
            {
                // The reviewed restore names every resurrected root and binds its current revision.
                var confirmation = Py.DictWith(evidence, ("subject_id", Py.Clone(target!["id"])), ("revision", Py.Clone(target["revision"])),
                                               ("decision", JsonValue.Create("forget")),
                                               ("approved_restore_subject", Py.Clone(preview["subject_id"])));
                memory.Forget(target["id"], target["revision"], confirmation);
            }

            var entries = (JsonArray)bundle!["tombstones"]!;
            foreach (var entry in entries)
            {
                memory.Conn.Execute("INSERT OR IGNORE INTO memory_tombstones VALUES(?,?,?,?)", entry!["id"], memory.Account, entry["key_hash"],
                                    entry["forgotten_at"]);
            }

            return new JsonObject
            {
                ["restored_tombstones"] = (long)entries.Count,
                ["erased_roots"] = (long)targets.Count,
                ["outbound_actions"] = false,
            };
        });
    }

    /// <summary>
    /// <c>export_preview(memory, memory_id, recipe)</c>: checks that an active, confirmed, current lesson may be exported
    /// as this sanitised recipe (no identifiers, URLs, paths or credential markers) and returns the reviewed subject.
    /// </summary>
    public static JsonObject ExportPreview(MemoryStore memory, JsonNode? memoryId, JsonNode? recipe)
    {
        ArgumentNullException.ThrowIfNull(memory);
        var row = memory.Show(memoryId);
        if (!PyJson.PyEquals(row["kind"], JsonValue.Create("lesson")) || !PyJson.PyEquals(row["status"], JsonValue.Create("active"))
            || !PyJson.PyEquals(MemoryStore.Item(row, "authority"), JsonValue.Create("user_confirmed")))
        {
            throw new StateError("only an active, explicitly confirmed lesson can be exported");
        }

        if (!memory.WorkSourcesCurrent(row) || PyJson.PyEquals(Py.Get(row, "sensitivity"), JsonValue.Create("sensitive")))
        {
            throw new StateError("lesson is stale, inaccessible or sensitive; export refused");
        }

        var stamp = StateStore.UtcNow();
        if (Compare(Py.Has(row, "valid_from") ? row["valid_from"] : JsonValue.Create(stamp), stamp) > 0
            || Compare(Py.Has(row, "valid_to") ? row["valid_to"] : JsonValue.Create("9999"), stamp) <= 0
            || Compare(Py.Has(row, "review_after") ? row["review_after"] : JsonValue.Create("9999"), stamp) <= 0)
        {
            throw new StateError("lesson validity or review period has expired");
        }

        if (recipe is not JsonObject fields || !SameKeys(fields, RecipeKeys))
        {
            throw new StateError("recipe requires title, goal, preconditions, steps and limitations only");
        }

        foreach (var (_, value) in fields)
        {
            if (Py.AsString(value) is not { } text || PyRepr.Strip(text).Length == 0 || PyRepr.Length(text) > 4000)
            {
                throw new StateError("recipe fields must be nonempty strings of at most 4000 characters");
            }
        }

        var encoded = StateStore.CanonicalJson(fields);
        if (PyPatterns.HasPrivateMarker(encoded))
        {
            throw new StateError("recipe contains an identifier, URL, path or credential marker; replace it with a generic placeholder");
        }

        var preview = new JsonObject
        {
            ["account"] = memory.Account,
            ["memory_id"] = Py.Clone(memoryId),
            ["memory_revision"] = Py.Clone(row["revision"]),
            ["recipe"] = Py.Clone(fields),
        };
        return Py.DictWith(preview, ("subject_id", JsonValue.Create("memory-export:" + Digest(preview))), ("revision", JsonValue.Create(1L)),
                           ("warning", JsonValue.Create(ExportWarning)));
    }

    private static int Compare(JsonNode? value, string stamp) =>
        Py.AsString(value) is { } text
            ? Py.Compare(text, stamp)
            : throw new PyTypeError("'>' not supported between instances of '" + PyJson.TypeName(value) + "' and 'str'");

    /// <summary>
    /// <c>export_recipe(memory, memory_id, recipe, evidence, output)</c>: writes the reviewed recipe to a new private file
    /// (never replacing one: a no-replace hard link of a private staging file). Not a publication.
    /// </summary>
    public static JsonObject ExportRecipe(MemoryStore memory, JsonNode? memoryId, JsonNode? recipe, JsonNode? evidence, string output)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(output);
        return memory.Transaction(() =>
        {
            var preview = ExportPreview(memory, memoryId, recipe);
            memory.Human(evidence, preview["subject_id"], JsonValue.Create(1L), "export");
            var fields = recipe!.AsObject();
            var destination = PyPath.Absolute(PyPath.ExpandUser(output));
            StateStore.NoSymlinks(destination);
            var parent = StateStore.SafeDirectory(PyPath.Parent(destination), explicitOverride: true);
            StateStore.MkdirPrivate(parent);
            StateStore.Private(parent, directory: true);
            var content = new StringBuilder("# ").Append(PyRepr.Strip(Py.AsString(fields["title"])!)).Append("\n\n");
            content.Append(string.Join("\n\n", RecipeSections
                                           .Select(field => "## " + char.ToUpperInvariant(field[0]) + field[1..] + "\n\n" + PyRepr.Strip(Py.AsString(fields[field])!))));
            content.Append('\n');
            var text = content.ToString();
            var bytes = PyIO.EncodeUtf8(text);
            var temporary = CreateTemporary(parent, bytes);
            try
            {
                try
                {
                    PosixNative.Link(temporary, destination);
                }
                catch (PyOSError ex) when (ex.Errno != 0)
                {
                    throw new PyOSErrorTwoPaths(ex.Errno, ex.StrError, temporary, destination);
                }
            }
            finally
            {
                PosixNative.Unlink(temporary);
            }

            return new JsonObject
            {
                ["output"] = destination,
                ["sha256"] = PyIds.Sha256Hex(bytes),
                ["published"] = false,
                ["source_metadata_exported"] = false,
            };
        });
    }

    /// <summary>
    /// <c>tempfile.NamedTemporaryFile(mode="w", dir=parent, prefix=".memory-export-", delete=False)</c> written, flushed and
    /// fsynced: a 0600 file created exclusively under a random name (mkstemp's alphabet and length).
    /// </summary>
    private static string CreateTemporary(string parent, byte[] content)
    {
        for (var attempt = 0; attempt < 10000; attempt++)
        {
            var random = PyRuntime.RandomBytes(8);
            var name = new string(random.Select(b => TempAlphabet[b % TempAlphabet.Length]).ToArray());
            var path = PyPath.Join(parent, ".memory-export-" + name);
            int fd;
            try
            {
                fd = PosixNative.Open(path, 2 | PosixNative.OCreat | PosixNative.OExcl | PosixNative.ONoFollow, Convert.ToUInt32("600", 8));
            }
            catch (PyOSError ex) when (ex.Errno == PyErrno.EEXIST)
            {
                continue;
            }

            try
            {
                PosixNative.WriteAll(fd, content);
                PosixNative.Fsync(fd);
            }
            catch
            {
                PosixNative.Close(fd);
                PosixNative.Unlink(path);
                throw;
            }

            PosixNative.Close(fd);
            return path;
        }

        throw new PyOSError(PyErrno.EEXIST, "No usable temporary file name found");
    }
}

/// <summary>
/// An <c>OSError</c> raised with two file names (<c>os.link(a, b)</c>): <c>[Errno 17] File exists: 'a' -&gt; 'b'</c>.
/// State.Core's <see cref="PosixNative.Link"/> names only the source; this belongs there.
/// </summary>
public sealed class PyOSErrorTwoPaths : PyOSError
{
    public PyOSErrorTwoPaths(int errno, string strerror, string filename, string filename2)
        : base(errno, strerror, filename)
    {
        Filename2 = filename2;
    }

    /// <summary><c>exc.filename2</c>.</summary>
    public string Filename2 { get; }

    public override string Message => "[Errno " + Errno.ToString(System.Globalization.CultureInfo.InvariantCulture) + "] " + StrError + ": "
                                      + PyRepr.Str(Filename!) + " -> " + PyRepr.Str(Filename2);
}

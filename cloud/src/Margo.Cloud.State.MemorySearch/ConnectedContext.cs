using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Memory;
using static Margo.Cloud.State.MemorySearch.MemoryContext;

namespace Margo.Cloud.State.MemorySearch;

/// <summary>
/// Port of <c>memory_context.ConnectedContext</c>: the current eligible records (re-read after eligibility), the
/// mandatory-constraint guard and the bounded breadth-first build over memory links, shared entities, work
/// references and the work ledger. Diagnostics (<see cref="Gaps"/>) never recite withheld content.
/// </summary>
public sealed class ConnectedContext
{
    private static readonly string[] WorkKinds = ["item", "outcome", "meeting"];
    private static readonly string[] WorkFields = ["owner", "due", "effort", "definition_of_done", "next_step", "blocker",
                                                  "scheduled_start", "scheduled_end", "series_id", "occurrence_id", "attendance"];

    private readonly HashSet<string> _gapKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _tables;

    /// <summary>
    /// <c>ConnectedContext(memory, domain=, routine=, usage=, environment=)</c>. <paramref name="usage"/> omitted is
    /// Python's omitted keyword (the store's and the diagnostics' default, "reasoning").
    /// </summary>
    public ConnectedContext(IMemorySource memory, JsonNode? domain = null, JsonNode? routine = null, PyArg usage = default,
                            JsonNode? environment = null)
    {
        Memory = memory ?? throw new ArgumentNullException(nameof(memory));
        Conn = memory.Conn;
        Domain = domain;
        Routine = routine;
        Usage = usage.Or("reasoning");
        Environment = environment;
        foreach (var row in memory.Eligible(domain, routine, Usage, environment))
        {
            var actual = memory.Show(PyNode.Index(row, "id"));
            if (PyNode.Is(PyNode.Index(actual, "status"), "active")
                && PyObj.EqBool(PyNode.Index(actual, "revision"), PyNode.Index(row, "revision")) && Applicable(actual))
            {
                Current[Key(PyNode.Index(actual, "id"))] = actual;
            }
        }

        _tables = Conn.Execute("SELECT name FROM sqlite_master WHERE type='table'").FetchAll()
                      .Select(row => (string)row[0]!).ToHashSet(StringComparer.Ordinal);
    }

    public IMemorySource Memory { get; }

    public PyConnection Conn { get; }

    public JsonNode? Domain { get; }

    public JsonNode? Routine { get; }

    public JsonNode? Usage { get; }

    public JsonNode? Environment { get; }

    /// <summary><c>self.current</c>: id to current record, in eligibility order.</summary>
    public OrderedDictionary<string, JsonObject> Current { get; } = new(StringComparer.Ordinal);

    /// <summary><c>self.gaps</c>: at most 50 distinct diagnostics (the list object the build result shares).</summary>
    public JsonArray Gaps { get; } = [];

    /// <summary><c>self.edges</c>.</summary>
    public List<JsonObject> Edges { get; } = [];

    /// <summary><c>self.conflicted_ids</c>: safety state that never depends on truncated diagnostics.</summary>
    public HashSet<string> ConflictedIds { get; } = new(StringComparer.Ordinal);

    /// <summary><c>self.truncated</c>.</summary>
    public bool Truncated { get; private set; }

    /// <summary><c>self.gap(code, origin=None, **details)</c>.</summary>
    public void Gap(string code, string? origin = null, params (string Key, JsonNode? Value)[] details)
    {
        ArgumentNullException.ThrowIfNull(details);
        var value = new JsonObject { ["code"] = code };
        foreach (var (key, item) in details)
        {
            value[key] = PyNode.Copy(item);
        }

        if (!string.IsNullOrEmpty(origin))
        {
            value["origin_id"] = origin;
        }

        var key2 = StateStore.CanonicalJson(value);
        if (_gapKeys.Add(key2))
        {
            if (Gaps.Count < 50)
            {
                Gaps.Add(value);
            }
            else
            {
                Truncated = true;
            }
        }
    }

    private static string Key(JsonNode? id) =>
        PyNode.AsString(id) ?? throw new PyTypeError("memory ids are strings, not " + PyJson.TypeName(id));

    private static (string, JsonNode?) D(string key, JsonNode? value) => (key, value);

    private static JsonArray Ids(IEnumerable<string> values) => PyNode.StrList(values);

    private static string Other(JsonNode? edge, string identity) =>
        Key(PyNode.Is(PyNode.Index(edge, "source_id"), identity) ? PyNode.Index(edge, "target_id") : PyNode.Index(edge, "source_id"));

    private bool ConstraintScope(JsonNode? row)
    {
        if (PyJson.Truthy(Get(row, "routines")) && !PyObj.Contains(PyNode.Index(row, "routines"), Routine))
        {
            return false;
        }

        if (PyJson.Truthy(Domain) && !PyObj.EqBool(PyNode.Index(row, "domain"), Domain))
        {
            return false;
        }

        var metadata = Get(row, "metadata", new JsonObject());
        var fallback = Get(Get(row, "metadata", new JsonObject()), "environment", new JsonObject());
        var requirements = Get(metadata, "environment_requirements", fallback);

        // Known scope mismatches are inapplicable; missing environment information
        // is uncertainty, not permission to discard an accepted constraint.
        return !Contradicts(requirements, Environment);
    }

    private static bool Contradicts(JsonNode? required, JsonNode? supplied)
    {
        if (required is not JsonObject need || supplied is not JsonObject have)
        {
            return false;
        }

        foreach (var (key, value) in need)
        {
            if (!have.ContainsKey(key))
            {
                continue;
            }

            if (value is JsonObject)
            {
                if (Contradicts(value, have[key]))
                {
                    return true;
                }
            }
            else if (!PyJson.PyEquals(have[key], value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInactive(JsonNode? status, params string[] statuses)
    {
        PyNode.RequireHashable(status);
        return PyNode.IsOneOf(status, statuses);
    }

    private static JsonObject Window(JsonNode? row)
    {
        var window = new JsonObject();
        foreach (var key in new[] { "valid_from", "valid_to" })
        {
            if (PyJson.Truthy(Get(row, key)))
            {
                window[key] = PyNode.Copy(PyNode.Index(row, key));
            }
        }

        return window;
    }

    /// <summary>
    /// <c>constraint_guard()</c>: the number of applicable mandatory constraints that are unavailable or conflicted
    /// (fail closed without reciting excluded constraint text or source details).
    /// </summary>
    public int ConstraintGuard()
    {
        var blocked = 0;
        foreach (var row in Memory.List(Domain))
        {
            if (IsInactive(PyNode.Index(row, "status"), "candidate", "rejected", "suppressed", "forgotten")
                || !MandatoryConstraint(row) || !ConstraintScope(row))
            {
                continue;
            }

            var id = PyNode.Index(row, "id");
            var revision = PyNode.Index(row, "revision");
            if (!Applicable(Window(row)))
            {
                Gap("mandatory_constraint_outside_validity", null, D("record_id", id), D("revision", revision), D("mandatory", true));
                continue;
            }

            if (!Current.ContainsKey(Key(id)))
            {
                var successors = Successors(Key(id));
                if (successors.Count > 0 && successors.All(value => MandatoryConstraint(Current[value])))
                {
                    continue;
                }

                var status = PyNode.Index(row, "status");
                var reason = PyNode.Is(Get(row, "sensitivity"), "sensitive") ? "sensitive_content_withheld"
                    : PyNode.Is(status, "superseded") ? "supersession_unresolved"
                    : "freshness_scope_or_source_unverified";
                var details = new List<(string, JsonNode?)>
                {
                    D("record_id", id), D("revision", revision), D("status", status), D("reason", reason),
                    D("mandatory", true), D("requires_revalidation", true),
                };
                if (PyJson.Truthy(Get(Get(row, "metadata", new JsonObject()), "imported_configuration"))
                    && PyObj.Iterate(Get(row, "source_refs", new JsonArray())).Any(reference => PyNode.Is(PyNode.Index(Node(reference), "kind"), "file"))
                    && !Memory.WorkSourcesCurrent(row))
                {
                    details.Add(D("action", MemoryStore.PreferencesRecovery));
                }

                Gap("mandatory_constraint_unavailable", null, [.. details]);
                blocked++;
                continue;
            }

            var conflict = PyJson.Truthy(Get(Get(row, "metadata", new JsonObject()), "conflicts"));
            foreach (var edge in Memory.Links(id))
            {
                if (!PyNode.Is(PyNode.Index(edge, "relation"), "contradicts") || !Applicable(Get(edge, "data", new JsonObject())))
                {
                    continue;
                }

                var otherId = PyObj.EqBool(PyNode.Index(edge, "source_id"), id) ? PyNode.Index(edge, "target_id") : PyNode.Index(edge, "source_id");
                JsonObject other;
                try
                {
                    other = Memory.Show(otherId);
                }
                catch (Exception ex) when (ex is PyKeyError or StateError)
                {
                    conflict = true;
                    continue;
                }

                if (!IsInactive(PyNode.Index(other, "status"), "forgotten", "suppressed", "rejected", "superseded")
                    && ConstraintScope(other) && Applicable(Window(other)))
                {
                    conflict = true;
                }
            }

            if (conflict)
            {
                Gap("mandatory_constraint_conflict", null, D("record_id", id), D("revision", revision),
                    D("status", PyNode.Index(row, "status")), D("mandatory", true), D("requires_revalidation", true));
                blocked++;
            }
        }

        return blocked;
    }

    /// <summary><c>_diagnose(memory_id, origin=None)</c>: the status of an ineligible memory, or null when it is withheld.</summary>
    public string? Diagnose(string memoryId, string? origin = null)
    {
        JsonObject row;
        try
        {
            row = Memory.Show(memoryId);
        }
        catch (Exception ex) when (ex is PyKeyError or StateError)
        {
            Gap("unavailable_reference", origin);
            return null;
        }

        // Withheld, forgotten and audience-ineligible content is never a diagnostics back door.
        if (IsInactive(Get(row, "status"), "forgotten", "suppressed", "rejected")
            || PyNode.Is(Get(row, "sensitivity"), "sensitive")
            || (PyJson.Truthy(Get(row, "allowed_uses")) && !PyObj.Contains(PyNode.Index(row, "allowed_uses"), Usage))
            || (PyJson.Truthy(Get(row, "routines")) && !PyObj.Contains(PyNode.Index(row, "routines"), Routine))
            || (PyJson.Truthy(Domain) && !PyObj.EqBool(PyNode.Index(row, "domain"), Domain)))
        {
            Gap("unavailable_reference", origin);
            return null;
        }

        var status = PyNode.Index(row, "status");
        Gap("ineligible_memory", origin, D("record_id", PyNode.Index(row, "id")), D("status", status), D("requires_revalidation", true));
        return PyNode.AsString(status);
    }

    /// <summary><c>_successors(memory_id)</c>: current superseding records, following at most two more hops.</summary>
    public List<string> Successors(string memoryId)
    {
        var pending = new List<(string Identity, int Depth)> { (memoryId, 0) };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var found = new List<string>();
        while (pending.Count > 0 && seen.Count < 50)
        {
            var (identity, depth) = pending[^1];
            pending.RemoveAt(pending.Count - 1);
            if (!seen.Add(identity))
            {
                continue;
            }

            List<JsonObject> edges;
            try
            {
                var record = Memory.Show(identity);
                if (!PyNode.IsOneOf(PyNode.Index(record, "status"), "active", "superseded") || PyNode.Is(Get(record, "sensitivity"), "sensitive"))
                {
                    PyNode.RequireHashable(PyNode.Index(record, "status"));
                    continue;
                }

                edges = ActiveEdges(Memory, identity);
                foreach (var link in Memory.Links(identity))
                {
                    if (PyNode.Is(PyNode.Index(link, "relation"), "supersedes") && PyNode.Is(PyNode.Index(link, "target_id"), identity)
                        && !edges.Any(edge => PyJson.PyEquals(edge, link)) && Applicable(Get(link, "data", new JsonObject())))
                    {
                        // Lifecycle retirement may hide an otherwise valid historical edge.
                        // Extra source evidence still needs the store's live verification.
                        if (!PyJson.Truthy(Get(Get(link, "data", new JsonObject()), "source_refs")))
                        {
                            edges.Add((JsonObject)link!);
                        }
                        else
                        {
                            Gap("supersession_evidence_unavailable", identity, D("requires_revalidation", true));
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is PyKeyError or StateError)
            {
                continue;
            }

            foreach (var edge in edges.Take(100))
            {
                if (!PyNode.Is(PyNode.Index(edge, "relation"), "supersedes") || !PyNode.Is(PyNode.Index(edge, "target_id"), identity))
                {
                    continue;
                }

                var source = Key(PyNode.Index(edge, "source_id"));
                if (Current.ContainsKey(source))
                {
                    found.Add(source);
                }
                else if (depth < 2)
                {
                    pending.Add((source, depth + 1));
                }
                else
                {
                    Gap("supersession_limit", memoryId, D("requires_revalidation", true));
                }
            }
        }

        return PyText.Sorted(found.Distinct(StringComparer.Ordinal));
    }

    /// <summary><c>_work(work_id)</c>: the account's work item or record (<c>data</c> parsed), or null.</summary>
    public JsonObject? Work(string workId)
    {
        foreach (var (table, kind) in new[] { ("work_items", "item"), ("work_records", (string?)null) })
        {
            if (!_tables.Contains(table))
            {
                continue;
            }

            var row = Conn.Execute("SELECT * FROM " + table + " WHERE id=? AND account=?", workId, Memory.Account).FetchOne();
            if (row is not null)
            {
                var data = PyNode.Loads(row["data"]);
                var result = PyNode.FromRow(row);
                if (kind is not null)
                {
                    result["kind"] = kind;
                }

                result["data"] = data;
                return result;
            }
        }

        return null;
    }

    private (JsonArray Sources, bool Valid) Sources(JsonNode? refs, string origin)
    {
        var result = new JsonArray();
        var valid = true;
        foreach (var item in PyObj.Iterate(refs))
        {
            var reference = Node(item);
            if (!_tables.Contains("work_sources") || !_tables.Contains("work_source_revisions"))
            {
                Gap("source_unavailable", origin);
                valid = false;
                continue;
            }

            var source = Conn.Execute(@"SELECT s.current_revision,r.fingerprint,r.data
                FROM work_sources s LEFT JOIN work_source_revisions r
                ON s.id=r.source_id AND s.current_revision=r.revision
                WHERE s.id=? AND s.account=?", Get(reference, "source_id"), Memory.Account).FetchOne();
            if (source is null || !PyObj.Truthy(source["data"]))
            {
                Gap("source_unavailable", origin);
                valid = false;
                continue;
            }

            var data = PyNode.Loads(source["data"]);
            var sensitivity = Get(data, "sensitivity", "unknown");
            if (PyNode.AsString(sensitivity) is not { } level || PyRepr.CaseFold(level) is not ("public" or "private" or "none" or "unknown"))
            {
                Gap("source_withheld", origin);
                valid = false;
                continue;
            }

            var current = new JsonObject
            {
                ["source_id"] = PyNode.Copy(PyNode.Index(reference, "source_id")),
                ["revision"] = PyNode.FromSql(source["current_revision"]),
                ["fingerprint"] = PyNode.FromSql(source["fingerprint"]),
                ["web_link"] = PyNode.Copy(Get(data, "web_link")),
            };
            result.Add(current);
            if (PyObj.Ne(source["current_revision"], Get(reference, "revision")) || PyObj.Ne(source["fingerprint"], Get(reference, "fingerprint")))
            {
                Gap("source_changed", origin, D("current_source", current), D("requires_revalidation", true));
                valid = false;
            }
        }

        return (result, valid);
    }

    private static JsonObject WorkRecord(JsonObject row) => new()
    {
        ["store"] = "work",
        ["id"] = PyNode.Copy(row["id"]),
        ["revision"] = PyNode.Copy(row["revision"]),
    };

    private JsonObject? WorkEntry(JsonObject row, JsonArray reasons)
    {
        var data = PyNode.Index(row, "data");
        var kind = PyNode.Index(row, "kind");
        var state = PyNode.Index(row, "state");
        var id = Key(PyNode.Index(row, "id"));
        var (sources, valid) = Sources(Get(data, "source_refs", new JsonArray()), id);
        if (!valid)
        {
            return null;
        }

        if (PyNode.Is(kind, "item") && (!PyJson.Truthy(PyNode.Index(row, "confirmed"))
                                         || !PyNode.IsOneOf(state, "confirmed", "active", "waiting", "deferred")))
        {
            Gap(PyJson.Truthy(row["confirmed"]) ? "work_not_open" : "unconfirmed_work", id, D("state", state), D("source_record", WorkRecord(row)));
            return null;
        }

        if (PyNode.Is(kind, "outcome") && !PyNode.IsOneOf(state, "agreed", "active"))
        {
            Gap("outcome_not_current", id, D("state", state));
            return null;
        }

        if (PyNode.Is(kind, "meeting") && PyNode.Is(state, "cancelled"))
        {
            Gap("meeting_cancelled", id);
            return null;
        }

        if (!PyNode.IsOneOf(kind, WorkKinds))
        {
            Gap("unsupported_work_reference", id);
            return null;
        }

        string[] required = PyNode.Is(kind, "item") ? ["owner", "due"]
            : PyNode.Is(kind, "outcome") ? ["owner", "due", "effort", "definition_of_done"] : [];
        foreach (var field in required)
        {
            if (Get(data, field) is null)
            {
                Gap("unknown_work_field", id, D("field", field));
            }
        }

        var item = new JsonObject
        {
            ["id"] = id,
            ["revision"] = PyNode.Copy(row["revision"]),
            ["kind"] = PyNode.Copy(kind),
            ["domain"] = "work",
            ["title"] = PyNode.Copy(Get(data, "title", PyNode.Copy(kind))),
            ["state"] = PyNode.Copy(state),
            ["authority"] = PyNode.Is(kind, "item") ? "confirmed_work" : PyNode.Is(kind, "outcome") ? "agreed_outcome" : "recorded_meeting",
            ["source_refs"] = sources,
            ["source_record"] = WorkRecord(row),
            ["selection_reasons"] = reasons,
            ["mandatory"] = false,
        };
        var fields = (JsonObject)data!;
        foreach (var field in WorkFields)
        {
            if (fields.ContainsKey(field))
            {
                item[field] = PyNode.Copy(fields[field]);
            }
        }

        if (PyNode.Is(kind, "meeting"))
        {
            // data.get("recap_retry", {}).get("status") in {"blocked", "exhausted"}: set membership hashes the status.
            var recap = Get(Get(data, "recap_retry", new JsonObject()), "status");
            PyNode.RequireHashable(recap);
            if (PyNode.IsOneOf(recap, "blocked", "exhausted"))
            {
                Gap("recap_unavailable", id);
            }
        }

        return item;
    }

    private List<string> WorkIds(JsonObject record)
    {
        var values = Get(Get(record, "metadata", new JsonObject()), "work_refs", new JsonArray());
        var id = Key(PyNode.Index(record, "id"));
        var items = new List<JsonNode?>();
        if (values is JsonArray list)
        {
            items.AddRange(list);
        }
        else
        {
            Gap("invalid_work_references", id);
        }

        foreach (var value in PyObj.Iterate(Get(record, "entities", new JsonArray())))
        {
            var node = Node(value);
            if (PyNode.AsString(node) is not { } text)
            {
                throw new PyAttributeError("'" + PyJson.TypeName(node) + "' object has no attribute 'startswith'");
            }

            if (text.StartsWith("item_", StringComparison.Ordinal) || text.StartsWith("outcome_", StringComparison.Ordinal)
                || text.StartsWith("meeting_", StringComparison.Ordinal))
            {
                items.Add(node);
            }
        }

        var result = new List<string>();
        foreach (var value in items)
        {
            if (PyNode.AsString(value) is { } text)
            {
                result.Add(text);
            }
            else if (value is JsonObject obj && PyNode.AsString(Get(obj, "id")) is { } inner)
            {
                result.Add(inner);
            }
        }

        return result;
    }

    private List<(string Id, string Reason, string? Origin)> RelatedWork(List<JsonObject> records, HashSet<string> entities)
    {
        var owners = new PyDict<JsonNode?>();
        foreach (var row in records)
        {
            foreach (var item in PyObj.Iterate(Get(row, "source_refs", new JsonArray())))
            {
                var reference = Node(item);
                if (PyNode.Is(Get(reference, "kind"), "work_source"))
                {
                    owners.Set(PyNode.Index(reference, "ref"), PyNode.Index(row, "id"));
                }
            }
        }

        var wanted = owners.Items().Select(pair => pair.Key).ToList();
        var found = new List<(string, string, string?)>();
        if (wanted.Count == 0 && entities.Count == 0)
        {
            return found;
        }

        foreach (var table in new[] { "work_items", "work_records" })
        {
            if (!_tables.Contains(table))
            {
                continue;
            }

            var rows = Conn.Execute("SELECT id,data FROM " + table + " WHERE account=? ORDER BY updated_at DESC,id LIMIT 201", Memory.Account).FetchAll();
            if (rows.Count > 200)
            {
                Gap("work_discovery_limit", null, D("requires_revalidation", true));
                Truncated = true;
            }

            foreach (var row in rows.Take(200))
            {
                var data = PyNode.Loads(row["data"]);
                var sources = new PyDict<bool>();
                foreach (var reference in PyObj.Iterate(Get(data, "source_refs", new JsonArray())))
                {
                    sources.Set(Node(Get(Node(reference), "source_id")), true);
                }

                var shared = wanted.Where(key => sources.TryGetValue(key, out _)).ToList();
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var value in PyObj.Iterate(Get(data, "entities", new JsonArray())))
                {
                    if (MemoryStore.ScopedEntityKey(Node(value)) is { } key)
                    {
                        keys.Add(key);
                    }
                }

                // A plain owner label is not a provider-scoped identity.
                var rowId = (string)row["id"]!;
                if (shared.Count > 0 || entities.Overlaps(keys) || entities.Contains(rowId))
                {
                    string? origin = null;
                    if (shared.Count > 0)
                    {
                        var first = PyText.Sorted(shared.Select(key => Key(Node(key))))[0];
                        owners.TryGetValue(first, out var owner);
                        origin = Key(owner);
                    }

                    found.Add((rowId, shared.Count > 0 ? "shared_source" : "exact_entity", origin));
                }
            }
        }

        return found;
    }

    private string? Decision(JsonNode? reference, string origin)
    {
        var canonicalId = Get(reference, "canonical_id");
        var candidates = Current.Values.Where(row => PyNode.Is(PyNode.Index(row, "kind"), "decision")
                                                     && (PyObj.EqBool(PyNode.Index(row, "id"), canonicalId)
                                                         || PyObj.EqBool(Get(Get(row, "metadata", new JsonObject()), "canonical_id"), canonicalId)))
                                       .Where(row => PyNode.Is(PyNode.Index(row, "authority"), "user_confirmed")).ToList();
        if (candidates.Count == 0 && PyNode.AsString(canonicalId) is { } canonical)
        {
            candidates = Successors(canonical).Select(identity => Current[identity])
                .Where(row => PyNode.Is(row["kind"], "decision") && PyNode.Is(row["authority"], "user_confirmed")).ToList();
            if (candidates.Count > 0)
            {
                Gap("decision_reference_superseded", origin, D("canonical_id", canonicalId),
                    D("replacement_ids", new JsonArray(candidates.Select(row => PyNode.Copy(row["id"])).ToArray())));
            }
        }

        if (!PyNode.Is(Get(reference, "status"), "current") || candidates.Count != 1)
        {
            if (candidates.Count > 1)
            {
                ConflictedIds.UnionWith(candidates.Select(row => Key(row["id"])));
            }

            Gap("decision_" + (candidates.Count > 1 ? "conflict" : "unresolved"), origin, D("canonical_id", canonicalId),
                D("reported_status", Get(reference, "status")), D("web_link", Get(reference, "web_link")), D("requires_revalidation", true));
            return null;
        }

        return Key(candidates[0]["id"]);
    }

    /// <summary>
    /// A JSON node has one parent, so a result from an earlier <see cref="Build"/> (or whatever the caller moved its
    /// gaps into) keeps a copy of the gaps as they stood, and the live list moves to the new result. Python's results
    /// all share one list; the earlier one is the only difference.
    /// </summary>
    private void ReleaseGaps()
    {
        switch (Gaps.Parent)
        {
            case JsonObject owner:
                foreach (var key in owner.Where(pair => ReferenceEquals(pair.Value, Gaps)).Select(pair => pair.Key).ToList())
                {
                    owner[key] = PyNode.Copy(Gaps);
                }

                break;
            case JsonArray list:
                list[list.IndexOf(Gaps)] = PyNode.Copy(Gaps);
                break;
        }
    }

    private sealed class Selection(int depth, JsonArray reasons)
    {
        public int Depth { get; } = depth;

        public JsonArray Reasons { get; } = reasons;
    }

    /// <summary>
    /// <c>build(seed_ids, *, entities=None, work_ids=None, max_depth=2, max_nodes=30)</c>. The result's <c>gaps</c> is
    /// <see cref="Gaps"/> itself, so later diagnostics (such as <see cref="Diagnose"/>) appear in it, as in Python.
    /// </summary>
    public JsonObject Build(IEnumerable<string> seedIds, IEnumerable<string>? entities = null, IEnumerable<string>? workIds = null,
                            PyArg maxDepth = default, PyArg maxNodes = default)
    {
        ArgumentNullException.ThrowIfNull(seedIds);
        var (depthLimit, nodeLimit) = Bounds(maxDepth.Or(2L), maxNodes.Or(30L));
        var wanted = new HashSet<string>(entities ?? [], StringComparer.Ordinal);
        var seeds = seedIds.Distinct(StringComparer.Ordinal).ToList();
        foreach (var identity in seeds.ToList())
        {
            if (!Current.ContainsKey(identity))
            {
                var successors = Successors(identity);
                if (successors.Count > 0)
                {
                    Gap("memory_superseded", null, D("record_id", identity), D("replacement_ids", Ids(successors)));
                    seeds.AddRange(successors.Where(value => !seeds.Contains(value)));
                }
            }
        }

        foreach (var row in Current.Values)
        {
            var id = Key(row["id"]);
            if (wanted.Contains(id) || wanted.Overlaps(EntityKeys(row)))
            {
                if (!seeds.Contains(id))
                {
                    seeds.Add(id);
                }
            }
        }

        if (wanted.Count > 0)
        {
            foreach (var row in Memory.List(Domain))
            {
                var id = Key(PyNode.Index(row, "id"));
                if (!Current.ContainsKey(id) && (wanted.Contains(id) || wanted.Overlaps(EntityKeys(row))))
                {
                    var successors = Successors(id);
                    if (successors.Count > 0)
                    {
                        Gap("memory_superseded", null, D("record_id", id), D("replacement_ids", Ids(successors)));
                        foreach (var value in successors)
                        {
                            if (!seeds.Contains(value))
                            {
                                seeds.Add(value);
                            }
                        }
                    }
                    else
                    {
                        Diagnose(id);
                    }
                }
            }
        }

        var nodes = new OrderedDictionary<string, JsonObject>(StringComparer.Ordinal);
        var selected = new Dictionary<string, Selection>(StringComparer.Ordinal);
        var queue = new Queue<string>();

        void Enqueue(string identity, int depth, string reason, string? origin = null, string? relation = null)
        {
            if (selected.TryGetValue(identity, out var existing))
            {
                if (!existing.Reasons.Any(value => PyNode.Is(value, reason)))
                {
                    existing.Reasons.Add(reason);
                }

                if (!string.IsNullOrEmpty(origin) && Edges.Count < nodeLimit * 2)
                {
                    Edges.Add(new JsonObject { ["source_id"] = origin, ["target_id"] = identity, ["relation"] = relation, ["selection_reason"] = reason });
                }

                return;
            }

            if (depth > depthLimit || selected.Count >= nodeLimit)
            {
                Truncated = true;
                return;
            }

            selected[identity] = new Selection(depth, [reason]);
            queue.Enqueue(identity);
            if (!string.IsNullOrEmpty(origin))
            {
                Edges.Add(new JsonObject { ["source_id"] = origin, ["target_id"] = identity, ["relation"] = relation, ["selection_reason"] = reason });
            }
        }

        foreach (var identity in seeds)
        {
            if (Current.TryGetValue(identity, out var row))
            {
                Enqueue(identity, 0, wanted.Contains(identity) || wanted.Overlaps(EntityKeys(row)) ? "exact_entity" : "retrieval");
            }
        }

        foreach (var identity in workIds ?? [])
        {
            Enqueue(identity, 0, "explicit_work_reference");
        }

        var selectedRecords = seeds.Where(Current.ContainsKey).Select(identity => Current[identity]).ToList();
        foreach (var (identity, reason, origin) in RelatedWork(selectedRecords, wanted))
        {
            Enqueue(identity, origin is not null ? 1 : 0, reason, origin, "shared_source");
        }

        while (queue.Count > 0)
        {
            var identity = queue.Dequeue();
            var depth = selected[identity].Depth;
            var reasons = selected[identity].Reasons;
            if (Current.TryGetValue(identity, out var row))
            {
                if (PyNode.Is(row["kind"], "decision") && !PyNode.Is(row["authority"], "user_confirmed"))
                {
                    Gap("unconfirmed_decision", identity);
                    continue;
                }

                nodes[identity] = MemoryEntry(row, reasons);
                if (PyNode.IsOneOf(row["kind"], "person", "project") && EntityKeys(row).Count == 0)
                {
                    Gap("unresolved_identity", identity);
                }

                foreach (var key in new[] { "conflicts", "gaps", "coverage" })
                {
                    if (PyJson.Truthy(Get(Get(row, "metadata", new JsonObject()), key)))
                    {
                        if (key == "conflicts")
                        {
                            ConflictedIds.Add(identity);
                        }

                        Gap("reported_" + key, identity, D("evidence", PyNode.Index(PyNode.Index(row, "metadata"), key)), D("requires_revalidation", true));
                    }
                }

                var available = ActiveEdges(Memory, identity);
                foreach (var edge in Memory.Links(identity))
                {
                    var other = Other(edge, identity);
                    if (!Applicable(Get(edge, "data", new JsonObject())))
                    {
                        Gap("relationship_outside_validity", identity, D("relation", PyNode.Index(edge, "relation")));
                    }
                    else if (!Current.ContainsKey(other))
                    {
                        var successors = Successors(other);
                        if (successors.Count > 0)
                        {
                            Gap("memory_superseded", identity, D("record_id", other), D("replacement_ids", Ids(successors)));
                            foreach (var successor in successors)
                            {
                                Enqueue(successor, depth + 1, "current_superseding_memory", identity, "about");
                            }
                        }
                        else
                        {
                            var status = Diagnose(other, identity);
                            if (PyNode.Is(PyNode.Index(edge, "relation"), "contradicts") && status == "disputed")
                            {
                                ConflictedIds.Add(identity);
                                ConflictedIds.Add(other);
                                Gap("conflicting_memories", identity, D("record_ids", Ids(PyText.Sorted([identity, other]))),
                                    D("counterpart_status", "disputed"), D("requires_revalidation", true));
                            }
                        }
                    }
                    else if (!available.Any(value => PyJson.PyEquals(value, edge)))
                    {
                        Gap("relationship_evidence_unavailable", identity, D("relation", PyNode.Index(edge, "relation")));
                    }
                }

                foreach (var edge in available)
                {
                    var other = Other(edge, identity);
                    if (!Current.ContainsKey(other))
                    {
                        continue;
                    }

                    var relation = PyNode.Index(edge, "relation");
                    if (PyNode.Is(relation, "contradicts"))
                    {
                        ConflictedIds.Add(identity);
                        ConflictedIds.Add(other);
                        Gap("conflicting_memories", identity, D("record_ids", Ids(PyText.Sorted([identity, other]))), D("requires_revalidation", true));
                    }

                    Enqueue(other, depth + 1, "relationship:" + PyObj.StrOperand(relation));
                    if (Edges.Count < nodeLimit * 2)
                    {
                        var value = new JsonObject
                        {
                            ["source_id"] = PyNode.Copy(edge["source_id"]),
                            ["target_id"] = PyNode.Copy(edge["target_id"]),
                            ["relation"] = PyNode.Copy(relation),
                            ["source_refs"] = PyNode.Copy(Get(Get(edge, "data", new JsonObject()), "source_refs", new JsonArray())),
                        };
                        if (!Edges.Any(existing => PyJson.PyEquals(existing, value)))
                        {
                            Edges.Add(value);
                        }
                    }
                    else
                    {
                        Truncated = true;
                    }
                }

                if (depth < depthLimit)
                {
                    var keys = EntityKeys(row);
                    foreach (var other in Current.Values.ToList())
                    {
                        var otherId = Key(other["id"]);
                        if (otherId != identity && keys.Overlaps(EntityKeys(other)))
                        {
                            Enqueue(otherId, depth + 1, "exact_entity", identity, "shared_entity");
                        }
                    }

                    foreach (var workId in WorkIds(row))
                    {
                        Enqueue(workId, depth + 1, "memory_work_reference", identity, "about");
                    }
                }
                else if (WorkIds(row).Count > 0)
                {
                    Truncated = true;
                }
            }
            else
            {
                var work = Work(identity);
                if (work is null)
                {
                    Gap("work_reference_unavailable", identity);
                    continue;
                }

                var item = WorkEntry(work, reasons);
                if (item is null)
                {
                    continue;
                }

                nodes[identity] = item;
                var data = work["data"];
                var workKind = work["kind"];
                var targets = new List<(string Target, string Relation)>();
                foreach (var value in PyObj.Iterate(Get(data, "work_item_ids", new JsonArray())))
                {
                    targets.Add((Key(Node(value)), PyNode.Is(workKind, "outcome") ? "advanced_by" : "discusses"));
                }

                if (PyJson.Truthy(Get(data, "outcome_id")))
                {
                    targets.Add((Key(PyNode.Index(data, "outcome_id")), "advances"));
                }

                foreach (var reference in PyObj.Iterate(Get(data, "decision_refs", new JsonArray())))
                {
                    if (Decision(Node(reference), identity) is { Length: > 0 } decision)
                    {
                        targets.Add((decision, "discusses"));
                    }
                }

                if (PyNode.Is(workKind, "item") && _tables.Contains("work_relationships"))
                {
                    var edges = Conn.Execute(@"SELECT source_id,target_id,kind FROM work_relationships
                        WHERE source_id=? OR target_id=? ORDER BY kind,source_id,target_id LIMIT ?", identity, identity, (long)(nodeLimit * 2 + 1)).FetchAll();
                    if (edges.Count > nodeLimit * 2)
                    {
                        Truncated = true;
                    }

                    foreach (var edge in edges.Take(nodeLimit * 2))
                    {
                        var target = (string)edge["target_id"]!;
                        var source = (string)edge["source_id"]!;
                        var other = source == identity ? target : source;
                        var kind = (string)edge["kind"]!;
                        if (kind == "tracked_in")
                        {
                            continue;
                        }

                        if (kind == "supersedes" && target == identity)
                        {
                            var newer = Work(other);
                            if (newer is not null && PyJson.Truthy(PyNode.Get(newer, "confirmed")))
                            {
                                nodes.Remove(identity);
                                Gap("work_superseded", identity, D("replacement_id", other));
                            }
                        }

                        var relation = kind;
                        if (target == identity)
                        {
                            relation = kind switch
                            {
                                "blocks" => "blocked_by",
                                "depends_on" => "dependency_of",
                                "supersedes" => "superseded_by",
                                _ => kind,
                            };
                        }

                        targets.Add((other, relation));
                    }
                }

                foreach (var (target, relation) in targets)
                {
                    Enqueue(target, depth + 1, "work_relationship:" + relation, identity, relation);
                }
            }
        }

        // Contradictions are evidence, never an authoritative current decision.
        foreach (var identity in nodes.Keys.ToList())
        {
            if (PyNode.Is(nodes[identity]["kind"], "decision") && ConflictedIds.Contains(identity))
            {
                nodes.Remove(identity);
            }
            else if (ConflictedIds.Contains(identity))
            {
                nodes[identity]["conflicted"] = true;
            }
        }

        var canonical = new PyDict<List<string>>();
        foreach (var row in Current.Values)
        {
            var key = Get(Get(row, "metadata", new JsonObject()), "canonical_id");
            if (PyNode.Is(row["kind"], "decision") && PyJson.Truthy(key) && PyNode.Is(row["authority"], "user_confirmed"))
            {
                canonical.SetDefault(key, []).Add(Key(row["id"]));
            }
        }

        foreach (var (key, identities) in canonical.Items())
        {
            if (identities.Count > 1 && identities.Any(nodes.ContainsKey))
            {
                ConflictedIds.UnionWith(identities);
                Gap("decision_conflict", null, D("canonical_id", (JsonNode?)key), D("record_ids", Ids(identities)), D("requires_revalidation", true));
                foreach (var identity in identities)
                {
                    nodes.Remove(identity);
                }
            }
        }

        var reverse = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["advanced_by"] = "advances", ["blocked_by"] = "blocks", ["dependency_of"] = "depends_on", ["superseded_by"] = "supersedes",
        };
        var edgesOut = new List<JsonObject>();
        foreach (var original in Edges)
        {
            var edge = original;
            if (!nodes.ContainsKey(Key(edge["source_id"])) || !nodes.ContainsKey(Key(edge["target_id"])))
            {
                continue;
            }

            if (PyNode.AsString(edge["relation"]) is { } relation && reverse.TryGetValue(relation, out var reversed))
            {
                edge = PyNode.CopyObject(edge);
                edge["source_id"] = PyNode.Copy(original["target_id"]);
                edge["target_id"] = PyNode.Copy(original["source_id"]);
                edge["relation"] = reversed;
            }

            if (!edgesOut.Any(existing => PyJson.PyEquals(existing, edge)))
            {
                edgesOut.Add(edge);
            }
        }

        if (edgesOut.Count > nodeLimit * 2)
        {
            Truncated = true;
            edgesOut = edgesOut.Take(nodeLimit * 2).ToList();
        }

        foreach (var (identity, node) in nodes)
        {
            node["depth"] = (long)selected[identity].Depth;
        }

        ReleaseGaps();
        return new JsonObject
        {
            ["nodes"] = new JsonArray([.. nodes.Values]),
            ["edges"] = new JsonArray([.. edgesOut.Select(edge => PyNode.Copy(edge))]),
            ["gaps"] = Gaps,
            ["truncated"] = Truncated,
            ["max_depth"] = (long)depthLimit,
            ["max_nodes"] = (long)nodeLimit,
            ["conflicted_count"] = (long)ConflictedIds.Count,
        };
    }
}

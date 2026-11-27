using System.Numerics;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Memory;

namespace Margo.Cloud.State.MemoryLearning;

/// <summary>
/// Port of <c>skills/chief-of-staff/scripts/memory_consolidation.py</c>: bounded, deterministic review proposals over
/// one page of memories, and minimal once-per-evidence delivery receipts. Nothing is merged, activated, retired or
/// rewritten; only hashes and IDs are persisted.
/// </summary>
public static class MemoryConsolidation
{
    /// <summary>The module docstring.</summary>
    public const string Doc = "Bounded deterministic review proposals and minimal, once-per-evidence delivery receipts.";

    private static readonly string[] EnvironmentKeys = ["host", "account"];
    private static readonly IReadOnlySet<string> ReviewableStatuses = new HashSet<string>(StringComparer.Ordinal) { "candidate", "active" };
    private static readonly string[] ParameterKeys = ["limit", "environment", "after", "scan_limit"];

    /// <summary><c>_stored(memory, key)</c>: the JSON value of a <c>memory_meta</c> row, or null.</summary>
    public static JsonNode? Stored(MemoryStore memory, string key)
    {
        ArgumentNullException.ThrowIfNull(memory);
        var row = memory.Conn.Execute("SELECT value FROM memory_meta WHERE key=?", key).FetchOne();
        return row is null ? null : PyNode.Loads(row[0]);
    }

    /// <summary>
    /// <c>_sources(memory, record, seen=None, budget=None, depth=0)</c>: a snapshot of what each source reference of
    /// <paramref name="record"/> currently resolves to, recursing through memory parents with a shared budget of 200
    /// references and a depth limit of 10.
    /// </summary>
    public static JsonArray Sources(MemoryStore memory, JsonNode? record, IEnumerable<string>? seen = null, long[]? budget = null, int depth = 0)
    {
        ArgumentNullException.ThrowIfNull(memory);
        var visited = new HashSet<string>(seen ?? [], StringComparer.Ordinal);
        budget ??= [200];
        var identity = PyNode.Index(record, "id");
        PyNode.RequireHashable(identity);
        if ((PyNode.AsString(identity) is { } id && visited.Contains(id)) || depth >= 10)
        {
            return new JsonArray("dependency_cycle_or_depth_limit");
        }

        visited.Add(PyNode.AsString(identity) ?? PyNode.Str(identity));
        var result = new JsonArray();
        var refs = PyNode.Get(PyNode.Dict(record), "source_refs", new JsonArray());
        if (PyObj.Len(refs) > 200)
        {
            return new JsonArray("source_budget_exceeded");
        }

        foreach (var reference in PyNode.Iter(refs))
        {
            budget[0] -= 1;
            if (budget[0] < 0)
            {
                result.Add("source_budget_exceeded");
                break;
            }

            JsonNode? observed;
            var kind = PyNode.Index(reference, "kind");
            if (PyNode.Is(kind, "memory_record"))
            {
                try
                {
                    var current = memory.Show(PyNode.Index(reference, "ref"));
                    var stamp = JsonValue.Create(StateStore.UtcNow());
                    var available = !PyJson.PyEquals(PyNode.Get(current, "authority"), "inferred")
                                    && !PyJson.PyEquals(PyNode.Get(current, "sensitivity"), "sensitive")
                                    && PyObj.Le(PyNode.Get(current, "valid_from", stamp), stamp)
                                    && PyObj.Gt(PyNode.Get(current, "valid_to", "9999"), stamp)
                                    && PyObj.Gt(PyNode.Get(current, "review_after", "9999"), stamp);
                    observed = new JsonArray(
                        PyNode.Copy(current["id"]), PyNode.Copy(current["revision"]), PyNode.Copy(current["status"]), available,
                        Sources(memory, current, visited, budget, depth + 1));
                }
                catch (StateError)
                {
                    observed = new JsonArray("unavailable");
                }
            }
            else if (PyNode.Is(kind, "file"))
            {
                try
                {
                    var (_, source) = MemoryLearning.File(PyNode.Index(reference, "ref"));
                    observed = PyNode.Copy(source["revision"]);
                }
                catch (PyValueError)
                {
                    observed = "unavailable";
                }
            }
            else if (PyNode.Is(kind, "work_source"))
            {
                observed = memory.WorkSourcesCurrent(new JsonObject { ["source_refs"] = new JsonArray(PyNode.Copy(reference)) });
            }
            else
            {
                observed = PyNode.Copy(PyNode.Get(PyNode.Dict(reference), "revision"));
            }

            result.Add(new JsonArray(PyNode.Copy(reference), observed));
        }

        return result;
    }

    /// <summary><c>_current_sources(snapshot)</c>: whether every reference in a <see cref="Sources"/> snapshot is current.</summary>
    public static bool CurrentSources(JsonNode? snapshot)
    {
        foreach (var entry in PyNode.Iter(snapshot))
        {
            if (entry is not JsonArray pair || pair.Count != 2 || pair[0] is not JsonObject reference)
            {
                return false;
            }

            var observed = pair[1];
            var kind = PyNode.Index(reference, "kind");
            if (PyNode.Is(kind, "memory_record")
                && (observed is not JsonArray parent || parent.Count != 5
                    || !PyJson.PyEquals(JsonValue.Create(PyNode.Str(parent[1])), PyNode.Get(reference, "revision"))
                    || !PyJson.PyEquals(parent[2], "active")
                    || !PyJson.Truthy(parent[3]) || !CurrentSources(parent[4])))
            {
                return false;
            }

            if (PyNode.Is(kind, "file") && !PyJson.PyEquals(observed, PyNode.Get(reference, "revision")))
            {
                return false;
            }

            if (PyNode.Is(kind, "work_source") && !(PyJson.IsBool(observed) && PyJson.Truthy(observed)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// <c>_trend_evidence(record)</c>: the digest of a trend's material facts (definition, window, coverage, population,
    /// rate and independent outcomes), so wording-only changes are not resurfaced.
    /// </summary>
    public static string TrendEvidence(JsonNode? record)
    {
        var metadata = PyNode.Dict(PyNode.Index(record, "metadata"));
        var outcomes = new List<(JsonNode? Key, JsonNode? Matched)>();
        foreach (var entry in PyNode.Iter(PyNode.Get(metadata, "resolved_events", new JsonArray())))
        {
            var key = PyNode.Index(entry, "independence_key");
            var matched = PyNode.Index(entry, "matched");
            PyNode.RequireHashable(key);
            PyNode.RequireHashable(matched);
            if (!outcomes.Any(pair => PyJson.PyEquals(pair.Key, key) && PyJson.PyEquals(pair.Matched, matched)))
            {
                outcomes.Add((key, matched));
            }
        }

        var sorted = PySort.SortedBy(outcomes, pair => (IReadOnlyList<object?>)[pair.Key, pair.Matched], PyObj.TupleLt);
        JsonNode? Field(string name) => PyNode.Copy(PyNode.Get(metadata, name));
        return MemoryLearning.Digest(new JsonArray("trend_review", new JsonObject
        {
            ["trend_type"] = Field("trend_type"),
            ["definition"] = Field("definition"),
            ["definition_revision"] = Field("definition_revision"),
            ["window_start"] = Field("window_start"),
            ["window_end"] = Field("window_end"),
            ["coverage"] = Field("coverage"),
            ["coverage_fraction"] = Field("coverage_fraction"),
            ["expected_population"] = Field("expected_population"),
            ["population"] = Field("population"),
            ["observations"] = Field("observations"),
            ["rate"] = Field("rate"),
            ["independent_event_ids"] = PyNode.Copy(PyNode.Get(metadata, "independent_event_ids", new JsonArray())),
            ["independent_outcomes"] = new JsonArray(sorted.Select(pair => (JsonNode?)new JsonArray(PyNode.Copy(pair.Key), PyNode.Copy(pair.Matched))).ToArray()),
        }));
    }

    /// <summary>
    /// <c>_proposal(memory, reason, records, environment)</c>: one review proposal for exact records, or null when
    /// this evidence was already delivered for the proposal and for every target.
    /// </summary>
    public static JsonObject? Proposal(MemoryStore memory, string reason, IReadOnlyList<JsonObject> records, JsonNode? environment)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(records);
        var refs = PySort.SortedBy(records.Select(row => new JsonObject { ["id"] = PyNode.Copy(row["id"]), ["revision"] = PyNode.Copy(row["revision"]) }),
                                   row => (object?)PyJson.Scalar(row["id"]), PyObj.Lt);
        var identity = "learning-proposal:" + MemoryLearning.Digest(new JsonArray(
            reason, new JsonArray(refs.Select(row => PyNode.Copy(row["id"])).ToArray())));
        var trend = reason == "review_trend";
        List<string> evidence;
        var targetEvidence = new List<(JsonNode? Id, List<string> Evidence)>();
        if (trend)
        {
            evidence = [TrendEvidence(records[0])];
            targetEvidence.Add((records[0]["id"], evidence));
        }
        else
        {
            evidence = records.Select(row => MemoryLearning.Digest(new JsonArray(
                MemoryLearning.Data(row), Sources(memory, row), PyNode.Copy(environment)))).ToList();
            targetEvidence.AddRange(records.Zip(evidence, (row, token) => (row["id"], new List<string> { token })));
        }

        if (evidence.Count == 0)
        {
            return null;
        }

        var prior = Stored(memory, "learning:surfaced:" + identity);
        if (PyJson.Truthy(prior))
        {
            var known = StringSet(PyNode.Index(prior, "evidence"));
            if (trend ? known.SetEquals(evidence) : evidence.All(known.Contains))
            {
                return null;
            }
        }

        var unseen = false;
        foreach (var (id, tokens) in targetEvidence)
        {
            var stored = Stored(memory, "learning:target-surfaced:" + PyNode.Str(id));
            if (!unseen)
            {
                var previous = PyJson.Truthy(stored) ? StringSet(PyNode.Index(stored, "evidence")) : [];
                unseen = trend ? !previous.SetEquals(tokens) : !tokens.All(previous.Contains);
            }
        }

        if (!unseen)
        {
            return null;
        }

        return new JsonObject
        {
            ["id"] = identity,
            ["reason"] = reason,
            ["targets"] = new JsonArray(refs.Select(row => (JsonNode?)row).ToArray()),
            ["source_fingerprint"] = MemoryLearning.Digest(new JsonArray(records.Select(row => (JsonNode?)Sources(memory, row)).ToArray())),
            ["evidence"] = PyNode.StrList(PyText.Sorted(evidence.Distinct(StringComparer.Ordinal))),
            ["target_evidence"] = new JsonArray(targetEvidence.Select(target => (JsonNode?)new JsonObject
            {
                ["id"] = PyNode.Copy(target.Id),
                ["evidence"] = PyNode.StrList(target.Evidence),
            }).ToArray()),
            ["requires_review"] = true,
            ["action"] = "Review exact records; no merge, activation, retirement or permission change is performed.",
        };
    }

    /// <summary><c>set(values)</c> of stored evidence tokens (hex digests).</summary>
    private static HashSet<string> StringSet(JsonNode? values)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in PyNode.Iter(values))
        {
            PyNode.RequireHashable(value);
            result.Add(PyNode.AsString(value) ?? "\u0000" + PyNode.Repr(value));
        }

        return result;
    }

    /// <summary>
    /// <c>consolidation_plan(memory, limit=10, environment=None, after=None, scan_limit=100)</c>: inspect one bounded page
    /// and propose review; never rewrite or promote memories. Host/account mismatches are excluded; duplicate detection is
    /// exact and page-local. The plan carries its own <c>subject_id</c> digest.
    /// </summary>
    public static JsonObject ConsolidationPlan(MemoryStore memory, PyArg limit = default, JsonNode? environment = null, JsonNode? after = null,
                                               PyArg scanLimit = default)
    {
        ArgumentNullException.ThrowIfNull(memory);
        var pageLimit = limit.Or(10);
        var scan = scanLimit.Or(100);
        if (!PyJson.IsInt(pageLimit) || PyJson.AsInt(pageLimit) < 1 || PyJson.AsInt(pageLimit) > 50)
        {
            throw new StateError("consolidation limit must be between 1 and 50");
        }

        if (!PyJson.IsInt(scan) || PyJson.AsInt(scan) < 1 || PyJson.AsInt(scan) > 200)
        {
            throw new StateError("consolidation scan_limit must be between 1 and 200");
        }

        if (after is not null && !PyJson.IsStr(after))
        {
            throw new StateError("consolidation cursor must be a string");
        }

        if (environment is not null && (environment is not JsonObject requested || !PyJson.PyEquals(PyNode.Get(requested, "account"), memory.Account)))
        {
            throw new StateError("consolidation environment must name this account");
        }

        var maximum = (int)PyJson.AsInt(pageLimit);
        var scanCount = (int)PyJson.AsInt(scan);
        return memory.Transaction(() =>
        {
            var rows = memory.Conn.Execute(
                "SELECT id FROM memory_records WHERE account=? AND id>? "
                + "AND status IN ('candidate','active') ORDER BY id LIMIT ?",
                memory.Account, PyJson.Truthy(after) ? after : "", scanCount + 1).FetchAll();
            var scanned = rows.Take(scanCount).Select(row => memory.Show(PyNode.FromSql(row["id"]))).ToList();
            var groups = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
            foreach (var record in scanned)
            {
                var requirements = Requirements(record);
                if (Excluded(requirements, environment))
                {
                    continue;
                }

                var signature = Signature(record, requirements);
                if (!groups.TryGetValue(signature, out var members))
                {
                    groups[signature] = members = [];
                }

                members.Add(record);
            }

            var items = new JsonArray();
            var handled = new HashSet<string>(StringComparer.Ordinal);
            var last = PyNode.Copy(after);
            foreach (var record in scanned)
            {
                last = PyNode.Copy(record["id"]);
                var id = PyNode.AsString(record["id"])!;
                if (handled.Contains(id))
                {
                    continue;
                }

                var requirements = Requirements(record);
                if (Excluded(requirements, environment))
                {
                    continue;
                }

                var signature = Signature(record, requirements);
                var duplicates = (groups.TryGetValue(signature, out var members) ? members : [])
                    .Where(row => PyNode.Is(row["status"], "candidate")).ToList();
                JsonObject? proposal = null;
                if (duplicates.Count > 1)
                {
                    proposal = Proposal(memory, "review_exact_duplicates", duplicates, environment);
                    foreach (var row in duplicates)
                    {
                        handled.Add(PyNode.AsString(row["id"])!);
                    }
                }
                else if (PyNode.Is(record["kind"], "lesson")
                         && (!CurrentSources(Sources(memory, record))
                             || (environment is not null && !memory.EnvironmentSubset(requirements, environment))))
                {
                    proposal = Proposal(memory, "revalidate_lesson", [record], environment);
                }
                else if (PyNode.Is(record["kind"], "capability")
                         && PyJson.PyEquals(PyNode.Get(PyNode.Dict(PyNode.Get(record, "metadata", new JsonObject())), "state"), "degraded"))
                {
                    proposal = Proposal(memory, "review_capability_failure", [record], environment);
                }
                else if (PyNode.Is(record["status"], "candidate"))
                {
                    var reason = PyNode.Is(record["kind"], "trend") ? "review_trend" : "review_candidate";
                    proposal = Proposal(memory, reason, [record], environment);
                }

                if (proposal is not null)
                {
                    items.Add(proposal);
                    if (items.Count >= maximum)
                    {
                        break;
                    }
                }
            }

            var lastId = PyNode.AsString(last);
            var manifest = new JsonObject
            {
                ["account"] = memory.Account,
                ["items"] = items,
                ["parameters"] = new JsonObject
                {
                    ["limit"] = PyNode.Copy(pageLimit),
                    ["environment"] = PyNode.Copy(environment),
                    ["after"] = PyNode.Copy(after),
                    ["scan_limit"] = PyNode.Copy(scan),
                },
                ["scanned"] = PyJson.Truthy(last)
                    ? scanned.Count(record => PyRepr.CompareStrings(PyNode.AsString(record["id"]), lastId) <= 0)
                    : 0,
                ["next_after"] = rows.Count > 0 && (rows.Count > scanCount || lastId != PyNode.AsString(scanned[^1]["id"]))
                    ? PyNode.Copy(last)
                    : null,
                ["deduplication_scope"] = "exact_content_within_bounded_page_only",
            };
            return PyNode.DictWith(manifest, ("subject_id", "consolidation:" + MemoryLearning.Digest(manifest)), ("revision", 1));
        });
    }

    private static JsonNode? Requirements(JsonObject record) =>
        PyNode.Get(PyNode.Dict(PyNode.Get(record, "metadata", new JsonObject())), "environment_requirements", new JsonObject());

    /// <summary>A record pinned to another host or account than <paramref name="environment"/>.</summary>
    private static bool Excluded(JsonNode? requirements, JsonNode? environment)
    {
        if (environment is null)
        {
            return false;
        }

        var requested = (JsonObject)environment;
        var pinned = PyNode.Dict(requirements);
        return EnvironmentKeys.Any(key => PyJson.Truthy(PyNode.Get(pinned, key))
                                          && !PyJson.PyEquals(pinned[key], PyNode.Get(requested, key)));
    }

    private static string Signature(JsonObject record, JsonNode? requirements) => MemoryLearning.Digest(new JsonArray(
        PyNode.Copy(PyNode.Index(record, "domain")), PyNode.Copy(PyNode.Index(record, "kind")), PyNode.Copy(PyNode.Get(record, "scope")),
        PyNode.Copy(PyNode.Get(record, "text")), PyNode.Copy(requirements)));

    /// <summary>
    /// <c>surface_consolidation(memory, plan)</c>: record that this exact proposal page was delivered; a repeated delivery
    /// is a no-op. A stale revision or source snapshot is rejected even on replay. Only hashes and IDs are persisted.
    /// </summary>
    public static JsonObject SurfaceConsolidation(MemoryStore memory, JsonNode? plan)
    {
        ArgumentNullException.ThrowIfNull(memory);
        if (plan is not JsonObject page || !PyJson.PyEquals(PyNode.Get(page, "account"), memory.Account)
            || !PyJson.IsInt(PyNode.Get(page, "revision")) || !PyJson.PyEquals(page["revision"], 1))
        {
            throw new StateError("invalid account-scoped consolidation plan");
        }

        var manifest = new JsonObject();
        foreach (var (key, value) in page)
        {
            if (key is not ("subject_id" or "revision"))
            {
                manifest[key] = PyNode.Copy(value);
            }
        }

        if (!PyJson.PyEquals(PyNode.Get(page, "subject_id"), "consolidation:" + MemoryLearning.Digest(manifest)))
        {
            throw new StateError("consolidation plan content changed");
        }

        if (PyNode.Get(page, "items") is not JsonArray items || items.Count > 50)
        {
            throw new StateError("invalid bounded proposal list");
        }

        return memory.Transaction(() =>
        {
            foreach (var item in items)
            {
                var records = new JsonArray();
                if (item is not JsonObject proposal || PyNode.Get(proposal, "targets") is not JsonArray targets || targets.Count > 200)
                {
                    throw new StateError("invalid consolidation targets");
                }

                foreach (var target in targets)
                {
                    var row = memory.Show(PyNode.Index(target, "id"));
                    if (!PyJson.PyEquals(row["revision"], PyNode.Index(target, "revision")) || !PyNode.In(row["status"], ReviewableStatuses))
                    {
                        throw new StateError("consolidation revision conflict; refresh the proposal page");
                    }

                    records.Add(row);
                }

                if (!PyJson.PyEquals(JsonValue.Create(MemoryLearning.Digest(new JsonArray(records.Select(row => (JsonNode?)Sources(memory, row)).ToArray()))),
                                     PyNode.Get(proposal, "source_fingerprint")))
                {
                    throw new StateError("consolidation source evidence changed; refresh the proposal page");
                }
            }

            var subject = PyNode.AsString(page["subject_id"])!;
            var receiptKey = "learning:delivery:" + subject;
            if (Stored(memory, receiptKey) is not null)
            {
                return new JsonObject { ["surfaced"] = new JsonArray(), ["replayed"] = true, ["subject_id"] = subject };
            }

            if (PyNode.Get(page, "parameters") is not JsonObject parameters
                || parameters.Count != ParameterKeys.Length || !ParameterKeys.All(parameters.ContainsKey))
            {
                throw new StateError("invalid consolidation parameters");
            }

            var fresh = ConsolidationPlan(memory, PyArg.Of(PyNode.Copy(parameters["limit"])), PyNode.Copy(parameters["environment"]),
                                          PyNode.Copy(parameters["after"]), PyArg.Of(PyNode.Copy(parameters["scan_limit"])));
            if (!PyJson.PyEquals(fresh, page))
            {
                throw new StateError("consolidation proposals changed; refresh the proposal page");
            }

            foreach (var item in items)
            {
                var proposal = (JsonObject)item!;
                var trend = PyNode.Is(proposal["reason"], "review_trend");
                var marker = "learning:surfaced:" + PyNode.AsString(proposal["id"]);
                var previous = Stored(memory, marker);
                var evidence = Merge(trend ? null : previous, proposal["evidence"]);
                if (evidence.Count > 4096)
                {
                    throw new StateError("delivery history budget exceeded; explicit maintenance review required");
                }

                Upsert(memory, marker, evidence);
                foreach (var target in (JsonArray)proposal["target_evidence"]!)
                {
                    marker = "learning:target-surfaced:" + PyNode.AsString(target!["id"]);
                    previous = Stored(memory, marker);
                    evidence = Merge(trend ? null : previous, target["evidence"]);
                    if (evidence.Count > 4096)
                    {
                        throw new StateError("target delivery history budget exceeded; explicit maintenance review required");
                    }

                    Upsert(memory, marker, evidence);
                }
            }

            memory.Conn.Execute("INSERT INTO memory_meta VALUES (?,?)", receiptKey, StateStore.CanonicalJson(new JsonObject
            {
                ["targets"] = new JsonArray(items.Select(item => PyNode.Copy(item!["targets"])).ToArray()),
            }));
            return new JsonObject { ["surfaced"] = PyNode.Copy(items), ["replayed"] = false, ["subject_id"] = subject };
        });
    }

    /// <summary><c>sorted(set(previous["evidence"]) | set(evidence))</c> (or only the new evidence without a previous record).</summary>
    private static List<string> Merge(JsonNode? previous, JsonNode? evidence)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        if (PyJson.Truthy(previous))
        {
            tokens.UnionWith(PyNode.Iter(PyNode.Index(previous, "evidence")).Select(value => PyNode.AsString(value)!));
        }

        tokens.UnionWith(PyNode.Iter(evidence).Select(value => PyNode.AsString(value)!));
        return PyText.Sorted(tokens);
    }

    private static void Upsert(MemoryStore memory, string key, List<string> evidence) =>
        memory.Conn.Execute("INSERT INTO memory_meta VALUES (?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                            key, StateStore.CanonicalJson(new JsonObject { ["evidence"] = PyNode.StrList(evidence) }));
}

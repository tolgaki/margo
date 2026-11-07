using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Memory;

namespace Margo.Cloud.State.MemorySearch;

/// <summary>
/// Port of the module functions of <c>skills/chief-of-staff/scripts/memory_context.py</c>: read-only, bounded joins
/// between memory and the authoritative local work ledger. <see cref="ConnectedContext"/> is the class;
/// <see cref="MemorySearch.Context"/>, <see cref="MemorySearch.Graph"/> and <see cref="MemorySearch.Explain"/> are
/// the caller-facing wrappers. No call here records queries, usage, observations or approval.
/// </summary>
public static class MemoryContext
{
    /// <summary><c>SECTIONS</c>.</summary>
    public static readonly IReadOnlyList<string> Sections =
        ["constraints", "people_projects", "current_decisions", "current_outcomes", "open_work", "meetings", "scoped_lessons", "other_context"];

    /// <summary><c>RELATIONS</c>.</summary>
    public static readonly IReadOnlySet<string> Relations = new HashSet<string>(
        ["owns", "reports_to", "depends_on", "constrains", "advances", "discusses", "contradicts", "derives_from", "about", "related_to", "supersedes"],
        StringComparer.Ordinal);

    private static readonly string[] IdentityFields = ["provider", "scope", "external_id"];
    private static readonly string[] EntryFields = ["id", "revision", "domain", "kind", "title", "text", "authority", "scope", "source_refs"];

    /// <summary><c>identity_key(identity)</c>: the unambiguous, case-preserving provider/scope/external_id key.</summary>
    public static string IdentityKey(JsonNode? identity)
    {
        if (identity is not JsonObject value || value.Count != 3 || !IdentityFields.All(value.ContainsKey)
            || !value.All(pair => PyNode.IsText(pair.Value)))
        {
            throw new StateError("identity requires provider, scope and external_id");
        }

        return "identity:" + StateStore.CanonicalJson(new JsonArray(IdentityFields.Select(key => (JsonNode?)PyNode.Copy(value[key])).ToArray()));
    }

    /// <summary><c>entity_keys(record)</c>.</summary>
    public static HashSet<string> EntityKeys(JsonNode? record)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in PyObj.Iterate(Get(record, "entities", new JsonArray())))
        {
            if (MemoryStore.ScopedEntityKey(Node(value)) is { } key)
            {
                result.Add(key);
            }
        }

        if (PyJson.Truthy(Get(record, "identity")))
        {
            result.Add(IdentityKey(PyNode.Index(record, "identity")));
        }

        return result;
    }

    /// <summary><c>applicable(data)</c>: the validity window and review date hold now (unparseable values do not).</summary>
    public static bool Applicable(JsonNode? data)
    {
        var stamp = PyDateTime.Now();
        foreach (var (key, before) in new[] { ("valid_from", true), ("valid_to", false), ("review_after", false) })
        {
            var raw = Get(data, key);
            if (!PyJson.Truthy(raw))
            {
                continue;
            }

            if (PyNode.AsString(raw) is not { } text)
            {
                throw new PyAttributeError("'" + PyJson.TypeName(raw) + "' object has no attribute 'replace'");
            }

            PyDateTime value;
            try
            {
                value = PyDateTime.FromIsoformat(text.Replace("Z", "+00:00", StringComparison.Ordinal));
            }
            catch (PyValueError)
            {
                return false;
            }
            catch (PyTypeError)
            {
                return false;
            }

            if (!value.IsAware || (before ? stamp < value : stamp >= value))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary><c>active_edges(memory, memory_id)</c>: the temporal/source-safe edge seam.</summary>
    public static List<JsonObject> ActiveEdges(IMemorySource memory, JsonNode? memoryId)
    {
        ArgumentNullException.ThrowIfNull(memory);
        var result = new List<JsonObject>();
        foreach (var edge in memory.ActiveLinks(memoryId))
        {
            if (PyNode.In(Get(edge, "relation"), Relations) && Applicable(Get(edge, "data", new JsonObject())))
            {
                result.Add((JsonObject)edge!);
            }
        }

        return result;
    }

    /// <summary>
    /// <c>with read_snapshot(conn): body()</c>: a deferred transaction rolled back afterwards, unless the caller
    /// already holds one (never commit or roll back a caller's transaction).
    /// </summary>
    public static T ReadSnapshot<T>(PyConnection conn, Func<T> body)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(body);
        var owned = !conn.InTransaction;
        if (owned)
        {
            conn.Execute("BEGIN");
        }

        try
        {
            return body();
        }
        finally
        {
            if (owned)
            {
                conn.Rollback();
            }
        }
    }

    /// <summary><c>bounds(max_depth, max_nodes)</c>.</summary>
    public static (int Depth, int Nodes) Bounds(JsonNode? maxDepth, JsonNode? maxNodes)
    {
        if (!PyJson.IsInt(maxDepth) || PyJson.AsInt(maxDepth) < 0 || PyJson.AsInt(maxDepth) > 3)
        {
            throw new StateError("context depth must be between 0 and 3");
        }

        if (!PyJson.IsInt(maxNodes) || PyJson.AsInt(maxNodes) < 1 || PyJson.AsInt(maxNodes) > 50)
        {
            throw new StateError("context nodes must be between 1 and 50");
        }

        return ((int)PyJson.AsInt(maxDepth), (int)PyJson.AsInt(maxNodes));
    }

    /// <summary>
    /// <c>memory_entry(row, reasons, mandatory)</c>. <paramref name="reasons"/> is stored by reference, as in Python:
    /// reasons appended to the same list later show in the entry.
    /// </summary>
    public static JsonObject MemoryEntry(JsonNode? row, JsonArray reasons, bool mandatory = false)
    {
        ArgumentNullException.ThrowIfNull(reasons);
        var item = new JsonObject();
        foreach (var key in EntryFields)
        {
            item[key] = PyNode.Copy(PyNode.Index(row, key));
        }

        var uses = Get(row, "allowed_uses", new JsonArray("reasoning"));
        item["mandatory"] = mandatory;
        item["selection_reasons"] = reasons;
        item["source_record"] = new JsonObject
        {
            ["store"] = "memory",
            ["id"] = PyNode.Copy(PyNode.Index(row, "id")),
            ["revision"] = PyNode.Copy(PyNode.Index(row, "revision")),
        };
        item["allowed_uses"] = PyNode.Copy(uses);
        item["sensitivity"] = PyNode.Copy(Get(row, "sensitivity", "private"));
        item["copyable_to_draft"] = PyObj.Contains(Get(row, "allowed_uses", new JsonArray()), "drafting");
        if (PyJson.Truthy(Get(row, "identity")))
        {
            item["identity"] = PyNode.Copy(PyNode.Index(row, "identity"));
        }

        return item;
    }

    /// <summary><c>bool(mandatory_constraint(row))</c>: confirmed preferences and the imported "About me" profile.</summary>
    public static bool MandatoryConstraint(JsonNode? row) => PyJson.Truthy(MandatoryConstraintValue(row));

    /// <summary>
    /// <c>mandatory_constraint(row)</c> as Python evaluates it: the value of its <c>and</c>/<c>or</c> chain (a bool, or a
    /// falsy <c>imported_configuration</c> value).
    /// </summary>
    public static JsonNode? MandatoryConstraintValue(JsonNode? row)
    {
        if (PyNode.Is(PyNode.Index(row, "kind"), "preference") && PyNode.Is(PyNode.Index(row, "authority"), "user_confirmed"))
        {
            return true;
        }

        if (!PyNode.Is(PyNode.Index(row, "domain"), "user"))
        {
            return false;
        }

        var imported = Get(Get(row, "metadata", new JsonObject()), "imported_configuration");
        if (!PyJson.Truthy(imported))
        {
            return PyNode.Copy(imported);
        }

        var heading = Get(Get(row, "metadata", new JsonObject()), "source_heading", "");
        if (PyNode.AsString(heading) is not { } text)
        {
            throw new PyAttributeError("'" + PyJson.TypeName(heading) + "' object has no attribute 'casefold'");
        }

        return PyRepr.CaseFold(text) == "about me";
    }

    /// <summary><c>section(entry)</c>.</summary>
    public static string Section(JsonNode? entry)
    {
        var kind = PyNode.Index(entry, "kind");
        if (PyJson.Truthy(Get(entry, "mandatory")) || PyNode.IsOneOf(kind, "preference", "profile"))
        {
            return "constraints";
        }

        PyNode.RequireHashable(kind);
        return PyNode.AsString(kind) switch
        {
            "person" or "project" => "people_projects",
            "decision" => "current_decisions",
            "outcome" => "current_outcomes",
            "item" => "open_work",
            "meeting" => "meetings",
            "lesson" or "capability" => "scoped_lessons",
            _ => "other_context",
        };
    }

    /// <summary><c>sized(result)</c>: <c>used_chars</c> is the code-point length of the whole canonical JSON.</summary>
    public static JsonObject Sized(JsonObject result)
    {
        ArgumentNullException.ThrowIfNull(result);
        result["used_chars"] = 0L;
        while (true)
        {
            long size = PyRepr.Length(StateStore.CanonicalJson(result));
            if (PyJson.AsInt(result["used_chars"]) == size)
            {
                return result;
            }

            result["used_chars"] = size;
        }
    }

    /// <summary>
    /// <c>budget_packet(result, budget_chars, graph=False)</c>: bound the complete canonical JSON (wrappers, reasons
    /// and gaps included), dropping optional entries, then edges, then gaps; mandatory constraints are never dropped.
    /// </summary>
    public static JsonObject BudgetPacket(JsonObject result, JsonNode? budgetChars, bool graph = false)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!PyJson.IsInt(budgetChars) || PyJson.AsInt(budgetChars) < 1000 || PyJson.AsInt(budgetChars) > 50000)
        {
            throw new StateError("context budget must be between 1000 and 50000 characters");
        }

        var budget = (long)PyJson.AsInt(budgetChars);
        result["budget_chars"] = budget;
        result["budget_scope"] = "complete_canonical_json";
        result["omitted"] = new JsonObject { ["entries"] = 0L, ["edges"] = 0L, ["gaps"] = 0L };
        var key = graph ? "nodes" : "entries";
        PyNode.Index(result, key);
        var original = Detach(result, key);
        var required = original.Where(entry => PyJson.Truthy(Get(entry, "mandatory"))).ToList();
        var optional = original.Where(entry => !PyJson.Truthy(Get(entry, "mandatory"))).ToList();
        result[key] = new JsonArray([.. required, .. optional]);
        var omitted = (JsonObject)result["omitted"]!;

        void Bump(string name, long by) => omitted[name] = PyJson.Int(PyJson.AsInt(omitted[name]) + by);

        long Refresh()
        {
            var ids = new List<JsonNode?>();
            foreach (var entry in (JsonArray)result[key]!)
            {
                ids.Add(PyNode.Index(entry, "id"));
            }

            PyNode.Index(result, "edges");
            var edges = Detach(result, "edges");
            var kept = edges.Where(edge => ids.Any(id => PyObj.EqBool(id, PyNode.Index(edge, "source_id")))
                                           && ids.Any(id => PyObj.EqBool(id, PyNode.Index(edge, "target_id")))).ToList();
            Bump("edges", edges.Count - kept.Count);
            result["edges"] = new JsonArray([.. kept]);
            if (!graph)
            {
                var sections = new JsonObject();
                foreach (var name in Sections)
                {
                    sections[name] = new JsonArray(((JsonArray)result[key]!).Where(entry => Section(entry) == name)
                                                    .Select(entry => PyNode.Copy(PyNode.Index(entry, "id"))).ToArray());
                }

                var gapCount = PyObj.Len(PyNode.Index(result, "gaps"));
                sections["unknown_conflicting_evidence"] = new JsonArray(Enumerable.Range(0, gapCount).Select(index => (JsonNode?)(long)index).ToArray());
                result["sections"] = sections;
            }

            return (long)PyJson.AsInt(Sized(result)["used_chars"]);
        }

        while (Refresh() > budget)
        {
            if (optional.Count > 0)
            {
                optional.RemoveAt(optional.Count - 1);
                Detach(result, key);
                result[key] = new JsonArray([.. required, .. optional]);
                Bump("entries", 1);
            }
            else if (result["edges"] is JsonArray { Count: > 0 } edges)
            {
                edges.RemoveAt(edges.Count - 1);
                Bump("edges", 1);
            }
            else if (result["gaps"] is JsonArray { Count: > 0 } gaps)
            {
                gaps.RemoveAt(gaps.Count - 1);
                Bump("gaps", 1);
            }
            else
            {
                return Sized(new JsonObject
                {
                    ["status"] = "blocked",
                    ["account"] = PyNode.Copy(result["account"]),
                    [key] = new JsonArray(),
                    ["requires_larger_budget"] = true,
                    ["reason"] = "Mandatory constraints and packet envelope exceed the context budget.",
                    ["mandatory_count"] = (long)required.Count,
                    ["budget_chars"] = budget,
                    ["budget_scope"] = "complete_canonical_json",
                    ["context_is_consent"] = false,
                });
            }
        }

        return (JsonObject)PyJson.Loads(StateStore.CanonicalJson(result))!;
    }

    /// <summary>Removes the list at <paramref name="key"/> and returns its items, detached so they can be re-parented.</summary>
    internal static List<JsonNode?> Detach(JsonObject owner, string key)
    {
        var list = owner[key] as JsonArray;
        var items = list is null ? [] : list.ToList();
        list?.Clear();
        return items;
    }

    // ------------------------------------------------------------------------------ Python dict access

    /// <summary><c>x.get(key, fallback)</c>: <c>AttributeError</c> unless <paramref name="value"/> is a dict.</summary>
    internal static JsonNode? Get(JsonNode? value, string key, JsonNode? fallback = null) =>
        PyNode.Get(PyNode.Dict(value), key, fallback);

    /// <summary>A value PyObj produced from a JSON node, back as a node.</summary>
    internal static JsonNode? Node(object? value) => value switch
    {
        null => null,
        JsonNode node => node,
        string text => JsonValue.Create(text),
        _ => PyNode.FromSql(value),
    };
}

using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Memory;
using Margo.Cloud.State.Work;

namespace Margo.Cloud.State.MemoryLearning;

public static partial class MemoryLearning
{
    private static readonly (string Category, IReadOnlySet<string> Codes)[] ErrorGroups =
    [
        ("caller_mistake", Set("invalid_argument", "invalid_query", "bad_request", "400", "inefficientfilter")),
        ("stale_host_binding", Set("tool_not_found", "binding_stale", "schema_changed", "unknown_tool_binding")),
        ("policy_denial", Set("access_denied", "forbidden", "policy_denied", "403")),
        ("missing_capability", Set("not_implemented", "capability_missing", "unsupported_operation")),
        ("bad_data", Set("invalid_json", "invalid_input_shape", "malformed_data")),
        ("transient_environment_failure", Set("timeout", "rate_limited", "429", "503", "oauth_expired", "401")),
    ];

    private static readonly HashSet<string> SchemaKeywords = Set(
        "type", "required", "properties", "additionalProperties", "items", "enum",
        "description", "title", "$schema", "minimum", "maximum", "minLength", "maxLength");

    private static readonly HashSet<string> SchemaTypes = Set("object", "array", "string", "boolean", "integer", "number", "null");

    private static readonly IReadOnlySet<string> ConstrainingKeywords = Set("type", "required", "properties", "enum");

    /// <summary>
    /// <c>classify_error(code, *, confirmed_defect=False)</c>: a conservative taxonomy; an unfamiliar error is not
    /// automatically a product defect.
    /// </summary>
    public static string ClassifyError(JsonNode? code, bool confirmedDefect = false)
    {
        var normalized = PyRepr.CaseFold(PyJson.Truthy(code) ? PyNode.Str(code) : "").Replace("-", "_", StringComparison.Ordinal);
        foreach (var (category, codes) in ErrorGroups)
        {
            if (codes.Contains(normalized))
            {
                return category;
            }
        }

        return normalized == "confirmed_defect" && confirmedDefect ? "actual_defect" : "unclassified";
    }

    /// <summary>
    /// <c>_input_contract(value, schema, depth=0)</c>: a bounded JSON-schema subset. Unsupported keywords fail closed
    /// (<see cref="StateError"/>), not as a false pass.
    /// </summary>
    public static bool InputContract(JsonNode? value, JsonNode? schema, int depth = 0)
    {
        if (schema is not JsonObject rules || depth > 10 || rules.Any(pair => !SchemaKeywords.Contains(pair.Key)))
        {
            throw new StateError("synthetic validator cannot establish this schema; use an existing execution receipt");
        }

        foreach (var field in new[] { "minimum", "maximum" })
        {
            if (rules.TryGetPropertyValue(field, out var bound)
                && (!(PyJson.IsInt(bound) || PyJson.IsFloat(bound)) || !IsFinite(bound)))
            {
                throw new StateError("invalid numeric constraint in exposed schema");
            }
        }

        foreach (var field in new[] { "minLength", "maxLength" })
        {
            if (rules.TryGetPropertyValue(field, out var length) && (!PyJson.IsInt(length) || PyJson.AsInt(length) < 0))
            {
                throw new StateError("invalid length constraint in exposed schema");
            }
        }

        if (rules.TryGetPropertyValue("enum", out var options) && (options is not JsonArray choices || choices.Count == 0))
        {
            throw new StateError("invalid enum in exposed schema");
        }

        var kind = PyNode.Get(rules, "type");
        if (kind is not null && (PyNode.AsString(kind) is not { } typeName || !SchemaTypes.Contains(typeName)))
        {
            throw new StateError("synthetic validator does not support this schema type");
        }

        if (kind is not null && !HasType(value, PyNode.AsString(kind)!))
        {
            return false;
        }

        if (rules["enum"] is JsonArray allowed)
        {
            var canonical = StateStore.CanonicalJson(value);
            var texts = allowed.Select(StateStore.CanonicalJson).ToList();
            if (!texts.Contains(canonical, StringComparer.Ordinal))
            {
                return false;
            }
        }

        if (value is JsonObject obj)
        {
            var properties = PyNode.Get(rules, "properties", new JsonObject());
            var required = PyNode.Get(rules, "required", new JsonArray());
            if (properties is not JsonObject declared || required is not JsonArray needed || needed.Any(key => !PyJson.IsStr(key)))
            {
                throw new StateError("invalid exposed object schema");
            }

            if (needed.Any(key => !obj.ContainsKey(PyNode.AsString(key)!)))
            {
                return false;
            }

            // set(value) - set(properties): iterated in the value's order (Python iterates a set of strings in hash order).
            var extra = obj.Select(pair => pair.Key).Where(key => !declared.ContainsKey(key)).ToList();
            var additional = PyNode.Get(rules, "additionalProperties", true);
            if (!PyJson.IsBool(additional) && additional is not JsonObject)
            {
                throw new StateError("invalid additionalProperties in exposed schema");
            }

            if (PyJson.IsBool(additional) && !PyJson.Truthy(additional) && extra.Count > 0)
            {
                return false;
            }

            if (additional is JsonObject nested && extra.Any(key => !InputContract(obj[key], nested, depth + 1)))
            {
                return false;
            }

            if (declared.Any(pair => obj.ContainsKey(pair.Key) && !InputContract(obj[pair.Key], pair.Value, depth + 1)))
            {
                return false;
            }
        }

        if (value is JsonArray list && rules.ContainsKey("items"))
        {
            if (list.Count > MaxObservations)
            {
                throw new StateError("synthetic array exceeds the exercise budget");
            }

            if (list.Any(item => !InputContract(item, rules["items"], depth + 1)))
            {
                return false;
            }
        }

        if (PyNode.AsString(value) is { } text)
        {
            var length = (System.Numerics.BigInteger)PyRepr.Length(text);
            if (length < PyJson.AsInt(PyNode.Get(rules, "minLength", 0))
                || length > PyJson.AsInt(PyNode.Get(rules, "maxLength", MaxFileBytes)))
            {
                return false;
            }
        }

        if (PyJson.IsInt(value) || PyJson.IsFloat(value))
        {
            if (!IsFinite(value)
                || PyObj.Lt(value, PyNode.Get(rules, "minimum", double.NegativeInfinity))
                || PyObj.Gt(value, PyNode.Get(rules, "maximum", double.PositiveInfinity)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary><c>math.isfinite(x)</c> for an int or float: an int beyond the float range raises <c>OverflowError</c>.</summary>
    private static bool IsFinite(JsonNode? number) => PyJson.IsInt(number)
        ? double.IsFinite(PyNumbers.IntToDouble(PyJson.AsInt(number)))
        : double.IsFinite(PyJson.AsDouble(number));

    private static bool HasType(JsonNode? value, string kind) => kind switch
    {
        "object" => value is JsonObject,
        "array" => value is JsonArray,
        "string" => PyJson.IsStr(value),
        "boolean" => PyJson.IsBool(value),
        "integer" => PyJson.IsInt(value),
        "number" => PyJson.IsInt(value) || PyJson.IsFloat(value),
        _ => PyNode.IsNone(value),
    };

    /// <summary>
    /// <c>_observation(memory, capability, evidence)</c>: a pinned synthetic input-contract exercise or an existing,
    /// settled execution receipt of this account, resolved from the store (never an asserted success).
    /// </summary>
    public static JsonObject Observation(MemoryStore memory, JsonObject capability, JsonNode? evidence)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(capability);
        if (evidence is not JsonObject proof)
        {
            throw new StateError("validation requires an evidence reference, not an assertion of success");
        }

        var meta = Item(capability, "metadata");
        var requirements = Environment(memory, Get(meta, "environment_requirements"));
        if (Eq(requirements["host"], "unbound"))
        {
            throw new StateError("validation requires a named host environment");
        }

        if (Eq(PyNode.Get(proof, "kind"), "synthetic_observation"))
        {
            if (!KeysAre(proof, "kind", "fixture") || proof["fixture"] is not JsonObject fixture)
            {
                throw new StateError("synthetic evidence requires only its pinned fixture");
            }

            if (!KeysAre(fixture, "path", "sha256"))
            {
                throw new StateError("synthetic fixture requires path and sha256");
            }

            Text(fixture["sha256"], "fixture hash");
            var (document, source) = JsonFile(fixture["path"], fixture["sha256"]);
            if (!(PyJson.IsBool(PyNode.Get(document, "synthetic")) && PyJson.Truthy(document["synthetic"]))
                || !Eq(PyNode.Get(document, "environment"), requirements)
                || !Eq(PyNode.Get(document, "capability_id"), Item(capability, "id"))
                || !document.ContainsKey("input"))
            {
                throw new StateError("synthetic fixture must pin the capability and exact environment");
            }

            if (!Eq(Get(meta, "capability_kind"), "tool") || !Eq(Get(meta, "availability"), "host_exposed"))
            {
                throw new StateError("synthetic input-contract exercise requires an exposed tool schema");
            }

            var schemaKeys = PyNode.Iter(Item(meta, "input_schema"));
            schemaKeys.ForEach(PyNode.RequireHashable);
            if (!schemaKeys.Any(key => PyNode.In(key, ConstrainingKeywords)))
            {
                throw new StateError("an unconstrained schema cannot establish an input-contract validation");
            }

            var success = InputContract(document["input"], Item(meta, "input_schema"));
            return new JsonObject
            {
                ["id"] = "synthetic:" + Digest(new JsonArray(PyNode.Copy(Item(capability, "id")), PyNode.Copy(source["revision"]))),
                ["outcome"] = success ? "succeeded" : "failed",
                ["error_category"] = success ? null : "bad_data",
                ["level"] = "synthetic_input_contract_only",
                ["runtime_validated"] = false,
                ["source_refs"] = new JsonArray(source),
                ["evidence"] = PyNode.Copy(proof),
                ["environment"] = requirements,
                ["note"] = "Only the supplied input shape was exercised locally; remote execution was not tested.",
            };
        }

        if (!Eq(PyNode.Get(proof, "kind"), "execution_receipt") || !KeysAre(proof, "kind", "attempt_id"))
        {
            throw new StateError("use a reproducible synthetic fixture or an existing execution_receipt attempt_id");
        }

        var attempt = Text(proof["attempt_id"], "attempt_id");
        PyRow? row;
        try
        {
            row = memory.Conn.Execute(
                "SELECT e.* FROM work_executions e JOIN work_actions a ON a.id=e.action_id "
                + "WHERE e.id=? AND a.account=?", attempt, memory.Account).FetchOne();
        }
        catch (PySqliteError ex) when (ex.Kind == PySqliteErrorKind.OperationalError && ex.Message.Contains("no such table", StringComparison.Ordinal))
        {
            row = null;
        }

        if (row is null || !(row["state"] is string state && SettledStates.Contains(state)) || !PyObj.Truthy(row["receipt"]))
        {
            throw new StateError("validation needs a settled, existing execution in this account");
        }

        var receipt = PyNode.Loads(row["receipt"]);
        Ledger.ValidateReceipt(receipt, state);
        var stored = (JsonObject)receipt!;
        if (!Eq(PyNode.Get(stored, "environment"), requirements)
            || !Eq(PyNode.Get(stored, "capability_id"), Item(capability, "id"))
            || !Eq(PyNode.Get(stored, "capability_hash"), Item(meta, "content_hash")))
        {
            throw new StateError("stored receipt does not prove this exact capability/version/host environment");
        }

        var executionId = PyObj.Str(row["id"]);
        string? category = state == "succeeded" ? null
            : ClassifyError(PyNode.Get(stored, "error_code"), PyJson.Truthy(PyNode.Get(stored, "defect_reference")));
        return new JsonObject
        {
            ["id"] = "execution:" + executionId,
            ["outcome"] = state,
            ["error_category"] = category,
            ["executed_at"] = StateStore.ValidateTimestamp(Item(stored, "recorded_at")),
            ["level"] = "existing_execution_receipt",
            ["runtime_validated"] = state == "succeeded",
            ["source_refs"] = new JsonArray(new JsonObject
            {
                ["kind"] = "tool_result",
                ["ref"] = "execution:" + executionId,
                ["revision"] = Digest(stored),
            }),
            ["evidence"] = PyNode.Copy(proof),
            ["environment"] = requirements,
            ["note"] = "Existing receipt only; no action was performed for validation.",
        };
    }

    /// <summary>
    /// <c>validation(memory, capability_id, revision, evidence)</c>: record a reproducible local exercise or a settled
    /// receipt against an active capability; no tool is invoked. A replay of known evidence returns the record unchanged.
    /// </summary>
    public static JsonObject Validation(MemoryStore memory, JsonNode? capabilityId, JsonNode? revision, JsonNode? evidence)
    {
        ArgumentNullException.ThrowIfNull(memory);
        return memory.Transaction(() =>
        {
            var current = memory.Show(capabilityId);
            Conditional(current, revision);
            if (!Eq(Item(current, "kind"), "capability") || !Eq(Item(current, "status"), "active"))
            {
                throw new StateError("validation requires an active capability observation");
            }

            var metadata = Item(current, "metadata");
            if (PyNode.In(Get(metadata, "state"), UnavailableStates))
            {
                throw new StateError("unavailable capabilities cannot be validated");
            }

            if (!memory.WorkSourcesCurrent(current))
            {
                throw new StateError("capability evidence changed; refresh the inventory before validation");
            }

            var result = Observation(memory, current, evidence);
            var signature = Digest(result);
            var previous = Get(metadata, "last_validation", new JsonObject());
            var history = Get(metadata, "validation_history", new JsonArray());
            var scope = ValidationScope(metadata);
            var scoped = PyNode.Iter(history).Where(entry => Eq(Get(entry, "scope", scope), scope)).ToList();
            if (Eq(Get(previous, "fingerprint"), signature) || scoped.Any(entry => Eq(Item(entry, "id"), result["id"])))
            {
                return current;
            }

            if (PyObj.Len(history) >= MaxObservations)
            {
                throw new StateError("capability validation history budget exceeded; review maintenance before adding evidence");
            }

            if (PyJson.Truthy(PyNode.Get(result, "executed_at"))
                && scoped.Any(entry => PyObj.Gt(Get(entry, "executed_at", ""), result["executed_at"])))
            {
                throw new StateError("older execution evidence cannot supersede a newer validation outcome");
            }

            var entry = new JsonObject();
            foreach (var key in EventFields)
            {
                if (result.ContainsKey(key))
                {
                    entry[key] = PyNode.Copy(result[key]);
                }
            }

            entry["scope"] = scope;
            var data = Data(current);
            data["source_refs"] = Refs(Concat(Item(current, "source_refs"), result["source_refs"]));
            var historyList = Concat(history, new JsonArray());
            historyList.Add(entry);
            data["metadata"] = PyNode.DictWith(metadata,
                ("state", Eq(result["outcome"], "succeeded") ? "validated" : "degraded"),
                ("validation", PyNode.Copy(result["level"])),
                ("last_validation", PyNode.DictWith(result, ("fingerprint", signature))),
                ("validation_history", new JsonArray(historyList.Select(PyNode.Copy).ToArray())));
            data["text"] = PyNode.Str(Item(metadata, "name")) + ": " + PyNode.Str(result["outcome"]) + ". "
                           + PyNode.Str(result["note"]) + " No new execution authority.";
            return memory.Revise(capabilityId, data, "active", revision);
        });
    }

    /// <summary>
    /// <c>_feedback(memory, reference)</c>: an exact, recorded Work feedback record with human evidence that permits
    /// learning, as a citable source reference.
    /// </summary>
    public static JsonObject Feedback(MemoryStore memory, JsonNode? reference)
    {
        ArgumentNullException.ThrowIfNull(memory);
        if (reference is not JsonObject exact || !KeysAre(exact, "id", "revision") || !PyJson.IsInt(exact["revision"]))
        {
            throw new StateError("feedback requires its exact id and revision");
        }

        PyRow? row;
        try
        {
            row = memory.Conn.Execute(
                "SELECT r.*,v.evidence FROM work_records r JOIN work_record_revisions v "
                + "ON v.record_id=r.id AND v.revision=r.revision WHERE r.id=? AND r.account=?",
                exact["id"], memory.Account).FetchOne();
        }
        catch (PySqliteError ex) when (ex.Kind == PySqliteErrorKind.OperationalError && ex.Message.Contains("no such table", StringComparison.Ordinal))
        {
            row = null;
        }

        if (row is null || !PyObj.Eq(row["kind"], "feedback") || !PyObj.Eq(row["revision"], exact["revision"])
            || !PyObj.Eq(row["state"], "recorded"))
        {
            throw new StateError("feedback must be existing, exact, and not marked do_not_learn");
        }

        var data = PyNode.Loads(row["data"]);
        if (PyJson.Truthy(Get(data, "do_not_learn")) || !PyObj.Truthy(row["evidence"]))
        {
            throw new StateError("feedback does not permit learning or lacks recorded human evidence");
        }

        var confirmation = PyNode.Loads(row["evidence"]);
        WorkLedger.Human(confirmation, PyNode.FromSql(row["id"]), PyNode.FromSql(row["revision"]), "confirm");
        return new JsonObject
        {
            ["kind"] = "tool_result",
            ["ref"] = "feedback:" + PyObj.Str(row["id"]),
            ["revision"] = Digest(new JsonArray(data, confirmation)),
        };
    }
}

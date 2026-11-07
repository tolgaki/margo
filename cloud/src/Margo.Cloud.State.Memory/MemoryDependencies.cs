using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.State.Memory;

/// <summary>
/// <c>work_ledger.human</c> (with its <c>text</c>, <c>timestamp</c> and <c>now</c> helpers), which memory_store calls
/// for every reviewed change. The Work port owns <c>work_ledger.py</c>; this copy keeps the memory port independent
/// of it until the two assemblies are integrated (PORTING.md: replace with <c>WorkLedger.Human</c>).
/// </summary>
public static class HumanEvidence
{
    private static readonly string[] Prefixes = ["conversation:", "host-interaction:", "legacy-review:"];

    /// <summary><c>work_ledger.text(value, field)</c>.</summary>
    public static string Text(JsonNode? value, string field) =>
        Py.IsText(value) ? Py.AsString(value)! : throw new StateError(field + " must be a nonempty string");

    /// <summary><c>work_ledger.timestamp(value)</c>: an aware ISO datetime converted to UTC.</summary>
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

        // astimezone() is inside Python's try, but its OverflowError is not a ValueError: it escapes.
        return parsed.ToUtc();
    }

    /// <summary>
    /// <c>work_ledger.human(evidence, subject_id, revision, decision)</c>: validates a recorded human decision for the
    /// exact subject and revision; it does not authenticate the caller as a human.
    /// </summary>
    public static JsonObject Human(JsonNode? evidence, JsonNode? subjectId, JsonNode? revision, string? decision = null)
    {
        if (evidence is not JsonObject record || !PyJson.PyEquals(Py.Get(record, "kind"), JsonValue.Create("human_confirmation")))
        {
            throw new StateError("explicit human_confirmation evidence is required");
        }

        foreach (var key in new[] { "actor", "statement", "evidence_ref", "decision" })
        {
            Text(Py.Get(record, key), key);
        }

        var reference = Py.AsString(record["evidence_ref"])!;
        if (!Prefixes.Any(prefix => reference.StartsWith(prefix, StringComparison.Ordinal)))
        {
            throw new StateError("human evidence must reference a conversation, host interaction, or legacy review");
        }

        if (!PyJson.PyEquals(Py.Get(record, "subject_id"), subjectId) || !PyJson.IsInt(Py.Get(record, "revision"))
            || !PyJson.PyEquals(Py.Get(record, "revision"), revision))
        {
            throw new StateError("human evidence must name the exact subject and revision");
        }

        if (!string.IsNullOrEmpty(decision) && !PyJson.PyEquals(record["decision"], JsonValue.Create(decision)))
        {
            throw new StateError("human evidence must record the " + decision + " decision");
        }

        if (Timestamp(Py.Get(record, "decided_at")) > PyDateTime.Now() + PyTimeDelta.FromMinutes(1))
        {
            throw new StateError("human decision cannot be in the future");
        }

        return record;
    }
}

/// <summary>
/// The session-checkpoint source helpers of <c>memory_dream.py</c> that memory_store and memory_governance call:
/// <c>source_keys</c>, <c>validate_source</c> and its bounded <c>text</c> check. They live here, the lowest layer
/// that needs them; the MemoryCommands port of memory_dream.py should call these instead of re-porting them.
/// </summary>
public static class DreamSources
{
    /// <summary><c>memory_dream.text(value, field, maximum)</c>.</summary>
    public static string Text(JsonNode? value, string field, int maximum = 1000)
    {
        if (Py.AsString(value) is not { } text || PyRepr.Strip(text).Length == 0 || PyRepr.Length(text) > maximum)
        {
            throw new StateError(field + " must be a bounded nonempty string");
        }

        return text;
    }

    /// <summary><c>source_keys(ref)</c>: the event and locator aliases of a session checkpoint source.</summary>
    public static (string Source, string Locator) SourceKeys(JsonObject reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var identity = StateStore.ParseJson(RefText(reference));
        var parts = identity as JsonArray ?? throw new PyTypeError("'" + PyJson.TypeName(identity) + "' object is not subscriptable");
        var head = new JsonArray(parts.Take(5).Select(Py.Clone).ToArray());
        var locator = new JsonArray(parts.Take(3).Select(Py.Clone).ToArray());
        if (parts.Count <= 5)
        {
            throw new PyIndexError("list index out of range");
        }

        locator.Add(Py.Clone(parts[5]));
        return ("dream-source:" + StateStore.CanonicalJson(head), "dream-locator:" + StateStore.CanonicalJson(locator));
    }

    /// <summary><c>validate_source(memory, ref)</c>: validates the source identity and returns its canonical checkpoint key.</summary>
    public static string ValidateSource(MemoryStore memory, JsonObject reference)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(reference);
        JsonNode? identity;
        try
        {
            identity = StateStore.ParseJson(RefText(reference));
        }
        catch (Exception ex) when (ex is PyValueError or PyTypeError)
        {
            throw new StateError("invalid session checkpoint source identity", ex);
        }

        if (identity is not JsonArray parts || parts.Count != 6 || !PyJson.PyEquals(parts[0], JsonValue.Create(memory.Account))
            || parts.Any(part => !Py.IsText(part))
            || StateStore.CanonicalJson(parts) != Py.AsString(reference["ref"]))
        {
            throw new StateError("session source requires canonical account, host, workspace, session, event and locator");
        }

        Text(Py.Get(reference, "revision"), "source revision", 200);
        var (source, locator) = SourceKeys(reference);
        foreach (var key in new[] { source, locator })
        {
            var (_, keyHash) = memory.Identity("user", key);
            if (memory.Conn.Execute("SELECT 1 FROM memory_tombstones WHERE account=? AND key_hash=?", memory.Account, keyHash).FetchOne() is not null)
            {
                throw new StateError("forgotten session evidence cannot be reimported under another identity");
            }
        }

        return "dream-event:" + MemoryGovernance.Digest(JsonValue.Create(StateStore.CanonicalJson(new JsonArray(parts.Take(5).Select(Py.Clone).ToArray()))));
    }

    private static string RefText(JsonObject reference)
    {
        if (!reference.TryGetPropertyValue("ref", out var value))
        {
            throw new PyKeyError("ref");
        }

        return Py.AsString(value) ?? throw new PyTypeError("the JSON object must be str, bytes or bytearray, not " + PyJson.TypeName(value));
    }
}

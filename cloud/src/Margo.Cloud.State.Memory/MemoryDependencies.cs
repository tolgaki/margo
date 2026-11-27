using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.State.Memory;

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
        if (PyNode.AsString(value) is not { } text || PyRepr.Strip(text).Length == 0 || PyRepr.Length(text) > maximum)
        {
            throw new StateError(field + " must be a bounded nonempty string");
        }

        return text;
    }

    /// <summary>
    /// <c>source_keys(ref)</c>: the event and locator aliases of a session checkpoint source. An unvalidated identity
    /// fails as Python's <c>identity[:5]</c> / <c>identity[:3] + [identity[5]]</c> do (callers pass validated or stored
    /// references, so these only matter to direct callers).
    /// </summary>
    public static (string Source, string Locator) SourceKeys(JsonObject reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var identity = StateStore.ParseJson(RefText(reference));
        switch (identity)
        {
            case JsonArray parts:
                var head = new JsonArray(parts.Take(5).Select(PyNode.Copy).ToArray());
                var locator = new JsonArray(parts.Take(3).Select(PyNode.Copy).ToArray());
                if (parts.Count <= 5)
                {
                    throw new PyIndexError("list index out of range");
                }

                locator.Add(PyNode.Copy(parts[5]));
                return ("dream-source:" + StateStore.CanonicalJson(head), "dream-locator:" + StateStore.CanonicalJson(locator));
            case JsonObject:
                // dict[:5]: slices are hashable since Python 3.12, so the lookup fails with the slice as the key.
                throw PyKeyError.ForSlice(5);
            case JsonValue value when PyJson.Scalar(value) is string text:
                // str[:5] works; then identity[5] fails on a short string, or str + list fails.
                throw PyRepr.Length(text) <= 5
                    ? new PyIndexError("string index out of range")
                    : new PyTypeError("can only concatenate str (not \"list\") to str");
            default:
                throw new PyTypeError("'" + PyJson.TypeName(identity) + "' object is not subscriptable");
        }
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
            || parts.Any(part => !PyNode.IsText(part))
            || StateStore.CanonicalJson(parts) != PyNode.AsString(reference["ref"]))
        {
            throw new StateError("session source requires canonical account, host, workspace, session, event and locator");
        }

        Text(PyNode.Get(reference, "revision"), "source revision", 200);
        var (source, locator) = SourceKeys(reference);
        foreach (var key in new[] { source, locator })
        {
            var (_, keyHash) = memory.Identity("user", key);
            if (memory.Conn.Execute("SELECT 1 FROM memory_tombstones WHERE account=? AND key_hash=?", memory.Account, keyHash).FetchOne() is not null)
            {
                throw new StateError("forgotten session evidence cannot be reimported under another identity");
            }
        }

        return "dream-event:" + MemoryGovernance.Digest(JsonValue.Create(StateStore.CanonicalJson(new JsonArray(parts.Take(5).Select(PyNode.Copy).ToArray()))));
    }

    /// <summary>
    /// <c>ref["ref"]</c> as the text <c>parse_json</c> reads: <c>parse_json</c> turns json.loads' <c>TypeError</c> for a
    /// non-string into <c>StateError("invalid JSON")</c> (which <c>validate_source</c> reports as an invalid identity).
    /// </summary>
    private static string RefText(JsonObject reference)
    {
        if (!reference.TryGetPropertyValue("ref", out var value))
        {
            throw new PyKeyError("ref");
        }

        return PyNode.AsString(value) ?? throw new StateError(
            "invalid JSON", new PyTypeError("the JSON object must be str, bytes or bytearray, not " + PyJson.TypeName(value)));
    }
}

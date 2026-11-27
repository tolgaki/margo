using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Memory;
using Margo.Cloud.State.Work;

namespace Margo.Cloud.State.MemoryLearning;

/// <summary>
/// Port of <c>skills/chief-of-staff/scripts/memory_learning.py</c>: explicit, private, execution-backed learning.
/// Capability observations come from installed skill files and host-exported manifests only; validations come from a
/// pinned local fixture or an existing settled execution receipt; lessons are candidate recipes that a person must
/// activate; trends are aggregate hypotheses with explicit coverage; preference import is bound to an exact reviewed
/// preview. Stored recipes are never executed and nothing here reaches the network.
/// </summary>
public static partial class MemoryLearning
{
    /// <summary>The module docstring.</summary>
    public const string Doc = "Explicit, private, execution-backed learning; stored recipes are never executed.";

    /// <summary><c>MAX_OBSERVATIONS</c>.</summary>
    public const int MaxObservations = 200;

    /// <summary><c>MAX_FILE_BYTES</c>.</summary>
    public const int MaxFileBytes = 1024 * 1024;

    /// <summary><c>TREND_TYPES</c>.</summary>
    public static readonly IReadOnlySet<string> TrendTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "meeting_preparation", "focus_fragmentation", "workstream_dependency",
        "draft_edits", "tool_failures", "retrieval_latency", "notification_value",
    };

    private static readonly IReadOnlySet<string> InactiveCapabilityStates = Set("forgotten", "suppressed", "rejected");
    private static readonly IReadOnlySet<string> ExercisedStates = Set("validated", "degraded");
    private static readonly IReadOnlySet<string> UnavailableStates = Set("discovered", "retired");
    private static readonly IReadOnlySet<string> Risks = Set("read_only", "local_mutation", "approval_required_external");
    private static readonly HashSet<string> SettledStates = Set("succeeded", "failed");
    private static readonly IReadOnlySet<string> CoverageStatuses = Set("complete", "partial");
    private static readonly string[] ComparableFields = ["content_hash", "base_environment", "availability", "origin"];
    private static readonly string[] EventFields = ["id", "outcome", "error_category", "level", "executed_at"];
    private static readonly string[] PersonPrefixes = ["person:", "people:", "colleague:"];
    private static readonly string[] PreferenceWords = ["scheduling", "communication", "triage", "brief preferences"];
    private static readonly string[] DataExcluded = ["id", "account", "revision", "status", "created_at", "updated_at"];

    /// <summary><c>DEFAULT_TREND</c> (a fresh dict on every call).</summary>
    public static JsonObject DefaultTrend => new()
    {
        ["min_independent_events"] = 3,
        ["window_days"] = 90,
        ["min_population"] = 3,
        ["min_coverage"] = 1.0,
        ["require_complete"] = true,
    };

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.Ordinal);

    private static readonly string[] EvidenceFields = ["evidence", "counterexamples"];

    // ------------------------------------------------------------------------------ small helpers

    /// <summary><c>_digest(value)</c>: SHA-256 of the canonical JSON.</summary>
    public static string Digest(JsonNode? value) => PyIds.Sha256Hex(StateStore.CanonicalJson(value));

    /// <summary><c>_text(value, field)</c>: a nonempty string of at most 4000 code points.</summary>
    public static string Text(JsonNode? value, string field)
    {
        if (!PyNode.IsStr(value, out var text) || PyRepr.Strip(text).Length == 0 || PyRepr.Length(text) > 4000)
        {
            throw new StateError(field + " requires a bounded nonempty string");
        }

        return text;
    }

    /// <summary><c>_strings(value, field, allow_empty=False)</c>.</summary>
    public static JsonArray Strings(JsonNode? value, string field, bool allowEmpty = false)
    {
        if (value is not JsonArray list || list.Count > MaxObservations || (list.Count == 0 && !allowEmpty))
        {
            throw new StateError(field + " requires a bounded list");
        }

        return PyNode.StrList(list.Select(item => Text(item, field)).ToList());
    }

    /// <summary><c>_time(value)</c>: <c>datetime.fromisoformat(validate_timestamp(value))</c>.</summary>
    public static PyDateTime Time(object? value) => PyDateTime.FromIsoformat(StateStore.ValidateTimestamp(value));

    /// <summary><c>_environment(memory, value)</c>: a canonical (key-sorted) copy of this account's environment.</summary>
    public static JsonObject Environment(MemoryStore memory, JsonNode? value)
    {
        ArgumentNullException.ThrowIfNull(memory);
        if (value is not JsonObject environment || environment.Count == 0)
        {
            throw new StateError("environment requires host and account");
        }

        Text(PyNode.Get(environment, "host"), "environment.host");
        if (!PyJson.PyEquals(PyNode.Get(environment, "account"), memory.Account))
        {
            throw new StateError("environment account does not match this private store");
        }

        if (PyRepr.Length(StateStore.CanonicalJson(environment)) > 16000)
        {
            throw new StateError("environment exceeds the observation budget");
        }

        return (JsonObject)PyJson.Loads(StateStore.CanonicalJson(environment))!;
    }

    /// <summary>
    /// <c>_file(path, expected=None)</c>: up to <see cref="MaxFileBytes"/> + 1 bytes of a regular local file (not a
    /// symlink) and its file source reference. <paramref name="path"/> is any Python value: only a string names a file.
    /// </summary>
    public static (byte[] Payload, JsonObject Source) File(JsonNode? path, JsonNode? expected = null)
    {
        if (!PyNode.IsStr(path, out var text))
        {
            throw new StateError("observation path must name a local file");
        }

        return File(text, expected);
    }

    /// <summary><c>_file(path, expected=None)</c> for a path string.</summary>
    public static (byte[] Payload, JsonObject Source) File(string path, JsonNode? expected = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        var source = PyPath.ExpandUser(PyPath.Normalize(path));
        if (PyPath.IsSymlink(source))
        {
            throw new StateError("observation must be a regular local file, not a symlink");
        }

        source = Resolve(source);
        byte[] payload;
        try
        {
            var fd = MemoryNative.OpenRead(source);
            try
            {
                payload = MemoryNative.Read(fd, MaxFileBytes + 1);
            }
            finally
            {
                PosixNative.Close(fd);
            }
        }
        catch (PyOSError ex)
        {
            throw new StateError("observation file is unavailable", ex);
        }

        if (payload.Length > MaxFileBytes)
        {
            throw new StateError("observation exceeds the file budget");
        }

        var fingerprint = PyIds.Sha256Hex(payload);
        if (expected is not null && !PyJson.PyEquals(JsonValue.Create(fingerprint), expected))
        {
            throw new StateError("observation file revision changed");
        }

        return (payload, new JsonObject { ["kind"] = "file", ["ref"] = source, ["revision"] = fingerprint });
    }

    /// <summary>
    /// <c>Path(path).resolve()</c> as CPython 3.13 raises it: <c>realpath</c>'s <c>os.lstat</c> refuses a NUL with its own
    /// text (State.Core's <see cref="PyPath.Resolve"/> reports the encoder's <c>embedded null byte</c>).
    /// </summary>
    internal static string Resolve(string path)
    {
        try
        {
            return PyPath.Resolve(path);
        }
        catch (PyValueError ex) when (ex.GetType() == typeof(PyValueError) && ex.Message == "embedded null byte")
        {
            throw new PyValueError("lstat: embedded null character in path");
        }
    }

    /// <summary><c>_json_file(path, expected=None)</c>: the file's JSON object (<c>parse_json</c> of the bytes).</summary>
    public static (JsonObject Data, JsonObject Source) JsonFile(JsonNode? path, JsonNode? expected = null)
    {
        var (payload, source) = File(path, expected);
        return (ParseObservation(payload), source);
    }

    /// <summary><c>_json_file</c> for a path string.</summary>
    public static (JsonObject Data, JsonObject Source) JsonFile(string path, JsonNode? expected = null)
    {
        var (payload, source) = File(path, expected);
        return (ParseObservation(payload), source);
    }

    private static JsonObject ParseObservation(byte[] payload)
    {
        JsonNode? data;
        try
        {
            // parse_json(bytes): json.loads decodes the detected encoding with surrogatepass first. Every failure on the
            // way (UnicodeDecodeError, JSONDecodeError, StateError) is a ValueError, which _json_file maps to one message.
            data = StateStore.ParseJson(PyIO.DecodeJsonBytes(payload));
        }
        catch (PyValueError ex)
        {
            throw new StateError("observation must contain valid JSON", ex);
        }

        return data as JsonObject ?? throw new StateError("observation JSON must be an object");
    }

    /// <summary><c>_data(record)</c>: a stored record without its identity, revision, status and timestamps.</summary>
    public static JsonObject Data(JsonObject record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var result = new JsonObject();
        foreach (var (key, value) in record)
        {
            if (!DataExcluded.Contains(key))
            {
                result[key] = PyNode.Copy(value);
            }
        }

        return result;
    }

    /// <summary><c>_refs(values)</c>: one reference per canonical digest, sorted by canonical JSON.</summary>
    public static JsonArray Refs(IEnumerable<JsonNode?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var order = new List<string>();
        var unique = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var digest = Digest(value);
            if (!unique.ContainsKey(digest))
            {
                order.Add(digest);
            }

            unique[digest] = value;
        }

        var sorted = PySort.SortedBy(order.Select(digest => unique[digest]), value => StateStore.CanonicalJson(value),
                                     (left, right) => PyRepr.CompareStrings(left, right) < 0);
        return new JsonArray(sorted.Select(PyNode.Copy).ToArray());
    }

    /// <summary><c>_prior(memory, key, domain="agent")</c>: the record stored under <paramref name="key"/>, if any.</summary>
    public static JsonObject? Prior(MemoryStore memory, JsonNode? key, string domain = "agent")
    {
        ArgumentNullException.ThrowIfNull(memory);
        var identity = memory.RecordId(domain, key);
        return memory.List(domain).OfType<JsonObject>().FirstOrDefault(row => PyNode.Is(row["id"], identity));
    }

    /// <summary><c>_conditional(prior, revision)</c>: the conditional-revision rules for learning records.</summary>
    public static void Conditional(JsonObject? prior, JsonNode? revision)
    {
        if (revision is not null && !PyJson.IsInt(revision))
        {
            throw new StateError("learning revision must be an integer");
        }

        var exists = prior is { Count: > 0 };
        if (exists && (!PyJson.IsInt(revision) || !PyJson.PyEquals(revision, PyNode.Index(prior, "revision"))))
        {
            throw new StateError("learning revision conflict; review the current record");
        }

        if (!exists && revision is not null && !PyJson.PyEquals(revision, 1))
        {
            throw new StateError("new learning revision must be 1");
        }
    }

    /// <summary><c>_normalise_proposal(memory, data)</c>: the store's normalisation and work-source resolution.</summary>
    public static JsonObject NormaliseProposal(MemoryStore memory, JsonNode? data)
    {
        ArgumentNullException.ThrowIfNull(memory);
        return memory.ResolveWorkSources(memory.NormaliseData(data));
    }

    /// <summary><c>_validation_scope(metadata)</c>: the base environment and content hash a validation applies to.</summary>
    public static string ValidationScope(JsonNode? metadata)
    {
        var meta = PyNode.Dict(metadata);
        return Digest(new JsonArray(PyNode.Copy(PyNode.Get(meta, "base_environment")), PyNode.Copy(PyNode.Get(meta, "content_hash"))));
    }

    private static JsonObject Obj(JsonNode? value, string attribute = "get") => PyNode.Dict(value, attribute);

    private static JsonNode? Get(JsonNode? value, string key, JsonNode? fallback = null) => PyNode.Get(Obj(value), key, fallback);

    private static JsonNode? Item(JsonNode? value, string key) => PyNode.Index(value, key);

    private static bool Eq(JsonNode? left, JsonNode? right) => PyJson.PyEquals(left, right);

    private static bool Eq(JsonNode? left, string right) => PyJson.PyEquals(left, JsonValue.Create(right));

    /// <summary><c>set(value) == {keys}</c> for a dict.</summary>
    private static bool KeysAre(JsonObject value, params string[] keys) =>
        value.Count == keys.Length && keys.All(value.ContainsKey);

    /// <summary><c>key in d</c> for a dict whose keys are strings: unhashable keys raise <c>TypeError</c>.</summary>
    private static bool InKeys(JsonNode? key, OrderedDictionary<string, JsonObject> keys)
    {
        PyNode.RequireHashable(key);
        return PyNode.AsString(key) is { } text && keys.ContainsKey(text);
    }

    /// <summary><c>left + right</c> for lists, with Python's <c>TypeError</c> otherwise.</summary>
    private static List<JsonNode?> Concat(JsonNode? left, JsonNode? right)
    {
        if (left is JsonArray first && right is JsonArray second)
        {
            return [.. first, .. second];
        }

        if (left is JsonArray)
        {
            throw new PyTypeError("can only concatenate list (not \"" + PyJson.TypeName(right) + "\") to list");
        }

        if (PyJson.IsStr(left))
        {
            throw new PyTypeError("can only concatenate str (not \"" + PyJson.TypeName(right) + "\") to str");
        }

        throw new PyTypeError("unsupported operand type(s) for +: '" + PyJson.TypeName(left) + "' and '" + PyJson.TypeName(right) + "'");
    }
}

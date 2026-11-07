using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Margo.Cloud.Core;

/// <summary>Route states. Python: <c>instances.READY</c>, <c>PROVISIONING</c>, ... and <c>STATES</c>.</summary>
public static class RouteStates
{
    public const string Ready = "ready";
    public const string Provisioning = "provisioning";
    public const string Suspended = "suspended";
    public const string Retired = "retired";
    public const string Failed = "failed";

    /// <summary>Every state.</summary>
    public static IReadOnlyList<string> All { get; } = [Ready, Provisioning, Suspended, Retired, Failed];

    /// <summary>Whether <paramref name="state"/> is one of <see cref="All"/>.</summary>
    public static bool IsKnown(string? state) => state is Ready or Provisioning or Suspended or Retired or Failed;
}

/// <summary>
/// A snapshot of one route entry. The entry is open-ended JSON (a newer writer may add fields), so
/// the typed properties read the known fields and <see cref="ToJsonObject"/> returns all of them.
/// A property is <see langword="null"/> when the field is missing or not of the expected JSON type.
/// </summary>
public sealed class RouteEntry
{
    private readonly JsonObject _fields;

    internal RouteEntry(JsonObject fields)
    {
        _fields = (JsonObject)fields.DeepClone();
    }

    public string? AgentIdentityId => Text("agent_identity_id");

    public string? AgentUserId => Text("agent_user_id");

    /// <summary>One of <see cref="RouteStates"/>; <see cref="RouteTable"/> guarantees it for its entries.</summary>
    public string? State => Text("state");

    /// <summary>The instance's internal HTTPS address, once known.</summary>
    public string? Url => Text("url");

    /// <summary>Incremented by every <see cref="RouteTable.Put"/>; <see langword="null"/> if not an integer that fits.</summary>
    public long? Revision =>
        _fields["revision"] is JsonValue value && value.GetValueKind() == JsonValueKind.Number
        && PythonValues.TryNumber(value, out var integer, out _) && integer >= long.MinValue && integer <= long.MaxValue
            ? (long)integer
            : null;

    /// <summary>UTC time of the last change, as Python's <c>isoformat(timespec="seconds")</c>.</summary>
    public string? UpdatedAt => Text("updated_at");

    /// <summary>Why the last attempt failed (an exception type name), at most 500 code points.</summary>
    public string? Error => Text("error");

    public string? RetiredAt => Text("retired_at");

    /// <summary>All fields of the entry, as a copy.</summary>
    public JsonObject ToJsonObject() => (JsonObject)_fields.DeepClone();

    private string? Text(string name) => PythonValues.AsString(_fields[name]);
}

/// <summary>The result of <see cref="RouteTable.Resolve"/>: both <see langword="null"/> when the
/// identifiers cannot form an instance key; <see cref="Entry"/> <see langword="null"/> when the
/// instance has no route yet.</summary>
public sealed record RouteLookup(string? Key, RouteEntry? Entry);

/// <summary>
/// Where each instance lives. Written only by the provisioner; read by the router.
/// Python: <c>instances.RouteTable</c>. Not thread-safe, like the Python class: load, change and
/// save one table per operation.
/// </summary>
/// <remarks>
/// The blob text is shared by the Python and C# services during the cutover: <see cref="Dumps"/>
/// writes exactly what Python's <c>json.dumps(..., sort_keys=True, indent=1)</c> writes, and entries
/// keep fields this version does not know.
/// </remarks>
public sealed class RouteTable
{
    /// <summary>The only route table format. Python: <c>instances.SCHEMA_VERSION</c>.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The longest error text kept on an entry, in code points.</summary>
    public const int MaxErrorLength = 500;

    private readonly JsonObject _routes;
    private readonly TimeProvider _time;

    /// <summary>An empty table.</summary>
    /// <param name="timeProvider">The clock for <c>updated_at</c>; the system clock by default.</param>
    public RouteTable(TimeProvider? timeProvider = null)
        : this(new JsonObject(), timeProvider)
    {
    }

    private RouteTable(JsonObject routes, TimeProvider? timeProvider)
    {
        _routes = routes;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Every route, keyed by instance key, in table order.</summary>
    public IReadOnlyList<KeyValuePair<string, RouteEntry>> Routes =>
        _routes.Select(pair => new KeyValuePair<string, RouteEntry>(pair.Key, new RouteEntry((JsonObject)pair.Value!))).ToList();

    public int Count => _routes.Count;

    /// <summary>Parse the route table blob. Empty or <see langword="null"/> text is an empty table.</summary>
    /// <exception cref="RouteException">The text is not a valid route table; nothing is loaded.</exception>
    public static RouteTable Loads(string? text, TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new RouteTable(timeProvider);
        }

        JsonNode? data;
        try
        {
            data = PythonJson.Loads(text);
        }
        catch (JsonException exc)
        {
            throw new RouteException("route table is not JSON", exc);
        }

        if (data is not JsonObject root || !PythonValues.EqualsOne(root["schema_version"]))
        {
            throw new RouteException("unsupported route table");
        }

        if (root["routes"] is not JsonObject routes)
        {
            throw new RouteException("route table has no routes");
        }

        foreach (var (key, entry) in routes)
        {
            // Python raises TypeError for a list or object state (unhashable); here it is malformed too.
            if (!Instances.IsInstanceKey(key) || entry is not JsonObject fields || !RouteStates.IsKnown(PythonValues.AsString(fields["state"])))
            {
                throw new RouteException("malformed route entry");
            }
        }

        root.Remove("routes");
        return new RouteTable(routes, timeProvider);
    }

    /// <summary>The blob text: Python's <c>json.dumps({"schema_version": 1, "routes": ...}, sort_keys=True, indent=1)</c>.</summary>
    public string Dumps()
    {
        var root = new JsonObject
        {
            ["schema_version"] = SchemaVersion,
            ["routes"] = _routes.DeepClone(),
        };
        return PythonJson.Dumps(root, sortKeys: true, indent: 1);
    }

    /// <summary>The route for <paramref name="key"/>, or <see langword="null"/>.</summary>
    public RouteEntry? Get(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _routes[key] is JsonObject fields ? new RouteEntry(fields) : null;
    }

    /// <summary>The key and route for one hired instance. Identifiers that cannot form a key give
    /// <c>(null, null)</c>; any other failure (a lone surrogate) propagates, as in Python.</summary>
    public RouteLookup Resolve(string? agentIdentityId, string? agentUserId)
    {
        string key;
        try
        {
            key = Instances.InstanceKey(agentIdentityId, agentUserId);
        }
        catch (BindingException)
        {
            return new RouteLookup(null, null);
        }

        return new RouteLookup(key, Get(key));
    }

    /// <summary>
    /// Record a new state for one instance. The entry is rebuilt from the known fields; <c>url</c> is
    /// kept from the previous entry when <paramref name="url"/> is <see langword="null"/>; the
    /// revision is one more than the previous one; <c>retired_at</c> is set on retirement. The key is
    /// not validated here, as in Python.
    /// </summary>
    /// <exception cref="RouteException">The state is unknown, or the previous revision is not an
    /// integer (Python raises TypeError or ValueError there); the table is unchanged.</exception>
    public RouteEntry Put(string key, string? agentIdentityId, string? agentUserId, string? state, string? url = null, string? error = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!RouteStates.IsKnown(state))
        {
            throw new RouteException("unknown route state");
        }

        var previous = _routes[key] as JsonObject ?? new JsonObject();
        BigInteger revision = BigInteger.Zero;
        if (previous.TryGetPropertyValue("revision", out var previousRevision) && !PythonValues.TryToInt(previousRevision, out revision))
        {
            throw new RouteException("malformed route entry");
        }

        var updatedAt = _time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'", CultureInfo.InvariantCulture);
        var entry = new JsonObject
        {
            ["agent_identity_id"] = agentIdentityId,
            ["agent_user_id"] = agentUserId,
            ["state"] = state,
            ["url"] = url is not null ? JsonValue.Create(url) : previous["url"]?.DeepClone(),
            ["revision"] = Integer(revision + 1),
            ["updated_at"] = updatedAt,
        };
        if (!string.IsNullOrEmpty(error))
        {
            entry["error"] = PythonText.TruncateCodePoints(error, MaxErrorLength);
        }

        if (state == RouteStates.Retired)
        {
            entry["retired_at"] = updatedAt;
        }

        _routes[key] = entry;
        return new RouteEntry(entry);
    }

    private static JsonNode Integer(BigInteger value) =>
        value >= long.MinValue && value <= long.MaxValue
            ? JsonValue.Create((long)value)
            : PythonJson.Loads(value.ToString(CultureInfo.InvariantCulture))!;
}

using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.State.Memory;

/// <summary>
/// Port of <c>skills/chief-of-staff/scripts/memory_store.py</c>: authoritative account-scoped memory records and
/// lifecycle storage (records, revisions, links, index jobs, tombstones, usage, policy rows) in the private
/// account database. Method names follow the Python ones; the private helpers other memory modules call
/// (<c>_identity</c>, <c>_normalise_data</c>, <c>_resolve_work_sources</c>, <c>_human</c>, <c>_work_sources_current</c>,
/// <c>_environment_subset</c>, <c>_row</c>, <c>_derived_descendants</c>, <c>_file_digest</c>, <c>_put</c>) are public here.
/// Values are Python values as <see cref="JsonNode"/> (State.Core's PyJson value model): parameters that Python
/// takes from model-supplied JSON are <see cref="JsonNode"/>, and <see cref="PyArg"/> where Python's default is not
/// <c>None</c>. Results are <see cref="JsonObject"/>s with Python's key order.
/// </summary>
public sealed class MemoryStore : IDisposable
{
    /// <summary><c>SCHEMA_VERSION</c>.</summary>
    public const string SchemaVersion = "2";

    /// <summary><c>MAX_SOURCE_FILE_BYTES</c>.</summary>
    public const int MaxSourceFileBytes = 1024 * 1024;

    /// <summary><c>MAX_MEMORY_TEXT_CHARS</c> (<c>memory_encoder.MAX_TEXT_CHARS</c>).</summary>
    public const int MaxMemoryTextChars = MemoryEncoder.MaxTextChars;

    /// <summary><c>PREFERENCES_RECOVERY</c>.</summary>
    public const string PreferencesRecovery =
        "Imported preferences changed or are unavailable. Restore/correct the private preferences file, "
        + "run memory_state.py preferences-preview PRIVATE_PREFERENCES_FILE, then review and run "
        + "preferences-import with fresh approval. Stale constraints remain blocked until revalidated.";

    public static readonly IReadOnlySet<string> Domains = Set("user", "agent");

    public static readonly IReadOnlySet<string> Kinds = Set("profile", "preference", "person", "project", "decision", "episode",
                                                          "lesson", "capability", "trend");

    public static readonly IReadOnlySet<string> Authorities = Set("user_confirmed", "source_observed", "inferred");

    public static readonly IReadOnlySet<string> Sensitivities = Set("public", "private", "sensitive");

    public static readonly IReadOnlySet<string> Uses = Set("reasoning", "drafting");

    public static readonly IReadOnlySet<string> SourceKinds = Set("user_statement", "file", "work_source", "tool_result", "memory_record",
                                                                "session_checkpoint");

    public static readonly IReadOnlySet<string> Statuses = Set("candidate", "active", "rejected", "disputed", "stale", "superseded",
                                                             "suppressed", "forgotten");

    public static readonly IReadOnlySet<string> TypedRelations = Set("owns", "reports_to", "depends_on", "constrains", "advances",
                                                                   "discusses", "contradicts");

    public static readonly IReadOnlySet<string> Relations = Set(["derives_from", "about", "related_to", "supersedes", .. TypedRelations]);

    public static readonly IReadOnlySet<string> ObservationalKinds = Set([.. Kinds.Where(kind => kind is not ("preference" or "lesson"))]);

    /// <summary><c>TRANSITIONS</c>: the allowed status changes.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Transitions = new Dictionary<string, IReadOnlySet<string>>
    {
        ["candidate"] = Set("active", "rejected", "disputed", "stale"),
        ["active"] = Set("disputed", "stale", "superseded", "suppressed"),
        ["disputed"] = Set("active", "rejected", "stale", "superseded"),
        ["stale"] = Set("active", "rejected", "superseded"),
        ["rejected"] = Set(),
        ["superseded"] = Set(),
        ["suppressed"] = Set("active", "disputed", "stale", "superseded"),
        ["forgotten"] = Set(),
    };

    /// <summary><c>SCHEMA_V1</c> (byte-identical to the Python text).</summary>
    public const string SchemaV1 = @"
CREATE TABLE IF NOT EXISTS memory_meta (
 key TEXT PRIMARY KEY, value TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS memory_records (
 id TEXT PRIMARY KEY, account TEXT NOT NULL, key_hash TEXT NOT NULL,
 revision INTEGER NOT NULL, domain TEXT NOT NULL, kind TEXT NOT NULL,
 status TEXT NOT NULL, data TEXT NOT NULL, indexed_content_hash TEXT,
 created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
 UNIQUE(account,key_hash)
);
CREATE TABLE IF NOT EXISTS memory_revisions (
 memory_id TEXT NOT NULL REFERENCES memory_records(id), revision INTEGER NOT NULL,
 status TEXT NOT NULL, domain TEXT NOT NULL, kind TEXT NOT NULL,
 data TEXT NOT NULL, evidence TEXT, content_hash TEXT, created_at TEXT NOT NULL,
 PRIMARY KEY(memory_id,revision)
);
CREATE TABLE IF NOT EXISTS memory_links (
 source_id TEXT NOT NULL REFERENCES memory_records(id),
 target_id TEXT NOT NULL REFERENCES memory_records(id),
 relation TEXT NOT NULL, created_at TEXT NOT NULL,
 PRIMARY KEY(source_id,target_id,relation)
);
CREATE TABLE IF NOT EXISTS memory_jobs (
 memory_id TEXT PRIMARY KEY REFERENCES memory_records(id), revision INTEGER NOT NULL,
 op TEXT NOT NULL, content_hash TEXT NOT NULL, created_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS memory_tombstones (
 id TEXT PRIMARY KEY, account TEXT NOT NULL, key_hash TEXT NOT NULL,
 forgotten_at TEXT NOT NULL, UNIQUE(account,key_hash)
);
CREATE INDEX IF NOT EXISTS memory_records_account_status
 ON memory_records(account,status);
CREATE INDEX IF NOT EXISTS memory_links_target
 ON memory_links(target_id,relation);
";

    /// <summary><c>SCHEMA</c>: version 2 (link data, policy and usage tables).</summary>
    public static readonly string Schema = SchemaV1.Replace(
        "relation TEXT NOT NULL, created_at TEXT NOT NULL,",
        "relation TEXT NOT NULL, created_at TEXT NOT NULL, data TEXT NOT NULL DEFAULT '{}',",
        StringComparison.Ordinal) + @"
CREATE TABLE IF NOT EXISTS memory_policy (
 account TEXT PRIMARY KEY, revision INTEGER NOT NULL,
 data TEXT NOT NULL, evidence TEXT NOT NULL, updated_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS memory_usage (
 id TEXT PRIMARY KEY, account TEXT NOT NULL, routine TEXT NOT NULL,
 refs TEXT NOT NULL, outcome TEXT NOT NULL, created_at TEXT NOT NULL
);
";

    private static readonly HashSet<string> DataFields = Set("domain", "kind", "title", "text", "authority", "scope", "sensitivity",
                                                                  "allowed_uses", "entities", "routines", "source_refs", "valid_from",
                                                                  "valid_to", "review_after", "metadata", "identity");

    private static readonly string[] SchemaPragmas = ["table_info", "foreign_key_list"];
    private static readonly HashSet<string> RefFields = Set("kind", "ref", "revision", "verified_at");
    private static readonly string[] SemanticTableOrder = ["semantic_vectors", "semantic_documents", "semantic_fts"];

    private Dictionary<(string Path, (ulong, ulong, long, long, long) Signature), string?>? _fileChecks;

    /// <summary>
    /// <c>MemoryStore(account, state_root, migrate, read_only)</c>: opens the account database and checks (or, when
    /// writable, creates) the memory schema. A read-only open never creates or repairs state.
    /// </summary>
    public MemoryStore(string? account = null, string? stateRoot = null, bool migrate = false, bool readOnly = false)
    {
        if (migrate && readOnly)
        {
            throw new StateError("migration requires explicit writable access");
        }

        Account = StateStore.ResolveAccount(account);
        ReadOnly = readOnly;
        Conn = StateStore.Connect(Account, stateRoot, readOnly);
        try
        {
            Conn.Execute("PRAGMA foreign_keys=ON");
            if (!readOnly)
            {
                Conn.Execute("PRAGMA secure_delete=ON");
            }

            InitializeSchema(migrate);
        }
        catch
        {
            Conn.Close();
            throw;
        }
    }

    /// <summary><c>memory.account</c>.</summary>
    public string Account { get; }

    /// <summary><c>memory.read_only</c>.</summary>
    public bool ReadOnly { get; }

    /// <summary><c>memory.conn</c>: the account connection (rows support names, like <c>sqlite3.Row</c>).</summary>
    public PyConnection Conn { get; }

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.Ordinal);

    // ------------------------------------------------------------------------------ module functions

    /// <summary><c>search_text(record)</c>: the stable text covered by memory job content hashes.</summary>
    public static string SearchText(JsonNode? record)
    {
        if (record is not JsonObject value)
        {
            throw new StateError("memory record must be a JSON object");
        }

        var parts = new JsonNode?[]
        {
            Py.Has(value, "title") ? value["title"] : JsonValue.Create(""),
            Py.Has(value, "text") ? value["text"] : JsonValue.Create(""),
            JsonValue.Create(JoinSpace(Py.Has(value, "entities") ? value["entities"] : new JsonArray())),
            JsonValue.Create(JoinSpace(Py.Has(value, "routines") ? value["routines"] : new JsonArray())),
            Py.Has(value, "scope") ? value["scope"] : JsonValue.Create(""),
        };
        var kept = parts.Where(PyJson.Truthy).ToList();
        var builder = new StringBuilder();
        for (var i = 0; i < kept.Count; i++)
        {
            if (Py.AsString(kept[i]) is not { } text)
            {
                throw new PyTypeError("sequence item " + i.ToString(CultureInfo.InvariantCulture) + ": expected str instance, "
                                      + PyJson.TypeName(kept[i]) + " found");
            }

            if (i > 0)
            {
                builder.Append('\n');
            }

            builder.Append(text);
        }

        return builder.ToString();
    }

    /// <summary><c>" ".join(value)</c> for a JSON value.</summary>
    private static string JoinSpace(JsonNode? value)
    {
        IEnumerable<JsonNode?> items = value switch
        {
            JsonArray array => array,
            JsonObject obj => obj.Select(pair => (JsonNode?)JsonValue.Create(pair.Key)),
            JsonValue scalar when PyJson.Scalar(scalar) is string text => PyRepr.CodePoints(text).Select(point => (JsonNode?)JsonValue.Create(Py.CodePointString(point))),
            _ => throw new PyTypeError("can only join an iterable"),
        };
        var strings = new List<string>();
        var index = 0;
        foreach (var item in items)
        {
            strings.Add(Py.AsString(item) ?? throw new PyTypeError("sequence item " + index.ToString(CultureInfo.InvariantCulture)
                                                                   + ": expected str instance, " + PyJson.TypeName(item) + " found"));
            index++;
        }

        return string.Join(" ", strings);
    }

    /// <summary><c>memory_identity(account, domain, key)</c>: (<c>mem_</c> + 40 hex, full SHA-256 key hash).</summary>
    public static (string Id, string KeyHash) MemoryIdentity(string account, JsonNode? domain, JsonNode? key)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (!Py.In(domain, Domains))
        {
            throw new StateError("domain must be user or agent");
        }

        var text = TextField(key, "key");
        if (PyRepr.Length(text) > 4096)
        {
            throw new StateError("key is too long");
        }

        var keyHash = PyIds.Sha256Hex(account + "\0" + Py.AsString(domain) + "\0" + text);
        return ("mem_" + keyHash[..40], keyHash);
    }

    /// <summary><c>scoped_entity_key(value)</c>: the account-local graph key of an entity, or null for a display name.</summary>
    public static string? ScopedEntityKey(JsonNode? value)
    {
        if (Py.AsString(value) is not { } text || text.Length == 0 || PyRepr.Length(text) > 4096)
        {
            return null;
        }

        if (text == "user")
        {
            return text;
        }

        if (text.StartsWith("identity:", StringComparison.Ordinal))
        {
            JsonNode? parts;
            try
            {
                parts = StateStore.ParseJson(text["identity:".Length..]);
            }
            catch (StateError)
            {
                return null;
            }

            if (parts is JsonArray list && list.Count == 3 && list.All(Py.IsText))
            {
                return "identity:" + StateStore.CanonicalJson(list);
            }

            return null;
        }

        if (PyPatterns.NamespacedKey(text) || PyPatterns.RecordKey(text))
        {
            return text;
        }

        return null;
    }

    // ------------------------------------------------------------------------------ validation helpers

    /// <summary><c>_text(value, field)</c>.</summary>
    internal static string TextField(JsonNode? value, string field) =>
        Py.IsText(value) ? Py.AsString(value)! : throw new StateError(field + " must be a nonempty string");

    /// <summary><c>_integer(value, field)</c>: an int, not a bool.</summary>
    internal static BigInteger Integer(JsonNode? value, string field) =>
        PyJson.IsInt(value) ? PyJson.AsInt(value) : throw new StateError(field + " must be an integer");

    /// <summary><c>_json_object(value, field, allow_empty)</c>.</summary>
    internal static JsonObject JsonObjectField(JsonNode? value, string field, bool allowEmpty = true)
    {
        if (value is not JsonObject obj || (!allowEmpty && obj.Count == 0))
        {
            throw new StateError(field + " must be " + (allowEmpty ? "" : "a nonempty ") + "a JSON object");
        }

        StateStore.CanonicalJson(obj);
        return obj;
    }

    /// <summary><c>_parse_time(value)</c>: <c>datetime.fromisoformat(validate_timestamp(value))</c>.</summary>
    internal static PyDateTime ParseTime(JsonNode? value) => PyDateTime.FromIsoformat(StateStore.ValidateTimestamp(value));

    /// <summary><c>dict.get(key, default)</c> on a value Python expects to be a dict (<c>AttributeError</c> otherwise).</summary>
    internal static JsonNode? DictGet(JsonNode? value, string key, JsonNode? fallback = null)
    {
        if (value is not JsonObject obj)
        {
            throw new PyAttributeError("'" + PyJson.TypeName(value) + "' object has no attribute 'get'");
        }

        return obj.TryGetPropertyValue(key, out var found) ? found : fallback;
    }

    /// <summary><c>value[key]</c> on a dict (<c>KeyError</c> when absent).</summary>
    internal static JsonNode? Item(JsonNode? value, string key) => value switch
    {
        JsonObject obj => obj.TryGetPropertyValue(key, out var found) ? found : throw new PyKeyError(key),
        JsonArray => throw new PyTypeError("list indices must be integers or slices, not str"),
        JsonValue scalar when PyJson.Scalar(scalar) is string => throw new PyTypeError("string indices must be integers, not 'str'"),
        _ => throw new PyTypeError("'" + PyJson.TypeName(value) + "' object is not subscriptable"),
    };

    private static string RowText(PyRow row, string column) => row[column] as string ?? Py.Str(row[column]);

    // ------------------------------------------------------------------------------ schema and transactions

    private void InitializeSchema(bool migrate)
    {
        var statements = Schema.Split(';').Select(PyRepr.Strip).Where(part => part.Length > 0).ToList();
        var expected = statements.Where(statement => statement.StartsWith("CREATE TABLE", StringComparison.Ordinal))
                                 .Select(statement => statement.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[5])
                                 .ToHashSet(StringComparer.Ordinal);
        Transaction(() =>
        {
            var allTables = Conn.Execute("SELECT name FROM sqlite_master WHERE type='table'").Select(row => row[0] as string)
                                .Where(name => name is not null).Select(name => name!).ToHashSet(StringComparer.Ordinal);
            var existing = allTables.Where(name => name.StartsWith("memory_", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
            var markerRow = Conn.Execute("SELECT value FROM margo_meta WHERE key='memory_schema_version'").FetchOne();
            var hasMarker = markerRow is not null;
            var marker = markerRow?[0];
            if (ReadOnly && !hasMarker && existing.Count == 0)
            {
                throw new NotInitialized("Memory is not initialized; run memory_state.py init explicitly.");
            }

            if (hasMarker && Py.Equal(marker, "1") && migrate)
            {
                MigrateV1(existing);
                existing = expected;
                marker = SchemaVersion;
            }

            if (hasMarker && !Py.Equal(marker, SchemaVersion))
            {
                throw new StateError("memory schema marker mismatch; explicit migration required");
            }

            if (hasMarker && !existing.SetEquals(expected))
            {
                throw new StateError("incomplete memory schema; refusing to recreate missing history");
            }

            if (existing.Count > 0 && !hasMarker)
            {
                throw new StateError("memory schema has no global marker; explicit recovery required");
            }

            if (existing.Count > 0 && !existing.SetEquals(expected))
            {
                throw new StateError("unknown or incomplete memory schema; explicit recovery required");
            }

            if (existing.Count > 0)
            {
                var local = Conn.Execute("SELECT value FROM memory_meta WHERE key='schema_version'").FetchOne();
                if (local is null || !Py.Equal(local[0], SchemaVersion))
                {
                    throw new StateError("unsupported or missing memory schema version");
                }

                using var reference = PyConnection.Connect(":memory:");
                reference.ExecuteScript(Schema);
                foreach (var table in Py.Sorted(expected))
                {
                    foreach (var pragma in SchemaPragmas)
                    {
                        if (!SameRows(Conn, reference, "PRAGMA " + pragma + "(" + table + ")"))
                        {
                            throw new StateError("memory schema contract mismatch: " + table);
                        }
                    }
                }
            }

            if (ReadOnly)
            {
                return;
            }

            foreach (var statement in statements)
            {
                Conn.Execute(statement);
            }

            Conn.Execute("INSERT OR IGNORE INTO memory_meta VALUES ('schema_version',?)", SchemaVersion);
            Conn.Execute("INSERT OR IGNORE INTO margo_meta VALUES ('memory_schema_version',?)", SchemaVersion);
        });
    }

    private static bool SameRows(PyConnection actual, PyConnection wanted, string sql)
    {
        var left = actual.Execute(sql).FetchAll();
        var right = wanted.Execute(sql).FetchAll();
        return left.Count == right.Count && left.Zip(right).All(pair => pair.First.ValuesEqual(pair.Second));
    }

    private void MigrateV1(HashSet<string> existing)
    {
        using (var reference = PyConnection.Connect(":memory:"))
        {
            reference.ExecuteScript(SchemaV1);
            var expected = reference.Execute("SELECT name FROM sqlite_master WHERE type='table'").Select(row => (string)row[0]!)
                                    .ToHashSet(StringComparer.Ordinal);
            var local = Conn.Execute("SELECT value FROM memory_meta WHERE key='schema_version'").FetchOne();
            if (!existing.SetEquals(expected) || local is null || !Py.Equal(local[0], "1"))
            {
                throw new StateError("incomplete legacy memory schema; migration refused");
            }

            // Python iterates a set (hash order); a fixed order reports the first drifted table (PORTING.md).
            foreach (var table in Py.Sorted(expected))
            {
                foreach (var pragma in SchemaPragmas)
                {
                    if (!SameRows(Conn, reference, "PRAGMA " + pragma + "(" + table + ")"))
                    {
                        throw new StateError("legacy memory schema contract mismatch: " + table);
                    }
                }
            }
        }

        Conn.Execute("ALTER TABLE memory_links ADD COLUMN data TEXT NOT NULL DEFAULT '{}'");
        foreach (var statement in Schema.Split(';'))
        {
            if (PyRepr.Strip(statement).Length > 0)
            {
                Conn.Execute(statement);
            }
        }

        Conn.Execute("UPDATE memory_meta SET value=? WHERE key='schema_version'", SchemaVersion);
        Conn.Execute("UPDATE margo_meta SET value=? WHERE key='memory_schema_version'", SchemaVersion);
    }

    /// <summary>
    /// <c>with memory.transaction(): ...</c>: the outermost caller owns <c>BEGIN IMMEDIATE</c> (<c>BEGIN</c> when
    /// read-only) and the commit; nested callers get a savepoint that is released, or rolled back and released.
    /// </summary>
    public T Transaction<T>(Func<T> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var nested = Conn.InTransaction;
        var savepoint = nested ? "memory_" + PyIds.Uuid4Hex() : null;
        try
        {
            Conn.Execute(nested ? "SAVEPOINT " + savepoint : ReadOnly ? "BEGIN" : "BEGIN IMMEDIATE");
            var result = body();
            if (nested)
            {
                Conn.Execute("RELEASE SAVEPOINT " + savepoint);
            }
            else
            {
                Conn.Commit();
            }

            return result;
        }
        catch
        {
            if (nested)
            {
                Conn.Execute("ROLLBACK TO SAVEPOINT " + savepoint);
                Conn.Execute("RELEASE SAVEPOINT " + savepoint);
            }
            else
            {
                Conn.Rollback();
            }

            throw;
        }
    }

    /// <summary><c>with memory.transaction(): body</c>.</summary>
    public void Transaction(Action body)
    {
        ArgumentNullException.ThrowIfNull(body);
        Transaction(() =>
        {
            body();
            return 0;
        });
    }

    /// <summary><c>memory.close()</c>.</summary>
    public void Close() => Conn.Close();

    public void Dispose() => Close();

    // ------------------------------------------------------------------------------ identity and normalisation

    /// <summary><c>_identity(domain, key)</c>.</summary>
    public (string Id, string KeyHash) Identity(JsonNode? domain, JsonNode? key) => MemoryIdentity(Account, domain, key);

    /// <summary><c>record_id(domain, key)</c>.</summary>
    public string RecordId(JsonNode? domain, JsonNode? key) => Identity(domain, key).Id;

    private static JsonArray NormaliseStrings(JsonNode? value, string field, IReadOnlySet<string>? allowed = null)
    {
        if (value is null)
        {
            return [];
        }

        if (value is not JsonArray list)
        {
            throw new StateError(field + " must be a list");
        }

        var result = new List<string>();
        foreach (var entry in list)
        {
            var text = TextField(entry, field + " entry");
            if (allowed is not null && !allowed.Contains(text))
            {
                throw new StateError("unsupported " + field + " entry: " + text);
            }

            if (!result.Contains(text, StringComparer.Ordinal))
            {
                result.Add(text);
            }
        }

        return Py.StrArray(Py.Sorted(result));
    }

    /// <summary><c>_normalise_source_refs(refs)</c>: validated, de-duplicated and sorted by canonical JSON.</summary>
    public static JsonArray NormaliseSourceRefs(JsonNode? refs)
    {
        if (refs is not JsonArray list || list.Count == 0)
        {
            throw new StateError("source_refs must be a nonempty list");
        }

        var result = new List<JsonObject>();
        var seen = new HashSet<(string, string, string?)>();
        foreach (var item in list)
        {
            if (item is not JsonObject reference)
            {
                throw new StateError("each source reference must be an object");
            }

            var unknown = reference.Select(pair => pair.Key).Where(key => !RefFields.Contains(key)).ToList();
            if (unknown.Count > 0)
            {
                throw new StateError("unknown source reference fields: " + string.Join(", ", Py.Sorted(unknown)));
            }

            if (!Py.In(Py.Get(reference, "kind"), SourceKinds))
            {
                throw new StateError("unsupported source reference kind");
            }

            TextField(Py.Get(reference, "ref"), "source reference");
            var kind = Py.AsString(reference["kind"])!;
            var value = new JsonObject { ["kind"] = kind, ["ref"] = Py.AsString(reference["ref"]) };
            if (kind == "file")
            {
                var revision = Py.Has(reference, "revision") ? Py.AsString(reference["revision"]) : null;
                if (revision is null || PyRepr.Length(revision) != 64 || PyRepr.CodePoints(revision).Any(c => !"0123456789abcdef".Contains((char)c, StringComparison.Ordinal) || c > 0xFFFF))
                {
                    throw new StateError("file source requires its verified SHA256 content revision");
                }
            }

            if (Py.Has(reference, "revision"))
            {
                value["revision"] = TextField(reference["revision"], "source revision");
            }

            if (kind == "memory_record")
            {
                var revision = Py.Has(reference, "revision") ? Py.Str(reference["revision"]) : "";
                if (!Py.IsDigit(revision) || PyArgumentParser.ParsePyInt(Py.AsString(reference["revision"])!) < 1)
                {
                    throw new StateError("memory source requires its positive revision as a string");
                }
            }

            if (Py.Has(reference, "verified_at"))
            {
                value["verified_at"] = StateStore.ValidateTimestamp(reference["verified_at"]);
            }

            var identity = (kind, Py.AsString(value["ref"])!, value.TryGetPropertyValue("revision", out var r) ? Py.AsString(r) : null);
            if (!seen.Add(identity))
            {
                throw new StateError("duplicate source reference");
            }

            result.Add(value);
        }

        return SortedByCanonical(result);
    }

    internal static JsonArray SortedByCanonical(IEnumerable<JsonObject> values)
    {
        var keyed = values.Select(value => (Key: StateStore.CanonicalJson(value), Value: value)).ToList();
        var ordered = keyed.Select((entry, index) => (entry.Key, entry.Value, Index: index))
                           .OrderBy(entry => entry.Key, PyRepr.StringComparer).ThenBy(entry => entry.Index);
        return new JsonArray(ordered.Select(entry => (JsonNode?)(entry.Value.Parent is null ? entry.Value : Py.Clone(entry.Value))).ToArray());
    }

    /// <summary><c>_normalise_data(data)</c>: validated memory data in Python's key order.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Mirrors the Python method on the store instance.")]
    public JsonObject NormaliseData(JsonNode? data) => NormaliseDataCore(data);

    internal static JsonObject NormaliseDataCore(JsonNode? data)
    {
        if (data is not JsonObject input)
        {
            throw new StateError("memory data must be a JSON object");
        }

        var unknown = input.Select(pair => pair.Key).Where(key => !DataFields.Contains(key)).ToList();
        if (unknown.Count > 0)
        {
            throw new StateError("unknown memory fields: " + string.Join(", ", Py.Sorted(unknown)));
        }

        if (!Py.In(Py.Get(input, "domain"), Domains))
        {
            throw new StateError("domain must be user or agent");
        }

        if (!Py.In(Py.Get(input, "kind"), Kinds))
        {
            throw new StateError("unsupported memory kind");
        }

        if (!Py.In(Py.Get(input, "authority"), Authorities))
        {
            throw new StateError("unsupported memory authority");
        }

        TextField(Py.Get(input, "title"), "title");
        TextField(Py.Get(input, "text"), "text");
        TextField(Py.Get(input, "scope"), "scope");
        var value = Py.Copy(input);
        if (!value.ContainsKey("sensitivity"))
        {
            value["sensitivity"] = "private";
        }

        if (!Py.In(value["sensitivity"], Sensitivities))
        {
            throw new StateError("unsupported sensitivity");
        }

        value["allowed_uses"] = NormaliseStrings(value.ContainsKey("allowed_uses") ? value["allowed_uses"] : new JsonArray("reasoning"),
                                                 "allowed_uses", Uses);
        if (value["allowed_uses"]!.AsArray().Count == 0)
        {
            throw new StateError("allowed_uses must not be empty");
        }

        var entities = NormaliseStrings(value.ContainsKey("entities") ? value["entities"] : new JsonArray(), "entities");
        value["entities"] = entities;
        if (entities.Any(entity => ScopedEntityKey(entity) is null))
        {
            throw new StateError("entities require scoped identifiers; keep display names in title/text");
        }

        value["entities"] = Py.StrArray(Py.Sorted(entities.Select(entity => ScopedEntityKey(entity)!).Distinct(StringComparer.Ordinal)));
        value["routines"] = NormaliseStrings(value.ContainsKey("routines") ? value["routines"] : new JsonArray(), "routines");
        value["source_refs"] = NormaliseSourceRefs(value.ContainsKey("source_refs") ? value["source_refs"] : null);
        value["metadata"] = Py.Copy(JsonObjectField(value.ContainsKey("metadata") ? value["metadata"] : new JsonObject(), "metadata"));
        if (value.ContainsKey("identity"))
        {
            var identity = JsonObjectField(value["identity"], "identity", allowEmpty: false);
            if (!identity.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal).SetEquals(["provider", "scope", "external_id"]))
            {
                throw new StateError("identity requires provider, scope and external_id only");
            }

            foreach (var (field, entry) in identity)
            {
                TextField(entry, "identity." + field);
            }
        }

        var metadata = value["metadata"]!.AsObject();
        var requirements = metadata.ContainsKey("environment_requirements") ? metadata["environment_requirements"] : Py.Get(metadata, "environment");
        if (requirements is not null && requirements is not JsonObject)
        {
            throw new StateError("environment requirements must be a JSON object");
        }

        foreach (var field in new[] { "valid_from", "valid_to", "review_after" })
        {
            if (Py.Get(value, field) is null)
            {
                value.Remove(field);
            }
            else
            {
                value[field] = StateStore.ValidateTimestamp(value[field]);
            }
        }

        if (PyJson.Truthy(Py.Get(value, "valid_from")) && PyJson.Truthy(Py.Get(value, "valid_to"))
            && ParseTime(value["valid_from"]) >= ParseTime(value["valid_to"]))
        {
            throw new StateError("valid_from must precede valid_to");
        }

        StateStore.CanonicalJson(value);
        if (PyRepr.Length(SearchText(value)) > MaxMemoryTextChars)
        {
            throw new StateError("memory search representation exceeds " + MaxMemoryTextChars.ToString(CultureInfo.InvariantCulture)
                                 + " characters; split the memory before saving");
        }

        return value;
    }

    /// <summary>
    /// <c>_resolve_work_sources(data, _checkpoint_ref=...)</c>: checks session-checkpoint and memory-record dependencies
    /// and pins the current revision of this account's work sources that name none.
    /// </summary>
    public JsonObject ResolveWorkSources(JsonObject data, JsonNode? checkpointRef = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        var refs = new List<JsonObject>();
        foreach (var original in Iterate(Item(data, "source_refs")))
        {
            var reference = Py.DictWith(original);
            var kind = Py.Get(reference, "kind");
            if (PyJson.PyEquals(Item(reference, "kind"), JsonValue.Create("session_checkpoint")))
            {
                var key = DreamSources.ValidateSource(this, reference);
                if (!PyJson.PyEquals(reference, checkpointRef))
                {
                    var checkpoint = Conn.Execute("SELECT status FROM memory_records WHERE id=? AND account=?", RecordId("user", key), Account).FetchOne();
                    if (checkpoint is null || Py.Equal(checkpoint["status"], "forgotten"))
                    {
                        throw new StateError("session checkpoint dependency is unknown or forgotten; use dream-checkpoint");
                    }
                }
            }

            if (PyJson.PyEquals(kind, JsonValue.Create("memory_record")))
            {
                var parent = Conn.Execute("SELECT status FROM memory_records WHERE id=? AND account=?", Item(reference, "ref"), Account).FetchOne();
                if (parent is null || Py.Equal(parent["status"], "forgotten"))
                {
                    throw new StateError("memory dependency is unknown or forgotten");
                }
            }

            if (PyJson.PyEquals(kind, JsonValue.Create("work_source")))
            {
                PyRow? source;
                try
                {
                    source = Conn.Execute("SELECT account,current_revision FROM work_sources WHERE id=?", Item(reference, "ref")).FetchOne();
                }
                catch (PySqliteError ex) when (ex.Kind == PySqliteErrorKind.OperationalError)
                {
                    if (!ex.Message.Contains("no such table: work_sources", StringComparison.Ordinal))
                    {
                        throw;
                    }

                    source = null;
                }

                if (source is not null && Py.Equal(source["account"], Account) && !reference.ContainsKey("revision"))
                {
                    reference["revision"] = Py.Str(source["current_revision"]);
                }
            }

            refs.Add(reference);
        }

        var value = Py.Copy(data);
        value["source_refs"] = SortedByCanonical(refs);
        return value;
    }

    /// <summary><c>_content_hash(data)</c>: SHA-256 of <see cref="SearchText"/>.</summary>
    public static string ContentHash(JsonNode? data) => PyIds.Sha256Hex(SearchText(data));

    // ------------------------------------------------------------------------------ reading

    /// <summary><c>_row(memory_id)</c>: this account's record row, or <c>unknown memory: ID</c>.</summary>
    public PyRow Row(JsonNode? memoryId) =>
        Conn.Execute("SELECT * FROM memory_records WHERE id=? AND account=?", memoryId, Account).FetchOne()
        ?? throw new StateError("unknown memory: " + Py.Str(memoryId));

    /// <summary><c>_flatten(row)</c>: identity columns, then the stored data, then timestamps.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Mirrors the Python method on the store instance.")]
    public JsonObject Flatten(PyRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var result = new JsonObject
        {
            ["id"] = Py.FromDb(row["id"]),
            ["account"] = Py.FromDb(row["account"]),
            ["revision"] = Py.FromDb(row["revision"]),
            ["status"] = Py.FromDb(row["status"]),
            ["domain"] = Py.FromDb(row["domain"]),
            ["kind"] = Py.FromDb(row["kind"]),
        };
        var data = ParseStored(row["data"]);
        if (data is not JsonObject values)
        {
            throw new PyTypeError("stored memory data is not a JSON object");
        }

        foreach (var (key, value) in values.ToList())
        {
            values.Remove(key);
            result[key] = value;
        }

        result["created_at"] = Py.FromDb(row["created_at"]);
        result["updated_at"] = Py.FromDb(row["updated_at"]);
        return result;
    }

    /// <summary><c>parse_json(value)</c> of a TEXT column.</summary>
    internal static JsonNode? ParseStored(object? value) =>
        StateStore.ParseJson(value as string ?? throw new PyTypeError("the JSON object must be str, bytes or bytearray, not "
                                                                       + (value is null ? "NoneType" : PyJson.TypeName(Py.FromDb(value)))));

    internal static JsonObject ParseStoredObject(object? value) =>
        ParseStored(value) as JsonObject ?? throw new PyAttributeError("stored memory data is not a JSON object");

    /// <summary><c>show(memory_id)</c>.</summary>
    public JsonObject Show(JsonNode? memoryId)
    {
        TextField(memoryId, "memory id");
        return Flatten(Row(memoryId));
    }

    /// <summary><c>list(domain=None, status=None)</c>: newest first.</summary>
    public JsonArray List(JsonNode? domain = null, JsonNode? status = null)
    {
        if (domain is not null && !Py.In(domain, Domains))
        {
            throw new StateError("domain must be user or agent");
        }

        if (status is not null && !Py.In(status, Statuses))
        {
            throw new StateError("unsupported memory status");
        }

        var clauses = new List<string> { "account=?" };
        var values = new List<object?> { Account };
        if (domain is not null)
        {
            clauses.Add("domain=?");
            values.Add(domain);
        }

        if (status is not null)
        {
            clauses.Add("status=?");
            values.Add(status);
        }

        var rows = Conn.Execute("SELECT * FROM memory_records WHERE " + string.Join(" AND ", clauses) + " ORDER BY updated_at DESC,id",
                                [.. values]).FetchAll();
        return new JsonArray(rows.Select(row => (JsonNode?)Flatten(row)).ToArray());
    }

    private static string? EvidenceJson(JsonNode? evidence)
    {
        if (evidence is null)
        {
            return null;
        }

        JsonObjectField(evidence, "evidence", allowEmpty: false);
        return StateStore.CanonicalJson(evidence);
    }

    /// <summary><c>_human(evidence, memory_id, revision, decision)</c>: <c>work_ledger.human</c>.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Mirrors the Python method on the store instance.")]
    public JsonObject Human(JsonNode? evidence, JsonNode? subjectId, JsonNode? revision, string? decision) =>
        HumanEvidence.Human(evidence, subjectId, revision, decision);

    private static bool ActivationConfirmationRequired(JsonObject data)
    {
        var authority = Py.AsString(Item(data, "authority"));
        if (authority == "inferred")
        {
            throw new StateError("inferred memory cannot be active");
        }

        if (authority == "source_observed")
        {
            if (Py.In(Item(data, "kind"), Set("preference", "lesson")))
            {
                return true;
            }

            if (!Py.In(Item(data, "kind"), ObservationalKinds))
            {
                throw new StateError("source-observed memory kind is not observational");
            }

            return false;
        }

        return true;
    }

    private static bool Confirmed(JsonObject data) =>
        PyJson.PyEquals(Py.Get(data, "authority"), JsonValue.Create("user_confirmed")) || Py.In(Py.Get(data, "kind"), Set("preference", "lesson"));

    private bool Protected(PyRow row, JsonObject data)
    {
        if (Py.Equal(row["status"], "active") && Confirmed(data))
        {
            return true;
        }

        foreach (var revision in Conn.Execute("SELECT data FROM memory_revisions WHERE memory_id=? AND status='active'", row["id"]))
        {
            if (Confirmed(ParseStoredObject(revision["data"])))
            {
                return true;
            }
        }

        return false;
    }

    private static string TransitionDecision(string newStatus) => newStatus switch
    {
        "active" => "confirm",
        "rejected" => "reject",
        "disputed" => "dispute",
        "stale" => "stale",
        "superseded" => "supersede",
        "suppressed" => "suppress",
        _ => "edit",
    };

    private void Snapshot(object? memoryId, object revision, object? status, JsonObject data, JsonNode? evidence, string stamp)
    {
        var domain = Item(data, "domain");
        var kind = Item(data, "kind");
        var encoded = StateStore.CanonicalJson(data);
        var evidenceJson = EvidenceJson(evidence);
        var contentHash = ContentHash(data);
        Conn.Execute(
            """
            INSERT INTO memory_revisions
                           (memory_id,revision,status,domain,kind,data,evidence,content_hash,created_at)
                           VALUES(?,?,?,?,?,?,?,?,?)
            """,
            memoryId, revision, status, domain, kind, encoded, evidenceJson, contentHash, stamp);
    }

    private void Queue(object? memoryId, object? revision, object? status, JsonObject data, string stamp)
    {
        Conn.Execute("DELETE FROM memory_meta WHERE key=?", "index-blocked:" + Py.Str(memoryId));
        var contentHash = ContentHash(data);
        var operation = Py.Equal(status, "active") && !PyJson.PyEquals(Py.Get(data, "sensitivity"), JsonValue.Create("sensitive"))
                        && DreamIndexCurrent(data)
            ? "upsert"
            : "delete";
        Conn.Execute(
            """
            INSERT INTO memory_jobs(memory_id,revision,op,content_hash,created_at)
                           VALUES(?,?,?,?,?)
                           ON CONFLICT(memory_id) DO UPDATE SET
                            revision=excluded.revision,op=excluded.op,
                            content_hash=excluded.content_hash,created_at=excluded.created_at
            """,
            memoryId, revision, operation, contentHash, stamp);
    }

    /// <summary><c>dream_index_current(data)</c>: whether Dream-derived content may be indexed now.</summary>
    public bool DreamIndexCurrent(JsonNode? data)
    {
        var role = DictGet(DictGet(data, "metadata", new JsonObject()), "dream");
        if (Py.In(role, Set("checkpoint", "snapshot")))
        {
            return false;
        }

        var sourced = PyJson.Truthy(role);
        if (!sourced)
        {
            foreach (var reference in Iterate(DictGet(data, "source_refs", new JsonArray())))
            {
                if (Py.In(Item(reference, "kind"), Set("session_checkpoint", "memory_record")))
                {
                    sourced = true;
                    break;
                }
            }
        }

        return !sourced || WorkSourcesCurrent(data);
    }

    private static IEnumerable<JsonNode?> Iterate(JsonNode? value) => value switch
    {
        JsonArray array => array,
        JsonObject obj => obj.Select(pair => (JsonNode?)JsonValue.Create(pair.Key)),
        JsonValue scalar when PyJson.Scalar(scalar) is string text => PyRepr.CodePoints(text).Select(point => (JsonNode?)JsonValue.Create(Py.CodePointString(point))),
        _ => throw new PyTypeError("'" + PyJson.TypeName(value) + "' object is not iterable"),
    };

    // ------------------------------------------------------------------------------ writing

    /// <summary><c>put(key, data, status="candidate", revision=None, evidence=None)</c>.</summary>
    public JsonObject Put(JsonNode? key, JsonNode? data, PyArg status = default, JsonNode? revision = null, JsonNode? evidence = null)
    {
        var normal = NormaliseData(data);
        var (memoryId, keyHash) = Identity(normal["domain"], key);
        return PutRecord(memoryId, keyHash, normal, status.Or("candidate"), revision, evidence);
    }

    /// <summary><c>revise(memory_id, data, status, revision, evidence=None)</c>: a reviewed correction or lifecycle change.</summary>
    public JsonObject Revise(JsonNode? memoryId, JsonNode? data, JsonNode? status, JsonNode? revision, JsonNode? evidence = null)
    {
        var row = Row(memoryId);
        var normal = NormaliseData(data);
        if (!Py.Equal(row["domain"], normal["domain"]) || !Py.Equal(row["kind"], normal["kind"]))
        {
            throw new StateError("memory domain/kind is immutable; propose a separate sourced record");
        }

        // The caller's ID matched the row's TEXT id, so it is that exact string.
        return PutRecord(RowText(row, "id"), RowText(row, "key_hash"), normal, status, revision, evidence);
    }

    /// <summary>
    /// <c>_put(memory_id, key_hash, data, status, revision, evidence, _checkpoint_ref=None)</c>: creates or conditionally
    /// revises one normalised record, enforcing tombstones, transitions, review evidence and capture policy.
    /// </summary>
    public JsonObject PutRecord(string memoryId, string keyHash, JsonObject data, JsonNode? status, JsonNode? revision, JsonNode? evidence,
                                JsonNode? checkpointRef = null)
    {
        ArgumentNullException.ThrowIfNull(memoryId);
        ArgumentNullException.ThrowIfNull(data);
        var idNode = JsonValue.Create(memoryId);
        if (!Py.In(status, Statuses) || Py.AsString(status) == "forgotten")
        {
            throw new StateError("unsupported memory status");
        }

        var state = Py.AsString(status)!;
        return Transaction(() =>
        {
            if (checkpointRef is not null)
            {
                var checkpoint = checkpointRef as JsonObject ?? throw new PyTypeError("checkpoint reference must be a dict");
                var key = DreamSources.ValidateSource(this, checkpoint);
                if (memoryId != RecordId("user", key)
                    || !PyJson.PyEquals(DictGet(Item(data, "metadata"), "dream"), JsonValue.Create("checkpoint"))
                    || !PyJson.PyEquals(Item(data, "source_refs"), new JsonArray(Py.Clone(checkpointRef))))
                {
                    throw new StateError("checkpoint capture must write its exact canonical root");
                }
            }

            data = ResolveWorkSources(data, checkpointRef);
            var dream = PyJson.Truthy(DictGet(Item(data, "metadata"), "dream"));
            if (!dream)
            {
                foreach (var reference in Iterate(Item(data, "source_refs")))
                {
                    var kind = Item(reference, "kind");
                    if (PyJson.PyEquals(kind, JsonValue.Create("session_checkpoint"))
                        || (PyJson.PyEquals(kind, JsonValue.Create("memory_record"))
                            && PyJson.Truthy(DictGet(DictGet(Show(Item(reference, "ref")), "metadata", new JsonObject()), "dream"))))
                    {
                        dream = true;
                        break;
                    }
                }
            }

            if (dream && state is "active" or "candidate")
            {
                MemoryGovernance.AuthorizeCapture(this, data, allowConfirmation: evidence is not null);
            }

            if (Conn.Execute("SELECT id FROM memory_tombstones WHERE account=? AND key_hash=?", Account, keyHash).FetchOne() is not null)
            {
                throw new StateError("forgotten memory identity cannot be recreated");
            }

            var row = Conn.Execute("SELECT * FROM memory_records WHERE id=? AND account=?", idNode, Account).FetchOne();
            var encoded = StateStore.CanonicalJson(data);
            if (row is not null && Py.Equal(row["status"], "forgotten"))
            {
                throw new StateError("forgotten memory identity cannot be recreated");
            }

            if (row is not null && (!Py.Equal(row["domain"], data["domain"]) || !Py.Equal(row["kind"], data["kind"])))
            {
                throw new StateError("memory domain/kind is immutable; propose a separate sourced record");
            }

            if (row is not null && Py.Equal(row["status"], state) && Py.Equal(row["data"], encoded))
            {
                return Flatten(row);
            }

            if (row is null)
            {
                if (revision is not null && Integer(revision, "revision") != BigInteger.One)
                {
                    throw new StateError("new memory revision must be 1");
                }

                if (state == "active" && ActivationConfirmationRequired(data))
                {
                    if (!PyJson.PyEquals(revision, JsonValue.Create(1L)))
                    {
                        throw new StateError("active confirmed memory creation requires explicit revision 1");
                    }

                    Human(evidence, idNode, JsonValue.Create(1L), "confirm");
                }

                var created = StateStore.UtcNow();
                var contentHash = ContentHash(data);
                Conn.Execute(
                    """
                    INSERT INTO memory_records
                                       (id,account,key_hash,revision,domain,kind,status,data,indexed_content_hash,
                                        created_at,updated_at)
                                       VALUES(?,?,?,?,?,?,?,?,?,?,?)
                    """,
                    idNode, Account, keyHash, 1L, data["domain"], data["kind"], state, encoded, contentHash, created, created);
                Snapshot(idNode, 1L, state, data, evidence, created);
                Queue(idNode, 1L, state, data, created);
                return Show(idNode);
            }

            var currentRevision = row["revision"];
            if (revision is null)
            {
                throw new StateError("revision is required to change existing memory");
            }

            Integer(revision, "revision");
            if (!Py.Equal(currentRevision, revision))
            {
                throw new StateError("revision conflict: current revision is " + Py.Str(currentRevision));
            }

            var oldStatus = RowText(row, "status");
            if (!Transitions.TryGetValue(oldStatus, out var allowed))
            {
                throw new PyKeyError(oldStatus);
            }

            if (state != oldStatus && !allowed.Contains(state))
            {
                throw new StateError("invalid memory transition: " + oldStatus + " -> " + state);
            }

            var oldData = ParseStoredObject(row["data"]);
            var needsConfirmation = state == "active" && ActivationConfirmationRequired(data);
            var currentNode = Py.FromDb(currentRevision);
            if (Protected(row, oldData))
            {
                Human(evidence, idNode, currentNode, state == oldStatus ? "edit" : TransitionDecision(state));
            }
            else if ((oldStatus == "suppressed" && state != "suppressed") || (oldStatus is "disputed" or "stale" && state == "active"))
            {
                Human(evidence, idNode, currentNode, TransitionDecision(state));
            }
            else if (needsConfirmation)
            {
                Human(evidence, idNode, currentNode, "confirm");
            }

            var nextRevision = (long)currentRevision! + 1;
            var stamp = StateStore.UtcNow();
            var hash = ContentHash(data);
            var changed = Conn.Execute(
                """
                UPDATE memory_records SET revision=?,domain=?,kind=?,status=?,data=?,
                                   indexed_content_hash=?,updated_at=?
                                   WHERE id=? AND account=? AND revision=?
                """,
                nextRevision, data["domain"], data["kind"], state, encoded, hash, stamp, idNode, Account, currentRevision).RowCount;
            if (changed != 1)
            {
                throw new StateError("revision conflict; memory changed concurrently");
            }

            Snapshot(idNode, nextRevision, state, data, evidence, stamp);
            Queue(idNode, nextRevision, state, data, stamp);
            InvalidateDerivedIndexes(memoryId);
            return Show(idNode);
        });
    }

    /// <summary>Requeues derived descendants and removes their derived index rows, without rewriting evidence history.</summary>
    private void InvalidateDerivedIndexes(string memoryId)
    {
        var identities = DerivedDescendants(memoryId).Where(identity => identity != memoryId).ToList();
        var tables = TableNames();
        // Python iterates a set (hash order); a fixed (sorted) order is used here (PORTING.md).
        foreach (var identity in identities)
        {
            var row = Row(identity);
            Queue(identity, row["revision"], row["status"], ParseStoredObject(row["data"]), StateStore.UtcNow());
            foreach (var table in SemanticTableOrder)
            {
                if (tables.Contains(table))
                {
                    Conn.Execute("DELETE FROM " + table + " WHERE memory_id=?", identity);
                }
            }
        }
    }

    private HashSet<string> TableNames() =>
        Conn.Execute("SELECT name FROM sqlite_master WHERE type='table'").Select(row => row[0] as string).Where(name => name is not null)
            .Select(name => name!).ToHashSet(StringComparer.Ordinal);

    private bool Cycle(string sourceId, string targetId, string relation)
    {
        var edges = Conn.Execute("SELECT source_id,target_id FROM memory_links WHERE relation=?", relation)
                        .Select(row => (Source: row["source_id"], Target: row["target_id"])).ToList();
        var pending = new List<object?> { targetId };
        var seen = new HashSet<object?>();
        while (pending.Count > 0)
        {
            var node = pending[^1];
            pending.RemoveAt(pending.Count - 1);
            if (Py.Equal(node, sourceId))
            {
                return true;
            }

            if (seen.Add(node))
            {
                pending.AddRange(edges.Where(edge => Py.Equal(edge.Source, node)).Select(edge => edge.Target));
            }
        }

        return false;
    }

    /// <summary>
    /// <c>link(source_id, target_id, relation, revision, evidence=None, data=None, target_revision=None)</c>: adds one
    /// relationship from the source record (typed relationships and supersession need review evidence).
    /// </summary>
    public JsonObject Link(JsonNode? sourceId, JsonNode? targetId, JsonNode? relation, JsonNode? revision, JsonNode? evidence = null,
                           JsonNode? data = null, JsonNode? targetRevision = null)
    {
        if (!Py.In(relation, Relations))
        {
            throw new StateError("unsupported memory relationship");
        }

        var kind = Py.AsString(relation)!;
        if (PyJson.PyEquals(sourceId, targetId))
        {
            throw new StateError("self relationship is invalid");
        }

        var edgeData = Py.Copy(JsonObjectField(PyJson.Truthy(data) ? data : new JsonObject(), "relationship data"));
        if (edgeData.Any(pair => pair.Key is not ("valid_from" or "valid_to" or "source_refs")))
        {
            throw new StateError("relationship data supports validity and source_refs only");
        }

        foreach (var field in new[] { "valid_from", "valid_to" })
        {
            if (edgeData.ContainsKey(field))
            {
                edgeData[field] = StateStore.ValidateTimestamp(edgeData[field]);
            }
        }

        if (PyJson.Truthy(Py.Get(edgeData, "valid_from")) && PyJson.Truthy(Py.Get(edgeData, "valid_to"))
            && Py.Compare(Py.AsString(edgeData["valid_from"])!, Py.AsString(edgeData["valid_to"])!) >= 0)
        {
            throw new StateError("relationship valid_from must precede valid_to");
        }

        if (edgeData.ContainsKey("source_refs"))
        {
            edgeData["source_refs"] = NormaliseSourceRefs(edgeData["source_refs"]);
        }

        if (TypedRelations.Contains(kind) && !(edgeData.ContainsKey("valid_from") && edgeData.ContainsKey("source_refs")))
        {
            throw new StateError("typed relationships require valid_from and source_refs");
        }

        return Transaction(() =>
        {
            var source = Row(sourceId);
            var target = Row(targetId);
            if (Py.Equal(source["status"], "forgotten") || Py.Equal(target["status"], "forgotten"))
            {
                throw new StateError("forgotten memory cannot be linked");
            }

            var existing = Conn.Execute(
                """
                SELECT source_id,target_id,relation,created_at,data FROM memory_links
                                   WHERE source_id=? AND target_id=? AND relation=?
                """,
                sourceId, targetId, kind).FetchOne();
            if (existing is not null)
            {
                if (!Py.Equal(existing["data"], StateStore.CanonicalJson(edgeData)))
                {
                    throw new StateError("relationship already exists with different evidence/validity; remove and review its replacement");
                }

                var replay = Flatten(source);
                replay["source_id"] = Py.FromDb(existing["source_id"]);
                replay["target_id"] = Py.FromDb(existing["target_id"]);
                replay["relation"] = Py.FromDb(existing["relation"]);
                replay["link_created_at"] = Py.FromDb(existing["created_at"]);
                return replay;
            }

            Integer(revision, "revision");
            if (!Py.Equal(source["revision"], revision))
            {
                throw new StateError("revision conflict: current revision is " + Py.Str(source["revision"]));
            }

            if (TypedRelations.Contains(kind) || kind == "supersedes")
            {
                if (!PyJson.IsInt(targetRevision) || !Py.Equal(target["revision"], targetRevision))
                {
                    throw new StateError("typed relationship requires the current target_revision");
                }

                Human(evidence, sourceId, revision, "relate");
            }

            if (kind == "supersedes" && (!Py.Equal(source["kind"], target["kind"]) || !Py.Equal(source["domain"], target["domain"])))
            {
                throw new StateError("supersession requires the same memory kind and domain");
            }

            var sourceText = RowText(source, "id");
            var targetText = RowText(target, "id");
            if (kind is "derives_from" or "supersedes" or "depends_on" or "reports_to" && Cycle(sourceText, targetText, kind))
            {
                throw new StateError("memory relationship would create a cycle");
            }

            var sourceData = ParseStoredObject(source["data"]);
            if (Protected(source, sourceData))
            {
                Human(evidence, sourceId, revision, "relate");
            }

            var stamp = StateStore.UtcNow();
            Conn.Execute("INSERT INTO memory_links VALUES(?,?,?,?,?)", sourceId, targetId, kind, stamp, StateStore.CanonicalJson(edgeData));
            var nextRevision = PyJson.AsInt(revision) + 1;
            Conn.Execute("UPDATE memory_records SET revision=?,updated_at=? WHERE id=? AND account=?", nextRevision, stamp, sourceId, Account);
            var change = new JsonObject
            {
                ["operation"] = "link",
                ["source_id"] = Py.Clone(sourceId),
                ["target_id"] = Py.Clone(targetId),
                ["relation"] = kind,
                ["data"] = Py.Clone(edgeData),
            };
            var recorded = Py.DictWith(PyJson.Truthy(evidence) ? evidence : new JsonObject(), ("relationship_change", change));
            Snapshot(sourceId, nextRevision, source["status"], sourceData, recorded, stamp);
            Queue(sourceId, nextRevision, source["status"], sourceData, stamp);
            var result = Show(sourceId);
            result["source_id"] = Py.Clone(sourceId);
            result["target_id"] = Py.Clone(targetId);
            result["relation"] = kind;
            result["link_created_at"] = stamp;
            result["data"] = edgeData;
            return result;
        });
    }

    /// <summary><c>links(memory_id)</c>: every relationship touching the record (bounded graph fields only).</summary>
    public JsonArray Links(JsonNode? memoryId)
    {
        Row(memoryId);
        var result = new JsonArray();
        foreach (var row in Conn.Execute(
                     """
                     SELECT source_id,target_id,relation,data FROM memory_links
                                        WHERE source_id=? OR target_id=?
                                        ORDER BY relation,source_id,target_id
                     """,
                     memoryId, memoryId))
        {
            var edge = new JsonObject
            {
                ["source_id"] = Py.FromDb(row["source_id"]),
                ["target_id"] = Py.FromDb(row["target_id"]),
                ["relation"] = Py.FromDb(row["relation"]),
            };
            if (!Py.Equal(row["data"], "{}"))
            {
                edge["data"] = ParseStored(row["data"]);
            }

            result.Add(edge);
        }

        return result;
    }

    /// <summary><c>active_links(memory_id)</c>: relationships that are valid now between current, active records.</summary>
    public JsonArray ActiveLinks(JsonNode? memoryId) => FreshnessPass(() =>
    {
        var stamp = StateStore.UtcNow();
        var result = new JsonArray();
        foreach (var edge in Links(memoryId).Select(node => node!.AsObject()).ToList())
        {
            var data = DictGet(edge, "data", new JsonObject());
            if (Later(DictGet(data, "valid_from", JsonValue.Create(stamp)), stamp) || !Later(DictGet(data, "valid_to", JsonValue.Create("9999")), stamp)
                || !WorkSourcesCurrent(data))
            {
                continue;
            }

            var current = true;
            var endpoints = new[] { Row(edge["source_id"]), Row(edge["target_id"]) };
            foreach (var row in endpoints)
            {
                if (!Py.Equal(row["status"], "active") || !WorkSourcesCurrent(ParseStored(row["data"]))
                    || Later(DictGet(ParseStored(row["data"]), "valid_from", JsonValue.Create(stamp)), stamp)
                    || !Later(DictGet(ParseStored(row["data"]), "valid_to", JsonValue.Create("9999")), stamp)
                    || !Later(DictGet(ParseStored(row["data"]), "review_after", JsonValue.Create("9999")), stamp))
                {
                    current = false;
                    break;
                }
            }

            if (current)
            {
                result.Add(Py.Clone(edge));
            }
        }

        return result;
    });

    /// <summary>Python <c>value &gt; stamp</c> for the ISO strings memory compares lexically.</summary>
    private static bool Later(JsonNode? value, string stamp)
    {
        if (Py.AsString(value) is not { } text)
        {
            throw new PyTypeError("'>' not supported between instances of '" + PyJson.TypeName(value) + "' and 'str'");
        }

        return Py.Compare(text, stamp) > 0;
    }

    /// <summary><c>unlink(source_id, target_id, relation, revision, evidence)</c>: reviewed removal of a typed or plain relationship.</summary>
    public JsonObject Unlink(JsonNode? sourceId, JsonNode? targetId, JsonNode? relation, JsonNode? revision, JsonNode? evidence)
    {
        if (!Py.In(relation, Relations) || Py.AsString(relation) is "derives_from" or "supersedes")
        {
            throw new StateError("derivation and supersession history cannot be unlinked; propose a sourced replacement");
        }

        var kind = Py.AsString(relation)!;
        return Transaction(() =>
        {
            var source = Row(sourceId);
            Row(targetId);
            if (!PyJson.IsInt(revision) || !Py.Equal(source["revision"], revision))
            {
                throw new StateError("relationship source revision conflict");
            }

            Human(evidence, sourceId, revision, "unrelate");
            var removed = Conn.Execute("DELETE FROM memory_links WHERE source_id=? AND target_id=? AND relation=?", sourceId, targetId, kind).RowCount;
            if (removed == 0)
            {
                throw new StateError("relationship does not exist");
            }

            var stamp = StateStore.UtcNow();
            var data = ParseStoredObject(source["data"]);
            var next = PyJson.AsInt(revision) + 1;
            Conn.Execute("UPDATE memory_records SET revision=?,updated_at=? WHERE id=?", next, stamp, sourceId);
            var change = new JsonObject { ["operation"] = "unlink", ["target_id"] = Py.Clone(targetId), ["relation"] = kind };
            Snapshot(sourceId, next, source["status"], data, Py.DictWith(evidence, ("relationship_change", change)), stamp);
            Queue(sourceId, next, source["status"], data, stamp);
            return Show(sourceId);
        });
    }

    /// <summary><c>history(memory_id, limit=30)</c>: revisions, newest first.</summary>
    public JsonArray History(JsonNode? memoryId, PyArg limit = default)
    {
        Row(memoryId);
        var count = limit.Or(30);
        if (!PyJson.IsInt(count) || PyJson.AsInt(count) < 1 || PyJson.AsInt(count) > 100)
        {
            throw new StateError("history limit must be between 1 and 100");
        }

        var result = new JsonArray();
        foreach (var row in Conn.Execute(
                     "SELECT revision,status,data,evidence,created_at FROM memory_revisions WHERE memory_id=? ORDER BY revision DESC LIMIT ?",
                     memoryId, count))
        {
            result.Add(new JsonObject
            {
                ["revision"] = Py.FromDb(row["revision"]),
                ["status"] = Py.FromDb(row["status"]),
                ["data"] = ParseStored(row["data"]),
                ["evidence"] = row["evidence"] is { } evidence && Truthy(evidence) ? ParseStored(evidence) : null,
                ["created_at"] = Py.FromDb(row["created_at"]),
            });
        }

        return result;
    }

    private static bool Truthy(object? value) => value switch
    {
        null => false,
        string text => text.Length > 0,
        long number => number != 0,
        double number => number != 0,
        byte[] bytes => bytes.Length > 0,
        _ => true,
    };

    /// <summary>
    /// <c>_derived_descendants(memory_id)</c>: the record and everything derived from it (derivation links, memory-record
    /// sources in any revision, and session-checkpoint groups), as a sorted set.
    /// </summary>
    public SortedSet<string> DerivedDescendants(string memoryId)
    {
        ArgumentNullException.ThrowIfNull(memoryId);
        var children = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var checkpointGroups = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var groupOrder = new List<string>();

        void AddChild(string parent, string child)
        {
            if (!children.TryGetValue(parent, out var set))
            {
                children[parent] = set = new SortedSet<string>(PyRepr.StringComparer);
            }

            set.Add(child);
        }

        foreach (var row in Conn.Execute("SELECT source_id,target_id FROM memory_links WHERE relation='derives_from'"))
        {
            AddChild(RowText(row, "target_id"), RowText(row, "source_id"));
        }

        // A correction cannot disconnect old copied substance from its erasure provenance.
        foreach (var row in Conn.Execute(
                     "SELECT r.memory_id,r.data FROM memory_revisions r JOIN memory_records m "
                     + "ON m.id=r.memory_id WHERE m.account=? AND m.status<>'forgotten'", Account))
        {
            var memory = RowText(row, "memory_id");
            foreach (var reference in Iterate(DictGet(ParseStored(row["data"]), "source_refs", new JsonArray())))
            {
                var kind = Item(reference, "kind");
                if (PyJson.PyEquals(kind, JsonValue.Create("memory_record")))
                {
                    AddChild(Py.Str(Item(reference, "ref")), memory);
                }
                else if (PyJson.PyEquals(kind, JsonValue.Create("session_checkpoint")))
                {
                    var (source, locator) = DreamSources.SourceKeys(reference as JsonObject ?? throw new PyTypeError("source reference is not a dict"));
                    foreach (var key in new[] { source, locator })
                    {
                        if (!checkpointGroups.TryGetValue(key, out var group))
                        {
                            checkpointGroups[key] = group = new SortedSet<string>(PyRepr.StringComparer);
                            groupOrder.Add(key);
                        }

                        group.Add(memory);
                    }
                }
            }
        }

        foreach (var key in groupOrder)
        {
            var identities = checkpointGroups[key];
            var root = identities.Min!;
            foreach (var identity in identities)
            {
                AddChild(root, identity);
                AddChild(identity, root);
            }
        }

        var pending = new List<string> { memoryId };
        var seen = new SortedSet<string>(PyRepr.StringComparer) { memoryId };
        while (pending.Count > 0)
        {
            var target = pending[^1];
            pending.RemoveAt(pending.Count - 1);
            if (children.TryGetValue(target, out var set))
            {
                foreach (var child in set)
                {
                    if (seen.Add(child))
                    {
                        pending.Add(child);
                    }
                }
            }
        }

        return seen;
    }

    /// <summary><c>forget_preview(memory_id)</c>: what erasure would affect and what it retains.</summary>
    public JsonObject ForgetPreview(JsonNode? memoryId)
    {
        var root = Show(memoryId);
        var affected = new JsonArray();
        foreach (var identity in DerivedDescendants(Py.AsString(memoryId)!))
        {
            var row = Show(identity);
            if (!PyJson.PyEquals(row["status"], JsonValue.Create("forgotten")))
            {
                affected.Add(new JsonObject
                {
                    ["id"] = Py.Clone(row["id"]),
                    ["revision"] = Py.Clone(row["revision"]),
                    ["domain"] = Py.Clone(row["domain"]),
                    ["kind"] = Py.Clone(row["kind"]),
                });
            }
        }

        return new JsonObject
        {
            ["subject_id"] = Py.Clone(memoryId),
            ["revision"] = Py.Clone(root["revision"]),
            ["affected"] = affected,
            ["retained"] = Py.StrArray(["Minimal anti-reimport tombstones.", "Source mail/documents and work/approval records.",
                                        "Previously generated outputs, exports and backups are not recalled."]),
        };
    }

    /// <summary>
    /// <c>forget(memory_id, revision, evidence=None, *, policy_revision=None)</c>: erases the record and everything derived
    /// from it (data, history substance, links, index rows, usage references), keeping minimal tombstones. Needs
    /// forget evidence, or an approved retention policy revision under which the record is due.
    /// </summary>
    public JsonObject Forget(JsonNode? memoryId, JsonNode? revision, JsonNode? evidence = null, JsonNode? policyRevision = null) => Transaction(() =>
    {
        var root = Row(memoryId);
        if (Py.Equal(root["status"], "forgotten"))
        {
            var replay = Flatten(root);
            replay["forgotten_ids"] = new JsonArray(Py.Clone(memoryId));
            replay["derived_forgotten_ids"] = new JsonArray();
            return replay;
        }

        Integer(revision, "revision");
        if (!Py.Equal(root["revision"], revision))
        {
            throw new StateError("revision conflict: current revision is " + Py.Str(root["revision"]));
        }

        if (policyRevision is null)
        {
            Human(evidence, memoryId, revision, "forget");
        }
        else
        {
            MemoryGovernance.RetentionAuthorized(this, Flatten(root), policyRevision);
        }

        var rootId = RowText(root, "id");
        var identities = DerivedDescendants(rootId);
        var rows = new SortedDictionary<string, PyRow>(PyRepr.StringComparer);
        foreach (var row in Conn.Execute("SELECT * FROM memory_records WHERE account=?", Account))
        {
            if (row["id"] is string id && identities.Contains(id) && !Py.Equal(row["status"], "forgotten"))
            {
                rows[id] = row;
            }
        }

        var stamp = StateStore.UtcNow();
        var forgotten = new List<string>();
        var redactedHash = PyIds.Sha256Hex("forgotten");
        foreach (var (identity, row) in rows)
        {
            // Source identities survive erasure, including older corrected revisions.
            foreach (var history in Conn.Execute("SELECT data FROM memory_revisions WHERE memory_id=?", identity).FetchAll())
            {
                foreach (var reference in Iterate(DictGet(ParseStored(history["data"]), "source_refs", new JsonArray())))
                {
                    if (PyJson.PyEquals(Item(reference, "kind"), JsonValue.Create("session_checkpoint")))
                    {
                        var (source, locator) = DreamSources.SourceKeys(reference as JsonObject ?? throw new PyTypeError("source reference is not a dict"));
                        foreach (var key in new[] { source, locator })
                        {
                            var (alias, aliasHash) = Identity("user", key);
                            Conn.Execute("INSERT OR IGNORE INTO memory_tombstones VALUES(?,?,?,?)", alias, Account, aliasHash, stamp);
                        }
                    }
                }
            }

            var nextRevision = (long)row["revision"]! + 1;
            Conn.Execute(
                """
                UPDATE memory_revisions SET data='{}',evidence=NULL,content_hash=NULL
                                       WHERE memory_id=?
                """,
                identity);
            Conn.Execute(
                """
                UPDATE memory_records SET revision=?,status='forgotten',data='{}',
                                       indexed_content_hash=NULL,updated_at=?
                                       WHERE id=? AND account=?
                """,
                nextRevision, stamp, identity, Account);
            Conn.Execute(
                """
                INSERT INTO memory_revisions
                                       (memory_id,revision,status,domain,kind,data,evidence,content_hash,created_at)
                                       VALUES(?,?,?,?,?,'{}',NULL,NULL,?)
                """,
                identity, nextRevision, "forgotten", row["domain"], row["kind"], stamp);
            Conn.Execute(
                """
                INSERT OR IGNORE INTO memory_tombstones
                                       (id,account,key_hash,forgotten_at) VALUES(?,?,?,?)
                """,
                identity, Account, row["key_hash"], stamp);
            Conn.Execute(
                """
                INSERT INTO memory_jobs(memory_id,revision,op,content_hash,created_at)
                                       VALUES(?,?,'delete',?,?)
                                       ON CONFLICT(memory_id) DO UPDATE SET
                                        revision=excluded.revision,op='delete',
                                        content_hash=excluded.content_hash,created_at=excluded.created_at
                """,
                identity, nextRevision, redactedHash, stamp);
            Conn.Execute("DELETE FROM memory_meta WHERE key=?", "index-blocked:" + identity);
            forgotten.Add(identity);
        }

        if (forgotten.Count > 0)
        {
            var placeholders = string.Join(",", forgotten.Select(_ => "?"));
            Conn.Execute("DELETE FROM memory_links WHERE source_id IN (" + placeholders + ") OR target_id IN (" + placeholders + ")",
                         [.. forgotten, .. forgotten]);
            var tables = TableNames();
            foreach (var table in SemanticTableOrder)
            {
                if (tables.Contains(table))
                {
                    Conn.Execute("DELETE FROM " + table + " WHERE memory_id IN (" + placeholders + ")", [.. forgotten]);
                }
            }

            foreach (var usage in Conn.Execute("SELECT id,refs FROM memory_usage WHERE account=?", Account).FetchAll())
            {
                var remaining = new JsonArray();
                foreach (var reference in Iterate(ParseStored(usage["refs"])))
                {
                    if (!forgotten.Contains(Py.AsString(Item(reference, "id")) ?? "\0not-a-string", StringComparer.Ordinal))
                    {
                        remaining.Add(Py.Clone(reference));
                    }
                }

                if (remaining.Count > 0)
                {
                    Conn.Execute("UPDATE memory_usage SET refs=? WHERE id=?", StateStore.CanonicalJson(remaining), usage["id"]);
                }
                else
                {
                    Conn.Execute("DELETE FROM memory_usage WHERE id=?", usage["id"]);
                }
            }
        }

        var result = Show(memoryId);
        result["forgotten_ids"] = Py.StrArray(forgotten);
        result["derived_forgotten_ids"] = Py.StrArray(forgotten.Where(identity => !PyJson.PyEquals(JsonValue.Create(identity), memoryId)));
        return result;
    });

    // ------------------------------------------------------------------------------ freshness and eligibility

    /// <summary><c>_environment_subset(required, current)</c>: every required key (recursively) is present and equal.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Mirrors the Python method on the store instance.")]
    public bool EnvironmentSubset(JsonNode? required, JsonNode? current)
    {
        if (required is not JsonObject needed || current is not JsonObject actual)
        {
            return false;
        }

        foreach (var (key, value) in needed)
        {
            if (!actual.TryGetPropertyValue(key, out var present))
            {
                return false;
            }

            if (value is JsonObject)
            {
                if (!EnvironmentSubset(value, present))
                {
                    return false;
                }
            }
            else if (!PyJson.PyEquals(present, value))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// <c>with memory.freshness_pass(): ...</c>: one eligibility pass shares source-file hashes; nested passes reuse the
    /// outer cache, and the cache never outlives the outermost pass.
    /// </summary>
    public T FreshnessPass<T>(Func<T> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var owned = _fileChecks is null;
        if (owned)
        {
            _fileChecks = [];
        }

        try
        {
            return body();
        }
        finally
        {
            if (owned)
            {
                _fileChecks = null;
            }
        }
    }

    /// <summary>
    /// <c>_file_digest(path)</c>: SHA-256 of a regular file of at most <see cref="MaxSourceFileBytes"/>, or null when it
    /// is missing, too large, unreadable or changed while it was read.
    /// </summary>
    public string? FileDigest(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            var before = MemoryNative.Stat(path);
            if (!before.IsRegular || before.Size > MaxSourceFileBytes)
            {
                return null;
            }

            var key = (path, before.Signature);
            var cache = _fileChecks;
            if (cache is not null && cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            using var hasher = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            long total = 0;
            PyFileStat opened;
            PyFileStat after;
            var fd = MemoryNative.OpenRead(path);
            try
            {
                opened = MemoryNative.FStat(fd);
                // Windows stat/fstat can disagree on ctime; compare it only within each API.
                if (opened.Identity != before.Identity)
                {
                    return null;
                }

                while (true)
                {
                    var chunk = MemoryNative.Read(fd, (int)Math.Min(65536, MaxSourceFileBytes + 1 - total));
                    if (chunk.Length == 0)
                    {
                        break;
                    }

                    total += chunk.Length;
                    if (total > MaxSourceFileBytes)
                    {
                        return null;
                    }

                    hasher.AppendData(chunk);
                }

                after = MemoryNative.FStat(fd);
            }
            finally
            {
                PosixNative.Close(fd);
            }

            if (total != before.Size || after.Signature != opened.Signature || MemoryNative.Stat(path).Signature != before.Signature)
            {
                return null;
            }

            var result = Convert.ToHexStringLower(hasher.GetHashAndReset());
            cache?.TryAdd(key, result);
            return result;
        }
        catch (PyOSError)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>_work_sources_current(data, seen=None)</c>: every source the data names is still current (session checkpoint
    /// active, parent memory current and eligible, file hash unchanged, work source revision current and not sensitive).
    /// </summary>
    public bool WorkSourcesCurrent(JsonNode? data, IReadOnlySet<string>? seen = null)
    {
        var visited = new HashSet<string>(seen ?? new HashSet<string>(), StringComparer.Ordinal);
        foreach (var reference in Iterate(DictGet(data, "source_refs", new JsonArray())))
        {
            var kind = DictGet(reference, "kind");
            if (PyJson.PyEquals(kind, JsonValue.Create("session_checkpoint")))
            {
                string key;
                try
                {
                    key = DreamSources.ValidateSource(this, (JsonObject)reference!);
                }
                catch (StateError)
                {
                    return false;
                }

                var checkpoint = Conn.Execute("SELECT status,data FROM memory_records WHERE id=? AND account=?", RecordId("user", key), Account).FetchOne();
                if (checkpoint is null || !Py.Equal(checkpoint["status"], "active")
                    || !Contains(DictGet(ParseStored(checkpoint["data"]), "source_refs", new JsonArray()), reference))
                {
                    return false;
                }
            }

            if (PyJson.PyEquals(kind, JsonValue.Create("memory_record")))
            {
                var parentId = Item(reference, "ref");
                Py.EnsureHashable(parentId);
                if (Py.AsString(parentId) is { } parentText && visited.Contains(parentText))
                {
                    return false;
                }

                var row = Conn.Execute("SELECT * FROM memory_records WHERE id=? AND account=?", parentId, Account).FetchOne();
                if (row is null || !Py.Equal(row["status"], "active")
                    || !PyJson.PyEquals(JsonValue.Create(Py.Str(row["revision"])), DictGet(reference, "revision")))
                {
                    return false;
                }

                var parent = ParseStored(row["data"]);
                var stamp = StateStore.UtcNow();
                var next = new HashSet<string>(visited, StringComparer.Ordinal);
                if (Py.AsString(parentId) is { } added)
                {
                    next.Add(added);
                }

                if (PyJson.PyEquals(DictGet(parent, "sensitivity"), JsonValue.Create("sensitive"))
                    || PyJson.PyEquals(DictGet(parent, "authority"), JsonValue.Create("inferred"))
                    || Later(DictGet(parent, "valid_from", JsonValue.Create(stamp)), stamp)
                    || !Later(DictGet(parent, "valid_to", JsonValue.Create("9999")), stamp)
                    || !Later(DictGet(parent, "review_after", JsonValue.Create("9999")), stamp)
                    || !WorkSourcesCurrent(parent, next))
                {
                    return false;
                }

                continue;
            }

            if (PyJson.PyEquals(kind, JsonValue.Create("file")))
            {
                if (!PyJson.Truthy(DictGet(reference, "revision")))
                {
                    return false;
                }

                var target = Item(reference, "ref");
                var path = PyPath.ExpandUser(Py.AsString(target) ?? throw new PyTypeError(
                    "argument should be a str or an os.PathLike object where __fspath__ returns a str, not '" + PyJson.TypeName(target) + "'"));
                if (!PyPath.IsAbsolute(path))
                {
                    return false;
                }

                if (!PyJson.PyEquals(Py.Scalar(FileDigest(path)), Item(reference, "revision")))
                {
                    return false;
                }

                continue;
            }

            if (!PyJson.PyEquals(kind, JsonValue.Create("work_source")))
            {
                continue;
            }

            PyRow? source;
            try
            {
                source = Conn.Execute("SELECT account,current_revision FROM work_sources WHERE id=?", Item(reference, "ref")).FetchOne();
            }
            catch (PySqliteError ex) when (ex.Kind == PySqliteErrorKind.OperationalError)
            {
                if (ex.Message.Contains("no such table: work_sources", StringComparison.Ordinal))
                {
                    return false;
                }

                throw;
            }

            if (source is null || !Py.Equal(source["account"], Account) || !((JsonObject)reference!).ContainsKey("revision")
                || Py.Str(source["current_revision"]) != Py.Str(reference["revision"]))
            {
                return false;
            }

            var revision = Conn.Execute("SELECT data FROM work_source_revisions WHERE source_id=? AND revision=?",
                                        Item(reference, "ref"), Py.Str(reference["revision"])).FetchOne();
            if (revision is null)
            {
                return false;
            }

            var sensitivity = DictGet(ParseStored(revision["data"]), "sensitivity", JsonValue.Create("unknown"));
            if (Py.AsString(sensitivity) is not { } level || PyRepr.CaseFold(level) is not ("unknown" or "public" or "private" or "none"))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// <c>eligible(domain=None, routine=None, usage="reasoning", environment=None, include_stale=False)</c>: the active
    /// (optionally stale) records that may inform reasoning or drafting now; preferences first.
    /// </summary>
    public JsonArray Eligible(JsonNode? domain = null, JsonNode? routine = null, PyArg usage = default, JsonNode? environment = null,
                              PyArg includeStale = default) => FreshnessPass(() =>
    {
        var use = usage.Or("reasoning");
        var stale = includeStale.Or(false);
        if (domain is not null && !Py.In(domain, Domains))
        {
            throw new StateError("domain must be user or agent");
        }

        if (routine is not null)
        {
            TextField(routine, "routine");
        }

        if (!Py.In(use, Uses))
        {
            throw new StateError("unsupported memory use");
        }

        if (environment is not null)
        {
            JsonObjectField(environment, "environment");
        }

        if (!PyJson.IsBool(stale))
        {
            throw new StateError("include_stale must be a boolean");
        }

        var withStale = PyJson.Truthy(stale);
        var values = new List<object?> { Account, "active" };
        if (withStale)
        {
            values.Add("stale");
        }

        var domainClause = "";
        if (domain is not null)
        {
            domainClause = " AND domain=?";
            values.Add(domain);
        }

        var rows = Conn.Execute(
            "SELECT * FROM memory_records\n               WHERE account=? AND status IN (" + (withStale ? "?,?" : "?") + ")" + domainClause + "\n"
            + "               ORDER BY CASE WHEN kind='preference' THEN 0 ELSE 1 END,updated_at DESC,id",
            [.. values]).FetchAll();
        var current = PyDateTime.Now();
        var result = new List<JsonObject>();
        foreach (var row in rows)
        {
            var data = ParseStored(row["data"]);
            if (Py.In(DictGet(DictGet(data, "metadata", new JsonObject()), "dream"), Set("snapshot", "checkpoint")))
            {
                continue;
            }

            if (PyJson.PyEquals(DictGet(data, "authority"), JsonValue.Create("inferred"))
                || PyJson.PyEquals(DictGet(data, "sensitivity"), JsonValue.Create("sensitive")))
            {
                continue;
            }

            if (!Contains(DictGet(data, "allowed_uses", new JsonArray()), use))
            {
                continue;
            }

            var routines = DictGet(data, "routines", new JsonArray());
            if (routine is null)
            {
                if (PyJson.Truthy(routines))
                {
                    continue;
                }
            }
            else if (PyJson.Truthy(routines) && !Contains(routines, routine))
            {
                continue;
            }

            if (PyJson.Truthy(DictGet(data, "valid_from")) && current < ParseTime(DictGet(data, "valid_from")))
            {
                continue;
            }

            if (PyJson.Truthy(DictGet(data, "valid_to")) && current >= ParseTime(DictGet(data, "valid_to")))
            {
                continue;
            }

            if (!withStale && PyJson.Truthy(DictGet(data, "review_after")) && current >= ParseTime(DictGet(data, "review_after")))
            {
                continue;
            }

            var metadata = DictGet(data, "metadata", new JsonObject());
            var requirements = DictGet(metadata, "environment_requirements", DictGet(metadata, "environment"));
            if (PyJson.Truthy(requirements) && (environment is null || !EnvironmentSubset(requirements, environment)))
            {
                continue;
            }

            if (!WorkSourcesCurrent(data))
            {
                continue;
            }

            result.Add(Flatten(row));
        }

        var eligibleIds = result.Select(record => record["id"]).ToList();
        var superseded = new List<JsonNode?>();
        foreach (var record in result)
        {
            foreach (var edge in ActiveLinks(record["id"]))
            {
                if (PyJson.PyEquals(edge!["relation"], JsonValue.Create("supersedes")) && eligibleIds.Any(id => PyJson.PyEquals(id, edge["source_id"])))
                {
                    superseded.Add(edge["target_id"]);
                }
            }
        }

        return new JsonArray(result.Where(record => !superseded.Any(id => PyJson.PyEquals(id, record["id"])))
                                   .Select(record => (JsonNode?)record).ToArray());
    });

    /// <summary>Python <c>value in container</c> for a list, string (substring) or dict (key).</summary>
    private static bool Contains(JsonNode? container, JsonNode? value) => container switch
    {
        JsonArray list => Py.InList(value, list),
        JsonObject obj => Py.AsString(value) is { } key ? obj.ContainsKey(key) : EnsureHashableFalse(value),
        JsonValue scalar when PyJson.Scalar(scalar) is string text => Py.AsString(value) is { } part
            ? text.Contains(part, StringComparison.Ordinal)
            : throw new PyTypeError("'in <string>' requires string as left operand, not " + PyJson.TypeName(value)),
        _ => throw new PyTypeError("argument of type '" + PyJson.TypeName(container) + "' is not iterable"),
    };

    private static bool EnsureHashableFalse(JsonNode? value)
    {
        Py.EnsureHashable(value);
        return false;
    }

    // ------------------------------------------------------------------------------ policy, usage, jobs, health

    /// <summary><c>policy()</c>: <see cref="MemoryGovernance.Policy"/>.</summary>
    public JsonObject Policy() => MemoryGovernance.Policy(this);

    /// <summary><c>capture(key, data, revision=None)</c>: <see cref="MemoryGovernance.Capture"/>.</summary>
    public JsonObject Capture(JsonNode? key, JsonNode? data, JsonNode? revision = null) => MemoryGovernance.Capture(this, key, data, revision);

    /// <summary>
    /// <c>record_usage(refs, routine, outcome="used", event_id=None)</c>: opt-in minimal usage events (memory IDs and
    /// revisions only); a replayed event must not change.
    /// </summary>
    public JsonObject RecordUsage(JsonNode? refs, JsonNode? routine, PyArg outcome = default, JsonNode? eventId = null)
    {
        var result = outcome.Or("used");
        if (refs is not JsonArray list || list.Count < 1 || list.Count > 50)
        {
            throw new StateError("usage requires between 1 and 50 memory references");
        }

        if (!Py.In(result, Set("used", "helpful", "corrected", "discarded")))
        {
            throw new StateError("unsupported usage outcome; do-not-learn records no learning event");
        }

        var routineText = TextField(routine, "routine");
        var eventNode = PyJson.Truthy(eventId) ? eventId : JsonValue.Create("usage_" + PyIds.Uuid4Hex());
        var eventText = TextField(eventNode, "event_id");
        var normalised = new List<JsonObject>();
        var prior = Transaction(() =>
        {
            if (!PyJson.Truthy(Item(Item(Policy(), "data"), "usage_enabled")))
            {
                throw new StateError("memory usage capture is disabled by policy");
            }

            foreach (var item in list)
            {
                if (item is not JsonObject reference || !reference.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal).SetEquals(["id", "revision"]))
                {
                    throw new StateError("usage references contain id and revision only");
                }

                var row = Row(reference["id"]);
                if (!Py.Equal(row["revision"], reference["revision"]) || !PyJson.IsInt(reference["revision"]) || !Py.Equal(row["status"], "active"))
                {
                    throw new StateError("usage memory is not current and active");
                }

                normalised.Add(reference);
            }

            var ordered = normalised.Select((reference, index) => (Key: Py.Str(reference["id"]), Reference: reference, Index: index))
                                    .OrderBy(entry => entry.Key, PyRepr.StringComparer).ThenBy(entry => entry.Index)
                                    .Select(entry => (JsonNode?)Py.Clone(entry.Reference)).ToArray();
            var encoded = StateStore.CanonicalJson(new JsonArray(ordered));
            var existing = Conn.Execute("SELECT * FROM memory_usage WHERE id=?", eventText).FetchOne();
            if (existing is not null)
            {
                if (!Py.Equal(existing["account"], Account) || !Py.Equal(existing["routine"], routineText) || !Py.Equal(existing["refs"], encoded)
                    || !Py.Equal(existing["outcome"], Py.AsString(result)))
                {
                    throw new StateError("usage event replay changed its contents");
                }
            }
            else
            {
                Conn.Execute("INSERT INTO memory_usage VALUES(?,?,?,?,?,?)", eventText, Account, routineText, encoded, result, StateStore.UtcNow());
            }

            return existing;
        });
        return new JsonObject { ["id"] = eventText, ["recorded"] = true, ["replayed"] = prior is not null };
    }

    /// <summary><c>usage(memory_id, limit=30)</c>: usage events that name the record, newest first.</summary>
    public JsonArray Usage(JsonNode? memoryId, PyArg limit = default)
    {
        Row(memoryId);
        var count = limit.Or(30);
        if (!PyJson.IsInt(count) || PyJson.AsInt(count) < 1 || PyJson.AsInt(count) > 100)
        {
            throw new StateError("usage limit must be between 1 and 100");
        }

        var id = Py.AsString(memoryId) ?? throw new PyTypeError("can only concatenate str (not \"" + PyJson.TypeName(memoryId) + "\") to str");
        var result = new JsonArray();
        foreach (var row in Conn.Execute("SELECT * FROM memory_usage WHERE account=? AND refs LIKE ? ORDER BY created_at DESC LIMIT ?",
                                         Account, "%\"" + id + "\"%", count))
        {
            result.Add(new JsonObject
            {
                ["id"] = Py.FromDb(row["id"]),
                ["routine"] = Py.FromDb(row["routine"]),
                ["outcome"] = Py.FromDb(row["outcome"]),
                ["created_at"] = Py.FromDb(row["created_at"]),
                ["refs"] = ParseStored(row["refs"]),
            });
        }

        return result;
    }

    /// <summary><c>pending_jobs(limit=100)</c>: upsert/delete index jobs, oldest first.</summary>
    public JsonArray PendingJobs(PyArg limit = default)
    {
        var count = limit.Or(100);
        if (!PyJson.IsInt(count) || PyJson.AsInt(count) < 1 || PyJson.AsInt(count) > 10000)
        {
            throw new StateError("job limit must be between 1 and 10000");
        }

        var result = new JsonArray();
        foreach (var row in Conn.Execute(
                     """
                     SELECT memory_id,revision,op,content_hash,created_at
                                        FROM memory_jobs WHERE op IN ('upsert','delete') ORDER BY created_at,memory_id LIMIT ?
                     """,
                     count))
        {
            result.Add(new JsonObject
            {
                ["memory_id"] = Py.FromDb(row["memory_id"]),
                ["revision"] = Py.FromDb(row["revision"]),
                ["operation"] = Py.FromDb(row["op"]),
                ["content_hash"] = Py.FromDb(row["content_hash"]),
            });
        }

        return result;
    }

    /// <summary><c>complete_job(memory_id, revision)</c>: removes the job only if it is still at that revision.</summary>
    public bool CompleteJob(JsonNode? memoryId, JsonNode? revision)
    {
        Integer(revision, "revision");
        return Transaction(() => Conn.Execute("DELETE FROM memory_jobs WHERE memory_id=? AND revision=?", memoryId, revision).RowCount != 0);
    }

    /// <summary>
    /// <c>block_job(memory_id, revision, reason)</c>: quarantines the index job of an active record whose search text
    /// exceeds the encoder limit (the only deterministic failure that may be quarantined).
    /// </summary>
    public bool BlockJob(JsonNode? memoryId, JsonNode? revision, JsonNode? reason)
    {
        Integer(revision, "revision");
        if (!PyJson.PyEquals(reason, JsonValue.Create("representation_exceeds_limit")))
        {
            throw new StateError("only deterministic representation-limit failures may be quarantined");
        }

        return Transaction(() =>
        {
            var row = Row(memoryId);
            if (!Py.Equal(row["revision"], revision) || !Py.Equal(row["status"], "active"))
            {
                return false;
            }

            var data = ParseStored(row["data"]);
            if (PyJson.PyEquals(DictGet(data, "sensitivity"), JsonValue.Create("sensitive")) || PyRepr.Length(SearchText(data)) <= MaxMemoryTextChars)
            {
                return false;
            }

            var previous = Conn.Execute("SELECT revision,op FROM memory_jobs WHERE memory_id=?", memoryId).FetchOne();
            if (previous is not null && Py.Equal(previous["revision"], revision) && Py.Equal(previous["op"], "blocked"))
            {
                return true;
            }

            var stamp = StateStore.UtcNow();
            Conn.Execute(
                "INSERT INTO memory_jobs VALUES(?,?,'blocked',?,?) "
                + "ON CONFLICT(memory_id) DO UPDATE SET revision=excluded.revision,op='blocked',"
                + "content_hash=excluded.content_hash,created_at=excluded.created_at",
                memoryId, revision, ContentHash(data), stamp);
            Conn.Execute(
                "INSERT INTO memory_meta VALUES(?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                "index-blocked:" + Py.Str(memoryId), StateStore.CanonicalJson(new JsonObject { ["revision"] = Py.Clone(revision), ["reason"] = Py.Clone(reason) }));
            return true;
        });
    }

    /// <summary><c>blocked_jobs(limit=100)</c>: quarantined index jobs with their recorded reason.</summary>
    public JsonArray BlockedJobs(PyArg limit = default)
    {
        var count = limit.Or(100);
        if (!PyJson.IsInt(count) || PyJson.AsInt(count) < 1 || PyJson.AsInt(count) > 10000)
        {
            throw new StateError("blocked job limit must be between 1 and 10000");
        }

        var result = new JsonArray();
        foreach (var row in Conn.Execute("SELECT memory_id,revision FROM memory_jobs WHERE op='blocked' ORDER BY created_at,memory_id LIMIT ?", count)
                                .FetchAll())
        {
            var detail = Conn.Execute("SELECT value FROM memory_meta WHERE key=?", "index-blocked:" + Py.Str(row["memory_id"])).FetchOne();
            result.Add(new JsonObject
            {
                ["memory_id"] = Py.FromDb(row["memory_id"]),
                ["revision"] = Py.FromDb(row["revision"]),
                ["reason"] = detail is not null ? Py.Clone(Item(ParseStored(detail[0]), "reason")) : "invalid_record",
            });
        }

        return result;
    }

    /// <summary><c>health()</c>: lifecycle counts, index and review backlog, stale sources, conflicts and capture state.</summary>
    public JsonObject Health() => FreshnessPass(() =>
    {
        var counts = new JsonObject();
        foreach (var status in Py.Sorted(Statuses))
        {
            counts[status] = 0L;
        }

        foreach (var row in Conn.Execute(
                     """
                     SELECT status,count(*) AS count FROM memory_records
                                        WHERE account=? GROUP BY status
                     """,
                     Account))
        {
            counts[Py.Str(row["status"])] = Py.FromDb(row["count"]);
        }

        long sensitive = 0, reviewDue = 0, staleSources = 0;
        var staleImports = new List<string>();
        var stamp = StateStore.UtcNow();
        foreach (var row in Conn.Execute("SELECT id,data,status FROM memory_records WHERE account=? AND status<>'forgotten'", Account).FetchAll())
        {
            var data = ParseStored(row["data"]);
            if (PyJson.PyEquals(DictGet(data, "sensitivity"), JsonValue.Create("sensitive")))
            {
                sensitive++;
            }

            if (Py.Equal(row["status"], "active") && !Later(DictGet(data, "review_after", JsonValue.Create("9999")), stamp))
            {
                reviewDue++;
            }

            if (Py.Equal(row["status"], "active") && !WorkSourcesCurrent(data))
            {
                staleSources++;
                if (PyJson.Truthy(DictGet(DictGet(data, "metadata", new JsonObject()), "imported_configuration"))
                    && Iterate(DictGet(data, "source_refs", new JsonArray())).Any(reference => PyJson.PyEquals(Item(reference, "kind"), JsonValue.Create("file"))))
                {
                    staleImports.Add(RowText(row, "id"));
                }
            }
        }

        var conflictSources = Conn.Execute("SELECT DISTINCT source_id FROM memory_links WHERE relation='contradicts'").FetchAll()
                                  .Select(row => Py.FromDb(row[0])).ToList();
        var conflicts = new HashSet<(string, string)>();
        foreach (var source in conflictSources)
        {
            foreach (var edge in ActiveLinks(source))
            {
                if (PyJson.PyEquals(edge!["relation"], JsonValue.Create("contradicts")))
                {
                    conflicts.Add((Py.Str(edge["source_id"]), Py.Str(edge["target_id"])));
                }
            }
        }

        var warnings = new JsonArray();
        if (sensitive > 0)
        {
            warnings.Add("Sensitive memories are stored but excluded from ordinary eligibility.");
        }

        var total = BigInteger.Zero;
        foreach (var (_, value) in counts)
        {
            total += PyJson.AsInt(value);
        }

        return new JsonObject
        {
            ["schema_version"] = SchemaVersion,
            ["account"] = Account,
            ["total"] = PyJson.Int(total),
            ["by_status"] = counts,
            ["pending_jobs"] = Py.FromDb(Conn.Execute("SELECT count(*) FROM memory_jobs WHERE op IN ('upsert','delete')").FetchOne()![0]),
            ["blocked_jobs"] = BlockedJobs(),
            ["blocked_job_count"] = Py.FromDb(Conn.Execute("SELECT count(*) FROM memory_jobs WHERE op='blocked'").FetchOne()![0]),
            ["tombstones"] = Py.FromDb(Conn.Execute("SELECT count(*) FROM memory_tombstones WHERE account=?", Account).FetchOne()![0]),
            ["sensitive_excluded"] = sensitive,
            ["review_due"] = reviewDue,
            ["stale_sources"] = staleSources,
            ["conflict_links"] = (long)conflicts.Count,
            ["usage_events"] = Py.FromDb(Conn.Execute("SELECT count(*) FROM memory_usage WHERE account=?", Account).FetchOne()![0]),
            ["capture_enabled"] = Py.Clone(Item(Item(Item(Policy(), "data"), "capture"), "enabled")),
            ["stale_imported_preferences"] = new JsonObject
            {
                ["count"] = (long)staleImports.Count,
                ["ids"] = Py.StrArray(staleImports.Take(50)),
            },
            ["recovery_actions"] = staleImports.Count > 0 ? Py.StrArray([PreferencesRecovery]) : new JsonArray(),
            ["warnings"] = warnings,
        };
    });
}

/// <summary>Python <c>AttributeError</c> (missing from State.Core's PyErrors; it belongs there).</summary>
public sealed class PyAttributeError : PyException
{
    public PyAttributeError(string message) : base(message)
    {
    }

    public override string PythonType => "AttributeError";
}


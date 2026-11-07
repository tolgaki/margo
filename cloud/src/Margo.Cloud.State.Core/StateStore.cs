using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Margo.Cloud.State.Core;

/// <summary>
/// Port of <c>skills/chief-of-staff/scripts/margo_store.py</c>: private, account-scoped SQLite storage shared by
/// Margo's state CLIs. The state root (<c>--state-dir</c>/<c>MARGO_STATE_DIR</c>) is a base directory; the
/// SHA-256 of the account is always appended. Repository and synchronised directories are rejected unless
/// <c>MARGO_ALLOW_UNSAFE_STATE_DIR=1</c> is set with an explicit root. Private file modes are checked, not
/// encryption. Function names follow the Python ones.
/// </summary>
public static partial class StateStore
{
    /// <summary>The Python module docstring (argparse description of <c>margo_store.py</c>).</summary>
    public const string Doc =
        "Private, account-scoped SQLite storage shared by Margo's local CLIs.\n"
        + "\n"
        + "``state_root``/MARGO_STATE_DIR is a *base* directory; an account hash is always\n"
        + "appended. The default is ~/.copilot/margo/state. Account selection is explicit:\n"
        + "--account, MARGO_ACCOUNT, then the private ~/.copilot/margo/config.json object's\n"
        + "``account`` field (MARGO_CONFIG may select another private configuration file).\n"
        + "Repository/synchronised directories are rejected. Synthetic tests may opt in\n"
        + "with MARGO_ALLOW_UNSAFE_STATE_DIR=1 and an explicit state-root override.\n"
        + "\n"
        + "No encryption is implied by private file permissions. Do not store credentials.\n"
        + "Use ``with connection:`` for transactions; read-modify-write callers must execute\n"
        + "BEGIN IMMEDIATE before reading. Closing a connection remains the caller's job.\n"
        + "\n"
        + "Explicit private configuration (no identity lookup or network access):\n"
        + "    python3 margo_store.py init --account OWNER_PRINCIPAL\n"
        + "This saves the ledger owner's principal in the config's account field, without\n"
        + "creating state or changing an existing account. --config selects another private\n"
        + "config file; repository/shared overrides require the synthetic-testing opt-in.\n";

    private static readonly string[] SyncedWords = ["onedrive", "dropbox", "icloud", "cloudstorage", "mobile documents", "google drive", "googledrive"];
    private static readonly string[] SharedNames = ["shared", "public", "box", "box sync"];

    // ------------------------------------------------------------------------------ time and JSON

    /// <summary><c>utc_now()</c>: <c>datetime.now(timezone.utc).isoformat(timespec="microseconds")</c>.</summary>
    public static string UtcNow() => PyDateTime.Now().Isoformat("microseconds");

    /// <summary><c>validate_timestamp(value)</c>: an explicitly zoned ISO datetime, normalised to UTC microseconds.</summary>
    public static string ValidateTimestamp(object? value)
    {
        if (value is JsonNode node)
        {
            value = PyJson.Scalar(node);
        }

        if (value is not string text || !TimestampPattern().IsMatch(text))
        {
            throw new StateError("timestamp must be an ISO-8601 datetime with an explicit timezone");
        }

        try
        {
            if (text.Length >= 6 && text[^6] is '+' or '-')
            {
                if (PyArgumentParser.ParsePyInt(text[^5..^3]) > 23 || PyArgumentParser.ParsePyInt(text[^2..]) > 59)
                {
                    throw new PyValueError("invalid offset");
                }
            }

            var parsed = PyDateTime.FromIsoformat(text.Replace("Z", "+00:00", StringComparison.Ordinal));
            return parsed.ToUtc().Isoformat("microseconds");
        }
        catch (Exception ex) when (ex is PyValueError or PyOverflowError)
        {
            throw new StateError("invalid timestamp", ex);
        }
    }

    /// <summary><c>parse_timestamp</c> is an alias of <see cref="ValidateTimestamp"/>.</summary>
    public static string ParseTimestamp(object? value) => ValidateTimestamp(value);

    /// <summary><c>canonical_json(value)</c>: sorted keys, compact separators, UTF-8 text, finite numbers only.</summary>
    public static string CanonicalJson(JsonNode? value)
    {
        try
        {
            return PyJson.Dumps(value, itemSeparator: ",", keySeparator: ":", sortKeys: true, ensureAscii: false, allowNan: false);
        }
        catch (Exception ex) when (ex is PyTypeError or PyValueError)
        {
            throw new StateError("value must contain finite JSON data", ex);
        }
    }

    /// <summary>
    /// <c>parse_json(raw)</c>: rejects duplicate keys (<c>duplicate JSON object key</c>), NaN/Infinity
    /// (<c>non-finite JSON number</c>), overflowing floats and invalid JSON (<c>invalid JSON</c>).
    /// </summary>
    public static JsonNode? ParseJson(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var options = new PyJsonLoadOptions
        {
            ObjectPairsHook = pairs =>
            {
                var result = new JsonObject();
                foreach (var (key, value) in pairs)
                {
                    if (result.ContainsKey(key))
                    {
                        throw new StateError("duplicate JSON object key");
                    }

                    result.Add(key, value);
                }

                return result;
            },
            ParseConstant = _ => throw new StateError("non-finite JSON number"),
        };
        try
        {
            var value = PyJson.Loads(raw, options);
            CanonicalJson(value);
            return value;
        }
        catch (PyJsonDecodeError ex)
        {
            throw new StateError("invalid JSON", ex);
        }
    }

    /// <summary><c>read_json(path)</c>.</summary>
    public static JsonNode? ReadJson(string path)
    {
        string text;
        try
        {
            text = PyIO.ReadTextFile(path);
        }
        catch (Exception ex) when (ex is PyOSError or PyUnicodeError)
        {
            throw new StateError("cannot read valid JSON from " + path, ex);
        }

        return ParseJson(text);
    }

    // ------------------------------------------------------------------------------ transactions

    /// <summary>
    /// <c>transaction(connection, read_only=...)</c>: the outermost caller owns <c>BEGIN</c>/<c>BEGIN IMMEDIATE</c>
    /// and the commit; nested callers get a uniquely named savepoint that is released, or rolled back and
    /// released when the body throws.
    /// </summary>
    public static T Transaction<T>(PyConnection connection, Func<T> body, bool readOnly = false)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(body);
        var nested = connection.InTransaction;
        var savepoint = nested ? "margo_" + PyIds.Uuid4Hex() : null;
        try
        {
            connection.Execute(nested ? "SAVEPOINT " + savepoint : readOnly ? "BEGIN" : "BEGIN IMMEDIATE");
            var result = body();
            if (nested)
            {
                connection.Execute("RELEASE SAVEPOINT " + savepoint);
            }
            else
            {
                connection.Commit();
            }

            return result;
        }
        catch
        {
            if (nested)
            {
                connection.Execute("ROLLBACK TO SAVEPOINT " + savepoint);
                connection.Execute("RELEASE SAVEPOINT " + savepoint);
            }
            else
            {
                connection.Rollback();
            }

            throw;
        }
    }

    /// <summary><c>with transaction(connection, read_only=...): body</c>.</summary>
    public static void Transaction(PyConnection connection, Action body, bool readOnly = false)
    {
        ArgumentNullException.ThrowIfNull(body);
        Transaction(connection, () =>
        {
            body();
            return 0;
        }, readOnly);
    }

    /// <summary><c>add_state_arguments(parser)</c>: <c>--account</c> and <c>--state-dir</c>.</summary>
    public static void AddStateArguments(PyArgumentParser parser)
    {
        ArgumentNullException.ThrowIfNull(parser);
        parser.AddArgument("--account", help: "explicit principal; otherwise MARGO_ACCOUNT/private config");
        parser.AddArgument("--state-dir", help: "private state base directory; account hash is appended");
    }

    // ------------------------------------------------------------------------------ paths

    /// <summary><c>copilot_home()</c>: <c>COPILOT_HOME</c> (default <c>~/.copilot</c>), expanded and resolved.</summary>
    public static string CopilotHome() => PyPath.Resolve(PyPath.ExpandUser(PyRuntime.GetEnv("COPILOT_HOME") ?? "~/.copilot"));

    /// <summary><c>_no_symlinks(path)</c>: the path and every ancestor must not be a symlink.</summary>
    public static void NoSymlinks(string path)
    {
        foreach (var part in new[] { PyPath.Normalize(path) }.Concat(PyPath.Parents(path)))
        {
            if (PyPath.IsSymlink(part))
            {
                throw new StateError("symlinked state/config paths are not supported");
            }
        }
    }

    /// <summary><c>_private(path, directory)</c>: right type, owned by the current user, mode 0700/0600, one link.</summary>
    public static void Private(string path, bool directory = false)
    {
        var info = PosixNative.Stat(path);
        if (directory ? !info.IsDirectory : !info.IsRegular)
        {
            throw new StateError("state/config path has the wrong file type");
        }

        if (info.Uid != PosixNative.GetUid())
        {
            throw new StateError("state/config path must belong to the current user");
        }

        if ((info.Permissions & 0x3F) != 0)
        {
            throw new StateError("state/config path must be private (directory 0700, file 0600)");
        }

        if (!directory && info.NLink != 1)
        {
            throw new StateError("hard-linked state/config files are not supported");
        }
    }

    /// <summary><c>resolve_account(account)</c>: explicit, then <c>MARGO_ACCOUNT</c>, then the private config.</summary>
    public static string ResolveAccount(string? account = null)
    {
        object? candidate = account ?? PyRuntime.GetEnv("MARGO_ACCOUNT");
        if (candidate is null)
        {
            // os.environ.get("MARGO_CONFIG", str(copilot_home() / ...)) evaluates the default eagerly.
            var fallback = PyPath.Join(CopilotHome(), "margo/config.json");
            var config = PyPath.Absolute(PyPath.ExpandUser(PyRuntime.GetEnv("MARGO_CONFIG") ?? fallback));
            NoSymlinks(config);
            if (PyPath.Exists(config))
            {
                Private(config);
                var data = ReadJson(config);
                if (data is not JsonObject obj)
                {
                    throw new StateError("installation config must be a JSON object");
                }

                obj.TryGetPropertyValue("account", out var value);
                candidate = value is JsonValue scalar ? PyJson.Scalar(scalar) : value;
            }
        }

        if (candidate is null)
        {
            throw new SetupRequired("Set MARGO_ACCOUNT or account in private ~/.copilot/margo/config.json");
        }

        if (candidate is not string text || PyRepr.Strip(text).Length == 0 || PyRepr.Length(text) > 512
            || AccountForbidden().IsMatch(text))
        {
            throw new StateError("account must be an explicit, non-placeholder principal without whitespace");
        }

        // Opaque principal IDs may be case-sensitive; preserve exactly what was configured.
        return text;
    }

    /// <summary><c>_safe_directory(root, explicit_override)</c>.</summary>
    public static string SafeDirectory(string root, bool explicitOverride = false)
    {
        root = PyPath.Absolute(PyPath.ExpandUser(root));
        NoSymlinks(root);
        root = PyPath.Resolve(root);
        var unsafeTest = explicitOverride && PyRuntime.GetEnv("MARGO_ALLOW_UNSAFE_STATE_DIR") == "1";
        if (!unsafeTest)
        {
            var ancestors = new[] { root }.Concat(PyPath.Parents(root)).ToList();
            foreach (var ancestor in ancestors)
            {
                var name = PyPath.Name(ancestor).ToLowerInvariant();
                if (PyPath.Exists(PyPath.Join(ancestor, ".git"))
                    || SyncedWords.Any(word => name.Contains(word, StringComparison.Ordinal))
                    || SharedNames.Contains(name, StringComparer.Ordinal))
                {
                    throw new StateError("repository/shared/synchronised state/config roots are forbidden");
                }
            }

            foreach (var ancestor in ancestors)
            {
                if (PyPath.Exists(ancestor) && (PosixNative.Stat(ancestor).Mode & 0x12) != 0)
                {
                    throw new StateError("state/config root has a group/world-writable ancestor");
                }
            }
        }

        return root;
    }

    /// <summary><c>state_path(account, state_root)</c>: (principal, &lt;root&gt;/&lt;sha256(account)&gt;/margo.sqlite3).</summary>
    public static (string Principal, string Database) StatePath(string? account = null, string? stateRoot = null)
    {
        var principal = ResolveAccount(account);
        var overrideRoot = stateRoot ?? PyRuntime.GetEnv("MARGO_STATE_DIR");
        var root = SafeDirectory(overrideRoot ?? PyPath.Join(CopilotHome(), "margo/state"), explicitOverride: overrideRoot is not null);
        var key = PyIds.Sha256Hex(principal);
        return (principal, PyPath.Join(PyPath.Join(root, key), "margo.sqlite3"));
    }

    /// <summary><c>_mkdir_private(path)</c>: creates missing directories with mode 0700, then checks privacy.</summary>
    public static void MkdirPrivate(string path)
    {
        path = PyPath.Normalize(path);
        if (!PyPath.Exists(path))
        {
            var missing = new List<string>();
            var current = path;
            while (!PyPath.Exists(current))
            {
                missing.Add(current);
                current = PyPath.Parent(current);
            }

            missing.Reverse();
            foreach (var directory in missing)
            {
                try
                {
                    PosixNative.Mkdir(directory, Convert.ToUInt32("700", 8));
                }
                catch (PyOSError error) when (error.Errno == PyErrno.EEXIST)
                {
                }
            }
        }

        NoSymlinks(path);
        Private(path, directory: true);
    }

    // ------------------------------------------------------------------------------ connect

    /// <summary>
    /// <c>connect(account, state_root, read_only=...)</c>: opens validated storage; never infers an account
    /// or replaces damaged state. A read-only open of missing storage raises <see cref="NotInitialized"/>.
    /// </summary>
    public static PyConnection Connect(string? account = null, string? stateRoot = null, bool readOnly = false)
    {
        PyConnection? connection = null;
        try
        {
            var (principal, database) = StatePath(account, stateRoot);
            var parent = PyPath.Parent(database);
            if (readOnly)
            {
                if (!PyPath.Exists(database))
                {
                    throw new NotInitialized("Private memory state is not initialized; run memory_state.py init explicitly.");
                }

                Private(PyPath.Parent(parent), directory: true);
                Private(parent, directory: true);
            }
            else
            {
                MkdirPrivate(PyPath.Parent(parent));
                MkdirPrivate(parent);
            }

            NoSymlinks(database);
            var created = false;
            if (readOnly)
            {
                Private(database);
            }
            else
            {
                try
                {
                    var fd = PosixNative.Open(database, PosixNative.OCreat | PosixNative.OExcl | PosixNative.OWrOnly | PosixNative.ONoFollow,
                                              Convert.ToUInt32("600", 8));
                    PosixNative.Close(fd);
                    created = true;
                }
                catch (PyOSError error) when (error.Errno == PyErrno.EEXIST)
                {
                    Private(database);
                }
            }

            foreach (var suffix in new[] { "-journal", "-wal", "-shm" })
            {
                var sidecar = database + suffix;
                NoSymlinks(sidecar);
                if (PyPath.Exists(sidecar))
                {
                    Private(sidecar);
                }
            }

            connection = PyConnection.Connect(database, readOnly, timeout: 5, isolationLevel: "IMMEDIATE");
            connection.RowFactoryIsRow = true;
            connection.Execute("PRAGMA busy_timeout=5000");
            connection.Execute("PRAGMA foreign_keys=ON");
            connection.Execute("PRAGMA temp_store=MEMORY");
            var open = connection;
            open.With(() =>
            {
                open.Execute(readOnly ? "BEGIN" : "BEGIN IMMEDIATE");
                var tables = open.Execute("SELECT name FROM sqlite_master WHERE type='table'")
                                 .Select(row => (string?)row[0]).ToHashSet(StringComparer.Ordinal);
                if (!tables.Contains("margo_meta"))
                {
                    if (!created)
                    {
                        throw new StateError("existing database has no Margo identity; refusing to reset it");
                    }

                    open.Execute("CREATE TABLE margo_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL)");
                    open.ExecuteMany("INSERT INTO margo_meta VALUES (?,?)", [["account", principal], ["store_version", "1"]]);
                }

                var metadata = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var row in open.Execute("SELECT key,value FROM margo_meta"))
                {
                    metadata[(string)row[0]!] = row[1];
                }

                if (!Equals(metadata.GetValueOrDefault("account"), principal) || !Equals(metadata.GetValueOrDefault("store_version"), "1"))
                {
                    throw new StateError("database account/schema mismatch; explicit migration required");
                }

                if (!Equals(open.Execute("PRAGMA quick_check").FetchOne()?[0], "ok"))
                {
                    throw new StateError("database integrity check failed; restore a verified backup");
                }

                if (open.Execute("PRAGMA foreign_key_check").FetchOne() is not null)
                {
                    throw new StateError("database contains broken references; refusing to continue");
                }
            });
            if (!readOnly)
            {
                connection.Execute("PRAGMA synchronous=FULL");
            }

            Private(database);
            return connection;
        }
        catch (Exception ex) when (ex is PyOSError or PySqliteError or StateError)
        {
            connection?.Close();
            if (ex is StateError)
            {
                throw;
            }

            var name = ex is PyOSError os ? os.PythonType : ((PySqliteError)ex).PythonType;
            throw new StateError("storage unavailable; no reset performed (" + name + ")", ex);
        }
    }

    // ------------------------------------------------------------------------------ config

    /// <summary>
    /// <c>initialize_config(account, config_path)</c>: creates the private owner config atomically (staging file,
    /// fsync, no-replace hard link), never replacing another owner. Returns the canonical result object.
    /// </summary>
    public static JsonObject InitializeConfig(string? account, string? configPath = null)
    {
        if (account is null)
        {
            throw new StateError("init requires an explicit --account owner principal");
        }

        var principal = ResolveAccount(account);
        var overridePath = configPath ?? PyRuntime.GetEnv("MARGO_CONFIG");
        var path = PyPath.Absolute(PyPath.ExpandUser(overridePath ?? PyPath.Join(CopilotHome(), "margo/config.json")));
        string? staging = null;
        try
        {
            NoSymlinks(path);
            var parent = SafeDirectory(PyPath.Parent(path), explicitOverride: overridePath is not null);
            path = PyPath.Join(parent, PyPath.Name(path));
            MkdirPrivate(parent);

            void VerifyExisting()
            {
                NoSymlinks(path);
                Private(path);
                var data = ReadJson(path);
                if (data is not JsonObject obj || !obj.TryGetPropertyValue("account", out var value)
                    || value is null || !PyJson.IsStr(value) || (string?)PyJson.Scalar(value) != principal)
                {
                    throw new StateError("existing config has a different/missing account; refusing to replace it");
                }
            }

            JsonObject Result(bool created) => new()
            {
                ["status"] = "configured",
                ["created"] = created,
                ["account_configured"] = true,
                ["config_path"] = path,
            };

            if (PyPath.Exists(path))
            {
                VerifyExisting();
                return Result(false);
            }

            staging = PyPath.Join(parent, "." + PyPath.Name(path) + "." + PyIds.Uuid4Hex() + ".new");
            var fd = PosixNative.Open(staging, PosixNative.OCreat | PosixNative.OExcl | PosixNative.OWrOnly | PosixNative.ONoFollow,
                                      Convert.ToUInt32("600", 8));
            using (var handle = new SafeFileHandle(fd, ownsHandle: true))
            using (var stream = new FileStream(handle, FileAccess.Write))
            {
                var bytes = PyIO.EncodeUtf8(CanonicalJson(new JsonObject { ["account"] = principal }) + "\n");
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            var created = true;
            try
            {
                // An atomic no-replace link prevents concurrent initializers from
                // silently changing the account selected by another initializer.
                LinkHook(staging, path);
            }
            catch (PyOSError error) when (error.Errno == PyErrno.EEXIST)
            {
                created = false;
            }

            PosixNative.Unlink(staging);
            staging = null;
            VerifyExisting();
            var directory = PosixNative.Open(parent, PosixNative.ORdOnly | PosixNative.ODirectory);
            try
            {
                PosixNative.Fsync(directory);
            }
            finally
            {
                PosixNative.Close(directory);
            }

            return Result(created);
        }
        catch (PyOSError ex)
        {
            throw new StateError("configuration unavailable; no existing config replaced", ex);
        }
        finally
        {
            if (staging is not null)
            {
                try
                {
                    PosixNative.Unlink(staging);
                }
                catch (PyOSError error) when (error.Errno == PyErrno.ENOENT)
                {
                }
            }
        }
    }

    private static readonly AsyncLocal<Action<string, string>?> LinkOverride = new();

    /// <summary>The <c>os.link</c> call of <see cref="InitializeConfig"/>.</summary>
    private static void LinkHook(string source, string destination) => (LinkOverride.Value ?? PosixNative.Link)(source, destination);

    /// <summary>Replaces <c>os.link</c> for the current async flow (tests simulate an interrupted publication).</summary>
    internal static IDisposable OverrideLink(Action<string, string> link)
    {
        var outer = LinkOverride.Value;
        LinkOverride.Value = link;
        return new LinkRestore(outer);
    }

    private sealed class LinkRestore(Action<string, string>? outer) : IDisposable
    {
        public void Dispose() => LinkOverride.Value = outer;
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,6})?(?:Z|[+-]\d{2}:\d{2})\z", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampPattern();

    [GeneratedRegex(@"[\x00-\x20\x7f{}]", RegexOptions.CultureInvariant)]
    private static partial Regex AccountForbidden();
}

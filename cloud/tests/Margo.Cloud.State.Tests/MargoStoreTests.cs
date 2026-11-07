using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using Xunit;

namespace Margo.Cloud.State.Tests;

/// <summary>Ports of the margo_store cases in tests/test_proactive_state.py and the store contract.</summary>
public sealed class MargoStoreTests
{
    [Fact]
    public void Store_identity_permissions_transactions_and_foreign_keys()
    {
        using var root = TempRoot.Create();
        using var connection = StateStore.Connect("tenant:user", root.State);
        Assert.Equal(1L, connection.Execute("PRAGMA foreign_keys").FetchOne()![0]);
        Assert.IsType<PyRow>(connection.Execute("SELECT 1").FetchOne());
        connection.Execute("CREATE TABLE test_transaction (value TEXT)");
        Assert.Throws<InvalidOperationException>(() => connection.With(() =>
        {
            connection.Execute("INSERT INTO test_transaction VALUES ('rollback')");
            throw new InvalidOperationException("interrupted");
        }));
        Assert.Equal(0L, connection.Execute("SELECT count(*) FROM test_transaction").FetchOne()![0]);
        var (_, path) = StateStore.StatePath("tenant:user", root.State);
        Assert.Equal("600", Modes.Of(path));
        Assert.Equal("700", Modes.Of(Path.GetDirectoryName(path)!));
        Assert.Equal(PyIds.Sha256Hex("tenant:user"), Path.GetFileName(Path.GetDirectoryName(path)));
        using var other = StateStore.Connect("other-tenant:user", root.State);
        Assert.Equal("other-tenant:user", other.Execute("SELECT value FROM margo_meta WHERE key='account'").FetchOne()![0]);
        Assert.Empty(other.Execute("SELECT name FROM sqlite_master WHERE name='test_transaction'").FetchAll());
    }

    [Fact]
    public void Connect_pragmas_and_identity_rows()
    {
        using var root = TempRoot.Create();
        using (var connection = StateStore.Connect("owner@example.com", root.State))
        {
            Assert.Equal(5000L, connection.Execute("PRAGMA busy_timeout").FetchOne()![0]);
            Assert.Equal(2L, connection.Execute("PRAGMA temp_store").FetchOne()![0]);
            Assert.Equal(2L, connection.Execute("PRAGMA synchronous").FetchOne()![0]);
            Assert.False(connection.InTransaction);
            Assert.Equal("IMMEDIATE", connection.IsolationLevel);
            var meta = connection.Execute("SELECT key,value FROM margo_meta ORDER BY key").Select(row => (string)row[0]! + "=" + row[1]).ToList();
            Assert.Equal(["account=owner@example.com", "store_version=1"], meta);
        }

        using var readOnly = StateStore.Connect("owner@example.com", root.State, readOnly: true);
        Assert.Throws<PySqliteError>(() => readOnly.Execute("CREATE TABLE nope (x)"));
    }

    [Fact]
    public void Read_only_connect_never_creates_state()
    {
        using var root = TempRoot.Create();
        var error = Assert.Throws<NotInitialized>(() => StateStore.Connect("owner@example.com", root.State, readOnly: true));
        Assert.Equal("Private memory state is not initialized; run memory_state.py init explicitly.", error.Message);
        Assert.False(Directory.Exists(root.State));
    }

    [Fact]
    public void Account_config_and_unsafe_roots_fail_closed()
    {
        using var root = TempRoot.Create();
        using (TempRoot.With(("MARGO_ACCOUNT", ""), ("MARGO_ALLOW_UNSAFE_STATE_DIR", "0")))
        {
            Assert.Throws<StateError>(() => StateStore.Connect(null, root.Combine("bad")));
            Assert.Throws<StateError>(() => StateStore.Connect("user", root.Combine("bad")));
        }

        var config = root.Combine("config.json");
        File.WriteAllText(config, "{\"account\":\"private-user\"}");
        Modes.Set(config, "600");
        using (TempRoot.With(("MARGO_ACCOUNT", null), ("MARGO_CONFIG", config)))
        {
            Assert.Equal("private-user", StateStore.ResolveAccount());
            Modes.Set(config, "644");
            Assert.Equal("state/config path must be private (directory 0700, file 0600)",
                         Assert.Throws<StateError>(() => StateStore.ResolveAccount()).Message);
        }

        var target = root.Combine("linked");
        Directory.CreateSymbolicLink(target, root.State);
        Assert.Equal("symlinked state/config paths are not supported",
                     Assert.Throws<StateError>(() => StateStore.Connect("user", target)).Message);
    }

    [Fact]
    public void Missing_account_is_setup_required_and_invalid_principals_are_refused()
    {
        using var root = TempRoot.Create();
        Assert.IsType<SetupRequired>(Assert.ThrowsAny<StateError>(() => StateStore.ResolveAccount()));
        foreach (var account in new[] { "two words", "{placeholder}", " ", "tab\there", new string('a', 513), "del\u007f" })
        {
            Assert.Equal("account must be an explicit, non-placeholder principal without whitespace",
                         Assert.Throws<StateError>(() => StateStore.ResolveAccount(account)).Message);
        }

        Assert.Equal(new string('a', 512), StateStore.ResolveAccount(new string('a', 512)));
        Assert.Equal("CaseSensitive:Id", StateStore.ResolveAccount("CaseSensitive:Id"));
    }

    [Fact]
    public void Repository_roots_are_forbidden_without_the_synthetic_opt_in()
    {
        using var root = TempRoot.Create();
        var repository = root.Combine("repo");
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        using (TempRoot.With(("MARGO_ALLOW_UNSAFE_STATE_DIR", "0")))
        {
            Assert.Equal("repository/shared/synchronised state/config roots are forbidden",
                         Assert.Throws<StateError>(() => StateStore.SafeDirectory(Path.Combine(repository, "state"), true)).Message);
            Assert.Equal("repository/shared/synchronised state/config roots are forbidden",
                         Assert.Throws<StateError>(() => StateStore.SafeDirectory(root.Combine("OneDrive - Example", "x"), true)).Message);
        }

        Assert.Equal(Path.Combine(repository, "state"), StateStore.SafeDirectory(Path.Combine(repository, "state"), true));
        Assert.Equal("repository/shared/synchronised state/config roots are forbidden",
                     Assert.Throws<StateError>(() => StateStore.SafeDirectory(Path.Combine(repository, "state"), false)).Message);
    }

    [Fact]
    public void Malformed_databases_and_empty_files_are_never_reset()
    {
        using var root = TempRoot.Create();
        var (_, damaged) = StateStore.StatePath("damaged", root.State);
        Directory.CreateDirectory(root.State, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.CreateDirectory(Path.GetDirectoryName(damaged)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllBytes(damaged, "not a database"u8.ToArray());
        Modes.Set(damaged, "600");
        Assert.Equal("storage unavailable; no reset performed (DatabaseError)",
                     Assert.Throws<StateError>(() => StateStore.Connect("damaged", root.State)).Message);
        Assert.Equal("not a database"u8.ToArray(), File.ReadAllBytes(damaged));

        var (_, empty) = StateStore.StatePath("empty-existing", root.State);
        Directory.CreateDirectory(Path.GetDirectoryName(empty)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllBytes(empty, []);
        Modes.Set(empty, "600");
        Assert.Equal("existing database has no Margo identity; refusing to reset it",
                     Assert.Throws<StateError>(() => StateStore.Connect("empty-existing", root.State)).Message);
        Assert.Equal(0, new FileInfo(empty).Length);
    }

    [Fact]
    public void Wrong_owner_identity_and_shared_modes_are_refused()
    {
        using var root = TempRoot.Create();
        StateStore.Connect("owner@example.com", root.State).Close();
        var (_, path) = StateStore.StatePath("owner@example.com", root.State);
        using (var raw = PyConnection.Connect(path))
        {
            raw.Execute("UPDATE margo_meta SET value='someone-else@example.com' WHERE key='account'");
            raw.Commit();
        }

        Assert.Equal("database account/schema mismatch; explicit migration required",
                     Assert.Throws<StateError>(() => StateStore.Connect("owner@example.com", root.State)).Message);
        Modes.Set(path, "640");
        Assert.Equal("state/config path must be private (directory 0700, file 0600)",
                     Assert.Throws<StateError>(() => StateStore.Connect("owner@example.com", root.State)).Message);
        Modes.Set(path, "600");
        var link = path + ".hardlink";
        PosixNative.Link(path, link);
        Assert.Equal("hard-linked state/config files are not supported",
                     Assert.Throws<StateError>(() => StateStore.Connect("owner@example.com", root.State)).Message);
    }

    [Fact]
    public void Nested_transactions_use_savepoints_owned_by_the_outer_caller()
    {
        using var root = TempRoot.Create();
        using var connection = StateStore.Connect("owner@example.com", root.State);
        connection.Execute("CREATE TABLE items (label TEXT UNIQUE)");
        List<string> Labels() => connection.Execute("SELECT label FROM items ORDER BY rowid").Select(r => (string)r[0]!).ToList();
        StateStore.Transaction(connection, () =>
        {
            Assert.True(connection.InTransaction);
            connection.Execute("INSERT INTO items VALUES ('outer')");
            Assert.Throws<InvalidOperationException>(() => StateStore.Transaction(connection, () =>
            {
                connection.Execute("INSERT INTO items VALUES ('inner')");
                throw new InvalidOperationException("inner failure");
            }));
            Assert.True(connection.InTransaction);
            Assert.Equal(["outer"], Labels());
            StateStore.Transaction(connection, () => connection.Execute("INSERT INTO items VALUES ('kept')"));
        });
        Assert.False(connection.InTransaction);
        Assert.Equal(["outer", "kept"], Labels());
        var error = Assert.Throws<PySqliteError>(() => StateStore.Transaction(connection, () =>
        {
            connection.Execute("INSERT INTO items VALUES ('lost')");
            connection.Execute("INSERT INTO items VALUES ('outer')");
        }));
        Assert.Equal(PySqliteErrorKind.IntegrityError, error.Kind);
        Assert.Equal("UNIQUE constraint failed: items.label", error.Message);
        Assert.False(connection.InTransaction);
        Assert.Equal(["outer", "kept"], Labels());
        Assert.Equal(2, StateStore.Transaction(connection, () => Labels().Count, readOnly: true));
    }

    [Fact]
    public void Private_config_init_cli_replay_and_owner_isolation()
    {
        using var root = TempRoot.Create();
        var config = root.Combine("private-config", "config.json");
        const string Principal = "synthetic-owner@example.com";
        var (code, stdout, stderr) = ScriptRunner.Run(new MargoStoreScript(), ["init", "--account", Principal, "--config", config]);
        Assert.Equal(0, code);
        Assert.Equal("", stderr);
        Assert.True(PyJson.Truthy(PyJson.Loads(stdout)!["created"]));
        Assert.DoesNotContain(Principal, stdout, StringComparison.Ordinal);
        Assert.Equal("{\"account\":\"synthetic-owner@example.com\"}", StateStore.CanonicalJson(StateStore.ReadJson(config)));
        Assert.Equal("600", Modes.Of(config));
        Assert.Equal("700", Modes.Of(Path.GetDirectoryName(config)!));
        Assert.Equal([config], Directory.GetFileSystemEntries(Path.GetDirectoryName(config)!));
        (code, stdout, _) = ScriptRunner.Run(new MargoStoreScript(), ["init", "--account", Principal, "--config", config]);
        Assert.Equal(0, code);
        Assert.False(PyJson.Truthy(PyJson.Loads(stdout)!["created"]));
        var original = File.ReadAllBytes(config);
        Assert.Throws<StateError>(() => StateStore.InitializeConfig("different-owner@example.com", config));
        Assert.Equal(original, File.ReadAllBytes(config));
        using (TempRoot.With(("MARGO_ACCOUNT", null), ("MARGO_CONFIG", config)))
        {
            Assert.Equal(Principal, StateStore.ResolveAccount());
        }

        File.WriteAllText(config, "{broken");
        Assert.Throws<StateError>(() => StateStore.InitializeConfig(Principal, config));
        Assert.Equal("{broken", File.ReadAllText(config));
    }

    [Fact]
    public void Config_init_unsafe_roots_and_interrupted_publication()
    {
        using var root = TempRoot.Create();
        var config = root.Combine("private-config", "config.json");
        using (TempRoot.With(("MARGO_ALLOW_UNSAFE_STATE_DIR", "0")))
        {
            Assert.Throws<StateError>(() => StateStore.InitializeConfig("synthetic-owner", config));
        }

        Assert.False(Directory.Exists(Path.GetDirectoryName(config)));
        using (StateStore.OverrideLink((_, _) => throw new PyOSError("simulated interruption")))
        {
            Assert.Equal("configuration unavailable; no existing config replaced",
                         Assert.Throws<StateError>(() => StateStore.InitializeConfig("synthetic-owner", config)).Message);
        }

        Assert.False(File.Exists(config));
        Assert.Empty(Directory.GetFileSystemEntries(Path.GetDirectoryName(config)!));
        var result = StateStore.InitializeConfig("synthetic-owner", config);
        Assert.Equal("{\"account_configured\":true,\"config_path\":\"" + config + "\",\"created\":true,\"status\":\"configured\"}",
                     StateStore.CanonicalJson(result));
        Assert.Equal("init requires an explicit --account owner principal",
                     Assert.Throws<StateError>(() => StateStore.InitializeConfig(null, config)).Message);
    }

    [Fact]
    public void Concurrent_initializers_never_replace_the_first_owner()
    {
        using var root = TempRoot.Create();
        var config = root.Combine("cfg", "config.json");
        using (StateStore.OverrideLink((source, destination) =>
               {
                   File.WriteAllText(destination, "{\"account\":\"winner@example.com\"}\n");
                   Modes.Set(destination, "600");
                   PosixNative.Link(source, destination);
               }))
        {
            Assert.Equal("existing config has a different/missing account; refusing to replace it",
                         Assert.Throws<StateError>(() => StateStore.InitializeConfig("loser@example.com", config)).Message);
        }

        Assert.Equal("{\"account\":\"winner@example.com\"}", StateStore.CanonicalJson(StateStore.ReadJson(config)));
        Assert.Equal([config], Directory.GetFileSystemEntries(Path.GetDirectoryName(config)!));
    }

    [Fact]
    public void Read_json_reports_unreadable_paths_and_invalid_documents()
    {
        using var root = TempRoot.Create();
        var missing = root.Combine("missing.json");
        Assert.Equal("cannot read valid JSON from " + missing, Assert.Throws<StateError>(() => StateStore.ReadJson(missing)).Message);
        var bad = root.Combine("bad.json");
        File.WriteAllBytes(bad, [0xff, 0xfe]);
        Assert.Equal("cannot read valid JSON from " + bad, Assert.Throws<StateError>(() => StateStore.ReadJson(bad)).Message);
        File.WriteAllText(bad, "\uFEFF{}");
        Assert.Equal("invalid JSON", Assert.Throws<StateError>(() => StateStore.ReadJson(bad)).Message);
        File.WriteAllText(bad, "{\"a\":\r\n1}");
        Assert.Equal("{\"a\":1}", StateStore.CanonicalJson(StateStore.ReadJson(bad)));
    }

    [Fact]
    public void Add_state_arguments_defines_account_and_state_dir()
    {
        var parser = new PyArgumentParser("x.py");
        StateStore.AddStateArguments(parser);
        var ns = parser.ParseArgs(["--state-dir", "/s", "--account", "a"], TextWriter.Null, TextWriter.Null);
        Assert.Equal("/s", ns.GetString("state_dir"));
        Assert.Equal("a", ns.GetString("account"));
    }
}

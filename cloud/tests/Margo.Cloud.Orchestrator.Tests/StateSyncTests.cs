using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using Microsoft.Data.Sqlite;

namespace Margo.Cloud.Orchestrator.Tests;

/// <summary><c>StateSyncTests</c> of <c>tests/test_autopilot_runtime.py</c>, with the in-memory lease store, plus the bundle checks.</summary>
public sealed class StateSyncTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 0, 0, 0, TimeSpan.Zero);

    private readonly InMemoryLeaseStore _store = new();

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private (string Database, StateSync Sync) Session(TempState state, string name, string? account = null, ILeaseStore? store = null,
                                                      TimeSpan? renew = null)
    {
        var database = state.Combine(name, "state", "margo.sqlite3");
        return (database, new StateSync(database, store ?? _store, account ?? Fixtures.Binding.Account, state.Combine(name, "preferences.md"), renew));
    }

    private static Task<StateSyncOutcome<bool>> Turn(StateSync sync, Action work) =>
        sync.RunAsync(_ =>
        {
            work();
            return Task.FromResult(true);
        }, Now, Cancel);

    private static void MakeDb(string path, string account)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Execute(path, "CREATE TABLE IF NOT EXISTS margo_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL)");
        Execute(path, "INSERT OR IGNORE INTO margo_meta VALUES ('account', $account), ('store_version', '1')", ("$account", account));
        Execute(path, "CREATE TABLE IF NOT EXISTS notes (text TEXT)");
    }

    private static void WriteFile(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(path, text);
    }

    private static void AddNote(string path, string text) => Execute(path, "INSERT INTO notes VALUES ($text)", ("$text", text));

    private static List<string> Notes(string path)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT text FROM notes ORDER BY rowid";
        using var reader = command.ExecuteReader();
        var notes = new List<string>();
        while (reader.Read())
        {
            notes.Add(reader.GetString(0));
        }

        return notes;
    }

    private static void Execute(string path, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    [Fact]
    public async Task Two_sandboxes_share_one_state()
    {
        using var state = new TempState();
        var (dbA, syncA) = Session(state, "sandbox-a");
        var (dbB, syncB) = Session(state, "sandbox-b");
        await Turn(syncA, () =>
        {
            MakeDb(dbA, Fixtures.Binding.Account);
            AddNote(dbA, "from the manager chat");
            File.WriteAllText(state.Combine("sandbox-a", "preferences.md"), "- **Replies to other people:** acknowledge\n");
        });
        var result = await Turn(syncB, () => AddNote(dbB, "from a colleague's message"));
        Assert.NotNull(result.Restored);
        Assert.Contains("acknowledge", File.ReadAllText(state.Combine("sandbox-b", "preferences.md")), StringComparison.Ordinal);
        await Turn(syncA, () => { });
        Assert.Equal(["from the manager chat", "from a colleague's message"], Notes(dbA));
        Assert.Contains("daily/2026-10-17.zip", _store.Puts);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(dbB));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(state.Combine("sandbox-b", "preferences.md")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(dbB)!));
    }

    [Fact]
    public async Task Concurrent_turns_never_overlap()
    {
        using var state = new TempState();
        var (dbA, syncA) = Session(state, "sandbox-a");
        var (dbB, syncB) = Session(state, "sandbox-b");
        await Turn(syncA, () => MakeDb(dbA, Fixtures.Binding.Account));
        var events = new List<string>();

        Task Worker(StateSync sync, string db, string label) => Task.Run(() => sync.RunAsync(async _ =>
        {
            lock (events)
            {
                events.Add(label + ":start");
            }

            await Task.Delay(50, Cancel);
            AddNote(db, label);
            lock (events)
            {
                events.Add(label + ":end");
            }

            return true;
        }, cancellationToken: Cancel), Cancel);

        await Task.WhenAll(Worker(syncA, dbA, "a"), Worker(syncB, dbB, "b"));
        Assert.Contains(string.Join(",", events), new[] { "a:start,a:end,b:start,b:end", "b:start,b:end,a:start,a:end" });
        await Turn(syncA, () => { });
        Assert.Equal(["a", "b"], Notes(dbA).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Failed_turn_does_not_leak_unsaved_changes()
    {
        using var state = new TempState();
        var (dbA, syncA) = Session(state, "sandbox-a");
        await Turn(syncA, () =>
        {
            MakeDb(dbA, Fixtures.Binding.Account);
            AddNote(dbA, "saved");
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => Turn(syncA, () =>
        {
            AddNote(dbA, "never saved");
            throw new InvalidOperationException("model failed");
        }));
        Assert.False(File.Exists(syncA.MarkerPath));
        await Turn(syncA, () => { });
        Assert.Equal(["saved"], Notes(dbA));
    }

    [Fact]
    public async Task A_failed_first_turn_leaves_nothing_to_build_on()
    {
        using var state = new TempState();
        var (dbA, syncA) = Session(state, "sandbox-a");
        var preferences = state.Combine("sandbox-a", "preferences.md");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Turn(syncA, () =>
        {
            MakeDb(dbA, Fixtures.Binding.Account);
            AddNote(dbA, "never saved");
            File.WriteAllText(preferences, "- **Replies to other people:** acknowledge\n");
            throw new InvalidOperationException("model failed");
        }));
        await Turn(syncA, () => MakeDb(dbA, Fixtures.Binding.Account));
        Assert.Empty(Notes(dbA));
        Assert.False(File.Exists(preferences));
    }

    [Fact]
    public async Task Removed_shared_state_is_not_resurrected_from_a_sandbox()
    {
        using var state = new TempState();
        var (dbA, syncA) = Session(state, "sandbox-a");
        await Turn(syncA, () =>
        {
            MakeDb(dbA, Fixtures.Binding.Account);
            AddNote(dbA, "before removal");
        });
        Assert.True(_store.Remove(StateSync.Bundle)); // an operator removed the instance's data
        await Turn(syncA, () => { });
        Assert.False(File.Exists(dbA));
        Assert.DoesNotContain(StateSync.Bundle, _store.Names);
    }

    [Fact]
    public async Task Foreign_or_tampered_bundles_are_refused()
    {
        using var state = new TempState();
        var (dbA, syncA) = Session(state, "sandbox-a");
        await Turn(syncA, () =>
        {
            MakeDb(dbA, Fixtures.Binding.Account);
            AddNote(dbA, "dana's state");
        });
        var (dbX, syncX) = Session(state, "sandbox-x", account: "autopilot:agent-identity-1:principal-2");
        var foreign = await Assert.ThrowsAsync<SyncException>(() => Turn(syncX, () => { }));
        Assert.Equal("state bundle belongs to another account; refusing it", foreign.Message);
        Assert.False(File.Exists(dbX));
        var (data, _) = _store.Get(StateSync.Bundle)!.Value;
        _store.Set(StateSync.Bundle, [.. data[..^20], .. Encoding.ASCII.GetBytes(new string('x', 20))], "etag-tampered");
        var (dbC, syncC) = Session(state, "sandbox-c");
        var tampered = await Assert.ThrowsAsync<SyncException>(() => Turn(syncC, () => { }));
        Assert.Equal("state bundle is unreadable", tampered.Message);
        Assert.False(File.Exists(dbC));
    }

    [Fact]
    public async Task Refuses_to_upload_another_accounts_database()
    {
        using var state = new TempState();
        var (dbA, syncA) = Session(state, "sandbox-a");
        var error = await Assert.ThrowsAsync<SyncException>(() => Turn(syncA, () => MakeDb(dbA, "autopilot:other:person")));
        Assert.Equal("state belongs to another account; refusing it", error.Message);
        Assert.DoesNotContain(StateSync.Bundle, _store.Names);
    }

    [Fact]
    public async Task Lease_renewal_failure_is_unsaved_released_and_typed()
    {
        using var state = new TempState();
        var (dbA, syncA) = Session(state, "sandbox-a");
        await Turn(syncA, () =>
        {
            MakeDb(dbA, Fixtures.Binding.Account);
            AddNote(dbA, "saved");
        });
        var flaky = new FlakyStore(_store);
        var (_, flakySync) = Session(state, "sandbox-a", store: flaky, renew: TimeSpan.FromMilliseconds(10));
        var error = await Assert.ThrowsAsync<SyncException>(() => flakySync.RunAsync(async _ =>
        {
            AddNote(dbA, "unsaved");
            await Task.Delay(100, Cancel);
            return true;
        }, cancellationToken: Cancel));
        Assert.Equal("the state lease could not be renewed; nothing was saved", error.Message);
        Assert.Equal(1, flaky.Released);
        await Turn(syncA, () => { });
        Assert.Equal(["saved"], Notes(dbA));
    }

    [Fact]
    public void Container_names_are_valid_and_distinct()
    {
        var name = StateSync.ContainerName(Fixtures.Binding.Account);
        Assert.InRange(name.Length, 3, 63);
        Assert.Equal(name.ToLowerInvariant(), name);
        Assert.Equal("margo-" + PyIds.Sha256Hex(Fixtures.Binding.Account)[..40], name);
        Assert.NotEqual(name, StateSync.ContainerName("autopilot:agent-identity-1:principal-2"));
    }

    // -- beyond the Python tests --------------------------------------------------------------

    [Fact]
    public async Task The_bundle_holds_a_verified_snapshot_and_a_manifest()
    {
        using var state = new TempState();
        var (dbA, syncA) = Session(state, "sandbox-a");
        var preferences = state.Combine("sandbox-a", "preferences.md");
        WriteFile(preferences, "never saved, so discarded");
        var outcome = await Turn(syncA, () =>
        {
            Assert.False(File.Exists(preferences)); // no shared bundle: local state that was never saved is discarded
            MakeDb(dbA, Fixtures.Binding.Account);
            WriteFile(preferences, "- **Replies to other people:** none\n");
        });
        Assert.Null(outcome.Restored); // nothing was installed: there was no shared bundle
        var (data, etag) = _store.Get(StateSync.Bundle)!.Value;
        Assert.Equal(etag, File.ReadAllText(syncA.MarkerPath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(syncA.MarkerPath));
        using var bundle = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
        Assert.Equal(["manifest.json", "margo.sqlite3", "preferences.md"], bundle.Entries.Select(entry => entry.FullName));
        var database = ReadEntry(bundle, "margo.sqlite3");
        var manifest = Encoding.UTF8.GetString(ReadEntry(bundle, "manifest.json"));
        Assert.Equal("{\"account_sha256\": \"" + PyIds.Sha256Hex(Fixtures.Binding.Account) + "\", \"database_sha256\": \""
                     + PyIds.Sha256Hex(database) + "\", \"format\": 1, \"taken_at\": \"2026-10-17T00:00:00+00:00\"}", manifest);
        Assert.Equal("- **Replies to other people:** none\n", Encoding.UTF8.GetString(ReadEntry(bundle, "preferences.md")));
        Assert.Equal(data, _store.GetCopy("daily/2026-10-17.zip"));
    }

    [Fact]
    public async Task A_newer_bundle_is_installed_and_reported_once()
    {
        using var state = new TempState();
        var (dbA, syncA) = Session(state, "sandbox-a");
        var (dbB, syncB) = Session(state, "sandbox-b");
        await Turn(syncA, () => MakeDb(dbA, Fixtures.Binding.Account));
        var first = await Turn(syncB, () => { });
        Assert.Equal(PyIds.Sha256Hex(Fixtures.Binding.Account), PyNode.AsString(first.Restored!["account_sha256"]));
        Assert.Equal("2026-10-17T00:00:00+00:00", PyNode.AsString(first.Restored!["taken_at"]));
        var second = await Turn(syncB, () => { });
        Assert.Null(second.Restored); // the local copy is current
        Assert.True(File.Exists(dbB));
    }

    [Fact]
    public async Task Bundles_the_python_service_would_refuse_are_refused()
    {
        using var state = new TempState();
        var (dbA, syncA) = Session(state, "sandbox-a");
        await Turn(syncA, () => MakeDb(dbA, Fixtures.Binding.Account));
        var (good, _) = _store.Get(StateSync.Bundle)!.Value;

        async Task<SyncException> Refused(byte[] bundle, string name)
        {
            _store.Set(StateSync.Bundle, bundle, "etag-" + name);
            var (db, sync) = Session(state, name);
            var error = await Assert.ThrowsAsync<SyncException>(() => Turn(sync, () => { }));
            Assert.False(File.Exists(db));
            return error;
        }

        Assert.Equal("state bundle is unreadable", (await Refused(Encoding.ASCII.GetBytes("not a zip"), "garbage")).Message);
        Assert.Equal("state bundle is unreadable", (await Refused(Rezip(good, drop: "manifest.json"), "no-manifest")).Message);
        Assert.Equal("state bundle is unreadable", (await Refused(Rezip(good, drop: "margo.sqlite3"), "no-database")).Message);
        Assert.Equal("state bundle is unreadable", (await Refused(Rezip(good, replace: ("manifest.json", "{not json"u8.ToArray())), "bad-json")).Message);
        Assert.Equal("state bundle content does not match its manifest",
                     (await Refused(Rezip(good, replace: ("margo.sqlite3", [1, 2, 3])), "mismatch")).Message);
        // A manifest that matches a database of another account: the database check refuses it too.
        var (otherDb, otherSync) = Session(state, "other", account: "autopilot:other:person", store: new InMemoryLeaseStore());
        MakeDb(otherDb, "autopilot:other:person");
        var otherBytes = File.ReadAllBytes(otherDb);
        var forged = Rezip(good, replace: ("margo.sqlite3", otherBytes));
        forged = Rezip(forged, replace: ("manifest.json", Encoding.UTF8.GetBytes(
            "{\"account_sha256\": \"" + PyIds.Sha256Hex(Fixtures.Binding.Account) + "\", \"database_sha256\": \"" + PyIds.Sha256Hex(otherBytes) + "\"}")));
        Assert.Equal("state belongs to another account; refusing it", (await Refused(forged, "forged")).Message);
        Assert.NotNull(otherSync);
    }

    [Fact]
    public async Task An_entry_that_fails_its_checksum_is_refused_as_python_s_zipfile_refuses_it()
    {
        // zipfile checks each entry's CRC-32 as it reads it ("Bad CRC-32", BadZipFile): a corrupt bundle is unreadable,
        // even where the manifest's database hash does not cover the bytes (the preferences, the manifest itself).
        using var state = new TempState();
        var (dbA, syncA) = Session(state, "sandbox-a");
        await Turn(syncA, () =>
        {
            MakeDb(dbA, Fixtures.Binding.Account);
            WriteFile(state.Combine("sandbox-a", "preferences.md"), "- **Replies to other people:** acknowledge\n");
        });
        var (good, _) = _store.Get(StateSync.Bundle)!.Value;
        var stored = Rezip(good, compression: CompressionLevel.NoCompression);

        async Task Refused(byte[] bundle, string name)
        {
            _store.Set(StateSync.Bundle, bundle, "etag-" + name);
            var (db, sync) = Session(state, name);
            Assert.Equal("state bundle is unreadable", (await Assert.ThrowsAsync<SyncException>(() => Turn(sync, () => { }))).Message);
            Assert.False(File.Exists(db));
            Assert.False(File.Exists(state.Combine(name, "preferences.md")));
        }

        await Refused(Corrupt(stored, "acknowledge"u8, "ACKNOWLEDGE"u8), "preferences-corrupt");
        await Refused(Corrupt(stored, "\"taken_at\": \"2026"u8, "\"taken_at\": \"2025"u8), "manifest-corrupt");
    }

    [Fact]
    public async Task Bundles_move_between_the_python_service_and_this_port_in_both_directions()
    {
        using var state = new TempState();
        var script = Path.Combine(AppContext.BaseDirectory, "Parity", "statesync_bundle.py");
        var account = Fixtures.Binding.Account;

        // This port packs; Python's _install accepts it and installs the same database and preferences.
        var (dbA, syncA) = Session(state, "sandbox-a");
        await Turn(syncA, () =>
        {
            MakeDb(dbA, account);
            AddNote(dbA, "packed in C#");
            WriteFile(state.Combine("sandbox-a", "preferences.md"), "- **Replies to other people:** acknowledge\n");
        });
        var packed = state.Combine("packed.zip");
        File.WriteAllBytes(packed, _store.Get(StateSync.Bundle)!.Value.Data);
        var pythonDb = state.Combine("python", "state", "margo.sqlite3");
        var pythonPreferences = state.Combine("python", "preferences.md");
        var (status, stdout, stderr) = Programs.Run(Fixtures.Python(), ["-B", "-I", script, Fixtures.Root, "install", packed, pythonDb, pythonPreferences, account]);
        Assert.True(status == 0, stderr);
        var installed = (JsonObject)PyJson.Loads(stdout)!;
        Assert.True(installed.ContainsKey("manifest"), stdout);
        Assert.Equal(["packed in C#"], Notes(pythonDb));
        Assert.Equal("- **Replies to other people:** acknowledge\n", File.ReadAllText(pythonPreferences));
        Assert.Equal("2026-10-17T00:00:00+00:00", PyNode.AsString(installed["manifest"]!["taken_at"]));

        // Python packs (deflate, its own manifest); this port installs it.
        AddNote(pythonDb, "packed in Python");
        var pythonBundle = state.Combine("python.zip");
        (status, _, stderr) = Programs.Run(Fixtures.Python(), ["-B", "-I", script, Fixtures.Root, "pack", pythonDb, pythonPreferences, account, pythonBundle]);
        Assert.True(status == 0, stderr);
        _store.Set(StateSync.Bundle, File.ReadAllBytes(pythonBundle), "etag-python");
        var (dbB, syncB) = Session(state, "sandbox-b");
        var outcome = await Turn(syncB, () => { });
        Assert.Equal("2026-10-17T09:30:00+00:00", PyNode.AsString(outcome.Restored!["taken_at"]));
        Assert.Equal(["packed in C#", "packed in Python"], Notes(dbB));
        Assert.Equal("- **Replies to other people:** acknowledge\n", File.ReadAllText(state.Combine("sandbox-b", "preferences.md")));

        // A bundle of another account is refused by both, with the same reason.
        var (otherDb, otherSync) = Session(state, "other", account: "autopilot:agent-identity-1:principal-2", store: new InMemoryLeaseStore());
        (status, stdout, stderr) = Programs.Run(Fixtures.Python(), ["-B", "-I", script, Fixtures.Root, "install", packed, state.Combine("python-other", "margo.sqlite3"),
                                                                    state.Combine("python-other", "preferences.md"), "autopilot:agent-identity-1:principal-2"]);
        Assert.True(status == 0, stderr);
        Assert.Equal("""{"sync_error": "state bundle belongs to another account; refusing it"}""", stdout.Trim());
        Assert.Equal("state bundle belongs to another account; refusing it",
                     Assert.Throws<SyncException>(() => otherSync.Install(File.ReadAllBytes(packed))).Message);
        Assert.False(File.Exists(otherDb));
    }

    [Fact]
    public async Task A_bundle_without_preferences_removes_the_local_ones()
    {
        using var state = new TempState();
        var (dbA, syncA) = Session(state, "sandbox-a");
        var (dbB, syncB) = Session(state, "sandbox-b");
        WriteFile(state.Combine("sandbox-b", "preferences.md"), "stale");
        await Turn(syncA, () => MakeDb(dbA, Fixtures.Binding.Account));
        await Turn(syncB, () => Assert.True(File.Exists(dbB)));
        Assert.False(File.Exists(state.Combine("sandbox-b", "preferences.md")));
    }

    [Fact]
    public async Task A_missed_daily_copy_is_not_a_failure_and_the_lease_is_always_released()
    {
        using var state = new TempState();
        var store = new FlakyStore(_store, failRenew: false, failPut: true);
        var (dbA, syncA) = Session(state, "sandbox-a", store: store);
        await Turn(syncA, () => MakeDb(dbA, Fixtures.Binding.Account));
        Assert.Contains(StateSync.Bundle, _store.Names);
        Assert.Equal(1, store.Released);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Turn(syncA, () => throw new InvalidOperationException("boom")));
        Assert.Equal(2, store.Released);
    }

    private static byte[] ReadEntry(ZipArchive bundle, string name)
    {
        using var stream = bundle.GetEntry(name)!.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>Replaces the first occurrence of <paramref name="from"/> in the raw zip bytes (same length, so every header and
    /// checksum stays as it was).</summary>
    private static byte[] Corrupt(byte[] data, ReadOnlySpan<byte> from, ReadOnlySpan<byte> to)
    {
        var copy = data.ToArray();
        var at = copy.AsSpan().IndexOf(from);
        Assert.True(at >= 0, "the bytes to corrupt are not stored as they are");
        to.CopyTo(copy.AsSpan(at));
        return copy;
    }

    private static byte[] Rezip(byte[] data, string? drop = null, (string Name, byte[] Data)? replace = null,
                                CompressionLevel compression = CompressionLevel.Optimal)
    {
        using var source = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
        using var buffer = new MemoryStream();
        using (var target = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                if (entry.FullName == drop)
                {
                    continue;
                }

                var bytes = replace is { } swap && swap.Name == entry.FullName ? swap.Data : ReadEntry(source, entry.FullName);
                using var stream = target.CreateEntry(entry.FullName, compression).Open();
                stream.Write(bytes);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>Wraps a store: renewals fail (the Python test's <c>Flaky</c>), daily copies may fail, releases are counted.</summary>
    private sealed class FlakyStore(InMemoryLeaseStore inner, bool failRenew = true, bool failPut = false) : ILeaseStore
    {
        private int _released;

        public bool FailRenew => failRenew;

        public int Released => Volatile.Read(ref _released);

        public async Task<IStateLease> AcquireAsync(string name, CancellationToken cancellationToken = default) =>
            new Lease(this, await inner.AcquireAsync(name, cancellationToken));

        public Task PutAsync(string name, byte[] data, CancellationToken cancellationToken = default) =>
            failPut ? Task.FromException(new IOException("daily copy unavailable")) : inner.PutAsync(name, data, cancellationToken);

        private sealed class Lease(FlakyStore store, IStateLease lease) : IStateLease
        {
            public Task<(byte[]? Data, string? ETag)> ReadAsync(CancellationToken cancellationToken = default) => lease.ReadAsync(cancellationToken);

            public Task<string?> WriteAsync(byte[] data, CancellationToken cancellationToken = default) => lease.WriteAsync(data, cancellationToken);

            public Task RenewAsync(CancellationToken cancellationToken = default) =>
                store.FailRenew ? Task.FromException(new InvalidOperationException("transient renew failure")) : lease.RenewAsync(cancellationToken);

            public ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref store._released);
                return lease.DisposeAsync();
            }
        }
    }
}

using System.Globalization;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using Microsoft.Data.Sqlite;

namespace Margo.Cloud.Orchestrator;

/// <summary>
/// State could not be loaded or saved safely; the turn must not proceed or claim success (<c>statesync.py</c>
/// <c>SyncError</c>, a <c>RuntimeError</c>). The Host's Azure store throws it too.
/// </summary>
public sealed class SyncException : Exception
{
    public SyncException()
    {
    }

    public SyncException(string message)
        : base(message)
    {
    }

    public SyncException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// The lease was not taken, so nothing was read, run or changed: another writer holds the state (a turn in
    /// another container), or storage did not answer. Set by <see cref="StateSync.RunAsync{T}"/>; the state itself is
    /// not known to be unsafe.
    /// </summary>
    public bool LeaseNotAcquired { get; init; }

    /// <summary>
    /// The turn ran to its end and then its state could not be saved (the lease was lost, or the upload failed).
    /// Whatever the turn did outside the state, an approved action included, may have happened; only the record of
    /// it is gone. Set by <see cref="StateSync.RunAsync{T}"/>. No Python equivalent.
    /// </summary>
    public bool NotSaved { get; init; }
}

/// <summary>What <see cref="StateSync.RunAsync{T}"/> returns: <c>{"result", "restored"}</c>.</summary>
/// <param name="Result">The work's result.</param>
/// <param name="Restored">The manifest of the bundle installed before the work ran, or null when none was installed.</param>
public sealed record StateSyncOutcome<T>(T Result, JsonObject? Restored);

/// <summary>
/// One writer at a time: the instance's private state, shared through a leased Blob bundle (<c>statesync.py</c>
/// <c>StateSync</c>).
/// </summary>
/// <remarks>
/// <para>A container's disk is not durable: Container Apps restarts, scales an idle instance to zero and replaces
/// revisions, and a process serving several instances may run turns side by side. A SQLite file on local disk alone
/// would be lost or fork. Instead each turn:</para>
/// <list type="number">
/// <item>takes an exclusive lease on the instance's state blob (renewed while the turn runs);</item>
/// <item>installs the newest bundle if the local copy is stale or missing, or, when there is no shared bundle at all,
/// discards any local copy: it was never saved, or the shared copy was removed;</item>
/// <item>runs the turn against the local SQLite file, with no other session able to write;</item>
/// <item>uploads a verified snapshot under the same lease, then releases it.</item>
/// </list>
/// <para>The bundle is a zip of <c>margo.sqlite3</c> (an online-backup snapshot), the manager's private
/// <c>preferences.md</c> if present, and a manifest binding it to the account. A foreign, tampered or corrupt bundle is
/// refused and nothing local is replaced. No SQLite write transaction is held here across storage calls: the snapshot is
/// a read, and installing replaces the file.</para>
/// </remarks>
public sealed class StateSync
{
    /// <summary><c>BUNDLE</c>: the leased blob's name (after the prefix).</summary>
    public const string Bundle = "state.zip";

    /// <summary><c>MARKER</c>: the file beside the database holding the ETag the local copy came from.</summary>
    public const string Marker = ".synced-etag";

    /// <summary>The file beside the database holding the content hash of the shared bundle the local copy matches
    /// (<see cref="ContentHash"/>): a turn that leaves the same content uploads nothing.</summary>
    public const string ContentMarker = ".synced-content";

    /// <summary>The file beside the database holding the date (<c>yyyy-MM-dd</c>) of the last daily copy written from here.</summary>
    public const string DailyMarker = ".daily-copy";

    private static readonly string[] Sidecars = ["-journal", "-wal", "-shm"];

    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <param name="database">The local <c>margo.sqlite3</c> the turn uses.</param>
    /// <param name="store">The shared leased store.</param>
    /// <param name="account">The account the state must belong to.</param>
    /// <param name="preferences">The manager's private <c>preferences.md</c>, carried in the bundle; null for none.</param>
    /// <param name="renewInterval">How often the lease is renewed while the turn runs (Python: 20 seconds).</param>
    /// <param name="prefix">Keeps one account's bundle apart inside a shared per-instance container.</param>
    public StateSync(string database, ILeaseStore store, string account, string? preferences = null, TimeSpan? renewInterval = null,
                     string prefix = "")
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(prefix);
        Database = database;
        Store = store;
        Account = account;
        BundleName = prefix + Bundle;
        DailyPrefix = prefix + "daily/";
        Preferences = string.IsNullOrEmpty(preferences) ? null : preferences;
        RenewInterval = renewInterval ?? TimeSpan.FromSeconds(20);
        var directory = Path.GetDirectoryName(Path.GetFullPath(database))!;
        MarkerPath = Path.Combine(directory, Marker);
        ContentMarkerPath = Path.Combine(directory, ContentMarker);
        DailyMarkerPath = Path.Combine(directory, DailyMarker);
    }

    /// <summary>The local database.</summary>
    public string Database { get; }

    /// <summary>The shared store.</summary>
    public ILeaseStore Store { get; }

    /// <summary>The account the state belongs to.</summary>
    public string Account { get; }

    /// <summary>The leased blob's full name (<c>prefix + state.zip</c>).</summary>
    public string BundleName { get; }

    /// <summary>Where the daily copies go (<c>prefix + daily/</c>).</summary>
    public string DailyPrefix { get; }

    /// <summary>How many days of daily copies are kept; 0 keeps them all (Python kept them all).</summary>
    public int DailyRetentionDays { get; init; } = DefaultDailyRetentionDays;

    /// <summary>The default for <see cref="DailyRetentionDays"/>.</summary>
    public const int DefaultDailyRetentionDays = 30;

    /// <summary>The manager's preferences file, when carried.</summary>
    public string? Preferences { get; }

    /// <summary>The lease renewal interval.</summary>
    public TimeSpan RenewInterval { get; }

    /// <summary>The ETag marker file.</summary>
    public string MarkerPath { get; }

    /// <summary>The content hash marker file.</summary>
    public string ContentMarkerPath { get; }

    /// <summary>The daily copy marker file.</summary>
    public string DailyMarkerPath { get; }

    /// <summary>
    /// Called (under the lease, before the work runs) whenever the local copy was replaced by the shared bundle or
    /// discarded: whatever the caller remembers about the local files (schemas it initialised) no longer holds.
    /// </summary>
    public Action? LocalStateReplaced { get; init; }

    /// <summary><c>container_name(account)</c>: a per-instance, per-principal Blob container name (lowercase, 3-63 characters).</summary>
    public static string ContainerName(string account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return "margo-" + PyIds.Sha256Hex(account)[..40];
    }

    /// <summary>
    /// <c>run(work, now=None)</c>: runs <paramref name="work"/> while holding the instance lease and returns its result.
    /// </summary>
    /// <remarks>If the turn or the upload fails, the local copy is marked stale so the next turn reinstalls the shared
    /// state instead of building on changes that were never saved. The lease is released (or left to expire) either way.</remarks>
    /// <param name="work">The turn; it runs against the local database.</param>
    /// <param name="now">The time stamped on the manifest and naming the daily copy; null for the current UTC time.</param>
    /// <param name="cancellationToken">Cancels the turn; nothing is saved then.</param>
    /// <exception cref="SyncException">The state could not be loaded or saved safely.</exception>
    public async Task<StateSyncOutcome<T>> RunAsync<T>(Func<CancellationToken, Task<T>> work, DateTimeOffset? now = null,
                                                       CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var stamp = now ?? PyRuntime.UtcNow();
        IStateLease lease;
        try
        {
            lease = await Store.AcquireAsync(BundleName, cancellationToken).ConfigureAwait(false);
        }
        catch (SyncException exc)
        {
            throw new SyncException(exc.Message, exc) { LeaseNotAcquired = true };
        }

        using var renewal = new CancellationTokenSource();
        var renewer = RenewAsync(lease, renewal.Token);
        var saved = false;
        var ran = false;
        try
        {
            var known = LocalETag();
            var (data, etag, unchanged) = await lease.ReadIfChangedAsync(string.IsNullOrEmpty(known) ? null : known, cancellationToken)
                .ConfigureAwait(false);
            JsonObject? restored = null;
            if (unchanged)
            {
                // The store confirmed the local copy's ETag without sending the bundle again.
            }
            else if (data is null || data.Length == 0)
            {
                await Task.Run(DiscardLocal, cancellationToken).ConfigureAwait(false);
                LocalStateReplaced?.Invoke();
            }
            else if (!string.Equals(etag, known, StringComparison.Ordinal))
            {
                restored = await Task.Run(() => Install(data), cancellationToken).ConfigureAwait(false);
                SetLocalETag(etag);
                LocalStateReplaced?.Invoke();
            }

            var result = await work(cancellationToken).ConfigureAwait(false);
            ran = true;
            if (renewer.IsFaulted)
            {
                throw new SyncException("the state lease could not be renewed; nothing was saved");
            }

            if (PyPath.Exists(Database))
            {
                // A turn that changed nothing uploads nothing: the snapshot is compared with the content the shared
                // bundle is known to hold (installed from it, or uploaded from here, under this ETag).
                var (database, preferences) = await Task.Run(Snapshot, cancellationToken).ConfigureAwait(false);
                var content = ContentHash(database, preferences);
                byte[]? bundle = null;
                if (string.IsNullOrEmpty(LocalETag()) || !string.Equals(content, ReadMarker(ContentMarkerPath), StringComparison.Ordinal))
                {
                    bundle = await Task.Run(() => MakeBundle(database, preferences, stamp), cancellationToken).ConfigureAwait(false);
                    var newETag = await lease.WriteAsync(bundle, cancellationToken).ConfigureAwait(false);
                    SetLocalETag(newETag);
                    WriteMarker(ContentMarkerPath, content);
                }

                saved = true;
                var day = stamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (!string.Equals(day, ReadMarker(DailyMarkerPath), StringComparison.Ordinal))
                {
                    // One daily copy a day from here: the first turn of the day, changed or not.
                    try
                    {
                        bundle ??= await Task.Run(() => MakeBundle(database, preferences, stamp), cancellationToken).ConfigureAwait(false);
                        await Store.PutAsync(DailyPrefix + day + ".zip", bundle, cancellationToken).ConfigureAwait(false);
                        WriteMarker(DailyMarkerPath, day);
                        if (DailyRetentionDays > 0)
                        {
                            // Daily copies expire: what the manager had Margo forget must not stay in old copies for ever.
                            var firstKept = stamp.AddDays(-DailyRetentionDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                            await Store.DeleteBeforeAsync(DailyPrefix, DailyPrefix + firstKept + ".zip", cancellationToken).ConfigureAwait(false);
                        }
                    }
                    catch (Exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        // The leased copy is saved; a missed daily copy is not data loss (the next turn tries again).
                    }
                }
            }
            else
            {
                saved = true;
            }

            return new StateSyncOutcome<T>(result, restored);
        }
        catch (SyncException exc) when (ran && !saved)
        {
            throw new SyncException(exc.Message, exc) { NotSaved = true };
        }
        finally
        {
            try
            {
                await renewal.CancelAsync().ConfigureAwait(false);
                try
                {
                    await renewer.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Cancelled, or the renewal failure already reported above.
                }

                if (!saved)
                {
                    foreach (var path in new[] { MarkerPath, ContentMarkerPath })
                    {
                        if (PyPath.Exists(path))
                        {
                            File.Delete(path);
                        }
                    }
                }
            }
            finally
            {
                try
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // An unreleased lease expires on its own.
                }
            }
        }
    }

    // -- bundle ---------------------------------------------------------------------------

    /// <summary><c>_pack(now)</c>: a verified online-backup snapshot of the database, the preferences and the manifest, zipped.</summary>
    internal byte[] Pack(DateTimeOffset now)
    {
        var (database, preferences) = Snapshot();
        return MakeBundle(database, preferences, now);
    }

    /// <summary>
    /// What identifies a bundle's content whatever its time stamp: the SHA-256 of the database snapshot and of the
    /// preferences file (<c>-</c> for none).
    /// </summary>
    internal static string ContentHash(byte[] database, byte[]? preferences) =>
        PyIds.Sha256Hex(database) + ":" + (preferences is null ? "-" : PyIds.Sha256Hex(preferences));

    /// <summary>An online-backup snapshot of the database (not yet checked) and the preferences file's bytes.</summary>
    /// <remarks>An online backup of an unchanged database is the same bytes each time (it copies the pages), so its
    /// hash tells whether the turn changed anything; a turn that only read leaves it equal.</remarks>
    internal (byte[] Database, byte[]? Preferences) Snapshot()
    {
        byte[] database;
        var scratch = Directory.CreateTempSubdirectory("margo-sync-").FullName;
        try
        {
            var snapshot = Path.Combine(scratch, "snapshot.sqlite3");
            using (var source = OpenSqlite(Database, timeoutSeconds: 10))
            using (var target = OpenSqlite(snapshot))
            {
                source.BackupDatabase(target);
            }

            database = File.ReadAllBytes(snapshot);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }

        return (database, Preferences is not null && PyPath.IsFile(Preferences) ? File.ReadAllBytes(Preferences) : null);
    }

    /// <summary>The rest of <c>_pack(now)</c>: checks the snapshot (an intact store of this account) and zips it with
    /// the preferences and the manifest.</summary>
    internal byte[] MakeBundle(byte[] database, byte[]? preferences, DateTimeOffset now)
    {
        var scratch = Directory.CreateTempSubdirectory("margo-sync-").FullName;
        try
        {
            var snapshot = Path.Combine(scratch, "snapshot.sqlite3");
            File.WriteAllBytes(snapshot, database);
            CheckDb(snapshot, Account);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }

        var manifest = new JsonObject
        {
            ["account_sha256"] = PyIds.Sha256Hex(Account),
            ["database_sha256"] = PyIds.Sha256Hex(database),
            ["taken_at"] = now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
            ["format"] = 1,
        };
        using var buffer = new MemoryStream();
        using (var bundle = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(bundle, "manifest.json", PyIO.EncodeUtf8(PyJson.Dumps(manifest, sortKeys: true)));
            Add(bundle, "margo.sqlite3", database);
            if (preferences is not null)
            {
                Add(bundle, "preferences.md", preferences);
            }
        }

        return buffer.ToArray();
    }

    /// <summary><c>_install(data)</c>: checks the bundle against the account and its manifest, then replaces the local copy.</summary>
    /// <returns>The installed bundle's manifest.</returns>
    internal JsonObject Install(byte[] data)
    {
        JsonNode? manifestNode;
        byte[] database;
        byte[]? preferences;
        try
        {
            using var bundle = new ZipArchive(new MemoryStream(data, writable: false), ZipArchiveMode.Read);
            manifestNode = PyJson.LoadsBytes(Read(bundle, "manifest.json"));
            database = Read(bundle, "margo.sqlite3");
            preferences = bundle.Entries.Any(entry => entry.FullName == "preferences.md") ? Read(bundle, "preferences.md") : null;
        }
        catch (Exception exc) when (exc is InvalidDataException or KeyNotFoundException or PyValueError)
        {
            throw new SyncException("state bundle is unreadable", exc);
        }

        var manifest = PyNode.Dict(manifestNode);
        if (!PyNode.Is(PyNode.Get(manifest, "account_sha256"), PyIds.Sha256Hex(Account)))
        {
            throw new SyncException("state bundle belongs to another account; refusing it");
        }

        if (!PyNode.Is(PyNode.Get(manifest, "database_sha256"), PyIds.Sha256Hex(database)))
        {
            throw new SyncException("state bundle content does not match its manifest");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(Database))!;
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var staging = Path.Combine(directory, ".install-" + PyIds.TokenHex(8));
        try
        {
            using (var stream = new FileStream(staging, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            }))
            {
                stream.Write(database);
                stream.Flush(flushToDisk: true);
            }

            File.SetUnixFileMode(staging, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            CheckDb(staging, Account);
            foreach (var suffix in Sidecars)
            {
                if (PyPath.Exists(Database + suffix))
                {
                    File.Delete(Database + suffix);
                }
            }

            File.Move(staging, Database, overwrite: true);
        }
        finally
        {
            if (File.Exists(staging))
            {
                File.Delete(staging);
            }
        }

        if (Preferences is not null)
        {
            if (preferences is null)
            {
                if (PyPath.Exists(Preferences))
                {
                    File.Delete(Preferences);
                }
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(Preferences))!,
                                          UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                File.WriteAllBytes(Preferences, preferences);
                File.SetUnixFileMode(Preferences, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        // What the local copy now matches: the next turn uploads only if it leaves something else.
        WriteMarker(ContentMarkerPath, ContentHash(database, Preferences is null ? null : preferences));
        return manifest;
    }

    /// <summary><c>_discard_local()</c>: removes local state the shared store does not hold, so it is never built on or uploaded again.</summary>
    internal void DiscardLocal()
    {
        foreach (var path in new[] { Database }.Concat(Sidecars.Select(suffix => Database + suffix)).Append(Preferences).Append(MarkerPath)
                     .Append(ContentMarkerPath).Append(DailyMarkerPath))
        {
            if (path is not null && PyPath.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary><c>_check_db(path, account)</c>: an intact Margo store of this account, or <see cref="SyncException"/>.</summary>
    /// <remarks>A file SQLite cannot open as a database fails with <see cref="SqliteException"/>, as Python's
    /// <c>sqlite3.DatabaseError</c> escapes <c>_check_db</c>.</remarks>
    internal static void CheckDb(string path, string account)
    {
        using var connection = OpenSqlite(path);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA quick_check";
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.GetValue(0) is not "ok")
            {
                throw new SyncException("state snapshot failed its integrity check");
            }
        }

        var tables = new HashSet<string>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetValue(0) is string name)
                {
                    tables.Add(name);
                }
            }
        }

        if (!tables.Contains("margo_meta"))
        {
            throw new SyncException("state snapshot is not a Margo store");
        }

        object? owner = null;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT key,value FROM margo_meta";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetValue(0) is "account")
                {
                    owner = reader.GetValue(1);
                }
            }
        }

        if (owner is not string text || !string.Equals(text, account, StringComparison.Ordinal))
        {
            throw new SyncException("state belongs to another account; refusing it");
        }
    }

    private string? LocalETag()
    {
        try
        {
            return PyPath.Exists(Database) ? PyRepr.Strip(PyIO.ReadTextFile(MarkerPath)) : null;
        }
        catch (Exception exc) when (exc is PyOSError or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void SetLocalETag(string? etag)
    {
        File.WriteAllBytes(MarkerPath, PyIO.EncodeUtf8(etag ?? ""));
        File.SetUnixFileMode(MarkerPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static string? ReadMarker(string path)
    {
        try
        {
            return PyPath.Exists(path) ? PyRepr.Strip(PyIO.ReadTextFile(path)) : null;
        }
        catch (Exception exc) when (exc is PyOSError or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteMarker(string path, string text)
    {
        File.WriteAllBytes(path, PyIO.EncodeUtf8(text));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private async Task RenewAsync(IStateLease lease, CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(RenewInterval, cancellationToken).ConfigureAwait(false);
            await lease.RenewAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static SqliteConnection OpenSqlite(string path, int timeoutSeconds = 5)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = timeoutSeconds,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void Add(ZipArchive bundle, string name, byte[] data)
    {
        var entry = bundle.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(data);
    }

    /// <summary><c>ZipFile.read(name)</c>: the last entry of that name, checked against its CRC-32 as <c>zipfile</c> checks
    /// it (<c>BadZipFile("Bad CRC-32 for file ...")</c>); <see cref="ZipArchive"/> does not check it.</summary>
    private static byte[] Read(ZipArchive bundle, string name)
    {
        var entry = bundle.Entries.LastOrDefault(item => item.FullName == name) ?? throw new KeyNotFoundException("There is no item named '" + name + "' in the archive");
        byte[] data;
        using (var stream = entry.Open())
        using (var copy = new MemoryStream())
        {
            stream.CopyTo(copy);
            data = copy.ToArray();
        }

        if (Crc32(data) != entry.Crc32)
        {
            throw new InvalidDataException("Bad CRC-32 for file '" + name + "'");
        }

        return data;
    }

    /// <summary>The zip format's CRC-32 (IEEE 802.3, reflected, polynomial 0xEDB88320), as <c>zlib.crc32</c>.</summary>
    internal static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (var n = 0u; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}

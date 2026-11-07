using System.Diagnostics;
using Margo.Cloud.State.Core;
using Xunit;

namespace Margo.Cloud.State.Tests;

/// <summary>
/// Behaviour where the first port drifted from CPython 3.13: Unicode tables, pathlib root joins and
/// <c>~user</c> lookups, the identity table, sqlite3 transaction ends, OSError classes and write failures.
/// Expected values were measured with CPython 3.13.16 (see the comments).
/// </summary>
public sealed class PythonDriftTests
{
    private const string Forbidden = "repository/shared/synchronised state/config roots are forbidden";
    private const string WorldWritable = "state/config root has a group/world-writable ancestor";

    [Fact]
    public void Safe_directory_compares_names_with_python_casefold()
    {
        using var root = TempRoot.Create();
        using var strict = TempRoot.With(("MARGO_ALLOW_UNSAFE_STATE_DIR", "0"));
        // '\u017Fhared'.casefold() == 'shared'; 'cloud\uFB06orage'.casefold() == 'cloudstorage'; 'BOX \u017FYNC' -> 'box sync'.
        foreach (var name in new[] { "\u017Fhared", "cloud\uFB06orage", "BOX \u017FYNC", "My DROPBOX" })
        {
            Assert.Equal(Forbidden, Assert.Throws<StateError>(() => StateStore.SafeDirectory(root.Combine(name, "state"), true)).Message);
        }

        // 'ONEDR\u0130VE'.casefold() == 'onedri\u0307ve' (not 'onedrive'); 'Kbox' with KELVIN SIGN folds to 'kbox'.
        foreach (var name in new[] { "ONEDR\u0130VE", "\u212Abox" })
        {
            var error = Record.Exception(() => StateStore.SafeDirectory(root.Combine(name, "state"), true));
            Assert.True(error is null || error.Message == WorldWritable, name + ": " + error?.Message);
        }
    }

    [Fact]
    public void Casefold_matches_python()
    {
        Assert.Equal("shared", PyRepr.CaseFold("\u017Fhared"));
        Assert.Equal("strasse", PyRepr.CaseFold("Stra\u00DFe"));
        Assert.Equal("onedri\u0307ve", PyRepr.CaseFold("ONEDR\u0130VE"));
        Assert.Equal("\u03C3\u03B1\u03C3", PyRepr.CaseFold("\u03A3\u0391\u03A3"));
        Assert.Equal("\U00010428", PyRepr.CaseFold("\U00010400"));
        Assert.Equal("a\udcffb", PyRepr.CaseFold("A\udcffB"));
    }

    [Fact]
    public void Python_int_and_repr_use_unicode_15_1()
    {
        Assert.Equal(1, (int)PyArgumentParser.ParsePyInt("\u0661"));
        Assert.Equal(1, (int)PyArgumentParser.ParsePyInt("\U0001D7CF")); // MATHEMATICAL BOLD DIGIT ONE
        // U+1CCF1 OUTLINED DIGIT ONE is new in Unicode 16.0: int() rejects it and repr() escapes it.
        Assert.Equal("invalid literal for int() with base 10: '\\U0001ccf1'",
                     Assert.Throws<PyValueError>(() => PyArgumentParser.ParsePyInt("\U0001CCF1")).Message);
        Assert.Equal("'x\\U0001cc00'", PyRepr.Str("x\U0001CC00"));
        Assert.Equal("'\\u0897'", PyRepr.Str("\u0897"));
        Assert.Equal("'\U0002EBF0'", PyRepr.Str("\U0002EBF0")); // CJK Extension I, assigned in 15.1
        Assert.Equal(1.5, PyArgumentParser.ParsePyFloat("\u0661.\u0665"));
        Assert.Equal(100.0, PyArgumentParser.ParsePyFloat("\u0661e\u0662"));
        Assert.Throws<PyValueError>(() => PyArgumentParser.ParsePyFloat("\u0130nf"));
    }

    [Fact]
    public void Timestamp_pattern_uses_python_decimal_digits()
    {
        // re's \d matches the astral MATHEMATICAL BOLD DIGIT ONE, so the parser, not the pattern, rejects it.
        Assert.Equal("invalid timestamp",
                     Assert.Throws<StateError>(() => StateStore.ValidateTimestamp("2024-01-01T00:00:0\U0001D7CFZ")).Message);
        Assert.Equal("timestamp must be an ISO-8601 datetime with an explicit timezone",
                     Assert.Throws<StateError>(() => StateStore.ValidateTimestamp("2024-01-01T00:00:0\U0001CCF1Z")).Message);
        Assert.Equal("invalid timestamp",
                     Assert.Throws<StateError>(() => StateStore.ValidateTimestamp("2024-01-01T00:00:0\u0661Z")).Message);
        Assert.Equal("2024-01-01T00:00:00.000000+00:00", StateStore.ValidateTimestamp("2024-01-01T00:00:00Z"));
    }

    [Fact]
    public void Joining_onto_the_filesystem_root_keeps_one_slash()
    {
        Assert.Equal("/x", PyPath.Join("/", "x"));
        Assert.Equal("//x", PyPath.Join("//", "x"));
        Assert.Equal("/a", PyPath.Join("/a", ""));
        using var root = TempRoot.Create();
        var (_, database) = StateStore.StatePath("owner@example.com", "/");
        Assert.Equal("/" + PyIds.Sha256Hex("owner@example.com") + "/margo.sqlite3", database);
    }

    [Fact]
    public void Expanduser_reads_the_password_database()
    {
        var passwd = File.ReadAllLines("/etc/passwd").Select(line => line.Split(':')).Where(fields => fields.Length >= 7).ToList();
        using var root = TempRoot.Create();
        foreach (var fields in passwd.Where(fields => fields[0] is "root" or "daemon"))
        {
            // pwd.getpwnam(name).pw_dir, whatever $HOME says.
            Assert.Equal(PyPath.Normalize(fields[5].TrimEnd('/') + "/x"), PyPath.ExpandUser("~" + fields[0] + "/x"));
        }

        Assert.Throws<InvalidOperationException>(() => PyPath.ExpandUser("~no-such-user-margo/x"));
        using (TempRoot.With(("HOME", "~elsewhere")))
        {
            Assert.Equal("Could not determine home directory.",
                         Assert.Throws<InvalidOperationException>(() => PyPath.ExpandUser("~/x")).Message);
        }

        using (TempRoot.With(("HOME", null)))
        {
            var own = passwd.FirstOrDefault(fields => fields[2] == PosixNative.GetUid().ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (own is null)
            {
                Assert.Throws<InvalidOperationException>(() => PyPath.ExpandUser("~/x"));
            }
            else
            {
                Assert.Equal(PyPath.Normalize(own[5].TrimEnd('/') + "/x"), PyPath.ExpandUser("~/x"));
            }
        }
    }

    [Fact]
    public void Connect_accepts_identity_rows_with_null_or_blob_keys()
    {
        // dict(connection.execute("SELECT key,value FROM margo_meta")) keeps None and bytes keys; only
        // 'account' and 'store_version' are looked up.
        using var root = TempRoot.Create();
        using (var connection = StateStore.Connect("owner@example.com", root.State))
        {
            connection.Execute("INSERT INTO margo_meta VALUES (NULL, 'null key')");
            connection.Execute("INSERT INTO margo_meta VALUES (x'00', 'blob key')");
            connection.Commit();
        }

        using (var again = StateStore.Connect("owner@example.com", root.State))
        {
            Assert.Equal(4L, again.Execute("SELECT count(*) FROM margo_meta").FetchOne()![0]);
        }

        using var readOnly = StateStore.Connect("owner@example.com", root.State, readOnly: true);
        Assert.False(readOnly.InTransaction);
    }

    [Fact]
    public void Commit_rollback_and_executescript_end_explicit_transactions_in_autocommit_mode()
    {
        // sqlite3 3.13 (legacy control) commits/rolls back whenever SQLite is in a transaction, even with
        // isolation_level=None.
        using var connection = PyConnection.Connect(":memory:", isolationLevel: null);
        connection.Execute("CREATE TABLE t (x)");
        connection.Execute("BEGIN");
        connection.Execute("INSERT INTO t VALUES (1)");
        connection.Commit();
        Assert.False(connection.InTransaction);
        connection.Execute("BEGIN");
        connection.Execute("INSERT INTO t VALUES (2)");
        connection.Rollback();
        Assert.False(connection.InTransaction);
        connection.Execute("BEGIN");
        connection.Execute("INSERT INTO t VALUES (3)");
        connection.ExecuteScript("SELECT 1");
        Assert.False(connection.InTransaction);
        connection.With(() =>
        {
            connection.Execute("BEGIN");
            connection.Execute("INSERT INTO t VALUES (4)");
        });
        Assert.False(connection.InTransaction);
        Assert.Equal([1L, 3L, 4L], connection.Execute("SELECT x FROM t ORDER BY x").Select(row => (long)row[0]!).ToList());
    }

    [Fact]
    public void OSError_classes_follow_the_errno()
    {
        var expected = new Dictionary<int, string>
        {
            [1] = "PermissionError", [2] = "FileNotFoundError", [3] = "ProcessLookupError", [4] = "InterruptedError",
            [10] = "ChildProcessError", [11] = "BlockingIOError", [13] = "PermissionError", [17] = "FileExistsError",
            [20] = "NotADirectoryError", [21] = "IsADirectoryError", [27] = "OSError", [28] = "OSError",
            [32] = "BrokenPipeError", [40] = "OSError", [103] = "ConnectionAbortedError",
            [104] = "ConnectionResetError", [108] = "BrokenPipeError", [110] = "TimeoutError",
            [111] = "ConnectionRefusedError", [114] = "BlockingIOError", [115] = "BlockingIOError",
        };
        foreach (var (errno, name) in expected)
        {
            Assert.Equal(name, PyErrno.Error(errno).PythonType);
        }
    }

    [Fact]
    public void Paths_are_encoded_with_surrogateescape()
    {
        using var root = TempRoot.Create();
        var directory = root.Combine("names");
        StateStore.MkdirPrivate(directory);
        // A lone U+DCFF is the undecodable byte 0xFF (os.fsencode), not U+FFFD.
        var path = directory + "/\udcff.json";
        PosixNative.Close(PosixNative.Open(path, PosixNative.OCreat | PosixNative.OExcl | PosixNative.OWrOnly, Convert.ToUInt32("600", 8)));
        Assert.True(PyPath.Exists(path));
        Assert.False(File.Exists(Path.Combine(directory, "\uFFFD.json")));
        Assert.Equal([0xFF, (byte)'.', (byte)'j', (byte)'s', (byte)'o', (byte)'n'], PyIO.FsEncode("\udcff.json"));
        Assert.Equal("\udcff.json", PyIO.FsDecode([0xFF, (byte)'.', (byte)'j', (byte)'s', (byte)'o', (byte)'n']));
        // Other lone surrogates cannot be encoded: os functions raise UnicodeEncodeError, pathlib's
        // exists()/is_symlink() report False.
        var error = Assert.Throws<PyUnicodeError>(() => PosixNative.Stat(directory + "/x\ud800"));
        Assert.Equal("UnicodeEncodeError", error.PythonType);
        Assert.False(PyPath.Exists(directory + "/x\ud800"));
        Assert.Equal("embedded null byte", Assert.Throws<PyValueError>(() => PosixNative.Stat(directory + "/x\0y")).Message);
        Assert.False(PyPath.Exists(directory + "/x\0y"));
    }

    [Fact]
    public void Command_line_and_environment_bytes_are_decoded_with_surrogateescape()
    {
        byte[] raw = [.. "dotnet\0margo-state.dll\0margo_store.py\0"u8, (byte)'a', 0xFF, (byte)'@', 0, .. "ok\0"u8];
        Assert.Equal(["margo_store.py", "a\udcff@", "ok"],
                     PyRuntime.DecodeArguments(raw, ["margo_store.py", "a\uFFFD@", "ok"]));
        // The runtime may replace one invalid sequence (an encoded surrogate here) with fewer U+FFFD than
        // Encoding.UTF8; the valid text still has to correspond.
        byte[] encodedSurrogate = [.. "x\0"u8, 0xED, 0xA0, 0x80, (byte)'x', 0];
        Assert.Equal(["\udced\udca0\udc80x"], PyRuntime.DecodeArguments(encodedSurrogate, ["\uFFFD\uFFFDx"]));
        Assert.Equal(["\uFFFDy"], PyRuntime.DecodeArguments(encodedSurrogate, ["\uFFFDy"]));
        // The managed arguments win when they do not correspond to the process command line.
        Assert.Equal(["other"], PyRuntime.DecodeArguments(raw, ["other"]));
        byte[] environment = [.. "HOME=/h\0MARGO_ACCOUNT="u8, (byte)'b', 0xC3, 0];
        Assert.Equal("b\udcc3", PyRuntime.DecodeEnvironmentValue(environment, "MARGO_ACCOUNT", "b\uFFFD"));
        Assert.Equal("changed", PyRuntime.DecodeEnvironmentValue(environment, "MARGO_ACCOUNT", "changed"));
        Assert.Equal("/h", PyRuntime.DecodeEnvironmentValue(environment, "HOME", "/h"));
    }

    [Fact]
    public void Read_errors_are_os_errors()
    {
        // Reading /proc/self/mem from offset 0 fails with EIO: read_json turns the OSError into a StateError.
        if (!File.Exists("/proc/self/mem"))
        {
            Assert.Skip("needs /proc/self/mem");
        }

        Assert.Equal(5, Assert.Throws<PyOSError>(() => PyIO.ReadBytes("/proc/self/mem")).Errno);
        Assert.Equal("cannot read valid JSON from /proc/self/mem",
                     Assert.Throws<StateError>(() => StateStore.ReadJson("/proc/self/mem")).Message);
    }

    [Fact]
    public void Stderr_backslash_escapes_lone_surrogates_without_a_byte_order_mark()
    {
        // sys.stderr uses errors="backslashreplace": "unrecognized arguments: extra\udcff" stays readable.
        using var stream = new MemoryStream();
        using (var writer = PyIO.ErrorWriter(stream))
        {
            writer.Write("extra\udcff \U0001F600\n");
        }

        Assert.Equal([.. "extra\\udcff "u8, 0xF0, 0x9F, 0x98, 0x80, (byte)'\n'], stream.ToArray());
    }

    [Fact]
    public async Task Init_reports_write_failures_as_configuration_unavailable()
    {
        // python3 margo_store.py init under RLIMIT_FSIZE=1024 (SIGXFSZ is ignored by CPython): the staging
        // write fails with EFBIG -> "ERROR: configuration unavailable; no existing config replaced", exit 2,
        // and the staging file is removed.
        var shell = "/bin/sh";
        if (!File.Exists(shell))
        {
            Assert.Skip("needs /bin/sh to set a file size limit");
        }

        using var root = TempRoot.Create();
        var config = root.Combine("cfg", "config.json");
        var start = new ProcessStartInfo(shell)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "-c", "ulimit -f 1 && exec \"$@\"", "sh", Dotnet(), CliPath(), "margo_store.py", "init",
                                         "--account", new string('\u20AC', 500) + "@example.com", "--config", config })
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["MARGO_ALLOW_UNSAFE_STATE_DIR"] = "1";
        start.Environment["HOME"] = root.Combine("home");
        start.Environment["DOTNET_EnableWriteXorExecute"] = "0"; // the runtime's double mapping is a file too
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal("", await stdout);
        Assert.Equal("ERROR: configuration unavailable; no existing config replaced\n", await stderr);
        Assert.Equal(2, process.ExitCode);
        Assert.Empty(Directory.GetFileSystemEntries(root.Combine("cfg")));
    }

    internal static string CliPath()
    {
        // tests/Margo.Cloud.State.Tests/bin/<Configuration>/net10.0 -> src/Margo.Cloud.StateCli/bin/<Configuration>/net10.0
        var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var cloud = output.Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
        var cli = Path.Combine(cloud, "src", "Margo.Cloud.StateCli", "bin", output.Parent.Name, output.Name, "margo-state.dll");
        Assert.True(File.Exists(cli), "margo-state was not built at " + cli);
        return cli;
    }

    private static string Dotnet() =>
        Environment.ProcessPath is { } host && Path.GetFileNameWithoutExtension(host) == "dotnet" ? host : "dotnet";
}

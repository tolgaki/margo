using System.Numerics;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using Xunit;

namespace Margo.Cloud.State.Tests;

/// <summary>Python sqlite3 (3.13, legacy transaction control) semantics the state scripts rely on.</summary>
public sealed class PyConnectionTests
{
    [Fact]
    public void Dml_opens_an_implicit_transaction_and_ddl_does_not()
    {
        using var connection = PyConnection.Connect(":memory:");
        connection.Execute("CREATE TABLE t (id INTEGER PRIMARY KEY, v)");
        Assert.False(connection.InTransaction);
        var cursor = connection.Execute("  -- comment\n/* block */ INSERT INTO t(v) VALUES (?)", "a");
        Assert.True(connection.InTransaction);
        Assert.Equal(1, cursor.RowCount);
        Assert.Equal(1L, cursor.LastRowId);
        Assert.Equal(-1, connection.Execute("SELECT * FROM t").RowCount);
        connection.Rollback();
        Assert.Empty(connection.Execute("SELECT * FROM t").FetchAll());
        connection.Execute("WITH x AS (SELECT 1) INSERT INTO t(v) SELECT 'cte' FROM x");
        Assert.False(connection.InTransaction); // not recognised as DML, as in Python: autocommitted
        Assert.Single(connection.Execute("SELECT * FROM t").FetchAll());
    }

    [Fact]
    public void Executemany_executescript_and_with()
    {
        using var connection = PyConnection.Connect(":memory:");
        connection.ExecuteScript("CREATE TABLE t (k TEXT PRIMARY KEY, v); CREATE INDEX t_v ON t(v);  ");
        var cursor = connection.ExecuteMany("INSERT INTO t VALUES (?,?)", [["a", 1], ["b", 2.5], ["c", null]]);
        Assert.Equal(3, cursor.RowCount);
        Assert.Null(cursor.LastRowId);
        Assert.True(connection.InTransaction);
        connection.ExecuteScript("CREATE TABLE u (x)");
        Assert.False(connection.InTransaction); // executescript commits first
        Assert.Equal(PySqliteErrorKind.ProgrammingError,
                     Assert.Throws<PySqliteError>(() => connection.ExecuteMany("SELECT ?", [[1]])).Kind);
        connection.With(() => connection.Execute("DELETE FROM t WHERE k='a'"));
        Assert.False(connection.InTransaction);
        Assert.Throws<PyValueError>(() => connection.With(() =>
        {
            connection.Execute("DELETE FROM t");
            throw new PyValueError("abandon");
        }));
        Assert.Equal(2, connection.Execute("SELECT count(*) FROM t").FetchOne()![0] is long count ? count : -1);
    }

    [Fact]
    public void Statement_and_binding_errors_match_python()
    {
        using var connection = PyConnection.Connect(":memory:");
        connection.Execute("CREATE TABLE t (v)");
        var error = Assert.Throws<PySqliteError>(() => connection.Execute("SELECT 1; SELECT 2"));
        Assert.Equal(("ProgrammingError", "You can only execute one statement at a time."), (error.PythonType, error.Message));
        error = Assert.Throws<PySqliteError>(() => connection.Execute("INSERT INTO t VALUES (?)", 1, 2));
        Assert.Equal("Incorrect number of bindings supplied. The current statement uses 1, and there are 2 supplied.", error.Message);
        error = Assert.Throws<PySqliteError>(() => connection.Execute("INSERT INTO t VALUES (?)", new JsonObject()));
        Assert.Equal("Error binding parameter 1: type 'dict' is not supported", error.Message);
        Assert.Equal("Python int too large to convert to SQLite INTEGER",
                     Assert.Throws<PyOverflowError>(() => connection.Execute("INSERT INTO t VALUES (?)", BigInteger.Pow(2, 63))).Message);
        Assert.StartsWith("'utf-8' codec can't encode character '\\ud800'",
                          Assert.Throws<PyUnicodeError>(() => connection.Execute("INSERT INTO t VALUES (?)", "\ud800")).Message,
                          StringComparison.Ordinal);
        error = Assert.Throws<PySqliteError>(() => connection.Execute("SELECT nope FROM t"));
        Assert.Equal(("OperationalError", "no such column: nope"), (error.PythonType, error.Message));
        Assert.Throws<PySqliteError>(() => connection.Execute("SELECT 1 FROM t WHERE v = '\0'"));
    }

    [Fact]
    public void Values_map_to_python_types_and_rows_support_names()
    {
        using var connection = PyConnection.Connect(":memory:");
        var row = connection.Execute("SELECT 1 AS Total, 1.5 AS f, 'x' AS s, x'00ff' AS b, NULL AS n, ? AS flag, ? AS j",
                                     true, JsonValue.Create(7L)).FetchOne()!;
        Assert.Equal(1L, row["total"]);
        Assert.Equal(1.5, row["F"]);
        Assert.Equal("x", row[2]);
        Assert.Equal(new byte[] { 0, 255 }, row["b"]);
        Assert.Null(row["n"]);
        Assert.Equal(1L, row["flag"]);
        Assert.Equal(7L, row["j"]);
        Assert.Equal(["Total", "f", "s", "b", "n", "flag", "j"], row.Keys());
        Assert.Throws<PyIndexError>(() => row["missing"]);
        Assert.Equal("(1, 1.5, 'x', b'\\x00\\xff', None, 1, 7)", PyRepr.Object(row));
        var other = connection.Execute("SELECT 1.0, 1.5, 'x', x'00ff', NULL, 1, 7").FetchOne()!;
        Assert.True(row.ValuesEqual(other));
    }

    [Fact]
    public void Close_rolls_back_and_closed_connections_refuse_work()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("margo-sqlite-").FullName, "db.sqlite3");
        try
        {
            using (var connection = PyConnection.Connect(path))
            {
                connection.Execute("CREATE TABLE t (v)");
                connection.Execute("INSERT INTO t VALUES (1)");
            }

            using var reopened = PyConnection.Connect(path);
            Assert.Empty(reopened.Execute("SELECT * FROM t").FetchAll());
            reopened.Close();
            Assert.Equal("Cannot operate on a closed database.", Assert.Throws<PySqliteError>(() => reopened.Execute("SELECT 1")).Message);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}

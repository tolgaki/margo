using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using SQLitePCL;

namespace Margo.Cloud.State.Core;

/// <summary>The <c>sqlite3</c> exception classes.</summary>
public enum PySqliteErrorKind
{
    Error,
    InterfaceError,
    DatabaseError,
    DataError,
    OperationalError,
    IntegrityError,
    InternalError,
    ProgrammingError,
    NotSupportedError,
}

/// <summary>
/// <c>sqlite3.Error</c> and subclasses. <see cref="Exception.Message"/> is Python's <c>str(exc)</c>, which for
/// engine errors is <c>sqlite3_errmsg()</c> (for example <c>UNIQUE constraint failed: t.c</c>).
/// </summary>
public sealed class PySqliteError : PyException
{
    public PySqliteError(PySqliteErrorKind kind, string message, int? errorCode = null) : base(message)
    {
        Kind = kind;
        SqliteErrorCode = errorCode;
    }

    public PySqliteErrorKind Kind { get; }

    /// <summary><c>exc.sqlite_errorcode</c> (extended code) for engine errors.</summary>
    public int? SqliteErrorCode { get; }

    public override string PythonType => Kind.ToString();

    /// <summary><c>isinstance(exc, sqlite3.DatabaseError)</c>.</summary>
    public bool IsDatabaseError => Kind is not (PySqliteErrorKind.Error or PySqliteErrorKind.InterfaceError);
}

/// <summary>
/// <c>sqlite3.Row</c>: values by index or by column name (ASCII case-insensitive, like Python). Values are
/// <see cref="long"/> (INTEGER), <see cref="double"/> (REAL), <see cref="string"/> (TEXT), <see cref="byte"/>[]
/// (BLOB) or null.
/// </summary>
public sealed class PyRow : IReadOnlyList<object?>
{
    private readonly string[] _columns;
    private readonly object?[] _values;

    internal PyRow(string[] columns, object?[] values)
    {
        _columns = columns;
        _values = values;
    }

    public int Count => _values.Length;

    public object? this[int index] => _values[index];

    /// <summary><c>row["name"]</c>; a missing name raises <c>IndexError("No item with that key")</c>.</summary>
    public object? this[string name]
    {
        get
        {
            for (var i = 0; i < _columns.Length; i++)
            {
                if (string.Equals(_columns[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return _values[i];
                }
            }

            throw new PyIndexError("No item with that key");
        }
    }

    /// <summary><c>row.keys()</c>.</summary>
    public IReadOnlyList<string> Keys() => _columns;

    /// <summary><c>dict(row)</c> (column order kept; a repeated column name keeps the last value).</summary>
    public OrderedDictionary<string, object?> ToDict()
    {
        var result = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < _columns.Length; i++)
        {
            result[_columns[i]] = _values[i];
        }

        return result;
    }

    /// <summary><c>tuple(row) == tuple(other)</c> with Python value equality (1 == 1.0).</summary>
    public bool ValuesEqual(PyRow other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other.Count != Count)
        {
            return false;
        }

        for (var i = 0; i < Count; i++)
        {
            if (!PyConnection.ValueEquals(_values[i], other._values[i]))
            {
                return false;
            }
        }

        return true;
    }

    public string? GetString(int index) => (string?)_values[index];

    public string? GetString(string name) => (string?)this[name];

    public long? GetInt64(int index) => _values[index] is null ? null : Convert.ToInt64(_values[index], CultureInfo.InvariantCulture);

    public long? GetInt64(string name) => this[name] is { } value ? Convert.ToInt64(value, CultureInfo.InvariantCulture) : null;

    public IEnumerator<object?> GetEnumerator() => ((IEnumerable<object?>)_values).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// The result of <c>execute()</c>: rows are read eagerly (Python reads lazily; the difference is not
/// observable for the helpers' statements), consumed by <see cref="FetchOne"/>/<see cref="FetchAll"/> like a cursor.
/// </summary>
public sealed class PyCursor : IEnumerable<PyRow>
{
    private readonly List<PyRow> _rows;
    private int _position;

    internal PyCursor(List<PyRow> rows, IReadOnlyList<string>? description, long rowCount, long? lastRowId)
    {
        _rows = rows;
        Description = description;
        RowCount = rowCount;
        LastRowId = lastRowId;
    }

    /// <summary>Column names (<c>cursor.description</c>), or null for statements without result columns.</summary>
    public IReadOnlyList<string>? Description { get; }

    /// <summary><c>cursor.rowcount</c>: changed rows for INSERT/UPDATE/DELETE/REPLACE, otherwise -1.</summary>
    public long RowCount { get; }

    /// <summary><c>cursor.lastrowid</c> (null after <c>executemany</c>).</summary>
    public long? LastRowId { get; }

    /// <summary><c>fetchone()</c>.</summary>
    public PyRow? FetchOne() => _position < _rows.Count ? _rows[_position++] : null;

    /// <summary><c>fetchall()</c>: the remaining rows.</summary>
    public List<PyRow> FetchAll()
    {
        var rest = _rows.GetRange(_position, _rows.Count - _position);
        _position = _rows.Count;
        return rest;
    }

    public IEnumerator<PyRow> GetEnumerator()
    {
        while (_position < _rows.Count)
        {
            yield return _rows[_position++];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// A port of Python's <c>sqlite3.Connection</c> (3.13, legacy transaction control) over the SQLite engine that
/// Microsoft.Data.Sqlite ships (SQLitePCLRaw): implicit <c>BEGIN &lt;isolation_level&gt;</c> before
/// INSERT/UPDATE/DELETE/REPLACE, DDL in autocommit, one statement per <c>execute()</c>, <c>executescript()</c>
/// committing first, <c>with connection:</c> commit/rollback, Python's type mapping and error classes.
/// </summary>
public sealed class PyConnection : IDisposable
{
    private static readonly object InitLock = new();
    private static bool _initialized;
    private sqlite3? _db;

    private PyConnection(sqlite3 db, string? isolationLevel)
    {
        _db = db;
        IsolationLevel = isolationLevel;
    }

    /// <summary><c>isolation_level</c>: null for autocommit, otherwise "", "DEFERRED", "IMMEDIATE" or "EXCLUSIVE".</summary>
    public string? IsolationLevel { get; }

    /// <summary>Whether <c>row_factory = sqlite3.Row</c> was set (informational; rows always support names).</summary>
    public bool RowFactoryIsRow { get; set; }

    /// <summary><c>connection.in_transaction</c>.</summary>
    public bool InTransaction => raw.sqlite3_get_autocommit(Db) == 0;

    /// <summary><c>connection.total_changes</c>.</summary>
    public long TotalChanges => raw.sqlite3_total_changes(Db);

    public bool IsClosed => _db is null;

    private sqlite3 Db => _db ?? throw new PySqliteError(PySqliteErrorKind.ProgrammingError, "Cannot operate on a closed database.");

    /// <summary>
    /// <c>sqlite3.connect(database, timeout=..., isolation_level=...)</c>; <paramref name="readOnly"/> is the
    /// <c>file:...?mode=ro</c> URI form. <c>:memory:</c> opens a private in-memory database.
    /// </summary>
    public static PyConnection Connect(string database, bool readOnly = false, double timeout = 5.0, string? isolationLevel = "")
    {
        ArgumentNullException.ThrowIfNull(database);
        EnsureInitialized();
        var flags = readOnly ? raw.SQLITE_OPEN_READONLY : raw.SQLITE_OPEN_READWRITE | raw.SQLITE_OPEN_CREATE;
        var rc = raw.sqlite3_open_v2(database, out var db, flags, null);
        if (rc != raw.SQLITE_OK)
        {
            var message = db is null || db.IsInvalid ? raw.sqlite3_errstr(rc).utf8_to_string() : raw.sqlite3_errmsg(db).utf8_to_string();
            db?.Dispose();
            throw new PySqliteError(KindFor(rc), message, rc);
        }

        raw.sqlite3_busy_timeout(db, (int)(timeout * 1000));
        return new PyConnection(db, isolationLevel);
    }

    /// <summary><c>connection.execute(sql, parameters)</c>.</summary>
    public PyCursor Execute(string sql, params object?[] parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return Run(sql, [parameters], multiple: false);
    }

    /// <summary><c>connection.executemany(sql, rows)</c>.</summary>
    public PyCursor ExecuteMany(string sql, IEnumerable<IReadOnlyList<object?>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return Run(sql, rows, multiple: true);
    }

    /// <summary><c>connection.executescript(sql)</c>: commits a pending transaction, then runs every statement.</summary>
    public void ExecuteScript(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var db = Db;
        if (sql.Contains('\0', StringComparison.Ordinal))
        {
            throw new PySqliteError(PySqliteErrorKind.ProgrammingError, "embedded null character");
        }

        if (raw.sqlite3_get_autocommit(db) == 0) // legacy control ends any open transaction, whatever isolation_level is
        {
            Check(raw.sqlite3_exec(db, "COMMIT"));
        }

        var script = sql;
        while (true)
        {
            var rc = raw.sqlite3_prepare_v2(db, script, out var stmt, out var tail);
            if (rc == raw.SQLITE_OK && stmt is not null && !stmt.IsInvalid)
            {
                do
                {
                    rc = raw.sqlite3_step(stmt);
                }
                while (rc == raw.SQLITE_ROW);

                rc = raw.sqlite3_finalize(stmt);
            }
            else if (rc == raw.SQLITE_OK)
            {
                stmt?.Dispose();
            }

            if (rc != raw.SQLITE_OK)
            {
                throw ErrorFromDb();
            }

            if (string.IsNullOrEmpty(tail))
            {
                break;
            }

            script = tail;
        }
    }

    /// <summary><c>connection.commit()</c>.</summary>
    public void Commit()
    {
        var db = Db;
        if (raw.sqlite3_get_autocommit(db) == 0) // legacy control ends any open transaction, whatever isolation_level is
        {
            Check(raw.sqlite3_exec(db, "COMMIT"));
        }
    }

    /// <summary><c>connection.rollback()</c>.</summary>
    public void Rollback()
    {
        var db = Db;
        if (raw.sqlite3_get_autocommit(db) == 0) // legacy control ends any open transaction, whatever isolation_level is
        {
            Check(raw.sqlite3_exec(db, "ROLLBACK"));
        }
    }

    /// <summary><c>with connection: body</c>: commit on success, roll back on any exception.</summary>
    public void With(Action body)
    {
        ArgumentNullException.ThrowIfNull(body);
        With(() =>
        {
            body();
            return 0;
        });
    }

    /// <summary><c>with connection: return body()</c>.</summary>
    public T With<T>(Func<T> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        T result;
        try
        {
            result = body();
        }
        catch
        {
            Rollback();
            throw;
        }

        try
        {
            Commit();
        }
        catch
        {
            Rollback();
            throw;
        }

        return result;
    }

    /// <summary><c>connection.close()</c>: an open transaction is rolled back by SQLite.</summary>
    public void Close()
    {
        if (_db is { } db)
        {
            _db = null;
            raw.sqlite3_close_v2(db);
            db.Dispose();
        }
    }

    public void Dispose() => Close();

    /// <summary>Python <c>==</c> between two values read from SQLite.</summary>
    public static bool ValueEquals(object? a, object? b) => (a, b) switch
    {
        (null, null) => true,
        (null, _) or (_, null) => false,
        (string x, string y) => string.Equals(x, y, StringComparison.Ordinal),
        (byte[] x, byte[] y) => x.AsSpan().SequenceEqual(y),
        (long x, long y) => x == y,
        (double x, double y) => x == y,
        (long x, double y) => x == y,
        (double x, long y) => x == y,
        _ => Equals(a, b),
    };

    private PyCursor Run(string sql, IEnumerable<IReadOnlyList<object?>> parameterSets, bool multiple)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var db = Db;
        if (sql.Contains('\0', StringComparison.Ordinal))
        {
            throw new PySqliteError(PySqliteErrorKind.ProgrammingError, "the query contains a null character");
        }

        var rc = raw.sqlite3_prepare_v2(db, sql, out var stmt, out var tail);
        if (rc != raw.SQLITE_OK)
        {
            stmt?.Dispose();
            throw ErrorFromDb();
        }

        try
        {
            if (LStrip(tail) is not null)
            {
                throw new PySqliteError(PySqliteErrorKind.ProgrammingError, "You can only execute one statement at a time.");
            }

            var head = LStrip(sql);
            var isDml = head is not null && (StartsWithWord(head, "insert") || StartsWithWord(head, "update")
                                             || StartsWithWord(head, "delete") || StartsWithWord(head, "replace"));
            var empty = stmt is null || stmt.IsInvalid;
            if (multiple && !empty && raw.sqlite3_stmt_readonly(stmt) != 0)
            {
                throw new PySqliteError(PySqliteErrorKind.ProgrammingError, "executemany() can only execute DML statements.");
            }

            long rowCount = isDml ? 0 : -1;
            if (IsolationLevel is not null && isDml && raw.sqlite3_get_autocommit(db) != 0)
            {
                var begin = raw.sqlite3_exec(db, "BEGIN " + IsolationLevel);
                if (begin != raw.SQLITE_OK)
                {
                    throw ErrorFromDb();
                }
            }

            var rows = new List<PyRow>();
            string[]? columns = null;
            foreach (var parameters in parameterSets)
            {
                if (empty)
                {
                    continue;
                }

                raw.sqlite3_reset(stmt);
                raw.sqlite3_clear_bindings(stmt);
                Bind(stmt!, parameters);
                while (true)
                {
                    rc = raw.sqlite3_step(stmt);
                    if (rc != raw.SQLITE_ROW && rc != raw.SQLITE_DONE)
                    {
                        throw ErrorFromDb();
                    }

                    var count = raw.sqlite3_column_count(stmt);
                    if (columns is null && count > 0)
                    {
                        columns = new string[count];
                        for (var i = 0; i < count; i++)
                        {
                            columns[i] = raw.sqlite3_column_name(stmt, i).utf8_to_string();
                        }
                    }

                    if (rc == raw.SQLITE_DONE)
                    {
                        if (isDml)
                        {
                            rowCount += raw.sqlite3_changes(db);
                        }

                        break;
                    }

                    rows.Add(ReadRow(stmt!, columns!));
                }
            }

            long? lastRowId = multiple ? null : raw.sqlite3_last_insert_rowid(db);
            return new PyCursor(rows, columns, rowCount, lastRowId);
        }
        finally
        {
            if (stmt is not null && !stmt.IsInvalid)
            {
                raw.sqlite3_finalize(stmt);
            }

            stmt?.Dispose();
        }
    }

    private static PyRow ReadRow(sqlite3_stmt stmt, string[] columns)
    {
        var count = raw.sqlite3_data_count(stmt);
        var values = new object?[count];
        for (var i = 0; i < count; i++)
        {
            switch (raw.sqlite3_column_type(stmt, i))
            {
                case raw.SQLITE_NULL:
                    values[i] = null;
                    break;
                case raw.SQLITE_INTEGER:
                    values[i] = raw.sqlite3_column_int64(stmt, i);
                    break;
                case raw.SQLITE_FLOAT:
                    values[i] = raw.sqlite3_column_double(stmt, i);
                    break;
                case raw.SQLITE_TEXT:
                    var bytes = raw.sqlite3_column_blob(stmt, i);
                    try
                    {
                        values[i] = PyIO.DecodeUtf8(bytes);
                    }
                    catch (PyUnicodeError)
                    {
                        throw new PySqliteError(PySqliteErrorKind.OperationalError,
                            "Could not decode to UTF-8 column '" + columns[i] + "' with text '"
                            + System.Text.Encoding.UTF8.GetString(bytes) + "'");
                    }

                    break;
                default:
                    values[i] = raw.sqlite3_column_blob(stmt, i).ToArray();
                    break;
            }
        }

        return new PyRow(columns, values);
    }

    private void Bind(sqlite3_stmt stmt, IReadOnlyList<object?> parameters)
    {
        var needed = raw.sqlite3_bind_parameter_count(stmt);
        if (parameters.Count != needed)
        {
            throw new PySqliteError(PySqliteErrorKind.ProgrammingError, string.Format(CultureInfo.InvariantCulture,
                "Incorrect number of bindings supplied. The current statement uses {0}, and there are {1} supplied.",
                needed, parameters.Count));
        }

        for (var i = 0; i < parameters.Count; i++)
        {
            var position = i + 1;
            var value = parameters[i];
            if (value is JsonValue json)
            {
                value = PyJson.Scalar(json) switch
                {
                    BigInteger integer => integer,
                    var other => other,
                };
            }

            int rc;
            switch (value)
            {
                case null:
                    rc = raw.sqlite3_bind_null(stmt, position);
                    break;
                case bool flag:
                    rc = raw.sqlite3_bind_int64(stmt, position, flag ? 1 : 0);
                    break;
                case int or long or short or sbyte or byte or ushort or uint:
                    rc = raw.sqlite3_bind_int64(stmt, position, Convert.ToInt64(value, CultureInfo.InvariantCulture));
                    break;
                case ulong unsigned:
                    rc = unsigned > long.MaxValue
                        ? throw new PyOverflowError("Python int too large to convert to SQLite INTEGER")
                        : raw.sqlite3_bind_int64(stmt, position, (long)unsigned);
                    break;
                case BigInteger big:
                    rc = big < long.MinValue || big > long.MaxValue
                        ? throw new PyOverflowError("Python int too large to convert to SQLite INTEGER")
                        : raw.sqlite3_bind_int64(stmt, position, (long)big);
                    break;
                case double or float:
                    rc = raw.sqlite3_bind_double(stmt, position, Convert.ToDouble(value, CultureInfo.InvariantCulture));
                    break;
                case string text:
                    rc = raw.sqlite3_bind_text(stmt, position, PyIO.EncodeUtf8(text).AsSpan());
                    break;
                case byte[] blob:
                    rc = raw.sqlite3_bind_blob(stmt, position, blob.AsSpan());
                    break;
                default:
                    var typeName = value switch
                    {
                        JsonObject => "dict",
                        JsonArray => "list",
                        _ => value.GetType().Name,
                    };
                    throw new PySqliteError(PySqliteErrorKind.ProgrammingError, string.Format(CultureInfo.InvariantCulture,
                        "Error binding parameter {0}: type '{1}' is not supported", position, typeName));
            }

            if (rc != raw.SQLITE_OK)
            {
                throw ErrorFromDb();
            }
        }
    }

    private void Check(int rc)
    {
        if (rc != raw.SQLITE_OK)
        {
            throw ErrorFromDb();
        }
    }

    private PySqliteError ErrorFromDb()
    {
        var db = Db;
        var code = raw.sqlite3_errcode(db) & 0xff;
        var extended = raw.sqlite3_extended_errcode(db);
        return new PySqliteError(KindFor(code), raw.sqlite3_errmsg(db).utf8_to_string(), extended);
    }

    private static PySqliteErrorKind KindFor(int code) => (code & 0xff) switch
    {
        raw.SQLITE_INTERNAL or raw.SQLITE_NOTFOUND => PySqliteErrorKind.InternalError,
        raw.SQLITE_ERROR or raw.SQLITE_PERM or raw.SQLITE_ABORT or raw.SQLITE_BUSY or raw.SQLITE_LOCKED
            or raw.SQLITE_READONLY or raw.SQLITE_INTERRUPT or raw.SQLITE_IOERR or raw.SQLITE_FULL
            or raw.SQLITE_CANTOPEN or raw.SQLITE_PROTOCOL or raw.SQLITE_EMPTY or raw.SQLITE_SCHEMA => PySqliteErrorKind.OperationalError,
        raw.SQLITE_CORRUPT => PySqliteErrorKind.DatabaseError,
        raw.SQLITE_TOOBIG => PySqliteErrorKind.DataError,
        raw.SQLITE_CONSTRAINT or raw.SQLITE_MISMATCH => PySqliteErrorKind.IntegrityError,
        raw.SQLITE_MISUSE or raw.SQLITE_RANGE => PySqliteErrorKind.InterfaceError,
        _ => PySqliteErrorKind.DatabaseError,
    };

    /// <summary>CPython's <c>lstrip_sql</c>: skips whitespace and comments; null when nothing else remains.</summary>
    internal static string? LStrip(string? sql)
    {
        if (sql is null)
        {
            return null;
        }

        for (var pos = 0; pos < sql.Length; pos++)
        {
            switch (sql[pos])
            {
                case ' ' or '\t' or '\f' or '\n' or '\r':
                    break;
                case '-' when pos + 1 < sql.Length && sql[pos + 1] == '-':
                    pos += 2;
                    while (pos < sql.Length && sql[pos] != '\n')
                    {
                        pos++;
                    }

                    if (pos >= sql.Length)
                    {
                        return null;
                    }

                    break;
                case '/' when pos + 1 < sql.Length && sql[pos + 1] == '*':
                    pos += 2;
                    while (pos < sql.Length && !(sql[pos] == '*' && pos + 1 < sql.Length && sql[pos + 1] == '/'))
                    {
                        pos++;
                    }

                    if (pos >= sql.Length)
                    {
                        return null;
                    }

                    pos++;
                    break;
                default:
                    return sql[pos..];
            }
        }

        return null;
    }

    private static bool StartsWithWord(string text, string word) => text.StartsWith(word, StringComparison.OrdinalIgnoreCase);

    private static void EnsureInitialized()
    {
        lock (InitLock)
        {
            if (!_initialized)
            {
                Batteries_V2.Init();
                _initialized = true;
            }
        }
    }
}

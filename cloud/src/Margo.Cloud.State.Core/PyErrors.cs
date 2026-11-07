namespace Margo.Cloud.State.Core;

/// <summary>
/// Base of the Python exception taxonomy the state scripts rely on. The Python helpers catch
/// families such as <c>except (StateError, OSError, sqlite3.Error, ValueError, TypeError, KeyError)</c>
/// and print <c>str(exc)</c>; ports catch the matching C# types and print <see cref="Exception.Message"/>,
/// which is always the Python <c>str(exc)</c> text.
/// </summary>
public abstract class PyException : Exception
{
    protected PyException(string message, Exception? inner = null) : base(message, inner)
    {
    }

    /// <summary>The Python class name, for example <c>ValueError</c> or <c>StateError</c>.</summary>
    public abstract string PythonType { get; }
}

/// <summary>Python <c>ValueError</c> (and subclasses such as <c>JSONDecodeError</c>, <c>UnicodeError</c>).</summary>
public class PyValueError : PyException
{
    public PyValueError(string message, Exception? inner = null) : base(message, inner)
    {
    }

    public override string PythonType => "ValueError";
}

/// <summary>Python <c>TypeError</c>.</summary>
public class PyTypeError : PyException
{
    public PyTypeError(string message, Exception? inner = null) : base(message, inner)
    {
    }

    public override string PythonType => "TypeError";
}

/// <summary>
/// Python <c>KeyError</c>. <c>str(KeyError(key))</c> is the <em>repr</em> of the key, so the message is
/// built with <see cref="PyRepr.Str(string)"/>.
/// </summary>
public class PyKeyError : PyException
{
    public PyKeyError(string key) : base(PyRepr.Str(key))
    {
        Key = key;
    }

    public string Key { get; }

    public override string PythonType => "KeyError";
}

/// <summary>Python <c>OverflowError</c>.</summary>
public class PyOverflowError : PyException
{
    public PyOverflowError(string message) : base(message)
    {
    }

    public override string PythonType => "OverflowError";
}

/// <summary>
/// Python <c>RecursionError</c> (a <c>RuntimeError</c>). The scripts never catch it, so a port lets it
/// escape and the CLI reports an uncaught exception with exit status 1, as Python does.
/// </summary>
public class PyRecursionError : PyException
{
    public PyRecursionError(string message) : base(message)
    {
    }

    public override string PythonType => "RecursionError";
}

/// <summary>
/// Python <c>OSError</c> and its errno subclasses. <see cref="Exception.Message"/> follows
/// <c>str(OSError)</c>: <c>[Errno 2] No such file or directory: 'path'</c>.
/// </summary>
public class PyOSError : PyException
{
    public PyOSError(int errno, string strerror, string? filename = null, Exception? inner = null)
        : base(Format(errno, strerror, filename), inner)
    {
        Errno = errno;
        StrError = strerror;
        Filename = filename;
    }

    /// <summary>An OSError without an errno, for example <c>OSError("simulated")</c>.</summary>
    public PyOSError(string message, Exception? inner = null) : base(message, inner)
    {
        StrError = message;
    }

    public int Errno { get; }

    public string StrError { get; }

    public string? Filename { get; }

    /// <summary>The <c>OSError</c> subclass CPython picks for the errno (Objects/exceptions.c, Linux values).</summary>
    public override string PythonType => Errno switch
    {
        PyErrno.ENOENT => "FileNotFoundError",
        PyErrno.EEXIST => "FileExistsError",
        PyErrno.EACCES or PyErrno.EPERM => "PermissionError",
        PyErrno.EISDIR => "IsADirectoryError",
        PyErrno.ENOTDIR => "NotADirectoryError",
        4 => "InterruptedError", // EINTR
        3 => "ProcessLookupError", // ESRCH
        10 => "ChildProcessError", // ECHILD
        11 or 114 or 115 => "BlockingIOError", // EAGAIN/EWOULDBLOCK, EALREADY, EINPROGRESS
        32 or 108 => "BrokenPipeError", // EPIPE, ESHUTDOWN
        103 => "ConnectionAbortedError",
        104 => "ConnectionResetError",
        111 => "ConnectionRefusedError",
        110 => "TimeoutError", // ETIMEDOUT
        _ => "OSError",
    };

    private static string Format(int errno, string strerror, string? filename) =>
        filename is null ? $"[Errno {errno}] {strerror}" : $"[Errno {errno}] {strerror}: {PyRepr.Str(filename)}";
}

/// <summary>Linux errno values used by the ports, with the glibc <c>strerror</c> texts.</summary>
public static class PyErrno
{
    public const int EPERM = 1;
    public const int ENOENT = 2;
    public const int EIO = 5;
    public const int EACCES = 13;
    public const int EEXIST = 17;
    public const int ENOTDIR = 20;
    public const int EISDIR = 21;
    public const int EINVAL = 22;
    public const int ENOSPC = 28;
    public const int ELOOP = 40;
    public const int ENAMETOOLONG = 36;

    public static string StrError(int errno) => errno switch
    {
        EPERM => "Operation not permitted",
        ENOENT => "No such file or directory",
        EIO => "Input/output error",
        EACCES => "Permission denied",
        EEXIST => "File exists",
        ENOTDIR => "Not a directory",
        EISDIR => "Is a directory",
        EINVAL => "Invalid argument",
        ENOSPC => "No space left on device",
        ENAMETOOLONG => "File name too long",
        ELOOP => "Too many levels of symbolic links",
        _ => "Unknown error " + errno.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    public static PyOSError Error(int errno, string? filename = null, Exception? inner = null) =>
        new(errno, StrError(errno), filename, inner);
}

/// <summary>
/// Python <c>UnicodeDecodeError</c>/<c>UnicodeEncodeError</c> (both <c>ValueError</c> subclasses).
/// </summary>
public sealed class PyUnicodeError : PyValueError
{
    public PyUnicodeError(string pythonType, string message) : base(message)
    {
        UnicodeType = pythonType;
    }

    public string UnicodeType { get; }

    public override string PythonType => UnicodeType;
}

/// <summary>
/// <c>SystemExit</c>: argparse errors (status 2) and <c>--help</c> (status 0). The script host turns it
/// into the process exit status; output has already been written.
/// </summary>
public sealed class PySystemExit : PyException
{
    public PySystemExit(int code) : base("SystemExit: " + code.ToString(System.Globalization.CultureInfo.InvariantCulture))
    {
        Code = code;
    }

    public int Code { get; }

    public override string PythonType => "SystemExit";
}

/// <summary>margo_store.StateError: invalid input or unavailable state; never reset or continue unlocked.</summary>
public class StateError : PyValueError
{
    public StateError(string message, Exception? inner = null) : base(message, inner)
    {
    }

    public override string PythonType => "StateError";
}

/// <summary>margo_store.SetupRequired: no account is configured.</summary>
public class SetupRequired : StateError
{
    public SetupRequired(string message) : base(message)
    {
    }

    public override string PythonType => "SetupRequired";
}

/// <summary>margo_store.NotInitialized: explicit initialization is required; a read must not create storage.</summary>
public class NotInitialized : StateError
{
    public NotInitialized(string message) : base(message)
    {
    }

    public override string PythonType => "NotInitialized";
}

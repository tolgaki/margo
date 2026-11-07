namespace Margo.Cloud.State.Core;

/// <summary>
/// The <c>pathlib.Path</c> (POSIX) operations the state helpers use, on plain strings in pathlib's
/// normalised form: repeated slashes and <c>.</c> collapse, <c>..</c> stays (only <see cref="Resolve"/>
/// removes it), no trailing slash.
/// </summary>
public static class PyPath
{
    /// <summary><c>str(PurePosixPath(text))</c>.</summary>
    public static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0)
        {
            return ".";
        }

        var root = path.StartsWith('/') ? (path.StartsWith("//", StringComparison.Ordinal) && !path.StartsWith("///", StringComparison.Ordinal) ? "//" : "/") : "";
        var parts = path.Split('/').Where(part => part.Length > 0 && part != ".").ToList();
        var text = root + string.Join("/", parts);
        return text.Length == 0 ? "." : text;
    }

    /// <summary><c>Path(path).is_absolute()</c>.</summary>
    public static bool IsAbsolute(string path) => path.StartsWith('/');

    /// <summary><c>Path(path).absolute()</c>: joins the working directory, without resolving <c>..</c> or links.</summary>
    public static string Absolute(string path)
    {
        var normalized = Normalize(path);
        if (IsAbsolute(normalized))
        {
            return normalized;
        }

        var cwd = Directory.GetCurrentDirectory();
        return normalized == "." ? cwd : Join(cwd, normalized);
    }

    /// <summary><c>Path(path).expanduser()</c>, raising <c>RuntimeError</c> (as <see cref="InvalidOperationException"/>) when no home is known.</summary>
    public static string ExpandUser(string path)
    {
        var normalized = Normalize(path);
        if (!normalized.StartsWith('~'))
        {
            return normalized;
        }

        var slash = normalized.IndexOf('/', StringComparison.Ordinal);
        var first = slash < 0 ? normalized : normalized[..slash];
        string? home;
        if (first == "~")
        {
            // $HOME when set (even empty), otherwise the password database.
            home = PyRuntime.GetEnv("HOME") ?? NullIfEmpty(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }
        else
        {
            home = first[1..] == Environment.UserName
                ? NullIfEmpty(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
                : null;
        }

        if (home is null)
        {
            // posixpath.expanduser leaves the path unchanged; pathlib then raises RuntimeError.
            throw new InvalidOperationException("Could not determine home directory.");
        }

        var result = home.TrimEnd('/') + (slash < 0 ? "" : normalized[slash..]);
        return Normalize(result.Length == 0 ? "/" : result);
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    /// <summary><c>path.parent</c>.</summary>
    public static string Parent(string path)
    {
        var normalized = Normalize(path);
        if (normalized is "/" or "//" or ".")
        {
            return normalized;
        }

        var slash = normalized.LastIndexOf('/');
        if (slash < 0)
        {
            return ".";
        }

        if (slash == 0)
        {
            return "/";
        }

        return normalized.StartsWith("//", StringComparison.Ordinal) && slash == 1 ? "//" : normalized[..slash];
    }

    /// <summary><c>path.parents</c> (nearest first).</summary>
    public static List<string> Parents(string path)
    {
        var result = new List<string>();
        var current = Normalize(path);
        while (true)
        {
            var parent = Parent(current);
            if (parent == current)
            {
                break;
            }

            result.Add(parent);
            current = parent;
        }

        return result;
    }

    /// <summary><c>path.name</c>.</summary>
    public static string Name(string path)
    {
        var normalized = Normalize(path);
        if (normalized is "/" or "//" or ".")
        {
            return "";
        }

        var slash = normalized.LastIndexOf('/');
        return slash < 0 ? normalized : normalized[(slash + 1)..];
    }

    /// <summary><c>path / name</c>.</summary>
    public static string Join(string path, string name)
    {
        if (IsAbsolute(name))
        {
            return Normalize(name);
        }

        var normalized = Normalize(path);
        if (normalized == ".")
        {
            return Normalize(name);
        }

        return Normalize(normalized + "/" + name);
    }

    /// <summary><c>path.exists()</c> (follows links; ENOENT/ENOTDIR/EBADF/ELOOP mean false, other errors raise).</summary>
    public static bool Exists(string path) => PosixNative.TryStat(path) is not null;

    /// <summary><c>path.is_symlink()</c>.</summary>
    public static bool IsSymlink(string path) => PosixNative.TryStat(path, followSymlinks: false) is { IsSymlink: true };

    /// <summary><c>path.is_dir()</c>.</summary>
    public static bool IsDir(string path) => PosixNative.TryStat(path) is { IsDirectory: true };

    /// <summary><c>path.is_file()</c>.</summary>
    public static bool IsFile(string path) => PosixNative.TryStat(path) is { IsRegular: true };

    /// <summary>
    /// <c>Path(path).resolve()</c>: CPython 3.13 <c>posixpath.realpath(strict=False)</c>, then pathlib normalisation.
    /// </summary>
    public static string Resolve(string path)
    {
        var filename = Normalize(path);
        var rest = new Stack<string?>(filename.Split('/').Reverse());
        var partCount = rest.Count;
        var resolved = filename.StartsWith('/') ? "/" : Directory.GetCurrentDirectory();
        var seen = new Dictionary<string, string?>(StringComparer.Ordinal);
        while (partCount > 0)
        {
            var name = rest.Pop();
            if (name is null)
            {
                seen[rest.Pop()!] = resolved;
                continue;
            }

            partCount--;
            if (name.Length == 0 || name == ".")
            {
                continue;
            }

            if (name == "..")
            {
                var cut = resolved.LastIndexOf('/');
                resolved = cut <= 0 ? "/" : resolved[..cut];
                continue;
            }

            var newPath = resolved == "/" ? "/" + name : resolved + "/" + name;
            string target;
            try
            {
                var info = PosixNative.Stat(newPath, followSymlinks: false);
                if (!info.IsSymlink)
                {
                    resolved = newPath;
                    continue;
                }

                if (seen.TryGetValue(newPath, out var cached))
                {
                    resolved = cached ?? newPath;
                    continue;
                }

                target = PosixNative.ReadLink(newPath);
            }
            catch (PyOSError)
            {
                resolved = newPath;
                continue;
            }

            if (target.StartsWith('/'))
            {
                resolved = "/";
            }

            seen[newPath] = null;
            rest.Push(newPath);
            rest.Push(null);
            var parts = target.Split('/');
            for (var i = parts.Length - 1; i >= 0; i--)
            {
                rest.Push(parts[i]);
            }

            partCount += parts.Length;
        }

        return Normalize(resolved);
    }
}

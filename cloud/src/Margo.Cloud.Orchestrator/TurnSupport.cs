using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Margo.Cloud.Guard;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.Orchestrator;

/// <summary>Tokens for one turn (<c>orchestrator.py</c> <c>TurnTokens</c>). A missing token throws; there is no fallback.</summary>
public sealed class TurnTokens : ITokenProvider
{
    private readonly Dictionary<string, string?> _tokens;

    /// <param name="tokens">The turn's tokens by subject (<c>margo</c>, <c>principal</c>); a null or empty value is missing.</param>
    public TurnTokens(IReadOnlyDictionary<string, string?> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        _tokens = new Dictionary<string, string?>(tokens, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    /// <exception cref="TokenUnavailableException">"<c>{subject} token not available for this turn</c>".</exception>
    public string TokenFor(string subject)
    {
        if (subject is null || !_tokens.TryGetValue(subject, out var value) || string.IsNullOrEmpty(value))
        {
            throw new TokenUnavailableException(subject + " token not available for this turn");
        }

        return value;
    }
}

/// <summary>
/// The directory lookup did not succeed; that is not evidence that there is no manager (<c>orchestrator.py</c>
/// <c>ManagerLookupFailed</c>). The message is the guard's status, never data.
/// </summary>
public sealed class ManagerLookupFailedException : Exception
{
    public ManagerLookupFailedException()
    {
    }

    public ManagerLookupFailedException(string message)
        : base(message)
    {
    }

    public ManagerLookupFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The manager found in the directory.</summary>
/// <param name="Id">The manager's directory object ID.</param>
/// <param name="DisplayName">The display name, <c>your manager</c> when the directory gave none, or null when it gave a
/// value that is not text (Python keeps that value, which the reply gate then refuses to name).</param>
public sealed record Manager(string Id, string? DisplayName);

/// <summary>Reads Margo's manager from a Work IQ fetch of <c>/me/manager</c> (<c>orchestrator.py</c> <c>_manager_from</c>).</summary>
public static class ManagerDirectory
{
    /// <summary>The fetch the orchestrator makes on every turn, with Margo's own token.</summary>
    public const string ManagerUrl = "/me/manager?$select=id,displayName";

    /// <summary>
    /// <c>_manager_from(result)</c>: the manager, or null when the directory answered that there is none (404 only).
    /// Any other non-ok result throws: an outage, throttling or a billing denial says nothing about who manages Margo.
    /// </summary>
    /// <exception cref="ManagerLookupFailedException">The lookup did not succeed.</exception>
    public static Manager? From(GuardResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var data = result.Data ?? new JsonObject();
        if (result.Status == "error")
        {
            var codes = GuardPolicy.StatusCodes(data);
            if (codes.Count > 0 && codes.All(code => PyJson.AsInt(code) == 404))
            {
                return null;
            }
        }

        if (result.Status != "ok")
        {
            throw new ManagerLookupFailedException(string.IsNullOrEmpty(result.Status) ? "no status" : result.Status);
        }

        var candidates = new List<JsonNode?> { data };
        if (PyNode.Get(data, "results") is JsonArray results)
        {
            foreach (var entry in results)
            {
                if (entry is JsonObject item)
                {
                    var nested = PyNode.Get(item, "data");
                    candidates.Add(PyJson.Truthy(nested) ? nested : new JsonObject());
                }
            }
        }

        if (PyNode.Get(data, "data") is JsonObject direct)
        {
            candidates.Add(direct);
        }

        foreach (var candidate in candidates)
        {
            if (candidate is JsonObject found && PyNode.AsString(PyNode.Get(found, "id")) is { Length: > 0 } id)
            {
                var name = PyNode.Get(found, "displayName");
                return new Manager(id, !PyJson.Truthy(name) ? "your manager" : PyNode.AsString(name));
            }
        }

        return null;
    }
}

/// <summary>The manager's chat commands, matched as Python's <c>re</c> does (<c>VOICE_COMMAND</c>, <c>REPLIES_COMMAND</c>).</summary>
/// <remarks>
/// Python's patterns are <c>^\s*/voice\s*$</c> and <c>^\s*/replies\s+(none|acknowledge|approve)\s*$</c> with
/// <c>re.IGNORECASE</c>. They are matched here by hand with State.Core's Python character tables: <c>\s</c> is
/// <c>str.isspace()</c> and case-insensitive letters include CPython's extras (KELVIN SIGN for <c>k</c>, LONG S for
/// <c>s</c>, dotted and dotless I for <c>i</c>), which .NET's <c>\s</c> and <c>IgnoreCase</c> do not match identically.
/// </remarks>
public static class ManagerCommands
{
    private static readonly string[] ReplyModeWords = [ReplyModes.None, ReplyModes.Acknowledge, ReplyModes.Approve];

    /// <summary><c>VOICE_COMMAND.match(text)</c>.</summary>
    public static bool IsVoice(string? text)
    {
        var points = PyText.CodePoints(text ?? "");
        var index = SkipSpace(points, 0);
        return Word(points, ref index, "/voice") && SkipSpace(points, index) == points.Length;
    }

    /// <summary><c>REPLIES_COMMAND.match(text)</c>: the requested mode, lower-cased, or null when the text is not the command.</summary>
    public static string? ReplyMode(string? text)
    {
        var points = PyText.CodePoints(text ?? "");
        var index = SkipSpace(points, 0);
        if (!Word(points, ref index, "/replies"))
        {
            return null;
        }

        var start = index;
        index = SkipSpace(points, index);
        if (index == start)
        {
            return null;
        }

        foreach (var mode in ReplyModeWords)
        {
            var at = index;
            if (Word(points, ref at, mode) && SkipSpace(points, at) == points.Length)
            {
                return mode;
            }
        }

        return null;
    }

    private static int SkipSpace(int[] points, int index)
    {
        while (index < points.Length && PyText.IsSpace(points[index]))
        {
            index++;
        }

        return index;
    }

    private static bool Word(int[] points, ref int index, string word)
    {
        if (index + word.Length > points.Length)
        {
            return false;
        }

        for (var i = 0; i < word.Length; i++)
        {
            var letter = word[i];
            var point = points[index + i];
            var matches = letter is >= 'a' and <= 'z' ? PyText.MatchesIgnoreCase(letter, point) : point == letter;
            if (!matches)
            {
                return false;
            }
        }

        index += word.Length;
        return true;
    }
}

/// <summary>The manager's private <c>preferences.md</c> as the orchestrator writes it (<c>orchestrator.py</c> <c>set_reply_mode</c>).</summary>
public static class ReplySetting
{
    private static readonly Regex Line = new(@"^[ \t]*[-*][ \t]+\*\*Replies to other people:\*\*.*$",
                                             RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>
    /// <c>set_reply_mode(preferences, mode)</c>: writes the single setting line, preserving the rest of the manager's
    /// preferences file. Every existing setting line is replaced; without one, the line is appended. The file is read
    /// as strict UTF-8 with universal newlines and written back with <c>\n</c>, mode 0600, its directory 0700.
    /// </summary>
    public static void SetReplyMode(string preferences, string mode)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(mode);
        var line = "- **" + ReplyPolicy.SettingLabel + ":** " + mode;
        var text = PyPath.IsFile(preferences) ? PyIO.ReadTextFile(preferences) : "";
        if (Line.IsMatch(text))
        {
            text = Line.Replace(text, _ => line);
        }
        else
        {
            text = (text.TrimEnd('\n') + "\n" + line + "\n").TrimStart('\n');
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(preferences))!;
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllBytes(preferences, PyIO.EncodeUtf8(text));
        File.SetUnixFileMode(preferences, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>The manager's preferences text, or null when there is no file (strict UTF-8, universal newlines).</summary>
    public static string? Read(string preferences) => PyPath.IsFile(preferences) ? PyIO.ReadTextFile(preferences) : null;

    /// <summary>The note returned to the manager after <c>/replies MODE</c>.</summary>
    public static string ChangedNote(string mode) => mode switch
    {
        ReplyModes.None => "I won't reply to anyone else; their messages wait for you.",
        ReplyModes.Acknowledge => "I'll send one short 'passed this on' note per conversation, nothing more.",
        ReplyModes.Approve => "Noted, but approval cards aren't in this version yet, so until they are I'll treat this as 'none'.",
        _ => throw new PyKeyError(mode),
    };
}

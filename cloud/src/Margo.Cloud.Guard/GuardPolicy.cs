using System.Collections.Frozen;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Work;

namespace Margo.Cloud.Guard;

/// <summary>
/// The guard's fixed policy tables and argument checks (<c>guard.py</c> module level). The tables are public because
/// the engine builds its function tools from them (<c>engine.py</c> imports <c>TOOL_ARGS</c> and <c>WRITE_TOOLS</c>) and
/// the orchestrator reads status codes with <see cref="StatusCodes"/>.
/// </summary>
public static class GuardPolicy
{
    /// <summary><c>MAX_FETCH_PATHS</c>: the most paths one <c>fetch</c> may name.</summary>
    public const int MaxFetchPaths = 20;

    /// <summary><c>MAX_RETRIEVE_QUERIES</c>.</summary>
    public const int MaxRetrieveQueries = 10;

    /// <summary><c>MAX_PATH</c>, in code points.</summary>
    public const int MaxPath = 2048;

    /// <summary><c>UNKNOWN_WRITE</c>: the reason given when a write may have taken effect.</summary>
    public const string UnknownWrite = "the write may have taken effect; outcome unknown, not retried";

    /// <summary><c>TOOL_ARGS</c>: Work IQ's tools and the arguments each accepts (besides the control arguments).</summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> ToolArgs { get; } = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
    {
        ["fetch"] = Set("entityUrls", "agentId"),
        ["fetch_blob"] = Set("path", "format", "agentId"),
        ["call_function"] = Set("functionUrl", "parentUrl", "agentId"),
        ["create_entity"] = Set("parentUrl", "jsonBody", "agentId"),
        ["update_entity"] = Set("entityUrl", "jsonBody", "agentId"),
        ["delete_entity"] = Set("entityUrl", "agentId"),
        ["do_action"] = Set("actionUrl", "jsonBody", "agentId"),
        ["ask"] = Set("question", "agentId", "fileUrls", "conversationId", "timeZone"),
        ["retrieve"] = Set("query", "agentId", "strategy", "capabilities", "includeDeveloperCard"),
        ["list_agents"] = Set(),
        ["get_schema"] = Set("operationIds", "path", "operationType", "format", "backend", "agentId"),
        ["search_paths"] = Set("query", "filter", "backend", "agentId"),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary><c>WRITE_TOOLS</c>.</summary>
    public static IReadOnlySet<string> WriteTools { get; } = Set("create_entity", "update_entity", "delete_entity", "do_action");

    /// <summary><c>SINGLE_PATH_ARG</c>: the one path argument of each single-path tool.</summary>
    public static IReadOnlyDictionary<string, string> SinglePathArg { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["fetch_blob"] = "path",
        ["create_entity"] = "parentUrl",
        ["update_entity"] = "entityUrl",
        ["delete_entity"] = "entityUrl",
        ["do_action"] = "actionUrl",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary><c>MUST_BE_EMPTY</c>: reserved or routing parameters that would send the request to another agent or backend.</summary>
    public static IReadOnlyList<string> MustBeEmpty { get; } = ["agentId", "backend"];

    /// <summary><c>CONTROL_ARGS</c>: the guard's own arguments, never sent to Work IQ.</summary>
    public static IReadOnlySet<string> ControlArgs { get; } = Set("on_data_of", "action_id", "attempt_id");

    /// <summary><c>ROOTS</c>: the allowed first path segment (lower-cased) per <c>on_data_of</c>.</summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> Roots { get; } = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
    {
        [Subjects.Principal] = Set("me", "users", "sites", "drives", "chats", "teams", "planner"),
        [Subjects.Self] = Set("me", "users", "chats"),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary><c>USER_DIRECTORY_READS</c>: what <c>/users/{id}/...</c> may read.</summary>
    public static IReadOnlySet<string> UserDirectoryReads { get; } = Set("manager", "directreports");

    /// <summary><c>BLOCKED_SEGMENTS</c>: never reachable, at any depth.</summary>
    public static IReadOnlySet<string> BlockedSegments { get; } = Set("authentication", "serviceprincipals");

    /// <summary><c>RETRIEVE_CAPABILITIES</c>.</summary>
    public static IReadOnlySet<string> RetrieveCapabilities { get; } =
        Set("People", "Meetings", "OneDriveAndSharePoint", "Email", "TeamsMessages", "GraphConnectors");

    /// <summary>
    /// <c>REJECTED_NO_EFFECT</c>: Graph rejected these before acting; anything else after a write was sent stays unknown.
    /// </summary>
    public static IReadOnlySet<int> RejectedNoEffect { get; } = FrozenSet.Create(400, 401, 403, 404, 405, 409, 412, 413, 415, 422, 429);

    /// <summary><c>THIRD_PARTY_TOOLS</c>: schema discovery, the only tools a third-party run may use besides opening its trigger.</summary>
    public static IReadOnlySet<string> ThirdPartyTools { get; } = Set("get_schema", "search_paths", "list_agents");

    /// <summary>
    /// <c>DELEGABLE</c>: Exchange delegation covers mail and calendar only. Teams, files and Copilot search do not
    /// follow it, so under delegation those principal sources are reported unavailable, not empty.
    /// </summary>
    public static IReadOnlySet<string> Delegable { get; } = Set("messages", "mailfolders", "calendar", "calendars", "calendarview", "events");

    /// <summary><c>PATH_KEYS</c>: the arguments that carry resource paths, in the order delegation rewrites them.</summary>
    public static IReadOnlyList<string> PathKeys { get; } = ["entityUrls", "path", "functionUrl", "parentUrl", "entityUrl", "actionUrl"];

    private static readonly string[] EncodedSeparators = ["%2e", "%2f", "%5c", "%00"];
    private static readonly string[] CallFunctionPaths = ["functionUrl", "parentUrl"];
    private static readonly string[] SearchKeys = ["query", "filter"];
    private static readonly string[] AskStrings = ["conversationId", "timeZone"];
    private static readonly IReadOnlySet<string> Strategies = Set("grounding", "copilot");
    private static readonly IReadOnlySet<string> OperationTypes = Set("fetch", "create", "update", "action", "function");

    /// <summary>
    /// <c>model_tools()</c>: the tool definitions shown to the model, Work IQ's tools plus an explicit subject, sorted
    /// by name. Write tools also take the claim arguments; the routing arguments are never offered.
    /// </summary>
    public static JsonArray ModelTools()
    {
        var tools = new JsonArray();
        foreach (var name in ToolArgs.Keys.Order(StringComparer.Ordinal))
        {
            var properties = new JsonObject
            {
                ["on_data_of"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray(Subjects.Principal, Subjects.Self),
                    ["description"] = "Whose data this call is about: the principal's, or Margo's own.",
                },
            };
            foreach (var arg in ToolArgs[name].Except(MustBeEmpty, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                properties[arg] = new JsonObject();
            }

            if (WriteTools.Contains(name))
            {
                properties["action_id"] = new JsonObject { ["type"] = "string", ["description"] = "Approved action being executed." };
                properties["attempt_id"] = new JsonObject { ["type"] = "string", ["description"] = "Live execution attempt from the journal." };
            }

            tools.Add(new JsonObject
            {
                ["name"] = name,
                ["input_schema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = properties,
                    ["required"] = new JsonArray("on_data_of"),
                },
            });
        }

        return tools;
    }

    /// <summary>
    /// <c>_status_codes(result)</c>: the payload's integer <c>statusCode</c>, then each <c>results[].statusCode</c>, by
    /// Python's <c>isinstance(x, int)</c> (booleans count; <c>200.0</c> does not). Values are detached copies, so a
    /// boolean stays a boolean (Python records <c>true</c>) and an integer beyond 64 bits stays exact.
    /// </summary>
    public static IReadOnlyList<JsonNode> StatusCodes(JsonNode? result)
    {
        if (result is not JsonObject payload)
        {
            return [];
        }

        var codes = new List<JsonNode>();
        if (PyDynamic.Get(payload, "statusCode") is { } code && PyJson.IsIntOrBool(code))
        {
            codes.Add(PyDynamic.Clone(code)!);
        }

        if (PyDynamic.Get(payload, "results") is JsonArray results)
        {
            foreach (var entry in results)
            {
                if (entry is JsonObject item && PyDynamic.Get(item, "statusCode") is { } entryCode && PyJson.IsIntOrBool(entryCode))
                {
                    codes.Add(PyDynamic.Clone(entryCode)!);
                }
            }
        }

        return codes;
    }

    /// <summary><c>200 &lt;= code &lt; 300</c> for a status code from <see cref="StatusCodes"/> (<c>True</c> is 1).</summary>
    public static bool IsSuccess(JsonNode code)
    {
        var value = PyJson.AsInt(code);
        return value >= 200 && value < 300;
    }

    /// <summary><c>code in REJECTED_NO_EFFECT</c> for a status code from <see cref="StatusCodes"/>.</summary>
    public static bool IsRejectedNoEffect(JsonNode code)
    {
        var value = PyJson.AsInt(code);
        return value >= int.MinValue && value <= int.MaxValue && RejectedNoEffect.Contains((int)value);
    }

    // -- checks (private in Python) ------------------------------------------------------

    /// <summary>
    /// <c>_check_path(value, intent, write, delegated_to=None)</c>: the path without its query string, or a refusal.
    /// <paramref name="delegatedTo"/> is the bound principal's object ID when the guard itself rewrote <c>/me</c> to
    /// their delegated mailbox; only that exact mailbox is then reachable under <c>/users</c>.
    /// </summary>
    internal static string CheckPath(JsonNode? value, string intent, bool write, string? delegatedTo = null)
    {
        if (PyDynamic.AsString(value) is not { } text || text.Length == 0 || PyRepr.Length(text) > MaxPath)
        {
            throw new RefusedException("resource path must be a non-empty string");
        }

        if (!text.StartsWith('/') || text.StartsWith("//", StringComparison.Ordinal) || text.Contains("://", StringComparison.Ordinal))
        {
            throw new RefusedException("only relative Work IQ resource paths are allowed");
        }

        if (text.Any(ch => ch < 32 || ch == 127) || text.Contains('\\', StringComparison.Ordinal))
        {
            throw new RefusedException("control characters and backslashes are not allowed in paths");
        }

        var path = text.Split('?', 2)[0];
        var lowered = LowerAscii(path);
        if (EncodedSeparators.Any(code => lowered.Contains(code, StringComparison.Ordinal)))
        {
            throw new RefusedException("encoded path separators are not allowed");
        }

        if (path.Any(PyRepr.IsSpace))
        {
            throw new RefusedException("whitespace is not allowed in the path");
        }

        var trimmed = path.EndsWith('/') && path.Length > 1 ? path[..^1] : path;
        var segments = trimmed.Split('/')[1..];
        if (segments.Length == 0 || segments.Any(segment => segment is "" or "." or ".."))
        {
            throw new RefusedException("empty or relative path segments are not allowed");
        }

        var names = segments.Select(LowerAscii).ToArray();
        if (!Roots[intent].Contains(names[0]))
        {
            throw new RefusedException("'/" + segments[0] + "' is not an allowed root for on_data_of=" + intent);
        }

        if (names.Any(BlockedSegments.Contains))
        {
            throw new RefusedException("path reaches a blocked resource");
        }

        if (names[0] == "users" && delegatedTo is not null && names.Length >= 3 && Delegable.Contains(names[2]))
        {
            if (!string.Equals(segments[1], delegatedTo, StringComparison.Ordinal))
            {
                throw new RefusedException("delegated access reaches only the bound principal's mailbox");
            }

            return path;
        }

        // Colleagues' mail, calendars, chats and files are never reachable; the principal's own data uses /me under
        // the principal token. Directory lookups stay available.
        if (names[0] == "users" && (write || segments.Length is not (2 or 3) || (segments.Length == 3 && !UserDirectoryReads.Contains(names[2]))))
        {
            throw new RefusedException("/users/{id} allows only profile, manager and directReports reads");
        }

        return path;
    }

    /// <summary><c>_is_message(path, message_id)</c>: <c>/me/messages/{message_id}</c> or its attachments. Graph message IDs are case-sensitive.</summary>
    internal static bool IsMessage(string path, string? messageId)
    {
        var segments = path.Trim('/').Split('/');
        return messageId is not null && segments.Length is 3 or 4
               && LowerAscii(segments[0]) == "me" && LowerAscii(segments[1]) == "messages"
               && string.Equals(segments[2], messageId, StringComparison.Ordinal)
               && (segments.Length == 3 || LowerAscii(segments[3]) == "attachments");
    }

    /// <summary><c>_check_json_body(value)</c>: absent, or a string Python's <c>json.loads</c> accepts.</summary>
    /// <remarks>A body nested beyond CPython's limit raises <see cref="PyRecursionError"/>, as Python's <c>RecursionError</c>
    /// escapes this check (the guard then refuses it as a malformed tool call).</remarks>
    internal static void CheckJsonBody(JsonNode? value)
    {
        if (IsNone(value))
        {
            return;
        }

        if (PyDynamic.AsString(value) is not { } text)
        {
            throw new RefusedException("jsonBody must be a JSON-encoded string");
        }

        try
        {
            PyJson.Loads(text);
        }
        catch (PyValueError)
        {
            throw new RefusedException("jsonBody is not valid JSON");
        }
    }

    /// <summary><c>_validate(tool, args, intent, write, delegated_to=None)</c>: check one call's arguments and return the resource paths it touches.</summary>
    internal static List<string> Validate(string tool, JsonObject args, string intent, bool write, string? delegatedTo = null)
    {
        foreach (var key in MustBeEmpty)
        {
            var routed = PyDynamic.Get(args, key);
            if (!IsNone(routed) && !PyDynamic.Is(routed, ""))
            {
                throw new RefusedException("'" + key + "' would route outside the built-in Work IQ path");
            }
        }

        switch (tool)
        {
            case "fetch":
                if (PyDynamic.Get(args, "entityUrls") is not JsonArray urls || urls.Count == 0 || urls.Count > MaxFetchPaths)
                {
                    throw new RefusedException("entityUrls must list 1-" + MaxFetchPaths + " paths");
                }

                return urls.Select(url => CheckPath(url, intent, write, delegatedTo)).ToList();
            case "call_function":
                var present = CallFunctionPaths.Where(args.ContainsKey).ToList();
                if (present.Count != 1)
                {
                    throw new RefusedException("call_function needs exactly one of functionUrl or parentUrl");
                }

                return [CheckPath(args[present[0]], intent, write, delegatedTo)];
            case "retrieve":
                ValidateRetrieve(args);
                return [];
            case "ask":
                ValidateAsk(args);
                return [];
            default:
                break;
        }

        if (SinglePathArg.TryGetValue(tool, out var pathArg))
        {
            CheckJsonBody(PyDynamic.Get(args, "jsonBody"));
            return [CheckPath(PyDynamic.Get(args, pathArg), intent, write, delegatedTo)];
        }

        if (tool == "get_schema" && !In(PyDynamic.Get(args, "operationType"), OperationTypes, allowNone: false))
        {
            throw new RefusedException("get_schema needs operationType fetch, create, update, action or function");
        }

        if (tool == "search_paths")
        {
            var present = SearchKeys.Where(args.ContainsKey).ToList();
            if (present.Count != 1 || PyDynamic.AsString(args[present[0]]) is not { } query || PyRepr.Strip(query).Length == 0)
            {
                throw new RefusedException("search_paths needs exactly one non-empty query");
            }
        }

        return [];
    }

    private static void ValidateRetrieve(JsonObject args)
    {
        // Work IQ takes a list of queries, as the playbooks write it; a single string also works.
        var query = PyDynamic.Get(args, "query");
        List<JsonNode?>? queries = PyDynamic.AsString(query) is not null ? [query] : (query as JsonArray)?.ToList();
        if (queries is null || queries.Count < 1 || queries.Count > MaxRetrieveQueries
            || !queries.All(item => PyDynamic.AsString(item) is { } text && PyRepr.Strip(text).Length > 0))
        {
            throw new RefusedException("retrieve needs a query: a string or a list of 1-" + MaxRetrieveQueries + " strings");
        }

        if (!In(PyDynamic.Get(args, "strategy"), Strategies, allowNone: true))
        {
            throw new RefusedException("unknown retrieve strategy");
        }

        if (PyDynamic.Get(args, "capabilities", new JsonArray()) is not JsonArray capabilities)
        {
            throw new RefusedException("capabilities must be a list");
        }

        foreach (var capability in capabilities)
        {
            if (capability is not JsonObject fields || PyDynamic.AsString(PyDynamic.Get(fields, "name")) is not { } name
                || !RetrieveCapabilities.Contains(name))
            {
                throw new RefusedException("unsupported retrieve capability");
            }

            if (fields.Any(pair => LowerAscii(pair.Key).Contains("mailbox", StringComparison.Ordinal)))
            {
                throw new RefusedException("other people's or shared mailboxes are not searchable in v1");
            }
        }

        if (!PyJson.IsBool(PyDynamic.Get(args, "includeDeveloperCard", JsonValue.Create(false))))
        {
            throw new RefusedException("includeDeveloperCard must be a boolean");
        }
    }

    private static void ValidateAsk(JsonObject args)
    {
        if (PyDynamic.AsString(PyDynamic.Get(args, "question")) is not { } question || PyRepr.Strip(question).Length == 0)
        {
            throw new RefusedException("ask needs a question");
        }

        if (PyDynamic.Get(args, "fileUrls", new JsonArray()) is not JsonArray urls
            || !urls.All(url => PyDynamic.AsString(url) is { } text && text.StartsWith("https://", StringComparison.Ordinal)))
        {
            throw new RefusedException("fileUrls must be https document URLs");
        }

        foreach (var key in AskStrings)
        {
            if (args.TryGetPropertyValue(key, out var value) && PyDynamic.AsString(value) is null)
            {
                throw new RefusedException(key + " must be a string");
            }
        }
    }

    /// <summary><c>json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False)</c> (<c>_canonical</c>).</summary>
    internal static string Canonical(JsonNode? value) =>
        PyJson.Dumps(value, itemSeparator: ",", keySeparator: ":", sortKeys: true, ensureAscii: false);

    /// <summary>
    /// Python's <c>str.lower()</c> for comparing with ASCII words and searching for the ASCII patterns used here: only
    /// ASCII letters and U+212A KELVIN SIGN lower-case to ASCII (U+0130 becomes <c>i</c> plus a combining dot, which no
    /// word or pattern here can match). Other characters are left as they are; they never match ASCII either way.
    /// </summary>
    internal static string LowerAscii(string text)
    {
        if (!text.Any(ch => ch is (>= 'A' and <= 'Z') or '\u212A'))
        {
            return text;
        }

        return string.Create(text.Length, text, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var ch = source[i];
                span[i] = ch switch
                {
                    >= 'A' and <= 'Z' => (char)(ch + 32),
                    '\u212A' => 'k',
                    _ => ch,
                };
            }
        });
    }

    /// <summary><c>value in (None, "a", "b")</c> (or without <c>None</c>): a tuple test by equality, so lists and dicts are simply not in it.</summary>
    private static bool In(JsonNode? value, IReadOnlySet<string> options, bool allowNone) =>
        IsNone(value) ? allowNone : PyDynamic.AsString(value) is { } text && options.Contains(text);

    /// <summary>Python <c>None</c>: a missing key, a JSON <c>null</c>, or a value node holding one.</summary>
    internal static bool IsNone(JsonNode? value) => value is null || (value is JsonValue && PyJson.KindOf(value) == PyKind.None);

    private static FrozenSet<string> Set(params string[] values) => values.ToFrozenSet(StringComparer.Ordinal);
}

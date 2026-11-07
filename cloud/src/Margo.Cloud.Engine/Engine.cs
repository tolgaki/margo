using System.Text.Json.Nodes;
using Margo.Cloud.Guard;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.Engine;

/// <summary>The office queue as the engine sees it (Python passes an <c>OfficeReader</c> with <c>pending()</c>).</summary>
public interface IOfficeRequests
{
    /// <summary><c>pending()</c>: the unreviewed messages other people sent Margo, as JSON (a list of request objects).</summary>
    Task<JsonNode?> PendingAsync(CancellationToken cancellationToken = default);
}

/// <summary>What one turn returns (<c>{"text", "response_id", "rounds"}</c>, plus <c>"stopped": true</c>).</summary>
/// <param name="Text">The model's final text, or the <see cref="EngineTools.Stopped"/> message.</param>
/// <param name="ResponseId">The final response's ID, to continue the conversation with; null when stopped or when
/// the response had no string ID.</param>
/// <param name="Rounds">Tool rounds used (one more than the limit when stopped).</param>
/// <param name="Stopped">The round limit was reached before the model finished.</param>
public sealed record EngineResult(string Text, string? ResponseId, int Rounds, bool Stopped = false)
{
    /// <summary>The result as Python's dict.</summary>
    public JsonObject ToJsonObject()
    {
        var result = new JsonObject { ["text"] = Text, ["response_id"] = ResponseId, ["rounds"] = Rounds };
        if (Stopped)
        {
            result["stopped"] = true;
        }

        return result;
    }
}

/// <summary>
/// One Margo turn: the desktop persona and skills, the model, and guarded tools, bounded (<c>engine.py</c>
/// <c>Engine</c>).
/// </summary>
/// <remarks>
/// <para>Hosted Margo is the hosted, always-on version of desktop Margo. The persona file and every skill are loaded
/// unchanged (<see cref="SkillLibrary"/>), the Work IQ tools carry the names the skills use (<c>workiq-fetch</c>), and
/// the state scripts the procedures run are offered as <c>margo-state</c> (<see cref="StateCommands"/>). What is not
/// hosted yet is listed in <see cref="NotHosted"/> and told to the model.</para>
/// <para>The model gets function tools only. Every Work IQ call goes through <see cref="WorkIQGuard"/>; the model
/// never holds a token and never calls an MCP server directly. This version exposes read-only Work IQ tools: outward
/// actions arrive with approval cards in a later phase.</para>
/// </remarks>
public sealed class Engine
{
    /// <param name="model">The Responses API client.</param>
    /// <param name="deployment">The model deployment name sent as <c>model</c>.</param>
    /// <param name="repoRoot">The root holding <c>agents/</c> and <c>skills/</c>.</param>
    /// <param name="preferencesPath">The default private <c>preferences.md</c>.</param>
    /// <param name="timeZone">The IANA time zone the model states times in.</param>
    /// <param name="maxRounds">The most tool rounds in one turn.</param>
    public Engine(IModelClient model, string deployment, string repoRoot, string? preferencesPath = null,
                  string timeZone = EngineTools.DefaultTimeZone, int maxRounds = 12)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(deployment);
        ArgumentNullException.ThrowIfNull(repoRoot);
        ArgumentNullException.ThrowIfNull(timeZone);
        Model = model;
        Deployment = deployment;
        RepoRoot = repoRoot;
        PreferencesPath = preferencesPath;
        TimeZone = timeZone;
        MaxRounds = maxRounds;
    }

    /// <summary>The Responses API client.</summary>
    public IModelClient Model { get; }

    /// <summary>The model deployment.</summary>
    public string Deployment { get; }

    /// <summary>The repository root.</summary>
    public string RepoRoot { get; }

    /// <summary>The default preferences file.</summary>
    public string? PreferencesPath { get; }

    /// <summary>The time zone.</summary>
    public string TimeZone { get; }

    /// <summary>The most tool rounds in one turn.</summary>
    public int MaxRounds { get; }

    /// <summary>
    /// <c>run(text, *, mode, guard, office, state, previous_response_id, now, preferences_path)</c>.
    /// </summary>
    /// <param name="text">The manager's message.</param>
    /// <param name="mode">One of <see cref="RunModes"/>.</param>
    /// <param name="guard">The only path to Work IQ.</param>
    /// <param name="office">The office queue (attended runs only), or null.</param>
    /// <param name="state">This instance's <c>margo-state</c> tool, or null where Margo's state is not reachable.</param>
    /// <param name="previousResponseId">Continues an earlier conversation when not empty.</param>
    /// <param name="now">The time shown to the model; null for the current time.</param>
    /// <param name="preferencesPath">This turn's preferences file; null for <see cref="PreferencesPath"/>.</param>
    /// <param name="cancellationToken">Cancels the turn.</param>
    /// <exception cref="MalformedModelResponseException">The model answered with a shape Python fails on.</exception>
    public async Task<EngineResult> RunAsync(string text, string? mode, WorkIQGuard guard, IOfficeRequests? office = null,
                                             IStateCommands? state = null, string? previousResponseId = null,
                                             DateTimeOffset? now = null, string? preferencesPath = null,
                                             CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(guard);
        var library = new SkillLibrary(RepoRoot, preferencesPath ?? PreferencesPath);
        var instructions = EngineTools.LoadInstructions(RepoRoot, TimeZone, now, library);
        var tools = EngineTools.FunctionTools(mode);
        var request = new JsonObject
        {
            ["model"] = Deployment,
            ["instructions"] = instructions,
            ["input"] = text,
            ["tools"] = PyNode.Copy(tools),
        };
        if (!string.IsNullOrEmpty(previousResponseId))
        {
            request["previous_response_id"] = previousResponseId;
        }

        var response = AsDict(await Model.CreateResponseAsync(request, cancellationToken).ConfigureAwait(false));
        var rounds = 0;
        while (true)
        {
            var calls = Items(response, "output").Where(item => PyNode.Is(Get(item, "type"), "function_call")).ToList();
            if (calls.Count == 0)
            {
                return new EngineResult(OutputText(response), PyNode.AsString(Get(response, "id")), rounds);
            }

            rounds++;
            if (rounds > MaxRounds)
            {
                return new EngineResult(EngineTools.Stopped.Replace("{rounds}", MaxRounds.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal),
                                        null, rounds, Stopped: true);
            }

            var outputs = new JsonArray();
            foreach (var call in calls)
            {
                var result = await DispatchAsync(Get(call, "name"), Get(call, "arguments"), mode, guard, office, library, state,
                                                 cancellationToken).ConfigureAwait(false);
                outputs.Add(new JsonObject
                {
                    ["type"] = "function_call_output",
                    ["call_id"] = PyNode.Copy(Get(call, "call_id")),
                    ["output"] = EngineTools.Clip(result),
                });
            }

            response = AsDict(await Model.CreateResponseAsync(new JsonObject
            {
                ["model"] = Deployment,
                ["instructions"] = instructions,
                ["tools"] = PyNode.Copy(tools),
                ["previous_response_id"] = PyNode.Copy(Get(response, "id")),
                ["input"] = outputs,
            }, cancellationToken).ConfigureAwait(false));
        }
    }

    /// <summary>
    /// <c>_dispatch(name, raw_arguments, mode, guard, office, library, state)</c>: one function call. Refusals are
    /// results. The voice server calls this directly with its own library and tools.
    /// </summary>
    /// <param name="name">The function name the model called (any JSON value).</param>
    /// <param name="rawArguments">The model's <c>arguments</c> (normally a JSON string).</param>
    /// <param name="mode">The run mode.</param>
    /// <param name="guard">The only path to Work IQ.</param>
    /// <param name="office">The office queue, or null.</param>
    /// <param name="library">The turn's skill library.</param>
    /// <param name="state">The <c>margo-state</c> tool, or null.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public static async Task<JsonNode?> DispatchAsync(JsonNode? name, JsonNode? rawArguments, string? mode, WorkIQGuard guard,
                                                      IOfficeRequests? office, SkillLibrary library, IStateCommands? state = null,
                                                      CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(library);
        JsonNode? arguments;
        if (!PyJson.Truthy(rawArguments))
        {
            arguments = new JsonObject(); // json.loads(raw_arguments or "{}")
        }
        else if (PyNode.AsString(rawArguments) is { } raw)
        {
            try
            {
                arguments = PyJson.Loads(raw);
            }
            catch (PyValueError)
            {
                return Refused("arguments were not valid JSON");
            }
        }
        else
        {
            return Refused("arguments were not valid JSON"); // json.loads of a non-string is a TypeError
        }

        if (arguments is not JsonObject given)
        {
            return Refused("arguments must be an object");
        }

        var function = PyNode.AsString(name);
        if (function is not null && function.StartsWith(EngineTools.Prefix, StringComparison.Ordinal))
        {
            var tool = function[EngineTools.Prefix.Length..];
            if (GuardPolicy.WriteTools.Contains(tool) || !GuardPolicy.ToolArgs.ContainsKey(tool))
            {
                return Refused("not available in this version");
            }

            return (await guard.CallAsync(tool, given, mode, cancellationToken).ConfigureAwait(false)).ToJsonObject();
        }

        if (function == "load_skill")
        {
            return library.Load(PyNode.Get(given, "name"));
        }

        if (function == "read_skill_file")
        {
            return library.Read(PyNode.Get(given, "skill"), PyNode.Get(given, "path"));
        }

        if (function == EngineTools.StateTool && mode is RunModes.Attended or RunModes.Unattended && state is not null)
        {
            return await state.RunAsync(given, mode, cancellationToken).ConfigureAwait(false);
        }

        if (function == "office_requests" && mode == RunModes.Attended && office is not null)
        {
            try
            {
                return new JsonObject { ["status"] = "ok", ["requests"] = PyNode.Copy(await office.PendingAsync(cancellationToken).ConfigureAwait(false)) };
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                return new JsonObject { ["status"] = "error", ["reason"] = "the request queue could not be read" };
            }
        }

        return Refused("unknown tool");
    }

    /// <summary><c>_output_text(response)</c>: <c>output_text</c> when it is a non-empty string, else the message texts joined by newlines.</summary>
    public static string OutputText(JsonObject response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (PyNode.AsString(Get(response, "output_text")) is { Length: > 0 } direct)
        {
            return direct;
        }

        var parts = new List<string>();
        foreach (var item in Items(response, "output"))
        {
            if (!PyNode.Is(Get(item, "type"), "message"))
            {
                continue;
            }

            foreach (var content in Items(item, "content"))
            {
                if (PyNode.IsOneOf(Get(content, "type"), "output_text", "text") && PyNode.AsString(Get(content, "text")) is { } part)
                {
                    parts.Add(part);
                }
            }
        }

        return string.Join("\n", parts);
    }

    private static JsonObject Refused(string reason) => new() { ["status"] = "refused", ["reason"] = reason };

    /// <summary><c>_as_dict</c>: a JSON object, or <c>{}</c> for anything else.</summary>
    private static JsonObject AsDict(JsonNode? response) => response as JsonObject ?? [];

    /// <summary><c>value.get(key)</c> where Python would call <c>.get</c> on whatever the item is.</summary>
    private static JsonNode? Get(JsonNode? item, string key) => item switch
    {
        JsonObject obj => PyNode.Get(obj, key),
        _ => throw new MalformedModelResponseException("'" + PyNode.TypeName(item) + "' object has no attribute 'get'"),
    };

    /// <summary>
    /// <c>for item in value.get(key, [])</c>: a missing key is empty; a list yields its items; an empty string yields
    /// nothing; a dict yields its keys and a string its characters (on which Python's <c>.get</c> then fails); anything
    /// else is not iterable.
    /// </summary>
    private static JsonNode?[] Items(JsonNode? owner, string key)
    {
        var obj = owner as JsonObject ?? throw new MalformedModelResponseException("'" + PyNode.TypeName(owner) + "' object has no attribute 'get'");
        if (!obj.TryGetPropertyValue(key, out var value))
        {
            return [];
        }

        return value switch
        {
            JsonArray array => array.ToArray(),
            JsonObject keys => keys.Select(pair => (JsonNode?)JsonValue.Create(pair.Key)).ToArray(),
            _ when PyNode.AsString(value) is { } text => PyText.CodePointStrings(text).Select(c => (JsonNode?)JsonValue.Create((string)c!)).ToArray(),
            _ => throw new MalformedModelResponseException("'" + PyNode.TypeName(value) + "' object is not iterable"),
        };
    }
}

/// <summary>
/// The model answered with a shape Python's engine fails on (an <c>AttributeError</c> or <c>TypeError</c> while
/// reading <c>output</c>); the turn fails, as in Python.
/// </summary>
public sealed class MalformedModelResponseException : InvalidOperationException
{
    public MalformedModelResponseException(string message) : base(message)
    {
    }

    public MalformedModelResponseException()
    {
    }

    public MalformedModelResponseException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

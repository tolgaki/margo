using System.Text.Json.Nodes;
using Margo.Cloud.Guard;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.Engine;

/// <summary>The office queue as the engine sees it (Python passes an <c>OfficeReader</c> with <c>pending()</c>).</summary>
public interface IOfficeRequests
{
    /// <summary><c>pending()</c>: the unreviewed messages other people sent Margo, as JSON: a list of request objects
    /// (Python's shape), or an object holding that list as <c>requests</c> plus counts (<c>total</c>, <c>shown</c>, ...),
    /// which the engine passes on beside the list.</summary>
    Task<JsonNode?> PendingAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Asking the manager to approve one proposed action on a card in Teams (no Python equivalent). The engine offers the
/// <c>request_approval</c> tool only when a turn has one; nothing the model does with it sends anything.
/// </summary>
public interface IApprovalRequests
{
    /// <summary>Checks the proposed action (by its ledger ID) and, when it can be run as proposed, queues a card for the
    /// manager. Returns the tool's answer for the model; a refusal is an answer, not an exception.</summary>
    Task<JsonObject> RequestAsync(string actionId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Office documents the manager asks for (no Python equivalent): created in Margo's own OneDrive and shared with the
/// manager alone. The engine offers <c>create_document</c> only when a turn has one.
/// </summary>
public interface IDocuments
{
    /// <summary>Creates one document from a complete outline and shares it with the manager. Returns the tool's answer;
    /// a failure is an answer, not an exception. Never retried.</summary>
    Task<JsonObject> CreateAsync(string app, string outline, string? request, CancellationToken cancellationToken = default);
}

/// <summary>Token use of one turn, summed over its model requests (the Responses API's <c>usage</c>). No content.</summary>
/// <param name="InputTokens">Input tokens of every request of the turn.</param>
/// <param name="CachedInputTokens">The part of them served from the provider's prompt cache.</param>
/// <param name="OutputTokens">Output tokens, reasoning included.</param>
/// <param name="ReasoningTokens">The reasoning part of the output tokens.</param>
/// <param name="Requests">Model requests made.</param>
/// <param name="ToolCalls">Function calls dispatched.</param>
/// <param name="LastInputTokens">Input tokens of the turn's last request: the size the conversation has reached.</param>
public sealed record EngineUsage(long InputTokens = 0, long CachedInputTokens = 0, long OutputTokens = 0, long ReasoningTokens = 0,
                                 int Requests = 0, int ToolCalls = 0, long LastInputTokens = 0);

/// <summary>What one turn returns (<c>{"text", "response_id", "rounds"}</c>, plus <c>"stopped": true</c>).</summary>
/// <param name="Text">The model's final text, or the <see cref="EngineTools.Stopped"/> message.</param>
/// <param name="ResponseId">The final response's ID (for the record only: responses are not stored by the service, so it
/// cannot continue a conversation); null when stopped or when the response had no string ID.</param>
/// <param name="Rounds">Tool rounds used (one more than the limit when stopped).</param>
/// <param name="Stopped">The round limit was reached before the model finished.</param>
public sealed record EngineResult(string Text, string? ResponseId, int Rounds, bool Stopped = false)
{
    /// <summary>
    /// The conversation after this turn, within the engine's budget (<see cref="ConversationHistory.Trim"/>): pass it
    /// as <c>history</c> to continue. The caller keeps it (in memory); nothing is kept by the model service.
    /// </summary>
    public JsonArray History { get; init; } = [];

    /// <summary>Token use of the turn.</summary>
    public EngineUsage Usage { get; init; } = new();

    /// <summary>The model's last response was cut off (<c>status: incomplete</c>, e.g. the output token ceiling).</summary>
    public bool Incomplete { get; init; }

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

/// <summary>Request controls and budgets of an <see cref="Engine"/>; the defaults suit a small reasoning model.</summary>
public sealed record EngineOptions
{
    /// <summary>
    /// <c>reasoning.effort</c>. Null or empty for a deployment that is not a reasoning model: then neither
    /// <c>reasoning</c> nor <c>include: ["reasoning.encrypted_content"]</c> is sent.
    /// </summary>
    public string? ReasoningEffort { get; init; } = "low";

    /// <summary><c>max_output_tokens</c> (reasoning included): a ceiling against runaway output; null or below 1 sends none.</summary>
    public int? MaxOutputTokens { get; init; } = 16000;

    /// <summary>The most code points of one tool result shown to the model (at least <see cref="EngineTools.MinOutputChars"/>).</summary>
    public int MaxToolOutputChars { get; init; } = EngineTools.DefaultMaxOutputChars;

    /// <summary>How many consecutive Work IQ reads of one model response run at once; 1 runs every call in turn.</summary>
    public int MaxParallelReads { get; init; } = 4;

    /// <summary>The most characters (of JSON text) of conversation carried into the next turn; older whole turns are dropped.</summary>
    public int HistoryMaxChars { get; init; } = 200_000;

    /// <summary>When a turn's last request used more input tokens than this, the older half of the conversation is dropped.</summary>
    public long HistoryMaxInputTokens { get; init; } = 100_000;

    /// <summary>Mark observed tool results as untrusted data (<see cref="EngineTools.ModelOutput"/>).</summary>
    public bool MarkObservedData { get; init; } = true;
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
/// <para>Nothing is kept by the model service: every request says <c>store: false</c> and carries the conversation
/// itself (the manager's messages, the model's output items and the function results), which the caller holds in
/// memory between turns. For a reasoning model the encrypted reasoning items are requested and sent back, so the
/// model keeps its reasoning between the rounds of a turn.</para>
/// </remarks>
public sealed class Engine
{
    /// <param name="model">The Responses API client.</param>
    /// <param name="deployment">The model deployment name sent as <c>model</c>.</param>
    /// <param name="repoRoot">The root holding <c>agents/</c> and <c>skills/</c>.</param>
    /// <param name="preferencesPath">The default private <c>preferences.md</c>.</param>
    /// <param name="timeZone">The IANA time zone the model states times in.</param>
    /// <param name="maxRounds">The most tool rounds in one turn.</param>
    /// <param name="options">Request controls and budgets; null for the defaults.</param>
    public Engine(IModelClient model, string deployment, string repoRoot, string? preferencesPath = null,
                  string timeZone = EngineTools.DefaultTimeZone, int maxRounds = 12, EngineOptions? options = null)
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
        Options = options ?? new EngineOptions();
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

    /// <summary>Request controls and budgets.</summary>
    public EngineOptions Options { get; }

    /// <summary>
    /// <c>run(text, *, mode, guard, office, state, previous_response_id, now, preferences_path)</c>, with the
    /// conversation carried by the caller (<paramref name="history"/>) instead of <c>previous_response_id</c>.
    /// </summary>
    /// <param name="text">The manager's message.</param>
    /// <param name="mode">One of <see cref="RunModes"/>.</param>
    /// <param name="guard">The only path to Work IQ.</param>
    /// <param name="office">The office queue (attended runs only), or null.</param>
    /// <param name="state">This instance's <c>margo-state</c> tool, or null where Margo's state is not reachable.</param>
    /// <param name="history">An earlier turn's <see cref="EngineResult.History"/> to continue from; null or empty starts
    /// a conversation. Not changed by this call.</param>
    /// <param name="now">The time shown to the model; null for the current time.</param>
    /// <param name="preferencesPath">This turn's preferences file; null for <see cref="PreferencesPath"/>.</param>
    /// <param name="cacheKey">Sent as <c>prompt_cache_key</c> so one instance's requests share a prompt cache; an opaque
    /// key (never a name or address); null or empty sends none.</param>
    /// <param name="cancellationToken">Cancels the turn.</param>
    /// <exception cref="MalformedModelResponseException">The model answered with a shape Python fails on.</exception>
    public async Task<EngineResult> RunAsync(string text, string? mode, WorkIQGuard guard, IOfficeRequests? office = null,
                                             IStateCommands? state = null, JsonArray? history = null,
                                             DateTimeOffset? now = null, string? preferencesPath = null, string? cacheKey = null,
                                             IApprovalRequests? approvals = null, IDocuments? documents = null,
                                             CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(guard);
        var library = new SkillLibrary(RepoRoot, preferencesPath ?? PreferencesPath);
        approvals = mode == RunModes.Attended ? approvals : null;
        documents = mode == RunModes.Attended ? documents : null;
        var instructions = EngineTools.StableInstructions(RepoRoot, TimeZone, library, guard.PrincipalAccess, approvals is not null, documents is not null);
        // Work IQ describes its own tools (tools/list); the guard limits them to what it allows.
        var tools = EngineTools.FunctionTools(mode, await guard.ServerToolsAsync(cancellationToken).ConfigureAwait(false));
        if (approvals is not null)
        {
            tools.Add(EngineTools.ApprovalToolDefinition());
        }

        if (documents is not null)
        {
            tools.Add(EngineTools.DocumentToolDefinition());
        }
        var earlier = history is null ? [] : (JsonArray)PyNode.Copy(history)!;
        var message = new JsonObject { ["role"] = "user", ["content"] = EngineTools.TimeLine(TimeZone, now) + "\n\n" + text };
        var items = (JsonArray)PyNode.Copy(earlier)!;
        items.Add(PyNode.Copy(message));

        var usage = new EngineUsage();
        var response = AsDict(await Model.CreateResponseAsync(Request(instructions, items, tools, cacheKey), cancellationToken).ConfigureAwait(false));
        var rounds = 0;
        while (true)
        {
            usage = Count(usage, response);
            var output = Items(response, "output");
            var calls = output.Where(item => PyNode.Is(Get(item, "type"), "function_call")).ToList();
            if (calls.Count == 0)
            {
                foreach (var item in output)
                {
                    items.Add(PyNode.Copy(item));
                }

                var answer = OutputText(response);
                var incomplete = PyNode.Is(Get(response, "status"), "incomplete");
                if (incomplete && answer.Length == 0)
                {
                    answer = EngineTools.Incomplete;
                }

                return new EngineResult(answer, PyNode.AsString(Get(response, "id")), rounds)
                {
                    History = ConversationHistory.Trim(items, Options.HistoryMaxChars, usage.LastInputTokens > Options.HistoryMaxInputTokens),
                    Usage = usage,
                    Incomplete = incomplete,
                };
            }

            rounds++;
            if (rounds > MaxRounds)
            {
                // The unanswered function calls are not kept: the turn is remembered as the question and this notice.
                var stopped = EngineTools.Stopped.Replace("{rounds}", MaxRounds.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
                earlier.Add(message);
                earlier.Add(new JsonObject { ["role"] = "assistant", ["content"] = stopped });
                return new EngineResult(stopped, null, rounds, Stopped: true)
                {
                    History = ConversationHistory.Trim(earlier, Options.HistoryMaxChars, usage.LastInputTokens > Options.HistoryMaxInputTokens),
                    Usage = usage,
                };
            }

            // Every output item goes back as it came (reasoning items before their function calls), then the results.
            foreach (var item in output)
            {
                items.Add(PyNode.Copy(item));
            }

            var results = await DispatchAllAsync(calls, mode, guard, office, library, state, approvals, documents, cancellationToken).ConfigureAwait(false);
            usage = usage with { ToolCalls = usage.ToolCalls + calls.Count };
            for (var index = 0; index < calls.Count; index++)
            {
                var function = PyNode.AsString(Get(calls[index], "name"));
                items.Add(new JsonObject
                {
                    ["type"] = "function_call_output",
                    ["call_id"] = PyNode.Copy(Get(calls[index], "call_id")),
                    ["output"] = Options.MarkObservedData ? EngineTools.ModelOutput(function, results[index], Options.MaxToolOutputChars)
                        : EngineTools.Clip(results[index], Options.MaxToolOutputChars),
                });
            }

            response = AsDict(await Model.CreateResponseAsync(Request(instructions, items, tools, cacheKey), cancellationToken).ConfigureAwait(false));
        }
    }

    /// <summary>
    /// One request: the stable part first (<c>model</c>, <c>instructions</c>, the conversation so far, <c>tools</c>),
    /// then the controls. <c>store</c> is always false.
    /// </summary>
    private JsonObject Request(string instructions, JsonArray items, JsonArray tools, string? cacheKey)
    {
        var request = new JsonObject
        {
            ["model"] = Deployment,
            ["instructions"] = instructions,
            ["input"] = PyNode.Copy(items),
            ["tools"] = PyNode.Copy(tools),
            ["store"] = false,
        };
        if (!string.IsNullOrEmpty(Options.ReasoningEffort))
        {
            request["include"] = new JsonArray("reasoning.encrypted_content");
            request["reasoning"] = new JsonObject { ["effort"] = Options.ReasoningEffort };
        }

        if (Options.MaxOutputTokens is { } ceiling and > 0)
        {
            request["max_output_tokens"] = ceiling;
        }

        if (!string.IsNullOrEmpty(cacheKey))
        {
            request["prompt_cache_key"] = cacheKey;
        }

        return request;
    }

    /// <summary>
    /// Runs one response's function calls and returns their results in the calls' order. Consecutive Work IQ reads run
    /// side by side (at most <see cref="EngineOptions.MaxParallelReads"/>; the guard, its budget and the transport are
    /// safe to call concurrently); every other call, <c>margo-state</c> above all, runs alone, in order, so a read
    /// never overlaps a state change the model asked for before or after it.
    /// </summary>
    private async Task<JsonNode?[]> DispatchAllAsync(List<JsonNode?> calls, string? mode, WorkIQGuard guard, IOfficeRequests? office,
                                                     SkillLibrary library, IStateCommands? state, IApprovalRequests? approvals,
                                                     IDocuments? documents, CancellationToken cancellationToken)
    {
        var results = new JsonNode?[calls.Count];
        bool Read(int at) => PyNode.AsString(Get(calls[at], "name"))?.StartsWith(EngineTools.Prefix, StringComparison.Ordinal) == true;
        Task<JsonNode?> One(int at) =>
            DispatchAsync(Get(calls[at], "name"), Get(calls[at], "arguments"), mode, guard, office, library, state, approvals, documents, cancellationToken);

        var index = 0;
        while (index < calls.Count)
        {
            var end = index;
            while (Options.MaxParallelReads > 1 && end < calls.Count && Read(end))
            {
                end++;
            }

            if (end - index < 2)
            {
                results[index] = await One(index).ConfigureAwait(false);
                index++;
                continue;
            }

            using var slots = new SemaphoreSlim(Options.MaxParallelReads);
            async Task RunAsync(int at)
            {
                await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    results[at] = await One(at).ConfigureAwait(false);
                }
                finally
                {
                    slots.Release();
                }
            }

            await Task.WhenAll(Enumerable.Range(index, end - index).Select(RunAsync)).ConfigureAwait(false);
            index = end;
        }

        return results;
    }

    /// <summary>Adds one response's <c>usage</c> to the turn's. Counts only; a response without usage adds a request.</summary>
    private static EngineUsage Count(EngineUsage usage, JsonObject response)
    {
        var reported = response["usage"] as JsonObject;
        var input = Number(reported?["input_tokens"]);
        return usage with
        {
            InputTokens = usage.InputTokens + input,
            CachedInputTokens = usage.CachedInputTokens + Number((reported?["input_tokens_details"] as JsonObject)?["cached_tokens"]),
            OutputTokens = usage.OutputTokens + Number(reported?["output_tokens"]),
            ReasoningTokens = usage.ReasoningTokens + Number((reported?["output_tokens_details"] as JsonObject)?["reasoning_tokens"]),
            Requests = usage.Requests + 1,
            LastInputTokens = input,
        };
    }

    private static long Number(JsonNode? value) =>
        value is JsonValue && long.TryParse(value.ToJsonString(), System.Globalization.NumberStyles.None,
                                            System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : 0;

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
    /// <param name="approvals">The approval cards of an attended chat turn, or null.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public static async Task<JsonNode?> DispatchAsync(JsonNode? name, JsonNode? rawArguments, string? mode, WorkIQGuard guard,
                                                      IOfficeRequests? office, SkillLibrary library, IStateCommands? state = null,
                                                      IApprovalRequests? approvals = null, IDocuments? documents = null,
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

        if (function == EngineTools.DocumentTool && mode == RunModes.Attended && documents is not null)
        {
            var app = PyNode.AsString(PyNode.Get(given, "app"));
            var outline = PyNode.AsString(PyNode.Get(given, "outline"));
            if (app is null || !WorkIQGuard.OfficeApps.Contains(app) || string.IsNullOrWhiteSpace(outline))
            {
                return Refused("create_document needs app (Word, Excel or PowerPoint) and the complete outline");
            }

            return await documents.CreateAsync(app, outline, PyNode.AsString(PyNode.Get(given, "request")), cancellationToken).ConfigureAwait(false);
        }

        if (function == EngineTools.ApprovalTool && mode == RunModes.Attended && approvals is not null)
        {
            if (given is not JsonObject request || PyNode.AsString(PyNode.Get(request, "action_id")) is not { Length: > 0 } actionId)
            {
                return Refused("request_approval needs the action_id that work_state.py propose returned");
            }

            return await approvals.RequestAsync(actionId, cancellationToken).ConfigureAwait(false);
        }

        if (function == EngineTools.OfficeTool && mode == RunModes.Attended && office is not null)
        {
            try
            {
                var pending = await office.PendingAsync(cancellationToken).ConfigureAwait(false);
                var answer = new JsonObject { ["status"] = "ok" };
                if (pending is JsonObject page && page["requests"] is JsonArray)
                {
                    // A page: the requests shown, then its counts (total, shown, ...).
                    foreach (var (key, value) in page)
                    {
                        if (key != "status")
                        {
                            answer[key] = PyNode.Copy(value);
                        }
                    }
                }
                else
                {
                    answer["requests"] = PyNode.Copy(pending);
                }

                return answer;
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

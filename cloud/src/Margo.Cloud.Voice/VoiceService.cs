using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Margo.Cloud.Core;
using Margo.Cloud.Engine;
using Margo.Cloud.Guard;
using Margo.Cloud.Orchestrator;
using Margo.Cloud.State.Core;
using Margo.Cloud.Voice.Jwt;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Margo.Cloud.Voice;

/// <summary>
/// The two tokens a voice session needs, which the Host acquires with the Microsoft 365 Agents SDK (Python:
/// <c>connections.get_default_connection()</c>).
/// </summary>
public interface IVoiceTokens
{
    /// <summary><c>connection.get_agentic_user_token(tenant_id, agent_identity_id, agent_user_id, scopes)</c>: Margo's
    /// own token for this instance. Null or empty means none ("no agent token").</summary>
    Task<string?> GetAgenticUserTokenAsync(string tenantId, string agentIdentityId, string agentUserId, IReadOnlyList<string> scopes,
                                           CancellationToken cancellationToken);

    /// <summary><c>connection.acquire_token_on_behalf_of(scopes, user_token)</c>: the manager's Work IQ token on behalf
    /// of their voice sign-in. Asked only when principal access is <c>obo</c>.</summary>
    Task<string?> AcquireTokenOnBehalfOfAsync(IReadOnlyList<string> scopes, string userToken, CancellationToken cancellationToken);
}

/// <summary>Optional settings of <see cref="VoiceService"/>; null members take the Python defaults.</summary>
public sealed record VoiceServiceOptions
{
    /// <summary>The blueprint's delegated voice scope (Python <c>scope="Voice.Talk"</c>; the Host passes
    /// <c>MARGO_VOICE_SCOPE</c>).</summary>
    public string Scope { get; init; } = "Voice.Talk";

    /// <summary>The voice settings; null reads them from the environment (<see cref="VoiceSettings.FromEnv"/>).</summary>
    public VoiceSettings? Settings { get; init; }

    /// <summary>The Voice Live endpoint; null or empty reads <c>VOICE_LIVE_ENDPOINT</c>.</summary>
    public string? Endpoint { get; init; }

    /// <summary>Opens Voice Live sessions; null for <see cref="VoiceLiveConnector"/>.</summary>
    public IVoiceLiveConnector? VoiceLive { get; init; }

    /// <summary>Reads the tenant's signing keys; null for <see cref="HttpJwksSource"/>.</summary>
    public IJwksSource? Keys { get; init; }

    /// <summary>The <c>margo.voice</c> logger; null logs nothing.</summary>
    public ILogger? Logger { get; init; }

    /// <summary>The clock (key cache, claim checks, the session's time limit); null for the system clock.</summary>
    public TimeProvider? Time { get; init; }
}

/// <summary>
/// <c>VoiceService</c> (<c>voice/server.py</c>): the voice endpoint inside one instance container: the page, its
/// sign-in config and the session.
/// </summary>
/// <remarks>
/// Order, each step failing closed:
/// <list type="number">
/// <item>The page signs the person in with Entra (authorization code + PKCE) for the blueprint's delegated voice scope
/// and sends the token as its first WebSocket message.</item>
/// <item>The token's signature, issuer and expiry are verified against the tenant's published keys, then its tenant,
/// audience, scope and caller (<see cref="VoiceClaims.Check"/>).</item>
/// <item>Margo's own token is obtained for this instance, and her manager is looked up in the directory exactly as in a
/// Teams turn. Only the manager may talk to her.</item>
/// <item>The manager's token for Work IQ is obtained on behalf of the person's sign-in.</item>
/// <item>A Voice Live session starts with Margo's instructions and tools; every tool call runs through the engine's
/// dispatcher, the guard and, for state, the state lease.</item>
/// </list>
/// No audio or transcript is stored, and tokens, audio and text are never logged.
/// </remarks>
public sealed class VoiceService : IDisposable
{
    /// <summary><c>WORKIQ_SCOPE</c>.</summary>
    public const string WorkIQScope = WorkIQScopes.DefaultScope;

    /// <summary><c>VOICE_CALL_BUDGET</c>: Work IQ calls per voice session.</summary>
    public const int VoiceCallBudget = 120;

    /// <summary><c>VOICE_STATE_BUDGET</c>: <c>margo-state</c> calls per voice session.</summary>
    public const int VoiceStateBudget = 60;

    /// <summary><c>MAX_FRAME</c>: the largest message the page may send (aiohttp refuses one that reaches it).</summary>
    public const int MaxFrame = 256 * 1024;

    /// <summary><c>HELLO_SECONDS</c>: how long the first message may take.</summary>
    public static readonly TimeSpan HelloSeconds = TimeSpan.FromSeconds(15);

    /// <summary>The page socket's <c>heartbeat</c>: a ping every 20 seconds, closed after 10 more without a pong.</summary>
    public static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(20);

    /// <summary>Sent to the page when the sign-in cannot be used.</summary>
    public const string SignInRefused = "Your sign-in can't be used to talk to this Margo.";

    /// <summary>Sent to the page when anything else fails.</summary>
    public const string SessionFailed = "Something failed on my side, so the call ended. Nothing was sent.";

    private readonly Margo.Cloud.Orchestrator.Orchestrator _orchestrator;
    private readonly IVoiceTokens _tokens;
    private readonly IVoiceLiveConnector _voiceLive;
    private readonly EntraTokenVerifier _verifier;
    private readonly HttpJwksSource? _ownedKeys;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, bool> _initialised = new(StringComparer.Ordinal);

    /// <param name="orchestrator">The instance's orchestrator: its engine, Work IQ transport, state root, state sync,
    /// recorders, principal access mode and <c>margo-state</c> command.</param>
    /// <param name="tokens">Margo's agentic token and the on-behalf-of exchange.</param>
    /// <param name="instance">This container's instance.</param>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="clientId">The blueprint's application ID.</param>
    /// <param name="options">Optional settings.</param>
    /// <exception cref="InvalidOperationException">No Voice Live endpoint ("VOICE_LIVE_ENDPOINT is required for voice"):
    /// fail at start-up, not mid-call.</exception>
    /// <exception cref="PyValueError">No settings were given and the environment's are missing or invalid.</exception>
    public VoiceService(Margo.Cloud.Orchestrator.Orchestrator orchestrator, IVoiceTokens tokens, AgentInstance instance, string tenantId,
                        string clientId, VoiceServiceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(clientId);
        options ??= new VoiceServiceOptions();
        var endpoint = string.IsNullOrEmpty(options.Endpoint) ? PyRuntime.GetEnv("VOICE_LIVE_ENDPOINT") : options.Endpoint;
        if (string.IsNullOrEmpty(endpoint))
        {
            throw new InvalidOperationException("VOICE_LIVE_ENDPOINT is required for voice");
        }

        Endpoint = endpoint;
        _orchestrator = orchestrator;
        _tokens = tokens;
        Instance = instance;
        TenantId = tenantId;
        ClientId = clientId;
        Scope = options.Scope ?? throw new ArgumentException("the voice scope is required", nameof(options));
        Settings = options.Settings ?? VoiceSettings.FromEnv();
        _voiceLive = options.VoiceLive ?? new VoiceLiveConnector();
        _logger = options.Logger ?? NullLogger.Instance;
        _time = options.Time ?? TimeProvider.System;
        IJwksSource keys;
        if (options.Keys is { } given)
        {
            keys = given;
        }
        else
        {
            _ownedKeys = new HttpJwksSource();
            keys = _ownedKeys;
        }

        _verifier = new EntraTokenVerifier(tenantId, clientId, Scope, keys, _time);
    }

    /// <summary>The Voice Live endpoint.</summary>
    public string Endpoint { get; }

    /// <summary>The voice settings.</summary>
    public VoiceSettings Settings { get; }

    /// <summary>This container's instance.</summary>
    public AgentInstance Instance { get; }

    /// <summary>The tenant.</summary>
    public string TenantId { get; }

    /// <summary>The blueprint's application ID.</summary>
    public string ClientId { get; }

    /// <summary>The delegated voice scope.</summary>
    public string Scope { get; }

    /// <summary>Tests only: <see cref="HelloSeconds"/>.</summary>
    internal TimeSpan HelloTimeout { get; set; } = HelloSeconds;

    /// <summary><c>config</c>: what the page needs to sign the person in.</summary>
    public JsonObject Config() => new()
    {
        ["tenantId"] = TenantId,
        ["clientId"] = ClientId,
        ["scope"] = "api://" + ClientId + "/" + Scope,
    };

    public void Dispose()
    {
        _verifier.Dispose();
        _ownedKeys?.Dispose();
    }

    /// <summary><c>socket</c> after the handshake: the session, the fatal message if it fails, and the close.</summary>
    internal async Task RunAsync(MessageSocket ws, CancellationToken aborted)
    {
        try
        {
            await SessionAsync(ws, aborted).ConfigureAwait(false);
        }
        catch (TokenRejectedException exc)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("voice sign-in refused: {Reason}", exc.Message);
            }

            await FatalAsync(ws, SignInRefused).ConfigureAwait(false);
        }
        catch (Exception exc) when (!(exc is OperationCanceledException && aborted.IsCancellationRequested))
        {
            _logger.LogError(exc, "voice session failed");
            await FatalAsync(ws, SessionFailed).ConfigureAwait(false);
        }
        finally
        {
            await ws.CloseAsync().ConfigureAwait(false);
        }
    }

    private static async Task FatalAsync(MessageSocket ws, string message)
    {
        if (ws.Closed)
        {
            return;
        }

        try
        {
            await SendJsonAsync(ws, new JsonObject { ["type"] = "error", ["fatal"] = true, ["message"] = message }, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exc) when (exc is InvalidOperationException or System.Net.WebSockets.WebSocketException or IOException)
        {
            // The page went away while the message was being sent; the socket is closed next.
        }
    }

    /// <summary><c>ws.send_json(message)</c>: <c>json.dumps</c> text.</summary>
    private static Task SendJsonAsync(MessageSocket ws, JsonObject message, CancellationToken cancellationToken) =>
        ws.SendTextAsync(PyJson.Dumps(message), cancellationToken);

    private async Task SessionAsync(MessageSocket ws, CancellationToken cancellationToken)
    {
        var hello = await ws.ReceiveAsync(cancellationToken).WaitAsync(HelloTimeout, _time, cancellationToken).ConfigureAwait(false);
        var data = hello.Kind == SocketMessageKind.Text ? PyJson.Loads(hello.Text!) : new JsonObject();
        var token = data is JsonObject values && PyNode.Is(PyNode.Get(values, "type"), "hello") ? PyNode.Get(values, "token") : null;
        if (PyNode.AsString(token) is not { Length: > 0 } userToken)
        {
            throw new TokenRejectedException("no token");
        }

        var caller = await _verifier.VerifyAsync(userToken, cancellationToken).ConfigureAwait(false);
        var (margoToken, principalToken) = await TokensAsync(userToken, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(margoToken))
        {
            throw new InvalidOperationException("no agent token");
        }

        var manager = await LookUpManagerAsync(margoToken, cancellationToken).ConfigureAwait(false);
        if (manager is null || manager.Id != caller)
        {
            throw new TokenRejectedException("caller is not this instance's manager");
        }

        var binding = new InstanceBinding(Instance.AgentIdentityId, Instance.AgentUserId, manager.Id);
        var (sync, preferences) = _orchestrator.StateSyncFor(binding);
        var root = _orchestrator.StateRoot;
        var options = _orchestrator.Options;
        var state = new StateCommands(binding.Account, root, preferences, new CallBudget(VoiceStateBudget), options.StateRecorder,
                                      cliCommand: options.StateCliCommand);

        async Task<JsonNode?> RunLocked(Func<CancellationToken, Task<JsonNode?>> work, CancellationToken token) =>
            (await sync.RunAsync(work, cancellationToken: token).ConfigureAwait(false)).Result;

        if (!_initialised.ContainsKey(binding.Account))
        {
            await RunLocked(async token =>
            {
                await Task.Run(() => binding.OpenLedger(root).Dispose(), token).ConfigureAwait(false);
                await state.InitialiseAsync(token).ConfigureAwait(false);
                return null;
            }, cancellationToken).ConfigureAwait(false);
            _initialised.TryAdd(binding.Account, true);
        }

        var tokens = new Dictionary<string, string?>(StringComparer.Ordinal) { [Subjects.Margo] = margoToken };
        if (!string.IsNullOrEmpty(principalToken))
        {
            tokens[Subjects.Principal] = principalToken;
        }

        var guard = new WorkIQGuard(binding, new TurnTokens(tokens), _orchestrator.Transport, recorder: options.GuardRecorder,
                                    budget: new CallBudget(VoiceCallBudget), principalAccess: options.PrincipalAccess);
        var engine = _orchestrator.Engine;
        var library = new SkillLibrary(engine.RepoRoot, preferences);
        var office = new OfficeReader(() => binding.OpenLedger(root));
        var instructions = EngineTools.LoadInstructions(engine.RepoRoot, engine.TimeZone, library: library) + "\n\n" + VoiceSession.Addendum;

        Task<JsonNode?> Dispatch(JsonNode? name, JsonNode? rawArguments, CancellationToken token) =>
            Margo.Cloud.Engine.Engine.DispatchAsync(name, rawArguments, RunModes.Attended, guard, office, library, state, token);

        await ConverseAsync(ws, instructions, EngineTools.FunctionTools(RunModes.Attended), Dispatch, RunLocked, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary><c>_tokens(user_token)</c>: Margo's own token, and the manager's on behalf of the sign-in under <c>obo</c>.</summary>
    private async Task<(string? Margo, string? Principal)> TokensAsync(string userToken, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> scopes = [WorkIQScope];
        var margo = await _tokens.GetAgenticUserTokenAsync(TenantId, Instance.AgentIdentityId, Instance.AgentUserId, scopes, cancellationToken)
            .ConfigureAwait(false);
        string? principal = null;
        if (_orchestrator.Options.PrincipalAccess == PrincipalAccessModes.Obo)
        {
            principal = await _tokens.AcquireTokenOnBehalfOfAsync(scopes, userToken, cancellationToken).ConfigureAwait(false);
        }

        return (margo, principal);
    }

    /// <summary>The orchestrator's <c>_manager(event, margo_token)</c>: Margo's own token through the guard (budget 1,
    /// unattended), exactly as in a Teams turn. Throws <see cref="ManagerLookupFailedException"/> when the lookup fails.</summary>
    private async Task<Manager?> LookUpManagerAsync(string margoToken, CancellationToken cancellationToken)
    {
        var bootstrap = new InstanceBinding(Instance.AgentIdentityId, Instance.AgentUserId, Margo.Cloud.Orchestrator.Orchestrator.UnboundManager);
        var guard = new WorkIQGuard(bootstrap, new TurnTokens(new Dictionary<string, string?> { [Subjects.Margo] = margoToken }),
                                    _orchestrator.Transport, recorder: _orchestrator.Options.GuardRecorder, budget: new CallBudget(1));
        var arguments = new JsonObject { ["on_data_of"] = Subjects.Self, ["entityUrls"] = new JsonArray(ManagerDirectory.ManagerUrl) };
        var result = await guard.CallAsync("fetch", arguments, RunModes.Unattended, cancellationToken).ConfigureAwait(false);
        return ManagerDirectory.From(result);
    }

    /// <summary><c>_converse</c>: the Voice Live session, both directions, until either side ends, the person stops, or
    /// the time limit; then <c>closed</c> with the reason.</summary>
    private async Task ConverseAsync(MessageSocket ws, string instructions, JsonArray tools,
                                     Func<JsonNode?, JsonNode?, CancellationToken, Task<JsonNode?>> dispatch,
                                     Func<Func<CancellationToken, Task<JsonNode?>>, CancellationToken, Task<JsonNode?>> runLocked,
                                     CancellationToken cancellationToken)
    {
        var connection = await _voiceLive.ConnectAsync(Endpoint, Settings.Model, cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var bridge = new VoiceBridge(dispatch, connection.SendAsync, (message, token) => SendJsonAsync(ws, message, token), Settings,
                                         EngineTools.Clip, runLocked, () => _time.GetTimestamp() / (double)_time.TimestampFrequency);
            await bridge.StartAsync(instructions, tools, cancellationToken).ConfigureAwait(false);

            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            async Task FromService()
            {
                while (await connection.ReceiveAsync(stop.Token).ConfigureAwait(false) is { } serviceEvent)
                {
                    await bridge.FromServiceAsync(serviceEvent, stop.Token).ConfigureAwait(false);
                }
            }

            async Task FromClient()
            {
                while (true)
                {
                    var message = await ws.ReceiveAsync(stop.Token).ConfigureAwait(false);
                    if (message.Kind != SocketMessageKind.Text)
                    {
                        return;
                    }

                    JsonNode? payload;
                    try
                    {
                        payload = PyJson.Loads(message.Text!);
                    }
                    catch (PyValueError)
                    {
                        continue;
                    }

                    if (await bridge.FromClientAsync(payload, stop.Token).ConfigureAwait(false) == "stop")
                    {
                        return;
                    }
                }
            }

            var tasks = new[] { Task.Run(FromService, CancellationToken.None), Task.Run(FromClient, CancellationToken.None) };
            var limit = Task.Delay(Limit(Settings.MaxSeconds), _time, stop.Token);
            try
            {
                await Task.WhenAny(tasks[0], tasks[1], limit).ConfigureAwait(false);
            }
            finally
            {
                await stop.CancelAsync().ConfigureAwait(false);
                foreach (var task in tasks.Append(limit))
                {
                    // Python cancels both and never reads their outcome: an error in either ends the call quietly.
                    _ = task.ContinueWith(static finished => _ = finished.Exception, CancellationToken.None,
                                          TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }

            var reason = bridge.Expired() ? "time limit reached" : "call ended";
            if (!ws.Closed)
            {
                await SendJsonAsync(ws, new JsonObject { ["type"] = "closed", ["reason"] = reason }, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary><c>asyncio.wait(timeout=max_seconds)</c>: zero or less returns at once; the longest .NET timer is about
    /// 49 days.</summary>
    private static TimeSpan Limit(long seconds) =>
        seconds <= 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(Math.Min(seconds * 1000.0, uint.MaxValue - 1.0));
}

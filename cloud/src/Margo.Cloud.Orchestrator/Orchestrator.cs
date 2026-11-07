using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Margo.Cloud.Core;
using Margo.Cloud.Engine;
using Margo.Cloud.Guard;
using Margo.Cloud.State.Core;
using Margo.Cloud.WorkIQ;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Margo.Cloud.Orchestrator;

/// <summary>
/// One inbound activity for a hired Margo instance (<c>orchestrator.py</c>'s <c>event</c> dict). The host builds it from
/// the authenticated activity; nothing in it decides who Margo works for.
/// </summary>
public sealed record InboundEvent
{
    /// <summary>The agent identity (blueprint instance) the activity was addressed to.</summary>
    public string? AgentIdentityId { get; init; }

    /// <summary>Margo's own agent user.</summary>
    public string? AgentUserId { get; init; }

    /// <summary>The authenticated sender's directory object ID.</summary>
    public string? SenderId { get; init; }

    /// <summary>The sender's display name (shown to the manager; never trusted).</summary>
    public string? SenderName { get; init; }

    /// <summary>The activity's conversation ID (not the Graph chat ID).</summary>
    public string? ConversationId { get; init; }

    /// <summary>Teams' <c>conversationType</c>: <c>personal</c> for a one-to-one chat.</summary>
    public string? ConversationType { get; init; }

    /// <summary>The activity's (or mail message's) stable ID.</summary>
    public string? ActivityId { get; init; }

    /// <summary><c>teams</c>, <c>mail</c>, ...</summary>
    public string? Channel { get; init; }

    /// <summary>The message text: data, never instructions, unless the sender is the manager in their one-to-one chat.</summary>
    public string? Text { get; init; }
}

/// <summary>What <see cref="Orchestrator.HandleAsync"/> returns (<c>{"reply": str|None, "status": str}</c>).</summary>
/// <param name="Reply">The text to send back on the activity's channel, or null for none.</param>
/// <param name="Status">One of <see cref="TurnStatuses"/>.</param>
public sealed record TurnReply(string? Reply, string Status)
{
    /// <summary>Python's dict: <c>{"reply", "status"}</c>.</summary>
    public JsonObject ToJsonObject() => new() { ["reply"] = Reply, ["status"] = Status };
}

/// <summary>Every status <see cref="Orchestrator.HandleAsync"/> reports.</summary>
public static class TurnStatuses
{
    /// <summary>Margo's own token was not available; nothing was read.</summary>
    public const string NoAgentToken = "no_agent_token";

    /// <summary>The activity was for another instance than the one this container serves.</summary>
    public const string WrongInstance = "wrong_instance";

    /// <summary>The identities in the activity, or the manager found, do not make a valid binding.</summary>
    public const string InvalidBinding = "invalid_binding";

    /// <summary>The directory lookup failed; that is not "no manager".</summary>
    public const string ManagerLookupFailed = "manager_lookup_failed";

    /// <summary>The directory says Margo has no manager.</summary>
    public const string NoManager = "no_manager";

    /// <summary>The manager wrote outside their one-to-one chat.</summary>
    public const string GroupRefused = "group_refused";

    /// <summary>The state could not be loaded or saved safely.</summary>
    public const string StateUnsafe = "state_unsafe";

    /// <summary>The turn failed.</summary>
    public const string Error = "error";

    /// <summary>The model answered the manager.</summary>
    public const string Answered = "answered";

    /// <summary>The model's round limit was reached.</summary>
    public const string Stopped = "stopped";

    /// <summary><c>/voice</c>: the voice link, or that voice is not set up.</summary>
    public const string VoiceLink = "voice_link";

    /// <summary><c>/replies MODE</c> changed the setting.</summary>
    public const string SettingChanged = "setting_changed";

    /// <summary>A message from anyone else (or any mail) was queued for the manager.</summary>
    public const string Queued = "queued";
}

/// <summary>Settings of an <see cref="Orchestrator"/> beyond the engine, transport, state root and store.</summary>
public sealed record OrchestratorOptions
{
    /// <summary><see cref="PrincipalAccessModes.Obo"/> (the principal's on-behalf-of token) or <see cref="PrincipalAccessModes.Delegation"/>.</summary>
    public string PrincipalAccess { get; init; } = PrincipalAccessModes.Obo;

    /// <summary>Receives every Work IQ guard receipt (Python's <c>recorder</c>).</summary>
    public Action<GuardReceipt>? GuardRecorder { get; init; }

    /// <summary>Receives every state command receipt (Python's <c>recorder</c>).</summary>
    public Action<StateReceipt>? StateRecorder { get; init; }

    /// <summary>In per-instance hosting, the one instance this container serves; anything else is refused.</summary>
    public AgentInstance? Instance { get; init; }

    /// <summary>That instance's own Blob container, holding each manager's state under its own prefix. Without it, each
    /// instance and manager has a container of its own (<see cref="StateSync.ContainerName"/>).</summary>
    public string? StateContainer { get; init; }

    /// <summary>The voice page; <c>{instance}</c> is replaced by the instance key.</summary>
    public string? VoiceUrl { get; init; }

    /// <summary>The <c>margo-state</c> command (see <see cref="StateCommands"/>); null for the CLI shipped next to the host.</summary>
    public IReadOnlyList<string>? StateCliCommand { get; init; }

    /// <summary>How often the state lease is renewed during a turn; null for <see cref="StateSync"/>'s 20 seconds.</summary>
    public TimeSpan? StateRenewInterval { get; init; }

    /// <summary>Warnings (manager lookup failures, by status or exception type only) and turn failures (Python's
    /// <c>margo.autopilot</c> logger). Nothing logged holds message text or tokens.</summary>
    public ILogger? Logger { get; init; }
}

/// <summary>The one <c>(agent_identity_id, agent_user_id)</c> pair a per-instance container serves.</summary>
public sealed record AgentInstance(string AgentIdentityId, string AgentUserId);

/// <summary>
/// Handles one inbound activity for a hired Margo instance (<c>orchestrator.py</c> <c>Orchestrator</c>). No SDK types;
/// the host adapts.
/// </summary>
/// <remarks>
/// <para>Order matters, and each step fails closed:</para>
/// <list type="number">
/// <item>Get Margo's own token and look up her manager in the directory (<c>/me/manager</c>), through the guard, on
/// every turn. The manager is who she works for; a message never decides that. A failed lookup is reported as a
/// failure, never as "no manager".</item>
/// <item>Compare the authenticated sender's object ID with the manager's.</item>
/// <item>The manager is served only in their one-to-one chat. In a group chat or channel a turn would read their private
/// data and answer in front of everyone, so it is refused with a fixed note.</item>
/// <item>Only for the manager, request a principal token. On-behalf-of exchanges the <em>sender's</em> token, so asking
/// for it on anyone else's message would hand the guard a colleague's token labelled as the manager's.</item>
/// <item>Run the turn under the instance's state lease (<see cref="StateSync"/>). SQLite work runs off the request thread,
/// and no SQLite write transaction is open across a network or model call.</item>
/// <item>Messages from anyone else, and all mail, are queued as untrusted candidate requests; any reply is decided by the
/// manager's <c>Replies to other people</c> setting, enforced by <see cref="ReplyGate"/>.</item>
/// </list>
/// <para>The only other text that leaves is a fixed notice (no manager, lookup failed, group refused, state unsafe, turn
/// failed). Notices name no one and carry no data, and they go only to a one-to-one Teams chat (the failure notices only
/// to the manager).</para>
/// </remarks>
public sealed class Orchestrator
{
    /// <summary><c>TURN_CALL_BUDGET</c>: Work IQ calls one manager turn may make.</summary>
    public const int TurnCallBudget = 40;

    /// <summary><c>STATE_CALL_BUDGET</c>: state commands one manager turn may run.</summary>
    public const int StateCallBudget = 40;

    /// <summary><c>PERSONAL</c>: Teams' <c>conversationType</c> of a one-to-one chat.</summary>
    public const string Personal = "personal";

    /// <summary><c>VOICE_UNAVAILABLE</c>.</summary>
    public const string VoiceUnavailable = "Voice isn't set up for this deployment yet, so we can only talk here for now.";

    /// <summary><c>NO_MANAGER</c>.</summary>
    public const string NoManager = "I can't find my manager in the directory, so I don't know who I work for yet. "
                                    + "An administrator needs to set my manager before I can help.";

    /// <summary><c>MANAGER_UNKNOWN</c>.</summary>
    public const string ManagerUnknown = "I couldn't check who I work for just now (the directory lookup failed), so I haven't "
                                         + "acted on this message. Please try again shortly.";

    /// <summary><c>GROUP_REFUSED</c>.</summary>
    public const string GroupRefused = "I only work with you in our one-to-one chat. I don't take part in group chats or "
                                       + "channels, so I haven't read or shared anything here. Message me directly.";

    /// <summary><c>STATE_UNSAFE</c>.</summary>
    public const string StateUnsafe = "I couldn't load my records safely, so I've stopped rather than work from a partial "
                                      + "memory. Nothing was sent. Try again shortly; if it persists, an operator should check "
                                      + "my state storage.";

    /// <summary><c>TURN_FAILED</c>.</summary>
    public const string TurnFailed = "Something failed on my side before I could finish, so I stopped. Nothing was sent. "
                                     + "Try again; if it keeps happening, an operator should check my logs.";

    /// <summary>The placeholder manager of the bootstrap binding used for the directory lookup.</summary>
    public const string UnboundManager = "unbound-manager";

    private readonly ConcurrentDictionary<string, string> _conversations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _initialised = new(StringComparer.Ordinal);
    private readonly ILogger _logger;

    /// <param name="engine">The model turn loop.</param>
    /// <param name="transport">The Work IQ transport (the guard checks its endpoint).</param>
    /// <param name="stateRoot">The private state root on local disk; each account's database lives under it by
    /// State.Core's <see cref="StateStore.StatePath"/> rules.</param>
    /// <param name="storeFor">The leased store for a Blob container name (the host reuses one store per container).</param>
    /// <param name="options">Everything else; null for the defaults.</param>
    public Orchestrator(Margo.Cloud.Engine.Engine engine, IWorkIQTransport transport, string stateRoot, Func<string, ILeaseStore> storeFor,
                        OrchestratorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(stateRoot);
        ArgumentNullException.ThrowIfNull(storeFor);
        Engine = engine;
        Transport = transport;
        StateRoot = stateRoot;
        StoreFor = storeFor;
        Options = options ?? new OrchestratorOptions();
        _logger = Options.Logger ?? NullLogger.Instance;
    }

    /// <summary>The model turn loop.</summary>
    public Margo.Cloud.Engine.Engine Engine { get; }

    /// <summary>The Work IQ transport.</summary>
    public IWorkIQTransport Transport { get; }

    /// <summary>The private state root.</summary>
    public string StateRoot { get; }

    /// <summary>The leased store per container.</summary>
    public Func<string, ILeaseStore> StoreFor { get; }

    /// <summary>The settings.</summary>
    public OrchestratorOptions Options { get; }

    /// <summary>The instance this container serves, in per-instance hosting.</summary>
    public AgentInstance? Instance => Options.Instance;

    /// <summary>Tests only: sees each manager turn's guard before the engine runs (the Python tests spy on <c>engine.run</c>).</summary>
    internal Action<WorkIQGuard>? TurnGuardObserver { get; set; }

    /// <summary>
    /// <c>handle(event, get_token)</c>: one activity. Never throws for a failed turn: the failure is the status
    /// (only cancellation of <paramref name="cancellationToken"/> throws).
    /// </summary>
    /// <param name="inbound">The activity.</param>
    /// <param name="getToken">Returns the token of a kind (<c>margo</c>, <c>principal</c>), or null; an exception counts as null.</param>
    /// <param name="cancellationToken">Cancels the turn.</param>
    public async Task<TurnReply> HandleAsync(InboundEvent inbound, Func<string, Task<string?>> getToken, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inbound);
        ArgumentNullException.ThrowIfNull(getToken);
        var margoToken = await TokenAsync(getToken, Subjects.Margo, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(margoToken))
        {
            return new TurnReply(null, TurnStatuses.NoAgentToken);
        }

        if (Instance is { } instance
            && !(string.Equals(inbound.AgentIdentityId, instance.AgentIdentityId, StringComparison.Ordinal)
                 && string.Equals(inbound.AgentUserId, instance.AgentUserId, StringComparison.Ordinal)))
        {
            return new TurnReply(null, TurnStatuses.WrongInstance); // defence in depth behind the router
        }

        var sender = inbound.SenderId;
        var oneToOne = inbound.Channel != "mail" && inbound.ConversationType == Personal;
        Manager? manager;
        try
        {
            manager = await LookUpManagerAsync(inbound, margoToken, cancellationToken).ConfigureAwait(false);
        }
        catch (BindingException)
        {
            return new TurnReply(null, TurnStatuses.InvalidBinding);
        }
        catch (Exception exc) when (!IsCancellation(exc, cancellationToken))
        {
            // ManagerLookupFailedException, or a guard/transport configuration error. Never the payload.
            _logger.LogWarning("manager lookup failed: {Reason}", exc is ManagerLookupFailedException ? exc.Message : exc.GetType().Name);
            return new TurnReply(oneToOne && !string.IsNullOrEmpty(sender) ? ManagerUnknown : null, TurnStatuses.ManagerLookupFailed);
        }

        if (manager is null)
        {
            return new TurnReply(oneToOne && !string.IsNullOrEmpty(sender) ? NoManager : null, TurnStatuses.NoManager);
        }

        // Mail is never a conversation turn in this version, even from the manager: it is queued.
        var isManager = sender is not null && string.Equals(sender, manager.Id, StringComparison.Ordinal) && inbound.Channel != "mail";
        if (isManager && !oneToOne)
        {
            return new TurnReply(GroupRefused, TurnStatuses.GroupRefused);
        }

        InstanceBinding binding;
        try
        {
            // No principal chat: the activity's conversation ID is not the Graph chat ID recorded at hire, so the
            // unattended principal-chat post stays unavailable in this version.
            binding = new InstanceBinding(inbound.AgentIdentityId!, inbound.AgentUserId!, manager.Id);
        }
        catch (ArgumentException)
        {
            return new TurnReply(null, TurnStatuses.InvalidBinding);
        }

        try
        {
            return await RunAsync(inbound, getToken, binding, isManager, manager.DisplayName, margoToken, cancellationToken).ConfigureAwait(false);
        }
        catch (SyncException)
        {
            return new TurnReply(isManager ? StateUnsafe : null, TurnStatuses.StateUnsafe);
        }
        catch (Exception exc) when (!IsCancellation(exc, cancellationToken))
        {
            _logger.LogError(exc, "turn failed");
            return new TurnReply(isManager ? TurnFailed : null, TurnStatuses.Error);
        }
    }

    /// <summary>
    /// <c>state_sync(binding)</c>: the leased state for one instance and manager, wherever this deployment keeps it, and
    /// the manager's preferences file. With a per-instance container, each manager's bundle sits under
    /// <c>sha256(account)[:40]/</c>; otherwise each account has a container of its own.
    /// </summary>
    public (StateSync Sync, string Preferences) StateSyncFor(InstanceBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var (database, preferences) = Paths(binding.Account);
        if (!string.IsNullOrEmpty(Options.StateContainer))
        {
            var prefix = PyIds.Sha256Hex(binding.Account)[..40] + "/";
            return (new StateSync(database, StoreFor(Options.StateContainer), binding.Account, preferences, Options.StateRenewInterval, prefix),
                    preferences);
        }

        return (new StateSync(database, StoreFor(StateSync.ContainerName(binding.Account)), binding.Account, preferences, Options.StateRenewInterval),
                preferences);
    }

    /// <summary><c>_paths(account)</c>: the account's database by the store's own path rules (hashed, private, never a
    /// synced folder) and <c>preferences.md</c> beside it.</summary>
    /// <exception cref="StateError">The state root is unsafe or the account is invalid.</exception>
    public (string Database, string Preferences) Paths(string account)
    {
        var (_, database) = StateStore.StatePath(account, StateRoot);
        return (database, Path.Combine(Path.GetDirectoryName(database)!, "preferences.md"));
    }

    /// <summary>
    /// <c>_manager(event, margo_token)</c>: looks the manager up on every turn, with Margo's own token through the guard
    /// (budget 1, unattended). A re-org takes effect immediately and a former manager stops being treated as the
    /// principal; a changed manager is a different account, so the previous person's ledger and memory are never
    /// opened for the new one.
    /// </summary>
    private async Task<Manager?> LookUpManagerAsync(InboundEvent inbound, string margoToken, CancellationToken cancellationToken)
    {
        var bootstrap = new InstanceBinding(inbound.AgentIdentityId!, inbound.AgentUserId!, UnboundManager);
        var guard = new WorkIQGuard(bootstrap, new TurnTokens(new Dictionary<string, string?> { [Subjects.Margo] = margoToken }), Transport,
                                    recorder: Options.GuardRecorder, budget: new CallBudget(1));
        var arguments = new JsonObject { ["on_data_of"] = Subjects.Self, ["entityUrls"] = new JsonArray(ManagerDirectory.ManagerUrl) };
        var result = await guard.CallAsync("fetch", arguments, RunModes.Unattended, cancellationToken).ConfigureAwait(false);
        return ManagerDirectory.From(result);
    }

    private async Task<TurnReply> RunAsync(InboundEvent inbound, Func<string, Task<string?>> getToken, InstanceBinding binding, bool isManager,
                                           string? managerName, string margoToken, CancellationToken cancellationToken)
    {
        var (sync, preferences) = StateSyncFor(binding);
        var tokens = new Dictionary<string, string?>(StringComparer.Ordinal) { [Subjects.Margo] = margoToken };
        if (isManager && Options.PrincipalAccess == PrincipalAccessModes.Obo)
        {
            tokens[Subjects.Principal] = await TokenAsync(getToken, Subjects.Principal, cancellationToken).ConfigureAwait(false);
        }

        async Task<TurnReply> Work(CancellationToken token)
        {
            if (isManager)
            {
                // Create the store if needed, so this turn saves (a changed setting included).
                await Task.Run(() => binding.OpenLedger(StateRoot).Dispose(), token).ConfigureAwait(false);
                if (!_initialised.ContainsKey(binding.Account))
                {
                    await NewStateCommands(binding, preferences).InitialiseAsync(token).ConfigureAwait(false);
                    _initialised.TryAdd(binding.Account, true);
                }

                return await ManagerTurnAsync(inbound, binding, tokens, preferences, token).ConfigureAwait(false);
            }

            return await Task.Run(() => ThirdPartyTurn(inbound, binding, preferences, managerName), token).ConfigureAwait(false);
        }

        var outcome = await sync.RunAsync(Work, cancellationToken: cancellationToken).ConfigureAwait(false);
        return outcome.Result;
    }

    /// <summary><c>_state_commands(binding, preferences, budget)</c>: the skill scripts, run for this instance's account
    /// only, inside its state lease.</summary>
    private StateCommands NewStateCommands(InstanceBinding binding, string preferences, CallBudget? budget = null) =>
        new(binding.Account, StateRoot, preferences, budget, Options.StateRecorder, cliCommand: Options.StateCliCommand);

    private async Task<TurnReply> ManagerTurnAsync(InboundEvent inbound, InstanceBinding binding, IReadOnlyDictionary<string, string?> tokens,
                                                   string preferences, CancellationToken cancellationToken)
    {
        if (ManagerCommands.IsVoice(inbound.Text))
        {
            if (string.IsNullOrEmpty(Options.VoiceUrl))
            {
                return new TurnReply(VoiceUnavailable, TurnStatuses.VoiceLink);
            }

            var url = Options.VoiceUrl.Replace("{instance}", Instances.InstanceKey(binding.AgentIdentityId, binding.AgentUserId), StringComparison.Ordinal);
            return new TurnReply("Talk to me here: " + url, TurnStatuses.VoiceLink);
        }

        if (ManagerCommands.ReplyMode(inbound.Text) is { } mode)
        {
            await Task.Run(() => ReplySetting.SetReplyMode(preferences, mode), cancellationToken).ConfigureAwait(false);
            return new TurnReply(ReplySetting.ChangedNote(mode), TurnStatuses.SettingChanged);
        }

        var guard = new WorkIQGuard(binding, new TurnTokens(tokens), Transport, recorder: Options.GuardRecorder,
                                    budget: new CallBudget(TurnCallBudget), principalAccess: Options.PrincipalAccess);
        if (inbound.ConversationId is null)
        {
            // Python: binding.account + "|" + None.
            throw new PyTypeError("can only concatenate str (not \"NoneType\") to str");
        }

        var key = PyIds.Sha256Hex(binding.Account + "|" + inbound.ConversationId);
        TurnGuardObserver?.Invoke(guard);
        var office = new OfficeReader(() => binding.OpenLedger(StateRoot));
        var state = NewStateCommands(binding, preferences, new CallBudget(StateCallBudget));
        _conversations.TryGetValue(key, out var previous);
        var result = await Engine.RunAsync(inbound.Text ?? "", RunModes.Attended, guard, office, state, previous,
                                           preferencesPath: preferences, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(result.ResponseId))
        {
            _conversations[key] = result.ResponseId;
        }
        else
        {
            _conversations.TryRemove(key, out _);
        }

        return new TurnReply(string.IsNullOrEmpty(result.Text) ? null : result.Text, result.Stopped ? TurnStatuses.Stopped : TurnStatuses.Answered);
    }

    /// <summary><c>_third_party_turn</c>: runs on a worker thread; the ledger is opened, used and disposed there, and
    /// disposed before the reply gate decides.</summary>
    private TurnReply ThirdPartyTurn(InboundEvent inbound, InstanceBinding binding, string preferences, string? managerName)
    {
        var policy = ReplyPolicy.Load(ReplySetting.Read(preferences));
        var mode = policy.Mode != ReplyModes.Approve ? policy.Mode : ReplyModes.None;
        if (inbound.Channel == "mail")
        {
            mode = ReplyModes.None; // v1 does not answer mail from other people at all
        }

        bool? first;
        using (var ledger = binding.OpenLedger(StateRoot))
        {
            var office = new OfficeQueue(ledger);
            // Only an acknowledgement depends on earlier messages; otherwise skip the lookup.
            first = mode == ReplyModes.Acknowledge ? !office.SeenBefore(inbound.SenderId, inbound.ConversationId) : null;
            office.Record(string.IsNullOrEmpty(inbound.Channel) ? "teams" : inbound.Channel, inbound.ActivityId, inbound.SenderId,
                          inbound.SenderName, inbound.Text, inbound.ConversationId);
        }

        var decision = new ReplyGate(binding, mode).Decide(inbound.SenderId, inbound.ConversationId, null, managerName,
                                                           firstInConversation: first);
        return new TurnReply(decision.Send ? decision.Text : null, TurnStatuses.Queued);
    }

    private static async Task<string?> TokenAsync(Func<string, Task<string?>> getToken, string kind, CancellationToken cancellationToken)
    {
        try
        {
            return await getToken(kind).ConfigureAwait(false);
        }
        catch (Exception exc) when (!IsCancellation(exc, cancellationToken))
        {
            return null;
        }
    }

    private static bool IsCancellation(Exception exc, CancellationToken cancellationToken) =>
        exc is OperationCanceledException && cancellationToken.IsCancellationRequested;
}

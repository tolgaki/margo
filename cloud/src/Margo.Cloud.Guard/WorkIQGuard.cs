using System.Text.Json.Nodes;
using Margo.Cloud.Core;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Work;
using Margo.Cloud.WorkIQ;
using static Margo.Cloud.Guard.GuardPolicy;

namespace Margo.Cloud.Guard;

/// <summary>
/// The Work IQ guard: the only path from a hosted Margo's model to Microsoft 365 (<c>guard.py</c> <c>WorkIQGuard</c>).
/// Validate, authorise, execute and record one Work IQ call at a time.
/// </summary>
/// <remarks>
/// <para>Why it exists. Work IQ's delegated permission is scoped to the token's subject. Margo holds two tokens: her
/// own agent-user token and an on-behalf-of token for her principal. <c>/me</c> means whichever subject signed the
/// call. A brief that silently used Margo's token would summarise her empty inbox and report a quiet day: plausible,
/// success-shaped and wrong. Prompt instructions reduce that risk; this guard removes it.</para>
/// <para>What it enforces (fails closed):</para>
/// <list type="number">
/// <item>One endpoint, <see cref="WorkIQScopes.McpUrl"/>. No other MCP server.</item>
/// <item>The model declares <c>on_data_of</c> (<c>principal</c> or <c>self</c>); the guard selects the token. The
/// model never sees or chooses a token.</item>
/// <item>Path policy: relative Graph paths only, no traversal or encoded separators, allowed roots per intent, and
/// <c>/users/{id}</c> limited to directory reads (profile, manager, reports).</item>
/// <item>Writes need a live execution claim from the approval journal whose stored Work IQ call equals this call
/// exactly. Unattended runs get no write path, except Margo posting to the fixed principal chat recorded at hire.
/// Third-party runs get no write path at all: a stranger's message must not be able to steer what reaches the
/// principal.</item>
/// <item>Requests triggered by someone other than the principal never read as the principal, and as Margo may only
/// open the triggering message. Claims are consumed durably in the ledger before dispatch, so no attempt is ever
/// sent twice.</item>
/// <item>A missing token is <c>blocked</c>, and so is a readable principal token whose subject is not the bound
/// principal (<see cref="TokenSubject"/>). The guard never falls back to the other subject.</item>
/// <item>Every attempt leaves a receipt: subject, tool, path (no query text), status and outcome. Tokens, bodies and
/// results are never recorded, and neither is text the model chose: a recorded path is a template of resource types
/// (<see cref="ReceiptText.PathTemplate"/>), without identifiers, addresses, file names or function arguments, and a
/// reason is fixed text.</item>
/// </list>
/// <para>Thread safety: calls may run concurrently (the engine may run read calls in parallel); receipts and consumed
/// claims are guarded by a lock and the budget is charged atomically, so every call leaves exactly one receipt and the
/// budget is never exceeded. Receipts are stored, and the recorder is called, in the order calls finish; the recorder
/// is called outside the lock, possibly from several threads at once, and so is the token provider. The claim
/// journal (one SQLite connection, whose transactions nest by connection state) is only ever used by one call at a
/// time: every <c>Load</c>, <c>MarkDispatched</c> and <c>Settle</c> runs under the guard's journal lock, so an
/// overlapping dispatch can never become part of another call's transaction (and be rolled back with it). The lock
/// is never held across the Work IQ call.</para>
/// </remarks>
public sealed class WorkIQGuard
{
    private readonly ITokenProvider _tokens;
    private readonly Action<GuardReceipt>? _recorder;
    private readonly object _gate = new();
    private readonly object _journalGate = new();
    private readonly HashSet<string> _consumed = new(StringComparer.Ordinal);
    private readonly List<GuardReceipt> _receipts = [];
    private readonly IReadOnlySet<string>? _fileHosts;

    /// <param name="binding">The instance binding recorded at hire.</param>
    /// <param name="tokens">The turn's tokens; the guard picks the subject.</param>
    /// <param name="transport">Must be wired to <see cref="WorkIQScopes.McpUrl"/>.</param>
    /// <param name="journal">The approval journal for claimed writes; without one, only the principal-chat post can write.</param>
    /// <param name="recorder">Receives each receipt when a call finishes (the host logs it). An exception it throws
    /// propagates to the caller, after the claim was settled, as in Python.</param>
    /// <param name="budget">Charged once before each provider call.</param>
    /// <param name="principalAccess"><see cref="PrincipalAccessModes.Obo"/> or <see cref="PrincipalAccessModes.Delegation"/>.</param>
    /// <param name="triggerMessageId">In a third-party run, the one message in Margo's mailbox that started it.
    /// Without it, a third-party run cannot open any message.</param>
    /// <exception cref="GuardConfigException">The transport is not wired to Work IQ, or the access mode is unknown.</exception>
    public WorkIQGuard(InstanceBinding binding, ITokenProvider tokens, IWorkIQTransport? transport, IClaimJournal? journal = null,
                       Action<GuardReceipt>? recorder = null, CallBudget? budget = null,
                       string principalAccess = PrincipalAccessModes.Obo, string? triggerMessageId = null)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(tokens);
        if (transport is null || !string.Equals(transport.Endpoint, WorkIQScopes.McpUrl, StringComparison.Ordinal))
        {
            throw new GuardConfigException("the guard only connects to " + WorkIQScopes.McpUrl);
        }

        if (principalAccess is null || !PrincipalAccessModes.All.Contains(principalAccess))
        {
            throw new GuardConfigException("principal_access must be 'obo' or 'delegation'");
        }

        Binding = binding;
        _tokens = tokens;
        Transport = transport;
        Journal = journal;
        _recorder = recorder;
        Budget = budget;
        PrincipalAccess = principalAccess;
        TriggerMessageId = triggerMessageId;
    }

    /// <summary>The instance binding.</summary>
    public InstanceBinding Binding { get; }

    /// <summary>The Work IQ transport.</summary>
    public IWorkIQTransport Transport { get; }

    /// <summary>The approval journal, when configured.</summary>
    public IClaimJournal? Journal { get; }

    /// <summary>The call budget, when configured.</summary>
    public CallBudget? Budget { get; }

    /// <summary><see cref="PrincipalAccessModes.Obo"/> or <see cref="PrincipalAccessModes.Delegation"/>.</summary>
    public string PrincipalAccess { get; }

    /// <summary>The message a third-party run may open.</summary>
    public string? TriggerMessageId { get; }

    /// <summary>
    /// The exact hosts an <c>ask</c> document link (<c>fileUrls</c>) may name, for example the tenant's
    /// <c>contoso.sharepoint.com</c> and <c>contoso-my.sharepoint.com</c>. Null (the default) accepts any single
    /// tenant label under <see cref="GuardPolicy.FileHostSuffixes"/>; set it to pin the links to this organisation's
    /// own hosts (recommended: another tenant's SharePoint is also a <c>sharepoint.com</c> host), or for a cloud whose
    /// file hosts have another suffix. An empty set accepts no links. No Python equivalent.
    /// </summary>
    /// <exception cref="GuardConfigException">An entry is not a plain DNS host name.</exception>
    public IReadOnlySet<string>? FileHosts
    {
        get => _fileHosts;
        init
        {
            if (value is null)
            {
                _fileHosts = null;
                return;
            }

            var hosts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var host in value)
            {
                if (host is null || !IsHostName(host))
                {
                    throw new GuardConfigException("file hosts must be plain DNS host names");
                }

                hosts.Add(host.ToLowerInvariant());
            }

            _fileHosts = hosts;
        }
    }

    /// <summary>A snapshot of every receipt so far, in order.</summary>
    public IReadOnlyList<GuardReceipt> Receipts
    {
        get
        {
            lock (_gate)
            {
                return _receipts.ToArray();
            }
        }
    }

    /// <summary>
    /// One Work IQ call (<c>call(tool, arguments, *, mode)</c>). Refusals and provider failures are results, never
    /// exceptions; every call that starts leaves exactly one receipt.
    /// </summary>
    /// <param name="tool">The Work IQ tool name; null stands for anything that is not a string.</param>
    /// <param name="arguments">The model's arguments, including <c>on_data_of</c> and, for a claimed write,
    /// <c>action_id</c>/<c>attempt_id</c>. Not modified; values are copied.</param>
    /// <param name="mode">One of <see cref="RunModes"/>; null stands for anything that is not a string.</param>
    /// <param name="cancellationToken">Cancels the call. Already cancelled: nothing happens and no receipt is left.
    /// Cancelled while Work IQ is being called: the request may have been sent, so a claim is settled
    /// <c>outcome_unknown</c> and an <c>unknown</c> receipt is recorded before the
    /// <see cref="OperationCanceledException"/> is rethrown.</param>
    /// <summary>
    /// Work IQ's own description of its tools (<c>tools/list</c>), asked as Margo with her own token. Empty when it cannot
    /// be listed; the caller then uses its built-in definitions. Not charged to the call budget: it reads no one's data.
    /// </summary>
    public async Task<IReadOnlyList<WorkIQTool>> ServerToolsAsync(CancellationToken cancellationToken = default)
    {
        string bearer;
        try
        {
            bearer = _tokens.TokenFor(Subjects.Margo);
        }
        catch (Exception)
        {
            return [];
        }

        try
        {
            return await Transport.ListToolsAsync(bearer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return [];
        }
    }

    public async Task<GuardResult> CallAsync(string? tool, JsonNode? arguments, string? mode, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var intent = arguments is JsonObject given ? PyNode.AsString(PyNode.Get(given, "on_data_of")) : null;
        var receipt = new Draft
        {
            At = StateStore.UtcNow(),
            Mode = mode,
            Tool = tool,
            OnDataOf = intent,
            ReadAs = intent == Subjects.Principal ? Subjects.Principal : intent == Subjects.Self ? Subjects.Margo : null,
        };

        Authorisation authorised;
        try
        {
            authorised = Authorise(tool, arguments, intent, mode);
        }
        catch (RefusedException exc)
        {
            return Finish(receipt, GuardStatuses.Refused, exc.Message);
        }
        catch (UnavailableException exc)
        {
            return Finish(receipt, GuardStatuses.Unavailable, exc.Message);
        }
        catch (Exception) // malformed model input is refused, never a crash without a receipt
        {
            return Finish(receipt, GuardStatuses.Refused, "malformed tool call");
        }

        receipt.Paths = authorised.Paths;
        var write = WriteTools.Contains(tool!);
        var claim = authorised.Claim;
        if (claim is not null)
        {
            receipt.ActionId = claim.ActionId;
            receipt.AttemptId = claim.AttemptId;
        }

        if (Budget is not null && !Budget.Charge())
        {
            Settle(receipt, claim, "failed", null);
            return Finish(receipt, GuardStatuses.BudgetExhausted, "call budget exhausted before the call");
        }

        var delegated = intent == Subjects.Principal && PrincipalAccess == PrincipalAccessModes.Delegation;
        if (delegated)
        {
            receipt.Via = "delegation";
        }

        string bearer;
        var asPrincipal = intent == Subjects.Principal && !delegated;
        var subject = asPrincipal ? Subjects.Principal : Subjects.Margo;
        try
        {
            bearer = _tokens.TokenFor(subject);
            if (asPrincipal && !TokenSubject.MayBe(bearer, Binding.PrincipalId))
            {
                throw new TokenUnavailableException("the token is not the bound principal's");
            }
        }
        catch (Exception exc) // nothing was sent, so a claim settles as a no-effect failure
        {
            Settle(receipt, claim, "failed", null);
            var detail = exc is TokenUnavailableException ? ReceiptText.Detail(exc.Message) : "token provider error";
            return Finish(receipt, GuardStatuses.Blocked, "token unavailable for " + receipt.ReadAs + ": " + detail);
        }

        if (claim is not null)
        {
            try
            {
                lock (_journalGate)
                {
                    Journal!.MarkDispatched(claim);
                }
            }
            catch (Exception)
            {
                return Finish(receipt, GuardStatuses.Error, DispatchNotRecorded);
            }

            if (DispatchCheckpoint is { } checkpoint)
            {
                try
                {
                    await checkpoint(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exc)
                {
                    // Nothing has been sent: the attempt ends here as a no-effect failure rather than as a dispatch.
                    Settle(receipt, claim, "failed", null);
                    var refused = Finish(receipt, GuardStatuses.Error, DispatchNotSaved);
                    if (exc is OperationCanceledException && cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }

                    return refused;
                }
            }
        }

        JsonObject? result;
        try
        {
            try
            {
                result = await Transport.CallAsync(tool!, authorised.Arguments, bearer, cancellationToken).ConfigureAwait(false);
            }
            catch (TransportException exc) when (!write && exc.StatusCode == 401 && _tokens is IRefreshingTokenProvider refreshing)
            {
                // The token expired during the turn: a read is sent once more with a fresh one. Writes are never resent.
                var fresh = await refreshing.RefreshAsync(subject, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrEmpty(fresh) || fresh == bearer || (asPrincipal && !TokenSubject.MayBe(fresh, Binding.PrincipalId)))
                {
                    throw;
                }

                result = await Transport.CallAsync(tool!, authorised.Arguments, fresh, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // No Python equivalent: the request may have landed, so it is settled as unknown and never resent.
            Settle(receipt, claim, "outcome_unknown", null);
            Finish(receipt, GuardStatuses.Unknown, "cancelled; effect unknown, not retried");
            throw;
        }
        catch (TransportTimeoutException)
        {
            Settle(receipt, claim, "outcome_unknown", null);
            return Finish(receipt, GuardStatuses.Unknown, "no response; effect unknown, not retried");
        }
        catch (TransportResponseTooLargeException) when (!write)
        {
            // No Python equivalent (Python buffered any answer). The read was answered; asking for less can succeed.
            return Finish(receipt, GuardStatuses.Error, ResponseTooLarge);
        }
        catch (TransportException exc)
        {
            var code = exc.StatusCode;
            receipt.StatusCodes = code is { } value ? [JsonValue.Create(value)] : [];
            if (write && (code is null || !RejectedNoEffect.Contains(code.Value)))
            {
                Settle(receipt, claim, "outcome_unknown", code);
                return Finish(receipt, GuardStatuses.Unknown, UnknownWrite);
            }

            Settle(receipt, claim, "failed", code);
            // The server's own words help the model correct its arguments; they go to the model only, never the receipt.
            return Finish(receipt, GuardStatuses.Error, "Work IQ error",
                          exc.ServerMessage is { Length: > 0 } said ? new JsonObject { ["error"] = said } : null);
        }
        catch (Exception) // connection reset, TLS, decoding: the request may have landed
        {
            Settle(receipt, claim, "outcome_unknown", null);
            return Finish(receipt, GuardStatuses.Unknown, "transport failure; effect unknown, not retried");
        }

        var codes = StatusCodes(result);
        receipt.StatusCodes = codes;
        var good = codes.Count(IsSuccess);
        var status = good == codes.Count ? GuardStatuses.Ok : good > 0 ? GuardStatuses.Partial : GuardStatuses.Error;
        if (!write)
        {
            return Finish(receipt, status, data: result);
        }

        // A write is only "ok" with a success code, and only "error" when Graph rejected it before acting. Anything
        // else (no code, mixed, 5xx) may have landed: the model is told so.
        if (status == GuardStatuses.Ok && codes.Count > 0)
        {
            Settle(receipt, claim, "succeeded", (int)PyJson.AsInt(codes[0]));
        }
        else if (status == GuardStatuses.Error && codes.All(IsRejectedNoEffect))
        {
            Settle(receipt, claim, "failed", (int)PyJson.AsInt(codes[0]));
        }
        else
        {
            Settle(receipt, claim, "outcome_unknown", null);
            return Finish(receipt, GuardStatuses.Unknown, UnknownWrite, result);
        }

        return Finish(receipt, status, data: result);
    }

    /// <summary>
    /// Checks an exact write call as it would be proposed for the manager's approval, without running it: the same
    /// argument, path and subject rules a claimed write meets, and, under delegation, that it can be sent through the
    /// manager's mailbox. A proposal that would be refused at execution is refused here, before the manager is asked.
    /// </summary>
    /// <param name="tool">The Work IQ write tool, without the <c>workiq-</c> prefix.</param>
    /// <param name="arguments">The exact arguments, with <c>on_data_of</c> and without claim fields.</param>
    /// <returns>The resource paths the call would reach.</returns>
    /// <exception cref="RefusedException">The call is not an allowed write.</exception>
    /// <exception cref="UnavailableException">The call cannot be made with this instance's access.</exception>
    public IReadOnlyList<string> PreviewWrite(string? tool, JsonNode? arguments)
    {
        if (tool is null || !WriteTools.Contains(tool))
        {
            throw new RefusedException("not a Work IQ write tool");
        }

        if (arguments is not JsonObject given)
        {
            throw new RefusedException("arguments must be an object");
        }

        if (given.ContainsKey("action_id") || given.ContainsKey("attempt_id"))
        {
            throw new RefusedException("a proposed call carries no execution claim");
        }

        var intent = PyNode.AsString(PyNode.Get(given, "on_data_of"));
        if (intent is null || !Subjects.Intents.Contains(intent))
        {
            throw new RefusedException("on_data_of must be 'principal' or 'self'");
        }

        var args = new JsonObject();
        foreach (var (key, value) in given)
        {
            if (!ControlArgs.Contains(key))
            {
                args[key] = PyNode.Copy(value);
            }
        }

        var unknown = args.Select(pair => pair.Key).Where(key => !ToolArgs[tool].Contains(key)).Order(PyRepr.StringComparer).ToList();
        if (unknown.Count > 0)
        {
            throw new RefusedException("unexpected arguments: " + ReceiptText.ArgumentNames(unknown));
        }

        if (PyNode.Get(args, "jsonBody") is { } body && !IsJsonObjectText(body))
        {
            throw new RefusedException("jsonBody must be a JSON object, written as a string");
        }

        var paths = Validate(tool, args, intent, write: true, fileHosts: _fileHosts);
        if (intent == Subjects.Principal && PrincipalAccess == PrincipalAccessModes.Delegation)
        {
            paths = Validate(tool, Delegated(tool, args), Subjects.Principal, write: true, Binding.PrincipalId, _fileHosts);
        }

        return paths;
    }

    /// <summary><see cref="PreviewWrite"/> without exceptions: null when the call would be allowed, else why not.</summary>
    public string? WriteProblem(string? tool, JsonNode? arguments)
    {
        try
        {
            PreviewWrite(tool, arguments);
            return null;
        }
        catch (Exception exc) when (exc is RefusedException or UnavailableException)
        {
            return exc.Message;
        }
    }

    private static bool IsJsonObjectText(JsonNode body)
    {
        try
        {
            return PyNode.AsString(body) is { } text && PyJson.Loads(text) is JsonObject;
        }
        catch (Exception exc) when (exc is PyJsonDecodeError or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    // -- policy -------------------------------------------------------------------------------

    private Authorisation Authorise(string? tool, JsonNode? arguments, string? intent, string? mode)
    {
        if (mode is null || !RunModes.All.Contains(mode))
        {
            throw new RefusedException("unknown run mode");
        }

        if (arguments is not JsonObject given)
        {
            throw new RefusedException("arguments must be an object");
        }

        if (tool is null || !ToolArgs.TryGetValue(tool, out var allowed))
        {
            throw new RefusedException("not a Work IQ tool");
        }

        if (intent is null || !Subjects.Intents.Contains(intent))
        {
            throw new RefusedException("on_data_of must be 'principal' or 'self'");
        }

        var args = new JsonObject();
        foreach (var (key, value) in given)
        {
            if (!ControlArgs.Contains(key))
            {
                args[key] = PyNode.Copy(value);
            }
        }

        // A paging or delta link Work IQ returned is followed as the relative path it names (then checked like any path).
        if (tool == "fetch" && args["entityUrls"] is JsonArray links)
        {
            for (var i = 0; i < links.Count; i++)
            {
                if (RelativeGraphLink(links[i]) is { } relative)
                {
                    links[i] = relative;
                }
            }
        }
        else if (tool == "call_function")
        {
            foreach (var key in (string[])["functionUrl", "parentUrl"])
            {
                if (RelativeGraphLink(args[key]) is { } relative)
                {
                    args[key] = relative;
                }
            }
        }

        var unknown = args.Select(pair => pair.Key).Where(key => !allowed.Contains(key)).Order(PyRepr.StringComparer).ToList();
        if (unknown.Count > 0)
        {
            throw new RefusedException("unexpected arguments: " + ReceiptText.ArgumentNames(unknown));
        }

        if (mode == RunModes.ThirdParty && intent == Subjects.Principal)
        {
            throw new RefusedException("a request from someone other than the principal never reads as the principal");
        }

        if (OwnAccessOnly && intent == Subjects.Principal)
        {
            throw new RefusedException("a run your manager did not start reads only with your own access; use on_data_of self");
        }

        if (OwnAccessOnly && args.ContainsKey("agentId"))
        {
            throw new RefusedException("a run your manager did not start asks no other agent");
        }

        var write = WriteTools.Contains(tool);
        var claimed = given.ContainsKey("action_id") || given.ContainsKey("attempt_id");
        if (claimed && !write)
        {
            throw new RefusedException("execution claims apply only to write tools");
        }

        var paths = Validate(tool, args, intent, write, fileHosts: _fileHosts);
        if (mode == RunModes.ThirdParty && write)
        {
            throw new RefusedException("a run triggered by someone other than the principal cannot write");
        }

        if (mode == RunModes.ThirdParty)
        {
            CheckThirdPartyRead(tool, paths);
        }

        var sendArgs = args;
        if (intent == Subjects.Principal && PrincipalAccess == PrincipalAccessModes.Delegation)
        {
            sendArgs = Delegated(tool, args);
            paths = Validate(tool, sendArgs, Subjects.Principal, write, Binding.PrincipalId, _fileHosts);
        }

        if (!write)
        {
            return new Authorisation(sendArgs, null, paths);
        }

        if (!claimed && mode == RunModes.Attended && AllowsOwnDocuments && IsOwnDocumentCall(tool, intent, args))
        {
            return new Authorisation(sendArgs, null, paths);
        }

        // Margo acting as herself, in a turn her manager started: her own account, so no card. Never as someone else.
        if (!claimed && mode == RunModes.Attended && AllowsOwnWrites && intent == Subjects.Self)
        {
            if (NamesAnotherSender(args))
            {
                throw new RefusedException("sending as someone else is not your own action: propose it and ask for your manager's approval");
            }

            if (Interlocked.Increment(ref _ownWrites) > MaxOwnWrites)
            {
                throw new RefusedException("at most " + MaxOwnWrites + " actions of your own in one turn");
            }

            return new Authorisation(sendArgs, null, paths);
        }

        if (!claimed && AllowsOwnWrites && intent == Subjects.Principal)
        {
            throw new RefusedException("this acts on your manager's account, which needs their approval of this exact action: "
                                       + "record it with work_state.py propose, then call request_approval");
        }

        // The manager's standing permission for what only they receive: an email from Margo to them alone, or a file
        // shared with them alone. Attended or unattended (a note about something Margo observed); never third-party.
        if (!claimed && mode is RunModes.Attended or RunModes.Unattended && AllowsManagerChannel && IsManagerChannelCall(tool, intent, args))
        {
            return new Authorisation(sendArgs, null, paths);
        }

        if (!claimed && IsPrincipalChatPost(tool, intent, paths))
        {
            if (PyNode.AsString(PyNode.Get(args, "jsonBody")) is not { } body || PyJson.Loads(body) is not JsonObject)
            {
                throw new RefusedException("a chat message needs a JSON object body");
            }

            return new Authorisation(sendArgs, null, paths);
        }

        if (mode != RunModes.Attended)
        {
            throw new RefusedException("unattended runs can only post to the principal's chat recorded at hire");
        }

        if (Journal is null)
        {
            throw new RefusedException("no approval journal is configured; writes are unavailable");
        }

        ExecutionClaim claim;
        try
        {
            lock (_journalGate)
            {
                claim = Journal.Load(PyNode.Get(given, "action_id"), PyNode.Get(given, "attempt_id"));
            }
        }
        catch (ClaimException exc) // its messages are fixed texts
        {
            throw new RefusedException("no live execution claim: " + ReceiptText.Detail(exc.Message));
        }
        catch (Exception) // a ledger failure: its message may quote what it was asked for
        {
            throw new RefusedException("no live execution claim: the approval journal could not be read");
        }

        lock (_gate)
        {
            if (_consumed.Contains(claim.AttemptId))
            {
                throw new RefusedException("execution claim already used");
            }
        }

        if (!string.Equals(claim.Account, Binding.Account, StringComparison.Ordinal))
        {
            throw new RefusedException("execution claim belongs to another instance");
        }

        if (!PyNode.Is(claim.ApprovedBy, Binding.PrincipalId))
        {
            throw new RefusedException("the approval was not given by this instance's manager");
        }

        if (!PyNode.Is(claim.OnDataOf, intent))
        {
            throw new RefusedException("approved action was for on_data_of=" + ReceiptText.Subject(PyNode.AsString(claim.OnDataOf) ?? "?"));
        }

        var requested = new JsonObject { ["tool"] = tool, ["arguments"] = PyNode.Copy(args) };
        if (claim.CallKey != LedgerClaimJournal.WorkIQCallKey || Canonical(claim.Call) != Canonical(requested))
        {
            throw new RefusedException("call differs from the approved Work IQ call; revise and re-approve");
        }

        lock (_gate)
        {
            if (!_consumed.Add(claim.AttemptId))
            {
                throw new RefusedException("execution claim already used");
            }
        }

        return new Authorisation(sendArgs, claim, paths);
    }

    /// <summary>The reason when a read was answered with more than the transport will buffer.</summary>
    public const string ResponseTooLarge = "Work IQ's answer was too large to read; ask for less (fewer paths, $top, $select)";

    /// <summary>Why principal Copilot search is unavailable under delegation, and what to use instead.</summary>
    public const string DelegationSearchHint =
        "Copilot search of the principal's data needs on-behalf-of access. Under delegation, read their mail and calendar "
        + "with fetch or call_function and on_data_of \"principal\" (/me/messages, /me/mailFolders, /me/events, /me/calendarView), "
        + "and search what is shared with you (mail you received, chats and channels you are in, files shared with you) "
        + "with retrieve or ask and on_data_of \"self\".";

    /// <summary>Why a principal path outside mail and calendar is unavailable under delegation, and what to use instead.</summary>
    public const string DelegationPathHint =
        "only the principal's mail and calendar are reachable through delegation (/me/messages, /me/mailFolders, /me/events, "
        + "/me/calendar, /me/calendars, /me/calendarView); for chats, channels and files they shared with you, use on_data_of \"self\".";

    /// <summary>Rewrite principal <c>/me/...</c> mail and calendar paths to the delegated mailbox (<c>_delegated</c>).</summary>
    private JsonObject Delegated(string tool, JsonObject args)
    {
        if (tool is "retrieve" or "ask")
        {
            throw new UnavailableException(DelegationSearchHint);
        }

        if (tool is "list_agents" or "get_schema" or "search_paths")
        {
            return args;
        }

        var write = WriteTools.Contains(tool);
        var rewritten = PyNode.CopyObject(args);
        foreach (var key in PathKeys)
        {
            if (rewritten.TryGetPropertyValue(key, out var value))
            {
                rewritten[key] = value is JsonArray list
                    ? new JsonArray(list.Select(item => (JsonNode?)JsonValue.Create(Rewrite(item, write))).ToArray())
                    : JsonValue.Create(Rewrite(value, write));
            }
        }

        return rewritten;
    }

    private string Rewrite(JsonNode? value, bool write)
    {
        // Only paths _validate accepted reach here, so the value is a string starting with one "/".
        var path = PyNode.AsString(value) ?? throw new InvalidOperationException("path argument is not a string");
        var segments = path.Split('?', 2)[0].Trim('/').Split('/');
        if (segments.Length < 2 || LowerAscii(segments[0]) != "me"
            || !(Delegable.Contains(LowerAscii(segments[1])) || (write && DelegableWrites.Contains(LowerAscii(segments[1])))))
        {
            throw new UnavailableException(DelegationPathHint);
        }

        return "/users/" + Binding.PrincipalId + "/" + path["/me/".Length..];
    }

    /// <summary>
    /// Least privilege when someone else triggered the run (<c>_check_third_party_read</c>). Margo's own chats, search
    /// and Copilot can surface what she has posted to the principal, and her mailbox holds other people's requests,
    /// so a third-party-triggered run may only open the triggering message and use schema discovery.
    /// </summary>
    private void CheckThirdPartyRead(string tool, List<string> paths)
    {
        if (ThirdPartyTools.Contains(tool))
        {
            return;
        }

        if (tool != "fetch" || !paths.All(path => IsMessage(path, TriggerMessageId)))
        {
            throw new RefusedException("a third-party-triggered run may only open the message that triggered it");
        }
    }

    /// <summary>
    /// The manager's standing permission for documents they ask for (decided by the manager, Nov 25, 2026): in an
    /// attended turn, with <see cref="AllowsOwnDocuments"/> on, two writes run without a card. Creating an Office
    /// document in Margo's own space (<c>/office-agent-mcp/generate_artifact</c>), and sharing an item with the manager
    /// and nobody else (<c>.../items/{id}/invite</c> whose only recipient is the bound manager's object ID, sign-in
    /// required). Neither is visible to anyone but the manager. Any other shape, or any other recipient, needs a claim.
    /// </summary>
    public bool AllowsOwnDocuments { get; init; }

    /// <summary>
    /// The manager's standing permission for what only they receive, without a card: an email from Margo to
    /// <see cref="ManagerMail"/> alone, and a file shared with the manager alone. Posts to the manager's 1:1 chat
    /// (<see cref="InstanceBinding.PrincipalChatId"/>) are allowed without it. Set only on the guard of the manager
    /// channel (the orchestrator's <c>TurnManagerChannel</c>), never on the model's own guard.
    /// </summary>
    public bool AllowsManagerChannel { get; init; }

    /// <summary>
    /// The manager's standing permission for Margo to act as herself: when true, a write with intent
    /// <see cref="Subjects.Self"/> runs without an approval card, in an attended run only. It is her own mailbox,
    /// chats, calendar and files; a write that names another sender (<c>from</c>, <c>sender</c>) is not her own and is
    /// refused. Writes on the manager's account (intent <see cref="Subjects.Principal"/>) always need a claim of the
    /// manager's approval of that exact call. Set only on the model's guard in a turn the manager started in their
    /// one-to-one chat; never in a room, and never in a run someone else's message or a timer started.
    /// </summary>
    public bool AllowsOwnWrites { get; init; }

    /// <summary>At most this many writes as herself in one turn (<see cref="AllowsOwnWrites"/>).</summary>
    public const int MaxOwnWrites = 10;

    private int _ownWrites;

    /// <summary>
    /// For a run the manager did not start (someone else's message, or listening in rooms): when true, nothing is read
    /// or asked as the manager, whatever the access mode, and no Copilot agent is asked. What reached Margo from
    /// other people is then looked into only with what her own account can see.
    /// </summary>
    public bool OwnAccessOnly { get; init; }

    /// <summary>
    /// Called for a claimed write after its dispatch is recorded in the journal and before the call leaves: saves that
    /// record where a restart or a failed end-of-turn save cannot lose it (the orchestrator uploads the leased state).
    /// If it fails, nothing is sent. Without it the record of a sent action lives only on this machine until the turn
    /// ends, and losing it would leave the approval usable a second time.
    /// </summary>
    public Func<CancellationToken, Task>? DispatchCheckpoint { get; init; }

    /// <summary>The reason given when the journal could not record the dispatch: the call was never sent.</summary>
    public const string DispatchNotRecorded = "could not record dispatch; nothing was sent";

    /// <summary>The reason given when <see cref="DispatchCheckpoint"/> failed: the call was never sent.</summary>
    public const string DispatchNotSaved = "could not save the record of dispatch; nothing was sent";

    /// <summary>The manager's email address, from the directory; an email may go to this address alone.</summary>
    public string? ManagerMail { get; init; }

    /// <summary>The longest email body the manager channel sends.</summary>
    public const int MaxManagerMailChars = 20_000;

    /// <summary>The Office applications a document can be created for.</summary>
    public static readonly IReadOnlySet<string> OfficeApps = new HashSet<string>(StringComparer.Ordinal) { "Word", "Excel", "PowerPoint" };

    /// <summary>True when a write's body names a sender (<c>from</c> or <c>sender</c>, at the top or on its message):
    /// with delegated access to the manager's mailbox, such a call from Margo's own account would send as the manager.</summary>
    private static bool NamesAnotherSender(JsonObject args)
    {
        if (PyNode.AsString(PyNode.Get(args, "jsonBody")) is not { Length: > 0 } text)
        {
            return false;
        }

        JsonNode? parsed;
        try
        {
            parsed = PyJson.Loads(text);
        }
        catch (Exception exc) when (exc is PyValueError or System.Text.Json.JsonException or FormatException)
        {
            return true; // unreadable: not shown to be her own
        }

        return parsed is JsonObject body
               && new[] { body, body["message"] as JsonObject }.Any(part => part is not null && part.Any(pair => pair.Key.Equals("from", StringComparison.OrdinalIgnoreCase)
                                                                                                             || pair.Key.Equals("sender", StringComparison.OrdinalIgnoreCase)));
    }

    private bool IsOwnDocumentCall(string tool, string intent, JsonObject args)
    {
        if (SelfAction(tool, intent, args) is not var (url, body))
        {
            return false;
        }

        if (url == "/office-agent-mcp/generate_artifact")
        {
            return body.All(pair => pair.Key is "appName" or "taskDescription" or "userRequest")
                   && PyNode.AsString(body["appName"]) is { } app && OfficeApps.Contains(app)
                   && PyNode.AsString(body["taskDescription"]) is { Length: > 0 }
                   && (!body.ContainsKey("userRequest") || PyNode.AsString(body["userRequest"]) is not null);
        }

        return IsManagerInvite(url, body);
    }

    /// <summary>
    /// The manager channel: <c>/me/sendMail</c> from Margo's own mailbox to the manager's address alone (no copies,
    /// no other recipients, no attachments), or a file shared with the manager alone (<see cref="IsManagerInvite"/>).
    /// </summary>
    private bool IsManagerChannelCall(string tool, string intent, JsonObject args)
    {
        if (SelfAction(tool, intent, args) is not var (url, body))
        {
            return false;
        }

        if (IsManagerInvite(url, body))
        {
            return true;
        }

        return string.Equals(url, "/me/sendMail", StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrEmpty(ManagerMail)
               && body.All(pair => pair.Key is "message" or "saveToSentItems")
               && (body["saveToSentItems"] is null || (body["saveToSentItems"] is JsonValue save && save.TryGetValue<bool>(out _)))
               && body["message"] is JsonObject message
               && message.All(pair => pair.Key is "subject" or "body" or "toRecipients" or "importance")
               && (message["subject"] is null || PyNode.AsString(message["subject"]) is { Length: <= 255 })
               && (message["importance"] is null || PyNode.AsString(message["importance"]) is "low" or "normal" or "high")
               && message["body"] is JsonObject content && content.All(pair => pair.Key is "contentType" or "content")
               && PyNode.AsString(content["contentType"]) is { } type
               && (type.Equals("text", StringComparison.OrdinalIgnoreCase) || type.Equals("html", StringComparison.OrdinalIgnoreCase))
               && PyNode.AsString(content["content"]) is { Length: > 0 and <= MaxManagerMailChars }
               && message["toRecipients"] is JsonArray { Count: 1 } to && to[0] is JsonObject recipient && recipient.Count == 1
               && recipient["emailAddress"] is JsonObject address && address.All(pair => pair.Key is "address" or "name")
               && string.Equals(PyNode.AsString(address["address"]), ManagerMail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A <c>do_action</c> as Margo with only a path and a JSON object body, parsed; null for anything else.</summary>
    private static (string Url, JsonObject Body)? SelfAction(string tool, string intent, JsonObject args)
    {
        if (tool != "do_action" || intent != Subjects.Self || args.Any(pair => pair.Key is not ("actionUrl" or "jsonBody"))
            || PyNode.AsString(args["actionUrl"]) is not { } url || url.Contains('?', StringComparison.Ordinal)
            || PyNode.AsString(args["jsonBody"]) is not { } text)
        {
            return null;
        }

        try
        {
            return PyJson.Loads(text) is JsonObject body ? (url, body) : null;
        }
        catch (Exception exc) when (exc is PyJsonDecodeError or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>A drive item shared with the manager alone, by their object ID, read or write, sign-in required.</summary>
    private bool IsManagerInvite(string url, JsonObject body)
    {
        var segments = url.Split('/');
        var invite = (segments is ["", "drives", { Length: > 0 }, "items", { Length: > 0 }, "invite"])
                     || (segments is ["", "me", "drive", "items", { Length: > 0 }, "invite"]);
        return invite
               && body.All(pair => pair.Key is "recipients" or "roles" or "requireSignIn" or "sendInvitation" or "message")
               && body["recipients"] is JsonArray { Count: 1 } recipients && recipients[0] is JsonObject recipient
               && recipient.Count == 1 && PyNode.AsString(recipient["objectId"]) is { } objectId
               && string.Equals(objectId, Binding.PrincipalId, StringComparison.Ordinal)
               && body["roles"] is JsonArray { Count: 1 } roles && PyNode.AsString(roles[0]) is "read" or "write"
               && body["requireSignIn"] is JsonValue signIn && signIn.TryGetValue<bool>(out var required) && required
               && (body["sendInvitation"] is null || (body["sendInvitation"] is JsonValue notify && notify.TryGetValue<bool>(out _)))
               && (body["message"] is null || PyNode.AsString(body["message"]) is { Length: <= 1000 });
    }

    /// <summary>The chat ID must be the Graph chat ID recorded at hire; unbound means no such post (<c>_is_principal_chat_post</c>).</summary>
    private bool IsPrincipalChatPost(string tool, string intent, List<string> paths)
    {
        if (tool != "create_entity" || intent != Subjects.Self || paths.Count != 1 || string.IsNullOrEmpty(Binding.PrincipalChatId))
        {
            return false;
        }

        var segments = paths[0].Trim('/').Split('/');
        if (segments.Length > 0 && LowerAscii(segments[0]) == "me")
        {
            segments = segments[1..];
        }

        return segments.Length == 3 && LowerAscii(segments[0]) == "chats"
               && string.Equals(segments[1], Binding.PrincipalChatId, StringComparison.Ordinal)
               && LowerAscii(segments[2]) == "messages";
    }

    // -- records ------------------------------------------------------------------------------

    private void Settle(Draft receipt, ExecutionClaim? claim, string state, int? statusCode)
    {
        if (claim is null)
        {
            return;
        }

        try
        {
            lock (_journalGate)
            {
                Journal!.Settle(claim, state, statusCode);
            }

            receipt.Journal = state;
        }
        catch (Exception) // the dispatch event still blocks a resend; reconciliation is needed
        {
            receipt.Journal = "unsettled";
        }
    }

    private GuardResult Finish(Draft draft, string status, string? reason = null, JsonObject? data = null)
    {
        var reasonText = string.IsNullOrEmpty(reason) ? null : reason;
        var receipt = new GuardReceipt
        {
            At = draft.At,
            Mode = ReceiptText.Label(draft.Mode),
            Tool = ReceiptText.Label(draft.Tool),
            OnDataOf = ReceiptText.Subject(draft.OnDataOf),
            ReadAs = draft.ReadAs,
            Paths = draft.Paths.Select(ReceiptText.PathTemplate).ToArray(),
            StatusCodes = draft.StatusCodes,
            ActionId = draft.ActionId,
            AttemptId = draft.AttemptId,
            Via = draft.Via,
            Journal = draft.Journal,
            Status = status,
            Reason = reasonText,
        };
        lock (_gate)
        {
            _receipts.Add(receipt);
        }

        _recorder?.Invoke(receipt);
        return new GuardResult(status, draft.ReadAs, draft.Tool, reasonText, data);
    }

    private sealed record Authorisation(JsonObject Arguments, ExecutionClaim? Claim, List<string> Paths);

    /// <summary>The receipt while a call is in progress (Python mutates one dict).</summary>
    private sealed class Draft
    {
        public required string At { get; init; }

        public string? Mode { get; init; }

        public string? Tool { get; init; }

        public string? OnDataOf { get; init; }

        public string? ReadAs { get; init; }

        public IReadOnlyList<string> Paths { get; set; } = [];

        public IReadOnlyList<JsonNode> StatusCodes { get; set; } = [];

        public string? ActionId { get; set; }

        public string? AttemptId { get; set; }

        public string? Via { get; set; }

        public string? Journal { get; set; }
    }
}

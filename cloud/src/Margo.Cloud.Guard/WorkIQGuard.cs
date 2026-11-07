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
/// <item>A missing token is <c>blocked</c>. The guard never falls back to the other subject.</item>
/// <item>Every attempt leaves a receipt: subject, tool, path (no query text), status and outcome. Tokens, bodies and
/// results are never recorded.</item>
/// </list>
/// <para>Thread safety: calls may run concurrently; receipts and consumed claims are guarded by a lock. The claim
/// journal (one SQLite connection) is used synchronously and is not made concurrent by this class.</para>
/// </remarks>
public sealed class WorkIQGuard
{
    private readonly ITokenProvider _tokens;
    private readonly Action<GuardReceipt>? _recorder;
    private readonly object _gate = new();
    private readonly HashSet<string> _consumed = new(StringComparer.Ordinal);
    private readonly List<GuardReceipt> _receipts = [];

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
    public async Task<GuardResult> CallAsync(string? tool, JsonNode? arguments, string? mode, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var intent = arguments is JsonObject given ? PyDynamic.AsString(PyDynamic.Get(given, "on_data_of")) : null;
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
        try
        {
            bearer = _tokens.TokenFor(intent == Subjects.Principal && !delegated ? Subjects.Principal : Subjects.Margo);
        }
        catch (Exception exc) // nothing was sent, so a claim settles as a no-effect failure
        {
            Settle(receipt, claim, "failed", null);
            var detail = exc is TokenUnavailableException ? exc.Message : "token provider error";
            return Finish(receipt, GuardStatuses.Blocked, "token unavailable for " + receipt.ReadAs + ": " + detail);
        }

        if (claim is not null)
        {
            try
            {
                Journal!.MarkDispatched(claim);
            }
            catch (Exception)
            {
                return Finish(receipt, GuardStatuses.Error, "could not record dispatch; nothing was sent");
            }
        }

        JsonObject? result;
        try
        {
            result = await Transport.CallAsync(tool!, authorised.Arguments, bearer, cancellationToken).ConfigureAwait(false);
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
            return Finish(receipt, GuardStatuses.Error, "Work IQ error");
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
                args[key] = PyDynamic.Clone(value);
            }
        }

        var unknown = args.Select(pair => pair.Key).Where(key => !allowed.Contains(key)).Order(PyRepr.StringComparer).ToList();
        if (unknown.Count > 0)
        {
            throw new RefusedException("unexpected arguments: " + string.Join(", ", unknown));
        }

        if (mode == RunModes.ThirdParty && intent == Subjects.Principal)
        {
            throw new RefusedException("a request from someone other than the principal never reads as the principal");
        }

        var write = WriteTools.Contains(tool);
        var claimed = given.ContainsKey("action_id") || given.ContainsKey("attempt_id");
        if (claimed && !write)
        {
            throw new RefusedException("execution claims apply only to write tools");
        }

        var paths = Validate(tool, args, intent, write);
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
            paths = Validate(tool, sendArgs, Subjects.Principal, write, Binding.PrincipalId);
        }

        if (!write)
        {
            return new Authorisation(sendArgs, null, paths);
        }

        if (!claimed && IsPrincipalChatPost(tool, intent, paths))
        {
            if (PyDynamic.AsString(PyDynamic.Get(args, "jsonBody")) is not { } body || PyJson.Loads(body) is not JsonObject)
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
            claim = Journal.Load(PyDynamic.Get(given, "action_id"), PyDynamic.Get(given, "attempt_id"));
        }
        catch (Exception exc) // ClaimException or ledger failure
        {
            throw new RefusedException("no live execution claim: " + exc.Message);
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

        if (!PyDynamic.Is(claim.ApprovedBy, Binding.PrincipalId))
        {
            throw new RefusedException("the approval was not given by this instance's manager");
        }

        if (!PyDynamic.Is(claim.OnDataOf, intent))
        {
            throw new RefusedException("approved action was for on_data_of=" + PyDynamic.ToStr(claim.OnDataOf));
        }

        var requested = new JsonObject { ["tool"] = tool, ["arguments"] = PyDynamic.Clone(args) };
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

    /// <summary>Rewrite principal <c>/me/...</c> mail and calendar paths to the delegated mailbox (<c>_delegated</c>).</summary>
    private JsonObject Delegated(string tool, JsonObject args)
    {
        if (tool is "retrieve" or "ask")
        {
            throw new UnavailableException("Copilot search of the principal's data needs on-behalf-of access");
        }

        if (tool is "list_agents" or "get_schema" or "search_paths")
        {
            return args;
        }

        var rewritten = PyDynamic.CloneObject(args);
        foreach (var key in PathKeys)
        {
            if (rewritten.TryGetPropertyValue(key, out var value))
            {
                rewritten[key] = value is JsonArray list
                    ? new JsonArray(list.Select(item => (JsonNode?)JsonValue.Create(Rewrite(item))).ToArray())
                    : JsonValue.Create(Rewrite(value));
            }
        }

        return rewritten;
    }

    private string Rewrite(JsonNode? value)
    {
        // Only paths _validate accepted reach here, so the value is a string starting with one "/".
        var path = PyDynamic.AsString(value) ?? throw new InvalidOperationException("path argument is not a string");
        var segments = path.Split('?', 2)[0].Trim('/').Split('/');
        if (segments.Length < 2 || LowerAscii(segments[0]) != "me" || !Delegable.Contains(LowerAscii(segments[1])))
        {
            throw new UnavailableException("only the principal's mail and calendar are reachable through delegation");
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
            Journal!.Settle(claim, state, statusCode);
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
            Mode = draft.Mode,
            Tool = draft.Tool,
            OnDataOf = draft.OnDataOf,
            ReadAs = draft.ReadAs,
            Paths = draft.Paths,
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

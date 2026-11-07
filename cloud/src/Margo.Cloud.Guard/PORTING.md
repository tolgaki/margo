# Margo.Cloud.Guard: porting notes

Port of `autopilot/src/margo_autopilot/guard.py` (the Work IQ guard: the only path from a hosted Margo's model to
Microsoft 365) and `autopilot/src/margo_autopilot/claims.py` (execution claims through the approval journal), plus
`instance.py`'s `InstanceBinding.open_ledger`, which `Margo.Cloud.Core` left out. The Python code is the
specification; everything not listed under "Deliberate deviations" is meant to behave the same.

## What maps to what

### guard.py

| Python | C# | Notes |
| --- | --- | --- |
| `WORKIQ_MCP_URL` | `WorkIQScopes.McpUrl` | Same string as `Margo.Cloud.WorkIQ`'s `WorkIQEndpoints.McpUrl`, which the transport defaults to |
| `WORKIQ_RESOURCE` | `WorkIQScopes.Resource` | `api://workiq.svc.cloud.microsoft` |
| `WORKIQ_SCOPE` (`host/app.py`, `voice/server.py`) | `WorkIQScopes.DefaultScope` | `api://workiq.svc.cloud.microsoft/.default`; for the Host and Voice ports |
| `PRINCIPAL`, `SELF`, `MARGO`, `INTENTS` | `Subjects.Principal`, `Self`, `Margo`, `Intents` | |
| `ATTENDED`, `UNATTENDED`, `THIRD_PARTY`, `MODES` | `RunModes.Attended`, `Unattended`, `ThirdParty`, `All` | |
| `OBO`, `DELEGATION`, `PRINCIPAL_ACCESS` | `PrincipalAccessModes.Obo`, `Delegation`, `All` | |
| `TOOL_ARGS`, `WRITE_TOOLS`, `SINGLE_PATH_ARG`, `MUST_BE_EMPTY`, `CONTROL_ARGS` | `GuardPolicy.ToolArgs`, `WriteTools`, `SinglePathArg`, `MustBeEmpty`, `ControlArgs` | Frozen, ordinal |
| `ROOTS`, `USER_DIRECTORY_READS`, `BLOCKED_SEGMENTS`, `DELEGABLE`, `PATH_KEYS` | `GuardPolicy.Roots`, `UserDirectoryReads`, `BlockedSegments`, `Delegable`, `PathKeys` | |
| `RETRIEVE_CAPABILITIES`, `REJECTED_NO_EFFECT`, `THIRD_PARTY_TOOLS`, `UNKNOWN_WRITE` | `GuardPolicy.RetrieveCapabilities`, `RejectedNoEffect`, `ThirdPartyTools`, `UnknownWrite` | |
| `MAX_FETCH_PATHS`, `MAX_RETRIEVE_QUERIES`, `MAX_PATH` | `GuardPolicy.MaxFetchPaths`, `MaxRetrieveQueries`, `MaxPath` | `MaxPath` counts code points, as `len()` does |
| `GuardConfigError` (a `ValueError`) | `GuardConfigException` (an `ArgumentException`) | Core's convention for `ValueError`s |
| `TokenUnavailable` | `TokenUnavailableException` | Its message reaches the model in the `blocked` reason |
| `TransportError`, `TransportTimeout` | `TransportException`, `TransportTimeoutException` | Owned by `Margo.Cloud.WorkIQ` |
| `_Refused`, `_Unavailable` | internal `RefusedException`, `UnavailableException` | |
| `CallBudget(limit)`, `.limit`, `.used`, `.charge()` | `CallBudget(int)`, `Limit`, `Used`, `Charge()` | Thread-safe |
| `_utc_now` | `StateStore.UtcNow()` | `isoformat(timespec="microseconds")` |
| `_canonical` | internal `GuardPolicy.Canonical` | `PyJson.Dumps(sortKeys: true, ",", ":", ensureAscii: false)` |
| `_check_path`, `_is_message`, `_check_json_body`, `_validate` | internal `GuardPolicy.CheckPath`, `IsMessage`, `CheckJsonBody`, `Validate` | |
| `_status_codes` | `GuardPolicy.StatusCodes` (public), `IsSuccess`, `IsRejectedNoEffect` | Python's `isinstance(x, int)`: booleans count, `200.0` does not; values kept as `JsonNode`, so `true` and ints beyond 64 bits stay what Python records |
| `WorkIQGuard(binding, tokens, transport, *, journal, recorder, budget, principal_access, trigger_message_id)` | `WorkIQGuard(InstanceBinding, ITokenProvider, IWorkIQTransport?, IClaimJournal? journal, Action<GuardReceipt>? recorder, CallBudget? budget, string principalAccess, string? triggerMessageId)` | |
| `.binding`, `.transport`, `.journal`, `.budget`, `.principal_access`, `.trigger_message_id`, `.receipts` | `Binding`, `Transport`, `Journal`, `Budget`, `PrincipalAccess`, `TriggerMessageId`, `Receipts` | `Receipts` is a snapshot |
| `.call(tool, arguments, *, mode)` | `CallAsync(tool, arguments, mode, cancellationToken)` | Returns `GuardResult` |
| the returned dict | `GuardResult` (`Status`, `ReadAs`, `Tool`, `Reason`, `Data`; `ToJsonObject()`) | Python's key order |
| a receipt dict | `GuardReceipt` (`ToJsonObject()`) | Python's key order; see below |
| `_authorise`, `_delegated`, `_check_third_party_read`, `_is_principal_chat_post`, `_settle`, `_finish` | private methods of `WorkIQGuard` | |
| `model_tools()` | `GuardPolicy.ModelTools()` | Byte-identical JSON (fixture check) |
| the `tokens` object (`token_for(subject)`) | `ITokenProvider.TokenFor(subject)` | |

### claims.py

| Python | C# | Notes |
| --- | --- | --- |
| `DISPATCHED` | `LedgerClaimJournal.DispatchedEvent` | `workiq_dispatched` |
| `ClaimError` (a `ValueError`) | `ClaimException` (a State.Core `PyValueError`, `PythonType` `ClaimError`) | `InnerException` is the `raise ... from` cause |
| `_now` | `StateStore.UtcNow()` | |
| `LedgerClaimJournal(ledger)` | `LedgerClaimJournal(Ledger) : IClaimJournal` | Does not own the ledger |
| `.load(action_id, attempt_id, call_key="workiq")` | `Load(JsonNode? actionId, JsonNode? attemptId, string callKey = "workiq")` | Raw model values, as in Python |
| the claim dict | `ExecutionClaim` record | Values are detached copies |
| `.mark_dispatched(claim)`, `.settle(claim, state, status_code=None)` | `MarkDispatched`, `Settle(claim, state, int? statusCode)` | |
| `_approver`, `_dispatched` | private `Approver`, `Dispatched` | |
| `call_key` values `"workiq"`, `"reply"` | `LedgerClaimJournal.WorkIQCallKey`, `ReplyCallKey` | |

### instance.py

| Python | C# |
| --- | --- |
| `InstanceBinding.open_ledger(state_root)` | `InstanceLedger.OpenLedger(this InstanceBinding, string? stateRoot)`: `new Ledger(binding.Account, stateRoot)` through State.Work's public API |

## Behaviour reproduced

- **Order of checks** in `CallAsync`, as in `call`/`_authorise`: run mode, arguments object, known tool, `on_data_of`,
  unexpected arguments (sorted by code point), third-party principal reads, claims on read tools, per-tool argument
  checks and paths (`MUST_BE_EMPTY` first), third-party writes, third-party read rule, the delegation rewrite and
  re-validation, the principal-chat post (JSON object body), unattended writes, the journal, then the claim checks
  (already used in this guard, account, approver, `on_data_of`, exact canonical call). Then budget, token, durable
  dispatch, transport. Any other exception during authorisation is "malformed tool call", with a receipt.
- **Path policy** follows `_check_path` exactly: relative paths only, control characters, backslashes, encoded
  separators (`%2e`, `%2f`, `%5c`, `%00`, case-insensitive), Python `str.isspace` whitespace, empty/`.`/`..`
  segments, roots per subject, blocked segments at any depth, `/users/{id}` directory reads only, and under
  delegation only the bound principal's mailbox (case-sensitive object ID). Lower-casing follows `str.lower()` for
  the ASCII words compared (U+212A KELVIN SIGN becomes `k`; see Core's `PythonText.LowerAscii` note).
- **Token selection.** Principal intent uses the principal token under OBO and Margo's token under delegation
  (receipt `via: delegation`); self intent uses Margo's. A `TokenUnavailableException` gives its message; any other
  provider exception gives "token provider error" (its message never reaches the model). Never a fallback.
- **Trigger-message rule.** A third-party run may only use schema discovery or `fetch` paths that are
  `/me/messages/{trigger}` or its `attachments` (message ID case-sensitive, the rest not); no trigger, no message.
- **Principal-chat post.** The only claimless write: `create_entity`, self intent, one path
  `[/me]/chats/{principal_chat_id}/messages` with the chat ID recorded at hire (case-sensitive), a JSON object
  body, attended or unattended. An unbound chat allows none. A status-less answer is `unknown`.
- **Outcomes.** Reads: `ok`/`partial`/`error` from the integer status codes; transport errors are `error`;
  timeouts and other failures `unknown`. Writes: `ok` only with all codes 2xx and at least one; `error` only when
  every code is in `REJECTED_NO_EFFECT`; anything else `unknown` with `UnknownWrite`. A `TransportException` on a
  write is `failed` only for a status in `REJECTED_NO_EFFECT` (`statusCode: null` is unknown).
- **Claims.** `Load` reads with `Ledger.Show` and `Ledger.History`; `MarkDispatched` records `workiq_dispatched`
  (`attempt_id`, `dispatched_at`) inside `Ledger.Transaction` after re-reading the action, before the provider call,
  and nothing (network or model) runs inside that transaction; `Settle` calls `Ledger.Finish` with a
  `tool_result` receipt (`reference` `workiq:{attempt}`, `outcome`, `recorded_at`, `status_code` when known,
  `definitive_no_effect` for `failed`) or no receipt for `outcome_unknown`. A dispatched attempt is never loaded
  again ("reconcile it, never resend"). A journal failure while settling leaves the receipt's `journal` as
  `unsettled`; a failure to record dispatch sends nothing (`error`).
- **Receipts** hold `at`, `mode`, `tool`, `on_data_of`, `read_as`, `paths` (no query text), `status_codes`, then
  `action_id`/`attempt_id`, `via`, `journal`, `status`, `reason` when present, in Python's insertion order. Never
  tokens, bodies, queries or results. The recorder is called after the receipt is stored; an exception it throws
  propagates to the caller after the claim was settled, as in Python.
- **`jsonBody`** is parsed with State.Core's `PyJson.Loads` (Python's `json.loads`: `NaN`/`Infinity` accepted, the
  4300-digit integer limit is a `ValueError`, nesting deeper than 9998 is a `RecursionError`, which escapes the
  JSON check and makes the call a malformed tool call; the boundary was measured on guard.py under CPython 3.13).

## API for the other ports

- **Engine** (`engine.py`): `function_tools` is built from `GuardPolicy.ToolArgs` minus `GuardPolicy.WriteTools`
  and minus `MustBeEmpty` arguments, with the engine's own argument schemas and descriptions (they live in
  `engine.py`, not here); `GuardPolicy.ModelTools()` is `model_tools()` for anything that wants the guard's own
  schema. `_dispatch` calls `await guard.CallAsync(tool, arguments, mode, cancellationToken)` (Python:
  `asyncio.to_thread(guard.call, ...)`) and feeds `result.ToJsonObject()` to `_clip`. `Subjects` and `RunModes`
  hold the constants `engine.py` and `statecmd.py` import; `CallBudget` is also charged by the state tool.
- **Receipt recorder hook.** `recorder: Action<GuardReceipt>` (Python `recorder=`). The host's `_receipt_logger`
  (`json.dumps(receipt, sort_keys=True, default=str)`) is `PyJson.Dumps(receipt.ToJsonObject(), sortKeys: true)`.
- **Orchestrator** (`orchestrator.py`): `TurnTokens` implements `ITokenProvider` and throws
  `TokenUnavailableException("<subject> token not available for this turn")`; `_manager_from` uses
  `GuardPolicy.StatusCodes(data)` (compare with `PyJson.AsInt(code) == 404`); `guard.Binding.PrincipalChatId` is
  what the tests inspect.
- **Reply gate** (`replies.py`, Orchestrator): `IClaimJournal.Load(actionId, attemptId, LedgerClaimJournal.ReplyCallKey)`,
  then `claim.Account`, `claim.ApprovedBy` (`PyDynamic.Is(claim.ApprovedBy, binding.PrincipalId)`), `claim.Call`
  compared with `{"to": sender, "text": text}` by `PyJson.PyEquals`, `MarkDispatched`, and `Settle` after sending.
- **Voice, Host:** `WorkIQGuard`, `CallBudget`, `WorkIQScopes.DefaultScope`, `InstanceLedger.OpenLedger`.

## Deliberate deviations

1. **Async with cancellation.** `CallAsync` awaits `IWorkIQTransport.CallAsync`. Python has no cancellation:
   - already cancelled on entry: `OperationCanceledException`, nothing charged, no token asked for, no receipt;
   - cancelled by the caller's token while Work IQ is being called: the request may have been sent, so a claim is
     settled `outcome_unknown` (never resent), an `unknown` receipt with reason "cancelled; effect unknown, not
     retried" is recorded, and the exception is rethrown. This also covers a transport that stops before sending
     (the guard cannot tell), which errs on the side of reconciliation;
   - an `OperationCanceledException` not caused by the caller's token is Python's "any other exception":
     `unknown`, "transport failure; effect unknown, not retried".
   The journal and token provider stay synchronous (SQLite; tokens are acquired before the turn, as in Python).
2. **Typed parameters.** `tool` and `mode` are `string?`; a non-string Python value (a list, a number) arrives as
   `null`, with the same outcome (refused, `null` in the receipt). `arguments` is any `JsonNode?`.
3. **Status codes as `int?` where Python passes them on.** `TransportException.StatusCode` is `int?`
   (`Margo.Cloud.WorkIQ` deviation 8). `IClaimJournal.Settle` takes `int?`; every value the guard settles with is
   2xx or in `REJECTED_NO_EFFECT`, so nothing is lost. Payload status codes stay `JsonNode`s in receipts.
4. **Typed results and receipts.** `GuardResult`/`GuardReceipt` instead of dicts; `ToJsonObject()` gives Python's
   dict (key order, omitted keys). Receipts are immutable, so the recorder gets the stored receipt itself where
   Python passes a `dict()` copy. `GuardResult.ToJsonObject()` copies the payload each time.
5. **Thread safety.** Receipts and the in-memory set of used claims are behind a lock and `CallBudget.Charge` is
   atomic, so one guard may serve concurrent calls (Python's engine calls it one at a time). The ledger behind a
   `LedgerClaimJournal` is one SQLite connection whose transactions nest by connection state, so the guard runs
   every journal call (`Load`, `MarkDispatched`, `Settle`) under its own journal lock, never across the Work IQ call.
   Without it, a `MarkDispatched` overlapping another call's journal transaction became a savepoint inside it and
   was lost if that transaction rolled back, while the write was still sent (found in review; covered by
   `Concurrent_claimed_writes_never_use_the_journal_at_the_same_time`). Anything else sharing that ledger (the state
   tool, the office reader) must still not use it concurrently with the guard, as in Python.
6. **Construction.** A `null` transport is a `GuardConfigException`, like a transport without `endpoint`.
   `CallBudget` takes an `int`; a negative limit is `ArgumentOutOfRangeException` (Python: `ValueError`).
7. **Journal exceptions from the database.** A SQLite failure inside `Load` is reported in the refusal reason with
   Microsoft.Data.Sqlite's message where Python shows sqlite3's. A ledger that is not this guard's account is caught
   by the account check either way.

## Not ported here

- **`replies.py`** (`ReplyGate`, `load_reply_policy`) belongs to `Margo.Cloud.Orchestrator` (see `cloud/README.md`).
  Its two tests in `tests/test_autopilot_guard.py` that use the journal (`test_reply_gate_*`) are ported here at
  the journal level (what the gate loads, compares, dispatches and settles); the gate's own decisions and
  `ReplyPolicyTests` are for the Orchestrator port.
- **`InstanceBindingTests`** of `tests/test_autopilot_guard.py` are in `Margo.Cloud.Core.Tests`.
- The `test_autopilot_runtime.py` engine and orchestrator tests that pass through the guard belong to those ports.

## Tests and evidence

`cloud/tests/Margo.Cloud.Guard.Tests` (1770 test cases), fakes for the transport and tokens, a real temporary
`Ledger` for claims:

- `GuardPolicyTests`: every test of `GuardPolicyTests` in `tests/test_autopilot_guard.py` (19).
- `GuardedWriteTests`: the 13 guard tests of `GuardedWriteTests` there, against the real approval journal (receipts:
  action and attempt IDs, journal state, paths, status codes; ledger receipts: `status_code`, `reference`,
  `definitive_no_effect`; the `workiq_dispatched` event).
- `ClaimJournalTests`: `claims.py` against the real ledger (every `ClaimError` message, the claim fields, the
  dispatched event, settle receipts, the approver of the current revision, another instance's account) and the two
  reply-gate tests at the journal level. Expected messages were checked against `claims.py` on the same scenarios.
- `DelegationTests`: `DelegationTests` of `tests/test_autopilot_runtime.py` (5).
- `GuardRuntimeTests`: cancellation, the recorder hook, result and receipt shapes, payload copies, concurrent
  budgets, concurrent claimed writes (the journal is never entered by two calls at once), the `jsonBody` nesting
  boundary, constants, `OpenLedger`.
- `GuardParityTests`: 1709 scenarios (809 hand-written for every branch, 900 seeded random; 1717 calls) recorded from
  `guard.py` by `Parity/guard_cases.py` into `Parity/guard_cases.json`, replayed against the C# guard: same
  response, receipt (apart from its time stamp), transport call (tool, arguments, bearer), token requests and journal
  interaction, compared as `json.dumps` text. Also `model_tools()` and `_status_codes`. `Fixture_is_current_with_guard_py`
  runs `guard_cases.py --check`, so a change to `guard.py` fails the suite until the fixture is regenerated.

```sh
cd cloud && dotnet test --project tests/Margo.Cloud.Guard.Tests/Margo.Cloud.Guard.Tests.csproj
python3 cloud/tests/Margo.Cloud.Guard.Tests/Parity/guard_cases.py --check   # fixture still matches guard.py
python3 cloud/tests/Margo.Cloud.Guard.Tests/Parity/guard_cases.py --write   # regenerate after guard.py changes
```

Verification at commit (re-run after the review fix): `dotnet build cloud/Margo.Cloud.slnx` 0 warnings;
`dotnet test --solution Margo.Cloud.slnx` 4809 passed, 0 failed, 0 skipped; `python3 tools/state_parity.py --check` 104 scenarios passed;
`python3 -m unittest discover -s tests -p 'test_*.py'` 518 OK (4 skipped); `./tools/check-clean.sh` clean (the
random path segment `Users` became `USERS` so the fixture holds no `/Users/<name>` text).

Independent review (on top of the port): 22,036 further scenarios generated from `guard.py` with other seeds and
wider inputs (random characters in paths, Unicode whitespace and case folding, lone surrogates, path lengths at
the 2048-code-point boundary with astral and surrogate characters, unexpected keys that sort differently by code
point and by UTF-16 unit, claim fields of every JSON type, boolean and 64-bit-overflowing status codes, repeated
calls) were replayed through `GuardParityTests` with the same comparison; all matched. They are not checked in
(the generator reused `guard_cases.py`'s fakes and `run`). The one defect found was the concurrent journal use in
deviation 5.

Not verified: anything against the real Work IQ endpoint or a real token provider; Python 3.12 (the image's
version; the fixture was recorded on 3.13). These are policy and contract tests, not model evaluations.

## Open questions

1. **Answers to `Margo.Cloud.WorkIQ`'s open questions** (its `PORTING.md` is not edited here): cancellation after
   dispatch settles `outcome_unknown` and rethrows (question 2); `_status_codes` uses State.Core's `PyJson`
   (`IsIntOrBool`, `AsInt`), so `PythonJson` can stay internal (question 3); the tests name `statusCode: null`
   (question 5).
2. **`Margo.Cloud.Core`'s `PORTING.md`** says the Orchestrator and Voice open the ledger through State.Work with
   `binding.Account`; `InstanceLedger.OpenLedger` here does exactly that and can be what they call.
3. **`PyDynamic`** (used for Python value rules on claims and arguments) still lives in `Margo.Cloud.State.Work`;
   if it moves to State.Core, only the `using` changes here.
4. **Hosted writes.** As in Python, the orchestrator builds its guard without a journal, so hosted turns have no
   claimed-write path today (the engine also refuses write tools before the guard). The journal path is complete
   and tested for when that changes; the `LedgerClaimJournal`'s ledger then needs one owner per call.

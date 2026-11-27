# Margo.Cloud.Guard: porting notes

Port of `guard.py` (Python hosted runtime, removed at the cutover) (the Work IQ guard: the only path from a hosted Margo's model to
Microsoft 365) and `claims.py` (Python hosted runtime, removed at the cutover) (execution claims through the approval journal), plus
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

## Behavior reproduced

- **Order of checks** in `CallAsync`, as in `call`/`_authorise`: run mode, arguments object, known tool, `on_data_of`,
  unexpected arguments (sorted by code point), third-party principal reads, claims on read tools, per-tool argument
  checks and paths (`MUST_BE_EMPTY` first), third-party writes, third-party read rule, the delegation rewrite and
  re-validation, the principal-chat post (JSON object body), unattended writes, the journal, then the claim checks
  (already used in this guard, account, approver, `on_data_of`, exact canonical call). Then budget, token, durable
  dispatch, transport. Any other exception during authorization is "malformed tool call", with a receipt.
- **Path policy** follows `_check_path` (plus the percent-encoding rule under "Privacy and hardening"): relative paths only, control characters, backslashes, encoded
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
  tokens, bodies, queries or results; since the review below, never identifiers or model-chosen text either. The recorder is called after the receipt is stored; an exception it throws
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
   - already canceled on entry: `OperationCanceledException`, nothing charged, no token asked for, no receipt;
   - canceled by the caller's token while Work IQ is being called: the request may have been sent, so a claim is
     settled `outcome_unknown` (never resent), an `unknown` receipt with reason "canceled; effect unknown, not
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
8. **The principal's token must be the principal's (security review).** Before a principal call is sent (on-behalf-of
   access), the guard reads the subject of the token it was handed: a compact JWS whose payload has no `oid`, or an
   `oid` other than the bound principal's directory object ID (compared without case), is `blocked`
   (`token unavailable for principal: the token is not the bound principal's`), nothing is sent and a claim settles
   `failed`. Python sent whatever `token_for("principal")` returned. The token is never verified or recorded here;
   a token that is not a three-segment JWS (opaque or encrypted) cannot be read and is sent as before. Margo's own
   token and delegated principal reads (Margo's token) are not checked. Tests: `TokenSubjectTests`.

## Delegation-mode changes after the cutover (Nov 21, 2026)

Deliberate behavior changes, decided by the owner after the first live delegation run; Margo Cloud now differs from the
frozen Python `guard.py` here, and the affected answers in `Parity/guard_cases.json` were regenerated:

1. **`self` reaches what is shared with Margo.** `Roots[self]` adds `teams`, `sites` and `drives`, so Margo's own token
   can read channels of teams she belongs to and files and sites shared with her. Reads only in effect: every write still
   needs a live claim approved by the manager, and a run someone else triggered may still only open its triggering
   message. Python allowed `me`, `users`, `chats` only. Twelve recorded cases changed: the reads now succeed and are sent;
   the writes and third-party runs among them stay refused, for their next reason.
2. **Delegation refusals point to what works.** `DelegationSearchHint` (principal `ask`/`retrieve`) and `DelegationPathHint`
   (a principal path outside mail and calendar) name the `fetch` paths for the manager's mail and calendar and the `self`
   alternative, so the model retries correctly. Statuses are unchanged (`unavailable`); 62 recorded reasons changed text.

## Privacy and hardening (review, Nov 23, 2026)

Deliberate behavior changes after a security and privacy review; Margo Cloud differs from the frozen Python `guard.py`
in each. Tests: `HardeningTests` (every case fails without its fix, except the concurrency test, which confirms).

1. **Percent-encoding cannot disguise a path segment.** Python refused only `%2e`, `%2f`, `%5c` and `%00`, so
   `/me/%61uthentication/methods` passed the blocked-segment check. Now every escape in the path part must be two hex
   digits and must not stand for an unreserved character (a letter, a digit, `-`, `.`, `_`, `~`), `/`, `\`, `?`, a
   control character or `%` itself (double encoding); a run of escapes must be valid UTF-8; and the roots, blocked
   segments, delegable names and directory reads are matched against the segment decoded once. Refusal:
   `GuardPolicy.DisguisedSegment` (the four old escapes keep "encoded path separators are not allowed"). Still accepted,
   and sent as given: `%3D`, `%2B`, `%40`, `%3A`, `%20`, `%26`, `%23` and UTF-8 bytes, as message IDs, addresses, chat
   IDs and file names carry them. Limitation: a file whose name contains `%` or `?` cannot be addressed by path (use its
   item ID). No recorded case changed (the fixture holds only the four old escapes).
2. **`ask` document links stay inside Microsoft 365.** Python accepted any `https://` string in `fileUrls`, so a
   prompt-injected model could put data into a link to its own host. Now the host must be one tenant label under
   `GuardPolicy.FileHostSuffixes` (`.sharepoint.com`, `.sharepoint.us`: `contoso` or `contoso-my`), with no user-info,
   port, escape or non-ASCII in the host and no control characters, whitespace or backslashes anywhere; at most
   `MaxFileUrls` (20) links of at most `MaxPath` characters. Path, query and fragment stay free (`?web=1`, `?d=`,
   `?csf=1`). Refusal: `GuardPolicy.ForeignFileUrl`. `WorkIQGuard.FileHosts` (init-only, default null) replaces the
   suffix rule with an exact host list: the way to pin links to the organization's own hosts, and to serve a cloud with
   other suffixes. The eleven recorded cases with `https://contoso.example.com/d` are unchanged: `GuardParityTests`
   pins `FileHosts` to that fictional host; the default rule is covered by `HardeningTests`.
   Residual risk: without `FileHosts`, another tenant's SharePoint host is accepted.
3. **Receipts and reasons hold no identifiers or model-chosen text** (`ReceiptText`). The host logs every receipt.
   - `paths` are templates: a segment is kept only when it is a known Graph resource word (`ReceiptText.Words`, an
     allow-list, case as given); anything else is `{id}` (`{user}` after `users`); a path addressed by name
     (`root:/Folder/File.docx:`) is `{path}`; function or key arguments are `name(...)` (`{function}(...)` for an
     unknown name). `/users/dana@example.com/messages/AAMk=` is recorded as `/users/{user}/messages/{id}`. What is
     sent to Work IQ is unchanged. A word missing from the list only makes a receipt less specific.
   - `tool` and `mode` are kept when they are identifiers (ASCII letters, digits, `_`, `-`, at most 64), else
     `{other}`; `on_data_of` is kept when it is `principal`, `self`, `margo` or empty, else `{other}`.
   - Reasons (also what the model is told): the refused root is named only when it is a known resource word, else
     `'/{id}'`; unexpected argument names are listed only when they are identifiers (at most 32 characters, at most
     eight), the rest counted (`z, 1 other name`); a claim's `on_data_of` is named only when it is a subject word; a
     journal failure that is not a `ClaimException` is "the approval journal could not be read" (its message is not
     quoted; deviation 7 no longer applies to the reason); a token provider's message is clipped to 160 printable
     ASCII characters.
   - `Parity/guard_cases.json`: 256 recorded cases changed, strings only (199 receipt `paths`, 56 `reason`s in receipt
     and response, one receipt `on_data_of`); no status, sent call, token request or journal interaction changed. The
     new values were produced by the C# guard and substituted in place, object by object, by a script that asserted
     only those three fields differ; the file was not re-serialized (256 lines differ).
4. **An oversized answer.** `Margo.Cloud.WorkIQ` now bounds the response body (`TransportResponseTooLargeException`).
   A read gets `error` with `WorkIQGuard.ResponseTooLarge` (ask for less); a write stays `unknown`, as any transport
   error without a status code does.
5. **Concurrent reads are safe (verified, no change needed).** `CallBudget.Charge` is a compare-and-swap loop and
   receipts are appended under a lock; the per-call draft is never shared.
   `Concurrent_reads_leave_one_intact_receipt_each_and_never_exceed_the_budget` runs 400 overlapping calls. What a
   caller running reads in parallel must know: receipts are stored, and the recorder is called, in completion order;
   the recorder and the token provider are called from several threads at once and must be thread-safe themselves.
6. **Another mailbox, or a write (re-audit, no gap found).** New adversarial cases: mixed-case `/Me/`, `/me//messages`,
   `/users/{other}/...` and `/users/{principal}/...` as `self` (Margo's token holds the delegation, so `self` must never
   name a mailbox), `$batch`, `$value`, `/me/sendMail`, `reply`, encoded and double-encoded separators, a query string
   that looks like a path, every write tool in every mode, subject and access mode without a claim.

### Requests to other components (from this review)

- **Host / Orchestrator / Voice:** set `WorkIQGuard.FileHosts` to the tenant's own SharePoint and OneDrive hosts
  (for example from configuration: `contoso.sharepoint.com`, `contoso-my.sharepoint.com`) where the guards are built
  (`Orchestrator.cs`, `VoiceService.cs`); until then the suffix rule applies.
- **Host:** the receipt line it logs needs no change; `VERIFY.md`'s example (`"paths": ["/me/manager"]`) still holds,
  but paths with identifiers now show `{id}`/`{user}`.
- **Engine:** read calls may run in parallel through one guard, provided the recorder it passes is thread-safe.
  The model-facing reason for unexpected argument names is unchanged for identifier-like names, so
  `engine_answers.jsonl` is not affected.

## Not ported here

- **`replies.py`** (`ReplyGate`, `load_reply_policy`) belongs to `Margo.Cloud.Orchestrator` (see `cloud/README.md`).
  Its two tests in the former Python guard tests that use the journal (`test_reply_gate_*`) are ported here at
  the journal level (what the gate loads, compares, dispatches and settles); the gate's own decisions and
  `ReplyPolicyTests` are for the Orchestrator port.
- **`InstanceBindingTests`** of the former Python guard tests are in `Margo.Cloud.Core.Tests`.
- The the former Python runtime tests engine and orchestrator tests that pass through the guard belong to those ports.

## Documents the manager asks for (Nov 25, 2026)

`AllowsOwnDocuments` (off by default; the orchestrator turns it on for the guard of `create_document` only) lets an
attended turn make two writes without a claim, as Margo (`self`): `/office-agent-mcp/generate_artifact` with exactly
`appName` (Word, Excel, PowerPoint), `taskDescription` and optional `userRequest`; and `.../items/{id}/invite` whose
only recipient is `{"objectId": <bound manager>}`, one role (`read` or `write`), `requireSignIn: true`, optional
`sendInvitation` and a message of at most 1000 characters. Any other shape needs a claim. The manager decided this
standing permission (it is invisible to anyone else); see `docs/safety.md`. `self` gains the roots `shares` (reading an
item by its sharing link) and `office-agent-mcp`. Tests: `OwnDocumentTests`.

## Tests and evidence

`cloud/tests/Margo.Cloud.Guard.Tests` (1907 test cases), fakes for the transport and tokens, a real temporary
`Ledger` for claims:

- `HardeningTests`: the review fixes above.
- `GuardPolicyTests`: every test of `GuardPolicyTests` in the former Python guard tests (19).
- `GuardedWriteTests`: the 13 guard tests of `GuardedWriteTests` there, against the real approval journal (receipts:
  action and attempt IDs, journal state, paths, status codes; ledger receipts: `status_code`, `reference`,
  `definitive_no_effect`; the `workiq_dispatched` event).
- `ClaimJournalTests`: `claims.py` against the real ledger (every `ClaimError` message, the claim fields, the
  dispatched event, settle receipts, the approver of the current revision, another instance's account) and the two
  reply-gate tests at the journal level. Expected messages were checked against `claims.py` on the same scenarios.
- `DelegationTests`: `DelegationTests` of the former Python runtime tests (5).
- `GuardRuntimeTests`: cancellation, the recorder hook, result and receipt shapes, payload copies, concurrent
  budgets, concurrent claimed writes (the journal is never entered by two calls at once), the `jsonBody` nesting
  boundary, constants, `OpenLedger`.
- `TokenSubjectTests` (security review, deviation 8): a principal token for someone else or without a subject is
  blocked and never sent, claimed writes included; the bound principal's, opaque, self and delegated tokens are sent.
- `GuardParityTests`: 1709 scenarios (809 hand-written for every branch, 900 seeded random; 1717 calls) recorded from
  `guard.py` into `Parity/guard_cases.json` (frozen at the cutover; see **Cutover**), replayed against the C# guard: same
  response, receipt (apart from its time stamp), transport call (tool, arguments, bearer), token requests and journal
  interaction, compared as `json.dumps` text. Also `model_tools()` and `_status_codes`. Until the cutover
  `Fixture_is_current_with_guard_py` ran `guard_cases.py --check`; both were removed with `guard.py`.

```sh
cd cloud && dotnet test --project tests/Margo.Cloud.Guard.Tests/Margo.Cloud.Guard.Tests.csproj
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
4. **Hosted writes** (resolved Nov 23, 2026, below): model turns still get a guard without a journal; the approved
   action runs through a guard with the `LedgerClaimJournal`, whose ledger the decision owns for that one call.

## Approval cards (Nov 23, 2026)

- `DelegableWrites` (`sendmail`): under delegation an approved `/me/sendMail` is rewritten to
  `/users/{manager}/sendMail`, for write tools only; reads of that path stay unavailable. Deliberate deviation: the
  recorded case "claimed principal delegation" expected `unavailable` and is skipped in `GuardParityTests`
  (`DeliberateDeviations`); `ApprovalWriteTests` covers the new outcome.
- `PreviewWrite` / `WriteProblem`: the claimed-write checks without a claim, so a proposal is refused before the
  manager is asked if it would be refused when run.
- The orchestrator now builds a guard with the ledger's claim journal (`LedgerClaimJournal`) for one call: the
  manager's approved action, after their tap on its card. Model turns still get a guard without a journal.

## Cutover

The Python hosted runtime was removed in the Phase 6 cutover. It was the specification of this
port; the C# code is now the implementation of record, and the desktop state helpers in
`skills/chief-of-staff/scripts/` stay the specification of the state commands (`tools/state_parity.py`).

- `Parity/guard_cases.json` is frozen: `guard_cases.py --check` passed just before the cutover, and the script and
  `Fixture_is_current_with_guard_py` were removed with `guard.py`. The 1709 replayed scenarios remain the guard's
  regression baseline; a deliberate change to the guard's behavior now edits the fixture by hand, with review
  (never by re-serializing the file: one case per line, edit only the lines that change).

## Work IQ through the MCP SDK (Nov 25, 2026)

`ServerToolsAsync` asks the transport for Work IQ's own tool list with Margo's own token (never the manager's), not
charged to the call budget, and returns an empty list when it cannot. A read that fails with a JSON-RPC error returns
the server's text to the model as `data.error`; the receipt keeps the fixed reason "Work IQ error", because the text
may echo the request. Tests: `ServerToolsTests`.

## Discovery, paging, Copilot agents and token refresh (Nov 25, 2026)

- Work IQ's own path families (`office-agent-mcp`, `pages`, `engage`, `sharepoint`, `copilot`) can be read; see
  "No fixed list of roots" below.
- A Microsoft Graph paging or delta link (`https://graph.microsoft.com/v1.0/...` or `/beta/...`) in `fetch`'s
  `entityUrls` or `call_function`'s path is sent as the relative path it names, after meeting every path rule.
- `ask` may name a Copilot agent (`agentId`, as `list_agents` gives it: 1-128 letters, digits and `._:-`). Every other
  tool still refuses `agentId` and `backend`.
- `IRefreshingTokenProvider`: when a read gets HTTP 401, the guard asks once for a fresh token for the same subject and
  sends the read once more (never a write; never the same token; a principal token must still be the bound principal's).
- Receipt words gained the Work IQ families' path words. Tests: `DiscoveryPolicyTests`, `ServerToolsTests`.

## No fixed list of roots (Nov 25, 2026)

Deliberate deviation from Python's `ROOTS`: any first path segment Work IQ serves may be read, for either subject, so
whatever `search_paths` finds can be used. What still holds: the token decides whose data it is; `/users/{id}` reaches
only profile, manager and direct reports; blocked segments (`authentication`, `servicePrincipals`, and `$batch`, whose
body carries requests no path rule would see), encoded separators and traversal are refused; delegation reaches only
the manager's own mail and calendar; every write needs a live claim. The frozen Python cases that expected a root
refusal are skipped as deliberate deviations (`GuardParityTests`).

## The manager channel (Nov 25, 2026)

`AllowsManagerChannel` (set only on the manager channel's guards) lets attended and unattended runs, never third-party
ones, make two writes without a claim, as Margo: `/me/sendMail` with exactly one `toRecipients` entry equal to
`ManagerMail` (case-insensitive), no copies, attachments or reply-to, a text or HTML body of at most 20,000 characters;
and an `invite` to the manager alone (the same rule as the documents grant, factored into `IsManagerInvite`). Posts to
the manager's 1:1 chat use the existing principal-chat rule, with the chat ID the channel resolves. The owner decided
this standing permission (Nov 25, 2026). Tests: `ManagerChannelTests`.

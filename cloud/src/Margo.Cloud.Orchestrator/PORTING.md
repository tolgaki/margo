# Margo.Cloud.Orchestrator: porting notes

Port of `orchestrator.py` (Python hosted runtime, removed at the cutover) (one inbound activity: identity, manager lookup, one-to-one
rule, tokens, the turn under the state lease), `office.py` (requests from other people as untrusted candidates),
`replies.py` (the manager's `Replies to other people` setting and `ReplyGate`) and `statesync.py` (the leased Blob
bundle). The Python code is the specification; everything not listed under "Deliberate deviations" is meant to behave
the same.

## What maps to what

### orchestrator.py

| Python | C# | Notes |
| --- | --- | --- |
| `TURN_CALL_BUDGET`, `STATE_CALL_BUDGET`, `PERSONAL` | `Orchestrator.TurnCallBudget`, `StateCallBudget`, `Personal` | 40, 40, `personal` |
| `VOICE_UNAVAILABLE`, `NO_MANAGER`, `MANAGER_UNKNOWN`, `GROUP_REFUSED`, `STATE_UNSAFE`, `TURN_FAILED` | `Orchestrator.VoiceUnavailable`, `NoManager`, `ManagerUnknown`, `GroupRefused`, `StateUnsafe`, `TurnFailed` | Identical text (a test compares them with the Python module) |
| `REPLIES_COMMAND`, `VOICE_COMMAND` | `ManagerCommands.ReplyMode(text)`, `ManagerCommands.IsVoice(text)` | Matched by hand with Python's `\s` and `IGNORECASE` tables (see deviation 6) |
| `ManagerLookupFailed` | `ManagerLookupFailedException` | Message is the guard status, or `no status` |
| `TurnTokens(tokens)`, `.token_for(subject)` | `TurnTokens(IReadOnlyDictionary<string,string?>) : ITokenProvider`, `TokenFor` | `TokenUnavailableException("<subject> token not available for this turn")`; no fallback |
| `_manager_from(result)` | `ManagerDirectory.From(GuardResult)` returning `Manager(Id, DisplayName)` or null | 404-only error is "no manager"; any other non-ok status throws |
| `set_reply_mode(preferences, mode)` | `ReplySetting.SetReplyMode(path, mode)` | Strict UTF-8, universal newlines, every setting line replaced or one appended, file 0600, directory 0700 |
| the notes after `/replies MODE` | `ReplySetting.ChangedNote(mode)` | |
| `Orchestrator(engine=, transport=, state_root=, store_for=, principal_access=, recorder=, instance=, state_container=, voice_url=)` | `Orchestrator(Engine, IWorkIQTransport, stateRoot, Func<string, ILeaseStore> storeFor, OrchestratorOptions)` | Options: `PrincipalAccess`, `GuardRecorder`, `StateRecorder`, `Instance` (`AgentInstance`), `StateContainer`, `VoiceUrl`, `StateCliCommand`, `StateRenewInterval`, `Logger` |
| the `event` dict | `InboundEvent` record | `agent_identity_id`, `agent_user_id`, `sender_id`, `sender_name`, `conversation_id`, `conversation_type`, `activity_id`, `channel`, `text` |
| `handle(event, get_token)` returning `{"reply", "status"}` | `HandleAsync(InboundEvent, Func<string, Task<string?>> getToken, CancellationToken)` returning `TurnReply(Reply, Status)` | `TurnReply.ToJsonObject()` is the dict; statuses in `TurnStatuses` |
| `_paths(account)` | `Orchestrator.Paths(account)` | `StateStore.StatePath(account, stateRoot)` (State.Core's `margo_store.state_path`) and `preferences.md` beside the database |
| `state_sync(binding)` | `Orchestrator.StateSyncFor(binding)` | Per-instance container with a `sha256(account)[:40]/` prefix, else `StateSync.ContainerName(account)` |
| `_manager(event, margo_token)` | private `LookUpManagerAsync` | Bootstrap binding with manager `unbound-manager`, guard budget 1, `fetch` `/me/manager?$select=id,displayName`, `on_data_of: self`, unattended, on every turn |
| `_run`, `_state_commands`, `_manager_turn`, `_third_party_turn` | private `RunAsync`, `NewStateCommands`, `ManagerTurnAsync`, `ThirdPartyTurn` | |
| `_conversations` (continuity map), `_initialised` | `ConcurrentDictionary`s | Key `sha256(account + "|" + conversation_id)`; a turn without a response ID forgets the conversation |
| `logger = logging.getLogger("margo.autopilot")` | `OrchestratorOptions.Logger` (`ILogger`) | Same messages: `manager lookup failed: <status or exception type>` (warning), `turn failed` (error, with the exception) |

Every status of `handle` is reached by the port and by a test: `no_agent_token`, `wrong_instance`, `invalid_binding`,
`manager_lookup_failed`, `no_manager`, `group_refused`, `state_unsafe`, `error`, `answered`, `stopped`, `voice_link`,
`setting_changed`, `queued`.

Order and rules, as in Python: Margo's token first (an exception from the getter counts as none); the instance check
(per-instance hosting); the manager lookup through the guard with Margo's own token on every turn (a changed manager is a
different account and different state); notices only in a one-to-one Teams chat with a sender (mail is never one-to-one);
mail is never a conversation turn, even from the manager; the manager outside a one-to-one chat is refused before any
token, state or model call; the principal token is requested only for the manager and only under `obo`; the turn runs
under `StateSync`; for the manager the ledger is created and closed, memory and task state are initialised once per
account (`StateCommands.InitialiseAsync`), then `/voice`, `/replies MODE` or the engine (attended, guard budget 40, state
budget 40, office reader, the manager's preferences, the conversation's previous response ID); for anyone else the
message is queued as a candidate and the reply gate decides (`approve` is treated as `none`; mail always `none`). A
`SyncException` is `state_unsafe` (notice to the manager only); any other failure is `error` (notice to the manager
only, logged).

### office.py

| Python | C# |
| --- | --- |
| `OFFICE_FAMILY`, `CONVERSATION_FAMILY`, `CONVERSATION_SCOPE`, `OFFICE_NOTE`, `EXCERPT_LIMIT` | `OfficeQueue.OfficeFamily`, `ConversationFamily`, `ConversationScope`, `OfficeNote`, `ExcerptLimit` |
| `_conversation_key(sender_id, conversation_id)` | `OfficeQueue.ConversationKey` |
| `OfficeQueue(ledger)`, `.record(...)`, `.seen_before(...)`, `.pending(limit=20)` | `OfficeQueue(Ledger)`, `Record(channel, externalId, senderId, senderName, text, conversationId, observedAt)`, `SeenBefore`, `Pending(limit)` |
| `OfficeReader(open_ledger)`, `.pending(limit)` | `OfficeReader(Func<Ledger>, limit) : IOfficeRequests` (the engine's interface), `Pending()`, `PendingAsync()` |

Through State.Work's public API only: `Ledger.Source` (the per-sender-and-conversation marker source, then the request
source with the untrusted evidence), `Ledger.Ingest` (a candidate, claim key `office:<channel>:<id>`),
`Ledger.FindSource` (one indexed lookup for "written here before"), `Ledger.List("decisions")`. The excerpt is
`text.strip()[:500]` in code points, or `(no text)`; names fall back to `(unknown)`/`someone`; a missing message ID
fails in the ledger as Python's `None` does (and is formatted `None` in the web link and claim key). `Pending`'s limit
is checked after an item is added, as in Python.

### replies.py

| Python | C# |
| --- | --- |
| `NONE`, `ACKNOWLEDGE`, `APPROVE`, `MODES` | `ReplyModes.None`, `Acknowledge`, `Approve`, `All` |
| `SETTING_LABEL`, `ACKNOWLEDGEMENT`, `_SETTING` | `ReplyPolicy.SettingLabel`, `Acknowledgement`, private regex (ASCII classes only, multiline) |
| `load_reply_policy(markdown)` -> `{"mode", "issues"}` | `ReplyPolicy.Load(string?)` -> `ReplyPolicy(Mode, Issues)` |
| `ReplyGate(binding, mode, journal=None)` | `ReplyGate(InstanceBinding, mode, IClaimJournal?)` (`ArgumentException("unknown reply mode")`) |
| `.decide(sender_id, conversation_id, proposed_text, *, manager_name, action_id, attempt_id, first_in_conversation)` | `Decide(senderId, conversationId, proposedText, managerName, actionId, attemptId, firstInConversation)` -> `ReplyDecision(Send, Text, Reason, Claim)` |
| `._approved(...)`, `.settle(claim, state, status_code)` | private `Approved`, `Settle` |

Reasons are Python's strings. Approve mode loads the `reply` call through Guard's `IClaimJournal`
(`LedgerClaimJournal.ReplyCallKey`), checks the account and the approver (`PyNode.Is(claim.ApprovedBy, principal)`),
compares the call with `{"to": sender, "text": text}` by `PyJson.PyEquals`, marks it dispatched, and the caller settles
it after sending.

### statesync.py

| Python | C# |
| --- | --- |
| `BUNDLE`, `MARKER` | `StateSync.Bundle`, `StateSync.Marker` |
| `SyncError` | `SyncException` (public: the Host's Azure store throws it too) |
| `container_name(account)` | `StateSync.ContainerName(account)` (`margo-` + 40 hex) |
| `StateSync(database, store, *, account, preferences, renew_seconds=20, prefix="")` | `StateSync(database, ILeaseStore, account, preferences, TimeSpan? renewInterval, prefix)` |
| `.run(work, now=None)` -> `{"result", "restored"}` | `RunAsync<T>(Func<CancellationToken, Task<T>>, DateTimeOffset? now, CancellationToken)` -> `StateSyncOutcome<T>(Result, Restored)` |
| `_pack`, `_install`, `_discard_local`, `_check_db`, `_local_etag`, `_set_local_etag`, `_renew` | internal `Pack`, `Install`, `DiscardLocal`, `CheckDb`; private `LocalETag`, `SetLocalETag`, `RenewAsync` |
| the store (`lease(name)` context manager with `read`/`write`/`renew`, `put(name, bytes)`) | `ILeaseStore` (`AcquireAsync`, `PutAsync`) and `IStateLease` (`ReadAsync`, `WriteAsync`, `RenewAsync`, `DisposeAsync` = release) |
| the tests' `FakeLeaseStore` | `InMemoryLeaseStore` (public, for tests and local development) |

Per turn, as in Python: take the exclusive lease (renewed every interval while the turn runs); read the shared bundle;
none (or empty) discards the local database, its sidecars, the preferences and the marker; a different ETag than the
marker installs the bundle (zip with `manifest.json`, `margo.sqlite3`, optional `preferences.md`; manifest account and
database hashes checked; the database written to a private staging file, fsynced, checked with `PRAGMA quick_check`,
`margo_meta` and its account, sidecars removed, atomically renamed; preferences replaced or removed) and records the
ETag; run the work; a failed renewal is `SyncException` and nothing is saved; if the database exists, pack an
online-backup snapshot (checked the same way), write it under the lease, record the new ETag, and put a daily copy
`daily/YYYY-MM-DD.zip` (a failure there is ignored); on any failure the marker is removed so the next turn reinstalls the
shared state; the lease is always released (a failed release is ignored: it expires). The manifest is
`json.dumps({"account_sha256", "database_sha256", "taken_at": now.isoformat(timespec="seconds"), "format": 1}, sort_keys=True)`.

No SQLite write transaction is held across a network or model call anywhere in this project: the lease is a Blob
lease, the snapshot is a read (`BackupDatabase`), the office queue's ledger calls are each their own transaction and
the ledger is closed before the reply gate runs, and the manager turn closes its ledger before the engine starts.

## Deliberate deviations

1. **Typed event.** `InboundEvent` has nullable string properties. A missing key in Python's dict raised `KeyError`;
   here it is `null`. Where Python raised `KeyError` for a missing `agent_identity_id`/`agent_user_id` (status
   `manager_lookup_failed`), a null is refused by the binding check (`invalid_binding`, as Python's `None` is). A null
   `conversation_id` in a manager turn throws `PyTypeError` (`error`, as Python's `str + None`); a null `activity_id`
   from anyone else fails in the ledger (`error`, as Python's `KeyError` or `None`). Non-string values cannot occur.
2. **Two recorders and a logger.** Python's single `recorder` received guard and state receipts; they are different
   types here (`GuardRecorder`, `StateRecorder`). The `margo.autopilot` logger is an `ILogger` (the project references
   the ASP.NET Core shared framework for `Microsoft.Extensions.Logging` only; no new package version).
3. **Asynchronous lease store with cancellation.** `ILeaseStore`/`IStateLease` are async; disposing the lease is
   `__exit__`. Cancelling `HandleAsync`'s token throws `OperationCanceledException` (Python has no cancellation); a
   cancelled turn saves nothing and its marker is removed. A token getter that throws counts as no token, except for
   that cancellation.
4. **Zip and SQLite libraries.** Bundles are written with `System.IO.Compression` (deflate) and read back by
   `zipfile`; Python bundles are read here (both directions are tested against the Python `StateSync`). Every entry
   read is checked against its CRC-32 as `zipfile` checks it (`ZipArchive` does not; `StateSync.Crc32`), so a corrupt
   entry, including `preferences.md` and the manifest that no hash covers, is "state bundle is unreadable" as in
   Python. Any `InvalidDataException` while reading a bundle (not a zip, bad CRC, corrupt deflate data) is "state
   bundle is unreadable"; Python's `zlib.error` for corrupt deflate data escaped as a plain error (`error` rather than
   `state_unsafe`). `ZipArchive` is stricter than `zipfile` in ways that only fail closed, never install more: it
   refuses data prepended to the archive and entries compressed with bzip2 or LZMA (Python accepts all three, but its
   `_pack` writes none of them), and an encrypted entry is "state bundle is unreadable" here where `zipfile` raised a
   plain `RuntimeError` (`error`). It does not compare an entry's local header name with the central directory as
   `zipfile` does; that only matters for a hand-made archive, whose content is still checked by the manifest hashes
   and the database checks. A manifest that is not an object raises `PyAttributeError`, and a
   staged file SQLite cannot read raises `SqliteException`, as Python's `AttributeError`/`sqlite3.DatabaseError`
   escape (`error`). Snapshots use Microsoft.Data.Sqlite's `BackupDatabase` (no pooling), 10 s busy timeout on the
   source as in Python.
5. **`now`** is a `DateTimeOffset` (always aware); Python also accepted a naive `datetime`. Default: State.Core's
   `PyRuntime.UtcNow()`.
6. **Command patterns.** Python's `re` patterns are matched by hand (`ManagerCommands`) so that `\s` is
   `str.isspace()` and `IGNORECASE` includes CPython's extras (KELVIN SIGN, LONG S, dotted/dotless I) from State.Core's
   tables; 24 inputs are compared with Python's results in `TurnSupportTests`.
7. **Display names that are not text.** Python kept a truthy non-string `displayName` (the reply gate then refused to
   name it); here it is `null`, with the same gate outcome ("manager display name unavailable").
8. **`ReplyGate.Settle` without a journal** throws `InvalidOperationException` (Python: `AttributeError`).
9. **Threads.** Python opens a ledger on the thread that uses it (`asyncio.to_thread`); here SQLite work runs in
   `Task.Run` and each ledger is opened, used and disposed within one call (`OfficeReader` per read). State.Core's SQLite
   connections are not thread-affine, but none is shared between concurrent calls. `_conversations` and
   `_initialised` are concurrent dictionaries; the acknowledgement set is locked.
10. **The `margo-state` command** comes from Engine's `StateCommands` (the C# CLI by default, or
    `OrchestratorOptions.StateCliCommand`), not `scripts_dir`.
11. **Test seam.** `Orchestrator.TurnGuardObserver` (internal) sees each manager turn's guard, where the Python test
    replaced `engine.run` with a spy.

## Not ported here

- **The Azure Blob lease store** (`host/azure_store.py`) and the host wiring (`host/app.py`) belong to
  `Margo.Cloud.Host`; see request 2.
- **`OfficeQueue.record` patched to fail** (`test_a_failed_turn_is_reported_to_the_manager_not_silent`): C# cannot
  patch the method; the test makes the ledger refuse the record instead (a message without an ID), which takes the same
  path (`error`, no reply, logged).
- **`test_seen_before_is_per_sender_and_conversation_without_a_scan`** patches `Ledger.list` to fail; the port checks
  that the marker source exists and that `SeenBefore` uses `FindSource` (by reading the code: one call, no `List`).

## Requests to other components

1. **`memory_state.py` in `margo-state`** (`Margo.Cloud.State.MemoryCommands`). Until the C# CLI implements it,
   `StateCommands.InitialiseAsync` fails and every manager turn on the C# CLI alone ends with `TURN_FAILED`/`error`
   (fails closed; `Without_memory_state_in_the_cli_a_manager_turn_fails_closed` asserts it and flips to "answered"
   once it lands). The tests run manager turns with a shell shim (`StateCli` in the tests) that sends only
   `memory_state.py` to the Python helper (`skills/chief-of-staff/scripts/memory_state.py`, the specification) against
   the same state directory; every other script runs in the C# CLI. The shim is used only while the C# CLI lacks the
   script.
2. **`Margo.Cloud.Host`:** implement `ILeaseStore` on Azure Blob Storage as `azure_store.py` does (one store per
   container, reused; container created on first use and again after a 404; the lease blob bootstrapped empty, a 412
   on that create ignored; 60 s leases; wait up to 120 s on 409, else `SyncException("instance state is busy or
   unavailable")`; `SyncException("could not read instance state")` / `("could not save instance state; the lease may
   have been lost")`; a missing blob reads as `(null, null)`; release errors ignored); build `InboundEvent` and the
   token getter from the activity; pass `loggerFactory.CreateLogger("margo.autopilot")`, the receipt logger as both
   recorders, `MARGO_STATE_CONTAINER`, `MARGO_INSTANCE_KEY`-based voice URL and the instance. The class is
   `Margo.Cloud.Orchestrator.Orchestrator`: outside this namespace, name it fully (the namespace has the same name).
3. **`Margo.Cloud.Voice`** can reuse `OfficeReader` and `InstanceLedger.OpenLedger` as `voice/server.py` does.
4. **`Margo.Cloud.State.Tests`:** done on the base branch (96fb6a0, merged into the review branch): the in-suite
   parity wrapper now runs only with `MARGO_RUN_PARITY_IN_TESTS=1`, and `state_parity.py --check` is its own gate.

## Tests and evidence

`cloud/tests/Margo.Cloud.Orchestrator.Tests` (119 test cases), a fake model, a fake Work IQ directory transport
behind the real `WorkIQGuard`, `InMemoryLeaseStore`, real temporary state, the real engine and the real `margo-state`
CLI (referenced by the test project, so `margo-state.dll` sits next to the tests; see request 1 for `memory_state.py`):

- `OrchestratorTests`: every `OrchestratorTests` test of the former Python runtime tests (all 18 Python tests, the
  group-chat one as a 3-case theory and the lookup failures as a 4-case theory), including the instance-mode
  (`An_instance_container_serves_only_its_own_instance`) and voice-link tests; plus a throwing token getter, invalid
  bindings, a misconfigured transport, unsafe state, initialisation failure, initialisation once per account,
  conversation continuity, the round limit (`stopped`), the setting written to private preferences, and state paths.
- `StateSyncTests`: every `StateSyncTests` test (9), plus the bundle and manifest contents and modes, installing once,
  bundles Python would refuse (not a zip, missing entries, bad JSON, hash mismatch, a forged manifest over another
  account's database), entries that fail their CRC-32 (preferences and manifest), bundles exchanged with the Python
  `StateSync` in both directions (until the cutover; since then a bundle the Python service packed,
  `Parity/python_state_bundle.zip`, installs here), preferences removed with the bundle, a failed daily
  copy, and the lease always released.
- `OfficeTests`: the three `LedgerBackedTests` office tests, plus the recorded evidence and marker, pending order and
  limit, and a record without an ID.
- `ReplyTests`: `ReplyPolicyTests` of the former Python guard tests (4) and the two `test_reply_gate_*` tests against
  the real approval journal, plus 9 policy texts compared with Python's results and the gate's reasons.
- `TurnSupportTests`: `TurnTokens`, `_manager_from` (15 results, 13 of them checked against Python's output), the commands (24 texts),
  `set_reply_mode` (4 files compared with Python's output), and every fixed text and limit compared with the Python
  modules (recorded in `Parity/orchestrator_constants.json` at the cutover).
- `DifferentialTests`: 1,800 seeded generated cases answered by the Python helpers (recorded in `Parity/orchestrator_answers.json` at the cutover)
  and by the port, compared exactly: `load_reply_policy`, the two command patterns, `set_reply_mode`, `_manager_from`,
  the office excerpt and conversation key, and `ReplyGate.decide` sequences in `none` and `acknowledge` modes.

Verification at the original port commit (cab0043): this project 116 passed; the full solution run had one failure,
the in-suite parity wrapper hitting its 15-minute limit (request 4, since resolved on the base branch).

Verification after the review (base branch merged): `dotnet build cloud/Margo.Cloud.slnx` 0 warnings, 0 errors;
`dotnet test --solution Margo.Cloud.slnx` 5266 total, 5261 passed, 0 failed, 5 skipped (Engine's four, and the
parity wrapper, which now runs only on request); `python3 tools/state_parity.py --check` 182 of 182 scenarios passed (15 min 54 s); `python3 -m unittest discover -s tests -p 'test_*.py'` 520 OK (4 skipped); `./tools/check-clean.sh` clean.

Not verified: anything against real Azure Blob Storage, Work IQ, Entra or a model (contract tests with fakes, not model
evaluations).

## Review (independent reviewer, on top of the port)

Read against the Python side by side; differential tests added (above); all findings fixed with a failing test first:

1. **CRC-32 not checked when installing a bundle** (`StateSync.cs`, `Read`). `ZipArchive` does not verify an entry's
   CRC-32; `zipfile` does (`BadZipFile`, so `SyncError`). A bundle whose `preferences.md` (not covered by the manifest)
   was corrupted in storage was installed here, silently replacing the manager's `Replies to other people` setting with
   the corrupted text, where Python refuses the bundle and replaces nothing. Fixed: every entry read is checked
   (`An_entry_that_fails_its_checksum_is_refused_as_python_s_zipfile_refuses_it`). The earlier note here claimed a bad
   CRC was already refused; it was not.
2. Not defects, recorded as deviations: `ZipArchive`'s stricter reading (deviation 4).

Checked and found to match: every status and its order; the fixed texts; the command patterns, reply setting and
manager lookup over 18,000 generated cases (one 10x run beyond the committed 1,800); the cross-language bundle format.

## Open questions

1. Python reports corrupt deflate data in a bundle as a plain error (`zlib.error` escapes `_install`); this port
   reports it as `state_unsafe` (deviation 4). Should Python catch `zlib.error` too?
2. A turn with no shared bundle discards a local `preferences.md` before the work runs (as Python does), so a setting
   written by an operator onto local disk is lost unless it arrives through a saved turn. That is the documented
   design; noted because it surprised a test written for this port.

## Cutover

The Python hosted runtime was removed in the Phase 6 cutover. It was the specification of this
port; the C# code is now the implementation of record, and the desktop state helpers in
`skills/chief-of-staff/scripts/` stay the specification of the state commands (`tools/state_parity.py`).

- `TurnSupportTests.Every_fixed_text_and_limit_matches_the_python_modules` reads `Parity/orchestrator_constants.json`,
  the Python modules' values printed just before the cutover.
- `DifferentialTests` reads `Parity/orchestrator_answers.json`: the Python helpers' answers to the 1,800 seeded cases,
  with the SHA-256 of the cases, so a changed generator fails instead of comparing against the wrong answers.
- `StateSyncTests.A_bundle_the_python_service_packed_installs_here` replaces the two-way bundle test: a deployment that
  switches images finds Python-packed bundles in Blob storage, so `Parity/python_state_bundle.zip` (packed by the
  Python `StateSync` from a fictional database) must install here; the other direction has no Python reader left.

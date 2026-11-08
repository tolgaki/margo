# Margo.Cloud.Voice: porting notes

Port of `voice/session.py` (Python hosted runtime, removed at the cutover) (settings, the Voice Live `session.update`, the event bridge,
`check_claims`) and `voice/server.py` (the voice endpoint: page, sign-in config, WebSocket session). The Python code is
the specification; everything not listed under "Deliberate deviations" is meant to behave the same. The browser page
lives in `wwwroot/` (moved there unchanged from the Python package's `voice/static/` at the cutover): the csproj
embeds its five served files, and `node --test cloud/src/Margo.Cloud.Voice/wwwroot/*.test.mjs` covers it.

## What maps to what

### voice/session.py

| Python | C# | Notes |
| --- | --- | --- |
| `VOICE_ADDENDUM`, `DISCLOSURE`, `DEFAULT_VOICE`, `MAX_AUDIO_CHARS`, `LOCKED_TOOLS`, `PROGRESS` | `VoiceSession.Addendum`, `Disclosure`, `DefaultVoice`, `MaxAudioChars`, `LockedTools`, `Progress` | Text compared with `session.py` by a test |
| `VoiceSettings` (`from_env`, `voice()`) | `VoiceSettings` record (`FromEnv`, `Voice()`) | Same variables; values `str.strip()`ed; limits read with Python `int()` (State.Core `PyArgumentParser.ParsePyInt`); errors are `PyValueError` with Python's text |
| `session_update(instructions, tools, settings)` | `VoiceSession.SessionUpdate` | Same keys in the same order: `azure_semantic_vad`, `azure_deep_noise_suppression`, `server_echo_cancellation`, `azure-speech` transcription, function tools, `tool_choice: auto` |
| `_progress(name)` | `VoiceSession.ProgressFor` | |
| `base64.b64decode(audio, validate=True)` | `VoiceSession.IsStrictBase64` | Checked against CPython 3.13 for every string of up to 8 characters over `A`, `=` and newline |
| `VoiceBridge(dispatch, upstream, downstream, settings, clip, run_locked, clock)` | `VoiceBridge` (`StartAsync`, `Expired`, `FromClientAsync`, `FromServiceAsync`, `ToolCalls`) | Delegates take a `CancellationToken`; `clock` is monotonic seconds |
| `TokenRejected` | `TokenRejectedException` | |
| `check_claims(claims, tenant_id, client_id, scope, now)` | `VoiceClaims.Check` | Python value semantics: `str(scp).split()` (non-strings by `repr`, Unicode whitespace), `exp` any int/float/bool compared exactly, `aud`/`tid`/`idtyp` compared as strings only |

### voice/server.py

| Python | C# | Notes |
| --- | --- | --- |
| `WORKIQ_SCOPE`, `VOICE_CALL_BUDGET`, `VOICE_STATE_BUDGET`, `HELLO_SECONDS`, `MAX_FRAME`, `heartbeat=20` | `VoiceService.WorkIQScope`, `VoiceCallBudget` (120), `VoiceStateBudget` (60), `HelloSeconds` (15 s), `MaxFrame` (256 KiB), `Heartbeat` (20 s, pong within 10 s) | |
| `FILES`, `CSP` | `VoiceEndpoints.Files`, `ContentSecurityPolicy` | Compared with `server.py` by a test |
| `VoiceService(orchestrator=, connections=, instance=, tenant_id=, client_id=, scope=, settings=, endpoint=)` | `VoiceService(Orchestrator, IVoiceTokens, AgentInstance, tenantId, clientId, VoiceServiceOptions)` | Options: `Scope`, `Settings`, `Endpoint`, `VoiceLive`, `Keys`, `Logger`, `Time`. `VOICE_LIVE_ENDPOINT` is checked first (`InvalidOperationException` "VOICE_LIVE_ENDPOINT is required for voice"), then the settings |
| `connections.get_default_connection()`: `get_agentic_user_token`, `acquire_token_on_behalf_of` | `IVoiceTokens.GetAgenticUserTokenAsync`, `AcquireTokenOnBehalfOfAsync` | Implemented by the Host with the Agents SDK |
| `page`, `config` | `VoiceEndpoints` handlers, `VoiceService.Config()` | |
| `_verify` (PyJWT `PyJWKClient` + `jwt.decode`) | `Jwt.EntraTokenVerifier.VerifyAsync` | See "The sign-in check" |
| `_tokens` | private `TokensAsync` | OBO only when the orchestrator's principal access is `obo` |
| `orchestrator._manager(event, margo_token)` | private `LookUpManagerAsync` | The orchestrator's is private in C#, so it is reproduced from its public pieces: bootstrap binding with `Orchestrator.UnboundManager`, guard budget 1, `fetch` of `ManagerDirectory.ManagerUrl` `on_data_of: self`, unattended, `ManagerDirectory.From` |
| `orchestrator.state_sync`, `state_root`, `_state_commands`, `transport`, `recorder`, `principal_access` | `Orchestrator.StateSyncFor`, `StateRoot`, `new StateCommands(..., Options.StateRecorder, cliCommand: Options.StateCliCommand)`, `Transport`, `Options.GuardRecorder`, `Options.PrincipalAccess` | |
| `socket`, `_fatal`, `_session`, `_converse` | `VoiceService.RunAsync` (internal), private `SessionAsync`, `ConverseAsync` | |
| `azure.ai.voicelive.aio.connect` | `IVoiceLiveConnector` / `IVoiceLiveConnection`; `VoiceLiveConnector` | See "Voice Live" |
| `mount(app, service)` | `VoiceEndpoints.Map(IEndpointRouteBuilder, VoiceService)` | One `/voice/{**rest}` endpoint that dispatches on the exact path, so `/voice` (301) and `/voice/` (the page) stay distinct |
| aiohttp `WebSocketResponse` / `ClientWebSocketResponse` | internal `MessageSocket` | See "Sockets" |

## The session

As in Python, each step failing closed: the first message within 15 s must be text JSON `{"type": "hello", "token":
"<non-empty string>"}` (anything else that parses is "no token"; a binary or close message too; invalid JSON or a
timeout is the generic failure); the token is verified; Margo's agentic token for Work IQ is requested (empty is the
generic failure "no agent token"), then the manager's OBO token under `obo`; the manager is looked up with Margo's token
and must be the caller (no manager, or another person, is a refused sign-in); the state lease's ledger and
memory/task state are initialised once per account per service; then the guard (budget 120, the instance's principal
access, the orchestrator's guard recorder), the skill library with the manager's preferences, the office reader and
`margo-state` (budget 60) back the engine's own dispatcher (`Engine.DispatchAsync`, attended); the instructions are
`EngineTools.LoadInstructions(repo, engine time zone, library)` + `"\n\n"` + the addendum; the tools are
`EngineTools.FunctionTools("attended")`. `margo-state` and `office_requests` calls run under `StateSync` (the lease).

The page and Voice Live are pumped concurrently until either ends, the person sends `stop`, or `MARGO_VOICE_MAX_SECONDS`
passes; then `{"type": "closed", "reason": "time limit reached" | "call ended"}` if the page is still open, and the
Voice Live session is closed. An error in either loop (a dispatch that throws, an unhashable tool name, a Voice Live
message that is not a JSON object, a send to a closed socket) ends the call quietly, as Python never read the
cancelled tasks' outcome. A refused sign-in sends `{"type": "error", "fatal": true, "message": "Your sign-in can't be
used to talk to this Margo."}` and logs `voice sign-in refused: <reason>` (information); anything else sends "Something
failed on my side, so the call ended. Nothing was sent." and logs `voice session failed` (error, with the exception).
Nothing is sent on a closed socket. No token, audio, transcript or text is logged or stored.

## The sign-in check

`EntraTokenVerifier` reproduces PyJWT 2.15.1 (read in the venv) with the server's arguments, adapted from the router's
reading of the same library (`Margo.Cloud.Router/Authentication`, internal there, so the needed parts are copied here:
`CompactJws`, `SigningKeys` and `PythonBase64`):

1. `PyJWKClient("https://login.microsoftonline.com/<tenant>/discovery/v2.0/keys")`: one per service; the set is cached
   300 s; an unknown or missing `kid` forces one refresh unless a fetch succeeded in the last 30 s; failed fetches leave
   the cache. Keys: `use` `sig` or absent, a truthy `kid`; `PyJWKSet` skips members that are not objects or not usable.
2. The token is read unverified (split, strict canonical base64url, JSON object header with a string `kid`, `crit` only
   `b64`, `b64: false` refused, JSON object payload).
3. `alg` must be exactly `RS256`; the key must be an RSA public key (an `oct` key is `InvalidKeyError`; an EC/OKP key or
   a private RSA key raises a non-PyJWT error there, so here an `InvalidOperationException`, the generic failure); the
   RSASSA-PKCS1-v1_5 SHA-256 signature must verify.
4. `exp`, `iat`, `aud`, `iss` present and not null; `iat`, `nbf` (when present) not after now + 60; `exp` after now - 60
   (Python `int()` of each: digit strings with `_` and whitespace, floats truncated, booleans; anything else refused);
   `iss` a string equal to `https://login.microsoftonline.com/<tenant>/v2.0` or `https://sts.windows.net/<tenant>/`;
   `aud` truthy, a string or list of strings, containing the client ID or `api://<client ID>`; `sub` and `jti` strings
   when present.
5. A PyJWT error becomes `token failed verification: <Python exception type>`; then `check_claims` with the same clock.

The outcomes were checked against the real PyJWT for the cases in `TokenVerifierTests` (a script in the session's
scratchpad, not committed: it drove `PyJWKClient` and `jwt.decode` exactly as `_verify` does).

## Voice Live

`azure-ai-voicelive` 1.3.0 (`aio/_patch.py`, read in the venv) was not used as a library: there is no .NET equivalent
in the solution, and the wire protocol is small. `VoiceLiveConnector` speaks it over `ClientWebSocket`:

- URL: `_prepare_url()`: scheme `https` to `wss` (other `http*` to `ws`), the endpoint's path without trailing `/` plus
  `/voice-live/realtime`, query `api-version=2026-07-15&model=<model>` then the endpoint's own non-blank query
  parameters not already present (`parse_qs` first values, `urlencode` with `quote_plus`), params and fragment kept.
  Six endpoints are compared with the strings the SDK itself printed. As Python 3.13 `urlsplit`, a netloc with an
  unmatched bracket, text before `[` or after `]` other than a port, an IPv4 address in brackets, an invalid IPvFuture
  literal or a bracketed host that is not an IPv6 address (`ipaddress` rules: scope ID, at most 4 hex digits per group,
  one `::`, embedded IPv4) raises `PyValueError` ("Invalid IPv6 URL" and Python's other texts), so the call ends before
  connecting; .NET's `Uri` would otherwise accept some of these and connect elsewhere (`wss://[::1];x/...` becomes
  `wss://[::1]/;x/...`).
- `Authorization: Bearer <token>` for `https://ai.azure.com/.default`, from `ManagedIdentityCredential(AZURE_CLIENT_ID)`
  when set, else `DefaultAzureCredential`, a new credential per session as in Python.
- Client events: one text message each, `json.dumps(event)` (`PyJson.Dumps`: `", "`/`": "`, `ensure_ascii`). Server
  events: text or binary, UTF-8 JSON; `event.as_dict()` of the SDK's models is the received object unchanged (checked in
  the venv), so the bridge gets the object as received. An empty message, a close or a socket error ends the session;
  a non-object raises (Python: `AttributeError` in `ServerEvent._deserialize`), and so does an object whose `type` is a
  list or object (Python: `TypeError: unhashable type` in the same lookup, wrapped as `ConnectionError` by `recv`);
  any other `type` value (number, null, missing) passes through unchanged.
- 4 MiB messages and a 30 s heartbeat (the SDK's defaults for `max_msg_size` and `heartbeat`); close waits 10 s.

`Azure.AI.VoiceLive` (.NET) was not adopted: verifying that it produces the identical protocol (URL, query order, event
JSON) would need its source and a capture, and the hand-written client is checked against a local WebSocket server.

## Sockets

`MessageSocket` gives a .NET `WebSocket` aiohttp's behaviour as the server uses it: a reader task queues whole
messages, so waiting with a timeout (the hello) or stopping (the end of the call) never aborts the socket (cancelling a
.NET receive does); a message whose size reaches the limit closes with 1009 and a non-UTF-8 text message with 1007; a
peer close is answered with 1000; once closing has started receives return "closed" and sends fail; one sender at a
time (Python's single-threaded loop made concurrent sends safe; here both pumps may send). As aiohttp's
`WebSocketDataQueue` (limit `2**16`), once more than 64 KiB of messages wait unread the reader stops reading the socket
until the caller takes one, so a peer that sends faster than the session handles its messages is held back by the
transport rather than queued without limit in memory; once closing has started the reader no longer waits and drops
what follows until the peer's close frame. The page socket is accepted
with `KeepAliveInterval` 20 s and `KeepAliveTimeout` 10 s (aiohttp `heartbeat=20`).

## Deliberate deviations

1. **Static file responses** carry `Content-Type`, `Content-Length`, the CSP, `Cache-Control: no-store` and `nosniff`,
   but not aiohttp `FileResponse`'s `ETag`, `Last-Modified`, `Accept-Ranges` or range/conditional handling (embedded
   files have no modification time, and `no-store` makes browsers not revalidate).
2. **The WebSocket handshake**: a request to `/voice/ws` that is not a WebSocket upgrade gets aiohttp's 400 text for a
   missing/wrong `Upgrade` header; other handshake errors are ASP.NET Core's. `permessage-deflate` is not negotiated
   (aiohttp's `compress=True` accepted it); message contents are unaffected.
3. **Unmatched paths and methods**: `404: Not Found` and `405: Method Not Allowed` (with `Allow: GET,HEAD`) as plain
   text, close to aiohttp's; aiohttp's HTML/text error bodies and `Allow` ordering were not reproduced byte for byte.
4. **Limits beyond 64 bits**: `MARGO_VOICE_MAX_SECONDS`/`MAX_TOOL_CALLS` are read with Python `int()` but stored as
   `long` (saturating); the call timer is capped at about 49 days (the .NET timer limit). A `MARGO_VOICE_MAX_SECONDS`
   too large for a float (over about 1.8e308) makes Python's `asyncio.wait` raise `OverflowError`, so every Python call
   ended with the generic failure; here such a call simply has no practical time limit.
7. **The NFKC netloc check** of Python's `urlsplit` (`_checknetloc`: a non-ASCII host whose NFKC form contains `/?#@:`
   is a `ValueError`) is not reproduced: the solution builds with invariant globalization, where .NET has no NFKC
   normalisation. `VOICE_LIVE_ENDPOINT` is operator configuration; such a host fails at DNS resolution instead.
5. **EC/OKP keys in the key set** are accepted by key type only; PyJWT also validates their coordinates and skips an
   invalid one. Either way such a key never verifies an RS256 token (Entra publishes RSA keys only); the difference is
   the generic failure versus a refused sign-in.
6. **Cancellation**: when the call ends, the loops' pending work is cancelled through a `CancellationToken` (a running
   tool call may finish in the background, as Python's thread did); the hello wait and the pumps never cancel a socket
   receive.

## Not ported, and why

- **Host wiring** (`host/app.py`: constructing `VoiceService` from `MARGO_VOICE_SCOPE`, the tenant and blueprint IDs,
  mounting it, keeping the Agents SDK's JWT middleware off `/voice` and `/voice/...`, and calling `app.UseWebSockets()`)
  belongs to `Margo.Cloud.Host`.
- **The `/voice` chat command** belongs to `Margo.Cloud.Orchestrator` (`ManagerCommands.IsVoice`, `VoiceUrl`); its
  runtime test is also ported here (`RuntimeVoiceTests`).

## Requests to other components

1. **`Margo.Cloud.Host`**: implement `IVoiceTokens` with the Agents SDK (`GetAgenticUserTokenAsync` for the instance's
   agent identity and agent user with `VoiceService.WorkIQScope`; `AcquireTokenOnBehalfOfAsync` with the person's
   voice token); construct `VoiceService` when voice is enabled (it throws at start-up without `VOICE_LIVE_ENDPOINT` or
   `VOICE_LIVE_MODEL`); call `app.UseWebSockets()` and `VoiceEndpoints.Map(app, service)`; exclude `/voice` and
   `/voice/...` from the Agents SDK token check; pass a `margo.voice` logger.
2. **`Margo.Cloud.Orchestrator`** (optional): exposing the manager lookup (Python `_manager`) and `_state_commands`
   publicly would let voice call them instead of reproducing them from public pieces.
3. **`Margo.Cloud.Router`** (optional): the PyJWT pieces (`CompactJws`, `JwksClient`, `PythonBase64`) are now copied in
   two projects; a shared home (for example a small `Margo.Cloud.Jwt` project) would remove the copy.

## Tests and evidence

`cloud/tests/Margo.Cloud.Voice.Tests` (78 test cases):

- `SessionConfigurationTests`, `BridgeTests`, `ClaimsTests`: every test of the former Python voice tests, plus the
  settings' environment rules, the exact `session.update` JSON, the texts compared with `session.py`, strict base64,
  progress messages, unhashable tool names, ready-once, caption trimming and clipping, expiry, and claim edge cases.
- `TokenVerifierTests`: locally minted RSA tokens (by hand and by `Microsoft.IdentityModel.JsonWebTokens`) against an
  injected key source: issuer, audience, required claims, leeway, `int()` semantics, algorithm, signature, structure,
  key cache, cooldown and rotation, key set failures.
- `EndpointTests`: the page files byte for byte with headers, HEAD, 404/405/301, config JSON, the plain-GET 400, the
  start-up checks, and the files and CSP compared with `server.py`.
- `SessionFlowTests`: the whole session through a test server with the real orchestrator, guard, engine dispatcher,
  `StateSync` and `margo-state` CLI and a fake Voice Live: tokens, audio after ready, Work IQ reads with the manager's token,
  a real `margo-state` call, refused writes, `response.create`, captions, stop; delegation mode; every refused hello;
  wrong caller, expired token, other tenant, no manager; the generic failures (bad JSON, no agent token, directory
  failure, Voice Live failure, hello timeout); binary hello; the 1009 frame limit; the time limit; the tool budget.
- `VoiceLiveTests`: the URL against the SDK's output, and the wire protocol against a local Kestrel WebSocket server
  (bearer token and scope, path and query, `ensure_ascii` JSON, text and binary events, a non-object, close), the
  bracketed-host rules against the SDK's outcomes, and an unhashable event `type`.
- `MessageSocketTests`: unread messages hold the peer back (64 KiB) and arrive in order; closing still completes while
  the peer has sent more than the queue holds, and the peer's held-back sends finish (read and dropped) before it
  answers the close frame. The test peer answers only after its own sends: a .NET `WebSocket` checks its state before
  queueing a send behind its send lock, so a send concurrent with `CloseOutputAsync` can put a data frame after the
  close frame, which nothing reads (RFC 6455 and aiohttp both stop at the close frame). The test once did exactly that
  and hung about 1 run in 10; `MessageSocket` itself was not at fault and is unchanged. A scratch console program
  (not committed) confirmed the .NET order directly: a send blocked on a full pipe, then `CloseOutputAsync`, then a
  second send produced the frames data, close, data on the wire.
- `RuntimeVoiceTests`: the voice test of `tests/test_autopilot_runtime.py` (the `/voice` command).
  the peer has sent more than the queue holds. The test's peer answers the close only after its held-back sends have
  finished: otherwise a send that passed its state check could write after the peer's close frame into a transport
  nobody reads, and the test timed out (it failed in 2 of 2 full-solution runs under load, never alone).
- `RuntimeVoiceTests`: the voice test of the former Python runtime tests (the `/voice` command).

Not covered by tests: a real Voice Live service and a real Entra key set (no network or Azure account here).

### Review (differential checks, not committed)

An independent review drove the Python originals and this port with generated inputs from a scratch console app
(outside the repository): `check_claims` (Python value semantics), `VoiceSettings.from_env`, `b64decode(validate=True)`
and whole `VoiceBridge` event sequences with a fake dispatcher (18,000 cases, no differences); the server's `_verify`
with real PyJWT 2.15.1 and `PyJWKClient` against `EntraTokenVerifier` over mutated RSA tokens and key sets (9,000
cases, no differences); and the SDK's `_prepare_url` against `RealtimeUrl` (6,000 endpoints; after the bracket fix the
only difference is the exception type). It fixed: the bracketed-host rules, the unhashable event `type`, the reader's
unbounded queue, and `SessionFlowTests` sending pre-ready audio in a way that raced the fake service and failed on
every run here.

A second review (of the `MessageSocketTests` close fix) found that `SessionFlowTests` still raced: audio sent before
`session.updated` is queued before the session is ready but read by the page pump, which starts only after
`session.update` and runs concurrently with the service pump (as Python's two tasks under `asyncio.wait`), so it was
sometimes read after `session.updated` and passed on (`AAAA` instead of `AAAB`; 1 failure in 30 project runs, 3 in 600
repetitions under 4-way load). The flow test no longer sends pre-ready audio (0 in 600 under the same load); the rule
itself is the bridge's and is checked deterministically by `SessionTests.Audio_flows_only_after_ready_and_barge_in_interrupts`.
At the flow level, audio queued before ready may be passed on if read after ready, in Python and here alike.

## Open questions

- PyJWT checked `iat` against the wall clock at decode time and `check_claims` used `time.time()` again; here both use
  the same injected clock, which is the same in production.

## Cutover

The Python hosted runtime was removed in the Phase 6 cutover. It was the specification of this
port; the C# code is now the implementation of record, and the desktop state helpers in
`skills/chief-of-staff/scripts/` stay the specification of the state commands (`tools/state_parity.py`).

- The page moved with `git mv` from `voice/static/` to `wwwroot/`; the csproj embeds it from there and the node tests
  run from there. `The_page_lists_the_python_server_s_files_and_policy` and
  `Addendum_and_disclosure_are_python_s_text` compare with the Python values recorded at the cutover instead of
  reading `server.py` and `session.py`.

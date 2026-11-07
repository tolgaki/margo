# Margo.Cloud.WorkIQ: porting notes

Port of `autopilot/src/margo_autopilot/transport.py`, the standard-library MCP client for
`https://workiq.svc.cloud.microsoft/mcp`, plus the transport-facing pieces of `guard.py` that the
transport raises or defaults to. The Python code is the specification; everything below that is not
listed under "Deliberate deviations" is meant to behave the same.

## What maps to what

| Python | C# | Notes |
| --- | --- | --- |
| `guard.WORKIQ_MCP_URL` | `WorkIQEndpoints.McpUrl` | Here because the transport defaults to it and `Margo.Cloud.Guard` references this project, not the reverse |
| `guard.TransportError(message, status_code=None)` | `TransportException(message, int? statusCode)` | Public; the guard catches it |
| `guard.TransportTimeout` | `TransportTimeoutException : TransportException` | |
| the transport object the guard holds (`.endpoint`, `.call(tool, args, bearer=)`) | `IWorkIQTransport` (`Endpoint`, `CallAsync(tool, arguments, bearer, ct)`) | The interface the Guard port should depend on |
| `transport.McpHttpTransport(url=WORKIQ_MCP_URL, *, timeout=60, allow_loopback_http=False)` | `McpHttpTransport(url = McpUrl, timeout = 60 s, allowLoopbackHttp = false, handler = null, disposeHandler = false)` | `IDisposable`; `handler` is the test seam |
| `.endpoint`, `.timeout` | `Endpoint`, `Timeout` | |
| `PROTOCOL_VERSION`, `CLIENT_INFO` | `McpHttpTransport.ProtocolVersion`, `ClientName`, `ClientVersion` | Same values: `2025-06-18`, `margo-autopilot`, `0.1.0` |
| `McpHttpTransport.call` | `McpHttpTransport.CallAsync` | |
| `_session`, `_post` | private `SessionAsync`, `PostAsync` | |
| `_parse_body` | `McpMessages.ParseBody` (internal) | |
| `_normalise` | `McpMessages.Normalise` (internal) | |
| `_NoRedirect` / `_OPENER` | default `SocketsHttpHandler { AllowAutoRedirect = false }`; an injected handler that follows redirects is refused | |
| `_UnknownSession` | `McpUnknownSessionException` (internal, **not** a `TransportException`) | When it escapes, Python's guard sees a plain exception |
| `TypeError` / `AttributeError` / `ValueError` / `RecursionError` raised inside the transport | `TransportFaultException` (internal, not a `TransportException`) | See "Failure classification" |
| `json.loads`, truthiness, `isinstance(x, int)`, `==`, `str.strip` | `PythonJson` (internal) | Python value rules on `JsonNode` |

## How the guard uses it

`guard.py` calls `self.transport.call(tool, args, bearer=bearer)` synchronously; the engine and
orchestrator run `guard.call` through `asyncio.to_thread`, so the transport is only ever used from worker
threads and must be thread-safe. In C# the guard is async: it awaits `IWorkIQTransport.CallAsync`, and the
transport is thread-safe (sessions under a lock, ids from `Interlocked`). The guard also checks
`getattr(transport, "endpoint", None) != WORKIQ_MCP_URL`; the C# guard compares `Endpoint` with
`WorkIQEndpoints.McpUrl` (ordinal).

What each outcome means to the guard (`guard.py` lines around `self.transport.call`):

| Transport outcome | Python guard | C# type |
| --- | --- | --- |
| a payload object | status from `statusCode` / `results[].statusCode` | `JsonObject`, detached, caller-owned |
| no answer in time | `unknown`, claim `outcome_unknown` | `TransportTimeoutException` |
| HTTP error, JSON-RPC error, tool error, not-an-answer, unreachable before sending | reads: `error`; writes: `failed` only for a status in `REJECTED_NO_EFFECT`, else `unknown` | `TransportException` (`StatusCode` = HTTP or tool status, else `null`) |
| anything else | `unknown`, "transport failure; effect unknown, not retried" | any other exception |
| caller cancelled | (no Python equivalent) | `OperationCanceledException` |

## Behaviour reproduced

- **Requests.** JSON-RPC 2.0 POSTs with keys in Python's order: `initialize` (`protocolVersion`,
  `capabilities: {}`, `clientInfo`), `notifications/initialized` (no id), `tools/call`
  (`{"name": tool, "arguments": arguments}`). Ids come from one counter per transport starting at 1,
  shared by `initialize` and `tools/call`; the call takes its id after its session exists. Headers:
  `Content-Type: application/json`, `Accept: application/json, text/event-stream`,
  `Authorization: Bearer <token>`, and on a session `MCP-Protocol-Version` and (when the server gave one)
  `Mcp-Session-Id`. What urllib adds by itself is also sent: `Accept-Encoding: identity`,
  `Connection: close`, `Content-Length`. HTTP/1.1 only, as http.client. Bodies are ASCII-only, as
  `json.dumps(ensure_ascii=True)`.
- **Sessions.** One per SHA-256 of the bearer token. The cache is checked under the lock, the
  handshake runs outside it (two concurrent first calls may both initialise; the last one stored wins, as
  in Python). `initialize` answered with a JSON-RPC `error` is "Work IQ refused the MCP session". The
  session id is the last `Mcp-Session-Id` response header; an empty one counts as none. The protocol
  version is `(result or {}).get("protocolVersion", "2025-06-18")`, rendered as http.client renders a header
  (string as is; int or bool through `str()`, so `true` becomes `True`); `null`, floats, objects and a truthy
  non-object `result` fail before the notification is sent and nothing is cached.
- **The one retry.** A 404 to a request that carried a session id means the session is gone: the cached
  session is dropped, a new one is opened, and the same request (same id) is sent once more. A 404 to the
  notification, or a second 404 to the resend, escapes as `McpUnknownSessionException`; a 404 without a
  session id is an ordinary `TransportException` with status 404.
- **Responses.** Non-2xx is `Work IQ HTTP <code>` with that status (redirects included; they are never
  followed). SSE is chosen by a case-sensitive `text/event-stream` substring of the raw `Content-Type`;
  otherwise JSON. The body is decoded as UTF-8 with replacement (a BOM stays and makes JSON invalid).
  SSE: CRLF to LF, blocks split on blank lines, `data:` lines joined with `\n` after Python `lstrip()`,
  non-JSON events skipped, a JSON array event is not a message. JSON: an empty or Python-whitespace-only
  body, or `null`, answers nothing; an array is a batch; a parse failure is "Work IQ returned a non-JSON
  body". The first object whose `id == request_id` by Python equality (`7.0`, `7e0` and `true == 1`
  match; `"7"` does not) is the answer, else "Work IQ response did not answer the request". Duplicate JSON
  keys keep the last value at the first key's position, as Python dicts do.
- **Normalisation.** A non-object `result` is "malformed tool result". An object `structuredContent` is the
  payload; otherwise text items (`type == "text"` and a string `text`) are joined with `\n` and parsed: an
  object is the payload, any other JSON value becomes `{"value": ...}`, non-JSON becomes `{"text": ...}`, no
  text gives `{}`. A `content` that is a string or an object yields no items; `null`, numbers and booleans
  fail as Python's `TypeError` does. A truthy `isError` (Python truthiness) raises "Work IQ tool error" with
  the payload's integer `statusCode`.
- **Timeouts.** Python's `timeout` is a socket timeout: it bounds each blocking operation, not the whole
  exchange. The port arms one timer for connect, send and the response headers, then re-arms it before
  every body read, so a body that keeps arriving is never cut off (tested with a trickled body) and a stalled
  one times out.
- **Size.** Python reads the whole body with no limit; so does the port (bounded only by memory and the
  2 GB `MemoryStream` ceiling). No limit was added; see open questions.

### Failure classification

urllib wraps only connect-and-send failures (`h.request`) as `URLError`; `getresponse()` and `read()`
failures escape as they are. The port tracks whether the request body has been handed to the connection
and flushed (`SentTrackingContent`), and classifies the same way:

| Situation | Python | C# |
| --- | --- | --- |
| socket timeout or `ETIMEDOUT` anywhere | `TransportTimeout("Work IQ timed out")` | `TransportTimeoutException` |
| refused, DNS, TLS, proxy, reset before the request is sent | `TransportError("Work IQ unreachable")` | `TransportException` (`StatusCode` null) |
| server hangs up or sends a bad status line after the request | `RemoteDisconnected` / `BadStatusLine` | the `HttpRequestException`, unwrapped |
| truncated or reset body | `IncompleteRead` / `ConnectionResetError` | the `IOException` (`HttpIOException`), unwrapped |
| header value not Latin-1, or with a bare CR/LF (token, session id, version) | `ValueError` / `UnicodeEncodeError`, nothing sent | `TransportFaultException`, nothing sent |
| non-iterable `content`, truthy non-object init `result`, unsendable version | `TypeError` / `AttributeError` | `TransportFaultException` |
| JSON nested beyond the parser limit | `RecursionError` (not absorbed by the JSON or SSE paths) | `TransportFaultException` |

Every connection carries `Connection: close` (as urllib's does) and the default handler never pools
connections. This is load-bearing in .NET: on a reused keep-alive connection `SocketsHttpHandler` may resend
a request whose connection dropped before the first response byte, which could repeat a write the server
already processed. With one request per connection that resend cannot happen.

## Deliberate deviations

1. **Async with cancellation.** `CallAsync` takes a `CancellationToken`. Cancelling throws
   `OperationCanceledException`, never a timeout. Python has no equivalent; once a request may have been
   sent, its effect is unknown (see open questions for the guard).
2. **Timeout before the response headers.** Connect, send and the wait for headers share one timeout;
   Python applies the timeout to each of those socket operations separately, so its worst case before the
   headers is a few multiples of the timeout. After the headers the behaviour is the same (per read).
3. **Timeout validation.** A zero, negative or longer-than-24.8-day timeout is refused at construction
   (`ArgumentOutOfRangeException`). Python accepts any value and fails each call instead (`ValueError` for
   negative, "unreachable" for zero). `Timeout.InfiniteTimeSpan` is Python's `timeout=None`.
4. **Injectable handler.** For tests and hosting. A handler that follows redirects (directly or behind
   `DelegatingHandler`s) is refused, so the "never carry the token to another URL" rule cannot be undone by
   configuration.
5. **User-Agent.** `margo-autopilot/0.1.0` instead of urllib's `Python-urllib/3.x`.
6. **Wire JSON formatting.** Compact separators and `System.Text.Json` escapes (uppercase `\uXXXX`, also
   escaping `<`, `>`, `&`, `'`, `+`) instead of Python's `", "`/`": "` and lowercase escapes. Both are ASCII
   and JSON-equivalent. Numbers in the arguments are written as given (Python re-renders floats). A lone
   UTF-16 surrogate in the arguments is written as U+FFFD; Python writes `\udXXX`.
7. **JSON parsing limits.** Python's `json.loads` accepts `NaN`, `Infinity` and `-Infinity`; the port does
   not (a JSON body containing them is "non-JSON", an SSE event is skipped, content text becomes
   `{"text": ...}`). Nesting is limited to 1000 levels (`PythonJson.MaxNesting`, the default depth of
   `Utf8JsonWriter`); Python's limit is interpreter-dependent (about 10 000 on 3.13; the autopilot image runs
   3.12). Beyond either limit the result is a non-transport failure.
8. **Status code type.** `StatusCode` is `int?`. A boolean tool `statusCode` (an `int` in Python) is carried as
   1 or 0, so a guard receipt would show `1` where Python records `true`. An integer outside the `int` range
   is carried as `null`; Python keeps the big int. The guard's decision is the same in both cases (neither is
   in `REJECTED_NO_EFFECT`).
9. **Header-level edge cases not reproduced.** http.client allows at most 100 response headers and 64 KB per
   line; `SocketsHttpHandler` allows 64 KB of headers in total. A folded header value (`"\r\n "`) that
   http.client would send may be refused by .NET. A bearer token containing a lone surrogate fails while
   hashing in Python and at the header check here (both before anything is sent, both non-transport).
   A malformed `https://` URL that `Uri` cannot parse is "Work IQ unreachable" at call time; Python reports
   some of those (`InvalidURL`) as non-transport failures.

## Not ported

- **`ModelContextProtocol` (official C# MCP SDK).** Not used, although `docs/margo-cloud-plan.md` names it.
  The SDK owns session negotiation, reconnection and its own error types, so it cannot reproduce what the
  guard relies on: exactly one resend and only after a 404 for a known session id, one session per bearer
  token with the token chosen per call, no redirects, the precise before/after-send error classification and
  the Python normalisation of tool results. No package was added.
- **`guard.WORKIQ_RESOURCE`** (the token audience). The transport does not use it; it stays with the guard or
  host port.
- **The guard's own tests** (`tests/test_autopilot_guard.py`) use a fake transport and belong to the Guard
  port. None of them exercises `transport.py` directly.

## Tests and evidence

`cloud/tests/Margo.Cloud.WorkIQ.Tests` (186 test cases):

- `TransportTests`: the Python `TransportTests` (https rule, JSON and SSE handshake, session per token,
  expired session re-initialised once, 503 / tool error / timeout mapping) and `RedirectTests`, against a
  real loopback HTTP server (`LoopbackHttpServer`, `FakeMcpServer`: the Python `_McpHandler` with extra
  modes), plus request shapes and headers, version handling, refused and failed sessions, notification
  failures, id sequence, concurrency, detachment, and the failure classification table above.
- `TransportParityTests`: 80 response shapes whose outcomes were recorded from the real `_parse_body` and
  `_normalise` by `Parity/transport_cases.py` into `Parity/transport_cases.json`.
- `Parity/transport_paths.py`: runs the real `McpHttpTransport` through 22 network scenarios (hang-up, bad
  status line, truncated, stalled and trickled bodies, notification 404, session forgotten twice, unsendable
  versions and tokens, refused connection) and checks Python's outcome is the one `TransportTests` asserts
  for C#.
- `PythonJsonTests`, `ScriptedHandlerTests`: Python value rules, and failure classes a socket cannot produce
  on demand (OS-level timeout, failure before vs after sending, redirect-following handlers).

```sh
cd cloud && dotnet test --project tests/Margo.Cloud.WorkIQ.Tests/Margo.Cloud.WorkIQ.Tests.csproj
python3 cloud/tests/Margo.Cloud.WorkIQ.Tests/Parity/transport_cases.py --check   # fixture still matches transport.py
python3 cloud/tests/Margo.Cloud.WorkIQ.Tests/Parity/transport_paths.py           # Python network paths
```

Not verified: anything against the real Work IQ endpoint, TLS or a proxy (all tests are loopback or
in-process), and Python 3.12 (the image's version); the Python checks ran on 3.13.

## Open questions

1. **Response size.** Python reads an unbounded body. A bound (for example 16 MB) would protect both
   implementations, but adding it only here would change behaviour; it should be decided for both.
2. **Cancellation in the guard.** The Guard port must decide what an `OperationCanceledException` after
   dispatch does to a claim. Python's nearest path is "any other exception": settle `outcome_unknown` and
   never resend. Suggested: settle as unknown, then rethrow.
3. **Python value rules for the guard.** `_status_codes` in `guard.py` needs Python's `isinstance(x, int)`
   (booleans count, `200.0` does not) on the payload. `PythonJson` here is internal; the Guard port can
   either get it made public here or use `Margo.Cloud.State.Core`'s `PyJson` if it offers the same.
4. **Client identity.** `clientInfo` stays `margo-autopilot` `0.1.0` (and the User-Agent follows it). The cloud
   may want its own name; that is a behaviour change for both implementations.

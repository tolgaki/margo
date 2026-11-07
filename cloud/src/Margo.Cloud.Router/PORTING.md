# Margo.Cloud.Router: porting notes

Ports `autopilot/src/margo_autopilot/router/core.py` and `router/app.py`: the public front door that
checks the platform token, forwards each activity to the one container serving the addressed
instance, turns lifecycle notifications and first activities into provisioning messages, and proxies
the voice page and its WebSocket. Instance names, the route table and lifecycle mapping come from
`Margo.Cloud.Core` (`Instances`, `RouteTable`, `ProvisioningMessage`); nothing there is re-implemented.

## What maps to what

| Python | C# |
| --- | --- |
| `core.MAX_ACTIVITY_BYTES`, `RETRY_AFTER_SECONDS`, `VOICE_PATH` | `RouterCore.MaxActivityBytes`, `RetryAfterSeconds`, `VoicePath()` |
| `core.decide` (dict) | `RouterCore.Decide(JsonNode?, RouteTable)` (`RouterDecision`, `RouterDecisionKinds`) |
| `core.voice_target` (dict) | `RouterCore.VoiceTargetFor(string?, RouteTable)` (`VoiceTarget`) |
| `app.HEALTH_PATHS`, `ACTIVITY_PATHS`, `build_app`, the aiohttp router | `RouterService.HandleAsync` (one terminal handler with aiohttp's routing rules), `RouterApplication.AddMargoRouter` / `UseMargoRouter` |
| `app.ROUTES_TTL_SECONDS`, `Router.table()` | `RouteTableCache` (15 s, keep last good) |
| `Router.messages` | `RouterService.MessagesAsync` |
| `Router.voice`, `Router._voice_socket`, `MAX_VOICE_FRAME` | `RouterService.VoiceAsync`, `VoiceBridge`, `VoiceBridge.MaxFrameBytes` |
| `FORWARD_TIMEOUT`, the `ClientSession` | `InstanceConnector` (300 s total, 10 s connect, 100 connections, heartbeat 20 s) |
| `main`, `_credential`, `load_routes`, `send_message` | `Program.cs`, `RouterSettings`, `BlobRouteSource`, `StorageQueueProvisioningSink` |
| `jwt_authorization_middleware`, `_extract_bearer_token` (Agents SDK, aiohttp adapter) | `AgentTokenValidator.AuthorizeAsync`, `ExtractBearerToken` |
| `_authorize_request`, `JwtTokenValidator.validate_token` (Agents SDK core) | `AgentTokenValidator.AuthorizeAsync`, `ValidateTokenAsync` |
| `_entra_issuers` (`_build_jwks_uri`, `entra_issuer_info`, Bot Framework issuers) | `EntraIssuers` |
| PyJWT `PyJWS._load`, `_validate_headers`, `_decode_base64url_segment` | `CompactJws` |
| PyJWT `_verify_signature`, `_validate_claims` | `AgentTokenValidator.VerifySignature`, `ValidateClaims` |
| PyJWT `PyJWKClient` (cache), `PyJWKSet`, `PyJWK`; the SDK's `_JwkClientManager` | `JwksClient`, `SigningKey`, `JwksClientManager` |
| `PyJWKClient.fetch_data` (urllib) | `IJwksSource`, `HttpJwksSource` |
| `json.loads(bytes)` (`detect_encoding`, `surrogatepass`) | `PythonJsonBytes` |
| `request.path` / `rel_url.path_safe` (yarl) | `RequestPath.FromRawTarget` |
| `str.isspace`, `str.lower`, truthiness, `int()` | `PythonCompat` (Core has the same helpers, but internal) |
| `binascii.a2b_base64` (non-strict), PyJWT `base64url_decode` | `PythonBase64` |
| aiohttp's parser checks (repeated singleton headers, `Content-Encoding`), `_default_expect_handler`, `WebSocketResponse._handshake`, `RequestHandler.handle_error` (500/504) | `RouterService.ParserError`, `RequestContentEncoding`, `UnknownExpectation`, `HandshakeError`, `HandleAsync` |
| aiohttp's request decompression (`DeflateBuffer`) | `RouterService.ReadActivityAsync` |

## The token check

The Python router called the Microsoft 365 Agents SDK's aiohttp `jwt_authorization_middleware` with
`AgentAuthConfiguration(client_id=<blueprint>, tenant_id=<tenant>)` for `/api/messages` and
`/activity/messages` (by `request.path`, whatever the method). Read from the SDK in the venv
(microsoft-agents-hosting-aiohttp and -core 1.10.0.dev6, PyJWT 2.15.1), that configuration accepts a
request when all of these hold, in this order:

1. The first `Authorization` header is `Bearer <token>`: the scheme in any case, whitespace (Python's
   `str.isspace`) around it allowed, none inside the token. Missing: 401
   `{"error": "Authorization header not found"}`; anything else: 401
   `{"error": "Invalid token or authentication failed."}`.
2. The token is a compact JWS as PyJWT reads it: split at the last and first dot, strict base64url
   (canonical, at most two `=`), a JSON object header (`kid` a string if present, `crit` only `b64`,
   `b64: false` always refused) and a JSON object payload (JSON read as `json.loads(bytes)`).
3. The unverified `aud` is a non-empty string (otherwise refused before any key is read).
4. Signing keys: the Bot Framework set (`https://login.botframework.com/v1/.well-known/keys`, or the
   US Government one) when the unverified `iss` is exactly `https://api.botframework.com` (or `.us`);
   otherwise `https://login.microsoftonline.com/<tenant>/discovery/v2.0/keys`.
5. The header has `kid`. Without it the SDK raised an uncaught `KeyError`: aiohttp answered 500. Kept.
6. The key with that `kid` among the set's signing keys (`use` is `sig` or absent, `kid` truthy). The
   set is cached 300 s; an unknown `kid` forces one refresh unless a fetch succeeded in the last 30 s;
   a failed fetch (network, status outside 2xx, redirect, not JSON, no usable keys) refuses the token
   and leaves the cache as it was.
7. `alg` is exactly `RS256` and equals the key's algorithm (its `alg`, or `RS256` for an RSA key
   without one); the RSASSA-PKCS1-v1_5 SHA-256 signature verifies.
8. With 300 s leeway, each only when present: `iat` and `nbf` are not after now + 300, `exp` is
   after now - 300 (Python `int()` of the claim, so `"123"`, `123.9` and `true` are accepted and
   `null` or `"soon"` refused; compared exactly with the float clock). `sub` and `jti` must be strings
   when present. No claim is required: a token without `exp` is accepted.
9. `aud` is one string equal to the client ID, ignoring case (Python `str.lower`).
10. Tenant binding: when `iss` is a recognised Entra issuer with a GUID tenant
    (`https://sts.windows.net/<guid>/`, `https://login.microsoftonline.com/<guid>/v2.0`, or the `.us`
    form; the prefix compared with Python's Unicode `IGNORECASE`, `$` also before a final newline) and
    `tid` is a string, `tid` must be that tenant, ignoring case.

The issuer is not otherwise checked: the SDK's `VALIDATE_ISSUER` was off, so a token signed with the
Entra or Bot Framework keys for the blueprint's audience is accepted from any issuer string (subject to
the tenant binding). That is what Python accepted; it is kept and flagged below.

**Why not Microsoft.IdentityModel or the Agents SDK for .NET.** `JsonWebTokenHandler` (and
`AddJwtBearer`) accept and refuse different tokens: they require `exp` unless told otherwise, treat
`exp` equal to the bound differently, do not check `iat`, `sub` or `jti`, decode base64url leniently,
ignore `crit`, parse JSON differently (duplicate claims, UTF-16 payloads) and refresh keys on their own
schedule (`ConfigurationManager`). The Agents SDK for .NET validates with those, plus an issuer list.
Matching Python exactly needed the checks written out, so they are, over
`System.Security.Cryptography` for the signature. Microsoft.IdentityModel.JsonWebTokens is used only
by the tests, to mint a token the way Microsoft's libraries do and show it is accepted.

**Key source.** `IJwksSource` returns a key set document for a URI; the service uses `HttpJwksSource`
(no redirects, 30 s), the tests a local set made from RSA keys generated in the test.

## Formats shared with the Python services

- **Provisioning queue messages** are `ProvisioningMessage.ToJson()` (Core): exactly Python's
  `json.dumps(message, sort_keys=True)`, e.g.
  `{"action": "ensure", "agent_identity_id": "agent-identity-1", "agent_user_id": "margo-user-new", "event": "first-activity"}`.
  They are sent as plain text: Python's `QueueClient` used `NoEncodePolicy` (read in
  azure-storage-queue 12.18.1); .NET's `QueueClientOptions.MessageEncoding` defaults to
  `QueueMessageEncoding.None`, and `StorageQueueProvisioningSink.ClientOptions()` sets it explicitly.
  A test checks both and that the `MessageText` on the wire is the JSON itself, not Base64.
- **The route table** is read with `RouteTable.Loads` (Core). A 404 from Blob storage is an empty table,
  as Python's `ResourceNotFoundError` was; the blob is decoded as strict UTF-8.

## Configuration

The environment variables the Python router read, which `autopilot/infra/main.bicep` sets (read through
`IConfiguration`, where `A__B` is the key `A:B`): `CONNECTIONS__SERVICE_CONNECTION__SETTINGS__CLIENTID`
and `...__TENANTID` (both required, checked first: the router exits with
`the agent blueprint client ID is not set; see docs/autopilot-deployment.md` and status 1),
`MARGO_ROUTES_URL`, `MARGO_PROVISION_QUEUE_URL`, `AZURE_CLIENT_ID` (a user-assigned managed identity;
empty or absent means `DefaultAzureCredential`), `HOST` (default `0.0.0.0`), `PORT` (default 8088, the
ingress target port) and `LOG_LEVEL` (default `INFO`; Python's level names).

## Deliberate deviations

1. **The body is read up to the limit.** Python read it with `request.content.read(MAX + 1)`, and
   aiohttp's `StreamReader.read(n)` returns whatever is buffered, not `n` bytes. Driving the Python
   router showed it: a valid 1 MiB body and a 1 MiB + 11 byte one were both cut short and answered
   400; the 413 branch was unreachable for bodies sent normally. This port reads up to
   `MAX + 1` bytes: at most 1 MiB is routed, more is 413. Python's outcome depended on how the body
   arrived: one larger than a single network read could be cut short at any size, and one shorter than
   its declared `Content-Length` was routed as far as it had arrived (review: a 163-byte activity
   declared as 40,000,000 bytes was forwarded); this port waits for the body or the limit, and a
   declared length over Kestrel's 30 MB limit is 413 at once.
2. **JSON that System.Text.Json cannot hold.** `NaN`, `Infinity` and `-Infinity` and nesting deeper
   than 1000 are refused (activity: 400; token: 401; key set: refused). Python accepted the literals,
   accepted nesting up to about 10,000 levels on 3.13 (review: an activity nested 1,200 deep was routed
   and a token with a claim nested 1,500 deep was accepted) and failed deeper nesting with
   `RecursionError` (500). Lone surrogates, which Core's
   `PythonJson.Loads` refuses, are kept here by a second reader, so an activity whose text holds a
   broken surrogate is still routed and one whose recipient holds one fails with 500, as in Python.
3. **No access log.** aiohttp logged every request line at INFO, including the voice page's query string,
   which carries the one-time sign-in code. This port never logs request lines (the
   `Microsoft.AspNetCore` categories stay at Warning whatever `LOG_LEVEL` says). Messages that Python
   logged are logged with the same text: `activity kind=... key=...` (`None` for no key),
   `route table could not be loaded`, `voice connection to the instance failed`, the SDK's
   `Malformed authorization header.`, `JWT validation error: <Python exception type>`,
   `JWT audience not accepted.`, `JWT tenant binding mismatch.`, and `Error handling request` for a 500.
   The console format is .NET's single-line format, not Python's.
4. **Connections to instances.** aiohttp's session limits (100 connections across all instances,
   10 s to get one, 300 s in total) are kept: a semaphore of 100 slots held for a request or a voice
   socket, waited for up to 10 s, then a 10 s TCP connect timeout (aiohttp shared one 10 s budget). Any
   of these timing out is answered with an empty 504 (`Content-Type: text/plain`), as aiohttp's server
   answered the `asyncio.TimeoutError` its client raised. The
   session's cookie jar is not kept (the instance sets no cookies). Redirects are followed (at most 10);
   .NET drops `Authorization` on every redirect, aiohttp only across origins; the instance does not
   redirect these requests. Responses are decompressed (gzip, deflate) as aiohttp did. The JWKS fetch
   has a 30 s total timeout; urllib's applied per socket operation. No trace context (`traceparent`,
   `tracestate`, `baggage`) is added to any outgoing request (aiohttp and urllib sent none; .NET would
   pass on the caller's). Header values are written as UTF-8, as aiohttp wrote them. aiohttp sent
   `User-Agent: Python/3.13 aiohttp/3.14.5`; .NET sends no `User-Agent`. When the instance closes the
   connection without answering the voice page GET, aiohttp tried twice and .NET up to four times (the
   activity POST is sent once by both).
5. **WebSockets.** No permessage-deflate is negotiated with the browser (aiohttp accepted it); the
   256 KiB limit applies to the uncompressed message (aiohttp: a message whose size reaches 256 KiB is
   refused, which is kept; with compression it checked compressed frames). aiohttp's limit of 1024
   fragments per message is not ported. Heartbeats: both sides ping every 20 s and give up 10 s
   without a pong; aiohttp pinged only after 20 s without a received message.
6. **A WebSocket upgrade that aiohttp would have accepted but ASP.NET Core cannot** (protocol version
   7 or 8, or a key with characters Python's lenient Base64 skipped) is answered 400
   `400: Bad Request`. Every handshake aiohttp refused gets aiohttp's 400 text, checked in its order:
   `No CONNECTION upgrade hdr: <Connection>`, `Unsupported version: <version>`,
   `Handshake error: <repr(key)>`; a key that is not ASCII is a 500 (Python's `b64decode` raised a
   `ValueError` aiohttp did not catch).
7. **A route whose `url` is truthy but not text** (the provisioner never writes one) makes `Decide` and
   `VoiceTargetFor` throw, so the request fails with 500. Python's `decide` returned it and the
   request failed with `TypeError` when the address was built; `voice_target` failed the same way.
8. **Startup messages.** A missing `MARGO_ROUTES_URL` or `MARGO_PROVISION_QUEUE_URL` or a `PORT` that is
   not a port number prints one line and exits with 1; Python died with a traceback (`KeyError`,
   `ValueError`).
9. **Key sets** (the documents Microsoft publishes, not caller input). RSA keys are read as PyJWT and
   `cryptography` read them (`e` at least 3, below `n` and odd). Keys of other families (EC, oct, OKP)
   are kept by key type and curve without checking their parameters; PyJWT skips one whose parameters
   are invalid, which could change whether an unknown `kid` refreshes. A private RSA key in the set
   fails verification with 500 (PyJWT: `AttributeError`), even one PyJWT could not build and skipped.
   `n` and `e` are decoded exactly as `from_base64url_uint` decodes them on Python 3.13
   (`PythonBase64`, checked against the venv with 20,000 generated inputs).
10. **Request paths** are taken from the raw request target (Kestrel's `Request.Path` removes dot
    segments and Python's did not), decoded as yarl decodes them: `path_safe` for routing, the full
    decoding for the activity check and `voice_target`; `OPTIONS *` is the path `*` (404). Kestrel
    itself refuses, with an empty 400, some requests aiohttp accepted: an absolute-form target whose
    host is not the `Host` header, `%00` in the path, a header value that is not UTF-8 (Python decoded
    it with `surrogateescape`; an `Authorization` like that was a 401), a repeated `Host`,
    `Content-Length` or `Transfer-Encoding` (aiohttp's 400 named the header); and it answers a non-OPTIONS
    `*` target with 405 (aiohttp: 400). Browsers and the platform never send these.
11. **HTTP-level details aiohttp produced that Kestrel does not.** aiohttp answered its parser errors
    with an `HTTP/1.0` status line and wrote some reason phrases differently (413 `Content Too Large`,
    Kestrel `Payload Too Large`); it sent `100 Continue` for `Expect: 100-continue` before routing,
    Kestrel when the body is first read; it closed the connection after a 500 or 504. Statuses, headers
    and bodies are the same.
12. **Compressed request bodies.** Like aiohttp, a last `Content-Encoding` of `gzip` or `deflate` (any
    case; deflate with or without the zlib header, chosen by the first byte) is decompressed before the
    size limit and JSON parsing, and the instance gets the decompressed body; data that does not
    decompress is a 500; `br` and `zstd` are refused with aiohttp's 400 text (it had no Brotli or zstd
    module) before anything else. Not reproduced: aiohttp failed gzip data followed by bytes that are
    not another gzip member (500; .NET ignores them), decoded concatenated zlib streams (.NET reads the
    first) and refused a deflate stream cut short with a 400 `deflate` (here the cut JSON is a 400 with
    no body).

## Kept as Python does it, though it may look odd

- A token without `kid` is a 500, not a 401 (see above), even when its audience is wrong but a string.
- The issuer is not checked against a list; only the tenant binding applies (see above).
- A failed route table load is retried on the very next request, with no back-off; until the first
  load succeeds the table is empty, so every activity is answered 503 and queued as `first-activity`.
- A lifecycle or provisioning message that cannot be queued fails the request with 500.
- The forwarded response keeps the instance's status and body and only the media type of its
  `Content-Type` (no `charset`); no other header, not even `Retry-After`, is passed on. An empty body
  has no `Content-Type`; a body without one is `application/octet-stream`.
- The voice page response keeps `Content-Type` (whole), `Cache-Control` and `Content-Security-Policy`
  only, first value of each. Neither the voice page nor the WebSocket receives any caller header,
  cookie or the query string. HEAD on a voice path is 405 (`Allow: GET`); health paths accept HEAD.
- An activity path with a wrong method is 401 without a valid token and 405 (`Allow: POST`) with one.
- A close from either WebSocket peer is answered with 1000 whatever its code.
- Before routing or the token check, as aiohttp's parser and default `Expect` handler did: a repeated
  `Content-Location`, `Content-Range`, `Content-Type`, `Etag`, `Max-Forwards`, `Server` or
  `User-Agent` header is a 400 `Duplicate '<Name>' header found.` (aiohttp's spelling, `Etag`
  included), and on HTTP/1.1 a first `Expect` value other than `100-continue` is a 417
  `Unknown Expect: <value>`, on every path.
- An unexpected failure is aiohttp's HTML 500 page (`text/html; charset=utf-8`) when the first
  `Accept` header contains `text/html` (case-sensitive), its plain-text 500 otherwise. A Kestrel
  request-size refusal (a declared body over 30 MB) is this router's 413, not a 500.

## Not ported

- `AgentAuthConfiguration` features the router never configured: anonymous access, US Government
  authorities, `VALIDATE_ISSUER`, explicit issuer lists, several connections.
- `python -m margo_autopilot.router` in `main.bicep`'s container command: the C# image needs its own
  entry point (infrastructure is outside this project; see open questions).

## Tests

`cloud/tests/Margo.Cloud.Router.Tests` (xUnit v3; TestServer, a real Kestrel router for WebSockets and
raw request targets, a fake instance on a loopback port, fake Blob/Queue/key sources, a hand-moved
clock, tokens signed with RSA keys generated in the test):

- `RouterDecisionTests`: `tests/test_autopilot_instances.py` `RouterDecisions` scenario for scenario,
  plus `decide`/`voice_target` values computed with the Python functions.
- `TokenValidationTests`, `SigningKeyCacheTests`: the token check and key cache. Every expected
  outcome (status and which key set was fetched) was produced by running the same cases through the
  Python SDK in the venv with a fixed clock and a stubbed key set fetch, except the token minted with
  Microsoft.IdentityModel.
- `RouterEndpointTests`, `VoiceProxyTests`: messages, forwarding, provisioning messages, health,
  404/405, size limit, the route cache, logging, the voice page and WebSocket bridge. Statuses, headers,
  bodies, queue texts, what reached the instance, close codes and the 37 raw voice targets were
  observed by driving the Python router (`build_app` with fake storage and a fake aiohttp instance)
  over raw sockets and an aiohttp WebSocket client. Exceptions, marked in the tests: the size limit
  (deviation 1) and the text of the bad-handshake 400 (deviation 6).
- `StartupTests`: configuration and the entry point (`WebApplicationFactory<Program>`), and the queue
  encoding on the wire. `PythonCompatTests`: `json.detect_encoding`, `surrogatepass`, yarl path
  decoding, `int()` and `lower()`, checked against Python 3.13. `InstanceConnectorTests`: the limits.

- `ReviewParityTests` (added in review): the differences an independent review found by sending the
  same raw requests to the Python router (`build_app` in the venv) and to this one side by side, each
  test failing before its fix: trace context passed on, non-ASCII header values (500), the HTML 500,
  504 on instance timeouts, `Expect`, repeated singleton headers, `br`/`zstd` and compressed bodies,
  Kestrel's 30 MB refusal (was 500), `OPTIONS *`, the WebSocket handshake texts, and `n`/`e` decoding
  and the odd-exponent check of key sets. The side-by-side run (about 230 fixed requests, 13 WebSocket
  scenarios and 700 generated tokens) left only the differences listed under the deviations above.

`tests/test_autopilot_runtime.py` has no router tests (its only router reference is a voice URL in an
orchestrator test); there is nothing more to port from it. The probe scripts that drove the Python
router are not in the repository (they import the scratch venv); the outcomes are in the tests.

## Open questions

- **Core's `int()`**: `PythonValues.TryParseInt` strips U+001C to U+001F, which Python's `int()` does
  not (`int("\x1f5")` raises `ValueError`; checked on 3.13). This project's copy is corrected; Core's
  owner may want the same fix.
- **Issuer validation**: Python accepted tokens from any issuer for the blueprint audience (the SDK's
  `VALIDATE_ISSUER` off). Turning it on is a behaviour change for both implementations, not a port.
- **Deployment**: `main.bicep` runs the router with `python -m margo_autopilot.router`; a C# image
  needs a different command (for example `dotnet Margo.Cloud.Router.dll`). The environment variables
  and port are unchanged.
- **`HOST`** `0.0.0.0` listens on IPv4 only, as aiohttp did with that value.
- **Which Python is the reference.** This port follows the venv (Python 3.13.16, aiohttp 3.14.5), but
  `autopilot/Dockerfile` builds the image from `python:3.12-slim`. Where they differ the deployed
  Python behaved otherwise: 3.12's `binascii.a2b_base64` stopped at the first complete padding (3.13
  skips `=` before the end), which changes the WebSocket key and key set decoding for inputs with a `=`
  inside, and 3.12's JSON nesting limit is lower. Neither matters for real browsers or Microsoft's keys.
- **Configuration keys** are read through `IConfiguration`, which also takes `appsettings.json`,
  command-line arguments and `DOTNET_`/`ASPNETCORE_`-prefixed variables, and ignores case; Python read
  the exact environment variable names only. The infrastructure sets only the exact names.

# Margo.Cloud.Host: porting notes

Port of `host/app.py` (Python hosted runtime, removed at the cutover) (the Microsoft 365 Agents SDK activity endpoint, self-hosted for
Agent 365) and `host/azure_store.py` (the Azure Blob lease store), on ASP.NET Core with the Agents SDK for .NET
(`Microsoft.Agents.Hosting.AspNetCore`, `.Builder`, `.Authentication.Msal` 1.9.205) and Agent 365 notifications
(`Microsoft.Agents.A365.Notifications` 2.0.0). The Python code is the specification; everything not listed under
"Deliberate deviations" is meant to behave the same. No package version was added.

## What maps to what

### host/app.py

| Python | C# | Notes |
| --- | --- | --- |
| `WORKIQ_SCOPE`, `HEALTH_PATHS`, `ACTIVITY_PATHS` | `HostSettings.WorkIQScope`, `HealthPaths`, `ActivityPaths` | |
| `HOSTING`, `AGENTIC_HANDLER`, `PRINCIPAL_HANDLER`, the `os.getenv` reads of `build_orchestrator` and `web_app` | `HostSettings.Load(IConfiguration)` | Same variables, same defaults, Python's order of failures (below) |
| `_configure_telemetry` | `MargoHost.Configure` (`TelemetryConnection.Problem`, then `UseAzureMonitor`) | Same three log messages; `OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT` set to `false` unless already set; a connection string Python's parser refused (or the .NET exporter would refuse at start-up) logs the failure and the host runs without telemetry, as in Python |
| `_credential()` | the `TokenCredential` service | `ManagedIdentityCredential(AZURE_CLIENT_ID)` else `DefaultAzureCredential` |
| `model_base_url`, `_model_client` | Engine's `AzureOpenAIModelClient` | Entra tokens for `https://cognitiveservices.azure.com/.default`, no keys |
| `_receipt_logger` | the guard and state recorders of `BuildOrchestrator` | `workiq ` + `json.dumps(receipt, sort_keys=True)` (Core's `PythonJson.Dumps`) at INFO |
| `_state_root()` | `HostSettings.EnsureStateRoot` | `MARGO_STATE_DIR` or `~/margo/state`; root and parent 0700 |
| `build_orchestrator()` | `MargoHost.BuildOrchestrator` | Engine (deployment, `MARGO_REPO_ROOT` default `/app`, `MARGO_TIMEZONE` default `America/Los_Angeles`), `McpHttpTransport`, the Blob store factory, `MARGO_PRINCIPAL_ACCESS` (default `obo`), the instance, `MARGO_STATE_CONTAINER`, the voice URL, logger `margo.autopilot` |
| `_identity(activity)` | `ActivityEvents.Identity` | `recipient.agenticAppId`; `recipient.agenticUserId` or `recipient.aadObjectId`; `from.aadObjectId`, `from.name`. Never the text |
| `_email_reference(activity)` | `ActivityEvents.EmailReference` | `AgentNotificationActivity(activity).EmailNotification` |
| `activity.is_email`, `stable_ids` | Core's `ActivityClassifier` via `ActivityEvents.IsEmail` | Entities passed as their JSON (`ActivityEntity.FromJson`), so `ProductInfoId` is not needed |
| `MargoHost.__init__`, `MsalConnectionManager`, `CloudAdapter`, `Authorization`, `AgentApplication`, `MemoryStorage` | `MargoHost.Configure`: `AddDefaultMsalAuth`, `AddAgentApplicationOptions`, `AddAgent<MargoAgent, MargoCloudAdapter>`, `MemoryStorage` | Connections, user authorization and options from configuration, as `load_configuration_from_env` did |
| `AgentNotification(app)` | the mail route (`MargoAgent.IsMailNotification`, the extension's email selector), `AgentNotificationExtensions.OnLifecycleNotification` | The mail route reads the Outlook reference with `ActivityEvents.EmailReference` instead of the extension's wrapper, which throws on a reference it cannot read (Python's dropped it) |
| `_token_getter(context)` | `MargoTurns.TokenGetter` | `margo` through `AUTH_HANDLER_NAME`, `principal` through `PRINCIPAL_AUTH_HANDLER` (none when empty), scope `api://workiq.svc.cloud.microsoft/.default`, via `UserAuthorization.ExchangeTurnTokenAsync`; any failure logs `token exchange failed for <kind>` (never the token) and counts as none |
| `on_message` | `MargoTurns.OnMessageAsync` (`ActivityEvents.Message`) | Ignored without agent identity, agent user or conversation (`message without agent identity or conversation; ignored`); mail keeps its Outlook IDs; typing indicator every 4 s; `turn failed` as the last resort; `turn status=<status>`; markdown reply, never to mail |
| `on_mail_notification` | `MargoTurns.OnMailNotificationAsync` (`ActivityEvents.MailNotification`) | Sender `from.aadObjectId` or `from.id`; channel `mail`; `notification status=<status>` / `notification failed`; no reply |
| `on_lifecycle` | `MargoTurns.OnLifecycleAsync` | Logs `lifecycle notification: <valueType>` only (deployed, the router turns these into provisioning) |
| `on_installation` | `MargoTurns.OnInstallationAsync` | Logs `installation update: <action>` only |
| `_typing` | private `TypingAsync` | Failures end it quietly |
| `web_app()`: `jwt` middleware, `agent_configuration`, routes | `MargoHost.Map`, `InboundAuthentication` | Token check on everything but `/`, `/liveness`, `/readiness`, `/voice`, `/voice/...`; `POST` on both activity paths (a body over 1 MiB, aiohttp's `client_max_size`, is `400 {"error": "Invalid JSON or unsupported Content-Type"}` as Python's SDK answered); `GET`/`HEAD` health answering `{"status": "ok"}` |
| `jwt_authorization_middleware` (SDK) | `Authentication/AgentTokenValidator` (copied from `Margo.Cloud.Router`) | See "The token check" |
| `VoiceService(...)`, `mount(app, voice)` | `VoiceService` and `VoiceEndpoints.Map` from `Margo.Cloud.Voice`, `SdkVoiceTokens` | When `MARGO_HOSTING=instance` and `VOICE_LIVE_MODEL`; `UseWebSockets`; logger `margo.voice`; scope `MARGO_VOICE_SCOPE` (default `Voice.Talk`) |
| `main()` / `run_app(host=HOST, port=PORT)` | `Program.cs` | Kestrel on `HOST` (default `0.0.0.0`) and `PORT` (default 8088); a start-up failure prints its message and exits 1 |

### host/azure_store.py

| Python | C# |
| --- | --- |
| `LEASE_SECONDS = 60`, `WAIT_SECONDS = 120`, `time.sleep(1)` | `AzureBlobLeaseStore.LeaseDuration`, `WaitLimit`, `RetryPause` |
| `AzureBlobLeaseStore(service, container)` | `AzureBlobLeaseStore(BlobServiceClient, container, TimeProvider?, delay?)`: implements the Orchestrator's `ILeaseStore` |
| `.factory(account_url, credential)` | `AzureBlobLeaseStore.Factory(Uri, TokenCredential)` (and `Factory(BlobServiceClient)` for tests); one store per container, under a lock; no network call |
| `ensure_container()`, `forget_container()` | `EnsureContainerAsync`, `ForgetContainerAsync` (one `SemaphoreSlim`, held across the create as Python held its lock) |
| `lease(name)` / `_Lease.__enter__` | `AcquireAsync`: bootstrap, then acquire a 60 s lease, retrying every second on 409 until 120 s (monotonic) have passed since the bootstrap |
| `_bootstrap(retry=True)` | empty upload with `If-None-Match: *`; 409 is fine; 412 (another holder's lease) is fine; 404 forgets the container and retries once |
| `read()` | `ReadAsync` under the lease ID; 404 is `(null, null)`; empty content is `null` data; ETag in header form (with its quotes, as Python's SDK returned it) |
| `write(data)` | `WriteAsync` under the lease ID; returns the new ETag |
| `renew()` | `RenewAsync`; exceptions propagate (StateSync treats them as a lost lease) |
| `__exit__` | `DisposeAsync`: release once; a failed release is ignored (the lease expires) |
| `put(name, data)` | `PutAsync`: container ensured, unleased overwrite (StateSync's daily copies `daily/YYYY-MM-DD.zip`) |
| `except AzureError` | `IsAzureError`: `RequestFailedException`, `AuthenticationFailedException`, `CredentialUnavailableException`, and, with the SDK's retries on, the `AggregateException` of connection failures or network timeouts (Python: `ServiceRequestError`, `ServiceResponseTimeoutError`); never the caller's own cancellation |
| `SyncError(...)` | Orchestrator's `SyncException` with the same three messages, the Azure exception as inner exception |

## Configuration

The host reads the same environment variables the Python host read and `cloud/infra/instance.bicep` and the
Dockerfile set; nothing in the infrastructure changes. .NET maps an environment variable `A__B` to the configuration key
`A:B`, and configuration keys ignore case, so the upper-case names with `SERVICE_CONNECTION` reach the SDK as they are:

- `CONNECTIONS__SERVICE_CONNECTION__SETTINGS__{AUTHTYPE,CLIENTID,TENANTID,FEDERATEDCLIENTID,SCOPES__0}`: the SDK's
  `ConfigurationConnections` reads section `Connections` and builds `MsalAuth` (the default type) with
  `AuthType=FederatedCredentials`. Without `AUTHTYPE` the SDK's default is `ClientSecret`, which needs
  `CLIENTSECRET` (local development, as in Python).
- `CONNECTIONSMAP__0__{SERVICEURL,CONNECTION}`: section `ConnectionsMap`; the connection name matches exactly.
- `AGENTAPPLICATION__STARTTYPINGTIMER`, `AGENTAPPLICATION__USERAUTHORIZATION__AUTOSIGNIN`,
  `AGENTAPPLICATION__USERAUTHORIZATION__HANDLERS__AGENTIC__{TYPE,SETTINGS__SCOPES}`: section `AgentApplication`; the
  handler type name `AgenticUserAuthorization` resolves to the SDK's built-in class.
- `MSALConfiguration` (optional) as in the SDK; `AddDefaultMsalAuth` registers MSAL's HTTP client and options.

`ConfigurationTests.The_sdk_reads_the_infrastructure_s_variables` sets these exact variables (with a private prefix) in
the process and reads them through .NET's environment-variable provider into the SDK's own types. Two adaptations were
unavoidable, both made in code (`MargoHost.SdkConfigurationAdaptations`), not in the infrastructure:

1. **`...__HANDLERS__AGENTIC__SETTINGS__SCOPES` is one string** (the Dockerfile). The .NET binder does not turn a string
   into the handler's `IList<string> Scopes` (it stays null; the test shows it); the host adds the same value as
   `...:Scopes:0`. These scopes are used only when the SDK signs Margo in for the route; every Work IQ token is asked for
   with the Work IQ scope explicitly.
2. **`AgentApplication:NormalizeMentions` defaults to `false`** (unless set). The .NET SDK normalises every mention in
   message text by default; Python only removed the recipient's mention, which `RemoveRecipientMention` (on in both)
   still does. The text matters: it is the command (`/voice`, `/replies`) and part of a derived message ID.

The SDK checks its configuration at start-up only in Development; the host resolves the connections, the application
options (user authorization handlers), the orchestrator and voice before it serves, so a misconfiguration stops the
process, as constructing `MargoHost` did in Python.

Start-up failures, in Python's order: `MARGO_MODEL_DEPLOYMENT is required`; `MARGO_STATE_BLOB_URL is required: state
must not live only in one session`; `AZURE_OPENAI_ENDPOINT is required (https://ACCOUNT.openai.azure.com)`; per-instance
`MARGO_INSTANCE_AGENT_ID`, `MARGO_INSTANCE_USER_ID`, `MARGO_STATE_CONTAINER`, and with voice `MARGO_INSTANCE_KEY` (when
`MARGO_PUBLIC_BASE_URL` is set), `MARGO_TENANT_ID` and the blueprint client ID (`<NAME> is required`, where Python raised
`KeyError`); then `the agent blueprint client ID and tenant are required to check activity tokens`. Voice's own
`VOICE_LIVE_ENDPOINT is required for voice` and settings errors come from `VoiceService`.

## The token check

`web_app()` wrapped every request except the health paths and `/voice`, `/voice/...` in the Agents SDK's aiohttp
`jwt_authorization_middleware` with `AgentAuthConfiguration(client_id=<blueprint>, tenant_id=<tenant>)`. That is the
check `Margo.Cloud.Router` already ports exactly (see its PORTING.md, "The token check": Bot Framework or tenant Entra
keys, RS256, 300 s leeway, audience equal to the blueprint ignoring case, the tenant binding, the 401 bodies and log
messages). Its four files and three helpers are copied into `Authentication/` with only the namespace changed (request
1). The verified claims become the request's `ClaimsIdentity` (Python: `request["claims_identity"]`), which the SDK
uses to pick the connection (audience) and validate the activity's `serviceUrl` against the token's `serviceurl` claim.

The SDK for .NET would instead use ASP.NET Core's JWT bearer handler (`AddJwtBearer`), which accepts and refuses
different tokens (see the Router's notes); it is not used. `CONNECTIONS__SERVICE_CONNECTION__SETTINGS__CLIENTSECRET`
and `SCOPES__0` changed the Python `AgentAuthConfiguration` but not what it accepted; they are not read by the check.

## Deliberate deviations

1. **202 before the turn.** The .NET `CloudAdapter` queues an ordinary (non-invoke, non-`expectReplies`) activity on its
   background service and answers 202 at once; Python's adapter ran the turn before answering. The router therefore no
   longer waits for a turn (its 300 s limit never applies); everything else (one turn per activity, the state lease) is
   unchanged. A turn still running when the process stops is cancelled through the turn's token; its state is not saved
   (StateSync removes the marker) and the next turn reinstalls the shared bundle.
2. **Route order.** Python registered the message route first, so mail arriving as a `message` on `agents:email` was a
   message turn (`Mail reaches this route first`), and its notification handler only saw other activity types. The .NET
   SDK sorts agentic routes, which the notification extension adds, before all others. `MargoAgent` registers the message
   route as agentic before the notification routes, and again for non-agentic requests, which restores Python's order
   (tested: a mail `message` reports `message`, an email `event` reports `mail`).
3. **Lifecycle selector.** The .NET extension also requires `type == "event"` (Python matched the name `agentLifecycle`
   on any type). Deployed, the router never forwards lifecycle events; either way they are only logged.
4. **Unknown token handler.** Python's `exchange_token` raised `ValueError` for a handler that is not configured; the
   .NET SDK returns null. The host checks the configured handler names and logs the same `token exchange failed for
   <kind>` warning.
5. **Unmatched requests.** With a valid token, an unknown path or method gets ASP.NET Core's empty 404/405 instead of
   aiohttp's text bodies; without one it is 401, as in Python (the check runs first). A token without `kid` is an empty
   500 (Python: aiohttp's 500 page). aiohttp's request-parser quirks the Router reproduces are not reproduced here: the
   host only ever sees the router's forwarded requests and Agent 365's.
6. **Logging.** .NET's single-line console format, at `LOG_LEVEL`. Request lines are never logged (the voice page's
   sign-in code is in a query string): `Microsoft.AspNetCore` and `System.Net.Http` are held at Warning, as in the
   Router. The SDK's own categories (`Microsoft.Agents*`) are held at Warning, and `MargoCloudAdapter` drops the four
   `CloudAdapter` log events whose message is an activity or response body (IDs 2, 3, 4, 10). Python logged neither.
7. **Typed start-up errors.** `KeyError: 'NAME'` becomes `NAME is required`; Python printed a traceback, this prints the
   message and exits 1.
8. **Voice settings** are read from configuration (the same variables) rather than the process environment, so a test
   host can set them; `VoiceService` gets them as `VoiceServiceOptions.Settings` and `.Endpoint`.
9. **Cancellation.** The turn's cancellation token (the SDK's background service) reaches the orchestrator; Python had
   none. Cancellation is not logged as `turn failed`.
10. **Health** answers `HEAD` too (aiohttp's `add_get` did as well).
11. **Mail notification without a readable reference** (review): Python's `on_email` wrapper raised `ValueError` when an
    activity had neither a readable `emailNotification` entity nor a known notification `name` (the turn then failed);
    the host queues it with the transport's IDs instead. A reference that fails validation is dropped in both.
12. **Telemetry connection strings** (review): Python's parser and the .NET exporter's accept different strings. The host
    refuses what Python refused (same messages) and also what the .NET exporter would refuse at start-up (an empty value,
    a value without a keyword, a repeated keyword, an endpoint that is not an absolute URI, a bad `Location` or
    `EndpointSuffix`, over 4096 characters); either way it logs `Telemetry configuration failed; continuing without
    it.` and runs without telemetry. The `APPINSIGHTS_INSTRUMENTATIONKEY` fallback of Python's parser is not read.
13. **Bodies** (review): the 1 MiB limit is on the bytes received; aiohttp also decompressed a `Content-Encoding: gzip`
    or `deflate` body first (the SDK for .NET does not decompress; the router forwards decompressed bodies).

## Not ported

- `logging.basicConfig`'s exact line format (above).
- `run_app`'s access log (never wanted: it carried query strings).
- aiohttp's middleware chain as such; the order (token check, then routing) is kept.

## Requests to other components

1. **Shared token check** (integration owner): `Authentication/` is a copy of `Margo.Cloud.Router/Authentication` plus
   `PythonCompat`, `PythonBase64`, `PythonJsonBytes`. Moving them into one library both services reference (and Voice's
   `Jwt/` pieces with them) would leave one implementation to review. Until then, a change to one must be made to both.
2. **Cutover (image and Dockerfile, not done here):** a .NET 10 ASP.NET runtime image; `dotnet publish -c Release` of
   this project (it brings `margo-state.dll` and its `runtimeconfig.json`; the build fails if the contract probes would
   ship); `agents/` and `skills/` under `/app` (`MARGO_REPO_ROOT=/app`); **the `tzdata` package** (State's `PyZoneInfo`
   reads the system TZif files for `MARGO_TIMEZONE`); `ENTRYPOINT ["dotnet", "Margo.Cloud.Host.dll"]`; the same `ENV`
   lines (SDK names, `HOST`, `PORT=8088`, `OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=false`). The voice page's
   files are already embedded by `Margo.Cloud.Voice`.
3. **Orchestrator:** none. `InMemoryLeaseStore` and the public `SyncException` were enough.

## Open questions

1. **The manager's on-behalf-of token.** In both SDKs `exchange_token` / `ExchangeTurnTokenAsync` only returns a token for
   a handler that signed in during this turn, and the routes sign in only the agentic handler (Python passed
   `auth_handlers=[AGENTIC_HANDLER]`). So `PRINCIPAL_AUTH_HANDLER` alone yields no token and `obo` turns read the
   manager's data as unavailable. Kept as Python behaves; the first live run should confirm it (VERIFY.md step 6). The
   fix, if confirmed, is adding the principal handler to the message route's sign-in handlers for the manager only.
2. **Request telemetry.** Azure Monitor's ASP.NET Core instrumentation records request URLs; recent OpenTelemetry
   versions redact query-string values by default. Confirm in the test tenant that the voice page's `?code=` does not
   reach Application Insights (VERIFY.md step 9).
3. **Concurrency.** The SDK's background service runs activities concurrently; turns for one instance are serialised by
   the state lease, as in Python with several workers. Whether Agent 365 retries an activity that was answered 202 but
   whose turn later failed is unknown; the office queue's stable IDs make a redelivery a no-op either way.

## Independent review (worktree-wf_d9a503d3-da2-2, on top of ec3f9c7)

Found by differential runs against the Python SDKs in the reference venv and the decompiled .NET SDKs, each fixed with a
failing-first test:

1. **Connection failures and network timeouts escaped `except AzureError`.** With the Azure SDK's default retries,
   .NET reports them as an `AggregateException` of the attempts, which `IsAzureError` did not recognise: a storage
   outage became turn status `error` ("turn failed") instead of Python's `state_unsafe`, and a release over a dead
   connection threw out of `DisposeAsync`. (`AzureBlobLeaseStoreTests.Connection_failures_after_the_sdk_s_retries_are_typed_sync_errors`,
   `Network_timeouts_are_typed_sync_errors_but_the_caller_s_cancellation_is_not`.)
2. **A malformed `APPLICATIONINSIGHTS_CONNECTION_STRING` stopped the host at start-up** (the exporter parses it only when
   the host starts) after logging "Application Insights configured"; Python logged the failure and served without
   telemetry. (`HostIntegrationTests.A_malformed_application_insights_connection_string_is_logged_and_the_host_serves_without_telemetry`,
   `TelemetryConnectionTests`: Python's verdicts recorded from the venv, and the .NET exporter's own parser as the oracle
   over 20,000 generated strings; the test reaches that internal parser by reflection, test-only.)
3. **An activity over 1 MiB was processed**; Python's host (aiohttp `client_max_size`) answered 400.
   (`HostIntegrationTests.An_activity_over_one_mebibyte_is_refused_as_python_s_host_did`.)
4. **A mail notification whose Outlook reference the SDK could not read failed the turn** (the A365 extension's wrapper
   throws on, for example, a numeric `id`); the SDK's turn-error handler then sends the exception text to the
   conversation, a reply to mail. Python dropped the reference and queued the mail.
   (`HostIntegrationTests.A_mail_notification_with_an_unreadable_outlook_reference_is_still_queued_without_a_reply`.)
   `ActivityEvents.EmailReference` now also catches every exception, as `_email_reference` did.

Checked and found consistent (no change): route order for mail as `message` versus `event`; the token check (a byte
copy of the Router's); the four `AzureStoreTests`; the configuration keys through the SDK's types; the logging floors
with Azure Monitor added (no provider-specific rule re-enables `Microsoft.Agents` debug output); the Release publish
(`margo-state.dll` present, no probes assembly); `RemoveRecipientMention` against Python's; the SDK's default
turn-error reply (both SDKs send the exception text, so it is not a regression). Not fixed, noted: the .NET lifecycle
route still uses the extension's wrapper (a lifecycle event carrying an unreadable mail reference would fail its turn;
Python's would not); the .NET `RemoveRecipientMention` throws on a recipient mention in a message without text (Python
skipped such messages; read in the decompiled SDK, not run).

## Tests and evidence

`cloud/tests/Margo.Cloud.Host.Tests` (75 test cases after the review below), no network:

- `AzureBlobLeaseStoreTests`: the four `AzureStoreTests` of the former Python runtime tests (stores reused and no
  network at construction; a 412 bootstrap waited for; other failures typed `SyncException`; a removed container
  created again), plus a container missing twice, the 60 s lease and `If-None-Match: *` bootstrap, 409 retried every
  second up to 120 s, reads/writes under the lease ID with quoted ETags, a missing blob and failed reads/writes, renewal
  failure propagating and release failure ignored (released once), daily copies, and two sandboxes sharing one bundle
  through the real `StateSync`. All against the real Azure SDK over an in-memory Blob service (`FakeBlobService`, an
  `HttpClientTransport` handler with Blob status and error codes).
- `HostIntegrationTests`: `WebApplicationFactory<Program>` with the instance template's and Dockerfile's variables, real
  Activity JSON and tokens minted with a local RSA key (the key set injected as `IJwksSource`), through the token check,
  the SDK adapter, routes and agentic sign-in (a fake MSAL connection), the orchestrator, the engine with a fake model,
  the guard with a fake Work IQ, the in-memory lease store, and replies through a fake connector service: health without
  a token; refused tokens (none, wrong audience, wrong key, other tenant, expired) and 401 before routing; a manager turn
  answered in markdown with a typing indicator first, Margo's Work IQ token for exactly this agent user, state saved in
  `MARGO_STATE_CONTAINER`; a colleague queued; mail on the message route queued and never answered; an email
  notification queued without a reply; lifecycle and installation only logged; another instance's activity
  (`wrong_instance`) and a missing identity; a group chat refused; an unconfigured principal handler; voice mounted
  outside the check with the `/voice` link. Logs are checked to hold no message text, model output or token.
- `ConfigurationTests`: the refusals and their order, defaults, the private state root, the exact variables through
  .NET's environment provider into the SDK's types (and why the scope adaptation is needed).
- `ActivityEventsTests`: identity and fallbacks, derived IDs, email shapes on the SDK activity, Outlook IDs, the
  notification's sender fallback, `SdkVoiceTokens`, the body-free adapter logger.

Release output checked by hand: `dotnet build -c Release` and `dotnet publish -c Release` put `margo-state.dll`,
`margo-state.runtimeconfig.json` and `.deps.json` next to the host and no `Margo.Cloud.State.Probes.dll`; the published
`margo-state.dll` runs and does not know the probe scripts. The csproj target `MargoRefuseProbesInRelease` fails a
Release build or publish that would ship the probes.

Not verified: anything against real Entra, Agent 365, Teams, Outlook, Work IQ, Blob Storage, Azure OpenAI or Voice Live.
These are contract tests with fakes, not a live run; `VERIFY.md` is the checklist for the first test-tenant run.

## Cutover

The Python hosted runtime was removed in the Phase 6 cutover. It was the specification of this
port; the C# code is now the implementation of record, and the desktop state helpers in
`skills/chief-of-staff/scripts/` stay the specification of the state commands (`tools/state_parity.py`).

- Image: `cloud/Dockerfile` publishes the host (with `margo-state` next to it), the router and the provisioner in
  Release and runs them on `mcr.microsoft.com/dotnet/aspnet:10.0-alpine` plus `tzdata`, as the non-root `APP_UID`
  user; the default command is the host. The ENV lines are the Python Dockerfile's (minus the Python ones).
  `instance.bicep`/`instance.json` name the host's command explicitly. The cutover review built it with
  Docker (outside Azure; the Alpine package mirror was unreachable, so the zone files were copied in instead of
  `apk add tzdata`, which stays unverified) and started each role in it as the non-root user: the host and router
  answered `/`, `/liveness` and `/readiness`, the provisioner printed its usage and failed on the missing
  `AZURE_SUBSCRIPTION_ID` as designed, and `margo-state` created its SQLite databases (musl `e_sqlite3`). Never run in
  a tenant.

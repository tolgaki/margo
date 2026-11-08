# Margo.Cloud.Provisioner: porting notes

Ports `provision/` (Python hosted runtime, removed at the cutover) (`core.py`, `azure.py`, `__main__.py`): the
scheduled Container Apps job that drains the provisioning queue, deploys or retires one container
per hired Margo and keeps the route table. Naming, plans, ARM parameters, provisioning messages and
the route table come from `Margo.Cloud.Core`; nothing is re-implemented here.

## What maps to what

| Python | C# |
| --- | --- |
| `core.Provisioner(cloud=, config=, template=, log=)` | `Provisioner(ICloud, JsonNode? config, JsonNode? template, Action<string>? log, TimeProvider?)` |
| `Provisioner.handle(raw, dequeue_count)` -> `"done"`/`"retry"`/`"dropped"` | `HandleAsync(string?, long, CancellationToken)` and `HandleAsync(JsonNode?, ...)` -> `HandleOutcome` |
| `MAX_ATTEMPTS`, `ROUTE_STATES` | `Provisioner.MaxAttempts`, `Provisioner.RouteStateSteps` |
| `_set_route`'s 5 optimistic tries | `Provisioner.RouteWriteAttempts` |
| the `cloud` duck type (`deploy`, `delete_app`, `delete_identity`, `load_routes`, `save_routes`) | `ICloud` (`RouteBlob` for `(text, etag)`) |
| `azure.AzureCloud` | `AzureCloud` (Azure.ResourceManager, Azure.Storage.Blobs) |
| `QueueClient.from_queue_url` (`receive_messages`, `delete_message`, `send_message`) | `IProvisioningQueueClient`, `AzureProvisioningQueueClient` (`QueuedMessage`) |
| `_credential()`, `AzureCloud(...)`, `_queue(credential)` | `IProvisionerServices`, `AzureProvisionerServices` |
| `run`, `enqueue`, `redeploy_all`, `main`, `__main__` | `ProvisionerCommands.RunAsync`, `EnqueueAsync`, `RedeployAllAsync`, `MainAsync`, `InvokeAsync`; `Program.cs` |
| `VISIBILITY_SECONDS`, `messages_per_page=16` | `ProvisionerCommands.VisibilitySeconds`, `MessagesPerPage` |
| `APP_API`, `IDENTITY_API` | `AzureCloud.AppApiVersion`, `AzureCloud.IdentityApiVersion` |
| `load_config` | `ProvisionerConfig.Load` (`ProvisionerConfig.Overrides`) |
| `json.loads(Path(MARGO_INSTANCE_TEMPLATE or the image's `instance.json`).read_text())` | `ProvisionerConfig.LoadTemplate` |
| `os.environ[name]` (KeyError) | `ProvisionerConfig.Require` (`KeyNotFoundException`) |
| `logging.basicConfig(...)` + `logging.getLogger("margo.provision")` | `ProvisionerLog` |

## Behaviour kept exactly

- **Handling one message.** Malformed text, a non-object, an unknown action or identifiers that
  cannot form a key: logged as `dropped malformed provisioning message` and dropped, before the
  route table is read. Otherwise the route table is read, `Instances.Plan` decides the steps, and
  `instance KEY: ACTION -> ['step', ...]` (or `-> nothing to do`) is logged with Python's list
  text. A failing step logs `instance KEY: step failed: TYPE`, records the route as `failed` with
  `error` = the exception's type name (`could not record the failure` when that write fails too),
  and returns retry, or dropped once `dequeue_count >= 5`. Log lines carry instance keys, never
  identifiers, tokens or message text.
- **Route writes.** Each route step re-reads the blob, applies `RouteTable.Put` with the instance's
  internal URL, and writes against the ETag it read; a conflict is retried up to five times, then
  `RouteException("route table kept changing; try again")` fails the step. Route steps need the
  configuration's `environmentDomain` (a missing key, a non-object configuration or a bad domain
  fails the step, and recording the failure fails the same way, as in Python).
- **Reading the route table is not guarded** (Python reads it outside `handle`'s `try`, despite the
  docstring's "never raises"): an unreadable blob or invalid table propagates, the job exits 1 and
  the message reappears after its visibility timeout.
- **Draining.** Pages of up to 16 messages, each hidden for 900 seconds, are received until a page
  comes back empty (Python's `MessagesPaged` stop rule), handled one at a time; done and dropped
  messages are deleted, retry messages are left to reappear. `dequeue_count or 1`. There is no
  poison queue: after the fifth failure the message is deleted and the route stays `failed`.
  `processed N message(s)` is logged at the end.
- **Retiring.** `route_retired`, then delete the Container App (404 ignored), then the identity:
  read its `principalId` (404: nothing to do), list the resource group's role assignments with
  `$filter=principalId eq '...'`, delete each by id as its page is read (200 deleted and 204
  already gone both succeed, anything else throws, as `delete_by_id` did), then delete the
  identity. Storage is never deleted: `ICloud` has no call that could.
- **Deploying.** `margo-instance-KEY`, `{"properties": {"mode": "Incremental", "template": ...,
  "parameters": ...}}` into the job's resource group, waiting until the deployment ends; a failed
  deployment throws. API versions are pinned to what Python used: Container Apps `2024-03-01`,
  identities `2023-01-31`, deployments `2025-03-01` and role assignments `2022-04-01` (the defaults
  of `azure-mgmt-resource` 23.4.0 and `azure-mgmt-authorization` 4.0.0).
- **Route blob conditions.** Read: 404 (blob or container) is `("", null)`; the ETag is kept as the
  service sent it. Write: `If-None-Match: *` for a new blob, `If-Match: ETAG` otherwise; any 409, or
  412 with `ConditionNotMet` or `BlobOverwritten`, is a conflict (`false`), as the Python SDK mapped
  them to `ResourceExistsError`/`ResourceModifiedError`; anything else throws. Text is strict UTF-8
  both ways (a byte order mark is kept and then refused as "route table is not JSON").
- **Queue text.** No Base64 (`QueueMessageEncoding.None`, Python's default `NoEncodePolicy`), so the
  router's JSON is read as is. `enqueue` writes `ProvisioningMessage.ToJson()` (Python's
  `json.dumps(..., sort_keys=True)`) with `event: operator`; `redeploy-all` writes each ready
  route's raw identifier values with `PythonJson.Dumps(sortKeys: true)`, unvalidated, and stops
  with `KeyNotFoundException` on an entry missing one (messages already queued stay queued).
- **Command line.** `run ...` and `redeploy-all ...` ignore further arguments; `enqueue ACTION ID
  USER [--redeploy]` needs exactly that shape; anything else prints three usage lines to standard
  error and exits 2. `enqueue` validates before signing in or reading `MARGO_PROVISION_QUEUE_URL`.
  `run` reads its settings in Python's order: template, `AZURE_SUBSCRIPTION_ID`,
  `AZURE_RESOURCE_GROUP`, `MARGO_ROUTES_URL`, `MARGO_PROVISIONER_CONFIG` (+ overrides),
  `MARGO_PROVISION_QUEUE_URL`. An empty variable is a value; a missing one is an error.
- **Configuration.** `MARGO_PROVISIONER_CONFIG` is parsed with `PythonJson.Loads` (repeated keys as
  Python) and kept as JSON whatever it is; each of `MARGO_INSTANCE_IMAGE`, `MARGO_STORAGE_ROLE_ID`,
  `MARGO_PULL_ROLE_ID`, `MARGO_MODEL_ROLE_ID`, `MARGO_SPEECH_ROLE_ID`, `MARGO_BLUEPRINT_CLIENT_ID`
  that is set and not empty overrides its key in place (new keys go last). An override on a
  non-object configuration fails.
- **Sign-in.** `ManagedIdentityCredential` for `AZURE_CLIENT_ID` when set and not empty, otherwise
  `DefaultAzureCredential`.
- **Logs.** `YYYY-MM-DD HH:MM:SS,mmm INFO margo.provision | message`, local time, standard error.

## Deliberate deviations

1. **Template location.** Python read `the image's `instance.json`` unless
   `MARGO_INSTANCE_TEMPLATE` was set. Here the default is `instance.json` next to the binary: the
   csproj links `cloud/infra/instance.json` and copies it to the build and publish output
   (requested for this port). `MARGO_INSTANCE_TEMPLATE` still wins, even when empty, as
   `os.getenv(name, default)` did.
2. **Exception type names on failed routes.** The route's `error` is the .NET type name
   (`RequestFailedException`, `InvalidOperationException`, `KeyNotFoundException`,
   `RouteException`) where Python wrote `HttpResponseError`, `TypeError`, `KeyError`, `RouteError`.
   The field is diagnostic, and Core already records `RouteException` for Python built-in errors.
   The ported Python test asserts the fake's type name accordingly.
3. **Uncaught errors.** Python printed a traceback and exited 1; `InvokeAsync` writes the
   exception's `ToString()` (type, message, stack) to standard error and returns 1. Usage text names
   `Margo.Cloud.Provisioner` instead of the Python module.
4. **Cancellation.** Python has none. Here an `OperationCanceledException` raised because the job's
   token was cancelled propagates instead of being recorded as a step failure (the analogue of the
   process being stopped); `Program.cs` passes no token, so the job behaves as Python's did.
5. **Identity without a usable principal.** A missing `properties` gives `InvalidOperationException`
   (Python: `TypeError`), a missing `principalId` `KeyNotFoundException` (Python: `KeyError`), and a
   non-string `principalId` `InvalidOperationException` (Python would have sent `str(value)` in the
   filter and failed at ARM). All fail the step before anything is deleted.
6. **Request encoding details.** The role assignment filter is sent with an unescaped `'`, and the
   template and parameters are written by the .NET SDK; ARM reads both the same. The .NET SDK
   parses subscription ids and role assignment principal ids as GUIDs: a malformed
   `AZURE_SUBSCRIPTION_ID` fails each ARM step with `FormatException` before any request, where
   Python sent the request and ARM refused it (either way the step is recorded as failed and the
   job keeps draining). Azure SDK HTTP logging (Python's `azure.*` loggers at INFO through
   `basicConfig`) is not reproduced: the .NET SDK logs through EventSource, off by default.
7. **ARM deployments package.** `Azure.ResourceManager.Resources` 1.12.0 marks its deployment types
   obsolete in favour of `Azure.ResourceManager.Resources.Deployments`, and its obsolete
   `ArmDeploymentCollection.CreateOrUpdate` fails at run time ("No ModelReaderWriterTypeBuilder
   found for ArmDeploymentData"; found by `AzureCloudTests`). This project therefore references
   `Azure.ResourceManager.Resources.Deployments` 1.0.0 (added to `cloud/Directory.Packages.props`;
   it needs no newer Azure.Core than the solution already resolves) instead of
   `Azure.ResourceManager.Resources`, which nothing else here used.
8. **A deployment whose first response is already "Failed" fails the step.** azure-core's
   `LROPoller` never runs its polling loop when the PUT response is already terminal, so Python's
   `.result()` returned normally for `provisioningState: Failed` in the first response and the
   route went to `ready`. The .NET operation throws `RequestFailedException`; the step is recorded
   as failed and retried. Kept on purpose: reproducing it would report a failed deployment as
   ready (AGENTS.md: no success-shaped results). ARM normally answers a deployment PUT with
   `Accepted` and an `Azure-AsyncOperation` header, where both agree.
9. **A 404 while polling a Container App delete counts as deleted.** Azure.Core ends a DELETE
   whose `Location` poll answers 404 as complete; Python's `ARMPolling` raised
   `HttpResponseError`, the step failed and the next attempt's DELETE got 404 (ignored) and went
   on to the identity. Both end with the app and identity gone; C# one attempt sooner. A 404 on
   the initial DELETE is ignored in both.
10. **JSON the Core reader refuses reaches the provisioner as a malformed message.**
    `PythonJson.Loads` (Margo.Cloud.Core) rejects `NaN`, `Infinity`, `-Infinity`, escaped lone
    surrogates and nesting deeper than 1000, which Python's `json.loads` accepts. A queue message
    holding one of these (for example `"redeploy": NaN` or `"event": "\udc00"`) is dropped here
    and handled in Python; a route table holding one stops the job here and is read in Python.
    Neither the Python nor the C# router or provisioner writes such JSON (identifiers with lone
    surrogates are dropped by both, as they cannot form a key). Owned by Margo.Cloud.Core; not
    changed here.

## Not ported

- `logging.basicConfig` for every logger (Azure SDK loggers included): only `margo.provision`
  lines are written, see deviation 6.
- The `Deployment` tests in the former Python instance tests (`main.bicep` outputs against
  `postprovision.sh`, no Foundry hosted agent): they check the deployment scripts and infrastructure,
  not the provisioner. The `instance.json`/`instance.bicep` parameter check is in
  `Margo.Cloud.Core.Tests` (`PlanTests`).
- (Done at the cutover: `main.bicep` runs `dotnet /app/provisioner/Margo.Cloud.Provisioner.dll run`.) Pointing the job at
  this binary is infrastructure work outside this project.

## Tests

`cloud/tests/Margo.Cloud.Provisioner.Tests`:

- `ProvisionerStepsTests`: the three `ProvisionerSteps` tests of the former Python instance tests
  with the same `FakeCloud`, plus scenarios whose expected logs, calls, outcomes and route blob were
  recorded by running `provision/core.py` on Python 3.13 with the same fake and `_utc_now` fixed
  (lifecycle, failure records, persistent conflicts, bad configuration, unreadable table),
  malformed messages, retirement retries and cancellation.
- `CommandsTests`: `main`, `run`, `enqueue`, `redeploy-all`, `load_config` and the template with a
  fake queue that applies visibility timeouts and dequeue counts. The drain scenario (18 messages
  deleted, 19 log lines, then 15 redeploys in table order with exact text) and the enqueue texts
  and usage were recorded by running `provision/azure.py` on Python 3.13 with the SDK clients
  replaced by fakes; retries across runs and the fifth-attempt drop are checked with a moving clock.
- `AzureCloudTests`: `AzureCloud` and `AzureProvisioningQueueClient` through the real Azure SDK
  pipelines against canned ARM, Blob and Queue HTTP responses (an `HttpMessageHandler`, no
  network): URLs, API versions, deployment body, long-running deployment polling, the deletion
  order, ETag conditions and conflict mapping, queue paging parameters and plain-text messages,
  role assignments answered with 204 and listed in pages, refused role assignment deletes, and
  deviations 8 and 9.

What these do not show: behaviour against real Azure (permissions, real LRO headers, throttling
and retries), and that the C# job and a Python router agree on a live queue. Neither was run.

## Review (independent, after the port)

Evidence gathered outside the repository (scratch harnesses, not committed):

- **Differential fuzz of `handle`:** 23,000 generated cases (messages with odd actions,
  identifiers, `redeploy` values and raw JSON; route tables with every state, odd revisions, extra
  fields and invalid tables; configurations with missing, falsy and odd values; conflicts, failing
  steps, failing reads, dequeue counts) run through `provision/core.py` and this `Provisioner` with
  equivalent scripted clouds. Outcomes, log lines, cloud calls (deployment parameters and template
  text included), every route blob written and the final ETag were identical, except exception
  type names (deviation 2) and messages holding JSON the Core reader refuses (deviation 10).
- **Differential fuzz of the commands:** 15,000 generated `main` command lines, environments, route
  tables and queue pages through `provision/azure.py` (SDK clients replaced by fakes) and
  `ProvisionerCommands`: exit codes, standard error (timestamps and program name aside), clients
  created, messages sent, receives, deletes, cloud calls and route writes were identical, except
  the default template path (deviation 1) and exception type names.
- **HTTP-level differential:** `AzureCloud` and `AzureProvisioningQueueClient` against the real
  Python job's `AzureCloud` and `QueueClient` (azure-mgmt-resource 23.4.0, azure-mgmt-authorization
  4.0.0, azure-storage-blob 12.31.1, azure-storage-queue 12.18.1) with the same 28 canned response
  scripts. Methods, URLs, query strings, conditional headers and bodies matched, except: Python's
  first blob GET carries `x-ms-range` (and an empty blob costs it a second GET; same result), the
  queue body's XML declaration, deviations 8 and 9, and the defect below.
- **Defect fixed:** a role assignment DELETE answered 204 No Content made
  `RoleAssignmentResource.DeleteAsync` throw `NullReferenceException` after the request had
  succeeded. Retiring an instance whose assignment list still showed an already deleted
  assignment failed that attempt (route `failed`, error `NullReferenceException`), and every
  later one while the list still showed it; after the fifth the message was dropped and the
  identity left behind. Python went on to the next assignment and the identity.
  Role assignments are now deleted with a plain `DELETE {id}?api-version=2022-04-01` on the ARM
  pipeline that succeeds on 200 or 204 and throws otherwise, as `delete_by_id` did (a generic
  resource delete would refuse assignments above a subscription and poll a 202).

## Open questions

- Should the job pass a cancellation token from SIGTERM (Container Apps stops a job replica with
  SIGTERM)? Python did not handle it; neither does `Program.cs`.
- `main.bicep` and the image build must switch the provisioner job to this binary and include
  `instance.json` next to it (or set `MARGO_INSTANCE_TEMPLATE`).
- Inherited from Python, unchanged: a page of 16 messages is hidden for 900 seconds, but 16
  deployments can take longer. A message handled after its visibility timeout may already have
  been received again (new pop receipt), so its delete fails and the job exits 1 (`replicaTimeout`
  is 1800 seconds). Should the job receive fewer messages per page, or renew visibility?

## Cutover

The Python hosted runtime was removed in the Phase 6 cutover. It was the specification of this
port; the C# code is now the implementation of record, and the desktop state helpers in
`skills/chief-of-staff/scripts/` stay the specification of the state commands (`tools/state_parity.py`).

- `main.bicep` runs `dotnet /app/provisioner/Margo.Cloud.Provisioner.dll run`; the template moved to
  `cloud/infra/instance.json` and the csproj copies it from there (the image also copies it to the same place).

# Margo.Cloud.Provisioner: porting notes

Ports `autopilot/src/margo_autopilot/provision/` (`core.py`, `azure.py`, `__main__.py`): the
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
| `json.loads(Path(MARGO_INSTANCE_TEMPLATE or /app/autopilot/infra/instance.json).read_text())` | `ProvisionerConfig.LoadTemplate` |
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
  `$filter=principalId eq '...'`, delete each by id, then delete the identity. Storage is never
  deleted: `ICloud` has no call that could.
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

1. **Template location.** Python read `/app/autopilot/infra/instance.json` unless
   `MARGO_INSTANCE_TEMPLATE` was set. Here the default is `instance.json` next to the binary: the
   csproj links `autopilot/infra/instance.json` and copies it to the build and publish output
   (requested for this port). `MARGO_INSTANCE_TEMPLATE` still wins, even when empty, as
   `os.getenv(name, default)` did.
2. **Exception type names on failed routes.** The route's `error` is the .NET type name
   (`RequestFailedException`, `InvalidOperationException`, `KeyNotFoundException`,
   `RouteException`) where Python wrote `HttpResponseError`, `TypeError`, `KeyError`, `RouteError`.
   The field is diagnostic, and Core already records `RouteException` for Python built-in errors.
   The ported Python test asserts the fake's type name accordingly.
3. **Uncaught errors.** Python printed a traceback and exited 1; `InvokeAsync` writes the
   exception's `ToString()` (type, message, stack) to standard error and returns 1. Usage text names
   `Margo.Cloud.Provisioner` instead of `python -m margo_autopilot.provision`.
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

## Not ported

- `logging.basicConfig` for every logger (Azure SDK loggers included): only `margo.provision`
  lines are written, see deviation 6.
- The `Deployment` tests in `tests/test_autopilot_instances.py` (`main.bicep` outputs against
  `postprovision.sh`, no Foundry hosted agent): they check the deployment scripts and infrastructure,
  not the provisioner. The `instance.json`/`instance.bicep` parameter check is in
  `Margo.Cloud.Core.Tests` (`PlanTests`).
- `main.bicep` still runs `python -m margo_autopilot.provision run` in the job; pointing the job at
  this binary is infrastructure work outside this project.

## Tests

`cloud/tests/Margo.Cloud.Provisioner.Tests`:

- `ProvisionerStepsTests`: the three `ProvisionerSteps` tests of `tests/test_autopilot_instances.py`
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
  order, ETag conditions and conflict mapping, queue paging parameters and plain-text messages.

What these do not show: behaviour against real Azure (permissions, real LRO headers, throttling
and retries), and that the C# job and a Python router agree on a live queue. Neither was run.

## Open questions

- Should the job pass a cancellation token from SIGTERM (Container Apps stops a job replica with
  SIGTERM)? Python did not handle it; neither does `Program.cs`.
- `main.bicep` and the image build must switch the provisioner job to this binary and include
  `instance.json` next to it (or set `MARGO_INSTANCE_TEMPLATE`).

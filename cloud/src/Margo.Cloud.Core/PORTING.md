# Margo.Cloud.Core: porting notes

Ports `autopilot/src/margo_autopilot/instances.py`, `activity.py` and `instance.py`: instance keys and
Azure names, the route table, Agent 365 lifecycle mapping, provisioning messages and plans, ARM
parameters for `autopilot/infra/instance.json`, email classification, stable message IDs and the
instance binding. The project has no package references: no Agents SDK, no Azure SDK.

## What maps to what

| Python | C# |
| --- | --- |
| `instance._ID`, `instance._check` | `OpaqueIdentifier.IsValid`, `OpaqueIdentifier.Check` |
| `instance.BindingError` (a `ValueError`) | `BindingException` (an `ArgumentException`) |
| `instance.InstanceBinding`, `.account`, `.is_principal` | `InstanceBinding`, `.Account`, `.IsPrincipal` |
| `InstanceBinding.open_ledger` | not ported, see below |
| `instances.RouteError` (a `ValueError`) | `RouteException` (an `ArgumentException`) |
| `SCHEMA_VERSION` | `RouteTable.SchemaVersion` |
| `READY` ... `FAILED`, `STATES` | `RouteStates` |
| `ENSURE`, `SUSPEND`, `RETIRE` | `ProvisioningActions` |
| `LIFECYCLE` | `Instances.Lifecycle` |
| `KEY` | `Instances.IsInstanceKey` |
| `instance_key` | `Instances.InstanceKey` |
| `resource_names` (dict) | `Instances.ResourceNames` (`InstanceResourceNames`) |
| `internal_url` | `Instances.InternalUrl` |
| `activity_identity` (tuple) | `Instances.ActivityIdentity(JsonNode?)` (`AgentIdentity`) |
| `lifecycle_message` (dict) | `Instances.LifecycleMessage(JsonNode?)` (`ProvisioningMessage`) |
| `check_message` | `Instances.CheckMessage(JsonNode?)`, `CheckMessage(ProvisioningMessage)`, `ParseMessage(string)` (`CheckedMessage`) |
| `json.dumps(message, sort_keys=True)` (router, `enqueue`, `redeploy_all`) | `ProvisioningMessage.ToJson()` |
| `RouteTable`, `.loads`, `.dumps`, `.get`, `.resolve`, `.put`, `.routes` | `RouteTable`, `.Loads`, `.Dumps`, `.Get`, `.Resolve`, `.Put`, `.Routes` (`RouteEntry`, `RouteLookup`) |
| `_utc_now` | a `TimeProvider` passed to `RouteTable` / `RouteTable.Loads` (system clock by default) |
| `plan` and its step names | `Instances.Plan(ProvisioningMessage, RouteEntry?)`, `Instances.Plan(action, redeploy, state)`, `PlanSteps` |
| `REQUIRED_CONFIG` | `Instances.RequiredConfig` |
| `deployment_parameters` | `Instances.DeploymentParameters` (config and result are `JsonObject`) |
| `activity._value` on a dict | `ActivityEntity.FromJson` |
| `activity.is_email` | `ActivityClassifier.IsEmail(EmailFacts)`, `ActivityClassifier.IsEmail(JsonNode?)` |
| `activity.stable_ids` (tuple) | `ActivityClassifier.StableIds` (`MessageIds`) |
| `json.loads`, `json.dumps`, `float.__repr__` | `PythonJson.Loads`, `PythonJson.Dumps`, `PythonJson.FormatFloat` |
| `str.isspace`, `len`, slicing, ordering, `str.lower()`, `.encode("utf-8")` | `PythonText` (internal) |
| truthiness, `int()`, `== 1` on JSON values | `PythonValues` (internal) |

## Formats shared with the Python services

Python and C# services may read each other's data during the cutover, so these are byte-for-byte:

- **Route table blob.** `RouteTable.Dumps()` writes exactly `json.dumps({"schema_version": 1,
  "routes": ...}, sort_keys=True, indent=1)`: keys sorted by code point, `ensure_ascii` escapes with
  lower-case hex, Python `int` text and `float.__repr__`. Entries are kept as JSON objects, so
  fields a newer writer adds survive a load and save. A repeated key resolves as in Python (last
  value, first position). `updated_at` is `isoformat(timespec="seconds")` in UTC (`+00:00`).
- **Provisioning queue messages.** `ProvisioningMessage.ToJson()` writes `json.dumps(message,
  sort_keys=True)`: `action`, `agent_identity_id`, `agent_user_id`, then `event` when set and
  `"redeploy": true` only when true, as every Python writer does.
- **Instance keys and stable message IDs.** Same SHA-256 inputs: `"{identity}\n{user}"` and
  `json.dumps([sender or None, conversation or None, text or ""], ensure_ascii=False)`, in UTF-8.

Evidence: the golden strings in `RouteTableTests`, `LifecycleTests`, `NamesTests` and
`ActivityTests` were produced by running the Python functions on Python 3.13 with
`instances._utc_now` fixed at `2026-11-07T09:30:00+00:00` (non-ASCII identifiers, an error over
500 code points, retirement, a foreign blob with unknown fields and floats, and both queue message
shapes). The C# output is compared to them exactly.

## Calling it from the Host (Agents SDK)

`Margo.Cloud.Core` must not depend on the SDK, so the Host passes values it reads from the SDK
`Activity`:

- `ActivityClassifier.IsEmail(new EmailFacts { Channel, SubChannel, ChannelIdText, ProductInfoId,
  Entities })`. Python reads `activity.channel_id` either as an object (`channel`,
  `sub_channel`) or as text (`agents:email`): set `Channel`/`SubChannel` from the structured
  channel id and `ChannelIdText` to its text, which is used only when `Channel` is empty.
  `ProductInfoId` is the id of the activity's product-info entity (Python's
  `get_product_info_entity()`); `Entities` is each entity's type and `id`
  (`new ActivityEntity(type, id)`, or `ActivityEntity.FromJson` for entity JSON). I have not
  verified the C# SDK's member names for these here.
- `ActivityClassifier.StableIds(messageId, conversationId, senderId, text)` with the same values
  `host/app.py` passes: the mail reference's id or the activity id, its conversation id or the
  activity's, the sender's object id, and the text.
- The Router passes raw activity JSON (`PythonJson.Loads(body)`) to
  `Instances.LifecycleMessage` and `Instances.ActivityIdentity`, and uses
  `RouteTable.Resolve`. `PythonJson.Loads` is recommended over `JsonNode.Parse` for activities,
  queue messages and provisioner configuration because it handles repeated keys as Python does
  (`JsonNode.Parse` throws on them when the object is read).

## Deliberate deviations

1. **Exception types.** `BindingError`/`RouteError` become `BindingException`/`RouteException`,
   both `ArgumentException`s, because Python's are `ValueError`s and callers catch `ValueError`
   (the provisioner's `except (ValueError, RouteError)`). Python's `UnicodeEncodeError` for a lone
   surrogate becomes `EncoderFallbackException`, also an `ArgumentException` and, as in Python,
   not a binding error: `RouteTable.Resolve` lets it through.
2. **Python built-in errors become `RouteException`.** Where Python raises `TypeError`,
   `ValueError` or `OverflowError` from a built-in on malformed data, C# throws `RouteException`:
   a list or object `state` in a loaded entry (Python: unhashable `TypeError`) gives "malformed
   route entry"; a previous `revision` that `int()` cannot convert gives "malformed route entry";
   a `minReplicas` that `int()` cannot convert gives the "minReplicas must be 0 or 1" message; a
   non-iterable `extraEnv` gives "extraEnv must be a list". Every current Python caller catches
   these with `except Exception` or not at all, so the outcome is the same. The visible
   difference: the provisioner records `type(exc).__name__` in the route entry's `error`, so a C#
   provisioner would record `RouteException` where Python recorded `TypeError`. That field is
   diagnostic and already differs for Azure SDK exceptions.
3. **JSON the reader refuses.** `PythonJson.Loads` uses `System.Text.Json`, which cannot hold the
   literals `NaN`, `Infinity` and `-Infinity` or an escaped lone surrogate; these and nesting
   deeper than 1000 are reported as not JSON ("route table is not JSON", "provisioning message is
   not JSON"). Python accepts them, but no Python writer of the shared formats produces them
   (nothing stores a float or a lone surrogate there).
4. **`ParseMessage` is a composition.** Python's provisioner does `check_message(json.loads(raw))`
   and drops a message on `ValueError`. `ParseMessage` does both and throws `RouteException`
   ("provisioning message is not JSON") where Python raised `JSONDecodeError`; both are dropped.
5. **`CheckMessage` returns a typed message.** Python returns `dict(message, key=key)` with every
   field. `CheckedMessage` keeps what a provisioner reads: the recomputed key, `action`, the two
   identifiers, `event` when it is a string, and `redeploy` evaluated by Python truthiness. Unknown
   fields are not re-serialised by any Python caller, so nothing observable changes.
6. **`RouteEntry` is a snapshot with typed accessors.** A property is `null` when the field is
   missing or not of the expected JSON type (Python would hand back the raw value, and a
   non-string `url` would crash the router's string concatenation). `ToJsonObject()` returns the
   raw fields. `RouteTable.Routes` returns snapshots in table order.
7. **`InstanceBinding` cannot be re-set with `with`.** Its properties have no `init` accessor, so
   a copy cannot skip validation (Python's `dataclasses.replace` re-runs `__post_init__`).
8. **Integer text.** `int()` of a string follows Python (strip `str.isspace` characters, optional
   sign, single underscores between digits, Unicode decimal digits). Decimal digits come from
   .NET's Unicode tables, which may differ from Python 3.13's for digits added in later Unicode
   versions.
9. **Lower-casing.** Python compares `str.lower()` results with ASCII words (`agentlifecycle`,
   lifecycle events, `email`, `agents`, `productinfo`, entity keys). `PythonText.LowerAscii` is
   exact for that purpose: only ASCII letters and U+212A KELVIN SIGN lower-case to ASCII (U+0130
   becomes `i` plus a combining dot). It is written out because `OrdinalIgnoreCase` does not treat
   U+212A as `k`, and .NET casing depends on the globalization mode.
10. **Non-string entity fields.** `ActivityEntity.FromJson` keeps a truthy non-string value as its
    JSON text where Python uses `str()`; neither ever equals `productinfo` or `email`.
11. **Additions for adapters, no new behaviour:** `EmailFacts.FromActivityJson` and
    `ActivityClassifier.IsEmail(JsonNode?)` read raw activity JSON (Python only had the SDK object
    path; the entity loop covers what the product-info getter would find, so `ProductInfoId` is
    left empty there); `Instances.Plan(action, redeploy, state)`; `CheckMessage(ProvisioningMessage)`
    for operator enqueues.

Kept as Python does it, though it may look odd: `RouteTable.Put` does not validate the key (a later
load refuses it); `schema_version` equal to `1.0` or `true` is accepted (`1 == 1.0 == True`); an
empty `agenticUserId` falls back to `aadObjectId`, and an empty `agenticAppId` is returned by
`ActivityIdentity` (it fails later at `InstanceKey`); any action other than ensure or suspend is
planned as a retirement; `config.get("voiceName", "")` keeps a present `null`.

## Not ported

- `InstanceBinding.open_ledger`: the binding supplies `Account`; the Guard and Orchestrator build
  ledgers through `Margo.Cloud.State.Work`, so Core stays free of state code.
- `router/core.py` (`decide`, `voice_target`) and `provision/core.py` (`Provisioner`): they belong
  to `Margo.Cloud.Router` and `Margo.Cloud.Provisioner`, which call this project.

## Tests

`cloud/tests/Margo.Cloud.Core.Tests` ports, scenario for scenario:

- `tests/test_autopilot_instances.py` `NamesAndRoutes` (keys, route table round trip and
  validation, lifecycle mapping) and `Plans` (plans, deployment parameters, and the
  `instance.json`/`instance.bicep` consistency check, reading both files from the repository root
  found above `AppContext.BaseDirectory`). `RouterDecisions`, `ProvisionerSteps` and `Deployment`
  belong to the Router and Provisioner ports.
- `tests/test_autopilot_runtime.py` `EmailClassificationTests` and `StableIdTests`, and
  `tests/test_autopilot_guard.py` `InstanceBindingTests` (the binding scenarios live there).
- Edge cases checked against Python 3.13 output: identifier characters and lengths in code
  points, Python lower-casing, `activity_identity` truthiness, `_value` key lookup, `int()`
  conversions, `list()` of `extraEnv`, missing-configuration order, float formatting, key order by
  code point, and the golden blobs above.

## Open questions

- `PythonJson` overlaps the `PyJson` helper planned in `Margo.Cloud.State.Core`. Core does not
  reference State.Core (it is ported in parallel and Core should not depend on state code); merge
  them once both exist, keeping the golden tests.
- The plan mentions shared contract fixtures in `cloud/contracts/` for instance names and router
  decisions. This port keeps its Python-generated goldens inside the test project because
  `cloud/contracts/` is outside its write scope.
- Queue message encoding (text or Base64) must match between the Python and .NET Queue SDK
  clients; that is configured in the Router and Provisioner, not here, and is not verified here.

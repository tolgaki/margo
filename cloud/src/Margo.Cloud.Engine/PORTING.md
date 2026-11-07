# Margo.Cloud.Engine: porting notes

Port of `autopilot/src/margo_autopilot/engine.py` (the model turn loop), `skills.py` (the persona and skills served
unchanged), `parity.py` (what is not hosted yet, told to the model) and `statecmd.py` (the `margo-state` tool), plus
the model client `host/app.py` built with `openai.AsyncOpenAI`. The Python code is the specification; everything not
listed under "Deliberate deviations" is meant to behave the same.

## What maps to what

### engine.py

| Python | C# | Notes |
| --- | --- | --- |
| `PREFIX`, `MAX_OUTPUT_CHARS`, `STOPPED`, `HOSTED_ADDENDUM`, `STATE_TOOL` | `EngineTools.Prefix`, `MaxOutputChars`, `Stopped`, `HostedAddendum`, `StateTool` | Text identical (checked against Python) |
| `DESCRIPTIONS`, `ARG_SCHEMAS`, `TOOL_ARG_SCHEMAS` | `EngineTools.Descriptions`, `ArgSchemas`, `ToolArgSchemas` | Schemas held as JSON text |
| `load_instructions(repo_root, *, timezone, now, library)` | `EngineTools.LoadInstructions(repoRoot, timeZone, now, library)` | `str.format` in one pass; `strftime("%A %d %B %Y, %H:%M")` in the C locale |
| `SKILL_TOOLS`, `state_tool()`, `function_tools(mode)` | `EngineTools.SkillTools()`, `StateToolDefinition()`, `FunctionTools(mode)` | Byte-identical JSON for every mode (checked against Python) |
| `_clip(result)` | `EngineTools.Clip(result)` | `json.dumps(ensure_ascii=False)`, lengths in code points |
| `_output_text(response)` | `Engine.OutputText(JsonObject)` | |
| `_as_dict(response)` | private `Engine.AsDict` | anything but an object is `{}` |
| `Engine(model, deployment, repo_root, preferences_path, timezone, max_rounds)` | `Engine(IModelClient, deployment, repoRoot, preferencesPath, timeZone, maxRounds)` | `.repo_root`, `.timezone` are `RepoRoot`, `TimeZone` (the voice server reads them) |
| `Engine.run(text, *, mode, guard, office, state, previous_response_id, now, preferences_path)` | `Engine.RunAsync(...)` returning `EngineResult` | `EngineResult.ToJsonObject()` is Python's dict |
| `Engine._dispatch(name, raw_arguments, mode, guard, office, library, state)` | `Engine.DispatchAsync(...)` (public, static) | The voice server calls it directly, as Python's does |
| `model.responses.create(**kwargs)` | `IModelClient.CreateResponseAsync(JsonObject)` | The request object holds the kwargs in Python's order |
| `office.pending()` (an `OfficeReader`) | `IOfficeRequests.PendingAsync()` | Implemented by the Orchestrator port |
| `state.run(arguments, mode=mode)` | `IStateCommands.RunAsync(arguments, mode)` | `StateCommands` implements it |
| `guard.call(tool, arguments, mode=mode)` via `asyncio.to_thread` | `await guard.CallAsync(tool, arguments, mode, ct)` then `.ToJsonObject()` | `Margo.Cloud.Guard` |

### host/app.py (the model client only)

| Python | C# |
| --- | --- |
| `MODEL_SCOPE` | `AzureOpenAIModelClient.Scope` (`https://cognitiveservices.azure.com/.default`) |
| `model_base_url(endpoint)` (`RuntimeError` without one) | `AzureOpenAIModelClient.ModelBaseUrl` (`InvalidOperationException`, same message) |
| `_model_client(credential)`: `AsyncOpenAI(base_url=..., api_key=get_bearer_token_provider(credential, MODEL_SCOPE))` | `new AzureOpenAIModelClient(HttpClient, endpoint, TokenCredential)` or `AzureOpenAIModelClient.FromEnvironment(http)` |
| `_credential()`: `ManagedIdentityCredential(client_id=AZURE_CLIENT_ID)` or `DefaultAzureCredential()` | `AzureOpenAIModelClient.InstanceCredential()` |
| `openai.APIStatusError`, `APITimeoutError`, `APIConnectionError` | `ModelApiException` (an `HttpRequestException` with `StatusCode`), `ModelTimeoutException`, `ModelConnectionException` |

`CreateResponseAsync` posts the request as JSON to `{AZURE_OPENAI_ENDPOINT}/openai/v1/responses` with
`Authorization: Bearer <Entra token>`; no API key anywhere. It behaves as the SDK did: 600 s per attempt, 2 retries
after a connection error, a timeout or a 408/409/429/5xx answer (`x-should-retry` decides when present; a
`retry-after` over 60 s is not waited for), waiting `retry-after-ms`/`retry-after` when it is between 0 and 60 s,
else 0.5 s doubling up to 8 s less up to 25% jitter, with one idempotency key for all attempts of a request. The error
message is the SDK's (`Error code: 503 - {'error': ...}` with the body's Python repr). The token is cached and
refreshed five minutes before it expires, as `get_bearer_token_provider`'s policy does. `CreateHttpClient()` gives
the SDK's 5 s connect timeout and redirect following.

### skills.py

| Python | C# |
| --- | --- |
| `MAX_FILE_CHARS`, `PRIVATE_PREFERENCES`, `LEDGER_ONLY`, `EXCLUDED_DIRS` | `SkillLibrary.MaxFileChars`, `PrivatePreferences`, `LedgerOnly`, `ExcludedDirs` |
| `split_front_matter(text)` | `SkillLibrary.SplitFrontMatter(text)` (`Fields`, `Body`) |
| `SkillLibrary(repo_root, preferences)` | `SkillLibrary(repoRoot, preferences)` |
| `.persona()`, `.skills()`, `.index()`, `.files(name)`, `.load(name)`, `.read(name, path)` | `Persona()`, `Skills()` (`SkillInfo` records), `Index()`, `Files(name)`, `Load(name)`, `Read(name, path)` |
| `._skill(name)`, `._relative(path)` | private `FindSkill`, public `SkillLibrary.Relative(path)` |
| `posixpath.normpath` | `SkillLibrary.NormPath` |
| `Path.read_text(encoding="utf-8")` | `SkillLibrary.ReadText`: strict UTF-8 (State.Core `PyIO.DecodeUtf8`, `UnicodeDecodeError` text), BOM kept, universal newlines |

`Load`/`Read` take the model's raw JSON values (`JsonNode?`), so a non-string name or path behaves as in Python;
string overloads exist for callers. Ordering follows Python: skills by directory name and files by path parts, in
code-point order; `rglob("*.md")` semantics of Python 3.13 (hidden entries match, symbolic links to directories are
not followed, a symbolic link is never served, a directory named `x.md` is listed and reading it fails).

### parity.py

| Python | C# |
| --- | --- |
| `NOT_HOSTED_PROGRAMS`, `NOT_HOSTED_SCRIPTS`, `NOT_HOSTED_WORKIQ` | `NotHosted.Programs`, `Scripts`, `WorkIQ` (sorted tool list) |
| `summary()` | `NotHosted.Summary()` (identical text, checked against Python) |

### statecmd.py

| Python | C# |
| --- | --- |
| `READ`, `PRIVATE`, `HUMAN`, `MODE_CLASSES` | `StateCommandClasses.Read`, `Private`, `Human`; `StateCommands.ModeClasses` |
| `COMMANDS`, `STATEFUL`, `NOT_HOSTED_COMMANDS`, `FORBIDDEN_OPTIONS` | `StateCommands.Commands` (a stateless helper is `{read: {null}}`), `Stateful`, `NotHostedCommands`, `ForbiddenOptions` |
| `FILE_NAME`, `MAX_ARGS`, `MAX_ARG`, `MAX_ARGS_TOTAL`, `MAX_INPUT`, `MAX_FILES`, `MAX_FILES_TOTAL`, `MAX_OUTPUT`, `TIMEOUT_SECONDS` | same names in `StateCommands` (`DefaultTimeout`) |
| `ENV_PASSTHROUGH` | `StateCommands.EnvironmentPassthrough` (plus `DOTNET_ROOT`, see deviation 1) |
| `command_class(script, command)` | `StateCommands.CommandClass` |
| `StateCommands(scripts_dir, account, state_root, preferences, budget, recorder, timeout, python)` | `StateCommands(account, stateRoot, preferences, budget, recorder, timeout, cliCommand)` |
| `.initialise()` (`RuntimeError`) | `InitialiseAsync()` (`InvalidOperationException`, same message) |
| `.run(arguments, *, mode)` | `RunAsync(arguments, mode)` |
| `_first_error(stderr)` | internal `StateCommands.FirstError` |
| the receipt dict | `StateReceipt` (`ToJsonObject()` in Python's key order; never arguments, input or output) |
| `_Refused`, `_Unavailable` | private `RefusedException`, `UnavailableException` |

The command runs as `[dotnet, <margo-state.dll>, SCRIPT.py, --account A, --state-dir D, ARGS...]` with no shell:
the DLL is `MARGO_STATE_CLI` when set, else `margo-state.dll` next to the running application
(`StateCommands.DefaultCliCommand()`); `dotnet` is the running host when this process is `dotnet`. The account and
state root come from the constructor (the binding), never from the model. `cliCommand` replaces
`[python, -s, scripts_dir/]` for tests. Order of checks, messages, the classes allowed per mode, `@file:NAME` files
written (0600) to a private 0700 directory that is the process's working directory and is removed after the call,
`@preferences`, stdin from `input` (`--input -`), the budget charged after validation and before running, the
timeout (`error` for a read, `unknown` for a write), the output cap (`partial`), exit 2 or 0 as `error`, any other
failure of a write as `unknown`, and receipts are all as in Python.

## Deliberate deviations

1. **The CLI is .NET.** The environment passed to it is `ENV_PASSTHROUGH` plus `DOTNET_ROOT` (so `dotnet` finds its
   runtime where it is not installed globally); `PYTHONDONTWRITEBYTECODE`/`PYTHONIOENCODING` are not set (the C# CLI
   always speaks UTF-8). Probes stay off: `MARGO_STATE_PROBES` is never passed.
2. **A missing CLI file ran nothing.** With the default command, a missing `margo-state.dll` is reported as
   `error`, "could not start the command: FileNotFoundError", before starting anything; `dotnet missing.dll` would
   otherwise exit with a host error code that a write would report as `unknown`. (Python's `python3 -s missing.py`
   exits 2, which is `error` too.) A program that cannot be started is reported with the `OSError` subclass name
   for its errno (`FileNotFoundError`, `PermissionError`, ...), as Python does.
3. **Exit codes of killed processes.** .NET reports a process killed by signal N as exit code 128+N where Python
   reports -N. Only receipts show it; both are "unknown" for a write.
4. **Timeouts kill the process tree** (`Process.Kill(entireProcessTree: true)`); Python kills the child only. With
   `dotnet margo-state.dll` there is one process either way.
5. **Cancellation** (Python has none). A cancelled token on entry throws `OperationCanceledException` and records
   nothing. Cancelled while the CLI runs: the process is killed, a receipt `error` (read) or `unknown` (write) with
   reason "cancelled..." is recorded, and the exception is rethrown. A cancelled office read is not reported as
   "the request queue could not be read".
6. **Exceptions where Python raises.** Kept, with Python's type and message as State.Core `PyException`s: a list or
   object `script` (`TypeError: unhashable type`), a lone surrogate in `files` or `input` (`UnicodeEncodeError`), an
   option name with a NUL character (`ValueError: embedded null byte`), output so deeply nested that `json.loads`
   runs out of recursion (`RecursionError`). Python's `subprocess` passes a lone surrogate in U+DC80..U+DCFF through
   as a raw byte (surrogateescape); .NET cannot put raw bytes in an argument, so every lone surrogate in an argument
   is a `UnicodeEncodeError`. A model response Python fails on while reading `output` (an item that is not an
   object, `output: null`, a non-empty string) is `MalformedModelResponseException` where Python raised
   `AttributeError`/`TypeError`; the turn fails either way.
7. **Typed values.** `EngineResult.ResponseId` is a string or null: a non-string `id` from the model is dropped
   (Python would store it and send it back as `previous_response_id`, which the API refuses). Within a turn the
   follow-up request still carries the raw `id`, as in Python, including `null` when the response had none.
   `now` is a `DateTimeOffset` (always aware); Python also accepted a naive `datetime`. `mode` is `string?`.
8. **`str.lower()`** for skill names uses .NET's simple lower-case mapping plus Python's one unconditional special
   case (U+0130 becomes `i` and U+0307); the final-sigma rule is not modelled, so a skill name ending in a capital
   sigma would compare differently. Skill names are ASCII.
9. **Time zones** come from the system's tz database through `TimeZoneInfo` (Python: `zoneinfo`); an unknown zone is
   `TimeZoneNotFoundException`. The year is printed without padding, as glibc's `%Y` does.
10. **Model client details.** The idempotency key is `margo-cloud-retry-<guid>` (the SDK's `stainless-python-retry-`
    prefix names the SDK); the SDK's `x-stainless-*` telemetry headers are not sent. The body is compact JSON
    (`ensure_ascii=False`, no NaN), which the service reads the same as the SDK's. The response is parsed with
    State.Core's `PyJson` (Python's `json.loads`), not through the SDK's pydantic models, so fields are kept as sent.
11. **`@preferences`** passes the configured path as given; Python passed `str(Path(...))`, which normalises
    repeated and trailing slashes.

## Not ported here

- **The orchestrator, office queue, voice server and host wiring** (`orchestrator.py`, `office.py`,
  `voice/server.py`, `host/app.py` apart from the model client) belong to `Margo.Cloud.Orchestrator`,
  `Margo.Cloud.Voice` and `Margo.Cloud.Host`. They implement `IOfficeRequests`, build one `StateCommands` per turn
  (with the turn's `CallBudget`) and call `InitialiseAsync` once per instance, as Python does.
- **`LedgerBackedTests`, `StateSyncTests`, `OrchestratorTests`** and the rest of `tests/test_autopilot_runtime.py`
  belong to those ports; `DelegationTests` and `TransportTests` are in `Margo.Cloud.Guard.Tests` and
  `Margo.Cloud.WorkIQ.Tests`.

## Requests to other components

1. **`memory_state.py` in `margo-state`.** `task_state.py` (`Margo.Cloud.State.Tasks`) has landed and is checked in
   full. `memory_state.py` (`Margo.Cloud.State.MemoryCommands`) has not: until it does, `InitialiseAsync` fails with
   "could not initialise memory_state.py: unknown script 'memory_state.py' ..." and memory commands answer `error`.
   The engine needs no change when it lands; the skipped tests below then run in full (they compare all 47 of its
   subcommand parsers with Python's, so a partial port fails rather than passes).
2. **Margo.Cloud.Host** must ship `margo-state.dll` (with its `.deps.json`/`.runtimeconfig.json` and the
   `Margo.Cloud.State.*` assemblies) next to the host, a Release build so no contract probes are included, or set
   `MARGO_STATE_CLI`; and its image must copy `agents/` and `skills/` to the engine's `repoRoot`
   (`The_image_carries_the_persona_and_every_skill` checks any Dockerfile under `cloud/`).
3. **Margo.Cloud.WorkIQ tests: a timing-sensitive test.** `TransportTests.Errors_map_to_guard_transport_errors`
   gives its first call (session handshake plus a 503) a 200 ms deadline. In a full `dotnet test --solution` run on
   a 4-core machine it now fails with `TransportTimeoutException` instead of `TransportException` (503). It passed
   5 of 5 runs on its own and 3 of 3 alongside `Margo.Cloud.Guard.Tests`, and failed in 4 of 6 runs started together
   with this whole test project (1 of 10 with a single test class) and in 4 of 4 full solution runs. Nothing here touches the WorkIQ transport or its
   fake server; the extra load of another test host appears to be enough. A longer deadline for the calls that are
   not meant to time out would fix it; this port does not edit another component's tests.

## Review (engine-review)

An independent review read `engine.py`, `skills.py`, `parity.py`, `statecmd.py` and the model client side by side
with this port, then tried to break it with generated inputs compared against the Python modules and with attacks on
the tool boundary.

**Behaviour: no defect found in the port's sources.** Generated cases, all compared as text with Python: 20,000+
`margo-state` calls (hostile scripts including probe names, `../` and NUL; forbidden options and their abbreviations,
`=` forms, padding and case; paths in every form the rules name; `@file:` and `@preferences` in and out of options;
file names, counts and sizes around `MAX_FILES`/`MAX_FILES_TOTAL`; `input` around `MAX_INPUT`; argument counts and
lengths around the limits; lone surrogates, astral characters, NUL; unknown modes), identical results and receipts;
1,500 command outcomes from a fake CLI printing arbitrary bytes with any exit status (invalid and partial UTF-8, BOMs,
`NaN`, duplicate keys, `{"error": ...}` of every type, stderr JSON lines, Unicode line separators, output around
`MAX_OUTPUT` in one- to four-byte characters), identical; 11,000 function calls with odd names, raw arguments and
modes, identical results and Work IQ calls; 4,600 whole turns of odd model responses (non-object items, odd `output`,
`id`, `call_id`, `arguments`, `output_text`, round limits 0-3), identical requests (`previous_response_id`, inputs,
call ids, clipped outputs), results and Work IQ calls, apart from deviation 7; 40,000 skill paths and front matter
texts, identical. The only differences seen were deviations 6 (surrogateescape) and 7, already listed.

**Security: nothing the model can reach gets past the rules, in either implementation.** Checked: forbidden options
by abbreviation after the subcommand (`--acc=x`, `--state-d=x`, `--state-r x`) are rejected by the C# CLI's parser
exactly as by argparse (exit 2), and nothing can precede the subcommand; short-option tricks (`-input=/etc/passwd`,
`-i/x`) are rejected by both parsers; relative paths cannot leave the per-call directory without `..` or `/../`;
the per-call directory is 0700, the file 0600, the directory is the CLI's working directory and is removed; the CLI
sees only `ENV_PASSTHROUGH` and `DOTNET_ROOT` (managed-identity secrets, `MARGO_STATE_PROBES` and
`DOTNET_STARTUP_HOOKS` in the service's environment do not reach it), and the account and state directory come
first from the binding; the model client does not send the Entra token to another origin when it follows a 302 or
307 redirect (.NET drops `Authorization` on redirects).

**Test defects fixed** (the evidence for each is stated with it):

1. `ParityTests.PendingCliScripts` still listed `task_state.py` after it landed in the CLI, so a CLI that lost it
   would have made two parity tests skip instead of fail (found by reading). It now lists only `memory_state.py`,
   and a script listed as pending that the CLI implements fails the test.
2. "Implemented by the C# CLI" meant only that the subcommand's name appeared in `margo-state SCRIPT --help`. A
   subcommand with missing or different options passed. Both CLI parity tests now require each command's
   `margo-state SCRIPT COMMAND --help` (exit status, stdout and stderr) to equal `python3 SCRIPT COMMAND --help`, for
   every classified or listed subcommand of every implemented script and for the stateless helpers. A mutated
   Python help for `task_state.py create` was caught by both tests.
3. The scan of commands the skills show missed every command shown without its script name (`create KEY --input
   FILE`, `start`, `charge`, `finish` in task-runs.md; the `dream-*` commands; 63 commands in all) and ignored the
   options shown with them. It now also takes bare subcommands in inline code of a file that names the script, keeps
   the options a procedure shows, and checks each is accepted by the C# subcommand. Every one of the 63 is
   classified, and those of `task_state.py` and `proactive_state.py` are implemented with Python's parser today, so
   this widens what is checked; it did not expose a broken command.

No defect was found in `Margo.Cloud.Engine`'s sources, so none were changed.

## Tests and evidence

`cloud/tests/Margo.Cloud.Engine.Tests` (55 tests): a fake model, a recording Work IQ transport behind the real
`WorkIQGuard`, real temporary state through the real `margo-state` CLI (the test project references
`Margo.Cloud.StateCli`, so `margo-state.dll` sits next to the tests and is the default command), and live
comparisons with the Python modules through `Parity/engine_cases.py` (`python3 -B -I`, no network, no state).

- `EngineTests`: every `EngineTests` test of `tests/test_autopilot_runtime.py` (guard path and feedback, read tools
  only, third-party tools, refused writes and bad arguments, bounded rounds, large results), plus the request shape,
  odd model responses, office and state dispatch by mode, and 27 dispatch cases compared with Python's `_dispatch`.
- `SkillLibraryTests`: `test_skill_files_are_served_unchanged_and_confined`, `normpath`, 34 paths and 13 front
  matter texts compared with Python, a fictional skill tree (hidden entries, CRLF, symbolic links, excluded
  directories, astral characters at the size limit, private preferences, the ledger-only file) listed, loaded and
  read by both, and an undecodable file.
- `StateCommandTests`: `test_the_same_commands_run_against_the_instance_state` (work, proactive and task state through
  the C# CLI, read back through `InstanceLedger.OpenLedger`; an uninitialised task read is an `error`),
  `test_mode_policy_and_unhosted_commands_run_nothing`, what the CLI process sees (private 0700 directory as working
  directory, 0600 file, binding arguments first, only the passed-through environment),
  `test_outcomes_are_reported_truthfully` (a fake CLI: refusal exit 2, crash after a possible commit, timed-out write
  and read, `{"error"}` on stdout, stderr JSON, plain text, oversized output, the private file directory),
  `test_budget_is_charged_before_running_and_receipts_hold_no_content`, cancellation, programs that cannot start,
  `InitialiseAsync` failure, and 64 argument sets in 5 modes (320 cases) compared with Python's `run` result and
  receipts, plus the tables.
- `ParityTests`: every test of `tests/test_autopilot_parity.py` (persona and skills word for word, every skill file
  reachable, every script, subcommand, program and Work IQ tool a procedure names hosted or listed, the model told
  what is missing; the Dockerfile check as described above), plus: every subcommand classified against the Python
  scripts' argparse parsers; every hosted command implemented by the C# CLI (`margo-state SCRIPT --help` choices
  equal the classified set, and every subcommand's `--help` equals Python's); every state command the skills show,
  after its script or bare in a file about it, classified and implemented (listed by the CLI, the same parser as
  Python's, and taking every option shown with it); tools, instructions (three time zones), summary, lists and
  `_clip` identical to Python. Each distinct CLI or Python `--help` runs once per test run (four at a time), and the
  test classes run one at a time (`xunit.runner.json`), to keep the load these process-starting tests add to a full
  solution run small.
- `GeneratedCaseTests` (fixed seeds; `MARGO_ENGINE_SEED` tries others): 3,000 generated `margo-state` calls, 200
  generated command outcomes from a fake CLI (both sides run it), 3,000 generated function calls, 800 generated
  turns and 16,000 generated skill paths and front matter texts, each compared with the Python modules through
  `engine_cases.py` (which gained `runs` and `state_exec` for this). The review ran the same generators at larger
  counts and other seeds (numbers above).
- `ModelClientTests`: URL, bearer token and scope, body, token caching and refresh, retries and their delays, retry
  headers, error messages, connection failures and timeouts, cancellation, an engine turn over the HTTP client, and
  302/307 redirects through the real handler (followed, without the token).

Skipped, honestly (they skip, never pass, while the condition holds; they run in full once it does):
`Initialise_prepares_memory_and_task_state`, `Every_hosted_command_is_implemented_by_the_cli` and
`Every_state_command_a_procedure_shows_is_classified_and_implemented` (request 1; their other checks run first and
must pass, `task_state.py` and `work_state.py`/`proactive_state.py` included), and
`The_image_carries_the_persona_and_every_skill` (no C# image yet). Any hosted script missing from the CLI other than
`memory_state.py` fails.

Verification at the review commit: `dotnet build cloud/Margo.Cloud.slnx` 0 warnings; `dotnet test --solution
Margo.Cloud.slnx` 5156 total, 5151 passed, 5 skipped (the four above and State.Tests' in-suite parity judge, which
runs only on request), 0 failed (the WorkIQ timing test of request 3 passed in this run; it remains timing-sensitive);
this project alone 55 total, 51 passed, 4 skipped (rerun after the last edit, a skip message);
`python3 tools/state_parity.py --check` 182 scenarios passed; `python3 -m unittest discover -s tests -p 'test_*.py'`
520 OK (4 skipped); `./tools/check-clean.sh` clean.

Not verified: anything against a real Azure OpenAI endpoint, Entra or Work IQ; model behaviour (these are contract
tests, not model evaluations).

## Open questions

1. Python's `statecmd.run` documents "never raises" but raises on the values in deviation 6; the port keeps that.
   Should both catch them as refusals?
2. `EngineResult.ResponseId` drops a non-string `id` (deviation 7). The orchestrator port stores it per conversation;
   a string is all the Responses API returns.

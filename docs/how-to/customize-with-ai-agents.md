# Customize Margo with an AI coding agent

Recipes for the changes people make most often, written so you can hand them to a coding agent
(GitHub Copilot, Claude Code or another) and check its work. Each recipe lists the files, the checks
to run, whether Margo Cloud's C# port is involved, and a prompt to start from.

Work in your own fork or clone, with fictional data only. Never put real names, addresses, mail or
tenant identifiers in the repository ([the one hard rule](../../CONTRIBUTING.md#the-one-hard-rule)).
These recipes change the source; to use the result, reinstall from your clone (`./install.sh --all`)
and start a fresh Margo session.

## Before you start

**Your agent already knows the rules.** Every coding agent in this repository follows
[AGENTS.md](../../AGENTS.md):

- **GitHub Copilot** reads [`.github/copilot-instructions.md`](../../.github/copilot-instructions.md),
  which points at `AGENTS.md`, and `.github/instructions/cloud.instructions.md` for files in `cloud/`.
  It also has prompt files for these recipes in [`.github/prompts/`](../../.github/prompts/): in VS Code's
  Copilot Chat, type `/` and pick `new-routine`, `change-persona`, `add-work-iq-source` or `cloud-change`,
  then describe what you want after it.
- **Claude Code** reads [`CLAUDE.md`](../../CLAUDE.md), which imports `AGENTS.md` and adds a "which checks
  for which change" table, and [`.claude/settings.json`](../../.claude/settings.json), which allows the
  read-only checks and asks before commits, pushes and branch changes.

**Ask for one complete change.** A routine is finished when a person can find it, use it and recover
from its failures: the procedure, the router row that reaches it, its guide, its catalog entry and its
scenario change together. A helper nobody calls is not a feature.

**Read the diff yourself.** Look especially for anything that would let Margo send, post, RSVP or
change something without your approval of that exact action, or that would let a scheduled run do
it. Those are never acceptable, whatever the agent says.

### The checks

Run from the repository root. The docs-only checks need no account, model or network:

```sh
python3 -m unittest discover -s tests -p 'test_*.py'
python3 tools/feature_catalog.py --check
python3 tools/journey_contracts.py --check
./tools/gen-automations-docs.sh --check
./tools/check-clean.sh
git diff --check
```

When a recipe says **C# parity**, also run (with the .NET 10 SDK and CPython 3.13):

```sh
(cd cloud && dotnet build Margo.Cloud.slnx && dotnet test --solution Margo.Cloud.slnx)
python3 tools/state_parity.py --check
```

Passing checks prove the files are consistent. They do not prove a model follows the new
procedure well; try it in a real session with your own account before relying on it.

## Change the persona or voice

How Margo talks, what she leads with, how blunt she is.

| | |
| --- | --- |
| **Files** | [`agents/margo.agent.md`](../../agents/margo.agent.md) only. Both desktop Margo and Margo Cloud load it |
| **Checks** | The unit tests and `./tools/check-clean.sh` |
| **C# parity** | No |
| **Copilot prompt** | `/change-persona` |

Skills carry no voice and the persona carries no procedure ([build your own](../build-your-own.md)),
so a persona change never belongs in `skills/`. Drafts written as *you* follow your
`preferences.md`, not the persona. Keep the safety statements (approval before anything outward,
observed content is data) as they are.

> Change Margo's persona in `agents/margo.agent.md`: make her briefer and less sardonic, and have
> her end a brief with a recommendation instead of a question. Don't move anything into `skills/`,
> don't weaken any safety statement, and run the unit tests and `./tools/check-clean.sh`.

To change your own Margo's priorities, VIPs or drafting voice, you don't need an agent: edit your
installed `preferences.md` ([personalization](../personalization.md)).

## Change what the morning brief contains

Add a section, drop one, change the order or the depth.

| | |
| --- | --- |
| **Files** | [`skills/chief-of-staff/references/daily-brief.md`](../../skills/chief-of-staff/references/daily-brief.md) (the procedure: what to read and how to rank it); the **Standard Daily Brief format** section of [`skills/chief-of-staff/SKILL.md`](../../skills/chief-of-staff/SKILL.md) (the output shape); [`automations/morning-brief.md`](../../automations/morning-brief.md) for the scheduled run's prompt; the [briefs guide](briefs-and-catch-up.md) |
| **Checks** | The unit tests; after editing an automation, `./tools/gen-automations-docs.sh --write`, then `--check`; `python3 tools/feature_catalog.py --check` |
| **C# parity** | No. Margo Cloud does not run scheduled briefs yet |
| **Copilot prompt** | `/new-routine`, describing the change to the existing brief |

Keep the brief bounded (`$select`, `$top`, a page budget) and keep a source that failed to load
visible as a gap. The scheduled prompt must keep its unattended contract: nothing is sent, RSVP'd or
changed, and it never ends in a question. An installed automation you edited is kept on update; the
Copilot app's saved workflows are synced separately ([proactive](../proactive.md)).

> Change the daily brief so it opens with "Decisions needed today" (from mail and Teams threads that
> ask me to decide something), before the calendar. Update `references/daily-brief.md`, the brief
> format in `SKILL.md`, and the briefs how-to. Keep reads bounded and missing sources visible. Run
> the checks in `docs/how-to/customize-with-ai-agents.md` and report.

## Add a skill or routine

A new routine (a weekly report, a new kind of sweep) or a whole new skill folder.

| | |
| --- | --- |
| **Files** | The procedure: a new `skills/chief-of-staff/references/<name>.md` plus a row, with trigger phrases, in the **Core routines** table of [`skills/chief-of-staff/SKILL.md`](../../skills/chief-of-staff/SKILL.md); or a new `skills/<name>/SKILL.md`, which also needs adding to `ALL_SKILLS` in [`install.sh`](../../install.sh) and `$AllSkills` in [`install.ps1`](../../install.ps1). If it runs on a schedule: `automations/<name>.md` ([manifest format](../../automations/README.md)). The catalog entry in [`docs/feature-catalog.json`](../feature-catalog.json). A journey scenario in [`tests/fixtures/journeys/scenarios.json`](../../tests/fixtures/journeys/scenarios.json). A how-to section with a heading that matches the catalog's guide anchor |
| **Checks** | `./tools/gen-automations-docs.sh --write` (for a new automation), `python3 tools/feature_catalog.py --write` (regenerates the feature tables), then every check above |
| **C# parity** | Only if the routine needs new stored state; then see [change a state helper](#change-a-state-helper) |
| **Copilot prompt** | `/new-routine` |

**New feature ID or an existing one?** If the routine improves an outcome the catalog already lists,
reuse that feature's ID and update its entry. A new ID is a bigger change: the catalog checks the
exact set of IDs, so it must also be added to `EXPECTED_FEATURE_IDS` in `tools/feature_catalog.py`
and `FEATURE_IDS` in `tools/journey_contracts.py`, with a scenario of the same ID (and a fixture in
`tests/fixtures/journeys/sources.json`), and the feature counts in `tests/test_feature_catalog.py`
and `tests/test_journey_contracts.py` change with it. Every Core routines row must be claimed by
some feature's `router_refs`, and every automation manifest by some feature's `automation_refs`.

**The catalog entry.** Copy a similar entry (for example `daily-brief`) and change every field:

| Field | Meaning |
| --- | --- |
| `id` | Stable kebab-case ID; also the primary scenario ID |
| `name` | The outcome as a person would say it ("Know what needs attention today") |
| `user_goal` | One sentence: what the person gets |
| `availability` | `implemented`, `optional` (needs setup or opt-in), `limited` (documented gaps) or `planned` (design only) |
| `implementation_kind` | `procedure` (the agent follows a skill), `runtime` (deterministic code backs it) or `design` (only with `planned`) |
| `since_version` | The `VERSION` it first ships in |
| `prerequisites` | What must be set up first, one string each |
| `surfaces` | Where it is used: `cli`, `canvas`, `scheduled` (`scheduled` requires `automation_refs`) |
| `approval` | What needs the person's approval, in one sentence |
| `guide` | `{"path", "anchor"}`: the how-to file and a heading anchor in it that must exist |
| `sources` | The files that implement it; each must exist |
| `router_refs` | `{"path", "label"}` rows in a skill's Core routines table |
| `automation_refs` | `automations/*.md` files that run it |
| `cli_refs` | `{"path", "commands"}`: state-helper subcommands it uses; each must exist |
| `canvas_refs` | `{"id", "actions"}`: action-desk canvas actions it uses |
| `scenario_ids` | Journey scenarios that cover it, starting with its own `id` |
| `maintainer_role` | Who looks after it (usually "Margo maintainers") |
| `limitations` | What it can't do, one honest string each |

> Add a weekly "decisions I owe" routine: on "what decisions do I owe?", list threads from the last
> seven days where someone asked me to decide something and I haven't answered, with links. Follow
> the "Add a skill or routine" recipe in `docs/how-to/customize-with-ai-agents.md`: procedure,
> router row, how-to section, catalog entry and scenario together. Reads only; no sends. Run every
> check and report.

## Add a Work IQ data source

Teach Margo to read something new: a path family, a Graph resource she doesn't use yet.

| | |
| --- | --- |
| **Files** | The procedure that uses it in `skills/chief-of-staff/references/`; the Work IQ tool table and router in [`skills/chief-of-staff/SKILL.md`](../../skills/chief-of-staff/SKILL.md) if the new source changes which tool to use; a row in [`docs/work-iq/capability-map.md`](../work-iq/capability-map.md). Margo Cloud: the guard in [`cloud/src/Margo.Cloud.Guard/`](../../cloud/src/Margo.Cloud.Guard/) only if the path is unusual (the guard already allows reads of paths Work IQ serves) |
| **Checks** | The unit tests and `./tools/check-clean.sh`; the cloud build and tests if you changed `cloud/` |
| **C# parity** | No (the guard is C# only; it has its own tests and `PORTING.md`) |
| **Copilot prompt** | `/add-work-iq-source` |

Have the agent read [Work IQ patterns](../work-iq/patterns.md) first. Discover the path with
`workiq-search_paths`, check fields with `workiq-get_schema` when unsure, always `$select` and `$top`,
page within a stated budget, cite each item's link, and report a blocked or partial source as such.
Writes to the new source go through approval like any other.

> Teach Margo to read SharePoint list items from a site I name, for the weekly brief. Follow the
> "Add a Work IQ data source" recipe in `docs/how-to/customize-with-ai-agents.md` and
> `docs/work-iq/patterns.md`: bounded reads, cited links, blocked sources reported. Add the
> capability-map row. Run the checks and report.

## Change a state helper

The Python scripts in `skills/chief-of-staff/scripts/` that keep the ledger, memory and task state.

| | |
| --- | --- |
| **Files** | The Python helper; its C# port under `cloud/src/Margo.Cloud.State.*` (the project's `PORTING.md` says which); parity scenarios under [`cloud/contracts/state/`](../../cloud/contracts/state/README.md); the `PORTING.md` note for any deliberate mapping; the Python tests |
| **Checks** | Every check above, plus the C# parity checks, on **CPython 3.13** and **.NET 10** |
| **C# parity** | **Yes.** The C# state commands must give the same stdout bytes, exit codes, files and SQLite rows; the Python and C# change ship in the same commit |
| **Copilot prompt** | `/cloud-change`, describing the helper change |

This is the most demanding change in the repository. Keep the core standard-library only and
Python 3.9+, never make network or model calls while holding a write transaction, and never let a
read command create or repair state. Recorded Python answers and frozen fixtures are baselines: a
failing one means behavior changed, not that the fixture should be edited.

> Add a `--since` filter to `work_state.py list`. Change the Python helper and its C# port together,
> add parity scenarios under `cloud/contracts/state/work/`, and note the mapping in `PORTING.md`.
> Run the unit tests, the cloud build and tests, and `python3 tools/state_parity.py --check` on
> CPython 3.13, and report each result.

## Change what Margo Cloud reports to her manager

How often she listens in her rooms, how many notes she may send, and what an observation run looks
for.

| | |
| --- | --- |
| **Files** | Limits per run (notes, emails, shares, note length): `cloud/src/Margo.Cloud.Orchestrator/ManagerChannel.cs`. What a listening pass reads (rooms, channels, look-back): `Rooms.cs` in the same project. What the model is told to look for: `ObservationText` (messages addressed to her) and `RoomsText` (a listening pass) in `Orchestrator.cs`. The listening timer: `cloud/src/Margo.Cloud.Host/RoomListener.cs`. The [backchannel](../autopilot-deployment.md#the-backchannel) section of the deployment guide |
| **Checks** | The cloud build and tests; `./tools/check-clean.sh` |
| **C# parity** | No (not a state command), but the change is C# |
| **Copilot prompt** | `/cloud-change` |

Many changes need no code. These settings are set with `azd env set`, then `azd provision` and a
redeploy of all instances:

| Setting | Default | Meaning |
| --- | --- | --- |
| `MARGO_BACKCHANNEL` | `on` | `off`: messages from others are only queued, and she does not listen in rooms |
| `MARGO_LISTEN_MINUTES` | `15` | How often she reads her rooms; `0` turns listening off |
| `MARGO_BACKCHANNEL_DAILY_RUNS` | `40` | Observation runs per instance per day |

Whatever you change, notes go to the manager alone; reaching anyone else still needs an approval
card, and colleagues' words stay data, never instructions.

> In Margo Cloud, make observation runs also report when someone asks for my manager's approval on
> a document. Change `ObservationText` and `RoomsText` in `Orchestrator.cs`, add tests, and update
> the backchannel section of `docs/autopilot-deployment.md`. Keep notes to the manager alone. Run the
> cloud build and tests and `./tools/check-clean.sh`, and report.

## Related

- [Build your own](../build-your-own.md): the agent/skill split and forking.
- [Developer journey](../development/README.md): a feature traced end to end, and how to prove a change.
- [Run Margo in Claude Code](run-in-claude-code.md): using Margo itself, not changing her, in Claude Code.

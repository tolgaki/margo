# Margo: instructions for Claude Code

@AGENTS.md

AGENTS.md above is the rulebook for every coding agent in this repository; follow it exactly. This file adds only
what Claude Code needs on top.

## Which checks for which change

Run the smallest set that covers what you changed, then report the results honestly (a check you did not run is not a
pass).

| You changed | Run |
| --- | --- |
| Docs only (`*.md`) | `python3 tools/feature_catalog.py --check`, `python3 tools/journey_contracts.py --check`, `./tools/check-clean.sh` |
| The persona (`agents/`) or a procedure (`skills/**/SKILL.md`, `skills/**/references/*.md`) | `python3 -m unittest discover -s tests -p 'test_*.py'` and the docs checks |
| An automation prompt (`automations/*.md`) | `./tools/gen-automations-docs.sh --write`, then `--check` and the tests |
| The feature catalog (`docs/feature-catalog.json`) | `python3 tools/feature_catalog.py --write`, then `--check` |
| A desktop state helper (`skills/chief-of-staff/scripts/*.py`) | The tests, then the matching C# change in `cloud/` and `python3 tools/state_parity.py --check` (CPython 3.13, .NET 10) |
| Anything in `cloud/` | `(cd cloud && dotnet build Margo.Cloud.slnx && dotnet test --solution Margo.Cloud.slnx)`; parity too if you touched state |
| The action desk (`.github/extensions/margo-action-desk/`) | `node --test .github/extensions/margo-action-desk/*.test.mjs` |
| The installers | `bash -n install.sh` and a dry run: `./install.sh --all --dry-run --dest "$(mktemp -d)"` |

Before handing off, always: `./tools/check-clean.sh` and `git diff --check`.

## Traps to avoid

- Never edit a frozen fixture or recorded baseline to make a test pass (`cloud/tests/**/Parity/`, `cloud/contracts/`).
  A failing baseline means your change altered behavior; say so and ask.
- A change to a desktop state helper is never Python-only: the C# state commands must stay byte-identical.
- Use fictional data only (`example.com`, the cast in `CONTRIBUTING.md`); `check-clean.sh` rejects real-looking
  names, addresses and paths.
- Do not commit, push or switch branches unless the user asked for it (the project settings ask first).

## Customizing Margo

Recipes for the common changes (persona, the morning brief, a new routine, a Work IQ source, Margo Cloud's
backchannel): [docs/how-to/customize-with-ai-agents.md](docs/how-to/customize-with-ai-agents.md). Running Margo
itself in Claude Code: [docs/how-to/run-in-claude-code.md](docs/how-to/run-in-claude-code.md).

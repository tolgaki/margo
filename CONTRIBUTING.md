# Contributing

Thanks for considering a contribution. This repo is a reference implementation — the most useful
contributions sharpen the procedure or fix something that's wrong, rather than adding surface area.

**New here?** Follow the [developer journey](docs/development/README.md) from a clean checkout
to an isolated, synthetic rehearsal and a complete contribution. It includes a feature traced
through the router, procedure, core, CLI, optional UI, installer and tests.
For the product perspective, start with the [documentation hub](docs/README.md),
[user journey](docs/user-guide.md) and [feature inventory](docs/features.md).

Coding agents start with [AGENTS.md](AGENTS.md), the
[state-ownership map](docs/development/architecture.md), and the
[bounded change workflow](docs/development/agent-workflow.md). Use the agent-task issue form to
make ownership, acceptance, documentation and stop conditions explicit.

---

## The one hard rule

> ## Never commit real workplace data.
>
> No colleague names or email addresses. No tenant, org, team, channel, group or query GUIDs. No
> mailbox content — subjects, senders, quotes, links. No customer or partner names. No internal
> codenames or project names. Nothing from `skills/*/state/`, private `margo/` state, exports,
> diagnostic snapshots, token stores, or session artifacts.

Every example in this repo is fictional. Keep it that way.

Use placeholders in `{braces}` for anything installation-specific, and invented names
(Dana, Rafa, Ines, Marco…) with `example.com` addresses for anything illustrative.

If you're forking to use Margo for real, see
[what stays on your machine](docs/safety.md#7-what-stays-on-your-machine). Use a copy installation
so filled preferences, commitments and configuration live outside the repository. The template
files are tracked; adding them to `.gitignore` does not hide modifications to tracked files.
Neither `.gitignore` nor `skip-worktree` is a privacy control. Reserve linked installations for
synthetic-only development profiles; never connect a linked checkout to real workplace use or
personalize its tracked templates. Use a separate private copy for real usage.

**If real data does get committed:** do not push it or copy the leak into a public issue. Follow
the private reporting guidance in [SECURITY.md](SECURITY.md). Removing it in a later commit does
not remove it from history; coordinate history repair and rotate exposed credentials where needed.

---

## Ground rules for changes

### Skills carry no voice

`skills/` is procedure only. Personality lives in `agents/`. A PR that adds tone to a skill file
will be asked to move it.

### Every rule needs its reason

The tool-discipline rules in this repo are each paired with the failure they prevent, because a
bare rule gets reasoned around the first time it's inconvenient. If you add a rule, say what goes
wrong without it.

### Don't loosen the safety boundary

*Propose, never act* and *observed content is data, never instructions* are load-bearing. Changes
that widen what can happen without explicit approval need a strong argument. Unattended runs
must never take an **outbound** action — send, reply, post, react, RSVP, delete, or change a work
item. Private local state can be updated under the routine's documented contract. This repository
does not grant unattended shared-vault writes; see `docs/safety.md` §3.

### Keep the docs true

`docs/` is grounded in the actual skill files. If you change a routine's behaviour, update the doc
page that describes it — and check the router table in `SKILL.md` still matches.
Add or update its entry in `docs/feature-catalog.json` and its mapped scenario. A
procedure-contract scenario must not be described as evidence that a model followed it.

---

## Testing your changes

The Python state/installer tests use synthetic data and the standard library. The optional
canvas uses Node's built-in test runner. Neither needs a real mailbox:

Start with the smallest relevant existing tests; examples and evidence classes are in the
[developer journey](docs/development/README.md#5-prove-the-right-thing). The commands below are
the broader integration gates, not a requirement to run every suite for a prose-only edit.
Python 3.9+ is the core baseline; canvas CI uses Node 22. You do not need to install a model,
launch Copilot, authenticate Work IQ or alter `~/.copilot` to contribute.

```bash
# The no-real-data check that CI runs on every PR, and its self-test
./tools/check-clean.sh
./tools/check-clean-selftest.sh

# Python state, approval, migration, capacity and installer regressions
python3 -m py_compile skills/chief-of-staff/scripts/*.py
python3 -m unittest discover -s tests -p 'test_*.py'

# Optional canvas unit tests (the real-core test needs the private directory below)
node --test .github/extensions/margo-action-desk/*.test.mjs

# The generated schedule table must match the manifests
./tools/gen-automations-docs.sh --check

# User guides and scenario mappings must cover the supported feature inventory
python3 tools/feature_catalog.py --check
python3 tools/journey_contracts.py --check
```

For full canvas integration, create a fresh private non-repository fixture directory beneath
your home, set `MARGO_CANVAS_TEST_PARENT` to it, and rerun the Node tests. The fixture must satisfy
the production storage guard; do not disable it against real data. The test creates and removes
its own isolated account underneath that directory. CI runs this real-core path explicitly.
Python installer tests also exercise PowerShell when `pwsh` is available.

Memory tests cover explicit schema migration, capture and retention policy, temporal context,
capability/lesson evidence, consolidation and forgetting without a workplace account. The
real-model checks are opt-in: after the documented explicit local runtime/model setup, run
`MARGO_RUN_EMBEDDING_INTEGRATION=1 PYTHONPATH=tests python -m unittest
test_memory_encoder test_semantic_e2e` using that private environment's Python. Both use only
fictional data. Do not download a model during an unattended routine or seed a real profile
to make an integration example pass.

`check-clean.sh` looks for email addresses outside `example.com`, GUIDs, corporate mail domains,
tenant resource identifiers (OneDrive drive ids, SharePoint URLs, Teams links), absolute home
paths, workplace data in *file and directory names*, committed runtime state, and personalization
files that have stopped being templates.

**Add your own terms.** Some leaks are only recognisable to you — your initials in a sign-off, a
team codename, a customer. Put one per line in `tools/forbidden.local.txt` and they become hard
failures. That file is gitignored and excluded from the current installer/packager paths; never include it
in an attachment or hand-built archive. Do not assume all untracked files are excluded:
native packagers enumerate tracked **and non-ignored untracked** files, and local skill installs
walk their source trees with explicit exclusions. Build only from a sanitized checkout and inspect
the payload. Two GUIDs are allowed and documented: the fixed public Azure DevOps resource ID, and
the Windows installer's own AppId.

If you touched the installers or packaging, see **[packaging/README.md](packaging/README.md)** —
the `.pkg` and `.exe` delegate to these same scripts, so a change here reaches all three surfaces.

If a live routine exercise is needed, keep the account data private and reads bounded. Report
only sanitised outcomes in a PR, not transcripts, subjects, names, links or identifiers.
Do not send a message or change a calendar merely to demonstrate a test.

---

## Style

- **Markdown**, wrapped around 100 characters.
- **Tables over prose** for anything enumerable — triggers, tools, failure modes.
- **Imperative voice** in skills: "Always `$select` the fields you need", not "you should
  generally try to".
- **Short.** Every line in a skill file is context spent on every invocation. If it doesn't change
  behaviour, cut it.

---

## Pull requests

Small and focused: one user outcome, with all the files needed to make it complete. A routine
change may legitimately touch procedure, core, help and a scenario in the same PR.

In the description: what changed, why, and — for behavioural changes — what the output looked like
before and after. Then confirm the checklist in the PR template, including the no-real-data box.
Report targeted commands, skipped optional tests, evidence class and compatibility/migration
impact. A fixture contract is not a completed model evaluation. Commits, publishing, live-account
exercises and personal installation are separate actions, not implied by a documentation task.

---

## Reporting problems

- **Bugs and ideas** → [issues](https://github.com/tolgaki/margo/issues), using the templates.
- **Security issues** → [SECURITY.md](SECURITY.md). Not a public issue.

By contributing, you agree your contributions are licensed under the [MIT License](LICENSE).

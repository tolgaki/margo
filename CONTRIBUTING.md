# Contributing

Thanks for considering a contribution. This repo is a reference implementation — the most useful
contributions sharpen the procedure or fix something that's wrong, rather than adding surface area.

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

---

## Testing your changes

The Python state/installer tests use synthetic data and the standard library. The optional
canvas uses Node's built-in test runner. Neither needs a real mailbox:

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
```

For full canvas integration, create a fresh private non-repository fixture directory beneath
your home, set `MARGO_CANVAS_TEST_PARENT` to it, and rerun the Node tests. The fixture must satisfy
the production storage guard; do not disable it against real data. The test creates and removes
its own isolated account underneath that directory. CI runs this real-core path explicitly.
Python installer tests also exercise PowerShell when `pwsh` is available.

`check-clean.sh` looks for email addresses outside `example.com`, GUIDs, corporate mail domains,
tenant resource identifiers (OneDrive drive ids, SharePoint URLs, Teams links), absolute home
paths, workplace data in *file and directory names*, committed runtime state, and personalization
files that have stopped being templates.

**Add your own terms.** Some leaks are only recognisable to you — your initials in a sign-off, a
team codename, a customer. Put one per line in `tools/forbidden.local.txt` and they become hard
failures. That file is gitignored and never published; it is also excluded from installer
payloads, which are built from `git ls-files` rather than from the working tree. Two GUIDs are allowed and documented: the fixed public Azure DevOps resource ID, and
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

Small and focused. One routine, one fix, one doc page.

In the description: what changed, why, and — for behavioural changes — what the output looked like
before and after. Then confirm the checklist in the PR template, including the no-real-data box.

---

## Reporting problems

- **Bugs and ideas** → [issues](https://github.com/tolgaki/margo/issues), using the templates.
- **Security issues** → [SECURITY.md](SECURITY.md). Not a public issue.

By contributing, you agree your contributions are licensed under the [MIT License](LICENSE).

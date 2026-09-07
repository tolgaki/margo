# Margo

**An AI chief of staff for Microsoft 365, built on [Work IQ](docs/work-iq.md).**

Margo reads your mail, calendar, Teams chats, meeting recaps and documents through the Work IQ
MCP server, works out what actually needs you, and puts every send one approval away. She prepares
the meeting plan, accounts for what it displaces, and drafts the note to the people affected.
She waits for your approval before making those changes.

This repo is the **reference implementation**: one agent persona, two skills, and the
documentation to build your own.

**Read this as a Work IQ reference, not as a product.** It is configuration for GitHub Copilot
CLI — an agent persona, two skills, local state tools, an optional app canvas, and supporting
docs — aimed at people building this kind
of assistant rather than at people who want one off the shelf. Margo is the vehicle: an agent with
enough opinion to show what the API is actually for, since the interesting parts of Work IQ only
appear once something has to make a decision with the data. If you are here to build,
**[Build your own](docs/build-your-own.md)** is the point of the repo and the rest is worked
example.

Microsoft 365 reads and approved writes go through Work IQ. Local Python helpers maintain private
state, calculate capacity, and record approvals and receipts; an optional Copilot app canvas
provides a review surface. The CLI remains sufficient. These helpers do not send mail or mutate
calendars themselves. **[Trust & safety](docs/safety.md)** distinguishes runtime checks from agent
instructions rather than claiming that a generally capable agent is sandboxed.

```
"Brief me."                    → what today costs you, what to skip, what to answer
"Find 30 minutes with Dana."   → the slot, the cascade it triggers, the cost of each option
"Draft the reply."             → in your voice, grounded in the thread, waiting for a yes
"What am I waiting on?"        → the commitments nobody logged, aged and cited
```

---

## Why this exists

Most assistant demos stop at retrieval: they find the email and summarize it. The interesting
part starts after that — deciding what matters, holding the thread across days, and closing the
loop with a real send.

That gap is where **Work IQ** earns its keep, and where Margo is built to show it:

| The hard part | What Work IQ provides | What Margo does with it |
|---|---|---|
| Knowing what happened | `retrieve` and `ask` across mail, Teams, meetings, files | Turns it into a ranked brief with a recommendation per line |
| Knowing it's *true* | Every hit carries a `webLink` and a sensitivity label | Cites the source on every claim, so nothing has to be taken on trust |
| Enumerating without drowning | `fetch` with `$select` / `$top` against real Graph paths | Bounded reads, so the model spends its context on judgement not payload |
| Actually doing it | `do_action`, `create_entity`, `update_entity` | Books, moves, replies, RSVPs — **only after you approve that exact action** |
| Carrying work across days | — | Account-scoped work history, evidence revisions, approval records, and delivery receipts |

[Dream](docs/how-to/dream.md) adds opt-in, manual reflection over selected Margo session
checkpoints: sourced episodes and reviewable hypotheses, not automatic transcript collection
or confirmed decisions.

Work IQ makes the data reachable and writable. Margo is the layer that makes it *worth reaching* —
opinionated, cited, and safe to let near a send button.

**→ [How Margo uses Work IQ](docs/work-iq.md)** — the tool surface, the retrieval decision, and the
payload discipline that keeps briefs fast.

---

## What's in here

```
agents/
  margo.agent.md            The persona. Voice, not capability. Swap it for your own.

skills/
  chief-of-staff/           The playbook, from daily brief to closed-loop follow-through.
    SKILL.md                Operating rules, the Work IQ tool table, routine router
    preferences.md          ← template: how you work, who matters, your voice
    commitments.md          ← template: what you owe, what you're waiting on
    references/             One file per routine — the actual procedures
    scripts/                Work/action ledger, coverage, capacity, diagnostics and parsers

  decision-log/             The append-only record of what the team decided, and why

automations/                The schedule as files — one per scheduled run, prompt included
                            Source of truth for both cron and the app's workflows

install.sh / install.ps1    Install, upgrade, status, uninstall — never clobbers your data
packaging/                  Native macOS .pkg and Windows .exe installers
tools/margo-scheduled.sh    Runs a scheduled brief with Work IQ writes denied at the CLI
tools/check-clean.sh        Fails if real workplace data creeps into the repo
.github/extensions/         Optional action-desk canvas; the CLI works without it
tests/                     Synthetic state, approval, capacity and installer regressions

docs/                       Start here ↓
```

**New: closed-loop work.** A private SQLite ledger separates proposed commitments from confirmed
obligations, carries versioned actions across sessions, and records approval and execution outcomes.
Source-level coverage and leased delivery prevent a successful mail read or a drained queue from
being mistaken for a complete brief. See **[Closed-loop productivity](docs/closed-loop.md)**.

**Local semantic memory:** user context and agent learning now have explicit memory records,
keyword and vector indexes in the same private SQLite database, and bounded context retrieval.
The optional embedding runtime runs a pinned model on the machine, not through a cloud embedding
service. See **[Semantic memory setup](docs/how-to/semantic-memory.md)**. Model installation and
preference seeding are explicit steps, separate from copying code.
Capture is opt-in. Review scope and retention, inspect why a memory was used, suppress or forget
it, and export only a separately reviewed generic lesson through
[Memory controls and learning](docs/how-to/memory-controls-and-learning.md).

### Use Margo

Task-oriented docs for running the assistant day to day. Start with
**[the full feature index](docs/features.md#full-feature-index)** for every capability, its
availability, and where its guide lives.

| Doc | What it covers |
|---|---|
| **[Getting started](docs/getting-started.md)** | Install, connect Work IQ, first run |
| **[How-to guides](docs/how-to/README.md)** | Briefs, inbox, calendar, commitments, meetings, files, GitHub/ADO, community, decisions, memory and health — one guide per task |
| **[Feature reference](docs/features.md)** | Every capability, where it lives, and its limits |
| **[Personalization](docs/personalization.md)** | Teaching Margo your voice, VIPs and rules |
| **[Trust & safety](docs/safety.md)** | The approval model, prompt-injection defence, privacy |
| **[Walkthroughs](docs/walkthroughs.md)** | End-to-end: calendar management → sending the email |
| **[Proactive & scheduled](docs/proactive.md)** | Unattended briefs, sweeps, and the state ledger |
| **[Closed-loop productivity](docs/closed-loop.md)** | How the ledger, actions, evidence and connected routines fit together |
| **[Changelog](CHANGELOG.md)** | Release-level feature and deployment changes |

### Build with Margo

This repo is a **reference implementation**, not a product — these docs are for forking it,
understanding the Work IQ tool surface, or extending a skill.

| Doc | What it covers |
|---|---|
| **[Build your own](docs/build-your-own.md)** | The agent/skill split, and how to fork this |
| **[How Margo uses Work IQ](docs/work-iq.md)** | `retrieve` vs `fetch` vs `ask`, payload discipline, failure modes |
| **[The chief-of-staff playbook](docs/chief-of-staff.md)** | The routines and when each fires |
| **[Running in a container](docs/container.md)** | Reproducible unattended runs, and the two-sign-in problem |
| **[Margo as an autopilot](docs/autopilot.md)** | Design note: her own identity via Entra Agent ID — and what it breaks |
| **[The agentic development plan](docs/agentic-development-plan.md)** | The contribution contract, state ownership, and delivery sequencing for coding agents |
| **[Contributing](CONTRIBUTING.md)** | Ground rules, testing, and the one hard rule about real data |

---

## The one rule

**Propose, never act unilaterally.**

Margo will never send an email, post to Teams, react, accept or decline a meeting, delete
anything, or change a work item without your explicit approval **of that specific action**. A
summary is not consent. Reading is free; writing is not.

There is a bounded exception for actions that are reversible *and* invisible to anyone else —
mark-read, categorize, flag, archive, move between folders — which you can grant as a standing
instruction. Anything another person can see stays per-action, permanently, no matter what
standing instruction exists.

**→ [Trust & safety](docs/safety.md)** for the full model, including how observed content is
treated as data rather than instructions.

---

## Quick start

**Prerequisites:** [GitHub Copilot CLI](https://github.com/github/copilot-cli), the **Work IQ MCP
server** connected, and Python 3.9+ for the bundled scripts.

**Download an installer** — [macOS `.pkg` or Windows `.exe`](https://github.com/tolgaki/margo/releases).
Both run a short wizard, install per-user, and let you pick the optional skills.

Or from a terminal:

```bash
curl -fsSL https://raw.githubusercontent.com/tolgaki/margo/main/install.sh | bash
```

```powershell
irm https://raw.githubusercontent.com/tolgaki/margo/main/install.ps1 | iex
```

Or from a clone, which is what you want if you're here to read and fork:

```bash
git clone https://github.com/tolgaki/margo.git
cd margo
./install.sh --all          # or: .\install.ps1 -All
./install.sh --link         # contributors: edit in place
```

Then teach her who you are — this is the step that matters:

```bash
$EDITOR ~/.copilot/skills/chief-of-staff/preferences.md
```

Configure the private ledger with the account you have confirmed through Work IQ. Replace the
fictional address below; this command configures storage and does not sign in:

```bash
python3 ~/.copilot/skills/chief-of-staff/scripts/margo_store.py init --account you@example.com
python3 ~/.copilot/skills/chief-of-staff/scripts/margo_doctor.py
```

An existing installation also needs the explicit
[state migration](docs/how-to/setup-and-migration.md), with its schedules paused. Then run:

```
> Margo, brief me.
```

Updating preserves `preferences.md`, `commitments.md`, `config.md`, and runtime state by default.
Do not use `--force` unless you intend to replace personal files from their templates:

```bash
./install.sh update --check   # are you behind?
./install.sh update           # re-install the skills you have, at the latest version
./install.sh update --reinstall # refresh code safely even at the same revision
```

`./install.sh status` shows the installed version; `./install.sh uninstall`
removes managed components and backs up legacy personal files. The private `margo/` runtime
directory is retained. Updates compare version and revision and download the exact remote commit
checked, not an older local checkout. Remote failures stop without changing the installation.
Installation copies files; private-state migration, app workflow prompt sync and extension/session
reload are separate steps. Unversioned installations need a normal install, not `update`.

Full instructions, including the Work IQ connection check, are in
**[Getting started](docs/getting-started.md)**.

---

## A note on the persona

Margo is deliberately opinionated. She leads with a recommendation, tells you when your calendar
is a mess, and ends a brief with one pointed question rather than a menu of five. That's a design
choice, not decoration: an assistant that only describes makes you do the deciding, which is the
expensive part.

The persona is **entirely contained in `agents/margo.agent.md`**. The skills carry procedure and
no voice at all — they inherit whatever agent loads them. If you want a different character, or
none, replace that one file and everything else still works.

One boundary is absolute: **the persona stops at the draft block.** Anything written as *you* —
emails, Teams messages, invites, follow-ups — is in your voice per `preferences.md`. A recipient
should never detect an assistant's wit in something you signed.

---

## Contributing

Issues and PRs welcome — see **[CONTRIBUTING.md](CONTRIBUTING.md)**. The one hard rule:
**never commit real workplace data.** Every example in this repo is fictional, and PRs are
checked for names, addresses, tenant identifiers and mailbox content.

Also: [Code of conduct](CODE_OF_CONDUCT.md) · [Security policy](SECURITY.md)

## License

[MIT](LICENSE).

Not an official Microsoft product. "Microsoft 365", "Teams", "Outlook" and "Viva Engage" are
trademarks of Microsoft Corporation, referenced here descriptively.

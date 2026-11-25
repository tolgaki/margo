# Margo

**A Microsoft Work IQ reference implementation. Margo is the worked example.**

*MARGO: Managing Actions, Relationships, Goals and Obligations. An AI chief of staff for Microsoft 365.*

Margo reads your Microsoft 365 mail, calendar, Teams chats, meeting recaps and documents through
the Work IQ MCP server, works out what actually needs you, and puts every send one approval away.
She prepares the meeting plan, accounts for what it displaces, and drafts the note to the people
affected. She waits for your approval before making those changes.

| | What it is | Status | Start here |
| --- | --- | --- | --- |
| **Margo desktop** | GitHub Copilot CLI on your machine, with Margo's persona, skills and private local state | Stable and supported for individual use (installers: 1.0.0) | [Getting started](docs/getting-started.md) |
| **Margo Cloud** | Self-hosted in your own Azure subscription as an Agent 365 agent with her own identity | Preview, not for production: the core chat-and-read path is verified in one test tenant; approval cards, documents and on-behalf-of access are built but not verified there ([maturity](docs/margo-cloud.md#maturity)) | [Margo Cloud overview](docs/margo-cloud.md) |
| **Learn Work IQ** | How to load Work IQ in a harness or a custom agent, its tools, identity, errors and writes, with Margo's code as the example | Guide | [Learn Work IQ](docs/work-iq/README.md) |

**Needs:** a Microsoft 365 work account with a Microsoft 365 Copilot license and Work IQ enabled in
your tenant (possibly with admin consent). Desktop also needs a GitHub Copilot plan; Margo Cloud
needs an Azure subscription and Agent 365.

Margo is not a service anyone runs for you: you run it on your machine, or deploy it into your own
tenant. Desktop is stable and supported for individual use; new work goes into Margo Cloud. Choose
desktop if you want to try Margo today without deploying anything to your tenant. Both share the persona (`agents/`) and
the skills (`skills/`).

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
| Enumerating without drowning | `fetch` with `$select` / `$top` against real Graph paths | Bounded reads, so the model spends its context on judgment not payload |
| Actually doing it | `do_action`, `create_entity`, `update_entity` | Books, moves, replies, RSVPs — **only after you approve that exact action** |
| Carrying work across days | — | Account-scoped work history, evidence revisions, approval records, and delivery receipts |

Work IQ makes the data reachable and writable. Margo is the layer that makes it *worth reaching* —
opinionated, cited, and safe to let near a send button.

**→ [Learn Work IQ](docs/work-iq/README.md)**: loading the server, identity and consent, the tool
choice and payload discipline that keep briefs fast, errors, and the approval pattern for writes.

---

## Documentation

### Use Margo

Start with the **[user journey](docs/user-guide.md)** for an ordered path from your first brief
to ongoing work. The **[full feature index](docs/features.md#full-feature-index)** lists every
capability, its availability, and where its detailed guide lives.

| Doc | What it covers |
|---|---|
| **[User journey](docs/user-guide.md)** | What to ask, what to expect, what you decide, and how to recover at each stage |
| **[Getting started](docs/getting-started.md)** | Install, connect Work IQ, first run |
| **[How-to guides](docs/how-to/README.md)** | Briefs, inbox, calendar, commitments, meetings, files, GitHub/ADO, community, decisions, memory and health — one guide per task |
| **[Feature reference](docs/features.md)** | Every capability, where it lives, and its limits |
| **[Personalization](docs/personalization.md)** | Teaching Margo your voice, VIPs and rules |
| **[Trust & safety](docs/safety.md)** | The approval model, prompt-injection defense, privacy |
| **[Walkthroughs](docs/walkthroughs.md)** | End-to-end: calendar management → sending the email |
| **[Proactive & scheduled](docs/proactive.md)** | Unattended briefs, sweeps, and the state ledger |
| **[Closed-loop productivity](docs/closed-loop.md)** | How the ledger, actions, evidence and connected routines fit together |
| **[FAQ](docs/faq.md)** | Data, approvals, costs, desktop versus Margo Cloud, uninstalling |
| **[Changelog](CHANGELOG.md)** | Release-level feature and deployment changes |

### Build with Margo

Start with the **[developer journey](docs/development/README.md)**. You can explore and exercise
the synthetic core without connecting a real mailbox or installing into your normal profile.

| Doc | What it covers |
|---|---|
| **[Developer journey](docs/development/README.md)** | Local exploration, extension points, a feature traced end-to-end, and a first contribution |
| **[Architecture and state ownership](docs/development/architecture.md)** | Which layer owns evidence, work, approval, memory, task progress, and the optional UI |
| **[Build your own](docs/build-your-own.md)** | The agent/skill split, and how to fork this |
| **[Learn Work IQ](docs/work-iq/README.md)** | Loading Work IQ, identity and consent, capability map, patterns, errors, writes and approvals |
| **[The chief-of-staff playbook](docs/chief-of-staff.md)** | The routines and when each fires |
| **[Running in a container](docs/container.md)** | Run scheduled briefs in a container (separate Copilot and Work IQ sign-ins) |
| **[Contributing](CONTRIBUTING.md)** | Ground rules, testing, and the one hard rule about real data |

### Margo Cloud

| Doc | What it covers |
|---|---|
| **[Margo Cloud overview](docs/margo-cloud.md)** | What it is, architecture, who sees what, licenses and costs, maturity, glossary |
| **[Deploy Margo Cloud](docs/autopilot-deployment.md)** | Prerequisites, `azd provision`, the blueprint, approval cards, hiring, operation and known limits |
| **[Margo Cloud code](cloud/README.md)** | Build, test, image and project layout (C#, .NET 10) |
| **[Design and decision record](docs/autopilot.md)** | Why Margo Cloud is shaped the way it is: identity, guard, memory, threat model |

---

## The one rule

**Propose, never act unilaterally.**

Margo will never send an email, post to Teams, react, accept or decline a meeting, delete
anything, or change a work item without your explicit approval **of that specific action**. A
summary is not consent. Reading is free; writing is not. (Margo Cloud asks on a card in Teams for each action, and
sends two fixed notices without per-message approval; see [Trust & safety](docs/safety.md#margo-cloud-approval-cards).)

There is a bounded exception for actions that are reversible *and* invisible to anyone else —
mark-read, categorize, flag, archive, move between folders — which you can grant as a standing
instruction. Anything another person can see stays per-action, permanently, no matter what
standing instruction exists.

**→ [Trust & safety](docs/safety.md)** for the full model, including how observed content is
treated as data rather than instructions.

---

## Quick start

This is desktop Margo. For Margo Cloud, start at the [overview](docs/margo-cloud.md).

**What you need:** [GitHub Copilot CLI](https://github.com/github/copilot-cli) and a GitHub Copilot
plan; the Work IQ MCP server registered in it and signed in with your work account
([how](samples/harness/README.md)); Python 3.9+. macOS, Linux and Windows are supported. Each brief
uses your Copilot plan's model requests, and Work IQ usage where your tenant meters it. The full
table is in [getting started](docs/getting-started.md#what-you-need).

> **Installers are 1.0.0.** The packaged installers are release 1.0.0. For the features in the
> [changelog](CHANGELOG.md) after 1.0.0, install from the terminal or a clone, below.

**Download an installer:** [macOS `.pkg` or Windows `.exe`](https://github.com/tolgaki/margo/releases).
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
```

This copies the installation outside the checkout. For synthetic-only contributor setups and
link mode, follow the [developer journey](docs/development/README.md); never personalize tracked
files with real workplace data.

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

`init` answers `"status":"configured"`; the doctor prints a JSON health report whose `status` names
anything still missing, such as unfilled preferences. An existing installation also needs the explicit
[state migration](docs/how-to/setup-and-migration.md), with its schedules paused. Start Margo:

```bash
copilot --agent margo
```

Then ask:

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

## How Margo works

Desktop Margo is three things: a persona, procedures and a private local database.

- **The persona** (`agents/margo.agent.md`) is her voice. The **skills** (`skills/`) are the
  procedures, one file per routine, which any agent can load.
- **Microsoft 365** is reached only through Work IQ, for reads and for approved writes.
- **Margo keeps a private local database** of what you owe and what is waiting on you, so work
  survives between sessions. It separates commitments she *suspects* from the ones you *confirmed*,
  keeps each proposed action with its versions, approvals and results, and records which sources a
  brief actually covered, so a brief that could not read your mail never looks complete. See
  [closed-loop productivity](docs/closed-loop.md).
- **Memory** is optional and stays on your machine: what she has learned about you, with its source,
  which you can inspect, correct or have her forget. Search over it can use a small embedding model
  that runs locally, never a cloud service. See [semantic memory](docs/how-to/semantic-memory.md) and
  [memory controls](docs/how-to/memory-controls-and-learning.md).
- **[Dream](docs/how-to/dream.md)** is opt-in, manual reflection over selected Margo sessions: sourced
  episodes and reviewable ideas, not automatic transcript collection.
- **Optional panels** (the action desk, memory and task progress) appear in the Copilot app
  that hosts Copilot extensions; the CLI does everything without them. The database helpers never send mail or change a calendar.

[Trust and safety](docs/safety.md) says which of these rules are enforced by code and which are
instructions to the model.

### What's in here

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

cloud/                      Margo Cloud (C#, .NET 10): router, instance host, provisioner, infra

docs/                       Start here ↓
```

---

## A note on the persona

Margo is deliberately opinionated. She leads with a recommendation, tells you when your calendar
is a mess, and ends a brief with one pointed question rather than a menu of five. That's a design
choice, not decoration: an assistant that only describes makes you do the deciding, which is the
expensive part.

The persona is **entirely contained in `agents/margo.agent.md`**. The skills carry procedure and
no voice at all — they inherit whatever agent loads them. If you want a different character, or
none, replace that one file and everything else still works.

Select **Margo the agent** with `copilot --agent margo` or the host's agent picker.
The **chief-of-staff skill** loads for matching tasks, not for the name "Margo". A greeting or
coding request does not need the playbook; another agent can use it without becoming Margo.

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

Not an official Microsoft or GitHub product. Microsoft, Microsoft 365, Microsoft 365 Copilot, Work IQ, Agent 365,
Azure, Entra, Teams, Outlook, Viva Engage, GitHub and GitHub Copilot are trademarks of their owners, referenced here
descriptively.

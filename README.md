# Margo

**An AI chief of staff for Microsoft 365, built on [Work IQ](docs/work-iq.md).**

Margo reads your mail, calendar, Teams chats, meeting recaps and documents through the Work IQ
MCP server, works out what actually needs you, and puts every send one approval away. She books
the meeting, moves the four things it displaces, drafts the note to the people affected — and
then waits for you to say yes.

This repo is the **reference implementation**: one agent persona, three skills, and the
documentation to build your own.

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
| Remembering across days | — | A durable commitments ledger and an on-disk state ledger for scheduled runs |

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
  chief-of-staff/           The playbook. 18 routines, from daily brief to exec follow-up.
    SKILL.md                Operating rules, the Work IQ tool table, routine router
    preferences.md          ← template: how you work, who matters, your voice
    commitments.md          ← template: what you owe, what you're waiting on
    references/             One file per routine — the actual procedures
    scripts/                State ledger, large-file bridge, community parsers

  decision-log/             The append-only record of what the team decided, and why
  partner-updates/          A per-partner status board kept current from real sources

install.sh / install.ps1    Install, upgrade, status, uninstall — never clobbers your data
packaging/                  Native macOS .pkg and Windows .exe installers
tools/check-clean.sh        Fails if real workplace data creeps into the repo

docs/                       Start here ↓
```

| Doc | What it covers |
|---|---|
| **[Getting started](docs/getting-started.md)** | Install, connect Work IQ, first run |
| **[How Margo uses Work IQ](docs/work-iq.md)** | `retrieve` vs `fetch` vs `ask`, payload discipline, failure modes |
| **[The chief-of-staff playbook](docs/chief-of-staff.md)** | All 18 routines and when each fires |
| **[Walkthroughs](docs/walkthroughs.md)** | End-to-end: calendar management → sending the email |
| **[Personalization](docs/personalization.md)** | Teaching Margo your voice, VIPs and rules |
| **[Proactive & scheduled](docs/proactive.md)** | Unattended briefs, sweeps, and the state ledger |
| **[Trust & safety](docs/safety.md)** | The approval model, prompt-injection defence, privacy |
| **[Build your own](docs/build-your-own.md)** | The agent/skill split, and how to fork this |
| **[Running in a container](docs/container.md)** | Reproducible unattended runs, and the two-sign-in problem |
| **[Margo as a digital worker](docs/digital-worker.md)** | Design note: her own mailbox via Entra Agent ID — and what it breaks |

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

And run:

```
> Margo, brief me.
```

Updating is a first-class command, and safe — `preferences.md`, `commitments.md`,
`config.md` and saved state are never overwritten:

```bash
./install.sh update --check   # are you behind?
./install.sh update           # re-install the skills you have, at the latest version
```

`./install.sh status` shows the installed version; `./install.sh uninstall`
removes it and backs your files up.

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

# Getting started

## Prerequisites

| | |
|---|---|
| **[GitHub Copilot CLI](https://github.com/github/copilot-cli)** | The host that loads agents and skills |
| **WorkIQ MCP server** | The Microsoft 365 surface. Hosted — no local package needed for tool calls |
| **A Microsoft 365 work account** | With mail, calendar and Teams |
| **Python 3.9+** | For the bundled scripts (state ledger, large-file bridge, community parsers) |

Optional, for specific routines:

| | |
|---|---|
| `gh` CLI | The GitHub review-load routine |
| Azure CLI + `azure-devops` extension | The work-items routine |

---

## 1. Install

### The installers

| | |
|---|---|
| **macOS** | Download `Margo-*.pkg` from [Releases](https://github.com/tolgaki/margo/releases) and open it |
| **Windows** | Download `Margo-*-setup.exe` and run it — per-user, no admin rights needed |

Both show a wizard with checkboxes for the optional skills, and both install into
`~/.copilot` for your account only.

### Or from a terminal

```bash
curl -fsSL https://raw.githubusercontent.com/tolgaki/margo/main/install.sh | bash
```

```powershell
irm https://raw.githubusercontent.com/tolgaki/margo/main/install.ps1 | iex
```

### Or from a clone

```bash
git clone https://github.com/tolgaki/margo.git
cd margo

./install.sh              # agent + chief-of-staff
./install.sh --all        # also decision-log and partner-updates
```

```powershell
.\install.ps1
.\install.ps1 -All
```

Useful flags — `--skills a,b` to pick specific ones, `--dest DIR` for a different
Copilot directory, `--dry-run` to see what would happen, `--help` for the rest.
The PowerShell equivalents are `-Skills`, `-Dest`, `-DryRun`.

> **Upgrading is safe.** `preferences.md`, `commitments.md`, `config.md` and
> `state/` are never overwritten. Re-run the installer any time to pick up
> changes; your own files are left exactly as they are.

### Copilot CLI's own skill installer

`copilot skill add` takes a file, URL or directory, and is a fine way to add a
single skill without the installer:

```bash
copilot skill add skills/chief-of-staff
copilot skill list
```

It installs skills only — the agent file still needs `./install.sh`, and you lose
the version manifest that `update` depends on. Use it for trying one skill out;
use `./install.sh` for a real install.

### Contributors: link instead of copy

```bash
./install.sh --link       # or: .\install.ps1 -Link
```

Edits in the clone take effect immediately.

> ⚠️ If you link, `preferences.md`, `commitments.md` and `state/` then live
> **inside your clone**, and they will contain real names and mailbox content.
> `state/` is gitignored, but the personalization files are tracked, so adding
> them to `.gitignore` has no effect. Tell git to ignore your local edits:
>
> ```bash
> git update-index --skip-worktree \
>   skills/chief-of-staff/preferences.md skills/chief-of-staff/commitments.md \
>   skills/decision-log/config.md skills/partner-updates/config.md
> ```
>
> `./tools/check-clean.sh` inspects them regardless, so a filled copy is caught
> before it can be pushed.

### Updating

The installer records the version it put down, so Margo can tell you when you're
behind:

```bash
./install.sh update --check   # are you behind? changes nothing
./install.sh update           # re-install, at the latest version
```

`update` re-installs **only the skills you already have** — it won't quietly add
ones you never chose — and your personal files are preserved exactly as on any
reinstall. It compares against the published `VERSION`, falling back to your
local source when offline.

For a `--link` install there is nothing to copy: `update` tells you to
`git pull` in your clone instead, which is where the files actually live.

The PowerShell equivalents are `.\install.ps1 update -Check` and
`.\install.ps1 update`.

### Checking and removing

```bash
./install.sh status       # version, what's installed, and whether it's personalized
./install.sh uninstall    # removes it; personal files are backed up, not deleted
```

On macOS the package leaves a copy at `/usr/local/share/margo`, so
`/usr/local/share/margo/install.sh status` works even without a clone. On Windows,
uninstall through **Settings → Apps** as usual.

## 2. Check WorkIQ is connected

In Copilot CLI, confirm the tools are present and prefixed:

```
> list my tools
```

You're looking for `workiq-fetch`, `workiq-retrieve`, `workiq-ask` and friends. **The `workiq-`
prefix matters** — unprefixed names are not callable, and a missing prefix is the most common
first-run failure.

A quick live check:

```
> Margo, what's on my calendar today?
```

If that returns real events, the surface is working.

## 3. Teach her who you are

This is the step that decides whether Margo is useful or generic. Open the template:

```bash
$EDITOR ~/.copilot/skills/chief-of-staff/preferences.md
```

It works unfilled — everything falls back to a sensible default — but the four sections below are
worth ten minutes each:

| Section | Why it matters |
|---|---|
| **About me** | Role, working hours, and the focus blocks you want protected |
| **People → VIPs** | Drives what interrupts you and what waits for tomorrow's brief |
| **Scheduling defaults → priority ladder** | What yields to what. Without it, every reschedule becomes a question |
| **Communication & drafting voice** | Tone and sign-off. Drafts are written as *you*, so this is the difference between sending and rewriting |

See **[Personalization](personalization.md)** for how to fill it in well.

## 4. First run

```
> Margo, brief me.
```

Addressing her by name works because the agent file is loaded. To pin the session
to her explicitly — or to script her — use `--agent`:

```bash
copilot --agent margo                       # interactive, as Margo
copilot --agent margo -p "Brief me."        # one-shot, non-interactive
```

Then try, in rough order of how much they'll tell you:

```
> What did I miss?
> Triage my inbox.
> Prep me for my next meeting.
> Find 30 minutes with <someone> this week.
> What am I waiting on?
> How's my calendar looking?
```

Nothing in that list sends, posts, RSVPs or deletes anything. See
**[Trust & safety](safety.md)**.

---

## Optional setup

### Commitment tracking

`commitments.md` starts empty and fills up as you approve sends that create or resolve
obligations. Nothing to configure — but it's read on every brief, so it's worth glancing at
after the first week to check it's picking up what you'd expect.

### Azure DevOps work items

Fill in `preferences.md` → *Work tracking*, then `references/work-items.md`. You need the org,
project, area path and the two saved query IDs. Auth is your signed-in `az` identity — no PAT is
stored, and a 401 means `az login`, not a token hunt.

If you don't use ADO, delete `references/work-items.md` and its row from `SKILL.md`.

### Community sweeps

`references/engage.md` (Viva Engage) and `references/teams-feedback.md` (a Teams feedback
channel) both need IDs recorded in `preferences.md`. Resolve them once with `workiq-fetch` on
`/me/joinedTeams` and `/teams/{team}/channels`.

If you don't own a community, delete both files and their rows from `SKILL.md`.

### Scheduled runs

See **[Proactive & scheduled](proactive.md)**. Verify the state ledger first:

```bash
python3 ~/.copilot/skills/chief-of-staff/scripts/proactive_state.py status
```

### Large files

Only needed for files over 4 MB:

```bash
python3 ~/.copilot/skills/chief-of-staff/scripts/m365_files.py auth --account you@example.com
python3 ~/.copilot/skills/chief-of-staff/scripts/m365_files.py status
```

It reuses the client ID from your WorkIQ MCP OAuth config. If it can't find one, set
`MARGO_M365_CLIENT_ID` to your own app registration. Set `MARGO_M365_TENANT` if your tenant
rejects the default `organizations` authority.

---

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `tool does not exist` | Missing the `workiq-` prefix | Use the full prefixed name |
| Margo answers generically, ignores your rules | `preferences.md` not found or unfilled | Check it's next to `SKILL.md` in the installed skill |
| Brief has stale items | `$filter` without `$orderby` returns oldest-first | See [payload discipline](work-iq.md#payload-discipline) |
| `400 InefficientFilter` | No index backs that filter+sort pair | Drop the `$filter`, keep `$orderby`, narrow locally |
| `Access denied for path: X` | Tenant has disabled that path family | Not retryable — report it |
| Persona doesn't appear | Agent not loaded | Confirm `~/.copilot/agents/margo.agent.md` exists, then address her by name |
| Scheduled runs repeat themselves | State ledger reset | Run `proactive_state.py status`; check for `WARNING` output |
| Installer says "unidentified developer" | Unsigned build | Right-click the `.pkg` → **Open**, or `sudo installer -pkg Margo-*.pkg -target /` |
| Windows SmartScreen warning | Unsigned build | **More info → Run anyway**, or use the `irm ... \| iex` one-liner |
| Reinstall didn't pick up a change | The file is yours, not ours | `preferences.md`, `commitments.md` and `config.md` are never overwritten. Use `--force` to replace them (a backup is kept) |

For WorkIQ-specific failures, load the `workiq` skill and read its
`references/troubleshooting.md`.

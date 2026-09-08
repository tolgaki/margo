# Getting started

**Your first milestone is a cited brief for the right account, with nothing sent or changed.**
This page covers installation and connection. Continue through the
[user journey](user-guide.md) to turn that first brief into a daily workflow. Developers who
only want to explore or contribute should use the [synthetic-only developer setup](development/README.md)
instead of connecting their normal profile.

Follow sections 1-4 in order. Optional integrations and schedules can wait until the first
interactive run is useful. Existing users should review
[setup and migration](how-to/setup-and-migration.md) before updating private state.

## Prerequisites

| | |
|---|---|
| **[GitHub Copilot CLI](https://github.com/github/copilot-cli)** | The host that loads agents and skills |
| **Work IQ MCP server** | The Microsoft 365 surface. Hosted — no local package needed for tool calls |
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
./install.sh --all        # also the decision-log skill
```

```powershell
.\install.ps1
.\install.ps1 -All
```

Useful flags — `--skills a,b` to pick specific ones, `--dest DIR` for a different
Copilot directory, `--dry-run` to see what would happen, `--help` for the rest.
The PowerShell equivalents are `-Skills`, `-Dest`, `-DryRun`.

> **Normal installs and updates preserve personal files.** `preferences.md`, `commitments.md`,
> `config.md`, and runtime state are retained by default. `--force` / `-Force` can replace
> personal templates after backing them up; do not use it just to refresh code. File copying
> and private-state migration are separate operations.

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

Link mode (`--link` / `-Link`) points the installed skill at your checkout. Use it only for
synthetic contributor work, following the [developer journey](development/README.md).
Instruction changes are loaded by a fresh session; already-loaded instructions and extensions
may still need a reload.

For real use, keep the copy installation. In link mode, preferences and legacy skill-local
files resolve inside the checkout even though the account database stays outside it.
`.gitignore` and `skip-worktree` are not privacy controls and do not make it safe to fill tracked
templates with workplace data.

### Updating

The installer records the version it put down, so Margo can tell you when you're
behind:

```bash
./install.sh update --check   # are you behind? changes nothing
./install.sh update           # re-install, at the latest version
./install.sh update --reinstall # refresh even at the same revision, keeping personal files
```

`update` re-installs **only the skills you already have** — it won't quietly add
ones you never chose — and your personal files are preserved exactly as on any
reinstall. It compares both `VERSION` and the recorded source revision, so a same-version
commit is not mistaken for an up-to-date installation. Metadata and downloaded code are pinned
to the same remote commit, even when the command runs from an older local clone or native
package. `update --check` changes no installed files; `--dry-run` previews the pinned payload.

Remote failures or invalid metadata stop without changing the installation; there is no
success-shaped offline fallback. To install reviewed local changes deliberately, run normal
`install` from that source. Updates refuse an older remote version. `--reinstall` preserves
personal files; **do not use `--force` merely to refresh code** because it also replaces
personal files and customized prompts after backing them up.

For a `--link` install there is nothing to copy: `update` tells you to
update the reviewed clone explicitly instead, which is where the files actually live.
It never changes your branch or working tree for you.

The PowerShell equivalents are `.\install.ps1 update -Check` and
`.\install.ps1 update`; safe same-revision refresh is `.\install.ps1 update -Reinstall`.

File installation does **not** migrate private state, change capture policy, synchronize saved
app workflow prompts, or reload running sessions/extensions. Back up and pause writers before an
explicit memory migration; memory schema v1 requires `memory_state.py migrate`. Restore only
previously enabled schedules after the migration succeeds, reload app extensions, and start a
fresh Margo session to load updated instructions. See
[setup and migration](how-to/setup-and-migration.md). Installing Dream does not opt you into capture.

### Checking and removing

```bash
./install.sh status       # version, what's installed, and whether it's personalized
./install.sh uninstall    # removes it; personal files are backed up, not deleted
```

On macOS the package leaves a copy at `/usr/local/share/margo`, so
`/usr/local/share/margo/install.sh status` works even without a clone. On Windows,
uninstall through **Settings → Apps** as usual.

## 2. Check Work IQ is connected

In Copilot CLI, confirm the tools are present and prefixed:

```
> list my tools
```

You're looking for the Work IQ `fetch`, `retrieve`, `ask`, and discovery tools. This repository
uses names such as `workiq-fetch`, but the exact callable spelling comes from the host's registered
server. Discover the tools in that host rather than guessing a name or argument schema.
Installing Margo does not install or authenticate the Work IQ connection for you.

A quick live check:

```
> Margo, confirm which Work IQ account is signed in, then show my calendar today.
```

Confirm that it is the account you intended to connect. A successful, bounded read establishes
access to that calendar window; it does not prove mail, Teams, documents, or write permissions
are available. An empty calendar can be a valid result. A denied or failed source is a separate
limitation, not evidence of an empty day.

## 3. Teach her who you are

This is the step that decides whether Margo is useful or generic. Open the template:

```bash
$EDITOR ~/.copilot/skills/chief-of-staff/preferences.md
```

Unfilled settings produce generic advice, not a personalized plan. Fill these four sections
first; missing working hours or time zone should be resolved before relying on scheduling
recommendations:

| Section | Why it matters |
|---|---|
| **About me** | Role, working hours, and the focus blocks you want protected |
| **People → VIPs** | Drives what interrupts you and what waits for tomorrow's brief |
| **Scheduling defaults → priority ladder** | What yields to what. Without it, every reschedule becomes a question |
| **Communication & drafting voice** | Tone and sign-off. Drafts are written as *you*, so this is the difference between sending and rewriting |

See **[Personalization](personalization.md)** for how to fill it in well.

## 4. First run

Configure the ledger owner after confirming the signed-in Work IQ identity. Replace the
fictional principal below with that account:

```bash
python3 ~/.copilot/skills/chief-of-staff/scripts/margo_store.py init --account you@example.com
python3 ~/.copilot/skills/chief-of-staff/scripts/margo_doctor.py
```

On Windows, use `python` and the scripts beneath
`$HOME\.copilot\skills\chief-of-staff\scripts`. A custom installation needs the matching
`COPILOT_HOME` environment variable. Account configuration does not authenticate Work IQ.
Without an account, state commands report setup required instead of guessing.

If upgrading from legacy JSON or Markdown state, follow
[setup and migration](how-to/setup-and-migration.md) before running scheduled routines.

Start a session with the installed agent explicitly selected:

```bash
copilot --agent margo                       # interactive, as Margo
copilot --agent margo -p "Brief me."        # one-shot, non-interactive
```

In the interactive session, ask:

```
> Margo, brief me.
```

In a compatible app, select the Margo agent through the host's agent picker. Mentioning a name
is not a substitute for confirming which agent and installed instructions the host loaded.

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

You are ready for everyday use when the brief names its sources and time window, flags gaps,
and helps you choose a next action. Continue with
[your first useful brief](user-guide.md#2-get-your-first-useful-brief), then review one draft
before adding optional integrations.

---

## Optional setup

### Durable work and action desk

Start with [Closed-loop productivity](closed-loop.md). Configure an explicit account, migrate
legacy state with schedules paused, and review candidate commitments before confirming them.
`scripts/margo_doctor.py` distinguishes missing configuration, incomplete source coverage, and
failed automation runs. A successful scheduler status alone does not establish data coverage.

The app canvas is optional: `./install.sh --all --action-desk` or
`.\install.ps1 -All -ActionDesk`, followed by an extension reload. All core operations remain
available through the CLI. Its local action edits are not sends; final approval stays in the
foreground conversation. See [commitments and action desk](how-to/commitments-and-action-desk.md).

### Commitment tracking

An empty `commitments.md` is not evidence of an empty workload. Use the guided bootstrap to
review a bounded set of sourced candidates. After migration it is a readable export of the
work ledger, and confirmed state changes require explicit approval independently of sends.

### Azure DevOps work items

Fill in the installed `preferences.md` → *Work tracking* and read the installed
`references/work-items.md`. You need the org,
project, area path and the two saved query IDs. Auth is your signed-in `az` identity — no PAT is
stored, and a 401 means `az login`, not a token hunt.

If you do not use ADO, leave it unconfigured. You do not need to delete managed procedures
to skip an optional integration; see [GitHub and work items](how-to/github-and-work-items.md).

### Community sweeps

`references/engage.md` (Viva Engage) and `references/teams-feedback.md` (a Teams feedback
channel) need their own scope recorded in the private `preferences.md`. Teams discovery uses
the supported team/channel paths, such as `/me/joinedTeams` and `/teams/{team}/channels`.
An Engage GroupId is a different identifier, not a Teams channel ID; use the
[community guide](how-to/community-and-feedback.md) and the current host's supported discovery.

If you do not own a community, leave these optional routines unconfigured.

### Scheduled runs

See **[Proactive & scheduled](proactive.md)**. Verify the state ledger first:

```bash
python3 ~/.copilot/skills/chief-of-staff/scripts/proactive_state.py status
```

### Large files

The separate large-file bridge is only needed beyond the Work IQ binary transport limit.
Its refresh-token store currently requires **macOS Keychain**; it is not part of the
cross-platform core. See [documents and files](how-to/documents-and-files.md) before enabling
it. Downloads, copy/upload limitations, and sharing have different permission boundaries.

For a default macOS installation:

```bash
python3 ~/.copilot/skills/chief-of-staff/scripts/m365_files.py --account you@example.com auth
python3 ~/.copilot/skills/chief-of-staff/scripts/m365_files.py --account you@example.com status
```

It reuses the client ID from your Work IQ MCP OAuth config. If it can't find one, set
`MARGO_M365_CLIENT_ID` to your own app registration. Set `MARGO_M365_TENANT` if your tenant
rejects the default `organizations` authority.

This is a separate sign-in, not reuse of Work IQ's authenticated token. Confirm the account
matches the intended source using the returned `upn`, not just the Keychain label. `--account`
is a global option before the subcommand; the environment fallback is `MARGO_M365_ACCOUNT`,
not the ledger's `MARGO_ACCOUNT`. Status can refresh a token. OAuth discovery currently reads
`~/.copilot/mcp-oauth-config` even with a custom installation root. Do not change registrations
or scopes to bypass a policy denial.

---

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `tool does not exist` | Wrong callable name or missing server registration | Discover the host's Work IQ tools and schema; fix the connection if absent |
| Margo answers generically, ignores your rules | `preferences.md` not found or unfilled | Check it's next to `SKILL.md` in the installed skill |
| Brief has stale items | `$filter` without `$orderby` returns oldest-first | See [payload discipline](work-iq.md#payload-discipline) |
| `400 InefficientFilter` | No index backs that filter+sort pair | Drop the `$filter`, keep `$orderby`, narrow locally |
| `Access denied for path: X` | Tenant has disabled that path family | Not retryable — report it |
| Persona doesn't appear | Agent not loaded | Confirm `~/.copilot/agents/margo.agent.md` exists, then address her by name |
| Scheduled runs repeat themselves | State ledger reset | Run `proactive_state.py status`; check for `WARNING` output |
| Installer says "unidentified developer" | Build may be unsigned | Confirm the release source and follow your organization's software policy; use a reviewed source checkout if appropriate |
| Windows SmartScreen warning | Build may be unsigned or unrecognized | Confirm the release source and follow your organization's software policy rather than treating the warning as a setup step |
| Reinstall didn't pick up a change | Preserved personal file or customised prompt | Review and reconcile the diff. `--force` also replaces personal files; do not use it merely to update a prompt |
| State commands say setup required | No explicit account configured | Initialise the confirmed owner with `margo_store.py init`; this is separate from OAuth |
| App prompts did not change after copying | Workflow store is separate from installed files | Ask to sync the reviewed automation files to existing workflows |
| Canvas edits fail after a source changed | Revision or fingerprint is stale | Reload, revalidate sources, and prepare a new proposal; do not reuse old approval |

For failures specific to Work IQ, load the `workiq` skill and read its
`references/troubleshooting.md`.

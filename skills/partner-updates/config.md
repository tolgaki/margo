# Config — partner updates

Everything environment-specific lives here. Read it at the start of every routine.

> **This file is a template.** Replace every `{placeholder}` with your own board, roster and
> sources. The examples are illustrative and fictional. Until it's filled in, the skill should
> say so and offer to capture the details rather than guessing.
>
> ⚠️ Once filled in, this file names real people, teams and partners. Keep your working copy
> private.

## The board

| | |
|---|---|
| **Vault** | `{vault name}` — {e.g. an Obsidian vault, **not a git repo**, synced via OneDrive} |
| **Local path** | `{absolute path to the vault on this machine}` |
| **Board index** | `Partners/Partners.md` |
| **Consumers** | `Partners/Consumers/` |
| **Providers** | `Partners/Providers/` |
| **Review queue** | `Partners/Review queue.md` — created on first use |
| **Vault rules** | `AGENTS.md` at the vault root — **read it before your first write in a session** |

⚠️ The absolute path above is necessary *here* because this file lives outside the vault. **Never
write an absolute path into a vault note** — every contributor's sync root differs. Inside notes,
use vault-relative paths or `[[wiki links]]`.

⚠️ **If the vault is not version-controlled: no git, no diff, no undo.** Recovery means a human
digging through file version history. This is why the skill makes targeted edits and never
regenerates a file.

## Roster

{n} partners as of {YYYY-MM-DD}. The board index is authoritative if this drifts.

**Consumers ({n})** — surfaces that call your platform
`{Partner A}` · `{Partner B}` · `{Partner C}`

**Providers ({n})** — teams exposing data or tools through your platform
`{Provider A}` · `{Provider B}` · `{Provider C}`

## Aliases

⚠️ **Check this before creating any new partner note.** Every way a partner gets referred to in
transcripts and chats must resolve to the existing note, not a duplicate. This table is the single
highest-value part of the config — duplicates are the most common way a board rots.

| Heard as | Note |
|---|---|
| {acronym}, {product name}, {team name}, {codename} | `{Canonical note name}` |
| {…} | `{…}` |

⚠️ **Record deliberate splits.** If one product name maps to two notes on purpose — say a
consumer surface and a provider capability that share a brand — state it here and say why
merging them would hide something (e.g. that one of them has no engineering owner at all).

⚠️ **Record dual-role partners.** A partner that is both `partner-type: provider-and-consumer`
gets filed once but is relevant to questions on both sides. Note which folder it lives in.

## Not partners — do not create notes for these

Keeping this list is what stops the board accreting noise. Each row needs a reason.

| Thing | Why |
|---|---|
| {shared eval infra / test environment} | Infrastructure, not a partner |
| {internal capability or codename} | Your own platform capability, not an integration |
| {named customers} | Customer design partners, not platform surfaces |
| {adjacent product} | Doesn't directly call your APIs ({who said so}, {date}) |
| {deferred item} | Removed or deferred in {source} |
| {unresolved item} | ⚠️ Unresolved — no documented integration contract. Leave unclassified until there is one. |

## Sources, in priority order

| # | Source | How |
|---|---|---|
| 1 | **{Weekly shiproom}** — {day/time}, organizer {name} | Vault note first (`Meetings/{…}/`), then Work IQ transcript/recap/chat |
| 2 | **{Advisory board / steering}** — {day/time}, organizer {name}, {cadence} | Vault note first, then Work IQ |
| 3 | **Partner-specific standing meetings** | `{meeting}`, `{meeting}`, `{meeting}` |
| 4 | **Teams threads** | `{channel}`, `{channel}`, 1:1s with the people below |
| 5 | **Email** | Partner status updates, rollout notices |
| 6 | **Loop / SharePoint** | Ownership matrix, offsite pages, the shiproom deck at `{link}` |

## People

**Core team** — {name} ({what they own}) · {name} ({what they own}) · {name} ({what they own})

**Leadership in the room** — {name} · {name}

⚠️ **Several "owners" are escalation paths, not owners.** When a note lists a senior person as PM
for a partner that has no dedicated PM, that is an escalation path — say so rather than reporting
it as ownership. List the affected partners here explicitly so the distinction survives a
handover.

## Cadence

| | |
|---|---|
| **How it runs** | {e.g. **On demand only.** There is no scheduled sweep — the team maintains the board by hand.} |
| **Your job when invoked** | Propose sourced changes, apply them, and be explicit about what you could not verify. The human asking is in the loop, so use it: surface ambiguity rather than staging it silently. |
| **Indexing caveat** | A meeting transcript is typically not indexed for a few hours after the meeting ends. A same-evening sweep should mark it **pending**, not absent. |

⚠️ **Record automation decisions here.** If a scheduled workflow was tried and turned off, say so
and say why. Re-enabling it is a decision to re-take with the team, not a setting to quietly flip
back on.

The **Unattended mode** contract in `SKILL.md` applies only when you are invoked from a workflow.
In a normal interactive run, ask when unsure.

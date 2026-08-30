# Deploying Margo for a team

**Status: design note. None of this is implemented in this repo.**

Today Margo is a single-player install: skills on your laptop, `preferences.md` filled in by you,
Work IQ delegated to your account. Everything works because there is exactly one person, one
identity and one set of files.

This page is what it takes to put her in front of a team — the review of what breaks, the surface
to build on, and the order that de-risks it. It assumes [Memory](memory.md), which is the hard
half and should be read first.

---

## 1. What breaks when the second person arrives

A review of the current agent and skills, sorted by how load-bearing the assumption is.

| # | Assumption | Where | Severity |
|---|---|---|---|
| 1 | Derived notes need no access control | Everywhere. There is no memory system at all | **Blocking** — see [Memory](memory.md) |
| 2 | `/me` is the person being served | 40 `/me` references across 14 files under `skills/` | **Blocking** |
| 3 | `preferences.md` is a file next to the skill | Read at the start of every routine; referenced in 16 files | **Blocking** |
| 4 | `commitments.md` is one person's ledger | `follow-through.md`, every brief | **Blocking** |
| 5 | `state/` is one ledger for one user | `proactive_state.py`, file-locked, path-fixed | **Blocking** |
| 6 | Whoever is talking is the person who can approve a send | The `🛑` rules, `drafting.md` | **Blocking, and new** |
| 7 | "You" and "the user" mean one determinate person | 136 occurrences of "the user" in `skills/` | High |
| 8 | Cost is one person's usage | `workiq-ask` at 10–60s, hourly sweeps | High |
| 9 | Standing authorization is a property of the install | `SKILL.md` § 1 | Medium |

Items 2–5 are mechanical and are best done **while still single-user**, where nothing can leak
because there is nothing to leak into. Item 1 is the design problem. Item 6 is the one that is
genuinely new, and it is worth stating on its own.

### The approval rule that multi-user forces

The repo's central promise — *propose, never act unilaterally* — silently assumes the approver and
the principal are the same person. Put Margo in a channel and they come apart:

> Margo drafts a reply on Alex's behalf. Dana, in the same channel, says "send it."

Nothing in the current rules stops that, because the rules were written where it could not happen.
Two additions close it, and they belong in `SKILL.md` alongside the existing bounded-authorization
paragraph:

- **Approval is bound to the principal whose identity performs the action.** Only Alex can approve
  a send from Alex's mailbox. Everyone else in the room is a bystander, however senior.
- **In a shared room, Margo names whose approval she is waiting for**, so the gate is visible to
  everyone watching rather than only to the person holding it.

And one that follows from it: **standing authorization is per-principal, never per-room.** Alex
granting "you may archive without asking" is about Alex's mailbox and travels with Alex, not with
the channel he said it in.

---

## 2. The surface

Four ways to put her in front of people, and they are not alternatives so much as a sequence.

| Surface | What you get | What it costs | Verdict |
|---|---|---|---|
| **Per-user CLI install** *(today)* | Full playbook, full capability, zero infrastructure | Nothing is shared; every person configures from scratch; no team memory | **Keep.** This is the power-user and contributor path, and the reference implementation |
| **Declarative agent in Microsoft 365 Copilot** | Ships inside Teams and Copilot chat, tenant identity for free, near-zero ops | No custom store, so no memory design; the instruction budget will not hold this playbook | **No.** Fails on the one requirement that matters |
| **Custom engine agent (M365 Agents SDK), surfaced in Teams** | 1:1 chat, channel mention, group chat, meeting side panel. You own the memory store, the model and the playbook | Hosting, an Entra app registration, per-user consent, an on-call story | **Yes.** This is the recommendation |
| **Entra Agent ID + Agent User** | Margo has her own mailbox and presence; people can email *her* | A licence per agent, a real directory object, a much wider injection surface | **Later.** Layer onto the above, don't start here — see [digital worker](digital-worker.md) |

The recommendation is the third row: **a custom engine agent in Teams**, because it is the only
option that lets memory live in containers we choose while the conversation lives where the team
already is. The Teams app is the surface; it is not the product. The product is the playbook plus
the memory model, and both stay portable.

### What stays exactly as it is

The markdown is the asset, and none of this changes it into code:

- `agents/margo.agent.md` becomes the system persona, loaded once per deployment. One voice for
  the team, not one per person — the persona is shared; the *preferences* are not.
- `skills/*/SKILL.md` and `references/` load the same way they do today, by router.
- The `decision-log` skill needs no changes at all. It was written for a team from the start, its
  writes are already PRs, and it is the closest thing here to a working team-memory system.

---

## 3. Identity, and the choice that quietly decides everything

Margo must act **on behalf of the signed-in user** — OBO, delegated, per-request — for both Work
IQ and memory.

The alternative is available, easier, and fatal: an app-only identity with tenant-wide Graph
scopes will work on day one, pass every test, and delete the entire access-control model in a
single configuration line. Every guarantee in [Memory](memory.md) §5 rests on Graph refusing a
read. Give the app permission to read everything and Graph never refuses, so the only thing
standing between two people's private mail is the model's own care.

Three rules for the build:

1. **No app-wide `.All` Graph scopes.** If a feature seems to need one, it is a feature that needs
   redesigning, and the reason will be in what it wants to remember.
2. **Every retrieval call carries the reader set** — who will see the answer, not who asked. In a
   channel those differ, and that difference is the whole disclosure check.
3. **Consent is per-user.** Someone who has not consented gets a sign-in prompt, not a degraded
   answer assembled from someone else's grant.

The unattended path keeps the belt-and-braces the repo already uses: `tools/margo-scheduled.sh`
hard-codes `--deny-tool` for every Work IQ write rather than instructing the model not to send.
The server needs the same shape — writes gated in the host, not in the prompt.

---

## 4. What has to change in the skills

Small, and mostly mechanical, which is the argument for doing it early.

| Change | Shape |
|---|---|
| **Principal indirection** | `/me` → a configured principal. Do it while the principal is still you, so nothing behaves differently and the 40-site change lands under test |
| **Config out of the skill folder** | `preferences.md` and `commitments.md` move into the principal's own store — which is also their memory store, so this is one mechanism, not two |
| **State keyed by principal and room** | `proactive_state.py` keeps its CLI contract exactly, so no reference file changes; the backing store becomes keyed rather than a fixed path. The `flock` design does not survive a server |
| **Whose, not your** | The prose says "you" 136 times meaning one determinate person. In a room it has to say whose calendar is being protected. This is the largest diff and the least interesting |
| **Approval binding** | The two rules in §1 above, added to `SKILL.md` and `references/drafting.md` |
| **Room awareness** | New: a routine needs to know whether it is in a 1:1 or a room, because the disclosure check depends on it and nothing today has the concept |

---

## 5. Order

Each phase is shippable, and each one is chosen so that the thing that could leak does not exist
yet when the thing that could break is being built.

**Phase 0 — Make it multi-user-shaped, single-user.**
Principal indirection, config into the principal's store, state keyed rather than pathed.
Behaviour identical, one reader throughout. *Done when:* the existing install still works and
`/me` appears nowhere in `skills/`.

**Phase 1 — Memory, private scope only.**
The record format, provenance, `forget that`, cascade through `derived_from`. Still one person, so
audience is `{you}` everywhere and no disclosure check can fail. *Done when:* the format has
survived a fortnight of real use and `"what do you know about me"` returns something a person
recognizes.

**Phase 2 — Teams app, 1:1 only.**
Multiple principals, each with their own private memory, OBO throughout. No rooms means no
cross-user disclosure path exists yet. **This is the phase where "everyone on my team can use
her" is actually true**, and it is deliberately reached before rooms. *Done when:* two people use
her for a week and neither can get her to say anything sourced from the other.

**Phase 3 — Rooms.**
Channel-scoped memory in the channel's own container, witness sets, the disclosure check enforced
at retrieval, the approval-binding rules, the consent notice on join. Group chats and private
meetings record witnesses only, no content — [Memory](memory.md) §6. *Done when:* the withheld-record
log is boring.

**Phase 4 — Team memory and "who knows what".**
Generalize the decision log's shape to the rest: the audience-minus-witnesses queries, briefing a
new joiner, "does Dana know yet". This is the phase people will ask for on day one and it is
correctly last, because it is the one that reads across scopes.

**Phase 5 — Agent ID, if wanted.**
Only once people want to email Margo rather than talk to her. [digital worker](digital-worker.md)
already scopes it, and its warning stands: an agent with her own inbox is *more* exposed to
injection, not less.

---

## 6. Cost, honestly

A per-seat cost line, not a rounding error, and worth sizing before building rather than after.

- **Model usage** scales with people times routines. The morning brief is the expensive one and
  every person wants it at roughly the same time.
- **`workiq-ask` is 10–60 seconds** and Tier-2 sweeps already run ~40 times a week *per person*.
  The existing rule — never call `ask` in a sweep — stops being a style preference and becomes the
  thing that keeps the bill finite.
- **Hosting and on-call.** A laptop install that fails is one person's morning. A Teams app that
  fails is the team's, at 07:15.
- **Licensing**, if Phase 5 happens: an Agent User consumes a real Microsoft 365 licence, and
  Agent 365 is licensed separately.

Memory is a cost argument as well as a privacy one — remembering the answer is cheaper than asking
Work IQ for it again — but that is a hypothesis until something measures it.

---

## 7. Decisions needed before Phase 2

Stated as forks rather than assumptions, because each one changes the build:

1. **One tenant?** Everything here assumes a single Microsoft 365 tenant. Shared channels span
   tenants and are not covered.
2. **Hosted where, and by whom?** A custom engine agent needs somewhere to run and someone to be
   called when it doesn't.
3. **One persona for the team, or one per person?** The recommendation is one — shared voice,
   private preferences. Per-person personas multiply the review surface and buy little.
4. **Does compliance want Margo's memory discoverable?** The design in [Memory](memory.md) §5 makes
   it so automatically. That is almost certainly the right answer, and it is not a reversible one.
5. **Who owns the room-memory switch?** A channel owner, a team admin, or anyone in the room. This
   determines whether memory is opt-in or opt-out, which is the single most visible product choice
   in the whole plan.

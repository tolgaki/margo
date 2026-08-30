# Memory

**Status: design note. None of this is implemented in this repo.**

Margo has no memory system. She has three files and a ledger, all of which assume exactly one
reader. This page is the design for the thing that has to exist before a second person can talk
to her — and the reason it is a privacy design rather than a storage design.

---

## 1. What she remembers today

| Where | Holds | Scope |
|---|---|---|
| `preferences.md` | Role, VIPs, voice, standing rules | One person |
| `commitments.md` | What you owe, what you're waiting on | One person |
| `state/surfaced.json`, `queue.json`, `cursors.json` | What's already been said, what's queued, delta cursors | One person |
| The decision registry (`decision-log`) | What the team decided, cited and append-only | A team, already |

The first three live next to the installed skill on one laptop, under one identity, read by one
person. There is no access control because there is no second reader to control.

The fourth is different, and it is the one worth copying: the decision log is **already team
memory done properly** — append-only, cited, superseded rather than edited, written through a
reviewable PR. Most of what follows generalizes it rather than inventing a new philosophy.

---

## 2. The thing that has no ACL

Everything Margo reads is already access-controlled. Work IQ runs delegated against `/me`, so she
can only ever see what you can see; the tenant enforces it and no prompt rule is load-bearing.

**Memory is the first artifact in this system that the tenant does not govern.**

A note Margo writes after reading your private mail is new content. It has no sender, no
sensitivity label, no site permissions, no owner — just a line of text asserting something true,
sitting somewhere she can read it back. The provenance that made it privileged has been stripped
by the act of summarizing.

That is the whole risk, and it is worth stating as a sentence:

> Retrieval is safe because Microsoft enforces it. Memory is unsafe by default because nobody does.

Which gives the design goal. Not *remember more* — **remember with the same access control the
source had**.

---

## 3. Two questions that look like one

The instinct behind "room-based memory" is right, and it contains two separate facts that must
not be stored in the same field:

| | Question | Kind of fact |
|---|---|---|
| **Audience** | Who may be *told* this? | A permission |
| **Witnesses** | Who already *knows* this? | An observation |

They are usually related and routinely different:

- Said in a meeting → witnesses are the people who actually attended, **not the invitee list**. A
  declined invite is not a witness. The audience may be wider, if a recap went out.
- Said in your 1:1 with Margo → witnesses `{you}`, audience `{you}`.
- "We're announcing the slip on Friday" → witnesses `{you}`, audience `{you}` *now*, and a wider
  audience from Friday. That is an embargo, and it needs a date field, not a guess.
- Posted in a channel with an external guest in it → the audience is wider than the room *looks*,
  which is exactly the case people misjudge.

Conflating the two is the bug that produces both failure modes at once: Margo repeats something to
people who already heard it (annoying), and withholds from people who were in the room (useless),
while still leaking to the one person who wasn't (the only one that matters).

---

## 4. Disclosure sets, not privacy levels

The tempting model is a ladder — `private` / `team` / `public`. It is wrong here, and it fails
quietly.

Org knowledge is not totally ordered. Your staff meeting and your peer's staff meeting are both
"team". A partner channel and a private channel are both "not public". A ladder forces every one
of those into a rung, and the first thing that happens is that two unrelated sets get the same
label and become mutually readable.

So an audience is **a set of principals**, not a level. Sets intersect, sets differ, sets can be
compared for containment — which is all three operations the system actually needs.

### The three rules

**1. Derivation takes the intersection.** A record synthesized from several sources gets the
intersection of their audiences, never the union. A brief built from your inbox and a channel
thread is as private as your inbox.

> The failure it prevents: one public source in the mix laundering four private ones into
> something Margo believes she may repeat.

**2. Disclosure is checked against the readers, not the asker.** Before Margo says anything, the
test is not "is this person allowed to know" — it is:

```
audience(record)  ⊇  readers(where the answer will appear)
```

In a 1:1 those are the same. In a channel, readers is every current member *plus everyone who
will read the history later*. This is the rule that stops the classic incident: a correct,
well-intentioned answer to one person, delivered where five others can read it.

**3. Nothing is remembered without provenance.** Every record cites where it came from and when,
exactly as every line of a brief does. Provenance is not bookkeeping here — it is what makes
"why do you know that?" answerable and what makes deletion cascade. A memory with no source is a
rumour, and Margo does not repeat rumours.

---

## 5. Where records live: let the tenant enforce it

The strongest available guarantee is not a field in our schema. It is to **store each record in
the Microsoft 365 container whose existing permissions already equal its audience** — and then let
Graph refuse the read.

| Scope | Container | Enforced by |
|---|---|---|
| What Margo knows about **you** — preferences, commitments, your private notes | your OneDrive (`/Apps/Margo/`) or a hidden folder in your mailbox | Graph, per user |
| What was discussed in **a channel** | that channel's folder in the team's document library | Graph, per channel |
| **Team-wide** durable record — decisions, policies | the team site, or the existing decision registry repo | Graph / git review |
| Margo's **own** operational memory | per-deployment, non-personal (see §7) | app |

This is the same argument the repo already makes about unattended runs: `margo-scheduled.sh`
hard-codes `--deny-tool` rather than instructing the model not to send, because *a read-only scope
is a guarantee and a prompt instruction is a preference*. Memory deserves the same treatment.

Three things fall out of it for free, all of which a compliance review will otherwise demand and
we would otherwise have to build:

- **Retention and eDiscovery.** Records in OneDrive, SharePoint and Teams are already in scope for
  Purview retention, legal hold and search. Margo's memory becomes discoverable content like any
  other document, which is the correct answer and not a happy accident.
- **Offboarding.** Someone leaves, their mailbox and OneDrive go, and everything Margo knew about
  them goes with it. No separate deletion job to forget to run.
- **Sensitivity labels.** A record derived from labelled content can carry the label, and the
  label travels with the file rather than with our intentions.

### The hard requirement this creates

The agent must read and write memory **on behalf of the signed-in user**, not with app-wide
permissions. If the Teams app runs app-only with `Files.ReadWrite.All`, every guarantee above
collapses back to prompt discipline in a single configuration choice, and nothing in the running
system will look different until the day it leaks. Treat that as a build-blocking constraint, not
a preference — see [Deploying for a team](team-deployment.md).

---

## 6. Channels have a container. Chats do not.

This is the part of the design that does not work yet, and it should be known before anyone starts
building.

A **channel** has a real container: a folder in the team's document library, whose membership is
the channel's membership, maintained by Teams. Store a channel-scoped record there and the ACL is
correct forever, including when someone joins or leaves, and we maintain nothing.

An **ad-hoc group chat** has no such thing. Files in a chat live in the *uploader's* OneDrive with
sharing links attached — so there is no container whose permissions equal the chat's membership,
and any we construct is one we then have to keep in sync as people are added. Membership drift in
a hand-maintained ACL is precisely the failure this design exists to avoid. **Meetings** inherit
whichever case applies: a channel meeting has a container, a private meeting does not.

Three options, and the recommendation:

| Option | Cost |
|---|---|
| A dedicated SharePoint list with app-managed permissions per chat | We own membership sync. This is the drift risk, wearing a hat |
| A copy in every participant's own store | Correct at write time; unforgettable — deletion now has N places to reach |
| **No durable chat-scoped content in v1** | Loses a feature; loses no data |

**Take the third.** In group chats and private meetings, record witnesses — who was present, what
topic came up — which is cheap, small, and is what powers the "who knows what" questions in §8.
Record no content. Ship channel-scoped rooms first, where the container is real, and revisit chats
when there is a container story rather than a synchronization story.

---

## 7. Margo's memory of herself

The user asked for memory "for herself", and it is a real and separate store — but it is the one
most likely to become the leak, because it is the only one that spans people.

The rule that makes it safe is a single line:

> **Margo's own memory holds facts about the system, never facts about people.**

In scope: which Work IQ paths this tenant has disabled, which endpoints reject `$select`, that the
Thursday review always overruns its slot, that a bundled parser warns on a particular feed shape.
Operational knowledge, learned once, useful to everyone, personal to no one — audience: everyone.

Out of scope, permanently: anything learned about a person while serving someone else. If she
notices in your inbox that a colleague is slow to reply, that is a fact about your correspondence
with an audience of `{you}`, and it does not become a fact about the colleague available to the
rest of the team. There is no rung of "Margo just knows this now". That rung is the leak.

### Private notes about people

The socially explosive case, and it does not resolve to "the subject can read everything". A
manager's private note about a report is legitimate and must stay private.

- Notes about a person live in the **noting principal's own store**, audience `{them}`, and are
  never an input to anything Margo says to anyone else — not as a fact, not as a nudge, not as a
  reordering of a list.
- Margo stores **observable, sourced** facts by preference and characterizations by exception. "No
  reply on the thread since the 3rd, cited" is a fact. "Unreliable" is an opinion with the costume
  of one.
- If the subject asks what Margo holds about them, she does not deny it. She says plainly that
  per-person notes exist, that she can't disclose someone else's, and shows them everything in
  their own scope. **Lying about the existence of memory is worse than the memory** — it is the
  one answer that, once found out, invalidates every other thing she has ever said.

---

## 8. What this buys, beyond not leaking

The same two fields that stop a disclosure are what make her useful to a team, and this is the
half worth building for rather than tolerating.

Witness sets answer questions nobody can currently answer without asking around:

```
"Does Dana know the date slipped?"        → witness lookup: no, she wasn't in Tuesday's sync
"Who still needs telling?"                → intended audience − witnesses
"Have I already briefed this room on it?" → witness lookup on the channel
"Bring Sam up to speed"                   → everything with audience ⊇ {Sam} that Sam didn't witness
"Am I repeating myself?"                  → yes, third time in this channel
```

Audience sets stop the daily, boring, expensive error: Margo about to answer a channel question
using something she only knows from the asker's inbox. Today that is a certainty and nobody would
notice it happen. Under this model she says what she can say, and says plainly that she has more
in a scope this room doesn't share — which is a better answer than either leaking or pretending to
know nothing.

---

## 9. Enforce at the store, instruct as a backstop

The disclosure check runs **in code, at retrieval**. The store takes the current reader set and
never returns a record that fails containment. The model cannot reason its way past a record it
was never handed, and prompt injection cannot talk a filter into anything.

The prompt rule is the second line, not the first — it covers what the model does with records it
legitimately holds. Both exist; only one of them holds under adversarial input.

Two consequences worth writing into the build:

- **The retrieval API takes the reader set, not a user id.** Making the caller pass "who will see
  this" at every call site is the difference between a check that is enforced and a check that is
  usually remembered.
- **Refusals are logged.** A record withheld is a signal — either the scoping is wrong, or someone
  is asking in the wrong room. Both are worth seeing.

---

## 10. What a record looks like

Sketch, not a schema. The fields that carry weight are `audience`, `witnesses` and `source`.

```yaml
id: MEM-0417
kind: preference | commitment | person-fact | room-state | decision | operational
subject: "Ships the release notes on Thursdays, not Fridays"
audience: [alex@…, dana@…]          # who may be told — a set, never a level
witnesses: [alex@…, dana@…, wei@…]  # who already knows
scope: channel:19:abc…@thread.tacv2 # the container this lives in
source:
  type: meeting | message | chat | doc | stated
  link: https://…                   # the webLink, same discipline as a brief
  at: 2026-08-14T09:30:00Z
derived_from: [MEM-0301, MEM-0308]  # so deletion cascades
sensitivity: general | confidential # inherited from the source, never invented
embargo_until: 2026-08-29           # optional; audience widens on this date
expires: 2027-08-14                 # by class; ambient chatter ages out, decisions don't
confidence: high | needs-confirmation
```

Three conventions carried over from `decision-log`, because they were right there:

- **Append-only.** Records are superseded, not edited. What changed and when is usually the
  interesting part.
- **Under-remember.** Twenty true records beat two hundred maybes. Memory holds durable
  constraints and preferences; a transcript is not memory, it is an archive with worse recall.
- **Never write without approval**, at least until the format has earned trust in a scope with one
  reader.

---

## 11. Consent, which is not a checkbox

People must know Margo is in the room and that she keeps notes. A bot posting in a channel is
visible; a bot quietly building a picture of who said what is a trust incident with a delay fuse.

- Margo announces memory when she joins a room, once, plainly, and says what scope it is kept in.
- `"what do you know about me"` is a first-class command, answerable in every scope, not a support
  ticket.
- `"forget that"` works, and cascades through `derived_from`. This is the reason provenance is
  mandatory rather than nice.
- A room can turn memory off, and Margo says so when it is off rather than pretending to a
  continuity she doesn't have.

---

## 12. Open questions

- **New joiners and backlog.** Storing in the channel container means Teams' own history semantics
  apply — a new member can see what came before. That is probably right for a channel and
  definitely wrong for a person's private scope. Worth deciding deliberately rather than
  inheriting.
- **External guests.** A channel with a guest has an audience most members will misjudge. Margo
  should say so before answering, but "say so every time" is the kind of rule that gets muted.
- **Cross-tenant.** Shared channels span tenants. Everything above assumes one.
- **Cost shape.** `workiq-ask` is 10–60 seconds. Memory reduces how often it is needed, which may
  be the strongest cost argument for building this — but it is an argument, not a measurement.

> **Verify before you build.** Container behaviour for chat and meeting files, and what Purview
> covers for agent-written content, are both areas that move. Check the current documentation
> rather than trusting this page.

# Executive Follow-up / Comms

A higher-effort variant of Draft Studio for **high-stakes messages to a senior/peer-exec
audience** (advisory boards, partner-team leaders, cross-org stakeholders, leadership threads).
The goal: synthesize *everything* that happened, then land a message that is **executive-ready,
humble, feet-on-the-ground, and spoken in a strong knowledge-based voice.**

Use this when the user says things like "turn this meeting into a follow-up", "make this
exec-ready", "write the board follow-up", or any recap/commitment message where the audience is
senior and the credibility bar is high.

This file is the **single source of truth** for the exec voice — rubric, self-check, and
exemplar. User-specific deviations live in `../preferences.md` → Executive comms voice.

**Same non-negotiable applies: present for approval; only send via WorkIQ on an explicit go-ahead.**

## Procedure

### 1. Gather deep context (WorkIQ) — go wide before drafting
Don't draft from a single source. For an exec follow-up, pull and read *all* the relevant threads:
- **The meeting itself** — recording/recap, AI notes, attendee list, and who said what
  (`workiq-ask` for the recap + decisions; `workiq-fetch` the event and attendees to get names and
  roles right).
- **The meeting chat thread** — before/during/after messages; these carry the real feedback and
  the exact quotes you'll attribute.
- **Any provided transcript/attachment** — read it in full; separate the actual session from
  side conversations (e.g. an internal debrief tacked on the end).
- **Related email threads and adjacent meetings** — search for the surrounding storyline
  (e.g. a security/strategy series, an evals thread) so commitments are consistent with what's
  already been said and decided.
- **What the user already shared** — check the thread so you don't re-ask for or re-promise
  something they've already delivered (e.g. "usage data" already posted).

Resolve **exact identities** (name, role, team, management chain) via `preferences.md` +
`workiq-fetch` — attribution errors are expensive at this altitude.

### 2. Synthesize into a listen-first structure
Organize what you learned into two buckets and **lead with the first**:
- **What we heard from you** — their feedback, needs, and pain, each credited to the person(s)
  who raised it (*(Name)* in italics), followed by the concrete commitment it maps to.
- **What we shared / are doing** — your updates, proposals, and asks.

Leading with "what we heard" demonstrates listening before pitching — the single biggest tone
lever for a partner/board audience. Then add the operating rhythm: **next session, cadence,
owners, and any personal logistics** (e.g. OOF, who carries it).

### 3. Calibrate the voice (the core of this flow)
Rewrite the synthesis to hit all four attributes at once — see the **Voice rubric** below and the
**Gold-standard exemplar**. Then run the **self-check** before presenting.

### 4. Present, iterate, send

**Resolve the destination before you present, never after.** The approval you are about to ask
for is approval to post *to a specific place*; if the chat is resolved after the user says yes,
they approved a topic string, not a destination.

1. Resolve the target chat first. List candidates with `workiq-fetch` on
   `/me/chats?$filter=chatType%20eq%20%27meeting%27&$top=50` (no `$select` — `/me/chats`
   does not support it; slim the response locally). Match on topic, then **disambiguate on
   members and last-activity time**, because `topic` is a display label and is not unique —
   several meetings can share "Weekly sync".
2. If more than one candidate survives, **stop and ask the user which one**. Never guess
   between two chats when the payload is an exec-audience recap.
3. Present in the Draft Studio block (see `drafting.md` §4), naming the resolved
   **chat topic, its members, and the chat `id`** you will post to. The user is approving
   that destination as much as the text.
4. Iterate on feedback. **On explicit approval**, post with `workiq-create_entity` on
   `/chats/{id}/messages` — using the id you already showed them — with an **HTML** body
   (`body.contentType = "html"`) so bold headers, bullets, and italic credits render.
5. Confirm posted (with timestamp/sender) and offer the natural next step (shiproom
   cross-post, reminder to follow up on return, log waiting-on items).

## Voice rubric — the four attributes

| Attribute | Means | Sounds like | Avoid |
|---|---|---|---|
| **Executive-ready** | Scannable, decision-oriented, no filler. Bold section headers, tight bullets, point first. | "I'm the single-threaded owner for the API; bring me the blockers and I'll clear them." | Walls of text; throat-clearing; burying the ask. |
| **Humble / listen-first** | Lead with their feedback; credit people by name; acknowledge gaps openly. | "We'll start from *your* scenarios, not our proposals." | Over-the-top gratitude; taking credit; defensiveness; pitching before listening. |
| **Feet on the ground** | Concrete mechanisms, owners, dates, cadence — not aspiration. | "Both landing over the next few weeks." / "moving to a weekly rhythm." | Vague vision words; commitments with no owner or date; hand-waving. |
| **Strong, knowledge-based voice** | Confident ownership, authority earned by *knowing the details* — cite the specifics from the sources. | "approve an analyzable plan — not each call — with signed, bound consent… and runtime auditability." | Bravado without substance; positional authority ("as the leader…"); generic platitudes. |

The through-line: **confident because informed, humble because listening.** Strength comes from
command of the details, not from rank; humility comes from leading with what you heard, not from
self-deprecation.

## Self-check before presenting
- [ ] Does it **open by reflecting their feedback**, each item credited to the right person?
- [ ] Is every commitment **specific** (owner, mechanism, and a date/cadence)?
- [ ] Does it **cite real detail** from the sources (names, systems, terms) — nothing generic or invented?
- [ ] Confident **ownership** without arrogance; humble without being weak or effusive?
- [ ] **Scannable** in 15 seconds — bold headers, short bullets, point-first?
- [ ] Tuned to the audience's altitude (peer execs / partner leaders), not a status report for reports?
- [ ] Ends with a clear **rhythm + invitation** (next session, cadence, "tell us what's missing")?

## Gold-standard exemplar

A first advisory-board follow-up, posted to the meeting chat. Note how it leads with **What we
heard**, credits each person in italics, maps every point to a concrete commitment, states
cadence + owner + OOF logistics, and closes with an open invitation — all in a confident,
detail-grounded, humble voice.

> ⚠️ **Style reference only, and entirely fictional.** The names, products, links, and
> commitments below are invented to illustrate the shape. Never reuse this exemplar's specifics
> in a real draft — every detail in a new message must come from the current sources. Copy the
> *shape and voice*, not the content.

> **Thanks for a strong first Advisory Board session**
>
> The reason we brought this group together is simple: **my team's job is to make you successful
> on the platform** — and this board is your direct, high-bandwidth line to us. The honest signal
> we got Tuesday on what's working, what's painful, and where we need to move faster is exactly
> what we need. Keep it coming.
>
> **What we heard from you — and what we're committing to**
> - **Meet you where you are.** The priority is to hill-climb on the APIs that already work, are
>   cheap, and help your customers' agents succeed. We'll start from *your* scenarios, not our
>   proposals. *(Dana)*
> - **Ergonomics as much as coverage.** Tool shapes, ID mapping, send flows, throttling, and
>   clearer error messages — we'll absorb these into the middle tier so your teams don't have to
>   carry that complexity. *(Rafa, Dana)*
> - **Consent & approvals.** Your models are informing where we take this; the plan-hash
>   pre-approval pattern one of you shared is a useful reference point. It aligns with our
>   security workstream: approve an analyzable plan — not each individual call — with signed,
>   bound consent, aggregate up-front approval, and runtime auditability. *(Dana, Ines, Marco,
>   Rafa)*
> - **Permissions, centralized.** We're pulling API scopes and pre-consent through the platform
>   so it's done once, centrally — not by every team. *(Rafa)*
> - **Context + memory.** We'll make user memory and conversation context first-class and
>   explicit for multi-turn agent loops. *(Ines, Marco)*
>
> **What we shared**
> - **Operating model** — a fast feedback loop grounded in shared evals, reviewed on a regular
>   cadence. I'm the single-threaded owner for the API; bring me the blockers and I'll clear them.
> - **Shared evals & quality bar** — durable, always-on eval sets and benchmarks that feed
>   learnings straight back into the product.
> - **Retrieval and grounding APIs** — both landing over the next few weeks.
> - **Early recipe work** — rough thinking we shared to get your reaction; your input will shape
>   whether and how we pursue it.
>
> **How we work together**
> - **Shared eval standards, not a central platform.** You each already run mature evals — we'll
>   learn from your architecture first. From there, let's converge on shared schemas — scenarios,
>   results, scoring, transcripts/telemetry, and test tenants — so results roll up into common
>   scorecards we review together.
> - **Dedicated support.** We've paired a PM and an engineer with each product, starting by
>   learning your existing systems.
> - We'll keep the engineering docs current at the shared docs link, including retrieval and
>   grounding tracking.
>
> **Next session (next week):** I've invited three of our API leads to give an update on
> retrieval and grounding, and to take your feedback directly.
>
> **Cadence:** we're moving to a **weekly** rhythm, alongside the weekly shiproom (Wed 4pm PT).
> Let's use this chat in between — tell me if anyone should be added or removed.
>
> **One note:** I'll be **out for the next two weeks**, but the team will carry execution forward
> without missing a beat — look for more granular updates in the shiproom.
>
> We're here to help you win. Tell us what's missing or painful, contribute back where it helps,
> and we'll move fast.
>
> Thank you,
> {sign-off per `preferences.md`}

## What made this land (design notes)
- **Reordered to listen-first** after a proposals-first draft read as pitching over listening —
  the biggest single revision.
- **Named credits in italics** turn a status update into a mirror of the room; people see
  themselves in it.
- **Every "we heard" maps to a "we'll do"** — feedback without a matching commitment reads hollow.
- **Specifics over slogans.** Naming the actual mechanism (a pattern, a protocol, a doc link)
  signals that the author absorbed the detail — this *is* the knowledge-based voice.
- **Peer-to-peer framing** ("How we work together", not "How to plug in") — partners, not vendors.
- **Trimmed effusive gratitude** — a peer-exec audience reads over-thanking as weak; one genuine
  acknowledgment beats three.
- **Removed anything already delivered** so the message stays current with the thread.

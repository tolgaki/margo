# Keep up relationships and 1:1s

**Preconditions:** complete [setup](setup-and-migration.md) and confirm the Work IQ connection.

## One-on-ones

**What this helps you do:** stop reconstructing a 1:1 agenda five minutes before the meeting. A
rolling, per-person agenda accumulates through the week and renders cleanly when the meeting is
near.

**Before you start:** Margo detects recurring 1:1s from your calendar (two attendees, a stable
title, recurrence) — no separate setup is required, though a filled `preferences.md` → People
section helps it recognize who's who.

**Try it:**

> What's on the agenda with Dana?

> Add this to my 1:1 with Rafa: ask about the Q3 headcount plan.

**What you will see:** a card with the standing agenda, items carried over from last time, items
added since, and asks in both directions — each with a source link and a suggested next step
(discuss, resolve, or convert to a tracked commitment).

**What needs your decision:** converting a discussion topic into a tracked obligation ("add that
to commitments") needs your explicit confirmation — the agenda file itself is just a discussion
surface, not a second commitments ledger.

**Change your mind:** edit the person's agenda file directly (it's plain Markdown), or ask Margo
to drop, carry, or resolve any item; nothing here needs an undo procedure because it's always
human-editable.

**Your data:** each person's agenda lives as a Markdown file under your private `state/`
directory (`state/one-on-ones/{person-slug}.md`) — readable and editable directly, gitignored,
and excluded from installer payloads.

**If something goes wrong:** an empty agenda is a real, legitimate signal — Margo will suggest
shortening or skipping the meeting rather than manufacturing filler topics.

**Availability:** implemented, procedure. Since 1.0.0.

## Relationships

**What this helps you do:** notice, gently, who you meant to stay in touch with and haven't —
before it becomes awkward — without turning it into a scoreboard or ranking people.

**Before you start:** Margo seeds a roster from `preferences.md` → People with default cadences
(manager weekly, directs weekly, peers monthly, and so on); review and correct it once, since the
defaults are a starting point, not a judgment.

**Try it:**

> Who haven't I spoken to in a while?

> Am I neglecting anyone I should be checking in with?

**What you will see:** at most five names, worst drift first, each with why it matters now and one
concrete option — book time, drop a note, raise it at an existing meeting, or adjust the cadence
because it was set wrong. If nothing has drifted, Margo says one line and stops.

**What needs your decision:** this never books a meeting or drafts a note itself — each option
hands off to [calendar scheduling](calendar-management.md#calendar-scheduling) or
[drafting](drafting-and-follow-ups.md#drafting) as its own separate proposal.

**Change your mind:** mute a person permanently (cadence `none`), correct a cadence, or dismiss a
name for now — edit `state/relationships.md` directly, or ask Margo to do it.

**Your data:** the roster and last-contact tracking live in
`state/relationships.md`, a plain Markdown table you can read and edit directly — same gitignored,
non-distributed location as the 1:1 agendas above.

**If something goes wrong:** "no contact on record" is exactly what it says — evidence may exist
somewhere Margo can't see (a hallway conversation, an external call), and it will phrase it that
way rather than claiming certainty. It never surfaces drift about someone inside a message to
that same person.

**Availability:** implemented, procedure. This is the most intrusive routine in the skill by
design — ambient only, never interrupts, capped at a handful of names a week. Runs on request,
and folded into the weekly [ambient scan](automation-health.md#automation-ambient). Since 1.0.0.

## Advanced reference

Both routines are described in
[One-on-One Agendas](../../skills/chief-of-staff/references/one-on-ones.md) and
[Relationships — cadence and drift](../../skills/chief-of-staff/references/relationships.md).
Neither has a dedicated CLI — both use plain, human-editable Markdown state files under
`state/`, and any commitment or message that comes out of a conversation flows through the usual
[work ledger and action desk](commitments-and-action-desk.md).

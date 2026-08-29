# Walkthroughs

Four end-to-end runs, from the request to the send. Each one shows the Work IQ calls underneath,
where the approval gate sits, and what actually gets written down afterwards.

The illustrative names and content are fictional.

- [1. The morning brief](#1-the-morning-brief)
- [2. Finding time — and paying for it](#2-finding-time--and-paying-for-it)
- [3. Drafting and sending the reply](#3-drafting-and-sending-the-reply)
- [4. Calendar hygiene](#4-calendar-hygiene)

---

## 1. The morning brief

> **"Margo, brief me."**

### What happens

**Read `preferences.md`** — working hours, VIPs, protected focus blocks, what to always surface.
Everything downstream is filtered through it.

**Pull the skeleton in parallel** — one tool block, four `workiq-fetch` calls, every one bounded:

```
/me/calendarView?startDateTime=…&endDateTime=…
  &$select=id,subject,start,end,organizer,attendees,isAllDay,onlineMeeting,location,webLink
  &$top=50

/me/messages?$filter=isRead eq false&$orderby=receivedDateTime desc
  &$select=id,subject,from,receivedDateTime,isRead,flag,toRecipients,bodyPreview,webLink
  &$top=50
```

…plus Teams mentions and DMs, and anything due. Note `isRead` + `receivedDateTime` — the one mail
filter+sort pair an index actually backs. Adding `flag/flagStatus` here would return
`400 InefficientFilter`.

**Synthesize with one to three `workiq-ask` calls** — what's top of mind, what changed since
yesterday, what needs a response today. Not one giant prompt, and not a serial chain.

**Read `commitments.md`** for the waiting-on section. It's rendered from the file, never from
memory — that's the whole point of the file existing.

### What comes back

```
☀️ Daily Brief — Thursday, 28 August

🎯 Top priorities today
  1. Reply to Dana on the parity numbers — she's blocked, asked Tuesday → draft ready
  2. Q3 review deck — due Friday, no draft yet → block 90 min, it's the only gap you have

📅 Calendar (6 meetings, 09:05–16:30)
  • 09:05 Platform sync — you're presenting, no prep time before it ⚠️
  • 11:05 1:1 with Marco — three items carried over from last week
  ⚠️ Four back-to-backs 11:05–15:00. Zero prep time before the 09:05.

📥 Needs your response (3)
  • Dana — "Parity baseline" — asking for a decision on the 30 Sep date → reply  [Email]
  • Ines — "Consent model review" — wants you on Thursday's read-out → decline, Marco covers it

⏳ Waiting on others
  • Pricing confirmation from Rafa — asked 19 Aug, nine days, no reply → chase?

Four back-to-backs and you're presenting cold at 09:05. Want me to move the 11:05?
```

### Why it looks like that

Every line ends in a verb — *reply, decline, chase, block*. A brief that only describes leaves you
doing the deciding, which is the expensive part. Every line is also one click from its source via
the `webLink` that was `$select`ed on the way in.

And it ends with **one** question, not five. If everything is a question, nothing is a
recommendation.

---

## 2. Finding time — and paying for it

> **"Find 30 minutes with Dana this week."**

This is the walkthrough worth reading closely, because the naive version of it is a slot-finder
and the useful version is a schedule manager.

### The difference

A slot-finder says: *"Thursday 14:00 works if you move your 1:1."* Then leaves you to move the
1:1, re-home whoever it displaces, and message the people affected. That's half a job handed
back.

Margo is configured — in `preferences.md` — to **work out what has to shift, cost it, and bring
the whole plan.**

### What happens

**Resolve the person.** `workiq-fetch` against people lookup for the exact address. Check
`preferences.md` → People first. Never `ask` for this — you need an identifier, not a synthesis.

**Check availability.** The user's own `calendarView` over the candidate window; Dana's via
`getSchedule` / `findMeetingTimes` through `workiq-do_action` / `workiq-call_function`.

> **Free ≠ available.** Free/busy is all you can see for someone else. You cannot see what their
> time is *for*. A "free" block is a candidate, not a fact — and Margo says so rather than
> presenting it as certainty.

**Compute the cascade.** Apply the priority ladder from `preferences.md`:

| | |
|---|---|
| **Never move** | Your manager, your skip, external customers, anything where you're the named DRI |
| **Move freely** | Your 1:1s with directs, working sessions you organize, your own blocks |
| **Drop, don't move** | Large FYI meetings you're tentative on and not presenting at — declining is cheaper than reshuffling |
| **Ask before touching** | Anything with someone in a distant time zone, where the daily overlap is an hour |

**Show the whole damage.** No move cap. If it costs six moves, say six.

### What comes back

```
Two options. Neither is free.

A — Thursday 14:05, costs one move
  • Your 1:1 with Marco 14:00 → 15:35 (you organize it; he's free)
  • Nothing else displaced

B — Wednesday 10:35, costs three moves
  • Design review 10:30 → Thu 11:05
  • …which displaces the eng sync → Thu 15:35
  • …which lands on Marco's 1:1 again — second move for him this week ⚠️

A. B moves Marco twice in a week, which makes your calendar look unstable to your own team.

Book A?
```

### The approval structure

Three distinct levels, and this is where most of the design lives:

| Level | What | Approval |
|---|---|---|
| **Decide and act** | Finding slots, reading free/busy, computing the cascade, drafting the invite | None — just do it |
| **One approval per plan** | Moving meetings **you organize**, including a multi-step cascade. One yes covers the whole named set | One explicit yes to *that* set of moves |
| **Always per-action** | Proposing a new time to someone else, declining or moving a meeting **you don't own**, cancelling, messaging attendees | Every time, specifically |

The middle row is **not** a standing grant to rearrange your calendar. One yes covers one
specific, already-shown set of moves. A new request needs a new plan and a new yes.

**Recurring meetings:** single occurrence only, never the series, unless you say "the series".

### Then the invite body

`workiq-create_entity` on `/me/events` — but the body gets the same care as any outbound message,
because that's what it is. Margo writes it **for the person receiving it**:

- ❌ Never expose scheduling mechanics — cadence math, rotation logic, *why this slot won*, or
  that their calendar was inspected.
- ❌ **Never name other attendees or their separate meetings.** Telling someone they're one slot
  in a rotation tells them they're interchangeable. This is the most damaging leak and the most
  common.
- ❌ Never ship `{name}`-style template tokens. Write each invite separately, even near-identical
  ones — a mail-merge tell undercuts the whole gesture.
- ✅ Lead with *why this time exists*. Say what to bring. Hand them control over the time and
  cadence — a standing invite from someone senior reads as a summons unless one sentence prevents
  it.

Then the perspective check, before you ever see it: *if they forwarded this to a peer, would
anything embarrass either party? Does any line describe scheduling rather than the conversation?
Would they guess an assistant generated it?*

---

## 3. Drafting and sending the reply

> **"Draft the reply to Dana."**

### What happens

**Load voice** from `preferences.md` — tone, sign-off, length, do/don't.

**Gather real context.** `workiq-fetch` the exact thread so you have the actual content,
participants and the specific ask being answered. `workiq-ask` to summarize if it's long;
`workiq-retrieve` for citable related context from other mail, meetings or docs.

Every fact, date, link and number in the draft comes from that retrieval. Nothing is inferred to
make the draft read well.

**Present it in a block:**

```
✉️ Draft — Email reply
To: dana@example.com
Subject: RE: Parity baseline
─────────────────────────────
Dana — 30 Sep still holds. The baseline gap is parity work, not a
regression, and it's tracked as ENG-0042.

Two things from me by Tuesday: the updated parity numbers, and a
call on whether the eval set moves with it.

If that slips, I'll tell you Monday rather than Tuesday.

/ac
─────────────────────────────
Send as-is, edit, or discard?  (I won't send until you say so.)
```

### The voice boundary

This is the hard line in the whole system.

**Margo's personality stops at the edge of that block.** The commentary around the draft — the
recommendation, the caveat, the pointed question — is hers. The text *inside* the block is
**yours**, per `preferences.md`. You're signing your name to it; a recipient should never detect
an assistant's wit in it.

Note the sign-off is the user's own initials — here `/ac` — and there's **no assistant
attribution**. That's a deliberate default:
you approved the message, so it's your word, and an "AI Chief of Staff" line invites the reader to
discount it. Teams messages get no sign-off at all. Calendar invites *do* name the assistant —
they're logistics sent to people who didn't ask for them, and the line explains why it landed.

### On approval

Only now does anything leave. `workiq-create_entity` persists the draft, `workiq-do_action` sends
it — as a separate, approved step.

> "Draft" in Work IQ means a **persisted draft** you can open in Outlook. Inline suggested wording
> does not satisfy a drafting request.

**Then the loop closes.** The message promised parity numbers by Tuesday. That's a commitment, and
it goes in `commitments.md` with owner, due date and source link — with your approval, like
everything else:

```markdown
## 🔴 I owe (open commitments)

| What I committed to | To whom | Due | Source | Notes |
|---|---|---|---|---|
| Updated parity numbers | Dana | 2026-09-02 | RE: Parity baseline ([link]) | Also owes a call on the eval set |
```

Which is what makes tomorrow's brief say *"due tomorrow, still open"* instead of forgetting it
happened.

---

## 4. Calendar hygiene

> **"How's my calendar looking?"**

The other calendar modes act on one meeting. This one judges the whole thing and tells you what
it costs.

**Pull a real window** — last week and the next two, not today. `calendarView` requires both
`startDateTime` and `endDateTime` or it 400s. Page until the window is genuinely complete: a
truncated pull understates the problem, and understating it makes the exercise pointless.

**Compute, don't impress.** Hours, not adjectives:

```
📉 Calendar hygiene — last week + next two

  31h in meetings (78% of working hours) · 9h optional · 1 focus block ≥90min

  Worth cutting:
  • Platform office hours — weekly, 24 attendees, you're optional — 4h/month
    → decline the series
  • Partner triage — no agenda, 6 instances, you've declined 4 → drop?

  Your own:
  • Weekly eng sync — you organize, 11 attendees, last 3 instances thin → still needed?
```

**"One focus block of 90+ minutes"** is usually the most damning number available, and it lands
where "you have a lot of meetings" does not.

Note the last section. Margo names **your own sprawl first** — the recurring meetings you
organize that have outlived their purpose. It's easier to hear about your own meeting than
someone else's, and it earns the right to raise theirs.

**Then: propose, never act.** Declining a recurring series is highly visible to the organizer. It
needs per-action approval, always, and no standing authorization covers it. For a series, Margo
shows exactly what the organizer sees, and whether it declines one instance or all of them —
getting that wrong is a genuinely embarrassing mistake.

---

## The shape they share

Every walkthrough above follows the same four beats:

1. **Bounded reads** — `fetch` with `$select` and `$top`, in parallel, so context goes on
   judgement rather than payload.
2. **A recommendation, not a menu** — lead with the call, attach the cost, cite the source.
3. **One explicit approval gate**, at the exact point something becomes visible to another person.
4. **Write it down** — commitments, decisions and surfaced-item state persist to disk, because
   the next session starts with no memory of this one.

Next: **[Personalization](personalization.md)** for tuning the ladder and the voice to you, or
**[Proactive & scheduled](proactive.md)** for running all of this without being asked.

# Preferences — {Your name}

> **This file is a template.** Edit it to teach your Chief of Staff how you work. The skill reads
> it at the start of every routine. Anything left as a placeholder falls back to a sensible
> default, so it works out of the box and gets sharper as you fill it in.
>
> Everything below in `{curly braces}` is a placeholder. The examples are illustrative and
> deliberately fictional — replace them with your own.
>
> ⚠️ **This file will contain real names, addresses, and internal identifiers once you fill it in.**
> Keep your working copy out of version control (see `.gitignore`), or keep it in a private repo.

## About me

- **Name / preferred name:** {name}
- **Role / team:** {what you own, who you report to, who reports to you}
- **Time zone & working hours:** {e.g. 9:00–18:00, PT}
- **Focus-time blocks to protect:** {e.g. no meetings before 10am; heads-down 2–4pm}

## Scheduling defaults

- **Meeting start times:** {e.g. always start at :05 or :35 past the hour, never :00 or :30, so
  there's buffer between back-to-backs. A 25-min meeting therefore runs :05–:30 or :35–:00.}
- **Default duration:** {e.g. 25 min, or 55 min for longer sessions, to preserve the buffer}
- **Meeting invite sign-off:** {optional — an exact block appended verbatim to the body of every
  invite created or updated on your behalf. Example:}

  ```
  Margo,

  {Your name}'s AI Chief of Staff
  ```

  Naming the assistant on an invite is worth doing even if you don't sign emails that way:
  invites are logistics sent to people who didn't ask for them, and the line explains why it
  landed and who to reply to about timing.

### Act as my schedule manager, not a slot-finder

Don't hand me a slot that only works if I move something and then leave the moving to me. That is
half a job. Work out what would have to shift, cost it, and bring me the whole plan.

**Authority levels.** These are about *granularity of approval*, not a standing grant:

| Level | What | Approval |
|---|---|---|
| **Decide and act** | Finding slots, reading free/busy, computing cascades, drafting invites and notes | None — just do it |
| **One approval per plan** | Moving meetings **I organize**, including a multi-step cascade. Present the whole plan; I say yes once and you execute all of it | One explicit yes to that named set of moves |
| **Always per-action** | Anything that sends or is irreversible: proposing a new time to someone else, declining or moving a meeting **I don't own**, cancelling, messaging attendees | Explicit approval of that specific action, every time |

The middle row is **not** a standing authorization to rearrange my calendar whenever you think
best. It means one yes covers one specific, named, already-shown set of moves. A new request
needs a new plan and a new yes.

**Priority ladder — what yields to what.** Fill in your own; the categories matter more than the
examples:

| | |
|---|---|
| **Never move** | {your manager, your skip, named execs} · {external customer meetings} · {anything where you're the named DRI} |
| **Move freely** | {your 1:1s with directs} · {working sessions you organize} · {lunch, wrap-up blocks, your own focus blocks} |
| **Drop, don't move** | {large FYI meetings you're tentative on and not presenting at — declining is cheaper than reshuffling} |
| **Ask before touching** | {anything with someone in a distant time zone, where the working overlap is an hour a day — never shuffle without asking} |

**No move cap. Show me the whole damage.** If a request costs six moves, say it costs six moves
and let me decide it isn't worth it. Never quietly stop at two and offer me something worse —
that hides the cost rather than avoiding it. Rank the options by what they cost, lead with the
cheapest, and say plainly when something isn't worth the disruption.

**Hard rules when moving:**

- **Single occurrence only.** Never move a whole recurring series unless I say "the series".
  Recurring events need the occurrence handled explicitly, not the master.
- **Re-home what you displace.** A bumped meeting is not handled until it has a new slot or I've
  agreed to drop it. If its attendees can't be re-solved — realistically anything past four or
  five people — say so rather than leaving it homeless.
- **Free ≠ available.** Free/busy is all you can see for other people; you can't see what their
  time is for. Treat a "free" block on someone else's calendar as a candidate, not a fact.
- **Watch the churn.** Every move is visible to whoever is on that meeting. Frequent reshuffling
  makes my calendar look unstable to my team. Flag it when a request would move the same person's
  meeting twice in a week.

**When you can't move it:** propose an alternative time to the other party — as a draft for my
approval, never sent directly. For meetings I don't organize, prefer Outlook's propose-new-time
over asking me to decline.

## Priorities & projects

- **Current top priorities (this week/quarter):** {…}
- **Active projects (name → what it is → who's involved):** {…}
- **Things I'm waiting on / blocked by:** {…}

### Work tracking — Azure DevOps

{Optional. Delete this section if you don't use ADO.} Bugs and backlog live in
**{org} / {project}**. Queries, commands, and the gotchas are in **`references/work-items.md`** —
read it whenever I ask to add, update, or review the backlog or bugs.

- **ADO organization:** `https://dev.azure.com/{your-org}`
- **Project:** `{Your Project}`
- **Bugs:** saved flat query `{query-guid}` — *"{query name}"*.
- **Backlog:** saved tree query `{query-guid}` — *"{query name}"*, rooted at epic `{id}`.

### Community — Viva Engage

{Optional. Delete if you don't own a community.} If you own a product, its Engage community is
your shop window. Sweep it for unanswered customer questions, recurring themes, and drift in how
people understand the product. Procedure and the parser are in **`references/engage.md`**.

- **Community:** {name} — GroupId `{group-guid}`.
- **Standing concern:** an unanswered question in a public community is an open commitment.
  Surface those first, and tell me when a recurring complaint has no matching bug.

### Community — feedback channel (Teams)

{Optional.} The **{channel name}** channel is the second half of the same patch, and often the
sharper one: it's where people report what's actually broken. Same intent, same standing concern.
Procedure in **`references/teams-feedback.md`**.

- **Team:** `{team-guid}` — **Channel:** `{19:...@thread.tacv2}`.
- Treat Engage and this channel as one community view when briefing me; say which surface each
  item came from.

## People

> **Last verified: {YYYY-MM-DD}.** Org data goes stale — when this date is more than a quarter
> old, offer to refresh the chains below via Work IQ (`workiq-fetch` on `/users/{id}/manager` and
> `/users/{id}/directReports`) and update the date.

- **VIPs (always surface, respond fast):**
  - **Management chain (up from me):**
    - {Name} — {email} — {title} — **my direct manager**
    - {Name} — {email} — {title} — **skip**
    - {Name} — {email} — {title} — **skip+2**
  - **My manager's peers:** {name (alias) title; …}
  - **Other VIPs:** {name — email — title — why they matter}
- **My directs / team:** {name (email) — title; …}
- **Auto-lower-priority senders:** {newsletters, no-reply, automated systems}

## Communication & drafting voice

- **Default tone:** {e.g. warm but concise; direct; friendly-professional}
- **Signature / sign-off:**
  - **Email** — {e.g. initials only, and **no assistant attribution**. You approve every message
    before it sends, so it's your word — an "AI Chief of Staff" line invites the reader to
    discount it.}

    ```
    {your sign-off}
    ```

    Applies to new emails, replies, and forwards. If you want the attribution on a specific
    message, ask for it on that message.
  - **Teams** — {e.g. no sign-off at all. Chat is conversational; signature blocks on short
    messages read as machine-generated.}
  - **Calendar invites** — {e.g. keep the assistant block, see § Scheduling defaults}
- **Length preference:** {e.g. 3–5 sentences; bullet points for asks}
- **Do / don't:** {e.g. don't use exclamation marks; always propose a concrete next step; no
  corporate jargon}
- **Languages:** {…}

### Executive comms voice (for board / partner / leadership follow-ups)

The voice itself — rubric, structure, self-check, and gold-standard exemplar — is defined
canonically in **`references/exec-followup.md`**. This section holds only *your personal
deviations* from that rubric:

- **Deviations / additions:** {e.g. "never open with thanks", "always cc my chief of staff",
  or none}

## Standing rules for triage

- **Always flag:** {e.g. anything from my manager, anything with "urgent"/a deadline, external
  customer escalations}
- **Auto-deprioritize / read-later:** {e.g. FYI CCs, newsletters, calendar spam}
- **Meetings I'll usually decline:** {e.g. optional, no agenda, conflicts with focus time}
- **Delegate to:** {who handles what}

## Daily brief preferences

- **When I want it:** {e.g. every weekday 8:00am}
- **How deep:** {e.g. top 5 priorities + calendar + needs-response only}
- **Always include / never include:** {…}

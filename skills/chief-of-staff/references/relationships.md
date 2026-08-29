# Relationships — cadence and drift

People fall off the calendar quietly. Nobody sends a meeting request titled "we haven't spoken in
seven weeks". This file tracks who the user should be in contact with, how often, and which of
those have gone quiet — then says so, plainly, before it becomes awkward.

This is the most intrusive thing the skill does. Handle it accordingly: it is **ambient only**,
it never interrupts, and it surfaces at most a handful of names a week.

## The state file

`state/relationships.md` — a markdown table the user can read and edit directly. Markdown, not
JSON, because the user should be able to correct a cadence without a tool.

```markdown
# Relationship cadence

| Person | Email | Relationship | Cadence | Last contact | Last 1:1 | Notes |
|---|---|---|---|---|---|---|
| {Name} | {email} | Manager | weekly | {YYYY-MM-DD} | {YYYY-MM-DD} | |
```

- **Relationship**: Manager / Skip / Direct / Peer / Exec / External / Partner.
- **Cadence**: the intended contact interval — `weekly`, `2 weeks`, `monthly`, `quarterly`,
  or `none` to mute a person permanently.
- **Last contact**: any real two-way exchange — mail, Teams, or a meeting they both attended.
- **Last 1:1**: an actual scheduled conversation. Tracked separately because a flurry of email
  is not a relationship.

Seed it from `preferences.md` → People (VIPs, management chain, directs) with default cadences:

| Relationship | Default cadence |
|---|---|
| Manager | weekly |
| Direct report | weekly (1:1) |
| Skip-level (up) | monthly |
| Skip-level (down) | quarterly |
| Peer / exec peer | monthly |
| External / partner | quarterly |

Defaults are a starting point, not a judgment. Offer them once, then let the user correct.

## Procedure

1. **Read** `state/relationships.md` and `../preferences.md`. If the state file doesn't exist,
   propose seeding it from People and stop — don't guess a roster and start measuring against it.

2. **Refresh last-contact** for each tracked person. Batch these; don't loop one `workiq-ask` per
   person, which would be dozens of slow calls.
   - `workiq-retrieve` per person for recent two-way exchange, or
   - `workiq-fetch` `/me/messages` with `$orderby=receivedDateTime desc`,
     `$select=id,subject,from,toRecipients,receivedDateTime,webLink`, `$top=50`, matched locally
     against the roster. **Do not `$filter` on `from`** — it returns `400 InefficientFilter`.
   - Meetings: `/me/calendarView` over the window (mandatory `startDateTime`/`endDateTime`;
     it 400s without them), `$select=id,subject,start,attendees,organizer,webLink`. An attended
     meeting counts as contact; a declined one does not.

   **A large meeting is not contact.** Being in the same 30-person review as someone is not a
   relationship. Count a meeting only when it has roughly 5 or fewer attendees, or the user
   organized it.

3. **Compute drift** = working days since last contact, against cadence. Classify:
   - **On track** — within cadence. Say nothing.
   - **Drifting** — 1.5× cadence. Worth a mention.
   - **Drifted** — 2× cadence or more. Worth doing something about.
   - **Cold** — 3× or more, or no contact on record at all. Name it plainly.

4. **Apply the mute rules before surfacing anything.** Do not report drift for anyone who is:
   - on extended leave, or the user is,
   - cadence `none`,
   - dismissed for this person in the last 30 days (record it in Notes),
   - someone whose relationship has genuinely ended — a finished project, a reorg. Ask whether
     to remove them rather than reporting them cold forever.

5. **Promote on crossing, not on state.** A person who has been drifted for a month is not news
   every week. Surface a name the *first* time it crosses a band; then not again until it crosses
   the next band. Dedupe id: `person:{email}:{band}`.

6. **Render, capped.** Maximum five names, worst first. Each line ends in a concrete option:
   book a 1:1, drop a note, raise it at an existing meeting, or adjust the cadence because the
   real answer is that the intended cadence was wrong.

7. **Offer the fix, don't perform it.** Scheduling hands off to `calendar.md`; a message hands
   off to `drafting.md`. Both come back as proposals.

## Render card

```
🔗 Relationships — {n} drifting

  • {name} ({relationship}) — {n} weeks since {last contact type}, cadence {cadence}
    {what they're involved in / why it matters now}
    → {book 30 min / raise at {existing meeting} / drop a note / adjust cadence}

  Cold: {name}, {name} — no contact on record this quarter.
```

If nothing has drifted, say one line and stop. Do not list the healthy relationships as filler.

## Notes

- **This can read as surveillance.** Frame it as the user's own intent slipping — "you wanted
  monthly with {name}; it's been ten weeks" — not as a scoreboard of neglect. Never rank
  people, never score them, never speculate about *why* contact stopped.
- **Cadence is aspiration, not obligation.** Repeated drift on the same person usually means the
  cadence was wrong. Offer to change it — that's a legitimate and often correct resolution.
- **Never surface drift about someone in a message to that person.** Obvious, and worth writing
  down: this is input for the user's judgment, and it must never leak into a draft.
- **Absence of evidence is not absence of contact.** They may have talked in a corridor, on a
  call, or on a surface the assistant can't see. Always phrase as "no contact *on record*", and
  accept the user's correction into Notes without argument.
- **Don't track everyone.** A roster of 200 names produces noise and a slow scan. Cap it at the
  people whose drift would actually matter — VIPs, directs, key partners. Prune the rest.

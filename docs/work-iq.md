# How Margo uses WorkIQ

WorkIQ is the Microsoft 365 surface Margo runs on: an MCP server that exposes mail, calendar,
Teams chats and channels, OneDrive/SharePoint documents, meeting recaps and the people directory
to an agent — for reading *and* for writing.

This document is about the part that isn't obvious from the tool list: **which tool to reach for,
how to keep payloads small enough that judgement survives, and what to do when a call fails.**
Those three things are the difference between a brief that arrives in twenty seconds with a
recommendation on every line, and one that arrives in three minutes having spent its whole context
window on JSON.

> In Copilot CLI every tool is exposed with a `workiq-` prefix. Always call the full prefixed name
> (`workiq-fetch`, not `fetch`) — unprefixed names are not callable.

---

## The three retrieval tools

Getting this choice wrong is the single most common failure, and it's not a two-way split.

| Tool | Use when | Cost |
|---|---|---|
| **`retrieve`** | **Semantic find across M365.** The user describes what they want and you have no exact path — "emails about launch risk", "recent PDFs", "what's been shared with me". Returns ranked hits **with `webLink` and sensitivity labels**. One call is usually the whole answer. | Fast |
| **`fetch`** | **Literal lookup** of structured data with a knowable path and filter — today's `calendarView`, unread `messages`, a message by ID, channels in a team. Also the only way to resolve an exact ID before acting. | Sub-second |
| **`ask`** | **Synthesis and reasoning** across many sources — "what was decided and why", "summarize the thread", "what's top of mind". Pass `timeZone`. | 10–60s, minutes when broad |

Three rules that follow from the table:

- **Never put `ask` in a loop**, and never use it for a literal lookup. It reasons; it does not
  enumerate.
- **"How many" / "all" / "every" → `fetch`, never `retrieve`.** `retrieve` returns ranked
  semantic hits, not a complete set, so its hit count is not an answer to a counting question.
- **Resolve IDs with `fetch`, never with `ask`.** Before any write you need the exact event,
  message or person ID. A synthesized answer is not an identifier.

Margo's default shape for a brief is therefore: **`fetch` in parallel to enumerate the skeleton,
then one to three focused `ask` calls to work out what it means.** Enumeration and judgement are
different jobs and use different tools.

### The other tools

| Tool | For |
|---|---|
| `call_function` | **Delta** endpoints — "what changed since…". Delta is `call_function` only, never `fetch`. |
| `do_action` · `create_entity` · `update_entity` · `delete_entity` | Sending, replying, scheduling, RSVPing, marking read. **Only after explicit approval.** |
| `get_schema` · `search_paths` | Discover required fields and valid paths *before* any create or update. |
| `fetch_blob` | Binary content up to 4 MB. Larger files go through `scripts/m365_files.py` — see [large files](#large-files). |

---

## Payload discipline

A daily brief touches today's calendar, unread mail, Teams mentions, open commitments and
whatever changed overnight. Fetch all of that unbounded and the context window fills with payload
that crowds out the synthesis you actually wanted.

Every `fetch` against a collection in this repo obeys four rules:

**1. Always pass `$select`.** Only the fields you need. The defaults Margo uses:

```
messages → id,subject,from,receivedDateTime,isRead,flag,toRecipients,bodyPreview,webLink
events   → id,subject,start,end,organizer,attendees,isAllDay,onlineMeeting,location,webLink
```

`webLink` is on both lists deliberately — see [citations](#citations-are-not-decoration).

**2. Always pass `$top`.** 25–50 is usually plenty. A few endpoints reject it (for example
`/me/chats/{id}/members`); omit it there.

**3. Filter server-side only where an index backs the pair.** On mail, `isRead` +
`receivedDateTime` is safe. **`flag/flagStatus`, `from`, `importance` and `hasAttachments` are
not** — filtering on them returns `400 InefficientFilter`.

The recovery has a trap in it:

> Keep `$orderby=receivedDateTime desc`, **drop the `$filter`**, and narrow locally.
> Never do the reverse. A `$filter` with no `$orderby` returns **oldest-first**, which yields a
> silently stale brief with no error to notice.

**4. Convert relative dates before they reach a filter.** "This week" and "since yesterday" are
not queryable. Resolve them to explicit ISO datetimes first.

Beyond the four: fetch full message bodies only for the handful of items you're actually drafting
against, and issue independent fetches **in parallel in a single tool block** rather than serially.

### Documented exceptions win

Where a reference file records an exception, it overrides the general rule. The clearest example
is Teams channel messages, where passing `$select` for `body` or `reactions` returns
**500 InternalServerError** — so that collection is fetched whole and slimmed locally. That's
recorded in `references/teams-feedback.md`, next to the pagination constraint that
`$skiptoken` is rejected and you must walk `lastModifiedDateTime` forward instead.

These are the kind of details that are expensive to rediscover. Writing them down next to the
procedure that needs them is most of what the `references/` directory is for.

---

## Citations are not decoration

Every line Margo produces names where it came from — sender and subject, meeting title and time,
channel name, document title — and carries the `webLink` so the user is one click from the source.

This is a correctness mechanism, not a courtesy. An assistant that summarizes without citing is
asking to be trusted; one that cites is asking to be checked. For a brief you act on before
09:00, the second is the only defensible design.

Two related rules:

- **Check `sensitivityLabel` before quoting.** `retrieve` returns it. Reproducing labelled
  content into a summary the user might forward is how a label gets laundered off a document.
- **An empty result is `unknown`, not `zero`.** A failed page, a rate limit, or a parser warning
  means you didn't find out. Reporting it as "nothing found" is a fabrication with extra steps.
  If a bundled script prints `WARNING`/`PARTIAL` or exits non-zero, that goes in the read-out.

---

## Writing: where the care goes

WorkIQ writes **execute immediately**. There is no staging, no preview, no undo. A send, a
decline, a reaction or a `permanentDelete` is instantly visible to other people or unrecoverable.

That single property is why this repo's central rule exists:

> **Propose, never act unilaterally.** Present the exact draft or the exact field change, and wait
> for explicit approval of *that specific action*.

Two WorkIQ-specific notes that shape how Margo drafts:

- **"Draft" means a persisted draft.** Inline suggested wording does not satisfy a drafting
  request — the user must be able to open it in Outlook. Margo creates the draft entity, then
  sends it as a separate approved step.
- **Tasks are M365 data.** "Add a task" / "remind me" routes to Planner or To Do through WorkIQ.
  It never gets satisfied with a local file or an in-session list.

See **[Trust & safety](safety.md)** for the full approval model, including which actions can be
covered by a standing instruction and which never can.

---

## When WorkIQ says no

Failures here are mostly *informative*, and treating them as transient is the mistake.

| Symptom | Meaning | Do |
|---|---|---|
| `400 InefficientFilter` | No index backs that filter+sort pair | Drop the `$filter`, keep `$orderby`, narrow locally |
| `Access denied for path: X` | The tenant has disabled that path family server-side | **Don't retry, don't reroute, don't fall back to `ask`.** Tell the user the path is not available in their tenant |
| `400` on `calendarView` | Missing `startDateTime` / `endDateTime` | They're mandatory — supply both |
| Empty tree from a CLI query | Often a tool limitation, not an empty result | Verify by another route before reporting zero |
| `tool does not exist` | Missing the `workiq-` prefix, or the tool isn't released | Use the full prefixed name; check the tool list |

`/me/todo/*`, `/me/contacts` and `/me/outlook/masterCategories` writes are commonly denied at the
tenant level. Also worth knowing: directory users and personal contacts are **separate stores with
incompatible IDs** — a person found via people search cannot be updated as `/me/contacts/{id}`.

For deeper troubleshooting, load the `workiq` skill and read its `references/troubleshooting.md`.

---

## Large files

`fetch_blob` caps at **4 MB**, and WorkIQ cannot accept raw byte uploads. For anything larger,
this repo ships `skills/chief-of-staff/scripts/m365_files.py` — a small bridge that authenticates
with the same identity your WorkIQ MCP connection already uses (it reads the client ID from the
MCP's own OAuth config) and streams files to local disk.

```bash
python3 scripts/m365_files.py auth --account you@example.com
python3 scripts/m365_files.py status
python3 scripts/m365_files.py download --drive <driveId> --item <itemId>
```

Download works today. Server-side copy and upload are written and waiting on one thing: the
public client needs `Files.ReadWrite.All`, which its consented Graph scopes don't currently
include. `status` reports `can_write_files` so you can check rather than guess. Full detail in
`skills/chief-of-staff/references/files.md`.

---

## Where to look next

- **[Walkthroughs](walkthroughs.md)** — the tools above in sequence, from finding a slot to
  sending the email.
- **[The chief-of-staff playbook](chief-of-staff.md)** — which routine uses which tools.
- **[Proactive & scheduled](proactive.md)** — how unattended runs use delta cursors so they don't
  repeat themselves.

# Work IQ patterns: tool choice, payloads, citations and writes

Work IQ is the Microsoft 365 surface Margo runs on: one MCP server that exposes mail, calendar,
Teams chats and channels, OneDrive and SharePoint documents, meeting recaps and the people
directory to an agent, for reading *and* for writing. [What Work IQ is and how to load it](README.md)
covers the server itself; this page covers the part that isn't obvious from the tool list:
**which tool to reach for, how to keep payloads small enough that judgment survives, and how to
treat citations and writes.** Failures have their own page: [errors and recovery](errors.md).

Those choices are the difference between a brief that arrives in twenty seconds with a
recommendation on every line, and one that arrives in three minutes having spent its whole context
window on JSON.

> This repository uses names such as `workiq-fetch`. The callable name comes from the host's
> registered MCP server, not the skill folder. Discover the tool and its current schema before
> calling it; do not assume another host exposes the same spelling or arguments.

---

## The three retrieval tools

Getting this choice wrong is the single most common failure, and it's not a two-way split.

| Tool | Use when | Cost |
|---|---|---|
| **`retrieve`** | **Semantic find across M365.** The user describes what they want and you have no exact path — "emails about launch risk", "recent PDFs", "what's been shared with me". Returns ranked hits **with `webLink` and sensitivity labels**. One call is usually the whole answer. | Fast |
| **`fetch`** | **Literal lookup** of structured data with a knowable path and filter — today's `calendarView`, unread `messages`, a message by ID, channels in a team. Also the only way to resolve an exact ID before acting. | Sub-second |
| **`ask`** | **Synthesis and reasoning** across many sources — "what was decided and why", "summarize the thread", "what's top of mind". Pass `timeZone`. | 10–30s |

Three rules that follow from the table:

- **Never put `ask` in a loop**, and never use it for a literal lookup. It reasons; it does not
  enumerate.
- **"How many" / "all" / "every" → `fetch`, never `retrieve`.** `retrieve` returns ranked
  semantic hits, not a complete set, so its hit count is not an answer to a counting question.
- **Resolve IDs with `fetch`, never with `ask`.** Before any write you need the exact event,
  message or person ID. A synthesized answer is not an identifier.

Margo's default shape for a brief is therefore: **`fetch` in parallel to enumerate the skeleton,
then one to three focused `ask` calls to work out what it means.** Enumeration and judgment are
different jobs and use different tools.

The relative costs above explain the retrieval strategy, not a latency guarantee. Actual
latency, supported paths, payload limits, and permissions depend on the connected host and
service. Recheck capabilities rather than treating a previously observed limitation as universal.

### The other tools

| Tool | For |
|---|---|
| `call_function` | **Delta** endpoints — "what changed since…". Delta is `call_function` only, never `fetch`. |
| `do_action` · `create_entity` · `update_entity` · `delete_entity` | Sending, replying, scheduling, RSVPing, marking read. **Only after explicit approval.** |
| `get_schema` · `search_paths` | Discover a path you don't know, or the fields of one you aren't sure of, before a create or update. Well-known writes (send mail, create an event) don't need it. |
| `fetch_blob` | Binary content up to 4 MB. Larger files go through `scripts/m365_files.py` — see [large files](#large-files). |
| `list_agents` · `ask` with `agentId` | Find the Copilot agents available to the signed-in user, then ask one of them by ID. |

The [capability map](capability-map.md) lists every tool and path family with the Margo file that
uses it and what it requires.

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

- **Check `sensitivityLabel` before quoting.** `retrieve` returns it. Reproducing labeled
  content into a summary the user might forward is how a label gets laundered off a document.
- **Incomplete evidence is `unknown`, not `zero`.** A failed page, a rate limit, or a parser warning
  means you didn't find out. Reporting it as "nothing found" is a fabrication with extra steps.
  If a bundled script prints `WARNING`/`PARTIAL` or exits non-zero, that goes in the read-out.
  A successful, fully paged enumeration with no matches establishes zero only for its stated
  scope and interval. Record that coverage separately from semantic-search results.

---

## Writing: where the care goes

Work IQ writes **execute when called**; they are not automatically staged for later approval.
Reversible operations and explicit draft creation exist, but there is no universal undo or
Margo-specific preview gate in the provider. A send, decline, reaction, or `permanentDelete`
can become visible or irreversible immediately.

That single property is why this repo's central rule exists:

> **Propose, never act unilaterally.** Present the exact draft or the exact field change, and wait
> for explicit approval of *that specific action*.

Two notes specific to Work IQ that shape how Margo drafts:

- **Name the draft's location.** The action desk can hold a versioned local proposal unattended;
  it is not an Outlook draft. A request for an Outlook draft requires an explicitly approved
  draft-creation write, and sending remains separately approved. Never call a local proposal
  an Outlook draft or a prepared artifact a published one.
- **External tasks keep their canonical store.** A request for a Planner or To Do task uses
  the supported Work IQ surface and its approval boundary. A local commitment ledger or
  bounded task run is not a substitute for creating that requested external task. Link the
  canonical ID rather than creating another competing tracker; report a denied path as blocked.

See **[writes and approvals](writes-and-approvals.md)** for the reusable pattern and
**[Trust & safety](../safety.md)** for the full approval model, including which actions can be
covered by a standing instruction and which never can.

---

## When Work IQ says no

Failures here are mostly *informative*, and treating them as transient is the mistake. A denied
path is a tenant decision, not a cue to retry, reroute or fall back to `ask`. Every observed error,
with its request shape and recovery, is in **[errors and recovery](errors.md)**.

---

## Large files

The documented `fetch_blob` transport caps at **4 MB**, and Work IQ does not accept raw byte
uploads through the entity tools. Recheck the current host's binary tool contract before choosing
a transfer route. For larger files, this repo ships
`skills/chief-of-staff/scripts/m365_files.py`, a separate Graph client that streams to local disk.
It can discover the client ID from the Work IQ MCP OAuth configuration, but performs its own
interactive sign-in; it does not reuse the MCP connection's token or prove the same user signed in.

The bridge requires **macOS Keychain** for its refresh-token store. From a default
copy installation on macOS, replace the fictional account and provider IDs:

```bash
python3 ~/.copilot/skills/chief-of-staff/scripts/m365_files.py --account you@example.com auth
python3 ~/.copilot/skills/chief-of-staff/scripts/m365_files.py --account you@example.com status
python3 ~/.copilot/skills/chief-of-staff/scripts/m365_files.py --account you@example.com download \
  --drive "{driveId}" --item "{itemId}"
```

`--account` is a global option before the subcommand. Its environment fallback is
`MARGO_M365_ACCOUNT`, not the work ledger's `MARGO_ACCOUNT`. Confirm status's returned `upn`
matches the intended principal; the account label is only a login hint and credential key.
Status may refresh a token. Client discovery uses `~/.copilot/mcp-oauth-config`
even when `COPILOT_HOME` points at another installation.

Download works. Server-side copy and upload are written and waiting on one thing: the
documented client's consented Graph scopes lack file-write permission. `status` reports
`can_write_files` so you can check rather than guess. Scope availability alone does not prove
a completed transfer; the large upload-session path also has a documented token-audience
limitation. Full detail in
[documents and files](../how-to/documents-and-files.md) and the
[file procedure](../../skills/chief-of-staff/references/files.md). A scope or policy denial is a
blocked result, not permission to reroute through another credential or tool. This bridge is
not covered by the scheduled wrappers' Work IQ tool denials.

---

## Where to look next

- **[Walkthroughs](../walkthroughs.md)** — the tools above in sequence, from finding a slot to
  sending the email.
- **[The chief-of-staff playbook](../chief-of-staff.md)** — which routine uses which tools.
- **[Proactive & scheduled](../proactive.md)** — how unattended runs use delta cursors so they don't
  repeat themselves.

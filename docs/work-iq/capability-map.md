# Work IQ capability map

Each row is one Work IQ tool or path family: what it is for, the Margo feature that uses it, the
file that demonstrates it, how far Margo takes it, and what it needs. Paths under `skills/` are the
procedures both runtimes follow; paths under `cloud/` are Margo Cloud's code.

**Status:**

- **Worked example**: a Margo routine uses it end to end, and the file shows the request shapes.
- **Documented**: described here or in a procedure, but no routine depends on it, or the only
  evidence is offline tests.
- **Not built**: known to exist in Work IQ; Margo does not use it.

None of these is a promise of tenant access. Every row also needs a Work IQ connection, consent
for the delegated scopes and a Microsoft 365 account that can see the data
([identity and consent](identity-and-consent.md)). Writes also need the tenant's Work IQ policy to
allow them.

## Read tools

| Tool or family | What it is for | Margo feature | Demonstrated in | Status | Requires |
| --- | --- | --- | --- | --- | --- |
| `fetch` | Literal, bounded reads of a known Graph path: today's `calendarView`, unread mail, a message by ID, a team's channels | Daily brief, triage, calendar, meeting prep | [`daily-brief.md`](../../skills/chief-of-staff/references/daily-brief.md), [`triage.md`](../../skills/chief-of-staff/references/triage.md), [`calendar.md`](../../skills/chief-of-staff/references/calendar.md), [`meeting-prep.md`](../../skills/chief-of-staff/references/meeting-prep.md) | Worked example | `WorkIQ.Read` |
| `retrieve` | Ranked semantic search across mail, Teams, meetings, files and people, with `webLink` and sensitivity labels | Catch-up, meeting debrief, Engage sweep, document queue | [`meeting-debrief.md`](../../skills/chief-of-staff/references/meeting-debrief.md), [`engage.md`](../../skills/chief-of-staff/references/engage.md), [`doc-queue.md`](../../skills/chief-of-staff/references/doc-queue.md) | Worked example | `WorkIQ.Read`; may need Copilot Credits billing for the calling user |
| `ask` | Synthesis across many sources: "what was decided and why" | Brief judgment, debrief, one-on-ones | [`daily-brief.md`](../../skills/chief-of-staff/references/daily-brief.md), [`meeting-debrief.md`](../../skills/chief-of-staff/references/meeting-debrief.md), [`one-on-ones.md`](../../skills/chief-of-staff/references/one-on-ones.md) | Worked example | `WorkIQAgent.Ask`; a Microsoft 365 Copilot license for the subject |
| `call_function` + delta | "What changed since": Graph delta functions, replayed from a stored `deltaLink` | Sweeps, catch-up, end-of-day wrap-up | [`delta.md`](../../skills/chief-of-staff/references/delta.md), cursor storage in [`proactive_state.py`](../../skills/chief-of-staff/scripts/proactive_state.py) | Worked example | `WorkIQ.Read`; one stored link per source |
| `fetch_blob` | Binary file content, up to 4 MB | File download | [`files.md`](../../skills/chief-of-staff/references/files.md) | Worked example; over 4 MB uses the separate [large-file bridge](../how-to/documents-and-files.md) | `WorkIQ.Read` |
| `list_agents` + `ask` with `agentId` | Find the Copilot agents available to the user, then ask one by ID | None yet | Margo Cloud's guard accepts an `agentId` that came from `list_agents` ([`GuardPolicy.cs`](../../cloud/src/Margo.Cloud.Guard/GuardPolicy.cs)) | Documented | `WorkIQAgent.Ask`; the agent must be available to the subject |
| `get_schema` · `search_paths` | Discover valid paths, required fields and accepted query options before a call | Delta setup, Engage discovery, any create or update | [`delta.md`](../../skills/chief-of-staff/references/delta.md), [`engage.md`](../../skills/chief-of-staff/references/engage.md), [`SKILL.md`](../../skills/chief-of-staff/SKILL.md) | Worked example | `WorkIQ.Read` |
| Paging (`@odata.nextLink`, `$top`) | Bounded enumeration with a stated page budget | Brief coverage, sweeps | [`daily-brief.md`](../../skills/chief-of-staff/references/daily-brief.md), [`delta.md`](../../skills/chief-of-staff/references/delta.md), [`state-operations.md`](../../skills/chief-of-staff/references/state-operations.md) | Worked example | Some collections reject `$skiptoken` ([errors](errors.md#skiptoken-rejected)) |
| People and organization | Profiles, `manager`, `directReports`, people search through `retrieve` | Relationships, who Margo Cloud works for | [`relationships.md`](../../skills/chief-of-staff/references/relationships.md); the `/me/manager` lookup in [`TurnSupport.cs`](../../cloud/src/Margo.Cloud.Orchestrator/TurnSupport.cs) | Worked example | `WorkIQ.Read`; directory IDs are not contact IDs ([errors](errors.md#contacts-versus-directory-ids)) |

## Write tools

| Tool | What it is for | Margo feature | Demonstrated in | Status | Requires |
| --- | --- | --- | --- | --- | --- |
| `do_action` | Actions: send a draft, reply, RSVP, generate an Office document | Drafting and follow-ups, calendar, work products | [`drafting.md`](../../skills/chief-of-staff/references/drafting.md), [`calendar.md`](../../skills/chief-of-staff/references/calendar.md), cloud cards in [`Approvals.cs`](../../cloud/src/Margo.Cloud.Orchestrator/Approvals.cs) | Desktop: worked example. Margo Cloud: built, not verified in a tenant | `WorkIQ.Act`; tenant write policy; exact approval ([writes](writes-and-approvals.md)) |
| `create_entity` | Create a draft, an event, a chat message | Drafting, calendar, debrief follow-ups | [`drafting.md`](../../skills/chief-of-staff/references/drafting.md), [`meeting-debrief.md`](../../skills/chief-of-staff/references/meeting-debrief.md), [`exec-followup.md`](../../skills/chief-of-staff/references/exec-followup.md) | As above | As above |
| `update_entity` | Change an event, mark read, flag or categorize | Calendar moves, triage housekeeping | [`calendar.md`](../../skills/chief-of-staff/references/calendar.md), [`triage.md`](../../skills/chief-of-staff/references/triage.md) | As above | As above; reversible, invisible changes can be a standing instruction on desktop ([safety](../safety.md#2-standing-authorization-is-bounded)) |
| `delete_entity` | Delete an item | Rarely: only an exact, approved request | [`SKILL.md`](../../skills/chief-of-staff/SKILL.md) | Documented | As above |

## Path families beyond Microsoft Graph (preview)

| Family | What it is for | Margo feature | Demonstrated in | Status | Requires |
| --- | --- | --- | --- | --- | --- |
| Office documents (`/office-agent-mcp/generate_artifact`, `edit_artifact`) | Create a Word document, Excel workbook or PowerPoint deck from an outline; edit one | Work products | [`work-products.md`](../../skills/chief-of-staff/references/work-products.md), [`Documents.cs`](../../cloud/src/Margo.Cloud.Orchestrator/Documents.cs) | Built, not verified in a tenant | `WorkIQ.Act`; tenant policy allowing Office document writes and file sharing |
| Pages (`/pages`) | Copilot Pages | None yet | Path-family rule in [`SKILL.md`](../../skills/chief-of-staff/SKILL.md); Margo Cloud's guard allows the family ([`GuardPolicy.cs`](../../cloud/src/Margo.Cloud.Guard/GuardPolicy.cs)) | Not built | Discover with `search_paths`; writes need approval |
| Viva Engage (`/engage`) | Communities, threads and posts | Community sweep | [`engage.md`](../../skills/chief-of-staff/references/engage.md) | Worked example through `retrieve`; the `/engage` family is documented, not yet used by a routine | Discover with `search_paths`; the Graph path `/employeeExperience/communities` is denied ([errors](errors.md#access-denied-for-path)) |
| SharePoint (`/sharepoint`) | SharePoint content beyond the Graph `sites` and `drives` paths | None yet; files use Graph paths and `fetch_blob` | Path-family rule in [`SKILL.md`](../../skills/chief-of-staff/SKILL.md) | Documented | Discover with `search_paths` and `get_schema` |
| Meeting transcripts, after the meeting | The transcript and recap of a finished meeting | Meeting debrief, decision log | [`meeting-debrief.md`](../../skills/chief-of-staff/references/meeting-debrief.md), [`decision-log`](../../skills/decision-log/SKILL.md) | Worked example | Transcription allowed by the tenant's Teams meeting policy and turned on for that meeting |
| Real-time transcripts (`/copilot/communications/realtimeActivityFeed`) | Transcript lines while a meeting is running | None | — | Not built | A running, transcribed meeting; Teams admin settings |

## Tool name and schema

The callable name depends on the host (`workiq-fetch` in GitHub Copilot CLI, `fetch` from an MCP
client). Read the schema from `tools/list` (or the host's tool list) before relying on an argument:
Margo Cloud's guard follows the live schemas where they differ from the published reference, for
example `search_paths` takes `query`, and `get_schema` accepts the `action` and `function`
operation types ([`cloud/README.md`](../../cloud/README.md#the-hosted-action-convention)).

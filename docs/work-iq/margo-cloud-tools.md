# Margo Cloud's tools, and what they call

A custom agent decides which tools its model sees. Margo Cloud gives the model two kinds:

- **Work IQ's own tools**, as Work IQ describes them in `tools/list`, limited to the arguments the guard accepts.
  Margo adds one required argument to each, `on_data_of`: `principal` for the manager's data, `self` for Margo's own
  and what is shared with her. The read tools are offered in every run. The write tools (`do_action`,
  `create_entity`, `update_entity`, `delete_entity`) are offered only in the manager's one-to-one chat, and only run
  with `on_data_of: "self"`.
- **Margo's own tools**, which the model calls by name. Most of them make Work IQ calls underneath, always through the
  guard ([`WorkIQGuard.cs`](../../cloud/src/Margo.Cloud.Guard/WorkIQGuard.cs)) and the MCP SDK client
  ([`WorkIQMcpTransport.cs`](../../cloud/src/Margo.Cloud.WorkIQ/WorkIQMcpTransport.cs)).

So Margo acts **as herself** directly, and **for her manager** only through a card:

- **Her own account** (her mailbox, calendar, files and the chats she is in): in the manager's one-to-one chat the
  model calls Work IQ's write tools itself, with no card. The guard allows this only in a turn the manager started
  there, at most ten actions a turn, and refuses a call whose body names another sender (`from`, `sender`), because
  with delegated access that would send as the manager.
- **The manager's account** (`on_data_of: "principal"`): a write is refused when the model calls it. It runs only
  from an approved card, inside `request_approval` below.

This page lists Margo's own tools: what the model passes, which Work IQ calls they make, with whose token, and what
the guard checks.

For what each tool means to the manager, in plain words, see [trust and safety](../safety.md#margo-cloud-what-needs-a-card-and-who-sees-it).

## The tools

| Tool | The model passes | Work IQ underneath | Token | What the guard checks |
| --- | --- | --- | --- | --- |
| `load_skill` | A skill name | None: reads `SKILL.md` from the image | None | — |
| `read_skill_file` | A skill and a path, e.g. `references/daily-brief.md` | None: reads the file from the image. `preferences.md` comes from the instance's private copy, never the template | None | — |
| `margo-state` | A script, its arguments, optional input and files | None: runs the C# state commands against the instance's private SQLite store. Same commands and output as the desktop helpers ([parity](../../tools/state_parity.py)) | None | Some commands are refused when hosted, such as `work_state.py approve` (approvals come from cards only) and the legacy imports |
| `office_requests` | Nothing | None: reads the ledger, where other people's messages were queued as they arrived | None | Each request is marked as untrusted data |
| `request_approval` | The ID of an action proposed with `work_state.py propose` | `fetch` of the email, meeting, chat or file the action affects, so the card can name it. The card itself is an Adaptive Card in Margo's Teams reply, through the Microsoft 365 Agents SDK, not Work IQ | Margo's for the reads | Nothing is written yet |
| …the manager taps **Approve** | (no model) | `fetch` of the affected item again, then the approved write: `do_action`, `create_entity`, `update_entity` or `delete_entity`, for example `/users/{manager}/sendMail` or a calendar change | Delegation mode: Margo's, with Send on Behalf and calendar delegate rights. On-behalf-of mode: the manager's | The call must match the approved one byte for byte. If the item changed since the card, the card is out of date and nothing runs. Approval and attempt are recorded before sending; the write runs once, on its own MCP session, and an unclear result is recorded as `unknown`, never retried |
| `create_document` | `Word`, `Excel` or `PowerPoint`; the full content in Markdown; the request | 1. `do_action` `/office-agent-mcp/generate_artifact` builds the file in Margo's OneDrive<br>2. `fetch` `/shares/u!{link}/driveItem` finds it<br>3. `do_action` `/drives/{drive}/items/{item}/invite` shares it with the manager, edit rights | Margo's | A standing permission covers her own documents shared with the manager alone (`AllowsOwnDocuments`); any other recipient needs a card. Creation is never retried |
| `tell_manager` (Teams) | `via: "teams"`, the text | 1. `fetch` `/me/chats?$filter=members/any(...)` finds her 1:1 chat with the manager<br>2. `create_entity` `/chats/{chat}/messages` posts the note | Margo's | The chat is the manager's 1:1 chat (`AllowsManagerChannel`) |
| `tell_manager` (email) | `via: "email"`, the text, a subject | `do_action` `/me/sendMail` from Margo's mailbox, saved to Sent Items | Margo's | The manager is the only recipient: no Cc or Bcc, at most 20,000 characters |
| `share_with_manager` | A file link, `read` or `write` | 1. `fetch` `/shares/u!{link}/driveItem` finds the file<br>2. `do_action` `/drives/{drive}/items/{item}/invite`, sign-in required | Margo's | The manager is the only recipient |

## Which runs get which tools

| Run | Work IQ reads | Work IQ writes, as herself | `load_skill`, `read_skill_file` | `margo-state` | `office_requests` | `request_approval`, `create_document` | `tell_manager`, `share_with_manager` |
| --- | --- | --- | --- | --- | --- | --- | --- |
| The manager talks to Margo in their 1:1 chat | Yes | **Yes**, no card | Yes | Yes | Yes | Yes | Yes |
| The manager @mentions her in a group chat or channel | Yes | No: other people's words are in that conversation | Yes | Yes | Yes | Yes | Yes |
| Voice | Yes | No | Yes | Yes | Yes | No | No |
| Observation runs (someone else messaged, emailed or copied her) and listening in rooms | Yes, with her own access only: nothing is read as the manager, in either access mode, and no other agent is asked | No | Yes | No: these runs don't open her state | No | No | Yes |

## Limits

| Limit | Value |
| --- | --- |
| Work IQ calls in one manager turn | 40 |
| Work IQ calls in one observation run | 25 |
| Actions as herself in one turn | 10 |
| Time for one turn | 8 minutes; she then stops and says so |
| Documents in one turn | 3, each at most 100,000 characters of content |
| Notes to the manager in one run | 3 Teams messages, 3 emails |
| Files shared with the manager in one run | 5 |
| Other people's messages queued per sender per day | 20 |

## What a tool returns when it doesn't work

Every tool answers with a `status`, which the model must report honestly:

| Status | Meaning | What Margo does |
| --- | --- | --- |
| `ok` | Done | Says so, with the link where there is one |
| `partial` | Part of it worked, for example a document created but not shared | Says which part did not |
| `refused` | The guard or a limit stopped it before anything was sent | Explains, or asks for a card instead |
| `unavailable` | Something it needs is missing, such as the manager's chat or email address | Uses the other channel, or says what is missing |
| `unknown` | Work IQ gave no clear answer; it may or may not have happened | Says so, and never tries again |
| `error`, `blocked`, `budget_exhausted` | Work IQ or the tenant refused, or the run's call budget is spent | Says what failed; nothing is retried blindly |

The reasons for each Work IQ failure are in [errors and recovery](errors.md). The approval flow step by step is in
[writes and approvals](writes-and-approvals.md).

## The code

| Tool | Where |
| --- | --- |
| Tool definitions offered to the model | [`EngineTools.cs`](../../cloud/src/Margo.Cloud.Engine/EngineTools.cs), [`Engine.cs`](../../cloud/src/Margo.Cloud.Engine/Engine.cs) |
| `margo-state` | [`StateCommands.cs`](../../cloud/src/Margo.Cloud.Engine/StateCommands.cs) |
| `office_requests` | [`Office.cs`](../../cloud/src/Margo.Cloud.Orchestrator/Office.cs) |
| `request_approval` | [`Approvals.cs`](../../cloud/src/Margo.Cloud.Orchestrator/Approvals.cs), [`CardContext.cs`](../../cloud/src/Margo.Cloud.Orchestrator/CardContext.cs), [`ApprovalCards.cs`](../../cloud/src/Margo.Cloud.Host/ApprovalCards.cs) |
| `create_document` | [`Documents.cs`](../../cloud/src/Margo.Cloud.Orchestrator/Documents.cs) |
| `tell_manager`, `share_with_manager` | [`ManagerChannel.cs`](../../cloud/src/Margo.Cloud.Orchestrator/ManagerChannel.cs) |
| The guard and the MCP client | [`WorkIQGuard.cs`](../../cloud/src/Margo.Cloud.Guard/WorkIQGuard.cs), [`WorkIQMcpTransport.cs`](../../cloud/src/Margo.Cloud.WorkIQ/WorkIQMcpTransport.cs) |

**Status:** built and tested offline. The [maturity table](../margo-cloud.md#maturity) says which of these have been seen
working in a test tenant.

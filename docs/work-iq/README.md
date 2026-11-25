# Learn Work IQ with Margo

Microsoft Work IQ gives an agent one MCP server for Microsoft 365: mail, calendar, Teams chats and
channels, meetings and their recaps, OneDrive and SharePoint files, people and the organization, and
Copilot itself. Margo is a worked example of building on it: every routine in this repository
reads and writes Microsoft 365 through Work IQ and nothing else.

| | |
| --- | --- |
| Endpoint | `https://workiq.svc.cloud.microsoft/mcp` (Streamable HTTP MCP) |
| Token audience | `api://workiq.svc.cloud.microsoft` |
| Delegated scopes | `WorkIQAgent.Ask`, `WorkIQ.Read`, `WorkIQ.Act` ([identity and consent](identity-and-consent.md)) |
| Read tools | `fetch`, `retrieve`, `ask`, `call_function`, `fetch_blob`, `list_agents`, `get_schema`, `search_paths` |
| Write tools | `do_action`, `create_entity`, `update_entity`, `delete_entity` (blocked by the tenant's Work IQ policy until an admin allows them) |
| Beyond Graph paths | Preview path families found with `search_paths` and `get_schema`: `/office-agent-mcp` (Word, Excel, PowerPoint), `/pages`, `/engage`, `/sharepoint`, and real-time meeting transcripts under `/copilot/communications/realtimeActivityFeed` |

Work IQ answers as the subject of the token: `/me` is whoever signed in. Everything else on these
pages follows from that.

Microsoft's own documentation is the authority on the service: start at the
[Work IQ MCP overview](https://learn.microsoft.com/microsoft-365/copilot/extensibility/work-iq/mcp/overview),
the [tool reference](https://learn.microsoft.com/microsoft-365/copilot/extensibility/work-iq/mcp/tool-reference)
and [policy governance for Work IQ MCP](https://learn.microsoft.com/microsoft-365/copilot/extensibility/work-iq/mcp/policy-governance-mcp).
Preview surfaces change; recheck them before you depend on a detail here.

## Two ways to load Work IQ

| | In an agent harness | In a custom agent |
| --- | --- | --- |
| What it is | A host such as GitHub Copilot CLI registers the MCP server, signs you in and exposes its tools to the model | Your own service is the MCP client: it gets a token, opens the session and decides which tools the model sees |
| Who Work IQ sees | You, the signed-in user | The identity your service signs in as: in Margo Cloud, her own agent user, or the manager through delegation or on-behalf-of |
| Who enforces approval | The host's tool permissions plus the agent's instructions | Your code: Margo Cloud's guard runs a write only after the manager approves that exact call on a card |
| Margo's example | Desktop Margo: persona and skills in GitHub Copilot CLI ([getting started](../getting-started.md)) | Margo Cloud: C# on the official MCP C# SDK ([`cloud/src/Margo.Cloud.WorkIQ`](../../cloud/src/Margo.Cloud.WorkIQ/WorkIQMcpTransport.cs)) |
| Quickstart | [Register Work IQ in a harness](../../samples/harness/README.md) | [`cloud/samples/WorkIQHello`](../../cloud/samples/WorkIQHello/): a token, `initialize`, `tools/list`, one `fetch`, one `retrieve` |

In a harness, no Margo code talks to Work IQ: the skills call its tools by their registered names
(`workiq-fetch`, `workiq-ask`, ...; in Claude Code, `mcp__workiq__fetch` and so on, see
[run Margo in Claude Code](../how-to/run-in-claude-code.md)). In Margo Cloud, `Margo.Cloud.WorkIQ` keeps one MCP session per
identity, reads the server's own `tools/list` and gives the model those descriptions and argument
schemas, filtered by [the guard](../../cloud/src/Margo.Cloud.Guard/WorkIQGuard.cs) to the read tools
and arguments it allows. Writes never reach the model as tools, get an MCP session of their own and
are never sent twice.

## Reading order

1. **Connect.** [Register Work IQ in a harness](../../samples/harness/README.md) and make one bounded
   `fetch`, or run the custom-agent sample.
2. **[Identity and consent](identity-and-consent.md).** Whose data a token reaches, which scopes and
   admin steps each path needs, and why an agent cannot show a consent prompt.
3. **[Patterns](patterns.md).** `retrieve` versus `fetch` versus `ask`, payload discipline, citations
   and large files.
4. **[Capability map](capability-map.md).** Every tool and path family, the Margo file that
   demonstrates it, its status and its requirements.
5. **[Errors and recovery](errors.md).** What each observed failure means and what to do next.
6. **[Writes and approvals](writes-and-approvals.md).** The reusable pattern for letting an agent
   near a send button.

Then read Margo as the worked example: the [chief-of-staff playbook](../chief-of-staff.md), the
[walkthroughs](../walkthroughs.md) and, for a custom agent, the [Margo Cloud overview](../margo-cloud.md).

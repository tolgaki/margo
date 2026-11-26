# Glossary

Plain meanings for the words these docs use. Desktop terms first, then Margo Cloud terms.

**Page names.** Files named `autopilot*.md` (for example [autopilot-deployment.md](autopilot-deployment.md))
are Margo Cloud pages. "Autopilot" was Margo Cloud's earlier name, and Agent 365 still calls this
kind of agent an *autopilot*; the file names were kept so links keep working.

## Microsoft 365 and the hosts

| Term | Meaning |
| --- | --- |
| Work IQ | Microsoft's service that lets an agent read and, where your tenant allows it, write your Microsoft 365 mail, calendar, Teams, files and people data. Margo reaches Microsoft 365 only through it ([Learn Work IQ](work-iq/README.md)) |
| MCP | Model Context Protocol: the standard way an agent host loads a tool server. Work IQ is an MCP server; Copilot CLI, Claude Code and Margo Cloud each load it as one |
| Harness | An agent host, such as GitHub Copilot CLI or Claude Code, that registers the Work IQ MCP server, signs you in and exposes its tools to the model ([Learn Work IQ](work-iq/README.md)) |
| Copilot CLI | GitHub Copilot CLI, the terminal program (`copilot`) that runs desktop Margo: it supplies the model, loads Margo's agent and skills, and signs in to Work IQ |
| The Copilot app | The desktop app that hosts Copilot agents, extension canvases and scheduled workflows. It is separate from the Copilot CLI terminal; the optional panels and app workflows need it, the CLI does not |
| Frontier | Microsoft's early-access program. Agent 365's agent features, and so Margo Cloud, need the tenant to be enrolled |

## Desktop Margo

| Term | Meaning |
| --- | --- |
| Margo desktop | Margo running in Copilot CLI on your own machine, with her persona (`agents/`), her skills (`skills/`) and a private local database. Stable for individual use ([getting started](getting-started.md)) |
| Ledger | Margo's private local database of work items, proposed actions, approvals and results. It lives under `~/.copilot/margo/`, never in the repository ([closed loop](closed-loop.md)) |
| Candidate | Something Margo suspects (a commitment, a memory) but you have not confirmed. A candidate never counts as confirmed work |
| Coverage | Which sources a run actually read, and over which time window. A source that was not read is a gap, not an empty inbox |
| Anchor | A scheduled run at a fixed time (the morning brief, the end-of-day wrap-up) that makes a brief available, as opposed to a frequent sweep ([proactive](proactive.md)) |
| Action desk | The list of actions Margo has proposed, each with its exact content, waiting for your decision. You see it in the conversation, or as an optional panel |
| Canvas, panels | Optional review screens (action desk, memory, task progress) in the Copilot app, served by the [action-desk extension](../.github/extensions/margo-action-desk/README.md). The CLI does everything without them |
| Dream | Opt-in, manual reflection over selected Margo sessions into sourced episodes and reviewable ideas ([Dream](how-to/dream.md)) |

## Margo Cloud

| Term | Meaning |
| --- | --- |
| Margo Cloud | Margo as an agent in your own tenant: a C# service you deploy into your Azure subscription and publish through Agent 365. A preview ([overview](margo-cloud.md)) |
| Agent 365 | Microsoft's platform for publishing, approving, hiring and governing agents in a tenant. It gives each hired Margo her identity and delivers her Teams messages and mail |
| Agent blueprint | The Entra app that defines Margo once per tenant. Every hired Margo is created from it |
| Instance | One hired Margo, working for one person. Hiring is **Create instance** in Teams or the admin center |
| Agent user | An instance's own user account: her mailbox, Teams presence and OneDrive |
| Manager, principal | The one person an instance works for (her agent user's manager in the directory). Both words mean the same person |
| Delegation | Exchange permissions that let Margo's own account read her manager's mail and calendar. The default way Margo Cloud reads the manager's work |
| On-behalf-of (OBO) | Margo calling Work IQ with her manager's own token (after the manager signs in), with Margo recorded as the actor. Built, not verified in a tenant |
| Manager channel | Margo Cloud's direct line to her manager: a note in their one-to-one chat, an email to them alone, or a file shared with them alone, with no approval card because nobody else receives it |
| Backchannel | What Margo Cloud does with what reaches her from other people: when someone else messages, emails or copies her, and, when listening is on, in the group chats, meeting chats and channels she is a member of, she reads with her own token and may send her manager a note through the manager channel. `MARGO_BACKCHANNEL=off` turns this off; the manager channel stays ([the backchannel](autopilot-deployment.md#the-backchannel)) |
| Approval card | The card Margo Cloud posts in Teams for one exact outward action. Only her manager's tap on **Approve** runs it, once ([approval cards](safety.md#margo-cloud-approval-cards)) |

More Margo Cloud terms (agent identity, agentic sign-in, Copilot Credits, router, provisioner) are
in the [Margo Cloud glossary](margo-cloud.md#glossary).

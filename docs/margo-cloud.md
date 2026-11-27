# Margo Cloud overview

Margo Cloud is Margo as an agent in your own Microsoft 365 tenant: a C# (.NET 10) service you deploy
into your own Azure subscription and publish through Microsoft Agent 365. Each person who hires her
gets their own Margo, with her own account (mailbox, Teams presence, OneDrive), working for that one
person, her **manager**. She uses the same persona and skills as desktop Margo, and reaches
Microsoft 365 only through Work IQ.

It is a **preview** of a community reference implementation, not a Microsoft product and not a
supported one ([getting help](../SUPPORT.md)): its core path has run in one test tenant with fictional
users. It is not for production use. This page is for the IT admin or developer deciding whether to try it; the
[deployment guide](autopilot-deployment.md) has the steps.

Pages whose file names start with `autopilot` (the deployment guide, the design record, voice and
instance containers) are Margo Cloud pages: Agent 365 calls this kind of agent an autopilot. New to the terms? See the [glossary](glossary.md).

## How it fits together

```text
 Manager in Teams ──► Agent 365 ──► router (/api/messages) ──► Margo container for that instance
 (one-to-one chat)    (blueprint,     checks the platform        │  her own identity and state
                       hiring,        token, finds the           ├──► Work IQ MCP (Microsoft 365 data)
                       delivery)      instance                   ├──► Azure OpenAI (chat model)
                                                                 └──► Blob Storage (her private state)
 Provisioner job ◄── lifecycle events (hired, suspended, retired) from the router
```

- **Agent 365** provides the blueprint, publishing, admin approval, hiring, each instance's identity
  and agent user, and delivery of Teams messages, mail and notifications.
- **The router** is the blueprint's messaging endpoint. It checks the token and forwards each
  activity to the instance's own container.
- **One container per hired Margo**, with its own managed identity and its own storage container
  ([details](autopilot-instance-containers.md)).
- **Work IQ** is her only Microsoft 365 tool server, behind a guard that decides which token a call
  uses and refuses anything not allowed ([Learn Work IQ](work-iq/README.md)).

## Who sees what

| Who | Sees |
| --- | --- |
| The manager | Margo's answers in their one-to-one chat, and what she knows about them ("what do you know about me", "forget that") |
| Margo | In `delegation` mode: the manager's mail and calendar, and what is shared with her own account, including the chats and channels she is a member of. With on-behalf-of: what the manager can see through Work IQ ([identity](work-iq/identity-and-consent.md)) |
| Colleagues | What the manager approved on a card for them. In a group chat or channel where the manager @mentions Margo, everyone there also sees her answer, which can include what the request needs from the manager's work, and any approval card she posts there, with the text it proposes ([who sees what, action by action](safety.md#margo-cloud-what-needs-a-card-and-who-sees-it)). Nothing of the manager's otherwise. What they send Margo, and what they say in chats and channels she is a member of, she reads and may report to the manager; their messages to her are queued as untrusted requests, and they get at most a fixed "passed this on" note, if the manager turns it on |
| Your Azure administrators | Everyone's stored state: anyone with data access to the storage account can read every manager's records ([terms](../cloud/terms.md#privacy)) |
| Microsoft 365 audit and your monitoring | Which operations ran, on what kind of resource, and their outcome; never message content |

## What needs approval

Every action that reaches another person: sending or replying to mail, setting up, moving or canceling
meetings, posting in Teams, sharing or editing a document others use. Margo posts a card in the
manager's one-to-one chat (or the group chat where they asked) showing the exact action, and runs it once, only after the manager taps
**Approve** ([writes and approvals](work-iq/writes-and-approvals.md)). Without a card: anything that reaches the manager alone (a
Teams note in their one-to-one chat, an email to them, a file or a document shared with them, see
[the backchannel](autopilot-deployment.md#the-backchannel)), her answers to the manager in a group chat or channel
where they @mention her (everyone there sees the answer, and any card she posts there), and one fixed notice.
The full list, with who can see each: [what needs a card, and who sees it](safety.md#margo-cloud-what-needs-a-card-and-who-sees-it).

## What she does without being asked

Margo's communication is with her manager, but she hears what happens around her. When someone
messages, emails or copies her, and (when her container is kept running, `MARGO_INSTANCE_MIN_REPLICAS=1`) every 15 minutes in the
group chats, meeting chats and channels she is a member of, she reads what is new through Work IQ, looks into what might matter, and sends
the manager a short note when it does. She never answers those people and does not tell them she is reading;
their words are treated as data, so at most they shape a note that only the manager receives. Tell
the people who share chats with her: the [terms](../cloud/terms.md#privacy) say so ([the backchannel](autopilot-deployment.md#the-backchannel)).

## Licenses and costs

- **Tenant:** Frontier preview enrollment, an Agent 365 (or Microsoft 365 E7) license, Work IQ
  enabled, and admin consent for Work IQ's scopes on the blueprint.
- **Per hired Margo:** her agent user needs a Microsoft 365 license with Exchange Online and Teams,
  and a Microsoft 365 Copilot license. Copilot Credits where your tenant meters Work IQ.
- **Azure:** the router (always on, 0.25 vCPU), a Basic container registry, Log Analytics (capped at
  1 GB a day by default), storage, the chat model's tokens, Voice Live minutes if voice is on, and
  each instance's container while it runs (scaled to zero when idle; kept running when she listens
  in rooms).

Everything is billed to your organization. No estimate is made here
([costs](autopilot-deployment.md#what-it-costs)).

## Maturity

| Status | What |
| --- | --- |
| **Verified in a test tenant** | Provisioning; the router-to-instance path and agentic sign-in; reading the manager's mailbox and calendar through delegation; a reply in Teams |
| **Built, not verified in a tenant** | Approval cards and approved sends; Office documents; the MCP C# SDK client and Work IQ's own tool descriptions; on-behalf-of access and the manager's sign-in; the manager channel and the backchannel (notes, emails and shares to the manager alone; observation runs; listening in rooms); answering the manager in a group chat or channel; queuing other people's messages, with the optional acknowledgment; `margo-state` parity with the desktop helpers (judged offline); voice; **Create instance** from the admin center; the state lease under concurrent turns |
| **Not built** | Live meeting transcripts; scheduled briefs (morning brief, end of day) |

## Reading order

1. This page.
2. [Deploy Margo Cloud](autopilot-deployment.md): prerequisites, roles, steps, operation, known limits.
3. [Trust and safety](safety.md#margo-cloud-what-needs-a-card-and-who-sees-it): what needs a card and who sees it, approval cards and fixed notices.
4. [Identity and consent](work-iq/identity-and-consent.md): blueprint, agent user, delegation and
   on-behalf-of.
5. Optional: [one container per hired Margo](autopilot-instance-containers.md) and
   [talk to Margo by voice](autopilot-voice.md).
6. For developers: [Margo Cloud code](../cloud/README.md) and the
   [design and decision record](autopilot.md).

## Glossary

The Margo Cloud terms in detail. The [main glossary](glossary.md) covers desktop and Work IQ terms too.

| Term | Meaning |
| --- | --- |
| Agent 365 | Microsoft's platform for publishing, approving, hiring and governing agents in a tenant (search Microsoft Learn for "Agent 365") |
| Frontier preview | Microsoft's early-access program that Agent 365's agent features require |
| Agent blueprint | The Entra app that defines Margo once per tenant; each hire creates an instance from it |
| Instance, hire | One Margo for one manager; hiring is **Create instance** in Teams or the admin center |
| Agent identity | The instance's service principal |
| Agent user | The instance's user account: her mailbox, Teams presence and OneDrive |
| Agentic sign-in | How the instance gets her agent user's token through the blueprint's federated credential, with no password |
| Manager, principal | The one person an instance works for (the agent user's manager in the directory). Both words mean the same person |
| Delegation | Exchange permissions that let Margo's own account read the manager's mail and calendar |
| On-behalf-of (OBO) | Margo calling Work IQ with the manager's own token, with Margo recorded as the actor |
| Copilot Credits | Usage-based billing for Copilot and Work IQ, where a tenant meters them |
| ABAC | Attribute-based access control: conditions that limit which roles the provisioner may grant, and on what |
| Router, provisioner | The public messaging endpoint, and the job that creates and retires instance containers |

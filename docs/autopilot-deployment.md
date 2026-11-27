# Deploy Margo Cloud

[Overview](margo-cloud.md) · [Design record](autopilot.md) · [One container per instance](autopilot-instance-containers.md) ·
[Voice](autopilot-voice.md) · [Margo Cloud code](../cloud/README.md) · [Trust and safety](safety.md)

Margo Cloud is a C# (.NET 10) service in [`cloud/`](../cloud/README.md) that you deploy into your own Azure
subscription and publish as a Microsoft Agent 365 agent. Deploy it first to a **test tenant with fictional users**.
Agent 365 and its CLI are in preview: the commands below were checked against Agent 365 CLI 2.0.1, so compare them with
`a365 --help` for the version you install. New to the terms (blueprint, instance, agent user, delegation,
on-behalf-of)? See the [glossary](margo-cloud.md#glossary); for Agent 365 and the Frontier program, search
Microsoft Learn for "Agent 365" and "Frontier".

## Status

| | |
| --- | --- |
| **Verified in a test tenant** | Provisioning; the router-to-instance path and agentic sign-in; reading the manager's mailbox and calendar through delegation; a reply in Teams |
| **Built, not verified in a tenant** | Approval cards and approved sends; Office documents; the MCP C# SDK client and Work IQ's own tool descriptions; on-behalf-of access and the manager's sign-in; the manager channel and the backchannel (notes, emails and shares to the manager alone; observation runs; listening in rooms); answering the manager in a group chat or channel; queuing other people's messages, with the optional acknowledgment; `margo-state` parity with the desktop helpers (judged offline); voice; **Create instance** from the admin center; the state lease under concurrent turns |
| **Not built** | Live meeting transcripts; scheduled briefs (morning brief, end of day) |

[Known limits](#known-limits) has the details. Optional: the
[live verification checklist](../cloud/src/Margo.Cloud.Host/VERIFY.md) walks a test deployment step by step.

## How it fits together

Margo is **self-hosted**: her code runs in your Azure subscription, in Azure Container Apps. Agent
365 provides everything around it: the agent blueprint, publishing, admin approval, hiring, each
hired Margo's own Entra agent identity and agent user (with a mailbox, Teams and a place in the org
chart), and delivery of Teams messages, mail and notifications to her messaging endpoint.

```text
Teams / Outlook / Word ──► Agent 365 ──► https://<router>/api/messages   (the blueprint's messaging endpoint)
                                              │  checks the token, finds the instance
                                              └─► Margo container for instance A (her own identity and state)
                                              └─► Margo container for instance B …
```

Azure AI Services serves the chat model
(and Voice Live, if you turn voice on) directly to each container.

## What this version does

| Margo can | Margo cannot (yet) |
| --- | --- |
| Chat with her manager in Teams and answer as Margo | Send, post, invite, share or change anything for other people without her manager's tap on that exact action's card (the two things others can see without one are in [Who Margo answers](#who-margo-answers)) |
| Read her manager's mail and calendar (delegation mode), and what is shared with her own account, through the Work IQ guard | Send scheduled briefs (morning brief, end of day) |
| Brief, triage, prep meetings and draft text | Answer anyone's email but her manager's |
| Reach her manager directly, with no card: a Teams note in their 1:1 chat, an email to them alone, a file shared with them alone ([backchannel](#the-backchannel)) | Reach anyone else without a card |
| Listen in the group chats, meeting chats and channels she is a member of, look into what matters and tell her manager; answer the manager's email by email | Hear rooms she is not a member of, or what is spoken in meetings |
| **After her manager approves each one on a card in Teams:** send email and replies from the manager's mailbox (as Margo on their behalf), set up, move or cancel meetings on the manager's calendar, and post Teams messages as herself in chats and channels she is in | Ask for approval from a voice call, or take a "yes" typed in chat or spoken as approval |
| Create a Word document, Excel workbook or PowerPoint presentation her manager asks for, in her own OneDrive, and share it with the manager alone (no card) | Share a document with anyone else, or change one other people use, without a card |
| Queue messages and mail other people send her, as untrusted requests for her manager | Reply to other people with a drafted answer (the `approve` reply setting acts as `none`) |
| Send one fixed "passed this on" note per conversation, if her manager turns that on | Join meetings live |
| Talk by voice with her manager, if voice is turned on | Talk to any MCP server except Work IQ |

## What gets created

`azd provision` in `cloud/` creates, in one resource group (`azd` asks for it, or uses `AZURE_RESOURCE_GROUP`; create
it first if your role is scoped to a resource group):

| Resource | Why |
| --- | --- |
| Azure AI Services account with a chat model deployment | Margo's model, and Voice Live for voice. Entra sign-in only; local keys are off |
| Azure Container Registry | Holds the Margo Cloud image (`cloud/Dockerfile`), built in the cloud from this repository |
| Log Analytics and Application Insights | Operational logs. Message content capture is off |
| Storage account (no anonymous access, no shared keys; versioning with replaced versions deleted after 30 days, 14-day soft delete) | Each hired Margo's private state, the route table and the provisioning queue |
| Container Apps environment, the **router** app and the **provisioner** job | The public messaging endpoint, and the job that creates one container per hired Margo |
| Three managed identities | Router, provisioner, and the role-less identity that instance containers use to act as the blueprint |

The Agent 365 CLI then creates the **agent blueprint** in Entra and registers the router as its
messaging endpoint. The post-provision hook (`scripts/postprovision.sh`) does the rest:

1. Grants the router and the provisioner their roles, by role name. The provisioner may grant
   only the four roles an instance needs, each only on the resource it applies to (ABAC conditions on the storage
   account, the registry and the AI account): its state container, image pull, the chat model
   (`Cognitive Services OpenAI User`) and Voice Live (`Cognitive Services User`). It may grant them only to service
   principals, and never to the router, itself or the blueprint credential. A grant on the whole resource group from an
   earlier version is removed.
2. Runs the repository privacy check, builds the image from `cloud/Dockerfile` with `az acr build`, pins it
   by digest and records it in the azd environment (`MARGO_PINNED_IMAGE`), so the next provision keeps the router on
   a Margo image instead of resetting it to the public placeholder. It refuses a checkout with uncommitted changes
   unless `ALLOW_DIRTY=true`. The one image serves all three roles: the router and the provisioner job run `dotnet /app/router/Margo.Cloud.Router.dll` and
   `dotnet /app/provisioner/Margo.Cloud.Provisioner.dll run`; instance containers run the host,
   `dotnet /app/host/Margo.Cloud.Host.dll`.
3. Once the blueprint exists and the router runs a pinned image (from the second provision on): switches the router
   and the provisioner to the new image and blueprint,
   then runs `scripts/blueprint.sh`, which adds the blueprint's federated credential for instance
   containers and gives the blueprint Work IQ's delegated `WorkIQAgent.Ask`, `WorkIQ.Read` and `WorkIQ.Act` scopes (inheritable by
   every hired Margo, with tenant-wide consent). Any of these the deployer may not do is printed as
   a step for an administrator instead.

## Before you start

### How long it takes

Plan on **one to three weeks** from start to a first conversation, most of it waiting: Frontier
enrollment, licenses, the admin consent for Work IQ, Margo's approval in the admin center and
mailbox delegation each need someone else. The hands-on work (provisioning, the blueprint,
publishing, hiring) is **one to two days**. Line up the people in the next table before you begin.

### Known problem: Create instance can refuse

In the test tenant, **Create instance** (step 5) could not finish: the admin center's instance
policy showed an empty list of recognized Agent 365 licenses, although the tenant's Agent 365
subscription and all its service plans were active. If it happens to you, the instance can be
created without that page, through the Microsoft Graph (beta) Entra Agent ID APIs: create an agent
identity from the blueprint, create its agent user, set the agent user's manager and usage location,
and assign its licenses. Agent 365 then delivered the instance's Teams messages to the router as
usual. Treat this as a workaround for a preview issue, not the supported path, and budget time for
it or for a support request.

### People and roles

| Step | Who | Role |
| --- | --- | --- |
| Provision and build | You or an Azure admin | **Owner**, or **Contributor** plus **Role Based Access Control Administrator**, on the resource group; the first provision also registers resource providers, which needs those rights on the **subscription** (or have an admin register them once) |
| Create the blueprint (Agent 365 CLI) | You | **Agent ID Developer** at least; the CLI's permission steps may need **Agent ID Administrator** |
| Consent to Work IQ for the blueprint | Entra admin | **Global Administrator** or **Privileged Role Administrator** |
| Approve Margo | Microsoft 365 admin | **Global Administrator** or **AI Administrator** |
| Hire an instance, license her agent user | Anyone the admin allows; a license admin | Membership in the hiring scope; **License Administrator** |
| Grant mailbox delegation (delegation mode only) | Exchange admin | Exchange Online permission management |

### Tenant prerequisites

- **Frontier preview** enrollment and the Agent 365 terms accepted. Agent users, mail and
  document notifications, and **Create instance** need it.
- **Licenses:** an Agent 365 (or Microsoft 365 E7) license for the tenant's agent features. Each
  hired Margo's agent user needs a Microsoft 365 license that includes Exchange Online and Teams for
  her mailbox and chat, and a Microsoft 365 Copilot license for Work IQ.
- **Work IQ** enabled in the tenant. `scripts/blueprint.sh` checks for its service principal.
- Usage-based billing for Copilot Credits, if your tenant meters Work IQ that way. Which of the
  manager and Margo is billed under on-behalf-of access is still unverified.

- **One tenant.** The Azure subscription must be in the same Entra tenant as Microsoft 365: the router and instances
  trust tokens from the subscription's tenant only.

### Who can read the stored state

Each hired Margo's records (her work ledger, memory of her manager, requests from colleagues) are stored in your
storage account. **Anyone with data access to that storage account can read every manager's stored records.** Limit
that access, and tell managers who has it: the [terms](../cloud/terms.md#privacy) say so, and you publish them to your
users.

### What it costs

Everything is billed to your organization. Fixed while deployed: the router (always one replica, 0.25 vCPU), the
container registry (Basic), Log Analytics ingestion (capped at 1 GB a day by default) and storage. Per use: the chat
model's tokens (the `turn usage` log line shows them per turn), Voice Live minutes, and each instance's container
while it runs (scaled to zero when idle, unless kept warm). Listening in rooms keeps each instance's container running
(`MARGO_INSTANCE_MIN_REPLICAS=1`) and adds an observation run whenever a pass finds something new (at most
`MARGO_BACKCHANNEL_DAILY_RUNS` a day). Per hired Margo: her agent user's Microsoft 365 and
Copilot licenses, and Copilot Credits where Work IQ is metered. No estimate is made here; use the Azure pricing
calculator with your expected number of managers and turns.

### Tools on your machine

macOS, Linux or WSL: the `azd` hooks are POSIX shell scripts and there are no Windows versions.

| Tool | Why | How to get it |
| --- | --- | --- |
| `az` (Azure CLI) | Sign-in, role grants, the image build, Graph calls in `blueprint.sh` | Microsoft's Azure CLI install page |
| `azd` 1.27.1 or later | `azd provision` and its hooks | Microsoft's Azure Developer CLI install page |
| Agent 365 CLI (`a365`) | Creates the blueprint, registers the endpoint, publishes Margo | A .NET global tool, so installing it needs the **.NET SDK** on your machine. Follow Microsoft's current instructions: search Microsoft Learn for "Agent 365 CLI". Then check `a365 --help` |
| `git`, `python3` | The checkout; the hook scripts read JSON with Python | Your platform's packages |

Docker is not needed, and **the image build does not need .NET on your machine**: the image is built in Azure
Container Registry. The .NET SDK is needed only to install the `a365` tool (and to build and test Margo Cloud
yourself; see [cloud/README.md](../cloud/README.md)).

### Choose how Margo reads her manager's work

| Mode | Set | Reaches | Requires |
| --- | --- | --- | --- |
| `delegation` | `azd env set MARGO_PRINCIPAL_ACCESS delegation` | The manager's mail and calendar (direct reads through the delegation), plus everything shared with Margo herself: mail she receives, chats and channels she is in, files and sites shared with her (her own Copilot search included). Copilot search of the manager's whole data reports "unavailable" | The manager makes Margo a calendar delegate (or an Exchange admin grants mailbox access) and shares chats, channels and files with her |
| `obo` (on-behalf-of; **not verified in a tenant**) | `azd env set MARGO_PRINCIPAL_ACCESS obo`, plus the handler below | What the manager can see through Work IQ, including Copilot `ask` and `retrieve` over their own data, once they have signed in | The handler settings below, an OAuth connection on the agent's Azure Bot, and the manager's sign-in when Margo asks for it |

**Use `delegation`** (the default) unless you are ready to test `obo`. `obo` gives Margo the manager's own Work IQ
access, which is what makes Copilot reasoning and search over their mail, meetings and files possible.

**This deployment does not create an Azure Bot or its OAuth connection.** For `obo`, an administrator must create
and configure them (an Azure Bot resource for the agent, with an OAuth connection that signs the manager in, set up
for Teams single sign-on) before setting the handler below. If you can't, use `delegation`.

- **Settings:** `azd env set PRINCIPAL_AUTH_HANDLER PRINCIPAL` and
  `azd env set MARGO_PRINCIPAL_OAUTH_CONNECTION <name>` define, in every instance, an Agents SDK sign-in handler of
  type `AzureBotUserAuthorization` that uses that OAuth connection on the agent's Azure Bot (single sign-on in Teams)
  and exchanges the manager's sign-in, on their behalf, for a Work IQ token through the blueprint's own connection.
  The blueprint needs Work IQ's delegated permissions as inheritable, admin-consented permissions
  ([identity and consent](work-iq/identity-and-consent.md)).
- **Sign-in:** only the manager is asked, only in their 1:1 chat with Margo (never from a group chat, so in a group
  chat on-behalf-of reads of the manager's own data are unavailable), and only once Margo has looked them up
  (from their second message on). The Agents SDK runs that handler's sign-in before the turn: single sign-on in Teams
  where it is set up, otherwise a sign-in card. Nobody else who messages Margo is asked. Until the manager has signed
  in, every principal source reports `blocked`, and Margo never falls back to her own token.
- **Not verified in a tenant:** the sign-in, the token exchange, and Work IQ accepting the manager's token.

Set the mode, and the managers' time zone
(`azd env set MARGO_TIMEZONE Europe/Berlin`; default `America/Los_Angeles`), before provisioning;
both reach every instance container. After a change, provision and then redeploy all instances.

## 1. Provision Azure

All commands run from `cloud/` in a clean checkout of this repository.

```sh
cd cloud
az login --tenant {tenant-id}
az account set --subscription {subscription-id}
azd auth login --tenant-id {tenant-id}

azd env new margo-test
azd env set AZURE_LOCATION westus3
azd env set MARGO_PRINCIPAL_ACCESS delegation
azd provision
```

Use a real region name for `AZURE_LOCATION` (for example `westus3` or `eastus2`), one that offers
your chat model with quota in your subscription. The default model is `gpt-5-mini`, version
`2025-08-07`; choose another with `MARGO_MODEL_NAME` and `MARGO_MODEL_VERSION`. Check quota first:

```sh
az cognitiveservices usage list --location westus3 -o table | grep -i gpt-5-mini
```

**Expected result:** the hook ends with "Azure is ready; the router waits for an agent
blueprint", and prints the messaging endpoint, `https://<router>/api/messages`. The router keeps
a public placeholder image (port 80, without Margo's command) until an image is pinned and the blueprint exists.

## 2. Create the blueprint with the Agent 365 CLI

Keep the CLI's files **outside this repository**: they hold tenant identifiers and may hold a
client secret. Margo never uses that secret; her containers sign in through a federated
credential.

Run every `a365` command from one folder outside the repository. `a365 config init` writes
`a365.config.json` there; it must exist before `a365 setup blueprint --endpoint-only` below, which refuses to run
without it. The simplest order is to run `a365 config init` first; the first `setup blueprint` command takes its name
and tenant from its flags.

```sh
mkdir -p ~/margo-a365 && cd ~/margo-a365
a365 config init          # interactive; answers below
a365 setup blueprint -n Margo --tenant-id {tenant-id} --no-endpoint
```

With CLI 2.0.1, `a365.config.json` must name the tenant, the messaging endpoint (`https://<router>/api/messages`
from step 1), `needDeployment: false` (Margo is self-hosted) and `agentIdentityDisplayName`. Then register the
messaging endpoint:

```sh
a365 setup blueprint --endpoint-only
```

Use `--update-endpoint https://<router>/api/messages` if the router's address changes. In the Teams Developer
Portal (**Tools › Agent blueprints › Margo › Configuration**), the agent type should be **API
based** and the notification URL should be the same messaging endpoint.

The CLI writes the blueprint's ID to `a365.generated.config.json` as `agentBlueprintId`. If the CLI shows the
blueprint's client secret, delete it in the Entra admin center (**App registrations › Margo › Certificates &
secrets**) unless you use it for something else: Margo never needs it.

Work IQ's scopes can also be added with the CLI instead of `blueprint.sh`. `{work-iq-app-id}` is the application
(client) ID of Work IQ's service principal in your tenant, the one `blueprint.sh` looks up:

```sh
az ad sp show --id api://workiq.svc.cloud.microsoft --query appId -o tsv
```

If that finds nothing, Work IQ is not enabled in the tenant. Then:

```sh
a365 setup permissions custom --resource-app-id {work-iq-app-id} --scopes WorkIQAgent.Ask,WorkIQ.Read,WorkIQ.Act
a365 query-entra inheritance        # the Work IQ entry must show kind allAllowed
```

The CLI's own permission steps also consent Microsoft Graph scopes, including send scopes such as `Mail.Send`, and
make them inheritable. Margo does not use Graph directly (all its reads and writes go through Work IQ, and the guard
runs only writes the manager approved on a card), so remove the Graph grant and its inheritable entry once the instance works, or review them with
your Entra admin before production.

## 3. Connect the blueprint and finish

Go back to the `cloud/` folder of your checkout (you left it for `~/margo-a365` in step 2; replace
`~/src/margo` with wherever you cloned the repository):

```sh
cd ~/src/margo/cloud
sh scripts/connect-blueprint.sh ~/margo-a365/a365.generated.config.json
azd provision
```

This provision starts the router on the image pinned in step 1. **Expected result:** the hook ends with "Done.
Margo's messaging endpoint is …" and lists what remains. If it ends with "Run 'azd provision' once more" instead (the
blueprint was set before any image was pinned), run `azd provision` again. If it printed steps for an administrator
(the federated credential, the Work IQ scope or its consent), have them done now. Margo cannot sign in as her agent user without the credential,
and cannot read anything without the consent. Every step is idempotent; check again with
`azd hooks run postprovision`.

## 4. Publish and approve

1. Publish Margo with the Agent 365 CLI: `a365 publish --aiteammate true` builds the app package (a zip with
   `manifest.json` and icons). **Edit the manifest before uploading it**: CLI 2.0.1 fills in placeholder descriptions
   and "Microsoft Corporation" as the developer. Raise the version on every upload. Use these details:

   | Field | Value |
   | --- | --- |
   | Name | Margo |
   | Short description | Your chief of staff. Drafts and proposes; you approve what it sends for you. |
   | Full description | A personal chief of staff that briefs, triages, preps meetings and drafts replies for the person who hires it. Works only for its manager. Email, meetings, posts and shares that reach other people run only after the manager approves that exact action on a card. Without a card it writes only to its manager: notes, email and files for them alone, and answers where they ask, including a group chat or channel where they @mention it, which everyone there can see. A manager can also turn on one fixed "passed this on" note to colleagues who write to it. |
   | Developer, website | Your organization; this repository's URL |
   | Terms of use, privacy | [`cloud/terms.md`](../cloud/terms.md) (or your own) |
   | Icons | `cloud/assets/` |
   | Respond without @mention in group chats | Off |

   The descriptions are what managers and admins read before hiring; keep them in step with
   [what needs a card, and who sees it](safety.md#margo-cloud-what-needs-a-card-and-who-sees-it) if you change them.

2. A Microsoft 365 admin opens the [Microsoft 365 admin center](https://admin.cloud.microsoft/?#/agents/all/requested),
   **Agents › All agents › Requests**, selects **Margo**, and:
   - **Publish to users:** chooses who may hire Margo (the hiring scope; Margo does not restrict it);
   - **Apply template:** keeps Microsoft's default protections, and adds policies that block
     external recipients and apply your DLP rules;
   - **Review and finish**, then checks that **Registry** shows Margo as *Available*.

   If the approval page keeps spinning on its security-policy step, note the correlation ID the
   page shows and open a support request; nothing in the deployment can unblock it.

## 5. Hire an instance

1. In Teams, open **Apps › Agents for your team** and select **Margo › Create instance** (or
   from the admin center's **Agent templates**).
2. Name it, set its alias and domain, and **make sure the manager is the person Margo will work
   for**. Margo looks up her manager in the directory on every turn and treats only that person's
   messages as instructions. If the manager changes, the new manager starts with fresh state
   and the previous person's state is never opened for them.
3. **License her agent user.** Set its usage location first, then assign the Microsoft 365 and
   Copilot licenses. Without them she has no mailbox, no Teams presence and no Work IQ.
4. Wait a few minutes. The hire event reaches the router, which asks the provisioner to create her
   container (about two minutes). Margo messages nobody first; once she is running and her manager
   has chatted with her once, she may send them notes ([backchannel](#the-backchannel)).

**If Create instance refuses for a missing license,** see
[known problem: Create instance can refuse](#known-problem-create-instance-can-refuse).

You can also create a container ahead of time, from `cloud/`:

```sh
sh scripts/provisioner.sh enqueue ensure <agent-identity-id> <agent-user-id>
```

`scripts/provisioner.sh` runs the provisioner job once with the given command and everything else the job has. Do not
use `az containerapp job start --image/--command/--args` for this: it replaces the job's container, environment
included, and the run fails.

### Delegation mode: grant mailbox access

The manager can do it in Outlook: add Margo's agent user as a delegate of their calendar (this is what the test tenant
used; Margo's direct calendar reads then succeeded). Share chats, channels and files with her the usual way: add her
to the chat or team, or share the file. Margo never acts on what she reads: an outward action runs only after the
manager approves it on its card (see "Sending on your behalf" below).

For mail, or to grant both centrally, an Exchange admin runs, with the real addresses:

```powershell
Add-MailboxPermission -Identity dana@example.com -User margo-dana@example.com -AccessRights FullAccess -AutoMapping $false
Add-MailboxFolderPermission -Identity "dana@example.com:\Calendar" -User margo-dana@example.com -AccessRights Reviewer
```

Full Access is what Microsoft Graph needs to read another mailbox. It also permits changes at the
Exchange level; the guard lets through only a write the manager approved on its card, but that is a software control,
not an Exchange one. Remove the permissions when you retire the instance.

The UI equivalent: [Microsoft 365 admin center](https://admin.microsoft.com) › **Users › Active users** › the manager ›
**Mail** › **Read and manage permissions** › add Margo's agent user (or the Exchange admin center › **Recipients ›
Mailboxes** › the manager › **Delegation**).

### Documents she creates for you

Ask in your one-to-one chat: "turn this into a Word document", "build a deck on the Q3 results". Margo writes the full
content, Work IQ's Office document tools (preview) create the file in **her** OneDrive, and she shares it with **you
alone**, with editing rights: you get the usual "shared with you" email and a link in the chat. No card, because nobody
else can see it; the guard allows that share only to you, with sign-in required. Sharing it further, or changing a
document others use, goes through a card.

Needs: the tenant's Work IQ policy must allow writes for Office documents and file sharing (Microsoft blocks Work IQ
writes by default; see *Policy governance for Work IQ MCP* on Microsoft Learn), and Work IQ's `WorkIQ.Act` scope, which
`blueprint.sh` consents. Unverified in a tenant: that the Office tools accept Margo's own account, where they save the
file, and that her account can share it. If creation gets no clear answer, Margo says so and does not try again: check
your "Shared with me" list first.

### Sending on your behalf: approval cards

Margo can send, reply to and forward email, set up, move or cancel meetings, post Teams messages, and share, change,
publish or delete content (files, Copilot Pages, Viva Engage, SharePoint), each only after the manager taps
**Approve** on a card that shows that exact action:

1. The manager asks in their one-to-one chat ("email Rafa that the review moved to Thursday"). Margo drafts it,
   records the exact Work IQ call, and posts a card in plain words: who it goes to, the subject, the text, the meeting
   time, and for a reply or a meeting change which email or meeting it is (Margo looks it up). The card shows no
   technical details. If Margo can't describe an action or look up its email or meeting, the card says so: reject it
   and ask her in chat.
2. **Approve** runs exactly that call, once, and Margo replies with the result; the card loses its buttons. **Reject**
   discards it. A card for an older version, or one already decided, runs nothing.
3. Only the manager's tap counts, in their one-to-one chat or the group chat or channel the card was posted in. A "yes" typed in chat or said in a voice call is not an
   approval, and a voice call cannot ask for one.

What each kind needs, beyond the mailbox permission above:

| Action | Comes from | Needs |
| --- | --- | --- |
| Email, reply | The manager's mailbox, shown as "Margo on behalf of {manager}" | **Send on Behalf** for Margo's agent user on the manager's mailbox: admin center › the manager › **Mail** › **Send on behalf** (or `Set-Mailbox dana@example.com -GrantSendOnBehalfTo @{Add="margo-dana@example.com"}`) |
| Meeting set up, move, cancel | The manager's calendar; invitations on the manager's behalf | Margo as a calendar delegate with **Editor** rights (Outlook › calendar sharing › add Margo as delegate, "can edit") |
| Teams message | Margo herself, in a chat or channel she is a member of | Margo added to that chat or team. She cannot post as the manager, or start a new chat |

**Unverified in a tenant:** that Microsoft Graph sends `/users/{manager}/sendMail` as "on behalf of" with Send on
Behalf alone (it may require Send As), and that Work IQ passes delegated writes through. If a send fails, Margo
replies "Not sent: …" with the reason, and the card shows it. A result Margo could not confirm is reported as unknown
and never sent again: check Sent Items or the calendar.

## 6. First conversation

**First, name the platform's application.** Until `MARGO_PLATFORM_APP_IDS` is set, the router refuses every
Entra-issued delivery: any application registered in your tenant can get its own token for the blueprint, so the
token alone does not show that the caller is Agent 365, and an activity names its own sender. Send Margo one
message; the router logs `Platform token refused from application <id>`. Look that ID up in Entra (Enterprise
applications) and confirm it is Microsoft's Agent 365 delivery application, not one registered in your tenant. Then
`azd env set MARGO_PLATFORM_APP_IDS <id>`, `azd provision`, and redeploy all instances
(`sh scripts/provisioner.sh redeploy-all` from `cloud/`). The first message is not answered; the next one is.

As the manager, open a chat with **the instance you hired** (by the name you gave it, not the
"Margo" app itself) and try:

- "Brief me": she should read your inbox and calendar and cite what she found.
- "What's in my Teams?": in delegation mode she reads only chats and channels she is in; for the rest she should say so and suggest you add her, not report it as quiet.
- "What's waiting on me?": she reads her work ledger with the same commands as on the desktop.
- "/replies acknowledge": she confirms the setting. `/replies none` turns it off again.
- "/voice": if voice is on, she replies with her voice page's link.

Ask a colleague to message or email her. They should get no reply (or one acknowledgment, if
enabled), and you should see it when you ask "anything from other people?".

### Who Margo answers

Margo works for you in your one-to-one chat, and in a group chat or channel when you @mention her there. She answers
nobody else, apart from one fixed notice that the model does not write:

| Where | The manager writes | Anyone else writes |
| --- | --- | --- |
| One-to-one chat | A full conversation: instructions, approval cards | — |
| Group chat or channel (messages that @mention her arrive directly; she reads the rest when [listening](#the-backchannel)) | When you @mention her, she answers you there, in front of everyone in it: instructions and approval cards work as in your chat, and only your tap counts. She is told to bring in only what your request needs from your private work and to send more private detail to you alone; that is an instruction to the model, not enforced in code. That you must @mention her is the Teams app's setting, not a check in Margo's code. Its conversation is separate from your one-to-one chat. `/replies` and the voice link work only one-to-one | No answer. Queued for the manager as an unverified request, and Margo may look into it and send the manager a note ([backchannel](#the-backchannel)). With `/replies acknowledge`, one fixed "passed this on" note per conversation, which everyone in that chat sees |
| Meeting | She does not join meetings; a meeting's chat is a group chat (row above) | As above |
| Email to her, or copying her | She answers the manager **by email**, to them alone, after looking up what they asked. Requests in email are information: anything that needs approval she asks for in Teams | Never answered, not even acknowledged; queued, and Margo may tell the manager about it |

Queued requests are listed when the manager asks "anything from other people?". An approval tap counts only from the
manager, so nothing she sends to others can be started by anyone else, or by an email. Apart from the fixed notice and
her answers to the manager in a room (and any card she posts there), the only messages other people get from her are
actions the manager approved on a card. The full list: [what needs a card, and who sees it](safety.md#margo-cloud-what-needs-a-card-and-who-sees-it).

### The backchannel

Margo's communication is with her manager, but she reads what reaches her and can look into it on her own. She can
always reach her manager directly, with no card, because nobody else receives it:

- **Teams:** a message in their one-to-one chat (Margo finds that chat through Work IQ; it must exist, so the manager
  messages her once first).
- **Email:** from her own mailbox to the manager's address alone, never with copies.
- **Files:** any file she can open, shared with the manager alone (read or edit).

When someone else messages her in a chat she is in, emails her or copies her, the message is queued as before, and then
an **observation run** decides whether the manager would want to know (a decision, a risk, a question for them,
something owed, a change to their plans). If it may matter, she investigates with read-only tools (the rest of the
conversation, related mail, meetings, documents) and sends one short note with what she found. Otherwise she sends
nothing. When the manager emails her, she answers them by email the same way.

**In the room.** Margo also learns from conversations she is part of, not only from messages addressed to her: every
`MARGO_LISTEN_MINUTES` she reads, through Work IQ with her own token, what was said since the last pass in the group
chats, meeting chats and Teams channels she is a member of (up to 8 rooms and 6 channels a pass, the newest messages
of each). If anything is new, one observation run looks at all of it, may investigate further, and is told to send the
manager at most two notes. A quiet pass runs no model. One-to-one chats, her own messages and older messages are left out.
Listening needs the instance kept running (`azd env set MARGO_INSTANCE_MIN_REPLICAS 1`), since a container scaled to
zero runs no timer; it then costs one always-on container per Margo.

| Setting | Default | Meaning |
| --- | --- | --- |
| `MARGO_BACKCHANNEL` | `on` | `off`: messages from others are only queued, and Margo does not listen in rooms |
| `MARGO_BACKCHANNEL_DAILY_RUNS` | `40` | Observation runs per instance per day, listening passes included; later ones are skipped |
| `MARGO_LISTEN_MINUTES` | `15` | How often she reads her rooms; `0` turns listening off |
| `MARGO_INSTANCE_MIN_REPLICAS` | `0` | Set `1` for listening |

Limits: one observation run per conversation every ten minutes (later messages are read by the next run); this and the
daily count are kept in the running container's memory and start again when it restarts; a listening
pass that fails is read again next time, looking back at most six hours. An
observation run reads only with Margo's own access: in either mode the guard refuses anything read as the manager,
so the manager's mail and calendar are not read. Messages addressed to her arrive as the platform delivers them (in
Teams, usually @mentions); everything else in her rooms she reads when listening. Each run is a model run, billed like a turn. Not verified in a
tenant.

## Voice (optional)

```sh
azd env set MARGO_VOICE_LIVE_MODEL gpt-realtime-2.1
azd provision
```

The voice page signs the manager in through its **own app registration**, for a `Voice.Talk` scope that app exposes.
Agent blueprints refuse redirect URIs (`PropertyNotCompatibleWithAgenticApplication`), and the sign-in must never be a
token for the blueprint: a token for the blueprint is what Agent 365 presents at the messaging endpoint. If an earlier
setup added a `Voice.Talk` scope or consent to the blueprint, remove both.

These commands are standard Entra forms but **have not been run in a tenant** in this shape:

1. Create the voice app, give it an identifier URI and the scope, and make it a single-page app:
   ```sh
   VOICE_APP=$(az ad app create --display-name "Margo voice page" --sign-in-audience AzureADMyOrg --query appId -o tsv)
   VOICE_OBJ=$(az ad app show --id $VOICE_APP --query id -o tsv)
   SCOPE_ID=$(uuidgen | tr 'A-Z' 'a-z')
   az ad app update --id $VOICE_APP --identifier-uris "api://$VOICE_APP"
   az rest --method PATCH --url "https://graph.microsoft.com/v1.0/applications/$VOICE_OBJ" --body "{
     \"api\": {\"requestedAccessTokenVersion\": 2, \"oauth2PermissionScopes\": [{\"id\": \"$SCOPE_ID\", \"value\": \"Voice.Talk\",
       \"type\": \"User\", \"isEnabled\": true, \"adminConsentDisplayName\": \"Talk to Margo\", \"adminConsentDescription\": \"Talk to Margo by voice\",
       \"userConsentDisplayName\": \"Talk to Margo\", \"userConsentDescription\": \"Talk to Margo by voice\"}]},
     \"spa\": {\"redirectUris\": [\"https://<router>/i/<instance>/voice/\"]},
     \"requiredResourceAccess\": [{\"resourceAppId\": \"$VOICE_APP\", \"resourceAccess\": [{\"id\": \"$SCOPE_ID\", \"type\": \"Scope\"}]}]}"
   az ad sp create --id $VOICE_APP >/dev/null
   az ad app permission admin-consent --id $VOICE_APP
   azd env set MARGO_VOICE_CLIENT_ID $VOICE_APP
   ```
   Add every instance's `https://<router>/i/<instance>/voice/` to the app's SPA redirect URIs.
2. `azd provision`, then redeploy existing instances (below).

Only the instance's manager gets a session; anyone else's sign-in is refused before anything is exchanged or read. With
a voice app there is no on-behalf-of exchange in voice. Tool results given to the voice model are cut at 8,000
characters. `MARGO_VOICE_MAX_SECONDS` (default 1800) and `MARGO_VOICE_MAX_TOOL_CALLS` (default 60) are azd variables.
Details, the custom voice and limits: [Voice](autopilot-voice.md).

## Operate

| Task | How |
| --- | --- |
| See what Margo read | Application Insights traces named `workiq`: subject, tool, a path template such as `/users/{user}/messages/{id}` (no identifiers, names or query text), status and outcome. No message content |
| See what a turn cost | The `turn usage input= cached= output= reasoning= rounds= tool_calls=` log line, one per model turn |
| Name the platform's application | Required before the first conversation ([step 6](#6-first-conversation)). While `MARGO_PLATFORM_APP_IDS` is empty the router refuses Entra-issued deliveries and logs `Platform token refused from application <id>`. Confirm the ID is Agent 365's, then `azd env set MARGO_PLATFORM_APP_IDS <id>` (several: separated by spaces), provision, redeploy all |
| Allow file links to your tenant | Until this is set, a file link in a question to Copilot is refused for every host. `azd env set MARGO_SHAREPOINT_HOSTS "contoso.sharepoint.com contoso-my.sharepoint.com"` (your own hosts), provision, redeploy all |
| See her state | Storage account › containers named `margo-i-…`, one per hired Margo, with each manager's state under its own prefix. `state.zip` is current; `daily/` holds one copy per day |
| Ship a change | Commit, then `azd provision` (it builds and pins a new image for the router and the provisioner), then redeploy all |
| Update Microsoft's Work IQ skill | The image carries a pinned copy of Microsoft's `workiq` skill (`cloud/vendor/workiq/source.json` names the commit). From `cloud/`: `bash scripts/update-workiq-skill.sh` (or a tag or commit), review the diff, commit, then ship it as above |
| Redeploy all | From `cloud/`: `sh scripts/provisioner.sh redeploy-all`. It queues one redeploy per ready instance; the queue then starts the job again, which redeploys them (`az containerapp job execution list -n $(azd env get-value PROVISIONER_NAME) -g $(azd env get-value RESOURCE_GROUP) -o table` shows both runs). For one instance: `sh scripts/provisioner.sh enqueue ensure <agent-identity-id> <agent-user-id> --redeploy` |
| Keep instances warm | `azd env set MARGO_INSTANCE_MIN_REPLICAS 1`, provision, then redeploy all. For one instance: `az containerapp update -n margo-<key> -g <group> --min-replicas 1`. Optional: a cold instance still answers, but its first answer is slow (see Known limits) |
| Suspend one instance | Disable the agent user in the admin center: the lifecycle event suspends it. Or, from `cloud/`: `sh scripts/provisioner.sh enqueue suspend <agent-identity-id> <agent-user-id>`. The router stops delivering to it at once, and its container app is removed, so nothing runs in the background (no listening in rooms, no notes to the manager); its identity and state stay. Enabling the agent user, or `enqueue ensure` with the same identifiers, deploys the app again (a few minutes); a message to a suspended Margo does not. An instance suspended by an earlier version still has its app: run `enqueue suspend` for it once. If removing the app fails, the provisioner logs `suspend still failing` and tries again every 15 minutes for up to seven days |
| Retire one instance | Delete it in the admin center: the lifecycle event retires it (app and identity go; state stays). Or, from `cloud/`: `sh scripts/provisioner.sh enqueue retire <agent-identity-id> <agent-user-id>`. Remove any mailbox permissions |
| A retirement that is stuck | If the app or identity cannot be removed, the route stays `retired` with an `error` (the router keeps ignoring the instance) and the provisioner tries again by itself: five times 15 minutes apart, then once an hour for as long as it fails, each time logging `ERROR ... the removal is tried again within 60 minute(s)`. Fix what the log names; the next try finishes the removal. To try at once, or for a retirement that got stuck before this version (nothing retries those in a quiet deployment): `sh scripts/provisioner.sh enqueue retire <agent-identity-id> <agent-user-id>` (both identifiers are on the instance's entry in `margo-routes/routes.json`). Until it succeeds the instance's identity still exists |
| Delete a retired instance's or a former manager's state | Nothing deletes it automatically. Delete the instance's `margo-i-…` container (or, for a former manager, their prefix inside it). A deleted container is recoverable for 14 days. Until you do, that state and its daily copies stay ([forgotten information](#how-long-forgotten-information-stays-in-copies)) |
| Change retention | `MARGO_STATE_VERSION_RETENTION_DAYS` (replaced versions of the state bundle, default 30), `MARGO_STATE_DAILY_RETENTION_DAYS` (daily copies, default 30; 0 keeps all), `MARGO_LOG_RETENTION_DAYS` (30), `MARGO_LOG_DAILY_QUOTA_GB` (1). What a manager had Margo forget leaves her current records at once, but earlier copies last longer than these numbers, and for a Margo that is no longer used they are not deleted at all: see [how long forgotten information stays in copies](#how-long-forgotten-information-stays-in-copies) |
| Model settings | `MARGO_MODEL_CAPACITY` (thousand tokens a minute, default 100, shared by every instance), `MARGO_MODEL_SKU` (`GlobalStandard` by default: prompts may be processed outside your region; `DataZoneStandard` or `Standard` keep them closer), `MARGO_MODEL_REASONING_EFFORT` (default `low`; `none` for a model that is not a reasoning model), `MARGO_MAX_INSTANCES` (default 50) |
| Stop everyone's Margo | Block Margo in the admin center |
| Remove everything | Retire instances in the admin center; remove mailbox and calendar delegate permissions and the agent users' licenses; remove the blueprint with the Agent 365 CLI (`a365 cleanup`) and the Work IQ consent grant; delete the "Margo voice page" app registration if you made one; then `azd down --purge` (without `--purge` the AI Services account stays soft-deleted and its name reserved). `azd down` deletes the storage account, and with it every Margo's stored state and daily copies: tell managers first, and copy out anything you must keep |

### How long forgotten information stays in copies

When a manager has Margo forget something, that turn saves a new `state.zip` without it. Copies made before that still
hold it, and three separate mechanisms remove them. None is immediate, and one runs only while the Margo is used:

| Copy | What removes it | When |
| --- | --- | --- |
| Replaced versions of `state.zip` | The storage account's lifecycle rule. Azure runs it whether or not the Margo is used | Once the version is older than `MARGO_STATE_VERSION_RETENTION_DAYS` (default 30), at Azure's next lifecycle run |
| Daily copies (`daily/YYYY-MM-DD.zip` under each manager's prefix) | Margo's own code, on **that manager's first turn of a day**. No storage rule covers them | On each such turn, copies dated more than `MARGO_STATE_DAILY_RETENTION_DAYS` (default 30) before that day are deleted. With `0`, never |
| Anything deleted by the two rows above | Blob versioning and soft delete | A deleted daily copy first becomes a previous version, which the lifecycle rule removes at its next run once it is older than the version retention. A removed version stays recoverable for 14 more days (soft delete, set in `cloud/infra/main.bicep`, not an azd variable) |

So, with the defaults:

- **A Margo that is used every day:** a copy holding the forgotten information is unrecoverable about 44 days after it
  was written (30 days, then 14 of soft delete), plus the wait for Azure's lifecycle run. Azure's timing for that run is
  not something this deployment controls, and it has not been measured here.
- **A Margo whose manager sends her nothing more** (an unused instance, a manager who has left, an instance that was
  retired): replaced versions of `state.zip` still expire as above, but her **daily copies stay** until that manager's
  next turn with her, or until an administrator deletes them. Retiring an instance removes its app and identity and
  leaves its storage container. Nothing enforces the retention period for these copies.

To purge sooner, or for a Margo that is no longer used: with a data role on the storage account (for example Storage
Blob Data Contributor), delete the blobs under that manager's `daily/` prefix in the instance's `margo-i-…` container,
and the previous versions of `state.zip` and of those copies written before the manager's request; or delete the whole
container when the instance is retired. Each stays recoverable for the 14 days of soft delete. To shorten that, lower
`deleteRetentionPolicy` and `containerDeleteRetentionPolicy` in `cloud/infra/main.bicep` and provision **before** you
delete: a new period applies to what is deleted afterwards.

### Upgrading an existing deployment

A deployment that ran without `MARGO_PLATFORM_APP_IDS` stops answering after this upgrade until it is set
([step 6](#6-first-conversation)): unpinned Entra-issued tokens were accepted before and are refused now.

The first provision after upgrading from a version whose provisioner job ran on a schedule deletes and recreates the
job, which the provisioning queue starts. If that provision fails midway there is no job until the next
successful one: rerun `azd provision`, then redeploy all instances.

## Troubleshooting

| Symptom | Likely cause | What to do |
| --- | --- | --- |
| `InvalidResourceLocation` at the start of provisioning | `AZURE_LOCATION` is not a real region | `azd env set AZURE_LOCATION westus3` (or another region with your model) |
| "insufficient quota" or model not found | The model or version is not offered, or has no quota, in that region | Pick another model or region; check `az cognitiveservices usage list` |
| Provisioning stops partway at role assignments | Contributor without RBAC Administrator | Get Owner, or add Role Based Access Control Administrator, and rerun |
| `build-image.sh` refuses to build | Uncommitted changes, or the privacy check failed | Commit, or fix what `tools/check-clean.sh` reports |
| The router keeps restarting with "the agent blueprint client ID is not set" | It was switched to the Margo image without a blueprint | Finish steps 2 and 3 |
| Nothing reaches Margo; the router logs no activities | The messaging endpoint is not registered on the blueprint, or points elsewhere | `a365 setup blueprint --update-endpoint https://<router>/api/messages`; check the Developer Portal configuration |
| Nothing reaches Margo; the router logs `JWT issuer not accepted: <issuer>` | Agent 365 signs with an issuer this version does not accept by default | If the issuer is Microsoft's and not another tenant's, `azd env set MARGO_EXTRA_TOKEN_ISSUERS "<issuer>"`, provision, then redeploy all. Never add another tenant's issuer |
| Every turn fails with "Something failed on my side" right after upgrading | The model deployment refuses the request shape (reasoning settings, or the replayed conversation with `store: false`, which has not been run against a live model) | Check the `turn failed` entry for the status code. `azd env set MARGO_MODEL_REASONING_EFFORT none`, provision, redeploy all; if it persists, report it |
| Nothing reaches Margo; the router logs `Platform token refused from application <id>`, `JWT caller application not accepted` or `JWT is a person's token (scp)` | `MARGO_PLATFORM_APP_IDS` is empty or does not name the application Agent 365 delivers with | Take the application ID from the log line and, if it is Microsoft's platform application, set `MARGO_PLATFORM_APP_IDS` to it ([step 6](#6-first-conversation)) |
| The instance answers 400 and logs `ServiceUrl host is not in the configured allowed hosts` | Agent 365's reply address is not one of the hosts the SDK knows as Microsoft's | If the host is Microsoft's, `azd env set MARGO_EXTRA_SERVICE_HOSTS "<host>"`, provision, redeploy all. Never add a host you do not recognize |
| Margo says she is busy with an earlier request | A long turn for the same manager is still running elsewhere | Wait; messages in the same container queue behind it |
| A hire never gets a container; the provisioner logs `MaxInstancesExceeded` | The instance cap is reached | Raise `MARGO_MAX_INSTANCES`, provision, then `sh scripts/provisioner.sh enqueue ensure <agent-identity-id> <agent-user-id>` |
| Router logs `kind=reject` | An activity without an agent recipient (for example the app itself, not a hired instance) | Chat with a hired instance |
| Activities get 503 and nothing happens | The provisioner has not created the instance, or failed | Check the job's logs and the route's `error` in `margo-routes/routes.json` |
| Instance logs "token exchange failed for margo" | The federated credential is missing on the blueprint, or the agent user is not licensed yet | Add the credential (step 3); license the agent user |
| Every Work IQ read is `blocked` | No consent for the Work IQ scopes (`WorkIQAgent.Ask`, `WorkIQ.Read`, `WorkIQ.Act`), or they are not inheritable | Have an admin finish what `blueprint.sh` printed |
| Margo says she can't find her manager | The directory answered that her account has no manager | Set the manager on Margo's account in the directory |
| "I couldn't check who I work for just now" | The `/me/manager` lookup failed: outage, throttling, or a Copilot Credits denial for Margo's account. It is not a missing manager | Retry. If it persists, check the `workiq` traces for the lookup's status code and Margo's licenses |
| "Something failed on my side before I could finish" | The model call or the turn failed (throttling, content filter, bad `MARGO_TIMEZONE` or `MARGO_PRINCIPAL_ACCESS`) | Check the `turn failed` log entry; fix configuration and retry |
| Every principal source is `blocked` | `obo` mode, and the manager has not signed in through the on-behalf-of handler (or it is not configured) | Have the manager message Margo again in their 1:1 chat and complete the sign-in; check `PRINCIPAL_AUTH_HANDLER` and `MARGO_PRINCIPAL_OAUTH_CONNECTION`; or switch to `delegation` |
| Mail and calendar are `error` in delegation mode | Mailbox permissions missing or not yet propagated | Recheck the two Exchange commands; allow time to propagate |
| "I couldn't load my records safely" | The state lease was busy for two minutes, or a bundle failed its checks | Retry. If it persists, check the storage account and the instance's role on its container |
| Approval spins on the security-policy step | An admin center issue, not the deployment | Open a support request with the page's correlation ID |
| Voice: "Your sign-in can't be used to talk to this Margo" | Wrong tenant, missing `Voice.Talk` scope, or not this Margo's manager | Sign in as the manager; check the `Voice.Talk` scope on the voice page's own app registration (`MARGO_VOICE_CLIENT_ID`), its SPA redirect URI and admin consent. The scope must not be on the blueprint |

## Known limits

- Not verified in a tenant: the on-behalf-of token flow, mail and document notification payloads, **Create
  instance** from the admin center (see step 5), voice, Office documents, the backchannel and listening, answers to
  the manager in a group chat or channel, approval cards (sending on the manager's behalf, and Work IQ
  passing delegated writes through), the state lease under concurrent turns, and the Work IQ billing subject.
- The token check restricts which token issuers, calling applications and reply hosts are accepted. Which values
  Agent 365 really uses is not confirmed:
  each refusal is logged with the value, and `MARGO_EXTRA_TOKEN_ISSUERS`, `MARGO_PLATFORM_APP_IDS` and
  `MARGO_EXTRA_SERVICE_HOSTS` admit it (Troubleshooting). Until `MARGO_PLATFORM_APP_IDS` names the platform's
  application, no Entra-issued token is accepted, so nothing is answered ([step 6](#6-first-conversation)); the test
  tenant ran before this rule, and a pinned delivery has not been run there. Tokens issued by Bot Framework are
  accepted without further binding.
- The model request stores nothing at the service and resends the conversation each turn. This shape has only run
  against fakes.
- Whether the provisioner job's queue trigger fires with the job's managed identity is unverified;
  `az containerapp job start` runs it by hand.
- A scaled-to-zero instance takes longer to start than Agent 365 waits. The router keeps forwarding after Agent 365
  gives up, so the message is answered, just late. A repeated delivery of the same activity is dropped, but only by the
  container that saw the first one and only until it restarts.
- The conversation is kept in the instance's memory (up to 30 minutes without use, bounded in size) and is lost on a
  restart or scale-to-zero; durable facts live in her state.
- Isolation between managers rests on software checks: every instance container holds the blueprint's credential
  identity and could, if compromised, obtain another Margo's token. The provisioner holds **Contributor** on the
  resource group (it creates each instance's app and identity); its role administration is limited to the instance
  roles on the storage account, the registry and the AI account, for service principals other than Margo's own.
- A hired identity is not checked against the directory before a container is created; the instance cap bounds what a
  forged request could create.
- Storage, the registry and the AI account accept connections from public networks (no anonymous access, no shared
  keys, Entra sign-in only). There are no private endpoints.
- Records of messages from colleagues (sender ID and name, conversation, a short excerpt) have no automatic expiry.
- Old images are never purged from the registry. Purge by hand only after a redeploy of all instances: a sleeping
  instance pulls the image it was deployed with.
- Daily-copy deletion lists blobs in Azure; that path is not covered by the offline tests. Voice tool results are not
  marked as untrusted data as chat tool results are.
- The Agent 365 CLI's Graph consent (step 2) is wider than Margo needs; remove it. `blueprint.sh` consents Work IQ's
  `WorkIQ.Act`, which approved actions need; the guard, not Entra, limits writes to the ones the manager approved.
- Agent identities and agent users can carry an expiration date (Entra Agent ID lifecycle, reported
  to default to 180 days; **verify** in your tenant). Check `lifecycle.expirationDateTime` on each
  agent identity and agent user, and extend it before it passes, or the instance stops working.
- She has the same persona and skills as desktop Margo. What she cannot do here (GitHub review load, Azure DevOps
  work items, the team decision registry, the large-file bridge) is listed in `NotHosted`
  (`cloud/src/Margo.Cloud.Engine/NotHosted.cs`), and she says so when a routine needs one of them. The same list
  tells the model that it never calls Work IQ write tools itself: writes go through approval cards.
- Mail from anyone but the manager is queued, never answered; the manager's mail is answered by
  email. Word, Excel and PowerPoint comment notifications have no handler of their own yet; how they
  arrive (and so how Margo treats them) is unverified.
- Notes to the manager's one-to-one chat need that chat to exist (the manager messages Margo once);
  Margo finds it through Work IQ. Until then she emails instead.
- Listening reads only rooms Margo is a member of, a bounded number per pass, and only while her
  container runs; observation runs read with her own token, so in `obo` mode they can't read the
  manager's data. None of this is verified in a tenant.
- Notifications that arrive without a message ID are recorded under an ID derived from their
  sender, conversation and text. Two identical messages in the same thread count as one request.
- Copilot Credits, licenses and Azure usage are charged to the deploying organization. No cost
  estimate is made here.
- The image needs IANA zone files (Alpine's `tzdata`) for the persona's clock and `MARGO_TIMEZONE`; without them every
  turn fails with an unknown time zone. `ls /usr/share/zoneinfo/America/Los_Angeles` in a running container must
  succeed.

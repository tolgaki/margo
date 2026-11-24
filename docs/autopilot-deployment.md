# Deploy Margo as an Agent 365 agent

[Design](autopilot.md) · [One container per instance](autopilot-instance-containers.md) ·
[Voice](autopilot-voice.md) · [Margo Cloud code](../cloud/README.md) · [Trust and safety](safety.md)

**Status: first deployable version, partly proven in one test tenant (November 2026). The fixes made after the
security review of 23 November 2026 have not yet run in a tenant.** Hosted Margo is **Margo
Cloud**, a C# (.NET 10) service in [`cloud/`](../cloud/README.md); it replaced the earlier Python runtime. One end-to-end
run in a test tenant showed: the router receiving Agent 365 activities, the provisioner creating an instance container,
the instance signing in as its agent user, Work IQ answering `/me/manager`, a Teams chat answered by the hired Margo,
and a delegated calendar read. Everything else is built and tested offline against fakes and Microsoft's published
contracts; [Known limits](#known-limits) lists what is still unverified. Deploy it first to a **test tenant with
fictional users**, and run the phase-0 checklist in [Margo.Cloud.Host/VERIFY.md](../cloud/src/Margo.Cloud.Host/VERIFY.md);
the [design](autopilot.md#10-verification-list-phase-0) says what each check proves.
Agent 365 and its CLI are in preview. The commands below were checked against Agent 365 CLI 2.0.1; check them against
`a365 --help` for the version you install.

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

There is no Microsoft Foundry project or hosted agent. Azure AI Services serves the chat model
(and Voice Live, if you turn voice on) directly to each container.

## What this version does

| Margo can | Margo cannot (yet) |
| --- | --- |
| Chat with her manager in Teams and answer as Margo | Do anything outward without her manager's tap on that exact action's card |
| Read her manager's mail and calendar (delegation mode), and what is shared with her own account, through the Work IQ guard | Run on a schedule or brief you unprompted |
| Brief, triage, prep meetings and draft text | Answer email from anyone, including her manager |
| **After her manager approves each one on a card in Teams:** send email and replies from the manager's mailbox (as Margo on their behalf), set up, move or cancel meetings on the manager's calendar, and post Teams messages as herself in chats and channels she is in | Ask for approval from a voice call, or take a "yes" typed in chat or spoken as approval |
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

### People and roles

| Step | Who | Role |
| --- | --- | --- |
| Provision and build | You or an Azure admin | **Owner**, or **Contributor** plus **Role Based Access Control Administrator**, on the resource group; the first provision also registers resource providers, which needs those rights on the **subscription** (or have an admin register them once) |
| Create the blueprint (Agent 365 CLI) | You | **Agent ID Developer** at least; the CLI's permission steps may need **Agent ID Administrator** |
| Consent to Work IQ for the blueprint | Entra admin | **Global Administrator** or **Privileged Role Administrator** |
| Approve Margo | Microsoft 365 admin | **Global Administrator** or **AI Administrator** |
| Hire an instance, license her agent user | Anyone the admin allows; a licence admin | Membership in the hiring scope; **License Administrator** |
| Grant mailbox delegation (delegation mode only) | Exchange admin | Exchange Online permission management |

### Tenant prerequisites

- **Frontier preview** enrolment and the Agent 365 terms accepted. Agent users, mail and
  document notifications, and **Create instance** need it.
- **Licences:** an Agent 365 (or Microsoft 365 E7) licence for the tenant's agent features. Each
  hired Margo's agent user needs a Microsoft 365 licence that includes Exchange Online and Teams for
  her mailbox and chat, and a Microsoft 365 Copilot licence for Work IQ.
- **Work IQ** enabled in the tenant. `scripts/blueprint.sh` checks for its service principal.
- Usage-based billing for Copilot Credits, if your tenant meters Work IQ that way. Which of the
  manager and Margo is billed under on-behalf-of access is still unverified.

- **One tenant.** The Azure subscription must be in the same Entra tenant as Microsoft 365: the router and instances
  trust tokens from the subscription's tenant only.

### What it costs

Everything is billed to your organisation. Fixed while deployed: the router (always one replica, 0.25 vCPU), the
container registry (Basic), Log Analytics ingestion (capped at 1 GB a day by default) and storage. Per use: the chat
model's tokens (the `turn usage` log line shows them per turn), Voice Live minutes, and each instance's container
while it runs (scaled to zero when idle, unless kept warm). Per hired Margo: her agent user's Microsoft 365 and
Copilot licences, and Copilot Credits where Work IQ is metered. No estimate is made here; use the Azure pricing
calculator with your expected number of managers and turns.

### Tools on your machine

macOS, Linux or WSL: the `azd` hooks are POSIX shell scripts and there are no Windows versions.

`az` (Azure CLI), `azd` 1.27.1 or later, the **Agent 365 CLI** (`a365`, a .NET tool), `git`
and `python3` (the hook scripts use it to read JSON). Docker and the .NET SDK are not needed: the image is built
in Azure Container Registry. To build and test Margo Cloud yourself, see [cloud/README.md](../cloud/README.md).

### Choose how Margo reads her manager's work

| Mode | Set | Reaches | Requires |
| --- | --- | --- | --- |
| `delegation` | `azd env set MARGO_PRINCIPAL_ACCESS delegation` | The manager's mail and calendar (direct reads through the delegation), plus everything shared with Margo herself: mail she receives, chats and channels she is in, files and sites shared with her (her own Copilot search included). Copilot search of the manager's whole data reports "unavailable" | The manager makes Margo a calendar delegate (or an Exchange admin grants mailbox access) and shares chats, channels and files with her |
| `obo` (the parameter's default; **not functional in this version**) | `azd env set MARGO_PRINCIPAL_ACCESS obo` | Nothing of the manager's: every principal source reports `blocked` | An on-behalf-of handler, which the image does not configure |

**Use `delegation`.** `obo` is the parameter's default for historical reasons, but the image configures only Margo's
own (agentic) sign-in, so in `obo` mode every principal source reports `blocked`; Margo never falls back to her own
token. That is safe, but not useful. Set `delegation` explicitly in step 1. Set the mode, and the managers' time zone
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

```sh
mkdir -p ~/margo-a365 && cd ~/margo-a365
a365 setup blueprint -n Margo --tenant-id {tenant-id} --no-endpoint
```

Then register the messaging endpoint. `--endpoint-only` reads `a365.config.json` in the current folder; with CLI 2.0.1
that file must name the tenant, the messaging endpoint, `needDeployment: false` (Margo is self-hosted) and
`agentIdentityDisplayName`, or the command refuses to run (`a365 config init` writes it interactively):

```sh
a365 setup blueprint --endpoint-only
```

Use `--update-endpoint https://<router>/api/messages` if the router's address changes. In the Teams Developer
Portal (**Tools › Agent blueprints › Margo › Configuration**), the agent type should be **API
based** and the notification URL should be the same messaging endpoint.

The CLI writes the blueprint's ID to `a365.generated.config.json` as `agentBlueprintId`. If the CLI shows the
blueprint's client secret, delete it in the Entra admin center (**App registrations › Margo › Certificates &
secrets**) unless you use it for something else: Margo never needs it.

Work IQ's scopes can also be added with the CLI instead of `blueprint.sh`:

```sh
a365 setup permissions custom --resource-app-id {work-iq-app-id} --scopes WorkIQAgent.Ask,WorkIQ.Read,WorkIQ.Act
a365 query-entra inheritance        # the Work IQ entry must show kind allAllowed
```

The CLI's own permission steps also consent Microsoft Graph scopes, including send scopes such as `Mail.Send`, and
make them inheritable. This version of Margo does not use Graph directly (all its reads go through Work IQ, and the
guard refuses writes), so remove the Graph grant and its inheritable entry once the instance works, or review them with
your Entra admin before production.

## 3. Connect the blueprint and finish

```sh
cd path/to/margo/cloud
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
   | Short description | Your chief of staff. Proposes; never sends without your yes. |
   | Full description | A personal autopilot that briefs, triages, preps meetings and drafts replies for the person who hires it. Works only for its manager. Every outward action needs that manager's explicit approval of the exact action. |
   | Developer, website | Your organisation; this repository's URL |
   | Terms of use, privacy | [`cloud/terms.md`](../cloud/terms.md) (or your own) |
   | Icons | `cloud/assets/` |
   | Respond without @mention in group chats | Off |

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
   Copilot licences. Without them she has no mailbox, no Teams presence and no Work IQ.
4. Wait a few minutes. The hire event reaches the router, which asks the provisioner to create her
   container (about two minutes). Margo does not message first.

**If Create instance refuses for a missing licence.** In the test tenant the admin center's instance policy showed an
empty list of recognised Agent 365 licences, even though the tenant's Agent 365 subscription and all its service plans
were active, so **Create instance** could not finish. The instance can be created without that page, through the
Microsoft Graph (beta) Entra Agent ID APIs: create an agent identity from the blueprint, create its agent user, set the
agent user's manager and usage location, and assign its licences. Agent 365 then delivered the instance's Teams
messages to the router as usual. Treat this as a workaround for a preview issue, not the supported path.

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

### Sending on your behalf: approval cards

Margo can send email, reply, set up, move or cancel meetings and post Teams messages, each only after the manager taps
**Approve** on a card that shows that exact action:

1. The manager asks in their one-to-one chat ("email Rafa that the review moved to Thursday"). Margo drafts it,
   records the exact Work IQ call, and posts a card: recipients, subject, text or meeting times, and the exact request.
2. **Approve** runs exactly that call, once, and Margo replies with the result; the card loses its buttons. **Reject**
   discards it. A card for an older version, or one already decided, runs nothing.
3. Only the manager's tap in their one-to-one chat counts. A "yes" typed in chat or said in a voice call is not an
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

As the manager, open a chat with **the instance you hired** (by the name you gave it, not the
"Margo" app itself) and try:

- "Brief me": she should read your inbox and calendar and cite what she found.
- "What's in my Teams?": in delegation mode she reads only chats and channels she is in; for the rest she should say so and suggest you add her, not report it as quiet.
- "What's waiting on me?": she reads her work ledger with the same commands as on the desktop.
- "/replies acknowledge": she confirms the setting. `/replies none` turns it off again.
- "/voice": if voice is on, she replies with her voice page's link.

Ask a colleague to message or email her. They should get no reply (or one acknowledgement, if
enabled), and you should see it when you ask "anything from other people?".

### Who Margo answers

Margo works for you only in your one-to-one chat. Everywhere else she answers nobody, apart from two fixed notices that
the model does not write:

| Where | The manager writes | Anyone else writes |
| --- | --- | --- |
| One-to-one chat | A full conversation; the only place she takes instructions and posts approval cards | — |
| Group chat or channel (she receives only messages that @mention her) | One fixed note: she works only in the one-to-one chat. She reads nothing there | No answer. Queued for the manager as an unverified request. With `/replies acknowledge`, one fixed "passed this on" note per conversation, which everyone in that chat sees |
| Meeting | She does not join meetings; a meeting's chat is a group chat (row above) | As above |
| Email to her, or copying her | Never answered; queued | Never answered, not even acknowledged; queued |

Queued requests are listed when the manager asks "anything from other people?". An approval tap counts only in the
one-to-one chat, so nothing she sends can be started from a group chat, a meeting or an email. Apart from the two fixed
notices, the only messages other people get from her are actions the manager approved on a card.

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
| Pin the platform's application | After the first message, the router logs `Platform token accepted from application <id>`. `azd env set MARGO_PLATFORM_APP_IDS <id>`, provision, redeploy all: only that application's tokens are then accepted |
| Limit file links to your tenant | `azd env set MARGO_SHAREPOINT_HOSTS "contoso.sharepoint.com contoso-my.sharepoint.com"` (your own hosts), provision, redeploy all |
| See her state | Storage account › containers named `margo-i-…`, one per hired Margo, with each manager's state under its own prefix. `state.zip` is current; `daily/` holds one copy per day |
| Ship a change | Commit, then `azd provision` (it builds and pins a new image for the router and the provisioner), then redeploy all |
| Redeploy all | From `cloud/`: `sh scripts/provisioner.sh redeploy-all`. It queues one redeploy per ready instance; the queue then starts the job again, which redeploys them (`az containerapp job execution list -n $(azd env get-value PROVISIONER_NAME) -g $(azd env get-value RESOURCE_GROUP) -o table` shows both runs). For one instance: `sh scripts/provisioner.sh enqueue ensure <agent-identity-id> <agent-user-id> --redeploy` |
| Keep instances warm | `azd env set MARGO_INSTANCE_MIN_REPLICAS 1`, provision, then redeploy all. For one instance: `az containerapp update -n margo-<key> -g <group> --min-replicas 1`. Optional: a cold instance still answers, but its first answer is slow (see Known limits) |
| Retire one instance | Delete it in the admin center: the lifecycle event retires it (app and identity go; state stays). Or `dotnet /app/provisioner/Margo.Cloud.Provisioner.dll enqueue retire <agent-identity-id> <agent-user-id>`. Remove any mailbox permissions |
| Delete a retired instance's or a former manager's state | Nothing deletes it automatically. Delete the instance's `margo-i-…` container (or, for a former manager, their prefix inside it). A deleted container is recoverable for 14 days |
| Change retention | `MARGO_STATE_VERSION_RETENTION_DAYS` (replaced versions of the state bundle, default 30), `MARGO_STATE_DAILY_RETENTION_DAYS` (daily copies, default 30; 0 keeps all), `MARGO_LOG_RETENTION_DAYS` (30), `MARGO_LOG_DAILY_QUOTA_GB` (1). What a manager had Margo forget is gone from every copy after the state retention at the latest |
| Model settings | `MARGO_MODEL_CAPACITY` (thousand tokens a minute, default 100, shared by every instance), `MARGO_MODEL_SKU` (`GlobalStandard` by default: prompts may be processed outside your region; `DataZoneStandard` or `Standard` keep them closer), `MARGO_MODEL_REASONING_EFFORT` (default `low`; `none` for a model that is not a reasoning model), `MARGO_MAX_INSTANCES` (default 50) |
| Stop everyone's Margo | Block Margo in the admin center |
| Remove everything | Retire instances in the admin center; remove mailbox and calendar delegate permissions and the agent users' licences; remove the blueprint with the Agent 365 CLI (`a365 cleanup`) and the Work IQ consent grant; delete the "Margo voice page" app registration if you made one; then `azd down --purge` (without `--purge` the AI Services account stays soft-deleted and its name reserved) |

## Moving from the earlier Foundry deployment

Earlier versions ran Margo as a Microsoft Foundry hosted agent. That blueprint belongs to
Foundry and cannot be pointed at your own container, so this route starts with a new one.

1. Block the old Margo in the admin center and retire its instances.
2. Follow steps 1 to 6 above in the same azd environment (the old azd setting `MARGO_HOSTING` is no longer read; each instance's container sets it itself). Provisioning is incremental, so the Foundry project and its hosted agent stay
   until you delete them in the Azure portal; delete them once the new Margo works.
3. State is not carried over: each new hire has a new agent identity and starts fresh. The old
   `margo-…` state containers stay until you delete them.

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
| Nothing reaches Margo; the router logs `JWT is a person's token (scp)` or `JWT caller application not accepted` | Agent 365 delivers with a kind of token this version refuses by default | Take the application ID from the log line and, if it is Microsoft's platform application, set `MARGO_PLATFORM_APP_IDS` to it |
| The instance answers 400 and logs `ServiceUrl host is not in the configured allowed hosts` | Agent 365's reply address is not one of the hosts the SDK knows as Microsoft's | If the host is Microsoft's, `azd env set MARGO_EXTRA_SERVICE_HOSTS "<host>"`, provision, redeploy all. Never add a host you do not recognise |
| Margo says she is busy with an earlier request | A long turn for the same manager is still running elsewhere | Wait; messages in the same container queue behind it |
| A hire never gets a container; the provisioner logs `MaxInstancesExceeded` | The instance cap is reached | Raise `MARGO_MAX_INSTANCES`, provision, then `sh scripts/provisioner.sh enqueue ensure <agent-identity-id> <agent-user-id>` |
| Router logs `kind=reject` | An activity without an agent recipient (for example the app itself, not a hired instance) | Chat with a hired instance |
| Activities get 503 and nothing happens | The provisioner has not created the instance, or failed | Check the job's logs and the route's `error` in `margo-routes/routes.json` |
| Instance logs "token exchange failed for margo" | The federated credential is missing on the blueprint, or the agent user is not licensed yet | Add the credential (step 3); license the agent user |
| Every Work IQ read is `blocked` | No consent for the Work IQ scopes (`WorkIQAgent.Ask`, `WorkIQ.Read`, `WorkIQ.Act`), or they are not inheritable | Have an admin finish what `blueprint.sh` printed |
| Margo says she can't find her manager | The directory answered that her account has no manager | Set the manager on Margo's account in the directory |
| "I couldn't check who I work for just now" | The `/me/manager` lookup failed: outage, throttling, or a Copilot Credits denial for Margo's account. It is not a missing manager | Retry. If it persists, check the `workiq` traces for the lookup's status code and Margo's licences |
| "Something failed on my side before I could finish" | The model call or the turn failed (throttling, content filter, bad `MARGO_TIMEZONE` or `MARGO_PRINCIPAL_ACCESS`) | Check the `turn failed` log entry; fix configuration and retry |
| Every principal source is `blocked` | `obo` mode without a working on-behalf-of handler | Switch to `delegation`, or complete the handler setup |
| Mail and calendar are `error` in delegation mode | Mailbox permissions missing or not yet propagated | Recheck the two Exchange commands; allow time to propagate |
| "I couldn't load my records safely" | The state lease was busy for two minutes, or a bundle failed its checks | Retry. If it persists, check the storage account and the instance's role on its container |
| Approval spins on the security-policy step | An admin center issue, not the deployment | Open a support request with the page's correlation ID |
| Voice: "Your sign-in can't be used to talk to this Margo" | Wrong tenant, missing `Voice.Talk` scope, or not this Margo's manager | Sign in as the manager; check the `Voice.Talk` scope on the blueprint, the voice sign-in app's redirect URI and consent, and `MARGO_VOICE_CLIENT_ID` |

## Known limits

- Verified once, in one test tenant: the self-hosted blueprint with the Agent 365 CLI 2.0.1, the router's token
  check and routing on the activity's agent identity fields, instance provisioning, agentic sign-in, Work IQ reads as
  Margo, a Teams chat turn, and delegated calendar reads. Still unverified: the on-behalf-of token flow, delegated
  mail reads, mail and document notification payloads, **Create instance** from the admin center (see step 5), voice,
  the state lease under concurrent turns, and the Work IQ billing subject.
- **None of the changes made after the 23 November 2026 security review has run in a tenant.** They restrict which
  token issuers, calling applications and reply hosts are accepted. Which values Agent 365 really uses is unconfirmed:
  each refusal is logged with the value, and `MARGO_EXTRA_TOKEN_ISSUERS`, `MARGO_PLATFORM_APP_IDS` and
  `MARGO_EXTRA_SERVICE_HOSTS` admit it (Troubleshooting). Until `MARGO_PLATFORM_APP_IDS` is set, any application
  registered in your tenant that can obtain its own token for the blueprint is accepted as the platform; a signed-in
  person's token never is. Tokens issued by Bot Framework are accepted without further binding.
- The model request now stores nothing at the service and resends the conversation each turn. This shape has only
  run against fakes.
- The first provision after upgrading deletes and recreates the provisioner job (its trigger changed from a schedule to
  the provisioning queue). If that provision fails midway there is no job until the next successful one. Whether the
  queue trigger fires with the job's managed identity is unverified; `az containerapp job start` runs it by hand.
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
- She has the same persona and skills as desktop Margo. What she cannot do here yet (GitHub
  review load, Azure DevOps work items, the team decision registry, the large-file bridge,
  approvals and sends) is listed in `NotHosted` (`cloud/src/Margo.Cloud.Engine/NotHosted.cs`), and she says so
  when a routine needs one of them.
- Mail is queued, never answered. Word, Excel and PowerPoint comment notifications have no
  handler of their own yet; how they arrive (and so how Margo treats them) is unverified.
- The unattended message to the manager's one-to-one chat is designed but not available: it
  needs the Graph chat ID recorded at hire, and the activity's conversation ID is a different
  identifier. Until a hire-time record exists, Margo answers only in the conversation turn.
- Notifications that arrive without a message ID are recorded under an ID derived from their
  sender, conversation and text. Two identical messages in the same thread count as one request.
- Copilot Credits, licences and Azure usage are charged to the deploying organisation. No cost
  estimate is made here.
- The image is ASP.NET Core 10 on Alpine plus the `tzdata` package, which the persona's clock and Margo's time zone
  (`MARGO_TIMEZONE`) need; an image without IANA zone files fails every turn with an unknown time zone. Check
  `ls /usr/share/zoneinfo/America/Los_Angeles` in a running container (VERIFY.md, step 0). The image has been
  built with Docker outside Azure, with one substitution: the Alpine package mirror was unreachable there, so the
  zone files were copied in instead of `apk add tzdata`, which therefore remains unverified. In that image the host,
  the router and the provisioner started as the non-root user, the host and router answered their health and probe
  paths, and `margo-state` created its SQLite databases. It has since been built with `az acr build` and run in a test tenant.

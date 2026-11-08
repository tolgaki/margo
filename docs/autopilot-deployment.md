# Deploy Margo as an Agent 365 agent

[Design](autopilot.md) · [One container per instance](autopilot-instance-containers.md) ·
[Voice](autopilot-voice.md) · [Margo Cloud code](../cloud/README.md) · [Trust and safety](safety.md)

**Status: first deployable version, not yet proven in a live tenant.** Hosted Margo is **Margo Cloud**, a C#
(.NET 10) service in [`cloud/`](../cloud/README.md); it replaced the earlier Python runtime. Everything here is built
and tested offline against fakes and Microsoft's published contracts. Nobody has yet run this
route end to end in a real tenant. Deploy it first to a **test tenant with fictional users**, and
treat the first run as the phase-0 verification: the step-by-step checklist is
[Margo.Cloud.Host/VERIFY.md](../cloud/src/Margo.Cloud.Host/VERIFY.md), and the [design](autopilot.md#10-verification-list-phase-0)
says what each check proves.
Agent 365 and its CLI are in preview; where a step names a CLI flag, check it against
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
| Chat with her manager in Teams and answer as Margo | Send, reply, post, RSVP, move meetings or delete anything |
| Read her manager's mail and calendar (and, with on-behalf-of access, Teams and files) through the Work IQ guard | Run on a schedule or brief you unprompted |
| Brief, triage, prep meetings and draft text for her manager to send | Answer email from anyone, including her manager |
| Queue messages and mail other people send her, as untrusted requests for her manager | Use approval cards (the `approve` reply setting acts as `none`) |
| Send one fixed "passed this on" note per conversation, if her manager turns that on | Join meetings live |
| Talk by voice with her manager, if voice is turned on | Talk to any MCP server except Work IQ |

## What gets created

`azd provision` in `cloud/` creates, in one resource group:

| Resource | Why |
| --- | --- |
| Azure AI Services account with a chat model deployment | Margo's model, and Voice Live for voice. Entra sign-in only; local keys are off |
| Azure Container Registry | Holds the Margo Cloud image (`cloud/Dockerfile`), built in the cloud from this repository |
| Log Analytics and Application Insights | Operational logs. Message content capture is off |
| Storage account (no public access, no shared keys, versioning and 14-day soft delete) | Each hired Margo's private state, the route table and the provisioning queue |
| Container Apps environment, the **router** app and the **provisioner** job | The public messaging endpoint, and the job that creates one container per hired Margo |
| Three managed identities | Router, provisioner, and the role-less identity that instance containers use to act as the blueprint |

The Agent 365 CLI then creates the **agent blueprint** in Entra and registers the router as its
messaging endpoint. The post-provision hook (`scripts/postprovision.sh`) does the rest:

1. Grants the router and the provisioner their roles, by role name. The provisioner may grant
   only the four roles an instance needs (an ABAC condition): its state container, image pull,
   the chat model (`Cognitive Services OpenAI User`) and Voice Live (`Cognitive Services User`).
2. Runs the repository privacy check, builds the image from `cloud/Dockerfile` with `az acr build`, pins it
   by digest and records it in the azd environment (`MARGO_PINNED_IMAGE`), so the next provision keeps the router on
   a Margo image instead of resetting it to the public placeholder. It refuses a checkout with uncommitted changes
   unless `ALLOW_DIRTY=true`. The one image serves all three roles: the router and the provisioner job run `dotnet /app/router/Margo.Cloud.Router.dll` and
   `dotnet /app/provisioner/Margo.Cloud.Provisioner.dll run`; instance containers run the host,
   `dotnet /app/host/Margo.Cloud.Host.dll`.
3. Once the blueprint exists and the router runs a pinned image (from the second provision on): switches the router
   and the provisioner to the new image and blueprint,
   then runs `scripts/blueprint.sh`, which adds the blueprint's federated credential for instance
   containers and gives the blueprint Work IQ's delegated `WorkIQAgent.Ask` scope (inheritable by
   every hired Margo, with tenant-wide consent). Any of these the deployer may not do is printed as
   a step for an administrator instead.

## Before you start

### People and roles

| Step | Who | Role |
| --- | --- | --- |
| Provision and build | You or an Azure admin | **Owner**, or **Contributor** plus **Role Based Access Control Administrator**, on the resource group |
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

### Tools on your machine

`az` (Azure CLI), `azd` 1.27.1 or later, the **Agent 365 CLI** (`a365`, a .NET tool), `git`, `curl`
and `python3` (the hook scripts use it to read JSON). Docker and the .NET SDK are not needed: the image is built
in Azure Container Registry. To build and test Margo Cloud yourself, see [cloud/README.md](../cloud/README.md).

### Choose how Margo reads her manager's work

| Mode | Set | Reaches | Requires |
| --- | --- | --- | --- |
| `delegation` | `azd env set MARGO_PRINCIPAL_ACCESS delegation` | Mail and calendar only. Teams, files and Copilot search report "unavailable" | An Exchange admin grants Margo's account access to the manager's mailbox and calendar after hiring |
| `obo` (default) | `azd env set MARGO_PRINCIPAL_ACCESS obo` and `azd env set PRINCIPAL_AUTH_HANDLER {handler-name}` | Everything the manager can see through Work IQ | An on-behalf-of user-authorisation handler in the Agents SDK configuration |

**Recommendation for the first deployment: `delegation`.** Its moving parts are all documented.
With `obo` and no working handler, every principal source reports `blocked`; Margo never falls
back to her own token. That is safe, but not useful. Set the mode, and the managers' time zone
(`azd env set MARGO_TIMEZONE Europe/Berlin`; default `America/Los_Angeles`), before provisioning;
both reach every instance container. After a change, provision and then redeploy all instances.

## 1. Provision Azure

All commands run from `cloud/` in a clean checkout of this repository.

```sh
cd cloud
az login --tenant {tenant-id}
azd auth login --tenant-id {tenant-id}

azd env new margo-test
azd env set AZURE_LOCATION westus3
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
a365 config init
```

Answer for a **self-hosted** agent: no Azure deployment by the CLI (`needDeployment` false), and
the messaging endpoint from step 1. Name the blueprint "Margo". Then create the blueprint and
register the endpoint:

```sh
a365 setup blueprint --messaging-endpoint https://<router>/api/messages
```

If you created the blueprint without an endpoint, or the router's address changed, register it
with `a365 setup blueprint --endpoint-only` (or `--update-endpoint <url>`). In the Teams Developer
Portal (**Tools › Agent blueprints › Margo › Configuration**), the agent type should be **API
based** and the notification URL should be the same messaging endpoint.

The CLI writes the blueprint's ID to `a365.generated.config.json` as `agentBlueprintId`.

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

1. Publish Margo with the Agent 365 CLI (`a365 publish`; see `a365 publish --help`). Use these
   details:

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

You can also create a container ahead of time, with the provisioner's settings (for example as a one-off execution
of the provisioner job with this command):

```sh
dotnet /app/provisioner/Margo.Cloud.Provisioner.dll enqueue ensure <agenticAppId> <agenticUserId>
```

### Delegation mode: grant mailbox access

An Exchange admin runs, with the real addresses:

```powershell
Add-MailboxPermission -Identity dana@example.com -User margo-dana@example.com -AccessRights FullAccess -AutoMapping $false
Add-MailboxFolderPermission -Identity "dana@example.com:\Calendar" -User margo-dana@example.com -AccessRights Reviewer
```

Full Access is what Microsoft Graph needs to read another mailbox. It also permits changes at the
Exchange level; this version's guard refuses every write, but that is a software control, not
an Exchange one. Remove the permissions when you retire the instance.

## 6. First conversation

As the manager, open a chat with **the instance you hired** (by the name you gave it, not the
"Margo" app itself) and try:

- "Brief me": she should read your inbox and calendar and cite what she found.
- "What's in my Teams?": in delegation mode she should say Teams is unavailable, not quiet.
- "What's waiting on me?": she reads her work ledger with the same commands as on the desktop.
- "/replies acknowledge": she confirms the setting. `/replies none` turns it off again.
- "/voice": if voice is on, she replies with her voice page's link.

Ask a colleague to message or email her. They should get no reply (or one acknowledgement, if
enabled), and you should see it when you ask "anything from other people?".

Margo works for you only in your one-to-one chat. If you @mention her in a group chat or
channel she answers with a fixed note and reads nothing, so your mail and calendar never appear
in front of other people.

## Voice (optional)

```sh
azd env set MARGO_VOICE_LIVE_MODEL gpt-realtime-2.1
azd provision
```

Then, on the blueprint, expose a delegated `Voice.Talk` scope and add each instance's
`https://<router>/i/<instance>/voice/` as a single-page-app redirect URI. Redeploy existing
instances (below). Details, the custom voice and limits: [Voice](autopilot-voice.md).

## Operate

| Task | How |
| --- | --- |
| See what Margo read | Application Insights traces named `workiq`: subject, tool, path (no query text), status and outcome. No message content |
| See her state | Storage account › containers named `margo-i-…`, one per hired Margo, with each manager's state under its own prefix. `state.zip` is current; `daily/` holds one copy per day |
| Ship a change | Commit, then `azd provision` (it builds and pins a new image for the router and the provisioner), then redeploy all |
| Redeploy all | `dotnet /app/provisioner/Margo.Cloud.Provisioner.dll redeploy-all` with the provisioner's settings, for example as a one-off execution of the provisioner job with that command; or `… enqueue ensure <app> <user> --redeploy` for one |
| Keep instances warm | Add `"instanceMinReplicas": { "value": 1 }` to `infra/main.parameters.json`, provision, then redeploy all |
| Retire one instance | Delete it in the admin center: the lifecycle event retires it (app and identity go; state stays). Or `… enqueue retire <app> <user>`. Remove any mailbox permissions |
| Delete a retired instance's state | Delete its `margo-i-…` container after the retention period |
| Stop everyone's Margo | Block Margo in the admin center |
| Remove everything | Retire instances in the admin center, remove the blueprint with the Agent 365 CLI (`a365 cleanup`), then `azd down` |

## Moving from the earlier Foundry deployment

Earlier versions ran Margo as a Microsoft Foundry hosted agent. That blueprint belongs to
Foundry and cannot be pointed at your own container, so this route starts with a new one.

1. Block the old Margo in the admin center and retire its instances.
2. Follow steps 1 to 6 above in the same azd environment (`MARGO_HOSTING` is no longer read). Provisioning is incremental, so the Foundry project and its hosted agent stay
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
| Router logs `kind=reject` | An activity without an agent recipient (for example the app itself, not a hired instance) | Chat with a hired instance |
| Activities get 503 and nothing happens | The provisioner has not created the instance, or failed | Check the job's logs and the route's `error` in `margo-routes/routes.json` |
| Instance logs "token exchange failed for margo" | The federated credential is missing on the blueprint, or the agent user is not licensed yet | Add the credential (step 3); license the agent user |
| Every Work IQ read is `blocked` | No consent for `WorkIQAgent.Ask`, or it is not inheritable | Have an admin finish what `blueprint.sh` printed |
| Margo says she can't find her manager | The directory answered that her account has no manager | Set the manager on Margo's account in the directory |
| "I couldn't check who I work for just now" | The `/me/manager` lookup failed: outage, throttling, or a Copilot Credits denial for Margo's account. It is not a missing manager | Retry. If it persists, check the `workiq` traces for the lookup's status code and Margo's licences |
| "Something failed on my side before I could finish" | The model call or the turn failed (throttling, content filter, bad `MARGO_TIMEZONE` or `MARGO_PRINCIPAL_ACCESS`) | Check the `turn failed` log entry; fix configuration and retry |
| Every principal source is `blocked` | `obo` mode without a working on-behalf-of handler | Switch to `delegation`, or complete the handler setup |
| Mail and calendar are `error` in delegation mode | Mailbox permissions missing or not yet propagated | Recheck the two Exchange commands; allow time to propagate |
| "I couldn't load my records safely" | The state lease was busy for two minutes, or a bundle failed its checks | Retry. If it persists, check the storage account and the instance's role on its container |
| Approval spins on the security-policy step | An admin center issue, not the deployment | Open a support request with the page's correlation ID |
| Voice: "Your sign-in can't be used to talk to this Margo" | Wrong tenant, missing `Voice.Talk` scope, or not this Margo's manager | Sign in as the manager; check the scope and redirect URI on the blueprint |

## Known limits

- Unverified in a live tenant: the Agent 365 CLI steps for a self-hosted blueprint, the
  on-behalf-of token flow, activity identity fields (`recipient.agenticAppId`,
  `recipient.agenticUserId`, `from.aadObjectId`), notification payloads, the inheritable-permission
  call in `blueprint.sh`, and the Work IQ billing subject.
- Agent identities and agent users can carry an expiration date (Entra Agent ID lifecycle, reported
  to default to 180 days; **verify** in your tenant). Check `lifecycle.expirationDateTime` on each
  agent identity and agent user, and extend it before it passes, or the instance stops working.
- Conversation continuity (`previous_response_id`) is kept in the container's memory, so a restart
  or scale-to-zero starts a fresh thread; durable facts live in her state, not the thread.
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
  paths, and `margo-state` created its SQLite databases. It has not been built with `az acr build` or run in a tenant.

# Deploy Margo as a Foundry autopilot

[Design](autopilot.md) · [Autopilot code](../autopilot/README.md) · [Trust and safety](safety.md)

**Status: first deployable version, not yet proven in a live tenant.** Everything here is built
and tested offline against fakes and Microsoft's published contracts. Nobody has yet run it end
to end in a real tenant. Deploy it first to a **test tenant with fictional users**, and treat the
first run as the phase-0 verification described in the [design](autopilot.md#10-verification-list-phase-0).

## What this version does

| Margo can | Margo cannot (yet) |
| --- | --- |
| Chat with her manager in Teams and answer as Margo | Send, reply, post, RSVP, move meetings or delete anything |
| Read her manager's mail and calendar (and, with on-behalf-of access, Teams and files) through the Work IQ guard | Run on a schedule or brief you unprompted |
| Brief, triage, prep meetings and draft text for her manager to send | Answer email from anyone, including her manager |
| Queue messages other people send her, as untrusted requests for her manager | Use approval cards (the `approve` reply setting acts as `none`) |
| Send one fixed "passed this on" note per conversation, if her manager turns that on | Use Foundry's managed memory |
| Keep her private state in SQLite, shared safely across sandboxes through leased Blob storage | Talk to any MCP server except Work IQ |

## What gets created

`azd provision` in `autopilot/` creates, in one resource group:

| Resource | Why |
| --- | --- |
| Foundry account and project, with a chat model deployment | Hosts Margo and her model |
| Azure Container Registry | Holds the Margo image, built in the cloud from this repository |
| Log Analytics and Application Insights | Operational logs. Message content capture is off |
| Storage account (no public access, no shared keys, versioning and 14-day soft delete) | Margo's private state: one container per hired instance and manager |

Then the post-provision hook:

1. Grants the project's identity `AcrPull`, `Cognitive Services User` and
   `Monitoring Metrics Publisher`, by role name.
2. Runs the repository privacy check and builds the image with `az acr build`. It refuses a
   checkout with uncommitted changes unless `ALLOW_DIRTY=true`.
3. Creates a hosted-agent version that speaks the Teams activity protocol, waits for it to
   become active, and requires Bot Service RBAC on its endpoint.
4. Grants the agent's identity `Foundry User` (formerly `Azure AI User`) on the project and
   `Storage Blob Data Contributor` on the storage account.
5. Publishes Margo as an autopilot blueprint, requesting only Work IQ's delegated
   `WorkIQAgent.Ask` permission, and leaves it waiting for admin approval.

## Before you start

### People and roles

| Step | Who | Role |
| --- | --- | --- |
| Provision and build | You or an Azure admin | **Owner**, or **Contributor** plus **Role Based Access Control Administrator**, on the resource group; **Foundry User** on the project |
| Approve the blueprint | Microsoft 365 admin | **Global Administrator** or **AI Administrator** |
| Grant mailbox delegation (delegation mode only) | Exchange admin | Exchange Online permission management |
| Hire an instance | Anyone the admin allows | Membership in the hiring scope |

### Tenant prerequisites

- Frontier preview enrolment, Agent 365 terms accepted, and a free autopilot licence seat. Each
  hired instance uses one seat.
- Work IQ enabled in the tenant. `publish.sh` checks for its service principal and stops if it
  is missing.
- Usage-based billing for Copilot Credits, with a spending policy that covers Work IQ for both
  the manager and Margo's agent user. Which of the two is billed under on-behalf-of access is
  still unverified.

### Tools on your machine

`az` (Azure CLI), `azd` 1.27.1 or later, `git`, `curl` and `python3`. Docker is not needed;
the image is built in Azure Container Registry.

### Choose how Margo reads her manager's work

| Mode | Set | Reaches | Requires |
| --- | --- | --- | --- |
| `delegation` | `azd env set MARGO_PRINCIPAL_ACCESS delegation` | Mail and calendar only. Teams, files and Copilot search report "unavailable" | An Exchange admin grants Margo's account access to the manager's mailbox and calendar after hiring |
| `obo` (default) | `azd env set MARGO_PRINCIPAL_ACCESS obo` and `azd env set PRINCIPAL_AUTH_HANDLER {handler-name}` | Everything the manager can see through Work IQ | An on-behalf-of user-authorisation handler in the Agents SDK configuration, and `WorkIQAgent.Ask` as an inheritable permission on the blueprint |

**Recommendation for the first deployment: `delegation`.** Its moving parts are all documented.
The on-behalf-of path depends on how the Agents SDK obtains the manager's token inside an
autopilot conversation, which is the first item on the verification list. With `obo` and no
working handler, every principal source reports `blocked`; Margo never falls back to her own
token. That is safe, but not useful.

## Deploy

All commands run from `autopilot/` in a clean checkout of this repository.

```sh
cd autopilot
az login --tenant {tenant-id}
azd auth login --tenant-id {tenant-id}

azd env new margo-test
azd env set AZURE_LOCATION {region-with-hosted-agents}
azd env set MARGO_PRINCIPAL_ACCESS delegation
azd env set MARGO_TIMEZONE America/Los_Angeles      # the manager's time zone

azd provision --preview                             # see what will be created
azd provision                                       # create, build, deploy, publish
```

Optional settings: `MARGO_MODEL_NAME` and `MARGO_MODEL_VERSION` (defaults match Microsoft's
sample), `MARGO_APP_VERSION` (defaults to `VERSION`), and `SKIP_PUBLISH=true` to stop before
the approval request.

**Expected result:** the hook ends with "Done. Next: a Microsoft 365 admin approves Margo…",
and `azd env get-values` shows `BLUEPRINT_CLIENT_ID`, `AGENT_VERSION` and `MARGO_IMAGE`.

## Approve (Microsoft 365 admin)

1. In the [Microsoft 365 admin center](https://admin.cloud.microsoft/?#/agents/all/requested),
   open **Agents › All agents › Requests** and select **Margo** (state *Pending activate*).
2. **Publish to users:** choose who may create instances. That is the hiring scope; Margo does
   not restrict it.
3. **Apply template:** keep Microsoft's default protections. Add policies that block external
   recipients and apply your DLP rules.
4. **Accept permissions:** review and grant consent. Margo asks only for Work IQ's
   `WorkIQAgent.Ask`. For `obo` mode, also make that delegated permission inheritable on the
   blueprint ([Entra guidance](https://learn.microsoft.com/en-us/entra/agent-id/configure-inheritable-permissions-blueprints)).
5. **Review and finish**, then check that **Registry** shows Margo as *Available*.

## Hire an instance

1. In Teams, open **Apps › Agents for your team** and select **Margo › Create instance**.
2. Name it, set its alias and domain, and **make sure the manager is the person Margo will work
   for**. Margo looks up her manager in the directory on every turn and treats only that person's
   messages as instructions. If the manager changes, the new manager starts with fresh state
   and the previous person's state is never opened for them.
3. Wait a few minutes for the account to appear. Margo does not message first.

### Delegation mode: grant mailbox access

An Exchange admin runs, with the real addresses:

```powershell
Add-MailboxPermission -Identity dana@example.com -User margo-dana@example.com -AccessRights FullAccess -AutoMapping $false
Add-MailboxFolderPermission -Identity "dana@example.com:\Calendar" -User margo-dana@example.com -AccessRights Reviewer
```

Full Access is what Microsoft Graph needs to read another mailbox. It also permits changes at the
Exchange level; this version's guard refuses every write, but that is a software control, not
an Exchange one. Remove the permissions when you retire the instance.

## First conversation

As the manager, open a chat with Margo and try:

- "Brief me": she should read your inbox and calendar and cite what she found.
- "What's in my Teams?": in delegation mode she should say Teams is unavailable, not quiet.
- "What's waiting on me?": she reads her work ledger with the same commands as on the desktop.
- "/replies acknowledge": she confirms the setting. `/replies none` turns it off again.

Ask a colleague to message her. They should get no reply (or one acknowledgement, if enabled),
and you should see it when you ask "anything from other people?".

Margo works for you only in your one-to-one chat. If you @mention her in a group chat or
channel she answers with a fixed note and reads nothing, so your mail and calendar never appear
in front of other people.

## Operate

| Task | How |
| --- | --- |
| See what Margo read | Application Insights traces named `workiq`: subject, tool, path (no query text), status and outcome. No message content |
| See her state | Storage account › containers named `margo-…`, one per instance and manager. `state.zip` is current; `daily/` holds one copy per day |
| Ship a change | Commit, then `azd provision` again, or run `scripts/build-image.sh` and `scripts/create-agent.sh`. Set a new `MARGO_APP_VERSION` and run `scripts/publish.sh` when display details change |
| Stop everyone's Margo | Block the blueprint in the admin center |
| Stop one instance | Delete or disable the instance in the admin center, remove any mailbox permissions, then delete its `margo-…` container if its data should go |
| Remove everything | `azd down`. Hired instances and their agent accounts are not removed by this; retire them in the admin center first |

## Troubleshooting

| Symptom | Likely cause | What to do |
| --- | --- | --- |
| Provisioning stops partway at role assignments | Contributor without RBAC Administrator | Get Owner, or add Role Based Access Control Administrator, and rerun |
| `publish.sh`: "Work IQ is not enabled" | No Work IQ service principal in the tenant | Enable Work IQ for the tenant, then rerun `scripts/publish.sh` |
| `build-image.sh` refuses to build | Uncommitted changes, or the privacy check failed | Commit, or fix what `tools/check-clean.sh` reports |
| Margo says she can't find her manager | The directory answered that her account has no manager | Set the manager on Margo's account in the directory |
| "I couldn't check who I work for just now" | The `/me/manager` lookup failed: outage, throttling, or a Copilot Credits denial for Margo's account. It is not a missing manager | Retry. If it persists, check the `workiq` traces for the lookup's status code and Margo's spending-policy assignment |
| "Something failed on my side before I could finish" | The model call or the turn failed (throttling, content filter, bad `MARGO_TIMEZONE` or `MARGO_PRINCIPAL_ACCESS`) | Check the `turn failed` log entry; fix configuration and retry |
| Every principal source is `blocked` | `obo` mode without a working on-behalf-of handler | Switch to `delegation`, or complete the handler setup |
| Mail and calendar are `error` in delegation mode | Mailbox permissions missing or not yet propagated | Recheck the two Exchange commands; allow time to propagate |
| "I couldn't load my records safely" | The state lease was busy for two minutes, or a bundle failed its checks | Retry. If it persists, check the storage account and the agent's `Storage Blob Data Contributor` role |
| "Version already exists" on publish | Same `MARGO_APP_VERSION` as before | Set a new version |

## Known limits

- Unverified in a live tenant: on-behalf-of token flow, activity identity fields
  (`recipient.agenticAppId`, `from.aadObjectId`), notification payloads, Work IQ billing subject,
  and whether the publish API takes an icon. The icons are in `autopilot/assets/` for the admin
  center or app package.
- Conversation continuity (`previous_response_id`) is kept per sandbox, so a new sandbox starts a
  fresh thread; durable facts live in her state, not the thread.
- She has the same persona and skills as desktop Margo. What she cannot do here yet (GitHub
  review load, Azure DevOps work items, the team decision registry, the large-file bridge,
  approvals and sends) is listed in `autopilot/src/margo_autopilot/parity.py`, and she says so
  when a routine needs one of them.
- The unattended message to the manager's one-to-one chat is designed but not available: it
  needs the Graph chat ID recorded at hire, and the activity's conversation ID is a different
  identifier. Until a hire-time record exists, Margo answers only in the conversation turn.
- Notifications that arrive without a message ID are recorded under an ID derived from their
  sender, conversation and text. Two identical messages in the same thread count as one request.
- Copilot Credits and Azure usage are charged to the deploying organisation. No cost estimate is
  made here.

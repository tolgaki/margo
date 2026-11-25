# Identity and consent

Work IQ answers as the subject of the token. `/me/messages` is the signed-in user's mail, whoever
that is. So the first design question for any Work IQ agent is **whose token**, and the second is
**who consented to what**. This page answers both for Margo's two runtimes. The full Margo Cloud
reasoning is in the [design record](../autopilot.md#3-identity-model); what ships is in the
[deployment guide](../autopilot-deployment.md).

## Two kinds of identity

| | Delegated user (harness) | Custom agent identity (Margo Cloud) |
| --- | --- | --- |
| Who signs in | You, in a browser, when the harness first calls Work IQ | The agent's own service, with no person at a keyboard |
| Subject of the token | You | Margo's agent user, or the manager (see below) |
| Consent | You consent to the harness's Work IQ client, or an admin consents for the tenant | An admin consents once, on the agent blueprint, as inheritable permissions |
| Example | Desktop Margo in GitHub Copilot CLI ([register Work IQ](../../samples/harness/README.md)) | [Margo Cloud](../margo-cloud.md) |

### Delegated user: desktop

The harness owns the sign-in. GitHub Copilot CLI's Work IQ connection opens a browser for a
Microsoft Entra ID sign-in on first use and keeps the token itself; Margo never sees it. Every
call is yours, limited to what you can see and to what the tenant's policy allows. If your tenant
requires admin consent for the Work IQ application, the first call shows a consent page you cannot
complete yourself: ask your tenant administrator to grant it.

### Custom agent identity: Margo Cloud

Agent 365 gives each hired Margo an identity of her own. The terms:

| Term | What it is |
| --- | --- |
| **Agent blueprint** | The Entra application that defines Margo once per tenant: her messaging endpoint, her permissions, her federated credential. Created with the Agent 365 CLI |
| **Instance** | One hired Margo, created from the blueprint when someone selects **Create instance** |
| **Agent identity** | The instance's service principal. Its infrastructure identity |
| **Agent user** | The instance's user account: Margo's own mailbox, Teams presence, OneDrive and place in the org chart. Its manager is the person she works for |
| **Agentic sign-in** | How the instance gets a token for her agent user without a password or prompt: the container's managed identity is a federated credential on the blueprint, exchanged for the agent identity's token, then for an agent-user token audienced to Work IQ |

With her own token, `/me` is Margo: her inbox, the chats she is in, the files shared with her.

## Reaching the manager's data

An agent acting as itself cannot read anyone else's mailbox. Margo Cloud has two ways to reach the
manager's work, chosen per deployment with `MARGO_PRINCIPAL_ACCESS`:

| | `delegation` | `obo` (on-behalf-of) |
| --- | --- | --- |
| Token | Margo's own | The manager's own token, with Margo as the actor |
| How | Exchange: the manager makes Margo a calendar delegate, or an admin grants Full Access; chats, channels and files are shared with her the usual way | An on-behalf-of exchange of the manager's sign-in for a Work IQ token |
| Reaches | The manager's mail and calendar through `/users/{manager}/...`, plus whatever is shared with Margo | What the manager can see, including Copilot `ask` and `retrieve` over the manager's own data |
| Requires | Exchange permissions; Send on Behalf and calendar Editor rights for approved sends | The sign-in handler configured in the [deployment guide](../autopilot-deployment.md#choose-how-margo-reads-her-managers-work), and the manager's sign-in, which Margo asks for in their 1:1 chat |
| Status | Verified in a test tenant for mail and calendar reads | Built, not verified in a tenant |

Either way, the [guard](../../cloud/src/Margo.Cloud.Guard/WorkIQGuard.cs) picks the token from
the declared subject (`on_data_of: principal | self`), never from the model's wording, and a missing
token is `blocked`, never a fallback to the other subject.

## Scopes

Work IQ's permissions are delegated: access is always scoped to the token's subject. Margo
consents to all three. The split below is how Margo relies on them; confirm it against Microsoft's
reference before you request fewer.

| Scope | Margo relies on it for |
| --- | --- |
| `WorkIQAgent.Ask` | `ask`, and Copilot-backed answers |
| `WorkIQ.Read` | `fetch`, `retrieve`, `call_function`, `fetch_blob` and discovery |
| `WorkIQ.Act` | The write tools. Consent only makes a write possible: the [approval pattern](writes-and-approvals.md) decides whether one runs |

Check Microsoft's [Work IQ documentation](https://learn.microsoft.com/microsoft-365/copilot/extensibility/work-iq/mcp/overview)
for the authoritative scope definitions; they are preview and may change.

## Admin consent and inheritable permissions

An agent identity **cannot show a consent prompt**: a token request that would need one fails with
`AADSTS82014`. So Margo Cloud's consent is given once, by an admin, on the blueprint, and marked
**inheritable** so every instance receives it:

- `cloud/scripts/blueprint.sh` adds the three Work IQ scopes to the blueprint with tenant-wide
  consent, or prints the step for an administrator (Global Administrator or Privileged Role
  Administrator).
- `a365 query-entra inheritance` must show the Work IQ entry with kind `allAllowed`.
- A run started by someone else's message (Margo Cloud's backchannel and listening) reads only with
  the agent's own token. The platform's user token in that turn belongs to the sender, never to the
  manager, so it is never used as the manager's.
- On-behalf-of also requires the manager's incoming token to be audienced to the blueprint; a token
  for another audience fails with `AADSTS50013`.

The Agent 365 CLI's own permission steps also consent Microsoft Graph scopes Margo does not use;
remove them ([deployment guide, step 2](../autopilot-deployment.md#2-create-the-blueprint-with-the-agent-365-cli)).

## Tenant policy

Consent is not the only gate:

- **Work IQ write policy.** Microsoft blocks Work IQ writes by default. Until an admin allows them in
  the tenant's Work IQ policy, every write tool fails, whatever the scopes say. See
  [policy governance for Work IQ MCP](https://learn.microsoft.com/microsoft-365/copilot/extensibility/work-iq/mcp/policy-governance-mcp).
  Office document creation and file sharing are separate policy settings.
- **Path families.** A tenant can disable a path family; the call returns `Access denied for path`
  ([errors](errors.md#access-denied-for-path)).
- **Meeting transcripts.** A transcript exists only if the tenant's Teams meeting policy allows
  transcription and the meeting was transcribed. No transcript is a gap, not an empty meeting.
- **Licenses and billing.** The subject needs a Microsoft 365 Copilot license for Work IQ, and a
  tenant that meters Work IQ through Copilot Credits needs the calling user in a spending policy.
  Under on-behalf-of, whether the manager or Margo is billed is unverified.

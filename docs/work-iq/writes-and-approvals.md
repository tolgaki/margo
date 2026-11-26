# Writes and approvals

Work IQ writes execute when called. There is no staging area, no universal undo and no preview
gate in the service: a send, a decline or a delete can be visible or irreversible at once. Consent
to `WorkIQ.Act` and a tenant policy that allows writes make a write *possible*; they say nothing
about whether *this* write should run. That decision is yours to build, and this page is the
pattern Margo uses in both runtimes.

## The pattern

1. **Propose the exact call.** Resolve every ID with `fetch` first (never with `ask`), check the
   schema with `get_schema`, and record the complete Work IQ call (tool and arguments) as a versioned
   action, with the sources that justify it.
2. **A human approves that exact action.** Show what will happen in plain words: recipients, subject,
   full text, times, the existing item it affects. The approval binds the account, the action, its
   revision and a hash of its content. Editing the action makes a new revision and voids the
   approval. A summary, a goal, a "yes" to a plan or a remembered preference is not approval.
3. **Run it once.** Take a single-use claim on the approved revision and send the stored call, byte
   for byte. Anything that differs from what was approved is refused.
4. **Record the dispatch before sending.** Write "dispatched" to the ledger inside its transaction,
   then send. A crash after that point leaves an attempt to reconcile, never one to resend.
5. **Unknown is never retried.** A timeout, a dropped connection or a server error after sending is
   `outcome_unknown`. Only a refusal before acting (400, 401, 403, 404, 409, 429 and similar) is a
   clean "not sent".
6. **Re-read.** Confirm the effect from the source (Sent Items, the calendar, the chat) and record
   the receipt. Settle an unknown attempt only with that evidence.

Unattended runs never reach step 3: they may prepare and propose, never send.

## Desktop: the action desk

Desktop Margo follows the pattern through the work ledger's state helpers, with the foreground
conversation as the approval surface:

| Step | Where |
| --- | --- |
| Propose, edit, approve, begin, finish, reconcile | [`work_state.py`](../../skills/chief-of-staff/scripts/work_state.py) over [`work_ledger.py`](../../skills/chief-of-staff/scripts/work_ledger.py); procedure in [`action-desk.md`](../../skills/chief-of-staff/references/action-desk.md) and [`work-ledger.md`](../../skills/chief-of-staff/references/work-ledger.md) |
| Review surface | The conversation, or the optional [action-desk canvas](../../.github/extensions/margo-action-desk/README.md); canvas edits are local and never send |
| Unattended denial | [`tools/margo-scheduled.sh`](../../tools/margo-scheduled.sh) denies the Work IQ write tools at the CLI |

The approval journal records a decision; it does not authenticate who made it. The host's own
tool-permission prompt and the agent's instructions carry the rest ([safety](../safety.md#what-is-enforced-and-what-is-asked)).

## Margo Cloud: approval cards

Margo Cloud enforces the pattern in code, with the manager's authenticated tap as the approval:

| Step | Where |
| --- | --- |
| The model proposes the exact call (`request_approval`); it is never offered a write tool | [`Approvals.cs`](../../cloud/src/Margo.Cloud.Orchestrator/Approvals.cs) |
| A card drawn from the ledger, in plain words, with the email or meeting it affects | [`ApprovalCards.cs`](../../cloud/src/Margo.Cloud.Host/ApprovalCards.cs), [`CardContext.cs`](../../cloud/src/Margo.Cloud.Orchestrator/CardContext.cs) |
| Only the manager's tap (in their one-to-one chat, or the group chat or channel the card was posted in), for the current revision and hash, counts | [`Approvals.cs`](../../cloud/src/Margo.Cloud.Orchestrator/Approvals.cs) |
| The guard runs the stored call once, after recording `workiq_dispatched` | [`WorkIQGuard.cs`](../../cloud/src/Margo.Cloud.Guard/WorkIQGuard.cs), [`LedgerClaimJournal.cs`](../../cloud/src/Margo.Cloud.Guard/LedgerClaimJournal.cs) |
| Each write on an MCP session of its own, never resent | [`WorkIQMcpTransport.cs`](../../cloud/src/Margo.Cloud.WorkIQ/WorkIQMcpTransport.cs) |

A typed or spoken "yes" is not an approval, and a voice call cannot ask for one. The card runs the
approved snapshot: the target is not re-read between the tap and the call. Approval cards are built
and tested offline, not verified in a tenant. The user-facing description is in the
[deployment guide](../autopilot-deployment.md#sending-on-your-behalf-approval-cards).

## Standing grants

A standing grant replaces per-action approval only where nobody else can see the effect:

- **Desktop:** reversible actions invisible to anyone else (mark read, categorize, flag, archive,
  move between folders) can be granted as a standing instruction
  ([safety](../safety.md#2-standing-authorization-is-bounded)).
- **Margo Cloud:** a Word, Excel or PowerPoint document the manager asks for is created in Margo's
  OneDrive and shared with the manager alone, with sign-in required, without a card
  ([`Documents.cs`](../../cloud/src/Margo.Cloud.Orchestrator/Documents.cs),
  `WorkIQGuard.AllowsOwnDocuments`). Sharing it with anyone else, or editing a document other people
  use, needs a card.

Anything another person can see stays per-action, whatever standing instruction exists.

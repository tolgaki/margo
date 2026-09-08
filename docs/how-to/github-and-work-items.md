# Review GitHub and Azure DevOps work

**Preconditions:** complete [setup](setup-and-migration.md). These routines are optional: they
only do anything once you've configured the tool they depend on.

## Start with a read-only review

> Review my configured repositories and saved backlog queries. Name the account and scope,
> separate my work from what is waiting on others, and do not change any tracker.

Check the account/repository labels and coverage first, then choose one item to investigate.
A staleness threshold is a recommendation, not a newly agreed deadline. Ask for a
[candidate commitment](commitments-and-action-desk.md#commitments) only when the item belongs in
your personal follow-through; GitHub and Azure DevOps remain authoritative for their own records.

## GitHub reviews

**What this helps you do:** treat review requests, your own stale PRs, and assigned issues as
commitments with the same standing as an unanswered email — cited, aged, and with a recommended
next step — across up to two GitHub accounts.

**Before you start:** the `gh` CLI signed in (`gh auth status`), and, for the stale-PR sweep, a
list of repositories in `preferences.md`. If you don't use GitHub this way, this feature does
nothing and can be safely ignored. You do not need to delete bundled procedures or edit the
router to skip it; leave the integration unconfigured and say it is outside your scope.

**Try it:**

> What PRs need me?

> Any stale PRs or approved-but-unmerged ones I should deal with?

**What you will see:** review requests older than about two working days, your own PRs with no
activity/failing checks/changes-requested-with-no-response, blocked-on-others PRs, and assigned
issues — each labeled with the account, repo, number, age and a direct link.
Review age uses the actual review-request event when available; PR creation time is only an
approximation. Hosts exposing an app session list can also show unfinished Copilot work.
Without that capability, Margo cannot claim the repository sweep covered local-only sessions.

**What needs your decision:** reviewing, commenting, approving, merging, closing, or labelling a
PR or issue always needs explicit approval of that exact change — this routine only reads and
recommends.

**Change your mind:** ask for a narrower repo list or a different staleness window any time.

**Your data:** proposed obligations (a stale PR you own, say) go into your private work ledger as
candidates, same as any other commitment; nothing is written to GitHub without your approval.

**If something goes wrong:** a rate-limited or failed search is reported as a partial sweep, never
presented as "zero PRs need you." A second configured account is invisible unless the host exposes
its token binding. Inspect variable **names only**, never values:

```sh
python3 -c 'import os; print("\n".join(sorted(k for k in os.environ if k.startswith("COPILOT_GH_ACCOUNT_"))))'
```

These names are host-specific discovery hints, not proof of working authentication. Do not
print tokens into a conversation, diagnostic export or issue.

**Availability:** optional (needs `gh` signed in and repos configured), procedure. Runs on
request; daily [ambient scans](automation-health.md#automation-ambient) can feed the weekly digest.
Since 1.0.0.

## ADO work items

**What this helps you do:** review your bug list and backlog using your team's actual saved
queries — run by ID, so if the query definition changes, you inherit that change automatically —
instead of guessing at a WIQL filter.

**Before you start:** Azure CLI with the `azure-devops` extension, and
`preferences.md` → *Work tracking — Azure DevOps* filled in (org, project, area path, and the two
saved query IDs). Auth is your signed-in `az` identity; a 401 means `az login`, not a token hunt.
If you do not use Azure DevOps, leave it unconfigured and skip this routine.

**Try it:**

> How do the bugs look?

> Review my Azure DevOps backlog.

**What you will see:** the bug list grouped by priority, and the backlog grouped by parent
feature (reconstructed from the tree query — a flat dump of a hierarchy is never presented as a
backlog review), with unassigned items, stale items, and OOF-assignee conflicts flagged.

**What needs your decision:** creating or updating a work item always needs the exact field
change approved first — state, assignment, priority, iteration, anything.

**Change your mind:** ask for a different grouping or an explicitly narrower review. A saved
query remains authoritative: narrowing the displayed results is not silently rewriting its
definition or substituting a guessed query. Nothing changes in Azure DevOps without approval.

**Your data:** source reads and hydration files can remain in the private working directory and
session history. Tracked flows may retain minimal evidence, candidates, proposals and output
receipts in private state. Approval controls tracker writes, not all local retention.

**If something goes wrong:** `az boards query --id` returns **empty** for a tree-shaped query —
that is a known CLI limitation, not an empty backlog, and Margo uses the REST route instead and
says so. A failed hydration batch is reported as incomplete, never presented as a small backlog.

**Availability:** optional (needs Azure CLI and configured queries), procedure. Since 1.0.0.

## Advanced reference

[GitHub Commitments](../../skills/chief-of-staff/references/github.md) and
[Work items — Azure DevOps](../../skills/chief-of-staff/references/work-items.md) have the exact
`gh`/`az boards`/REST commands, including the second-account token-derivation rule and the
tree-query hydration procedure. Neither has a Margo-specific CLI; proposed obligations flow into
the same [work ledger](commitments-and-action-desk.md#commitments) as any other commitment.

# Review GitHub and Azure DevOps work

**Preconditions:** complete [setup](setup-and-migration.md). These routines are optional: they
only do anything once you've configured the tool they depend on.

## GitHub reviews

**What this helps you do:** treat review requests, your own stale PRs, and assigned issues as
commitments with the same standing as an unanswered email — cited, aged, and with a recommended
next step — across up to two GitHub accounts.

**Before you start:** the `gh` CLI signed in (`gh auth status`), and, for the stale-PR sweep, a
list of repositories in `preferences.md`. If you don't use GitHub this way, this feature does
nothing and can be safely ignored — delete `references/github.md` and its `SKILL.md` row if you
never want it offered.

**Try it:**

> What PRs need me?

> Any stale PRs or approved-but-unmerged ones I should deal with?

**What you will see:** review requests older than about two working days, your own PRs with no
activity/failing checks/changes-requested-with-no-response, blocked-on-others PRs, and assigned
issues — each labeled with the account, repo, number, age and a direct link.

**What needs your decision:** reviewing, commenting, approving, merging, closing, or labelling a
PR or issue always needs explicit approval of that exact change — this routine only reads and
recommends.

**Change your mind:** ask for a narrower repo list or a different staleness window any time.

**Your data:** proposed obligations (a stale PR you own, say) go into your private work ledger as
candidates, same as any other commitment; nothing is written to GitHub without your approval.

**If something goes wrong:** a rate-limited or failed search is reported as a partial sweep, never
presented as "zero PRs need you." A second configured account is invisible unless its token
environment variable is present — `env | grep COPILOT_GH_ACCOUNT` shows what's actually available.

**Availability:** optional (needs `gh` signed in and repos configured), procedure. Runs on
request, and folded into the weekly [ambient scan](automation-health.md#automation-ambient).
Since 1.0.0.

## ADO work items

**What this helps you do:** review your bug list and backlog using your team's actual saved
queries — run by ID, so if the query definition changes, you inherit that change automatically —
instead of guessing at a WIQL filter.

**Before you start:** Azure CLI with the `azure-devops` extension, and
`preferences.md` → *Work tracking — Azure DevOps* filled in (org, project, area path, and the two
saved query IDs). Auth is your signed-in `az` identity; a 401 means `az login`, not a token hunt.
If you don't use Azure DevOps, delete `references/work-items.md` and its `SKILL.md` row.

**Try it:**

> How do the bugs look?

> Review my Azure DevOps backlog.

**What you will see:** the bug list grouped by priority, and the backlog grouped by parent
feature (reconstructed from the tree query — a flat dump of a hierarchy is never presented as a
backlog review), with unassigned items, stale items, and OOF-assignee conflicts flagged.

**What needs your decision:** creating or updating a work item always needs the exact field
change approved first — state, assignment, priority, iteration, anything.

**Change your mind:** ask for a different grouping or a narrower area path any time; nothing
changes in Azure DevOps until you approve a specific edit.

**Your data:** proposed changes to a work item go through your approval before anything is
written; reads aren't stored locally beyond the current conversation.

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

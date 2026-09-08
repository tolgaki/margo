# The user journey

**Start with one useful day, not every integration.** Margo helps you decide what needs attention,
prepare the work, and carry it forward. You stay in control of commitments and anything sent or
changed on your behalf.

This guide takes you from a first brief to a repeatable weekly routine. Examples involving Dana,
Rafa, and a launch review are fictional, not a sample of your account. Prompts are examples to
adapt, not instructions to execute while reading the documentation.

For installation commands, use [Getting started](getting-started.md). For a specific task, jump
to [the how-to guides](how-to/README.md). For every capability and its availability, use
[the feature reference](features.md).

## 1. Set up a small, private starting point

You need Copilot CLI, a connected Work IQ MCP server, a Microsoft 365 work account, and Python
3.9+. Begin with the normal **copy installation**. Keep real preferences and account state
outside the repository; a contributor's linked checkout is not the place for your mailbox data.

Follow [Getting started](getting-started.md) to complete three distinct steps:

1. Install the persona and chief-of-staff skill. Extra skills and review panels are optional.
2. Confirm which account Work IQ is signed into with a bounded read.
3. Configure that confirmed account as the owner of the private local ledger.

Configuring an email address in the ledger does not sign in to Microsoft 365. If you are updating
an existing installation, use [setup and migration](how-to/setup-and-migration.md) rather than
starting a second empty tracker.

Fill in the private `preferences.md` with your time zone, working hours, protected focus time,
current priorities, and drafting voice. Start with a small set of important people. You can
refine these after seeing a real brief; you do not need a complete personal profile first.

**Ready to continue:** Margo can identify the intended account and report any missing sources.
Memory capture, embeddings, Dream, community integrations, and schedules can all wait.

## 2. Get your first useful brief

Ask:

> Margo, brief me for today. Focus on what needs a decision and tell me which sources you
> could not cover.

The result should help you choose, not just describe the inbox.

| Part of the brief | What to look for | Your decision |
| --- | --- | --- |
| Priorities | A short ranked list tied to deadlines or your stated outcomes | Which item deserves attention first |
| Calendar | Meetings, preparation needs, conflicts, and protected time | Whether a meeting needs preparation or a proposed change |
| Needs a response | The actual ask, its source, and a recommended action | Reply, delegate, defer, or ignore |
| Waiting on / commitments | Confirmed work, with suspected new obligations kept separate | Whether a candidate is a real commitment |
| Coverage | The time window and any missing, denied, or incomplete source | Whether the evidence is sufficient to act |

A useful result might say that Dana needs a decision before the launch review and that the
calendar leaves no preparation time. If Teams was unavailable, it should say that too, rather
than claiming there is nothing to answer there.

Open a source link before acting on a consequential claim. If the brief is too broad, narrow
the next request to a day, person, or project. If its ranking is wrong, correct the priority
rather than granting more action permissions.

Continue with [briefs and catch-up](how-to/briefs-and-catch-up.md) or see
[a worked morning brief](walkthroughs.md#1-the-morning-brief).

## 3. Turn one item into a reviewed reply

Choose one real item from the brief:

> Draft a reply to Dana's launch-review thread. Answer the open question, keep it concise,
> and leave it here for review.

Margo reads the relevant thread and presents the intended recipient, channel, subject, and
draft text. The text should sound like you, not like Margo. Missing facts should remain
questions or gaps, not become confident promises.

You can edit or discard it without sending. A local action-desk proposal is **not an Outlook
draft**. Creating a draft in Outlook is a separate external write; sending it is another.

When you do want to send, approve the exact displayed recipient and content. A changed
recipient, attachment, text, or action revision requires a new decision. Before executing,
Margo checks the current target and relevant evidence again.

**Afterwards, look for the result, not just the intention.** A successful receipt records what
happened. A failure should be explicit. If the send timed out and its effect is unknown, ask
Margo to reconcile the outcome; do not ask it to repeat the send blindly.

Use [drafting and follow-ups](how-to/drafting-and-follow-ups.md) for the detailed recipe and
[commitments and action desk](how-to/commitments-and-action-desk.md) for proposal review.

## 4. Carry the thread through a meeting

The same launch review now needs preparation:

> Prep me for the launch review with Dana. Include the outstanding decision and what I
> should leave the meeting having resolved.

You should receive the meeting's purpose, people, relevant context, talking points, and gaps.
An unavailable pre-read is a reported gap, not something silently omitted. If you need time
with Rafa first, ask for options:

> Find 30 minutes with Rafa this week. Show conflicts and anything that would have to move;
> do not book it yet.

The proposal should state the time zone, exact attendees, displaced meetings, and the cost of
each option. Free/busy is evidence for a candidate slot, not proof of someone else's intent.
An organizer-owned cascade may be approved as an exact named set of moves; a general request
to find time is not approval of those moves. Recurring meetings default to one occurrence.

After the review:

> Debrief that meeting. Separate decisions, my actions, what I am waiting on, and unresolved
> questions.

If the recap has not arrived, the state should remain pending. When it does arrive, inferred
obligations are candidates for your review, not automatically confirmed promises.

Follow [calendar management](how-to/calendar-management.md),
[outcomes and meetings](how-to/outcomes-and-meetings.md), and
[relationships and one-on-ones](how-to/relationships-and-one-on-ones.md).

## 5. Make the work survive the next session

Several related records keep the thread intact. They answer different questions.

| Record | Question it answers | What it does not mean |
| --- | --- | --- |
| Evidence | Where did this claim come from, and which version? | That the source is still current |
| Commitment candidate | Is this something someone may owe? | A confirmed promise |
| Confirmed work item | What do I owe, or what am I waiting on? | Permission to send a follow-up |
| Action proposal | What exact message or change is ready for a decision? | That the action has happened |
| Weekly outcome | What result have I agreed to pursue? | Approval to rearrange the calendar |
| Task run | Where did this bounded attempt stop? | A second task tracker or proof of delivery |
| Memory | What context may help with this request? | Current truth, a canonical team decision, or permission |

Review the launch-review candidates and confirm only the obligations you actually accept.
After migration, `commitments.md` is a readable ledger export, not another file to maintain
independently. A Planner task or team decision stays in its canonical system; local records
link to it rather than replacing it.

Later, ask:

> What am I waiting on for the launch review? Check for replies before preparing any chases.

Margo should reconcile later evidence before recommending a nudge. If a source is unavailable,
the resolution check remains incomplete. Dismissing a proposed chase does not close the
underlying obligation.

For substantial multi-step work with task tracking initialized, you can ask where it stopped,
pause it, or review a safe resumption. Cancellation stops future steps; it cannot undo a send.
An exhausted budget is a visible stopping point, not permission to quietly expand the work.

Continue with [closed-loop productivity](closed-loop.md) and
[task progress and recovery](how-to/task-progress-and-recovery.md).

## 6. Build a weekly rhythm

Once the first day works, combine the routines rather than starting every conversation over.

| Moment | Ask | What carries forward |
| --- | --- | --- |
| Start of day | "Brief me." | Current priorities, preparation debt, and confirmed open work |
| After time away | "What changed since Monday afternoon?" | A bounded catch-up, with coverage gaps stated |
| Before a 1:1 | "What's on the agenda with Rafa?" | Rolling topics and unresolved work, not a new obligation list |
| End of day | "Wrap up today: what resolved and what rolls over?" | Proposed additions and resolutions for review |
| Before next week | "Plan next week around my outcomes and available focus time." | Up to three agreed outcomes, capacity, and explicit trade-offs |

For the launch review, an outcome might be "agree the rollout decision by Friday," with a
definition of done, owner, and effort estimate you confirm. Missing effort or calendar coverage
makes feasibility unknown; an empty-looking calendar is not automatically available capacity.

If this rhythm is useful, add **one scheduled anchor first**, usually the morning brief.
Review its prompt, cadence, time zone, host availability, and output location before enabling
it. Adding an installer or editing preferences does not itself create a schedule.

Scheduled runs prepare private work but do not send, post, RSVP, delete, or change external
work items. Wrapper-based schedules deny four Work IQ write tools; app workflows do not inherit
those flags. Neither is a general sandbox. A sleeping local machine cannot run the job.

Read [proactive and scheduled operation](proactive.md) before adding sweeps. Check
[source coverage and output delivery](how-to/automation-health.md) separately from whether
the scheduler exited successfully.

## 7. Add features when there is a reason

The [full feature index](features.md#full-feature-index) lists all 68 catalog entries. These
are the next destinations once the daily loop is useful.

| Your need | Features to explore | Extra setup or important boundary |
| --- | --- | --- |
| Less inbox noise | [Inbox and Teams triage](how-to/inbox-and-teams.md) | Teams attention is not a native unread feed; replies and reactions stay per-action |
| A better deliverable | [Work products and feedback](how-to/feedback-and-work-products.md) | Private memos, comparisons, and agendas are not shared documents; delivery is separate |
| Reading and file work | [Documents and files](how-to/documents-and-files.md) | Large-file bridge is macOS-only today; copy/upload have documented scope limitations |
| Review and backlog visibility | [GitHub and work items](how-to/github-and-work-items.md) | Configure `gh` or Azure CLI and the relevant repositories/queries |
| Community signals | [Community and feedback](how-to/community-and-feedback.md) | Configure the exact community or channel; incomplete collection is not an unanswered count |
| Durable team decisions | [Decision log](how-to/decision-log.md) | Install/configure the extra skill; log writes require approval and preserve supersession history |
| Recall across sessions | [Semantic memory](how-to/semantic-memory.md) | Explicit memory setup; semantic search also needs an explicitly installed local encoder |
| Control over remembered context | [Memory controls and learning](how-to/memory-controls-and-learning.md) | Capture is opt-in; correction, suppression, forgetting, retention, and export have distinct effects |
| Reflection on selected sessions | [Dream](how-to/dream.md) | Limited to authorized current-session checkpoints and manual bounded pages; no history importer or schedule |
| A visual review surface | [Action desk](how-to/commitments-and-action-desk.md#action-desk-canvas), [memory](how-to/memory-controls-and-learning.md#memory-canvas), and [task progress](how-to/task-progress-and-recovery.md) panels | Optional compatible app host; review requests are not approvals, and the CLI remains sufficient |

Memory and Dream are not required to get value from briefs. If you opt in, review the exact
capture scope before collecting anything. Similarity means relevance, not truth; a Dream
interpretation stays a candidate until separately confirmed. Neither can authorize an action.

## 8. Correct, recover, and keep control

An assistant becomes useful through specific corrections, not broad standing permissions.

> That draft was too formal. Correct this draft, but do not make it a standing rule.

Or, when it really is a repeated preference:

> Propose a rule for shorter internal updates. Show its scope before activating it.

Silence or a dismissed proposal should not be treated as feedback. "Do not learn from this"
is an opt-out for that feedback event, not deletion of all existing records. Forgetting memory
does not erase source mail, work history, earlier outputs, or backups.

| When this happens | Take this path |
| --- | --- |
| Wrong or uncertain account | Stop before capture or an external action; [confirm identity and storage](how-to/setup-and-migration.md) |
| Missing or denied source | Keep the partial result and its boundary; [diagnose coverage](how-to/automation-health.md), without bypassing the denial |
| Changed draft or stale proposal | Reload and review the new exact revision; [action-desk guide](how-to/commitments-and-action-desk.md) |
| Interrupted run or uncertain external effect | [Recover or reconcile](how-to/task-progress-and-recovery.md) before repeating work |
| Unwanted memory | [Inspect, suppress, or forget](how-to/memory-controls-and-learning.md) the specific record |
| An update did not appear | Check installed code, migrations, saved workflow prompts, and session/extension reload separately in [setup and migration](how-to/setup-and-migration.md) |
| You want to remove Margo | [Uninstall](how-to/setup-and-migration.md#uninstall) managed code; private runtime state is retained, not silently erased |

Return to the [documentation home](README.md) for the reading map, or the
[how-to index](how-to/README.md) for your next concrete task.

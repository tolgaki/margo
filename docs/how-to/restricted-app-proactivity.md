# Restricted Copilot-app preparation

This optional path supports **morning**, **sweep** and **eod** private preparation. It does not
replace the ordinary `margo` agent, create native app automations, or run workspace scenario
files. The separately managed Scout controller/scenarios are a different scheduler owner.

## Keep all three native automations disabled first

Install the optional extension with the existing `--action-desk` / `-ActionDesk` copy installer.
It includes `margo-proactive.agent.md` and the fixed local preparation helpers. A modified
scheduled profile or extension is preserved/refused rather than silently widening its grant.
Normal Margo keeps its existing capabilities. Reload only after the user has saved drafts.

Create/review the three native app entries through the host's supported workflow UI/API, with
`agent: margo-proactive` and `enabled: false`. Do not use Windows tasks, a shell wrapper or
another agent as a substitute for the user's app choice. No source file in this repository
registers them. Use these minimal prompts, choosing the corresponding routine:

```text
Run the restricted morning routine using the selected margo-proactive profile.
Start with margo_proactive_context {"routine":"morning"} and obey its skipped/blocked result.
Do not enable schedules, request unrestricted tools, or fall back to ordinary Margo.
```

Replace both `morning` occurrences with `sweep` or `eod` for the other entries. The profile
contains its complete bounded procedure; it does not need shell/view/skill access to bootstrap.
Private preferences and existing commitments arrive through the context tool.

## Explicit local policy

Inspect the installed `app_proactive.py policy-show`. To configure, supply a complete policy
on stdin to `app_proactive.py policy-set --expected-revision CONFIG_HASH`, using the current
private configuration revision from `margo_store.py profile-show`. This reuses the configuration
CAS lock and preserves account, name, work root and other settings. It never initializes state,
saves native workflows or changes their enablement. Example values below are fictional:

```json
{
  "enabled": false,
  "timezone": "Etc/UTC",
  "workdays": [0, 1, 2, 3, 4],
  "slots": {"morning": ["08:00"], "sweep": ["09:00", "10:00"], "eod": ["17:00"]},
  "grace_minutes": 10
}
```

Workdays use ISO numbering (Monday 0, Sunday 6). Slots are explicit local HH:MM, separated by
at least 15 minutes; one slot per anchor and at most twelve sweep slots. Grace is 1–15 minutes.
Missing IANA timezone data is a blocker, not a guessed offset or implicit package download.
Default/unconfigured preparation is disabled. Enabling local preparation is a separate reviewed
policy edit, **not** enabling the native schedules.

The actual runtime clock determines a due slot. Early/late/out-of-workday invocations are
skipped; missed slots are not backfilled. A stable routine/local-date/slot task identity prevents
duplicate wakes (including repeated DST wall time). One live restricted claim prevents overlap;
expired claims remain in history and cannot repeat their old slot. Interrupted runs require
foreground inspection, never a generic retry. A delayed wake arriving during another valid slot
cannot identify its intended original firing time: the native scheduler still needs verification.

## The implemented boundary

The authored `tools` list is exactly:

```text
margo_proactive_context
margo_proactive_prepare
workiq/fetch
workiq/get_schema
workiq/search_paths
```

The namespaced entries resolve to actual `workiq-fetch`, `workiq-get_schema`,
`workiq-search_paths` runtime tools in the tested CLI. No wildcard/alias enables shell, arbitrary
file access, web, delegation, session messaging, canvas writes, workflow authoring, Config,
automation-definition authoring or Work IQ mutation tools. Generic `call_function`, `ask`,
`retrieve` and binary fetch are withheld. The agent performs synthesis, not an unconstrained
provider-side copilot call.

**Observed runtime exception:** the tested CLI still offers built-in `sql` beside a custom-agent
allowlist. Therefore the authored list alone is **not** the complete effective boundary.
The extension's scoped `onPreToolUse` hook denies SQL and every tool outside the exact runtime
allowlist for `margo-proactive`, including late-added tools. A harmless `SELECT 1` was denied
before execution in a synthetic SDK session. The same hook is a no-op for verified ordinary Margo.
If agent introspection itself fails, tool use is withheld until metadata is available rather
than assuming this is an unrestricted session.
Local tools also verify the selected profile's exact authored grant and inspect current tool
metadata; missing or unexpected tool names stop preparation. Hooks/introspection failure and
future app session/resume behavior must be verified in deployment. This is not an OS sandbox.

The scoped read guard rejects non-`/me` entity routes, files/channels, alternate provider agents,
mutation schemas, broad discovery queries, missing select/page limits and mismatched mail/
calendar windows. It reserves task budget before allowed reads. The scope is the user's mail,
calendar, existing local commitments, one-to-one Teams DMs and actual @mentions only.
Raw chat IDs, DM membership, server-side mention filtering, actual provider pagination,
sensitivity and reported identity/source evidence cannot all be authenticated by these local
helpers; those semantic constraints still require correct provider/agent behavior. If a
mention-specific read is unavailable without broader group/channel content, record a gap.
Never fetch broad channels or call the gap an empty source.

## What local tools can persist

Context is read-only and never initializes a namespace. Start requires a current `/me` read
whose reported principal matches the configured owner and whose provenance is a `tool:`
reference. A configuration string is not authentication. Source text cannot supply approval.
The start response's claim is kept in the extension, not offered as a user-selected ID/path.

Finish accepts exactly four bounded source results, at most 25 minimal observations per source,
five candidate asks, one private Markdown artifact and a 4,000-character summary. The tracked
run caps provider calls at 12 and reserves finite tool/model/item/output budgets; provider
identity bootstrap is one bounded read before the tracked preparation claim. It expires by
the slot grace, at most 15 minutes. No remote/model call happens in a database write transaction.

Existing TaskStore, coverage, work/source/candidate, productivity-artifact and publication
APIs own all records. No new database schema or tracker is created. Complete enumeration
advances only that source's checkpoint; search/partial/failed/blocked input does not. Unknown/
restricted sensitivity stores metadata only. The narrow API cannot confirm obligations,
approve/execute actions, close confirmed work, capture/import memory, activate a lesson, export
to work root, change settings, delete records or register/enable native workflows.
A proposed resolution is a private discussion artifact with a canonical work pointer, not closure.

Sweeps return silent only when coverage is complete and there is no pending new/material ask
with an explicit near-term deadline. The existing queue deduplicates source revision alerts;
at most one queued urgent ask is included per result. Old queued alerts can be recorded without
re-notifying stale content. Anchors return sourced summaries; any source gap stays visible.
All finish effects and local output/ack/claim settlement commit together. Exact replay returns
silent, not another delivery. The receipt establishes **local availability only**, never host
notification delivery or human reading. A lost output can be inspected through the existing
task/publication journals in foreground; it is not permission to blindly resend.

## Required deployment gates

Before enabling either the private preparation policy or native app entries:

- Verify the exact selected `margo-proactive` profile and the effective runtime tool set,
  permitted local tool/read approvals, scoped denial hook, absent dangerous tools and no
  fallthrough to ordinary Margo. Include newly discovered tools, resumed and future-run sessions.
- Verify Work IQ identity/authentication through the selected operating host; unresolved
  reauthentication remains a stop, not an invitation to retry sign-in unattended.
- Verify actual app timezone, next-fire times, DST, awake/running behavior and host output
  delivery. App workflow settings have no assumed per-workflow IANA timezone permission field.
- Confirm policy slots, source semantics, missing VIP/priorities and expected source coverage.
  Perform a separately approved bounded synthetic/work-data pilot before enablement.

Do not translate a source test into an organization-approval claim. Synthetic SDK and journal
tests prove the exercised contracts, not model adherence or future desktop scheduler enforcement.

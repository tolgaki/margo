# Automations

The scheduled half of Margo, as files. One Markdown file per automation: YAML front matter for
the schedule, the body is the prompt that gets sent.

```
---
name: CoS — Morning brief
verb: brief
tier: anchor
routine: daily-brief.md (full)
cron: "0 6 * * 1-5"
mode: autopilot
---

Load the `chief-of-staff` skill, then run …
```

| Field | What it is |
|---|---|
| `name` | What it is called in the app. Sync matches on this. |
| `verb` | `./tools/margo-scheduled.sh <verb>`. Must be unique, and not `list`, `crontab`, `schtasks` or `help`. |
| `tier` | `anchor` · `sweep` · `ambient` |
| `routine` | Which reference it runs — used for the docs table |
| `cron` | Local time. Quote it, or the `*` is a YAML alias. |
| `mode` | The session mode the app registers |

All six are required. A file missing one is an **error**, not a file that gets
skipped — see below.

**This directory is the source of truth.** Both ways of running a schedule read from it, and
`docs/proactive.md`'s tier table is generated from it:

```bash
./tools/gen-automations-docs.sh --check   # CI runs this
./tools/gen-automations-docs.sh --write   # regenerate after editing front matter
```

## Why files rather than the app's workflow store

The prompts used to live in exactly one place — the app's local database, on one machine,
backed up by nothing — while the repo shipped a wrapper that sent `"Run my morning brief."`
instead. Two schedule paths, no shared source of truth, and a documented tier table that had
drifted from the live crons. Editing a prompt here now changes both paths.

## Running them

**Via cron, launchd or Task Scheduler** — the wrapper, which hard-codes the Work IQ write
denials:

```bash
./tools/margo-scheduled.sh list        # what is defined
./tools/margo-scheduled.sh brief       # run one
./tools/margo-scheduled.sh crontab     # ready-to-install crontab lines
```

**Via the Copilot app's scheduled workflows** — ask Margo to *"sync my automations"* and she
registers each file with `save_workflow`, matching on `name`.

> ⚠️ The two paths do not carry the same guarantee. The wrapper makes the four Work IQ write
> tools uncallable; app workflows run under the app's own permissions with **no deny list**, so
> read-only there is an instruction, not a wall. See
> [`docs/safety.md`](../docs/safety.md#what-is-enforced-and-what-is-asked).

## Editing

The prompt body carries the unattended contract explicitly. Keep it — the skill gates that
contract on *"when invoked by a scheduled workflow"*, which is a condition the model has to
infer, and a prompt that does not say so out loud can legally end in a question and hang the
run.

**Your edits survive an update.** A modified automation is kept in place and reported as
`(yours, kept)`; `--force` overwrites it and archives the original first. The installer never
reverts a tuned prompt silently, because the thing that would change is what runs at 06:00.

## Everything here fails closed

A manifest that does not parse is an error and stops the run. It used to be worse than that:
an unresolvable verb was indistinguishable from an unknown one, so `margo-scheduled.sh brief`
would send the literal word **`brief`** as the entire prompt — unattended, at 06:00, and
exiting 0. The wrapper now refuses to start on a missing field, a duplicate verb, a reserved
verb, or an empty directory, and an unknown bare word is rejected rather than sent.

Free-form prompts still work, but must be a phrase (`margo-scheduled.sh "what did I miss"`),
or carry `--prompt` to force a single word through.

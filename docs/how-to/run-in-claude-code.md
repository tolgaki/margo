# Run Margo in Claude Code

Margo is written for GitHub Copilot CLI, but her persona and skills are plain Markdown and her
helpers are plain Python, so they also load in Claude Code. **This is not officially supported:**
the installer, the scheduled-run wrapper and the optional panels are built and tested for Copilot
CLI only. This page says what works and what to watch for.

To change Margo's code with Claude Code as your coding agent, you don't need any of this; see
[customize Margo with an AI coding agent](customize-with-ai-agents.md).

## What works and what doesn't

| | In Claude Code |
| --- | --- |
| The chief-of-staff and decision-log skills | Work, as Claude Code skills |
| Margo's persona | Works, loaded as an added system prompt or as a subagent (below) |
| Work IQ reads and approved writes | Work, through the Work IQ MCP server under Claude Code's tool names |
| The private ledger and state helpers | Work, with `COPILOT_HOME` pointing at the install folder |
| `./install.sh update` and `status` | Work with `--dest ~/.claude` |
| Microsoft's `workiq` skill | Comes with Microsoft's Work IQ plugin (the plugin route in section 2); the installer route does not add it. Margo's procedures work without it |
| Scheduled runs (`tools/margo-scheduled.sh`) | **No.** The wrapper is Copilot-only. See [scheduled runs](#scheduled-runs) |
| The action desk, memory and task panels | No. They are Copilot app extensions; the conversation does everything without them |

## 1. Register Work IQ

Work IQ must be enabled in your tenant, as for Copilot CLI ([what you need](../getting-started.md#what-you-need)).
Register the server in Claude Code:

```sh
claude mcp add --transport http workiq https://workiq.svc.cloud.microsoft/mcp
```

Start `claude` and run `/mcp`. The `workiq` server should be listed; when it asks you to sign in,
authenticate and complete the browser sign-in with your Microsoft 365 work account. If the browser
shows "Need admin approval", your tenant administrator must consent to Work IQ first
([identity and consent](../work-iq/identity-and-consent.md)). Check Claude Code's own MCP
documentation for the current commands and for which scope (this project, or all your projects) the
server is added to.

Use the server name `workiq` exactly: Claude Code builds the tool names from it.

If you take the plugin route in section 2 and install Microsoft's Work IQ plugin first, check `/mcp`: when it
already lists a Work IQ server from that plugin, skip the command above so the server is not registered twice.

## 2. Install the skills and persona

The simplest way is the plugin, next to Microsoft's Work IQ plugin (`/plugin marketplace add
microsoft/work-iq`, `/plugin install workiq@work-iq`):

```text
/plugin marketplace add tolgaki/margo
/plugin install margo@margo
```

Margo's personal files then live in `~/.copilot/margo/` (or `$COPILOT_HOME/margo/`), outside the
plugin: your preferences, and the relationship roster and 1:1 agendas she keeps. Create the folder
private to you (`mkdir -p ~/.copilot/margo && chmod 700 ~/.copilot/margo`), or `init` stops with
`state/config path must be private (directory 0700, file 0600)`; see
[as a plugin](../getting-started.md#or-as-a-plugin-copilot-cli-claude-code-or-codex).

Or, from a clone, install into Claude Code's folder instead of `~/.copilot`:

```sh
./install.sh --all --dest ~/.claude
```

This puts the skills in `~/.claude/skills/`, where Claude Code finds personal skills, and the persona
in `~/.claude/agents/margo.agent.md`. It also copies Copilot-only files (`automations/`, `tools/`)
that Claude Code ignores. Update later with `./install.sh update --dest ~/.claude`.

Margo's helpers keep their state under `COPILOT_HOME`, which defaults to `~/.copilot`. Point it at the
same folder, in your shell profile so every session gets it:

```sh
export COPILOT_HOME="$HOME/.claude"
```

Then fill in your preferences and configure the ledger, as in [getting started](../getting-started.md#3-teach-her-who-you-are):

```sh
$EDITOR ~/.claude/skills/chief-of-staff/preferences.md
python3 ~/.claude/skills/chief-of-staff/scripts/margo_store.py init --account you@example.com
python3 ~/.claude/skills/chief-of-staff/scripts/margo_doctor.py
```

If `init` reports a "group/world-writable ancestor", run `chmod go-w ~/.claude ~` and try again.

## 3. Load the persona

The skills load on their own for matching requests ("brief me", "triage my inbox"), without the
persona. To talk to Margo herself, either:

- **Add the persona to the main session:**

  ```sh
  claude --append-system-prompt "$(cat ~/.claude/agents/margo.agent.md)"
  ```

- **Or use it as a subagent.** Claude Code reads subagents from `~/.claude/agents/`; the file's
  `name: margo` frontmatter names it. Ask for Margo by name ("use the margo agent to brief me"), or pick
  it in `/agents`. A subagent works in its own context and returns a result to your main session,
  so a back-and-forth conversation with Margo works better with the first option.

Then ask:

```text
> Margo, brief me.
```

## Tool names differ

Copilot CLI names the Work IQ tools `workiq-fetch`, `workiq-do_action` and so on; Claude Code names
the same tools `mcp__workiq__fetch`, `mcp__workiq__do_action`. The skills say so, and tell the model to
use the host's name wherever a procedure writes `workiq-<tool>`. If Margo reports "tool does not
exist", check `/mcp` and ask her to list her tools.

Approval works as on desktop: Margo shows the exact action and runs it only after your yes. Claude
Code may also ask its own permission before a Work IQ write tool runs; that prompt is in addition to
Margo's approval, not a replacement for it.

## Scheduled runs

[`tools/margo-scheduled.sh`](../../tools/margo-scheduled.sh) is Copilot-only: it denies the Work IQ
write tools with Copilot CLI's `--deny-tool` flag, so nothing can be sent while you are not
watching. There is no Claude Code version.

**Do not run Margo unattended in Claude Code** (from cron, a loop or a CI job) unless the four Work IQ
write tools are removed from its toolset for that run. Claude Code's `--disallowedTools` flag does
this; for example:

```sh
claude -p "Brief me." \
  --append-system-prompt "$(cat ~/.claude/agents/margo.agent.md)" \
  --disallowedTools mcp__workiq__do_action mcp__workiq__create_entity \
                    mcp__workiq__update_entity mcp__workiq__delete_entity
```

Check the flag's exact spelling and syntax against Claude Code's own CLI documentation for your
version before relying on it, and confirm in a test run that a write is refused. Removing these
tools covers Work IQ writes only; other tools the session can use (a shell, another MCP server)
are not limited by it. Scheduled routines also expect the unattended contract in their prompts:
start from the prompt bodies in [`automations/`](../../automations/README.md) rather than a bare
"Brief me.", and see [proactive and scheduled](../proactive.md) for what an unattended run may do.

## Related

- [Register Work IQ in an agent harness](../../samples/harness/README.md)
- [Learn Work IQ](../work-iq/README.md)
- [Trust and safety](../safety.md)

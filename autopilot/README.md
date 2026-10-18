# Margo autopilot — offline building blocks

**Status: first deployable version, unproven in a live tenant. Not installed by the local
installers and not a supported mode.** To deploy, follow
[Deploy Margo as a Foundry autopilot](../docs/autopilot-deployment.md). The design, decisions and
verification list are in [Margo as a Foundry autopilot](../docs/autopilot.md).

## What is here

| Path | Purpose |
| --- | --- |
| `src/margo_autopilot/guard.py` | The only path from the model to Work IQ: one endpoint, token chosen by `on_data_of`, path policy, run modes, delegation fallback, claim-checked writes, content-free receipts |
| `src/margo_autopilot/transport.py` | Standard-library MCP client for `https://workiq.svc.cloud.microsoft/mcp` |
| `src/margo_autopilot/engine.py` | One model turn: the desktop persona and skill index, Responses API function tools, bounded rounds |
| `src/margo_autopilot/skills.py` | The persona and every skill, served unchanged (`load_skill`, `read_skill_file`) |
| `src/margo_autopilot/statecmd.py` | The skill scripts as the `margo-state` tool: same arguments, no shell, classified by run mode |
| `src/margo_autopilot/parity.py` | Everything desktop Margo can do that hosted Margo cannot yet, with the reason |
| `src/margo_autopilot/instances.py` | One container per instance: names, route table, lifecycle events, plans |
| `src/margo_autopilot/router/`, `provision/` | The public router and the provisioner job (`python -m margo_autopilot.router`, `… .provision run`) |
| `src/margo_autopilot/voice/` | Talk to Margo: session bridge, endpoint, and the browser page in `static/` |
| `infra/instance.bicep`, `instance.json` | One instance's identity, scoped storage role and Container App (rebuild the JSON with `bicep build`) |
| `src/margo_autopilot/orchestrator.py` | Who is speaking, manager lookup, token order, queueing and replies |
| `src/margo_autopilot/statesync.py` | SQLite state shared across sandboxes through a leased Blob bundle |
| `src/margo_autopilot/office.py` | Requests other people send Margo, as untrusted candidate items |
| `src/margo_autopilot/replies.py` | The manager's "Replies to other people" setting and its gate |
| `src/margo_autopilot/claims.py`, `instance.py` | Execution claims via the approval journal; instance binding |
| `src/margo_autopilot/host/` | Microsoft 365 Agents SDK endpoint and Azure Blob store (hosted dependencies only) |
| `Dockerfile`, `requirements.txt` | Image built from the repository root; hosted-only dependencies |
| `azure.yaml`, `infra/`, `scripts/` | `azd` project, Bicep and deployment scripts |
| `config/`, `terms.md`, `assets/` | MCP config, publish payload, terms and privacy, icons |

Everything outside `host/` is Python 3.9+ and standard-library only, like the core, and is
tested offline. `host/` adds the Microsoft 365 Agents SDK, Azure identity, Blob Storage and the
Foundry project client; it is exercised only in a deployment.

## The hosted action convention

Approved writes executed by the autopilot carry the exact Work IQ call in the action snapshot,
so the human approves the call itself rather than a description of it:

```json
{
  "kind": "mail.send",
  "target": {"message_id": "draft-1", "on_data_of": "principal"},
  "payload": {"workiq": {"tool": "do_action", "arguments": {"actionUrl": "/me/messages/draft-1/send"}}},
  "why": "...", "source_refs": ["..."], "target_fingerprint": "...", "work_item_id": "..."
}
```

The guard refuses a write unless the run is attended, the claim is live for the current
revision, and the call matches `payload.workiq` exactly. It records a `workiq_dispatched` event
before sending, so an attempt is never sent twice, even after a crash; such an attempt needs
reconciliation. Actions in the older descriptive shape cannot execute through the guard.

The guard's argument checks follow the live Work IQ tool schemas where they differ from the
published reference: `search_paths` takes `query`, and `get_schema` accepts `action` and
`function` operation types.

## Test

```sh
python3 -m unittest tests.test_autopilot_guard tests.test_autopilot_runtime
```

The tests use fictional identifiers, fake Work IQ and model services, a local MCP test server,
an in-memory lease store and the real core ledger in a temporary directory. They establish the
runtime's behaviour, not platform compatibility, token flows or model judgement.

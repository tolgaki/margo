# Running Margo in a container

Useful for two things: reproducible unattended runs (the morning brief on a
schedule), and giving Margo a blast radius smaller than your laptop.

The awkward part is not the image. It is **authentication** — two separate
sign-ins, neither of which wants to happen in a headless container. That section
is the one to read first.

---

## What Copilot CLI actually loads

Everything lives under `~/.copilot`. Four paths matter:

```
~/.copilot/
  agents/margo.agent.md              the persona
  skills/<name>/SKILL.md             the playbooks
  mcp-config.json                    { "mcpServers": { … } }
  installed-plugins/_direct/<name>/  a plugin: .mcp.json + its own skills/
  mcp-oauth-config/                  ← OAuth state. CREDENTIALS. See below.
```

An MCP server can arrive two ways. Directly in `mcp-config.json`:

```json
{ "mcpServers": { "kusto": { "type": "stdio", "command": "…" } } }
```

…or as a **plugin**, which is how Work IQ ships — a directory containing a
`.mcp.json` that declares the server, plus a `skills/` folder the CLI picks up
alongside it. That is why installing the Work IQ plugin gives you both the
`workiq-*` tools and its `calendar` / `mail` / `teams` skills at once.

---

## The authentication problem

**Two independent sign-ins**, and both are interactive by default:

| | What it authenticates | Headless story |
|---|---|---|
| **Copilot CLI** | You, to GitHub | `GH_TOKEN` / `GITHUB_TOKEN` env var works |
| **Work IQ MCP** | You, to Microsoft 365 | OAuth authorization-code flow — needs a browser **once** |

The Work IQ flow writes its result to `~/.copilot/mcp-oauth-config/` as
`<hash>.json`, `<hash>.verifier` and `<hash>.tokens.json`.

> ### `*.tokens.json` contains a live refresh token
>
> Treat that directory as a credential store, because it is one.
>
> - **Never `COPY` it into an image.** It ends up in a layer, and layers get
>   pushed.
> - **Never commit it.** Anyone with that file can read your mail until it is
>   revoked.
> - Mount it at runtime, read-only where possible, and prefer a volume over a
>   bind mount from your home directory.

**The practical pattern:** authenticate once on a real machine with a browser,
then mount the resulting token state into the container. The container never
performs an interactive sign-in.

That is a deliberate trade-off — you are moving a credential into the container's
reach in exchange for unattended operation. If that is not acceptable, a
container is the wrong shape for the problem and a scheduled job on a trusted
machine is the right one.

---

## Dockerfile

Copilot CLI needs Node. This installs it, bakes in **only** the agent and skills,
and leaves every credential to runtime.

```dockerfile
FROM node:22-bookworm-slim

# python3: the bundled scripts (state ledger, parsers). git: some skills shell out.
RUN apt-get update \
 && apt-get install -y --no-install-recommends python3 git ca-certificates curl \
 && rm -rf /var/lib/apt/lists/*

RUN npm install -g @github/copilot

# Never run an agent with shell access as root.
RUN useradd --create-home --shell /bin/bash margo
USER margo
WORKDIR /home/margo

# Agent + skills only. No tokens, no preferences, no state.
COPY --chown=margo:margo agents/  /home/margo/.copilot/agents/
COPY --chown=margo:margo skills/  /home/margo/.copilot/skills/
COPY --chown=margo:margo tools/   /home/margo/.copilot/tools/

ENTRYPOINT ["copilot"]
```

Build from the repo root:

```bash
docker build -f Dockerfile -t margo:1.0.0 .
```

### What is baked vs mounted, and why

| | Baked into the image | Mounted at runtime |
|---|---|---|
| `agents/`, `skills/` | ✅ versioned with the image | |
| `mcp-config.json`, plugins | | ✅ environment-specific |
| `mcp-oauth-config/` | **never** | ✅ credential |
| `preferences.md`, `commitments.md` | **never** | ✅ your data |
| `skills/*/state/` | **never** | ✅ must persist between runs |

The rule: **if `check-clean.sh` would flag it, it does not belong in a layer.**

---

## Running it

```bash
docker run --rm -it \
  -e GH_TOKEN \
  -v margo-oauth:/home/margo/.copilot/mcp-oauth-config \
  -v margo-state:/home/margo/.copilot/skills/chief-of-staff/state \
  -v "$HOME/.copilot/installed-plugins:/home/margo/.copilot/installed-plugins:ro" \
  -v "$PWD/preferences.md:/home/margo/.copilot/skills/chief-of-staff/preferences.md:ro" \
  margo:1.0.0
```

Named volumes for the two things that must survive a run — OAuth tokens and the
state ledger. Read-only bind mounts for the two things the container should never
modify — the plugin directory and your personalization.

### Seeding the OAuth volume, once

```bash
# On a machine with a browser, sign in normally, then copy the result in.
docker run --rm \
  -v margo-oauth:/dst \
  -v "$HOME/.copilot/mcp-oauth-config:/src:ro" \
  alpine sh -c 'cp -a /src/. /dst/'
```

Do this deliberately and know what you just moved. Re-run it when tokens expire
beyond refresh.

### Unattended runs

```bash
docker run --rm \
  -e GH_TOKEN \
  -v margo-oauth:/home/margo/.copilot/mcp-oauth-config \
  -v margo-state:/home/margo/.copilot/skills/chief-of-staff/state \
  -v "$HOME/.copilot/installed-plugins:/home/margo/.copilot/installed-plugins:ro" \
  --entrypoint /home/margo/.copilot/tools/margo-scheduled.sh \
  margo:1.0.0 brief
```

**Use the wrapper, not a hand-written `copilot` line.** `--allow-all-tools` alone
removes the approval prompt, which in an unattended container means nothing stands
between a mistaken routine and a sent email. The wrapper adds four `--deny-tool`
rules covering every Work IQ write tool, and denial takes precedence over every
allow rule — so the read-only contract in [Proactive & scheduled](proactive.md)
becomes enforced rather than instructed. The deny list is hard-coded and cannot be
trimmed.

That requires copying `tools/` into the image; add it alongside `agents/` and
`skills/` in the Dockerfile.

Belt and braces: give the container a Work IQ identity with read-only scopes too,
so the tenant enforces it independently of any flag.

The state volume is what makes scheduled runs coherent — `proactive_state.py`
keeps the surfaced ledger, the in-flight queue and the delta cursors there. Lose
it and every run re-reports everything it already told you.

---

## Updating

The image carries the agent and skills, so **rebuild rather than run
`install.sh update` inside the container** — an update in a container writes to a
layer that disappears on exit.

```bash
echo 1.1.0 > VERSION            # see packaging/README.md
docker build -t margo:1.1.0 .
```

Mounted volumes are untouched by a rebuild, which is the point: your tokens,
state and preferences survive the upgrade exactly as they do on a normal
`install.sh update`.

---

## Hardening worth doing

- **Drop root.** The Dockerfile above already does; do not undo it.
- **`--read-only`** with `--tmpfs /tmp` if your skills do not need to write
  outside the mounted state volume.
- **`--network`** restricted to what Work IQ and GitHub actually need. An agent
  that reads your mail is an agent worth constraining.
- **`--cap-drop ALL`**. Nothing here needs capabilities.
- **Scope the identity, not just the container.** A read-only Graph scope is a
  guarantee; a prompt instruction is a preference.

---

## Known gaps

Honest list — none of this is exercised by CI, and no image is published:

- The Dockerfile above is **illustrative and untested in this repo.** There is no
  `Dockerfile` committed and no container job in `ci.yml`.
- Nothing verifies that the OAuth token state survives a container restart.
- The wrapper hard-codes the deny list, but nothing stops someone bypassing it and
  invoking `copilot` directly in their scheduler.

If you get this working, a `Dockerfile` plus a CI job that builds it would be a
genuinely useful contribution — see [CONTRIBUTING](../CONTRIBUTING.md).

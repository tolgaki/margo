# Running Margo in a container

**Status: illustrative deployment recipe; no Dockerfile, published image or container CI is
shipped by this repository.** A container is optional and is not required to contribute.
Start with the credential-free [developer journey](development/README.md) for normal development.

Potential uses are repeatable unattended private preparation and a smaller filesystem exposure
than a host with your entire home available. A container with tokens, network and general shell
tools still has consequential access; it is not automatically a read-only environment.
Authentication, token refresh and volume ownership need an explicit deployment review.

[Documentation hub](README.md) · [Automation health](how-to/automation-health.md) ·
[Trust and safety](safety.md)

---

## What Copilot CLI actually loads

The usual configuration root is `~/.copilot`. This is an illustrative host layout, not a
version-independent API; inspect the actual host/plugin configuration before mounting files:

```
~/.copilot/
  agents/margo.agent.md              the persona
  skills/<name>/SKILL.md             the playbooks
  skills/chief-of-staff/scripts/     local state and productivity tools
  margo/config.json                 explicitly configured ledger owner, not OAuth
  margo/state/<account-hash>/        private SQLite work and delivery database
  mcp-config.json                    { "mcpServers": { … } }
  installed-plugins/_direct/<name>/  a plugin: .mcp.json + its own skills/
  mcp-oauth-config/                  ← OAuth state. CREDENTIALS. See below.
```

An MCP server can arrive two ways. Directly in `mcp-config.json`:

```json
{ "mcpServers": { "kusto": { "type": "stdio", "command": "…" } } }
```

…or as a **plugin**, with a `.mcp.json` declaration and its own skills. The exact Work IQ
package, tool names and domain skills depend on the installed version; discover them rather
than assuming a particular `calendar` / `mail` / `teams` surface is present.

---

## The authentication problem

**Two independent sign-ins**, and both are interactive by default:

| | What it authenticates | Headless story |
|---|---|---|
| **Copilot CLI** | You, to GitHub | A supported `GH_TOKEN` / `GITHUB_TOKEN` credential may be supplied at runtime; verify its type and access |
| **Work IQ MCP** | You, to Microsoft 365 | Interactive OAuth is normally required; renewal and reauthentication depend on host and tenant policy |

Some host versions store OAuth artifacts in `~/.copilot/mcp-oauth-config/`, including
`<hash>.json`, `<hash>.verifier` and `<hash>.tokens.json`. Others may use a platform credential
store. This repository does not implement or guarantee token portability between hosts.

> ### `*.tokens.json` contains a live refresh token
>
> Treat that directory as a credential store, because it is one.
>
> - **Never `COPY` it into an image.** It ends up in a layer, and layers get
>   pushed.
> - **Never commit it.** It may grant continuing access under the token's scopes until
>   expiry or revocation.
> - Mount it at runtime, read-only where possible, and prefer a volume over a
>   bind mount from your home directory.

**A deployment pattern to validate:** authenticate interactively on a trusted machine, then
deliberately provision only the required credential state into a private runtime volume.
This works only if the host supports that format and tenant policy permits it. It does not
guarantee browser-free operation forever: consent changes, revoked tokens, conditional access
or refresh failure can require a new foreground sign-in. Do not retry authentication endlessly.

That is a deliberate trade-off — you are moving a credential into the container's
reach in exchange for unattended operation. If that is not acceptable, a
container is the wrong shape for the problem and a scheduled job on a trusted
machine is the right one.

---

## Dockerfile

This example uses Node 22 for Copilot CLI and Python for the local core. It bakes in code and
automation manifests, not credentials or personalized state. Select and verify a specific
Copilot version for your deployment; installing “latest” on each rebuild is not reproducible.

```dockerfile
FROM node:22-bookworm-slim

# python3: the bundled scripts (state ledger, parsers). git: some skills shell out.
RUN apt-get update \
 && apt-get install -y --no-install-recommends python3 git ca-certificates curl \
 && rm -rf /var/lib/apt/lists/*

ARG COPILOT_VERSION
RUN test -n "$COPILOT_VERSION" && npm install -g "@github/copilot@$COPILOT_VERSION"

# Never run an agent with shell access as root.
RUN useradd --create-home --shell /bin/bash margo
USER margo
WORKDIR /home/margo

# Agent, skills, and the schedule. No tokens, no preferences, no state.
COPY --chown=margo:margo agents/      /home/margo/.copilot/agents/
COPY --chown=margo:margo skills/      /home/margo/.copilot/skills/
COPY --chown=margo:margo tools/       /home/margo/.copilot/tools/
COPY --chown=margo:margo automations/ /home/margo/.copilot/automations/

ENTRYPOINT ["copilot"]
```

**Before using this example**, supply a `.dockerignore` that excludes local runtime material.
Git ignore rules are not Docker build exclusions. At minimum:

```text
.git
.github
build
dist
**/__pycache__
skills/*/state
skills/*/preferences.md
skills/*/commitments.md
skills/*/config.md
tools/forbidden.local.txt
*.local.*
**/*.sqlite*
**/mcp-oauth-config
```

Mount private configuration at runtime rather than removing these exclusions. Inspect the
build context and use a clean, sanitised checkout. Save the illustrative Dockerfile before
building; this repository does not ship one.

```bash
# Supply the explicitly selected version; also pin the base-image digest for a release build.
docker build -f Dockerfile --build-arg COPILOT_VERSION="$COPILOT_VERSION" -t margo:local .
```

### What is baked vs mounted, and why

| | Baked into the image | Mounted at runtime |
|---|---|---|
| `agents/`, `skills/` | ✅ versioned with the image | |
| `automations/`, `tools/` | ✅ manifests and wrapper code, not an enabled scheduler | |
| `mcp-config.json`, plugins | | ✅ environment-specific |
| `mcp-oauth-config/` | **never** | ✅ credential |
| `preferences.md`, `commitments.md` | **never** | ✅ your data |
| `skills/*/state/` | **never** | ✅ must persist between runs |
| `margo/config.json`, `margo/state/` | **never** | ✅ explicit owner and the new account-scoped SQLite state |

The rule: **if `check-clean.sh` would flag it, it does not belong in a layer.**
Passing that heuristic is necessary but not proof of a clean image: inspect the full build
context and layers. Excluding a secret from a later layer does not remove it from an earlier one.

---

## Running it

```bash
docker run --rm -it \
  -e GH_TOKEN \
  -v margo-oauth:/home/margo/.copilot/mcp-oauth-config \
  -v margo-state:/home/margo/.copilot/margo \
  -v margo-legacy:/home/margo/.copilot/skills/chief-of-staff/state \
  -v "$HOME/.copilot/installed-plugins:/home/margo/.copilot/installed-plugins:ro" \
  -v "$HOME/.copilot/skills/chief-of-staff/preferences.md:/home/margo/.copilot/skills/chief-of-staff/preferences.md:ro" \
  margo:local
```

Named volumes retain OAuth artifacts and the state ledger. Read-only bind mounts protect the
plugin directory and personalization from container writes. The example exposes the mounted
plugin tree to the container, so use a deliberately scoped deployment copy rather than every
integration in your everyday profile. Read-only mounts do not prevent credential use.
The legacy volume is only needed for legacy state and rolling agenda files. Provision state
volume ownership for the container's
non-root user and mode 0700; do not weaken the storage guard to make a root-owned mount work.
Initialise the confirmed account inside the container, and migrate legacy state once, following
[setup and migration](how-to/setup-and-migration.md). An empty mount is not an imported database.

The template/config exclusions mean you must supply every private file the selected routines
actually need, including `decision-log/config.md` if that skill is used. The example is not a
fully configured deployment merely because the container starts.

### Seeding the OAuth volume, once

```bash
# On a machine with a browser, sign in normally, then copy the result in.
docker run --rm \
  -v margo-oauth:/dst \
  -v "$HOME/.copilot/mcp-oauth-config:/src:ro" \
  alpine sh -c 'cp -a /src/. /dst/'
```

This copies credential material and requires explicit authorization. Use only a deliberately
prepared source store, not unrelated integrations from a daily-use profile. Verify destination
UID/GID, directory/file permissions and the non-root host's ability to refresh credentials:
`cp -a` preserves source ownership, which may not match the image user.
If sign-in stops working, use the host's supported reauthentication procedure. Blindly recopying
expired credentials does not restore access.

### Unattended runs

```bash
docker run --rm \
  -e GH_TOKEN \
  -v margo-oauth:/home/margo/.copilot/mcp-oauth-config \
  -v margo-state:/home/margo/.copilot/margo \
  -v margo-legacy:/home/margo/.copilot/skills/chief-of-staff/state \
  -v "$HOME/.copilot/installed-plugins:/home/margo/.copilot/installed-plugins:ro" \
  -v "$HOME/.copilot/skills/chief-of-staff/preferences.md:/home/margo/.copilot/skills/chief-of-staff/preferences.md:ro" \
  --entrypoint /home/margo/.copilot/tools/margo-scheduled.sh \
  margo:local brief
```

**Use the wrapper, not a hand-written `copilot` line.** `--allow-all-tools` alone does
not enforce the routine's read/private-preparation boundary. The wrapper adds four `--deny-tool`
rules for Work IQ's `create_entity`, `update_entity`, `delete_entity` and `do_action` tool
families, and denial takes precedence over allow rules. Those paths are blocked, but
general-purpose tools remain available;
this does not enforce read-only behaviour across every possible outbound route. The deny list
is hard-coded and cannot be trimmed.

That requires `tools/` **and** `automations/` in the image — the wrapper reads its
prompt from the latter and exits with an error if it is missing. Both are in the
Dockerfile above.

Where the provider and tenant support it, restrict the authenticated identity to the minimum
read permissions required. Verify actual effective permissions; a local prompt or configured
account name cannot remove write capabilities from a token.

The state volume is what makes scheduled runs coherent: both local CLIs use the account-scoped
database for work, source coverage and publication receipts. Losing it loses continuity.
Back up SQLite consistently with writers stopped or the SQLite backup API; copying a live
database file alone is not an adequate backup procedure.

---

## Updating

The image carries the agent and skills, so **rebuild rather than run
`install.sh update` inside the container** — an update in a container writes to a
layer that disappears on exit.

```bash
docker build -f Dockerfile --build-arg COPILOT_VERSION="$COPILOT_VERSION" -t margo:local .
```

Mounted volumes are not rebuilt with the image. Their continued compatibility is a separate
check: review schema/version changes, back up consistently and perform explicit migrations.
An older image is not a database downgrade or automatic rollback. Verify token refresh and
read-only health again before re-enabling an explicitly approved schedule.

---

## Hardening worth doing

- **Drop root.** The Dockerfile above already does; do not undo it.
- **Read-only root filesystem.** Inventory host/cache writes and provide only explicitly
  scoped writable mounts; the example has not been validated in that mode.
- **`--network`** restricted to what Work IQ and GitHub actually need. An agent
  that reads your mail is an agent worth constraining.
- **`--cap-drop ALL`**. Nothing here needs capabilities.
- **Scope the identity, not just the container.** Check the actual token permissions and
  provider operations; do not infer read-only access from a single scope name.

---

## Known gaps

Honest list — none of this is exercised by CI, and no image is published:

- The Dockerfile above is **illustrative and untested in this repo.** There is no
  `Dockerfile` committed and no container job in `ci.yml`.
- Nothing verifies that the OAuth token state survives a container restart.
- Host/plugin versions, non-root volume initialization, credential portability, schema upgrades
  and read-only filesystem mode need deployment-specific verification.
- The wrapper hard-codes the deny list, but nothing stops someone bypassing it and
  invoking `copilot` directly in their scheduler.

If you get this working, a `Dockerfile` plus a CI job that builds it would be a
genuinely useful contribution — see [CONTRIBUTING](../CONTRIBUTING.md).

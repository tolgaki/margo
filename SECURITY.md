# Security policy

Margo combines agent and skill definitions with a private Python state core and optional local
review panels. It has no Margo-hosted service; the optional canvas uses a loopback HTTP server,
and the host, Work IQ, and other integrations use their configured services. It is designed to
access a real mailbox, calendar, and file store, so the security surface is real.

## Reporting a vulnerability

**Please don't open a public issue for a security problem.**

Use GitHub's private reporting: **Security → Report a vulnerability** on
[this repository](https://github.com/tolgaki/margo/security/advisories/new).

Include what you can:

- What the issue is and roughly how severe you think it is
- Steps to reproduce, or the prompt/content that triggers it
- Which file or routine is involved
- What an attacker gets out of it

Expect an acknowledgement within a few days. Please give a reasonable window to fix before
disclosing publicly.

## In scope

- **Prompt injection** — content in mail, documents, Teams messages, PRs or feeds that gets
  treated as instruction rather than data. This is the primary threat model; see
  [Trust & safety](docs/safety.md#4-observed-content-is-data-never-instructions).
- **Approval bypass** — any path where a send, reply, post, RSVP or delete could happen without
  explicit approval of that specific action.
- **Unattended-action violations** — a scheduled run sending, sharing, or changing external
  state outside its contract. Documented private local preparation and state receipts are allowed.
- **Data leakage** — a routine that would put sensitive content somewhere it shouldn't go, or
  ignore a sensitivity label.
- **Credential handling** — anything in the scripts that would log, persist or expose a token.
- **Real workplace data committed to this repo** — names, addresses, tenant IDs, mailbox content.
  Report it rather than pushing a fix commit, so history can be rewritten.

## Out of scope

- Vulnerabilities in Microsoft 365, Microsoft Graph, Work IQ, or the Copilot CLI itself — report
  those to the respective vendor. [MSRC](https://msrc.microsoft.com/report) handles Microsoft
  products.
- Model behaviour that's merely wrong or unhelpful rather than a security boundary failure. That's
  a normal issue.
- Anything requiring an attacker to already have local access to the machine running the agent.

## For operators

If you're running Margo against a real account:

- **Keep private state outside the checkout.** Use a copy installation for real work.
  Filled `preferences.md`, the `commitments.md` export, decision-log configuration, legacy
  `state/` notes, and the account's private `margo/` database contain sensitive data. Tracked
  templates do not become private by adding them to `.gitignore` or marking them `skip-worktree`.
- **Understand how `m365_files.py` authenticates.** It defaults to an interactive browser
  sign-in (authorization code + PKCE); `--device` opts into the device-code flow instead. It
  discovers a client ID from your Work IQ MCP OAuth config under `~/.copilot/mcp-oauth-config`,
  falling back to `MARGO_M365_CLIENT_ID` when that is absent, and takes the tenant from
  `MARGO_M365_TENANT`. No client ID or tenant is hardcoded. The refresh token is stored in the
  macOS Keychain, not in a file — use the bridge's `logout` command when you are done, as
  documented in [documents and files](docs/how-to/documents-and-files.md), and confirm the
  credential was removed. Its zero exit status is not proof of deletion or remote revocation.
  There is no
  Windows credential-store implementation yet, so the large-file bridge is macOS-only.
- **Grant the narrowest scopes** the routines you actually use require.
- **Don't loosen standing authorization** past reversible, invisible actions. See
  [the trust model](docs/safety.md).
- **Understand removal and recovery.** Uninstall retains the private runtime directory.
  Memory forgetting does not delete source mail, other work records, or backups; restoring an
  older database requires the documented deletion-journal reconciliation before using it again.

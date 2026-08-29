# Large files — moving bytes in and out of M365

The WorkIQ MCP `fetch_blob` tool caps at **4 MB** because payloads are base64-encoded over
JSON-RPC. That is a transport limit, not a Graph limit. `scripts/m365_files.py` works around it.

## Which tool to use

**Under 4 MB → use `workiq-fetch_blob`.** It works correctly and is simpler than this script.

**Over 4 MB → use this script.** The ceiling exists because `fetch_blob` returns base64 in the
tool result, so **every byte lands in the agent's context**, inflated ~33% by base64 (a 25 KB
photo costs ~32.6 KB of context). This script streams socket → disk in 1 MB chunks and returns
only a path, so a 143.8 MB file costs a few hundred bytes of context. File contents never enter
the transcript unless something is deliberately read afterwards.

**A different surface entirely:** the `workiq` **CLI** commands `fetch-blob` and `upload-blob`
throw `NotSupportedException: Blob download is not supported via remote MCP` and cannot be used
against a remote endpoint. Noted only as a defect to report — **this script does not use the CLI
for anything**. Don't conflate the surfaces: the MCP *tool* works under 4 MB; the CLI *commands*
do not work at all.

## Current status — read this first

| | Flow | Status |
|---|---|---|
| **A** | M365 → `Margo_Files` (server-side copy) | ❌ blocked — no write scope |
| **B** | local → `Margo_Files` (upload) | ❌ blocked — no write scope |
| **C** | M365 → local disk (download) | ✅ **working**, verified at 143.8 MB in 13 s |

**A and B are blocked by one single thing:** the public client used by the remote WorkIQ MCP has
no write permission. Its consented Graph scopes are read-only. Adding **`Files.ReadWrite.All`** (or `Sites.ReadWrite.All`) and re-running `auth`
unblocks both at once — no code changes needed; the `copy` and `put` commands are already written
and waiting.

Check at any time with `m365_files.py status` → `can_write_files`.

## Auth

```bash
python3 scripts/m365_files.py auth      # opens a browser, once
python3 scripts/m365_files.py status
```

Interactive **authorization-code + PKCE** against the public client that the **remote WorkIQ
MCP** (`https://workiq.svc.cloud.microsoft/mcp`) already authenticates with. The client id is
read at runtime from the MCP's own OAuth config in `~/.copilot/mcp-oauth-config/`, preferring a
static registration over a dynamic one. Scope requested is
`https://graph.microsoft.com/.default` — whatever is already consented on that app.

**There is no dependency on the `workiq` CLI.** The binary is never invoked and need not be
installed; the identity comes from the remote MCP configuration. `status` reports
`client_id_source` so you can confirm this at a glance.

The refresh token goes to the **macOS Keychain** (service `margo-m365-files`), never to disk.

**Dead ends — do not retry:**

- **Azure CLI** (`04b07795-…`) is not preauthorized against Graph for Files scopes →
  `AADSTS65002`. `az login --scope` fails the same way; it is a preauthorization problem,
  not a consent prompt.
- **Device-code flow** is blocked tenant-wide by Conditional Access → `AADSTS53003`, even from a
  Compliant device. `auth --device` exists but will not work here.
- **Binding the loopback listener to `127.0.0.1` while redirecting to `localhost`** silently
  times out: macOS resolves `localhost` to `::1` first. The redirect URI must say `127.0.0.1`.

Granted scopes today: `Sites.Read.All`, `Mail.Read`, `Chat.Read`, `ChannelMessage.Read.All`,
`ExternalItem.Read.All`, `People.Read.All`, `OnlineMeetingTranscript.Read.All`. Note there is no
`User.Read`, so **`/me` returns 403** — don't use it as a health check. `Sites.Read.All` is
sufficient for reading driveItems and their content.

## C — download (working)

```bash
python3 scripts/m365_files.py info     --drive <driveId> --item <itemId>
python3 scripts/m365_files.py download --drive <driveId> --item <itemId> --out <dir>
```

Streams to `<name>.part`, verifies the byte count, renames on success. `MARGO_FILES_DIR` sets the
default output directory.

**Encrypted files are refused (exit 3).** The first chunk is sniffed for the OLE compound-file
magic `D0 CF 11 E0 A1 B1 1A E1`; RMS-protected OOXML is a CFB container holding an
`EncryptedPackage` stream, while unprotected `.docx`/`.xlsx`/`.pptx` is a plain zip (`PK`).
`--allow-protected` keeps the protected bytes, which are unreadable without decryption.

For protected content use `workiq-retrieve` / `workiq-ask`, which run server-side where MIP is
applied properly and the EXTRACT usage right is honored natively.

## A — server-side copy (ready, blocked on write scope)

```bash
python3 scripts/m365_files.py copy --drive <srcDrive> --item <srcItem> \
    --to-drive <dstDrive> --to-folder <dstFolderId> [--name <newName>]
```

Preferred whenever the file is already in M365: no bytes touch this machine, size is irrelevant,
and no label is stripped.

**Do not route this through the MCP.** `workiq-do_action .../copy` returns 403
`logicalPermissionAccessDenied` — the WorkIQ service principal is enrolled in ODSP logical
permissions and is not permitted to call it. That is a WorkIQ-side limitation, not a user
permission problem, and no consent change on the CLI app will fix the MCP path.

## B — upload (ready, blocked on write scope)

Prefer **simple upload**, which handles up to 250 MB in one request and stays on the Graph
audience:

```bash
python3 scripts/m365_files.py put --drive <driveId> --item <itemId> --file <path>
```

Create the target item first (the MCP allowlist rejects the colon path form
`/items/{parent}:/{name}:/createUploadSession`):

```
workiq-create_entity  /drives/{driveId}/items/{folderId}/children
  {"name": "file.ext", "file": {}, "@microsoft.graph.conflictBehavior": "replace"}
```

**Avoid upload sessions here.** `createUploadSession` succeeds (send `{}` as the body — an `item`
body returns 400 `invalidRequest`), but the returned `uploadUrl` points at
`*.sharepoint-df.com/_api/v2.0/…` and rejects a Graph token with
`401 invalidAudienceUri`. It also rejects anonymous PUTs, contrary to the documented contract.
The `upload` subcommand remains for >250 MB, but it needs a SharePoint-audience token that this
app registration cannot obtain.

## Tenant quirks — verified, contrary to documentation

Both reproduced with plain `curl`, independent of this script:

- **`@microsoft.graph.downloadUrlNoAuth` returns 401.** Documented as usable without an
  Authorization header. It is not.
- **`uploadSession.uploadUrl` returns 401 on an unauthenticated PUT**, then
  `401 invalidAudienceUri` on a Graph bearer token. Documented as opaque and pre-authenticated.
  It is not.

Also worth knowing: the `workiq` **CLI** commands `fetch-blob` / `upload-blob` throw
`NotSupportedException: Blob download is not supported via remote MCP` — they ship but cannot
work against a remote MCP endpoint. This does **not** affect the MCP tool `workiq-fetch_blob`,
which works correctly under 4 MB.

## Sharing

**Never** done by the script. Sharing is visible to other people and needs explicit per-action
approval every time.

```
workiq-do_action  /drives/{driveId}/items/{itemId}/invite
  {"recipients": [{"email": "someone@example.com"}],
   "roles": ["read"], "requireSignIn": true, "sendInvitation": false,
   "expirationDateTime": "<ISO>"}
```

`sendInvitation: false` creates the permission without emailing anyone, so the user hands out the
link themselves. Org-wide links are not exposed on `drives/` paths — only
`/sites/{siteId}/lists/{listId}/items/{listItemId}/createLink` — so prefer recipient-scoped
`invite` with an expiry.

## Standing rules

- Default to **A**. Only pull bytes local when the content must actually be processed.
- Never copy, upload, or share content the user has not asked you to move.
- Anything downloaded and read enters the session transcript, which persists locally **and in a
  cloud store outside the M365 compliance boundary**. Weigh that before pulling sensitive files.
- Purge local copies when finished.

## Reference

Resolve your own working folder once and record it in `preferences.md`, rather than
hardcoding it here:

```
workiq-fetch /me/drive/root:/{folder name}?$select=id,parentReference
```

That returns the folder `id` and the `parentReference.driveId` the scripts need.
Both are tenant-specific identifiers — treat them as configuration, never commit them.

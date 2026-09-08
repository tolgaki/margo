# Read, download, copy, upload and share files

**Preconditions:** complete [setup](setup-and-migration.md) and confirm the Work IQ connection.

## Choose the operation before moving bytes

| Your goal | Start with | Important distinction |
| --- | --- | --- |
| Understand a document or prepare for a meeting | Document queue, grounded retrieval or synthesis | Reading context need not download a file |
| Obtain local bytes | File download | Choose a private destination and consider sensitivity |
| Duplicate a file already in Microsoft 365 | Server-side copy, if supported and permitted | A copy is a write, not a download |
| Replace a cloud item's contents with a local file | File upload, if supported and permitted | `put` writes to an exact existing item ID |
| Let another person read it | File sharing | Changing access is distinct from sending a link |

For a comparison or decision memo based on documents, continue to
[work products](feedback-and-work-products.md#work-products). A summary, local file, uploaded item
and recipient permission are four different results; ask which one you actually need.

## Document queue

**What this helps you do:** know what you're actually expected to read before it becomes a
missed obligation — pre-reads for upcoming meetings, VIP-shared documents, anything with a stated
deadline — without every shared file turning into homework.

**Try it:**

> What should I be reading before my meetings this week?

> What's been shared with me that I haven't looked at?

**What you will see:** pre-reads for meetings in the next 48 hours (flagged as urgent), documents
needing a real decision (read, delegate, or declare bankruptcy on it), and an ambient read-later
list — each with who shared it, why it's relevant, and a source link. A document unread after
about three weeks gets a real recommendation, not permanent guilt.

**What needs your decision:** nothing here is an action by itself — if Margo proposes logging a
new commitment ("tell the sender it won't be reviewed"), that's a separate confirmation.

**Change your mind:** ask Margo to drop a document from the queue, or reclassify it, any time.

**Your data:** fresh Work IQ evidence informs recommendations, but private queue entries,
source references, coverage, task progress and published outputs/receipts can persist locally.
These are not authoritative Microsoft 365 read-state: a local queue entry or a missing open
signal cannot establish whether you actually read a document. Session content is also subject
to your host's retention and configured model-service processing.

**If something goes wrong:** "no record of you opening this" is exactly what it means — you may
have read it somewhere Margo can't see. A guessed document path fails loudly rather than looking
like an empty result.

**Availability:** implemented, procedure. Runs on request; the daily
[ambient scan](automation-health.md#automation-ambient) can queue findings for the weekly digest.
Since 1.0.0.

## File download

**What this helps you do:** pull a file larger than the 4 MB tool-transport limit straight to
local disk, without flooding the conversation with base64 bytes.

**Before you start:** the documented MCP `fetch_blob` route handles files under its 4 MB
transport cap. It is not the structured `fetch` tool and can return base64 in the tool result.
Discover the current host's binary-download contract first.

The local bridge streams larger files, but needs a separate browser sign-in and macOS Keychain.
After the [shared shell setup](README.md#before-running-a-cli-recipe), define this explicit
account-bound helper and sign in:

```sh
files() {
  python3 "$COPILOT_HOME/skills/chief-of-staff/scripts/m365_files.py" \
    --account "$MARGO_ACCOUNT" "$@"
}
files auth
files status
```

The bridge otherwise reads `MARGO_M365_ACCOUNT`, not the ledger's `MARGO_ACCOUNT`.
Compare status's token identity (`upn`) with the account you intend to use; the Keychain label
alone is not proof of identity. `status` may refresh a token; unlike the local ledger's status
commands it is not an offline inspection.
Client discovery currently reads `~/.copilot/mcp-oauth-config` even when the script was installed
under another `COPILOT_HOME`. A custom installation path alone does not relocate that OAuth
configuration. If discovery fails, inspect the intended connection and the reported client source;
do not guess an application ID or borrow another account's credentials.

**Try it:**

> Download that 140 MB recording to my Downloads folder.

**What you will see:** the file streamed straight to disk in chunks, returning a local path
rather than the file's bytes. Contents enter the conversation if deliberately read afterward.
The reference records a 143.8 MB macOS download in about 13 seconds; this is an observed example,
not a performance guarantee for your connection. The script writes a `.part` file, checks the
reported byte count when available, and renames it on success. Choose a new destination name:
an existing same-named file can be replaced, and an interrupted partial file is not a completed
download or an automatic resumable transfer.

**What needs your decision:** your download request selects the file and local destination;
there is no Microsoft 365 write to approve. Still review sensitivity and any local overwrite
before moving bytes. Downloading does not make the content safe to forward.

**Change your mind:** deleting the file removes the local bytes, not the path or any deliberately
read content from session history. Tracked task/output records may also remain.

**Your data:** the refresh token this script uses is stored in the **macOS Keychain** today
(service `margo-m365-files`) — this has only been verified on macOS, and is **not** a claim of
Windows or tenant-wide support. Downloaded content, once read in a session, becomes part of that
session's transcript like any other read content.

**If something goes wrong:** files whose header looks like a protected compound container are
refused by default (exit code 3). This is a header check, not complete sensitivity-label
detection or a decryption service; use
[Work IQ retrieval or synthesis](../work-iq.md) instead, which honors protection labels natively.

**Availability:** implemented, deterministic code (`m365_files.py`), verified working. Since 1.0.0.

## File copy

**What this helps you do:** copy a file between Microsoft 365 locations server-side, with no
bytes touching your machine — the right choice whenever the file is already in Microsoft 365.

**Try it:**

> Copy that spec from the shared drive into my Margo_Files folder.

**What you will see in the documented binding:** copy is implemented but **blocked** by missing
Graph write scope (`Files.ReadWrite.All` or `Sites.ReadWrite.All`). This is a recorded
application/binding limitation, not a tenant-independent fact. Check scope with status, not
by attempting a copy as a health probe:

```sh
files status
```

**What needs your decision:** once write scope is granted, a copy is still a write and gets the
same treatment as any other Work IQ write — nothing here changes that.

**Change your mind:** withhold approval or change the target before execution. Once a copy has
been accepted, verify the destination and any monitor result before claiming it completed.
The bridge can return an acceptance/monitor response; acceptance is not completed delivery.

**Your data:** no bytes leave Microsoft 365 for this operation once it's unblocked — that's the
point of preferring copy over download-then-upload.

**If something goes wrong:** do not route this through the Work IQ MCP `do_action` path as a
workaround — it returns a permission denial there too (`logicalPermissionAccessDenied`), for a
different, tenant-side reason. That is a documented limitation, not something to keep retrying.

**Availability:** limited — implemented against the API shape, but blocked by missing Graph write
scope in the documented binding. A successful end-to-end copy is not established by that evidence.
Since 1.0.0.

## File upload

**What this helps you do:** put a local file into Microsoft 365, once write scope is granted.

**Try it:**

> Upload this local export into my Margo_Files folder.

**What you will see in the documented binding:** also **blocked** for the same write-scope reason
as copy. Simple upload (`put`, up to 250 MB) writes the contents of an exact existing item;
creating a missing target item is a separate approved write. The >250 MB
session-based `upload` path additionally needs a SharePoint-audience token this app registration
cannot obtain today, so it will need further work even after write scope lands.

**What needs your decision:** same as copy — once unblocked, an upload is a write and follows the
same approval expectations as any other Work IQ write.

**Change your mind:** withhold approval or revise the exact file/target first. A replacement
upload is not a new harmless local draft; review the current destination before overwriting it.

**Your data:** content stays local while the bridge is blocked. If capability becomes available,
the approved bytes enter the chosen Microsoft 365 location and its retention/versioning rules.

**If something goes wrong:** check `m365_files.py status` → `can_write_files` for current state
rather than assuming a stale answer from an earlier session.

**Availability:** limited — implemented, blocked by the same missing Graph write scope in the
documented binding; the upload-session audience issue is an additional limitation. Since 1.0.0.

## File sharing

**What this helps you do:** grant someone read access to a file without emailing them the
attachment, and without necessarily notifying them by email at all.

**Try it:**

> Share that document with Rafa, view-only, no email notification.

**What you will see:** a proposed share — recipient, permission level, and whether an email
notification is sent — presented for approval like any other outward action.

**What needs your decision:** sharing is visible to the recipient and always needs explicit
per-action approval; there is no standing authorization that covers it.

**Change your mind:** revoke a granted permission the same way you'd revoke any Microsoft 365
sharing link — through the usual Microsoft 365 surface, not a Margo-specific undo.

**Your data:** sharing uses the Work IQ write path after approval of this exact share, not the
local file bridge. It changes Microsoft 365 permissions and may retain proposal/receipt history
in the private ledger. This path has its own capability and policy checks; the file bridge's
missing copy/upload scope alone does not establish whether sharing is available.

**If something goes wrong:** sharing capabilities and expiry support depend on the discovered
path. The documented approach prefers recipient-scoped permission with sign-in and an expiry
where supported, rather than broadening access. If unsupported or denied, keep the proposal
blocked; do not silently substitute an organization-wide or anonymous link.

**Availability:** implemented, procedure. Since 1.0.0.

## Advanced reference

The document queue is described in
[Document Queue](../../skills/chief-of-staff/references/doc-queue.md). File transfer is described
in [Large files](../../skills/chief-of-staff/references/files.md), including observed binding
limitations and the full command reference. With the `files` helper above, read-only inspection
and a requested download are:

```sh
files info --drive DRIVE_ID --item ITEM_ID
files download --drive DRIVE_ID --item ITEM_ID --out PRIVATE_DIR --name NEW_FILENAME
```

Only after current capability checks, exact approval and fresh source/target review:

```sh
files copy --drive DRIVE_ID --item ITEM_ID --to-drive DST --to-folder FOLDER_ID
files put --drive DRIVE_ID --item ITEM_ID --file PRIVATE_PATH
```

Unlike the `work` helper, these commands make real network writes. The bridge itself does not
implement the action ledger's approval gate; foreground procedure and host permissions still
matter. Missing scope is not a safety mechanism to rely on remaining missing. Do not run copy
or upload unattended, or retry an unknown write merely to see whether it works.

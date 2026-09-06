# Read, download, copy, upload and share files

**Preconditions:** complete [setup](setup-and-migration.md) and confirm the Work IQ connection.

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

**Availability:** implemented, procedure. Runs on request, and folded into the weekly
[ambient scan](automation-health.md#automation-ambient). Since 1.0.0.

## File download

**What this helps you do:** pull a file larger than the 4 MB tool-transport limit straight to
local disk, without flooding the conversation with base64 bytes.

**Before you start:** files under 4 MB go through the ordinary Work IQ fetch tool and need no
setup here. Over 4 MB needs a one-time browser sign-in for the local file-bridge script:

```sh
python3 skills/chief-of-staff/scripts/m365_files.py auth
python3 skills/chief-of-staff/scripts/m365_files.py status
```

**Try it:**

> Download that 140 MB recording to my Downloads folder.

**What you will see:** the file streamed straight to disk in chunks, returning a local path
rather than the file's bytes. Contents enter the conversation if deliberately read afterward.
The reference records a 143.8 MB macOS download in about 13 seconds; this is an observed example,
not a performance guarantee for your connection.

**What needs your decision:** nothing — this is a read from Microsoft 365 to your own disk.
Nothing is shared or modified in Microsoft 365 by a download.

**Change your mind:** deleting the file removes the local bytes, not the path or any deliberately
read content from session history. Tracked task/output records may also remain.

**Your data:** the refresh token this script uses is stored in the **macOS Keychain** today
(service `margo-m365-files`) — this has only been verified on macOS, and is **not** a claim of
Windows or tenant-wide support. Downloaded content, once read in a session, becomes part of that
session's transcript like any other read content.

**If something goes wrong:** an RMS/IRM-protected file is refused outright (exit code 3) rather
than silently downloading unreadable protected bytes; use
[Work IQ retrieval or synthesis](../work-iq.md) instead, which honors protection labels natively.

**Availability:** implemented, deterministic code (`m365_files.py`), verified working. Since 1.0.0.

## File copy

**What this helps you do:** copy a file between Microsoft 365 locations server-side, with no
bytes touching your machine — the right choice whenever the file is already in Microsoft 365.

**Try it:**

> Copy that spec from the shared drive into my Margo_Files folder.

**What you will see today:** this command is written and ready, but **currently blocked**: the
Work IQ MCP's consented Graph application has no write scope (`Files.ReadWrite.All` or
`Sites.ReadWrite.All`). Running it reports the blocked state rather than silently failing or
pretending to succeed. Check current status any time:

```sh
python3 skills/chief-of-staff/scripts/m365_files.py status
```

**What needs your decision:** once write scope is granted, a copy is still a write and gets the
same treatment as any other Work IQ write — nothing here changes that.

**Change your mind:** not applicable while blocked; there is nothing to undo.

**Your data:** no bytes leave Microsoft 365 for this operation once it's unblocked — that's the
point of preferring copy over download-then-upload.

**If something goes wrong:** do not route this through the Work IQ MCP `do_action` path as a
workaround — it returns a permission denial there too (`logicalPermissionAccessDenied`), for a
different, tenant-side reason. That is a documented limitation, not something to keep retrying.

**Availability:** limited — the code is implemented and tested against the API shape, but blocked
today on a missing Graph write scope in the consented application. Since 1.0.0.

## File upload

**What this helps you do:** put a local file into Microsoft 365, once write scope is granted.

**Try it:**

> Upload this local export into my Margo_Files folder.

**What you will see today:** also **currently blocked** for the same write-scope reason as copy,
above. Simple upload (`put`, up to 250 MB) is the preferred path once unblocked; the >250 MB
session-based `upload` path additionally needs a SharePoint-audience token this app registration
cannot obtain today, so it will need further work even after write scope lands.

**What needs your decision:** same as copy — once unblocked, an upload is a write and follows the
same approval expectations as any other Work IQ write.

**Change your mind:** not applicable while blocked.

**Your data:** nothing uploads until this is unblocked.

**If something goes wrong:** check `m365_files.py status` → `can_write_files` for current state
rather than assuming a stale answer from an earlier session.

**Availability:** limited — implemented and ready, blocked today on the same missing Graph write
scope as file copy. Since 1.0.0.

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

**If something goes wrong:** organization-wide links aren't exposed on the path this uses; Margo
defaults to a recipient-scoped, expiring link instead of a broader share.

**Availability:** implemented, procedure. Since 1.0.0.

## Advanced reference

The document queue is described in
[Document Queue](../../skills/chief-of-staff/references/doc-queue.md). File transfer is described
in [Large files](../../skills/chief-of-staff/references/files.md), including the exact tenant
quirks discovered by testing (several Microsoft-documented Graph behaviors do not hold in
practice — see that file for the specifics) and the full command reference:

```sh
python3 skills/chief-of-staff/scripts/m365_files.py auth
python3 skills/chief-of-staff/scripts/m365_files.py status
python3 skills/chief-of-staff/scripts/m365_files.py info     --drive DRIVE_ID --item ITEM_ID
python3 skills/chief-of-staff/scripts/m365_files.py download --drive DRIVE_ID --item ITEM_ID --out DIR
python3 skills/chief-of-staff/scripts/m365_files.py copy     --drive DRIVE_ID --item ITEM_ID --to-drive DST --to-folder FOLDER_ID
python3 skills/chief-of-staff/scripts/m365_files.py put      --drive DRIVE_ID --item ITEM_ID --file PATH
```

`copy` and `put` are written and tested against the documented API shape; both currently return a
blocked/denied result until Graph write scope is granted, as described above — that is the
current state of this specific app registration, not a claim about what Microsoft 365 supports in
general.

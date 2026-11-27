# SharePoint and OneDrive

Answer the everyday questions about files and sites without the user opening five browser tabs:
what is waiting for their approval, who has access to a file, what changed in a library, and how
to get last week's version back. Reads are free; every change (approve, reject, share, restore,
edit a list, organize, comment) needs the user's explicit approval of that exact action.

Moving bytes (download, upload, server-side copy) is `files.md`. Publishing a page is
`publishing.md`.

## What it needs

- Work IQ's `/sharepoint` path family, discovered at run time with `workiq-search_paths`
  (filter `sharepoint`). It covers site and library contents, lists, versions, approvals, and
  recent and shared files. Reads need the `WorkIQ.Read` delegated scope; writes need
  `WorkIQ.Act` and a tenant Work IQ policy that allows them. With only the `WorkIQAgent.Ask` scope,
  the family is hidden.
- When `/sharepoint` is not offered, standard Microsoft Graph paths under `/sites/...`,
  `/drives/...` and `/me/drive/...` cover some of the same reads. Discover and verify them the
  same way; do not guess.
- The user's own permissions on each site still apply. Work IQ never sees more than they can.

## Discover first

```text
workiq-search_paths  query: "SharePoint approvals pending for me"   filter: "sharepoint"
workiq-search_paths  query: "file permissions who has access"       filter: "sharepoint|drive"
workiq-search_paths  query: "file version history restore"          filter: "sharepoint|drive"
workiq-get_schema    path: {each path you will call}  operation: {its operation}
```

Record in the session which reads and writes were offered. A read or write that is not offered
is reported as unavailable for this user; it is not an empty result.

## What's waiting for my approval

1. Use the approvals read the discovery found (for example a pending-approvals collection under
   `/sharepoint`). Follow `@odata.nextLink` within a budget (default 5 pages); a remaining link
   makes the list partial.
2. For each request: the item and its site, who asked, when, any due date, and the link. Read
   the item itself when the user needs to judge it; show its sensitivity label.
3. Render it as a short decision list, oldest and due-soonest first:

   ```
   ✅ Waiting for your approval ({n}{, partial})
     • {item} — {site/library} — from {requester}, {n} days ago{, due {date}} → approve / reject / read first
   ```

4. **Approve or reject only on explicit approval of that exact request**, with any comment the
   user wants recorded. Discover the write, build the payload from its schema, and use the
   approval flow in `work-products.md` (kind `sharepoint.approve` or `sharepoint.reject`). An
   approval is visible to the requester and often triggers a workflow; say so on the proposal.

If no approvals read is offered, say "SharePoint approvals are not available for your account"
rather than reporting zero.

## Who has access to this file

1. Resolve the exact file (by link, or by name plus site; if several match, ask which).
2. Read its permissions with the path discovery found (in Graph,
   `/drives/{driveId}/items/{itemId}/permissions`). Follow paging.
3. Report people and groups with their role, sharing links with their scope (specific people,
   organization, anyone) and expiry, and anything inherited from the site. Flag broad links
   ("anyone with the link", organization-wide) and external guests first.
4. Changing access (remove a person, delete a link, share with someone new) is an action: propose
   the exact change and get approval through the `work-products.md` flow. For new sharing, use
   `files.md` § Sharing.

A permissions read the user is not allowed to make returns a denial; report it as "you don't
have permission to see who has access", not as "no one else has access".

## Restore last week's version

1. Resolve the exact file and read its version history with the discovered path (in Graph,
   `/drives/{driveId}/items/{itemId}/versions`).
2. Show the versions around the requested time: version label, modified time, who modified it,
   size. Pick the one the user means; if "last week" matches several, ask which.
3. If useful, compare it with the current version (`work-products.md` document comparison) so the
   user sees what restoring discards.
4. **Restore only on explicit approval** of that exact version of that exact file. Discover the
   restore action and its schema (in Graph, an action on
   `/drives/{driveId}/items/{itemId}/versions/{versionId}/restoreVersion`), propose it through the
   `work-products.md` flow (kind `sharepoint.restore`), and say that the restore creates a new
   current version that everyone with access sees.
5. Read the file back after the call and confirm the new current version.

## Recent, shared and changed files

- **Recent and shared with me** reads replace guessing from mail; use them in `doc-queue.md`.
- **What changed in a library**: read the library's items ordered by last modified within a
  window, or a delta path if discovery offers one (`delta.md`). Report what was covered.
- **Lists**: read items with the discovered list paths; editing an item, adding one, or changing
  a column is an action that needs approval.
- **Organize** (move, rename, create a folder) and **comment** are actions too, even when they
  feel small, because other people see them.

## How it fails

- Family not offered, or hidden by scope: say which capability is missing and stop. Do not try
  the same operation through `scripts/m365_files.py` or another path to get around it.
- Tenant policy blocks writes: report "blocked by your organization's Work IQ policy" and leave
  the proposal in the action desk.
- A timeout on any write is unknown: read the item back before saying anything, never repeat it.
- Unattended runs read only. They never approve, reject, restore, share or edit.

## Margo Cloud

Reads and writes use the same paths. In `delegation` mode, the manager's own sites and files are
not reachable; Margo can read only what is shared with her, so "who has access" and approvals
reflect her view, not the manager's. Say so. Margo Cloud never makes these changes directly;
each one is a proposal plus an approval card.

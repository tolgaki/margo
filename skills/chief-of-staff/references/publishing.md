# Publish a page

Turn a prepared, reviewed work product (status update, decision memo, meeting summary) into a
**Copilot Page** or a page or file on a **SharePoint site**, so other people can read it. Publishing
is always an outward action: it needs the user's explicit approval of the exact destination,
content and audience, every time. Preparing the content is `work-products.md`; this file is only
the delivery.

## What it needs

- **Copilot Pages** use Work IQ's `/pages` path family (create a page, edit a page). Writes need
  the `WorkIQ.Act` delegated scope and a tenant Work IQ policy that allows them.
- **SharePoint** pages and library files use the `/sharepoint` path family (or Microsoft Graph
  `/sites/...` paths when `/sharepoint` is not offered). Writes need `WorkIQ.Act` and the user's
  own edit permission on the site. Some tenants expose the family for reads only.
- The paths are discovered at run time. Never assume one from memory.

## Procedure

1. **Start from an approved work product.** The content must already be reviewed as content
   (`work-products.md`). Approval of the content is not approval to publish it.
2. **Check what the content may carry.** If any source is labeled Confidential or Highly
   Confidential, do not publish its substance to a wider audience than the source had. Say what
   you left out. Unknown labels are checked with `workiq-retrieve` before publishing.
3. **Resolve the destination.**
   - Copilot Page: a new page, or the existing page to edit (resolve it by title and confirm
     with its link; if several match, ask which).
   - SharePoint: the site and the page library or document library. Resolve the site with
     `workiq-fetch` (for example `/sites?search={site name}`) or a site read from the discovered
     `/sharepoint` paths, and show the site's URL, such as
     `https://{tenant}.sharepoint.com/sites/{site}`. Note who can already read that site or library
     (`sharepoint.md` § Who has access), because that is the audience.
4. **Discover the write.**

   ```text
   workiq-search_paths  query: "create Copilot page"          filter: "pages"
   workiq-search_paths  query: "create SharePoint site page"  filter: "sharepoint"
   workiq-get_schema    path: {the path found}  operation: {the operation found}
   ```

   Build the payload from the schema's fields only (title, content format, destination).
   If the needed write is not offered, stop: say publishing there is unavailable for this user
   and offer the Office document route in `work-products.md` or a link the user can paste.
5. **Propose.** Show the destination, the audience, the title, the exact content (or its
   rendered summary plus the full text on request), and what happens to an existing page.
   Record it through the approval flow in `work-products.md` with a kind such as
   `pages.publish` or `sharepoint.publish`.
6. **On approval only**, make the one approved call. Record the returned link as the delivery
   receipt (`work_state.py artifact-shared` for a desktop artifact). A timeout is unknown: read
   the destination back before saying anything, and never call again.
7. **Confirm** with the page's link and who can see it.

Editing an existing page that other people use is its own approval, separate from the first
publish. Each later revision needs a fresh proposal.

## How it fails

- Path not offered or tenant policy blocks writes: say "Publishing to {Copilot Pages |
  SharePoint} is blocked for your account" and keep the prepared content local.
- Permission denied on the site: the user lacks edit rights there; do not try another site or a
  different path.
- Unattended runs never publish. They may prepare the content and leave the proposal.

## Margo Cloud

Margo Cloud never publishes directly. Publishing is a proposal plus an approval card. In
`delegation` mode, the manager's own Pages and sites are not reachable; Margo can publish only to
a place shared with her, as herself, and must say so on the proposal.

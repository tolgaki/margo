# Work IQ errors and recovery

Every failure below was observed while building Margo. Most are *informative*: they say something
about the path, the tenant or the request, and treating them as transient is the mistake. Two
rules run through all of them:

- **Incomplete evidence is `unknown`, not zero.** A failed page or a denied source is a gap to
  report, never "nothing found".
- **A write that may have landed is never sent again.** Re-read the target instead.

Request shapes use the MCP tool arguments (`fetch` takes `entityUrls`, `call_function` takes
`functionUrl`). Microsoft's [tool reference](https://learn.microsoft.com/microsoft-365/copilot/extensibility/work-iq/mcp/tool-reference)
is the authority on arguments; preview behavior changes, so recheck a limit before treating it as
permanent.

## Summary

| Symptom | Meaning | Do |
| --- | --- | --- |
| [`400 InefficientFilter`](#inefficientfilter) | No index backs that filter and sort pair | Drop `$filter`, keep `$orderby`, narrow locally |
| [`Access denied for path: X`](#access-denied-for-path) | The tenant disabled that path family | Report it; don't retry or reroute |
| [`400` on `calendarView`](#calendarview-400-without-a-window) | Missing `startDateTime` or `endDateTime` | Supply both |
| [`500` on a channel's messages with `$select`](#channel-messages-select-500) | That collection cannot be projected | Fetch whole, slim locally |
| [`$skip is not permitted`](#skiptoken-rejected) | `$skiptoken` paging is rejected | Walk a timestamp or use delta |
| [10 messages per chat read](#ten-messages-per-chat-read) | A page cap on chat messages | Page; report partial coverage |
| [`fetch_blob` over 4 MB](#fetch_blob-over-4-mb) | Transport limit | Use the large-file bridge |
| [Contact update fails for a person found in search](#contacts-versus-directory-ids) | Directory and contact IDs differ | Resolve in the right store |
| [`tool does not exist`](#tool-does-not-exist) | Wrong callable name, or not registered | Discover the host's tool names |
| [`401`, `403`](#401-and-403) | Token or permission problem | Sign in again, or report blocked |
| [`429`](#429-throttled) | Throttled | Wait, then retry a read within budget |
| [`404` session not found](#404-session-not-found) | The MCP session expired | Open a new session |
| [`isError` result](#iserror-results) | The tool ran and failed | Read its `statusCode` |
| [JSON-RPC `-32602`](#json-rpc--32602-invalid-params) | Arguments don't match the schema | Read the schema, fix the call |
| [A write times out](#a-write-times-out) | Unknown outcome | Never retry; re-read |

## InefficientFilter

- **Request:** `fetch` `/me/messages?$filter=flag/flagStatus eq 'flagged'&$orderby=receivedDateTime desc&$top=25`
  (also `from`, `importance`, `hasAttachments`).
- **Response:** `400 InefficientFilter`.
- **Meaning:** Exchange has no index for that filter with that sort. On mail, `isRead` plus
  `receivedDateTime` is safe.
- **Recovery:** keep `$orderby=receivedDateTime desc`, **drop the `$filter`**, and narrow locally.
  Never the reverse: a `$filter` without `$orderby` returns oldest first, a silently stale brief.

## Access denied for path

- **Request:** any read or write on a path family the tenant disabled, for example
  `/employeeExperience/communities`, or writes under `/me/todo/*`, `/me/contacts` or
  `/me/outlook/masterCategories`.
- **Response:** `Access denied for path: X` (sometimes `400 Access denied`).
- **Meaning:** a tenant decision, made server-side. It is not transient.
- **Recovery:** don't retry, don't try a sibling path, don't fall back to `ask`. Tell the user the
  path is not available in their tenant and record the source as blocked.

## calendarView 400 without a window

- **Request:** `fetch` `/me/calendarView?$select=subject,start,end`.
- **Response:** `400`.
- **Meaning:** `startDateTime` and `endDateTime` are mandatory on `calendarView` (and on the first
  request of a calendar delta).
- **Recovery:** resolve "today" or "this week" to explicit ISO date-times in the user's time zone
  and pass both.

## Channel messages $select 500

- **Request:** `fetch` `/teams/{team}/channels/{channel}/messages?$select=body,reactions`.
- **Response:** `500 InternalServerError`.
- **Meaning:** this collection fails when `body` or `reactions` is projected.
- **Recovery:** fetch the entity whole and slim it locally. This is the one documented exception to
  `$select` discipline ([`teams-feedback.md`](../../skills/chief-of-staff/references/teams-feedback.md)).

## $skiptoken rejected

- **Request:** following an `@odata.nextLink` that carries `$skiptoken` on a channel's messages.
- **Response:** *"Query parameter $skip is not permitted. Use $filter instead."*
- **Recovery:** page by walking `lastModifiedDateTime` forward with the delta function, taking the
  maximum value of each page at full precision; never round the cursor
  ([`teams-feedback.md`](../../skills/chief-of-staff/references/teams-feedback.md)). Elsewhere, follow
  `@odata.nextLink` with a stated page budget ([`delta.md`](../../skills/chief-of-staff/references/delta.md)).

## Ten messages per chat read

- **Request:** `fetch` `/me/chats/{id}/messages?$top=50`.
- **Response:** at most 10 messages, whatever `$top` asks for.
- **Meaning:** Work IQ caps chat message reads per request (a channel's delta returns about 20).
- **Recovery:** page within a budget and say when you stopped: "last 10 messages read; older ones
  not checked" is a partial result, not the whole chat.

## fetch_blob over 4 MB

- **Request:** `fetch_blob` for a file larger than 4 MB.
- **Response:** an error or a truncated transfer; the content is base64 in the tool result.
- **Recovery:** under 4 MB use `fetch_blob`. Over it, desktop Margo has a separate Graph client with
  its own sign-in ([large files](patterns.md#large-files)). Work IQ does not accept raw byte uploads
  through the entity tools.

## Contacts versus directory IDs

- **Request:** `update_entity` `/me/contacts/{id}` with an ID found through people search or
  `/users`.
- **Response:** not found, or a denied path.
- **Meaning:** directory users and personal contacts are separate stores with incompatible IDs.
- **Recovery:** resolve the person in the store you will write to. Many tenants also deny contact
  writes entirely.

## Tool does not exist

- **Request:** `workiq-fetch` in a host that registered the server under another name, or a tool the
  host did not expose.
- **Response:** `tool does not exist` (the host's wording varies).
- **Recovery:** list the host's tools (`/mcp` in GitHub Copilot CLI, `tools/list` in a custom
  client), use the registered name and its schema. If no Work IQ tools are listed, the server is not
  registered or not signed in ([register Work IQ](../../samples/harness/README.md)).

## 401 and 403

- **401:** the token is missing, expired or for the wrong audience
  (`api://workiq.svc.cloud.microsoft`). In a harness, sign in again (restart the session or
  re-authenticate the server). In a custom agent, get a new token; Margo Cloud reports the source as
  `blocked` and never falls back to another identity.
- **403:** the subject lacks consent, a license, Copilot Credits billing, or the tenant's policy
  denies the call (writes are denied by default). Report it as blocked; a scope or policy denial is
  not a reason to try another credential or tool ([identity and consent](identity-and-consent.md)).
- **On a write:** both mean the service refused before acting, so nothing was sent.

## 429 throttled

- **Meaning:** too many requests for this user or tenant.
- **Recovery for reads:** wait (honor `Retry-After` when the response carries it), then retry within
  the run's call budget. If the budget runs out, report partial coverage.
- **On a write:** Margo Cloud's guard treats a 429 on a write as refused before acting, so nothing was
  sent; it still never retries on its own. A new attempt needs a new approval.

## 404 session not found

- **Meaning:** an MCP server can forget a session (the Streamable HTTP transport answers `404` on a
  session ID it no longer knows, for example after it expires).
- **Recovery:** start a new session (`initialize` again). Margo Cloud resends a *read* once on a fresh
  session, since the server did not process it. A write always opens a session of its own and is
  never resent ([`WorkIQMcpTransport.cs`](../../cloud/src/Margo.Cloud.WorkIQ/WorkIQMcpTransport.cs)).

## isError results

- **Response:** a `tools/call` result with `isError: true`, usually carrying the Graph `statusCode`
  in its JSON.
- **Meaning:** the call reached the tool and failed; the status code says why.
- **Recovery:** treat it by its status code, as above. Never read an `isError` result as data. For a
  write, a status in the refused-before-acting set (400, 401, 403, 404, 405, 409, 412, 413, 415, 422,
  429) means nothing was sent; any other failure is an unknown outcome.

## JSON-RPC -32602 invalid params

- **Response:** a JSON-RPC error with code `-32602`, with a message naming the bad argument.
- **Meaning:** the arguments don't match the tool's schema: a misspelled argument, a wrong type
  (`retrieve`'s `query` as an object), or an argument the live tool does not accept.
- **Recovery:** read the schema from `tools/list` or `get_schema`, fix the call and send it again.
  Margo Cloud passes the server's message (clipped) back to the model so it can correct itself, and
  never logs it, since it may echo the request.

## A write times out

- **Response:** no answer within the call's timeout, a dropped connection, a `5xx`, or an answer too
  large to read.
- **Meaning:** **unknown**. The write may have landed.
- **Recovery:** never retry. Record the attempt as `outcome_unknown`, tell the user, and re-read the
  target: Sent Items, the calendar, the chat. Settle it with evidence
  ([`action-desk.md`](../../skills/chief-of-staff/references/action-desk.md), reconcile). Margo Cloud
  records the attempt as dispatched before sending, so even a crash cannot send it twice
  ([writes and approvals](writes-and-approvals.md)).

## Empty results

An empty tree or an empty list from a query can be a tool limitation rather than an empty source.
Verify by another route before reporting zero; a successful, fully paged enumeration establishes
zero only for its stated scope and interval.

# Delta reads: "what changed since …"

Use this recipe whenever a routine needs the changes in a source since an earlier run: the morning
brief's "changed since yesterday", catch-up, the EOD wrap-up and every hourly sweep. Delta returns
exactly the items that were added, changed or removed, plus a link to replay next time. That is
more precise and much cheaper than asking `workiq-ask` "what changed", which guesses from search
and cannot prove coverage.

The pattern is always the same: **delta first, then `workiq-ask` only over the changed items.**

## The paths

Delta is a Microsoft Graph function, so it goes through `workiq-call_function` (argument
`functionUrl`), never `workiq-ask`. Each source has its own path and its own stored link; never
share one link or checkpoint between sources.

| Source | First request (full sync) | Notes |
|---|---|---|
| Calendar | `/me/calendarView/delta?startDateTime={ISO}&endDateTime={ISO}` | Both bounds are required on the first request and are baked into the returned link. The window is fixed: a new window is a new source (see below). |
| Mail | `/me/mailFolders/inbox/messages/delta?$select=id,subject,from,receivedDateTime,isRead,flag,toRecipients,bodyPreview,webLink&$filter=receivedDateTime ge {ISO}` | One link per folder. The `receivedDateTime ge` filter bounds the first sync; it is the only filter to rely on here. |
| Chats | Discover first: `workiq-search_paths` with `"chat messages delta"`. | A chat delta path may not exist on your binding. If none is offered, do not guess one. Use the bounded recency read in `triage.md` step 3 and record it as `kind: "snapshot"`, not as delta coverage. |
| Teams channel | Discover first; see `teams-feedback.md`. | The channel timestamp walk there is an approximation, not this recipe. |

Before the first call on a binding, run `workiq-get_schema` for the path and operation you will
call (use the operation type `workiq-search_paths` reports) to learn which query options it accepts. Some
delta paths reject `$select`, `$filter` or `$orderby`; if so, omit them and slim the result
locally. A path that is not offered or returns `capability_unsupported` is a coverage gap, not
an empty result.

## Paging

A delta response ends in exactly one of two links:

- `@odata.nextLink`: more pages remain. Call `workiq-call_function` again with that link as the
  `functionUrl` (as given, or the part after `/v1.0`). Do not change any query option.
- `@odata.deltaLink`: this was the last page. Store the link; it is the replay point.

State a page budget before you start (default 10 pages for a sweep, 25 for an anchor). If the
budget runs out before a `deltaLink` arrives, stop, record the remaining continuation and report
the source as **partial**. A partial read never advances the checkpoint and is never described
as "nothing else changed".

## Store and replay with `proactive_state.py`

Use the existing coverage commands; never write the link into a note, a brief or preferences.
Keep `capability`, `query_version` and `kind` identical between runs: the stored delta token is
bound to them, and changing any of them clears the token and forces a full sync.

1. **Start the attempt** and capture `attempt_id`:

   ```bash
   python3 scripts/proactive_state.py coverage-start --json '{"family":"mail","scope":{"folder":"inbox"},"window":{"start":"2026-09-01T08:00:00Z","end":"2026-09-02T08:00:00Z"},"run_id":"example-run","capability":"messages.delta","query_version":"1","kind":"enumeration"}'
   ```

   For calendar, use `"family":"calendar"`, `"capability":"calendarView.delta"`, and set
   `collection_window` to the same fixed start/end as `window`, because the provider binds the
   link to that window. When the horizon ends, retire it with
   `coverage-retire --source-key KEY --reason "Horizon ended"` and start a new one.

2. **Look up the stored link** for this source:

   ```bash
   python3 scripts/proactive_state.py coverage-resume --attempt ATTEMPT_ID
   ```

   It returns `delta_token`, `checkpoint_at` and `resync_required`. Treat the token as private:
   never print it in a brief or a log the user reads.

3. **Read.**
   - `delta_token` present and `resync_required` false: replay it with `workiq-call_function`.
   - No token (first run), or `resync_required` true: do a **full sync** with the first-request
     path above. Bound it: a sweep requests at most the last hour on first use; an anchor
     requests at most since the previous working day's start. Say in the output that this run
     established a new baseline and what interval it covers.

4. **Record every page**, observations first, before following the next link:

   ```bash
   python3 scripts/proactive_state.py coverage-page --attempt ATTEMPT_ID --json '{"page":1,"observations":[{"id":"message-1","revision":"CHANGE_KEY","link":"WEB_LINK"}],"final":false,"continuation":"NEXT_LINK"}'
   ```

   Use the item's `changeKey` (or `lastModifiedDateTime` when there is none) as `revision`. An
   item marked `@removed` is recorded with `"revision":"removed"` and `"removed":true`. Keep
   observations minimal: identity, revision, link, sensitivity label when present. The last
   page has `"final":true` and no `continuation`.

5. **Finish.**
   - Complete (a `deltaLink` arrived and every page is recorded):

     ```bash
     python3 scripts/proactive_state.py coverage-finish --attempt ATTEMPT_ID --json '{"status":"complete","checkpoint_at":"2026-09-02T08:00:00Z","delta_token":"DELTA_LINK"}'
     ```

   - Budget ran out: `{"status":"partial"}`, adding `error_class` only for a real cause such as
     `throttled` or `timeout`. The stored link and checkpoint stay as they were; a later run
     resumes the recorded pages with `resume_attempt` and the same `window`.
   - Access denied: `{"status":"blocked","error_class":"access_denied"}`. Do not retry through
     another path or through `workiq-ask`.

Partial and failed attempts keep the previous token and checkpoint, so the next run replays from
the last complete point and nothing in between is skipped.

## Expired link (HTTP 410)

Graph returns **410 Gone** (`syncStateNotFound` or a similar code) when a stored link is too old
or the provider reset its state. Then:

1. Finish the attempt with `{"status":"failed","error_class":"expired_token"}`. This discards the
   unusable token and keeps the last reliable checkpoint.
2. Start a new attempt and do a bounded full sync from that checkpoint (with a small overlap),
   deduplicating by item ID and revision.
3. Say so in the output: "Mail change tracking restarted from {checkpoint}; items from before
   that are not re-reported." Never skip forward to the current time to hide the gap.

## Coverage reporting

Report each source separately, in one line each, alongside the brief:

- **complete**: "Mail: changes since {checkpoint} (delta, {n} pages)".
- **new baseline**: "Calendar: first sync this run; changes before {start} not tracked".
- **partial**: "Mail: {n} of an unknown number of pages read; more changes may exist".
- **blocked / unavailable**: "Chats: no delta path offered; recency snapshot only" or
  "Calendar: access denied".

One source completing says nothing about another. `coverage-status` shows the current state of
every source without exposing tokens.

## Synthesis over the diff

Only after the delta read, use `workiq-ask` (anchors and on-demand runs only; sweeps never call
it) on the changed items. Name them in the question rather than asking about a whole period:

```text
These messages and meetings changed since {checkpoint}: {subject, sender or organizer, time, link,
one line each}. Which of them need a response from me today, what is the ask in each, and which
are FYI? Use only these items and their threads. Include source links.
```

Keep it to 1–3 focused questions. If the diff is empty and coverage is complete, say "no changes
since {checkpoint}" and skip the call. If coverage is partial, say which part is missing; do not
ask `workiq-ask` to fill the gap.

## Margo Cloud

The same paths go through `call_function` with `on_data_of: "principal"` for the manager's mail
and calendar. In `delegation` mode only mail and calendar delta are reachable for the manager;
chats, files and Copilot search of their data report unavailable, and the brief must say so.

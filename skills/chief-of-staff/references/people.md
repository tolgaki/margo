# People and org

Know who someone is before the user meets, chases or writes to them: their manager, their
direct reports, and the people the user works with most. Meeting prep ("know the room"),
relationship cadence and the follow-through escalation ladder all use this.

## The reads

All are `workiq-fetch` reads with `$select`. Verify each path once per binding with
`workiq-search_paths` (filter `manager|directReports|people`) and `workiq-get_schema` before
relying on it; a path that is not offered is a gap, not an empty org.

| Question | Path |
|---|---|
| Who is my manager | `/me/manager?$select=id,displayName,jobTitle,mail` |
| Who reports to me | `/me/directReports?$select=id,displayName,jobTitle,mail` |
| Who is someone's manager | `/users/{id or mail}/manager?$select=id,displayName,jobTitle,mail` |
| Who reports to someone | `/users/{id or mail}/directReports?$select=id,displayName,jobTitle,mail` |
| Who I work with most | `/me/people?$select=id,displayName,jobTitle,department,scoredEmailAddresses&$top=25` |
| One person's profile | `/users/{id or mail}?$select=id,displayName,jobTitle,department,officeLocation,mail` |

- Resolve a name to an exact person first (`/me/people?$search="{name}"` or the user directory).
  If two people match, show both and ask; never pick one silently.
- Walk up the chain one `/manager` call at a time. Stop at the level the question needs (usually
  two). Follow `@odata.nextLink` on `directReports` within a small budget.
- `/me/people` is a relevance ranking from the user's own communication, not an org chart and not
  a measure of a relationship. Use it to find likely collaborators, never to score anyone.
- External guests and people outside the directory have no manager or reports here. Say so.

## Using it

- **Meeting prep — know the room** (`meeting-prep.md`): for each key attendee, title,
  department, manager, and whether they are the user's manager, skip-level, direct report, peer
  or external. Flag the most senior person present and anyone whose manager is also in the room.
- **Relationships** (`relationships.md`): seed or refresh the roster's Relationship column
  (Manager, Skip, Direct, Peer) from the chain instead of asking the user to type it.
- **Follow-through escalation** (`follow-through.md`): at rung 3, name the person's actual
  manager from `/users/{id}/manager`, so "escalate to their manager" is a concrete option. It is
  still only an option the user chooses; drafting a message to that manager is a separate
  proposal.

## Boundaries

- Org data is a reference, not a judgment. Never speculate about performance, standing or why
  someone changed roles.
- Directory data can be stale. Prefer what a current source says (a signature, a recent
  announcement) when they disagree, and say which you used.
- Do not put org details about third parties into anything the user sends unless the user asks.
- These are reads; they need no approval and change nothing.

## Margo Cloud

Margo Cloud's guard allows `/users/{id}/manager` and `/users/{id}/directReports` lookups. In
`delegation` mode the manager's own `/me/people` relevance is unavailable; directory lookups made
with Margo's own token reflect what her account can see. Say when an answer comes from her view.

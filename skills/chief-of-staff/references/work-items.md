# Work items — Azure DevOps

Canonical locations for the user's bug list and feature backlog, plus the exact commands that
work. Use this whenever they say *"add / update / review the backlog"* or *"review bugs"*.

> **Configure this file before first use.** The `{placeholders}` below come from
> `preferences.md` → *Work tracking — Azure DevOps*. If that section is empty, say so and offer
> to capture the org, project, area path, and saved query IDs rather than guessing.
>
> If you don't use Azure DevOps, delete this file and the *Work items* row from `SKILL.md`.

**Org:** `https://dev.azure.com/{org}`
**Project:** `{Project}`
**Area path:** `{Project}\{Area}\{Sub-area}`

## Access

Auth is the signed-in Azure CLI identity. No PAT is stored. If a call 401s, the fix is
`az login`, not a token hunt.

```bash
az account show                       # confirm identity first
az extension list --query '[].name'   # azure-devops must be present
```

For REST calls, mint a token against the Azure DevOps resource GUID (this GUID is a fixed,
public Microsoft constant — it is the same for every tenant):

```bash
TOK=$(az account get-access-token --resource 499b84ac-1321-427f-aa17-267ca6975798 \
      --query accessToken -o tsv)
```

## 1. Bugs — saved **flat** query

- **Query ID:** `{bugs-query-guid}`
- **Path:** `Shared Queries/{…}/{Bugs query name}`
- **Shape:** `queryType: flat` — all non-Closed/non-Resolved bugs under your area path **or**
  carrying your product tag, ordered by priority then most-recently-changed.

This is the source of truth. Run it **by ID** so the user's saved definition stays authoritative
— if they edit the query, we inherit the change:

```bash
az boards query --id {bugs-query-guid} \
  --org https://dev.azure.com/{org} --project "{Project}" -o json
```

Returns a flat array; fields live under `.fields`. Summarize with:

```bash
jq -r '.[] | "\(.id) | P\(.fields["Microsoft.VSTS.Common.Priority"]) | \(.fields["System.State"]) | \(.fields["System.AssignedTo"].displayName // "unassigned") | \(.fields["System.Title"])"'
```

<details>
<summary>Equivalent inline WIQL (fallback only, if the saved query is moved or deleted)</summary>

```bash
az boards query --org https://dev.azure.com/{org} --project "{Project}" --wiql \
"SELECT [System.Id],[System.Title],[System.State],[System.AssignedTo],[Microsoft.VSTS.Common.Priority],[System.Tags] \
FROM WorkItems \
WHERE [System.TeamProject]='{Project}' AND [System.WorkItemType]='Bug' \
AND [System.State] NOT IN ('Closed','Resolved') \
AND ([System.AreaPath] UNDER '{Project}\\{Area}\\{Sub-area}' \
     OR [System.Tags] CONTAINS '{product-tag}') \
ORDER BY [Microsoft.VSTS.Common.Priority] ASC, [System.ChangedDate] DESC" -o json
```

</details>

## 2. Backlog — saved **tree** query

- **Query ID:** `{backlog-query-guid}`
- **Path:** `Shared Queries/{…}/{Backlog query name}`
- **Shape:** `queryType: tree` — a `WorkItemLinks` hierarchy rooted at epic **{epic-id}**,
  Hierarchy-Forward, excluding Tasks, changed within the last 180 days.

⚠️ **`az boards query --id <guid>` returns empty for a tree query.** That is not an auth failure
and not an empty backlog — the CLI cannot flatten a tree query. Never report "no backlog items"
on the strength of that command. Use the REST route:

```bash
curl -s -H "Authorization: Bearer $TOK" \
  "https://dev.azure.com/{org}/{Project}/_apis/wit/wiql/{backlog-query-guid}?api-version=7.1" \
  -o tree.json
```

That yields `.workItemRelations[]` with `source.id` / `target.id` (source `null` = tree root).
Note the `-o tree.json` above — the hydration step reads that file, so the query must be saved,
not just printed.

Hydrate the IDs in batches of **≤200**. The endpoint hard-caps at 200 ids per request, and a
mature backlog easily exceeds that — joining every id into one call returns an error, not a
short list:

```bash
jq -r '[.workItemRelations[].target.id] | unique | _nwise(200) | join(",")' tree.json |
while read -r IDS; do
  curl -s -H "Authorization: Bearer $TOK" \
    "https://dev.azure.com/{org}/_apis/wit/workitems?ids=$IDS&api-version=7.1&\$expand=none&fields=System.Id,System.WorkItemType,System.Title,System.State,System.AssignedTo,System.Tags,System.IterationPath,System.AreaPath"
done > hydrated.jsonl
```

Merge the batches before presenting, and check you hydrated as many ids as the tree contained.
If a batch errors, say so — a short backlog is not the same as a small backlog.

Rebuild the parent/child tree from `workItemRelations` before presenting — a flat dump of a
hierarchy is not a backlog review.

## Reading and writing

Read freely. **Creating or updating a work item is an action** and follows the standing rule:
present the exact field changes and wait for explicit approval. Never edit state, assignment,
priority, or iteration on the user's behalf without a specific yes.

```bash
az boards work-item show --id <id> --org https://dev.azure.com/{org} -o json
# after approval only:
az boards work-item update --id <id> --org https://dev.azure.com/{org} --state "Active"
az boards work-item create --org https://dev.azure.com/{org} --project "{Project}" \
  --type Bug --title "…" --area '{Project}\{Area}\{Sub-area}'
```

## Presenting results

- Lead with the count and the shape of the problem (P0/P1 load, unassigned, stale), not a table
  dump.
- Group by priority for bugs; by parent feature for backlog.
- Flag anything unassigned, anything untouched >30 days, and anything assigned to someone OOF —
  cross-check against the OOF events on the user's calendar.
- Cite the work item ID; it is the link. Item URL:
  `https://dev.azure.com/{org}/{Project}/_workitems/edit/<id>`

**Verification banner.** Keep a `**Last verified:** YYYY-MM-DD` line here recording the last time
both saved queries executed successfully, with the counts returned.

**How to re-verify (do not just re-count).** The banner covers the two saved *queries* only. The
hydration step is the part that breaks: on each refresh, confirm `tree.json` was written, and
that the number of hydrated work items **equals** the number of unique ids in
`workItemRelations`. A count that merely looks plausible is exactly what a dropped batch
produces.

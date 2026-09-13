# Config and managed automations

Margo Workspace keeps **Work / Memory / Tasks / Automations / Config** in one document.
`margo-action-desk` accepts optional initial `section: "config"` or `"automations"` in addition
to the original three sections. Legacy Memory/Task entrypoints and existing read actions remain.

## Config

Config reads the same account-scoped private profile as the CLI. Edit the assistant's display
name (default Margo) and the existing absolute everyday workspace folder, then **Save changes**.
There is no invented Browse picker, upload, directory scan or account/root-selector control.
The name is bounded plain text, not the user's sender identity. Technical selectors stay fixed.

Save uses the exact configuration revision through the existing profile API. **Cancel edits**
discards only local edits. Section switches and background profile refresh preserve a dirty
draft. A conflict keeps it blocked until **Read current settings** displays current values and
the user acknowledges that version before saving. An unavailable read never silently fills in
a default. Saved names update the in-panel heading immediately; existing host chrome may keep
its old title until a later supported open/rehydration. No forced reload discards other drafts.

Workspace changes apply to future explicitly requested outputs and managed definition reads.
They do **not** move files, private config/database/memory, automation playbooks, or native
scheduler project bindings; they do not authorize OneDrive upload/sharing. The existing
private-location locator/account stays unchanged. A newly selected folder is not assumed to
be the folder to which an existing native workflow is pinned.

The authenticated same-origin profile POST accepts only `expected_revision`, `assistant_name`
and `work_root` (null explicitly clears future output routing). Core directory/name/CAS checks
remain authoritative. No model-callable Config write action is registered; restricted
scheduled preparation has no settings operation.

## Automations: scenario definitions, not native workflows

The source of truth is the configured workspace's **AUTOMATIONS.md** registry plus registered
direct **automations/<id>.md** files. There is no second JSON catalog or recursive discovery.
All registered entries are listed, including disabled/review-required/draft definitions.
Unregistered files, temporary files, unrelated native PR workflows and old disabled Scout
originals are not imported. Invalid registered files are named as errors, never silently hidden.

The top area distinguishes the **one future native Scout controller** from its scenario rows.
No authoritative controller binding exists in schema v1, so this manager reports **unbound,
enabled unknown, workflow ID unknown** and cannot control it. It does not invent an RPC or
patch host storage. Native registration/enablement remains with the parent through supported
app operations. The three restricted Margo starter entries are a separate suite, never
automatically merged. Last results/dependency readiness remain unknown without authoritative
runtime receipts. Identical schedule expressions are flagged; this is not exhaustive conflict
or next-fire prediction.

Add a stable ID, title, description and the required instructions. Schedule/timezone may be
left unresolved only in a draft. **Review changes**, then **Save reviewed changes** writes a
new disabled/review-required scenario and registers its path. Edit uses the same preview/CAS
flow. Changing content/title/schedule/timezone invalidates the descriptor's prior review,
clears `approval_ref` and disables it. Existing provenance is preserved. Toggle **Disable
descriptor** or **Enable reviewed descriptor** requires an explicit confirmation; unreviewed
descriptors cannot be enabled. The manager has no operation that grants execution approval.

An approved-looking MD entry or its `approval_ref` is **not** a private reviewed-content hash,
tool/action permission, native controller enablement or cutover approval. All execution remains
unverified until a separate private approval/runtime contract exists. This feature does not
execute any imported routines. Saving either metadata or code-like prompt text never runs it.

### Shared natural-language API

In a foreground interactive session, ask:

- “Show enabled routines.”
- “Add a weekday morning research brief.” Resolve its time/timezone and content before creating.
- “Disable weekly newsletter.” Resolve the exact ID before previewing the toggle.
- “Move daily recap to 08:30.” Preserve its other content and timezone; preview the exact change.

The `margo_automation_definitions` tool calls the same `automation_definitions.py` pipeline used
by the canvas: `list {}`, `show {id}`, `preview CHANGE`, then `commit {change,preview_hash}`.
A change contains exactly `operation` (`create/update/enable/disable`), `id`,
`expected_revision`, `controller_revision`, `profile_revision` and `patch`.
Create uses `expected_revision:"missing"`; update/toggle requires the last full-file SHA256.
Patch permits only `title`, `timezone`, `schedule` and `sections`; toggle patch is empty.
Create requires all four fields and all nine section contents. Never pass account/config paths,
SQL, executables, native workflow IDs or permission fields.

The interactive tool is denied outside interaction mode and for `margo-proactive`; its name
is absent from the scheduled profile and denied by the scheduled hook. The agent must present
the exact preview and obtain the specific user's confirmation before commit. Mode/preview
hashes are guardrails, not caller authentication or substitute human consent. No arbitrary
outward-action permission can be established by this authoring tool.

### Version-1 file contract and limits

`AUTOMATIONS.md` has exactly one fenced JSON object between
`<!-- scenario-registry -->` and `<!-- end-scenario-registry -->`:

```json
{"schema_version":1,"scenario_registry":[{"id":"example-new-scenario","path":"automations/example-new-scenario.md"}]}
```

Scenario frontmatter is strict duplicate-key-rejecting JSON between `---` lines, with the
agreed keys: `schema_version,id,title,enabled,review_status,timezone,schedule,approval_ref,
review_issues,source_automation_id,source_enabled,provenance`. Unknown versions/fields fail.
Provenance contains `kind,source_path,source_sha256,definition_sha256,captured_at,step_count`;
its source digests describe the original import, not current execution approval.

The H1 exactly matches title, followed by these H2 headings in order: **Description/Purpose,
Conditions, Inputs, Steps, Outputs, Completion/Idempotency, Failure/Retry, Permissions/Review,
Source references**. Structure inside backtick/tilde fences is ignored. Metadata-only changes
preserve original body bytes, including mixed newlines and embedded source fences. Controller
updates replace only the bounded registry span; shared appendices and human prose are retained.
The controller's human index is not another schedule authority and is not rebuilt destructively.

Cron is exactly five single-space-separated fields, supporting decimal values, `*`, ascending
ranges, comma lists and `*/positiveStep`. Bounds: minute 0–59, hour 0–23, DOM 1–31, month 1–12,
DOW 0–6 (Sunday 0). **All fields are ANDed**, including DOM/DOW. Duplicates, impossible calendar
combinations, macros, names, seconds and unsupported syntax fail. IANA timezone availability
is checked; no offset or next run is guessed.

Reads are capped at 100 registry entries, 1 MiB controller and 256 KiB per scenario; the canvas
request adapter caps edits at 96 KiB. Large retained source bodies can still receive small
metadata-only edits. A workspace-wide exclusive lock and full-file/profile/controller CAS
protect cooperating authors and sync conflicts. Never remove a live lock blindly. Creation
publishes the scenario before registry insertion; an interrupted/failed registry write can
leave an **unregistered inert file**, reported as incomplete, never “saved.” Inspect the two
exact files before deliberate recovery. This is not a OneDrive transaction or replication
protocol. Symlinks/junctions, traversal, hardlinks and writes outside the fixed managed paths
are rejected. The tool never rewrites the original Scout registry or runs a broad dispatcher.

# Interactive workspace definition authoring

Use only for explicit foreground user requests, never a scheduled run. Config is available
inside the unified workspace for direct name/work-root edits; natural language uses the
existing conditional profile procedure in `state-operations.md`.

For managed automation scenarios use `margo_automation_definitions`:

1. `{"operation":"list","input":{}}` reads only the workspace's registered scenario files.
   Native controller enablement/ID is unbound/unknown until a separate host binding exists;
   these rows are not one native automation per file. Margo starters are separate.
2. Resolve the exact scenario ID; use `show` with `{"id":"..."}` to get full metadata, section
   content, `revision`, `controller_revision`, `profile_revision`. Do not infer ambiguous IDs.
3. Preview a change with `operation:"preview"` and input
   `{operation,id,expected_revision,controller_revision,profile_revision,patch}`.
   Inner operation is `create`, `update`, `enable` or `disable`. Create uses revision `missing`.
   Patch permits `title`, `timezone` (IANA or null draft), `schedule` (null or
   `{kind:"cron",expression:"15 9 * * 1-5"}`), and `sections`.
   Create requires all fields plus all nine sections. Update may include only changed fields
   so metadata-only changes preserve source-body bytes. Toggle patch is `{}`.
4. Sections are exactly `Description/Purpose`, `Conditions`, `Inputs`, `Steps`, `Outputs`,
   `Completion/Idempotency`, `Failure/Retry`, `Permissions/Review`, `Source references`.
   Ask once for necessary missing schedule/timezone/content; do not invent runnable defaults.
5. Present exact changes and the tool's warning. Only after the user's specific approval use
   `{"operation":"commit","input":{"change":EXACT_PREVIEW_INPUT,"preview_hash":RETURNED_HASH}}`.
   Read the returned saved record; a queued request is not saved. On conflict preserve the
   draft, reread current files and obtain a revised decision rather than bumping hashes blindly.

New/content/schedule/title edits are disabled/review-required and clear descriptor approval.
Do not add an approval API, alter source-enabled provenance, or treat `approval_ref` as trusted
private permission. Existing approved descriptor flags may be toggled after explicit review,
but execution is still not authorized. Tool mode checks and hashes are not authentication.
The scheduled `margo-proactive` profile cannot call this tool.

No native workflow creation, playbook execution, source imports, controller rebinding, config
root edits, arbitrary filesystem reads/writes or external actions are available. If native
controller control is requested, report unavailable here and use the host's supported
foreground workflow operations only under separate approval. Never patch app databases.

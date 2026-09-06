# Set up Margo 1.1 and migrate existing state

**Result:** one explicitly selected account, preserved personal files, repeat-safe imports, and
separately reviewed schedules. Installing files alone does not migrate commitments or update saved
app workflow prompts.

## 1. Choose copy or link

**Preconditions:** Python 3.9+, a local source checkout, and a private Copilot installation
directory. For connected work, configure Work IQ separately using
[the connection guide](../work-iq.md).
Pause affected schedules and other Margo writers before upgrading an existing installation.

Installation paths are not version-suffixed: the agent stays `margo`, the skill stays
`chief-of-staff`, and the default root stays `$HOME/.copilot`. The installer records version
metadata separately. Do not create a parallel `margo1.1` skill.
If an older installation has no version manifest, use a normal install rather than `update`.

From your checkout, preview and then install:

```sh
cd "$HOME/src/margo"
./install.sh --dest "$HOME/.copilot" --dry-run
./install.sh --dest "$HOME/.copilot"
./install.sh status --dest "$HOME/.copilot"
```

For an existing installation, set the environment values in section 2 and take the backup in
section 3 **before** installing or initializing configuration. The sections below describe each
step; do not run an upgrade first and back it up afterward.
Ordinary copy installation preserves personal files, legacy `state/`, and modified automation
files. Review anything reported as kept; preserved custom prompts may need a manual merge.
Use `--all` only if you also want the decision-log skill.

Copy is the recommended everyday mode. `--link` is for contributors: installed skill files resolve
into the checkout, so edits take effect immediately. Personalization files and legacy skill state
may then also live in that checkout. Ignoring a tracked file does not make it private. The new
SQLite store must still remain outside repositories and synchronized folders.

Changing copy to link can replace a personal directory after archiving it. Changing link to copy
does not automatically import the linked personal data. Back up and review that transition
explicitly. Do not use `--force` or an automatic yes flag to get past a preservation warning.
`--force` can overwrite personal/customized content despite making an archive.

## 2. Select the owner and private root

Set these values deliberately; Dana is fictional:

```sh
export COPILOT_HOME="$HOME/.copilot"
export MARGO_ACCOUNT="dana@example.com"
export MARGO_STATE_DIR="$COPILOT_HOME/margo/state"
cd "$COPILOT_HOME/skills/chief-of-staff"
python3 scripts/margo_store.py init --account "$MARGO_ACCOUNT"
```

**Expected:** `status:"configured"`. Repeating initialization with the same owner is safe.
An existing configuration for a different owner is refused, not replaced.

`init` records the owner in `$COPILOT_HOME/margo/config.json`; it does not authenticate Work IQ or
create populated state. Connected runs must read the current identity through Work IQ and compare
it with this owner before ingestion. Stop on mismatch.

Account precedence is explicit `--account`, then `MARGO_ACCOUNT`, then private configuration.
`MARGO_CONFIG` can select another private config file. `MARGO_STATE_DIR` and the CLI state override
select a **base**; an account hash and `margo.sqlite3` are appended automatically. Use the same
settings in the foreground, schedules, and optional canvas.

Roots must not be in a repository, shared directory, or cloud-sync folder. State/config paths
cannot be symlinked. On POSIX, directories must be private (0700), files private (0600), owned by
the current user, with no group/world-writable ancestors. Do not disable these checks.

For a custom install, pass `--dest` to the installer and set the matching `COPILOT_HOME` for
runtime processes. The installer does not select its destination from `COPILOT_HOME` alone.

## 3. Back up before migration

**Preconditions:** stop legacy writers and pause affected schedules. Close active work sessions.
Use a private, nonsynchronized backup location. Keep the originals.

For an existing copy install with both directories present, the following makes a new, uniquely
named backup. It refuses to reuse a destination:

```sh
cd "$COPILOT_HOME"
umask 077
mkdir -p "$HOME/.margo-backups"
backup="$HOME/.margo-backups/before-1.1-$(date -u +%Y%m%dT%H%M%SZ)"
mkdir "$backup" &&
  cp -pR "$COPILOT_HOME/margo" "$backup/margo" &&
  cp -pR "$COPILOT_HOME/skills/chief-of-staff" "$backup/chief-of-staff"
```

The skill copy includes `preferences.md`, `commitments.md`, rolling files, and legacy `state/`.
Also copy any installed decision-log `config.md` and customized `automations/` into that backup
before proceeding. On a first migration with no `margo/` directory yet, omit that one copy after
verifying it genuinely does not exist. Do not ignore other copy failures.

For linked installs, back up the actual personal files at the link target as well; a copied link
is not a data backup. Verify the backup contains readable personal files and, if present, every
account's database and sidecars. Copying only a live `margo.sqlite3` can lose WAL transactions.
If writers cannot be stopped, use a SQLite-aware backup procedure instead of this filesystem copy.
Private permissions are not encryption; protect the backup with the same care as the originals.

## 4. Import the legacy queue

Load the [shared shell setup](README.md#before-running-a-cli-recipe), then:

```sh
proactive import-legacy \
  --directory "$COPILOT_HOME/skills/chief-of-staff/state"
proactive status
```

**Expected:** an atomic import with counts and `confirmed_coverage_imported:false`. An identical
repeat reports `replayed:true`. Queued and in-flight legacy items become pending work for output;
old surfaced records and cursors remain hints, not delivery receipts or successful coverage.

**Recovery:** malformed input fails without a partial batch import. Inspect the preserved source,
correct only after review, and retry. An empty import is not proof you selected the old directory.
Never run old JSON writers alongside the migrated SQLite writers.

## 5. Review and import commitments

```sh
work import-preview "$COPILOT_HOME/skills/chief-of-staff/commitments.md"
```

Markdown returns `safe_structured:false` with the original content for review. Ask:

> Review this tracker row by row. Preserve unknown owners and dates. Separate historical claims
> from obligations I am confirming now. Show me the structured import before applying it.

Prepare `reviewed-commitments.json` in the private manual directory. Its envelope is
`{"schema_version":1,"items":[...]}`. Each row needs a stable `legacy_id`, explicit `state`, and
`data` with title, direction, owner, due, and source references or an attributed
`confirmation_source`.
See the [exact import shape](../../skills/chief-of-staff/references/work-ledger.md).
Use [candidate ingestion](commitments-and-action-desk.md) instead when evidence is review-only.

```sh
work import-preview reviewed-commitments.json
```

After real approval of that exact file, retain its returned digest and actual human evidence in
`import-approval.json`. Evidence uses `decision:"import"`, `subject_id:"import:REVIEWED_SHA256"`,
and revision 1; the digest is the returned value, not that literal placeholder.

```sh
work import-commitments reviewed-commitments.json \
  --digest REVIEWED_SHA256 --evidence import-approval.json
work list --view all --json
work export-commitments commitments-view.md
```

**Expected:** one transaction; identical import replay returns the original item IDs. A changed
row under the same legacy ID fails rather than overwriting it. Edit the existing item through
review instead. Import never grants action approval or proves external completion.

Exports include confirmed work only. Keep this generated view separate from the old tracker.
Hand edits or a nonempty unmanaged destination block replacement. Review/import those changes
first; only then use `--expected-digest CURRENT_SHA256` to adopt the reviewed file hash. Adoption
does not import edits, and there is no two-way Markdown sync.

## 6. Check health, then sync schedules separately

```sh
python3 "$COPILOT_HOME/skills/chief-of-staff/scripts/margo_doctor.py" \
  --account "$MARGO_ACCOUNT" --state-dir "$MARGO_STATE_DIR" \
  --install-root "$COPILOT_HOME"
```

Unknown source coverage and missing workflow snapshots are expected until measured, not evidence
that migration failed or that the system is healthy.

Ask Margo to show the proposed automation sync diff. Review custom prompt changes, account,
workspace, agent, and schedule before approving those specific changes. Sync matches workflow
names; leave unrelated workflows alone. Copy deployment does not modify saved app prompts.
Start with one reviewed anchor rather than enabling everything at once.
See [automation health](automation-health.md) for snapshot verification and wrapper limitations.

The optional canvas is installed with `./install.sh --action-desk --dest "$HOME/.copilot"` from
the checkout. Reload extensions in the invoking Copilot app and verify it opens against the right
account. It is not needed for the CLI. It edits action proposals and requests foreground review;
it cannot approve actions or edit candidate work records.

**If anything is damaged:** stop writes, preserve current files and the backup, and investigate.
Do not delete the database to make health green. A rollback must retain new history and must not
restart legacy writers concurrently.
See [state operations](../../skills/chief-of-staff/references/state-operations.md).

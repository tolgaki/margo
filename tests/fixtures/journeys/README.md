# Journey contract fixtures

`scenarios.json` is the catalog-facing coverage contract. It uses `schema_version: 1`; every one of
the shared feature IDs appears exactly once, and each primary scenario `id` equals its
`feature_id`. A catalog entry should therefore include its feature ID in `scenario_ids`.

[Developer journey](../../../docs/development/README.md) ·
[Feature inventory](../../../docs/features.md) · [Model-trace evaluation](../../../evals/README.md)

## Source fixtures and scenario contracts

`sources.json` contains fictional evidence bundles with stable source IDs and versions.
It does not contain recorded mailbox or tenant data. `scenarios.json` explains which evidence
and assertions cover each catalog feature; it is not an execution log.

Each scenario records:

| Field | Meaning |
| --- | --- |
| `coverage_kind` | `runtime`, `procedure-contract`, or `model-evaluation` |
| `source_fixture` | Exact `sources.json#<id>` synthetic evidence bundle |
| `routing` | Expected and forbidden skill routes |
| `expected` | User-visible invariants, forbidden effects, and evidence limits |
| `evidence.selectors` | Existing concrete Python unittest selectors for runtime coverage only |
| `model_scenario_id` | Versioned `evals/scenarios-v1.json` ID for model-evaluation coverage only |
| `cli_refs` | Optional catalog-shaped CLI path and commands, verified by parser introspection |
| `canvas_refs` | Optional catalog-shaped canvas ID and declared read actions |

Runtime coverage must execute production helpers. Procedure contracts describe evidence and safety
boundaries but do not claim a model followed them. Model-evaluation coverage remains unevaluated
until an actual host/model trace is imported. Validate the complete mapping with:

```bash
python3 tools/journey_contracts.py --check
```

The checker emits machine-readable JSON, verifies every selector exists, resolves every source and
evaluation fixture, and reports evidence limitations. Passing the checker proves mapping integrity,
not end-to-end behavior or model quality.

## Add or revise coverage

1. Find the current feature ID in `docs/feature-catalog.json`. Reuse it for the same user
   outcome; coordinate changes to the shared ID inventory with the integration owner.
2. Reuse or add a minimal fictional source bundle. Use invented names and `example.com`;
   preserve unknowns, source gaps and hostile content needed by the scenario. Never sanitize a
   private transcript by merely replacing its email addresses.
3. Choose the evidence class truthfully. Runtime selectors must call production APIs;
   procedure contracts declare boundaries; model-evaluation rows bind a versioned prompt in
   `evals/scenarios-v1.json` but remain unevaluated without an imported actual trace.
4. Include the expected visible result, forbidden effects and what this evidence cannot prove.
   Add the failure case that could otherwise look like success: missing recap, changed approval,
   unavailable source, expired claim or an unknown effect after cancellation.
5. Update the catalog, user guide and route/CLI/canvas references together. If source content
   changes, review the fixture version and any evaluation fingerprint; do not pretend old
   traces exercised the new input.
6. Run the mapping check **and** the referenced existing tests. The mapping checker does not
   execute the selectors it validates.

For example, task cancellation/recovery is exercised by
`UserJourneyTests.test_task_pause_cancel_expiry_retry_unknown_write_and_resume` in
`tests/test_user_journeys.py`. Its fake provider records calls and returns fictional receipts;
the real task/work/coverage APIs still perform the state transitions.

```bash
PYTHONPATH=tests python3 -m unittest test_user_journeys test_journey_contracts
python3 tools/feature_catalog.py --check
python3 tools/journey_contracts.py --check
```

The production storage guard must remain unchanged. Tests that intentionally use repository
fixture roots scope their synthetic-only override and clean up afterward; do not reuse that
override for a real account. For testing the actual private-root guard through the canvas, use
the [real-core integration setup](../../../.github/extensions/margo-action-desk/README.md#validation).

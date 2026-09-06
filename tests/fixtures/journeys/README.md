# Journey contract fixtures

`scenarios.json` is the catalog-facing coverage contract. It uses `schema_version: 1`; every one of
the 67 shared feature IDs appears exactly once, and each primary scenario `id` equals its
`feature_id`. A catalog entry should therefore include its feature ID in `scenario_ids`.

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

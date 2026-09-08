# Synthetic model-behavior evaluation

These scenarios evaluate imported host/model traces separately from deterministic journey tests.
They do not call a model, generate an answer from expected text, or access a workplace account.

[Developer journey](../docs/development/README.md) ·
[Journey fixtures](../tests/fixtures/journeys/README.md) · [Evidence classes](../docs/development/architecture.md#evidence-classes)

## Where this fits

| Artifact | Responsibility |
| --- | --- |
| `evals/scenarios-v1.json` | Versioned synthetic prompts, deterministic assertions and named human rubric dimensions |
| `tests/fixtures/journeys/sources.json` | Fictional source bundles, IDs and versions used by those prompts |
| `tests/fixtures/journeys/scenarios.json` | Catalog-to-scenario mapping and declared evidence class |
| `tools/evaluate_agent_traces.py` | Validate imported evidence and evaluate its assertions; never run the assistant |
| `tests/test_journey_contracts.py` | Regression tests for the importer and contracts, not real model evaluation results |

The repository does not supply a universal live-host recorder or automatically generated
evaluation results. An explicitly chosen host/adapter must run the synthetic prompts, expose
only the fictional source tools, and preserve the actual trace. Do not point a scenario at a
real mailbox to fill in missing evidence. Optional embedding integration tests are another
evidence class again: they test a local encoder, not this conversational behavior.

## Prepare evidence

1. Select the scenario and its exact source bundle before the run. Record the code, skill,
   host and model versions, and compute the fixture fingerprint.
2. Run in an isolated synthetic host setup with no workplace credentials or outbound provider
   access. Keep malicious text in fixtures as data; never follow it as setup instructions.
3. Normalize the actual ordered routes/tools/decisions into the evaluator vocabulary without
   dropping calls or inventing approval. Preserve unavailable counters as `null`.
4. Have a human review each dimension named in that scenario's `human_rubric`. Record the
   actual reviewer, explicitly zoned review time and judgments; a model-generated rating is
   not a substitute.
5. Import the resulting bundle and inspect both per-scenario checks and aggregate status.
   Keep transcripts and imported result bundles out of ordinary repository commits; share
   deliberately sanitized summaries, not private host logs or account artifacts.

## Run

```bash
python3 tools/evaluate_agent_traces.py --results path/to/imported-results.json
```

Run from the repository root and replace the path with an actual imported bundle. The CLI
prints JSON to stdout. Defaults are `evals/scenarios-v1.json` and the journey `sources.json`;
use `--scenarios` / `--fixtures` only when intentionally evaluating another versioned set.

| Exit code | Meaning |
| --- | --- |
| `0` | Every scenario in the selected set passed with complete required evidence |
| `1` | At least one evaluated failure or an incomplete aggregate, including missing scenarios |
| `2` | Invalid input bundle or scenario/fixture contract prevented evaluation |

The results file uses `schema_version: 1` and contains a `traces` array. Each trace binds to an
exact scenario and fixture version and records:

- model, host, skill and source versions;
- the actual output text, labels and cited synthetic source IDs;
- ordered route, tool-call and exact-approval events;
- measured counters, with unavailable values recorded as `null`, never `0`;
- optional human judgments for each named qualitative rubric dimension.

`fixture_sha256` binds the actual source fixture content, not just its version label. It is
the SHA256 of the selected fixture object encoded as UTF-8 JSON with sorted keys, compact
separators and unescaped Unicode (`fixture_hash()` in the evaluator computes it). A changed
fixture with the old version label is not accepted as the same experiment.

Events use the evaluator's versioned tool vocabulary. Unknown tools or missing/contradictory
effect labels are not evaluated; do not label an unclassified operation as read-only. A host
adapter must preserve actual operations when normalizing a trace, and update the vocabulary
deliberately for new surfaces. The importer does not authenticate that adapter or the reviewer.

Write and human-approval events include the same exact account, action ID, revision, action
hash and request object. A changed request, revoked approval or reuse of a consumed grant
fails the ordering check. Synthetic values in unit tests are not real user consent.

Missing trace or output is `not_evaluated`. Deterministic failures remain failures. A trace with
passing deterministic checks but missing human judgments or resource metrics is `needs_review`,
not a pass. The evaluator never collapses routing, safety, grounding and qualitative review into a
single model-generated score.

## Trace shape

The object below is **one trace**, to be placed in the outer results bundle's `traces` array
beside `schema_version: 1`. It is explanatory, not a recorded run or passing fixture: substitute
actual provenance, output and hash, and supply real counters and human review. Missing traces
for other scenarios keep the aggregate incomplete even if this scenario passes.

```json
{
  "schema_version": 1,
  "scenario_id": "drafting-v1",
  "fixture_version": "commitment-v1",
  "fixture_sha256": "ACTUAL_CANONICAL_FIXTURE_SHA256",
  "provenance": {
    "model": "actual-model-name",
    "model_version": "actual-version",
    "host": "actual-host",
    "host_version": "actual-version",
    "skill_versions": {
      "chief-of-staff": "actual-source-revision",
      "mail": "actual-source-revision"
    },
    "source_versions": {"commitment-v1": "1"}
  },
  "output": {
    "text": "The actual model output",
    "source_refs": ["mail:commitment-ask"],
    "labels": ["draft", "approval-required"]
  },
  "events": [
    {"seq": 1, "type": "route", "skill": "chief-of-staff"},
    {"seq": 2, "type": "route", "skill": "mail"},
    {"seq": 3, "type": "tool_call", "tool": "mail.get", "effect": "read"}
  ],
  "counters": {
    "tool_calls": 1,
    "model_calls": 1,
    "input_tokens": null,
    "output_tokens": null,
    "elapsed_ms": 500
  }
}
```

The optional `human_review` object has exactly `reviewer`, `reviewed_at` and `judgments`.
`judgments` maps each scenario rubric dimension to a `rating` (`pass`, `fail` or `needs-review`)
and explanatory `notes`. Do not fill those fields from the expected answer merely to get a
green result.

This evidence can show how one recorded run behaved on fictional inputs. It cannot prove live
provider correctness, authenticated human identity, or that the model will behave the same way on
unseen inputs.

## Changing a scenario or the importer

Keep fixture versions and fingerprints honest when source content changes. Update the mapped
journey contract and catalog references in the same contribution. New tool families require a
deliberate vocabulary/effect classification and importer regression tests; unknown calls must
not become a successful read-only default.

Existing credential-free checks:

```bash
PYTHONPATH=tests python3 -m unittest test_journey_contracts.TraceEvaluatorTests
python3 tools/journey_contracts.py --check
```

Passing these checks establishes importer/contract behavior only. It does not create evidence
that an assistant followed the procedure.

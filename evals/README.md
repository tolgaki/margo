# Synthetic model-behavior evaluation

These scenarios evaluate imported host/model traces separately from deterministic journey tests.
They do not call a model, generate an answer from expected text, or access a workplace account.

## Run

```bash
python3 tools/evaluate_agent_traces.py --results path/to/imported-results.json
```

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

This evidence can show how one recorded run behaved on fictional inputs. It cannot prove live
provider correctness, authenticated human identity, or that the model will behave the same way on
unseen inputs.

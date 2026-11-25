---
mode: agent
description: Add a new Margo routine or skill (for example a weekly report), as one complete vertical slice
---
Add the routine described below as one complete vertical slice. Read `AGENTS.md` and
`docs/how-to/customize-with-ai-agents.md` (the "Add a skill or routine" recipe) first.

A complete slice changes these together:
1. The procedure: a new `skills/chief-of-staff/references/<name>.md` and a router row (with trigger phrases) in
   `skills/chief-of-staff/SKILL.md`, or a new `skills/<name>/SKILL.md` (then add it to `ALL_SKILLS` in `install.sh` and
   `install.ps1`).
2. If it runs on a schedule: `automations/<name>.md`, then `./tools/gen-automations-docs.sh --write`.
3. The feature catalog entry in `docs/feature-catalog.json`, then `python3 tools/feature_catalog.py --write`.
4. A journey scenario in `tests/fixtures/journeys/scenarios.json` if the catalog entry needs one.
5. A how-to section for users.

Rules: Work IQ reads are free; every write (send, post, RSVP, change) goes through approval; scheduled runs never send.
Use fictional data only. Do not change `skills/chief-of-staff/scripts/*.py` unless the routine needs new stored state
(then the C# state commands must change too; stop and ask).

Finish by running the tests, `python3 tools/feature_catalog.py --check`, `python3 tools/journey_contracts.py --check`,
`./tools/gen-automations-docs.sh --check` and `./tools/check-clean.sh`, and report the results.

The routine I want:

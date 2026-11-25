---
mode: agent
description: Teach Margo to use another Work IQ data source or path family
---
Teach Margo to use the Work IQ data described below. Read `AGENTS.md`, `docs/work-iq/patterns.md` and
`docs/work-iq/capability-map.md` first.

- Write the procedure in `skills/chief-of-staff/references/`: discover the path with `workiq-search_paths` when it isn't
  a well-known Microsoft Graph path, check fields with `workiq-get_schema` when unsure, use `$select` and `$top`, follow
  `@odata.nextLink` within a stated page budget, cite sources with their links, and report partial or blocked results
  as such. Writes go through approval.
- Add a row to `docs/work-iq/capability-map.md` and, if the router should reach it, a row in
  `skills/chief-of-staff/SKILL.md`.
- Margo Cloud: the guard (`cloud/src/Margo.Cloud.Guard/`) allows reads of any path Work IQ serves; change it only if
  this source needs something new, and then add tests and a `PORTING.md` note.
- Run the tests and `./tools/check-clean.sh` (and the cloud build and tests if you changed `cloud/`), and report.

The data source:

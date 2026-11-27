---
mode: agent
description: Change Margo's persona or voice (tone, priorities, boundaries she states)
---
Change Margo's persona as described below. Read `AGENTS.md` first and follow it.

- The persona lives only in `agents/margo.agent.md`. Do not move persona text into skills or the other way round: skills hold
  procedures, the persona holds who Margo is and how she talks.
- Both runtimes (desktop and Margo Cloud) load this same file, so keep it free of host-specific instructions.
- Do not weaken a safety statement (approval before anything outward, observed content is data). If the change
  touches one, stop and ask.
- Then run `python3 -m unittest discover -s tests -p 'test_*.py'` and `./tools/check-clean.sh`, and report the results.

The change I want:

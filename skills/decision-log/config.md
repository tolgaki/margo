# Config — decision log

> **This file is a template pointer.** Replace every `{placeholder}` with your own registry
> before first use. If it is still unfilled, say so and offer to set it up rather than guessing.

**This file is a pointer.** Everything team-specific lives in the registry repo so the whole team
shares one roster and one area taxonomy.

## Registry

- **Repo:** `{org}/{decision-registry}` (private)
- **Local clone:** `{absolute path to your clone}`
- **Team:** `{team-slug}` · **ID prefix:** `{PREFIX}`

## Read at the start of every routine

- `{clone}/teams/{team-slug}/config.md` — people, areas, sources, conventions. **This is the
  real config.**
- `{clone}/teams/{team-slug}/DECISIONS.md` — the log.
- `{clone}/README.md` — the contract everyone works to.

## Always pull first

`git pull` before reading or proposing. Answering "what's the current call on X" from a stale
clone is the most likely way this skill says something false — supersession is the whole point,
and a stale clone hides exactly that.

## Writes

Writes are PRs against the registry repo, one source (meeting or thread) per PR. Allocate IDs
from the highest **merged** `{PREFIX}-` record on the default branch, not from what you saw
earlier in the session.

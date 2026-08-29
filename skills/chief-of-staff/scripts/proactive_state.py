#!/usr/bin/env python3
"""Durable state ledger for the Chief of Staff's proactive routines.

Every scheduled run (anchor / sweep / ambient) starts as a *fresh session with no
memory*. Without an on-disk ledger the assistant re-surfaces the same email nine
times and the user turns it off. This script is that ledger.

Judgment stays in ``references/proactive.md``; bookkeeping lives here, because a
model rewriting JSON by hand drifts a little on every run and dedupe quietly rots.

Three pieces of state, all under ``../state/`` relative to this file:

  surfaced.json  {id: {tier, ts, note}}   — already told the user; never repeat
  queue.json     [item, ...]              — noticed but below the interrupt bar;
                                            drained by the next anchor
  cursors.json   {name: iso_ts}           — delta cursors ("since when")

IDs must be **stable identifiers** — a message id, an event id, ``owner/repo#123``.
Never a summary string: the wording changes between runs and dedupe silently fails.

Usage:
    proactive_state.py seen <id>                     # exit 0 = already surfaced, 1 = new
    proactive_state.py mark <id> --tier sweep [--note "..."]
    proactive_state.py queue-add --json '{"id": "...", "title": "..."}'
    proactive_state.py queue-list [--format json|text]
    proactive_state.py queue-drain [--format json|text]   # prints, holds in flight
    proactive_state.py queue-ack [id ...]                # retire after rendering
    proactive_state.py cursor-get <name>
    proactive_state.py cursor-set <name> <iso_ts>
    proactive_state.py prune [--days 30]
    proactive_state.py status
"""

import argparse
import errno
import json
import uuid
import os
import sys
import tempfile
from contextlib import contextmanager
from datetime import datetime, timedelta, timezone
from pathlib import Path

# File locking is platform-specific. Importing fcntl unconditionally kills the
# script at import time on Windows — before argparse runs, so every subcommand
# fails, not just the ones that lock. The installers support Windows, so this
# has to degrade rather than explode.
try:                        # POSIX
    import fcntl

    def _lock(fh):
        fcntl.flock(fh.fileno(), fcntl.LOCK_EX)

    def _unlock(fh):
        fcntl.flock(fh.fileno(), fcntl.LOCK_UN)
except ImportError:         # Windows
    try:
        import msvcrt

        def _lock(fh):
            msvcrt.locking(fh.fileno(), msvcrt.LK_LOCK, 1)

        def _unlock(fh):
            try:
                fh.seek(0)
                msvcrt.locking(fh.fileno(), msvcrt.LK_UNLCK, 1)
            except OSError:
                pass
    except ImportError:     # neither: run unlocked, and say so
        def _lock(fh):
            raise OSError("no file-locking primitive available on this platform")

        def _unlock(fh):
            pass

STATE_DIR = Path(__file__).resolve().parent.parent / "state"
SURFACED = STATE_DIR / "surfaced.json"
QUEUE = STATE_DIR / "queue.json"
INFLIGHT = STATE_DIR / "inflight.json"
CURSORS = STATE_DIR / "cursors.json"
LOCK = STATE_DIR / ".lock"

# Surfaced entries are kept only long enough to prevent repeats. Beyond this the
# item is stale anyway, and an unbounded file eventually costs more to read than
# the dedupe is worth.
DEFAULT_TTL_DAYS = 30


def now_iso() -> str:
    return datetime.now(timezone.utc).replace(microsecond=0).isoformat()


@contextmanager
def state_lock():
    """Serialize access across overlapping runs.

    The hourly sweep and an anchor can fire in the same minute; both mutate the
    queue. flock keeps the read-modify-write atomic. If the lock cannot be taken
    we proceed anyway rather than dropping the run — a duplicate line in a brief
    is a far smaller failure than a brief that never appears.
    """
    STATE_DIR.mkdir(parents=True, exist_ok=True)
    fh = None
    try:
        fh = open(LOCK, "a+")
        _lock(fh)
    except OSError as exc:  # pragma: no cover - platform/permission dependent
        print(f"WARNING: could not acquire state lock ({exc}); continuing unlocked",
              file=sys.stderr)
        fh = None
    try:
        yield
    finally:
        if fh is not None:
            try:
                _unlock(fh)
            finally:
                fh.close()


def load(path: Path, default):
    """Read JSON, tolerating absence and corruption.

    A truncated file (killed mid-write on an earlier build, disk full) must not
    take the whole routine down. We warn loudly and start clean: the cost is some
    repeated items once, not a dead brief.
    """
    if not path.exists():
        return default
    try:
        with open(path, "r", encoding="utf-8") as fh:
            return json.load(fh)
    except (json.JSONDecodeError, OSError) as exc:
        print(f"WARNING: {path.name} unreadable ({exc}); starting from empty",
              file=sys.stderr)
        return default


def save(path: Path, data) -> None:
    """Write atomically so a crash never leaves a half-written ledger."""
    STATE_DIR.mkdir(parents=True, exist_ok=True)
    fd, tmp = tempfile.mkstemp(dir=str(STATE_DIR), prefix=f".{path.name}.", suffix=".tmp")
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as fh:
            json.dump(data, fh, indent=2, sort_keys=True)
            fh.write("\n")
            fh.flush()
            os.fsync(fh.fileno())
        os.replace(tmp, path)
    except Exception:
        try:
            os.unlink(tmp)
        except OSError as exc:
            if exc.errno != errno.ENOENT:
                raise
        raise


def parse_iso(value: str):
    if not value:
        return None
    try:
        return datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        return None


# --------------------------------------------------------------------------- #
# commands
# --------------------------------------------------------------------------- #

def cmd_seen(args) -> int:
    """Exit 0 when the id was already surfaced, 1 when it is new.

    Inverted from the usual convention on purpose, so callers read naturally:
        if proactive_state.py seen "$ID"; then skip; fi
    """
    surfaced = load(SURFACED, {})
    hit = surfaced.get(args.id)
    if hit:
        print(f"seen {args.id} (tier={hit.get('tier', '?')} at {hit.get('ts', '?')})")
        return 0
    print(f"new {args.id}")
    return 1


def cmd_mark(args) -> int:
    with state_lock():
        surfaced = load(SURFACED, {})
        already = args.id in surfaced
        surfaced[args.id] = {"tier": args.tier, "ts": now_iso(), "note": args.note or ""}
        save(SURFACED, surfaced)
    print(f"{'re-marked' if already else 'marked'} {args.id} tier={args.tier}")
    return 0


def cmd_queue_add(args) -> int:
    """Queue an item for the next anchor.

    Items already surfaced are dropped here rather than at the call site, so a
    forgetful caller cannot reintroduce a duplicate into the morning brief.
    """
    try:
        item = json.loads(args.json)
    except json.JSONDecodeError as exc:
        print(f"ERROR: --json is not valid JSON: {exc}", file=sys.stderr)
        return 2
    if not isinstance(item, dict) or not item.get("id"):
        print("ERROR: queued item must be a JSON object with a stable 'id'", file=sys.stderr)
        return 2

    item.setdefault("ts", now_iso())
    item.setdefault("tier", args.tier or "sweep")

    with state_lock():
        if args.id_in_surfaced_check and item["id"] in load(SURFACED, {}):
            print(f"skipped {item['id']} (already surfaced)")
            return 0
        queue = load(QUEUE, [])
        if not isinstance(queue, list):
            queue = []
        if any(existing.get("id") == item["id"] for existing in queue):
            print(f"skipped {item['id']} (already queued)")
            return 0
        # Already handed to a brief that has not acked yet — it will be
        # re-delivered by the next drain, so queueing it again would duplicate it.
        inflight = load(INFLIGHT, [])
        if isinstance(inflight, list) and any(
            existing.get("id") == item["id"] for existing in inflight
        ):
            print(f"skipped {item['id']} (in flight)")
            return 0
        queue.append(item)
        save(QUEUE, queue)
    print(f"queued {item['id']}")
    return 0


def _render_queue(queue, fmt: str) -> None:
    if fmt == "json":
        print(json.dumps(queue, indent=2, sort_keys=True))
        return
    if not queue:
        print("(queue empty)")
        return
    for item in queue:
        title = item.get("title") or item.get("id")
        source = item.get("source", "")
        action = item.get("action", "")
        line = f"- [{item.get('tier', '?')}] {title}"
        if source:
            line += f" — {source}"
        if action:
            line += f" → {action}"
        print(line)


def cmd_queue_list(args) -> int:
    queue = load(QUEUE, [])
    _render_queue(queue if isinstance(queue, list) else [], args.format)
    return 0


def cmd_queue_drain(args) -> int:
    """Hand the queue to the caller and hold it in flight — do NOT acknowledge.

    Draining is what turns the morning brief from a fresh scrape into an
    accumulation of what the day already noticed.

    Acknowledgement is deliberately NOT done here. `daily-brief.md` drains before
    it fetches anything, so at drain time nothing has reached the user yet. If the
    brief then fails — a Graph 500, a timeout, an interrupted run — marking on
    drain would both discard those items and poison them: `queue-add` skips
    anything already surfaced, so a later sweep that re-notices the same VP
    escalation would silently drop it until the 30-day prune.

    Instead items move to an in-flight holding area. A later `queue-ack` retires
    them once the brief actually rendered. A drain that is never acked returns the
    same items next time, which is the safe failure.
    """
    with state_lock():
        queue = load(QUEUE, [])
        inflight = load(INFLIGHT, [])
        if not isinstance(queue, list):
            queue = []
        if not isinstance(inflight, list):
            inflight = []
        # Re-deliver anything still in flight from a previous failed run, then
        # take the new queue. De-dupe by id, oldest first.
        seen_ids = set()
        merged = []
        for item in inflight + queue:
            if item.get("id") in seen_ids:
                continue
            seen_ids.add(item.get("id"))
            merged.append(item)
        # Stamp this hand-out with a batch id. Without it, two overlapping runs
        # share one in-flight pool and a bare ack from the first retires items
        # the second drained but never rendered — silently and unrecoverably.
        batch = uuid.uuid4().hex[:12]
        stamp = now_iso()
        for item in merged:
            item["batch"] = batch
            item.setdefault("drained_at", stamp)
        if queue or merged != inflight:
            save(INFLIGHT, merged)
            save(QUEUE, [])
    # stderr, so --format json keeps a clean stdout for piping.
    print(f"batch {batch} ({len(merged)} item(s) in flight)", file=sys.stderr)
    _render_queue(merged, args.format)
    return 0


def cmd_queue_ack(args) -> int:
    """Retire in-flight items after the brief actually rendered.

    Call this only once the output reached the user. Acking ids that are not in
    flight is not an error — a partially rendered brief can ack what it showed.

    You must say WHAT you rendered: either the explicit ids, or the batch id
    that `queue-drain` printed. A bare "ack everything in flight" retires items
    another concurrent run drained and never showed anyone.
    """
    with state_lock():
        inflight = load(INFLIGHT, [])
        if not isinstance(inflight, list):
            inflight = []
        if args.batch:
            wanted = {i.get("id") for i in inflight if i.get("batch") == args.batch}
            if not wanted:
                print(f"WARNING: no in-flight items for batch {args.batch}", file=sys.stderr)
        elif args.ids:
            wanted = set(args.ids)
        elif args.all:
            wanted = {i.get("id") for i in inflight}
        else:
            print("error: queue-ack needs ids, --batch <id>, or an explicit --all",
                  file=sys.stderr)
            return 2
        surfaced = load(SURFACED, {})
        stamp = now_iso()
        acked = 0
        remaining = []
        for item in inflight:
            if item.get("id") in wanted:
                surfaced[item["id"]] = {
                    "tier": item.get("tier", "queue"),
                    "ts": stamp,
                    "note": "rendered to user",
                }
                acked += 1
            else:
                remaining.append(item)
        save(SURFACED, surfaced)
        save(INFLIGHT, remaining)
    print(f"acked {acked} item(s); {len(remaining)} still in flight")
    return 0


def cmd_cursor_get(args) -> int:
    cursors = load(CURSORS, {})
    value = cursors.get(args.name, "")
    print(value)
    # Exit 1 signals "no cursor yet" so a first run can widen its window instead
    # of silently sweeping from the epoch.
    return 0 if value else 1


def cmd_cursor_set(args) -> int:
    if parse_iso(args.timestamp) is None:
        print(f"ERROR: '{args.timestamp}' is not an ISO-8601 timestamp", file=sys.stderr)
        return 2
    with state_lock():
        cursors = load(CURSORS, {})
        cursors[args.name] = args.timestamp
        save(CURSORS, cursors)
    print(f"cursor {args.name} = {args.timestamp}")
    return 0


def cmd_prune(args) -> int:
    cutoff = datetime.now(timezone.utc) - timedelta(days=args.days)
    removed = 0
    with state_lock():
        surfaced = load(SURFACED, {})
        kept = {}
        for key, meta in surfaced.items():
            ts = parse_iso(meta.get("ts", ""))
            # Undated entries predate this format; treat them as expired.
            if ts is not None and ts >= cutoff:
                kept[key] = meta
            else:
                removed += 1
        if removed:
            save(SURFACED, kept)

        # In-flight items age too. An item stuck here means briefs kept failing;
        # left alone it re-renders forever and grows the file without bound.
        # Dropping it is data loss, so say exactly what went and why.
        inflight = load(INFLIGHT, [])
        if not isinstance(inflight, list):
            inflight = []
        still, dropped = [], []
        for item in inflight:
            ts = parse_iso(item.get("drained_at", ""))
            (still if (ts is not None and ts >= cutoff) else dropped).append(item)
        if dropped:
            save(INFLIGHT, still)
    print(f"pruned {removed} surfaced entr{'y' if removed == 1 else 'ies'} older than {args.days}d")
    if dropped:
        print(f"WARNING: dropped {len(dropped)} in-flight item(s) never acknowledged "
              f"in {args.days}d — a brief has been failing:", file=sys.stderr)
        for item in dropped:
            print(f"  {item.get('id')}  {item.get('title', '')}", file=sys.stderr)
    return 0


def cmd_status(args) -> int:
    surfaced = load(SURFACED, {})
    queue = load(QUEUE, [])
    cursors = load(CURSORS, {})
    print(f"state dir : {STATE_DIR}")
    print(f"surfaced  : {len(surfaced)}")
    print(f"queued    : {len(queue) if isinstance(queue, list) else 0}")
    inflight = load(INFLIGHT, [])
    n_inflight = len(inflight) if isinstance(inflight, list) else 0
    print(f"in flight : {n_inflight}"
          + ("   <- drained but never acked; a brief failed" if n_inflight else ""))
    if cursors:
        print("cursors   :")
        for name, value in sorted(cursors.items()):
            print(f"  {name} = {value}")
    else:
        print("cursors   : (none)")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)

    p = sub.add_parser("seen", help="exit 0 if already surfaced, 1 if new")
    p.add_argument("id")
    p.set_defaults(func=cmd_seen)

    p = sub.add_parser("mark", help="record an id as surfaced")
    p.add_argument("id")
    p.add_argument("--tier", default="manual")
    p.add_argument("--note", default="")
    p.set_defaults(func=cmd_mark)

    p = sub.add_parser("queue-add", help="queue an item for the next anchor")
    p.add_argument("--json", required=True, help='JSON object, must contain "id"')
    p.add_argument("--tier", default=None)
    p.add_argument("--no-surfaced-check", dest="id_in_surfaced_check",
                   action="store_false", default=True,
                   help="queue even if the id was already surfaced")
    p.set_defaults(func=cmd_queue_add)

    p = sub.add_parser("queue-list", help="show the pending queue")
    p.add_argument("--format", choices=["json", "text"], default="text")
    p.set_defaults(func=cmd_queue_list)

    p = sub.add_parser("queue-drain",
                       help="print the queue and hold it in flight (does NOT acknowledge)")
    p.add_argument("--format", choices=["json", "text"], default="text")
    p.set_defaults(func=cmd_queue_drain)

    p = sub.add_parser("queue-ack",
                       help="retire in-flight items after the brief actually rendered")
    p.add_argument("ids", nargs="*", help="ids you actually rendered")
    p.add_argument("--batch", default=None,
                   help="ack the batch id that queue-drain printed")
    p.add_argument("--all", action="store_true",
                   help="ack everything in flight; unsafe if runs overlap")
    p.set_defaults(func=cmd_queue_ack)

    p = sub.add_parser("cursor-get", help="read a delta cursor")
    p.add_argument("name")
    p.set_defaults(func=cmd_cursor_get)

    p = sub.add_parser("cursor-set", help="write a delta cursor")
    p.add_argument("name")
    p.add_argument("timestamp")
    p.set_defaults(func=cmd_cursor_set)

    p = sub.add_parser("prune", help="drop old surfaced entries")
    p.add_argument("--days", type=int, default=DEFAULT_TTL_DAYS)
    p.set_defaults(func=cmd_prune)

    p = sub.add_parser("status", help="summarize current state")
    p.set_defaults(func=cmd_status)

    args = parser.parse_args()
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())

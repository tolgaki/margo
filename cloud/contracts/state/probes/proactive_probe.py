#!/usr/bin/env python3
"""Contract probe for proactive_state.py's library API and clock (reference implementation).

Not a Margo command. tools/state_parity.py runs this file and its C# twin
(Margo.Cloud.State.Probes.ProactiveProbeScript, ``margo-state proactive_probe.py``)
on the same inputs. It reaches what separate ``proactive_state.py`` processes cannot:

  [--now TS] [--account A] [--state-dir D] cli ARGS...
      proactive_state.main(ARGS) (with --account/--state-dir prepended when given)
      under a fixed clock (utc_now and datetime.now), so freshness, missed cadences,
      escalation, lease expiry and future-dating are deterministic.
  [--now TS] --account A --state-dir D call
      stdin: JSON list of {"fn": NAME, "args": [...], "outer": "commit"|"rollback"};
      calls proactive_state.NAME(conn, *args) on one connection, as task_runs.py and
      memory_dream.py do (optionally inside an outer margo_store.transaction that
      commits or is rolled back), and prints "NAME -> canonical result" or
      "NAME -> !Type[: message]" (messages for StateError and sqlite3 errors only).
      Non-data arguments must have the documented Python types (str, int, None,
      list of str); data arguments may be any JSON value, including NaN/Infinity.
  [--account A --state-dir D] sql
      stdin: JSON list of [SQL, [params]] run in one ``with conn:`` block on the
      private store (fixtures: backdated leases, corrupt payloads, dropped tables).
      Prints each statement's rows as a JSON list, or its rowcount.
  write [--hex] PATH        stdin (or hex-decoded stdin) to PATH, mode 0600.
  link PATH TARGET          a symbolic link PATH -> TARGET.

In cli and call arguments (and cli stdin), @{attempt}, @{batch}, @{source}, @{item}
and @{publication} are replaced by the newest attempt id, batch id, source key,
item key or publication id (by rowid); @{attempt:1} is the first, @{batch:-2} the
second newest. Random identifiers can thus be chained across steps.
It writes only below the paths it is given.
"""

import io
import json
import os
import re
import sqlite3
import sys
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[4]
sys.path.insert(0, str(ROOT / "skills" / "chief-of-staff" / "scripts"))

import margo_store  # noqa: E402
import proactive_state  # noqa: E402

FUNCTIONS = ("initialize", "validate_schema", "queue_add", "queue_drain", "queue_release", "queue_renew",
             "publication_record", "publication_publish", "queue_ack", "publication_review", "coverage_start",
             "coverage_retire", "coverage_page", "coverage_finish", "coverage_status", "publication_show",
             "publication_list", "status_report", "import_legacy", "_attempt")
REFERENCES = {
    "attempt": "SELECT id FROM proactive_attempts ORDER BY rowid",
    "batch": "SELECT id FROM proactive_batches ORDER BY rowid",
    "source": "SELECT source_key FROM proactive_sources ORDER BY rowid",
    "item": "SELECT item_key FROM proactive_items ORDER BY rowid",
    "publication": "SELECT id FROM proactive_publications ORDER BY rowid",
}
PLACEHOLDER = re.compile(r"@\{(attempt|batch|source|item|publication)(?::(-?[0-9]+))?\}")


class _Rollback(Exception):
    """Unwinds the outer transaction of a call with "outer": "rollback"."""


def _fix_clock(text):
    fixed = datetime.fromisoformat(text).astimezone(timezone.utc)

    class FixedDatetime(datetime):
        @classmethod
        def now(cls, tz=None):
            return fixed

    proactive_state.datetime = FixedDatetime
    proactive_state.utc_now = lambda: fixed.isoformat(timespec="microseconds")


def _resolver(conn):
    def resolve(match):
        rows = [row[0] for row in conn.execute(REFERENCES[match.group(1)])]
        index = int(match.group(2)) if match.group(2) is not None else -1
        position = index - 1 if index > 0 else index
        if index == 0 or not -len(rows) <= position < len(rows):
            raise SystemExit("probe: no %s #%d" % (match.group(1), index))
        return rows[position]
    return resolve


def _substitute(value, resolve):
    if isinstance(value, str):
        return PLACEHOLDER.sub(resolve, value)
    if isinstance(value, list):
        return [_substitute(item, resolve) for item in value]
    if isinstance(value, dict):
        return {key: _substitute(item, resolve) for key, item in value.items()}
    return value


def _render(value):
    if isinstance(value, sqlite3.Row):
        value = dict(value)
    return json.dumps(value, sort_keys=True)


def _error(exc):
    if isinstance(exc, (margo_store.StateError, sqlite3.Error)):
        return "!%s: %s" % (type(exc).__name__, exc)
    return "!" + type(exc).__name__


def _cli(account, state_dir, rest):
    state = (["--account", account] if account is not None else []) + \
            (["--state-dir", state_dir] if state_dir is not None else [])
    stdin = sys.stdin.read()
    if any(PLACEHOLDER.search(text) for text in rest + [stdin]):
        conn = margo_store.connect(account, state_dir)
        try:
            resolve = _resolver(conn)
            rest = [PLACEHOLDER.sub(resolve, arg) for arg in rest]
            stdin = PLACEHOLDER.sub(resolve, stdin)
        finally:
            conn.close()
    sys.stdin = io.StringIO(stdin)
    sys.argv[0] = "proactive_state.py"
    return proactive_state.main(state + rest)


def _call(account, state_dir):
    calls = json.loads(sys.stdin.read())
    conn = margo_store.connect(account, state_dir)
    try:
        resolve = _resolver(conn)
        for call in calls:
            name = call["fn"]
            if name not in FUNCTIONS:
                raise SystemExit("probe: unknown function " + name)
            function = getattr(proactive_state, name)
            args = _substitute(call.get("args", []), resolve)
            outer = call.get("outer")
            try:
                if outer is None:
                    result = function(conn, *args)
                else:
                    try:
                        with margo_store.transaction(conn):
                            result = function(conn, *args)
                            if outer == "rollback":
                                raise _Rollback()
                    except _Rollback:
                        pass
                print(name + " -> " + _render(result))
            except Exception as exc:
                print(name + " -> " + _error(exc))
    finally:
        conn.close()
    return 0


def _sql(account, state_dir):
    statements = json.loads(sys.stdin.read())
    conn = margo_store.connect(account, state_dir)
    try:
        with conn:
            for sql, params in statements:
                cursor = conn.execute(sql, params)
                rows = cursor.fetchall()
                print(json.dumps([list(row) for row in rows]) if rows else cursor.rowcount)
    finally:
        conn.close()
    return 0


def main(argv):
    now = account = state_dir = None
    argv = list(argv)
    while argv and argv[0] in ("--now", "--account", "--state-dir"):
        if len(argv) < 2:
            raise SystemExit("probe: %s needs a value" % argv[0])
        name, value = argv[0], argv[1]
        del argv[:2]
        if name == "--now":
            now = value
        elif name == "--account":
            account = value
        else:
            state_dir = value
    if not argv:
        raise SystemExit("probe: choose cli, call, sql, write or link")
    mode, rest = argv[0], argv[1:]
    if now is not None:
        _fix_clock(now)
    if mode == "cli":
        return _cli(account, state_dir, rest)
    if mode == "call":
        return _call(account, state_dir)
    if mode == "sql":
        return _sql(account, state_dir)
    if mode == "write":
        binary = rest[:1] == ["--hex"]
        path = Path(rest[-1])
        data = sys.stdin.read()
        content = bytes.fromhex(data) if binary else data.encode("utf-8")
        path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
        descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(content)
        return 0
    if mode == "link":
        Path(rest[0]).parent.mkdir(mode=0o700, parents=True, exist_ok=True)
        os.symlink(rest[1], rest[0])
        return 0
    raise SystemExit("probe: unknown mode " + mode)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))

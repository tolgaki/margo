#!/usr/bin/env python3
"""Contract probe for task runs (reference implementation).

Not a Margo command. tools/state_parity.py runs this file and its C# twin
(Margo.Cloud.State.Probes.TasksProbeScript, ``margo-state tasks_probe.py``)
on the same program, so what separate ``task_state.py`` processes cannot reach
is judged byte for byte:

* flows whose inputs depend on the current time or on earlier random IDs and
  tokens (bindings observed within five minutes, deadlines within seven days,
  claims, charges, receipts, approvals): each step runs ``task_state.main(argv)``
  in process, with arguments and ``--input`` JSON templated from the clock and
  from earlier results;
* the module clock: ``{"clock": ...}`` replaces ``task_runs.clock`` as Python's
  tests patch it, so expired leases, deadlines and provider backoff are reached
  without waiting (only ``task_runs.clock``; ``utc_now`` stamps, the ledger and
  the coverage journal keep the real time);
* the library API other ports call (``TaskStore`` methods, ``from_connection``,
  ``zero``, ``task_identity``, ...), the ledger and coverage journal on the
  store's connection, nested transactions and SQL fixtures.

Program (stdin): a JSON list of steps, each one of::

    {"cli": [ARGS...], "input": VALUE?, "stdin": TEXT?, "save": NAME?}
    {"call": METHOD, "on": "store"|"module"|"ledger"|"proactive"|"borrowed", "args": [...], "kwargs": {...},
     "save": NAME?, "read_only": true?, "outer": "rollback"?}
    {"sql": STATEMENT, "params": [...]}
    {"clock": TEMPLATE|null, "offset": "+Ns"?}
    {"write": NAME, "json": VALUE}
    {"write": NAME, "text": TEXT | "hex": BYTES, "repeat": N?, "prefix": TEXT?, "suffix": TEXT?, "suffix_hex": BYTES?}

"store" calls a method of a new TaskStore(account, state_dir, read_only),
"ledger" one of its ledger, "proactive" proactive_state.METHOD(store.conn, ...),
"borrowed" a method of TaskStore.from_connection(margo_store.connect(...)) (or
only borrows when METHOD is null), "module" a task_runs function. "outer":
"rollback" runs the call inside store.transaction() and then rolls it back.
Results print as canonical JSON (datetimes as isoformat()), errors as
"!Type: message". A text or hex write is PREFIX + BODY * N + SUFFIX + SUFFIX_HEX (UTF-8).

Strings are templates: in JSON values, "${EXPR}" alone is replaced by the JSON
value of EXPR, and "${EXPR}" inside a longer string by its text (a non-string as
JSON); command-line arguments, stdin and text files always get the text. EXPR is
``now``, ``now+N`` or ``now-N`` with N ending in s/m/h/d (the real
``datetime.now(timezone.utc) +/- N``, ISO text), ``dir``, ``account``, or
``NAME.key.0.key`` (a saved result). A clock step fixes ``task_runs.clock`` to
the templated timestamp plus the offset; null restores it. It writes only below
--state-dir and --dir.
"""

import argparse
import contextlib
import hashlib
import io
import json
import re
import sqlite3
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[4]
sys.path.insert(0, str(ROOT / "skills" / "chief-of-staff" / "scripts"))

import margo_store  # noqa: E402
import proactive_state  # noqa: E402
import task_runs  # noqa: E402
import task_state  # noqa: E402

WHOLE = re.compile(r"^\$\{([^}]+)\}$")
PART = re.compile(r"\$\{([^}]+)\}")
CLOCK = re.compile(r"^now(?:([+-])([0-9]+)([smhd]))?$")
OFFSET = re.compile(r"^([+-])([0-9]+)([smhd])$")
UNITS = {"s": "seconds", "m": "minutes", "h": "hours", "d": "days"}
REAL_CLOCK = task_runs.clock


class _Rollback(Exception):
    """Unwinds the outer transaction of a call with "outer": "rollback"."""


def delta(sign, count, unit):
    value = timedelta(**{UNITS[unit]: int(count)})
    return value if sign == "+" else -value


class Probe:
    def __init__(self, account, state_dir, directory):
        self.account, self.state_dir, self.directory = account, state_dir, directory
        self.saved = {}

    def value(self, expr):
        clock = CLOCK.match(expr)
        if clock:
            moment = datetime.now(timezone.utc)
            if clock.group(1):
                moment += delta(clock.group(1), clock.group(2), clock.group(3))
            return moment.isoformat()
        if expr == "dir":
            return self.directory
        if expr == "account":
            return self.account
        parts = expr.split(".")
        value = self.saved[parts[0]]
        for part in parts[1:]:
            value = value[int(part)] if isinstance(value, list) else value[part]
        return value

    def template(self, value):
        if isinstance(value, str):
            whole = WHOLE.match(value)
            return self.value(whole.group(1)) if whole else self.text(value)
        if isinstance(value, list):
            return [self.template(item) for item in value]
        if isinstance(value, dict):
            return {key: self.template(item) for key, item in value.items()}
        return value

    def text(self, value):
        """Template a string, always as text (command-line arguments)."""

        def text(match):
            found = self.value(match.group(1))
            return found if isinstance(found, str) else json.dumps(found, ensure_ascii=False)

        return PART.sub(text, value)

    def cli(self, index, step):
        args = [self.text(arg) for arg in step["cli"]]
        if "input" in step:
            path = Path(self.directory) / ("input-%d.json" % index)
            path.write_text(json.dumps(self.template(step["input"]), ensure_ascii=False), encoding="utf-8")
            args += ["--input", str(path)]
        argv = ["--account", self.account, "--state-dir", self.state_dir] + args
        out, err = io.StringIO(), io.StringIO()
        old_stdin, sys.stdin = sys.stdin, io.StringIO(self.text(step.get("stdin", "")))
        try:
            with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
                try:
                    code = task_state.main(argv)
                except SystemExit as exc:
                    code = exc.code
                except Exception as exc:  # the twin reports the same Python class and message
                    code = "uncaught %s: %s" % (type(exc).__name__, exc)
        finally:
            sys.stdin = old_stdin
        print("## %d cli %s: exit %s" % (index, args[0] if args else "", code))
        sys.stdout.write(out.getvalue())
        if err.getvalue():
            print("stderr: " + err.getvalue().rstrip("\n"))
        if code == 0 and step.get("save"):
            self.saved[step["save"]] = json.loads(out.getvalue())

    def call(self, index, step):
        on = step.get("on", "store")
        args = self.template(step.get("args", []))
        kwargs = self.template(step.get("kwargs", {}))
        store = connection = None
        try:
            if on == "module":
                result = getattr(task_runs, step["call"])(*args, **kwargs)
            else:
                if on == "borrowed":
                    connection = margo_store.connect(self.account, self.state_dir)
                    store = task_runs.TaskStore.from_connection(connection, step.get("borrow_account", self.account))
                else:
                    store = task_runs.TaskStore(self.account, self.state_dir, read_only=step.get("read_only", False))
                if on == "ledger":
                    method = getattr(store.ledger, step["call"])
                elif on == "proactive":
                    method = getattr(proactive_state, step["call"])
                    args = [store.conn] + list(args)
                else:
                    method = getattr(store, step["call"]) if step["call"] else (lambda: "borrowed")
                if step.get("outer") == "rollback":
                    try:
                        with store.transaction():
                            result = method(*args, **kwargs)
                            raise _Rollback()
                    except _Rollback:
                        pass
                else:
                    result = method(*args, **kwargs)
            if isinstance(result, datetime):
                result = result.isoformat()
            line = margo_store.canonical_json(result)
            if step.get("save"):
                self.saved[step["save"]] = result
        except Exception as exc:
            line = "!%s: %s" % (type(exc).__name__, exc)
        finally:
            if store is not None:
                store.close()
            if connection is not None:
                connection.close()
        print("## %d call %s.%s" % (index, on, step["call"]))
        print(line)

    def sql(self, index, step):
        database = Path(self.state_dir) / hashlib.sha256(self.account.encode("utf-8")).hexdigest() / "margo.sqlite3"
        connection = sqlite3.connect(str(database))
        try:
            cursor = connection.execute(step["sql"], self.template(step.get("params", [])))
            rows = [list(row) for row in cursor.fetchall()]
            connection.commit()
            line = json.dumps({"rowcount": cursor.rowcount, "rows": rows}, ensure_ascii=False)
        except sqlite3.Error as exc:
            line = "!%s: %s" % (type(exc).__name__, exc)
        finally:
            connection.close()
        print("## %d sql" % index)
        print(line)

    def clock(self, index, step):
        if step["clock"] is None:
            task_runs.clock = REAL_CLOCK
            print("## %d clock real" % index)
            return
        fixed = datetime.fromisoformat(self.text(step["clock"]))
        offset = OFFSET.match(step.get("offset", "+0s"))
        fixed += delta(offset.group(1), offset.group(2), offset.group(3))
        task_runs.clock = lambda: fixed
        print("## %d clock fixed" % index)

    def write(self, index, step):
        path = Path(self.directory) / step["write"]
        if "json" in step:
            path.write_text(json.dumps(self.template(step["json"]), ensure_ascii=False), encoding="utf-8")
        else:
            body = bytes.fromhex(step["hex"]) if "hex" in step else self.text(step["text"]).encode("utf-8")
            path.write_bytes(self.text(step.get("prefix", "")).encode("utf-8") + body * step.get("repeat", 1)
                             + self.text(step.get("suffix", "")).encode("utf-8") + bytes.fromhex(step.get("suffix_hex", "")))
        print("## %d write %s" % (index, step["write"]))

    def run(self, program):
        for index, step in enumerate(program):
            if "cli" in step:
                self.cli(index, step)
            elif "call" in step:
                self.call(index, step)
            elif "sql" in step:
                self.sql(index, step)
            elif "clock" in step:
                self.clock(index, step)
            elif "write" in step:
                self.write(index, step)
            else:
                raise SystemExit("unknown step %d" % index)


def main():
    parser = argparse.ArgumentParser(prog="tasks_probe.py")
    parser.add_argument("--account", required=True)
    parser.add_argument("--state-dir", required=True)
    parser.add_argument("--dir", required=True)
    args = parser.parse_args()
    program = json.loads(sys.stdin.read())
    if not isinstance(program, list):
        raise SystemExit("stdin must be a JSON list of steps")
    # argparse's prog in the task_state.py help and usage texts comes from sys.argv[0].
    sys.argv = ["task_state.py"]
    Path(args.dir).mkdir(mode=0o700, exist_ok=True)
    Probe(args.account, args.state_dir, args.dir).run(program)
    return 0


if __name__ == "__main__":
    sys.exit(main())

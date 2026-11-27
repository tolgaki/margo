#!/usr/bin/env python3
"""Contract probe for the work ledger (reference implementation).

Not a Margo command. tools/state_parity.py runs this file and its C# twin
(Margo.Cloud.State.Probes.WorkLedgerProbeScript, ``margo-state work_ledger_probe.py``)
on the same program, so two things the plain ``work_state.py`` scenarios cannot
reach are judged byte for byte:

* flows whose inputs depend on the current time or on earlier random IDs
  (approve, begin, finish, reconcile, artifact-shared, edit/defer/dismiss of an
  action): each step runs ``work_state.main(argv)`` in process, with arguments
  and ``--input`` JSON templated from the clock and from earlier results;
* the library API other ports call directly (``Ledger.find_source``,
  ``invalidate_approval``, ``from_connection``, ``validate_receipt``, ``event``,
  ``refs_stale``, ``source_refs``, ``human``, ``digest``, ...).

Program (stdin): a JSON list of steps, each one of::

    {"cli": [ARGS...], "input": VALUE?, "stdin": TEXT?, "save": NAME?}
    {"call": METHOD, "on": "ledger"|"products"|"module"|"borrowed", "args": [...], "kwargs": {...},
     "save": NAME?, "borrow_account": ACCOUNT?, "row_factory": false?, "read_only": true?}
    {"sql": STATEMENT, "params": [...]}
    {"write": NAME, "json": VALUE} | {"write": NAME, "text": TEXT} | {"write": NAME, "hex": BYTES}
    {"symlink": NAME, "target": PATH}

Strings are templates: in JSON values, "${EXPR}" alone is replaced by the JSON
value of EXPR, and "${EXPR}" inside a longer string by its text (a non-string as
JSON); command-line arguments, stdin and text files always get the text. EXPR is ``now``, ``now+N`` or
``now-N`` with N ending in s/m/h/d (``datetime.now(timezone.utc) +/- N``, ISO
text), ``dir``, ``account``, or ``NAME.key.0.key`` (a saved result). It writes
only below --state-dir and --dir.
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
import work_ledger  # noqa: E402
import work_productivity  # noqa: E402
import work_state  # noqa: E402

WHOLE = re.compile(r"^\$\{([^}]+)\}$")
PART = re.compile(r"\$\{([^}]+)\}")
CLOCK = re.compile(r"^now(?:([+-])([0-9]+)([smhd]))?$")
UNITS = {"s": "seconds", "m": "minutes", "h": "hours", "d": "days"}


class Probe:
    def __init__(self, account, state_dir, directory):
        self.account, self.state_dir, self.directory = account, state_dir, directory
        self.saved = {}

    def value(self, expr):
        clock = CLOCK.match(expr)
        if clock:
            moment = datetime.now(timezone.utc)
            if clock.group(1):
                delta = timedelta(**{UNITS[clock.group(3)]: int(clock.group(2))})
                moment = moment + delta if clock.group(1) == "+" else moment - delta
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
                    code = work_state.main(argv)
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
        on = step.get("on", "ledger")
        args = self.template(step.get("args", []))
        kwargs = self.template(step.get("kwargs", {}))
        ledger = connection = None
        try:
            if on == "module":
                result = getattr(work_ledger, step["call"])(*args, **kwargs)
            elif on == "static":
                result = getattr(work_ledger.Ledger, step["call"])(*args, **kwargs)
            elif on == "borrowed":
                connection = margo_store.connect(self.account, self.state_dir, read_only=step.get("read_only", False))
                if step.get("row_factory") is False:
                    connection.row_factory = None
                ledger = work_ledger.Ledger.from_connection(connection, step.get("borrow_account", self.account))
                result = getattr(ledger, step["call"])(*args, **kwargs) if step["call"] else "borrowed"
            else:
                ledger = work_ledger.Ledger(self.account, self.state_dir)
                target = work_productivity.Productivity(ledger) if on == "products" else ledger
                result = getattr(target, step["call"])(*args, **kwargs)
            if isinstance(result, datetime):
                result = result.isoformat()
            line = json.dumps(result, ensure_ascii=False)
            if step.get("save"):
                self.saved[step["save"]] = result
        except Exception as exc:
            line = "!%s: %s" % (type(exc).__name__, exc)
        finally:
            if ledger is not None:
                ledger.close()
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

    def write(self, index, step):
        path = Path(self.directory) / step["write"]
        if "json" in step:
            path.write_text(json.dumps(self.template(step["json"]), ensure_ascii=False), encoding="utf-8")
        elif "hex" in step:
            path.write_bytes(bytes.fromhex(step["hex"]))
        else:
            path.write_text(self.text(step["text"]), encoding="utf-8")
        print("## %d write %s" % (index, step["write"]))

    def symlink(self, index, step):
        (Path(self.directory) / step["symlink"]).symlink_to(self.text(step["target"]))
        print("## %d symlink %s" % (index, step["symlink"]))

    def run(self, program):
        for index, step in enumerate(program):
            if "cli" in step:
                self.cli(index, step)
            elif "call" in step:
                self.call(index, step)
            elif "sql" in step:
                self.sql(index, step)
            elif "write" in step:
                self.write(index, step)
            elif "symlink" in step:
                self.symlink(index, step)
            else:
                raise SystemExit("unknown step %d" % index)


def main():
    parser = argparse.ArgumentParser(prog="work_ledger_probe.py")
    parser.add_argument("--account", required=True)
    parser.add_argument("--state-dir", required=True)
    parser.add_argument("--dir", required=True)
    args = parser.parse_args()
    program = json.loads(sys.stdin.read())
    if not isinstance(program, list):
        raise SystemExit("stdin must be a JSON list of steps")
    # argparse's prog in the work_state.py help and usage texts comes from sys.argv[0].
    sys.argv = ["work_state.py"]
    Path(args.dir).mkdir(mode=0o700, exist_ok=True)
    Probe(args.account, args.state_dir, args.dir).run(program)
    return 0


if __name__ == "__main__":
    sys.exit(main())

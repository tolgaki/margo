#!/usr/bin/env python3
"""Contract probe for memory learning and consolidation (reference implementation).

Not a Margo command. tools/state_parity.py runs this file and its C# twin
(Margo.Cloud.State.Probes.MemoryLearningProbeScript, ``margo-state memory_learning_probe.py``)
on the same program, so memory_learning and memory_consolidation -- the libraries behind
memory_state.py's capabilities, validate-capability, propose-lesson, activate-lesson,
trend-definition, trend, preferences-preview/-import, consolidate and record-consolidation
(a later port) -- are judged byte for byte, private helpers included.

Program (stdin): a JSON list of steps, each one of::

    {"call": NAME, "on": "learning"|"consolidation"|"store"|"ledger"|"products",
     "args": [...], "kwargs": {...}, "save": NAME?, "use": STORE?, "account": ACCOUNT?, "read_only": BOOL?}
    {"open": STORE, "account": ACCOUNT?, "read_only": BOOL?}
    {"close": STORE} | {"begin": STORE} | {"commit": STORE} | {"rollback": STORE}
    {"sql": STATEMENT, "params": [...], "account": ACCOUNT?} | {"script": SQL, "account": ACCOUNT?}
    {"const": NAME}
    {"write": NAME, "text": TEXT | "hex": BYTES | "json": VALUE, "repeat": N?, "mode": "600"?, "save": NAME?}
    {"mkdir": NAME} | {"symlink": NAME, "target": PATH} | {"remove": NAME} | {"chmod": NAME, "mode": "000"}

"on": "learning" calls memory_learning.NAME and "consolidation" memory_consolidation.NAME, passing the
store first to the functions that take one (a fresh MemoryStore per call unless "use" names an open
one); "store" calls a MemoryStore method; "ledger" calls a work_ledger.Ledger method and "products" a
work_productivity.Productivity method on a fresh ledger of the account. Results print as JSON (tuples
as lists, bytes as hex, datetimes as isoformat, paths as strings); errors as "!Type: message". A write
with "save" stores the file's SHA-256 under that name.

Strings are templates: in JSON values, "${EXPR}" alone is replaced by the JSON value of EXPR, and
"${EXPR}" inside a longer string by its text. EXPR is ``now``, ``now+N`` or ``now-N`` with N ending in
s/m/h/d, ``dir``, ``account``, ``state_dir``, or ``NAME.key.0.key`` (a saved result). It writes only
below --state-dir and --dir.
"""

import argparse
import hashlib
import json
import os
import re
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[4]
sys.path.insert(0, str(ROOT / "skills" / "chief-of-staff" / "scripts"))

import margo_store  # noqa: E402
import memory_consolidation  # noqa: E402
import memory_learning  # noqa: E402
import memory_store  # noqa: E402
import work_ledger  # noqa: E402
import work_productivity  # noqa: E402

WHOLE = re.compile(r"^\$\{([^}]+)\}$")
PART = re.compile(r"\$\{([^}]+)\}")
CLOCK = re.compile(r"^now(?:([+-])([0-9]+)([smhd]))?$")
UNITS = {"s": "seconds", "m": "minutes", "h": "hours", "d": "days"}
WITH_MEMORY = {
    "learning": {"_environment", "_prior", "_normalise_proposal", "capabilities", "_observation", "validation",
                 "_feedback", "lesson_plan", "activate_lesson", "configure_trend", "trend", "preferences_plan",
                 "import_preferences"},
    "consolidation": {"_stored", "_sources", "_proposal", "consolidation_plan", "surface_consolidation"},
}
CONSTANTS = ("MAX_OBSERVATIONS", "MAX_FILE_BYTES", "TREND_TYPES", "DEFAULT_TREND", "__doc__")


def convert(value):
    if isinstance(value, (set, frozenset)):
        return sorted(convert(item) for item in value)
    if isinstance(value, (list, tuple)):
        return [convert(item) for item in value]
    if isinstance(value, dict):
        return {key: convert(item) for key, item in value.items()}
    if isinstance(value, (bytes, bytearray)):
        return bytes(value).hex()
    if isinstance(value, datetime):
        return value.isoformat()
    if isinstance(value, Path):
        return str(value)
    return value


def error(exc):
    return "!%s: %s" % (type(exc).__name__, exc)


class Probe:
    def __init__(self, account, state_dir, directory):
        self.account, self.state_dir, self.directory = account, state_dir, directory
        self.saved = {}
        self.stores = {}

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
        if expr == "state_dir":
            return self.state_dir
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
        def text(match):
            found = self.value(match.group(1))
            return found if isinstance(found, str) else json.dumps(found, ensure_ascii=False)

        return PART.sub(text, value)

    def path(self, name):
        return Path(self.directory) / self.text(name)

    def store(self, step):
        return memory_store.MemoryStore(account=self.text(step.get("account", self.account)),
                                        state_root=self.state_dir, read_only=step.get("read_only", False))

    def call(self, index, step):
        on = step["on"]
        name = step["call"]
        args = self.template(step.get("args", []))
        kwargs = self.template(step.get("kwargs", {}))
        memory = ledger = None
        owned = False
        try:
            if on in ("ledger", "products"):
                ledger = work_ledger.Ledger(account=self.text(step.get("account", self.account)), state_root=self.state_dir)
                target = ledger if on == "ledger" else work_productivity.Productivity(ledger)
                result = getattr(target, name)(*args, **kwargs)
            else:
                module = {"learning": memory_learning, "consolidation": memory_consolidation}.get(on)
                if on == "store" or name in WITH_MEMORY.get(on, ()):
                    if "use" in step:
                        memory = self.stores[step["use"]]
                    else:
                        memory, owned = self.store(step), True
                if on == "store":
                    result = getattr(memory, name)(*args, **kwargs)
                elif memory is not None:
                    result = getattr(module, name)(memory, *args, **kwargs)
                else:
                    result = getattr(module, name)(*args, **kwargs)
            result = convert(result)
            line = json.dumps(result, ensure_ascii=False)
            if step.get("save"):
                self.saved[step["save"]] = result
        except Exception as exc:  # the twin reports the same Python class and message
            line = error(exc)
        finally:
            if owned:
                memory.close()
            if ledger is not None:
                ledger.close()
        print("## %d call %s.%s" % (index, on, name))
        print(line)

    def hold(self, index, step):
        for verb in ("open", "close", "begin", "commit", "rollback"):
            if verb in step:
                name = step[verb]
                break
        try:
            if verb == "open":
                self.stores[name] = self.store(step)
            elif verb == "close":
                self.stores.pop(name).close()
            elif verb == "begin":
                self.stores[name].conn.execute("BEGIN IMMEDIATE")
            elif verb == "commit":
                self.stores[name].conn.commit()
            else:
                self.stores[name].conn.rollback()
            line = "ok"
        except Exception as exc:
            line = error(exc)
        print("## %d %s %s" % (index, verb, name))
        print(line)

    def sql(self, index, step):
        account = self.text(step.get("account", self.account))
        connection = None
        try:
            connection = margo_store.connect(account, self.state_dir)
            if "script" in step:
                connection.executescript(self.text(step["script"]))
                line = "ok"
            else:
                cursor = connection.execute(self.text(step["sql"]), self.template(step.get("params", [])))
                rows = [list(row) for row in cursor.fetchall()]
                connection.commit()
                line = json.dumps({"rowcount": cursor.rowcount, "rows": rows}, ensure_ascii=False)
        except Exception as exc:
            line = error(exc)
        finally:
            if connection is not None:
                connection.close()
        print("## %d %s" % (index, "script" if "script" in step else "sql"))
        print(line)

    def const(self, index, step):
        name = step["const"]
        if name not in CONSTANTS:
            raise SystemExit("unknown constant " + name)
        print("## %d const %s" % (index, name))
        print(json.dumps(convert(getattr(memory_learning, name)), ensure_ascii=False))

    def files(self, index, step):
        try:
            if "write" in step:
                path = self.path(step["write"])
                if "json" in step:
                    path.write_text(json.dumps(self.template(step["json"]), ensure_ascii=False), encoding="utf-8")
                elif "hex" in step:
                    path.write_bytes(bytes.fromhex(step["hex"]))
                else:
                    path.write_text(self.text(step["text"]) * step.get("repeat", 1), encoding="utf-8")
                if "mode" in step:
                    os.chmod(path, int(step["mode"], 8))
                label = "write " + step["write"]
                digest = hashlib.sha256(path.read_bytes()).hexdigest()
                if step.get("save"):
                    self.saved[step["save"]] = digest
                line = "sha256 " + digest
            elif "mkdir" in step:
                self.path(step["mkdir"]).mkdir(mode=0o700, parents=True)
                label, line = "mkdir " + step["mkdir"], "ok"
            elif "chmod" in step:
                os.chmod(self.path(step["chmod"]), int(step["mode"], 8))
                label, line = "chmod " + step["chmod"], "ok"
            elif "symlink" in step:
                self.path(step["symlink"]).symlink_to(self.text(step["target"]))
                label, line = "symlink " + step["symlink"], "ok"
            else:
                self.path(step["remove"]).unlink()
                label, line = "remove " + step["remove"], "ok"
        except OSError as exc:
            label, line = "file", error(exc)
        print("## %d %s" % (index, label))
        print(line)

    def run(self, program):
        for index, step in enumerate(program):
            if "call" in step:
                self.call(index, step)
            elif any(verb in step for verb in ("open", "close", "begin", "commit", "rollback")):
                self.hold(index, step)
            elif "sql" in step or "script" in step:
                self.sql(index, step)
            elif "const" in step:
                self.const(index, step)
            elif any(verb in step for verb in ("write", "mkdir", "chmod", "symlink", "remove")):
                self.files(index, step)
            else:
                raise SystemExit("unknown step %d" % index)
        for memory in self.stores.values():
            memory.close()


def main():
    parser = argparse.ArgumentParser(prog="memory_learning_probe.py")
    parser.add_argument("--account", required=True)
    parser.add_argument("--state-dir", required=True)
    parser.add_argument("--dir", required=True)
    args = parser.parse_args()
    program = json.loads(sys.stdin.read())
    if not isinstance(program, list):
        raise SystemExit("stdin must be a JSON list of steps")
    Path(args.dir).mkdir(mode=0o700, exist_ok=True)
    Probe(args.account, args.state_dir, args.dir).run(program)
    return 0


if __name__ == "__main__":
    sys.exit(main())

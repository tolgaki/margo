#!/usr/bin/env python3
"""Contract probe for memory_state.py and memory_dream.py (reference implementation).

Not a Margo command. tools/state_parity.py runs this file and its C# twin
(Margo.Cloud.State.Probes.MemoryCommandsProbeScript, ``margo-state
memory_commands_probe.py``) on the same program, so what separate
``memory_state.py`` processes cannot reach is judged byte for byte:

* flows whose inputs depend on earlier random IDs and tokens (Dream claims,
  attempts and tokens, policy and human-evidence subjects): each step runs
  ``memory_state.main(argv)`` in process, with arguments, stdin and option
  files templated from the clock and from earlier results;
* what Python's tests patch: ``task_runs.clock`` (expired claims) and
  ``memory_dream.MAX_SCAN`` (truncated scans and cursors);
* library calls the CLI does not offer (``memory_dream`` functions, store and
  governance calls, task cancellation, publication publish/review) on a fresh
  store, optionally under another account or state root;
* ``memory_dream._window`` and ``zoneinfo``: daily windows, utcoffset with
  fold, fromutc, and a digest sweep over every transition of the given zones.

Program (stdin): a JSON list of steps, each one of::

    {"cli": [ARGS...], "files": {"--opt": VALUE}?, "stdin": TEXT?, "stdin_json": VALUE?, "save": NAME?}
    {"call": NAME, "on": "dream"|"store"|"governance"|"tasks"|"proactive", "args": [...],
     "account": TEMPLATE?, "state_dir": TEMPLATE?, "save": NAME?}
    {"clock": TEMPLATE|null, "offset": "+Ns"?}
    {"max_scan": N|null}
    {"sql": STATEMENT, "params": [...]}
    {"write": NAME, "json": VALUE | "text": TEXT | "hex": BYTES, "save_sha256": NAME?}
    {"hold": true|false}
    {"window": VALUE}
    {"zone": KEY, "local": [[ISO, FOLD], ...], "utc": [ISO, ...]}
    {"zone_sweep": [KEY, ...], "years": N}

"files" writes each value as JSON to a file in --dir and appends the option and
its path; "stdin_json" is ``json.dumps(VALUE)`` (ASCII, as the Python tests
send it). Results print as canonical JSON (datetimes as isoformat()), errors as
"!Type: message". A hold step keeps a writer's transaction open on another connection
(BEGIN IMMEDIATE plus a pending memory_meta row) until a release step rolls it
back. Strings are templates as in tasks_probe.py: "${EXPR}" with
EXPR ``now``, ``now+N``/``now-N`` (N ending in s/m/h/d), ``day-N`` (the UTC
date N days ago), ``dir``, ``state_dir``, ``account``, or ``NAME.key.0``.
It writes only below --state-dir and --dir.
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
import zoneinfo
import zoneinfo._zoneinfo as pure_zoneinfo

ROOT = Path(__file__).resolve().parents[4]
sys.path.insert(0, str(ROOT / "skills" / "chief-of-staff" / "scripts"))

import margo_store  # noqa: E402
import memory_dream as dream  # noqa: E402
import memory_governance as governance  # noqa: E402
import memory_state  # noqa: E402
import proactive_state  # noqa: E402
import task_runs  # noqa: E402
from memory_store import MemoryStore  # noqa: E402

WHOLE = re.compile(r"^\$\{([^}]+)\}$")
PART = re.compile(r"\$\{([^}]+)\}")
CLOCK = re.compile(r"^now(?:([+-])([0-9]+)([smhd]))?$")
DAY = re.compile(r"^day-([0-9]+)$")
OFFSET = re.compile(r"^([+-])([0-9]+)([smhd])$")
UNITS = {"s": "seconds", "m": "minutes", "h": "hours", "d": "days"}
REAL_CLOCK = task_runs.clock
MAX_SCAN = dream.MAX_SCAN
EPOCH = datetime(1970, 1, 1)
LOW = int((datetime(1, 1, 2) - EPOCH).total_seconds())
HIGH = int((datetime(9999, 12, 30) - EPOCH).total_seconds())


def delta(sign, count, unit):
    value = timedelta(**{UNITS[unit]: int(count)})
    return value if sign == "+" else -value


def plain(value):
    if isinstance(value, datetime):
        return value.isoformat()
    if isinstance(value, tuple):
        return [plain(item) for item in value]
    return value


class Probe:
    def __init__(self, account, state_dir, directory):
        self.account, self.state_dir, self.directory = account, state_dir, directory
        self.saved = {}
        self.held = None

    def value(self, expr):
        clock = CLOCK.match(expr)
        if clock:
            moment = datetime.now(timezone.utc)
            if clock.group(1):
                moment += delta(clock.group(1), clock.group(2), clock.group(3))
            return moment.isoformat()
        day = DAY.match(expr)
        if day:
            return (datetime.now(timezone.utc) - timedelta(days=int(day.group(1)))).date().isoformat()
        if expr == "dir":
            return self.directory
        if expr == "state_dir":
            return self.state_dir
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
        def text(match):
            found = self.value(match.group(1))
            return found if isinstance(found, str) else json.dumps(found, ensure_ascii=False)

        return PART.sub(text, value)

    def cli(self, index, step):
        args = [self.text(arg) for arg in step["cli"]]
        for option, value in step.get("files", {}).items():
            path = Path(self.directory) / ("%s-%d.json" % (option.strip("-"), index))
            path.write_text(json.dumps(self.template(value), ensure_ascii=False), encoding="utf-8")
            args += [option, str(path)]
        argv = ["--account", self.account, "--state-dir", self.state_dir] + args
        if "stdin_json" in step:
            stdin = json.dumps(self.template(step["stdin_json"]))
        else:
            stdin = self.text(step.get("stdin", ""))
        out, err = io.StringIO(), io.StringIO()
        old_stdin, sys.stdin = sys.stdin, io.StringIO(stdin)
        try:
            with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
                try:
                    code = memory_state.main(argv)
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

    def invoke(self, on, name, args, memory, tasks):
        if on == "dream":
            if name == "scope_for":
                return dream.scope_for(*args)
            if name == "capability":
                return dream.capability(*args)
            if name == "bounded":
                return dream.bounded(*args)
            if name == "_current":
                return dream._current(memory, *args)
            return getattr(dream, name)(memory, *args)
        if on == "store":
            return getattr(memory, name)(*args)
        if on == "governance":
            return getattr(governance, name)(memory, *args)
        if on == "proactive":
            return getattr(proactive_state, name)(memory.conn, *args)
        if on == "tasks":
            return "opened" if name == "open" else getattr(tasks, name)(*args)
        raise SystemExit("unknown target " + on)

    def call(self, index, step):
        on, name = step["on"], step["call"]
        args = self.template(step.get("args", []))
        account = self.text(step.get("account", self.account))
        state_dir = self.text(step.get("state_dir", self.state_dir))
        memory = tasks = None
        try:
            if on == "tasks":
                tasks = task_runs.TaskStore(account, state_dir)
            else:
                memory = MemoryStore(account, state_dir)
            result = plain(self.invoke(on, name, args, memory, tasks))
            line = margo_store.canonical_json(result)
            if step.get("save"):
                self.saved[step["save"]] = result
        except Exception as exc:
            line = "!%s: %s" % (type(exc).__name__, exc)
        finally:
            if memory is not None:
                memory.close()
            if tasks is not None:
                tasks.close()
        print("## %d call %s.%s" % (index, on, name))
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

    def max_scan(self, index, step):
        dream.MAX_SCAN = MAX_SCAN if step["max_scan"] is None else step["max_scan"]
        print("## %d max_scan %s" % (index, dream.MAX_SCAN))

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
            path.write_bytes(self.text(step["text"]).encode("utf-8"))
        if step.get("save_sha256"):
            self.saved[step["save_sha256"]] = hashlib.sha256(path.read_bytes()).hexdigest()
        print("## %d write %s" % (index, step["write"]))

    def hold(self, index, step):
        if step["hold"]:
            self.held = MemoryStore(self.account, self.state_dir)
            self.held.conn.execute("BEGIN IMMEDIATE")
            self.held.conn.execute("INSERT INTO memory_meta VALUES('synthetic-write','pending')")
            print("## %d hold" % index)
        else:
            self.held.conn.rollback()
            self.held.close()
            self.held = None
            print("## %d release" % index)

    def window(self, index, step):
        try:
            start, end = dream._window(self.template(step["window"]))
            line = margo_store.canonical_json([start.isoformat(), end.isoformat()])
        except Exception as exc:
            line = "!%s: %s" % (type(exc).__name__, exc)
        print("## %d window" % index)
        print(line)

    def zone(self, index, step):
        print("## %d zone" % index)
        try:
            zone = zoneinfo.ZoneInfo(step["zone"])
        except Exception as exc:
            print("!%s: %s" % (type(exc).__name__, exc))
            return
        for value, fold in step.get("local", []):
            try:
                offset = datetime.fromisoformat(value).replace(tzinfo=zone, fold=fold).utcoffset()
                line = "%s fold=%d %d" % (value, fold, int(offset.total_seconds()))
            except Exception as exc:
                line = "%s fold=%d !%s: %s" % (value, fold, type(exc).__name__, exc)
            print(line)
        for value in step.get("utc", []):
            try:
                line = "%s -> %s" % (value, datetime.fromisoformat(value).astimezone(zone).isoformat())
            except Exception as exc:
                line = "%s -> !%s: %s" % (value, type(exc).__name__, exc)
            print(line)

    def zone_sweep(self, index, step):
        print("## %d zone_sweep" % index)
        years = step.get("years", 30)
        for key in step["zone_sweep"]:
            try:
                zone = zoneinfo.ZoneInfo(key)
                reference = pure_zoneinfo.ZoneInfo.no_cache(key)
            except Exception as exc:
                print("%s !%s: %s" % (key, type(exc).__name__, exc))
                continue
            values = []

            def local(seconds):
                if LOW <= seconds <= HIGH:
                    moment = EPOCH + timedelta(seconds=seconds)
                    for fold in (0, 1):
                        values.append(int(moment.replace(tzinfo=zone, fold=fold).utcoffset().total_seconds()))

            def utc(seconds):
                if LOW <= seconds <= HIGH:
                    instant = EPOCH + timedelta(seconds=seconds)
                    moment = instant.replace(tzinfo=timezone.utc).astimezone(zone)
                    offset = int((moment.replace(tzinfo=None) - instant).total_seconds())
                    values.append(offset)
                    return offset
                return None

            for transition in reference._trans_utc:
                for moment in (transition - 1, transition, transition + 1):
                    offset = utc(moment)
                    if offset is not None:
                        for wall in (moment + offset - 1, moment + offset, moment + offset + 1):
                            local(wall)
            rule = reference._tz_after
            if isinstance(rule, pure_zoneinfo._TZStr):
                last = (EPOCH + timedelta(seconds=reference._trans_utc[-1])).year if reference._trans_utc else 1970
                for year in list(range(last, last + years)) + [9999]:
                    start, end = rule.transitions(year)
                    diff = rule.dst_diff
                    for base in (start, end):
                        for wall in (base - diff - 1, base - diff, base - 1, base, base + diff - 1, base + diff):
                            local(wall)
                    for base in (start - int(rule.std.utcoff.total_seconds()), end - int(rule.dst.utcoff.total_seconds())):
                        for moment in (base - 1, base, base + 1):
                            utc(moment)
            text = "\n".join(str(value) for value in values)
            print("%s %d %s" % (key, len(values), hashlib.sha256(text.encode("ascii")).hexdigest()[:16]))

    def run(self, program):
        for index, step in enumerate(program):
            if "cli" in step:
                self.cli(index, step)
            elif "call" in step:
                self.call(index, step)
            elif "clock" in step:
                self.clock(index, step)
            elif "max_scan" in step:
                self.max_scan(index, step)
            elif "sql" in step:
                self.sql(index, step)
            elif "write" in step:
                self.write(index, step)
            elif "hold" in step:
                self.hold(index, step)
            elif "window" in step:
                self.window(index, step)
            elif "zone_sweep" in step:
                self.zone_sweep(index, step)
            elif "zone" in step:
                self.zone(index, step)
            else:
                raise SystemExit("unknown step %d" % index)


def main():
    parser = argparse.ArgumentParser(prog="memory_commands_probe.py")
    parser.add_argument("--account", required=True)
    parser.add_argument("--state-dir", required=True)
    parser.add_argument("--dir", required=True)
    args = parser.parse_args()
    program = json.loads(sys.stdin.read())
    if not isinstance(program, list):
        raise SystemExit("stdin must be a JSON list of steps")
    # argparse's prog in the memory_state.py help and usage texts comes from sys.argv[0].
    sys.argv = ["memory_state.py"]
    Path(args.dir).mkdir(mode=0o700, exist_ok=True)
    Probe(args.account, args.state_dir, args.dir).run(program)
    return 0


if __name__ == "__main__":
    sys.exit(main())

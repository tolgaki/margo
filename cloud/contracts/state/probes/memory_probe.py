#!/usr/bin/env python3
"""Contract probe for the memory store, governance and encoder status (reference implementation).

Not a Margo command. tools/state_parity.py runs this file and its C# twin
(Margo.Cloud.State.Probes.MemoryProbeScript, ``margo-state memory_probe.py``) on
the same program, so the libraries the memory_state.py CLI (a later port) and the
memory search/learning/dream modules call -- memory_store.MemoryStore,
memory_governance, memory_encoder's status functions without a model or runtime,
and memory_dream's source helpers -- are judged byte for byte.

Program (stdin): a JSON list of steps, each one of::

    {"call": NAME, "on": "store"|"governance"|"module"|"dream"|"encoder", "args": [...], "kwargs": {...},
     "save": NAME?, "use": STORE?, "account": ACCOUNT?, "state_dir": PATH?, "read_only": BOOL?, "migrate": BOOL?}
    {"open": STORE, "account": ACCOUNT?, "state_dir": PATH?, "read_only": BOOL?, "migrate": BOOL?}
    {"close": STORE} | {"begin": STORE} | {"commit": STORE} | {"rollback": STORE}
    {"sql": STATEMENT, "params": [...], "account": ACCOUNT?}
    {"script": SQL, "account": ACCOUNT?}            ("@SCHEMA_V1"/"@SCHEMA" name memory_store's schema)
    {"const": NAME, "on": "store"|"encoder"}
    {"write": NAME, "text": TEXT | "hex": BYTES | "json": VALUE, "repeat": N?, "mode": "600"?}
    {"mkdir": NAME, "mode": "700"} | {"chmod": NAME, "mode": "600"} | {"symlink": NAME, "target": PATH}
    {"read": NAME}

"on": "store" calls a MemoryStore method (a fresh store per call unless "use" names an
open one; "__init__" only opens it); "governance" calls memory_governance.NAME(memory, *args)
(digest and normalise_policy take no store);
"module" calls memory_store.NAME; "dream" calls memory_dream.validate_source(memory, ...) or
source_keys(...); "encoder" calls memory_encoder.NAME. Results print as JSON (sets sorted,
tuples as lists, rows as dicts, paths as strings); errors as "!Type: message".

Strings are templates: in JSON values, "${EXPR}" alone is replaced by the JSON value of
EXPR, and "${EXPR}" inside a longer string by its text. EXPR is ``now``, ``now+N`` or
``now-N`` with N ending in s/m/h/d, ``dir``, ``account``, ``state_dir``, or
``NAME.key.0.key`` (a saved result). It writes only below --state-dir and --dir.
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
import memory_dream  # noqa: E402
import memory_encoder  # noqa: E402
import memory_governance  # noqa: E402
import memory_store  # noqa: E402

WHOLE = re.compile(r"^\$\{([^}]+)\}$")
PART = re.compile(r"\$\{([^}]+)\}")
CLOCK = re.compile(r"^now(?:([+-])([0-9]+)([smhd]))?$")
UNITS = {"s": "seconds", "m": "minutes", "h": "hours", "d": "days"}
TEMPORARY = re.compile(r"\.memory-export-[a-z0-9_]{8}")
PATH_ARGUMENTS = {"_file_digest": 0, "_read_manifest": 0, "_verify_asset": 0}
STATELESS_GOVERNANCE = {"digest", "normalise_policy"}


def convert(value):
    if isinstance(value, (set, frozenset)):
        return sorted(convert(item) for item in value)
    if isinstance(value, (list, tuple)):
        return [convert(item) for item in value]
    if isinstance(value, dict):
        return {key: convert(item) for key, item in value.items()}
    if hasattr(value, "keys") and hasattr(value, "__getitem__") and not isinstance(value, str):
        return {key: value[key] for key in value.keys()}
    if isinstance(value, Path):
        return str(value)
    return value


def error(exc):
    return "!%s: %s" % (type(exc).__name__, TEMPORARY.sub(".memory-export-XXXXXXXX", str(exc)))


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

    def options(self, step):
        return (self.text(step.get("account", self.account)), self.text(step.get("state_dir", self.state_dir)),
                step.get("migrate", False), step.get("read_only", False))

    def store(self, step):
        account, state_dir, migrate, read_only = self.options(step)
        return memory_store.MemoryStore(account=account, state_root=state_dir, migrate=migrate, read_only=read_only)

    def call(self, index, step):
        on = step.get("on", "store")
        name = step["call"]
        args = self.template(step.get("args", []))
        kwargs = self.template(step.get("kwargs", {}))
        if name in PATH_ARGUMENTS and args:
            args[PATH_ARGUMENTS[name]] = Path(args[PATH_ARGUMENTS[name]])
        if name == "_verify_asset":
            args[1] = memory_encoder.ASSETS[args[1]]
        if name == "_digest_bytes":
            args[0] = bytes.fromhex(args[0])
        if name == "_work_sources_current" and len(args) > 1:
            args[1] = set(args[1])
        memory = None
        owned = False
        try:
            if on == "encoder":
                result = getattr(memory_encoder, name)(*args, **kwargs)
            elif on == "module":
                result = getattr(memory_store, name)(*args, **kwargs)
            elif on == "dream" and name == "source_keys":
                result = memory_dream.source_keys(*args, **kwargs)
            elif on == "governance" and name in STATELESS_GOVERNANCE:
                result = getattr(memory_governance, name)(*args, **kwargs)
            else:
                if "use" in step:
                    memory = self.stores[step["use"]]
                else:
                    memory, owned = self.store(step), True
                if name == "__init__":
                    result = "opened"
                elif on == "governance":
                    result = getattr(memory_governance, name)(memory, *args, **kwargs)
                elif on == "dream":
                    result = getattr(memory_dream, name)(memory, *args, **kwargs)
                else:
                    result = getattr(memory, name)(*args, **kwargs)
            result = convert(result)
            line = json.dumps(result, ensure_ascii=False)
            if step.get("save"):
                self.saved[step["save"]] = result
        except Exception as exc:  # the twin reports the same Python class and message
            line = error(exc)
        finally:
            if owned:
                memory.close()
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
                script = step["script"]
                script = {"@SCHEMA_V1": memory_store.SCHEMA_V1, "@SCHEMA": memory_store.SCHEMA}.get(script, script)
                connection.executescript(self.text(script))
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
        module = memory_encoder if step.get("on") == "encoder" else memory_store
        value = getattr(module, step["const"])
        if isinstance(value, dict):
            value = {key: convert(item) for key, item in value.items()}
        print("## %d const %s" % (index, step["const"]))
        print(json.dumps(convert(value), ensure_ascii=False))

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
                line = "sha256 " + hashlib.sha256(path.read_bytes()).hexdigest()
            elif "mkdir" in step:
                path = self.path(step["mkdir"])
                path.mkdir(mode=0o700)
                os.chmod(path, int(step.get("mode", "700"), 8))
                label, line = "mkdir " + step["mkdir"], "ok"
            elif "chmod" in step:
                os.chmod(self.path(step["chmod"]), int(step["mode"], 8))
                label, line = "chmod " + step["chmod"], "ok"
            elif "symlink" in step:
                self.path(step["symlink"]).symlink_to(self.text(step["target"]))
                label, line = "symlink " + step["symlink"], "ok"
            else:
                label = "read " + step["read"]
                line = json.dumps(self.path(step["read"]).read_text(encoding="utf-8"), ensure_ascii=False)
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
            elif any(verb in step for verb in ("write", "mkdir", "chmod", "symlink", "read")):
                self.files(index, step)
            else:
                raise SystemExit("unknown step %d" % index)
        for memory in self.stores.values():
            memory.close()


def main():
    parser = argparse.ArgumentParser(prog="memory_probe.py")
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

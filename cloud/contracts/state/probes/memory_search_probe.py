#!/usr/bin/env python3
"""Contract probe for memory search and connected context (reference implementation).

Not a Margo command. tools/state_parity.py runs this file and its C# twin
(Margo.Cloud.State.Probes.MemorySearchProbeScript, ``margo-state memory_search_probe.py``)
on the same program, so the libraries behind ``memory_state.py`` search/context/graph/
explain/index/status -- memory_search.MemorySearch and memory_context -- are judged
byte for byte, including the semantic paths with a deterministic fake encoder
(Margo Cloud has no embedding runtime; without "encoder" the real local runtime
is used and fails as it does in the cloud).

Program (stdin): a JSON list of steps, each one of::

    {"call": NAME, "on": "search"|"context"|"store"|"module", "args": [...], "kwargs": {...},
     "save": NAME?, "use": SEARCH?, "account": ACCOUNT?, "read_only": BOOL?, "rebuild_schema": BOOL?,
     "encoder": ENCODER?, "filters": {...}?}
    {"open": SEARCH, "account": ACCOUNT?, "read_only": BOOL?, "rebuild_schema": BOOL?, "encoder": ENCODER?}
    {"close": SEARCH} | {"begin": SEARCH} | {"rollback": SEARCH} | {"encoder_of": SEARCH, "encoder": ENCODER}
    {"sql": STATEMENT, "params": [...], "account": ACCOUNT?, "save": NAME?}

"on": "search" calls a MemorySearch method ("state" reports initialized/fts/read_only; "__init__"
only constructs); "context" builds memory_context.ConnectedContext(memory, **filters) and calls a
method, printing the result with the context's current IDs, gaps, edges, conflicted IDs and
truncation; "store" calls a MemoryStore method; "module" calls a function of memory_search or
memory_context (bytes are hex). Without "use", each call opens a fresh store (and search).
Results print as JSON (sets sorted, tuples as lists); errors as "!Type: message".

ENCODER is {"fingerprint": TEXT?, "dimensions": N?, "groups": [[WORD, ...], ...], "weights": [NUMBER, ...]?,
"default": VECTOR?, "result": VALUE?, "results": [VALUE, ...]?, "raise": TEXT?, "before": [STEP, ...]?,
"log": BOOL?}: per text, component i is (number of group-i words in text.casefold()) * weight i, or
"default" when every component is zero; "result"/"results" return fixed values, "raise" raises
StateError, "before" runs steps (templated when they run) on the first call, "log" prints every batch of texts.

Strings are templates: "${EXPR}" alone is replaced by the JSON value of EXPR, inside a longer
string by its text. EXPR is ``now``, ``now+N``/``now-N`` (N ending in s/m/h/d), ``account``,
``state_dir``, ``id:DOMAIN:KEY`` (MemoryStore.record_id), ``json:EXPR`` (json.loads of a
saved string) or ``NAME.key.0`` (a saved result).
It writes only below --state-dir.
"""

import argparse
import json
import re
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[4]
sys.path.insert(0, str(ROOT / "skills" / "chief-of-staff" / "scripts"))

import margo_store  # noqa: E402
import memory_context  # noqa: E402
import memory_search  # noqa: E402
import memory_store  # noqa: E402

WHOLE = re.compile(r"^\$\{([^}]+)\}$")
PART = re.compile(r"\$\{([^}]+)\}")
CLOCK = re.compile(r"^now(?:([+-])([0-9]+)([smhd]))?$")
UNITS = {"s": "seconds", "m": "minutes", "h": "hours", "d": "days"}
HOLD = ("open", "close", "begin", "rollback", "encoder_of")


def convert(value):
    if isinstance(value, (set, frozenset)):
        return sorted(convert(item) for item in value)
    if isinstance(value, (list, tuple)):
        return [convert(item) for item in value]
    if isinstance(value, dict):
        return {key: convert(item) for key, item in value.items()}
    if isinstance(value, (bytes, bytearray)):
        return value.hex()
    if hasattr(value, "keys") and hasattr(value, "__getitem__") and not isinstance(value, str):
        return {key: convert(value[key]) for key in value.keys()}
    return value


def error(exc):
    return "!%s: %s" % (type(exc).__name__, exc)


class Probe:
    def __init__(self, account, state_dir):
        self.account, self.state_dir = account, state_dir
        self.saved = {}
        self.searches = {}

    def value(self, expr):
        clock = CLOCK.match(expr)
        if clock:
            moment = datetime.now(timezone.utc)
            if clock.group(1):
                delta = timedelta(**{UNITS[clock.group(3)]: int(clock.group(2))})
                moment = moment + delta if clock.group(1) == "+" else moment - delta
            return moment.isoformat()
        if expr == "account":
            return self.account
        if expr == "state_dir":
            return self.state_dir
        if expr.startswith("json:"):
            return json.loads(self.value(expr[len("json:"):]))
        if expr.startswith("id:"):
            _, domain, key = expr.split(":", 2)
            return memory_store.memory_identity(self.account, domain, key)[0]
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

    # -------------------------------------------------------------------- the fake encoder

    def encoder(self, spec):
        if spec is None:
            return None
        hooks = spec.get("before")
        spec = self.template({key: value for key, value in spec.items() if key != "before"})
        calls = []

        def encode(texts):
            index = len(calls)
            calls.append(list(texts))
            if spec.get("log"):
                print("## encoder call %d" % index)
                print(json.dumps(list(texts), ensure_ascii=False))
            if index == 0 and hooks:
                for step in hooks:
                    self.step("hook", step)
            if "raise" in spec:
                raise margo_store.StateError(spec["raise"])
            if "results" in spec:
                return spec["results"][min(index, len(spec["results"]) - 1)]
            if "result" in spec:
                return spec["result"]
            groups = spec["groups"]
            weights = spec.get("weights", [1] * len(groups))
            vectors = []
            for text in texts:
                folded = text.casefold()
                vector = [sum(word in folded for word in group) * weight for group, weight in zip(groups, weights)]
                if not any(vector):
                    vector = list(spec.get("default", vector))
                vectors.append(vector)
            return {"model_fingerprint": spec.get("fingerprint", "probe-fake-model"),
                    "dimensions": spec.get("dimensions", len(groups)), "vectors": vectors}

        return encode

    # -------------------------------------------------------------------- objects

    def open_search(self, step):
        account = self.text(step.get("account", self.account))
        memory = memory_store.MemoryStore(account=account, state_root=self.state_dir,
                                          read_only=step.get("read_only", False))
        try:
            return memory_search.MemorySearch(memory, encoder=self.encoder(step.get("encoder")),
                                              rebuild_schema=step.get("rebuild_schema", False))
        except Exception:
            memory.close()
            raise

    def call(self, label, step):
        on = step.get("on", "search")
        name = step["call"]
        args = self.template(step.get("args", []))
        kwargs = self.template(step.get("kwargs", {}))
        search = None
        owned = False
        state = None
        try:
            if on == "module":
                if name in ("decoded_vector",):
                    args[0] = bytes.fromhex(args[0])
                module = memory_search if hasattr(memory_search, name) and name in (
                    "representation", "text_hash", "vector_bytes", "decoded_vector") else memory_context
                result = getattr(module, name)(*args, **kwargs)
            else:
                if "use" in step:
                    search = self.searches[step["use"]]
                    if "encoder" in step:
                        search.encoder = self.encoder(step["encoder"])
                elif on == "store":
                    account = self.text(step.get("account", self.account))
                    search = memory_store.MemoryStore(account=account, state_root=self.state_dir,
                                                      read_only=step.get("read_only", False))
                    owned = True
                else:
                    search, owned = self.open_search(step), True
                memory = search if on == "store" and owned else search.memory
                if on == "store":
                    if name == "list_ids":
                        result = [row["id"] for row in memory.list(*args, **kwargs)]
                    else:
                        result = getattr(memory, name)(*args, **kwargs)
                elif on == "context":
                    context = memory_context.ConnectedContext(memory, **self.template(step.get("filters", {})))
                    try:
                        result = getattr(context, name)(*args, **kwargs)
                    finally:
                        state = {"current": list(context.current), "gaps": context.gaps, "edges": context.edges,
                                 "conflicted_ids": sorted(context.conflicted_ids), "truncated": context.truncated}
                elif name == "__init__":
                    result = "opened"
                elif name == "state":
                    result = {"initialized": search.initialized, "fts": search.fts, "read_only": search.read_only}
                else:
                    result = getattr(search, name)(*args, **kwargs)
            result = convert(result)
            line = json.dumps(result, ensure_ascii=False)
            if step.get("save"):
                self.saved[step["save"]] = result
        except Exception as exc:  # the twin reports the same Python class and message
            line = error(exc)
        finally:
            if owned:
                (search if on == "store" else search.memory).close()  # a store call owns a bare store
        print("## %s call %s.%s" % (label, on, name))
        print(line)
        if state is not None:
            print(json.dumps(convert(state), ensure_ascii=False))

    def hold(self, label, step):
        verb = next(verb for verb in HOLD if verb in step)
        name = step[verb]
        try:
            if verb == "open":
                self.searches[name] = self.open_search(step)
            elif verb == "close":
                self.searches.pop(name).memory.close()
            elif verb == "begin":
                self.searches[name].conn.execute("BEGIN")
            elif verb == "rollback":
                search = self.searches[name]
                line = "in_transaction=%s" % search.conn.in_transaction
                search.conn.rollback()
            else:
                self.searches[name].encoder = self.encoder(step.get("encoder"))
            if verb != "rollback":
                line = "ok"
        except Exception as exc:
            line = error(exc)
        print("## %s %s %s" % (label, verb, name))
        print(line)

    def sql(self, label, step):
        account = self.text(step.get("account", self.account))
        connection = None
        try:
            connection = margo_store.connect(account, self.state_dir)
            cursor = connection.execute(self.text(step["sql"]), self.template(step.get("params", [])))
            rows = [convert(list(row)) for row in cursor.fetchall()]
            connection.commit()
            result = {"rowcount": cursor.rowcount, "rows": rows}
            line = json.dumps(result, ensure_ascii=False)
            if step.get("save"):
                self.saved[step["save"]] = result
        except Exception as exc:
            line = error(exc)
        finally:
            if connection is not None:
                connection.close()
        print("## %s sql" % label)
        print(line)

    def step(self, label, step):
        if "call" in step:
            self.call(label, step)
        elif any(verb in step for verb in HOLD):
            self.hold(label, step)
        elif "sql" in step:
            self.sql(label, step)
        else:
            raise SystemExit("unknown step %s" % label)

    def run(self, program):
        try:
            for index, step in enumerate(program):
                self.step(str(index), step)
        finally:
            for search in self.searches.values():
                search.memory.close()


def main():
    parser = argparse.ArgumentParser(prog="memory_search_probe.py")
    parser.add_argument("--account", required=True)
    parser.add_argument("--state-dir", required=True)
    args = parser.parse_args()
    program = json.loads(sys.stdin.read())
    if not isinstance(program, list):
        raise SystemExit("stdin must be a JSON list of steps")
    Probe(args.account, args.state_dir).run(program)
    return 0


if __name__ == "__main__":
    sys.exit(main())

#!/usr/bin/env python3
"""Contract probe for the stateless helpers (reference implementation).

Not a Margo command. tools/state_parity.py runs this file and its C# twin
(Margo.Cloud.State.Helpers.HelpersProbeScript, ``margo-state helpers_probe.py``) on the
same inputs, so the library code behind capacity.py, teams_feedback.py and engage_parse.py
-- and the Python semantics their C# ports reproduce (list.sort's comparison sequence,
comparisons and equality across JSON types, repr, int(), slicing, html.unescape,
urllib.parse.unquote, sum() of floats, int/float arithmetic, json.dump's chunked output)
-- is judged byte for byte, not only through the scripts' command lines.

``write``/``write-hex`` create input files for the scripts (relative to the working
directory, which the judge sets to the scenario root). Nothing else touches the disk.
"""

import argparse
import html
import json
import sys
from pathlib import Path
from urllib.parse import unquote

ROOT = Path(__file__).resolve().parents[4]
sys.path.insert(0, str(ROOT / "skills" / "chief-of-staff" / "scripts"))

import capacity  # noqa: E402
import engage_parse  # noqa: E402
import teams_feedback  # noqa: E402


def _error(exc):
    return "!" + type(exc).__name__


def _attempt(function):
    try:
        return function()
    except Exception as exc:  # the class of any failure is part of the contract
        return _error(exc)


def _stdin_json():
    return json.loads(sys.stdin.read())


def _show(value):
    print(json.dumps(value, ensure_ascii=True))


class Traced:
    """A key whose ``<`` is logged, so the exact comparison sequence of list.sort is observable."""

    log = []

    def __init__(self, index, value):
        self.index, self.value = index, value

    def __lt__(self, other):
        Traced.log.append([self.index, other.index])
        return self.value < other.value


def cmd_sort_trace(as_tuples):
    keys = _stdin_json()
    if as_tuples:
        keys = [tuple(key) for key in keys]
    items = [Traced(index, key) for index, key in enumerate(keys)]
    try:
        order = [item.index for item in sorted(items)]
        error = None
    except Exception as exc:
        order, error = None, _error(exc)
    _show({"compares": len(Traced.log), "trace": Traced.log, "order": order, "error": error})


def cmd_ops():
    """Python operations the helpers apply to untrusted JSON values, one result per case."""
    for case in _stdin_json():
        value = case
        _show({
            "truthy": bool(value),
            "str": _attempt(lambda: str(value)),
            "len": _attempt(lambda: len(value)),
            "slice": _attempt(lambda: value[:3]),
            "iter": _attempt(lambda: list(value)),
            "int": _attempt(lambda: str(int(value))),
            "in": _attempt(lambda: "value" in value),
            "index": _attempt(lambda: value["value"]),
            "get": _attempt(lambda: value.get("a", "default")),
            "hash": _attempt(lambda: hash(value) is not None),
        })


def cmd_compare():
    """``<``, ``>``, ``>=``, ``==``, ``!=``, ``in`` and dict-key equality for pairs of JSON values."""
    for left, right in _stdin_json():
        def key_equal():
            return {left: 1}.get(right, 0) == 1
        _show({
            "lt": _attempt(lambda: left < right),
            "gt": _attempt(lambda: left > right),
            "ge": _attempt(lambda: left >= right),
            "eq": left == right,
            "ne": left != right,
            "in": _attempt(lambda: left in [right]),
            "key": _attempt(key_equal),
            "tuple_lt": _attempt(lambda: (left,) < (right,)),
        })


def cmd_numbers():
    """float(int), int/float comparisons and arithmetic, int true division, sum() of floats."""
    for case in _stdin_json():
        kind = case[0]
        if kind == "float":
            _show(_attempt(lambda: repr(float(case[1]))))
        elif kind == "cmp":
            a, b = case[1], case[2]
            _show([_attempt(lambda: a < b), _attempt(lambda: a <= b), _attempt(lambda: a == b),
                   _attempt(lambda: a >= b)])
        elif kind == "add":
            _show(_attempt(lambda: repr(case[1] + case[2])))
        elif kind == "sub":
            _show(_attempt(lambda: repr(case[1] - case[2])))
        elif kind == "div":
            _show(_attempt(lambda: repr(case[1] / case[2])))
        elif kind == "sum":
            _show(_attempt(lambda: repr(sum(case[1]))))
        elif kind == "max0":
            _show(_attempt(lambda: repr(max(0, case[1]))))


def cmd_capacity():
    """capacity.py library functions on intervals given as [start, end] strings."""
    for case in _stdin_json():
        kind = case[0]

        def pairs(values):
            return [(capacity.instant(start), capacity.instant(end)) for start, end in values]

        def show(intervals):
            return [[start.isoformat(), end.isoformat()] for start, end in intervals]

        if kind == "instant":
            _show(_attempt(lambda: capacity.instant(case[1]).isoformat()))
        elif kind == "intervals":
            _show(_attempt(lambda: show(capacity.intervals(case[1], "label"))))
        elif kind == "merge":
            _show(_attempt(lambda: show(capacity.merge(pairs(case[1])))))
        elif kind == "intersection":
            _show(_attempt(lambda: show(capacity.intersection(pairs(case[1]), pairs(case[2])))))
        elif kind == "subtract":
            _show(_attempt(lambda: show(capacity.subtract(pairs(case[1]), pairs(case[2])))))
        elif kind == "minutes":
            _show(_attempt(lambda: repr(capacity.minutes(pairs(case[1])))))
        elif kind == "number":
            _show(_attempt(lambda: repr(capacity.number(case[1], "label", case[2]))))
        elif kind == "serialise":
            _show(_attempt(lambda: capacity.serialise(pairs(case[1]))))


def cmd_text(kind):
    text = sys.stdin.read()
    if kind == "clean":
        _show(_attempt(lambda: teams_feedback.clean(text)))
    elif kind == "html":
        _show(_attempt(lambda: html.unescape(text)))
    elif kind == "unescape":
        _show(engage_parse.unescape(text))
    elif kind == "salvage":
        _show(_attempt(lambda: engage_parse.salvage(text)))
    elif kind == "unquote":
        _show(unquote(text))


def cmd_values(kind):
    for value in _stdin_json():
        if kind == "clean":
            _show(_attempt(lambda: teams_feedback.clean(value)))
        elif kind == "html":
            _show(_attempt(lambda: html.unescape(value)))
        elif kind == "unquote":
            _show(_attempt(lambda: unquote(value)))
        elif kind == "unescape":
            _show(_attempt(lambda: engage_parse.unescape(value)))
        elif kind == "salvage":
            _show(_attempt(lambda: engage_parse.salvage(value)))
        elif kind == "encoded-key":
            _show(_attempt(lambda: engage_parse._encoded_key(value)))
        elif kind == "daygap":
            _show(engage_parse._daygap(value[0], value[1]))


def cmd_write(path, data):
    target = Path(path)
    target.write_bytes(data)
    target.chmod(0o600)


def cmd_write_nested(path, depth):
    """Write prefix + depth '[' + inner + depth ']' + suffix (stdin: a JSON [prefix, inner, suffix])."""
    prefix, inner, suffix = _stdin_json()
    cmd_write(path, (prefix + "[" * depth + inner + "]" * depth + suffix).encode("utf-8"))


def main():
    parser = argparse.ArgumentParser(description="contract probe for the stateless helpers (not a Margo command)")
    sub = parser.add_subparsers(dest="command", required=True)
    for name in ("ops", "compare", "numbers", "capacity", "dump"):
        sub.add_parser(name)
    command = sub.add_parser("sort-trace")
    command.add_argument("--tuples", action="store_true")
    command = sub.add_parser("text")
    command.add_argument("kind", choices=["clean", "html", "unescape", "salvage", "unquote"])
    command = sub.add_parser("values")
    command.add_argument("kind", choices=["clean", "html", "unquote", "unescape", "salvage", "encoded-key", "daygap"])
    for name in ("write", "write-hex"):
        command = sub.add_parser(name)
        command.add_argument("path")
    command = sub.add_parser("write-nested")
    command.add_argument("path")
    command.add_argument("depth", type=int)
    args = parser.parse_args()
    if args.command == "dump":
        # Called straight from main(), as teams_feedback.py and engage_parse.py call it, so the
        # recursion limit leaves the same container depth.
        json.dump(_stdin_json(), sys.stdout, indent=2)
        return 0
    if args.command == "sort-trace":
        cmd_sort_trace(args.tuples)
    elif args.command in ("text",):
        cmd_text(args.kind)
    elif args.command == "values":
        cmd_values(args.kind)
    elif args.command == "write":
        cmd_write(args.path, sys.stdin.read().encode("utf-8"))
    elif args.command == "write-hex":
        cmd_write(args.path, bytes.fromhex(sys.stdin.read()))
    elif args.command == "write-nested":
        cmd_write_nested(args.path, args.depth)
    else:
        {"ops": cmd_ops, "compare": cmd_compare, "numbers": cmd_numbers, "capacity": cmd_capacity}[args.command]()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

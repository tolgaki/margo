#!/usr/bin/env python3
"""Regenerate PythonFixtures.cs: expected outputs computed by CPython for the State.Core helper tests.

    python3 cloud/tests/Margo.Cloud.State.Tests/Fixtures/generate_python_fixtures.py

The C# tests compare PyJson/PyTime/PyArgs/PyRepr results with these literals. Inputs are fictional.
Run with the CPython version the ports target (3.13) and review the diff before committing.
"""

import argparse
import contextlib
import io
import json
import os
import random
import struct
import sys
import textwrap
from datetime import date, datetime, time, timezone
from pathlib import Path

sys.dont_write_bytecode = True
os.environ["COLUMNS"] = "80"  # argparse help width is part of the expected text
HERE = Path(__file__).resolve().parent
REPO = HERE.parents[3]
sys.path.insert(0, str(REPO / "skills" / "chief-of-staff" / "scripts"))
sys.path.insert(0, str(REPO / "cloud" / "contracts" / "state" / "probes"))

import margo_store  # noqa: E402
import state_core_probe  # noqa: E402


def cs(value):
    """A C# string literal (or null)."""
    if value is None:
        return "null"
    out = ['"']
    for ch in value:
        code = ord(ch)
        if ch == '"':
            out.append('\\"')
        elif ch == "\\":
            out.append("\\\\")
        elif ch == "\n":
            out.append("\\n")
        elif ch == "\r":
            out.append("\\r")
        elif ch == "\t":
            out.append("\\t")
        elif 0x20 <= code < 0x7f:
            out.append(ch)
        elif code > 0xFFFF:
            code -= 0x10000
            out.append("\\u%04x\\u%04x" % (0xD800 + (code >> 10), 0xDC00 + (code & 0x3FF)))
        else:
            out.append("\\u%04x" % code)
    out.append('"')
    return "".join(out)


def err(exc):
    return "!" + type(exc).__name__ + ": " + str(exc)


def dumps_cases():
    values = ["1", "-0", "1.0", "-0.0", "1e-05", "1e16", "1e15", "0.1", "0.30000000000000004", "100.0",
              "123456789012345678901234567890", "5e-324", "1.7976931348623157e308", "true", "null",
              '"plain"', '"\\u00e9\\u20ac\\ud83d\\ude00"', '"\\ud800"', '"\\u0000\\u001f\\b\\f\\n\\r\\t\\u007f\\"\\\\/"',
              '{"b": 1, "a": [1, 2, {"c": null}], "\\u00e9": true, "\\ud83d\\ude00": false, "\\uffff": 0, "B": 2}',
              '{"a": [], "b": {}, "c": [[]], "d": [1, [2, [3]]]}', '{"a": 1, "b": 2, "a": 3}', "NaN", "-Infinity",
              "[1.5, -2, \"x\", null, true]"]
    options = [dict(), dict(indent=2), dict(indent=0), dict(sort_keys=True), dict(ensure_ascii=False),
               dict(separators=(",", ":"), sort_keys=True, ensure_ascii=False, allow_nan=False),
               dict(indent=2, separators=(", ", ": "))]
    rows = []
    for option in options:
        for value in values:
            try:
                expected = json.dumps(json.loads(value), **option)
            except ValueError as exc:
                expected = err(exc)
            separators = option.get("separators")
            rows.append("            { %s, %s, %s, %s, %s, %s, %s, %s }," % (
                cs(value), option.get("indent", "null"), cs(separators[0]) if separators else "null",
                cs(separators[1]) if separators else "null", str(option.get("sort_keys", False)).lower(),
                str(option.get("ensure_ascii", True)).lower(), str(option.get("allow_nan", True)).lower(),
                cs(expected)))
    return rows


def loads_cases():
    texts = ["", " ", "{", "[", "[1,]", '{"a":1,}', '{"a" 1}', '{"a":1 "b":2}', "[1 2]", "{1:2}", '"abc',
             '"a\u0001"', '"\\x"', '"\\u12"', '"\\ud800"', '"\\ud83d\\ude00"', "01", "1.", "-", ".5", "tru",
             "NaN", "-Infinity", "1e400", "\ufeff{}", '{"a":1}x', "[1,2", '{"a":', "1E5", "1e", "-0", "-0.0",
             ' \n {"x": [1, {"y"\n:\n2}]', "[1,,2]", '{"a":1,,"b":2}', "-NaN", '"\U0001f600" x',
             '[" \U0001f600", 1 2]', "9" * 4301, '{"a":1,"a":2,"b":3,"a":4}', "123456789012345678901234567890"]
    rows = []
    for text in texts:
        try:
            expected = json.dumps(json.loads(text), ensure_ascii=True)
        except ValueError as exc:
            expected = err(exc)
        rows.append("            { %s, %s }," % (cs(text), cs(expected)))
    return rows


def parse_json_cases():
    texts = ['{"id":"one","id":"two"}', '{"number":NaN}', "{", "[1e400]", '{"a":{"b":1,"b":2}}',
             '{"x":[1,2,3],"y":{"z":1.0}}', '{"a":{"b":1,"b":2},"c":NaN}', '{"c":Infinity,"a":{"b":1,"b":2}}',
             '{"\u00e9":"\U0001f600","a":1,"B":[]}', "  7  "]
    rows = []
    for text in texts:
        try:
            expected = margo_store.canonical_json(margo_store.parse_json(text))
        except ValueError as exc:
            expected = err(exc)
        rows.append("            { %s, %s }," % (cs(text), cs(expected)))
    return rows


def float_cases():
    rng = random.Random(20261107)
    numbers = [0.1, 0.2, 1 / 3, 2 / 3, 1e-4, 1e-5, 9.999999999999999e-5, 1e15, 1e16, 9007199254740993.0,
               123456789.123456789, 2.5e-308, 5e-324, 1.7976931348623157e308, 4.35, 0.07, 1e22, 1e23, 8.41e21,
               5e-310, 3.0e-310, 1.5, -1.5, 100.0, 1234567890123456.0, 12345678901234567.0]
    for _ in range(600):
        numbers.append(struct.unpack("<d", struct.pack("<Q", rng.getrandbits(64) & 0x7FEFFFFFFFFFFFFF))[0])
    for _ in range(300):
        numbers.append(rng.uniform(-1e6, 1e6))
    for _ in range(200):
        numbers.append(round(rng.uniform(-1000, 1000), rng.randint(0, 6)))
    rows = []
    for number in numbers:
        bits = struct.unpack("<q", struct.pack("<d", number))[0]
        rows.append("            { %dL, %s }," % (bits, cs(repr(number))))
    return rows


def iso_cases():
    datetimes = ["2026-01-01", "2026-01-01T00", "2026-01-01T00:00:00Z", "2026-01-01T00:00:00.1234567Z",
                 "2026-01-01T00:00:00,5Z", "2026-01-01 00:00:00+05:30", "2026-01-01T00:00:00+0530",
                 "20260101T000000Z", "2026-W01-1", "2026W013", "2026-01-01T24:00:00", "2026-02-29T00:00:00Z",
                 "2026-01-01T00:00:00+24:00", "2026-01-01T00:00:00-23:59:59.999999", "2026-01-01T00:00:00+05:30:15.123",
                 "2026-01-01T", "2026-01-01x00:00", "0001-01-01T00:00:00+01:00", "9999-12-31T23:59:59-01:00",
                 "2026-01-01T00:00:00 +00:00", "2026-01-01T00:00:00z", "2026-01-01T1000", "2020-W53-7",
                 "2021-W53-1", "2026-01-01\u00e900:00", "2026-13-01", "abc"]
    rows = []
    for kind, values in (("datetime", datetimes), ("date", ["2026-01-01", "20260101", "2026-W01", "2026-02-30", "2026-1-01"]),
                         ("time", ["09:00", "0900", "09", "09:00:00Z", "T09:00", "09:00:00.123456789", "9:00", "24:00"])):
        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            state_core_probe.cmd_isoformat(kind, values)
        for value, line in zip(values, buffer.getvalue().splitlines()):
            rows.append("            { %s, %s, %s }," % (cs(kind), cs(value), cs(line)))
    return rows


def timestamp_cases():
    stamps = ["2026-01-01T09:00:00+09:00", "2026-01-01T00:00:00Z", "2026-06-30T23:59:59.999999-11:30", "yesterday",
              "2026-01-01", "2026-01-01T00:00:00", "2026-13-01T00:00:00Z", "2026-01-01T00:00:00+24:00",
              "2026-01-01T00:00:00+01:60", "0001-01-01T00:00:00+01:00", "9999-12-31T23:59:59-01:00",
              "2026-01-01T00:00:00Z\n", "2026-01-01T00:00:00.1234567Z"]
    rows = []
    for stamp in stamps:
        try:
            expected = margo_store.validate_timestamp(stamp)
        except ValueError as exc:
            expected = err(exc)
        rows.append("            { %s, %s }," % (cs(stamp), cs(expected)))
    return rows


def args_cases():
    cases = [["--help"], ["list", "--help"], ["link", "-h"], [], ["list"], ["nope"],
             ["--acc", "a@example.com", "--state-d", "/x", "list", "--j", "--lim", "5", "--view=approval"],
             ["--state", "/x", "list"], ["list", "--view", "nope"], ["list", "--limit", "x"], ["list", "--limit", " 1_0 "],
             ["list", "--limit=-4"], ["list", "--json=yes"], ["show", "--revision", "2", "--", "-weird-id"],
             ["show", "id"], ["search", "a", "b"], ["search", "q", "--entity", "a", "--entity", "b"],
             ["ack", "x", "y", "--receipt", "r", "--item-key", "k1", "--item-key=k2"], ["ack"],
             ["feedback", "a.json", "--replies"], ["feedback"],
             ["link", "s", "t", "--rel", "contradicts", "--rev", "1"], ["link", "s", "t", "--relation", "bogus", "--revision", "1"]]
    rows = []
    for case in cases:
        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            state_core_probe.cmd_args(case)
        argv = ("new[] { %s }" % ", ".join(cs(a) for a in case)) if case else "System.Array.Empty<string>()"
        rows.append("            { %s, %s }," % (argv, cs(buffer.getvalue())))
    return rows


def repr_cases():
    texts = ["plain", "it's", 'say "hi"', "both ' and \"", "tab\tnew\nline\r", "\x00\x7f\x80\xa0\xad",
             "\u00e9\u20ac\u4e2d", "\U0001f600", "\ud800", "\u2028\u200b\ufeff", "\\back", ""]
    return ["            { %s, %s }," % (cs(text), cs(repr(text))) for text in texts]


def int_cases():
    texts = ["0", "42", "-7", "+3", " 12 ", "1_000", "1__0", "_1", "1_", "\u0663\u0661", "0x10", "1.5", "", " ",
             "9" * 30, "\u00a012\u2003", "12abc"]
    rows = []
    for text in texts:
        try:
            expected = str(int(text))
        except ValueError as exc:
            expected = err(exc)
        rows.append("            { %s, %s }," % (cs(text), cs(expected)))
    return rows


def wrap_cases():
    texts = ["explicitly save the ledger owner in private config; no network calls",
             "conditional local work edit/transition; confirmed work needs human evidence",
             "a-very-long-hyphenated-word-that-cannot-fit-anywhere-at-all-in-a-short-line and more",
             "short", "x" * 50, "well-known self-contained read-modify-write -- dashes -- here"]
    rows = []
    for width in (11, 20, 54):
        for text in texts:
            rows.append("            { %d, %s, new[] { %s } }," % (width, cs(text), ", ".join(cs(line) for line in textwrap.wrap(text, width))))
    return rows


def utf8_cases():
    samples = ["68656c6c6f", "c3a9", "f09f9880", "ff", "80", "c0af", "c3", "e282", "e228a1", "e282e2", "eda080",
               "f4908080", "f09f98", "61ff62", "e0a080", "e08080", "f0808080", "efbbbf7b7d"]
    rows = []
    for sample in samples:
        try:
            expected = bytes.fromhex(sample).decode("utf-8")
        except UnicodeDecodeError as exc:
            expected = err(exc)
        rows.append("            { %s, %s }," % (cs(sample), cs(expected)))
    return rows


def main():
    sections = [
        ("Utf8", "TheoryData<string, string>", utf8_cases()),
        ("Dumps", "TheoryData<string, int?, string?, string?, bool, bool, bool, string>", dumps_cases()),
        ("Loads", "TheoryData<string, string>", loads_cases()),
        ("ParseJson", "TheoryData<string, string>", parse_json_cases()),
        ("FloatRepr", "TheoryData<long, string>", float_cases()),
        ("Isoformat", "TheoryData<string, string, string>", iso_cases()),
        ("Timestamps", "TheoryData<string, string>", timestamp_cases()),
        ("DemoArgs", "TheoryData<string[], string>", args_cases()),
        ("StrRepr", "TheoryData<string, string>", repr_cases()),
        ("PyInt", "TheoryData<string, string>", int_cases()),
        ("TextWrap", "TheoryData<int, string, string[]>", wrap_cases()),
    ]
    lines = ["// <auto-generated>",
             "// Generated by Fixtures/generate_python_fixtures.py with CPython %d.%d.%d; do not edit by hand." % sys.version_info[:3],
             "// </auto-generated>",
             "#nullable enable", "using Xunit;", "", "namespace Margo.Cloud.State.Tests;", "",
             "internal static class PythonFixtures", "{"]
    for name, kind, rows in sections:
        lines.append("    public static %s %s => new()" % (kind, name))
        lines.append("    {")
        lines.extend(row.replace("            ", "        ", 1) for row in rows)
        lines.append("    };")
        lines.append("")
    lines[-1] = "}"
    (HERE.parent / "PythonFixtures.cs").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print("wrote", HERE.parent / "PythonFixtures.cs")


if __name__ == "__main__":
    main()

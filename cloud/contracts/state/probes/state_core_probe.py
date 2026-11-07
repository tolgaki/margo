#!/usr/bin/env python3
"""Contract probe for the shared state helpers (reference implementation).

Not a Margo command. tools/state_parity.py runs this file and its C# twin
(Margo.Cloud.State.Core.StateCoreProbeScript, ``margo-state state_core_probe.py``)
on the same inputs, so the helpers every port relies on -- json.dumps/json.loads,
margo_store.parse_json/canonical_json/validate_timestamp, datetime ISO parsing,
argparse, hashing, margo_store.connect and nested transactions -- are judged
byte for byte. It writes only below the --state-dir/--config paths it is given.
"""

import argparse
import contextlib
import hashlib
import io
import json
import sys
import uuid
from datetime import date, datetime, time, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[4]
sys.path.insert(0, str(ROOT / "skills" / "chief-of-staff" / "scripts"))

import margo_store  # noqa: E402


def _error(exc):
    return "!" + type(exc).__name__ + ": " + str(exc)


def _cases():
    value = json.loads(sys.stdin.read())
    if not isinstance(value, list):
        raise SystemExit("stdin must be a JSON array")
    return value


def cmd_dumps():
    for case in _cases():
        options = dict(case.get("options", {}))
        if "separators" in options:
            options["separators"] = tuple(options["separators"])
        try:
            print(json.dumps(json.dumps(json.loads(case["value"]), **options)))
        except (ValueError, TypeError, RecursionError) as exc:
            print(_error(exc))


def cmd_loads():
    for text in _cases():
        try:
            value = json.loads(text)
            print(json.dumps(value, ensure_ascii=True, allow_nan=True))
        except (ValueError, RecursionError) as exc:
            print(_error(exc))


def cmd_parse_json():
    for text in _cases():
        try:
            value = margo_store.parse_json(text)
            print(json.dumps(margo_store.canonical_json(value)))
        except (ValueError, RecursionError) as exc:
            print(_error(exc))


def cmd_depth(shapes):
    """Nesting limits without megabyte fixtures: SHAPE is array:N, object:N or mixed:N."""
    for spec in shapes:
        kind, count = spec.split(":")
        count = int(count)
        if kind == "array":
            text = "[" * count + "]" * count
        elif kind == "object":
            text = '{"a":' * count + "1" + "}" * count
        else:
            text = '{"a":' * (count - 1) + "[1]" + "}" * (count - 1)
        line = [spec]
        for name, parse in (("loads", json.loads), ("parse_json", margo_store.parse_json)):
            try:
                out = json.dumps(parse(text))
                line.append("%s ok %d %s" % (name, len(out), hashlib.sha256(out.encode("utf-8")).hexdigest()[:16]))
            except (ValueError, RecursionError) as exc:
                line.append("%s %s" % (name, _error(exc)))
        print(" | ".join(line))


def cmd_timestamps(values):
    for value in values:
        try:
            print(margo_store.validate_timestamp(value))
        except ValueError as exc:
            print(_error(exc))


def cmd_isoformat(kind, values):
    for value in values:
        try:
            if kind == "date":
                parsed = date.fromisoformat(value)
                print(parsed.isoformat())
            elif kind == "time":
                parsed = time.fromisoformat(value)
                print(parsed.isoformat(), parsed.isoformat(timespec="microseconds"), str(parsed.utcoffset()))
            else:
                parsed = datetime.fromisoformat(value)
                line = [parsed.isoformat(), parsed.isoformat(timespec="milliseconds"), str(parsed), str(parsed.utcoffset())]
                if parsed.tzinfo is not None:
                    try:
                        line.append(parsed.astimezone(timezone.utc).isoformat())
                    except OverflowError as exc:
                        line.append(_error(exc))
                print(" | ".join(line))
        except (ValueError, OverflowError) as exc:
            print(_error(exc))


def cmd_ids(values):
    for value in values:
        print(hashlib.sha256(value.encode("utf-8")).hexdigest())
    token = uuid.uuid4().hex
    print(token, len(token), token[12], token[16] in "89ab")
    print(margo_store.utc_now())


def demo_parser():
    root = argparse.ArgumentParser(prog="demo.py",
                                   description="Contract probe parser; it exercises the argparse features "
                                               "the state scripts use, including help wrapping.")
    root.add_argument("--account", help="explicit principal; otherwise MARGO_ACCOUNT/private config")
    root.add_argument("--state-root", "--state-dir", dest="state_root", help="private state base directory")
    sub = root.add_subparsers(dest="command", required=True)
    command = sub.add_parser("list", help="portable review views; JSON is also the optional canvas contract "
                                          "and wraps over several lines")
    command.add_argument("--view", choices=["decisions", "approval", "waiting", "problems", "all"], default="decisions")
    command.add_argument("--json", action="store_true")
    command.add_argument("--limit", type=int, default=50)
    command = sub.add_parser("show")
    command.add_argument("id")
    command.add_argument("--revision", type=int, required=True)
    command = sub.add_parser("search", help="bounded local search")
    command.add_argument("query", nargs="?")
    command.add_argument("--input", help="private JSON {query}; - reads stdin instead of process arguments")
    command.add_argument("--entity", action="append", default=[])
    command.add_argument("--mode", choices=("hybrid", "semantic", "lexical"), default="hybrid")
    command = sub.add_parser("ack", help="ack actual receipt-covered items, not unrelated batch members")
    command.add_argument("ids", nargs="*")
    command.add_argument("--item-key", action="append", dest="item_keys")
    command.add_argument("--batch")
    command.add_argument("--receipt", required=True, help="publication ID returned after durable output recording")
    command = sub.add_parser("feedback")
    command.add_argument("paths", nargs="+")
    command.add_argument("--replies", nargs="*", default=[])
    command.add_argument("--since", help="only posts created on/after YYYY-MM-DD")
    command.add_argument("--format", choices=["table", "json"], default="table")
    command = sub.add_parser("link", help="link two memories")
    command.add_argument("source")
    command.add_argument("target")
    command.add_argument("--relation", required=True, choices=sorted({"supports", "contradicts", "supersedes"}))
    command.add_argument("--revision", required=True, type=int)
    command.add_argument("--evidence", required=False)
    return root


def cmd_args(argv):
    out, err = io.StringIO(), io.StringIO()
    status = 0
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
        try:
            namespace = demo_parser().parse_args(argv)
        except SystemExit as exc:
            status = exc.code
            namespace = None
    print("exit=%s" % status)
    if namespace is not None:
        print(json.dumps(vars(namespace), sort_keys=True))
    print("--- stdout")
    print(out.getvalue(), end="")
    print("--- stderr")
    print(err.getvalue(), end="")


def _show_connection(connection):
    pragmas = {name: connection.execute("PRAGMA " + name).fetchone()[0]
               for name in ("foreign_keys", "busy_timeout", "temp_store", "synchronous", "journal_mode")}
    print(json.dumps({"pragmas": pragmas, "in_transaction": connection.in_transaction,
                      "row_factory_row": connection.row_factory is __import__("sqlite3").Row,
                      "meta": dict(connection.execute("SELECT key,value FROM margo_meta ORDER BY key"))},
                     sort_keys=True))


def cmd_store(args):
    try:
        if args.store_command == "state-path":
            principal, path = margo_store.state_path(args.account, args.state_dir)
            print(principal)
            print(path)
            return 0
        if args.store_command == "resolve-account":
            print(margo_store.resolve_account(args.account))
            return 0
        if args.store_command == "read-json":
            print(json.dumps(margo_store.canonical_json(margo_store.read_json(args.path))))
            return 0
        connection = margo_store.connect(args.account, args.state_dir, read_only=args.store_command == "connect-read")
        try:
            if args.store_command in ("connect", "connect-read"):
                _show_connection(connection)
            else:
                transaction_demo(connection)
        finally:
            connection.close()
        return 0
    except margo_store.StateError as exc:
        print(_error(exc))
        return 2


def transaction_demo(connection):
    def rows():
        return [tuple(row) for row in connection.execute("SELECT id, label FROM probe_items ORDER BY id")]

    connection.execute("CREATE TABLE IF NOT EXISTS probe_items (id INTEGER PRIMARY KEY, label TEXT NOT NULL UNIQUE)")
    print("after create", connection.in_transaction, rows())
    with margo_store.transaction(connection):
        connection.execute("INSERT INTO probe_items(label) VALUES ('outer-1')")
        try:
            with margo_store.transaction(connection):
                connection.execute("INSERT INTO probe_items(label) VALUES ('inner-rolled-back')")
                raise RuntimeError("inner failure")
        except RuntimeError as exc:
            print("inner", exc, connection.in_transaction, rows())
        with margo_store.transaction(connection):
            connection.execute("INSERT INTO probe_items(label) VALUES ('inner-kept')")
        print("outer", connection.in_transaction, rows())
    print("committed", connection.in_transaction, rows())
    try:
        with margo_store.transaction(connection):
            connection.execute("INSERT INTO probe_items(label) VALUES ('outer-rolled-back')")
            connection.execute("INSERT INTO probe_items(label) VALUES ('outer-1')")
    except Exception as exc:  # sqlite3.IntegrityError
        print("outer failure", type(exc).__name__, exc, connection.in_transaction, rows())
    with margo_store.transaction(connection, read_only=True):
        print("read", connection.in_transaction, rows())
    with connection:
        cursor = connection.execute("UPDATE probe_items SET label=label||'!' WHERE label LIKE 'inner%'")
        print("implicit", connection.in_transaction, cursor.rowcount)
    print("after with", connection.in_transaction, rows())
    try:
        with connection:
            connection.execute("DELETE FROM probe_items")
            raise ValueError("abandon delete")
    except ValueError as exc:
        print("rolled back", exc, connection.in_transaction, rows())
    cursor = connection.execute("SELECT count(*) AS total, max(id) AS last FROM probe_items")
    row = cursor.fetchone()
    print("row", row["TOTAL"], row["last"], row.keys())
    for statement in ("SELECT 1; SELECT 2", "SELECT nope FROM probe_items"):
        try:
            connection.execute(statement)
        except Exception as exc:  # sqlite3.ProgrammingError / OperationalError
            print("error", type(exc).__name__, exc)
    try:
        connection.execute("INSERT INTO probe_items(label) VALUES (?)", (1, 2))
    except Exception as exc:  # sqlite3.ProgrammingError
        print("error", type(exc).__name__, exc)
    connection.rollback()


def main(argv=None):
    argv = list(sys.argv[1:] if argv is None else argv)
    if argv[:1] == ["args"]:
        cmd_args(argv[1:])
        return 0
    parser = argparse.ArgumentParser(prog="state_core_probe.py")
    parser.add_argument("--account")
    parser.add_argument("--state-dir")
    sub = parser.add_subparsers(dest="command", required=True)
    for name in ("dumps", "loads", "parse-json"):
        sub.add_parser(name)
    command = sub.add_parser("timestamps")
    command.add_argument("values", nargs="*")
    command = sub.add_parser("depth")
    command.add_argument("shapes", nargs="*")
    command = sub.add_parser("isoformat")
    command.add_argument("--kind", choices=("datetime", "date", "time"), default="datetime")
    command.add_argument("values", nargs="*")
    command = sub.add_parser("ids")
    command.add_argument("values", nargs="*")
    command = sub.add_parser("store")
    command.add_argument("store_command", choices=("connect", "connect-read", "transactions", "state-path",
                                                   "resolve-account", "read-json"))
    command.add_argument("--path")
    args = parser.parse_args(argv)
    if args.command == "dumps":
        cmd_dumps()
    elif args.command == "loads":
        cmd_loads()
    elif args.command == "parse-json":
        cmd_parse_json()
    elif args.command == "timestamps":
        cmd_timestamps(args.values)
    elif args.command == "depth":
        cmd_depth(args.shapes)
    elif args.command == "isoformat":
        cmd_isoformat(args.kind, args.values)
    elif args.command == "ids":
        cmd_ids(args.values)
    else:
        return cmd_store(args)
    return 0


if __name__ == "__main__":
    sys.exit(main())

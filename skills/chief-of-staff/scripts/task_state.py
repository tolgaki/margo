#!/usr/bin/env python3
"""Private task-run plans, bounded claims, receipts and safe foreground recovery."""

import argparse
from pathlib import Path
import sqlite3
import sys

from margo_store import NotInitialized, StateError, add_state_arguments, canonical_json, parse_json, resolve_account
from task_runs import TaskStore, task_identity


def load(path):
    if path == "-":
        raw = sys.stdin.read(262145)
    else:
        with Path(path).expanduser().open(encoding="utf-8") as stream:
            raw = stream.read(262145)
    if len(raw) > 262144:
        raise StateError("task input exceeds 256 KiB of text")
    value = parse_json(raw)
    if not isinstance(value, dict):
        raise StateError("task input must be an object")
    return value


def parser():
    root = argparse.ArgumentParser(description=__doc__)
    add_state_arguments(root)
    commands = root.add_subparsers(dest="command", required=True)
    commands.add_parser("init", help="explicitly initialize private task/work/coverage schemas; no source collection")
    commands.add_parser("health", help="read aggregate task journal health without initialization")
    command = commands.add_parser("record-id", help="resolve a local request key without initializing storage")
    command.add_argument("key")
    command = commands.add_parser("create", help="store a bounded private plan, not an external-action approval")
    command.add_argument("key")
    command.add_argument("--input", required=True)
    command = commands.add_parser("list", help="read existing runs without initialization")
    command.add_argument("--limit", type=int, default=50)
    command.add_argument("--after")
    for name in ("show", "history", "recover", "sync"):
        command = commands.add_parser(name)
        command.add_argument("id")
        if name == "history":
            command.add_argument("--limit", type=int, default=100)
    for name in ("pause", "cancel"):
        command = commands.add_parser(name, help="stop future work; never undo or erase an in-flight effect")
        command.add_argument("id")
        command.add_argument("--reason", required=True)
    for name in ("start", "charge", "finish", "resume", "retry", "reconcile"):
        command = commands.add_parser(name)
        command.add_argument("--input", required=True, help="exact API envelope; use - for private stdin")
    for name in ("replan-preview", "replan"):
        command = commands.add_parser(name)
        command.add_argument("id")
        command.add_argument("--input", required=True, help="complete replacement plan")
        if name == "replan":
            command.add_argument("--evidence", required=True)
    return root


def main(argv=None):
    args = parser().parse_args(argv)
    store = None
    try:
        if args.command == "record-id":
            print(canonical_json({"id": task_identity(resolve_account(args.account), args.key)}))
            return 0
        read_only = args.command in {"list", "show", "history", "health", "replan-preview"}
        store = TaskStore(args.account, args.state_dir, read_only=read_only)
        if args.command == "init":
            result = {"account": store.account, "schema_version": "1", "initialized": True,
                      "collected": False, "outbound_action": False}
        elif args.command == "create":
            result = store.create(args.key, load(args.input))
        elif args.command == "list":
            result = store.list(args.limit, args.after)
        elif args.command == "health":
            result = store.health()
        elif args.command == "show":
            result = store.show(args.id)
        elif args.command == "history":
            result = store.history(args.id, args.limit)
        elif args.command in {"pause", "cancel"}:
            operation = store.pause if args.command == "pause" else store.cancel
            result = operation(args.id, args.reason)
        elif args.command in {"recover", "sync"}:
            result = (store.recover if args.command == "recover" else store.sync)(args.id)
        elif args.command == "replan-preview":
            result = store.replan_preview(args.id, load(args.input))
        elif args.command == "replan":
            result = store.replan(args.id, load(args.input), load(args.evidence))
        else:
            operations = {
                "start": store.start, "charge": store.charge, "finish": store.finish,
                "resume": store.resume, "retry": store.retry, "reconcile": store.reconcile,
            }
            result = operations[args.command](**load(args.input))
        print(canonical_json(result))
        return 0
    except (StateError, OSError, sqlite3.Error, TypeError, KeyError, ValueError) as exc:
        result = {"error": str(exc), "command": args.command}
        if isinstance(exc, NotInitialized):
            result["code"] = "not_initialized"
        print(canonical_json(result), file=sys.stderr)
        return 2
    finally:
        if store is not None:
            store.close()


if __name__ == "__main__":
    sys.exit(main())

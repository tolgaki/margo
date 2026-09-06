#!/usr/bin/env python3
"""Portable CLI action desk. This program never calls an outbound API."""

import argparse
import json
import sqlite3
import sys
from pathlib import Path

RECORD_KINDS = ("artifact", "feedback", "meeting", "outcome", "rule")


def load(path):
    from work_ledger import StateError

    from margo_store import parse_json

    value = parse_json(sys.stdin.read() if path == "-" else Path(path).read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise StateError("input JSON must be an object")
    return value


def parser():
    root = argparse.ArgumentParser(description=__doc__)
    root.add_argument("--account", help="explicit configured account boundary; never silently defaults")
    root.add_argument("--state-root", "--state-dir", dest="state_root",
                      help="private state root accepted by margo_store")
    commands = root.add_subparsers(dest="command", required=True)
    command = commands.add_parser("source", help="ingest a stable source revision with minimal evidence")
    command.add_argument("--input", required=True)
    command = commands.add_parser("ingest", help="idempotently capture one candidate; never confirms")
    command.add_argument("--input", required=True, help="JSON {claim_key,data}")
    command = commands.add_parser("list", help="portable review views; JSON is also the optional canvas contract")
    command.add_argument("--view", choices=["decisions", "approval", "waiting", "problems", "all"], default="decisions")
    command.add_argument("--json", action="store_true")
    for name in ("show", "history"):
        command = commands.add_parser(name)
        command.add_argument("id")
        command.add_argument("--json", action="store_true")
    command = commands.add_parser("item-update", help="conditional local work edit/transition; confirmed work needs human evidence")
    command.add_argument("id")
    command.add_argument("--revision", type=int, required=True)
    command.add_argument("--input", required=True, help="JSON {state?,patch?,evidence?}")
    command = commands.add_parser("relate", help="link work items or a canonical tracker; cycle-checked")
    command.add_argument("id")
    command.add_argument("--revision", type=int, required=True)
    command.add_argument("--input", required=True, help="JSON {kind,target_id,evidence?}")
    command = commands.add_parser("propose", help="prepare a local exact action payload, not an Outlook draft")
    command.add_argument("--input", required=True)
    command = commands.add_parser("edit", help="new immutable action revision; invalidates prior approvals")
    command.add_argument("id")
    command.add_argument("--revision", type=int, required=True)
    command.add_argument("--expected-hash", help="additional atomic precondition from the displayed action_hash")
    command.add_argument("--input", required=True)
    command = commands.add_parser("approve", help="record one exact human decision; not an authentication boundary")
    command.add_argument("id")
    command.add_argument("--revision", type=int, required=True)
    command.add_argument("--input", required=True, help="JSON {action_hash,evidence,expires_at}; no --yes/approve-all")
    command = commands.add_parser("defer", help="defer a proposal or work item without deleting its sources")
    command.add_argument("id")
    command.add_argument("--revision", type=int, required=True)
    command.add_argument("--until", required=True)
    command.add_argument("--expected-hash", help="additional atomic action_hash precondition (actions only)")
    command.add_argument("--evidence", help="human evidence JSON required for confirmed work")
    command = commands.add_parser("dismiss", help="dismiss only an action proposal; never closes a work item")
    command.add_argument("id")
    command.add_argument("--revision", type=int, required=True)
    command.add_argument("--expected-hash", help="additional atomic precondition from the displayed action_hash")
    command = commands.add_parser("begin", help="transactional foreground preflight; return exact payload once, never sends")
    command.add_argument("id")
    command.add_argument("--revision", type=int, required=True)
    command.add_argument("--input", required=True, help="fresh read: checked_at,target_fingerprint,source_refs")
    for name in ("finish", "reconcile"):
        command = commands.add_parser(name, help="journal a real result; ambiguous writes must be reconciled, never retried")
        command.add_argument("attempt_id")
        command.add_argument("--input", required=True, help="JSON {state,receipt}")
    command = commands.add_parser("import-preview", help="review structured legacy JSON or expose unsafe Markdown migration")
    command.add_argument("path")
    command = commands.add_parser("import-commitments", help="transactional reviewed structured legacy import")
    command.add_argument("path")
    command.add_argument("--digest", required=True)
    command.add_argument("--evidence", required=True)
    command = commands.add_parser("export-commitments", help="confirmed-only Markdown compatibility view with edit detection")
    command.add_argument("path")
    command.add_argument("--expected-digest", help="explicitly reviewed current file hash when adopting a manual view")
    command = commands.add_parser("record-id", help="derive typed record ID before collecting a human decision")
    command.add_argument("kind", choices=RECORD_KINDS)
    command.add_argument("identity")
    command = commands.add_parser("record", help="create/update typed outcome, meeting, feedback, rule, or private Markdown artifact")
    command.add_argument("kind", choices=RECORD_KINDS)
    command.add_argument("identity")
    command.add_argument("--revision", type=int)
    command.add_argument("--input", required=True, help="JSON {data,state?,evidence?}; updates replace all data")
    command = commands.add_parser("recap-check", help="persist a bounded pending recap check; policy blocks stop retries")
    command.add_argument("id")
    command.add_argument("--revision", type=int, required=True)
    command.add_argument("--input", required=True, help="JSON {result,evidence_ref,next_check_at?}")
    command = commands.add_parser("carry-forward", help="carry open topic/work references into a later occurrence, without duplicating work")
    command.add_argument("id")
    command.add_argument("--revision", type=int, required=True)
    command.add_argument("--input", required=True, help="JSON {target_id,target_revision,evidence}")
    command = commands.add_parser("artifact-shared", help="link a succeeded exact delivery receipt; never publishes")
    command.add_argument("id")
    command.add_argument("--revision", type=int, required=True)
    command.add_argument("--attempt-id", required=True)
    return root


def dispatch(ledger, args):
    from work_ledger import StateError
    from work_productivity import Productivity

    command = args.command
    data = load(args.input) if hasattr(args, "input") else None
    if command == "source":
        return ledger.source(**data)
    if command == "ingest":
        return ledger.ingest(data["data"], data["claim_key"])
    if command == "list":
        return ledger.list(args.view)
    if command == "show":
        return ledger.show(args.id)
    if command == "history":
        return ledger.history(args.id)
    if command == "item-update":
        return ledger.update_item(args.id, args.revision, **data)
    if command == "relate":
        return ledger.relate(args.id, revision=args.revision, **data)
    if command == "propose":
        return ledger.propose(data)
    if command == "edit":
        return ledger.edit_action(args.id, args.revision, data, expected_hash=args.expected_hash)
    if command == "approve":
        return ledger.approve(args.id, args.revision, **data)
    if command == "defer":
        entity = ledger.show(args.id)
        if entity["type"] == "action":
            return ledger.disposition(args.id, args.revision, "deferred", args.until, expected_hash=args.expected_hash)
        if entity["type"] == "item":
            if args.expected_hash is not None:
                raise StateError("expected-hash is for action revisions, not work items")
            return ledger.update_item(args.id, args.revision, state="deferred", patch={"deferred_until": args.until},
                                      evidence=load(args.evidence) if args.evidence else None)
        raise StateError("defer supports items/actions; use record for typed lifecycle edits")
    if command == "dismiss":
        return ledger.disposition(args.id, args.revision, "dismissed", expected_hash=args.expected_hash)
    if command == "begin":
        return ledger.begin(args.id, args.revision, data)
    if command in {"finish", "reconcile"}:
        return ledger.finish(args.attempt_id, reconcile=command == "reconcile", **data)
    if command == "import-preview":
        return ledger.import_preview(args.path)
    if command == "import-commitments":
        return ledger.import_commitments(args.path, args.digest, load(args.evidence))
    if command == "export-commitments":
        return ledger.export_commitments(args.path, args.expected_digest)
    products = Productivity(ledger)
    if command == "record-id":
        return {"id": products.record_id(args.kind, args.identity)}
    if command == "record":
        return products.put(args.kind, args.identity, revision=args.revision, **data)
    if command == "recap-check":
        return products.recap_retry(args.id, args.revision, **data)
    if command == "carry-forward":
        return products.carry_forward(args.id, args.revision, **data)
    if command == "artifact-shared":
        return products.share_receipt(args.id, args.revision, args.attempt_id)
    raise StateError("unsupported command")


def main(argv=None):
    args = parser().parse_args(argv)
    try:
        from work_ledger import Ledger, StateError
    except ImportError as exc:
        print(json.dumps({"error": "Shared state dependency is not installed: %s" % exc,
                          "command": args.command}), file=sys.stderr)
        return 2
    ledger = None
    try:
        ledger = Ledger(account=args.account, state_root=args.state_root)
        result = dispatch(ledger, args)
        print(json.dumps(result, ensure_ascii=False, indent=2, allow_nan=False))
        return 0
    except (StateError, sqlite3.Error, OSError, ValueError, KeyError, TypeError) as exc:
        print(json.dumps({"error": str(exc), "command": args.command}, ensure_ascii=False), file=sys.stderr)
        return 2
    finally:
        if ledger is not None:
            ledger.close()


if __name__ == "__main__":
    sys.exit(main())

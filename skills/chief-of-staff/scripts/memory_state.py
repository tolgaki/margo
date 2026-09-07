#!/usr/bin/env python3
"""Private user/agent memory, local semantic indexing, and bounded context retrieval."""

import argparse
from pathlib import Path
import sqlite3
import sys

from margo_store import NotInitialized, StateError, add_state_arguments, canonical_json, parse_json, resolve_account


def load(path, maximum=None):
    if path == "-":
        raw = sys.stdin.read() if maximum is None else sys.stdin.read(maximum + 1)
    else:
        with Path(path).open(encoding="utf-8") as stream:
            raw = stream.read() if maximum is None else stream.read(maximum + 1)
    if maximum is not None and len(raw) > maximum:
        raise StateError("input exceeds its character budget")
    result = parse_json(raw)
    if not isinstance(result, dict):
        raise StateError("input must be a JSON object")
    return result


def parser():
    root = argparse.ArgumentParser(description=__doc__)
    add_state_arguments(root)
    sub = root.add_subparsers(dest="command", required=True)
    sub.add_parser("init", help="initialise private memory schema; does not collect sources")
    sub.add_parser("migrate", help="explicitly migrate memory schema v1 to v2 after pausing writers and backing up")
    sub.add_parser("status", help="memory lifecycle and semantic index health")
    command = sub.add_parser("record-id")
    command.add_argument("domain", choices=("user", "agent"))
    command.add_argument("key")
    command = sub.add_parser("put", help="capture or conditionally revise one sourced memory")
    command.add_argument("key")
    command.add_argument("--input", required=True, help="{data,status?,evidence?}; - reads stdin")
    command.add_argument("--revision", type=int)
    command = sub.add_parser("capture", help="stage a minimal observation under the reviewed capture policy")
    command.add_argument("key")
    command.add_argument("--input", required=True, help="memory data object, not an approval envelope")
    command.add_argument("--revision", type=int)
    command = sub.add_parser("revise", help="reviewed correction or lifecycle change using the current memory ID")
    command.add_argument("id")
    command.add_argument("--revision", required=True, type=int)
    command.add_argument("--input", required=True, help="{data,status}; full replacement memory data")
    command.add_argument("--evidence")
    command = sub.add_parser("show")
    command.add_argument("id")
    command = sub.add_parser("list")
    command.add_argument("--domain", choices=("user", "agent"))
    command.add_argument("--status")
    for name in ("inspect", "history", "forget-preview", "usage"):
        command = sub.add_parser(name, help="inspect memory and its private lifecycle; no approval or change")
        command.add_argument("id")
    for name in ("graph", "explain"):
        command = sub.add_parser(name, help="inspect bounded current relationships and why context is eligible")
        command.add_argument("id")
        command.add_argument("--depth", type=int, default=2)
        command.add_argument("--limit", type=int, default=20)
        command.add_argument("--routine")
        command.add_argument("--usage", choices=("reasoning", "drafting"), default="reasoning")
        command.add_argument("--environment")
        command.add_argument("--domain", choices=("user", "agent"))
        command.add_argument("--budget-chars", type=int, default=12000)
    for name in ("search", "context"):
        command = sub.add_parser(name)
        command.add_argument("query", nargs="?")
        command.add_argument("--input", help="private JSON {query}; - reads stdin instead of process arguments")
        command.add_argument("--domain", choices=("user", "agent"))
        command.add_argument("--routine")
        command.add_argument("--usage", choices=("reasoning", "drafting"), default="reasoning")
        command.add_argument("--entity", action="append", default=[])
        command.add_argument("--environment", help="JSON file with current host/tool versions")
        command.add_argument("--mode", choices=("hybrid", "semantic", "lexical"), default="hybrid")
        command.add_argument("--limit", type=int, default=8)
        if name == "context":
            command.add_argument("--budget-chars", type=int, default=12000)
            command.add_argument("--work-id", action="append", default=[])
            command.add_argument("--depth", type=int, default=2)
            command.add_argument("--nodes", type=int, default=30)
    command = sub.add_parser("index", help="process versioned outbox jobs with local embeddings")
    command.add_argument("--limit", type=int, default=100)
    command.add_argument("--rebuild", action="store_true")
    command = sub.add_parser("forget", help="erase memory and derived indexes after explicit user approval")
    command.add_argument("id")
    command.add_argument("--revision", required=True, type=int)
    command.add_argument("--evidence", required=True)
    from memory_store import RELATIONS
    for name in ("link", "unlink"):
        command = sub.add_parser(name)
        command.add_argument("source")
        command.add_argument("target")
        command.add_argument("--relation", required=True, choices=sorted(RELATIONS))
        command.add_argument("--revision", required=True, type=int)
        command.add_argument("--evidence", required=name == "unlink")
        if name == "link":
            command.add_argument("--input", help="relationship validity and source_refs JSON")
            command.add_argument("--target-revision", type=int)
    sub.add_parser("policy", help="show capture, retention and usage settings; capture is off until configured")
    for name in ("policy-preview", "policy-set"):
        command = sub.add_parser(name)
        command.add_argument("--input", required=True)
        if name == "policy-set":
            command.add_argument("--evidence", required=True)
    command = sub.add_parser("maintain", help="bounded expiry/review under approved retention; never source ingestion")
    command.add_argument("--limit", type=int, default=100)
    command.add_argument("--after")
    command = sub.add_parser("record-usage", help="opt-in minimal memory IDs/revisions, not prompts or transcripts")
    command.add_argument("--input", required=True, help="{refs,routine,outcome?,event_id?}")
    sub.add_parser("tombstones", help="export a minimal deletion journal for private backups")
    for name in ("tombstones-preview", "tombstones-restore"):
        command = sub.add_parser(name)
        command.add_argument("--input", required=True)
        if name == "tombstones-restore":
            command.add_argument("--evidence", required=True)
    for name in ("export-preview", "export"):
        command = sub.add_parser(name, help="review a sanitized lesson recipe; no automatic publication")
        command.add_argument("id")
        command.add_argument("--input", required=True, help="sanitized recipe fields, never raw memory export")
        if name == "export":
            command.add_argument("--evidence", required=True)
            command.add_argument("--out", required=True, help="new private file; existing files are never replaced")
    command = sub.add_parser("preferences-preview", help="inspect proposed private configuration chunks before importing")
    command.add_argument("path")
    command = sub.add_parser("preferences-import", help="seed saved preferences under explicit import approval")
    command.add_argument("path")
    command.add_argument("--evidence", required=True)
    command = sub.add_parser("capabilities", help="observe installed skill hashes, not validate competence")
    command.add_argument("--skills-dir")
    command.add_argument("--tools", help="private host-exported tool manifest")
    command.add_argument("--environment", help="JSON with exact host/account/version scope")
    command = sub.add_parser("validate-capability", help="record a pinned synthetic input exercise or existing execution receipt")
    command.add_argument("id")
    command.add_argument("--revision", required=True, type=int)
    command.add_argument("--evidence", required=True)
    command = sub.add_parser("propose-lesson", help="stage an evidence-backed scoped recipe, never executable instructions")
    command.add_argument("key")
    command.add_argument("--input", required=True)
    command.add_argument("--revision", type=int)
    command = sub.add_parser("activate-lesson", help="activate an exact reviewed candidate without granting any tool authority")
    command.add_argument("id")
    command.add_argument("--revision", required=True, type=int)
    command.add_argument("--evidence", required=True)
    command = sub.add_parser("trend-definition", help="preview or explicitly approve bounded aggregate trend thresholds")
    command.add_argument("name")
    command.add_argument("--input", required=True)
    command.add_argument("--revision", type=int)
    command.add_argument("--evidence")
    command = sub.add_parser("trend", help="stage a coverage-backed trend hypothesis, not a confirmed rule")
    command.add_argument("key")
    command.add_argument("--input", required=True)
    command.add_argument("--revision", type=int)
    command = sub.add_parser("consolidate", help="propose one bounded review page; no merge, activation or erasure")
    command.add_argument("--limit", type=int, default=10)
    command.add_argument("--scan-limit", type=int, default=100)
    command.add_argument("--after")
    command.add_argument("--environment")
    command = sub.add_parser("record-consolidation", help="record delivery of an unchanged proposal page, never human approval")
    command.add_argument("--input", required=True)
    for name in ("dream-checkpoint", "dream-plan", "dream-finish"):
        command = sub.add_parser(name, help="opted-in bounded Dream checkpoint/reflection; no history scraping")
        command.add_argument("--input", required=True, help="private checkpoint/reflection JSON; - reads stdin")
    command = sub.add_parser("dream-start", help="claim one manual reflection and reserve its host model budget")
    command.add_argument("key")
    command.add_argument("--input", required=True, help="{request,snapshot_hash,request_ref} from dream-plan and current user")
    command = sub.add_parser("dream-inspect", help="inspect collection, reflection, candidates and source validity")
    command.add_argument("id")
    command = sub.add_parser("dream-status", help="show exact host/workspace scope and opt-in; no setup or history reads")
    command.add_argument("--host", required=True)
    command.add_argument("--workspace", required=True)
    return root


def main(argv=None):
    args = parser().parse_args(argv)
    memory = None
    try:
        from memory_store import MemoryStore, memory_identity
        from memory_context import read_snapshot
        from memory_search import MemorySearch
        from memory_learning import (
            activate_lesson, capabilities, configure_trend, import_preferences, lesson_plan,
            preferences_plan, trend, validation,
        )
        import memory_governance as governance

        if args.command == "record-id":
            print(canonical_json({"id": memory_identity(resolve_account(args.account), args.domain, args.key)[0]}))
            return 0
        read_commands = {
            "status", "show", "inspect", "history", "forget-preview", "usage", "graph", "explain",
            "list", "search", "context", "policy", "policy-preview", "tombstones", "tombstones-preview",
            "export-preview", "preferences-preview", "consolidate",
            "dream-plan", "dream-inspect", "dream-status",
        }
        read_only = args.command in read_commands or (args.command == "trend-definition" and not args.evidence)
        memory = MemoryStore(account=args.account, state_root=args.state_dir,
                             migrate=args.command == "migrate", read_only=read_only)
        search = MemorySearch(memory, rebuild_schema=args.command == "index" and args.rebuild) if args.command in {
            "init", "migrate", "status", "search", "context", "graph", "explain", "index",
        } else None
        if args.command in ("init", "migrate"):
            result = {"account": memory.account, "status": "initialised", "collected": False,
                      "schema_version": memory.health()["schema_version"]}
        elif args.command == "status":
            from memory_encoder import EmbeddingError, status_local
            try:
                encoder = status_local()
            except EmbeddingError as exc:
                encoder = {"status": "unavailable", "error": str(exc)}
            result = {"account": memory.account, "memory": memory.health(), "index": search.health(),
                      "embedding_runtime": encoder, "policy": memory.policy()}
        elif args.command == "put":
            result = memory.put(args.key, revision=args.revision, **load(args.input))
        elif args.command == "capture":
            result = memory.capture(args.key, load(args.input), revision=args.revision)
        elif args.command == "revise":
            value = load(args.input)
            if set(value) != {"data", "status"}:
                raise StateError("revise requires data and status only")
            result = memory.revise(args.id, value["data"], value["status"], args.revision,
                                   load(args.evidence) if args.evidence else None)
        elif args.command == "show":
            result = memory.show(args.id)
        elif args.command == "inspect":
            with read_snapshot(memory.conn):
                result = {"memory": memory.show(args.id), "history": memory.history(args.id),
                          "links": memory.links(args.id), "forget_preview": memory.forget_preview(args.id),
                          "usage": memory.usage(args.id)}
        elif args.command == "history":
            result = {"id": args.id, "history": memory.history(args.id)}
        elif args.command == "forget-preview":
            result = memory.forget_preview(args.id)
        elif args.command == "usage":
            result = {"id": args.id, "usage": memory.usage(args.id)}
        elif args.command in ("graph", "explain"):
            operation = search.graph if args.command == "graph" else search.explain
            result = operation(args.id, max_depth=args.depth, max_nodes=args.limit, routine=args.routine,
                               usage=args.usage, environment=load(args.environment) if args.environment else None,
                               domain=args.domain, budget_chars=args.budget_chars)
        elif args.command == "list":
            with read_snapshot(memory.conn):
                records = memory.list(domain=args.domain, status=args.status)
                conflicts = {edge[key] for row in records for edge in memory.active_links(row["id"])
                             if edge["relation"] == "contradicts" for key in ("source_id", "target_id")}
                result = {"account": memory.account, "memories": records, "conflicted_ids": sorted(conflicts)}
        elif args.command in ("search", "context"):
            query = load(args.input).get("query") if args.input else args.query
            if args.input and args.query is not None:
                raise StateError("choose a positional query or --input, not both")
            options = {"routine": args.routine, "usage": args.usage, "entities": args.entity,
                       "environment": load(args.environment) if args.environment else None, "mode": args.mode}
            if args.command == "search":
                result = search.search(query, limit=args.limit, domain=args.domain, **options)
            else:
                result = search.context(query, domain=args.domain, budget_chars=args.budget_chars,
                                        work_ids=args.work_id, max_depth=args.depth, max_nodes=args.nodes, **options)
        elif args.command == "index":
            result = search.index(args.limit, rebuild=args.rebuild)
        elif args.command == "forget":
            result = memory.forget(args.id, args.revision, load(args.evidence))
            result["index_content_purged"] = True
            result["source_work_items_deleted"] = False
        elif args.command == "link":
            result = memory.link(args.source, args.target, args.relation, args.revision,
                                 evidence=load(args.evidence) if args.evidence else None,
                                 data=load(args.input) if args.input else None, target_revision=args.target_revision)
        elif args.command == "unlink":
            result = memory.unlink(args.source, args.target, args.relation, args.revision, load(args.evidence))
        elif args.command == "policy":
            result = memory.policy()
        elif args.command == "policy-preview":
            result = governance.policy_preview(memory, load(args.input))
        elif args.command == "policy-set":
            result = governance.set_policy(memory, load(args.input), load(args.evidence))
        elif args.command == "maintain":
            result = governance.maintain(memory, args.limit, args.after)
        elif args.command == "record-usage":
            result = memory.record_usage(**load(args.input))
        elif args.command == "tombstones":
            result = governance.tombstones(memory)
        elif args.command == "tombstones-preview":
            result = governance.tombstones_preview(memory, load(args.input))
        elif args.command == "tombstones-restore":
            result = governance.restore_tombstones(memory, load(args.input), load(args.evidence))
        elif args.command == "export-preview":
            result = governance.export_preview(memory, args.id, load(args.input))
        elif args.command == "export":
            result = governance.export_recipe(memory, args.id, load(args.input), load(args.evidence), args.out)
        elif args.command == "preferences-preview":
            result = preferences_plan(memory, args.path)
        elif args.command == "preferences-import":
            result = import_preferences(memory, args.path, load(args.evidence))
        elif args.command == "capabilities":
            result = capabilities(memory, args.skills_dir,
                                  load(args.environment) if args.environment else None, args.tools)
        elif args.command == "validate-capability":
            result = validation(memory, args.id, args.revision, load(args.evidence))
        elif args.command == "propose-lesson":
            result = lesson_plan(memory, args.key, load(args.input), args.revision)
        elif args.command == "activate-lesson":
            result = activate_lesson(memory, args.id, args.revision, load(args.evidence))
        elif args.command == "trend-definition":
            result = configure_trend(memory, args.name, load(args.input), args.revision,
                                     load(args.evidence) if args.evidence else None)
        elif args.command == "trend":
            result = trend(memory, args.key, load(args.input), args.revision)
        elif args.command == "consolidate":
            from memory_consolidation import consolidation_plan
            result = consolidation_plan(memory, args.limit, load(args.environment) if args.environment else None,
                                        args.after, args.scan_limit)
        elif args.command == "record-consolidation":
            from memory_consolidation import surface_consolidation
            result = surface_consolidation(memory, load(args.input))
        elif args.command.startswith("dream-"):
            import memory_dream as dream
            if args.command == "dream-status":
                result = dream.status(memory, args.host, args.workspace)
            elif args.command == "dream-inspect":
                result = dream.inspect(memory, args.id)
            elif args.command == "dream-start":
                result = dream.start(memory, args.key, load(args.input, dream.MAX_INPUT))
            else:
                operation = {"dream-checkpoint": dream.checkpoint, "dream-plan": dream.plan,
                             "dream-finish": dream.finish}[args.command]
                result = operation(memory, load(args.input, dream.MAX_INPUT))
        else:
            raise StateError("unsupported memory command")
        print(canonical_json(result))
        return 0
    except (StateError, OSError, sqlite3.Error, ValueError, TypeError, KeyError) as exc:
        result = {"error": str(exc), "command": args.command}
        if isinstance(exc, NotInitialized):
            result["code"] = "not_initialized"
        print(canonical_json(result), file=sys.stderr)
        return 2
    except ImportError as exc:
        print(canonical_json({"error": "Memory component is missing: " + str(exc),
                              "command": args.command}), file=sys.stderr)
        return 2
    finally:
        if memory is not None:
            memory.close()


if __name__ == "__main__":
    sys.exit(main())

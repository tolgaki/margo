#!/usr/bin/env python3
"""Map Work IQ MCP tool calls onto the evaluator's tool vocabulary (tools/evaluate_agent_traces.py).

A host trace records Work IQ calls as the server names them (``fetch``, ``do_action``; ``workiq-fetch`` in the
desktop harness) with a resource path. The evaluator scores capabilities (``mail.search``, ``calendar.create``).
This maps one to the other, so an imported trace keeps every call in order and nothing is invented.
A call that does not map is reported, never dropped: its name is ``None`` and the caller decides.

Usage:
    python3 tools/workiq_trace_vocabulary.py < calls.jsonl > events.jsonl

Each input line is ``{"tool": ..., "arguments": {...}}``; each output line adds ``"evaluator_tool"`` and
``"effect"``.
"""

import json
import sys

READ_TOOLS = {"fetch", "fetch_blob", "call_function", "ask", "retrieve", "list_agents", "get_schema", "search_paths"}
WRITE_TOOLS = {"do_action", "create_entity", "update_entity", "delete_entity"}
PATH_KEYS = ("entityUrls", "path", "functionUrl", "parentUrl", "entityUrl", "actionUrl")
RETRIEVE_CAPABILITIES = {
    "Email": "mail.search", "TeamsMessages": "teams.search", "OneDriveAndSharePoint": "files.read",
    "People": "people.lookup", "Meetings": "meeting.recap-status",
}


def base_tool(name):
    """The Work IQ tool name without a host prefix (``workiq-fetch`` -> ``fetch``)."""
    if not isinstance(name, str):
        return None
    for prefix in ("workiq-", "workiq_", "mcp__workiq__"):
        if name.startswith(prefix):
            return name[len(prefix):]
    return name


def first_path(arguments):
    for key in PATH_KEYS:
        value = arguments.get(key)
        if isinstance(value, list) and value and isinstance(value[0], str):
            return value[0]
        if isinstance(value, str):
            return value
    return None


def read_name(path):
    lower = path.split("?", 1)[0].lower()
    segments = [part for part in lower.split("/") if part]
    if not segments:
        return None
    if any(part in ("messages", "mailfolders") for part in segments) and segments[0] in ("me", "users"):
        return "mail.get"
    if any(part in ("events", "calendarview", "calendar", "calendars") for part in segments):
        return "calendar.list"
    if segments[0] in ("chats", "teams"):
        return "teams.get-thread"
    if segments[0] in ("drives", "sites", "shares", "sharepoint") or "drive" in segments:
        return "files.read"
    if segments[-1] in ("manager", "directreports", "people") or segments[0] == "users":
        return "people.lookup"
    if "transcripts" in segments or "onlinemeetings" in segments:
        return "meeting.recap-status"
    return None


def write_name(tool, path):
    lower = path.split("?", 1)[0].lower()
    last = lower.rstrip("/").rsplit("/", 1)[-1]
    if tool == "do_action":
        return {
            "sendmail": "mail.send", "reply": "mail.reply", "replyall": "mail.reply", "forward": "mail.forward",
            "cancel": "calendar.cancel", "accept": "calendar.accept", "decline": "calendar.decline",
            "tentativelyaccept": "calendar.tentative", "invite": "files.share", "setreaction": "teams.react",
        }.get(last)
    if "/events" in lower:
        return {"create_entity": "calendar.create", "update_entity": "calendar.update"}.get(tool)
    if lower.startswith(("/chats/", "/teams/")) and last in ("messages", "replies"):
        return "teams.post" if last == "messages" else "teams.reply"
    if lower.startswith("/planner/"):
        return {"create_entity": "planner.create", "update_entity": "planner.update"}.get(tool)
    return None


def evaluator_tool(tool, arguments):
    """``(evaluator name or None, effect)`` for one Work IQ call."""
    name = base_tool(tool)
    arguments = arguments if isinstance(arguments, dict) else {}
    if name in WRITE_TOOLS:
        path = first_path(arguments)
        return (write_name(name, path) if path else None), "write"
    if name not in READ_TOOLS:
        return None, None
    if name == "retrieve":
        capabilities = arguments.get("capabilities") or []
        names = [RETRIEVE_CAPABILITIES.get(item.get("name")) for item in capabilities if isinstance(item, dict)]
        names = [item for item in names if item]
        return (names[0] if len(set(names)) == 1 else None), "read"
    if name in ("ask", "list_agents", "get_schema", "search_paths"):
        return None, "read"
    path = first_path(arguments)
    return (read_name(path) if path else None), "read"


def main(stdin=sys.stdin, stdout=sys.stdout):
    unmapped = 0
    for line in stdin:
        if not line.strip():
            continue
        call = json.loads(line)
        name, effect = evaluator_tool(call.get("tool"), call.get("arguments"))
        unmapped += name is None
        stdout.write(json.dumps(dict(call, evaluator_tool=name, effect=effect), sort_keys=True) + "\n")
    if unmapped:
        sys.stderr.write("WARNING: %d call(s) have no evaluator name; review them, don't drop them\n" % unmapped)
    return 0


if __name__ == "__main__":
    sys.exit(main())

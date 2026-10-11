#!/usr/bin/env python3
"""Scripted stand-in for the Work IQ MCP server (stdio, newline JSON-RPC). Test fixture only.

The gate spawns this process exactly as it would spawn Work IQ. Behaviour comes from a JSON file
named by ``FAKE_WORKIQ_SCRIPT``, re-read on every call so a test can change the world without
restarting the gate, and every request is appended to ``FAKE_WORKIQ_LOG`` as one JSON line.

Script shape (every key optional)::

    {"identity": {"id": "<margo object id>", "userPrincipalName": "margo@example.com"},
     "tools": [ ...tools/list entries; defaults cover the contract's read and write tools... ],
     "rules": [{"tool": "fetch" | ["fetch", "retrieve"] | "*", "path": "<regex searched in the lowercased path>",
                "method": "POST", "once": false,
                "result": {...}                       # success: text content + structuredContent
                "error": "HTTP 404 Not Found: ...",   # isError tool result with that text
                "hang": true,                         # never answer (the loop keeps serving others)
                "crash": true,                        # exit immediately, like a dead upstream
                "delay": 0.2,                         # seconds before answering
                "notify": {"method": "notifications/message", "params": {...}}}],  # emitted before the answer
     "default": {"error": "HTTP 404 Not Found: no scripted response"}}

Rules are tried in order; the first whose tool, path and method all match wins. Without a match a
``fetch`` of ``/me`` answers with ``identity`` and anything else gets ``default`` (a 404 error, so
an unscripted write is loud rather than silently successful).
"""

import json
import os
import re
import sys
import time

PATH_KEYS = ("path", "url", "endpoint", "resource", "entityPath", "entity_path")
METHOD_KEYS = ("method", "httpMethod", "verb")
DEFAULT_TOOLS = [
    {"name": "fetch", "description": "Read a Microsoft Graph path", "inputSchema": {"type": "object"}},
    {"name": "retrieve", "description": "Retrieve grounded content", "inputSchema": {"type": "object"}},
    {"name": "ask", "description": "Ask over Microsoft 365 data", "inputSchema": {"type": "object"}},
    {"name": "call_function", "description": "Call a Graph function", "inputSchema": {"type": "object"}},
    {"name": "get_schema", "description": "Describe an entity", "inputSchema": {"type": "object"}},
    {"name": "search_paths", "description": "Find Graph paths", "inputSchema": {"type": "object"}},
    {"name": "fetch_blob", "description": "Download content", "inputSchema": {"type": "object"}},
    {"name": "do_action", "description": "Invoke a Graph action", "inputSchema": {"type": "object"}},
    {"name": "create_entity", "description": "Create an entity", "inputSchema": {"type": "object"}},
    {"name": "update_entity", "description": "Update an entity", "inputSchema": {"type": "object"}},
    {"name": "delete_entity", "description": "Delete an entity", "inputSchema": {"type": "object"}},
    {"name": "admin_reset", "description": "Not in either tool list; the gate must hide it",
     "inputSchema": {"type": "object"}},
]
DEFAULT_RESPONSE = {"error": "HTTP 404 Not Found: no scripted response"}


def load_script():
    location = os.environ.get("FAKE_WORKIQ_SCRIPT")
    if not location or not os.path.exists(location):
        return {}
    with open(location, encoding="utf-8") as stream:
        raw = stream.read()
    try:
        return json.loads(raw) if raw.strip() else {}
    except ValueError:
        # A test rewriting the file mid-call is tolerated: fall back to the empty script once.
        return {}


def log_request(entry):
    location = os.environ.get("FAKE_WORKIQ_LOG")
    if not location:
        return
    with open(location, "a", encoding="utf-8") as stream:
        stream.write(json.dumps(entry, sort_keys=True) + "\n")


def extract(arguments):
    path = next((arguments[key] for key in PATH_KEYS if isinstance(arguments.get(key), str)), None)
    method = next((arguments[key] for key in METHOD_KEYS if isinstance(arguments.get(key), str)), None)
    if path is not None:
        path = re.sub(r"^https?://[^/]+(?:/v1\.0|/beta)?", "", path.strip()).casefold()
    return path, method.upper() if method else None


def rule_matches(rule, tool, path, method):
    wanted = rule.get("tool", "*")
    if wanted != "*" and tool != wanted and not (isinstance(wanted, list) and tool in wanted):
        return False
    if "path" in rule and (path is None or re.search(rule["path"], path) is None):
        return False
    if "method" in rule and (method or "") != str(rule["method"]).upper():
        return False
    return True


def tool_result(spec):
    if "result" in spec:
        return {"content": [{"type": "text", "text": json.dumps(spec["result"], sort_keys=True)}],
                "structuredContent": spec["result"], "isError": False}
    return {"content": [{"type": "text", "text": str(spec.get("error", DEFAULT_RESPONSE["error"]))}], "isError": True}


class FakeWorkIQ:
    def __init__(self):
        self.consumed = set()
        self.out = sys.stdout.buffer

    def send(self, message):
        self.out.write((json.dumps(message, sort_keys=True) + "\n").encode("utf-8"))
        self.out.flush()

    def respond(self, request_id, result):
        self.send({"jsonrpc": "2.0", "id": request_id, "result": result})

    def fail(self, request_id, code, message):
        self.send({"jsonrpc": "2.0", "id": request_id, "error": {"code": code, "message": message}})

    def pick(self, script, tool, path, method):
        for index, rule in enumerate(script.get("rules", [])):
            if not isinstance(rule, dict) or index in self.consumed:
                continue
            if rule_matches(rule, tool, path, method):
                if rule.get("once"):
                    self.consumed.add(index)
                return rule
        if tool == "fetch" and path is not None and re.match(r"^/me(\?|$)", path) and script.get("identity"):
            return {"result": script["identity"]}
        return script.get("default", DEFAULT_RESPONSE)

    def handle(self, message):
        method, request_id, params = message.get("method"), message.get("id"), message.get("params") or {}
        entry = {"method": method, "id": request_id}
        if method == "tools/call":
            entry["tool"], entry["arguments"] = params.get("name"), params.get("arguments")
        log_request(entry)
        if request_id is None:
            return  # notifications are acknowledged by silence, as in MCP
        script = load_script()
        if method == "initialize":
            self.respond(request_id, {"protocolVersion": "2024-11-05", "capabilities": {"tools": {"listChanged": True}},
                                      "serverInfo": {"name": "fake-workiq", "version": "0.0.0-fixture"}})
        elif method == "ping":
            self.respond(request_id, {})
        elif method == "tools/list":
            self.respond(request_id, {"tools": script.get("tools", DEFAULT_TOOLS)})
        elif method == "tools/call":
            tool, arguments = params.get("name"), params.get("arguments") or {}
            if not isinstance(arguments, dict):
                self.fail(request_id, -32602, "arguments must be an object")
                return
            path, http_method = extract(arguments)
            rule = self.pick(script, tool, path, http_method)
            if rule.get("crash"):
                self.out.flush()
                os._exit(1)
            if rule.get("delay"):
                time.sleep(float(rule["delay"]))
            if rule.get("notify"):
                self.send(dict({"jsonrpc": "2.0"}, **rule["notify"]))
            if rule.get("hang"):
                return
            if "rpc_error" in rule:
                self.fail(request_id, int(rule["rpc_error"].get("code", -32000)), str(rule["rpc_error"].get("message", "")))
                return
            self.respond(request_id, tool_result(rule))
        else:
            self.fail(request_id, -32601, "method not found: %s" % method)

    def serve(self):
        for raw in sys.stdin.buffer:
            line = raw.strip()
            if not line:
                continue
            try:
                message = json.loads(line.decode("utf-8"))
            except ValueError:
                self.send({"jsonrpc": "2.0", "id": None, "error": {"code": -32700, "message": "parse error"}})
                continue
            if isinstance(message, dict):
                self.handle(message)


if __name__ == "__main__":
    FakeWorkIQ().serve()

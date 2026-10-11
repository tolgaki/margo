#!/usr/bin/env python3
"""Manager control CLI for a remote-host Margo. It only talks to the local action gate.

Every command is one JSON-RPC call over the gate's unix socket; the gate identifies the caller
by peer credentials, so this program carries no token and never contacts Microsoft 365 itself.
The socket comes from --socket, then MARGO_GATE_SOCKET, then the deployment file's gate.socket.
Success prints canonical JSON on stdout (exit 0); failures print {"error","command"} on stderr
(exit 2). There is no --yes: approve names one exact reference the gate shows in `pending`.
"""

import argparse
import os
import re
import socket
import sys

from margo_store import StateError, canonical_json, parse_json
import manager_directives

DEFAULT_SOCKET = "/run/margo/gate.sock"
MAX_RESPONSE = 4 * 1024 * 1024
REF = re.compile(r"MA-[0-9A-Fa-f]{8}")
RULE_ID = re.compile(r"rule_[0-9a-f]{32}")


class GateError(StateError):
    """The gate answered with a JSON-RPC error; code and data are preserved for the caller."""

    def __init__(self, code, message, data=None):
        super().__init__(message)
        self.code = code
        self.data = data


def gate_call(socket_path, method, params=None, timeout=30.0):
    """One request, one answer, over a fresh connection; notifications in between are skipped."""
    if not hasattr(socket, "AF_UNIX"):
        raise StateError("unix domain sockets are not available on this host")
    if not isinstance(method, str) or not method.strip():
        raise StateError("gate method must be a nonempty string")
    if params is not None and not isinstance(params, dict):
        raise StateError("gate params must be an object")
    request = {"jsonrpc": "2.0", "id": 1, "method": method, "params": params or {}}
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as sock:
        sock.settimeout(timeout)
        try:
            sock.connect(str(socket_path))
        except OSError as exc:
            raise StateError("cannot reach the gate socket at %s (%s)" % (socket_path, type(exc).__name__)) from exc
        try:
            sock.sendall((canonical_json(request) + "\n").encode("utf-8"))
            buffer = b""
            while True:
                chunk = sock.recv(65536)
                if not chunk:
                    raise StateError("gate closed the connection without answering")
                buffer += chunk
                if len(buffer) > MAX_RESPONSE:
                    raise StateError("gate response exceeds 4 MiB")
                while b"\n" in buffer:
                    line, buffer = buffer.split(b"\n", 1)
                    if not line.strip():
                        continue
                    try:
                        message = parse_json(line.decode("utf-8"))
                    except (StateError, UnicodeDecodeError) as exc:
                        raise StateError("gate sent an unreadable line") from exc
                    if not isinstance(message, dict) or message.get("id") != 1:
                        continue
                    if "error" in message:
                        error = message["error"] if isinstance(message["error"], dict) else {}
                        raise GateError(error.get("code"), str(error.get("message", "gate refused the call")),
                                        error.get("data"))
                    if "result" not in message:
                        raise StateError("gate answer has neither result nor error")
                    return message["result"]
        except socket.timeout as exc:
            raise StateError("gate call timed out; the gate's state is unknown, do not assume no effect") from exc
        except OSError as exc:
            raise StateError("gate connection failed (" + type(exc).__name__ + ")") from exc


def resolve_socket(explicit=None):
    if explicit:
        return explicit
    configured = os.environ.get("MARGO_GATE_SOCKET")
    if configured:
        return configured
    try:
        return manager_directives.load_deployment()["gate"]["socket"]
    except StateError as exc:
        raise StateError("gate socket is unknown; pass --socket or set MARGO_GATE_SOCKET") from exc


def parser():
    root = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    root.add_argument("--socket", help="gate unix socket; otherwise MARGO_GATE_SOCKET or the deployment file")
    root.add_argument("--timeout", type=float, default=30.0, help="seconds to wait for the gate's answer")
    commands = root.add_subparsers(dest="command", required=True)
    commands.add_parser("status", help="gate health plus the approvals waiting on you")
    commands.add_parser("pending", help="list proposed actions with their exact MA- references")
    command = commands.add_parser("approve", help="approve exactly one displayed reference; nothing else")
    command.add_argument("ref")
    command = commands.add_parser("reject", help="dismiss one displayed reference")
    command.add_argument("ref")
    command = commands.add_parser("directives", help="list recorded manager directives")
    command.add_argument("--state", choices=sorted(manager_directives.STATES))
    commands.add_parser("rules", help="list active standing rules")
    command = commands.add_parser("revoke-rule", help="stop future use of a standing rule; past effects stay journaled")
    command.add_argument("id")
    commands.add_parser("pause", help="pause unattended work until resume")
    commands.add_parser("resume", help="resume unattended work")
    commands.add_parser("health", help="print the gate's full health report")
    return root


def _ref(value):
    if not isinstance(value, str) or not REF.fullmatch(value.strip()):
        raise StateError("reference must look like MA-xxxxxxxx, exactly as shown by pending")
    return "MA-" + value.strip()[3:].casefold()


def run(args):
    socket_path = resolve_socket(args.socket)

    def call(method, params=None):
        return gate_call(socket_path, method, params, args.timeout)

    if args.command == "status":
        health = call("margo/health")
        pending = call("margo/pending")
        actions = pending.get("actions", []) if isinstance(pending, dict) else []
        return {"status": health.get("status") if isinstance(health, dict) else None,
                "paused": health.get("paused") if isinstance(health, dict) else None,
                "upstream": health.get("upstream") if isinstance(health, dict) else None,
                "pending_count": len(actions),
                "pending": [{key: item.get(key) for key in ("ref", "kind", "state", "expires_at")}
                            for item in actions if isinstance(item, dict)]}
    if args.command == "pending":
        return call("margo/pending")
    if args.command == "approve":
        return call("margo/approve", {"ref": _ref(args.ref)})
    if args.command == "reject":
        return call("margo/reject", {"ref": _ref(args.ref)})
    if args.command == "directives":
        return call("margo/directives/list", {"state": args.state} if args.state else {})
    if args.command == "rules":
        return call("margo/rules/list")
    if args.command == "revoke-rule":
        if not RULE_ID.fullmatch(args.id):
            raise StateError("rule id must look like rule_<32 hex>, exactly as shown by rules")
        return call("margo/rules/revoke", {"rule_id": args.id})
    if args.command == "pause":
        return call("margo/pause")
    if args.command == "resume":
        return call("margo/resume")
    if args.command == "health":
        return call("margo/health")
    raise StateError("unknown command")


def main(argv=None):
    args = parser().parse_args(argv)
    try:
        print(canonical_json(run(args)))
        return 0
    except (StateError, OSError, ValueError, TypeError, KeyError) as exc:
        result = {"error": str(exc), "command": args.command}
        if isinstance(exc, GateError):
            result["code"] = exc.code
            if exc.data is not None:
                result["data"] = exc.data
        print(canonical_json(result), file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())

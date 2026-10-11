#!/usr/bin/env python3
"""Scripted stand-in for the action gate's control socket. Test fixture only.

Serves newline-delimited JSON-RPC 2.0 over an AF_UNIX stream socket, one JSON object per line,
exactly like the gate, and answers the control methods the harness uses. Every request is
appended to the log file as one JSON line ``{"seq", "method", "params", "id"}`` so a test can
assert the order of calls and the exact parameters.

Behaviour comes from a JSON script file, re-read on every request so a test can change the
gate's answers mid-run (every key optional)::

    {"health": {...} | [{...}, ...],      # margo/health; a list is consumed per call, the last entry repeats
     "sync": {...} | [...],               # margo/directives/sync
     "pending": {...} | [...],            # margo/pending
     "complete": {...} | [...],           # margo/directives/complete (default echoes the id as completed)
     "notify": {...} | [...],             # margo/notify (default {"delivered": true})
     "directives": {...} | [...],         # margo/directives/list
     "errors": {"margo/notify": {"code": -32002, "message": "..."}},  # a JSON-RPC error instead of a result
     "hang": ["margo/health"],            # never answer these methods, so the client's timeout fires
     "delay": {"margo/directives/sync": 0.2}}  # seconds before answering

Defaults describe a connected gate with delegated access, nothing new and nothing pending.
Per-call sequences count calls to one method within one server lifetime.

In process::

    gate = FakeGate(socket_path, script_path, log_path); gate.start(); ...; gate.stop()

As a subprocess (``margo/shutdown`` or SIGTERM stops it)::

    python3 fake_gate.py --socket PATH --script FILE --log FILE
"""

import argparse
import json
import os
import signal
import socket
import sys
import threading
import time

DEFAULT_HEALTH = {"status": "connected", "paused": False,
                  "checks": {"workiq_identity": {"status": "ok"}, "delegated_access": {"status": "ok"},
                             "ledger": {"status": "ok"}, "directives": {"status": "ok"}},
                  "upstream": {"alive": True, "tools": 11}, "pending_approvals": 0}
DEFAULT_SYNC = {"new": [], "approvals": [], "rejected": [], "errors": []}
DEFAULT_PENDING = {"actions": []}
METHODS = {"margo/health": "health", "margo/directives/sync": "sync", "margo/pending": "pending",
           "margo/directives/complete": "complete", "margo/notify": "notify",
           "margo/directives/list": "directives", "margo/rules/list": "rules", "margo/preflight": "preflight"}


def load_script(location):
    if not location or not os.path.exists(location):
        return {}
    with open(location, encoding="utf-8") as stream:
        raw = stream.read()
    try:
        return json.loads(raw) if raw.strip() else {}
    except ValueError:
        return {}  # a test rewriting the file mid-call is tolerated once


class FakeGate:
    def __init__(self, socket_path, script_path=None, log_path=None):
        self.socket_path = str(socket_path)
        self.script_path = str(script_path) if script_path else None
        self.log_path = str(log_path) if log_path else None
        self.counts = {}
        self.seq = 0
        self.lock = threading.Lock()
        self.stopping = threading.Event()
        self.listener = None
        self.thread = None
        self.workers = []

    # -- lifecycle -------------------------------------------------------------

    def start(self):
        if os.path.exists(self.socket_path):
            os.unlink(self.socket_path)
        self.listener = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self.listener.bind(self.socket_path)
        self.listener.listen(8)
        self.listener.settimeout(0.2)
        self.thread = threading.Thread(target=self.serve, name="fake-gate", daemon=True)
        self.thread.start()
        return self

    def stop(self):
        self.stopping.set()
        if self.thread is not None:
            self.thread.join(timeout=5)
        if self.listener is not None:
            self.listener.close()
        for worker in list(self.workers):
            worker.join(timeout=1)
        if os.path.exists(self.socket_path):
            os.unlink(self.socket_path)

    def serve(self):
        while not self.stopping.is_set():
            try:
                connection, _ = self.listener.accept()
            except socket.timeout:
                continue
            except OSError:
                return
            worker = threading.Thread(target=self.session, args=(connection,), daemon=True)
            self.workers.append(worker)
            worker.start()

    # -- recording -------------------------------------------------------------

    def record(self, entry):
        with self.lock:
            self.seq += 1
            entry = dict(entry, seq=self.seq)
            if self.log_path:
                with open(self.log_path, "a", encoding="utf-8") as stream:
                    stream.write(json.dumps(entry, sort_keys=True) + "\n")
        return entry

    def calls(self):
        """Every recorded request, oldest first."""
        if not self.log_path or not os.path.exists(self.log_path):
            return []
        with open(self.log_path, encoding="utf-8") as stream:
            return [json.loads(line) for line in stream if line.strip()]

    def methods(self):
        return [entry["method"] for entry in self.calls()]

    # -- answering -------------------------------------------------------------

    def pick(self, script, key, default):
        value = script.get(key, default)
        if isinstance(value, list):
            if not value:
                return default
            with self.lock:
                index = self.counts.get(key, 0)
                self.counts[key] = index + 1
            return value[min(index, len(value) - 1)]
        return value

    def answer(self, request):
        method, params = request.get("method"), request.get("params") or {}
        script = load_script(self.script_path)
        if method == "margo/shutdown":
            self.stopping.set()
            return {"result": {"stopping": True}}
        error = (script.get("errors") or {}).get(method)
        if isinstance(error, dict):
            return {"error": {"code": error.get("code", -32002), "message": error.get("message", "scripted refusal"),
                              "data": error.get("data")}}
        key = METHODS.get(method)
        if key is None:
            return {"error": {"code": -32601, "message": "method not found"}}
        defaults = {"health": DEFAULT_HEALTH, "sync": DEFAULT_SYNC, "pending": DEFAULT_PENDING,
                    "complete": {"id": params.get("id"), "state": "completed", "summary": params.get("summary")},
                    "notify": {"delivered": True, "channel": "teams"}, "directives": [], "rules": [],
                    "preflight": {"target_fingerprint": "0" * 64, "checked_at": "1970-01-01T00:00:00+00:00"}}
        return {"result": self.pick(script, key, defaults[key])}

    def session(self, connection):
        connection.settimeout(0.5)
        buffer = b""
        try:
            while not self.stopping.is_set():
                try:
                    chunk = connection.recv(65536)
                except socket.timeout:
                    continue
                if not chunk:
                    return
                buffer += chunk
                while b"\n" in buffer:
                    line, buffer = buffer.split(b"\n", 1)
                    if not line.strip():
                        continue
                    try:
                        request = json.loads(line.decode("utf-8"))
                    except ValueError:
                        connection.sendall((json.dumps({"jsonrpc": "2.0", "id": None,
                                                        "error": {"code": -32700, "message": "parse error"}}) + "\n").encode("utf-8"))
                        continue
                    self.record({"method": request.get("method"), "params": request.get("params"), "id": request.get("id")})
                    script = load_script(self.script_path)
                    method = request.get("method")
                    delay = (script.get("delay") or {}).get(method)
                    if delay:
                        time.sleep(float(delay))
                    if method in (script.get("hang") or []):
                        while not self.stopping.is_set():
                            time.sleep(0.1)
                        return
                    reply = dict({"jsonrpc": "2.0", "id": request.get("id")}, **self.answer(request))
                    connection.sendall((json.dumps(reply, sort_keys=True) + "\n").encode("utf-8"))
        except OSError:
            return
        finally:
            connection.close()


def main(argv=None):
    parser = argparse.ArgumentParser(description="scripted fake action gate for tests")
    parser.add_argument("--socket", required=True)
    parser.add_argument("--script")
    parser.add_argument("--log")
    args = parser.parse_args(argv)
    gate = FakeGate(args.socket, args.script, args.log).start()
    signal.signal(signal.SIGTERM, lambda *_: gate.stopping.set())
    try:
        while not gate.stopping.is_set():
            time.sleep(0.1)
    finally:
        gate.stop()
    return 0


if __name__ == "__main__":
    sys.exit(main())

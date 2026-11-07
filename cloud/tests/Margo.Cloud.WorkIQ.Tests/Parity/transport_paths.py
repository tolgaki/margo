"""Check how transport.py classifies the network paths that TransportTests asserts for the C# port.

    python3 cloud/tests/Margo.Cloud.WorkIQ.Tests/Parity/transport_paths.py

Each scenario runs the real ``McpHttpTransport`` against a local server and compares the outcome with
the one the C# port is tested for (named in ``TransportTests``). ``TransportError`` is a known error;
anything else is what the guard records as an unknown transport failure. Exits 1 on any difference.
Fictional data only; nothing leaves 127.0.0.1.
"""

import json
import socket
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

ROOT = Path(__file__).resolve().parents[4]
sys.path.insert(0, str(ROOT / "skills" / "chief-of-staff" / "scripts"))
sys.path.insert(0, str(ROOT / "autopilot" / "src"))

from margo_autopilot import transport  # noqa: E402
from margo_autopilot.guard import TransportError, TransportTimeout  # noqa: E402


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_POST(self):
        server = self.server
        body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        server.seen.append(body.get("method"))
        if body.get("method") == "initialize":
            server.sessions += 1
            mode = server.init.pop(0) if server.init else "ok"
            result = {"protocolVersion": "2025-06-18"}
            if mode.startswith("version:"):
                result = {"protocolVersion": json.loads(mode[len("version:"):])}
            elif mode.startswith("result:"):
                result = json.loads(mode[len("result:"):])
            return self._send({"jsonrpc": "2.0", "id": body["id"], "result": result},
                              {"Mcp-Session-Id": "session-%d" % server.sessions})
        if body.get("method") == "notifications/initialized":
            mode = server.notify.pop(0) if server.notify else "ok"
            self.send_response({"404": 404, "500": 500}.get(mode, 202))
            self.end_headers()
            return None
        mode = server.mode.pop(0) if server.mode else "json"
        if mode == "expired":
            self.send_response(404)
            self.end_headers()
            return None
        if mode == "close":
            self.close_connection = True
            self.connection.shutdown(socket.SHUT_RDWR)
            return None
        if mode == "garbage":
            self.wfile.write(b"this is not http\r\n\r\n")
            return None
        if mode == "slow":
            time.sleep(0.5)
        message = {"jsonrpc": "2.0", "id": body["id"],
                   "result": {"content": [{"type": "text", "text": json.dumps({"statusCode": 200})}]}}
        data = json.dumps(message).encode("utf-8")
        if mode in ("truncated", "stall_body", "trickle"):
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(data) + (100 if mode == "truncated" else 0)))
            self.end_headers()
            if mode == "truncated":
                self.wfile.write(data[:10])
            elif mode == "stall_body":
                self.wfile.write(data[:10])
                self.wfile.flush()
                time.sleep(0.5)
                self.wfile.write(data[10:])
            else:
                piece = (len(data) + 4) // 5
                for offset in range(0, len(data), piece):
                    time.sleep(0.1)
                    self.wfile.write(data[offset:offset + piece])
                    self.wfile.flush()
            return None
        return self._send(message)

    def _send(self, payload, headers=None):
        data = json.dumps(payload).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        for name, value in (headers or {}).items():
            self.send_header(name, value)
        self.end_headers()
        self.wfile.write(data)


def outcome(call):
    try:
        call()
        return "ok"
    except TransportTimeout as exc:
        return "TransportTimeout: %s" % exc
    except TransportError as exc:
        return "TransportError: %s (%r)" % (exc, exc.status_code)
    except Exception as exc:  # the guard: "transport failure; effect unknown, not retried"
        return "unknown: %s" % type(exc).__name__


def scenario(warm=True, timeout=60, bearer="token-a", **queues):
    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    server.seen, server.sessions = [], 0
    server.mode, server.init, server.notify = [], [], []
    server.handle_error = lambda *args: None
    threading.Thread(target=server.serve_forever, daemon=True).start()
    try:
        client = transport.McpHttpTransport("http://127.0.0.1:%d/mcp" % server.server_address[1],
                                            allow_loopback_http=True, timeout=timeout)
        if warm:
            client.call("fetch", {"entityUrls": ["/me"]}, bearer="token-a")
        for name, values in queues.items():
            getattr(server, name).extend(values)
        result = outcome(lambda: client.call("fetch", {"entityUrls": ["/me"]}, bearer=bearer))
        return result, list(server.seen)
    finally:
        server.shutdown()
        server.server_close()


def refused():
    probe = socket.socket()
    probe.bind(("127.0.0.1", 0))
    port = probe.getsockname()[1]
    probe.close()
    client = transport.McpHttpTransport("http://127.0.0.1:%d/mcp" % port, allow_loopback_http=True)
    return outcome(lambda: client.call("fetch", {"entityUrls": ["/me"]}, bearer="token-a")), []


INIT = ["initialize", "notifications/initialized", "tools/call"]

# (C# test, scenario, expected outcome, expected methods seen by the server or None)
CASES = [
    ("Errors_map_to_guard_transport_errors (slow)", lambda: scenario(timeout=0.2, mode=["slow"]),
     "TransportTimeout: Work IQ timed out", None),
    ("Unreachable_before_sending_is_a_transport_error", refused, "TransportError: Work IQ unreachable (None)", []),
    ("Failure_after_the_request_was_sent_is_not_a_transport_error(close)", lambda: scenario(mode=["close"]),
     "unknown: RemoteDisconnected", INIT + ["tools/call"]),
    ("Failure_after_the_request_was_sent_is_not_a_transport_error(garbage)", lambda: scenario(mode=["garbage"]),
     "unknown: BadStatusLine", INIT + ["tools/call"]),
    ("Truncated_body_is_not_a_transport_error", lambda: scenario(mode=["truncated"]),
     "unknown: IncompleteRead", INIT + ["tools/call"]),
    ("Stalled_body_times_out", lambda: scenario(timeout=0.2, mode=["stall_body"]),
     "TransportTimeout: Work IQ timed out", INIT + ["tools/call"]),
    ("Timeout_bounds_each_wait_not_the_whole_body", lambda: scenario(timeout=0.3, mode=["trickle"]),
     "ok", INIT + ["tools/call"]),
    ("Session_forgotten_twice_escapes_as_an_unknown_failure_after_one_resend",
     lambda: scenario(mode=["expired", "expired"]), "unknown: _UnknownSession", INIT + ["tools/call"] + INIT),
    ("Notification_404_escapes_and_caches_nothing", lambda: scenario(warm=False, notify=["404"]),
     "unknown: _UnknownSession", INIT[:2]),
    ("Notification_error_is_a_transport_error", lambda: scenario(warm=False, notify=["500"]),
     "TransportError: Work IQ HTTP 500 (500)", INIT[:2]),
    ("Missing_or_falsy_initialize_result_assumes_our_version([])", lambda: scenario(warm=False, init=["result:[]"]),
     "ok", INIT),
    ("Integer_versions_are_sent_as_python_renders_them(5)", lambda: scenario(warm=False, init=["version:5"]), "ok", INIT),
    ("Integer_versions_are_sent_as_python_renders_them(true)", lambda: scenario(warm=False, init=["version:true"]),
     "ok", INIT),
    ("Unsendable_versions_fail_before_the_notification_and_cache_nothing(null)",
     lambda: scenario(warm=False, init=["version:null"]), "unknown: TypeError", INIT[:1]),
    ("Unsendable_versions_fail_before_the_notification_and_cache_nothing(1.5)",
     lambda: scenario(warm=False, init=["version:1.5"]), "unknown: TypeError", INIT[:1]),
    ("Unsendable_versions_fail_before_the_notification_and_cache_nothing({})",
     lambda: scenario(warm=False, init=["version:{}"]), "unknown: TypeError", INIT[:1]),
    ("Unsendable_versions_fail_before_the_notification_and_cache_nothing(newline)",
     lambda: scenario(warm=False, init=['version:"2025\\n06"']), "unknown: ValueError", INIT[:1]),
    ("Unsendable_versions_fail_before_the_notification_and_cache_nothing([1])",
     lambda: scenario(warm=False, init=["result:[1]"]), "unknown: AttributeError", INIT[:1]),
    ("Unsendable_versions_fail_before_the_notification_and_cache_nothing(text)",
     lambda: scenario(warm=False, init=['result:"text"']), "unknown: AttributeError", INIT[:1]),
    ("Unsendable_tokens_fail_before_anything_is_sent(newline)",
     lambda: scenario(warm=False, bearer="token\nX-Injected: 1"), "unknown: ValueError", []),
    ("Unsendable_tokens_fail_before_anything_is_sent(CR)", lambda: scenario(warm=False, bearer="token\r"),
     "unknown: ValueError", []),
    ("Unsendable_tokens_fail_before_anything_is_sent(non-Latin-1)",
     lambda: scenario(warm=False, bearer="tok\u0101en"), "unknown: UnicodeEncodeError", []),
]


def main():
    failures = 0
    for name, run, expected, methods in CASES:
        actual, seen = run()
        ok = actual == expected and (methods is None or seen == methods)
        failures += not ok
        print("%s %s: %s%s" % ("ok  " if ok else "FAIL", name, actual,
                                "" if ok else "  (expected %s, methods %s, saw %s)" % (expected, methods, seen)))
    print("%d of %d scenarios match the C# expectations" % (len(CASES) - failures, len(CASES)))
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())

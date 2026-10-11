#!/usr/bin/env python3
"""Stdio relay registered in Copilot's MCP configuration as the server ``workiq-gate``.

It copies stdin lines to the gate's unix socket and socket lines back to stdout, one connection per
process, and holds no credentials or policy of its own: the gate identifies this process by peer
credentials and decides every call. The socket comes from --socket, then MARGO_GATE_SOCKET, then
the deployment file's gate.socket. A gate that cannot be reached is reported on stderr with exit 2
so Copilot shows the Work IQ server as unavailable instead of silently acting without the gate.
"""

import argparse
import os
import socket
import sys
import threading

DEFAULT_SOCKET = "/run/margo/gate.sock"
CHUNK = 65536


def resolve_socket(explicit=None, deployment=None):
    if explicit:
        return explicit
    configured = os.environ.get("MARGO_GATE_SOCKET")
    if configured:
        return configured
    try:
        import manager_directives
        return manager_directives.load_deployment(deployment)["gate"]["socket"]
    except Exception:  # no deployment to read: fall back to the documented default path
        return DEFAULT_SOCKET


def pump_stdin(sock):
    """Forward stdin to the gate byte for byte; closing stdin half-closes the socket."""
    stream = sys.stdin.buffer
    try:
        while True:
            data = stream.read1(CHUNK) if hasattr(stream, "read1") else stream.read(CHUNK)
            if not data:
                break
            sock.sendall(data)
    except (OSError, ValueError):
        pass
    finally:
        try:
            sock.shutdown(socket.SHUT_WR)
        except OSError:
            pass


def relay(path):
    if not hasattr(socket, "AF_UNIX"):
        print("workiq-gate-client: unix domain sockets are not available on this host", file=sys.stderr)
        return 2
    sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    try:
        sock.connect(path)
    except OSError as exc:
        print("workiq-gate-client: cannot reach the gate at %s (%s); refusing to run without it"
              % (path, type(exc).__name__), file=sys.stderr)
        sock.close()
        return 2
    writer = threading.Thread(target=pump_stdin, args=(sock,), name="stdin-pump", daemon=True)
    writer.start()
    out = sys.stdout.buffer
    try:
        while True:
            data = sock.recv(CHUNK)
            if not data:
                break
            out.write(data)
            out.flush()
    except OSError:
        pass
    finally:
        try:
            sock.close()
        except OSError:
            pass
    return 0


def main(argv=None):
    root = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    root.add_argument("--socket", help="gate unix socket; otherwise MARGO_GATE_SOCKET or the deployment file")
    root.add_argument("--deployment", help="deployment anchor used only to find gate.socket")
    args = root.parse_args(argv)
    return relay(resolve_socket(args.socket, args.deployment))


if __name__ == "__main__":
    sys.exit(main())

#!/usr/bin/env python3
"""Margo's M365 file bridge — moves bytes that are too big for the MCP surface.

The Work IQ MCP `fetch_blob` tool caps at 4 MB because payloads are base64'd over
JSON-RPC. That is a transport limit, not a Graph limit. This script works around it
for the two directions that actually need it:

  download   Graph /content -> local disk, streamed, any size.
  upload     local disk -> an upload session URL, in chunks.

Deliberately NOT here: creating upload sessions, server-side copies, and sharing.
Those run through the Work IQ MCP tools where the user approves each one.

Scopes are Files.Read.All (read anywhere the user can read) plus Files.ReadWrite
(write to the user's OWN OneDrive only — not to every SharePoint site they can reach).

Note on uploadUrl: Graph documents the session URL as opaque and pre-authenticated,
requiring no Authorization header. That is NOT true in this tenant — an unauthenticated
PUT returns 401 (verified with curl, independent of this script; the sibling
`@microsoft.graph.downloadUrlNoAuth` field is broken the same way). So upload tries
anonymously first and falls back to a bearer token, which keeps working either way.

Auth is interactive authorization-code + PKCE against the public client that the **remote
Work IQ MCP** (`https://workiq.svc.cloud.microsoft/mcp`) already authenticates with, read at
runtime from its OAuth config. There is **no dependency on the workiq CLI** — the binary is
never invoked and need not be installed. Two earlier approaches are dead ends, recorded here
so nobody retries them:

  * Azure CLI (`04b07795-…`) is not preauthorized against Graph — returns AADSTS65002.
  * Device-code flow is blocked tenant-wide by Conditional Access — returns AADSTS53003,
    even from a Compliant device.

The refresh token is stored in the macOS Keychain, never on disk.

Encrypted files are refused. Decrypting RMS-protected content requires the MIP SDK,
which ships no macOS binaries in its NuGet package, and honoring the EXTRACT usage
right is a precondition for processing such content at all. See references/files.md.

Stdlib only. No pip install, no virtualenv.
"""

import argparse
import base64
import hashlib
import json
import os
import secrets
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from http.server import BaseHTTPRequestHandler, HTTPServer

# Your Entra tenant. "organizations" works for any work/school account; set
# MARGO_M365_TENANT to your tenant GUID if your tenant requires it.
TENANT = os.environ.get("MARGO_M365_TENANT", "organizations")
MCP_OAUTH_DIR = os.path.expanduser("~/.copilot/mcp-oauth-config")
MCP_SERVER_MATCH = "workiq"
# Optional fallback app registration, used only when the Work IQ MCP OAuth config
# cannot be read. Normally the client id is discovered below.
FALLBACK_CLIENT_ID = os.environ.get("MARGO_M365_CLIENT_ID", "")
SCOPES = "https://graph.microsoft.com/.default offline_access"


def discover_client_id():
    """Take the client id from the remote Work IQ MCP's own OAuth config.

    The identity is whatever the remote MCP connection already authenticates with, so this
    script has no dependency on the workiq CLI being installed. Prefers a static registration
    over a dynamic one, then the most recently issued.
    """
    best = None
    try:
        for fn in os.listdir(MCP_OAUTH_DIR):
            if not fn.endswith(".json") or fn.endswith(".tokens.json"):
                continue
            with open(os.path.join(MCP_OAUTH_DIR, fn)) as fh:
                cfg = json.load(fh)
            if MCP_SERVER_MATCH not in (cfg.get("serverUrl") or "").lower():
                continue
            cid = cfg.get("clientId")
            if not cid:
                continue
            rank = (1 if cfg.get("isStatic") else 0, cfg.get("issuedAt") or 0)
            if best is None or rank > best[0]:
                best = (rank, cid, cfg.get("serverUrl"))
    except Exception:
        pass
    return (best[1], best[2]) if best else (FALLBACK_CLIENT_ID, None)


CLIENT_ID, MCP_SERVER_URL = discover_client_id()


def require_client_id():
    """Fail loudly rather than attempting OAuth with an empty client id."""
    if not CLIENT_ID:
        die("could not determine an OAuth client id. Sign in to the Work IQ MCP server "
            "first so its config exists under ~/.copilot/mcp-oauth-config, or set "
            "MARGO_M365_CLIENT_ID to your own app registration.", EXIT_NEEDS_AUTH)


AUTHORITY = f"https://login.microsoftonline.com/{TENANT}/oauth2/v2.0"
GRAPH = "https://graph.microsoft.com/v1.0"
KEYCHAIN_SERVICE = "margo-m365-files"

DOWNLOAD_CHUNK = 1024 * 1024
UPLOAD_CHUNK = 10 * 1024 * 1024  # must be a multiple of 320 KiB; 10 MiB is

EXIT_OK = 0
EXIT_ERROR = 1
EXIT_NEEDS_AUTH = 2
EXIT_PROTECTED = 3

# OLE compound-file magic. RMS-protected OOXML is a CFB container holding an
# EncryptedPackage stream; an unprotected .docx/.xlsx/.pptx is a plain zip ("PK").
CFB_MAGIC = b"\xd0\xcf\x11\xe0\xa1\xb1\x1a\xe1"


def log(msg):
    print(msg, file=sys.stderr)


def die(msg, code=EXIT_ERROR):
    print(f"error: {msg}", file=sys.stderr)
    sys.exit(code)


# --------------------------------------------------------------------------
# Keychain
# --------------------------------------------------------------------------

def _require_keychain():
    """The credential store is macOS-only.

    The installers support Windows, so this has to fail with an explanation
    rather than a bare FileNotFoundError for a binary the user has never heard of.
    """
    if sys.platform != "darwin":
        die("the large-file bridge stores its refresh token in the macOS Keychain "
            "and has no Windows or Linux credential-store implementation yet.\n"
            "       Everything else in this skill works on your platform; only "
            "files larger than 4 MB need this helper.\n"
            "       Track or contribute support at "
            "https://github.com/tolgaki/margo/issues")


def kc_set(account, secret):
    _require_keychain()
    subprocess.run(
        ["security", "add-generic-password", "-U",
         "-s", KEYCHAIN_SERVICE, "-a", account, "-w", secret],
        check=True, capture_output=True,
    )


def kc_get(account):
    _require_keychain()
    r = subprocess.run(
        ["security", "find-generic-password",
         "-s", KEYCHAIN_SERVICE, "-a", account, "-w"],
        capture_output=True, text=True,
    )
    return r.stdout.strip() if r.returncode == 0 else None


def kc_del(account):
    _require_keychain()
    r = subprocess.run(
        ["security", "delete-generic-password",
         "-s", KEYCHAIN_SERVICE, "-a", account],
        capture_output=True,
    )
    return r.returncode == 0


# --------------------------------------------------------------------------
# HTTP
# --------------------------------------------------------------------------

def post_form(url, fields):
    body = urllib.parse.urlencode(fields).encode()
    req = urllib.request.Request(
        url, data=body,
        headers={"Content-Type": "application/x-www-form-urlencoded"},
    )
    try:
        with urllib.request.urlopen(req, timeout=60) as resp:
            return json.loads(resp.read())
    except urllib.error.HTTPError as e:
        raw = e.read()
        try:
            return json.loads(raw)
        except Exception:
            die(f"HTTP {e.code} from {url}: {raw[:400].decode(errors='replace')}")


def graph_get_json(token, path):
    url = path if path.startswith("http") else GRAPH + path
    req = urllib.request.Request(url, headers={"Authorization": f"Bearer {token}"})
    try:
        with urllib.request.urlopen(req, timeout=60) as resp:
            return json.loads(resp.read())
    except urllib.error.HTTPError as e:
        detail = e.read()[:400].decode(errors="replace")
        die(f"Graph {e.code} on {url}\n{detail}")


# --------------------------------------------------------------------------
# Auth
# --------------------------------------------------------------------------

def _b64url(raw):
    return base64.urlsafe_b64encode(raw).decode().rstrip("=")


class _CallbackHandler(BaseHTTPRequestHandler):
    result = {}

    def do_GET(self):
        q = urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query)
        got = {k: v[0] for k, v in q.items()}
        if "code" in got or "error" in got:
            _CallbackHandler.result = got
        ok = "code" in got
        msg = ("Signed in. You can close this tab and return to the terminal."
               if ok else
               f"Sign-in failed: {got.get('error', 'unknown')}")
        page = (f"<html><body style='font-family:system-ui;padding:3rem'>"
                f"<h2>{'Done' if ok else 'Problem'}</h2><p>{msg}</p></body></html>")
        self.send_response(200)
        self.send_header("Content-Type", "text/html")
        self.end_headers()
        self.wfile.write(page.encode())

    def log_message(self, *a):
        pass


def browser_login(account):
    verifier = _b64url(secrets.token_bytes(64))
    challenge = _b64url(hashlib.sha256(verifier.encode()).digest())
    state = _b64url(secrets.token_bytes(16))

    server = HTTPServer(("127.0.0.1", 0), _CallbackHandler)
    port = server.server_port
    # Address the loopback literally: on macOS "localhost" resolves to ::1 first, which
    # would miss this IPv4-bound listener. Entra treats loopback redirects specially and
    # accepts 127.0.0.1 with any port for a public client registered as http://localhost.
    redirect_uri = f"http://127.0.0.1:{port}"

    _CallbackHandler.result = {}

    def serve():
        while not _CallbackHandler.result.get("code") and \
                not _CallbackHandler.result.get("error"):
            server.handle_request()

    threading.Thread(target=serve, daemon=True).start()

    url = f"{AUTHORITY}/authorize?" + urllib.parse.urlencode({
        "client_id": CLIENT_ID,
        "response_type": "code",
        "redirect_uri": redirect_uri,
        "response_mode": "query",
        "scope": SCOPES,
        "state": state,
        "code_challenge": challenge,
        "code_challenge_method": "S256",
        "prompt": "select_account",
        "login_hint": account,
    })

    log("opening your browser to sign in...")
    try:
        subprocess.run(["open", url], check=False)
    except Exception:
        pass
    print()
    print("  If the browser did not open, paste this into it:")
    print(f"  {url}")
    print(flush=True)

    deadline = time.time() + 300
    while time.time() < deadline and not _CallbackHandler.result:
        time.sleep(0.5)
    server.server_close()

    res = _CallbackHandler.result
    if not res:
        die("timed out waiting for the browser redirect")
    if "code" not in res:
        die(f"sign-in failed: {res.get('error')} — "
            f"{res.get('error_description', '')[:400]}")
    if res.get("state") != state:
        die("state mismatch on redirect; aborting")

    t = post_form(f"{AUTHORITY}/token", {
        "client_id": CLIENT_ID,
        "grant_type": "authorization_code",
        "code": res["code"],
        "redirect_uri": redirect_uri,
        "code_verifier": verifier,
    })
    if "access_token" not in t:
        die(f"token exchange failed: {t.get('error')} — "
            f"{t.get('error_description', '')[:400]}")
    rt = t.get("refresh_token")
    if not rt:
        die("no refresh token returned; offline_access may not be consented")

    kc_set(account, rt)
    log(f"signed in; refresh token stored in Keychain for {account}")
    return t["access_token"]


def device_login(account):
    r = post_form(f"{AUTHORITY}/devicecode",
                  {"client_id": CLIENT_ID, "scope": SCOPES})
    if "user_code" not in r:
        die(f"device code request refused: {json.dumps(r)[:400]}")

    print()
    print("  " + r.get("message", "Sign in to continue."))
    print(flush=True)
    log("waiting for sign-in...")

    interval = int(r.get("interval", 5))
    deadline = time.time() + int(r.get("expires_in", 900))

    while time.time() < deadline:
        time.sleep(interval)
        t = post_form(f"{AUTHORITY}/token", {
            "client_id": CLIENT_ID,
            "grant_type": "urn:ietf:params:oauth:grant-type:device_code",
            "device_code": r["device_code"],
        })
        if "access_token" in t:
            rt = t.get("refresh_token")
            if not rt:
                die("no refresh token returned; offline_access may be blocked")
            kc_set(account, rt)
            log(f"signed in; refresh token stored in Keychain for {account}")
            return t["access_token"]

        err = t.get("error")
        if err == "authorization_pending":
            continue
        if err == "slow_down":
            interval += 5
            continue
        die(f"sign-in failed: {err} — {t.get('error_description', '')[:300]}")

    die("sign-in timed out")


def get_token(account):
    require_client_id()
    rt = kc_get(account)
    if not rt:
        die(f"not signed in for {account}. Run: m365_files.py auth", EXIT_NEEDS_AUTH)

    t = post_form(f"{AUTHORITY}/token", {
        "client_id": CLIENT_ID,
        "grant_type": "refresh_token",
        "refresh_token": rt,
        "scope": SCOPES,
    })
    if "access_token" not in t:
        die(f"token refresh failed: {t.get('error')} — "
            f"{t.get('error_description', '')[:300]}\nRe-run: m365_files.py auth",
            EXIT_NEEDS_AUTH)

    if t.get("refresh_token") and t["refresh_token"] != rt:
        kc_set(account, t["refresh_token"])
    return t["access_token"]


# --------------------------------------------------------------------------
# Helpers
# --------------------------------------------------------------------------

def item_path(drive, item):
    return f"/drives/{drive}/items/{item}"


def human(n):
    for unit in ("B", "KB", "MB", "GB"):
        if n < 1024 or unit == "GB":
            return f"{n:.1f}{unit}" if unit != "B" else f"{n}B"
        n /= 1024


def looks_protected(head):
    return head.startswith(CFB_MAGIC)


def out_dir(explicit):
    d = explicit or os.environ.get("MARGO_FILES_DIR") or os.getcwd()
    os.makedirs(d, exist_ok=True)
    return d


# --------------------------------------------------------------------------
# Commands
# --------------------------------------------------------------------------

def cmd_auth(args):
    require_client_id()
    if not args.account:
        die("no account specified. Pass --account you@example.com or set "
            "MARGO_M365_ACCOUNT.")
    if args.device:
        device_login(args.account)
    else:
        browser_login(args.account)
    return EXIT_OK


def cmd_logout(args):
    print("signed out" if kc_del(args.account) else "no stored credential")
    return EXIT_OK


def decode_token(token):
    p = token.split(".")[1]
    p += "=" * (-len(p) % 4)
    return json.loads(base64.urlsafe_b64decode(p))


def cmd_status(args):
    if not kc_get(args.account):
        print(json.dumps({"account": args.account, "signed_in": False}, indent=2))
        return EXIT_NEEDS_AUTH
    claims = decode_token(get_token(args.account))
    scopes = sorted(claims.get("scp", "").split())
    print(json.dumps({
        "account": args.account,
        "signed_in": True,
        "upn": claims.get("upn") or claims.get("unique_name"),
        "client_id": claims.get("appid"),
        "client_id_source": ("remote MCP oauth config" if MCP_SERVER_URL
                             else "fallback constant"),
        "mcp_server": MCP_SERVER_URL,
        "audience": claims.get("aud"),
        "scopes": scopes,
        "can_read_files": any(
            s in scopes for s in
            ("Sites.Read.All", "Sites.ReadWrite.All",
             "Files.Read.All", "Files.ReadWrite.All", "Files.Read", "Files.ReadWrite")),
        "can_write_files": any(
            s in scopes for s in
            ("Sites.ReadWrite.All", "Files.ReadWrite.All", "Files.ReadWrite")),
        "keychain_service": KEYCHAIN_SERVICE,
    }, indent=2))
    return EXIT_OK


def cmd_copy(args):
    """Server-side copy within M365. No bytes touch this machine.

    Needs a write scope on the app registration; without one Graph returns 403.
    """
    token = get_token(args.account)
    body = {"parentReference": {"driveId": args.to_drive, "id": args.to_folder}}
    if args.name:
        body["name"] = args.name

    url = f"{GRAPH}{item_path(args.drive, args.item)}/copy"
    req = urllib.request.Request(
        url, data=json.dumps(body).encode(), method="POST",
        headers={"Authorization": f"Bearer {token}",
                 "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=120) as resp:
            monitor = resp.headers.get("Location")
            print(json.dumps({"accepted": resp.status, "monitor": monitor}, indent=2))
    except urllib.error.HTTPError as e:
        detail = e.read()[:300].decode(errors="replace")
        if e.code == 403:
            die("copy denied — the app registration has no write scope.\n"
                "  Add Files.ReadWrite.All (or Sites.ReadWrite.All) to client "
                f"{CLIENT_ID}\n  and re-run auth. Detail: {detail}")
        die(f"copy failed with HTTP {e.code}: {detail}")
    return EXIT_OK


def cmd_put(args):
    """Simple upload straight to Graph — handles files up to 250 MB in one PUT.

    Simpler and more reliable than an upload session, and it stays on the Graph
    audience, avoiding the SharePoint-audience mismatch that breaks session URLs here.
    """
    if not os.path.isfile(args.file):
        die(f"no such file: {args.file}")
    total = os.path.getsize(args.file)
    if total > 250 * 1024 * 1024:
        die(f"{human(total)} exceeds the 250MB simple-upload limit; use an upload session")

    token = get_token(args.account)
    with open(args.file, "rb") as fh:
        data = fh.read()

    url = f"{GRAPH}{item_path(args.drive, args.item)}/content"
    req = urllib.request.Request(
        url, data=data, method="PUT",
        headers={"Authorization": f"Bearer {token}",
                 "Content-Type": "application/octet-stream"})
    log(f"uploading {os.path.basename(args.file)} ({human(total)})")
    try:
        with urllib.request.urlopen(req, timeout=600) as resp:
            print(json.dumps(json.loads(resp.read()), indent=2))
    except urllib.error.HTTPError as e:
        detail = e.read()[:300].decode(errors="replace")
        if e.code == 403:
            die("upload denied — the app registration has no write scope.\n"
                "  Add Files.ReadWrite.All (or Sites.ReadWrite.All) to client "
                f"{CLIENT_ID}\n  and re-run auth. Detail: {detail}")
        die(f"upload failed with HTTP {e.code}: {detail}")
    return EXIT_OK


def cmd_info(args):
    token = get_token(args.account)
    meta = graph_get_json(token, item_path(args.drive, args.item))
    print(json.dumps({
        "id": meta.get("id"),
        "name": meta.get("name"),
        "size": meta.get("size"),
        "mimeType": (meta.get("file") or {}).get("mimeType"),
        "lastModifiedDateTime": meta.get("lastModifiedDateTime"),
        "webUrl": meta.get("webUrl"),
    }, indent=2))
    return EXIT_OK


def cmd_download(args):
    token = get_token(args.account)
    meta = graph_get_json(token, item_path(args.drive, args.item))

    name = args.name or meta.get("name") or args.item
    size = meta.get("size") or 0
    if "folder" in meta:
        die("that item is a folder, not a file")

    dest_dir = out_dir(args.out)
    dest = os.path.join(dest_dir, name)
    part = dest + ".part"

    log(f"downloading {name} ({human(size)})")

    url = f"{GRAPH}{item_path(args.drive, args.item)}/content"
    req = urllib.request.Request(url, headers={"Authorization": f"Bearer {token}"})

    got = 0
    checked = False
    try:
        with urllib.request.urlopen(req, timeout=300) as resp, open(part, "wb") as fh:
            while True:
                chunk = resp.read(DOWNLOAD_CHUNK)
                if not chunk:
                    break

                if not checked:
                    checked = True
                    if looks_protected(chunk) and not args.allow_protected:
                        fh.close()
                        os.remove(part)
                        print(
                            f"error: '{name}' is RMS-protected (encrypted).\n"
                            "  Downloaded bytes would be an unreadable protected container.\n"
                            "  Decryption requires the MIP SDK and a granted EXTRACT usage\n"
                            "  right; the MIP .NET package ships Windows binaries only.\n"
                            "  Use Work IQ retrieve/ask for this file instead.\n"
                            "  Pass --allow-protected to save the protected bytes anyway.",
                            file=sys.stderr,
                        )
                        return EXIT_PROTECTED

                fh.write(chunk)
                got += len(chunk)
                if size and got % (16 * 1024 * 1024) < DOWNLOAD_CHUNK:
                    log(f"  {human(got)} / {human(size)}")
    except urllib.error.HTTPError as e:
        if os.path.exists(part):
            os.remove(part)
        die(f"Graph {e.code} downloading content: "
            f"{e.read()[:300].decode(errors='replace')}")

    if size and got != size:
        os.remove(part)
        die(f"size mismatch: expected {size}, got {got}")

    os.replace(part, dest)
    log(f"saved {dest} ({human(got)})")
    print(json.dumps({"path": dest, "bytes": got, "name": name}, indent=2))
    return EXIT_OK


def cmd_upload(args):
    """PUT a local file to an upload session URL in 10 MiB chunks.

    Tries anonymously first per the documented Graph contract, then falls back to a
    bearer token because this tenant rejects unauthenticated PUTs with 401.
    """
    path = args.file
    if not os.path.isfile(path):
        die(f"no such file: {path}")
    total = os.path.getsize(path)
    if total == 0:
        die("refusing to upload an empty file")

    log(f"uploading {os.path.basename(path)} ({human(total)}) in "
        f"{human(UPLOAD_CHUNK)} chunks")

    token = None
    sent = 0
    result = None

    with open(path, "rb") as fh:
        while sent < total:
            chunk = fh.read(UPLOAD_CHUNK)
            end = sent + len(chunk) - 1
            code = body = None

            for attempt in range(6):
                headers = {
                    "Content-Length": str(len(chunk)),
                    "Content-Range": f"bytes {sent}-{end}/{total}",
                }
                if token:
                    headers["Authorization"] = f"Bearer {token}"
                req = urllib.request.Request(
                    args.upload_url, data=chunk, headers=headers, method="PUT")
                try:
                    with urllib.request.urlopen(req, timeout=300) as resp:
                        code, body = resp.status, resp.read()
                    break
                except urllib.error.HTTPError as e:
                    if e.code == 401 and token is None:
                        log("  uploadUrl rejected anonymous PUT; using bearer token")
                        token = get_token(args.account)
                        continue
                    if e.code in (408, 429, 500, 502, 503, 504) and attempt < 5:
                        wait = 2 ** attempt
                        log(f"  transient {e.code}; retrying in {wait}s")
                        time.sleep(wait)
                        continue
                    die(f"upload failed at byte {sent} with HTTP {e.code}: "
                        f"{e.read()[:300].decode(errors='replace')}")
            else:
                die(f"upload failed at byte {sent} after repeated retries")

            sent = end + 1
            log(f"  {human(sent)} / {human(total)}")
            if code in (200, 201) and body:
                try:
                    result = json.loads(body)
                except Exception:
                    result = None

    print(json.dumps(result or {"uploaded": sent, "complete": sent >= total}, indent=2))
    return EXIT_OK


def main():
    p = argparse.ArgumentParser(
        prog="m365_files.py", description="Margo's M365 large-file bridge")
    p.add_argument("--account", default=os.environ.get(
        "MARGO_M365_ACCOUNT", ""))
    sub = p.add_subparsers(dest="cmd", required=True)

    a = sub.add_parser("auth", help="browser sign-in; stores refresh token in Keychain")
    a.add_argument("--device", action="store_true",
                   help="use device-code flow (blocked by Conditional Access here)")
    sub.add_parser("logout", help="remove the stored refresh token")
    sub.add_parser("status", help="show sign-in state")

    q = sub.add_parser("info", help="show driveItem metadata")
    q.add_argument("--drive", required=True)
    q.add_argument("--item", required=True)

    d = sub.add_parser("download", help="stream a file to local disk (any size)")
    d.add_argument("--drive", required=True)
    d.add_argument("--item", required=True)
    d.add_argument("--out", help="output directory (default $MARGO_FILES_DIR or cwd)")
    d.add_argument("--name", help="override the output filename")
    d.add_argument("--allow-protected", action="store_true",
                   help="save RMS-protected bytes instead of refusing")

    u = sub.add_parser("upload", help="chunked PUT to an upload session URL")
    u.add_argument("--upload-url", required=True)
    u.add_argument("--file", required=True)

    pu = sub.add_parser("put", help="simple upload to Graph (<=250MB, needs write scope)")
    pu.add_argument("--drive", required=True)
    pu.add_argument("--item", required=True)
    pu.add_argument("--file", required=True)

    c = sub.add_parser("copy", help="server-side copy within M365 (needs write scope)")
    c.add_argument("--drive", required=True)
    c.add_argument("--item", required=True)
    c.add_argument("--to-drive", required=True)
    c.add_argument("--to-folder", required=True)
    c.add_argument("--name")

    args = p.parse_args()
    handlers = {
        "auth": cmd_auth, "logout": cmd_logout, "status": cmd_status,
        "info": cmd_info, "download": cmd_download, "upload": cmd_upload,
        "put": cmd_put, "copy": cmd_copy,
    }
    sys.exit(handlers[args.cmd](args))


if __name__ == "__main__":
    main()

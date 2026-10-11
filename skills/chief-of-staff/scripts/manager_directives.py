"""Verified manager directives, standing rules and control-channel bookkeeping (no network access).

Authority comes from a verified identity, never from what a message says about itself. The
``verify_*`` functions are pure checks over Graph JSON that a caller has already fetched; they
return a verified record or raise ``StateError`` with the reason. Quoted, forwarded and attached
content is stripped before the manager's words are read, so observed text stays data.
``DirectiveStore`` journals what was verified; it does not authenticate anyone itself.
"""

import hashlib
import os
import re
import socket
import sqlite3
import uuid
from datetime import datetime, timedelta, timezone
from html.parser import HTMLParser
from pathlib import Path

import margo_store
from margo_store import NotInitialized, StateError, canonical_json, utc_now, validate_timestamp

try:
    import pwd
except ImportError:  # Windows has no passwd database; CLI logins cannot resolve there.
    pwd = None

DEFAULT_DEPLOYMENT = "/etc/margo/deployment.json"
CHANNELS = ("cli", "teams", "email")
KINDS = {"instruction", "standing_rule", "approval", "rejection", "revocation", "pause", "resume"}
STATES = {"active", "completed", "revoked", "rejected"}
RULE_STATES = {"active", "revoked"}
MAX_TEXT = 20000
PLACEHOLDER = re.compile(r"\{[a-z0-9-]+\}")
RULE_ID = re.compile(r"rule_[0-9a-f]{32}")
SCHEMA = """
CREATE TABLE IF NOT EXISTS directive_meta (key TEXT PRIMARY KEY,value TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS manager_directives (
 id TEXT PRIMARY KEY,account TEXT NOT NULL,manager_object_id TEXT NOT NULL,channel TEXT NOT NULL,
 source_ref TEXT NOT NULL UNIQUE,kind TEXT NOT NULL,text TEXT NOT NULL,text_hash TEXT NOT NULL,
 received_at TEXT NOT NULL,verified_at TEXT NOT NULL,verification TEXT NOT NULL,state TEXT NOT NULL,
 completed_at TEXT,revoked_at TEXT,created_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS manager_standing_rules (
 id TEXT PRIMARY KEY,account TEXT NOT NULL,directive_id TEXT NOT NULL REFERENCES manager_directives(id),
 rule TEXT NOT NULL,state TEXT NOT NULL,created_at TEXT NOT NULL,revoked_at TEXT,revoked_by_directive TEXT
);
CREATE TABLE IF NOT EXISTS directive_effects (
 id TEXT PRIMARY KEY,account TEXT NOT NULL,directive_id TEXT,rule_id TEXT,tool TEXT NOT NULL,
 arguments_digest TEXT NOT NULL,before TEXT NOT NULL,after TEXT NOT NULL,created_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS directive_events (
 seq INTEGER PRIMARY KEY AUTOINCREMENT,account TEXT NOT NULL,entity_id TEXT NOT NULL,event TEXT NOT NULL,
 data TEXT NOT NULL,created_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS control_replies (
 id TEXT PRIMARY KEY,account TEXT NOT NULL,channel TEXT NOT NULL,text_hash TEXT NOT NULL,created_at TEXT NOT NULL
);
"""


def text(value, field, maximum=4000):
    if not isinstance(value, str) or not value.strip() or len(value) > maximum:
        raise StateError(field + " must be a bounded nonempty string")
    return value


def digest(value):
    return hashlib.sha256(canonical_json(value).encode("utf-8")).hexdigest()


def clock():
    return datetime.now(timezone.utc)


def stamp(now=None):
    return (now or clock()).astimezone(timezone.utc).isoformat(timespec="microseconds")


def graph_timestamp(value, field):
    """Accept Graph's seven-digit fractional seconds, then apply the store's strict rule."""
    if not isinstance(value, str):
        raise StateError(field + " must be an ISO-8601 timestamp")
    try:
        return validate_timestamp(re.sub(r"(\.\d{6})\d+", r"\1", value.strip()))
    except StateError as exc:
        raise StateError(field + ": " + str(exc)) from exc


def validate_object_id(value):
    """Validate an Entra object id with the store's binding rule (lowercase GUID, never nil)."""
    return margo_store.validate_manager_object_id(value)


# ---------------------------------------------------------------------------
# Deployment anchor
# ---------------------------------------------------------------------------

def _require(section, key, kinds, path, optional=False, allow_none=False):
    if key not in section:
        if optional:
            return None
        raise StateError("deployment is missing " + path + "." + key)
    value = section[key]
    if value is None and allow_none:
        return None
    if (isinstance(value, bool) and bool not in kinds) or not isinstance(value, kinds):
        raise StateError("deployment " + path + "." + key + " has the wrong type")
    return value


def _strings(section, key, path, allow_empty=False):
    value = _require(section, key, (list,), path)
    if (not value and not allow_empty) or any(not isinstance(item, str) or not item.strip() for item in value):
        raise StateError("deployment " + path + "." + key + " must list nonempty strings")
    if len(set(value)) != len(value):
        raise StateError("deployment " + path + "." + key + " repeats an entry")
    return list(value)


def _no_extra(section, allowed, path):
    extra = set(section) - set(allowed)
    if extra:
        raise StateError("deployment %s has unsupported key: %s" % (path, sorted(extra)[0]))


def _identity(section, path, allow_placeholders):
    _no_extra(section, {"principal", "object_id", "display_name"}, path)
    principal = text(_require(section, "principal", (str,), path), path + ".principal", 512)
    if "@" not in principal or re.search(r"[\s{}]", principal):
        raise StateError("deployment " + path + ".principal must be a user principal name")
    raw = _require(section, "object_id", (str,), path)
    if allow_placeholders and PLACEHOLDER.fullmatch(raw.strip()):
        object_id = raw.strip()
    else:
        object_id = validate_object_id(raw)
    result = {"principal": principal, "object_id": object_id}
    display = _require(section, "display_name", (str,), path, optional=True)
    if display is not None:
        result["display_name"] = text(display, path + ".display_name", 200)
    return result


def load_deployment(path=None, allow_placeholders=False):
    """Read and validate the trusted deployment anchor; never fill in defaults for identity."""
    location = Path(path if path is not None else os.environ.get("MARGO_DEPLOYMENT", DEFAULT_DEPLOYMENT)).expanduser()
    data = margo_store.read_json(location)
    if not isinstance(data, dict):
        raise StateError("deployment must be a JSON object")
    _no_extra(data, {"schema_version", "profile", "margo", "manager", "control", "workiq", "harness", "gate"}, "root")
    if _require(data, "schema_version", (int,), "root") != 1:
        raise StateError("deployment schema version mismatch; explicit migration required")
    if _require(data, "profile", (str,), "root") != "remote-host":
        raise StateError("deployment profile must be remote-host")
    result = {"schema_version": 1, "profile": "remote-host"}
    for name in ("margo", "manager"):
        result[name] = _identity(_require(data, name, (dict,), "root"), name, allow_placeholders)
    if result["margo"]["principal"].casefold() == result["manager"]["principal"].casefold():
        raise StateError("deployment margo and manager principals must differ")
    if result["margo"]["object_id"] == result["manager"]["object_id"]:
        raise StateError("deployment margo and manager object ids must differ")

    control = _require(data, "control", (dict,), "root")
    _no_extra(control, {"cli_logins", "teams_chat_id", "teams_enabled", "email_enabled",
                        "require_authentication_results", "max_replies_per_hour", "approval_ttl_hours"}, "control")
    chat_id = _require(control, "teams_chat_id", (str,), "control", allow_none=True)
    if chat_id is not None and not chat_id.strip():
        raise StateError("deployment control.teams_chat_id must be null or nonempty")
    replies = _require(control, "max_replies_per_hour", (int,), "control")
    ttl = _require(control, "approval_ttl_hours", (int,), "control")
    if not 0 <= replies <= 1000:
        raise StateError("deployment control.max_replies_per_hour must be between 0 and 1000")
    if not 1 <= ttl <= 168:
        raise StateError("deployment control.approval_ttl_hours must be between 1 and 168")
    result["control"] = {
        "cli_logins": _strings(control, "cli_logins", "control", allow_empty=True),
        "teams_chat_id": chat_id,
        "teams_enabled": _require(control, "teams_enabled", (bool,), "control"),
        "email_enabled": _require(control, "email_enabled", (bool,), "control"),
        "require_authentication_results": _require(control, "require_authentication_results", (bool,), "control"),
        "max_replies_per_hour": replies,
        "approval_ttl_hours": ttl,
    }

    workiq = _require(data, "workiq", (dict,), "root")
    _no_extra(workiq, {"command", "env", "server_name", "read_tools", "write_tools", "argument_keys", "call_templates"},
              "workiq")
    env = _require(workiq, "env", (dict,), "workiq")
    if any(not isinstance(key, str) or not isinstance(value, str) for key, value in env.items()):
        raise StateError("deployment workiq.env must map strings to strings")
    argument_keys = _require(workiq, "argument_keys", (dict,), "workiq")
    for name in ("path", "method", "body", "action"):
        _strings(argument_keys, name, "workiq.argument_keys")
    _no_extra(argument_keys, {"path", "method", "body", "action"}, "workiq.argument_keys")
    templates = _require(workiq, "call_templates", (dict,), "workiq")
    for name, template in templates.items():
        if (not isinstance(template, dict) or set(template) != {"tool", "arguments"}
                or not isinstance(template.get("tool"), str) or not isinstance(template.get("arguments"), dict)):
            raise StateError("deployment workiq.call_templates.%s must have tool and arguments" % name)
    read_tools, write_tools = _strings(workiq, "read_tools", "workiq"), _strings(workiq, "write_tools", "workiq")
    if set(read_tools) & set(write_tools):
        raise StateError("deployment workiq read_tools and write_tools must not overlap")
    result["workiq"] = {
        "command": _strings(workiq, "command", "workiq"), "env": dict(env),
        "server_name": text(_require(workiq, "server_name", (str,), "workiq"), "workiq.server_name", 100),
        "read_tools": read_tools, "write_tools": write_tools,
        "argument_keys": {name: list(argument_keys[name]) for name in ("path", "method", "body", "action")},
        "call_templates": {name: {"tool": t["tool"], "arguments": t["arguments"]} for name, t in templates.items()},
    }

    harness = _require(data, "harness", (dict,), "root")
    _no_extra(harness, {"poll_seconds", "copilot_command", "copilot_check", "max_backoff_seconds", "startup_verb",
                        "health_path", "automations_dir", "scheduled_wrapper"}, "harness")
    poll = _require(harness, "poll_seconds", (int,), "harness")
    backoff = _require(harness, "max_backoff_seconds", (int,), "harness")
    if not 1 <= poll <= 86400 or not poll <= backoff <= 86400:
        raise StateError("deployment harness poll/backoff seconds are out of range")
    result["harness"] = {
        "poll_seconds": poll, "max_backoff_seconds": backoff,
        "copilot_command": _strings(harness, "copilot_command", "harness"),
        "copilot_check": _strings(harness, "copilot_check", "harness"),
        "startup_verb": text(_require(harness, "startup_verb", (str,), "harness"), "harness.startup_verb", 100),
        "health_path": text(_require(harness, "health_path", (str,), "harness"), "harness.health_path", 1000),
        "automations_dir": _require(harness, "automations_dir", (str,), "harness", allow_none=True),
        "scheduled_wrapper": _require(harness, "scheduled_wrapper", (str,), "harness", allow_none=True),
    }

    gate = _require(data, "gate", (dict,), "root")
    _no_extra(gate, {"socket", "audit_log", "margo_root_paths", "manager_root_paths"}, "gate")
    result["gate"] = {
        "socket": text(_require(gate, "socket", (str,), "gate"), "gate.socket", 1000),
        "audit_log": text(_require(gate, "audit_log", (str,), "gate"), "gate.audit_log", 1000),
        "margo_root_paths": _strings(gate, "margo_root_paths", "gate"),
        "manager_root_paths": _strings(gate, "manager_root_paths", "gate"),
    }
    return result


def cli_uids(deployment):
    """Resolve configured VM logins to uids; unknown names are reported, never guessed."""
    resolved, unknown = {}, []
    for login in deployment["control"]["cli_logins"]:
        entry = None
        if pwd is not None and not PLACEHOLDER.fullmatch(login):
            try:
                entry = pwd.getpwnam(login)
            except KeyError:
                entry = None
        if entry is None:
            unknown.append(login)
        else:
            resolved[int(entry.pw_uid)] = login
    return {"uids": resolved, "unknown": unknown}


# ---------------------------------------------------------------------------
# Instruction grammar
# ---------------------------------------------------------------------------

def approval_ref(action_hash):
    """Short reference bound to an exact action hash; the hash itself still has to match."""
    if not isinstance(action_hash, str) or not re.fullmatch(r"[0-9a-f]{64}", action_hash):
        raise StateError("approval reference requires the displayed action_hash")
    return "MA-" + action_hash[:8]


def _normalize_ref(value):
    return "MA-" + value[3:].casefold()


def parse_instruction(value):
    """Classify one verified message; anything that is not an exact command is an instruction."""
    if not isinstance(value, str) or not value.strip():
        raise StateError("instruction text must be a nonempty string")
    stripped = value.strip()
    if len(stripped) > MAX_TEXT:
        raise StateError("instruction text exceeds %d characters" % MAX_TEXT)
    result = {"kind": "instruction", "ref": None, "rule": None, "rule_id": None, "text": stripped}
    control = re.fullmatch(r"(pause|resume)\s*[.!]?", stripped, re.IGNORECASE)
    if control:
        result["kind"] = control.group(1).casefold()
        return result
    decision = re.match(r"(approve|approved|ok|yes|reject|rejected|decline|declined|deny|denied)\b"
                        r"[\s,:.-]*(?:with\s+)?(MA-[0-9A-Fa-f]{8})\b", stripped, re.IGNORECASE)
    if decision:
        word = decision.group(1).casefold()
        result["kind"] = "approval" if word in {"approve", "approved", "ok", "yes"} else "rejection"
        result["ref"] = _normalize_ref(decision.group(2))
        return result
    revocation = re.match(r"revoke\s+(?:rule\s+)?(rule_[0-9a-f]{32})\b", stripped, re.IGNORECASE)
    if revocation:
        result["kind"] = "revocation"
        result["rule_id"] = revocation.group(1).casefold()
        return result
    rule = re.match(r"(?:(always)\b|rule:)\s*(.*)", stripped, re.IGNORECASE | re.DOTALL)
    if rule:
        body = stripped if rule.group(1) else rule.group(2)
        body = re.sub(r"\s+", " ", body).strip()
        if body:
            result["kind"] = "standing_rule"
            result["rule"] = body[:2000]
            return result
    return result


# ---------------------------------------------------------------------------
# Text extraction: quoted, forwarded and attached content never counts
# ---------------------------------------------------------------------------

class _TextExtractor(HTMLParser):
    """Collect visible text, drop quoted/attached elements, stop at the first reply/forward header."""

    DROP = {"blockquote", "attachment", "style", "script", "head", "title"}
    BLOCK = {"p", "div", "br", "li", "tr", "h1", "h2", "h3", "h4", "h5", "h6", "pre", "table", "ul", "ol", "hr"}
    MARKERS = ("divrplyfwdmsg", "appendonsend", "gmail_quote", "outlookmessageheader", "moz-cite-prefix",
               "yahoo_quoted", "protonmail_quote")

    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.parts, self.dropping, self.stopped = [], [], False

    def _is_marker(self, attrs):
        for name, value in attrs:
            if name in {"id", "class"} and value:
                lowered = value.casefold()
                if any(marker in lowered for marker in self.MARKERS):
                    return True
        return False

    def handle_starttag(self, tag, attrs):
        if self.stopped:
            return
        if self._is_marker(attrs):
            self.stopped = True
            return
        if tag in self.DROP:
            self.dropping.append(tag)
            return
        if not self.dropping and tag in self.BLOCK:
            self.parts.append("\n")

    def handle_startendtag(self, tag, attrs):
        if not self.stopped and not self.dropping and tag in self.BLOCK:
            self.parts.append("\n")

    def handle_endtag(self, tag):
        if self.stopped:
            return
        if self.dropping:
            if tag == self.dropping[-1]:
                self.dropping.pop()
            return
        if tag in self.BLOCK:
            self.parts.append("\n")

    def handle_data(self, data):
        if not self.stopped and not self.dropping:
            self.parts.append(data)


_PLAIN_MARKERS = (
    re.compile(r"^\s*-{2,}\s*(original message|original appointment|forwarded message)\s*-{2,}\s*$", re.IGNORECASE),
    re.compile(r"^\s*_{5,}\s*$"),
    re.compile(r"^\s*begin forwarded message:?\s*$", re.IGNORECASE),
    re.compile(r"^\s*[>|]"),
    re.compile(r"^\s*[*_]{0,2}from:[*_]{0,2}\s", re.IGNORECASE),
)


def strip_quoted_text(value):
    """Keep only the part above the first reply/forward marker of a plain-text body."""
    lines = [re.sub(r"[ \t\xa0\r\f\v]+", " ", line).strip() for line in str(value).split("\n")]
    kept = []
    for index, line in enumerate(lines):
        if any(pattern.match(line) for pattern in _PLAIN_MARKERS):
            break
        if re.match(r"^on\s", line, re.IGNORECASE) and any(
                candidate.rstrip().endswith("wrote:") for candidate in lines[index:index + 3]):
            break
        if line:
            kept.append(line)
    return "\n".join(kept).strip()


def html_to_text(value):
    """Visible text of an HTML body without blockquotes, attachments or anything after a reply header."""
    parser = _TextExtractor()
    parser.feed(str(value))
    parser.close()
    return strip_quoted_text("".join(parser.parts))


def body_text(body):
    if not isinstance(body, dict) or not isinstance(body.get("content"), str):
        raise StateError("message body is missing")
    if str(body.get("contentType", "text")).casefold() == "html":
        return html_to_text(body["content"])
    return strip_quoted_text(body["content"])


# ---------------------------------------------------------------------------
# Channel verification (pure functions over fetched Graph JSON)
# ---------------------------------------------------------------------------

def _address(entry):
    if isinstance(entry, dict):
        entry = entry.get("emailAddress", entry)
        if isinstance(entry, dict):
            entry = entry.get("address")
    return entry.strip().casefold() if isinstance(entry, str) and entry.strip() else None


def _record(deployment, channel, source_id, received_at, message_text, verification, now=None):
    parsed = parse_instruction(message_text)
    verified_at = stamp(now)
    return {
        "channel": channel, "source_ref": channel + ":" + source_id,
        "manager_object_id": deployment["manager"]["object_id"],
        "manager_principal": deployment["manager"]["principal"],
        "received_at": received_at, "verified_at": verified_at,
        "text": parsed["text"], "kind": parsed["kind"], "ref": parsed["ref"],
        "rule": parsed["rule"], "rule_id": parsed["rule_id"], "verification": verification,
    }


def verify_cli(peer_uid, deployment, instruction=None, now=None):
    """A local caller counts as the manager only when its uid resolves to a configured login.

    Without ``instruction`` the result identifies the caller but cannot be recorded (kind is None).
    """
    if isinstance(peer_uid, bool) or not isinstance(peer_uid, int) or peer_uid < 0:
        raise StateError("peer uid must be a non-negative integer")
    resolved = cli_uids(deployment)
    if not resolved["uids"]:
        raise StateError("no configured manager login resolves on this host")
    login = resolved["uids"].get(peer_uid)
    if login is None:
        raise StateError("peer uid is not a configured manager login")
    verification = {"method": "cli-peer-credentials", "login": login, "peer_uid": peer_uid}
    moment = stamp(now)
    if instruction is None:
        return {"channel": "cli", "source_ref": "cli:" + uuid.uuid4().hex,
                "manager_object_id": deployment["manager"]["object_id"],
                "manager_principal": deployment["manager"]["principal"],
                "received_at": moment, "verified_at": moment, "text": None, "kind": None,
                "ref": None, "rule": None, "rule_id": None, "verification": verification}
    return _record(deployment, "cli", uuid.uuid4().hex, moment, instruction, verification, now)


def _member_ids(chat):
    members = chat.get("members")
    if not isinstance(members, list) or not members:
        raise StateError("teams chat membership is unknown")
    ids = set()
    for member in members:
        if not isinstance(member, dict):
            raise StateError("teams chat membership is unknown")
        identity = member.get("userId")
        if identity is None and isinstance(member.get("user"), dict):
            identity = member["user"].get("id")
        if not isinstance(identity, str) or not identity.strip():
            raise StateError("teams chat member has no user id")
        ids.add(identity.strip().casefold())
    return ids


def verify_teams(message, chat, deployment, now=None):
    """Accept only the manager's own, unedited message in the one-on-one chat with Margo."""
    if not deployment["control"]["teams_enabled"]:
        raise StateError("teams channel is disabled")
    if not isinstance(message, dict) or not isinstance(chat, dict):
        raise StateError("teams message and chat must be objects")
    if chat.get("chatType") != "oneOnOne":
        raise StateError("teams chat is not a one-on-one chat")
    chat_id = chat.get("id")
    if not isinstance(chat_id, str) or not chat_id.strip():
        raise StateError("teams chat has no id")
    expected_chat = deployment["control"]["teams_chat_id"]
    if expected_chat is not None and chat_id != expected_chat:
        raise StateError("teams chat is not the configured control chat")
    if message.get("chatId") not in (None, chat_id):
        raise StateError("teams message belongs to another chat")
    manager, margo = deployment["manager"]["object_id"], deployment["margo"]["object_id"]
    if _member_ids(chat) != {manager.casefold(), margo.casefold()}:
        raise StateError("teams chat members are not exactly the manager and margo")
    if message.get("messageType") != "message":
        raise StateError("teams message is not a user message")
    sender = message.get("from") if isinstance(message.get("from"), dict) else {}
    user = sender.get("user") if isinstance(sender.get("user"), dict) else {}
    if not isinstance(user.get("id"), str) or user["id"].strip().casefold() != manager.casefold():
        raise StateError("teams message sender is not the bound manager")
    if message.get("lastEditedDateTime") or message.get("deletedDateTime"):
        raise StateError("teams message was edited or deleted")
    message_id = message.get("id")
    if not isinstance(message_id, str) or not message_id.strip():
        raise StateError("teams message has no id")
    received_at = graph_timestamp(message.get("createdDateTime"), "teams createdDateTime")
    content = body_text(message.get("body"))
    if not content:
        raise StateError("teams message has no instruction text")
    verification = {"method": "teams-one-on-one", "chat_type": "oneOnOne",
                    "configured_chat": expected_chat is not None, "members_exact": True,
                    "sender_is_manager": True, "edited": False,
                    "attachments_ignored": bool(message.get("attachments"))}
    return _record(deployment, "teams", message_id.strip(), received_at, content, verification, now)


def _authentication_passed(headers):
    if not isinstance(headers, list):
        return None
    values = [str(item.get("value", "")) for item in headers if isinstance(item, dict)
              and str(item.get("name", "")).casefold() == "authentication-results"]
    if not values:
        return None
    joined = " ".join(values).casefold()
    dmarc = re.search(r"\bdmarc=pass\b", joined) is not None
    spf = re.search(r"\bspf=pass\b", joined) is not None
    dkim = re.search(r"\bdkim=pass\b", joined) is not None
    return dmarc or (spf and dkim)


def _sent_items_match(lookup, message_id):
    try:
        found = lookup(message_id)
    except (TimeoutError, socket.timeout):
        raise StateError("verification timed out")
    except StateError:
        raise
    except Exception as exc:  # The lookup is remote; any failure leaves verification unknown.
        raise StateError("sent items lookup failed; verification unknown (" + type(exc).__name__ + ")") from exc
    candidates = found.get("value") if isinstance(found, dict) and isinstance(found.get("value"), list) else [found]
    return any(isinstance(item, dict) and item.get("internetMessageId") == message_id for item in candidates)


def verify_email(message, sent_items_lookup, deployment, now=None):
    """Accept only mail the manager demonstrably sent to Margo; a spoofed From has no Sent Items copy."""
    if not deployment["control"]["email_enabled"]:
        raise StateError("email channel is disabled")
    if not isinstance(message, dict):
        raise StateError("email message must be an object")
    if not callable(sent_items_lookup):
        raise StateError("sent items lookup is required")
    manager = deployment["manager"]["principal"].casefold()
    margo = deployment["margo"]["principal"].casefold()
    sender_from = _address(message.get("from"))
    if sender_from != manager:
        raise StateError("email sender is not the bound manager")
    if _address(message.get("sender")) != sender_from:
        raise StateError("email sender header does not match from")
    recipients = message.get("toRecipients")
    if not isinstance(recipients, list) or margo not in {_address(item) for item in recipients}:
        raise StateError("email is not addressed to margo")
    message_id = message.get("internetMessageId")
    if not isinstance(message_id, str) or not message_id.strip():
        raise StateError("email has no internet message id")
    message_id = message_id.strip()
    authentication = "not-required"
    if deployment["control"]["require_authentication_results"]:
        passed = _authentication_passed(message.get("internetMessageHeaders"))
        if passed is None:
            raise StateError("email authentication results are missing")
        if not passed:
            raise StateError("email authentication results did not pass")
        authentication = "pass"
    received_at = graph_timestamp(message.get("receivedDateTime"), "email receivedDateTime")
    content = body_text(message.get("body"))
    if not content:
        raise StateError("email has no instruction text")
    # The remote lookup comes last so a message that fails locally never costs a Graph call.
    if not _sent_items_match(sent_items_lookup, message_id):
        raise StateError("email is not in the manager's sent items")
    verification = {"method": "email-sent-items", "authentication_results": authentication,
                    "sent_items_match": True, "attachments_ignored": bool(message.get("hasAttachments"))}
    return _record(deployment, "email", message_id, received_at, content, verification, now)


def build_approval_evidence(verified, action, decided_at):
    """Shape a verified approval as ledger evidence; Ledger.approve still checks hash, revision and expiry."""
    if not isinstance(verified, dict) or verified.get("kind") != "approval":
        raise StateError("approval evidence requires a verified approval directive")
    if not isinstance(action, dict) or action.get("type") != "action":
        raise StateError("approval evidence requires a ledger action")
    if verified.get("ref") != approval_ref(action.get("action_hash")):
        raise StateError("approval reference does not match the displayed action")
    channel = verified.get("channel")
    source_ref = verified.get("source_ref")
    if channel not in CHANNELS or not isinstance(source_ref, str) or not source_ref.startswith(channel + ":"):
        raise StateError("verified record has an invalid channel or source reference")
    source_id = source_ref[len(channel) + 1:]
    if not source_id.strip():
        raise StateError("verified record has an empty source id")
    if type(action.get("revision")) is not int:
        raise StateError("ledger action revision must be an integer")
    return {
        "kind": "human_confirmation", "actor": text(verified.get("manager_principal"), "manager principal", 512),
        "statement": text(verified.get("text"), "approval text", MAX_TEXT),
        "evidence_ref": "manager-channel:%s:%s" % (channel, source_id),
        "subject_id": action["id"], "revision": action["revision"], "decision": "approve",
        "action_hash": action["action_hash"], "decided_at": validate_timestamp(decided_at),
        "channel": channel, "manager_object_id": validate_object_id(verified.get("manager_object_id")),
        "directive_id": verified.get("id") or verified.get("directive_id"),
    }


# ---------------------------------------------------------------------------
# Durable directive journal
# ---------------------------------------------------------------------------

class DirectiveStore:
    TABLES = {"directive_meta", "manager_directives", "manager_standing_rules", "directive_effects",
              "directive_events", "control_replies"}

    @classmethod
    def from_connection(cls, conn, account):
        """Borrow initialized account state without creating or repairing namespaces."""
        principal = margo_store.resolve_account(account)
        metadata = dict(conn.execute("SELECT key,value FROM margo_meta"))
        if metadata.get("account") != principal or metadata.get("store_version") != "1":
            raise StateError("borrowed directive connection account/schema mismatch")
        if conn.row_factory is not sqlite3.Row:
            raise StateError("borrowed directive connection requires sqlite3.Row results")
        store = cls.__new__(cls)
        store.account, store.conn, store._owns_connection = principal, conn, False
        store.read_only = True
        store._initialize()
        store.read_only = False
        return store

    def __init__(self, account=None, state_root=None, read_only=False):
        self.account = margo_store.resolve_account(account)
        self.read_only = read_only
        self._owns_connection = True
        self.conn = None
        try:
            try:
                self.conn = margo_store.connect(self.account, state_root, read_only=read_only)
            except NotInitialized:
                raise NotInitialized("Directive state is not initialized; the gate initializes it explicitly.")
            self._initialize()
        except Exception:
            if self.conn is not None:
                self.conn.close()
            raise

    def close(self):
        if self._owns_connection:
            self.conn.close()

    def transaction(self):
        return margo_store.transaction(self.conn, read_only=self.read_only)

    def _initialize(self):
        with self.transaction():
            present = {row[0] for row in self.conn.execute(
                "SELECT name FROM sqlite_master WHERE type='table'")} & self.TABLES
            marker = self.conn.execute("SELECT value FROM margo_meta WHERE key='directive_schema_version'").fetchone()
            if not present and marker is None and self.read_only:
                raise NotInitialized("Directive state is not initialized; the gate initializes it explicitly.")
            if present or marker is not None:
                if present != self.TABLES or marker is None or marker[0] != "1":
                    raise StateError("incomplete or incompatible directive schema; explicit recovery required")
                local = self.conn.execute("SELECT value FROM directive_meta WHERE key='schema_version'").fetchone()
                if local is None or local[0] != "1":
                    raise StateError("directive schema marker mismatch")
                reference = sqlite3.connect(":memory:")
                try:
                    reference.executescript(SCHEMA)
                    for table in sorted(self.TABLES):
                        for pragma in ("table_info", "foreign_key_list"):
                            actual = [tuple(row) for row in self.conn.execute("PRAGMA %s(%s)" % (pragma, table))]
                            wanted = [tuple(row) for row in reference.execute("PRAGMA %s(%s)" % (pragma, table))]
                            if actual != wanted:
                                raise StateError("directive schema contract mismatch: " + table)
                finally:
                    reference.close()
            if not self.read_only:
                for statement in SCHEMA.split(";"):
                    if statement.strip():
                        self.conn.execute(statement)
                self.conn.execute("INSERT OR IGNORE INTO directive_meta VALUES('schema_version','1')")
                self.conn.execute("INSERT OR IGNORE INTO margo_meta VALUES('directive_schema_version','1')")

    # -- rows and events -----------------------------------------------------

    def _event(self, entity_id, event, data):
        self.conn.execute("INSERT INTO directive_events(account,entity_id,event,data,created_at) VALUES(?,?,?,?,?)",
                          (self.account, entity_id, event, canonical_json(data), utc_now()))

    def _directive(self, directive_id):
        row = self.conn.execute("SELECT * FROM manager_directives WHERE id=? AND account=?",
                                (directive_id, self.account)).fetchone()
        if row is None:
            raise StateError("unknown directive")
        return row

    def _rule(self, rule_id):
        row = self.conn.execute("SELECT * FROM manager_standing_rules WHERE id=? AND account=?",
                                (rule_id, self.account)).fetchone()
        if row is None:
            raise StateError("unknown standing rule")
        return row

    def _render_directive(self, row):
        result = dict(row)
        stored = margo_store.parse_json(result.pop("verification"))
        result["type"] = "directive"
        result["verification"] = stored.get("checks")
        for key in ("ref", "rule", "rule_id", "manager_principal"):
            result[key] = stored.get(key)
        result["rules"] = [r["id"] for r in self.conn.execute(
            "SELECT id FROM manager_standing_rules WHERE directive_id=? ORDER BY created_at,id", (row["id"],))]
        result["effects"] = [self._render_effect(r) for r in self.conn.execute(
            "SELECT * FROM directive_effects WHERE directive_id=? ORDER BY created_at,id", (row["id"],))]
        outcome = self.conn.execute(
            "SELECT data FROM directive_events WHERE entity_id=? AND event IN ('completed','rejected') "
            "ORDER BY seq DESC LIMIT 1", (row["id"],)).fetchone()
        result["summary"] = margo_store.parse_json(outcome["data"]).get("summary") if outcome else None
        return result

    def _render_rule(self, row):
        result = dict(row)
        result["type"] = "rule"
        result["effects"] = [self._render_effect(r) for r in self.conn.execute(
            "SELECT * FROM directive_effects WHERE rule_id=? ORDER BY created_at,id", (row["id"],))]
        return result

    @staticmethod
    def _render_effect(row):
        result = dict(row)
        result["type"] = "effect"
        for key in ("before", "after"):
            result[key] = margo_store.parse_json(result[key])
        return result

    # -- recording -----------------------------------------------------------

    def record(self, verified):
        """Journal one verified message exactly once; namespace-local kinds are applied in the same transaction."""
        if not isinstance(verified, dict):
            raise StateError("verified record must be an object")
        channel = verified.get("channel")
        if channel not in CHANNELS:
            raise StateError("verified record has an unsupported channel")
        source_ref = text(verified.get("source_ref"), "source_ref", 1000)
        if not source_ref.startswith(channel + ":") or len(source_ref) <= len(channel) + 1:
            raise StateError("source_ref must be <channel>:<message id>")
        kind = verified.get("kind")
        if kind not in KINDS:
            raise StateError("verified record has no instruction kind")
        body = text(verified.get("text"), "text", MAX_TEXT)
        manager = validate_object_id(verified.get("manager_object_id"))
        received_at = validate_timestamp(verified.get("received_at"))
        verified_at = validate_timestamp(verified.get("verified_at"))
        if not isinstance(verified.get("verification"), dict) or not verified["verification"]:
            raise StateError("verified record requires verification details")
        ref, rule, rule_id = verified.get("ref"), verified.get("rule"), verified.get("rule_id")
        if kind in {"approval", "rejection"} and (not isinstance(ref, str) or not re.fullmatch(r"MA-[0-9a-f]{8}", ref)):
            raise StateError("approval or rejection requires an MA- reference")
        if kind == "standing_rule" and (not isinstance(rule, str) or not rule.strip()):
            raise StateError("standing rule requires rule text")
        if kind == "revocation" and (not isinstance(rule_id, str) or not RULE_ID.fullmatch(rule_id)):
            raise StateError("revocation requires a rule id")
        text_hash = hashlib.sha256(body.encode("utf-8")).hexdigest()
        stored = {"checks": verified["verification"], "ref": ref, "rule": rule, "rule_id": rule_id,
                  "manager_principal": verified.get("manager_principal")}
        with self.transaction():
            existing = self.conn.execute("SELECT * FROM manager_directives WHERE source_ref=?", (source_ref,)).fetchone()
            if existing is not None:
                if existing["account"] != self.account:
                    raise StateError("source reference belongs to another account")
                if existing["text_hash"] != text_hash or existing["kind"] != kind:
                    raise StateError("same source reference was recorded with different content; refusing to replace it")
                return dict(self._render_directive(existing), created=False)
            directive_id, now = "dir_" + uuid.uuid4().hex, utc_now()
            self.conn.execute("INSERT INTO manager_directives VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                              (directive_id, self.account, manager, channel, source_ref, kind, body, text_hash,
                               received_at, verified_at, canonical_json(stored), "active", None, None, now))
            self._event(directive_id, "recorded", {"channel": channel, "kind": kind, "text_hash": text_hash})
            if kind == "standing_rule":
                new_rule = "rule_" + uuid.uuid4().hex
                self.conn.execute("INSERT INTO manager_standing_rules VALUES(?,?,?,?,?,?,?,?)",
                                  (new_rule, self.account, directive_id, rule.strip(), "active", now, None, None))
                stored["rule_id"] = new_rule
                self.conn.execute("UPDATE manager_directives SET verification=? WHERE id=?",
                                  (canonical_json(stored), directive_id))
                self._event(new_rule, "created", {"directive_id": directive_id})
                self._settle(directive_id, "completed", "standing rule %s recorded" % new_rule)
            elif kind == "revocation":
                target = self.conn.execute("SELECT * FROM manager_standing_rules WHERE id=? AND account=?",
                                           (rule_id, self.account)).fetchone()
                if target is None or target["state"] != "active":
                    self._settle(directive_id, "rejected", "unknown or inactive standing rule %s" % rule_id)
                else:
                    self._revoke(target, directive_id)
                    self._settle(directive_id, "completed", "standing rule %s revoked" % rule_id)
            elif kind in {"pause", "resume"}:
                self._set_paused(kind == "pause", directive_id)
                self._settle(directive_id, "completed", "control %sd" % kind)
            return dict(self._render_directive(self._directive(directive_id)), created=True)

    def _settle(self, directive_id, state, summary):
        self.conn.execute("UPDATE manager_directives SET state=?,completed_at=? WHERE id=?",
                          (state, utc_now(), directive_id))
        self._event(directive_id, state, {"summary": summary})

    def active(self, directive_id):
        row = self._directive(directive_id)
        if row["state"] != "active":
            raise StateError("directive is not active")
        return self._render_directive(row)

    def active_rule(self, rule_id):
        row = self._rule(rule_id)
        if row["state"] != "active":
            raise StateError("standing rule is not active")
        return self._render_rule(row)

    def complete(self, directive_id, summary, state="completed"):
        """Close an active directive with what actually happened; a replay with the same summary is harmless."""
        text(summary, "summary")
        if state not in {"completed", "rejected"}:
            raise StateError("a directive completes or is rejected")
        with self.transaction():
            row = self._directive(directive_id)
            if row["state"] != "active":
                current = self._render_directive(row)
                if row["state"] == state and current["summary"] == summary:
                    return current
                raise StateError("directive is not active")
            self._settle(directive_id, state, summary)
            return self._render_directive(self._directive(directive_id))

    def rules(self, state="active"):
        if state is not None and state not in RULE_STATES:
            raise StateError("unknown rule state")
        query = "SELECT * FROM manager_standing_rules WHERE account=?"
        params = [self.account]
        if state is not None:
            query += " AND state=?"
            params.append(state)
        return [self._render_rule(row) for row in self.conn.execute(query + " ORDER BY created_at,id", params)]

    def _revoke(self, rule, by_directive_id):
        self.conn.execute("UPDATE manager_standing_rules SET state='revoked',revoked_at=?,revoked_by_directive=? WHERE id=?",
                          (utc_now(), by_directive_id, rule["id"]))
        self._event(rule["id"], "revoked", {"directive_id": by_directive_id})

    def revoke_rule(self, rule_id, by_directive_id):
        """Stop future use of a rule; past effects stay journaled and are never undone here."""
        with self.transaction():
            rule = self._rule(rule_id)
            self._directive(by_directive_id)
            if rule["state"] == "revoked":
                if rule["revoked_by_directive"] == by_directive_id:
                    return self._render_rule(rule)
                raise StateError("standing rule is already revoked")
            self._revoke(rule, by_directive_id)
            return self._render_rule(self._rule(rule_id))

    def record_effect(self, directive_id=None, rule_id=None, *, tool, arguments, before, after):
        """Journal a T1 effect that already happened, with its prior state, so it can be undone deliberately."""
        if (directive_id is None) == (rule_id is None):
            raise StateError("an effect names exactly one directive or standing rule")
        text(tool, "tool", 200)
        if not isinstance(arguments, dict):
            raise StateError("effect arguments must be an object")
        with self.transaction():
            if directive_id is not None:
                self._directive(directive_id)
            else:
                self._rule(rule_id)
            effect_id = "effect_" + uuid.uuid4().hex
            self.conn.execute("INSERT INTO directive_effects VALUES(?,?,?,?,?,?,?,?,?)",
                              (effect_id, self.account, directive_id, rule_id, tool, digest(arguments),
                               canonical_json(before), canonical_json(after), utc_now()))
            self._event(directive_id or rule_id, "effect_recorded", {"effect_id": effect_id, "tool": tool})
            return self._render_effect(self.conn.execute("SELECT * FROM directive_effects WHERE id=?", (effect_id,)).fetchone())

    def list(self, state=None, limit=50):
        if state is not None and state not in STATES:
            raise StateError("unknown directive state")
        if type(limit) is not int or not 1 <= limit <= 500:
            raise StateError("limit must be an integer between 1 and 500")
        query = "SELECT * FROM manager_directives WHERE account=?"
        params = [self.account]
        if state is not None:
            query += " AND state=?"
            params.append(state)
        query += " ORDER BY created_at DESC,id LIMIT ?"
        params.append(limit)
        return [self._render_directive(row) for row in self.conn.execute(query, params)]

    def record_reply(self, channel, text_value, limit=None):
        """Count a control-channel reply; with a limit, refuse atomically once the hour is used up."""
        if channel not in CHANNELS:
            raise StateError("unsupported reply channel")
        text(text_value, "reply text", MAX_TEXT)
        if limit is not None and (type(limit) is not int or limit < 0):
            raise StateError("reply limit must be a non-negative integer")
        with self.transaction():
            recent = self.replies_in_last_hour()
            if limit is not None and recent >= limit:
                raise StateError("control-channel reply rate limit reached")
            reply_id, now = "reply_" + uuid.uuid4().hex, utc_now()
            text_hash = hashlib.sha256(text_value.encode("utf-8")).hexdigest()
            self.conn.execute("INSERT INTO control_replies VALUES(?,?,?,?,?)",
                              (reply_id, self.account, channel, text_hash, now))
            return {"id": reply_id, "channel": channel, "text_hash": text_hash, "created_at": now,
                    "replies_in_last_hour": recent + 1}

    def replies_in_last_hour(self):
        cutoff = stamp(clock() - timedelta(hours=1))
        return self.conn.execute("SELECT count(*) FROM control_replies WHERE account=? AND created_at>?",
                                 (self.account, cutoff)).fetchone()[0]

    def paused(self):
        row = self.conn.execute("SELECT value FROM directive_meta WHERE key='paused'").fetchone()
        return row is not None and row[0] == "1"

    def _set_paused(self, value, directive_id):
        self.conn.execute("INSERT OR REPLACE INTO directive_meta VALUES('paused',?)", ("1" if value else "0",))
        self._event("control", "paused" if value else "resumed", {"directive_id": directive_id})

    def set_paused(self, value, directive_id):
        """Pause or resume unattended work on the manager's say-so; the directive proves who asked."""
        if type(value) is not bool:
            raise StateError("paused must be boolean")
        with self.transaction():
            self._directive(directive_id)
            self._set_paused(value, directive_id)
            return {"paused": value, "directive_id": directive_id}

    def show(self, entity_id):
        text(entity_id, "entity id", 200)
        if entity_id.startswith("rule_"):
            return self._render_rule(self._rule(entity_id))
        return self._render_directive(self._directive(entity_id))

    def history(self, entity_id):
        self.show(entity_id)
        events = []
        for row in self.conn.execute(
                "SELECT * FROM directive_events WHERE account=? AND entity_id=? ORDER BY seq", (self.account, entity_id)):
            row = dict(row)
            row["data"] = margo_store.parse_json(row["data"])
            events.append(row)
        return {"id": entity_id, "events": events}

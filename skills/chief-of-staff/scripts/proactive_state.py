#!/usr/bin/env python3
"""Transactional proactive queue, publication receipts and source coverage.

All commands are local; Work IQ reads and publication remain the caller's job.
Global --account/--state-dir flags precede the command. JSON arguments accept
inline JSON or '-' for stdin. Failed validation/storage exits 2, never resets.

Delivery:
  queue-add --json '{"id":"event-1","revision":"v2","title":"Moved"}'
  queue-drain --owner morning-run --format json
  publication-record --batch BATCH --json '{"content":"Finished brief",
    "ids":["event-1"],"status":"available","receipt":{"kind":"local"}}'
  queue-ack --batch BATCH --receipt PUBLICATION_ID
  publication-list
  publication-show PUBLICATION_ID
  publication-review PUBLICATION_ID
An available local output is durably stored here, NOT proof of host delivery or
user review. For published output use status "published" and receipt:
{"kind":"host","receipt_id":"HOST_ID","location":"OUTPUT_LINK",
 "published_at":"2026-09-02T00:00:00Z"}.
An optional publication "id" makes recording replay-safe. Prepared output cannot
acknowledge anything. Acknowledge only included IDs; live leases are never stolen.
An interrupted owner can queue-release; otherwise expiry makes items eligible.

Coverage (source identity = account + family + scope + optional collection_window):
  coverage-start --json '{"family":"mail","scope":{"folder":"inbox"},
    "window":{"start":"2026-09-01T00:00:00Z","end":"2026-09-02T00:00:00Z"},
    "run_id":"run-1","capability":"messages.list","query_version":"1",
    "kind":"enumeration"}'
  coverage-page --attempt ATTEMPT --json '{"page":1,"observations":[
    {"id":"message-1","revision":"v1","link":"provider-link"}],"final":true}'
  coverage-finish --attempt ATTEMPT --json '{"status":"complete",
    "checkpoint_at":"2026-09-02T00:00:00Z","watermark":"2026-09-02T00:00:00Z"}'
Page numbers start at 1 and are contiguous. Every page and its observations are
committed together. Nonfinal pages may carry opaque continuation. Finish accepts
complete/partial/blocked/failed/unknown, error_class, retry_after_seconds,
next_retry_at, delta_token and covered_start/covered_end. Only complete enumeration
with a final page advances a checkpoint; even empty search is not proof of absence.
Complete may set delta_token:null to clear a delta token; expired_token failures
also discard the unusable delta token without changing the successful checkpoint.
coverage-status redacts tokens; coverage-resume explicitly retrieves private tokens.
coverage-start accepts cadence_seconds for freshness/escalation and resume_attempt
to resume compatible partial/failed pages; denied/expired tokens cannot be resumed.
Report OAuth failures as blocked with error_class authentication_required. No retry
is eligible until authentication is restored; then coverage-start requires the
explicit boolean reauthenticated:true. Configuration never authenticates Work IQ.
Missing tools or a stale host binding use failed/binding_unavailable instead.
A binding failure is not evidence of a tenant-wide authentication outage.
window is the per-attempt requested interval, not permanent polling identity.
For provider-bound calendar delta horizons, explicitly set collection_window to
that fixed window as well; retire finished horizons with coverage-retire.
Continuation resume requires the same original requested interval.
An empty-queue anchor can publication-record without --batch or item membership;
its standalone output remains discoverable and requires no queue acknowledgement.
cursor-set/get are legacy timestamp HINTS ONLY. import-legacy --directory DIR is
explicit, atomic and replay-safe; pause old writers first. It never edits old JSON.
"""

import argparse
import hashlib
import json
import re
import sqlite3
import sys
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path

from margo_store import (SetupRequired, StateError, add_state_arguments, canonical_json,
                         connect, parse_json, read_json, utc_now, validate_timestamp)


SCHEMA = (
    """CREATE TABLE IF NOT EXISTS proactive_meta (
       key TEXT PRIMARY KEY, value TEXT NOT NULL)""",
    """CREATE TABLE IF NOT EXISTS proactive_batches (
       id TEXT PRIMARY KEY, owner TEXT NOT NULL, created_at TEXT NOT NULL,
       expires_at TEXT NOT NULL, status TEXT NOT NULL
       CHECK(status IN ('leased','released','expired','acked')))""",
    """CREATE TABLE IF NOT EXISTS proactive_items (
       item_key TEXT PRIMARY KEY, stable_id TEXT NOT NULL, revision TEXT NOT NULL,
       family TEXT NOT NULL, scope TEXT NOT NULL, payload TEXT NOT NULL,
       created_at TEXT NOT NULL, status TEXT NOT NULL
       CHECK(status IN ('pending','leased','published')),
       batch_id TEXT REFERENCES proactive_batches(id),
       UNIQUE(family,scope,stable_id,revision))""",
    """CREATE TABLE IF NOT EXISTS proactive_publications (
       id TEXT PRIMARY KEY, batch_id TEXT NOT NULL REFERENCES proactive_batches(id),
       content TEXT NOT NULL, status TEXT NOT NULL
       CHECK(status IN ('prepared','available','published','reviewed')),
       created_at TEXT NOT NULL, available_at TEXT, reviewed_at TEXT, receipt TEXT)""",
    """CREATE TABLE IF NOT EXISTS proactive_publication_items (
       publication_id TEXT NOT NULL REFERENCES proactive_publications(id),
       item_key TEXT NOT NULL REFERENCES proactive_items(item_key),
       PRIMARY KEY(publication_id,item_key))""",
    """CREATE TABLE IF NOT EXISTS proactive_sources (
       source_key TEXT PRIMARY KEY, family TEXT NOT NULL, scope TEXT NOT NULL,
       window TEXT NOT NULL, status TEXT NOT NULL, latest_attempt_id TEXT,
       last_attempt_at TEXT, last_successful_coverage_at TEXT, checkpoint_at TEXT,
       covered_start TEXT, covered_end TEXT, watermark TEXT, delta_token TEXT,
       consecutive_failures INTEGER NOT NULL DEFAULT 0, episode INTEGER NOT NULL DEFAULT 0,
       error_class TEXT, retry_after_seconds INTEGER, next_retry_at TEXT,
       recovered_at TEXT, cadence_seconds INTEGER, failure_started_at TEXT)""",
    """CREATE TABLE IF NOT EXISTS proactive_attempts (
       id TEXT PRIMARY KEY, source_key TEXT NOT NULL REFERENCES proactive_sources(source_key),
       run_id TEXT NOT NULL, started_at TEXT NOT NULL, finished_at TEXT,
       requested_start TEXT, requested_end TEXT, capability TEXT NOT NULL,
       query_version TEXT NOT NULL, kind TEXT NOT NULL, status TEXT NOT NULL,
       pages INTEGER NOT NULL DEFAULT 0, observations INTEGER NOT NULL DEFAULT 0,
       pagination_complete INTEGER NOT NULL DEFAULT 0, continuation TEXT,
       error_class TEXT, result TEXT)""",
    """CREATE TABLE IF NOT EXISTS proactive_pages (
       attempt_id TEXT NOT NULL REFERENCES proactive_attempts(id), page INTEGER NOT NULL,
       digest TEXT NOT NULL, PRIMARY KEY(attempt_id,page))""",
    """CREATE TABLE IF NOT EXISTS proactive_observations (
       source_key TEXT NOT NULL REFERENCES proactive_sources(source_key),
       stable_id TEXT NOT NULL, revision TEXT NOT NULL, payload TEXT NOT NULL,
       observed_at TEXT NOT NULL, PRIMARY KEY(source_key,stable_id,revision))""",
    """CREATE TABLE IF NOT EXISTS proactive_health_events (
       source_key TEXT NOT NULL REFERENCES proactive_sources(source_key),
       episode INTEGER NOT NULL, transition TEXT NOT NULL, created_at TEXT NOT NULL,
       error_class TEXT, PRIMARY KEY(source_key,episode,transition))""",
    """CREATE TABLE IF NOT EXISTS proactive_legacy_cursors (
       name TEXT PRIMARY KEY, timestamp TEXT NOT NULL)""",
    """CREATE TABLE IF NOT EXISTS proactive_legacy_surfaced (
       stable_id TEXT PRIMARY KEY, metadata TEXT NOT NULL)""",
    """CREATE TABLE IF NOT EXISTS proactive_imports (
       digest TEXT PRIMARY KEY, imported_at TEXT NOT NULL, counts TEXT NOT NULL)""",
    "CREATE INDEX IF NOT EXISTS proactive_pending ON proactive_items(status,created_at)",
)


def validate_schema(conn, allow_empty=False):
    existing = {row[0] for row in conn.execute(
        "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'proactive_%'")}
    expected = {statement.split()[5] for statement in SCHEMA if statement.startswith("CREATE TABLE")}
    if not existing and allow_empty:
        return False
    if existing != expected:
        raise StateError("incomplete proactive schema; refusing to recreate missing state")
    row = conn.execute("SELECT value FROM proactive_meta WHERE key='schema_version'").fetchone()
    if row is None or row[0] != "1":
        raise StateError("unsupported/missing proactive schema version; explicit migration required")
    return True


def initialize(conn):
    with conn:
        conn.execute("BEGIN IMMEDIATE")
        validate_schema(conn, allow_empty=True)
        for statement in SCHEMA:
            conn.execute(statement)
        conn.execute("INSERT OR IGNORE INTO proactive_meta VALUES ('schema_version','1')")
        if conn.execute("SELECT 1 FROM proactive_meta WHERE key='source_identity_version'").fetchone() is None:
            # Old windows mixed poll bounds with provider-bound collection identity.
            # Preserve their evidence, but require explicit revalidation before reuse.
            for row in conn.execute("SELECT source_key,window FROM proactive_sources"):
                if json.loads(row["window"]):
                    conn.execute("INSERT OR REPLACE INTO proactive_meta VALUES (?,?)",
                                 ("retired:" + row["source_key"], "legacy window identity requires revalidation"))
            conn.execute("INSERT INTO proactive_meta VALUES ('source_identity_version','2')")


def text(value, label, optional=False):
    if value is None and optional:
        return None
    if not isinstance(value, str) or not value.strip() or "\x00" in value:
        raise StateError(label + " must be a nonempty string")
    return value


def integer(value, label, minimum=0, maximum=2147483647):
    if isinstance(value, bool) or not isinstance(value, int) or not minimum <= value <= maximum:
        raise StateError(label + " is out of range")
    return value


def object_json(value):
    if not isinstance(value, dict):
        raise StateError("JSON input must be an object")
    canonical_json(value)
    return value


def fields(data, allowed):
    extra = set(data) - set(allowed.split())
    if extra:
        raise StateError("unknown field(s): " + ", ".join(sorted(extra)))


def digest(value):
    return hashlib.sha256(canonical_json(value).encode("utf-8")).hexdigest()


def _family(value):
    if not isinstance(value, str) or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}", value):
        raise StateError("family must be a short provider/source identifier")
    return value


def _scope(value):
    if not isinstance(value, (str, dict)) or not value:
        raise StateError("source scope must be a nonempty string or object")
    return canonical_json(value)


def _item(data, tier=None):
    item = dict(object_json(data))
    stable_id = text(item.get("id"), "id")
    family = _family(item.get("family", "legacy"))
    scope = canonical_json(item.get("scope", {}))
    if not isinstance(item.get("scope", {}), (str, dict)):
        raise StateError("item scope must be an object or string")
    content = {key: value for key, value in item.items()
               if key not in ("ts", "tier", "batch", "owner", "drained_at", "lease_expires_at",
                              "revision", "item_key")}
    revision = text(item.get("revision", digest(content)), "revision")
    item["revision"] = revision
    item["ts"] = validate_timestamp(item["ts"]) if "ts" in item else utc_now()
    item.setdefault("tier", tier or "sweep")
    text(item["tier"], "tier")
    for key in ("batch", "owner", "drained_at", "lease_expires_at", "item_key"):
        item.pop(key, None)
    key = digest([family, scope, stable_id, revision])
    return key, stable_id, revision, family, scope, canonical_json(item), item["ts"]


def _insert_item(conn, data, tier=None):
    values = _item(data, tier)
    prior = conn.execute("SELECT * FROM proactive_items WHERE item_key=?", (values[0],)).fetchone()
    if prior:
        old = json.loads(prior["payload"])
        new = json.loads(values[5])
        for payload in (old, new):
            for key in ("ts", "tier"):
                payload.pop(key, None)
        if old != new:
            raise StateError("changed item content requires a new revision")
        return {"id": values[1], "revision": values[2], "status": prior["status"], "added": False}
    conn.execute("""INSERT INTO proactive_items
        (item_key,stable_id,revision,family,scope,payload,created_at,status)
        VALUES (?,?,?,?,?,?,?,'pending')""", values)
    return {"id": values[1], "revision": values[2], "status": "pending", "added": True}


def queue_add(conn, data, tier=None):
    with conn:
        conn.execute("BEGIN IMMEDIATE")
        return _insert_item(conn, data, tier)


def _release_expired(conn, now):
    expired = [r[0] for r in conn.execute(
        "SELECT id FROM proactive_batches WHERE status='leased' AND expires_at<=?", (now,))]
    for batch in expired:
        conn.execute("UPDATE proactive_items SET status='pending',batch_id=NULL "
                     "WHERE batch_id=? AND status='leased'", (batch,))
        conn.execute("UPDATE proactive_batches SET status='expired' WHERE id=?", (batch,))
    return len(expired)


def _render_item(row, batch=None):
    item = json.loads(row["payload"])
    item.update(item_key=row["item_key"], status=row["status"])
    if batch is not None:
        item.update(batch=batch["id"], owner=batch["owner"], drained_at=batch["created_at"],
                    lease_expires_at=batch["expires_at"])
    return item


def queue_drain(conn, owner=None, lease_seconds=900, limit=100):
    integer(lease_seconds, "lease_seconds", 1, 86400)
    integer(limit, "limit", 1, 10000)
    batch_id = uuid.uuid4().hex
    owner = text(owner or batch_id, "owner")
    now = utc_now()
    expires = (datetime.fromisoformat(now) + timedelta(seconds=lease_seconds)).isoformat(timespec="microseconds")
    with conn:
        conn.execute("BEGIN IMMEDIATE")
        _release_expired(conn, now)
        rows = list(conn.execute("SELECT * FROM proactive_items WHERE status='pending' "
                                 "ORDER BY created_at,item_key LIMIT ?", (limit,)))
        if not rows:
            return []
        conn.execute("INSERT INTO proactive_batches VALUES (?,?,?,?,'leased')",
                     (batch_id, owner, now, expires))
        conn.executemany("UPDATE proactive_items SET status='leased',batch_id=? WHERE item_key=?",
                         [(batch_id, row["item_key"]) for row in rows])
        batch = conn.execute("SELECT * FROM proactive_batches WHERE id=?", (batch_id,)).fetchone()
        result = [_render_item(row, batch) for row in rows]
        for item in result:
            item["status"] = "leased"
        return result


def _live_batch(conn, batch_id):
    row = conn.execute("SELECT * FROM proactive_batches WHERE id=?", (batch_id,)).fetchone()
    if not row or row["status"] != "leased" or row["expires_at"] <= utc_now():
        raise StateError("batch is absent, released, acknowledged or expired; cannot publish/ack")
    return row


def queue_release(conn, batch_id, owner):
    with conn:
        conn.execute("BEGIN IMMEDIATE")
        batch = conn.execute("SELECT * FROM proactive_batches WHERE id=?", (batch_id,)).fetchone()
        if batch is None or batch["owner"] != owner:
            raise StateError("batch owner mismatch")
        if batch["status"] in ("released", "expired"):
            return {"batch": batch_id, "released": 0}
        if batch["status"] != "leased":
            raise StateError("cannot release an acknowledged batch")
        count = conn.execute("UPDATE proactive_items SET status='pending',batch_id=NULL "
                             "WHERE batch_id=? AND status='leased'", (batch_id,)).rowcount
        conn.execute("UPDATE proactive_batches SET status='released' WHERE id=?", (batch_id,))
        return {"batch": batch_id, "released": count}


def queue_renew(conn, batch_id, owner, lease_seconds):
    integer(lease_seconds, "lease_seconds", 1, 86400)
    with conn:
        conn.execute("BEGIN IMMEDIATE")
        batch = _live_batch(conn, batch_id)
        if batch["owner"] != owner:
            raise StateError("batch owner mismatch")
        expires = (datetime.now(timezone.utc) + timedelta(seconds=lease_seconds)).isoformat(timespec="microseconds")
        conn.execute("UPDATE proactive_batches SET expires_at=? WHERE id=?", (expires, batch_id))
        return {"batch": batch_id, "expires_at": expires}


def _receipt(status, receipt, publication_id):
    if status == "prepared":
        if receipt is not None:
            raise StateError("prepared output cannot claim a publication receipt")
        return None
    receipt = object_json(receipt)
    if status == "available":
        fields(receipt, "kind")
        if receipt.get("kind") != "local":
            raise StateError("available requires a local durable-output receipt")
        return canonical_json({"kind": "local", "publication_id": publication_id})
    if status != "published" or receipt.get("kind") != "host":
        raise StateError("published requires a host publication receipt")
    fields(receipt, "kind receipt_id location published_at")
    published_at = validate_timestamp(receipt.get("published_at"))
    if published_at > utc_now():
        raise StateError("publication receipt cannot be future-dated")
    return canonical_json({"kind": "host", "receipt_id": text(receipt.get("receipt_id"), "receipt_id"),
                           "location": text(receipt.get("location"), "location"),
                           "published_at": published_at})


def _select_items(rows, data):
    ids, keys = data.get("ids"), data.get("item_keys")
    if (ids is None) == (keys is None):
        raise StateError("specify exactly one of ids or item_keys")
    selected = ids if ids is not None else keys
    if not isinstance(selected, list) or not selected or any(not isinstance(x, str) or not x for x in selected):
        raise StateError("included items must be a nonempty list of strings")
    if len(set(selected)) != len(selected):
        raise StateError("included items must not contain duplicates")
    column = "stable_id" if ids is not None else "item_key"
    chosen = [row for row in rows if row[column] in selected]
    if len(chosen) != len(selected):
        raise StateError("items are missing or IDs are ambiguous; use exact item_keys")
    return chosen


def _standalone(conn, publication_id):
    return conn.execute("SELECT 1 FROM proactive_meta WHERE key=?",
                        ("standalone:" + publication_id,)).fetchone() is not None


def publication_record(conn, batch_id, data):
    data = object_json(data)
    fields(data, "id content ids item_keys status receipt")
    publication_id = text(data.get("id", uuid.uuid4().hex), "publication id")
    content = text(data.get("content"), "content")
    status = data.get("status", "prepared")
    receipt = _receipt(status, data.get("receipt"), publication_id)
    standalone = batch_id is None
    if standalone and any(data.get(key) not in (None, []) for key in ("ids", "item_keys")):
        raise StateError("standalone publications cannot claim queued items")
    now = utc_now()
    with conn:
        conn.execute("BEGIN IMMEDIATE")
        existing = conn.execute("SELECT * FROM proactive_publications WHERE id=?", (publication_id,)).fetchone()
        if existing is not None:
            if _standalone(conn, publication_id) != standalone:
                raise StateError("publication ID already belongs to another publication mode")
            rows = list(conn.execute("""SELECT i.* FROM proactive_items i
                JOIN proactive_publication_items pi ON pi.item_key=i.item_key
                WHERE pi.publication_id=?""", (publication_id,)))
            chosen = [] if standalone else _select_items(rows, data)
            if (len(chosen) != len(rows) or (not standalone and existing["batch_id"] != batch_id)
                    or existing["content"] != content or existing["status"] != status
                    or existing["receipt"] != receipt):
                raise StateError("publication ID already exists with different content or status")
            return {"publication_id": publication_id, "status": status, "standalone": standalone, "replayed": True}
        if standalone:
            batch_id = "output-" + uuid.uuid4().hex
            conn.execute("INSERT INTO proactive_batches VALUES (?,?,?,?,'acked')",
                         (batch_id, publication_id, now, now))
            conn.execute("INSERT INTO proactive_meta VALUES (?,?)", ("standalone:" + publication_id, "1"))
            chosen = []
        else:
            _live_batch(conn, batch_id)
            rows = list(conn.execute("SELECT * FROM proactive_items WHERE batch_id=? AND status='leased'",
                                     (batch_id,)))
            chosen = _select_items(rows, data)
        conn.execute("INSERT INTO proactive_publications VALUES (?,?,?,?,?,?,NULL,?)",
                     (publication_id, batch_id, content, status, now,
                      now if receipt is not None else None, receipt))
        conn.executemany("INSERT INTO proactive_publication_items VALUES (?,?)",
                         [(publication_id, row["item_key"]) for row in chosen])
        return {"publication_id": publication_id, "status": status, "standalone": standalone,
                "included_items": len(chosen)}


def publication_publish(conn, publication_id, data):
    fields(object_json(data), "status receipt")
    status = data.get("status")
    if status not in ("available", "published"):
        raise StateError("publication status must be available or published")
    receipt = _receipt(status, data.get("receipt"), publication_id)
    with conn:
        conn.execute("BEGIN IMMEDIATE")
        publication = conn.execute("SELECT * FROM proactive_publications WHERE id=?", (publication_id,)).fetchone()
        if publication is None:
            raise StateError("unknown publication")
        if publication["status"] == status and publication["receipt"] == receipt:
            return {"publication_id": publication_id, "status": status, "replayed": True}
        if publication["status"] != "prepared":
            raise StateError("publication receipt is immutable once available/published")
        if not _standalone(conn, publication_id):
            _live_batch(conn, publication["batch_id"])
        conn.execute("UPDATE proactive_publications SET status=?,receipt=?,available_at=? WHERE id=?",
                     (status, receipt, utc_now(), publication_id))
        return {"publication_id": publication_id, "status": status}


def queue_ack(conn, receipt_id, batch_id=None, ids=None, item_keys=None):
    if not receipt_id:
        raise StateError("queue-ack/mark requires a durable --receipt publication ID")
    if not batch_id and not ids and not item_keys:
        raise StateError("queue-ack requires --batch, IDs or --item-key")
    with conn:
        conn.execute("BEGIN IMMEDIATE")
        publication = conn.execute("SELECT * FROM proactive_publications WHERE id=?", (receipt_id,)).fetchone()
        if (publication is None or publication["status"] not in ("available", "published", "reviewed")
                or not publication["receipt"]):
            raise StateError("ack requires an available/published output with a durable receipt")
        if _standalone(conn, receipt_id):
            raise StateError("standalone output has no queue items to acknowledge")
        if batch_id and batch_id != publication["batch_id"]:
            raise StateError("receipt belongs to a different batch")
        rows = list(conn.execute("""SELECT i.* FROM proactive_items i
            JOIN proactive_publication_items pi ON pi.item_key=i.item_key
            WHERE pi.publication_id=?""", (receipt_id,)))
        chosen = _select_items(rows, {"ids": ids} if ids else {"item_keys": item_keys}) if (ids or item_keys) else rows
        if all(row["status"] == "published" and row["batch_id"] == publication["batch_id"] for row in chosen):
            return {"acked": 0, "replayed": True, "publication_id": receipt_id}
        _live_batch(conn, publication["batch_id"])
        for row in chosen:
            if row["batch_id"] != publication["batch_id"] or row["status"] not in ("leased", "published"):
                raise StateError("receipt no longer owns the selected item lease")
        count = 0
        for row in chosen:
            count += conn.execute("UPDATE proactive_items SET status='published' "
                                  "WHERE item_key=? AND status='leased'", (row["item_key"],)).rowcount
        remaining = conn.execute("SELECT count(*) FROM proactive_items WHERE batch_id=? AND status='leased'",
                                 (publication["batch_id"],)).fetchone()[0]
        if not remaining:
            conn.execute("UPDATE proactive_batches SET status='acked' WHERE id=?", (publication["batch_id"],))
        return {"acked": count, "remaining": remaining, "publication_id": receipt_id}


def publication_review(conn, publication_id, reviewed_at=None):
    timestamp = validate_timestamp(reviewed_at) if reviewed_at is not None else utc_now()
    with conn:
        conn.execute("BEGIN IMMEDIATE")
        row = conn.execute("SELECT * FROM proactive_publications WHERE id=?", (publication_id,)).fetchone()
        if row is None or row["status"] not in ("available", "published", "reviewed"):
            raise StateError("only an available/published output can be explicitly reviewed")
        if timestamp < row["available_at"]:
            raise StateError("review cannot predate output availability")
        conn.execute("UPDATE proactive_publications SET status='reviewed',reviewed_at=COALESCE(reviewed_at,?) WHERE id=?",
                     (timestamp, publication_id))
        return {"publication_id": publication_id, "status": "reviewed"}


def _window(value):
    value = dict(object_json(value))
    fields(value, "start end")
    if value:
        value["start"] = validate_timestamp(value.get("start"))
        value["end"] = validate_timestamp(value.get("end"))
        if value["start"] >= value["end"]:
            raise StateError("window start must precede end")
    return value


def coverage_start(conn, data):
    data = object_json(data)
    fields(data, "family scope window collection_window run_id capability query_version kind "
           "cadence_seconds resume_attempt reauthenticated reactivate")
    family, scope = _family(data.get("family")), _scope(data.get("scope"))
    window = _window(data.get("window", {}))
    collection_window = _window(data.get("collection_window", {}))
    if collection_window and window != collection_window:
        raise StateError("a provider-bound collection_window must match its requested window")
    run_id = text(data.get("run_id"), "run_id")
    capability = text(data.get("capability"), "capability")
    query_version = text(data.get("query_version"), "query_version")
    kind = data.get("kind", "enumeration")
    if kind not in ("enumeration", "search", "snapshot"):
        raise StateError("kind must be enumeration, search or snapshot")
    cadence = data.get("cadence_seconds")
    if not isinstance(data.get("reauthenticated", False), bool):
        raise StateError("reauthenticated must be an explicit boolean")
    if not isinstance(data.get("reactivate", False), bool):
        raise StateError("reactivate must be an explicit boolean")
    if cadence is not None:
        integer(cadence, "cadence_seconds", 1)
    principal = conn.execute("SELECT value FROM margo_meta WHERE key='account'").fetchone()[0]
    key = digest([principal, family, scope, collection_window])
    attempt_id, now = uuid.uuid4().hex, utc_now()
    with conn:
        conn.execute("BEGIN IMMEDIATE")
        prior_source = conn.execute("SELECT * FROM proactive_sources WHERE source_key=?", (key,)).fetchone()
        if conn.execute("SELECT 1 FROM proactive_meta WHERE key=?", ("retired:" + key,)).fetchone():
            if not data.get("reactivate"):
                raise StateError("source is retired; explicitly revalidate and reactivate before reuse")
            conn.execute("DELETE FROM proactive_meta WHERE key=?", ("retired:" + key,))
        if (prior_source is not None and prior_source["error_class"] == "authentication_required"
                and not data.get("reauthenticated", False)):
            raise StateError("source is authentication-blocked; reauthenticate before an explicit reauthenticated retry")
        previous = None
        if data.get("resume_attempt") is not None:
            previous = _attempt(conn, text(data["resume_attempt"], "resume_attempt"))
            if (previous["source_key"] != key or previous["status"] not in ("partial", "failed")
                    or previous["pagination_complete"] or not previous["continuation"]
                    or previous["kind"] != kind or previous["query_version"] != query_version
                    or previous["capability"] != capability
                    or previous["requested_start"] != window.get("start")
                    or previous["requested_end"] != window.get("end")
                    or previous["error_class"] in ("expired_token", "access_denied", "authentication_required")):
                raise StateError("resume requires compatible partial pages and a still-usable continuation")
        conn.execute("""INSERT OR IGNORE INTO proactive_sources
            (source_key,family,scope,window,status) VALUES (?,?,?,?,'unknown')""",
                     (key, family, scope, canonical_json(collection_window)))
        conn.execute("""INSERT INTO proactive_attempts
            (id,source_key,run_id,started_at,requested_start,requested_end,capability,query_version,kind,status)
            VALUES (?,?,?,?,?,?,?,?,?,'running')""",
                     (attempt_id, key, run_id, now, window.get("start"), window.get("end"),
                      capability, query_version, kind))
        contract = canonical_json([capability, query_version, kind])
        token_contract = conn.execute("SELECT value FROM proactive_meta WHERE key=?",
                                      ("token-contract:" + key,)).fetchone()
        if (prior_source is not None and prior_source["delta_token"] is not None
                and (token_contract is None or token_contract[0] != contract)):
            # Provider tokens are bound to a query contract, not just a mailbox/window.
            conn.execute("UPDATE proactive_sources SET delta_token=NULL WHERE source_key=?", (key,))
            conn.execute("DELETE FROM proactive_meta WHERE key=?", ("token-contract:" + key,))
            conn.execute("INSERT INTO proactive_meta VALUES (?,?)",
                         ("resync:" + attempt_id, "query_contract_changed"))
        conn.execute("""UPDATE proactive_sources SET status='running',latest_attempt_id=?,
            last_attempt_at=?,cadence_seconds=COALESCE(?,cadence_seconds) WHERE source_key=?""",
                     (attempt_id, now, cadence, key))
        if previous is not None:
            conn.execute("""INSERT INTO proactive_pages SELECT ?,page,digest FROM proactive_pages
                WHERE attempt_id=?""", (attempt_id, previous["id"]))
            conn.execute("""UPDATE proactive_attempts SET pages=?,observations=?,continuation=? WHERE id=?""",
                         (previous["pages"], previous["observations"], previous["continuation"], attempt_id))
    return {"attempt_id": attempt_id, "source_key": key, "status": "running"}


def coverage_retire(conn, source_key, reason):
    text(reason, "retirement reason")
    with conn:
        conn.execute("BEGIN IMMEDIATE")
        source = conn.execute("SELECT * FROM proactive_sources WHERE source_key=?", (source_key,)).fetchone()
        if source is None:
            raise StateError("unknown source")
        if _attempt(conn, source["latest_attempt_id"])["status"] == "running":
            raise StateError("finish the source's running attempt before retirement")
        conn.execute("INSERT OR REPLACE INTO proactive_meta VALUES (?,?)", ("retired:" + source_key, reason))
    return {"source_key": source_key, "retired": True}


def _attempt(conn, attempt_id):
    row = conn.execute("SELECT * FROM proactive_attempts WHERE id=?", (attempt_id,)).fetchone()
    if row is None:
        raise StateError("unknown coverage attempt")
    return row


def coverage_page(conn, attempt_id, data):
    data = object_json(data)
    fields(data, "page observations final continuation")
    page = integer(data.get("page"), "page", 1)
    observations = data.get("observations")
    if not isinstance(observations, list):
        raise StateError("observations must be a list, including for empty enumeration")
    final = data.get("final")
    if not isinstance(final, bool):
        raise StateError("final must be true or false")
    continuation = text(data.get("continuation"), "continuation", optional=True)
    if final and continuation is not None:
        raise StateError("a final page cannot have a continuation")
    validated = []
    for observation in observations:
        observation = object_json(observation)
        stable_id = text(observation.get("id"), "observation id")
        revision = text(observation.get("revision"), "observation revision")
        for name in ("observed_at", "modified_at"):
            if name in observation:
                validate_timestamp(observation[name])
        observed_at = validate_timestamp(observation["observed_at"]) if "observed_at" in observation else utc_now()
        payload = {key: value for key, value in observation.items() if key != "observed_at"}
        validated.append((stable_id, revision, canonical_json(payload), observed_at))
    fingerprint = digest(dict(data, observations=[json.loads(row[2]) for row in validated]))
    with conn:
        conn.execute("BEGIN IMMEDIATE")
        attempt = _attempt(conn, attempt_id)
        existing = conn.execute("SELECT digest FROM proactive_pages WHERE attempt_id=? AND page=?",
                                (attempt_id, page)).fetchone()
        if existing:
            if existing[0] != fingerprint:
                raise StateError("page replay differs from the durable page")
            return {"attempt_id": attempt_id, "page": page, "replayed": True}
        if attempt["status"] != "running" or attempt["pagination_complete"] or page != attempt["pages"] + 1:
            raise StateError("attempt must be running and pages contiguous, with no page after final")
        for stable_id, revision, payload, observed_at in validated:
            prior = conn.execute("""SELECT payload FROM proactive_observations
                WHERE source_key=? AND stable_id=? AND revision=?""",
                                 (attempt["source_key"], stable_id, revision)).fetchone()
            prior_payload = json.loads(prior[0]) if prior else None
            if prior_payload is not None:
                prior_payload.pop("observed_at", None)
            if prior and canonical_json(prior_payload) != payload:
                raise StateError("changed source observation requires a new revision")
            conn.execute("""INSERT INTO proactive_observations VALUES (?,?,?,?,?)
                ON CONFLICT(source_key,stable_id,revision) DO UPDATE SET
                observed_at=MAX(observed_at,excluded.observed_at),payload=excluded.payload""",
                         (attempt["source_key"], stable_id, revision, payload, observed_at))
        conn.execute("INSERT INTO proactive_pages VALUES (?,?,?)", (attempt_id, page, fingerprint))
        conn.execute("""UPDATE proactive_attempts SET pages=?,observations=observations+?,
            pagination_complete=?,continuation=? WHERE id=?""",
                     (page, len(validated), int(final), continuation, attempt_id))
    return {"attempt_id": attempt_id, "page": page, "observations_stored": len(validated), "final": final}


ERROR_CLASSES = {"access_denied", "expired_token", "capability_unsupported", "throttled",
                 "timeout", "network", "unavailable", "invalid_response", "authentication_required",
                 "binding_unavailable", "unknown"}


def coverage_finish(conn, attempt_id, data):
    data = dict(object_json(data))
    fields(data, "status checkpoint_at watermark delta_token covered_start covered_end error_class "
           "retry_after_seconds next_retry_at")
    status = data.get("status")
    if status not in ("complete", "partial", "blocked", "failed", "unknown"):
        raise StateError("invalid coverage completion status")
    for name in ("checkpoint_at", "watermark", "covered_start", "covered_end", "next_retry_at"):
        if data.get(name) is not None:
            data[name] = validate_timestamp(data[name])
    token = text(data.get("delta_token"), "delta_token", optional=True)
    retry = data.get("retry_after_seconds")
    if retry is not None:
        integer(retry, "retry_after_seconds")
    error = data.get("error_class")
    if error is not None:
        data["error_class"] = error if isinstance(error, str) and error in ERROR_CLASSES else "unknown"
    if status == "blocked":
        if data.get("error_class") not in (None, "access_denied", "authentication_required"):
            raise StateError("blocked means explicit access denial or required authentication")
        if retry is not None or data.get("next_retry_at") is not None:
            raise StateError("blocked sources cannot schedule automatic retries")
    if data.get("error_class") == "authentication_required" and status != "blocked":
        raise StateError("authentication_required must be recorded as blocked")
    if status != "complete" and any(data.get(k) is not None for k in
                                    ("checkpoint_at", "watermark", "covered_start", "covered_end", "delta_token")):
        raise StateError("non-complete attempts cannot claim a successful checkpoint")
    if status == "complete" and (error is not None or retry is not None or data.get("next_retry_at")):
        raise StateError("complete coverage cannot simultaneously report a failure/retry")
    result_json = canonical_json(data)
    now = utc_now()
    with conn:
        conn.execute("BEGIN IMMEDIATE")
        attempt = _attempt(conn, attempt_id)
        if attempt["status"] != "running":
            if attempt["result"] != result_json:
                raise StateError("attempt is already finished with a different result")
            return {"attempt_id": attempt_id, "status": status, "replayed": True}
        source = conn.execute("SELECT * FROM proactive_sources WHERE source_key=?",
                              (attempt["source_key"],)).fetchone()
        if status == "complete":
            if attempt["kind"] != "enumeration" or not attempt["pagination_complete"] or not attempt["pages"]:
                raise StateError("complete requires all pages of an enumeration durably recorded")
            checkpoint = data.get("checkpoint_at")
            if checkpoint is None:
                raise StateError("complete requires checkpoint_at")
            if checkpoint > now or (source["checkpoint_at"] and checkpoint < source["checkpoint_at"]):
                raise StateError("checkpoint is future-dated or regresses reliable coverage")
            start = data.get("covered_start", attempt["requested_start"])
            end = data.get("covered_end", attempt["requested_end"])
            if (start is None) != (end is None) or (start is not None and start >= end):
                raise StateError("covered interval must contain an ordered start and end")
            if attempt["requested_start"] and (start != attempt["requested_start"] or end != attempt["requested_end"]):
                raise StateError("complete must cover the exact requested window; use partial otherwise")
            if data.get("watermark") and data["watermark"] > checkpoint:
                raise StateError("watermark cannot exceed checkpoint")
            if source["watermark"] and data.get("watermark") and data["watermark"] < source["watermark"]:
                raise StateError("watermark cannot regress")
        applied = source["latest_attempt_id"] == attempt_id
        error_class = None if status == "complete" else (
            data.get("error_class") or ("access_denied" if status == "blocked" else "unknown"))
        conn.execute("""UPDATE proactive_attempts SET status=?,finished_at=?,error_class=?,result=? WHERE id=?""",
                     (status, now, error_class, result_json, attempt_id))
        if applied and status == "complete":
            conn.execute("""UPDATE proactive_sources SET status='complete',
                last_successful_coverage_at=?,checkpoint_at=?,covered_start=?,covered_end=?,
                watermark=COALESCE(?,watermark),delta_token=CASE WHEN ? THEN ? ELSE delta_token END,
                consecutive_failures=0,error_class=NULL,retry_after_seconds=NULL,next_retry_at=NULL,
                recovered_at=CASE WHEN consecutive_failures>0 THEN ? ELSE recovered_at END,
                failure_started_at=NULL
                WHERE source_key=?""",
                         (now, checkpoint, start, end, data.get("watermark"),
                          int("delta_token" in data), token, now, source["source_key"]))
            if "delta_token" in data:
                contract_key = "token-contract:" + source["source_key"]
                if token is None:
                    conn.execute("DELETE FROM proactive_meta WHERE key=?", (contract_key,))
                else:
                    conn.execute("INSERT OR REPLACE INTO proactive_meta VALUES (?,?)",
                                 (contract_key, canonical_json([attempt["capability"],
                                                               attempt["query_version"], attempt["kind"]])))
            conn.execute("DELETE FROM proactive_meta WHERE key=?", ("resync:" + attempt_id,))
            if source["consecutive_failures"]:
                conn.execute("INSERT OR IGNORE INTO proactive_health_events VALUES (?,?, 'recovered',?,NULL)",
                             (source["source_key"], source["episode"], now))
        elif applied:
            failures = source["consecutive_failures"] + 1
            episode = source["episode"] + (1 if source["consecutive_failures"] == 0 else 0)
            failure_started = source["failure_started_at"] or now
            next_retry = data.get("next_retry_at")
            if retry is not None and next_retry is None:
                next_retry = (datetime.fromisoformat(now) + timedelta(seconds=retry)).isoformat(timespec="microseconds")
            conn.execute("""UPDATE proactive_sources SET status=?,consecutive_failures=?,episode=?,
                error_class=?,retry_after_seconds=?,next_retry_at=?,failure_started_at=?,
                delta_token=CASE WHEN ?='expired_token' THEN NULL ELSE delta_token END WHERE source_key=?""",
                         (status, failures, episode, error_class, retry, next_retry, failure_started,
                          error_class, source["source_key"]))
            elapsed = (datetime.fromisoformat(now) - datetime.fromisoformat(failure_started)).total_seconds()
            escalated = source["cadence_seconds"] is not None and elapsed >= source["cadence_seconds"]
            if failures == 1 or escalated:
                conn.execute("INSERT OR IGNORE INTO proactive_health_events VALUES (?,?,?,?,?)",
                             (source["source_key"], episode, "degraded" if failures == 1 else "escalated",
                              now, error_class))
    return {"attempt_id": attempt_id, "status": status, "source_updated": applied}


def coverage_status(conn):
    result = []
    for row in conn.execute("SELECT * FROM proactive_sources ORDER BY family,source_key"):
        item = dict(row)
        item["scope"], item["window"] = json.loads(item["scope"]), json.loads(item["window"])
        item["collection_window"] = item["window"]
        item["retired"] = conn.execute("SELECT 1 FROM proactive_meta WHERE key=?",
                                      ("retired:" + item["source_key"],)).fetchone() is not None
        item["has_delta_token"] = item.pop("delta_token") is not None
        attempt = _attempt(conn, item["latest_attempt_id"])
        item["requested_window"] = {"start": attempt["requested_start"], "end": attempt["requested_end"]}
        item["pagination"] = {"pages": attempt["pages"], "complete": bool(attempt["pagination_complete"]),
                              "observations": attempt["observations"],
                              "has_continuation": attempt["continuation"] is not None,
                              "kind": attempt["kind"]}
        item["retry_requires_reauthentication"] = item["error_class"] == "authentication_required"
        item["retry_eligible"] = (not item["retired"] and item["error_class"] not in ("access_denied", "authentication_required")
                                  and (item["next_retry_at"] is None or item["next_retry_at"] <= utc_now()))
        successful_at = item["last_successful_coverage_at"]
        cadence = item["cadence_seconds"]
        failure_started = item["failure_started_at"]
        item["missed_cadences"] = (None if cadence is None else 0 if not failure_started else
            1 + max(0, int((datetime.now(timezone.utc) - datetime.fromisoformat(failure_started)).total_seconds() // cadence)))
        item["escalated"] = item["missed_cadences"] is not None and item["missed_cadences"] >= 2
        item["fresh"] = (None if cadence is None else bool(successful_at and
            datetime.now(timezone.utc) <= datetime.fromisoformat(successful_at) + timedelta(seconds=cadence)))
        result.append(item)
    return result


def publication_list(conn, limit=20):
    integer(limit, "limit", 1, 1000)
    return [dict(row) for row in conn.execute("""SELECT p.id AS publication_id,p.batch_id,p.status,
        p.created_at,p.available_at,p.reviewed_at,count(pi.item_key) AS included_items,
        sum(CASE WHEN i.status!='published' THEN 1 ELSE 0 END) AS ack_pending
        FROM proactive_publications p LEFT JOIN proactive_publication_items pi ON pi.publication_id=p.id
        LEFT JOIN proactive_items i ON i.item_key=pi.item_key GROUP BY p.id
        ORDER BY p.created_at DESC,p.id LIMIT ?""", (limit,))]


def status_report(conn):
    counts = {row[0]: row[1] for row in conn.execute(
        "SELECT status,count(*) FROM proactive_items GROUP BY status")}
    oldest = conn.execute("SELECT min(created_at) FROM proactive_items WHERE status!='published'").fetchone()[0]
    publications = {row[0]: row[1] for row in conn.execute(
        "SELECT status,count(*) FROM proactive_publications GROUP BY status")}
    oldest_output = conn.execute("SELECT min(created_at) FROM proactive_publications WHERE status='prepared'").fetchone()[0]
    oldest = min(value for value in (oldest, oldest_output) if value) if oldest or oldest_output else None
    sources = coverage_status(conn)
    active_sources = [source for source in sources if not source["retired"]]
    return {"status": "ok", "account_configured": True,
            "queued": counts.get("pending", 0), "in_flight": counts.get("leased", 0),
            "published_items": counts.get("published", 0), "oldest_unpublished_at": oldest,
            "publications": publications, "recent_outputs": publication_list(conn), "coverage": sources,
            "unpublished_outputs": publications.get("prepared", 0),
            "all_clear": bool(active_sources) and all(s["status"] == "complete" and s["fresh"] for s in active_sources)
                         and not oldest,
            "expired_leases": conn.execute("SELECT count(*) FROM proactive_batches "
                "WHERE status='leased' AND expires_at<=?", (utc_now(),)).fetchone()[0],
            "legacy_cursor_hints": conn.execute("SELECT count(*) FROM proactive_legacy_cursors").fetchone()[0],
            "legacy_observations": conn.execute("SELECT count(*) FROM proactive_legacy_surfaced").fetchone()[0],
            "health_events": [dict(row) for row in conn.execute(
                "SELECT * FROM proactive_health_events ORDER BY created_at DESC LIMIT 50")]}


def import_legacy(conn, directory):
    directory = Path(directory)
    if not directory.is_dir():
        raise StateError("legacy directory does not exist")
    data = {}
    for name, default in (("surfaced.json", {}), ("queue.json", []), ("inflight.json", []), ("cursors.json", {})):
        path = directory / name
        if path.is_symlink():
            raise StateError("legacy import refuses symlinked files")
        data[name] = read_json(path) if path.exists() else default
        if not isinstance(data[name], type(default)):
            raise StateError(name + " has an invalid top-level shape")
    for stable_id, meta in data["surfaced.json"].items():
        text(stable_id, "legacy surfaced id")
        object_json(meta)
        if "ts" in meta:
            validate_timestamp(meta["ts"])
    for name, timestamp in data["cursors.json"].items():
        text(name, "legacy cursor name")
        validate_timestamp(timestamp)
    for item in data["queue.json"] + data["inflight.json"]:
        _item(item)
        if "drained_at" in item:
            validate_timestamp(item["drained_at"])
    fingerprint = digest(data)
    with conn:
        conn.execute("BEGIN IMMEDIATE")
        prior = conn.execute("SELECT counts FROM proactive_imports WHERE digest=?", (fingerprint,)).fetchone()
        if prior:
            return {"replayed": True, "counts": json.loads(prior[0])}
        queued = 0
        for item in data["queue.json"] + data["inflight.json"]:
            queued += int(_insert_item(conn, item)["added"])
        for stable_id, meta in data["surfaced.json"].items():
            conn.execute("INSERT OR IGNORE INTO proactive_legacy_surfaced VALUES (?,?)",
                         (stable_id, canonical_json(meta)))
        for name, timestamp in data["cursors.json"].items():
            conn.execute("INSERT OR IGNORE INTO proactive_legacy_cursors VALUES (?,?)",
                         (name, validate_timestamp(timestamp)))
        counts = {"queued": queued, "legacy_observations": len(data["surfaced.json"]),
                  "legacy_cursor_hints": len(data["cursors.json"])}
        conn.execute("INSERT INTO proactive_imports VALUES (?,?,?)",
                     (fingerprint, utc_now(), canonical_json(counts)))
    return {"replayed": False, "counts": counts, "confirmed_coverage_imported": False}


def _input(value):
    raw = sys.stdin.read() if value == "-" else value
    return object_json(parse_json(raw))


def _emit(value):
    print(json.dumps(value, ensure_ascii=False, indent=2, sort_keys=True, allow_nan=False))


def _queue_output(rows, fmt):
    if fmt == "json":
        _emit(rows)
    else:
        for row in rows:
            print("- [{}] {} (id={}, revision={})".format(
                row.get("tier", "?"), row.get("title", row["id"]), row["id"], row["revision"]))
        if not rows:
            print("(queue empty)")


def parser():
    root = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    add_state_arguments(root)
    sub = root.add_subparsers(dest="command", required=True)
    seen = sub.add_parser("seen", help="0 if published revision, 1 if new; legacy hints do not count")
    seen.add_argument("id")
    seen.add_argument("--revision", required=True, help="current provider/content revision, never just an event ID")
    seen.add_argument("--family", default="legacy")
    seen.add_argument("--scope", default="{}", help="JSON scope")
    mark = sub.add_parser("mark", help="compatibility ack of one ID; a durable receipt is mandatory")
    mark.add_argument("id")
    mark.add_argument("--receipt", required=True)
    mark.add_argument("--tier", default="manual", help="legacy compatibility; receipt remains authoritative")
    mark.add_argument("--note", default="", help="legacy compatibility; does not prove publication")
    add = sub.add_parser("queue-add", help="queue a stable ID and revision; arbitrary evidence fields allowed")
    add.add_argument("--json", required=True)
    add.add_argument("--tier")
    listing = sub.add_parser("queue-list", help="show pending items (leases are visible in status)")
    listing.add_argument("--format", choices=("text", "json"), default="text")
    drain = sub.add_parser("queue-drain", help="lease pending/expired items; never steal a live batch")
    drain.add_argument("--owner", help="run ID; defaults to unique batch ID")
    drain.add_argument("--lease-seconds", type=int, default=900)
    drain.add_argument("--limit", type=int, default=100)
    drain.add_argument("--format", choices=("text", "json"), default="text")
    ack = sub.add_parser("queue-ack", help="ack actual receipt-covered items, not unrelated batch members")
    ack.add_argument("ids", nargs="*")
    ack.add_argument("--item-key", action="append", dest="item_keys")
    ack.add_argument("--batch")
    ack.add_argument("--receipt", required=True, help="publication ID returned after durable output recording")
    for command in ("queue-release", "queue-renew"):
        lease = sub.add_parser(command, help="explicit owner-only lease " + command.split("-")[1])
        lease.add_argument("--batch", required=True)
        lease.add_argument("--owner", required=True)
        if command == "queue-renew":
            lease.add_argument("--lease-seconds", type=int, default=900)
    record = sub.add_parser("publication-record", help="durably save output, exact included items and receipt")
    record.add_argument("--batch", help="owned queue batch; omit for standalone output with no queued membership")
    record.add_argument("--json", required=True,
                        help="content, ids OR item_keys, status prepared/available/published, receipt; optional id for replay")
    publish = sub.add_parser("publication-publish", help="establish availability/publication of a prepared output")
    publish.add_argument("publication_id")
    publish.add_argument("--json", required=True, help="status and receipt; see top-level --help")
    show = sub.add_parser("publication-show", help="retrieve durable, locally discoverable output and its status")
    show.add_argument("publication_id")
    outputs = sub.add_parser("publication-list", help="discover durable outputs and pending acknowledgements")
    outputs.add_argument("--limit", type=int, default=20)
    review = sub.add_parser("publication-review", help="record explicit human review, never inferred from delivery")
    review.add_argument("publication_id")
    review.add_argument("--at", help="explicit review timestamp; defaults to now")
    start = sub.add_parser("coverage-start", help="start scoped source attempt, preserving the successful checkpoint")
    start.add_argument("--json", required=True,
                       help="family, scope, window (requested bounds), run_id, capability, query_version, kind; "
                       "optional collection_window (fixed provider scope), cadence_seconds, resume_attempt, reauthenticated, reactivate")
    for command in ("coverage-page", "coverage-finish"):
        cov = sub.add_parser(command, help=("store page+observations atomically" if command.endswith("page")
                                           else "finish attempt; only proven complete enumeration advances coverage"))
        cov.add_argument("--attempt", required=True)
        cov.add_argument("--json", required=True, help="see complete JSON contract in top-level --help")
    sub.add_parser("coverage-status", help="coverage and failure episodes; tokens redacted")
    retire = sub.add_parser("coverage-retire", help="exclude a finished historical collection from current monitoring")
    retire.add_argument("--source-key", required=True)
    retire.add_argument("--reason", required=True)
    resume = sub.add_parser("coverage-resume", help="retrieve private continuation/checkpoint tokens; never put in briefs")
    resume.add_argument("--attempt", required=True)
    for command in ("cursor-get", "cursor-set"):
        cursor = sub.add_parser(command, help="legacy timestamp hint, NOT a successful source checkpoint")
        cursor.add_argument("name")
        if command == "cursor-set":
            cursor.add_argument("timestamp")
    legacy = sub.add_parser("import-legacy", help="atomic replay-safe JSON import; never confirms coverage/review")
    legacy.add_argument("--directory", required=True)
    prune = sub.add_parser("prune", help="expire leases only; never silently discard unpublished items/receipts")
    prune.add_argument("--days", type=int, default=30, help="compatibility option; durable receipts are retained")
    sub.add_parser("status", help="health JSON, or setup-needed without inventing an account")
    return root


def main(argv=None):
    args = parser().parse_args(argv)
    conn = None
    try:
        conn = connect(args.account, args.state_dir)
        initialize(conn)
        command = args.command
        if command == "queue-add":
            result = queue_add(conn, _input(args.json), args.tier)
        elif command == "queue-list":
            _queue_output([_render_item(r) for r in conn.execute(
                "SELECT * FROM proactive_items WHERE status='pending' ORDER BY created_at,item_key")], args.format)
            return 0
        elif command == "queue-drain":
            rows = queue_drain(conn, args.owner, args.lease_seconds, args.limit)
            if rows:
                print("batch {} owner {} expires {}".format(rows[0]["batch"], rows[0]["owner"],
                                                            rows[0]["lease_expires_at"]), file=sys.stderr)
            _queue_output(rows, args.format)
            return 0
        elif command == "queue-ack":
            if args.ids and args.item_keys:
                raise StateError("choose IDs or --item-key, not both")
            result = queue_ack(conn, args.receipt, args.batch, args.ids, args.item_keys)
        elif command == "mark":
            result = queue_ack(conn, args.receipt, ids=[args.id])
        elif command == "queue-release":
            result = queue_release(conn, args.batch, args.owner)
        elif command == "queue-renew":
            result = queue_renew(conn, args.batch, args.owner, args.lease_seconds)
        elif command == "publication-record":
            result = publication_record(conn, args.batch, _input(args.json))
        elif command == "publication-publish":
            result = publication_publish(conn, args.publication_id, _input(args.json))
        elif command == "publication-list":
            result = publication_list(conn, args.limit)
        elif command == "publication-show":
            row = conn.execute("SELECT * FROM proactive_publications WHERE id=?", (args.publication_id,)).fetchone()
            if row is None:
                raise StateError("unknown publication")
            result = dict(row)
            result["standalone"] = _standalone(conn, args.publication_id)
            result["receipt"] = json.loads(row["receipt"]) if row["receipt"] else None
            result["items"] = [dict(r) for r in conn.execute("""SELECT i.stable_id AS id,i.revision,i.item_key
                FROM proactive_items i JOIN proactive_publication_items pi ON i.item_key=pi.item_key
                WHERE pi.publication_id=?""", (args.publication_id,))]
        elif command == "publication-review":
            result = publication_review(conn, args.publication_id, args.at)
        elif command == "coverage-start":
            result = coverage_start(conn, _input(args.json))
        elif command == "coverage-page":
            result = coverage_page(conn, args.attempt, _input(args.json))
        elif command == "coverage-finish":
            result = coverage_finish(conn, args.attempt, _input(args.json))
        elif command == "coverage-status":
            result = coverage_status(conn)
        elif command == "coverage-retire":
            result = coverage_retire(conn, args.source_key, args.reason)
        elif command == "coverage-resume":
            attempt = _attempt(conn, args.attempt)
            source = conn.execute("SELECT * FROM proactive_sources WHERE source_key=?",
                                  (attempt["source_key"],)).fetchone()
            contract = conn.execute("SELECT value FROM proactive_meta WHERE key=?",
                                    ("token-contract:" + attempt["source_key"],)).fetchone()
            incompatible = source["delta_token"] is not None and (
                contract is None or contract[0] != canonical_json(
                    [attempt["capability"], attempt["query_version"], attempt["kind"]]))
            result = {"attempt_id": args.attempt, "status": attempt["status"], "next_page": attempt["pages"] + 1,
                      "continuation": attempt["continuation"],
                      "delta_token": None if incompatible else source["delta_token"],
                      "checkpoint_at": source["checkpoint_at"], "watermark": source["watermark"],
                      "resync_required": incompatible or attempt["error_class"] == "expired_token" or conn.execute(
                          "SELECT 1 FROM proactive_meta WHERE key=?", ("resync:" + args.attempt,)).fetchone() is not None,
                      "blocked": attempt["error_class"] in ("access_denied", "authentication_required"),
                      "note": "Tokens are opaque/private. Revalidate access and capability before retrying."}
        elif command == "cursor-get":
            row = conn.execute("SELECT timestamp FROM proactive_legacy_cursors WHERE name=?", (args.name,)).fetchone()
            print(row[0] if row else "")
            print("legacy hint only; not confirmed source coverage", file=sys.stderr)
            return 0 if row else 1
        elif command == "cursor-set":
            timestamp = validate_timestamp(args.timestamp)
            with conn:
                conn.execute("INSERT INTO proactive_legacy_cursors VALUES (?,?) "
                             "ON CONFLICT(name) DO UPDATE SET timestamp=excluded.timestamp", (args.name, timestamp))
            result = {"name": args.name, "timestamp": timestamp, "confirmed_coverage": False}
        elif command == "seen":
            try:
                scope = canonical_json(json.loads(args.scope))
            except json.JSONDecodeError as exc:
                raise StateError("scope must be JSON") from exc
            row = conn.execute("""SELECT status FROM proactive_items WHERE stable_id=? AND revision=?
                AND family=? AND scope=? AND status='published'""",
                               (args.id, args.revision, args.family, scope)).fetchone()
            _emit({"id": args.id, "revision": args.revision, "published": row is not None})
            return 0 if row else 1
        elif command == "import-legacy":
            result = import_legacy(conn, args.directory)
        elif command == "prune":
            integer(args.days, "days", 1)
            with conn:
                conn.execute("BEGIN IMMEDIATE")
                released = _release_expired(conn, utc_now())
            result = {"expired_batches_released": released, "unpublished_items_deleted": 0,
                      "note": "Durable observations/receipts are retained; back up before explicit retention maintenance."}
        else:
            result = status_report(conn)
        _emit(result)
        return 0
    except SetupRequired as exc:
        if args.command == "status":
            _emit({"status": "setup-needed", "account_configured": False, "all_clear": False,
                   "action": str(exc)})
            return 0
        print("ERROR: " + str(exc), file=sys.stderr)
        return 2
    except (StateError, sqlite3.Error, OSError, UnicodeError, ValueError, TypeError, KeyError) as exc:
        message = str(exc) if isinstance(exc, StateError) else "storage unavailable; no reset performed"
        print("ERROR: " + message, file=sys.stderr)
        return 2
    finally:
        if conn is not None:
            conn.close()


if __name__ == "__main__":
    sys.exit(main())

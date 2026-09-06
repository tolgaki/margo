"""Account-scoped work ledger and foreground execution journal (no network access)."""

import contextlib
import hashlib
import json
import os
import sqlite3
import sys
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path

import margo_store

StateError = margo_store.StateError
canonical_json = margo_store.canonical_json
utc_now = margo_store.utc_now

ITEM_STATES = {
    "candidate", "confirmed", "active", "waiting", "resolved", "rejected",
    "deferred", "cancelled",
}
ITEM_TRANSITIONS = {
    "candidate": {"confirmed", "rejected", "deferred", "cancelled"},
    "confirmed": {"active", "waiting", "resolved", "deferred", "cancelled"},
    "active": {"waiting", "resolved", "deferred", "cancelled"},
    "waiting": {"active", "resolved", "deferred", "cancelled"},
    "deferred": {"candidate", "confirmed", "active", "waiting", "rejected", "cancelled"},
    "resolved": {"active"},
    "rejected": {"candidate"},
    "cancelled": {"active", "candidate"},
}
SCHEMA = """
CREATE TABLE IF NOT EXISTS work_sources (
 id TEXT PRIMARY KEY, account TEXT NOT NULL, family TEXT NOT NULL, scope TEXT NOT NULL,
 external_id TEXT NOT NULL, current_revision TEXT NOT NULL, created_at TEXT NOT NULL,
 UNIQUE(account,family,scope,external_id)
);
CREATE TABLE IF NOT EXISTS work_source_revisions (
 source_id TEXT NOT NULL REFERENCES work_sources(id), revision TEXT NOT NULL,
 fingerprint TEXT NOT NULL, data TEXT NOT NULL, observed_at TEXT NOT NULL,
 PRIMARY KEY(source_id,revision)
);
CREATE TABLE IF NOT EXISTS work_items (
 id TEXT PRIMARY KEY, account TEXT NOT NULL, revision INTEGER NOT NULL,
 state TEXT NOT NULL, confirmed INTEGER NOT NULL DEFAULT 0, ingest_key TEXT UNIQUE,
 ingest_hash TEXT, data TEXT NOT NULL, confirmation TEXT, created_at TEXT NOT NULL,
 updated_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS work_item_revisions (
 item_id TEXT NOT NULL REFERENCES work_items(id), revision INTEGER NOT NULL,
 snapshot TEXT NOT NULL, evidence TEXT, created_at TEXT NOT NULL,
 PRIMARY KEY(item_id,revision)
);
CREATE TABLE IF NOT EXISTS work_relationships (
 source_id TEXT NOT NULL REFERENCES work_items(id), kind TEXT NOT NULL,
 target_id TEXT NOT NULL, evidence TEXT, created_at TEXT NOT NULL,
 PRIMARY KEY(source_id,kind,target_id)
);
CREATE TABLE IF NOT EXISTS work_actions (
 id TEXT PRIMARY KEY, account TEXT NOT NULL, revision INTEGER NOT NULL,
 state TEXT NOT NULL, deferred_until TEXT, created_at TEXT NOT NULL,
 updated_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS work_action_revisions (
 action_id TEXT NOT NULL REFERENCES work_actions(id), revision INTEGER NOT NULL,
 action_hash TEXT NOT NULL, data TEXT NOT NULL, created_at TEXT NOT NULL,
 PRIMARY KEY(action_id,revision)
);
CREATE TABLE IF NOT EXISTS work_action_sources (
 action_id TEXT NOT NULL, action_revision INTEGER NOT NULL,
 source_id TEXT NOT NULL, source_revision TEXT NOT NULL, fingerprint TEXT NOT NULL,
 PRIMARY KEY(action_id,action_revision,source_id),
 FOREIGN KEY(action_id,action_revision) REFERENCES work_action_revisions(action_id,revision),
 FOREIGN KEY(source_id,source_revision) REFERENCES work_source_revisions(source_id,revision)
);
CREATE TABLE IF NOT EXISTS work_approvals (
 id TEXT PRIMARY KEY, account TEXT NOT NULL, action_id TEXT NOT NULL,
 revision INTEGER NOT NULL, action_hash TEXT NOT NULL, evidence TEXT NOT NULL,
 expires_at TEXT NOT NULL, invalidated_at TEXT, created_at TEXT NOT NULL,
 FOREIGN KEY(action_id,revision) REFERENCES work_action_revisions(action_id,revision)
);
CREATE TABLE IF NOT EXISTS work_executions (
 id TEXT PRIMARY KEY, action_id TEXT NOT NULL, revision INTEGER NOT NULL,
 approval_id TEXT NOT NULL REFERENCES work_approvals(id), state TEXT NOT NULL,
 preflight TEXT NOT NULL, receipt TEXT, started_at TEXT NOT NULL, finished_at TEXT,
 FOREIGN KEY(action_id,revision) REFERENCES work_action_revisions(action_id,revision),
 UNIQUE(action_id,revision)
);
CREATE TABLE IF NOT EXISTS work_events (
 seq INTEGER PRIMARY KEY AUTOINCREMENT, account TEXT NOT NULL, entity_id TEXT NOT NULL,
 event TEXT NOT NULL, data TEXT NOT NULL, created_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS work_records (
 id TEXT PRIMARY KEY, account TEXT NOT NULL, kind TEXT NOT NULL, revision INTEGER NOT NULL,
 state TEXT NOT NULL, identity_key TEXT, data TEXT NOT NULL, created_at TEXT NOT NULL,
 updated_at TEXT NOT NULL, UNIQUE(account,kind,identity_key)
);
CREATE TABLE IF NOT EXISTS work_record_revisions (
 record_id TEXT NOT NULL REFERENCES work_records(id), revision INTEGER NOT NULL,
 snapshot TEXT NOT NULL, evidence TEXT, created_at TEXT NOT NULL,
 PRIMARY KEY(record_id,revision)
);
CREATE TABLE IF NOT EXISTS work_record_sources (
 record_id TEXT NOT NULL REFERENCES work_records(id), record_revision INTEGER NOT NULL,
 source_id TEXT NOT NULL, source_revision TEXT NOT NULL, fingerprint TEXT NOT NULL,
 PRIMARY KEY(record_id,record_revision,source_id),
 FOREIGN KEY(source_id,source_revision) REFERENCES work_source_revisions(source_id,revision)
);
CREATE TABLE IF NOT EXISTS work_imports (
 digest TEXT PRIMARY KEY, evidence TEXT NOT NULL, item_ids TEXT NOT NULL, created_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS work_exports (
 path TEXT PRIMARY KEY, digest TEXT NOT NULL, created_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS work_meta (
 key TEXT PRIMARY KEY, value TEXT NOT NULL
);
"""


def digest(value):
    return hashlib.sha256(canonical_json(value).encode("utf-8")).hexdigest()


def text(value, field):
    if not isinstance(value, str) or not value.strip():
        raise StateError("%s must be a nonempty string" % field)
    return value


def timestamp(value):
    text(value, "timestamp")
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
        if parsed.tzinfo is None:
            raise ValueError()
        return parsed.astimezone(timezone.utc)
    except ValueError:
        raise StateError("timestamp requires an ISO datetime with UTC offset")


def now():
    return datetime.now(timezone.utc)


def human(evidence, subject_id, revision, decision=None):
    """Validate a recorded human decision, not authenticate the caller as a human."""
    if not isinstance(evidence, dict) or evidence.get("kind") != "human_confirmation":
        raise StateError("explicit human_confirmation evidence is required")
    for key in ("actor", "statement", "evidence_ref", "decision"):
        text(evidence.get(key), key)
    if not evidence["evidence_ref"].startswith(("conversation:", "host-interaction:", "legacy-review:")):
        raise StateError("human evidence must reference a conversation, host interaction, or legacy review")
    if (evidence.get("subject_id") != subject_id or type(evidence.get("revision")) is not int
            or evidence.get("revision") != revision):
        raise StateError("human evidence must name the exact subject and revision")
    if decision and evidence["decision"] != decision:
        raise StateError("human evidence must record the %s decision" % decision)
    if timestamp(evidence.get("decided_at")) > now() + timedelta(minutes=1):
        raise StateError("human decision cannot be in the future")
    return evidence


def check_revision(row, revision):
    if isinstance(revision, bool) or row["revision"] != revision:
        raise StateError("revision conflict: current revision is %s" % row["revision"])


class Ledger:
    def __init__(self, account=None, state_root=None):
        self.account = margo_store.resolve_account(account)
        self.conn = margo_store.connect(account=self.account, state_root=state_root)
        self._owns_connection = True
        try:
            self.conn.execute("PRAGMA foreign_keys=ON")
            self._initialize_schema()
        except Exception:
            self.conn.close()
            raise

    @classmethod
    def from_connection(cls, connection, account):
        """Borrow an initialized account connection for atomic cross-namespace operations."""
        principal = margo_store.resolve_account(account)
        metadata = dict(connection.execute("SELECT key,value FROM margo_meta"))
        if metadata.get("account") != principal or metadata.get("store_version") != "1":
            raise StateError("borrowed ledger connection account/schema mismatch")
        if metadata.get("work_schema_version") != "1":
            raise margo_store.NotInitialized("Work ledger is not initialized; initialize task/work state explicitly.")
        if connection.row_factory is not sqlite3.Row:
            raise StateError("borrowed ledger connection requires sqlite3.Row results")
        result = cls.__new__(cls)
        result.account, result.conn, result._owns_connection = principal, connection, False
        result._initialize_schema(read_only=True)
        return result

    def _initialize_schema(self, read_only=False):
        statements = [part.strip() for part in SCHEMA.split(";") if part.strip()]
        expected = {statement.split()[5] for statement in statements
                    if statement.startswith("CREATE TABLE")}
        with margo_store.transaction(self.conn, read_only=read_only):
            all_tables = {row[0] for row in self.conn.execute(
                "SELECT name FROM sqlite_master WHERE type='table'")}
            existing = {name for name in all_tables if name.startswith("work_")}
            global_marker = None
            if "margo_meta" in all_tables:
                global_marker = self.conn.execute(
                    "SELECT value FROM margo_meta WHERE key='work_schema_version'").fetchone()
            legacy = existing == expected - {"work_meta"} and global_marker is None
            if existing and existing != expected and not legacy:
                raise StateError("incomplete work schema; refusing to recreate missing history")
            if global_marker is not None and (global_marker[0] != "1" or "work_meta" not in existing):
                raise StateError("work schema marker mismatch; explicit recovery required")
            if "work_meta" in existing:
                marker = self.conn.execute("SELECT value FROM work_meta WHERE key='schema_version'").fetchone()
                if marker is None or marker[0] != "1":
                    raise StateError("unsupported or missing work schema version")
            if existing:
                reference = sqlite3.connect(":memory:")
                try:
                    reference.executescript(SCHEMA)
                    for table in existing:
                        for pragma in ("table_info", "foreign_key_list"):
                            actual = [tuple(row) for row in self.conn.execute(
                                "PRAGMA %s(%s)" % (pragma, table))]
                            wanted = [tuple(row) for row in reference.execute(
                                "PRAGMA %s(%s)" % (pragma, table))]
                            if actual != wanted:
                                raise StateError("work schema contract mismatch: " + table)
                finally:
                    reference.close()
            if read_only:
                return
            for statement in statements:
                self.conn.execute(statement)
            self.conn.execute("INSERT OR IGNORE INTO work_meta VALUES ('schema_version','1')")
            if "margo_meta" in all_tables:
                self.conn.execute("INSERT OR IGNORE INTO margo_meta VALUES ('work_schema_version','1')")

    def close(self):
        if getattr(self, "_owns_connection", True):
            self.conn.close()

    @contextlib.contextmanager
    def transaction(self):
        with margo_store.transaction(self.conn):
            yield

    def invalidate_approval(self, action_id, revision, action_hash, reason):
        """Revoke only an exact unused approval; never alter a newer or executed action."""
        text(reason, "invalidation reason")
        with self.transaction():
            action = self.show(action_id)
            if (action.get("type") != "action" or action["revision"] != revision
                    or action["action_hash"] != action_hash):
                raise StateError("approval invalidation requires the exact current action")
            if action["state"] in {"executing", "succeeded", "partial", "outcome_unknown"}:
                return {"invalidated": False, "reason": "execution_already_started"}
            self._invalidate(action_id, "task_cancelled", {"reason": reason})
            return {"invalidated": True, "action_id": action_id, "revision": revision}

    def event(self, entity_id, event, data):
        self.conn.execute(
            "INSERT INTO work_events(account,entity_id,event,data,created_at) VALUES(?,?,?,?,?)",
            (self.account, entity_id, event, canonical_json(data), utc_now()),
        )

    def row(self, table, entity_id):
        if table not in {"work_items", "work_actions", "work_records", "work_sources"}:
            raise StateError("unsupported entity table")
        row = self.conn.execute(
            "SELECT * FROM %s WHERE id=? AND account=?" % table,
            (entity_id, self.account),
        ).fetchone()
        if row is None:
            raise StateError("unknown entity: %s" % entity_id)
        return dict(row)

    def source(self, family, scope, external_id, revision, evidence, web_link,
               sensitivity="unknown", observed_at=None):
        for key, value in (("family", family), ("scope", scope), ("external_id", external_id),
                           ("revision", revision), ("web_link", web_link)):
            text(value, key)
        if not isinstance(evidence, (dict, str)) or not evidence:
            raise StateError("source evidence must be nonempty")
        observed_at = observed_at or utc_now()
        timestamp(observed_at)
        data = {"evidence": evidence, "web_link": web_link, "sensitivity": sensitivity}
        fingerprint = digest(data)
        source_id = "src_" + digest([self.account, family, scope, external_id])[:32]
        with self.transaction():
            old = self.conn.execute(
                "SELECT * FROM work_source_revisions WHERE source_id=? AND revision=?",
                (source_id, revision),
            ).fetchone()
            if old:
                if old["fingerprint"] != fingerprint:
                    raise StateError("same source revision has different evidence; use a new revision")
                return {"source_id": source_id, "revision": revision, "fingerprint": fingerprint}
            self.conn.execute(
                "INSERT OR IGNORE INTO work_sources VALUES(?,?,?,?,?,?,?)",
                (source_id, self.account, family, scope, external_id, revision, utc_now()),
            )
            self.conn.execute(
                "INSERT INTO work_source_revisions VALUES(?,?,?,?,?)",
                (source_id, revision, fingerprint, canonical_json(data), observed_at),
            )
            self.conn.execute("UPDATE work_sources SET current_revision=? WHERE id=?", (revision, source_id))
            linked = self.conn.execute("""
                SELECT DISTINCT a.id FROM work_actions a JOIN work_action_sources s
                ON a.id=s.action_id AND a.revision=s.action_revision
                WHERE s.source_id=? AND s.source_revision<>?
                AND a.state IN ('ready','approved','deferred','stale')
            """, (source_id, revision)).fetchall()
            for action in linked:
                self._invalidate(action["id"], "source_changed", {"source_id": source_id, "revision": revision})
            # Staleness is an observation, not an approved content edit.
            records = self.conn.execute("""
                SELECT DISTINCT r.id FROM work_records r JOIN work_record_sources s
                ON r.id=s.record_id AND r.revision=s.record_revision
                WHERE s.source_id=? AND s.source_revision<>?
            """, (source_id, revision)).fetchall()
            for record in records:
                self.event(record["id"], "source_changed", {"source_id": source_id, "revision": revision})
            self.event(source_id, "observed", {"revision": revision, "fingerprint": fingerprint})
        return {"source_id": source_id, "revision": revision, "fingerprint": fingerprint}

    def source_refs(self, refs, require_current=True):
        if not isinstance(refs, list):
            raise StateError("source_refs must be a list")
        result, seen = [], set()
        for ref in refs:
            if not isinstance(ref, dict) or set(ref) != {"source_id", "revision", "fingerprint"}:
                raise StateError("each source ref requires source_id, revision, fingerprint")
            if ref["source_id"] in seen:
                raise StateError("duplicate source reference")
            seen.add(ref["source_id"])
            source = self.row("work_sources", ref["source_id"])
            known = self.conn.execute(
                "SELECT fingerprint FROM work_source_revisions WHERE source_id=? AND revision=?",
                (ref["source_id"], ref["revision"]),
            ).fetchone()
            if not known or known["fingerprint"] != ref["fingerprint"]:
                raise StateError("unknown or mismatched source fingerprint")
            if require_current and source["current_revision"] != ref["revision"]:
                raise StateError("stale source revision")
            result.append(dict(ref))
        return sorted(result, key=lambda ref: ref["source_id"])

    def refs_stale(self, refs):
        return any(self.row("work_sources", r["source_id"])["current_revision"] != r["revision"] for r in refs)

    def source_evidence(self, refs):
        result = []
        for ref in refs:
            row = self.conn.execute("""
                SELECT s.family,s.scope,s.external_id,s.current_revision,r.data,r.observed_at
                FROM work_sources s JOIN work_source_revisions r ON s.id=r.source_id
                WHERE s.id=? AND r.revision=? AND s.account=?
            """, (ref["source_id"], ref["revision"], self.account)).fetchone()
            if not row:
                raise StateError("source evidence is missing")
            metadata = dict(row)
            metadata.update(json.loads(metadata.pop("data")))
            metadata.update(ref)
            result.append(metadata)
        return result

    def _item_data(self, data, require_current=True):
        if not isinstance(data, dict):
            raise StateError("item data must be an object")
        allowed = {"title", "owner", "direction", "due", "source_refs", "confidence",
                   "outcome_id", "next_step", "blocker", "deferred_until", "notes", "confirmation_source"}
        if set(data) - allowed:
            raise StateError("unknown item fields: %s" % ", ".join(sorted(set(data) - allowed)))
        text(data.get("title"), "title")
        if data.get("direction") not in {"owe", "waiting_on", "own"}:
            raise StateError("direction must be owe, waiting_on, or own")
        for key in ("owner", "due"):
            if key not in data:
                raise StateError("%s is required; use null for unknown" % key)
            if data[key] is not None:
                text(data[key], key)
        data = dict(data)
        data["source_refs"] = self.source_refs(data.get("source_refs", []), require_current=require_current)
        if not data["source_refs"] and not data.get("confirmation_source"):
            raise StateError("item requires source evidence or attributed direct human instruction")
        if data.get("deferred_until"):
            timestamp(data["deferred_until"])
        if data.get("outcome_id"):
            if self.row("work_records", data["outcome_id"])["kind"] != "outcome":
                raise StateError("outcome_id must refer to an outcome")
        return data

    def ingest(self, data, claim_key):
        """Exact revision + extractor claim key dedupe; never semantic merging."""
        text(claim_key, "claim_key")
        with self.transaction():
            data = self._item_data(data, require_current=False)
            ingest_key = digest([self.account, data["source_refs"], data.get("confirmation_source"), claim_key])
            content_hash = digest(data)
            existing = self.conn.execute("SELECT id,ingest_hash FROM work_items WHERE ingest_key=?", (ingest_key,)).fetchone()
            if existing:
                if existing["ingest_hash"] != content_hash:
                    raise StateError("same ingest identity has different content; explicitly edit the existing item")
                return self.show(existing["id"])
            item_id = "item_" + ingest_key[:32]
            stamp = utc_now()
            self.conn.execute(
                "INSERT INTO work_items VALUES(?,?,?,?,?,?,?,?,?,?,?)",
                (item_id, self.account, 1, "candidate", 0, ingest_key, content_hash,
                 canonical_json(data), None, stamp, stamp),
            )
            self._item_snapshot(item_id, None)
            self.event(item_id, "ingested", {"claim_key": claim_key})
            return self.show(item_id)

    def _item_snapshot(self, item_id, evidence):
        row = self.row("work_items", item_id)
        self.conn.execute("INSERT INTO work_item_revisions VALUES(?,?,?,?,?)",
                          (item_id, row["revision"], canonical_json(row),
                           canonical_json(evidence) if evidence else None, utc_now()))

    def update_item(self, item_id, revision, state=None, patch=None, evidence=None):
        with self.transaction():
            row = self.row("work_items", item_id)
            check_revision(row, revision)
            state = state or row["state"]
            if state not in ITEM_STATES:
                raise StateError("invalid work state")
            if state != row["state"] and state not in ITEM_TRANSITIONS[row["state"]]:
                raise StateError("invalid work transition: %s -> %s" % (row["state"], state))
            if row["confirmed"] and state in {"candidate", "rejected"}:
                raise StateError("confirmed work cannot be demoted to an unconfirmed candidate")
            if not row["confirmed"] and state in {"active", "waiting", "resolved"}:
                raise StateError("unconfirmed work must pass through confirmed before becoming authoritative")
            if row["confirmed"] or state in {"confirmed", "active", "waiting", "resolved"}:
                decision = "edit" if state == row["state"] else {
                    "confirmed": "confirm", "active": "activate", "waiting": "wait",
                    "resolved": "resolve", "deferred": "defer", "cancelled": "cancel",
                }[state]
                human(evidence, item_id, revision, decision)
            data = json.loads(row["data"])
            data.update(patch or {})
            data = self._item_data(data)
            if state == "deferred" and not data.get("deferred_until"):
                raise StateError("deferral requires deferred_until")
            if state != "deferred":
                data.pop("deferred_until", None)
            confirmed = bool(row["confirmed"] or state == "confirmed")
            confirmation = row["confirmation"] or (canonical_json(evidence) if confirmed else None)
            self.conn.execute(
                "UPDATE work_items SET revision=revision+1,state=?,confirmed=?,data=?,confirmation=?,updated_at=? WHERE id=?",
                (state, int(confirmed), canonical_json(data), confirmation, utc_now(), item_id),
            )
            self._item_snapshot(item_id, evidence)
            self.event(item_id, "updated", {"from_revision": revision, "state": state, "evidence": evidence})
            return self.show(item_id)

    def relate(self, source_id, kind, target_id, revision, evidence=None):
        if kind not in {"blocks", "depends_on", "supersedes", "tracked_in"}:
            raise StateError("unsupported relationship")
        with self.transaction():
            source = self.row("work_items", source_id)
            check_revision(source, revision)
            target = self.row("work_items", target_id) if kind != "tracked_in" else None
            if source_id == target_id:
                raise StateError("self relationship is invalid")
            if kind == "tracked_in":
                text(target_id, "canonical tracker reference")
                if ":" not in target_id:
                    raise StateError("tracked_in requires a namespaced canonical tracker reference")
            if source["confirmed"] or (target and target["confirmed"]):
                human(evidence, source_id, revision, "relate")
            edges = []
            for row in self.conn.execute("SELECT source_id,kind,target_id FROM work_relationships"):
                if kind in {"blocks", "depends_on"} and row["kind"] in {"blocks", "depends_on"}:
                    edges.append((row["target_id"], row["source_id"]) if row["kind"] == "blocks"
                                 else (row["source_id"], row["target_id"]))
                elif kind == "supersedes" and row["kind"] == kind:
                    edges.append((row["source_id"], row["target_id"]))
            start, end = (target_id, source_id) if kind == "blocks" else (source_id, target_id)
            pending, seen = [end], set()
            while pending:
                node = pending.pop()
                if node == start:
                    raise StateError("relationship would create a cycle")
                if node not in seen:
                    seen.add(node)
                    pending.extend(b for a, b in edges if a == node)
            changed = self.conn.execute(
                "INSERT OR IGNORE INTO work_relationships VALUES(?,?,?,?,?)",
                (source_id, kind, target_id, canonical_json(evidence) if evidence else None, utc_now()),
            ).rowcount
            if changed:
                self.conn.execute("UPDATE work_items SET revision=revision+1,updated_at=? WHERE id=?", (utc_now(), source_id))
                self._item_snapshot(source_id, evidence)
                self.event(source_id, "relationship_added", {"kind": kind, "target_id": target_id, "evidence": evidence})
            return self.show(source_id)

    def _action_data(self, data):
        required = {"kind", "target", "payload", "why", "source_refs", "target_fingerprint", "work_item_id"}
        optional = {"affected_people", "artifact_id", "artifact_revision", "dependencies"}
        if not isinstance(data, dict) or not required <= set(data) or set(data) - required - optional:
            raise StateError("action requires exact kind,target,payload,why,source_refs,target_fingerprint,work_item_id")
        for key in ("kind", "why", "target_fingerprint"):
            text(data[key], key)
        for key in ("target", "payload"):
            if not isinstance(data[key], dict):
                raise StateError("%s must be an object containing the exact reviewed fields" % key)
        if not data["target"]:
            raise StateError("target must identify exact recipients or target IDs")
        if data["work_item_id"] is not None:
            self.row("work_items", data["work_item_id"])
        data = dict(data)
        data["source_refs"] = self.source_refs(data["source_refs"])
        for key in ("affected_people", "dependencies"):
            if key in data and (not isinstance(data[key], list)
                                or not all(isinstance(value, str) and value.strip() for value in data[key])):
                raise StateError("%s must be a list of explicit people or work IDs" % key)
        if ("artifact_id" in data) != ("artifact_revision" in data):
            raise StateError("artifact_id and artifact_revision must be supplied together")
        for item_id in data.get("dependencies", []):
            self.row("work_items", item_id)
        if data.get("artifact_id"):
            artifact = self.show(data["artifact_id"])
            if artifact.get("kind") != "artifact" or artifact["revision"] != data.get("artifact_revision"):
                raise StateError("artifact must name an exact current artifact revision")
            if artifact["stale"]:
                raise StateError("artifact is stale")
        return data

    def _put_action_revision(self, action_id, revision, data):
        action_hash = digest({"account": self.account, "id": action_id, "revision": revision, "action": data})
        self.conn.execute("INSERT INTO work_action_revisions VALUES(?,?,?,?,?)",
                          (action_id, revision, action_hash, canonical_json(data), utc_now()))
        for ref in data["source_refs"]:
            self.conn.execute("INSERT INTO work_action_sources VALUES(?,?,?,?,?)",
                              (action_id, revision, ref["source_id"], ref["revision"], ref["fingerprint"]))

    def _check_action_hash(self, action_id, revision, expected_hash):
        stored = self.conn.execute(
            "SELECT action_hash FROM work_action_revisions WHERE action_id=? AND revision=?",
            (action_id, revision),
        ).fetchone()
        if expected_hash is not None and (not stored or stored["action_hash"] != expected_hash):
            raise StateError("action hash conflict; reload the current revision")

    def propose(self, data):
        with self.transaction():
            data = self._action_data(data)
            action_id, stamp = "act_" + uuid.uuid4().hex, utc_now()
            self.conn.execute("INSERT INTO work_actions VALUES(?,?,?,?,?,?,?)",
                              (action_id, self.account, 1, "ready", None, stamp, stamp))
            self._put_action_revision(action_id, 1, data)
            self.event(action_id, "proposed", {"revision": 1})
            return self.show(action_id)

    def _invalidate(self, action_id, reason, data=None, state="stale"):
        self.conn.execute(
            "UPDATE work_approvals SET invalidated_at=? WHERE action_id=? AND invalidated_at IS NULL",
            (utc_now(), action_id),
        )
        self.conn.execute("UPDATE work_actions SET state=?,updated_at=? WHERE id=?", (state, utc_now(), action_id))
        self.event(action_id, reason, data or {})

    def edit_action(self, action_id, revision, data, expected_hash=None):
        with self.transaction():
            row = self.row("work_actions", action_id)
            check_revision(row, revision)
            self._check_action_hash(action_id, revision, expected_hash)
            if row["state"] in {"executing", "succeeded", "partial", "outcome_unknown"}:
                raise StateError("cannot edit an executed or unresolved action; reconcile first")
            data = self._action_data(data)
            self._invalidate(action_id, "edited", {"from_revision": revision}, state="ready")
            self.conn.execute("UPDATE work_actions SET revision=revision+1,deferred_until=NULL WHERE id=?", (action_id,))
            self._put_action_revision(action_id, revision + 1, data)
            return self.show(action_id)

    def disposition(self, action_id, revision, state, until=None, expected_hash=None):
        if state not in {"deferred", "dismissed"}:
            raise StateError("invalid proposal disposition")
        if state == "deferred" and timestamp(until) <= now():
            raise StateError("defer until must be in the future")
        with self.transaction():
            row = self.row("work_actions", action_id)
            check_revision(row, revision)
            self._check_action_hash(action_id, revision, expected_hash)
            if row["state"] in {"executing", "succeeded", "partial", "outcome_unknown"}:
                raise StateError("cannot hide an unresolved or completed execution")
            snapshot = self.conn.execute(
                "SELECT data FROM work_action_revisions WHERE action_id=? AND revision=?", (action_id, revision)
            ).fetchone()
            self._invalidate(action_id, state, {"until": until}, state=state)
            self.conn.execute("UPDATE work_actions SET revision=revision+1,deferred_until=? WHERE id=?", (until, action_id))
            self._put_action_revision(action_id, revision + 1, json.loads(snapshot["data"]))
            return self.show(action_id)

    def _fresh_action(self, action):
        self.source_refs(action["source_refs"])
        if action.get("artifact_id"):
            artifact = self.show(action["artifact_id"])
            if artifact["revision"] != action["artifact_revision"] or artifact["stale"]:
                raise StateError("linked artifact changed; create a new action revision")
        for item_id in action.get("dependencies", []):
            if self.row("work_items", item_id)["state"] != "resolved":
                raise StateError("action dependency is not resolved: %s" % item_id)

    def approve(self, action_id, revision, action_hash, evidence, expires_at):
        with self.transaction():
            action = self.show(action_id)
            if action["type"] != "action":
                raise StateError("only actions use delivery approvals")
            check_revision(action, revision)
            if action["state"] not in {"ready", "approved"}:
                raise StateError("action is not ready for approval; explicitly edit/review it first")
            if action["action_hash"] != action_hash:
                raise StateError("approval hash does not match displayed action")
            human(evidence, action_id, revision, "approve")
            if evidence.get("action_hash") != action_hash:
                raise StateError("human evidence must include the displayed action_hash")
            expiry = timestamp(expires_at)
            if expiry <= now() or expiry > now() + timedelta(days=7):
                raise StateError("approval expiry must be in the future and within seven days")
            self._fresh_action(action)
            self.conn.execute(
                "UPDATE work_approvals SET invalidated_at=? WHERE action_id=? AND invalidated_at IS NULL",
                (utc_now(), action_id),
            )
            approval_id = "approval_" + uuid.uuid4().hex
            self.conn.execute("INSERT INTO work_approvals VALUES(?,?,?,?,?,?,?,?,?)",
                              (approval_id, self.account, action_id, revision, action_hash,
                               canonical_json(evidence), expires_at, None, utc_now()))
            self.conn.execute("UPDATE work_actions SET state='approved',updated_at=? WHERE id=?", (utc_now(), action_id))
            self.event(action_id, "approved", {"approval_id": approval_id, "evidence": evidence, "expires_at": expires_at})
            return self.show(action_id)

    def begin(self, action_id, revision, fresh):
        """Return the exact gated tool payload once, before the foreground agent writes."""
        stale_reason = None
        result = None
        with self.transaction():
            action = self.show(action_id)
            check_revision(action, revision)
            if action.get("type") != "action" or action["state"] != "approved":
                raise StateError("action has no executable approval or has already started")
            approval = self.conn.execute("""
                SELECT * FROM work_approvals WHERE action_id=? AND revision=?
                AND invalidated_at IS NULL ORDER BY created_at DESC LIMIT 1
            """, (action_id, revision)).fetchone()
            if not approval or timestamp(approval["expires_at"]) <= now():
                raise StateError("approval has expired or was invalidated")
            if approval["account"] != self.account or approval["action_hash"] != action["action_hash"]:
                raise StateError("approval account/hash mismatch")
            try:
                self._fresh_action(action)
            except StateError as exc:
                stale_reason = str(exc)
            if not isinstance(fresh, dict) or set(fresh) != {"checked_at", "target_fingerprint", "source_refs"}:
                raise StateError("fresh preflight requires checked_at,target_fingerprint,source_refs")
            age = now() - timestamp(fresh["checked_at"])
            if age < timedelta(seconds=-30) or age > timedelta(minutes=5):
                raise StateError("preflight must be a fresh foreground read within five minutes")
            if fresh["target_fingerprint"] != action["target_fingerprint"]:
                stale_reason = "target changed; revise and request a new approval"
            try:
                if self.source_refs(fresh["source_refs"]) != action["source_refs"]:
                    stale_reason = "source changed; revise and request a new approval"
            except StateError as exc:
                stale_reason = str(exc)
            if stale_reason:
                self._invalidate(action_id, "preflight_changed", {"reason": stale_reason})
            else:
                attempt_id = "exec_" + uuid.uuid4().hex
                self.conn.execute("INSERT INTO work_executions VALUES(?,?,?,?,?,?,?,?,?)",
                                  (attempt_id, action_id, revision, approval["id"], "executing",
                                   canonical_json(fresh), None, utc_now(), None))
                self.conn.execute("UPDATE work_actions SET state='executing',updated_at=? WHERE id=?", (utc_now(), action_id))
                self.event(action_id, "execution_started", {"attempt_id": attempt_id, "revision": revision})
                result = {"account": self.account, "action_id": action_id, "revision": revision,
                          "attempt_id": attempt_id, "action_hash": action["action_hash"],
                          "kind": action["kind"], "target": action["target"], "payload": action["payload"],
                          "instruction": "Foreground Work IQ executor only. Do not retry an uncertain write."}
        if stale_reason:
            raise StateError(stale_reason)
        return result

    def finish(self, attempt_id, state, receipt=None, reconcile=False):
        if state not in {"succeeded", "failed", "partial", "outcome_unknown"}:
            raise StateError("invalid execution outcome")
        if state != "outcome_unknown" or reconcile:
            self.validate_receipt(receipt, state)
        with self.transaction():
            attempt = self.conn.execute("SELECT * FROM work_executions WHERE id=?", (attempt_id,)).fetchone()
            if not attempt:
                raise StateError("unknown attempt")
            action = self.row("work_actions", attempt["action_id"])
            allowed = {"executing", "partial", "outcome_unknown"} if reconcile else {"executing"}
            if attempt["state"] not in allowed or action["state"] not in allowed:
                raise StateError("execution is already settled; no replay")
            if attempt["state"] == "partial" and state in {"failed", "outcome_unknown"}:
                raise StateError("known partial effects cannot become a no-effect failure or unknown outcome")
            check_revision(action, attempt["revision"])
            self.conn.execute("UPDATE work_executions SET state=?,receipt=?,finished_at=? WHERE id=?",
                              (state, canonical_json(receipt), utc_now(), attempt_id))
            self.conn.execute("UPDATE work_actions SET state=?,updated_at=? WHERE id=?",
                              (state, utc_now(), action["id"]))
            self.event(action["id"], "execution_reconciled" if reconcile else "execution_finished",
                       {"attempt_id": attempt_id, "state": state, "receipt": receipt})
            return self.show(action["id"])

    @staticmethod
    def validate_receipt(receipt, outcome=None):
        if not isinstance(receipt, dict):
            raise StateError("a real result receipt is required")
        for key in ("reference", "kind"):
            text(receipt.get(key), "receipt." + key)
        if receipt["kind"] not in {"tool_result", "provider_receipt", "reconciliation_read"}:
            raise StateError("receipt kind must identify an actual tool result or reconciliation read")
        if outcome and receipt.get("outcome") != outcome:
            raise StateError("receipt outcome must match recorded outcome")
        if timestamp(receipt.get("recorded_at")) > now() + timedelta(minutes=1):
            raise StateError("receipt cannot be in the future")
        if outcome == "failed" and receipt.get("definitive_no_effect") is not True:
            raise StateError("failed requires proof of no effect; otherwise use partial/outcome_unknown")

    def show(self, entity_id):
        for table, kind in (("work_items", "item"), ("work_actions", "action"), ("work_records", "record"),
                            ("work_sources", "source")):
            found = self.conn.execute(
                "SELECT * FROM %s WHERE id=? AND account=?" % table, (entity_id, self.account)
            ).fetchone()
            if not found:
                continue
            row = dict(found)
            row["type"] = kind
            if kind == "action":
                rev = self.conn.execute(
                    "SELECT * FROM work_action_revisions WHERE action_id=? AND revision=?",
                    (entity_id, row["revision"]),
                ).fetchone()
                row.update(json.loads(rev["data"]))
                row["action_hash"] = rev["action_hash"]
                row["approvals"] = [dict(r) for r in self.conn.execute(
                    "SELECT id,revision,action_hash,expires_at,invalidated_at FROM work_approvals WHERE action_id=?", (entity_id,))]
                row["executions"] = []
                for result in self.conn.execute("SELECT * FROM work_executions WHERE action_id=?", (entity_id,)):
                    result = dict(result)
                    result["receipt"] = json.loads(result["receipt"]) if result["receipt"] else None
                    result["preflight"] = json.loads(result["preflight"])
                    row["executions"].append(result)
                row["stale"] = self.refs_stale(row["source_refs"])
                row["sources"] = self.source_evidence(row["source_refs"])
                if row.get("artifact_id"):
                    artifact = self.show(row["artifact_id"])
                    row["stale"] = row["stale"] or artifact["stale"] or artifact["revision"] != row["artifact_revision"]
                if row["state"] in {"succeeded", "dismissed"}:
                    row["historical_sources_changed"] = row["stale"]
                    row["stale"] = False
            elif kind in {"item", "record"}:
                data = json.loads(row.pop("data"))
                row["data"] = data
                row["stale"] = self.refs_stale(data.get("source_refs", []))
                row["sources"] = self.source_evidence(data.get("source_refs", []))
                if kind == "item":
                    row["confirmed"] = bool(row["confirmed"])
                    row["confirmation"] = json.loads(row["confirmation"]) if row["confirmation"] else None
                    row["relationships"] = [dict(r) for r in self.conn.execute(
                        "SELECT source_id,kind,target_id FROM work_relationships WHERE source_id=? OR target_id=?",
                        (entity_id, entity_id))]
                elif row["kind"] == "artifact":
                    deliveries = [
                        json.loads(event["data"]) for event in self.conn.execute(
                            "SELECT data FROM work_events WHERE entity_id=? AND event='shared' ORDER BY seq",
                            (entity_id,))
                    ]
                    row["sharing"] = {
                        "state": "shared" if any(d["revision"] == row["revision"] for d in deliveries) else "private",
                        "deliveries": deliveries,
                    }
            else:
                row["revisions"] = []
                for rev in self.conn.execute("SELECT * FROM work_source_revisions WHERE source_id=?", (entity_id,)):
                    rev = dict(rev)
                    rev["data"] = json.loads(rev["data"])
                    row["revisions"].append(rev)
            return row
        raise StateError("unknown entity: %s" % entity_id)

    def history(self, entity_id):
        self.show(entity_id)
        events = []
        for row in self.conn.execute(
                "SELECT * FROM work_events WHERE account=? AND entity_id=? ORDER BY seq", (self.account, entity_id)):
            row = dict(row)
            row["data"] = json.loads(row["data"])
            events.append(row)
        revisions = []
        for table, column in (("work_item_revisions", "item_id"), ("work_action_revisions", "action_id"),
                              ("work_record_revisions", "record_id")):
            for row in self.conn.execute("SELECT * FROM %s WHERE %s=? ORDER BY revision" % (table, column), (entity_id,)):
                row = dict(row)
                for key in ("snapshot", "data", "evidence"):
                    if row.get(key):
                        row[key] = json.loads(row[key])
                revisions.append(row)
        return {"id": entity_id, "events": events, "revisions": revisions}

    def list(self, view="decisions"):
        if view not in {"all", "decisions", "approval", "waiting", "problems"}:
            raise StateError("unknown view")
        result = {"schema_version": 1, "account": self.account, "items": [], "actions": [], "records": []}
        states = {
            "decisions": {"candidate", "ready", "stale", "proposed", "debrief_proposed"},
            "approval": {"ready", "approved", "prepared", "proposed"},
            "waiting": {"waiting", "deferred", "recap_pending", "blocked"},
            "problems": {"failed", "partial", "outcome_unknown", "executing", "blocked", "stale"},
        }
        for table, key in (("work_items", "items"), ("work_actions", "actions"), ("work_records", "records")):
            for row in self.conn.execute("SELECT id FROM %s WHERE account=? ORDER BY created_at,id" % table, (self.account,)):
                obj = self.show(row["id"])
                until = obj.get("deferred_until") or obj.get("data", {}).get("deferred_until")
                due = obj["state"] == "deferred" and until and timestamp(until) <= now()
                stale_actionable = obj.get("stale") and obj["state"] not in {
                    "resolved", "rejected", "cancelled", "achieved", "carried_forward", "revoked", "superseded"
                }
                if (view == "all" or obj["state"] in states[view]
                        or (view in {"decisions", "problems"} and stale_actionable)
                        or (view == "decisions" and due)):
                    result[key].append(obj)
        return result

    def import_preview(self, path):
        content = Path(path).read_text(encoding="utf-8")
        content_digest = hashlib.sha256(content.encode("utf-8")).hexdigest()
        try:
            data = json.loads(content)
        except ValueError:
            return {"digest": content_digest, "safe_structured": False, "requires_review": True,
                    "reason": "Markdown is not automatically parsed or confirmed. Review into structured JSON.",
                    "content": content}
        if not isinstance(data, dict) or data.get("schema_version") != 1 or not isinstance(data.get("items"), list):
            raise StateError("structured legacy import requires schema_version:1 and items list")
        seen = set()
        for item in data["items"]:
            if not isinstance(item, dict) or not {"legacy_id", "state", "data"} <= set(item):
                raise StateError("each import row requires legacy_id,state,data")
            text(item["legacy_id"], "legacy_id")
            if item["legacy_id"] in seen:
                raise StateError("duplicate legacy_id")
            seen.add(item["legacy_id"])
            if item["state"] not in ITEM_STATES:
                raise StateError("invalid imported state")
            if not isinstance(item["data"], dict):
                raise StateError("import data must be an object")
        return {"digest": content_digest, "safe_structured": True, "requires_review": True,
                "count": len(data["items"]), "items": data["items"]}

    def import_commitments(self, path, expected_digest, evidence):
        preview = self.import_preview(path)
        if preview["digest"] != expected_digest or not preview["safe_structured"]:
            raise StateError("unsafe or changed import; preview and review structured JSON first")
        human(evidence, "import:" + expected_digest, 1, "import")
        with self.transaction():
            old = self.conn.execute("SELECT item_ids FROM work_imports WHERE digest=?", (expected_digest,)).fetchone()
            if old:
                return {"imported": False, "item_ids": json.loads(old["item_ids"])}
            ids = []
            for imported in preview["items"]:
                data = self._item_data(imported["data"])
                key = digest([self.account, "legacy", imported["legacy_id"]])
                item_id = "item_" + key[:32]
                content_hash = digest({"state": imported["state"], "data": data})
                existing = self.conn.execute("SELECT ingest_hash FROM work_items WHERE id=?", (item_id,)).fetchone()
                if existing:
                    if existing["ingest_hash"] != content_hash:
                        raise StateError("legacy row changed; explicitly review/edit the existing item")
                else:
                    confirmed = imported["state"] in {"confirmed", "active", "waiting", "resolved", "cancelled"}
                    stamp = utc_now()
                    self.conn.execute("INSERT INTO work_items VALUES(?,?,?,?,?,?,?,?,?,?,?)",
                                      (item_id, self.account, 1, imported["state"], int(confirmed), key, content_hash,
                                       canonical_json(data), canonical_json(evidence) if confirmed else None, stamp, stamp))
                    self._item_snapshot(item_id, evidence)
                    self.event(item_id, "legacy_imported", {"digest": expected_digest, "evidence": evidence})
                ids.append(item_id)
            self.conn.execute("INSERT INTO work_imports VALUES(?,?,?,?)",
                              (expected_digest, canonical_json(evidence), canonical_json(ids), utc_now()))
            return {"imported": True, "item_ids": ids}

    def export_commitments(self, path, expected_digest=None):
        path = Path(path).expanduser().absolute()
        if path.is_symlink():
            raise StateError("refusing a symlink export")
        with self.transaction():
            prior = self.conn.execute("SELECT digest FROM work_exports WHERE path=?", (str(path),)).fetchone()
            actual = hashlib.sha256(path.read_bytes()).hexdigest() if path.exists() else None
            if actual is not None:
                approved_digest = expected_digest or (prior["digest"] if prior else None)
                if actual != approved_digest:
                    raise StateError("compatibility view has hand edits or is unmanaged; preview/review before replacing")
            elif expected_digest:
                raise StateError("export changed since review")
            lines = ["# Commitments", "", "<!-- Generated compatibility view; edit the work ledger, not this file. -->", ""]
            for row in self.conn.execute("SELECT id FROM work_items WHERE account=? AND confirmed=1 ORDER BY created_at,id", (self.account,)):
                obj = self.show(row["id"])
                data = obj["data"]
                clean = lambda value: str(value if value is not None else "unknown").replace("\n", " ").replace("\r", " ")
                lines.append("- [%s] %s — owner: %s; due: %s; id: `%s`" %
                             (obj["state"], clean(data["title"]), clean(data["owner"]), clean(data["due"]), obj["id"]))
                for ref in data["source_refs"]:
                    source = self.conn.execute(
                        "SELECT data FROM work_source_revisions WHERE source_id=? AND revision=?", (ref["source_id"], ref["revision"])
                    ).fetchone()
                    lines.append("  - Source: %s (revision %s)" % (clean(json.loads(source["data"])["web_link"]), clean(ref["revision"])))
            content = "\n".join(lines) + "\n"
            output_digest = hashlib.sha256(content.encode("utf-8")).hexdigest()
            # Same-directory exclusive staging permits atomic replacement without a shared temp directory.
            staging = path.with_name("." + path.name + "." + uuid.uuid4().hex + ".staging")
            try:
                fd = os.open(str(staging), os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
                with os.fdopen(fd, "w", encoding="utf-8") as stream:
                    stream.write(content)
                    stream.flush()
                    os.fsync(stream.fileno())
                current = hashlib.sha256(path.read_bytes()).hexdigest() if path.exists() else None
                if current != actual or path.is_symlink():
                    raise StateError("compatibility view changed during export")
                os.replace(str(staging), str(path))
            finally:
                if staging.exists():
                    staging.unlink()
            self.conn.execute("INSERT OR REPLACE INTO work_exports VALUES(?,?,?)", (str(path), output_digest, utc_now()))
            return {"path": str(path), "digest": output_digest, "confirmed_only": True}

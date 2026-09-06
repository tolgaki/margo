#!/usr/bin/env python3
"""Authoritative account-scoped memory records and lifecycle storage."""

import contextlib
import functools
import hashlib
import os
import re
import sqlite3
import stat
import uuid
from pathlib import Path
from datetime import datetime, timezone

import margo_store
from memory_encoder import MAX_TEXT_CHARS as MAX_MEMORY_TEXT_CHARS


StateError = margo_store.StateError
canonical_json = margo_store.canonical_json
parse_json = margo_store.parse_json
utc_now = margo_store.utc_now
validate_timestamp = margo_store.validate_timestamp

SCHEMA_VERSION = "2"
MAX_SOURCE_FILE_BYTES = 1024 * 1024
PREFERENCES_RECOVERY = (
    "Imported preferences changed or are unavailable. Restore/correct the private preferences file, "
    "run memory_state.py preferences-preview PRIVATE_PREFERENCES_FILE, then review and run "
    "preferences-import with fresh approval. Stale constraints remain blocked until revalidated."
)
DOMAINS = {"user", "agent"}
KINDS = {
    "profile", "preference", "person", "project", "decision", "episode",
    "lesson", "capability", "trend",
}
AUTHORITIES = {"user_confirmed", "source_observed", "inferred"}
SENSITIVITIES = {"public", "private", "sensitive"}
USES = {"reasoning", "drafting"}
SOURCE_KINDS = {"user_statement", "file", "work_source", "tool_result", "memory_record", "session_checkpoint"}
STATUSES = {"candidate", "active", "rejected", "disputed", "stale", "superseded", "suppressed", "forgotten"}
TYPED_RELATIONS = {"owns", "reports_to", "depends_on", "constrains", "advances", "discusses", "contradicts"}
RELATIONS = {"derives_from", "about", "related_to", "supersedes"} | TYPED_RELATIONS
OBSERVATIONAL_KINDS = KINDS - {"preference", "lesson"}
TRANSITIONS = {
    "candidate": {"active", "rejected", "disputed", "stale"},
    "active": {"disputed", "stale", "superseded", "suppressed"},
    "disputed": {"active", "rejected", "stale", "superseded"},
    "stale": {"active", "rejected", "superseded"},
    "rejected": set(),
    "superseded": set(),
    "suppressed": {"active", "disputed", "stale", "superseded"},
    "forgotten": set(),
}

SCHEMA_V1 = """
CREATE TABLE IF NOT EXISTS memory_meta (
 key TEXT PRIMARY KEY, value TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS memory_records (
 id TEXT PRIMARY KEY, account TEXT NOT NULL, key_hash TEXT NOT NULL,
 revision INTEGER NOT NULL, domain TEXT NOT NULL, kind TEXT NOT NULL,
 status TEXT NOT NULL, data TEXT NOT NULL, indexed_content_hash TEXT,
 created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
 UNIQUE(account,key_hash)
);
CREATE TABLE IF NOT EXISTS memory_revisions (
 memory_id TEXT NOT NULL REFERENCES memory_records(id), revision INTEGER NOT NULL,
 status TEXT NOT NULL, domain TEXT NOT NULL, kind TEXT NOT NULL,
 data TEXT NOT NULL, evidence TEXT, content_hash TEXT, created_at TEXT NOT NULL,
 PRIMARY KEY(memory_id,revision)
);
CREATE TABLE IF NOT EXISTS memory_links (
 source_id TEXT NOT NULL REFERENCES memory_records(id),
 target_id TEXT NOT NULL REFERENCES memory_records(id),
 relation TEXT NOT NULL, created_at TEXT NOT NULL,
 PRIMARY KEY(source_id,target_id,relation)
);
CREATE TABLE IF NOT EXISTS memory_jobs (
 memory_id TEXT PRIMARY KEY REFERENCES memory_records(id), revision INTEGER NOT NULL,
 op TEXT NOT NULL, content_hash TEXT NOT NULL, created_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS memory_tombstones (
 id TEXT PRIMARY KEY, account TEXT NOT NULL, key_hash TEXT NOT NULL,
 forgotten_at TEXT NOT NULL, UNIQUE(account,key_hash)
);
CREATE INDEX IF NOT EXISTS memory_records_account_status
 ON memory_records(account,status);
CREATE INDEX IF NOT EXISTS memory_links_target
 ON memory_links(target_id,relation);
"""

SCHEMA = SCHEMA_V1.replace(
    "relation TEXT NOT NULL, created_at TEXT NOT NULL,",
    "relation TEXT NOT NULL, created_at TEXT NOT NULL, data TEXT NOT NULL DEFAULT '{}',",
) + """
CREATE TABLE IF NOT EXISTS memory_policy (
 account TEXT PRIMARY KEY, revision INTEGER NOT NULL,
 data TEXT NOT NULL, evidence TEXT NOT NULL, updated_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS memory_usage (
 id TEXT PRIMARY KEY, account TEXT NOT NULL, routine TEXT NOT NULL,
 refs TEXT NOT NULL, outcome TEXT NOT NULL, created_at TEXT NOT NULL
);
"""


def _text(value, field):
    if not isinstance(value, str) or not value.strip():
        raise StateError("%s must be a nonempty string" % field)
    return value


def _integer(value, field):
    if isinstance(value, bool) or not isinstance(value, int):
        raise StateError("%s must be an integer" % field)
    return value


def _json_object(value, field, allow_empty=True):
    if not isinstance(value, dict) or (not allow_empty and not value):
        raise StateError("%s must be %sa JSON object" % (field, "" if allow_empty else "a nonempty "))
    canonical_json(value)
    return value


def _parse_time(value):
    return datetime.fromisoformat(validate_timestamp(value))


def search_text(record):
    """Return the stable text representation covered by memory job content hashes."""
    if not isinstance(record, dict):
        raise StateError("memory record must be a JSON object")
    return "\n".join(
        part
        for part in (
            record.get("title", ""),
            record.get("text", ""),
            " ".join(record.get("entities", [])),
            " ".join(record.get("routines", [])),
            record.get("scope", ""),
        )
        if part
    )


def memory_identity(account, domain, key):
    if domain not in DOMAINS:
        raise StateError("domain must be user or agent")
    _text(key, "key")
    if len(key) > 4096:
        raise StateError("key is too long")
    key_hash = hashlib.sha256((account + "\0" + domain + "\0" + key).encode("utf-8")).hexdigest()
    return "mem_" + key_hash[:40], key_hash


def scoped_entity_key(value):
    """Names are searchable text, not exact graph keys; retain the account-local user key."""
    if not isinstance(value, str) or not value or len(value) > 4096:
        return None
    if value == "user":
        return value
    if value.startswith("identity:"):
        try:
            parts = parse_json(value[len("identity:"):])
        except StateError:
            return None
        if isinstance(parts, list) and len(parts) == 3 and all(isinstance(part, str) and part.strip() for part in parts):
            return "identity:" + canonical_json(parts)
        return None
    if re.fullmatch(r"[A-Za-z][A-Za-z0-9_.-]*:[^\s]+", value):
        return value
    if re.fullmatch(r"(?:mem|item|outcome|meeting|artifact|feedback|rule|action)_[0-9a-f]+", value):
        return value
    return None


def freshness_scoped(method):
    @functools.wraps(method)
    def scoped(self, *args, **kwargs):
        with self.freshness_pass():
            return method(self, *args, **kwargs)
    return scoped


class MemoryStore:
    def __init__(self, account=None, state_root=None, migrate=False, read_only=False):
        if migrate and read_only:
            raise StateError("migration requires explicit writable access")
        self.account = margo_store.resolve_account(account)
        self.read_only = read_only
        self._file_checks = None
        self.conn = margo_store.connect(account=self.account, state_root=state_root, read_only=read_only)
        try:
            self.conn.execute("PRAGMA foreign_keys=ON")
            if not read_only:
                self.conn.execute("PRAGMA secure_delete=ON")
            self._initialize_schema(migrate=migrate)
        except Exception:
            self.conn.close()
            raise

    def _initialize_schema(self, migrate=False):
        statements = [part.strip() for part in SCHEMA.split(";") if part.strip()]
        expected = {
            statement.split()[5]
            for statement in statements
            if statement.startswith("CREATE TABLE")
        }
        with self.transaction():
            all_tables = {
                row[0]
                for row in self.conn.execute(
                    "SELECT name FROM sqlite_master WHERE type='table'"
                )
            }
            existing = {name for name in all_tables if name.startswith("memory_")}
            marker = self.conn.execute(
                "SELECT value FROM margo_meta WHERE key='memory_schema_version'"
            ).fetchone()
            if getattr(self, "read_only", False) and marker is None and not existing:
                raise margo_store.NotInitialized("Memory is not initialized; run memory_state.py init explicitly.")
            if marker is not None and marker[0] == "1" and migrate:
                self._migrate_v1(existing)
                existing = expected
                marker = (SCHEMA_VERSION,)
            if marker is not None and marker[0] != SCHEMA_VERSION:
                raise StateError("memory schema marker mismatch; explicit migration required")
            if marker is not None and existing != expected:
                raise StateError("incomplete memory schema; refusing to recreate missing history")
            if existing and marker is None:
                raise StateError("memory schema has no global marker; explicit recovery required")
            if existing and existing != expected:
                raise StateError("unknown or incomplete memory schema; explicit recovery required")
            if existing:
                local = self.conn.execute(
                    "SELECT value FROM memory_meta WHERE key='schema_version'"
                ).fetchone()
                if local is None or local[0] != SCHEMA_VERSION:
                    raise StateError("unsupported or missing memory schema version")
                reference = sqlite3.connect(":memory:")
                try:
                    reference.executescript(SCHEMA)
                    for table in sorted(expected):
                        for pragma in ("table_info", "foreign_key_list"):
                            actual = [
                                tuple(row)
                                for row in self.conn.execute(
                                    "PRAGMA %s(%s)" % (pragma, table)
                                )
                            ]
                            wanted = [
                                tuple(row)
                                for row in reference.execute(
                                    "PRAGMA %s(%s)" % (pragma, table)
                                )
                            ]
                            if actual != wanted:
                                raise StateError("memory schema contract mismatch: " + table)
                finally:
                    reference.close()
            if getattr(self, "read_only", False):
                return
            for statement in statements:
                self.conn.execute(statement)
            self.conn.execute(
                "INSERT OR IGNORE INTO memory_meta VALUES ('schema_version',?)",
                (SCHEMA_VERSION,),
            )
            self.conn.execute(
                "INSERT OR IGNORE INTO margo_meta VALUES ('memory_schema_version',?)",
                (SCHEMA_VERSION,),
            )

    def _migrate_v1(self, existing):
        reference = sqlite3.connect(":memory:")
        try:
            reference.executescript(SCHEMA_V1)
            expected = {row[0] for row in reference.execute(
                "SELECT name FROM sqlite_master WHERE type='table'")}
            local = self.conn.execute(
                "SELECT value FROM memory_meta WHERE key='schema_version'").fetchone()
            if existing != expected or local is None or local[0] != "1":
                raise StateError("incomplete legacy memory schema; migration refused")
            for table in expected:
                for pragma in ("table_info", "foreign_key_list"):
                    actual = [tuple(row) for row in self.conn.execute("PRAGMA %s(%s)" % (pragma, table))]
                    wanted = [tuple(row) for row in reference.execute("PRAGMA %s(%s)" % (pragma, table))]
                    if actual != wanted:
                        raise StateError("legacy memory schema contract mismatch: " + table)
        finally:
            reference.close()
        self.conn.execute("ALTER TABLE memory_links ADD COLUMN data TEXT NOT NULL DEFAULT '{}'")
        for statement in SCHEMA.split(";"):
            if statement.strip():
                self.conn.execute(statement)
        self.conn.execute("UPDATE memory_meta SET value=? WHERE key='schema_version'", (SCHEMA_VERSION,))
        self.conn.execute("UPDATE margo_meta SET value=? WHERE key='memory_schema_version'", (SCHEMA_VERSION,))

    @contextlib.contextmanager
    def transaction(self):
        nested = self.conn.in_transaction
        savepoint = "memory_" + uuid.uuid4().hex if nested else None
        try:
            self.conn.execute("SAVEPOINT " + savepoint if nested else
                              "BEGIN" if getattr(self, "read_only", False) else "BEGIN IMMEDIATE")
            yield
            if nested:
                self.conn.execute("RELEASE SAVEPOINT " + savepoint)
            else:
                self.conn.commit()
        except Exception:
            if nested:
                self.conn.execute("ROLLBACK TO SAVEPOINT " + savepoint)
                self.conn.execute("RELEASE SAVEPOINT " + savepoint)
            else:
                self.conn.rollback()
            raise

    def close(self):
        self.conn.close()

    def _identity(self, domain, key):
        return memory_identity(self.account, domain, key)

    def record_id(self, domain, key):
        return self._identity(domain, key)[0]

    def _normalise_strings(self, value, field, allowed=None):
        if value is None:
            return []
        if not isinstance(value, list):
            raise StateError("%s must be a list" % field)
        result = []
        for entry in value:
            _text(entry, field + " entry")
            if allowed is not None and entry not in allowed:
                raise StateError("unsupported %s entry: %s" % (field, entry))
            if entry not in result:
                result.append(entry)
        return sorted(result)

    def _normalise_source_refs(self, refs):
        if not isinstance(refs, list) or not refs:
            raise StateError("source_refs must be a nonempty list")
        result = []
        seen = set()
        for ref in refs:
            if not isinstance(ref, dict):
                raise StateError("each source reference must be an object")
            unknown = set(ref) - {"kind", "ref", "revision", "verified_at"}
            if unknown:
                raise StateError("unknown source reference fields: %s" % ", ".join(sorted(unknown)))
            if ref.get("kind") not in SOURCE_KINDS:
                raise StateError("unsupported source reference kind")
            _text(ref.get("ref"), "source reference")
            value = {"kind": ref["kind"], "ref": ref["ref"]}
            if ref["kind"] == "file" and ("revision" not in ref or not isinstance(ref["revision"], str)
                                         or len(ref["revision"]) != 64
                                         or any(char not in "0123456789abcdef" for char in ref["revision"])):
                raise StateError("file source requires its verified SHA256 content revision")
            if "revision" in ref:
                _text(ref["revision"], "source revision")
                value["revision"] = ref["revision"]
            if ref["kind"] == "memory_record" and (
                    not str(ref.get("revision", "")).isdigit() or int(ref["revision"]) < 1):
                raise StateError("memory source requires its positive revision as a string")
            if "verified_at" in ref:
                value["verified_at"] = validate_timestamp(ref["verified_at"])
            identity = (value["kind"], value["ref"], value.get("revision"))
            if identity in seen:
                raise StateError("duplicate source reference")
            seen.add(identity)
            result.append(value)
        return sorted(result, key=canonical_json)

    def _normalise_data(self, data):
        if not isinstance(data, dict):
            raise StateError("memory data must be a JSON object")
        allowed = {
            "domain", "kind", "title", "text", "authority", "scope",
            "sensitivity", "allowed_uses", "entities", "routines", "source_refs",
            "valid_from", "valid_to", "review_after", "metadata", "identity",
        }
        unknown = set(data) - allowed
        if unknown:
            raise StateError("unknown memory fields: %s" % ", ".join(sorted(unknown)))
        if data.get("domain") not in DOMAINS:
            raise StateError("domain must be user or agent")
        if data.get("kind") not in KINDS:
            raise StateError("unsupported memory kind")
        if data.get("authority") not in AUTHORITIES:
            raise StateError("unsupported memory authority")
        _text(data.get("title"), "title")
        _text(data.get("text"), "text")
        _text(data.get("scope"), "scope")
        value = dict(data)
        value["sensitivity"] = value.get("sensitivity", "private")
        if value["sensitivity"] not in SENSITIVITIES:
            raise StateError("unsupported sensitivity")
        value["allowed_uses"] = self._normalise_strings(
            value.get("allowed_uses", ["reasoning"]), "allowed_uses", USES
        )
        if not value["allowed_uses"]:
            raise StateError("allowed_uses must not be empty")
        value["entities"] = self._normalise_strings(value.get("entities", []), "entities")
        if any(scoped_entity_key(entity) is None for entity in value["entities"]):
            raise StateError("entities require scoped identifiers; keep display names in title/text")
        value["entities"] = sorted({scoped_entity_key(entity) for entity in value["entities"]})
        value["routines"] = self._normalise_strings(value.get("routines", []), "routines")
        value["source_refs"] = self._normalise_source_refs(value.get("source_refs"))
        value["metadata"] = dict(_json_object(value.get("metadata", {}), "metadata"))
        if "identity" in value:
            identity = _json_object(value["identity"], "identity", allow_empty=False)
            if set(identity) != {"provider", "scope", "external_id"}:
                raise StateError("identity requires provider, scope and external_id only")
            for field, entry in identity.items():
                _text(entry, "identity." + field)
        requirements = value["metadata"].get(
            "environment_requirements", value["metadata"].get("environment")
        )
        if requirements is not None and not isinstance(requirements, dict):
            raise StateError("environment requirements must be a JSON object")
        for field in ("valid_from", "valid_to", "review_after"):
            if value.get(field) is None:
                value.pop(field, None)
            elif field in value:
                value[field] = validate_timestamp(value[field])
        if (
            value.get("valid_from")
            and value.get("valid_to")
            and _parse_time(value["valid_from"]) >= _parse_time(value["valid_to"])
        ):
            raise StateError("valid_from must precede valid_to")
        canonical_json(value)
        if len(search_text(value)) > MAX_MEMORY_TEXT_CHARS:
            raise StateError("memory search representation exceeds %s characters; split the memory before saving"
                             % MAX_MEMORY_TEXT_CHARS)
        return value

    def _resolve_work_sources(self, data):
        refs = []
        for original in data["source_refs"]:
            ref = dict(original)
            if ref["kind"] == "session_checkpoint":
                from memory_dream import validate_source
                validate_source(self, ref)
            if ref["kind"] == "memory_record":
                parent = self.conn.execute(
                    "SELECT status FROM memory_records WHERE id=? AND account=?",
                    (ref["ref"], self.account)).fetchone()
                if parent is None or parent["status"] == "forgotten":
                    raise StateError("memory dependency is unknown or forgotten")
            if ref["kind"] == "work_source":
                try:
                    source = self.conn.execute(
                        "SELECT account,current_revision FROM work_sources WHERE id=?",
                        (ref["ref"],),
                    ).fetchone()
                except sqlite3.OperationalError as exc:
                    if "no such table: work_sources" not in str(exc):
                        raise
                    source = None
                if source is not None and source["account"] == self.account and "revision" not in ref:
                    ref["revision"] = str(source["current_revision"])
            refs.append(ref)
        value = dict(data)
        value["source_refs"] = sorted(refs, key=canonical_json)
        return value

    def _content_hash(self, data):
        return hashlib.sha256(search_text(data).encode("utf-8")).hexdigest()

    def _row(self, memory_id):
        row = self.conn.execute(
            "SELECT * FROM memory_records WHERE id=? AND account=?",
            (memory_id, self.account),
        ).fetchone()
        if row is None:
            raise StateError("unknown memory: " + str(memory_id))
        return row

    def _flatten(self, row):
        result = {
            "id": row["id"],
            "account": row["account"],
            "revision": row["revision"],
            "status": row["status"],
            "domain": row["domain"],
            "kind": row["kind"],
        }
        result.update(parse_json(row["data"]))
        result["created_at"] = row["created_at"]
        result["updated_at"] = row["updated_at"]
        return result

    def show(self, memory_id):
        _text(memory_id, "memory id")
        return self._flatten(self._row(memory_id))

    def list(self, domain=None, status=None):
        if domain is not None and domain not in DOMAINS:
            raise StateError("domain must be user or agent")
        if status is not None and status not in STATUSES:
            raise StateError("unsupported memory status")
        clauses = ["account=?"]
        values = [self.account]
        if domain is not None:
            clauses.append("domain=?")
            values.append(domain)
        if status is not None:
            clauses.append("status=?")
            values.append(status)
        rows = self.conn.execute(
            "SELECT * FROM memory_records WHERE %s ORDER BY updated_at DESC,id"
            % " AND ".join(clauses),
            values,
        ).fetchall()
        return [self._flatten(row) for row in rows]

    def _evidence_json(self, evidence):
        if evidence is None:
            return None
        _json_object(evidence, "evidence", allow_empty=False)
        return canonical_json(evidence)

    def _human(self, evidence, memory_id, revision, decision):
        from work_ledger import human

        return human(evidence, memory_id, revision, decision)

    def _activation_confirmation_required(self, data):
        if data["authority"] == "inferred":
            raise StateError("inferred memory cannot be active")
        if data["authority"] == "source_observed":
            if data["kind"] in {"preference", "lesson"}:
                return True
            if data["kind"] not in OBSERVATIONAL_KINDS:
                raise StateError("source-observed memory kind is not observational")
            return False
        return True

    def _protected(self, row, data):
        if row["status"] == "active" and (
                data.get("authority") == "user_confirmed" or data.get("kind") in {"preference", "lesson"}):
            return True
        for revision in self.conn.execute(
                "SELECT data FROM memory_revisions WHERE memory_id=? AND status='active'", (row["id"],)):
            previous = parse_json(revision["data"])
            if previous.get("authority") == "user_confirmed" or previous.get("kind") in {"preference", "lesson"}:
                return True
        return False

    def _transition_decision(self, old_status, new_status):
        if new_status == "active":
            return "confirm"
        return {
            "rejected": "reject",
            "disputed": "dispute",
            "stale": "stale",
            "superseded": "supersede",
            "suppressed": "suppress",
        }.get(new_status, "edit")

    def _snapshot(self, memory_id, revision, status, data, evidence, stamp):
        self.conn.execute(
            """INSERT INTO memory_revisions
               (memory_id,revision,status,domain,kind,data,evidence,content_hash,created_at)
               VALUES(?,?,?,?,?,?,?,?,?)""",
            (
                memory_id,
                revision,
                status,
                data["domain"],
                data["kind"],
                canonical_json(data),
                self._evidence_json(evidence),
                self._content_hash(data),
                stamp,
            ),
        )

    def _queue(self, memory_id, revision, status, data, stamp):
        self.conn.execute("DELETE FROM memory_meta WHERE key=?", ("index-blocked:" + memory_id,))
        content_hash = self._content_hash(data)
        operation = (
            "upsert"
            if status == "active" and data.get("sensitivity") != "sensitive"
            and self.dream_index_current(data)
            else "delete"
        )
        self.conn.execute(
            """INSERT INTO memory_jobs(memory_id,revision,op,content_hash,created_at)
               VALUES(?,?,?,?,?)
               ON CONFLICT(memory_id) DO UPDATE SET
                revision=excluded.revision,op=excluded.op,
                content_hash=excluded.content_hash,created_at=excluded.created_at""",
            (memory_id, revision, operation, content_hash, stamp),
        )

    def dream_index_current(self, data):
        role = data.get("metadata", {}).get("dream")
        if role in {"checkpoint", "snapshot"}:
            return False
        return not role or self._work_sources_current(data)

    def put(self, key, data, status="candidate", revision=None, evidence=None):
        data = self._normalise_data(data)
        memory_id, key_hash = self._identity(data["domain"], key)
        return self._put(memory_id, key_hash, data, status, revision, evidence)

    def revise(self, memory_id, data, status, revision, evidence=None):
        row = self._row(memory_id)
        normalised = self._normalise_data(data)
        if normalised["domain"] != row["domain"] or normalised["kind"] != row["kind"]:
            raise StateError("memory domain/kind is immutable; propose a separate sourced record")
        return self._put(memory_id, row["key_hash"], normalised, status, revision, evidence)

    def _put(self, memory_id, key_hash, data, status, revision, evidence):
        if status not in STATUSES - {"forgotten"}:
            raise StateError("unsupported memory status")
        with self.transaction():
            data = self._resolve_work_sources(data)
            dream = bool(data["metadata"].get("dream")) or any(
                ref["kind"] == "session_checkpoint"
                or (ref["kind"] == "memory_record" and
                    self.show(ref["ref"]).get("metadata", {}).get("dream"))
                for ref in data["source_refs"])
            if dream and status in {"active", "candidate"}:
                from memory_governance import authorize_capture
                authorize_capture(self, data, allow_confirmation=evidence is not None)
            tombstone = self.conn.execute(
                "SELECT id FROM memory_tombstones WHERE account=? AND key_hash=?",
                (self.account, key_hash),
            ).fetchone()
            if tombstone is not None:
                raise StateError("forgotten memory identity cannot be recreated")
            row = self.conn.execute(
                "SELECT * FROM memory_records WHERE id=? AND account=?",
                (memory_id, self.account),
            ).fetchone()
            encoded = canonical_json(data)
            if row is not None and row["status"] == "forgotten":
                raise StateError("forgotten memory identity cannot be recreated")
            if row is not None and (row["domain"] != data["domain"] or row["kind"] != data["kind"]):
                raise StateError("memory domain/kind is immutable; propose a separate sourced record")
            if row is not None and row["status"] == status and row["data"] == encoded:
                return self._flatten(row)
            if row is None:
                if revision is not None and (_integer(revision, "revision") != 1):
                    raise StateError("new memory revision must be 1")
                if status == "active":
                    needs_confirmation = self._activation_confirmation_required(data)
                    if needs_confirmation:
                        if revision != 1:
                            raise StateError("active confirmed memory creation requires explicit revision 1")
                        self._human(evidence, memory_id, 1, "confirm")
                stamp = utc_now()
                content_hash = self._content_hash(data)
                self.conn.execute(
                    """INSERT INTO memory_records
                       (id,account,key_hash,revision,domain,kind,status,data,indexed_content_hash,
                        created_at,updated_at)
                       VALUES(?,?,?,?,?,?,?,?,?,?,?)""",
                    (
                        memory_id,
                        self.account,
                        key_hash,
                        1,
                        data["domain"],
                        data["kind"],
                        status,
                        encoded,
                        content_hash,
                        stamp,
                        stamp,
                    ),
                )
                self._snapshot(memory_id, 1, status, data, evidence, stamp)
                self._queue(memory_id, 1, status, data, stamp)
                return self.show(memory_id)

            current_revision = row["revision"]
            if revision is None:
                raise StateError("revision is required to change existing memory")
            _integer(revision, "revision")
            if revision != current_revision:
                raise StateError(
                    "revision conflict: current revision is %s" % current_revision
                )
            if status != row["status"] and status not in TRANSITIONS[row["status"]]:
                raise StateError(
                    "invalid memory transition: %s -> %s" % (row["status"], status)
                )
            old_data = parse_json(row["data"])
            if status == "active":
                needs_confirmation = self._activation_confirmation_required(data)
            else:
                needs_confirmation = False
            if self._protected(row, old_data):
                self._human(
                    evidence,
                    memory_id,
                    current_revision,
                    "edit"
                    if status == row["status"]
                    else self._transition_decision(row["status"], status),
                )
            elif ((row["status"] == "suppressed" and status != "suppressed")
                  or (row["status"] in {"disputed", "stale"} and status == "active")):
                self._human(evidence, memory_id, current_revision,
                            self._transition_decision(row["status"], status))
            elif needs_confirmation:
                self._human(evidence, memory_id, current_revision, "confirm")
            next_revision = current_revision + 1
            stamp = utc_now()
            content_hash = self._content_hash(data)
            changed = self.conn.execute(
                """UPDATE memory_records SET revision=?,domain=?,kind=?,status=?,data=?,
                   indexed_content_hash=?,updated_at=?
                   WHERE id=? AND account=? AND revision=?""",
                (
                    next_revision,
                    data["domain"],
                    data["kind"],
                    status,
                    encoded,
                    content_hash,
                    stamp,
                    memory_id,
                    self.account,
                    current_revision,
                ),
            ).rowcount
            if changed != 1:
                raise StateError("revision conflict; memory changed concurrently")
            self._snapshot(memory_id, next_revision, status, data, evidence, stamp)
            self._queue(memory_id, next_revision, status, data, stamp)
            self._invalidate_derived_indexes(memory_id)
            return self.show(memory_id)

    def _invalidate_derived_indexes(self, memory_id):
        """Remove obsolete derived index text without rewriting evidence history."""
        identities = self._derived_descendants(memory_id) - {memory_id}
        tables = {row[0] for row in self.conn.execute("SELECT name FROM sqlite_master WHERE type='table'")}
        for identity in identities:
            row = self._row(identity)
            self._queue(identity, row["revision"], "stale", parse_json(row["data"]), utc_now())
            for table in ("semantic_vectors", "semantic_documents", "semantic_fts"):
                if table in tables:
                    self.conn.execute("DELETE FROM %s WHERE memory_id=?" % table, (identity,))

    def _cycle(self, source_id, target_id, relation):
        edges = [
            (row["source_id"], row["target_id"])
            for row in self.conn.execute(
                "SELECT source_id,target_id FROM memory_links WHERE relation=?",
                (relation,),
            )
        ]
        pending = [target_id]
        seen = set()
        while pending:
            node = pending.pop()
            if node == source_id:
                return True
            if node not in seen:
                seen.add(node)
                pending.extend(target for source, target in edges if source == node)
        return False

    def link(self, source_id, target_id, relation, revision, evidence=None, data=None,
             target_revision=None):
        if relation not in RELATIONS:
            raise StateError("unsupported memory relationship")
        if source_id == target_id:
            raise StateError("self relationship is invalid")
        edge_data = dict(_json_object(data or {}, "relationship data"))
        if set(edge_data) - {"valid_from", "valid_to", "source_refs"}:
            raise StateError("relationship data supports validity and source_refs only")
        for field in ("valid_from", "valid_to"):
            if field in edge_data:
                edge_data[field] = validate_timestamp(edge_data[field])
        if (edge_data.get("valid_from") and edge_data.get("valid_to")
                and edge_data["valid_from"] >= edge_data["valid_to"]):
            raise StateError("relationship valid_from must precede valid_to")
        if "source_refs" in edge_data:
            edge_data["source_refs"] = self._normalise_source_refs(edge_data["source_refs"])
        if relation in TYPED_RELATIONS and not {"valid_from", "source_refs"} <= set(edge_data):
            raise StateError("typed relationships require valid_from and source_refs")
        with self.transaction():
            source = self._row(source_id)
            target = self._row(target_id)
            if source["status"] == "forgotten" or target["status"] == "forgotten":
                raise StateError("forgotten memory cannot be linked")
            existing = self.conn.execute(
                """SELECT source_id,target_id,relation,created_at,data FROM memory_links
                   WHERE source_id=? AND target_id=? AND relation=?""",
                (source_id, target_id, relation),
            ).fetchone()
            if existing is not None:
                if existing["data"] != canonical_json(edge_data):
                    raise StateError("relationship already exists with different evidence/validity; remove and review its replacement")
                result = self._flatten(source)
                result.update(
                    {
                        "source_id": existing["source_id"],
                        "target_id": existing["target_id"],
                        "relation": existing["relation"],
                        "link_created_at": existing["created_at"],
                    }
                )
                return result
            _integer(revision, "revision")
            if revision != source["revision"]:
                raise StateError(
                    "revision conflict: current revision is %s" % source["revision"]
                )
            if relation in TYPED_RELATIONS or relation == "supersedes":
                if type(target_revision) is not int or target_revision != target["revision"]:
                    raise StateError("typed relationship requires the current target_revision")
                self._human(evidence, source_id, revision, "relate")
            if relation == "supersedes" and (source["kind"], source["domain"]) != (target["kind"], target["domain"]):
                raise StateError("supersession requires the same memory kind and domain")
            if relation in {"derives_from", "supersedes", "depends_on", "reports_to"} and self._cycle(
                source_id, target_id, relation
            ):
                raise StateError("memory relationship would create a cycle")
            source_data = parse_json(source["data"])
            if self._protected(source, source_data):
                self._human(evidence, source_id, revision, "relate")
            stamp = utc_now()
            self.conn.execute(
                "INSERT INTO memory_links VALUES(?,?,?,?,?)",
                (source_id, target_id, relation, stamp, canonical_json(edge_data)),
            )
            next_revision = revision + 1
            self.conn.execute(
                "UPDATE memory_records SET revision=?,updated_at=? WHERE id=? AND account=?",
                (next_revision, stamp, source_id, self.account),
            )
            self._snapshot(
                source_id, next_revision, source["status"], source_data,
                dict(evidence or {}, relationship_change={
                    "operation": "link", "source_id": source_id, "target_id": target_id,
                    "relation": relation, "data": edge_data}), stamp
            )
            self._queue(
                source_id, next_revision, source["status"], source_data, stamp
            )
            result = self.show(source_id)
            result.update(
                {
                    "source_id": source_id,
                    "target_id": target_id,
                    "relation": relation,
                    "link_created_at": stamp,
                    "data": edge_data,
                }
            )
            return result

    def links(self, memory_id):
        self._row(memory_id)
        return [
            {
                "source_id": row["source_id"],
                "target_id": row["target_id"],
                "relation": row["relation"],
                **({"data": parse_json(row["data"])} if row["data"] != "{}" else {}),
            }
            for row in self.conn.execute(
                """SELECT source_id,target_id,relation,data FROM memory_links
                   WHERE source_id=? OR target_id=?
                   ORDER BY relation,source_id,target_id""",
                (memory_id, memory_id),
            )
        ]

    @freshness_scoped
    def active_links(self, memory_id):
        stamp = utc_now()
        result = []
        for edge in self.links(memory_id):
            data = edge.get("data", {})
            if (data.get("valid_from", stamp) > stamp or data.get("valid_to", "9999") <= stamp
                    or not self._work_sources_current(data)):
                continue
            endpoints = [self._row(edge[key]) for key in ("source_id", "target_id")]
            if all(row["status"] == "active" and self._work_sources_current(parse_json(row["data"]))
                   and parse_json(row["data"]).get("valid_from", stamp) <= stamp
                   and parse_json(row["data"]).get("valid_to", "9999") > stamp
                   and parse_json(row["data"]).get("review_after", "9999") > stamp
                   for row in endpoints):
                result.append(edge)
        return result

    def unlink(self, source_id, target_id, relation, revision, evidence):
        if relation not in RELATIONS or relation in {"derives_from", "supersedes"}:
            raise StateError("derivation and supersession history cannot be unlinked; propose a sourced replacement")
        with self.transaction():
            source = self._row(source_id)
            self._row(target_id)
            if type(revision) is not int or source["revision"] != revision:
                raise StateError("relationship source revision conflict")
            self._human(evidence, source_id, revision, "unrelate")
            removed = self.conn.execute(
                "DELETE FROM memory_links WHERE source_id=? AND target_id=? AND relation=?",
                (source_id, target_id, relation)).rowcount
            if not removed:
                raise StateError("relationship does not exist")
            stamp = utc_now()
            data = parse_json(source["data"])
            self.conn.execute("UPDATE memory_records SET revision=?,updated_at=? WHERE id=?",
                              (revision + 1, stamp, source_id))
            self._snapshot(source_id, revision + 1, source["status"], data, dict(
                evidence, relationship_change={"operation": "unlink", "target_id": target_id, "relation": relation}), stamp)
            self._queue(source_id, revision + 1, source["status"], data, stamp)
            return self.show(source_id)

    def history(self, memory_id, limit=30):
        self._row(memory_id)
        if type(limit) is not int or not 1 <= limit <= 100:
            raise StateError("history limit must be between 1 and 100")
        return [dict(row, data=parse_json(row["data"]),
                     evidence=parse_json(row["evidence"]) if row["evidence"] else None)
                for row in self.conn.execute(
                    "SELECT revision,status,data,evidence,created_at FROM memory_revisions "
                    "WHERE memory_id=? ORDER BY revision DESC LIMIT ?", (memory_id, limit))]

    def _derived_descendants(self, memory_id):
        children = {}
        checkpoint_groups = {}
        for row in self.conn.execute(
                "SELECT source_id,target_id FROM memory_links WHERE relation='derives_from'"):
            children.setdefault(row["target_id"], set()).add(row["source_id"])
        # A correction cannot disconnect old copied substance from its erasure provenance.
        for row in self.conn.execute(
                "SELECT r.memory_id,r.data FROM memory_revisions r JOIN memory_records m "
                "ON m.id=r.memory_id WHERE m.account=? AND m.status<>'forgotten'", (self.account,)):
            for ref in parse_json(row["data"]).get("source_refs", []):
                if ref["kind"] == "memory_record":
                    children.setdefault(ref["ref"], set()).add(row["memory_id"])
                elif ref["kind"] == "session_checkpoint":
                    from memory_dream import source_keys
                    for key in source_keys(ref):
                        checkpoint_groups.setdefault(key, set()).add(row["memory_id"])
        for identities in checkpoint_groups.values():
            root = min(identities)
            for identity in identities:
                children.setdefault(root, set()).add(identity)
                children.setdefault(identity, set()).add(root)
        pending = [memory_id]
        seen = {memory_id}
        while pending:
            target = pending.pop()
            for child in children.get(target, ()):
                if child not in seen:
                    seen.add(child)
                    pending.append(child)
        return seen

    def forget_preview(self, memory_id):
        root = self.show(memory_id)
        return {"subject_id": memory_id, "revision": root["revision"],
                "affected": [{"id": row["id"], "revision": row["revision"], "domain": row["domain"],
                              "kind": row["kind"]}
                             for row in (self.show(identity) for identity in sorted(self._derived_descendants(memory_id)))
                             if row["status"] != "forgotten"],
                "retained": ["Minimal anti-reimport tombstones.", "Source mail/documents and work/approval records.",
                             "Previously generated outputs, exports and backups are not recalled."]}

    def forget(self, memory_id, revision, evidence=None, *, policy_revision=None):
        with self.transaction():
            root = self._row(memory_id)
            if root["status"] == "forgotten":
                result = self._flatten(root)
                result.update(
                    {
                        "forgotten_ids": [memory_id],
                        "derived_forgotten_ids": [],
                    }
                )
                return result
            _integer(revision, "revision")
            if revision != root["revision"]:
                raise StateError(
                    "revision conflict: current revision is %s" % root["revision"]
                )
            if policy_revision is None:
                self._human(evidence, memory_id, revision, "forget")
            else:
                from memory_governance import retention_authorized
                retention_authorized(self, self._flatten(root), policy_revision)
            identities = self._derived_descendants(memory_id)
            rows = {
                row["id"]: row
                for row in self.conn.execute(
                    "SELECT * FROM memory_records WHERE account=?",
                    (self.account,),
                )
                if row["id"] in identities and row["status"] != "forgotten"
            }
            stamp = utc_now()
            forgotten = []
            revisions = {}
            redacted_hash = hashlib.sha256(b"forgotten").hexdigest()
            for identity in sorted(rows):
                row = rows[identity]
                # Source identities survive erasure, including older corrected revisions.
                for history in self.conn.execute(
                        "SELECT data FROM memory_revisions WHERE memory_id=?", (identity,)):
                    for ref in parse_json(history["data"]).get("source_refs", []):
                        if ref["kind"] == "session_checkpoint":
                            from memory_dream import source_keys
                            for key in source_keys(ref):
                                alias, alias_hash = self._identity("user", key)
                                self.conn.execute(
                                    "INSERT OR IGNORE INTO memory_tombstones VALUES(?,?,?,?)",
                                    (alias, self.account, alias_hash, stamp))
                next_revision = row["revision"] + 1
                self.conn.execute(
                    """UPDATE memory_revisions SET data='{}',evidence=NULL,content_hash=NULL
                       WHERE memory_id=?""",
                    (identity,),
                )
                self.conn.execute(
                    """UPDATE memory_records SET revision=?,status='forgotten',data='{}',
                       indexed_content_hash=NULL,updated_at=?
                       WHERE id=? AND account=?""",
                    (next_revision, stamp, identity, self.account),
                )
                self.conn.execute(
                    """INSERT INTO memory_revisions
                       (memory_id,revision,status,domain,kind,data,evidence,content_hash,created_at)
                       VALUES(?,?,?,?,?,'{}',NULL,NULL,?)""",
                    (
                        identity,
                        next_revision,
                        "forgotten",
                        row["domain"],
                        row["kind"],
                        stamp,
                    ),
                )
                self.conn.execute(
                    """INSERT OR IGNORE INTO memory_tombstones
                       (id,account,key_hash,forgotten_at) VALUES(?,?,?,?)""",
                    (identity, self.account, row["key_hash"], stamp),
                )
                self.conn.execute(
                    """INSERT INTO memory_jobs(memory_id,revision,op,content_hash,created_at)
                       VALUES(?,?,'delete',?,?)
                       ON CONFLICT(memory_id) DO UPDATE SET
                        revision=excluded.revision,op='delete',
                        content_hash=excluded.content_hash,created_at=excluded.created_at""",
                    (identity, next_revision, redacted_hash, stamp),
                )
                self.conn.execute("DELETE FROM memory_meta WHERE key=?", ("index-blocked:" + identity,))
                forgotten.append(identity)
                revisions[identity] = next_revision
            if forgotten:
                placeholders = ",".join("?" for _ in forgotten)
                self.conn.execute(
                    "DELETE FROM memory_links WHERE source_id IN (%s) OR target_id IN (%s)"
                    % (placeholders, placeholders),
                    forgotten + forgotten,
                )
                tables = {row[0] for row in self.conn.execute(
                    "SELECT name FROM sqlite_master WHERE type='table'")}
                for table in ("semantic_vectors", "semantic_documents", "semantic_fts"):
                    if table in tables:
                        self.conn.execute("DELETE FROM %s WHERE memory_id IN (%s)" % (table, placeholders),
                                          forgotten)
                for usage in self.conn.execute(
                        "SELECT id,refs FROM memory_usage WHERE account=?", (self.account,)).fetchall():
                    remaining = [ref for ref in parse_json(usage["refs"]) if ref["id"] not in forgotten]
                    if remaining:
                        self.conn.execute("UPDATE memory_usage SET refs=? WHERE id=?",
                                          (canonical_json(remaining), usage["id"]))
                    else:
                        self.conn.execute("DELETE FROM memory_usage WHERE id=?", (usage["id"],))
            result = self.show(memory_id)
            result.update(
                {
                    "forgotten_ids": forgotten,
                    "derived_forgotten_ids": [
                        identity for identity in forgotten if identity != memory_id
                    ],
                }
            )
            return result

    def _environment_subset(self, required, current):
        if not isinstance(required, dict) or not isinstance(current, dict):
            return False
        for key, value in required.items():
            if key not in current:
                return False
            if isinstance(value, dict):
                if not self._environment_subset(value, current[key]):
                    return False
            elif current[key] != value:
                return False
        return True

    @contextlib.contextmanager
    def freshness_pass(self):
        # Never share this cache across the pre- and post-inference eligibility passes.
        owned = getattr(self, "_file_checks", None) is None
        if owned:
            self._file_checks = {}
        try:
            yield
        finally:
            if owned:
                self._file_checks = None

    def _file_digest(self, path):
        def identity(info):
            return (info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns)

        def signature(info):
            return identity(info) + (info.st_ctime_ns,)

        try:
            before = path.stat()
            if not stat.S_ISREG(before.st_mode) or before.st_size > MAX_SOURCE_FILE_BYTES:
                return None
            key = (str(path), signature(before))
            cache = getattr(self, "_file_checks", None)
            if cache is not None and key in cache:
                return cache[key]
            hasher, total = hashlib.sha256(), 0
            with path.open("rb") as stream:
                opened = os.fstat(stream.fileno())
                # Windows stat/fstat can disagree on ctime; compare it only within each API.
                if identity(opened) != identity(before):
                    return None
                while True:
                    chunk = stream.read(min(65536, MAX_SOURCE_FILE_BYTES + 1 - total))
                    if not chunk:
                        break
                    total += len(chunk)
                    if total > MAX_SOURCE_FILE_BYTES:
                        return None
                    hasher.update(chunk)
                after = os.fstat(stream.fileno())
            if (total != before.st_size or signature(after) != signature(opened)
                    or signature(path.stat()) != signature(before)):
                return None
            result = hasher.hexdigest()
            if cache is not None:
                cache[key] = result
            return result
        except OSError:
            return None

    def _work_sources_current(self, data, seen=None):
        seen = set(seen or ())
        for ref in data.get("source_refs", []):
            if ref.get("kind") == "session_checkpoint":
                from memory_dream import validate_source
                from memory_governance import digest
                try:
                    validate_source(self, ref)
                except StateError:
                    return False
                checkpoint = self.conn.execute(
                    "SELECT status,data FROM memory_records WHERE id=? AND account=?",
                    (self.record_id("user", "dream-event:" + digest(canonical_json(parse_json(ref["ref"])[:5]))),
                     self.account)).fetchone()
                if checkpoint is not None and (
                        checkpoint["status"] != "active"
                        or ref not in parse_json(checkpoint["data"]).get("source_refs", [])):
                    return False
            if ref.get("kind") == "memory_record":
                if ref["ref"] in seen:
                    return False
                row = self.conn.execute(
                    "SELECT * FROM memory_records WHERE id=? AND account=?", (ref["ref"], self.account)).fetchone()
                if row is None or row["status"] != "active" or str(row["revision"]) != ref.get("revision"):
                    return False
                parent = parse_json(row["data"])
                stamp = utc_now()
                if (parent.get("sensitivity") == "sensitive" or parent.get("authority") == "inferred"
                        or parent.get("valid_from", stamp) > stamp
                        or parent.get("valid_to", "9999") <= stamp
                        or parent.get("review_after", "9999") <= stamp
                        or not self._work_sources_current(parent, seen | {ref["ref"]})):
                    return False
                continue
            if ref.get("kind") == "file":
                if not ref.get("revision"):
                    return False
                path = Path(ref["ref"]).expanduser()
                if not path.is_absolute():
                    return False
                current_hash = self._file_digest(path)
                if current_hash != ref["revision"]:
                    return False
                continue
            if ref.get("kind") != "work_source":
                continue
            try:
                source = self.conn.execute(
                    "SELECT account,current_revision FROM work_sources WHERE id=?",
                    (ref["ref"],),
                ).fetchone()
            except sqlite3.OperationalError as exc:
                if "no such table: work_sources" in str(exc):
                    return False
                raise
            if (
                source is None
                or source["account"] != self.account
                or "revision" not in ref
                or str(source["current_revision"]) != str(ref["revision"])
            ):
                return False
            revision = self.conn.execute(
                "SELECT data FROM work_source_revisions WHERE source_id=? AND revision=?",
                (ref["ref"], str(ref["revision"]))).fetchone()
            if revision is None:
                return False
            sensitivity = parse_json(revision["data"]).get("sensitivity", "unknown")
            if not isinstance(sensitivity, str) or sensitivity.casefold() not in {"unknown", "public", "private", "none"}:
                return False
        return True

    @freshness_scoped
    def eligible(
        self,
        domain=None,
        routine=None,
        usage="reasoning",
        environment=None,
        include_stale=False,
    ):
        if domain is not None and domain not in DOMAINS:
            raise StateError("domain must be user or agent")
        if routine is not None:
            _text(routine, "routine")
        if usage not in USES:
            raise StateError("unsupported memory use")
        if environment is not None:
            _json_object(environment, "environment")
        if not isinstance(include_stale, bool):
            raise StateError("include_stale must be a boolean")
        statuses = ("active", "stale") if include_stale else ("active",)
        placeholders = ",".join("?" for _ in statuses)
        values = [self.account] + list(statuses)
        domain_clause = ""
        if domain is not None:
            domain_clause = " AND domain=?"
            values.append(domain)
        rows = self.conn.execute(
            """SELECT * FROM memory_records
               WHERE account=? AND status IN (%s)%s
               ORDER BY CASE WHEN kind='preference' THEN 0 ELSE 1 END,updated_at DESC,id"""
            % (placeholders, domain_clause),
            values,
        ).fetchall()
        current = datetime.now(timezone.utc)
        result = []
        for row in rows:
            data = parse_json(row["data"])
            if data.get("metadata", {}).get("dream") in {"snapshot", "checkpoint"}:
                continue
            if data.get("authority") == "inferred":
                continue
            if data.get("sensitivity") == "sensitive":
                continue
            if usage not in data.get("allowed_uses", []):
                continue
            routines = data.get("routines", [])
            if routine is None:
                if routines:
                    continue
            elif routines and routine not in routines:
                continue
            if data.get("valid_from") and current < _parse_time(data["valid_from"]):
                continue
            if data.get("valid_to") and current >= _parse_time(data["valid_to"]):
                continue
            if (
                not include_stale
                and data.get("review_after")
                and current >= _parse_time(data["review_after"])
            ):
                continue
            requirements = data.get("metadata", {}).get(
                "environment_requirements",
                data.get("metadata", {}).get("environment"),
            )
            if requirements and (
                environment is None
                or not self._environment_subset(requirements, environment)
            ):
                continue
            if not self._work_sources_current(data):
                continue
            result.append(self._flatten(row))
        eligible_ids = {record["id"] for record in result}
        superseded = {edge["target_id"] for record in result for edge in self.active_links(record["id"])
                      if edge["relation"] == "supersedes" and edge["source_id"] in eligible_ids}
        return [record for record in result if record["id"] not in superseded]

    def policy(self):
        from memory_governance import policy
        return policy(self)

    def capture(self, key, data, revision=None):
        from memory_governance import capture
        return capture(self, key, data, revision)

    def record_usage(self, refs, routine, outcome="used", event_id=None):
        if not isinstance(refs, list) or not 1 <= len(refs) <= 50:
            raise StateError("usage requires between 1 and 50 memory references")
        if outcome not in {"used", "helpful", "corrected", "discarded"}:
            raise StateError("unsupported usage outcome; do-not-learn records no learning event")
        _text(routine, "routine")
        event_id = event_id or "usage_" + uuid.uuid4().hex
        _text(event_id, "event_id")
        normalised = []
        with self.transaction():
            if not self.policy()["data"]["usage_enabled"]:
                raise StateError("memory usage capture is disabled by policy")
            for ref in refs:
                if not isinstance(ref, dict) or set(ref) != {"id", "revision"}:
                    raise StateError("usage references contain id and revision only")
                row = self._row(ref["id"])
                if row["revision"] != ref["revision"] or type(ref["revision"]) is not int or row["status"] != "active":
                    raise StateError("usage memory is not current and active")
                normalised.append(ref)
            encoded = canonical_json(sorted(normalised, key=lambda ref: ref["id"]))
            prior = self.conn.execute("SELECT * FROM memory_usage WHERE id=?", (event_id,)).fetchone()
            if prior:
                if (prior["account"], prior["routine"], prior["refs"], prior["outcome"]) != (
                        self.account, routine, encoded, outcome):
                    raise StateError("usage event replay changed its contents")
            else:
                self.conn.execute("INSERT INTO memory_usage VALUES(?,?,?,?,?,?)",
                                  (event_id, self.account, routine, encoded, outcome, utc_now()))
        return {"id": event_id, "recorded": True, "replayed": prior is not None}

    def usage(self, memory_id, limit=30):
        self._row(memory_id)
        if type(limit) is not int or not 1 <= limit <= 100:
            raise StateError("usage limit must be between 1 and 100")
        return [{"id": row["id"], "routine": row["routine"], "outcome": row["outcome"],
                 "created_at": row["created_at"], "refs": parse_json(row["refs"])}
                for row in self.conn.execute(
                    "SELECT * FROM memory_usage WHERE account=? AND refs LIKE ? ORDER BY created_at DESC LIMIT ?",
                    (self.account, '%"' + memory_id + '"%', limit))]

    def pending_jobs(self, limit=100):
        if type(limit) is not int or not 1 <= limit <= 10000:
            raise StateError("job limit must be between 1 and 10000")
        return [
            {
                "memory_id": row["memory_id"],
                "revision": row["revision"],
                "operation": row["op"],
                "content_hash": row["content_hash"],
            }
            for row in self.conn.execute(
                """SELECT memory_id,revision,op,content_hash,created_at
                   FROM memory_jobs WHERE op IN ('upsert','delete') ORDER BY created_at,memory_id LIMIT ?""",
                (limit,),
            )
        ]

    def complete_job(self, memory_id, revision):
        _integer(revision, "revision")
        with self.transaction():
            return bool(
                self.conn.execute(
                    "DELETE FROM memory_jobs WHERE memory_id=? AND revision=?",
                    (memory_id, revision),
                ).rowcount
            )

    def block_job(self, memory_id, revision, reason):
        _integer(revision, "revision")
        if reason != "representation_exceeds_limit":
            raise StateError("only deterministic representation-limit failures may be quarantined")
        with self.transaction():
            row = self._row(memory_id)
            if row["revision"] != revision or row["status"] != "active":
                return False
            data = parse_json(row["data"])
            if data.get("sensitivity") == "sensitive" or len(search_text(data)) <= MAX_MEMORY_TEXT_CHARS:
                return False
            previous = self.conn.execute("SELECT revision,op FROM memory_jobs WHERE memory_id=?", (memory_id,)).fetchone()
            if previous is not None and previous["revision"] == revision and previous["op"] == "blocked":
                return True
            stamp = utc_now()
            self.conn.execute(
                "INSERT INTO memory_jobs VALUES(?,?,'blocked',?,?) "
                "ON CONFLICT(memory_id) DO UPDATE SET revision=excluded.revision,op='blocked',"
                "content_hash=excluded.content_hash,created_at=excluded.created_at",
                (memory_id, revision, self._content_hash(data), stamp))
            self.conn.execute(
                "INSERT INTO memory_meta VALUES(?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                ("index-blocked:" + memory_id, canonical_json({"revision": revision, "reason": reason})))
            return True

    def blocked_jobs(self, limit=100):
        if type(limit) is not int or not 1 <= limit <= 10000:
            raise StateError("blocked job limit must be between 1 and 10000")
        result = []
        for row in self.conn.execute(
                "SELECT memory_id,revision FROM memory_jobs WHERE op='blocked' ORDER BY created_at,memory_id LIMIT ?",
                (limit,)):
            detail = self.conn.execute("SELECT value FROM memory_meta WHERE key=?",
                                       ("index-blocked:" + row["memory_id"],)).fetchone()
            result.append({"memory_id": row["memory_id"], "revision": row["revision"],
                           "reason": parse_json(detail[0])["reason"] if detail else "invalid_record"})
        return result

    @freshness_scoped
    def health(self):
        counts = {status: 0 for status in sorted(STATUSES)}
        for row in self.conn.execute(
            """SELECT status,count(*) AS count FROM memory_records
               WHERE account=? GROUP BY status""",
            (self.account,),
        ):
            counts[row["status"]] = row["count"]
        sensitive = review_due = stale_sources = 0
        stale_imports = []
        stamp = utc_now()
        for row in self.conn.execute(
            "SELECT id,data,status FROM memory_records WHERE account=? AND status<>'forgotten'",
            (self.account,),
        ):
            data = parse_json(row["data"])
            if data.get("sensitivity") == "sensitive":
                sensitive += 1
            if row["status"] == "active" and data.get("review_after", "9999") <= stamp:
                review_due += 1
            if row["status"] == "active" and not self._work_sources_current(data):
                stale_sources += 1
                if data.get("metadata", {}).get("imported_configuration") and any(
                        ref["kind"] == "file" for ref in data.get("source_refs", [])):
                    stale_imports.append(row["id"])
        conflict_sources = [row[0] for row in self.conn.execute(
            "SELECT DISTINCT source_id FROM memory_links WHERE relation='contradicts'")]
        conflicts = {(edge["source_id"], edge["target_id"]) for source in conflict_sources
                     for edge in self.active_links(source) if edge["relation"] == "contradicts"}
        warnings = []
        if sensitive:
            warnings.append(
                "Sensitive memories are stored but excluded from ordinary eligibility."
            )
        return {
            "schema_version": SCHEMA_VERSION,
            "account": self.account,
            "total": sum(counts.values()),
            "by_status": counts,
            "pending_jobs": self.conn.execute(
                "SELECT count(*) FROM memory_jobs WHERE op IN ('upsert','delete')"
            ).fetchone()[0],
            "blocked_jobs": self.blocked_jobs(),
            "blocked_job_count": self.conn.execute("SELECT count(*) FROM memory_jobs WHERE op='blocked'").fetchone()[0],
            "tombstones": self.conn.execute(
                "SELECT count(*) FROM memory_tombstones WHERE account=?",
                (self.account,),
            ).fetchone()[0],
            "sensitive_excluded": sensitive,
            "review_due": review_due,
            "stale_sources": stale_sources,
            "conflict_links": len(conflicts),
            "usage_events": self.conn.execute(
                "SELECT count(*) FROM memory_usage WHERE account=?", (self.account,)).fetchone()[0],
            "capture_enabled": self.policy()["data"]["capture"]["enabled"],
            "stale_imported_preferences": {"count": len(stale_imports), "ids": stale_imports[:50]},
            "recovery_actions": [PREFERENCES_RECOVERY] if stale_imports else [],
            "warnings": warnings,
        }

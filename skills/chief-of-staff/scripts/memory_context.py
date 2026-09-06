"""Read-only, bounded joins between memory and the authoritative local work ledger.

Public APIs: ``identity_key(identity)``, ``active_edges(memory, id)`` and
``ConnectedContext(memory, **eligibility_filters).build(seed_ids, work_ids=...)``.
MemorySearch.context/graph/explain are the stable caller-facing wrappers.

``entities`` are exact scoped IDs (or identity_key values), never display names.
``metadata.work_refs`` may contain canonical local item/outcome/meeting IDs, or
objects with ``id`` and optional ``kind``. These are pointers, not status copies.
Meeting decision_refs resolve to eligible, confirmed memory decisions by memory
ID or metadata.canonical_id; unresolved external decisions require a fresh read.
No calls here record queries, usage, observations, or approval.
"""

import contextlib
from collections import deque
from datetime import datetime, timezone
import json

from margo_store import StateError, canonical_json, utc_now
from memory_store import PREFERENCES_RECOVERY, scoped_entity_key


SECTIONS = ("constraints", "people_projects", "current_decisions", "current_outcomes",
            "open_work", "meetings", "scoped_lessons", "other_context")
RELATIONS = {"owns", "reports_to", "depends_on", "constrains", "advances", "discusses",
             "contradicts", "derives_from", "about", "related_to", "supersedes"}


def identity_key(identity):
    """Unambiguous, case-preserving provider/scope/external_id key for entities."""
    if (not isinstance(identity, dict) or set(identity) != {"provider", "scope", "external_id"}
            or not all(isinstance(value, str) and value.strip() for value in identity.values())):
        raise StateError("identity requires provider, scope and external_id")
    return "identity:" + canonical_json([identity[key] for key in ("provider", "scope", "external_id")])


def entity_keys(record):
    result = {key for value in record.get("entities", []) if (key := scoped_entity_key(value)) is not None}
    if record.get("identity"):
        result.add(identity_key(record["identity"]))
    return result


def applicable(data):
    stamp = datetime.now(timezone.utc)
    try:
        for key, before in (("valid_from", True), ("valid_to", False), ("review_after", False)):
            if data.get(key):
                value = datetime.fromisoformat(data[key].replace("Z", "+00:00"))
                if value.tzinfo is None or (stamp < value if before else stamp >= value):
                    return False
    except (TypeError, ValueError):
        return False
    return True


def active_edges(memory, memory_id):
    """Temporal/source-safe edge seam, including compatibility with older stores."""
    loader = getattr(memory, "active_links", memory.links)
    return [edge for edge in loader(memory_id)
            if edge.get("relation") in RELATIONS and applicable(edge.get("data", {}))]


@contextlib.contextmanager
def read_snapshot(conn):
    """Do not commit/rollback a caller's transaction or initialize another store."""
    owned = not conn.in_transaction
    if owned:
        conn.execute("BEGIN")
    try:
        yield
    finally:
        if owned:
            conn.rollback()


def bounds(max_depth, max_nodes):
    if type(max_depth) is not int or not 0 <= max_depth <= 3:
        raise StateError("context depth must be between 0 and 3")
    if type(max_nodes) is not int or not 1 <= max_nodes <= 50:
        raise StateError("context nodes must be between 1 and 50")


def memory_entry(row, reasons, mandatory=False):
    item = {key: row[key] for key in ("id", "revision", "domain", "kind", "title", "text",
                                     "authority", "scope", "source_refs")}
    item.update(mandatory=mandatory, selection_reasons=reasons,
                source_record={"store": "memory", "id": row["id"], "revision": row["revision"]},
                allowed_uses=row.get("allowed_uses", ["reasoning"]),
                sensitivity=row.get("sensitivity", "private"),
                copyable_to_draft="drafting" in row.get("allowed_uses", []))
    if row.get("identity"):
        item["identity"] = row["identity"]
    return item


def mandatory_constraint(row):
    return ((row["kind"] == "preference" and row["authority"] == "user_confirmed")
            or (row["domain"] == "user" and row.get("metadata", {}).get("imported_configuration")
                and row.get("metadata", {}).get("source_heading", "").casefold() == "about me"))


def section(entry):
    if entry.get("mandatory") or entry["kind"] in {"preference", "profile"}:
        return "constraints"
    return {"person": "people_projects", "project": "people_projects",
            "decision": "current_decisions", "outcome": "current_outcomes",
            "item": "open_work", "meeting": "meetings", "lesson": "scoped_lessons",
            "capability": "scoped_lessons"}.get(entry["kind"], "other_context")


def sized(result):
    result["used_chars"] = 0
    while True:
        size = len(canonical_json(result))
        if result["used_chars"] == size:
            return result
        result["used_chars"] = size


def budget_packet(result, budget_chars, graph=False):
    """Bound the complete canonical JSON, including wrappers, reasons and gaps."""
    if type(budget_chars) is not int or not 1000 <= budget_chars <= 50000:
        raise StateError("context budget must be between 1000 and 50000 characters")
    result.update(budget_chars=budget_chars, budget_scope="complete_canonical_json",
                  omitted={"entries": 0, "edges": 0, "gaps": 0})
    key = "nodes" if graph else "entries"
    original = result[key]
    required = [entry for entry in original if entry.get("mandatory")]
    optional = [entry for entry in original if not entry.get("mandatory")]
    result[key] = required + optional

    def refresh():
        ids = {entry["id"] for entry in result[key]}
        kept = [edge for edge in result["edges"]
                if edge["source_id"] in ids and edge["target_id"] in ids]
        result["omitted"]["edges"] += len(result["edges"]) - len(kept)
        result["edges"] = kept
        if not graph:
            result["sections"] = {name: [entry["id"] for entry in result[key] if section(entry) == name]
                                  for name in SECTIONS}
            result["sections"]["unknown_conflicting_evidence"] = list(range(len(result["gaps"])))
        return sized(result)["used_chars"]

    while refresh() > budget_chars:
        # Preserve mandatory constraints and uncertainty before optional substance.
        if optional:
            optional.pop()
            result[key] = required + optional
            result["omitted"]["entries"] += 1
        elif result["edges"]:
            result["edges"].pop()
            result["omitted"]["edges"] += 1
        elif result["gaps"]:
            result["gaps"].pop()
            result["omitted"]["gaps"] += 1
        else:
            return sized({"status": "blocked", "account": result["account"],
                          key: [], "requires_larger_budget": True,
                          "reason": "Mandatory constraints and packet envelope exceed the context budget.",
                          "mandatory_count": len(required), "budget_chars": budget_chars,
                          "budget_scope": "complete_canonical_json", "context_is_consent": False})
    return json.loads(canonical_json(result))


class ConnectedContext:
    def __init__(self, memory, **filters):
        self.memory, self.conn, self.filters = memory, memory.conn, filters
        self.current = {}
        # Re-read after eligibility: indexing/inference and external file checks can race edits.
        for row in memory.eligible(**filters):
            actual = memory.show(row["id"])
            if (actual["status"] == "active" and actual["revision"] == row["revision"]
                    and applicable(actual)):
                self.current[actual["id"]] = actual
        self.tables = {row[0] for row in self.conn.execute("SELECT name FROM sqlite_master WHERE type='table'")}
        self.gaps, self._gap_keys, self.edges = [], set(), []
        self.conflicted_ids = set()  # Safety state must not depend on truncated diagnostics.
        self.truncated = False

    def gap(self, code, origin=None, **details):
        value = dict(code=code, **details)
        if origin:
            value["origin_id"] = origin
        key = canonical_json(value)
        if key not in self._gap_keys:
            self._gap_keys.add(key)
            if len(self.gaps) < 50:
                self.gaps.append(value)
            else:
                self.truncated = True

    def _constraint_scope(self, row):
        if row.get("routines") and self.filters.get("routine") not in row["routines"]:
            return False
        if self.filters.get("domain") and row["domain"] != self.filters["domain"]:
            return False
        requirements = row.get("metadata", {}).get(
            "environment_requirements", row.get("metadata", {}).get("environment", {}))

        def contradicts(required, supplied):
            if not isinstance(required, dict) or not isinstance(supplied, dict):
                return False
            for key, value in required.items():
                if key not in supplied:
                    continue
                if isinstance(value, dict):
                    if contradicts(value, supplied[key]):
                        return True
                elif supplied[key] != value:
                    return True
            return False

        # Known scope mismatches are inapplicable; missing environment information
        # is uncertainty, not permission to discard an accepted constraint.
        return not contradicts(requirements, self.filters.get("environment"))

    def constraint_guard(self):
        """Fail closed without recalling excluded constraint text or source details.

        Eligibility filters are not evidence that a previously accepted prohibition
        has been revoked. Explicit suppression/forgetting and known scope/validity
        boundaries differ from uncertain freshness, review or contradictory evidence.
        """
        blocked = 0
        for row in self.memory.list(domain=self.filters.get("domain")):
            if (row["status"] in {"candidate", "rejected", "suppressed", "forgotten"}
                    or not mandatory_constraint(row) or not self._constraint_scope(row)):
                continue
            window = {key: row[key] for key in ("valid_from", "valid_to") if row.get(key)}
            if not applicable(window):
                self.gap("mandatory_constraint_outside_validity", record_id=row["id"],
                         revision=row["revision"], mandatory=True)
                continue
            if row["id"] not in self.current:
                successors = self._successors(row["id"])
                if successors and all(mandatory_constraint(self.current[value]) for value in successors):
                    continue
                reason = ("sensitive_content_withheld" if row.get("sensitivity") == "sensitive"
                          else "supersession_unresolved" if row["status"] == "superseded"
                          else "freshness_scope_or_source_unverified")
                recovery = {}
                if (row.get("metadata", {}).get("imported_configuration")
                        and any(ref["kind"] == "file" for ref in row.get("source_refs", []))
                        and not self.memory._work_sources_current(row)):
                    recovery["action"] = PREFERENCES_RECOVERY
                self.gap("mandatory_constraint_unavailable", record_id=row["id"], revision=row["revision"],
                         status=row["status"], reason=reason, mandatory=True, requires_revalidation=True, **recovery)
                blocked += 1
                continue
            conflict = bool(row.get("metadata", {}).get("conflicts"))
            for edge in self.memory.links(row["id"]):
                if edge["relation"] != "contradicts" or not applicable(edge.get("data", {})):
                    continue
                other_id = edge["target_id"] if edge["source_id"] == row["id"] else edge["source_id"]
                try:
                    other = self.memory.show(other_id)
                except (KeyError, StateError):
                    conflict = True
                    continue
                if (other["status"] not in {"forgotten", "suppressed", "rejected", "superseded"}
                        and self._constraint_scope(other)
                        and applicable({key: other[key] for key in ("valid_from", "valid_to") if other.get(key)})):
                    conflict = True
            if conflict:
                self.gap("mandatory_constraint_conflict", record_id=row["id"], revision=row["revision"],
                         status=row["status"], mandatory=True, requires_revalidation=True)
                blocked += 1
        return blocked

    def _diagnose(self, memory_id, origin=None):
        try:
            row = self.memory.show(memory_id)
        except (KeyError, StateError):
            self.gap("unavailable_reference", origin)
            return
        # Withheld, forgotten and audience-ineligible content is never a diagnostics back door.
        if (row.get("status") in {"forgotten", "suppressed", "rejected"}
                or row.get("sensitivity") == "sensitive"
                or (row.get("allowed_uses") and self.filters.get("usage", "reasoning") not in row["allowed_uses"])
                or (row.get("routines") and self.filters.get("routine") not in row["routines"])
                or (self.filters.get("domain") and row["domain"] != self.filters["domain"])):
            self.gap("unavailable_reference", origin)
            return
        self.gap("ineligible_memory", origin, record_id=row["id"], status=row["status"],
                 requires_revalidation=True)
        return row["status"]

    def _successors(self, memory_id):
        """Follow current supersession edges, with the same hard traversal ceiling."""
        pending, seen, found = [(memory_id, 0)], set(), []
        while pending and len(seen) < 50:
            identity, depth = pending.pop()
            if identity in seen:
                continue
            seen.add(identity)
            try:
                record = self.memory.show(identity)
                if record["status"] not in {"active", "superseded"} or record.get("sensitivity") == "sensitive":
                    continue
                edges = active_edges(self.memory, identity)
                for edge in self.memory.links(identity):
                    if (edge["relation"] == "supersedes" and edge["target_id"] == identity
                            and edge not in edges and applicable(edge.get("data", {}))):
                        # Lifecycle retirement may hide an otherwise valid historical edge.
                        # Extra source evidence still needs the store's live verification.
                        if not edge.get("data", {}).get("source_refs"):
                            edges.append(edge)
                        else:
                            self.gap("supersession_evidence_unavailable", identity,
                                     requires_revalidation=True)
            except (KeyError, StateError):
                continue
            for edge in edges[:100]:
                if edge["relation"] != "supersedes" or edge["target_id"] != identity:
                    continue
                source = edge["source_id"]
                if source in self.current:
                    found.append(source)
                elif depth < 2:
                    pending.append((source, depth + 1))
                else:
                    self.gap("supersession_limit", memory_id, requires_revalidation=True)
        return sorted(set(found))

    def _work(self, work_id):
        for table, kind in (("work_items", "item"), ("work_records", None)):
            if table not in self.tables:
                continue
            row = self.conn.execute("SELECT * FROM " + table + " WHERE id=? AND account=?",
                                    (work_id, self.memory.account)).fetchone()
            if row:
                row = dict(row)
                row["kind"] = kind or row["kind"]
                row["data"] = json.loads(row["data"])
                return row
        return None

    def _sources(self, refs, origin):
        result, valid = [], True
        for ref in refs:
            if not {"work_sources", "work_source_revisions"} <= self.tables:
                self.gap("source_unavailable", origin)
                valid = False
                continue
            source = self.conn.execute("""SELECT s.current_revision,r.fingerprint,r.data
                FROM work_sources s LEFT JOIN work_source_revisions r
                ON s.id=r.source_id AND s.current_revision=r.revision
                WHERE s.id=? AND s.account=?""", (ref.get("source_id"), self.memory.account)).fetchone()
            if not source or not source["data"]:
                self.gap("source_unavailable", origin)
                valid = False
                continue
            data = json.loads(source["data"])
            sensitivity = data.get("sensitivity", "unknown")
            if (not isinstance(sensitivity, str)
                    or sensitivity.casefold() not in {"public", "private", "none", "unknown"}):
                self.gap("source_withheld", origin)
                valid = False
                continue
            current = {"source_id": ref["source_id"], "revision": source["current_revision"],
                       "fingerprint": source["fingerprint"], "web_link": data.get("web_link")}
            result.append(current)
            if source["current_revision"] != ref.get("revision") or source["fingerprint"] != ref.get("fingerprint"):
                self.gap("source_changed", origin, current_source=current, requires_revalidation=True)
                valid = False
        return result, valid

    def _work_entry(self, row, reasons):
        data, kind, state = row["data"], row["kind"], row["state"]
        sources, valid = self._sources(data.get("source_refs", []), row["id"])
        if not valid:
            return None
        if kind == "item" and (not row["confirmed"] or state not in {"confirmed", "active", "waiting", "deferred"}):
            self.gap("work_not_open" if row["confirmed"] else "unconfirmed_work",
                     row["id"], state=state, source_record={"store": "work", "id": row["id"],
                                                         "revision": row["revision"]})
            return None
        if kind == "outcome" and state not in {"agreed", "active"}:
            self.gap("outcome_not_current", row["id"], state=state)
            return None
        if kind == "meeting" and state == "cancelled":
            self.gap("meeting_cancelled", row["id"])
            return None
        if kind not in {"item", "outcome", "meeting"}:
            self.gap("unsupported_work_reference", row["id"])
            return None
        for field in (("owner", "due") if kind == "item" else
                      ("owner", "due", "effort", "definition_of_done") if kind == "outcome" else ()):
            if data.get(field) is None:
                self.gap("unknown_work_field", row["id"], field=field)
        item = {"id": row["id"], "revision": row["revision"], "kind": kind,
                "domain": "work", "title": data.get("title", kind), "state": state,
                "authority": "confirmed_work" if kind == "item" else
                             "agreed_outcome" if kind == "outcome" else "recorded_meeting",
                "source_refs": sources, "source_record": {"store": "work", "id": row["id"],
                                                         "revision": row["revision"]},
                "selection_reasons": reasons, "mandatory": False}
        for field in ("owner", "due", "effort", "definition_of_done", "next_step", "blocker",
                      "scheduled_start", "scheduled_end", "series_id", "occurrence_id", "attendance"):
            if field in data:
                item[field] = data[field]
        if kind == "meeting" and data.get("recap_retry", {}).get("status") in {"blocked", "exhausted"}:
            self.gap("recap_unavailable", row["id"])
        return item

    def _work_ids(self, record):
        values = record.get("metadata", {}).get("work_refs", [])
        if not isinstance(values, list):
            self.gap("invalid_work_references", record["id"])
            values = []
        values = list(values)
        values += [value for value in record.get("entities", [])
                   if value.startswith(("item_", "outcome_", "meeting_"))]
        return [value.get("id") if isinstance(value, dict) else value for value in values
                if isinstance(value, str) or isinstance(value, dict) and isinstance(value.get("id"), str)]

    def _related_work(self, records, entities):
        """Bounded discovery; exact pointers are never dependent on this scan."""
        source_owners = {ref["ref"]: row["id"] for row in records for ref in row.get("source_refs", [])
                         if ref.get("kind") == "work_source"}
        wanted_sources = set(source_owners)
        if not wanted_sources and not entities:
            return []
        found = []
        for table in ("work_items", "work_records"):
            if table not in self.tables:
                continue
            rows = self.conn.execute("SELECT id,data FROM " + table +
                                     " WHERE account=? ORDER BY updated_at DESC,id LIMIT 201",
                                     (self.memory.account,)).fetchall()
            if len(rows) > 200:
                self.gap("work_discovery_limit", requires_revalidation=True)
                self.truncated = True
            for row in rows[:200]:
                data = json.loads(row["data"])
                shared = wanted_sources & {ref.get("source_id") for ref in data.get("source_refs", [])}
                keys = {key for value in data.get("entities", []) if (key := scoped_entity_key(value)) is not None}
                # A plain owner label is not a provider-scoped identity.
                if shared or entities & keys or row["id"] in entities:
                    found.append((row["id"], "shared_source" if shared else "exact_entity",
                                  source_owners[sorted(shared)[0]] if shared else None))
        return found

    def _decision(self, ref, origin):
        canonical_id = ref.get("canonical_id")
        candidates = [row for row in self.current.values() if row["kind"] == "decision"
                      and (row["id"] == canonical_id or row.get("metadata", {}).get("canonical_id") == canonical_id)]
        candidates = [row for row in candidates if row["authority"] == "user_confirmed"]
        if not candidates and isinstance(canonical_id, str):
            candidates = [self.current[identity] for identity in self._successors(canonical_id)
                          if self.current[identity]["kind"] == "decision"
                          and self.current[identity]["authority"] == "user_confirmed"]
            if candidates:
                self.gap("decision_reference_superseded", origin, canonical_id=canonical_id,
                         replacement_ids=[row["id"] for row in candidates])
        if ref.get("status") != "current" or len(candidates) != 1:
            if len(candidates) > 1:
                self.conflicted_ids.update(row["id"] for row in candidates)
            self.gap("decision_" + ("conflict" if len(candidates) > 1 else "unresolved"), origin,
                     canonical_id=canonical_id, reported_status=ref.get("status"),
                     web_link=ref.get("web_link"), requires_revalidation=True)
            return None
        return candidates[0]["id"]

    def build(self, seed_ids, *, entities=None, work_ids=None, max_depth=2, max_nodes=30):
        bounds(max_depth, max_nodes)
        entities = set(entities or [])
        seeds = list(dict.fromkeys(seed_ids))
        for identity in list(seeds):
            if identity not in self.current:
                successors = self._successors(identity)
                if successors:
                    self.gap("memory_superseded", record_id=identity, replacement_ids=successors)
                    seeds.extend(value for value in successors if value not in seeds)
        for row in self.current.values():
            if row["id"] in entities or entities & entity_keys(row):
                if row["id"] not in seeds:
                    seeds.append(row["id"])
        if entities:
            for row in self.memory.list(domain=self.filters.get("domain")):
                if row["id"] not in self.current and (row["id"] in entities or entities & entity_keys(row)):
                    successors = self._successors(row["id"])
                    if successors:
                        self.gap("memory_superseded", record_id=row["id"], replacement_ids=successors)
                        seeds.extend(identity for identity in successors if identity not in seeds)
                    else:
                        self._diagnose(row["id"])
        nodes, selected, queue = {}, {}, deque()

        def enqueue(identity, depth, reason, origin=None, relation=None):
            if identity in selected:
                if reason not in selected[identity]["reasons"]:
                    selected[identity]["reasons"].append(reason)
                if origin and len(self.edges) < max_nodes * 2:
                    self.edges.append({"source_id": origin, "target_id": identity, "relation": relation,
                                       "selection_reason": reason})
                return
            if depth > max_depth or len(selected) >= max_nodes:
                self.truncated = True
                return
            selected[identity] = {"depth": depth, "reasons": [reason]}
            queue.append(identity)
            if origin:
                self.edges.append({"source_id": origin, "target_id": identity, "relation": relation,
                                   "selection_reason": reason})

        for identity in seeds:
            if identity in self.current:
                enqueue(identity, 0, "exact_entity" if identity in entities or
                        entities & entity_keys(self.current[identity]) else "retrieval")
        for identity in work_ids or []:
            enqueue(identity, 0, "explicit_work_reference")
        selected_records = [self.current[identity] for identity in seeds if identity in self.current]
        for identity, reason, origin in self._related_work(selected_records, entities):
            enqueue(identity, 1 if origin else 0, reason, origin, "shared_source")
        while queue:
            identity = queue.popleft()
            depth, reasons = selected[identity]["depth"], selected[identity]["reasons"]
            row = self.current.get(identity)
            if row:
                if row["kind"] == "decision" and row["authority"] != "user_confirmed":
                    self.gap("unconfirmed_decision", identity)
                    continue
                nodes[identity] = memory_entry(row, reasons)
                if row["kind"] in {"person", "project"} and not entity_keys(row):
                    self.gap("unresolved_identity", identity)
                for key in ("conflicts", "gaps", "coverage"):
                    if row.get("metadata", {}).get(key):
                        if key == "conflicts":
                            self.conflicted_ids.add(identity)
                        self.gap("reported_" + key, identity, evidence=row["metadata"][key],
                                 requires_revalidation=True)
                available_edges = active_edges(self.memory, identity)
                for edge in self.memory.links(identity):
                    other = edge["target_id"] if edge["source_id"] == identity else edge["source_id"]
                    if not applicable(edge.get("data", {})):
                        self.gap("relationship_outside_validity", identity, relation=edge["relation"])
                    elif other not in self.current:
                        successors = self._successors(other)
                        if successors:
                            self.gap("memory_superseded", identity, record_id=other,
                                     replacement_ids=successors)
                            for successor in successors:
                                enqueue(successor, depth + 1, "current_superseding_memory", identity, "about")
                        else:
                            status = self._diagnose(other, identity)
                            if edge["relation"] == "contradicts" and status == "disputed":
                                self.conflicted_ids.update((identity, other))
                                self.gap("conflicting_memories", identity,
                                         record_ids=sorted([identity, other]),
                                         counterpart_status="disputed", requires_revalidation=True)
                    elif edge not in available_edges:
                        self.gap("relationship_evidence_unavailable", identity, relation=edge["relation"])
                for edge in available_edges:
                    other = edge["target_id"] if edge["source_id"] == identity else edge["source_id"]
                    if other not in self.current:
                        continue
                    if edge["relation"] == "contradicts":
                        self.conflicted_ids.update((identity, other))
                        self.gap("conflicting_memories", identity, record_ids=sorted([identity, other]),
                                 requires_revalidation=True)
                    enqueue(other, depth + 1, "relationship:" + edge["relation"])
                    if len(self.edges) < max_nodes * 2:
                        value = {key: edge[key] for key in ("source_id", "target_id", "relation")}
                        value["source_refs"] = edge.get("data", {}).get("source_refs", [])
                        if value not in self.edges:
                            self.edges.append(value)
                    else:
                        self.truncated = True
                if depth < max_depth:
                    keys = entity_keys(row)
                    for other in self.current.values():
                        if other["id"] != identity and keys & entity_keys(other):
                            enqueue(other["id"], depth + 1, "exact_entity", identity, "shared_entity")
                    for work_id in self._work_ids(row):
                        enqueue(work_id, depth + 1, "memory_work_reference", identity, "about")
                elif self._work_ids(row):
                    self.truncated = True
            else:
                work = self._work(identity)
                if work is None:
                    self.gap("work_reference_unavailable", identity)
                    continue
                item = self._work_entry(work, reasons)
                if item is None:
                    continue
                nodes[identity] = item
                data = work["data"]
                targets = [(value, "advanced_by" if work["kind"] == "outcome" else "discusses")
                           for value in data.get("work_item_ids", [])]
                if data.get("outcome_id"):
                    targets.append((data["outcome_id"], "advances"))
                for ref in data.get("decision_refs", []):
                    decision = self._decision(ref, identity)
                    if decision:
                        targets.append((decision, "discusses"))
                if work["kind"] == "item" and "work_relationships" in self.tables:
                    edges = self.conn.execute("""SELECT source_id,target_id,kind FROM work_relationships
                        WHERE source_id=? OR target_id=? ORDER BY kind,source_id,target_id LIMIT ?""",
                                              (identity, identity, max_nodes * 2 + 1)).fetchall()
                    if len(edges) > max_nodes * 2:
                        self.truncated = True
                    for edge in edges[:max_nodes * 2]:
                        other = edge["target_id"] if edge["source_id"] == identity else edge["source_id"]
                        if edge["kind"] == "tracked_in":
                            continue
                        if edge["kind"] == "supersedes" and edge["target_id"] == identity:
                            newer = self._work(other)
                            if newer and newer.get("confirmed"):
                                nodes.pop(identity, None)
                                self.gap("work_superseded", identity, replacement_id=other)
                        relation = edge["kind"]
                        if edge["target_id"] == identity:
                            relation = {"blocks": "blocked_by", "depends_on": "dependency_of",
                                        "supersedes": "superseded_by"}.get(relation, relation)
                        targets.append((other, relation))
                for target, relation in targets:
                    enqueue(target, depth + 1, "work_relationship:" + relation, identity, relation)
        # Contradictions are evidence, never an authoritative current decision.
        for identity in list(nodes):
            if nodes[identity]["kind"] == "decision" and identity in self.conflicted_ids:
                del nodes[identity]
            elif identity in self.conflicted_ids:
                nodes[identity]["conflicted"] = True
        canonical = {}
        for row in self.current.values():
            key = row.get("metadata", {}).get("canonical_id")
            if row["kind"] == "decision" and key and row["authority"] == "user_confirmed":
                canonical.setdefault(key, []).append(row["id"])
        for key, identities in canonical.items():
            if len(identities) > 1 and any(identity in nodes for identity in identities):
                self.conflicted_ids.update(identities)
                self.gap("decision_conflict", canonical_id=key, record_ids=identities,
                         requires_revalidation=True)
                for identity in identities:
                    nodes.pop(identity, None)
        edges = []
        for edge in self.edges:
            if edge["source_id"] not in nodes or edge["target_id"] not in nodes:
                continue
            reverse = {"advanced_by": "advances", "blocked_by": "blocks",
                       "dependency_of": "depends_on", "superseded_by": "supersedes"}
            if edge["relation"] in reverse:
                edge = dict(edge, source_id=edge["target_id"], target_id=edge["source_id"],
                            relation=reverse[edge["relation"]])
            if edge not in edges:
                edges.append(edge)
        if len(edges) > max_nodes * 2:
            self.truncated = True
            edges = edges[:max_nodes * 2]
        for identity, node in nodes.items():
            node["depth"] = selected[identity]["depth"]
        return {"nodes": list(nodes.values()), "edges": edges, "gaps": self.gaps,
                "truncated": self.truncated, "max_depth": max_depth, "max_nodes": max_nodes,
                "conflicted_count": len(self.conflicted_ids)}

"""Synthetic, in-memory integration tests over the real memory/work schemas."""

import json
from pathlib import Path
import sqlite3
import subprocess
import sys
import unittest

SCRIPTS = Path(__file__).resolve().parents[1] / "skills/chief-of-staff/scripts"
sys.path.insert(0, str(SCRIPTS))
from margo_store import StateError, canonical_json, utc_now
from memory_context import identity_key
from memory_search import MemorySearch
from memory_store import MemoryStore
from work_ledger import Ledger, SCHEMA as WORK_SCHEMA
from work_productivity import Productivity


class ConnectedContextTests(unittest.TestCase):
    def setUp(self):
        self.conn = sqlite3.connect(":memory:")
        self.conn.row_factory = sqlite3.Row
        self.addCleanup(self.conn.close)
        self.conn.executescript("CREATE TABLE margo_meta (key TEXT PRIMARY KEY,value TEXT NOT NULL);")
        self.conn.executescript(WORK_SCHEMA)
        self.memory = MemoryStore.__new__(MemoryStore)
        self.memory.account = "context-fixture@example.com"
        self.memory.conn = self.conn
        self.memory._initialize_schema()
        self.ledger = Ledger.__new__(Ledger)
        self.ledger.account, self.ledger.conn = self.memory.account, self.conn
        self.productivity = Productivity(self.ledger)
        self.search = MemorySearch(self.memory, encoder=lambda _: self.fail("Unexpected embedding request"))

    def human(self, identity, revision=1, decision="confirm"):
        return {"kind": "human_confirmation", "actor": "dana@example.com",
                "subject_id": identity, "revision": revision, "decision": decision,
                "statement": "Confirm this synthetic fixture.", "evidence_ref": "conversation:fixture",
                "decided_at": utc_now()}

    def remember(self, key, kind="project", status="active", **fields):
        data = {"domain": "user", "kind": kind, "title": key, "text": "Synthetic " + key,
                "authority": "user_confirmed", "scope": "fixture",
                "source_refs": [{"kind": "user_statement", "ref": "conversation:fixture"}]}
        data.update(fields)
        identity = self.memory.record_id(data["domain"], key)
        return self.memory.put(key, data, status=status, revision=1 if status == "active" else None,
                               evidence=self.human(identity) if status == "active" else None)

    def link(self, left, right, relation="related_to", **data):
        left, right = self.memory.show(left["id"]), self.memory.show(right["id"])
        if relation not in {"related_to", "about", "derives_from", "supersedes"}:
            data.setdefault("valid_from", "2020-01-01T00:00:00Z")
            data.setdefault("source_refs", [{"kind": "user_statement", "ref": "conversation:fixture"}])
        return self.memory.link(left["id"], right["id"], relation, left["revision"],
                                evidence=self.human(left["id"], left["revision"], "relate"),
                                data=data, target_revision=right["revision"])

    def source(self, revision="1", sensitivity="private"):
        return self.ledger.source("calendar", "fixture-scope", "event-fixture", revision,
                                  {"summary": "Fictional source"}, "https://example.com/event",
                                  sensitivity=sensitivity)

    def work_item(self, confirmed=True, source=None, **fields):
        data = {"title": "Prepare fictional review", "owner": None, "due": None,
                "direction": "own", "source_refs": [source] if source else [],
                "confirmation_source": "conversation:fixture"}
        data.update(fields)
        row = self.ledger.ingest(data, "fixture-" + str(self.conn.total_changes))
        if confirmed:
            row = self.ledger.update_item(row["id"], row["revision"], state="confirmed",
                                          evidence=self.human(row["id"], row["revision"]))
        return row

    def meeting(self, source=None, **fields):
        data = {"series_id": "series:fixture", "occurrence_id": "occurrence:fixture",
                "scheduled_start": "2026-09-07T10:00:00Z", "scheduled_end": "2026-09-07T11:00:00Z",
                "source_refs": [source or self.source()]}
        data.update(fields)
        return self.productivity.put("meeting", data["occurrence_id"], data)

    def context(self, **kwargs):
        return self.search.context("unmatched-query", mode="lexical", **kwargs)

    def test_live_work_state_and_refs_not_duplicated_into_memory(self):
        item = self.work_item(source=self.source())
        project = self.remember("project", metadata={"work_refs": [item["id"]]})
        before = self.conn.execute("SELECT count(*) FROM memory_records").fetchone()[0]
        packet = self.context(entities=[project["id"]])
        entry = next(entry for entry in packet["entries"] if entry["id"] == item["id"])
        self.assertEqual(entry["state"], "confirmed")
        self.assertEqual(entry["source_refs"][0]["web_link"], "https://example.com/event")
        self.assertIn(item["id"], packet["sections"]["open_work"])
        item = self.ledger.update_item(item["id"], item["revision"], state="resolved",
                                       evidence=self.human(item["id"], item["revision"], "resolve"))
        fresh = MemorySearch(self.memory).context("unmatched-query", entities=[project["id"]], mode="lexical")
        self.assertNotIn(item["id"], [entry["id"] for entry in fresh["entries"]])
        self.assertTrue(any(gap["code"] == "work_not_open" for gap in fresh["gaps"]))
        self.assertEqual(before, self.conn.execute("SELECT count(*) FROM memory_records").fetchone()[0])

    def test_work_to_outcome_and_meeting_to_work_context(self):
        item = self.work_item()
        data = {"week": "2026-09-07", "title": "Fictional outcome", "owner": "person:fixture",
                "definition_of_done": "Reviewed", "due": "2026-09-11", "effort": 60,
                "next_step": "Prepare review", "allocation": "focus:fixture", "blocker": None,
                "work_item_ids": [item["id"]], "source_refs": []}
        identity = self.productivity.record_id("outcome", "outcome-fixture")
        outcome = self.productivity.put("outcome", "outcome-fixture", data, state="agreed",
                                         evidence=self.human(identity))
        meeting = self.meeting(work_item_ids=[item["id"]])
        packet = self.context(work_ids=[outcome["id"], meeting["id"]])
        self.assertEqual(packet["sections"]["current_outcomes"], [outcome["id"]])
        self.assertEqual(packet["sections"]["meetings"], [meeting["id"]])
        self.assertIn(item["id"], packet["sections"]["open_work"])
        self.assertTrue(any(gap.get("field") == "owner" for gap in packet["gaps"]))

    def test_supersession_chain_returns_only_current_confirmed_decision(self):
        project = self.remember("project")
        old = self.remember("old", kind="decision")
        middle = self.remember("middle", kind="decision")
        current = self.remember("current", kind="decision")
        self.link(project, old, "about")
        self.link(middle, old, "supersedes")
        self.link(current, middle, "supersedes")
        packet = self.context(entities=[project["id"]])
        self.assertEqual(packet["sections"]["current_decisions"], [current["id"]])
        self.assertNotIn(old["id"], [entry["id"] for entry in packet["entries"]])
        self.assertTrue(any(gap["code"] == "memory_superseded" for gap in packet["gaps"]))

    def test_meeting_decision_ref_follows_supersession_without_copying_decision(self):
        old = self.remember("old", kind="decision")
        current = self.remember("current", kind="decision")
        self.link(current, old, "supersedes")
        meeting = self.meeting(decision_refs=[
            {"canonical_id": old["id"], "web_link": "https://example.com/decision", "status": "current"},
            {"canonical_id": "external:unknown", "web_link": "https://example.com/unknown", "status": "unknown"},
        ])
        packet = self.context(work_ids=[meeting["id"]])
        self.assertEqual(packet["sections"]["current_decisions"], [current["id"]])
        self.assertTrue(any(gap["code"] == "decision_unresolved" for gap in packet["gaps"]))

    def test_explicitly_retired_decision_resolves_to_current_successor(self):
        old = self.remember("old", kind="decision")
        current = self.remember("current", kind="decision")
        self.link(current, old, "supersedes")
        data = json.loads(self.conn.execute("SELECT data FROM memory_records WHERE id=?",
                                           (old["id"],)).fetchone()[0])
        self.memory.revise(old["id"], data, "superseded", old["revision"],
                           self.human(old["id"], old["revision"], "supersede"))
        graph = self.search.explain(old["id"])
        self.assertEqual([node["id"] for node in graph["nodes"]], [current["id"]])
        self.assertTrue(any(gap["code"] == "memory_superseded" for gap in graph["gaps"]))

    def test_unconfirmed_and_conflicting_decisions_never_become_authority(self):
        observed = self.remember("observed", kind="decision", authority="source_observed",
                                 entities=["project:one"])
        first = self.remember("first", kind="decision", entities=["project:one"])
        second = self.remember("second", kind="decision", entities=["project:one"])
        self.link(first, second, "contradicts")
        packet = self.context(entities=["project:one"])
        self.assertEqual(packet["sections"]["current_decisions"], [])
        self.assertTrue(any(gap["code"] == "unconfirmed_decision" for gap in packet["gaps"]))
        self.assertTrue(any(gap["code"] == "conflicting_memories" for gap in packet["gaps"]))
        self.assertNotIn(observed["id"], [entry["id"] for entry in packet["entries"]])

    def test_duplicate_current_canonical_decisions_are_conflict_not_last_write_wins(self):
        for key in ("first", "second"):
            self.remember(key, kind="decision", metadata={"canonical_id": "decision:one"},
                          entities=["project:one"])
        packet = self.context(entities=["project:one"])
        self.assertEqual(packet["sections"]["current_decisions"], [])
        self.assertTrue(any(gap["code"] == "decision_conflict" for gap in packet["gaps"]))

    def test_diagnostic_limit_never_restores_conflicting_decision_authority(self):
        first = self.remember("first", kind="decision")
        second = self.remember("second", kind="decision")
        self.link(first, second, "contradicts")
        references = [{"canonical_id": "external:missing-%d" % number, "status": "unknown",
                       "web_link": "https://example.com/missing"} for number in range(50)]
        references.append({"canonical_id": first["id"], "status": "current",
                           "web_link": "https://example.com/decision"})
        meeting = self.meeting(decision_refs=references)
        packet = self.context(work_ids=[meeting["id"]], budget_chars=50000)
        self.assertEqual(len(packet["gaps"]), 50)
        self.assertTrue(packet["truncated"])
        self.assertEqual(packet["sections"]["current_decisions"], [])
        self.assertEqual(packet["conflicted_count"], 2)

    def test_disputed_contradiction_still_prevents_unqualified_decision_authority(self):
        current = self.remember("current", kind="decision", entities=["project:one"])
        dispute = self.remember("dispute", kind="decision", status="disputed")
        self.link(current, dispute, "contradicts")
        packet = self.context(entities=["project:one"])
        self.assertEqual(packet["sections"]["current_decisions"], [])
        self.assertTrue(any(gap.get("counterpart_status") == "disputed" for gap in packet["gaps"]))

    def test_same_name_is_not_identity_and_scopes_do_not_merge(self):
        first_id = {"provider": "directory", "scope": "scope-a", "external_id": "one"}
        second_id = dict(first_id, scope="scope-b")
        first = self.remember("first", kind="person", title="Dana", identity=first_id)
        second = self.remember("second", kind="person", title="Dana", identity=second_id)
        project = self.remember("project")
        self.link(first, project, "owns")
        packet = self.context(entities=[identity_key(first_id)])
        ids = {entry["id"] for entry in packet["entries"]}
        self.assertIn(first["id"], ids)
        self.assertIn(project["id"], ids)
        self.assertNotIn(second["id"], ids)
        graph = self.search.graph(first["id"])
        self.assertNotIn(second["id"], [entry["id"] for entry in graph["nodes"]])
        self.assertNotEqual(identity_key(first_id), identity_key(second_id))
        graph = self.search.graph(identity_key(first_id))
        self.assertIn(first["id"], [entry["id"] for entry in graph["nodes"]])
        self.assertNotIn(second["id"], [entry["id"] for entry in graph["nodes"]])
        self.assertTrue(any("exact_entity" in entry["selection_reasons"] for entry in graph["nodes"]))

    def test_identity_key_is_case_sensitive_and_unambiguous_with_delimiters(self):
        identity = {"provider": "directory", "scope": "scope:a", "external_id": 'person:"one"'}
        other = {"provider": "directory", "scope": "scope", "external_id": 'a:person:"one"'}
        self.assertNotEqual(identity_key(identity), identity_key(other))
        self.assertEqual(identity_key(identity), 'identity:["directory","scope:a","person:\\"one\\""]')
        person = self.remember("person", kind="person", identity=identity, title="Dana")
        packet = self.search.context(identity_key(identity), mode="lexical")
        self.assertIn(person["id"], [row["id"] for row in packet["entries"]])
        self.assertEqual(self.search.graph(identity_key(dict(identity, provider="DIRECTORY")))["nodes"], [])

    def test_expired_relationship_and_routine_ineligible_endpoint_are_withheld(self):
        person = self.remember("person", kind="person")
        expired = self.remember("expired", kind="project")
        scoped = self.remember("scoped", kind="project", routines=["drafting"])
        self.link(person, expired, "owns", valid_to="2021-01-01T00:00:00Z")
        self.link(person, scoped, "owns")
        graph = self.search.graph(person["id"], routine="calendar")
        self.assertEqual([row["id"] for row in graph["nodes"]], [person["id"]])
        self.assertEqual(graph["edges"], [])
        self.assertTrue(any(gap["code"] == "relationship_outside_validity" for gap in graph["gaps"]))
        self.assertNotIn("Synthetic scoped", canonical_json(graph))

    def test_relationship_source_change_invalidates_edge_without_hiding_gap(self):
        source = self.source()
        person = self.remember("person", kind="person")
        project = self.remember("project")
        self.link(person, project, "owns", source_refs=[
            {"kind": "work_source", "ref": source["source_id"], "revision": source["revision"]}])
        self.source("2")
        graph = self.search.graph(person["id"])
        self.assertEqual([row["id"] for row in graph["nodes"]], [person["id"]])
        self.assertTrue(any(gap["code"] == "relationship_evidence_unavailable" for gap in graph["gaps"]))

    def test_environment_filter_applies_to_graph_neighbors(self):
        first = self.remember("first")
        scoped = self.remember("scoped", metadata={"environment_requirements": {"host": "desktop"}})
        self.link(first, scoped)
        graph = self.search.graph(first["id"], environment={"host": "other"})
        self.assertEqual([row["id"] for row in graph["nodes"]], [first["id"]])
        graph = self.search.graph(first["id"], environment={"host": "desktop"})
        self.assertEqual({row["id"] for row in graph["nodes"]}, {first["id"], scoped["id"]})

    def test_disputed_and_coverage_evidence_preserved_without_claim_authority(self):
        project = self.remember("project", metadata={"coverage": {"complete": False, "source": "mail"},
                                                    "gaps": [{"code": "permission_denied"}]})
        dispute = self.remember("dispute", status="disputed")
        self.link(project, dispute)
        packet = self.context(entities=[project["id"]])
        self.assertNotIn(dispute["id"], [entry["id"] for entry in packet["entries"]])
        self.assertTrue(any(gap.get("status") == "disputed" for gap in packet["gaps"]))
        coverage = next(gap for gap in packet["gaps"] if gap["code"] == "reported_coverage")
        self.assertEqual(coverage["evidence"], {"complete": False, "source": "mail"})
        self.assertTrue(packet["sections"]["unknown_conflicting_evidence"])

    def test_exact_disputed_entity_is_a_gap_even_without_active_neighbors(self):
        disputed = self.remember("dispute", status="disputed", entities=["project:one"])
        packet = self.context(entities=["project:one"])
        self.assertEqual(packet["entries"], [])
        self.assertTrue(any(gap.get("record_id") == disputed["id"] and gap.get("status") == "disputed"
                            for gap in packet["gaps"]))

    def test_shared_source_discovery_obeys_graph_depth(self):
        source = self.source()
        item = self.work_item(source=source)
        project = self.remember("project", source_refs=[
            {"kind": "work_source", "ref": source["source_id"], "revision": source["revision"]}])
        graph = self.search.graph(project["id"], max_depth=0)
        self.assertEqual([row["id"] for row in graph["nodes"]], [project["id"]])
        graph = self.search.graph(project["id"], max_depth=1)
        self.assertIn(item["id"], [row["id"] for row in graph["nodes"]])
        self.assertTrue(any(edge["relation"] == "shared_source" for edge in graph["edges"]))

    def test_read_helpers_preserve_callers_transaction(self):
        project = self.remember("project")
        self.conn.execute("BEGIN")
        self.search.explain(project["id"])
        self.assertTrue(self.conn.in_transaction)
        self.conn.rollback()

    def test_work_reference_cannot_read_another_account(self):
        item = self.work_item()
        with self.conn:
            self.conn.execute("UPDATE work_items SET account=? WHERE id=?",
                              ("other-fixture@example.com", item["id"]))
        packet = self.context(work_ids=[item["id"]])
        self.assertEqual(packet["entries"], [])
        self.assertTrue(any(gap["code"] == "work_reference_unavailable" for gap in packet["gaps"]))
        self.assertNotIn("Prepare fictional review", canonical_json(packet))

    def test_stale_or_sensitive_work_source_withholds_work_content(self):
        source = self.source()
        item = self.work_item(source=source)
        self.source("2")
        packet = self.context(work_ids=[item["id"]])
        self.assertEqual(packet["sections"]["open_work"], [])
        self.assertEqual(next(gap["current_source"]["revision"] for gap in packet["gaps"]
                              if gap["code"] == "source_changed"), "2")
        self.source("3", sensitivity="confidential")
        packet = self.context(work_ids=[item["id"]])
        self.assertTrue(any(gap["code"] == "source_withheld" for gap in packet["gaps"]))
        self.assertNotIn("Prepare fictional review", canonical_json(packet))

    def test_graph_depth_node_and_serialized_budget_are_bounded(self):
        records = [self.remember("node-" + str(index)) for index in range(8)]
        for left, right in zip(records, records[1:]):
            self.link(left, right, "depends_on")
        graph = self.search.graph(records[0]["id"], max_depth=1, max_nodes=2)
        self.assertEqual(len(graph["nodes"]), 2)
        self.assertTrue(graph["truncated"])
        graph = self.search.graph(records[0]["id"], max_depth=3, max_nodes=3, budget_chars=1500)
        self.assertLessEqual(len(graph["nodes"]), 3)
        self.assertEqual(graph["used_chars"], len(canonical_json(graph)))
        self.assertLessEqual(graph["used_chars"], 1500)
        for bad in (-1, 4, True):
            with self.assertRaises(StateError):
                self.search.graph(records[0]["id"], max_depth=bad)

    def test_context_complete_output_budget_and_mandatory_overflow(self):
        preference = self.remember("preference", kind="preference", text="Protect focus time.")
        for index in range(8):
            self.remember("node-" + str(index), entities=["project:one"], text="x" * 700)
        packet = self.context(entities=["project:one"], budget_chars=2200)
        self.assertEqual(packet["used_chars"], len(canonical_json(packet)))
        self.assertLessEqual(packet["used_chars"], 2200)
        self.assertIn(preference["id"], [row["id"] for row in packet["entries"]])
        self.assertTrue(packet["omitted"]["entries"])
        self.remember("large-constraint", kind="preference", text="x" * 3000)
        blocked = self.context(budget_chars=1000)
        self.assertEqual(blocked["status"], "blocked")
        self.assertTrue(blocked["requires_larger_budget"])
        self.assertLessEqual(len(canonical_json(blocked)), 1000)

    def test_stale_or_disputed_mandatory_preference_blocks_without_reciting_text(self):
        for state in ("stale", "disputed"):
            with self.subTest(state=state):
                preference = self.remember("private-" + state, kind="preference", status=state,
                                           title="Withheld private prohibition",
                                           text="Never include this private detail.")
                packet = self.context()
                self.assertEqual(packet["status"], "blocked")
                self.assertTrue(packet["requires_constraint_review"])
                self.assertTrue(any(gap.get("record_id") == preference["id"] for gap in packet["gaps"]))
                self.assertEqual(packet["entries"], [])
                self.assertNotIn("private prohibition", canonical_json(packet))
                self.assertNotIn("Never include", canonical_json(packet))

    def test_changed_source_cannot_silently_remove_mandatory_prohibition(self):
        source = self.source()
        preference = self.remember("restriction", kind="preference", source_refs=[
            {"kind": "work_source", "ref": source["source_id"], "revision": source["revision"]}],
            text="Private scheduling prohibition.")
        self.source("2")
        packet = self.context()
        self.assertEqual(packet["status"], "blocked")
        gap = next(gap for gap in packet["gaps"] if gap.get("record_id") == preference["id"])
        self.assertEqual(gap["code"], "mandatory_constraint_unavailable")
        self.assertNotIn("Private scheduling prohibition", canonical_json(packet))
        self.assertNotIn(source["source_id"], canonical_json(packet))

    def test_overdue_review_cannot_silently_drop_an_active_prohibition(self):
        self.remember("restriction", kind="preference", review_after="2020-01-01T00:00:00Z",
                      text="Withheld overdue constraint wording.")
        packet = self.context()
        self.assertEqual(packet["status"], "blocked")
        self.assertTrue(packet["requires_constraint_review"])
        self.assertTrue(any(gap.get("status") == "active" for gap in packet["gaps"]))
        self.assertNotIn("Withheld overdue", canonical_json(packet))

    def test_restricted_source_label_yields_safe_constraint_block_not_cached_permission(self):
        source = self.source(sensitivity="confidential")
        preference = self.remember("restriction", kind="preference", source_refs=[
            {"kind": "work_source", "ref": source["source_id"], "revision": source["revision"]}],
            text="Restricted cached constraint wording.")
        packet = self.context()
        self.assertEqual(packet["status"], "blocked")
        self.assertTrue(packet["requires_constraint_review"])
        self.assertNotIn("Restricted cached", canonical_json(packet))
        self.assertNotIn(source["source_id"], canonical_json(packet))
        self.assertEqual(self.search.graph(preference["id"])["nodes"], [])
        self.assertIn("no live source permission verification", packet["permission_checks"])

    def test_mandatory_conflicts_block_even_when_query_does_not_retrieve_constraint(self):
        preference = self.remember("restriction", kind="preference", text="Private accepted prohibition.")
        disputed = self.remember("disputed-restriction", status="disputed", text="Private contrary text.")
        self.link(preference, disputed, "contradicts")
        packet = self.context()
        self.assertEqual(packet["status"], "blocked")
        self.assertTrue(any(gap["code"] == "mandatory_constraint_conflict" for gap in packet["gaps"]))
        self.assertNotIn("Private accepted prohibition", canonical_json(packet))
        self.assertNotIn("Private contrary text", canonical_json(packet))

    def test_mandatory_conflict_metadata_is_not_echoed_into_safe_diagnostics(self):
        self.remember("restriction", kind="preference",
                      metadata={"conflicts": [{"private_excerpt": "Do not reveal this excerpt."}]})
        packet = self.context()
        self.assertEqual(packet["status"], "blocked")
        self.assertTrue(packet["requires_constraint_review"])
        self.assertNotIn("private_excerpt", canonical_json(packet))
        self.assertNotIn("Do not reveal", canonical_json(packet))

    def test_sensitive_mandatory_content_is_withheld_but_omission_is_explicit(self):
        preference = self.remember("restriction", kind="preference", sensitivity="sensitive",
                                   text="Sensitive explicitly volunteered preference.")
        packet = self.context()
        self.assertEqual(packet["status"], "blocked")
        self.assertTrue(any(gap.get("reason") == "sensitive_content_withheld" for gap in packet["gaps"]))
        self.assertNotIn("explicitly volunteered", canonical_json(packet))
        self.assertEqual(self.search.graph(preference["id"])["nodes"], [])

    def test_known_expiry_and_unrelated_routine_do_not_block_context(self):
        expired = self.remember("expired", kind="preference", valid_to="2020-01-01T00:00:00Z",
                                text="Expired private wording.")
        self.remember("drafting-restriction", kind="preference", status="stale", routines=["drafting"])
        packet = self.context(routine="calendar")
        self.assertEqual(packet["status"], "ready")
        self.assertTrue(any(gap["code"] == "mandatory_constraint_outside_validity"
                            and gap.get("record_id") == expired["id"] for gap in packet["gaps"]))
        self.assertNotIn("Expired private wording", canonical_json(packet))

    def test_suppressed_preference_is_not_reintroduced_by_constraint_guard(self):
        preference = self.remember("restriction", kind="preference")
        data = json.loads(self.conn.execute("SELECT data FROM memory_records WHERE id=?",
                                           (preference["id"],)).fetchone()[0])
        self.memory.revise(preference["id"], data, "suppressed", preference["revision"],
                           self.human(preference["id"], preference["revision"], "suppress"))
        packet = self.context()
        self.assertEqual(packet["status"], "ready")
        self.assertEqual(packet["entries"], [])
        self.assertEqual(packet["gaps"], [])

    def test_current_confirmed_replacement_satisfies_mandatory_constraint_guard(self):
        old = self.remember("old-restriction", kind="preference", text="Old private prohibition.")
        current = self.remember("current-restriction", kind="preference", text="Current accepted rule.")
        self.link(current, old, "supersedes")
        packet = self.context()
        self.assertEqual(packet["status"], "ready")
        self.assertEqual(packet["sections"]["constraints"], [current["id"]])
        self.assertNotIn("Old private prohibition", canonical_json(packet))

    def test_scoped_successor_does_not_hide_applicable_preference_elsewhere(self):
        original = self.remember("original-restriction", kind="preference")
        scoped = self.remember("scoped-restriction", kind="preference", routines=["drafting"],
                               metadata={"environment_requirements": {"host": "desktop"}})
        self.link(scoped, original, "supersedes")
        for routine, host in (("calendar", "desktop"), ("drafting", "other")):
            with self.subTest(routine=routine, host=host):
                packet = self.context(routine=routine, environment={"host": host})
                self.assertEqual(packet["status"], "ready")
                self.assertEqual(packet["sections"]["constraints"], [original["id"]])
        packet = self.context(routine="drafting", environment={"host": "desktop"})
        self.assertEqual(packet["sections"]["constraints"], [scoped["id"]])

    def test_constraint_review_block_remains_explicit_under_small_budget(self):
        for index in range(12):
            self.remember("restriction-" + str(index), kind="preference", status="stale")
        packet = self.context(budget_chars=1000)
        self.assertEqual(packet["status"], "blocked")
        self.assertTrue(packet["requires_constraint_review"])
        self.assertEqual(packet["unavailable_constraint_count"], 12)
        self.assertGreater(packet["omitted"]["gaps"], 0)
        self.assertEqual(packet["used_chars"], len(canonical_json(packet)))
        self.assertLessEqual(packet["used_chars"], 1000)

    def test_forgetting_invalidates_graph_and_fresh_instance(self):
        person = self.remember("person", kind="person", entities=["person:one"])
        project = self.remember("project")
        self.link(person, project, "owns")
        self.assertEqual(len(self.search.graph(person["id"])["nodes"]), 2)
        person = self.memory.show(person["id"])
        self.memory.forget(person["id"], person["revision"],
                           self.human(person["id"], person["revision"], "forget"))
        fresh = MemorySearch(self.memory)
        result = fresh.explain(person["id"])
        self.assertEqual(result["nodes"], [])
        self.assertNotIn("Synthetic person", canonical_json(result))
        self.assertEqual(self.context(entities=["person:one"])["entries"], [])

    def test_read_helpers_never_record_usage_or_queries(self):
        project = self.remember("project")
        before = self.conn.total_changes
        self.search.graph(project["id"])
        self.search.explain(project["id"])
        self.context(entities=[project["id"]])
        self.assertEqual(self.conn.total_changes, before)
        self.assertEqual(self.conn.execute("SELECT count(*) FROM memory_usage").fetchone()[0], 0)

    def test_fresh_process_reopens_synthetic_snapshot_and_reads_current_work(self):
        item = self.work_item()
        self.remember("project", entities=["project:one"], metadata={"work_refs": [item["id"]]})
        if self.search.fts:
            self.conn.execute("DROP TABLE semantic_fts")
        script = """
import json,sqlite3,sys
sys.path.insert(0, sys.argv[1])
from memory_store import MemoryStore
from memory_search import MemorySearch
memory = MemoryStore.__new__(MemoryStore)
memory.account = 'context-fixture@example.com'
memory.conn = sqlite3.connect(':memory:')
memory.conn.executescript(sys.stdin.read())
memory.conn.row_factory = sqlite3.Row
memory._initialize_schema()
packet = MemorySearch(memory).context('unmatched-query', entities=['project:one'], mode='lexical')
print(json.dumps(packet))
memory.close()
"""
        snapshot = "\n".join(statement for statement in self.conn.iterdump()
                             if not statement.startswith(('DELETE FROM "sqlite_sequence"',
                                                          'INSERT INTO "sqlite_sequence"')))
        result = subprocess.run([sys.executable, "-B", "-c", script, str(SCRIPTS)],
                                input=snapshot.encode(), capture_output=True)
        self.assertEqual(result.returncode, 0, result.stderr.decode())
        packet = json.loads(result.stdout)
        self.assertIn(item["id"], packet["sections"]["open_work"])
        self.assertTrue(any(entry["selection_reasons"] for entry in packet["entries"]))
        self.assertFalse(packet["context_is_consent"])


if __name__ == "__main__":
    unittest.main()

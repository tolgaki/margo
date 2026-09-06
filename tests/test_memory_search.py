import copy
from pathlib import Path
import sqlite3
import sys
import unittest

SCRIPTS = Path(__file__).resolve().parents[1] / "skills/chief-of-staff/scripts"
sys.path.insert(0, str(SCRIPTS))
from memory_search import MemorySearch
from memory_context import identity_key
from memory_store import MAX_MEMORY_TEXT_CHARS, search_text
from margo_store import StateError, canonical_json


class FakeMemory:
    """Unit-test lifecycle seam; real storage/embeddings have separate integration coverage."""
    def __init__(self, read_only=False):
        self.account = "synthetic-memory-search"
        self.conn = sqlite3.connect(":memory:")
        self.conn.row_factory = sqlite3.Row
        self.read_only = read_only
        self.records = {}
        self.jobs = {}
        self.blocked = {}

    def add(self, identity, text, kind="episode", routines=None):
        self.records[identity] = {
            "id": identity, "account": self.account, "revision": 1, "domain": "user",
            "kind": kind, "status": "active", "title": identity, "text": text,
            "authority": "user_confirmed", "scope": "personal", "source_refs": [],
            "routines": routines or [], "entities": [],
        }
        self.jobs[identity] = {"memory_id": identity, "revision": 1, "operation": "upsert"}
        self.blocked.pop(identity, None)
        return self.records[identity]

    def revise(self, identity, text):
        self.records[identity].update(revision=self.records[identity]["revision"] + 1, text=text)
        revision = self.records[identity]["revision"]
        self.jobs[identity] = {"memory_id": identity, "revision": revision, "operation": "upsert"}
        self.blocked.pop(identity, None)
        return self.records[identity]

    def list(self, domain=None, status=None):
        return [copy.deepcopy(row) for row in self.records.values()
                if (not domain or row["domain"] == domain) and (not status or row["status"] == status)]

    def eligible(self, domain=None, routine=None, **_kwargs):
        return [row for row in self.list(domain, "active")
                if not row["routines"] or routine in row["routines"]]

    def dream_index_current(self, record):
        return True

    def show(self, identity):
        return copy.deepcopy(self.records[identity])

    def links(self, identity):
        return []

    def pending_jobs(self, limit=100):
        return [job for job in self.jobs.values() if job["operation"] != "blocked"][:limit]

    def complete_job(self, identity, revision):
        if identity in self.jobs and self.jobs[identity]["revision"] == revision:
            del self.jobs[identity]
            return True
        return False

    def block_job(self, identity, revision, reason):
        record = self.records[identity]
        if (reason != "representation_exceeds_limit" or record["revision"] != revision
                or record["status"] != "active" or len(search_text(record)) <= MAX_MEMORY_TEXT_CHARS):
            return False
        self.jobs[identity] = {
            "memory_id": identity, "revision": revision, "operation": "blocked",
        }
        self.blocked[identity] = {
            "memory_id": identity, "revision": revision, "reason": reason,
        }
        return True

    def blocked_jobs(self, limit=100):
        return list(self.blocked.values())[:limit]


def unit_encoder(texts):
    vectors = [[1, 0, 0] if any(word in text.casefold() for word in ("review", "preparation"))
               else [0, 1, 0] for text in texts]
    return {"model_fingerprint": "unit-test-vectors-not-production", "dimensions": 3, "vectors": vectors}


class SearchTests(unittest.TestCase):
    def setUp(self):
        self.memory = FakeMemory()
        self.addCleanup(self.memory.conn.close)
        self.search = MemorySearch(self.memory, encoder=unit_encoder)

    def test_index_and_semantic_rank(self):
        self.memory.add("meeting", "Leave preparation time before decision reviews")
        self.memory.add("lunch", "Food and lunch choices")
        self.assertEqual(self.search.index()["indexed"], 2)
        result = self.search.search("avoid rushed review", mode="semantic")
        self.assertEqual(result["results"][0]["memory"]["id"], "meeting")
        self.assertEqual(result["results"][0]["matched_by"], ["semantic"])

    def test_forget_during_embedding_is_never_returned(self):
        self.memory.add("meeting", "Preparation time")
        self.search.index()
        def forget_then_encode(texts):
            self.memory.records["meeting"]["status"] = "forgotten"
            return unit_encoder(texts)
        self.search.encoder = forget_then_encode
        self.assertEqual(self.search.search("review")["results"], [])
        self.assertEqual(self.search.purge_unavailable(), 1)
        self.assertEqual(self.search.health()["vectors"], 0)
        self.assertEqual(self.memory.conn.execute("SELECT count(*) FROM semantic_fts").fetchone()[0], 0)

    def test_forgotten_seed_cannot_supply_an_unrelated_relationship_result(self):
        self.memory.add("meeting", "Review preparation")
        self.memory.add("other", "Independent food context")
        self.memory.links = lambda _: [{"source_id": "meeting", "target_id": "other",
                                       "relation": "related_to"}]
        def forget_then_encode(texts):
            self.memory.records["meeting"].update(status="forgotten", revision=2)
            return unit_encoder(texts)
        self.search.encoder = forget_then_encode
        self.assertEqual(self.search.search("review")["results"], [])

    def test_changed_revision_never_uses_old_vectors(self):
        self.memory.add("meeting", "Preparation time")
        self.search.index()
        self.memory.records["meeting"].update(revision=2, text="Food and lunch choices")
        result = self.search.search("review", mode="semantic")
        self.assertEqual(result["results"], [])
        self.assertTrue(result["warnings"])

    def test_stale_keyword_index_cannot_retrieve_removed_text(self):
        self.memory.add("record", "alpha special keyword")
        self.search.index()
        self.memory.records["record"].update(revision=2, text="entirely unrelated content")
        self.assertEqual(self.search.search("alpha", mode="lexical")["results"], [])

    def test_replayed_fts_content_is_rejected_and_rebuilt_with_current_vectors(self):
        self.memory.add("record", "uniqueobsolete")
        self.search.index()
        old_text = self.memory.conn.execute("SELECT content FROM semantic_fts").fetchone()[0]
        self.memory.records["record"].update(revision=2, text="beta current")
        self.memory.jobs["record"] = {"memory_id": "record", "revision": 2, "operation": "upsert"}
        self.search.index()
        with self.memory.conn:
            self.memory.conn.execute("UPDATE semantic_fts SET content=? WHERE memory_id='record'", (old_text,))
        self.assertEqual(self.search.search("uniqueobsolete", mode="lexical")["results"], [])
        self.assertEqual(self.search.index(rebuild=True)["indexed"], 1)
        indexed = self.memory.conn.execute("SELECT content FROM semantic_fts WHERE memory_id='record'").fetchone()[0]
        self.assertNotIn("uniqueobsolete", indexed)
        self.assertEqual(self.search.search("beta", mode="lexical")["results"][0]["memory"]["id"], "record")

    def test_rebuild_does_not_keep_scheduling_sensitive_records_ahead_of_valid_work(self):
        self.memory.add("sensitive", "Private sensitive content")["sensitivity"] = "sensitive"
        self.memory.add("ordinary", "Current preparation")
        first = self.search.index(limit=1, rebuild=True)
        self.assertEqual(first["deleted_jobs"], 1)
        self.assertEqual(first["indexed"], 0)
        self.assertEqual(first["remaining_rebuild"], 1)
        second = self.search.index(limit=1, rebuild=True)
        self.assertEqual(second["indexed"], 1)
        self.assertEqual(second["remaining_rebuild"], 0)

    def test_forgetting_purges_orphan_indexes_without_a_document_row(self):
        self.memory.add("record", "Context")
        self.search.index()
        with self.memory.conn:
            self.memory.conn.execute("DELETE FROM semantic_documents")
        self.memory.records["record"]["status"] = "forgotten"
        self.assertEqual(self.search.purge_unavailable(), 1)
        self.assertEqual(self.search.health()["vectors"], 0)
        self.assertEqual(self.memory.conn.execute("SELECT count(*) FROM semantic_fts").fetchone()[0], 0)

    def test_context_rechecks_records_after_search_and_eligibility(self):
        self.memory.add("record", "review preparation")
        original = self.memory.eligible
        count = 0
        def change_after_eligibility(**kwargs):
            nonlocal count
            count += 1
            result = original(**kwargs)
            if count == 3:
                self.memory.records["record"]["status"] = "forgotten"
            return result
        self.memory.eligible = change_after_eligibility
        self.assertEqual(self.search.context("review", mode="lexical")["entries"], [])

    def test_lexical_fallback_explicit_and_unindexed_memories_visible(self):
        self.memory.add("review", "Acronym XYZSPEC needs review")
        def no_encoder(_texts):
            raise AssertionError("lexical search must not request embeddings")
        self.search.encoder = no_encoder
        result = self.search.search('XYZSPEC " OR *', mode="lexical")
        self.assertEqual(result["results"][0]["memory"]["id"], "review")
        self.assertIsNone(result["model_fingerprint"])

    def test_model_versions_never_mix(self):
        self.memory.add("meeting", "Preparation time")
        self.search.index()
        def newer(texts):
            return dict(unit_encoder(texts), model_fingerprint="different-model")
        self.search.encoder = newer
        self.assertEqual(self.search.search("review", mode="semantic")["results"], [])
        self.search.index(rebuild=True)
        self.assertEqual(len(self.search.search("review", mode="semantic")["results"]), 1)

    def test_forgetting_during_index_cannot_resurrect_vectors(self):
        self.memory.add("meeting", "Preparation time")
        def forget_then_encode(texts):
            self.memory.records["meeting"].update(status="forgotten", revision=2)
            return unit_encoder(texts)
        self.search.encoder = forget_then_encode
        result = self.search.index()
        self.assertEqual(result["indexed"], 0)
        self.assertEqual(result["skipped_changed"], 1)
        self.assertEqual(self.search.health()["vectors"], 0)

    def test_exact_provider_identity_does_not_merge_same_labels(self):
        identity = {"provider": "directory", "scope": "one", "external_id": "person"}
        first = self.memory.add("first", "Person context", kind="person")
        first.update(identity=identity, title="Dana")
        second = self.memory.add("second", "Person context", kind="person")
        second.update(identity=dict(identity, scope="two"), title="Dana")
        packet = self.search.search("unmatched-query", mode="lexical", entities=[identity_key(identity)])
        self.assertEqual([row["memory"]["id"] for row in packet["results"]], ["first"])
        packet = self.search.search(identity_key(identity), mode="lexical")
        exact = [row["memory"]["id"] for row in packet["results"] if "exact" in row["matched_by"]]
        self.assertEqual(exact, ["first"])

    def test_explicit_entities_are_validated_and_canonicalized(self):
        local = self.memory.add("local-test-id", "Local context")
        self.assertEqual(self.search.search(
            "unmatched-query", mode="lexical", entities=[local["id"]]
        )["results"][0]["memory"]["id"], local["id"])

        identity = {"provider": "directory", "scope": "one", "external_id": "person"}
        person = self.memory.add("person", "Person context", kind="person")
        person["identity"] = identity
        noncanonical = 'identity: [ "directory", "one", "person" ]'
        packet = self.search.search("unmatched-query", mode="lexical", entities=[noncanonical])
        self.assertEqual(packet["results"][0]["memory"]["id"], "person")

        for malformed in ("Dana", "identity:not-json", "person:display name", ""):
            with self.subTest(malformed=malformed), self.assertRaisesRegex(StateError, "scoped"):
                self.search.search("unmatched-query", mode="lexical", entities=[malformed])

    def test_temporally_inapplicable_links_cannot_supply_relationship_hits(self):
        self.memory.add("meeting", "Review preparation")
        self.memory.add("unrelated", "Other context")
        self.memory.links = lambda _: [
            {"source_id": "meeting", "target_id": "unrelated", "relation": "owns",
             "data": {"valid_to": "2020-01-01T00:00:00Z"}}
        ]
        packet = self.search.search("review", mode="lexical")
        self.assertEqual([row["memory"]["id"] for row in packet["results"]], ["meeting"])

    def test_context_budget_includes_serialization_and_reasons(self):
        self.memory.add("constraint", "Protect focus.", kind="preference")
        self.memory.add("meeting", ('\\\n"' * 300))
        packet = self.search.context("meeting", mode="lexical", budget_chars=2200)
        self.assertEqual(packet["used_chars"], len(canonical_json(packet)))
        self.assertLessEqual(packet["used_chars"], 2200)
        self.assertEqual(packet["entries"][0]["id"], "constraint")
        self.assertIn("mandatory_constraint", packet["entries"][0]["selection_reasons"])
        self.assertFalse(packet["context_is_consent"])

    def test_mandatory_preferences_not_lost_to_similarity(self):
        self.memory.add("constraint", "Never schedule outside agreed working hours", kind="preference",
                        routines=["calendar"])
        self.memory.add("meeting", "Preparation time")
        packet = self.search.context("review", routine="calendar", mode="lexical")
        self.assertEqual(packet["entries"][0]["id"], "constraint")
        self.assertTrue(packet["entries"][0]["mandatory"])
        self.assertNotIn("constraint", [entry["id"] for entry in self.search.context(
            "review", routine="drafting", mode="lexical")["entries"]])

    def test_saved_baseline_profile_is_context_even_without_a_keyword_match(self):
        profile = self.memory.add("profile", "Working hours and protected time.", kind="profile")
        profile["authority"] = "source_observed"
        profile["metadata"] = {"imported_configuration": True, "source_heading": "About me"}
        packet = self.search.context("unrelated query", mode="lexical")
        self.assertEqual(packet["entries"][0]["id"], "profile")
        self.assertTrue(packet["entries"][0]["mandatory"])
        self.assertEqual(packet["entries"][0]["authority"], "source_observed")

    def test_mandatory_overflow_blocks_rather_than_silently_truncating(self):
        self.memory.add("constraint", "a" * 2000, kind="preference")
        self.assertEqual(self.search.context("other", mode="lexical", budget_chars=1000)["status"], "blocked")

    def test_invalid_vector_or_query_fails_explicitly(self):
        self.memory.add("meeting", "Preparation time")
        self.search.encoder = lambda _texts: {"model_fingerprint": "bad", "dimensions": 3,
                                             "vectors": [[float("nan"), 0, 1]]}
        with self.assertRaises(StateError):
            self.search.index()
        with self.assertRaises(StateError):
            self.search.search("")

    def test_read_only_constructor_leaves_missing_index_uninitialized(self):
        memory = FakeMemory(read_only=True)
        self.addCleanup(memory.conn.close)
        memory.add("record", "Indexless lexical context")
        search = MemorySearch(memory, encoder=lambda _: self.fail("Unexpected embedding request"))
        self.assertFalse(search.initialized)
        self.assertEqual(search.search("Indexless", mode="lexical")["results"][0]["memory"]["id"], "record")
        self.assertEqual(search.graph("record")["nodes"][0]["id"], "record")
        self.assertEqual(search.health()["status"], "not_initialized")
        self.assertEqual(search.health()["vectors"], 0)
        self.assertEqual(memory.conn.execute(
            "SELECT count(*) FROM sqlite_master WHERE name LIKE 'semantic_%'").fetchone()[0], 0)

    def test_read_only_constructor_rejects_incomplete_index_schema(self):
        memory = FakeMemory(read_only=True)
        self.addCleanup(memory.conn.close)
        memory.conn.execute("CREATE TABLE semantic_meta(key TEXT PRIMARY KEY,value TEXT NOT NULL)")
        with self.assertRaisesRegex(StateError, "incomplete"):
            MemorySearch(memory, encoder=unit_encoder)

    def test_existing_index_constructor_only_inspects_schema(self):
        before = self.memory.conn.total_changes
        inspected = MemorySearch(self.memory, encoder=unit_encoder)
        self.assertTrue(inspected.initialized)
        self.assertEqual(self.memory.conn.total_changes, before)

    def test_representation_limit_boundary_and_quarantine(self):
        exact = self.memory.add("exact", "x")
        exact["text"] = "x" * (MAX_MEMORY_TEXT_CHARS - (len(search_text(exact)) - 1))
        oversized = self.memory.add("oversized", "x")
        oversized["text"] = "x" * (MAX_MEMORY_TEXT_CHARS - (len(search_text(oversized)) - 1) + 1)
        self.assertEqual(len(search_text(exact)), MAX_MEMORY_TEXT_CHARS)
        self.assertEqual(len(search_text(oversized)), MAX_MEMORY_TEXT_CHARS + 1)
        result = self.search.index()
        self.assertEqual(result["indexed"], 1)
        self.assertEqual(result["status"], "partial")
        self.assertEqual(result["blocked_ids"], ["oversized"])
        self.assertEqual(result["remaining_jobs"], 0)

    def test_oversized_job_does_not_consume_page_or_starve_short_job(self):
        oversized = self.memory.add("oversized", "x")
        oversized["text"] = "x" * (MAX_MEMORY_TEXT_CHARS + 1)
        self.memory.add("short", "Current preparation")
        result = self.search.index(limit=1)
        self.assertEqual(result["indexed"], 1)
        self.assertEqual(result["newly_blocked_ids"], ["oversized"])
        self.assertEqual(result["blocked_ids"], ["oversized"])
        self.assertEqual(self.memory.pending_jobs(), [])

    def test_blocked_job_recovers_only_after_new_revision(self):
        record = self.memory.add("oversized", "x")
        record["text"] = "x" * (MAX_MEMORY_TEXT_CHARS + 1)
        first = self.search.index()
        self.assertEqual(first["blocked_ids"], ["oversized"])
        self.assertEqual(self.search.index()["newly_blocked_ids"], [])
        self.assertEqual(self.search.index(rebuild=True)["newly_blocked_ids"], [])
        self.memory.revise("oversized", "Current preparation")
        recovered = self.search.index()
        self.assertEqual(recovered["indexed"], 1)
        self.assertEqual(recovered["blocked_ids"], [])
        self.assertEqual(recovered["status"], "ready")

    def test_transient_encoder_failure_preserves_retryable_queue(self):
        self.memory.add("short", "Current preparation")
        self.search.encoder = lambda _texts: (_ for _ in ()).throw(StateError("runtime unavailable"))
        with self.assertRaisesRegex(StateError, "runtime unavailable"):
            self.search.index()
        self.assertEqual([job["memory_id"] for job in self.memory.pending_jobs()], ["short"])
        self.assertEqual(self.memory.blocked_jobs(), [])

    def test_rebuild_drains_delete_without_loading_encoder(self):
        record = self.memory.add("forgotten", "Old context")
        record["status"] = "forgotten"
        record["revision"] = 2
        self.memory.jobs["forgotten"] = {
            "memory_id": "forgotten", "revision": 2, "operation": "delete",
        }
        self.search.encoder = lambda _texts: self.fail("Delete-only rebuild must not encode")
        result = self.search.index(rebuild=True)
        self.assertEqual(result["deleted_jobs"], 1)
        self.assertEqual(result["indexed"], 0)
        self.assertEqual(result["remaining_jobs"], 0)
        self.assertEqual(result["remaining_rebuild"], 0)


if __name__ == "__main__":
    unittest.main()

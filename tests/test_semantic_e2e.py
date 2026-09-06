"""Opt-in real-model retrieval tests; fictional state only, never the configured user's ledger."""

import os
from pathlib import Path
import socket
import sys
import tempfile
import unittest
from unittest.mock import patch

SCRIPTS = Path(__file__).resolve().parents[1] / "skills/chief-of-staff/scripts"
sys.path.insert(0, str(SCRIPTS))


@unittest.skipUnless(os.environ.get("MARGO_RUN_EMBEDDING_INTEGRATION") == "1",
                     "requires explicit local model/runtime setup")
class SemanticEndToEndTests(unittest.TestCase):
    def test_real_semantic_memory_survives_restart_and_forgetting(self):
        from memory_store import MemoryStore
        from memory_search import MemorySearch
        from margo_store import utc_now
        import memory_encoder

        corpus = [
            "Reserve preparation time before meetings where important decisions are made.",
            "Dana owns release approvals and is the accountable launch decision maker.",
            "Protect an uninterrupted ninety minute block for focused individual work.",
            "A missing tool in one host is a connection binding problem, not proof that the whole service is unavailable.",
            "The office lunch menu includes pasta and salads.",
        ]
        queries = [("How can I avoid another rushed review?", 0), ("Who can sign off on the launch?", 1),
                   ("Help me concentrate without interruptions.", 2),
                   ("The API works in another session but not this one.", 3)]
        with tempfile.TemporaryDirectory() as directory, patch.dict(os.environ, {"MARGO_ALLOW_UNSAFE_STATE_DIR": "1"}):
            directory = str(Path(directory).resolve())
            store = MemoryStore(account="semantic-fixture@example.com", state_root=directory)
            try:
                ids = []
                for number, text in enumerate(corpus):
                    domain = "agent" if number == 3 else "user"
                    identity = store.record_id(domain, "fixture-%d" % number)
                    evidence = {"kind": "human_confirmation", "actor": "dana@example.com",
                                "subject_id": identity, "revision": 1, "decision": "confirm",
                                "statement": "I confirm this fictional fixture for the test.",
                                "evidence_ref": "conversation:synthetic-fixture", "decided_at": utc_now()}
                    record = store.put("fixture-%d" % number, {
                        "domain": domain, "kind": "lesson" if number == 3 else "profile",
                        "title": "Fixture %d" % number, "text": text, "authority": "user_confirmed",
                        "scope": "personal", "sensitivity": "private", "allowed_uses": ["reasoning"],
                        "entities": [], "routines": [],
                        "source_refs": [{"kind": "user_statement", "ref": "conversation:synthetic-fixture"}],
                    }, status="active", revision=1, evidence=evidence)
                    ids.append(record["id"])
                search = MemorySearch(store)
                with patch.object(socket, "create_connection", side_effect=AssertionError("network attempted")), \
                        patch.object(memory_encoder.urllib.request, "urlopen", side_effect=AssertionError("network attempted")):
                    self.assertEqual(search.index()["indexed"], len(corpus))
                    for query, expected in queries:
                        actual = search.search(query, mode="semantic")
                        self.assertEqual(actual["results"][0]["memory"]["id"], ids[expected])
                        self.assertFalse(actual["similarity_is_evidence"])
                store.close()
                store = MemoryStore(account="semantic-fixture@example.com", state_root=directory)
                search = MemorySearch(store)
                self.assertEqual(search.search(queries[0][0], mode="semantic")["results"][0]["memory"]["id"], ids[0])
                evidence = {"kind": "human_confirmation", "actor": "dana@example.com", "subject_id": ids[0],
                            "revision": 1, "decision": "forget", "statement": "Forget this fictional memory.",
                            "evidence_ref": "conversation:synthetic-forgetting", "decided_at": utc_now()}
                store.forget(ids[0], 1, evidence)
                search.purge_unavailable()
                self.assertNotIn(ids[0], [row["memory"]["id"] for row in search.search(queries[0][0])["results"]])
                self.assertEqual(store.conn.execute(
                    "SELECT count(*) FROM semantic_vectors WHERE memory_id=?", (ids[0],)).fetchone()[0], 0)
                other = MemoryStore(account="separate-fixture@example.com", state_root=directory)
                try:
                    self.assertEqual(other.list(), [])
                finally:
                    other.close()
            finally:
                store.close()


if __name__ == "__main__":
    unittest.main()

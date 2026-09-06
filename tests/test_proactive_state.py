import contextlib
import hashlib
import importlib.util
import io
import json
import os
import shutil
import sqlite3
import stat
import subprocess
import sys
import unittest
import uuid
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[1]
SCRIPTS = ROOT / "skills/chief-of-staff/scripts"
sys.path.insert(0, str(SCRIPTS))
import margo_store as store
import margo_doctor as doctor

SCRIPT = ROOT / "skills/chief-of-staff/scripts/proactive_state.py"
SPEC = importlib.util.spec_from_file_location("proactive_under_test", SCRIPT)
proactive = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(proactive)
START = "2026-01-01T00:00:00Z"
END = "2026-01-02T00:00:00Z"


class StateTests(unittest.TestCase):
    def setUp(self):
        self.root = ROOT / "tests" / (".proactive-test-" + uuid.uuid4().hex)
        self.root.mkdir(mode=0o700)
        self.environment = patch.dict(os.environ, {
            "MARGO_ALLOW_UNSAFE_STATE_DIR": "1",
            "MARGO_CONFIG": str(self.root / "missing-config.json"),
        })
        self.environment.start()
        self.conn = store.connect("tenant:user", self.root / "state")
        proactive.initialize(self.conn)

    def tearDown(self):
        self.conn.close()
        self.environment.stop()
        shutil.rmtree(self.root)

    def cli(self, *args, account="tenant:user"):
        return subprocess.run([sys.executable, str(SCRIPT), "--account", account,
                               "--state-dir", str(self.root / "state"), *args],
                              capture_output=True, text=True, check=False)

    def add(self, stable_id="one", revision="v1"):
        return proactive.queue_add(self.conn, {"id": stable_id, "revision": revision, "title": "Example"})

    def start(self, **overrides):
        data = {"family": "mail", "scope": {"folder": "inbox"}, "window": {"start": START, "end": END},
                "run_id": "run-1", "capability": "messages.list", "query_version": "1",
                "cadence_seconds": 3600}
        data.update(overrides)
        return proactive.coverage_start(self.conn, data)["attempt_id"]

    def page(self, attempt, final=True, page=1, observations=None, continuation=None):
        data = {"page": page, "observations": observations or [], "final": final}
        if continuation is not None:
            data["continuation"] = continuation
        return proactive.coverage_page(self.conn, attempt, data)

    def complete(self, attempt):
        return proactive.coverage_finish(self.conn, attempt, {
            "status": "complete", "checkpoint_at": END, "watermark": END, "delta_token": "opaque-secret-token"})

    def test_reobserved_timestamp_is_not_content_revision(self):
        first = self.start()
        self.page(first, observations=[{"id": "same", "revision": "v1", "title": "Unchanged",
                                       "observed_at": "2026-01-01T10:00:00Z"}])
        self.complete(first)
        second = self.start()
        self.page(second, observations=[{"id": "same", "revision": "v1", "title": "Unchanged",
                                        "observed_at": "2026-01-01T11:00:00Z"}])
        row = self.conn.execute("SELECT payload,observed_at FROM proactive_observations").fetchone()
        self.assertNotIn("observed_at", json.loads(row["payload"]))
        self.assertEqual(row["observed_at"], store.validate_timestamp("2026-01-01T11:00:00Z"))

    def test_query_change_does_not_reuse_delta_token_or_lose_checkpoint(self):
        first = self.start()
        self.page(first)
        self.complete(first)
        second = self.start(query_version="2")
        info = json.loads(self.cli("coverage-resume", "--attempt", second).stdout)
        self.assertTrue(info["resync_required"])
        self.assertIsNone(info["delta_token"])
        self.assertEqual(info["checkpoint_at"], store.validate_timestamp(END))
        self.page(second)
        self.complete(second)
        old = json.loads(self.cli("coverage-resume", "--attempt", first).stdout)
        self.assertTrue(old["resync_required"])
        self.assertIsNone(old["delta_token"])
        current = json.loads(self.cli("coverage-resume", "--attempt", second).stdout)
        self.assertFalse(current["resync_required"])
        self.assertEqual(current["delta_token"], "opaque-secret-token")

    def available(self, batch, ids=None, **overrides):
        data = {"content": "A durable brief", "ids": ids or ["one"],
                "status": "available", "receipt": {"kind": "local"}}
        data.update(overrides)
        return proactive.publication_record(self.conn, batch, data)["publication_id"]

    def test_store_identity_permissions_transactions_and_foreign_keys(self):
        self.assertEqual(self.conn.execute("PRAGMA foreign_keys").fetchone()[0], 1)
        self.assertIsInstance(self.conn.execute("SELECT 1").fetchone(), sqlite3.Row)
        self.conn.execute("CREATE TABLE test_transaction (value TEXT)")
        with self.assertRaises(RuntimeError):
            with self.conn:
                self.conn.execute("INSERT INTO test_transaction VALUES ('rollback')")
                raise RuntimeError("interrupted")
        self.assertEqual(self.conn.execute("SELECT count(*) FROM test_transaction").fetchone()[0], 0)
        _, path = store.state_path("tenant:user", self.root / "state")
        if os.name != "nt":
            self.assertEqual(stat.S_IMODE(path.stat().st_mode), 0o600)
            self.assertEqual(stat.S_IMODE(path.parent.stat().st_mode), 0o700)
        other = store.connect("other-tenant:user", self.root / "state")
        try:
            proactive.initialize(other)
            self.add()
            self.assertEqual(other.execute("SELECT count(*) FROM proactive_items").fetchone()[0], 0)
        finally:
            other.close()

    def test_account_config_and_unsafe_roots_fail_closed(self):
        with patch.dict(os.environ, {"MARGO_ACCOUNT": "", "MARGO_ALLOW_UNSAFE_STATE_DIR": "0"}):
            with self.assertRaises(store.StateError):
                store.connect(None, self.root / "bad")
            with self.assertRaises(store.StateError):
                store.connect("user", self.root / "bad")
        config = self.root / "config.json"
        config.write_text('{"account":"private-user"}', encoding="utf-8")
        config.chmod(0o600)
        env = dict(os.environ)
        env.pop("MARGO_ACCOUNT", None)
        env["MARGO_CONFIG"] = str(config)
        with patch.dict(os.environ, env, clear=True):
            self.assertEqual(store.resolve_account(), "private-user")
            if os.name != "nt":
                config.chmod(0o644)
                with self.assertRaises(store.StateError):
                    store.resolve_account()
        target = self.root / "linked"
        target.symlink_to(self.root / "state", target_is_directory=True)
        with self.assertRaises(store.StateError):
            store.connect("user", target)

    def test_no_account_status_is_setup_needed_and_writes_fail(self):
        env = dict(os.environ)
        env.pop("MARGO_ACCOUNT", None)
        with patch.dict(os.environ, env, clear=True):
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                result = proactive.main(["--state-dir", str(self.root / "unconfigured"), "status"])
            self.assertEqual(result, 0)
            self.assertEqual(json.loads(output.getvalue())["status"], "setup-needed")
            self.assertFalse((self.root / "unconfigured").exists())
            with contextlib.redirect_stderr(io.StringIO()):
                self.assertEqual(proactive.main(["queue-add", "--json", '{"id":"no-account"}']), 2)

    def test_timestamp_validation_and_canonical_json(self):
        self.assertEqual(store.validate_timestamp("2026-01-01T09:00:00+09:00"),
                         "2026-01-01T00:00:00.000000+00:00")
        for invalid in ("yesterday", "2026-01-01", "2026-01-01T00:00:00", "2026-13-01T00:00:00Z",
                        "2026-01-01T00:00:00+24:00", "2026-01-01T00:00:00+01:60", None, 17):
            with self.subTest(invalid=invalid), self.assertRaises(store.StateError):
                store.validate_timestamp(invalid)
        for invalid in (float("nan"), float("inf"), object()):
            with self.assertRaises(store.StateError):
                store.canonical_json({"value": invalid})
        for invalid in ('{"id":"one","id":"two"}', '{"number":NaN}', '{'):
            with self.assertRaises(store.StateError):
                proactive._input(invalid)
        result = self.cli("cursor-set", "sweep", "2026-01-01")
        self.assertEqual(result.returncode, 2)
        self.assertEqual(self.conn.execute("SELECT count(*) FROM proactive_legacy_cursors").fetchone()[0], 0)

    def test_malformed_databases_and_missing_tables_are_never_reset(self):
        _, path = store.state_path("damaged", self.root / "state")
        path.parent.mkdir(mode=0o700)
        path.write_bytes(b"not a database")
        path.chmod(0o600)
        with self.assertRaises(store.StateError):
            store.connect("damaged", self.root / "state")
        self.assertEqual(path.read_bytes(), b"not a database")
        self.conn.execute("DROP TABLE proactive_imports")
        with self.assertRaises(store.StateError):
            proactive.initialize(self.conn)
        self.assertIsNone(self.conn.execute(
            "SELECT name FROM sqlite_master WHERE name='proactive_imports'").fetchone())
        _, empty = store.state_path("empty-existing", self.root / "state")
        empty.parent.mkdir(mode=0o700)
        empty.touch(mode=0o600)
        with self.assertRaises(store.StateError):
            store.connect("empty-existing", self.root / "state")
        self.assertEqual(empty.stat().st_size, 0)

    def test_legacy_migration_replay_and_unconfirmed_hints(self):
        legacy = self.root / "legacy"
        legacy.mkdir()
        documents = {
            "queue.json": [{"id": "one", "title": "old"}],
            "inflight.json": [{"id": "one", "title": "old", "batch": "old-batch"},
                              {"id": "two", "title": "interrupted", "drained_at": END}],
            "surfaced.json": {"one": {"tier": "sweep", "ts": END}},
            "cursors.json": {"sweep": END},
        }
        for name, data in documents.items():
            (legacy / name).write_text(json.dumps(data), encoding="utf-8")
        before = {name: (legacy / name).read_bytes() for name in documents}
        first = proactive.import_legacy(self.conn, legacy)
        self.assertEqual(first["counts"]["queued"], 2)
        self.conn.close()
        self.conn = store.connect("tenant:user", self.root / "state")
        proactive.initialize(self.conn)
        self.assertTrue(proactive.import_legacy(self.conn, legacy)["replayed"])
        self.assertEqual(self.conn.execute("SELECT count(*) FROM proactive_items").fetchone()[0], 2)
        self.assertEqual(self.conn.execute("SELECT count(*) FROM proactive_sources").fetchone()[0], 0)
        self.assertEqual(self.conn.execute("SELECT count(*) FROM proactive_publications").fetchone()[0], 0)
        self.assertEqual(before, {name: (legacy / name).read_bytes() for name in documents})
        (legacy / "queue.json").write_text('[{"id":"three"}]', encoding="utf-8")
        (legacy / "cursors.json").write_text('{"sweep":"not-a-time"}', encoding="utf-8")
        with self.assertRaises(store.StateError):
            proactive.import_legacy(self.conn, legacy)
        self.assertEqual(self.conn.execute("SELECT count(*) FROM proactive_items").fetchone()[0], 2)
        for malformed in ('{', '{}', '[17]'):
            (legacy / "queue.json").write_text(malformed, encoding="utf-8")
            with self.assertRaises(store.StateError):
                proactive.import_legacy(self.conn, legacy)
        (legacy / "queue.json").write_text('[]', encoding="utf-8")
        (legacy / "cursors.json").write_text('{"x":"a","x":"b"}', encoding="utf-8")
        with self.assertRaises(store.StateError):
            proactive.import_legacy(self.conn, legacy)

    def test_revision_aware_dedupe_and_cli_seen(self):
        self.assertTrue(self.add()["added"])
        self.assertFalse(self.add()["added"])
        self.assertTrue(self.add(revision="v2")["added"])
        with self.assertRaises(store.StateError):
            proactive.queue_add(self.conn, {"id": "one", "revision": "v2", "title": "material change"})
        self.assertEqual(self.cli("seen", "one", "--revision", "v1").returncode, 1)
        batch = proactive.queue_drain(self.conn, limit=1)[0]
        receipt = proactive.publication_record(self.conn, batch["batch"], {
            "content": "exact version", "item_keys": [batch["item_key"]],
            "status": "available", "receipt": {"kind": "local"}})["publication_id"]
        proactive.queue_ack(self.conn, receipt, batch["batch"])
        self.assertEqual(self.cli("seen", "one", "--revision", batch["revision"]).returncode, 0)
        self.assertEqual(self.cli("seen", "one", "--revision", "v3").returncode, 1)
        self.assertEqual(self.cli("mark", "one").returncode, 2)

    def test_overlapping_anchors_cannot_steal_live_batches(self):
        for index in range(12):
            self.add(str(index))
        def drain(owner):
            connection = store.connect("tenant:user", self.root / "state")
            try:
                proactive.initialize(connection)
                return proactive.queue_drain(connection, owner, limit=6)
            finally:
                connection.close()
        with ThreadPoolExecutor(max_workers=2) as executor:
            batches = list(executor.map(drain, ("anchor-one", "anchor-two")))
        self.assertEqual([len(batch) for batch in batches], [6, 6])
        self.assertFalse({r["item_key"] for r in batches[0]} & {r["item_key"] for r in batches[1]})
        self.assertNotEqual(batches[0][0]["batch"], batches[1][0]["batch"])
        self.assertEqual(proactive.queue_drain(self.conn, "third"), [])
        with self.assertRaises(store.StateError):
            proactive.queue_release(self.conn, batches[0][0]["batch"], "wrong-owner")

    def test_interruption_release_expiry_and_stale_receipt(self):
        self.add()
        first = proactive.queue_drain(self.conn, "first")[0]
        receipt = self.available(first["batch"])
        with self.conn:
            self.conn.execute("UPDATE proactive_batches SET expires_at=?", (store.validate_timestamp(END),))
        second = proactive.queue_drain(self.conn, "second")[0]
        self.assertNotEqual(first["batch"], second["batch"])
        with self.assertRaises(store.StateError):
            proactive.queue_ack(self.conn, receipt, first["batch"])
        with self.assertRaises(store.StateError):
            proactive.queue_renew(self.conn, first["batch"], "first", 900)
        self.assertEqual(proactive.queue_release(self.conn, second["batch"], "second")["released"], 1)
        self.assertEqual(proactive.queue_release(self.conn, second["batch"], "second")["released"], 0)
        self.assertEqual(len(proactive.queue_drain(self.conn, "third")), 1)

    def test_receipt_required_actual_subset_and_review_separate(self):
        self.add()
        self.add("two")
        batch = proactive.queue_drain(self.conn)[0]["batch"]
        prepared = proactive.publication_record(self.conn, batch, {
            "id": "output-1", "content": "Only one item", "ids": ["one"], "status": "prepared"})["publication_id"]
        with self.assertRaises(store.StateError):
            proactive.queue_ack(self.conn, prepared, batch)
        with self.assertRaises(store.StateError):
            proactive.publication_review(self.conn, prepared)
        proactive.publication_publish(self.conn, prepared, {"status": "available", "receipt": {"kind": "local"}})
        with self.assertRaises(store.StateError):
            proactive.queue_ack(self.conn, prepared, batch, ids=["two"])
        self.assertEqual(proactive.queue_ack(self.conn, prepared, batch)["acked"], 1)
        self.assertEqual(proactive.queue_ack(self.conn, prepared, batch)["acked"], 0)
        status = proactive.status_report(self.conn)
        self.assertEqual(status["in_flight"], 1)
        self.assertEqual(status["publications"]["available"], 1)
        self.assertNotIn("reviewed", status["publications"])
        proactive.publication_review(self.conn, prepared)
        self.assertEqual(proactive.status_report(self.conn)["publications"]["reviewed"], 1)
        self.assertEqual(json.loads(self.cli("publication-show", prepared).stdout)["content"], "Only one item")
        listing = json.loads(self.cli("publication-list").stdout)
        self.assertEqual(listing[0]["publication_id"], prepared)
        self.assertEqual(listing[0]["status"], "reviewed")

    def test_publication_replay_immutable_and_host_receipts(self):
        self.add()
        batch = proactive.queue_drain(self.conn)[0]["batch"]
        data = {"id": "durable-output", "content": "sent", "ids": ["one"], "status": "published",
                "receipt": {"kind": "host", "receipt_id": "host-id", "location": "host://output/1",
                            "published_at": END}}
        proactive.publication_record(self.conn, batch, data)
        self.assertTrue(proactive.publication_record(self.conn, batch, data)["replayed"])
        for field, value in (("content", "different"), ("ids", ["not-included"])):
            with self.assertRaises(store.StateError):
                proactive.publication_record(self.conn, batch, {**data, field: value})
        with self.assertRaises(store.StateError):
            self.available(batch, status="published", receipt={"kind": "host", "receipt_id": "missing-location"})
        with self.assertRaises(store.StateError):
            self.available(batch, ids=["one", "one"])

    def test_partial_failure_blocked_checkpoint_and_recovery(self):
        successful = self.start()
        self.page(successful)
        self.complete(successful)
        initial = proactive.coverage_status(self.conn)[0]
        checkpoint = initial["checkpoint_at"]
        failed = self.start()
        proactive.coverage_finish(self.conn, failed, {
            "status": "failed", "error_class": "expired_token", "retry_after_seconds": 60})
        state = proactive.coverage_status(self.conn)[0]
        self.assertEqual(state["checkpoint_at"], checkpoint)
        self.assertEqual(state["last_successful_coverage_at"], initial["last_successful_coverage_at"])
        self.assertEqual(state["consecutive_failures"], 1)
        self.assertFalse(state["has_delta_token"])
        self.assertFalse(proactive.status_report(self.conn)["all_clear"])
        self.assertNotIn("opaque-secret-token", json.dumps(proactive.status_report(self.conn)))
        self.assertFalse(state["escalated"])
        with self.conn:
            self.conn.execute("UPDATE proactive_sources SET failure_started_at=?", (store.validate_timestamp(END),))
        blocked = self.start()
        proactive.coverage_finish(self.conn, blocked, {"status": "blocked", "error_class": "access_denied"})
        self.assertEqual(proactive.coverage_status(self.conn)[0]["consecutive_failures"], 2)
        recovery = self.start()
        self.page(recovery)
        self.complete(recovery)
        recovered = proactive.coverage_status(self.conn)[0]
        self.assertEqual(recovered["consecutive_failures"], 0)
        self.assertIsNotNone(recovered["recovered_at"])
        self.assertEqual(recovered["episode"], 1)
        self.assertEqual({r[0] for r in self.conn.execute("SELECT transition FROM proactive_health_events")},
                         {"degraded", "escalated", "recovered"})
        self.assertTrue(proactive.status_report(self.conn)["all_clear"])

    def test_partial_pages_are_atomic_and_cannot_claim_completion(self):
        attempt = self.start()
        self.page(attempt, final=False, observations=[{"id": "a", "revision": "1"}], continuation="opaque-next")
        with self.assertRaises(store.StateError):
            self.complete(attempt)
        self.assertIsNone(proactive.coverage_status(self.conn)[0]["checkpoint_at"])
        with self.assertRaises(store.StateError):
            self.page(attempt, page=3)
        self.assertTrue(self.page(attempt, final=False, observations=[{"id": "a", "revision": "1"}],
                                  continuation="opaque-next")["replayed"])
        with self.assertRaises(store.StateError):
            self.page(attempt, page=2, observations=[{"id": "b", "revision": "1"},
                                                    {"id": "a", "revision": "1", "changed": True}])
        self.assertEqual(self.conn.execute("SELECT count(*) FROM proactive_observations").fetchone()[0], 1)
        proactive.coverage_finish(self.conn, attempt, {"status": "partial"})
        resumed = self.start(resume_attempt=attempt, run_id="next-run")
        self.page(resumed, page=2, observations=[{"id": "a", "revision": "2"}])
        self.complete(resumed)
        state = proactive.coverage_status(self.conn)[0]
        self.assertEqual(state["pagination"]["pages"], 2)
        self.assertEqual(self.conn.execute("SELECT count(*) FROM proactive_observations").fetchone()[0], 2)

    def test_empty_search_is_not_absence_and_sources_are_independent(self):
        mail = self.start()
        calendar = self.start(family="calendar", scope="calendar-id")
        search = self.start(family="teams", scope="channel-id", kind="search")
        self.page(mail)
        self.complete(mail)
        self.page(search)
        with self.assertRaises(store.StateError):
            self.complete(search)
        proactive.coverage_finish(self.conn, search, {"status": "unknown"})
        proactive.coverage_finish(self.conn, calendar, {"status": "failed", "error_class": "RAW TOKEN secret@example"})
        sources = {source["family"]: source for source in proactive.coverage_status(self.conn)}
        self.assertIsNotNone(sources["mail"]["checkpoint_at"])
        self.assertIsNone(sources["calendar"]["checkpoint_at"])
        self.assertIsNone(sources["teams"]["checkpoint_at"])
        self.assertNotIn("RAW TOKEN", json.dumps(proactive.status_report(self.conn)))
        self.assertFalse(proactive.status_report(self.conn)["all_clear"])
        self.assertEqual(sources["calendar"]["error_class"], "unknown")

    def test_invalid_timestamp_completion_and_out_of_order_attempts(self):
        old = self.start()
        newer = self.start()
        self.page(newer)
        self.complete(newer)
        self.page(old)
        self.assertFalse(self.complete(old)["source_updated"])
        for data in ({"status": "complete", "checkpoint_at": "2026-01-02"},
                     {"status": "failed", "checkpoint_at": END},
                     {"status": "complete", "checkpoint_at": "2999-01-01T00:00:00Z"}):
            attempt = self.start()
            self.page(attempt)
            with self.assertRaises(store.StateError):
                proactive.coverage_finish(self.conn, attempt, data)
        self.assertEqual(proactive.coverage_status(self.conn)[0]["checkpoint_at"], store.validate_timestamp(END))

    def test_failed_write_rolls_back_without_unlocked_fallback(self):
        second = store.connect("tenant:user", self.root / "state")
        second.execute("PRAGMA busy_timeout=10")
        try:
            with self.conn:
                self.conn.execute("BEGIN IMMEDIATE")
                self.conn.execute("INSERT INTO proactive_legacy_cursors VALUES ('locked',?)", (END,))
                with self.assertRaises(sqlite3.OperationalError):
                    proactive.queue_add(second, {"id": "blocked-write"})
            self.assertEqual(second.execute("SELECT count(*) FROM proactive_items").fetchone()[0], 0)
        finally:
            second.close()

    def test_doctor_field_checks_ignore_template_title_and_report_drift(self):
        config = self.root / "preferences.md"
        config.write_text("# Preferences — {Your name}\n\n" + "\n".join(
            "- **{}:** real value".format(field) for field in doctor.REQUIRED_PREFERENCES), encoding="utf-8")
        result = doctor.configuration_fields(config, doctor.REQUIRED_PREFERENCES)
        self.assertEqual(result["status"], "complete")
        config.write_text(config.read_text().replace("Role / team:** real value", "Role / team:** {role}"))
        self.assertEqual(doctor.configuration_fields(config, doctor.REQUIRED_PREFERENCES)["fields"]["Role / team"],
                         "placeholder")
        snapshot = {"captured_at": END, "installation": [{
            "path": str(config), "sha256": hashlib.sha256(b"different").hexdigest()}],
            "workflows": [{"id": "anchor", "status": "failed", "expected_at": END, "last_started_at": START,
                           "error_class": "workspace_configuration", "output_available": False,
                           "expected_prompt_sha256": hashlib.sha256(b"expected").hexdigest(), "prompt": "stale"}]}
        report = doctor.inspect_snapshot(snapshot)
        self.assertEqual(report["status"], "degraded")
        self.assertEqual(report["installation"][0]["status"], "drift")
        self.assertEqual(report["workflows"][0]["prompt"], "drift")
        self.assertTrue(report["workflows"][0]["missed_expected_start"])
        self.assertFalse(report["workflows"][0]["source_coverage_implied"])
        self.assertEqual(doctor.inspect_snapshot({})["status"], "unknown")

    def test_doctor_snapshot_cli_and_corrupt_state(self):
        snapshot = self.root / "snapshot.json"
        snapshot.write_text('{"workflows":[{"id":"x","status":"completed"}]}')
        result = subprocess.run([sys.executable, str(SCRIPTS / "margo_doctor.py"),
                                 "--account", "tenant:user", "--state-dir", str(self.root / "state"),
                                 "--snapshot", str(snapshot), "--preferences", str(self.root / "missing"),
                                 "--install-root", str(self.root / "installation"),
                                 "--decision-config", str(self.root / "missing")],
                                capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        report = json.loads(result.stdout)
        self.assertEqual(report["status"], "attention-needed")
        self.assertEqual(report["managed_installation"]["status"], "unknown")
        self.assertFalse((self.root / "installation").exists())
        self.assertFalse(report["state"]["all_clear"])
        snapshot.write_text('{"workflows":[{"id":"x","expected_at":"tomorrow"}]}')
        with self.assertRaises(store.StateError):
            doctor.inspect_snapshot(store.read_json(snapshot))
        self.conn.execute("DROP TABLE proactive_imports")
        self.conn.commit()
        with self.assertRaises(store.StateError):
            doctor.state_health("tenant:user", self.root / "state")

    def test_cli_delivery_and_coverage_end_to_end(self):
        def invoke(*args):
            result = self.cli(*args)
            self.assertEqual(result.returncode, 0, result.stderr)
            return json.loads(result.stdout)
        invoke("queue-add", "--json", '{"id":"one","revision":"v1"}')
        batch = invoke("queue-drain", "--owner", "cli-anchor", "--format", "json")[0]["batch"]
        publication = invoke("publication-record", "--batch", batch, "--json", json.dumps({
            "content": "CLI brief", "ids": ["one"], "status": "available", "receipt": {"kind": "local"}}))
        result = invoke("queue-ack", "--batch", batch, "--receipt", publication["publication_id"])
        self.assertEqual(result["acked"], 1)
        attempt = invoke("coverage-start", "--json", json.dumps({
            "family": "mail", "scope": "inbox", "run_id": "cli",
            "capability": "messages.list", "query_version": "1", "cadence_seconds": 3600}))["attempt_id"]
        invoke("coverage-page", "--attempt", attempt, "--json", '{"page":1,"observations":[],"final":true}')
        invoke("coverage-finish", "--attempt", attempt, "--json",
               json.dumps({"status": "complete", "checkpoint_at": END}))
        self.assertTrue(invoke("status")["all_clear"])
        self.assertEqual(invoke("coverage-resume", "--attempt", attempt)["checkpoint_at"],
                         store.validate_timestamp(END))

    def test_rapid_retries_do_not_count_as_missed_cadences(self):
        for _ in range(3):
            attempt = self.start()
            proactive.coverage_finish(self.conn, attempt, {"status": "failed", "error_class": "timeout"})
        source = proactive.coverage_status(self.conn)[0]
        self.assertEqual(source["consecutive_failures"], 3)
        self.assertEqual(source["missed_cadences"], 1)
        self.assertFalse(source["escalated"])
        self.assertEqual(self.conn.execute("SELECT count(*) FROM proactive_health_events").fetchone()[0], 1)

    def test_stale_snapshot_and_missing_schema_version_fail_truthfully(self):
        config = self.root / "installed"
        config.write_text("content")
        report = doctor.inspect_snapshot({
            "captured_at": END,
            "installation": [{"path": str(config), "sha256": hashlib.sha256(b"content").hexdigest()}],
            "workflows": [{"id": "x", "status": "completed", "expected_at": END, "last_started_at": END,
                           "prompt": "p", "expected_prompt_sha256": hashlib.sha256(b"p").hexdigest(),
                           "output_available": True}]})
        self.assertFalse(report["fresh"])
        self.assertEqual(report["status"], "unknown")
        with self.conn:
            self.conn.execute("DELETE FROM proactive_meta")
        with self.assertRaises(store.StateError):
            proactive.initialize(self.conn)
        self.assertEqual(self.conn.execute("SELECT count(*) FROM proactive_meta").fetchone()[0], 0)

    def test_scope_window_keys_and_malformed_payload(self):
        self.start()
        self.start(scope={"folder": "archive"})
        self.start(window={"start": "2026-01-02T00:00:00Z", "end": "2026-01-03T00:00:00Z"})
        self.assertEqual(self.conn.execute("SELECT count(*) FROM proactive_sources").fetchone()[0], 2)
        self.add()
        with self.conn:
            self.conn.execute("UPDATE proactive_items SET payload='{broken'")
        result = self.cli("queue-list", "--format", "json")
        self.assertEqual(result.returncode, 2)
        self.assertNotIn("Traceback", result.stderr)
        self.assertIn("no reset", result.stderr)
        self.assertEqual(self.conn.execute("SELECT payload FROM proactive_items").fetchone()[0], "{broken")

    def test_polling_windows_share_checkpoint_and_failure_episode(self):
        first = self.start()
        self.page(first)
        self.complete(first)
        second_end = "2026-01-03T00:00:00Z"
        second = self.start(window={"start": END, "end": second_end})
        self.assertEqual(proactive._attempt(self.conn, first)["source_key"],
                         proactive._attempt(self.conn, second)["source_key"])
        prior = json.loads(self.cli("coverage-resume", "--attempt", second).stdout)
        self.assertEqual(prior["checkpoint_at"], store.validate_timestamp(END))
        self.page(second)
        proactive.coverage_finish(self.conn, second, {"status": "complete", "checkpoint_at": second_end})
        self.assertTrue(proactive.status_report(self.conn)["all_clear"])
        for start, end in [(second_end, "2026-01-04T00:00:00Z"),
                           ("2026-01-04T00:00:00Z", "2026-01-05T00:00:00Z")]:
            attempt = self.start(window={"start": start, "end": end})
            proactive.coverage_finish(self.conn, attempt, {"status": "failed", "error_class": "timeout"})
        source = proactive.coverage_status(self.conn)[0]
        self.assertEqual(source["consecutive_failures"], 2)
        self.assertEqual(source["checkpoint_at"], store.validate_timestamp(second_end))

    def test_fixed_provider_windows_and_retirement(self):
        window = {"start": START, "end": END}
        first = self.start(family="calendar", collection_window=window)
        self.page(first)
        self.complete(first)
        other = {"start": END, "end": "2026-01-03T00:00:00Z"}
        second = self.start(family="calendar", window=other, collection_window=other)
        self.page(second)
        proactive.coverage_finish(self.conn, second, {"status": "complete", "checkpoint_at": other["end"]})
        first_key = proactive._attempt(self.conn, first)["source_key"]
        self.assertNotEqual(first_key, proactive._attempt(self.conn, second)["source_key"])
        proactive.coverage_retire(self.conn, first_key, "Old calendar horizon")
        with self.conn:
            self.conn.execute("UPDATE proactive_sources SET last_successful_coverage_at=? WHERE source_key=?",
                              (store.validate_timestamp(START), first_key))
        self.assertTrue(proactive.status_report(self.conn)["all_clear"])
        with self.assertRaises(store.StateError):
            self.start(family="calendar", collection_window=window)
        self.start(family="calendar", collection_window=window, reactivate=True)
        with self.assertRaises(store.StateError):
            self.start(family="calendar", window=other, collection_window=window)

    def test_continuation_cannot_move_to_a_new_polling_interval(self):
        first = self.start()
        self.page(first, final=False, continuation="opaque-next")
        proactive.coverage_finish(self.conn, first, {"status": "partial"})
        with self.assertRaises(store.StateError):
            self.start(window={"start": END, "end": "2026-01-03T00:00:00Z"}, resume_attempt=first)
        resumed = self.start(resume_attempt=first)
        self.assertEqual(proactive._attempt(self.conn, resumed)["pages"], 1)

    def test_legacy_window_identity_is_retired_without_losing_evidence(self):
        first = self.start(collection_window={"start": START, "end": END})
        self.page(first)
        self.complete(first)
        with self.conn:
            self.conn.execute("DELETE FROM proactive_meta WHERE key='source_identity_version'")
        proactive.initialize(self.conn)
        source = proactive.coverage_status(self.conn)[0]
        self.assertTrue(source["retired"])
        self.assertEqual(source["checkpoint_at"], store.validate_timestamp(END))
        self.assertFalse(proactive.status_report(self.conn)["all_clear"])

    def test_standalone_output_lifecycle_needs_no_dummy_items_or_ack(self):
        self.assertEqual(proactive.queue_drain(self.conn, "empty-anchor"), [])
        data = {"id": "standalone-1", "content": "A brief with no queued inputs", "status": "prepared"}
        created = proactive.publication_record(self.conn, None, data)
        self.assertTrue(created["standalone"])
        self.assertTrue(proactive.publication_record(self.conn, None, data)["replayed"])
        self.assertEqual(self.conn.execute("SELECT count(*) FROM proactive_items").fetchone()[0], 0)
        self.assertEqual(proactive.status_report(self.conn)["unpublished_outputs"], 1)
        proactive.publication_publish(self.conn, created["publication_id"],
                                      {"status": "available", "receipt": {"kind": "local"}})
        listing = proactive.publication_list(self.conn)
        self.assertEqual(listing[0]["included_items"], 0)
        self.assertEqual(listing[0]["ack_pending"], 0)
        self.assertEqual(proactive.status_report(self.conn)["unpublished_outputs"], 0)
        self.assertTrue(json.loads(self.cli("publication-show", "standalone-1").stdout)["standalone"])
        with self.assertRaises(store.StateError):
            proactive.queue_ack(self.conn, "standalone-1", batch_id=listing[0]["batch_id"])
        with self.assertRaises(store.StateError):
            proactive.publication_record(self.conn, None, dict(data, content="Changed"))
        with self.assertRaises(store.StateError):
            proactive.publication_record(self.conn, None, dict(data, id="other", ids=["one"]))
        result = self.cli("publication-record", "--json",
                          json.dumps({"content": "Another standalone output", "status": "available",
                                      "receipt": {"kind": "local"}}))
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_private_config_init_cli_replay_and_owner_isolation(self):
        config = self.root / "private-config" / "config.json"
        principal = "synthetic-owner@example.com"
        command = [sys.executable, str(SCRIPTS / "margo_store.py"),
                   "init", "--account", principal, "--config", str(config)]
        result = subprocess.run(command, capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertTrue(json.loads(result.stdout)["created"])
        self.assertNotIn(principal, result.stdout)
        self.assertEqual(store.read_json(config), {"account": principal})
        if os.name != "nt":
            self.assertEqual(stat.S_IMODE(config.stat().st_mode), 0o600)
            self.assertEqual(stat.S_IMODE(config.parent.stat().st_mode), 0o700)
        self.assertEqual(list(config.parent.iterdir()), [config])
        replay = subprocess.run(command, capture_output=True, text=True)
        self.assertEqual(replay.returncode, 0, replay.stderr)
        self.assertFalse(json.loads(replay.stdout)["created"])
        original = config.read_bytes()
        with self.assertRaises(store.StateError):
            store.initialize_config("different-owner@example.com", config)
        self.assertEqual(config.read_bytes(), original)
        environment = dict(os.environ)
        environment.pop("MARGO_ACCOUNT", None)
        environment["MARGO_CONFIG"] = str(config)
        with patch.dict(os.environ, environment, clear=True):
            self.assertEqual(store.resolve_account(), principal)
        config.write_text("{broken", encoding="utf-8")
        with self.assertRaises(store.StateError):
            store.initialize_config(principal, config)
        self.assertEqual(config.read_text(), "{broken")

    def test_config_init_unsafe_roots_and_interrupted_publication(self):
        config = self.root / "private-config" / "config.json"
        with patch.dict(os.environ, {"MARGO_ALLOW_UNSAFE_STATE_DIR": "0"}):
            with self.assertRaises(store.StateError):
                store.initialize_config("synthetic-owner", config)
        self.assertFalse(config.parent.exists())
        with patch.object(store.os, "link", side_effect=OSError("simulated interruption")):
            with self.assertRaises(store.StateError):
                store.initialize_config("synthetic-owner", config)
        self.assertFalse(config.exists())
        self.assertEqual(list(config.parent.iterdir()), [])
        store.initialize_config("synthetic-owner", config)
        with self.assertRaises(store.StateError):
            store.initialize_config(None, config)

    def test_authentication_failure_waits_for_explicit_reauthentication(self):
        successful = self.start()
        self.page(successful)
        self.complete(successful)
        blocked = self.start()
        proactive.coverage_finish(self.conn, blocked, {
            "status": "blocked", "error_class": "authentication_required"})
        source = proactive.coverage_status(self.conn)[0]
        self.assertFalse(source["retry_eligible"])
        self.assertTrue(source["retry_requires_reauthentication"])
        self.assertEqual(source["checkpoint_at"], store.validate_timestamp(END))
        with self.assertRaises(store.StateError):
            self.start()
        self.assertEqual(self.conn.execute("SELECT count(*) FROM proactive_attempts").fetchone()[0], 2)
        recovered = self.start(reauthenticated=True)
        self.page(recovered)
        self.complete(recovered)
        source = proactive.coverage_status(self.conn)[0]
        self.assertFalse(source["retry_requires_reauthentication"])
        self.assertTrue(source["retry_eligible"])
        snapshot = doctor.inspect_snapshot({"workflows": [{
            "id": "read-only", "status": "failed", "error_class": "authentication_required"}]})
        self.assertTrue(snapshot["workflows"][0]["retry_requires_reauthentication"])

    def test_complete_can_clear_obsolete_delta_token(self):
        attempt = self.start()
        self.page(attempt)
        self.complete(attempt)
        self.assertTrue(proactive.coverage_status(self.conn)[0]["has_delta_token"])
        next_attempt = self.start()
        self.page(next_attempt)
        proactive.coverage_finish(self.conn, next_attempt, {
            "status": "complete", "checkpoint_at": END, "delta_token": None})
        source = proactive.coverage_status(self.conn)[0]
        self.assertFalse(source["has_delta_token"])
        self.assertEqual(source["checkpoint_at"], store.validate_timestamp(END))

    def test_managed_installation_manifest_missing_matching_and_drift(self):
        install = self.root / "installation"
        self.assertEqual(doctor.inspect_installation_manifest(install)["status"], "unknown")
        self.assertFalse(install.exists())
        managed = install / "scripts" / "managed.py"
        managed.parent.mkdir(parents=True)
        managed.write_text("expected source", encoding="utf-8")
        expected = hashlib.sha256(managed.read_bytes()).hexdigest()
        manifest = install / ".margo-files.json"
        manifest.write_text(json.dumps({"scripts/managed.py": expected}), encoding="utf-8")
        before = manifest.read_bytes()
        result = doctor.inspect_installation_manifest(install)
        self.assertEqual(result["status"], "healthy")
        self.assertEqual(result["matching_files"], 1)
        managed.write_text("local modification", encoding="utf-8")
        result = doctor.inspect_installation_manifest(install)
        self.assertEqual(result["status"], "degraded")
        self.assertEqual(result["issues"][0]["status"], "drift")
        managed.unlink()
        self.assertEqual(doctor.inspect_installation_manifest(install)["issues"][0]["status"], "missing")
        self.assertEqual(manifest.read_bytes(), before)

    def test_managed_manifest_rejects_escapes_bad_hashes_and_malformed_json(self):
        install = self.root / "installation"
        install.mkdir()
        manifest = install / ".margo-files.json"
        for data in ([], {"../outside": "a" * 64}, {"/absolute": "a" * 64},
                     {"safe": "not-a-hash"}, {"C:\\outside": "a" * 64}):
            manifest.write_text(json.dumps(data), encoding="utf-8")
            with self.subTest(data=data), self.assertRaises(store.StateError):
                doctor.inspect_installation_manifest(install)
        manifest.write_text("{broken", encoding="utf-8")
        with self.assertRaises(store.StateError):
            doctor.inspect_installation_manifest(install)
        outside = self.root / "outside"
        outside.write_text("outside installation", encoding="utf-8")
        (install / "link").symlink_to(outside)
        manifest.write_text(json.dumps({"link": hashlib.sha256(outside.read_bytes()).hexdigest()}))
        with self.assertRaises(store.StateError):
            doctor.inspect_installation_manifest(install)
        manifest.write_text("{}", encoding="utf-8")
        self.assertEqual(doctor.inspect_installation_manifest(install)["status"], "unknown")

    def test_local_manifest_can_supply_snapshot_installation_evidence(self):
        now = store.utc_now()
        snapshot = {"captured_at": now, "workflows": [{
            "id": "anchor", "status": "completed", "expected_at": END, "last_started_at": END,
            "prompt": "current", "expected_prompt_sha256": hashlib.sha256(b"current").hexdigest(),
            "output_available": True}]}
        self.assertEqual(doctor.inspect_snapshot(snapshot)["status"], "unknown")
        self.assertEqual(doctor.inspect_snapshot(snapshot, installation_status="healthy")["status"], "healthy")
        self.assertEqual(doctor.inspect_snapshot(snapshot, installation_status="degraded")["status"], "degraded")

    def test_missing_host_binding_does_not_assert_authentication_outage(self):
        failed = self.start()
        proactive.coverage_finish(self.conn, failed, {
            "status": "failed", "error_class": "binding_unavailable"})
        source = proactive.coverage_status(self.conn)[0]
        self.assertFalse(source["retry_requires_reauthentication"])
        self.assertTrue(source["retry_eligible"])
        self.assertIsNone(source["checkpoint_at"])
        working_binding = self.start(capability="verified-session.read")
        self.page(working_binding, final=False)
        proactive.coverage_finish(self.conn, working_binding, {"status": "partial"})
        self.assertFalse(proactive.status_report(self.conn)["all_clear"])
        self.assertIsNone(proactive.coverage_status(self.conn)[0]["checkpoint_at"])
        snapshot = doctor.inspect_snapshot({"workflows": [{
            "id": "host-binding", "status": "failed", "error_class": "binding_unavailable"}]})
        self.assertFalse(snapshot["workflows"][0]["retry_requires_reauthentication"])


if __name__ == "__main__":
    unittest.main()

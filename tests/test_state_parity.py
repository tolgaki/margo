"""Self-test of tools/state_parity.py's normalisation and snapshot logic (no C# build needed)."""

import importlib.util
import json
import os
import sqlite3
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("state_parity_under_test", ROOT / "tools" / "state_parity.py")
parity = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(parity)


class TokenTests(unittest.TestCase):
    def test_token_kinds(self):
        # A synthetic UUID assembled at run time (a literal would look like a tenant id to check-clean.sh).
        synthetic_uuid = "-".join(("0" * 8, "1" * 4, "4" + "2" * 3, "8" + "3" * 3, "4" * 12))
        text = ('at 2026-01-01T00:00:00.123456+00:00 id ' + synthetic_uuid + ' '
                'act_0123456789abcdef0123456789abcdef epoch 1762526400.25 short beef1 word xdeadbeefcafe')
        self.assertEqual(parity.tokens(text), [
            "2026-01-01T00:00:00.123456+00:00", synthetic_uuid,
            "0123456789abcdef0123456789abcdef", "1762526400.25"])

    def test_shape_masks_digits_and_hex_letters_only(self):
        self.assertEqual(parity.shape("2026-01-01T00:00:00Z"), "9999-99-99T99:99:99Z")
        self.assertEqual(parity.shape("ab12-CD"), "9999-99")

    def test_volatile_positions_are_the_tokens_that_differ(self):
        first = 'id aaaaaaaa11 hash 0123456789abcdef at 2026-01-01T00:00:00Z'
        second = 'id bbbbbbbb22 hash 0123456789abcdef at 2026-01-01T00:00:00Z'
        self.assertEqual(parity.volatile_positions(first, second), {0})

    def test_unaligned_runs_are_reported(self):
        self.assertIsNone(parity.volatile_positions("a 0123456789", "a 0123456789 0123456789"))
        self.assertIsNone(parity.volatile_positions("x 0123456789", "y 0123456789"))

    def test_placeholders_are_numbered_by_first_occurrence_and_reused(self):
        normaliser = parity.Normaliser()
        text = "a 1111111111 b 2222222222 c 1111111111"
        self.assertEqual(normaliser.apply(text, {0, 1, 2}),
                         "a <V1:9999999999> b <V2:9999999999> c <V1:9999999999>")

    def test_deterministic_tokens_stay_literal(self):
        normaliser = parity.Normaliser()
        self.assertEqual(normaliser.apply("x 1111111111 y 2222222222", {1}), "x 1111111111 y <V1:9999999999>")
        self.assertEqual(parity.Normaliser().apply("x 1111111111", True), "x <V1:9999999999>")

    def test_wrong_reuse_of_an_identifier_is_visible(self):
        python = parity.Normaliser().apply("new 1111111111 then 2222222222", {0, 1})
        csharp = parity.Normaliser().apply("new 3333333333 then 3333333333", {0, 1})
        self.assertNotEqual(python, csharp)

    def test_format_change_of_a_volatile_value_is_visible(self):
        python = parity.Normaliser().apply("at 2026-01-01T00:00:00.123456+00:00", True)
        csharp = parity.Normaliser().apply("at 2026-01-01T00:00:00+00:00", True)
        self.assertNotEqual(python, csharp)

    def test_root_replacement_prefers_longest(self):
        self.assertEqual(parity.replace_roots("/tmp/x/y and /tmp/x", {"/tmp/x", "/tmp/x/y"}), "<ROOT> and <ROOT>")


class ReasonTests(unittest.TestCase):
    """The stderr line the hosted margo-state tool reports must match, not only stderr's emptiness."""

    def test_last_non_empty_line_is_the_reported_error(self):
        self.assertEqual(parity.reason(""), "")
        self.assertEqual(parity.reason("ERROR: account must be explicit\n\n"), "ERROR: account must be explicit")
        usage = "usage: x.py [-h]\nx.py: error: the following arguments are required: --account\n"
        self.assertEqual(parity.reason(usage), "x.py: error: the following arguments are required: --account")

    def test_uncaught_exceptions_compare_by_class_only(self):
        python = ('Traceback (most recent call last):\n  File "x.py", line 1, in <module>\n'
                  "sqlite3.IntegrityError: UNIQUE constraint failed: t.c\n")
        csharp = "Traceback (most recent call last):\n  (C# port)\nIntegrityError: UNIQUE constraint failed: t.c\n"
        self.assertEqual(parity.reason(python), "uncaught IntegrityError")
        self.assertEqual(parity.reason(python), parity.reason(csharp))
        self.assertEqual(parity.reason("Traceback (most recent call last):\n  ...\nKeyError\n"), "uncaught KeyError")

    def test_a_different_message_is_a_difference(self):
        self.assertNotEqual(
            parity.reason("ERROR: repository/shared/synchronised state/config roots are forbidden\n"),
            parity.reason("ERROR: state/config root has a group/world-writable ancestor\n"))


class SnapshotTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)

    def tearDown(self):
        self.tmp.cleanup()

    def test_database_dump_has_schema_version_and_ordered_rows(self):
        path = self.root / "state" / "db.sqlite3"
        path.parent.mkdir()
        connection = sqlite3.connect(path)
        connection.executescript("""
            CREATE TABLE b (id INTEGER PRIMARY KEY, label TEXT, data BLOB, score REAL);
            CREATE TABLE a (k TEXT PRIMARY KEY, v TEXT) WITHOUT ROWID;
            CREATE INDEX b_label ON b(label);
            PRAGMA user_version=7;
            INSERT INTO b(label, data, score) VALUES ('two', x'00ff', 1.5), (NULL, NULL, 2.0);
            INSERT INTO a VALUES ('z', 'last'), ('m', 'first');
        """)
        connection.commit()
        connection.close()
        files, databases = parity.snapshot(self.root)
        self.assertIn("user_version=7", databases)
        self.assertIn('schema index b_label on b: CREATE INDEX b_label ON b(label)', databases)
        self.assertIn('  1 | 1 | "two" | x\'00ff\' | 1.5', databases)
        self.assertIn("  2 | 2 | NULL | NULL | 2.0", databases)
        self.assertLess(databases.index('  "m" | "first"'), databases.index('  "z" | "last"'))
        self.assertIn("db   state/db.sqlite3 %04o" % (os.stat(path).st_mode & 0o7777), files)

    def test_file_tree_has_modes_text_contents_and_ignores_home_caches(self):
        (self.root / "home" / ".dotnet").mkdir(parents=True)
        (self.root / "home" / ".dotnet" / "cache").write_text("x")
        (self.root / "home" / ".copilot").mkdir()
        config = self.root / "home" / ".copilot" / "config.json"
        config.write_text('{"account":"owner@example.com"}\n')
        config.chmod(0o600)
        (self.root / "blob.bin").write_bytes(b"\xff\xfe")
        files, _ = parity.snapshot(self.root)
        text = parity.text_of_files(files, {str(self.root)})
        self.assertNotIn(".dotnet", text)
        self.assertIn("file home/.copilot/config.json 0600 text 1 lines", text)
        self.assertIn('  | {"account":"owner@example.com"}', text)
        self.assertIn("binary sha256:", text)


class ScenarioSchemaTests(unittest.TestCase):
    def write(self, data):
        handle = tempfile.NamedTemporaryFile("w", suffix=".json", delete=False)
        with handle:
            json.dump(data, handle)
        self.addCleanup(os.unlink, handle.name)
        return handle.name

    def test_valid_scenario_loads(self):
        data = {"description": "d", "steps": [{"script": "margo_store.py", "args": ["--help"], "expect_exit": 0}]}
        self.assertEqual(parity.load_scenario(self.write(data))["steps"][0]["script"], "margo_store.py")

    def test_invalid_scenarios_are_rejected(self):
        for data in ({"steps": []}, {"description": "d", "steps": []},
                     {"description": "d", "steps": [{"script": "x.py", "args": [1]}]},
                     {"description": "d", "steps": [{"script": "x.py", "expect_exit": "0"}]},
                     {"description": "d", "steps": [{"script": "x.py"}], "unknown": 1}):
            with self.subTest(data=data), self.assertRaises(parity.ScenarioError):
                parity.load_scenario(self.write(data))

    def test_committed_scenarios_follow_the_schema(self):
        scenarios = sorted(parity.SCENARIOS.rglob("*.json"))
        self.assertTrue(scenarios)
        for path in scenarios:
            with self.subTest(path=path.name):
                scenario = parity.load_scenario(path)
                for step in scenario["steps"]:
                    parity.python_script(step["script"])

    def test_contract_probes_are_enabled_for_every_step(self):
        # margo-state refuses its contract probes unless MARGO_STATE_PROBES=1; the reference scripts ignore it.
        env = parity.base_environment(Path("/r"))
        self.assertEqual(env["MARGO_STATE_PROBES"], "1")
        self.assertEqual(env["HOME"], "/r/home")

    def test_readme_lists_every_scenario_folder_and_probe(self):
        readme = (parity.SCENARIOS / "README.md").read_text(encoding="utf-8")
        folders = sorted({path.parent.name for path in parity.SCENARIOS.rglob("*.json")})
        self.assertTrue(folders)
        for folder in folders:
            with self.subTest(folder=folder):
                self.assertIn("`%s/`" % folder, readme)
        probes = sorted(path.name for path in parity.PROBES.glob("*.py"))
        self.assertTrue(probes)
        for probe in probes:
            with self.subTest(probe=probe):
                self.assertIn("`probes/%s`" % probe, readme)

    def test_substitution(self):
        self.assertEqual(parity.substitute("{root}/c {state_dir} {account}", Path("/r"), "a@example.com"),
                         "/r/c /r/state a@example.com")


if __name__ == "__main__":
    unittest.main()

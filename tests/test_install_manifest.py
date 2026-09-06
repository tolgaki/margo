import importlib.util
import json
from pathlib import Path
import tempfile
import unittest


PATH = Path(__file__).resolve().parents[1] / "skills/chief-of-staff/scripts/install_manifest.py"
SPEC = importlib.util.spec_from_file_location("install_manifest", PATH)
manifest = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(manifest)


class ManifestTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve()
        self.source = self.root / "source"
        self.dest = self.root / "dest"
        self.dest.mkdir()
        self.extension = self.source / ".github/extensions/margo-action-desk"
        self.extension.mkdir(parents=True)
        (self.extension / "extension.mjs").write_text("export {};\n", encoding="utf-8")

    def test_opt_in_install_record_and_safe_remove(self):
        manifest.install_canvas(self.source, self.dest, {})
        manifest.record(self.source, self.dest, ["chief-of-staff"], {})
        saved = manifest.read_manifest(self.dest)
        relative = "extensions/margo-action-desk/extension.mjs"
        self.assertIn(relative, saved)
        (self.dest / "extensions/margo-action-desk/custom.txt").write_text("user data")
        manifest.remove_canvas(self.dest, saved)
        self.assertFalse((self.dest / relative).exists())
        self.assertTrue((self.dest / "extensions/margo-action-desk/custom.txt").exists())

    def test_edited_renderer_blocks_upgrade_before_copy(self):
        manifest.install_canvas(self.source, self.dest, {})
        manifest.record(self.source, self.dest, [], {})
        saved = manifest.read_manifest(self.dest)
        target = self.dest / "extensions/margo-action-desk/extension.mjs"
        target.write_text("custom")
        (self.extension / "extension.mjs").write_text("updated")
        with self.assertRaises(ValueError):
            manifest.install_canvas(self.source, self.dest, saved)
        self.assertEqual(target.read_text(), "custom")

    def test_private_files_never_in_provenance(self):
        root = self.source / "skills/chief-of-staff"
        target = self.dest / "skills/chief-of-staff"
        (root / "state").mkdir(parents=True)
        (target / "state").mkdir(parents=True)
        for relative in ("preferences.md", "commitments.md", "state/secret.json", "SKILL.md"):
            (root / relative).write_text("fixture")
            (target / relative).write_text("fixture")
        manifest.record(self.source, self.dest, ["chief-of-staff"], {})
        self.assertEqual(set(manifest.read_manifest(self.dest)), {"skills/chief-of-staff/SKILL.md"})

    def test_manifest_path_traversal_is_rejected(self):
        (self.dest / ".margo-files.json").write_text(json.dumps({"../unrelated": "abc"}))
        with self.assertRaises(ValueError):
            manifest.read_manifest(self.dest)


if __name__ == "__main__":
    unittest.main()

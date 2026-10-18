"""The persona and skills desktop Margo uses, served unchanged to the hosted engine.

Desktop Margo runs in a Copilot host that lists the installed skills, loads one when its
routine is asked for, and lets the agent read the files it points to. This module does the same
from the repository's ``agents/`` and ``skills/`` directories, without editing a word, so the
hosted Margo has the same voice and procedures as the desktop one.

Two files differ by design, because they hold the manager's private data rather than procedure:
``chief-of-staff/preferences.md`` is the instance's private copy, and ``commitments.md`` is not a
file here because hosted instances keep commitments only in the work ledger.
"""

import json
import posixpath
from pathlib import Path

MAX_FILE_CHARS = 40000
PRIVATE_PREFERENCES = ("chief-of-staff", "preferences.md")
LEDGER_ONLY = ("chief-of-staff", "commitments.md")
EXCLUDED_DIRS = {"state", "scripts", "__pycache__"}


def split_front_matter(text):
    """Return ``(fields, body)`` for a Markdown file with optional ``---`` front matter."""
    if not text.startswith("---"):
        return {}, text
    end = text.find("\n---", 3)
    if end == -1:
        return {}, text
    fields = {}
    for line in text[3:end].strip().splitlines():
        key, sep, value = line.partition(":")
        if not sep or not key.strip() or key.startswith((" ", "\t")):
            continue
        value = value.strip()
        if value.startswith('"'):
            try:
                value = json.loads(value)
            except ValueError:
                value = value.strip('"')
        fields[key.strip()] = value
    return fields, text[end + 4:].lstrip("\n")


class SkillLibrary:
    def __init__(self, repo_root, preferences=None):
        self.root = Path(repo_root)
        self.preferences = Path(preferences) if preferences else None

    def persona(self):
        return split_front_matter((self.root / "agents" / "margo.agent.md").read_text(encoding="utf-8"))[1]

    def skills(self):
        """Every skill under ``skills/``, as its ``SKILL.md`` front matter declares it."""
        found = []
        for path in sorted((self.root / "skills").glob("*/SKILL.md")):
            fields, _ = split_front_matter(path.read_text(encoding="utf-8"))
            found.append({"name": fields.get("name") or path.parent.name,
                          "description": fields.get("description", ""), "directory": path.parent})
        return found

    def index(self):
        return "\n".join("- **%s**: %s" % (skill["name"], skill["description"]) for skill in self.skills())

    def _skill(self, name):
        for skill in self.skills():
            if isinstance(name, str) and name.strip().lower() == skill["name"]:
                return skill
        return None

    def files(self, name):
        """Readable Markdown files of a skill, relative to its directory."""
        skill = self._skill(name)
        if skill is None:
            return []
        base = skill["directory"]
        listed = []
        for path in sorted(base.rglob("*.md")):
            relative = path.relative_to(base)
            if path.is_symlink() or EXCLUDED_DIRS.intersection(relative.parts[:-1]):
                continue
            listed.append(relative.as_posix())
        return listed

    def load(self, name):
        skill = self._skill(name)
        if skill is None:
            return {"status": "refused", "reason": "unknown skill; available: %s"
                    % ", ".join(s["name"] for s in self.skills())}
        body = split_front_matter((skill["directory"] / "SKILL.md").read_text(encoding="utf-8"))[1]
        return {"status": "ok", "skill": skill["name"], "text": body,
                "files": [path for path in self.files(skill["name"]) if path != "SKILL.md"]}

    def read(self, name, path):
        skill = self._skill(name)
        if skill is None:
            return {"status": "refused", "reason": "unknown skill"}
        relative = self._relative(path)
        if relative is None:
            return {"status": "refused", "reason": "path must name a Markdown file inside the skill"}
        if (skill["name"], relative) == PRIVATE_PREFERENCES:
            if self.preferences is None or not self.preferences.is_file():
                return {"status": "unavailable", "reason": "your manager's preferences are not set up yet"}
            return {"status": "ok", "skill": skill["name"], "path": relative,
                    "text": self.preferences.read_text(encoding="utf-8")[:MAX_FILE_CHARS]}
        if (skill["name"], relative) == LEDGER_ONLY:
            return {"status": "unavailable", "skill": skill["name"], "path": relative,
                    "reason": "hosted instances keep commitments only in the work ledger (the migrated state); "
                              "read them with margo-state work_state.py list or show"}
        listed = set(self.files(skill["name"]))
        for candidate in (relative, "references/" + relative):
            if candidate in listed:
                text = (skill["directory"] / candidate).read_text(encoding="utf-8")
                return {"status": "ok", "skill": skill["name"], "path": candidate, "text": text[:MAX_FILE_CHARS],
                        **({"truncated": True} if len(text) > MAX_FILE_CHARS else {})}
        return {"status": "unavailable", "reason": "no such file in the %s skill" % skill["name"]}

    @staticmethod
    def _relative(path):
        """Normalise the paths procedures use (``references/x.md``, ``../preferences.md``, ``x``)."""
        if not isinstance(path, str) or not path.strip() or len(path) > 512 or "\\" in path or "\x00" in path:
            return None
        value = path.strip()
        if value.startswith("/"):
            return None
        value = posixpath.normpath(value)
        while value.startswith("../"):
            value = value[3:]
        if value in ("", ".", "..") or value.startswith("..") or "/../" in value:
            return None
        if not value.endswith(".md"):
            value += ".md"
        return value

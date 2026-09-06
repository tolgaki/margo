#!/usr/bin/env python3
"""Record installed code hashes and manage the opt-in action-desk renderer."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile


PERSONAL = {"preferences.md", "commitments.md", "config.md"}
EXTENSION = Path("extensions/margo-action-desk")


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def read_manifest(dest):
    path = dest / ".margo-files.json"
    if not path.exists():
        return {}
    data = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(data, dict) or not all(
        isinstance(key, str) and not Path(key).is_absolute() and ".." not in Path(key).parts
        and isinstance(value, str) for key, value in data.items()
    ):
        raise ValueError("invalid installed-file manifest")
    return data


def write_manifest(dest, data):
    fd, name = tempfile.mkstemp(prefix=".margo-files-", dir=dest)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as stream:
            json.dump(data, stream, indent=2, sort_keys=True)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(name, dest / ".margo-files.json")
    finally:
        if os.path.exists(name):
            os.unlink(name)


def extension_files(source):
    root = source / ".github/extensions/margo-action-desk"
    if not (root / "extension.mjs").is_file():
        raise ValueError("action-desk source is missing from this distribution")
    return [
        path for path in sorted(root.rglob("*")) if path.is_file()
        and not any(part in {"node_modules", "artifacts", ".git", "__pycache__"}
                    for part in path.relative_to(root).parts)
    ]


def install_canvas(source, dest, previous):
    root = source / ".github/extensions/margo-action-desk"
    planned = []
    for path in extension_files(source):
        relative = EXTENSION / path.relative_to(root)
        target = dest / relative
        if path.is_symlink() or any(p.is_symlink() for p in (target, *target.parents)):
            raise ValueError("refusing a symlink in extension installation: " + str(relative))
        if target.exists() and digest(target) not in (digest(path), previous.get(relative.as_posix())):
            raise ValueError("modified extension file preserved; review before installing: " + str(relative))
        planned.append((path, target))
    for source_file, target in planned:
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source_file, target)


def record(source, dest, skills, previous):
    result = dict(previous)
    roots = [(source / "agents/margo.agent.md", Path("agents/margo.agent.md"))]
    for skill in skills:
        if skill not in {"chief-of-staff", "decision-log"}:
            raise ValueError("unknown skill: " + skill)
        root = source / "skills" / skill
        for path in root.rglob("*"):
            relative = path.relative_to(root)
            if (path.is_file() and path.name not in PERSONAL
                    and "state" not in relative.parts and "__pycache__" not in relative.parts
                    and path.suffix != ".pyc"):
                roots.append((path, Path("skills") / skill / relative))
    for name in ("margo-scheduled.sh", "margo-scheduled.ps1"):
        roots.append((source / "tools" / name, Path("tools") / name))
    for path in (source / "automations").glob("*.md"):
        if path.name != "README.md":
            roots.append((path, Path("automations") / path.name))
    extension_root = source / ".github/extensions/margo-action-desk"
    if (dest / EXTENSION / "extension.mjs").is_file() and extension_root.is_dir():
        roots.extend((path, EXTENSION / path.relative_to(extension_root))
                     for path in extension_files(source))
    for _, relative in roots:
        target = dest / relative
        if target.is_file():
            result[relative.as_posix()] = digest(target)
    write_manifest(dest, result)


def remove_canvas(dest, previous):
    root = dest / EXTENSION
    if root.is_symlink():
        raise ValueError("refusing to remove a linked extension")
    for relative, expected in previous.items():
        path = Path(relative)
        if path.parts[:2] != EXTENSION.parts:
            continue
        target = dest / path
        if any(parent.is_symlink() for parent in (target, *target.parents)):
            raise ValueError("refusing linked extension path: " + relative)
        if target.is_file() and not target.is_symlink() and digest(target) == expected:
            target.unlink()
        elif target.exists():
            print("Preserved modified extension file: " + relative, file=sys.stderr)
    if root.is_dir():
        for directory in sorted((p for p in root.rglob("*") if p.is_dir()), reverse=True):
            if not directory.is_symlink() and not any(directory.iterdir()):
                directory.rmdir()
        if not any(root.iterdir()):
            root.rmdir()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path)
    parser.add_argument("--dest", required=True, type=Path)
    parser.add_argument("--skills", default="chief-of-staff")
    parser.add_argument("--install-canvas", action="store_true")
    parser.add_argument("--remove-canvas", action="store_true")
    args = parser.parse_args()
    try:
        dest = args.dest.resolve()
        previous = read_manifest(dest)
        if args.remove_canvas:
            remove_canvas(dest, previous)
        else:
            if args.source is None:
                raise ValueError("--source is required for recording an install")
            if args.install_canvas:
                install_canvas(args.source.resolve(), dest, previous)
            record(args.source.resolve(), dest, args.skills.replace(",", " ").split(), previous)
    except (ValueError, OSError) as exc:
        print("ERROR: " + str(exc), file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())

"""Explicit display identity and output routing; runtime state never follows the work root."""

import hashlib
import os
from pathlib import Path, PureWindowsPath
import re
import unicodedata
import uuid

import margo_store as store


def display_name(value):
    if not isinstance(value, str):
        raise store.StateError("Assistant name must be plain text.")
    value = unicodedata.normalize("NFC", value)
    if (not 1 <= len(value) <= 60 or value != value.strip()
            or not any(unicodedata.category(char)[0] in "LN" for char in value)
            or any(unicodedata.category(char)[0] not in "LMN" and char not in " .'-" for char in value)):
        raise store.StateError("Assistant name must be 1-60 letters/numbers with spaces, periods, apostrophes or hyphens; no controls or markup.")
    return value


def _configuration(config=None):
    path = store.configuration_path(config)
    store._no_symlinks(path)
    store._safe_directory(path.parent, explicit_override=True)
    if not path.exists():
        if config is not None or "MARGO_CONFIG" in os.environ:
            raise store.LocationError("The explicitly selected profile configuration is missing. Inspect margo_store.py locations; no fallback used.")
        return path, {}, None
    store._private(path)
    try:
        with path.open("rb") as stream:
            raw = stream.read(262145)
    except OSError as exc:
        raise store.LocationError("Private profile configuration is inaccessible; no defaults applied.") from exc
    if len(raw) > 262144:
        raise store.LocationError("Private configuration exceeds 256 KiB.")
    try:
        data = store.parse_json(raw.decode("utf-8"))
    except (UnicodeError, store.StateError) as exc:
        raise store.LocationError("Private configuration is not valid UTF-8 JSON; no defaults applied.") from exc
    if not isinstance(data, dict) or not isinstance(data.get("profiles", {}), dict):
        raise store.LocationError("Private configuration/profiles must be objects.")
    return path, data, hashlib.sha256(raw).hexdigest()


def _account(account, data):
    candidate = account if account is not None else os.environ.get("MARGO_ACCOUNT", data.get("account"))
    return store.resolve_account(candidate) if candidate is not None else None


def _profile(data, account):
    profile = data.get("profiles", {}).get(account, {})
    if not isinstance(profile, dict) or set(profile) - {"assistant_name", "work_root"}:
        raise store.StateError("Profile has unsupported fields; preserve it and review the configuration.")
    return profile


def _no_redirects(path):
    for part in (path,) + tuple(path.parents):
        if part.is_symlink():
            raise store.StateError("Symlinked workspace paths are not supported.")
        try:
            info = part.lstat()
        except FileNotFoundError:
            continue
        except OSError as exc:
            raise store.StateError("Workspace path cannot be inspected; no fallback is used.") from exc
        if getattr(info, "st_reparse_tag", 0) in {0xA0000003, 0xA000000C}:
            raise store.StateError("Workspace junctions or symbolic links are not supported.")


def work_root(value):
    if (not isinstance(value, str) or not value or len(value) > 4000
            or any(unicodedata.category(char).startswith("C") for char in value)):
        raise store.StateError("Work root must be an explicit absolute local directory.")
    path = Path(value)
    if (not path.is_absolute() or ".." in path.parts or str(path).startswith(("\\\\", "//"))
            or path == Path(path.anchor) or path == Path.home()):
        raise store.StateError("Work root must be a dedicated absolute local directory, not a root/home, network or traversal path.")
    _no_redirects(path)
    try:
        if not path.is_dir() or not os.access(path, os.R_OK | os.W_OK | os.X_OK):
            raise store.StateError("Configured work root is missing or inaccessible; no fallback directory is used.")
        resolved = path.resolve(strict=True)
    except OSError as exc:
        raise store.StateError("Configured work root is unavailable; no fallback directory is used.") from exc
    for ancestor in (resolved,) + tuple(resolved.parents):
        if (ancestor / ".git").exists():
            raise store.StateError("Work outputs cannot be routed into a repository.")
    protected = [
        store.copilot_home(),
        Path(__file__).resolve().parents[3],
        store.private_state_root(),
    ]
    if any(resolved == root or root in resolved.parents or resolved in root.parents for root in protected):
        raise store.StateError("The work root must be separate from the private installation/runtime directory.")
    return resolved


def show(account=None, config=None):
    path, data, revision = _configuration(config)
    principal = _account(account, data)
    profile = _profile(data, principal)
    name = display_name(profile.get("assistant_name", "Margo"))
    root = profile.get("work_root")
    workspace = {"path": root, "status": "not_configured"}
    if root is not None:
        try:
            workspace.update(path=str(work_root(root)), status="available")
        except (store.StateError, OSError) as exc:
            workspace.update(status="unavailable", reason=str(exc))
    return {"account": principal, "assistant_name": name, "work_root": workspace,
            "revision": revision, "config_path": str(path), "configured": revision is not None,
            "local_owner_configured": principal is not None, "m365_authentication": "not_checked",
            "runtime_state_moved": False}


def configure(account, config, expected_revision, assistant_name=None, root=None, clear_root=False):
    if assistant_name is None and root is None and not clear_root:
        raise store.StateError("Choose an explicit assistant-name or work-root change.")
    if root is not None and clear_root:
        raise store.StateError("Cannot set and clear the work root together.")
    def update(data, principal):
        profile = dict(_profile(data, principal))
        if assistant_name is not None:
            profile["assistant_name"] = display_name(assistant_name)
        if root is not None:
            profile["work_root"] = str(work_root(root))
        elif clear_root:
            profile.pop("work_root", None)
        data.setdefault("profiles", {})[principal] = profile

    update_configuration(account, config, expected_revision, update)
    return show(account, config)


def update_configuration(account, config, expected_revision, update):
    """Apply a trusted local configuration transform under the shared revision lock."""
    if not isinstance(expected_revision, str) or not re.fullmatch(r"[a-f0-9]{64}", expected_revision):
        raise store.StateError("Use the exact configuration revision from profile-show.")
    path, _, _ = _configuration(config)
    lock = path.with_name("." + path.name + ".profile-lock")
    staging = path.with_name("." + path.name + "." + uuid.uuid4().hex + ".new")
    locked = False
    try:
        descriptor = os.open(str(lock), os.O_CREAT | os.O_EXCL | os.O_WRONLY | getattr(os, "O_NOFOLLOW", 0), 0o600)
        os.close(descriptor)
        locked = True
        _, data, revision = _configuration(config)
        if revision is None:
            raise store.SetupRequired("Initialize private account config explicitly before configuring a profile.")
        if revision != expected_revision:
            raise store.StateError("Configuration revision conflict; reread profile-show and review the current settings.")
        principal = _account(account, data)
        if principal is None:
            raise store.SetupRequired("An explicit account is required.")
        update(data, principal)
        descriptor = os.open(str(staging), os.O_CREAT | os.O_EXCL | os.O_WRONLY | getattr(os, "O_NOFOLLOW", 0), 0o600)
        with os.fdopen(descriptor, "w", encoding="utf-8") as stream:
            stream.write(store.canonical_json(data) + "\n")
            stream.flush()
            os.fsync(stream.fileno())
        if _configuration(config)[2] != revision:
            raise store.StateError("Configuration changed during update; no settings replaced.")
        os.replace(str(staging), str(path))
        if os.name != "nt":
            descriptor = os.open(str(path.parent), os.O_RDONLY | getattr(os, "O_DIRECTORY", 0))
            try:
                os.fsync(descriptor)
            finally:
                os.close(descriptor)
        return _configuration(config)[2]
    except FileExistsError as exc:
        raise store.StateError("A profile update is already in progress or left a lock; inspect it before retrying.") from exc
    except OSError as exc:
        raise store.StateError("Profile update unavailable; no workspace files or runtime state moved.") from exc
    finally:
        staging.unlink(missing_ok=True)
        if locked:
            lock.unlink()


def output_path(relative, expected_revision, account=None, config=None):
    profile = show(account, config)
    if expected_revision != profile["revision"] or expected_revision is None:
        raise store.StateError("Profile revision conflict; recheck the work root before writing.")
    if profile["work_root"]["status"] != "available":
        raise store.StateError("Work root unavailable or not configured; output must not fall back to the repository.")
    if (not isinstance(relative, str) or not relative or len(relative) > 240
            or any(unicodedata.category(char).startswith("C") for char in relative)):
        raise store.StateError("Output name must be a bounded relative path.")
    relative_path = PureWindowsPath(relative)
    if (relative_path.is_absolute() or relative_path.drive or Path(relative).is_absolute()
            or any(part in {".", ".."} or part.endswith((".", " ")) or ":" in part
                   or re.fullmatch(r"(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\..*)?", part, re.I)
                   for part in relative_path.parts)):
        raise store.StateError("Output path must not escape the work root or use reserved names.")
    root = work_root(profile["work_root"]["path"])
    target = root.joinpath(*relative_path.parts)
    _no_redirects(target)
    parent = work_root(str(target.parent))
    if parent != root and root not in parent.parents:
        raise store.StateError("Output path escapes the configured work root.")
    if target.exists():
        raise store.StateError("Output already exists; choose a new explicit filename. Nothing was overwritten.")
    return target


def export_artifact(ledger, identity, revision, relative, profile_revision):
    artifact = ledger.show(identity)
    if artifact.get("kind") != "artifact" or type(revision) is not int or artifact["revision"] != revision:
        raise store.StateError("Artifact export requires an exact current artifact revision.")
    if artifact["stale"]:
        raise store.StateError("Artifact source evidence changed; review it before exporting.")
    if not isinstance(relative, str) or not relative.lower().endswith(".md"):
        raise store.StateError("Private artifact export supports Markdown filenames only.")
    target = output_path(relative, profile_revision, ledger.account)
    content = artifact["data"]["markdown"]
    if len(content.encode("utf-8")) > 1024 * 1024:
        raise store.StateError("Artifact exceeds the 1 MiB local export limit.")
    try:
        descriptor = os.open(str(target), os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0), 0o600)
        with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as stream:
            stream.write(content)
            stream.flush()
            os.fsync(stream.fileno())
    except OSError as exc:
        raise store.StateError("Artifact export failed; inspect the exact output path for a partial file before retrying.") from exc
    return {"path": str(target), "artifact_id": identity, "revision": revision,
            "written": True, "publication": "not_performed_by_helper",
            "sync_status": "not_observed", "runtime_state_moved": False}

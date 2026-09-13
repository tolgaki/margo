"""Explicit installation locator for existing private config/state; never a data store."""

import hashlib
import os
from pathlib import Path
import re
import uuid

import margo_store as store


LIMIT = 16384
ENVIRONMENT = {"config_path": "MARGO_CONFIG", "state_root": "MARGO_STATE_DIR"}


def binding_path():
    return store.copilot_home() / "margo" / "locations.json"


def _path(value):
    if (not isinstance(value, str) or not value or len(value) > 4000
            or re.search(r"[\x00-\x1f\x7f]", value)):
        raise store.LocationError("Binding paths must be bounded absolute local paths.")
    path = Path(value)
    if (not path.is_absolute() or ".." in path.parts or str(path).startswith(("\\\\", "//"))
            or any(":" in part or part.endswith((".", " ")) for part in path.parts[1:])):
        raise store.LocationError("Binding paths must be absolute local paths without traversal.")
    return path


def _raw():
    path = binding_path()
    try:
        store._no_symlinks(path)
        store._safe_directory(path.parent, explicit_override=True)
        try:
            store._private(path)
        except FileNotFoundError:
            return path, None, "missing"
        store._private(path.parent, directory=True)
        with path.open("rb") as stream:
            raw = stream.read(LIMIT + 1)
        if len(raw) > LIMIT:
            raise store.LocationError("Installation binding exceeds 16 KiB; inspect it explicitly.")
        return path, raw, hashlib.sha256(raw).hexdigest()
    except (OSError, store.StateError) as exc:
        raise store.LocationError("Installation binding cannot be read safely: %s" % exc) from exc


def _validate(value):
    if (not isinstance(value, dict) or set(value) != {"schema_version", "config_path", "state_root"}
            or type(value["schema_version"]) is not int or value["schema_version"] != 1):
        raise store.LocationError("Unsupported installation binding; expected schema_version 1, config_path and state_root.")
    config = _path(value["config_path"])
    root = _path(value["state_root"])
    try:
        store._no_symlinks(config)
        store._safe_directory(config.parent, explicit_override=True)
        store._safe_directory(root, explicit_override=True)
        store._private(config, directory=False)
        store._private(config.parent, directory=True)
        store._private(root, directory=True)
        if config == binding_path():
            raise store.LocationError("The installation binding cannot be its own account configuration.")
    except (OSError, store.StateError) as exc:
        raise store.LocationError("Bound private location unavailable: %s. No default store was selected." % exc) from exc
    return dict(value, config_path=str(config), state_root=str(root))


def read_binding():
    _, raw, revision = _raw()
    if raw is None:
        return None, revision
    try:
        value = store.parse_json(raw.decode("utf-8"))
        return _validate(value), revision
    except (UnicodeError, store.StateError) as exc:
        raise store.LocationError("Invalid or unavailable installation binding: %s. Inspect margo_store.py locations; no fallback used." % exc) from exc


def resolve_location(kind, override=None):
    supplied = override if override is not None else os.environ.get(ENVIRONMENT[kind])
    if supplied is not None:
        if not isinstance(supplied, (str, Path)) or not str(supplied):
            raise store.LocationError("Explicit %s must not be empty." % ENVIRONMENT[kind])
        path = Path(supplied).expanduser().absolute()
        try:
            store._no_symlinks(path)
            return (store._safe_directory(path, explicit_override=True) if kind == "state_root"
                    else store._safe_directory(path.parent, explicit_override=True) / path.name)
        except (OSError, store.StateError) as exc:
            raise store.LocationError("Explicit %s is unsafe or unavailable: %s. No fallback used." % (ENVIRONMENT[kind], exc)) from exc
    value, _ = read_binding()
    if value:
        return Path(value[kind])
    default = store.copilot_home() / "margo" / ("config.json" if kind == "config_path" else "state")
    return store._safe_directory(default, explicit_override=False) if kind == "state_root" else default


def inspect():
    path, raw, revision = _raw()
    result = {"binding_path": str(path), "revision": revision, "binding": None,
              "status": "not_bound" if raw is None else "bound", "m365_authentication": "not_checked"}
    try:
        if raw is not None:
            result["binding"] = _validate(store.parse_json(raw.decode("utf-8")))
        result["effective"] = {kind: {"path": str(resolve_location(kind)),
            "source": ENVIRONMENT[kind] if ENVIRONMENT[kind] in os.environ else "binding" if raw else "legacy_default"}
            for kind in ENVIRONMENT}
    except (UnicodeError, store.StateError) as exc:
        result.update(status="blocked", error=str(exc))
    return result


def _revision(expected):
    if expected != "missing" and (not isinstance(expected, str) or not re.fullmatch(r"[a-f0-9]{64}", expected)):
        raise store.StateError("Use the exact binding revision from locations, or missing for a first binding.")


def _replace(value, expected):
    _revision(expected)
    path, _, _ = _raw()
    store._mkdir_private(path.parent)
    lock = path.with_name(".locations.lock")
    staging = path.with_name(".locations-" + uuid.uuid4().hex + ".new")
    locked = False
    try:
        descriptor = os.open(str(lock), os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0), 0o600)
        os.close(descriptor)
        locked = True
        _, raw, actual = _raw()
        if actual != expected:
            raise store.StateError("Binding revision conflict; reread locations and review the change.")
        if value is None:
            if raw is not None:
                path.unlink()
        else:
            value = _validate(value)
            descriptor = os.open(str(staging), os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0), 0o600)
            with os.fdopen(descriptor, "w", encoding="utf-8") as stream:
                stream.write(store.canonical_json(value) + "\n")
                stream.flush()
                os.fsync(stream.fileno())
            if _raw()[2] != actual:
                raise store.StateError("Binding changed during update; nothing replaced.")
            if actual == "missing":
                os.link(str(staging), str(path))
                staging.unlink()
            else:
                os.replace(str(staging), str(path))
        if os.name != "nt":
            descriptor = os.open(str(path.parent), os.O_RDONLY | getattr(os, "O_DIRECTORY", 0))
            try:
                os.fsync(descriptor)
            finally:
                os.close(descriptor)
        return dict(inspect(), private_data_changed=False, initialized=False)
    except FileExistsError as exc:
        raise store.StateError("Binding update is locked or changed. Inspect locations/lock before retrying; never remove a live lock.") from exc
    except OSError as exc:
        raise store.LocationError("Binding write did not complete reliably; reread locations before retrying.") from exc
    finally:
        staging.unlink(missing_ok=True)
        if locked:
            lock.unlink()


def bind(config, state_root, account, expected_revision):
    if account is None:
        raise store.StateError("Binding requires the explicitly confirmed local owner.")
    value = _validate({"schema_version": 1, "config_path": config, "state_root": state_root})
    principal = store.resolve_account(account)
    try:
        with Path(config).open(encoding="utf-8") as stream:
            raw = stream.read(262145)
        if len(raw) > 262144:
            raise store.StateError("Account configuration exceeds 256 KiB.")
        data = store.parse_json(raw)
        if not isinstance(data, dict) or data.get("account") != principal:
            raise store.StateError("Existing configuration does not match the explicitly confirmed owner.")
    except (OSError, UnicodeError) as exc:
        raise store.LocationError("Existing account configuration could not be verified; no binding written.") from exc
    return _replace(value, expected_revision)


def clear(expected_revision):
    return _replace(None, expected_revision)

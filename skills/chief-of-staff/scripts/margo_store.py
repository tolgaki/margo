#!/usr/bin/env python3
"""Private, account-scoped SQLite storage shared by Margo's local CLIs.

``state_root``/MARGO_STATE_DIR is a *base* directory; an account hash is always
appended. The default is ~/.copilot/margo/state. Account selection is explicit:
--account, MARGO_ACCOUNT, then the private ~/.copilot/margo/config.json object's
``account`` field (MARGO_CONFIG may select another private configuration file).
Repository/synchronised directories are rejected. Synthetic tests may opt in
with MARGO_ALLOW_UNSAFE_STATE_DIR=1 and an explicit state-root override.

No encryption is implied by private file permissions. Do not store credentials.
Use ``with connection:`` for transactions; read-modify-write callers must execute
BEGIN IMMEDIATE before reading. Closing a connection remains the caller's job.

Explicit private configuration (no identity lookup or network access):
    python3 margo_store.py init --account OWNER_PRINCIPAL
This saves the ledger owner's principal in the config's account field, without
creating state or changing an existing account. --config selects another private
config file; repository/shared overrides require the synthetic-testing opt-in.
"""

import argparse
import contextlib
import hashlib
import json
import os
import re
import sqlite3
import stat
import sys
import uuid
from datetime import datetime, timezone
from pathlib import Path


class StateError(ValueError):
    """Invalid input or unavailable state; never reset or continue unlocked."""


class SetupRequired(StateError):
    """No account is configured."""


class NotInitialized(StateError):
    """Explicit initialization is required; a read must not create storage."""


def utc_now():
    return datetime.now(timezone.utc).isoformat(timespec="microseconds")


def validate_timestamp(value):
    """Validate an explicitly zoned ISO datetime and normalise to UTC."""
    if not isinstance(value, str) or not re.fullmatch(
        r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,6})?(?:Z|[+-]\d{2}:\d{2})",
        value,
    ):
        raise StateError("timestamp must be an ISO-8601 datetime with an explicit timezone")
    try:
        if value[-6:-5] in ("+", "-"):
            if int(value[-5:-3]) > 23 or int(value[-2:]) > 59:
                raise ValueError("invalid offset")
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
        return parsed.astimezone(timezone.utc).isoformat(timespec="microseconds")
    except (ValueError, OverflowError) as exc:
        raise StateError("invalid timestamp") from exc


parse_timestamp = validate_timestamp


def canonical_json(value):
    try:
        return json.dumps(value, sort_keys=True, separators=(",", ":"),
                          ensure_ascii=False, allow_nan=False)
    except (TypeError, ValueError) as exc:
        raise StateError("value must contain finite JSON data") from exc


def parse_json(raw):
    def pairs(entries):
        result = {}
        for key, value in entries:
            if key in result:
                raise StateError("duplicate JSON object key")
            result[key] = value
        return result

    def invalid_constant(_):
        raise StateError("non-finite JSON number")

    try:
        value = json.loads(raw, object_pairs_hook=pairs, parse_constant=invalid_constant)
        canonical_json(value)
        return value
    except (TypeError, json.JSONDecodeError) as exc:
        raise StateError("invalid JSON") from exc


def read_json(path):
    try:
        return parse_json(Path(path).read_text(encoding="utf-8"))
    except (OSError, UnicodeError) as exc:
        raise StateError("cannot read valid JSON from " + str(path)) from exc


@contextlib.contextmanager
def transaction(connection, *, read_only=False):
    """Keep an outer unit of work intact when existing state APIs are composed."""
    nested = connection.in_transaction
    savepoint = "margo_" + uuid.uuid4().hex if nested else None
    try:
        connection.execute("SAVEPOINT " + savepoint if nested else
                           "BEGIN" if read_only else "BEGIN IMMEDIATE")
        yield
        if nested:
            connection.execute("RELEASE SAVEPOINT " + savepoint)
        else:
            connection.commit()
    except Exception:
        if nested:
            connection.execute("ROLLBACK TO SAVEPOINT " + savepoint)
            connection.execute("RELEASE SAVEPOINT " + savepoint)
        else:
            connection.rollback()
        raise


def add_state_arguments(parser):
    parser.add_argument("--account", help="explicit principal; otherwise MARGO_ACCOUNT/private config")
    parser.add_argument("--state-dir", help="private state base directory; account hash is appended")


def copilot_home():
    """Resolve the explicitly configured installation root once, not arbitrary state links."""
    return Path(os.environ.get("COPILOT_HOME", "~/.copilot")).expanduser().resolve()


def _no_symlinks(path):
    for part in (path,) + tuple(path.parents):
        if part.is_symlink():
            raise StateError("symlinked state/config paths are not supported")


def _private(path, directory=False):
    info = path.stat()
    if not (stat.S_ISDIR(info.st_mode) if directory else stat.S_ISREG(info.st_mode)):
        raise StateError("state/config path has the wrong file type")
    if hasattr(os, "getuid") and info.st_uid != os.getuid():
        raise StateError("state/config path must belong to the current user")
    if os.name != "nt" and stat.S_IMODE(info.st_mode) & 0o077:
        raise StateError("state/config path must be private (directory 0700, file 0600)")
    if not directory and info.st_nlink != 1:
        raise StateError("hard-linked state/config files are not supported")


def resolve_account(account=None):
    candidate = account if account is not None else os.environ.get("MARGO_ACCOUNT")
    if candidate is None:
        config = Path(os.environ.get("MARGO_CONFIG", str(copilot_home() / "margo/config.json"))).expanduser().absolute()
        _no_symlinks(config)
        if config.exists():
            _private(config)
            data = read_json(config)
            if not isinstance(data, dict):
                raise StateError("installation config must be a JSON object")
            candidate = data.get("account")
    if candidate is None:
        raise SetupRequired("Set MARGO_ACCOUNT or account in private ~/.copilot/margo/config.json")
    if (not isinstance(candidate, str) or not candidate.strip() or len(candidate) > 512
            or re.search(r"[\x00-\x20\x7f{}]", candidate)):
        raise StateError("account must be an explicit, non-placeholder principal without whitespace")
    # Opaque principal IDs may be case-sensitive; preserve exactly what was configured.
    return candidate


def _safe_directory(root, explicit_override=False):
    root = Path(root).expanduser().absolute()
    _no_symlinks(root)
    root = root.resolve()
    unsafe_test = explicit_override and os.environ.get("MARGO_ALLOW_UNSAFE_STATE_DIR") == "1"
    if not unsafe_test:
        for ancestor in (root,) + tuple(root.parents):
            name = ancestor.name.casefold()
            if ((ancestor / ".git").exists()
                    or any(word in name for word in ("onedrive", "dropbox", "icloud",
                                                     "cloudstorage", "mobile documents",
                                                     "google drive", "googledrive"))
                    or name in ("shared", "public", "box", "box sync")):
                raise StateError("repository/shared/synchronised state/config roots are forbidden")
        for ancestor in (root,) + tuple(root.parents):
            if ancestor.exists() and os.name != "nt":
                if ancestor.stat().st_mode & 0o022:
                    raise StateError("state/config root has a group/world-writable ancestor")
    return root


def state_path(account=None, state_root=None):
    principal = resolve_account(account)
    override = state_root if state_root is not None else os.environ.get("MARGO_STATE_DIR")
    root = _safe_directory(override if override is not None else copilot_home() / "margo/state",
                           explicit_override=override is not None)
    key = hashlib.sha256(principal.encode("utf-8")).hexdigest()
    return principal, root / key / "margo.sqlite3"


def _mkdir_private(path):
    if not path.exists():
        missing = []
        current = path
        while not current.exists():
            missing.append(current)
            current = current.parent
        for current in reversed(missing):
            try:
                current.mkdir(mode=0o700)
            except FileExistsError:
                pass
    _no_symlinks(path)
    _private(path, directory=True)


def connect(account=None, state_root=None, *, read_only=False):
    """Open validated storage; never infer an account or replace damaged state."""
    connection = None
    try:
        principal, database = state_path(account, state_root)
        if read_only:
            if not database.exists():
                raise NotInitialized("Private memory state is not initialized; run memory_state.py init explicitly.")
            _private(database.parent.parent, directory=True)
            _private(database.parent, directory=True)
        else:
            _mkdir_private(database.parent.parent)
            _mkdir_private(database.parent)
        _no_symlinks(database)
        created = False
        if read_only:
            _private(database)
        else:
            try:
                descriptor = os.open(str(database), os.O_CREAT | os.O_EXCL | os.O_WRONLY
                                     | getattr(os, "O_NOFOLLOW", 0), 0o600)
                os.close(descriptor)
                created = True
            except FileExistsError:
                _private(database)
        for suffix in ("-journal", "-wal", "-shm"):
            sidecar = Path(str(database) + suffix)
            _no_symlinks(sidecar)
            if sidecar.exists():
                _private(sidecar)
        connection = sqlite3.connect(database.as_uri() + "?mode=ro" if read_only else str(database),
                                     uri=read_only, timeout=5, isolation_level="IMMEDIATE")
        connection.row_factory = sqlite3.Row
        connection.execute("PRAGMA busy_timeout=5000")
        connection.execute("PRAGMA foreign_keys=ON")
        connection.execute("PRAGMA temp_store=MEMORY")
        with connection:
            connection.execute("BEGIN" if read_only else "BEGIN IMMEDIATE")
            tables = {row[0] for row in connection.execute(
                "SELECT name FROM sqlite_master WHERE type='table'")}
            if "margo_meta" not in tables:
                if not created:
                    raise StateError("existing database has no Margo identity; refusing to reset it")
                connection.execute("CREATE TABLE margo_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL)")
                connection.executemany("INSERT INTO margo_meta VALUES (?,?)",
                                       (("account", principal), ("store_version", "1")))
            metadata = dict(connection.execute("SELECT key,value FROM margo_meta"))
            if metadata.get("account") != principal or metadata.get("store_version") != "1":
                raise StateError("database account/schema mismatch; explicit migration required")
            if connection.execute("PRAGMA quick_check").fetchone()[0] != "ok":
                raise StateError("database integrity check failed; restore a verified backup")
            if connection.execute("PRAGMA foreign_key_check").fetchone() is not None:
                raise StateError("database contains broken references; refusing to continue")
        if not read_only:
            connection.execute("PRAGMA synchronous=FULL")
        _private(database)
        return connection
    except (OSError, sqlite3.Error, StateError) as exc:
        if connection is not None:
            connection.close()
        if isinstance(exc, StateError):
            raise
        raise StateError("storage unavailable; no reset performed (" + type(exc).__name__ + ")") from exc


def initialize_config(account, config_path=None):
    """Create a private owner config atomically, without replacing another owner."""
    if account is None:
        raise StateError("init requires an explicit --account owner principal")
    principal = resolve_account(account)
    override = config_path if config_path is not None else os.environ.get("MARGO_CONFIG")
    path = Path(override if override is not None else copilot_home() / "margo/config.json").expanduser().absolute()
    staging = None
    try:
        _no_symlinks(path)
        parent = _safe_directory(path.parent, explicit_override=override is not None)
        path = parent / path.name
        _mkdir_private(parent)

        def verify_existing():
            _no_symlinks(path)
            _private(path)
            data = read_json(path)
            if not isinstance(data, dict) or data.get("account") != principal:
                raise StateError("existing config has a different/missing account; refusing to replace it")

        if path.exists():
            verify_existing()
            return {"status": "configured", "created": False, "account_configured": True,
                    "config_path": str(path)}
        staging = parent / ("." + path.name + "." + uuid.uuid4().hex + ".new")
        descriptor = os.open(str(staging), os.O_CREAT | os.O_EXCL | os.O_WRONLY
                             | getattr(os, "O_NOFOLLOW", 0), 0o600)
        with os.fdopen(descriptor, "w", encoding="utf-8") as stream:
            stream.write(canonical_json({"account": principal}) + "\n")
            stream.flush()
            os.fsync(stream.fileno())
        created = True
        try:
            # An atomic no-replace link prevents concurrent initializers from
            # silently changing the account selected by another initializer.
            os.link(str(staging), str(path))
        except FileExistsError:
            created = False
        staging.unlink()
        staging = None
        verify_existing()
        if os.name != "nt":
            descriptor = os.open(str(parent), os.O_RDONLY | getattr(os, "O_DIRECTORY", 0))
            try:
                os.fsync(descriptor)
            finally:
                os.close(descriptor)
        return {"status": "configured", "created": created, "account_configured": True,
                "config_path": str(path)}
    except (OSError, StateError) as exc:
        if isinstance(exc, StateError):
            raise
        raise StateError("configuration unavailable; no existing config replaced") from exc
    finally:
        if staging is not None:
            staging.unlink(missing_ok=True)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    init = commands.add_parser("init", help="explicitly save the ledger owner in private config; no network calls")
    init.add_argument("--account", required=True, help="explicit ledger owner principal")
    init.add_argument("--config", help="private config path; otherwise MARGO_CONFIG or ~/.copilot/margo/config.json")
    args = parser.parse_args(argv)
    try:
        print(canonical_json(initialize_config(args.account, args.config)))
        return 0
    except StateError as exc:
        print("ERROR: " + str(exc), file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())

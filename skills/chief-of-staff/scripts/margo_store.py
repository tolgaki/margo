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
    python3 margo_store.py init --account OWNER_PRINCIPAL --manager MANAGER_OBJECT_ID
    python3 margo_store.py migrate-config --account OWNER_PRINCIPAL --manager MANAGER_OBJECT_ID
    python3 margo_store.py rebind-manager --account OWNER_PRINCIPAL --current-manager BOUND_ID --manager NEW_ID
``init`` saves the ledger owner's principal in the config's account field, without
creating state or changing an existing account. A version-1 config holds the
account alone and keeps local behaviour. ``--manager`` binds exactly one manager by
immutable Entra object ID in a version-2 config ({"account", "config_version": 2,
"manager": {"object_id", "principal"}, "profile": "local"|"remote-host"}); only the
remote harness requires a binding. An existing version-1 file is upgraded only by
the explicit ``migrate-config`` command, and a bound manager changes only through
``rebind-manager`` with the current object ID. Reads never create, migrate or repair
the file; a newer or unknown shape is refused with "config version mismatch;
explicit migration required". No command prints configured principals or object
IDs. --config selects another private config file; repository/shared overrides
require the synthetic-testing opt-in.
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


class ManagerRequired(StateError):
    """A remote-host operation needs a bound manager; local behaviour needs none."""


class ConfigMismatch(StateError):
    """The private config has a shape this version cannot read; nothing is rewritten."""

    def __init__(self, version=None):
        super().__init__(CONFIG_MISMATCH)
        self.version = version


CONFIG_VERSION = 2
CONFIG_PROFILES = ("local", "remote-host")
CONFIG_MISMATCH = "config version mismatch; explicit migration required"
# Entra object IDs are GUIDs; the writer stores the lowercase form and readers compare it.
GUID = re.compile(r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")


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


def _validate_principal(candidate, label="account"):
    if (not isinstance(candidate, str) or not candidate.strip() or len(candidate) > 512
            or re.search(r"[\x00-\x20\x7f{}]", candidate)):
        raise StateError(label + " must be an explicit, non-placeholder principal without whitespace")
    # Opaque principal IDs may be case-sensitive; preserve exactly what was configured.
    return candidate


def validate_manager_object_id(value):
    """Normalise an Entra object ID to a lowercase GUID; placeholders and the nil GUID bind nobody."""
    candidate = value.casefold() if isinstance(value, str) else ""
    if not GUID.fullmatch(candidate) or set(candidate) <= set("0-"):
        raise StateError("manager must be an Entra object ID (GUID)")
    return candidate


def _validate_profile(value):
    if value not in CONFIG_PROFILES:
        raise StateError("profile must be local or remote-host")
    return value


def validate_config(data):
    """Check a config document's shape; version 1 is {"account"} alone, version 2 adds the binding."""
    if not isinstance(data, dict):
        raise StateError("installation config must be a JSON object")
    version = data["config_version"] if "config_version" in data else 1
    known = {1: {"account"}, CONFIG_VERSION: {"account", "config_version", "manager", "profile"}}
    if (isinstance(version, bool) or not isinstance(version, int) or version not in known
            or set(data) != known[version]):
        raise ConfigMismatch(version if isinstance(version, int) and not isinstance(version, bool) else None)
    if version == 1:
        return dict(data)
    manager = data["manager"]
    if (not isinstance(manager, dict) or set(manager) != {"object_id", "principal"}
            or data["profile"] not in CONFIG_PROFILES):
        raise StateError("installation config has an invalid manager binding")
    if manager["principal"] is not None:
        _validate_principal(manager["principal"], "manager principal")
    return dict(data, manager={"object_id": validate_manager_object_id(manager["object_id"]),
                               "principal": manager["principal"]})


def _config_path(config_path=None):
    """Select the private config: the explicit path, then MARGO_CONFIG, then the installation default."""
    override = config_path if config_path is not None else os.environ.get("MARGO_CONFIG")
    path = Path(override if override is not None else copilot_home() / "margo/config.json").expanduser().absolute()
    return path, override is not None


def load_config(config_path=None):
    """Read and validate the private config without creating, migrating or repairing it; None when absent."""
    path, _ = _config_path(config_path)
    _no_symlinks(path)
    if not path.exists():
        return None
    _private(path)
    return validate_config(read_json(path))


def resolve_account(account=None):
    candidate = account if account is not None else os.environ.get("MARGO_ACCOUNT")
    if candidate is None:
        config = load_config()
        if config is not None:
            candidate = config.get("account")
    if candidate is None:
        raise SetupRequired("Set MARGO_ACCOUNT or account in private ~/.copilot/margo/config.json")
    return _validate_principal(candidate)


def resolve_manager(config_path=None):
    """Return the bound manager {"object_id", "principal"}, or None for an absent or version-1 config."""
    config = load_config(config_path)
    if config is None or config.get("manager") is None:
        return None
    return dict(config["manager"])


def resolve_profile(config_path=None):
    """Return the deployment profile; an absent or version-1 config keeps local behaviour."""
    config = load_config(config_path)
    return "local" if config is None or config.get("manager") is None else config["profile"]


def require_manager(config_path=None):
    """Return the bound manager or raise ManagerRequired; only remote-host paths need to call this."""
    manager = resolve_manager(config_path)
    if manager is None:
        raise ManagerRequired("no manager is configured; run margo_store.py init --manager "
                              "or migrate-config explicitly before remote-host work")
    return manager


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


def _config_document(principal, manager=None, manager_principal=None, profile=None):
    if manager is None:
        return {"account": principal}
    return {"account": principal, "config_version": CONFIG_VERSION,
            "manager": {"object_id": manager, "principal": manager_principal}, "profile": profile}


def _config_summary(path, config, **extra):
    """Describe a config for CLI output: configured flags and versions, never the identifiers."""
    bound = config.get("manager") is not None
    result = {"status": "configured", "account_configured": True, "config_path": str(path),
              "manager_configured": bound, "config_version": CONFIG_VERSION if bound else 1,
              "profile": config["profile"] if bound else "local"}
    result.update(extra)
    return result


def _existing_config(path, principal):
    """Read a config that must already belong to ``principal``; nothing is rewritten here."""
    _no_symlinks(path)
    _private(path)
    try:
        original = path.read_bytes()
        data = parse_json(original.decode("utf-8"))
    except (OSError, UnicodeError) as exc:
        raise StateError("cannot read valid JSON from " + str(path)) from exc
    if not isinstance(data, dict) or data.get("account") != principal:
        raise StateError("existing config has a different/missing account; refusing to replace it")
    return original, validate_config(data)


def _stage_config(parent, name, document):
    staging = parent / ("." + name + "." + uuid.uuid4().hex + ".new")
    descriptor = os.open(str(staging), os.O_CREAT | os.O_EXCL | os.O_WRONLY
                         | getattr(os, "O_NOFOLLOW", 0), 0o600)
    with os.fdopen(descriptor, "w", encoding="utf-8") as stream:
        stream.write(canonical_json(document) + "\n")
        stream.flush()
        os.fsync(stream.fileno())
    return staging


def _fsync_directory(parent):
    if os.name != "nt":
        descriptor = os.open(str(parent), os.O_RDONLY | getattr(os, "O_DIRECTORY", 0))
        try:
            os.fsync(descriptor)
        finally:
            os.close(descriptor)


def _replace_config(path, parent, original, document):
    """Atomically replace a config whose bytes still equal ``original``; otherwise change nothing."""
    staging = _stage_config(parent, path.name, document)
    try:
        _no_symlinks(path)
        _private(path)
        # Compare-and-swap on the bytes read by the caller: a concurrent edit is
        # kept and reported rather than overwritten by a stale decision.
        if path.read_bytes() != original:
            raise StateError("config changed during the rewrite; no replacement made")
        os.replace(str(staging), str(path))
        staging = None
        _fsync_directory(parent)
        _private(path)
    finally:
        if staging is not None:
            staging.unlink(missing_ok=True)


def initialize_config(account, config_path=None, manager=None, manager_principal=None, profile=None):
    """Create a private owner config atomically, without replacing another owner or manager."""
    if account is None:
        raise StateError("init requires an explicit --account owner principal")
    principal = resolve_account(account)
    if manager is None and (manager_principal is not None or profile is not None):
        raise StateError("a manager principal or profile requires a manager object id")
    manager = validate_manager_object_id(manager) if manager is not None else None
    if manager_principal is not None:
        _validate_principal(manager_principal, "manager principal")
    if profile is not None:
        _validate_profile(profile)
    path, explicit = _config_path(config_path)
    staging = None
    try:
        _no_symlinks(path)
        parent = _safe_directory(path.parent, explicit_override=explicit)
        path = parent / path.name
        _mkdir_private(parent)

        def verify_existing():
            _, existing = _existing_config(path, principal)
            bound = existing.get("manager")
            if manager is not None:
                if bound is None:
                    raise StateError("existing config is version 1; run margo_store.py migrate-config explicitly")
                if bound["object_id"] != manager:
                    raise StateError("existing config has a different manager; refusing to replace it")
                if ((manager_principal is not None and bound["principal"] != manager_principal)
                        or (profile is not None and existing["profile"] != profile)):
                    raise StateError("existing config has a different manager binding; refusing to replace it")
            return existing

        if path.exists():
            return _config_summary(path, verify_existing(), created=False)
        document = _config_document(principal, manager, manager_principal,
                                    profile if profile is not None else "remote-host" if manager else None)
        staging = _stage_config(parent, path.name, document)
        created = True
        try:
            # An atomic no-replace link prevents concurrent initializers from
            # silently changing the account selected by another initializer.
            os.link(str(staging), str(path))
        except FileExistsError:
            created = False
        staging.unlink()
        staging = None
        existing = verify_existing()
        _fsync_directory(parent)
        return _config_summary(path, existing, created=created)
    except (OSError, StateError) as exc:
        if isinstance(exc, StateError):
            raise
        raise StateError("configuration unavailable; no existing config replaced") from exc
    finally:
        if staging is not None:
            staging.unlink(missing_ok=True)


def migrate_config(account, manager, config_path=None, manager_principal=None, profile="remote-host"):
    """Explicitly upgrade a version-1 config to version 2 with one bound manager; replay-safe."""
    if account is None or manager is None:
        raise StateError("migrate-config requires an explicit --account and --manager")
    principal = resolve_account(account)
    manager = validate_manager_object_id(manager)
    if manager_principal is not None:
        _validate_principal(manager_principal, "manager principal")
    _validate_profile(profile)
    path, explicit = _config_path(config_path)
    try:
        _no_symlinks(path)
        parent = _safe_directory(path.parent, explicit_override=explicit)
        path = parent / path.name
        if not path.exists():
            raise StateError("no existing config to migrate; run margo_store.py init explicitly")
        original, existing = _existing_config(path, principal)
        bound = existing.get("manager")
        if bound is not None:
            if bound["object_id"] != manager:
                raise StateError("existing config has a different manager; refusing to replace it")
            if bound["principal"] != manager_principal or existing["profile"] != profile:
                raise StateError("existing config has a different manager binding; refusing to replace it")
            return _config_summary(path, existing, migrated=False,
                                   from_version=CONFIG_VERSION, to_version=CONFIG_VERSION)
        document = _config_document(principal, manager, manager_principal, profile)
        _replace_config(path, parent, original, document)
        return _config_summary(path, document, status="migrated", migrated=True,
                               from_version=1, to_version=CONFIG_VERSION)
    except (OSError, StateError) as exc:
        if isinstance(exc, StateError):
            raise
        raise StateError("configuration unavailable; no existing config replaced") from exc


def rebind_manager(account, current_manager, new_manager, config_path=None, new_principal=None, profile=None):
    """Replace the bound manager; the caller must present the current object ID (privileged by context)."""
    if account is None or current_manager is None or new_manager is None:
        raise StateError("rebind-manager requires an explicit --account, --current-manager and --manager")
    principal = resolve_account(account)
    current = validate_manager_object_id(current_manager)
    new = validate_manager_object_id(new_manager)
    if new_principal is not None:
        _validate_principal(new_principal, "manager principal")
    if profile is not None:
        _validate_profile(profile)
    path, explicit = _config_path(config_path)
    try:
        _no_symlinks(path)
        parent = _safe_directory(path.parent, explicit_override=explicit)
        path = parent / path.name
        if not path.exists():
            raise StateError("no existing config to rebind; run margo_store.py init explicitly")
        original, existing = _existing_config(path, principal)
        bound = existing.get("manager")
        if bound is None:
            raise StateError("existing config is version 1; run margo_store.py migrate-config explicitly")
        if bound["object_id"] != current:
            raise StateError("current manager does not match the bound manager; refusing to rebind")
        # A new manager never inherits the old manager's principal; rebinding the
        # same manager keeps it unless the caller replaces it.
        kept = bound["principal"] if new == current else None
        document = _config_document(principal, new, new_principal if new_principal is not None else kept,
                                    profile if profile is not None else existing["profile"])
        if document == existing:
            return _config_summary(path, existing, rebound=False)
        _replace_config(path, parent, original, document)
        return _config_summary(path, document, status="rebound", rebound=True)
    except (OSError, StateError) as exc:
        if isinstance(exc, StateError):
            raise
        raise StateError("configuration unavailable; no existing config replaced") from exc


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    config_help = "private config path; otherwise MARGO_CONFIG or ~/.copilot/margo/config.json"
    principal_help = "the manager's principal, kept for display; it is never an authority by itself"
    init = commands.add_parser("init", help="explicitly save the ledger owner in private config; no network calls")
    init.add_argument("--account", required=True, help="explicit ledger owner principal")
    init.add_argument("--manager", help="bind exactly one manager by Entra object ID (writes a version-2 config)")
    init.add_argument("--manager-principal", help=principal_help)
    init.add_argument("--profile", choices=CONFIG_PROFILES,
                      help="deployment profile; requires --manager (default remote-host)")
    init.add_argument("--config", help=config_help)
    migrate = commands.add_parser("migrate-config",
                                  help="explicitly upgrade a version-1 config to version 2 with one bound manager")
    migrate.add_argument("--account", required=True, help="the configured ledger owner principal; must match the file")
    migrate.add_argument("--manager", required=True, help="Entra object ID of the single bound manager")
    migrate.add_argument("--manager-principal", help=principal_help)
    migrate.add_argument("--profile", choices=CONFIG_PROFILES, default="remote-host",
                         help="deployment profile (default remote-host)")
    migrate.add_argument("--config", help=config_help)
    rebind = commands.add_parser("rebind-manager",
                                 help="replace the bound manager; requires the currently bound object ID")
    rebind.add_argument("--account", required=True, help="the configured ledger owner principal; must match the file")
    rebind.add_argument("--current-manager", required=True, help="object ID bound today; a mismatch refuses the change")
    rebind.add_argument("--manager", required=True, help="Entra object ID of the manager to bind")
    rebind.add_argument("--manager-principal", help=principal_help)
    rebind.add_argument("--profile", choices=CONFIG_PROFILES, help="change the deployment profile; otherwise kept")
    rebind.add_argument("--config", help=config_help)
    args = parser.parse_args(argv)
    try:
        if args.command == "init":
            result = initialize_config(args.account, args.config, manager=args.manager,
                                       manager_principal=args.manager_principal, profile=args.profile)
        elif args.command == "migrate-config":
            result = migrate_config(args.account, args.manager, args.config,
                                    manager_principal=args.manager_principal, profile=args.profile)
        else:
            result = rebind_manager(args.account, args.current_manager, args.manager, args.config,
                                    new_principal=args.manager_principal, profile=args.profile)
        print(canonical_json(result))
        return 0
    except StateError as exc:
        print("ERROR: " + str(exc), file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())

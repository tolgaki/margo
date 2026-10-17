"""One writer at a time: the instance's private state, shared through a leased Blob bundle.

Why. A Foundry hosted agent serves every hired instance, and its sessions follow conversations,
so the manager's chat and a colleague's message can run in different sandboxes with different
``$HOME`` directories. A SQLite file in ``$HOME`` alone would fork. Instead each turn:

1. takes an exclusive lease on the instance's state blob (renewed while the turn runs);
2. installs the newest bundle if the local copy is stale or missing;
3. runs the turn against the local SQLite file, with no other session able to write;
4. uploads a verified snapshot under the same lease, then releases it.

The bundle is a zip of ``margo.sqlite3`` (an online-backup snapshot), the manager's private
``preferences.md`` if present, and a manifest binding it to the account. A foreign, tampered or
corrupt bundle is refused and nothing local is replaced.

``store.lease(name)`` must return a context manager whose object offers ``read() -> (bytes|None,
etag)``, ``write(bytes) -> etag`` and ``renew()``; ``store.put(name, bytes)`` writes an unleased
copy. The host implements this on Azure Blob Storage; tests use an in-memory store.
"""

import asyncio
import hashlib
import io
import json
import os
import sqlite3
import tempfile
import zipfile
from datetime import datetime, timezone
from pathlib import Path

BUNDLE = "state.zip"
MARKER = ".synced-etag"


class SyncError(RuntimeError):
    """State could not be loaded or saved safely; the turn must not proceed or claim success."""


def _sha(data):
    return hashlib.sha256(data).hexdigest()


def _check_db(path, account):
    connection = sqlite3.connect(str(path))
    try:
        if connection.execute("PRAGMA quick_check").fetchone()[0] != "ok":
            raise SyncError("state snapshot failed its integrity check")
        tables = {row[0] for row in connection.execute("SELECT name FROM sqlite_master WHERE type='table'")}
        if "margo_meta" not in tables:
            raise SyncError("state snapshot is not a Margo store")
        if dict(connection.execute("SELECT key,value FROM margo_meta")).get("account") != account:
            raise SyncError("state belongs to another account; refusing it")
    finally:
        connection.close()


def container_name(account):
    """A per-instance, per-principal Blob container name (lowercase, 3-63 chars)."""
    return "margo-" + _sha(account.encode("utf-8"))[:40]


class StateSync:
    def __init__(self, database, store, *, account, preferences=None, renew_seconds=20):
        self.database, self.store, self.account = Path(database), store, account
        self.preferences = Path(preferences) if preferences else None
        self.renew_seconds = renew_seconds
        self.marker = self.database.parent / MARKER

    # -- bundle ---------------------------------------------------------------------------

    def _pack(self, now):
        with tempfile.TemporaryDirectory() as scratch:
            snapshot = Path(scratch) / "snapshot.sqlite3"
            source, target = sqlite3.connect(str(self.database), timeout=10), sqlite3.connect(str(snapshot))
            try:
                source.backup(target)
            finally:
                target.close()
                source.close()
            _check_db(snapshot, self.account)
            database = snapshot.read_bytes()
        manifest = {"account_sha256": _sha(self.account.encode("utf-8")), "database_sha256": _sha(database),
                    "taken_at": now.isoformat(timespec="seconds"), "format": 1}
        buffer = io.BytesIO()
        with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED) as bundle:
            bundle.writestr("manifest.json", json.dumps(manifest, sort_keys=True))
            bundle.writestr("margo.sqlite3", database)
            if self.preferences and self.preferences.is_file():
                bundle.writestr("preferences.md", self.preferences.read_bytes())
        return buffer.getvalue()

    def _install(self, data):
        try:
            with zipfile.ZipFile(io.BytesIO(data)) as bundle:
                manifest = json.loads(bundle.read("manifest.json"))
                database = bundle.read("margo.sqlite3")
                preferences = bundle.read("preferences.md") if "preferences.md" in bundle.namelist() else None
        except (zipfile.BadZipFile, KeyError, ValueError) as exc:
            raise SyncError("state bundle is unreadable") from exc
        if manifest.get("account_sha256") != _sha(self.account.encode("utf-8")):
            raise SyncError("state bundle belongs to another account; refusing it")
        if manifest.get("database_sha256") != _sha(database):
            raise SyncError("state bundle content does not match its manifest")
        self.database.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
        os.chmod(str(self.database.parent), 0o700)
        descriptor, staging = tempfile.mkstemp(dir=str(self.database.parent), prefix=".install-")
        try:
            with os.fdopen(descriptor, "wb") as stream:
                stream.write(database)
                stream.flush()
                os.fsync(stream.fileno())
            os.chmod(staging, 0o600)
            _check_db(staging, self.account)
            for suffix in ("-journal", "-wal", "-shm"):
                sidecar = Path(str(self.database) + suffix)
                if sidecar.exists():
                    sidecar.unlink()
            os.replace(staging, str(self.database))
        finally:
            if os.path.exists(staging):
                os.unlink(staging)
        if self.preferences is not None:
            if preferences is None:
                if self.preferences.exists():
                    self.preferences.unlink()
            else:
                self.preferences.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
                self.preferences.write_bytes(preferences)
                os.chmod(str(self.preferences), 0o600)
        return manifest

    def _local_etag(self):
        try:
            return self.marker.read_text(encoding="utf-8").strip() if self.database.exists() else None
        except OSError:
            return None

    def _set_local_etag(self, etag):
        self.marker.write_text(etag or "", encoding="utf-8")
        os.chmod(str(self.marker), 0o600)

    # -- turn ---------------------------------------------------------------------------

    async def run(self, work, now=None):
        """Run ``await work()`` while holding the instance lease; return its result.

        If the turn or the upload fails, the local copy is marked stale so the next turn
        reinstalls the shared state instead of building on changes that were never saved.
        """
        now = now or datetime.now(timezone.utc)
        holder = self.store.lease(BUNDLE)
        lease = await asyncio.to_thread(holder.__enter__)
        renewer = asyncio.ensure_future(self._renew(lease))
        saved = False
        try:
            data, etag = await asyncio.to_thread(lease.read)
            restored = None
            if data and etag != self._local_etag():
                restored = await asyncio.to_thread(self._install, data)
                self._set_local_etag(etag)
            result = await work()
            if renewer.done() and not renewer.cancelled() and renewer.exception() is not None:
                raise SyncError("the state lease could not be renewed; nothing was saved")
            if self.database.exists():
                bundle = await asyncio.to_thread(self._pack, now)
                new_etag = await asyncio.to_thread(lease.write, bundle)
                self._set_local_etag(new_etag)
                saved = True
                try:
                    await asyncio.to_thread(self.store.put, "daily/%s.zip" % now.strftime("%Y-%m-%d"), bundle)
                except Exception:  # the leased copy is saved; a missed daily copy is not data loss
                    pass
            else:
                saved = True
            return {"result": result, "restored": restored}
        finally:
            try:
                renewer.cancel()
                try:
                    await renewer
                except (asyncio.CancelledError, Exception):
                    pass
                if not saved and self.marker.exists():
                    self.marker.unlink()
            finally:
                try:
                    await asyncio.to_thread(holder.__exit__, None, None, None)
                except Exception:
                    pass  # an unreleased lease expires on its own

    async def _renew(self, lease):
        while True:
            await asyncio.sleep(self.renew_seconds)
            await asyncio.to_thread(lease.renew)

"""Azure Blob implementation of the leased state store used by ``StateSync``.

Constructing a store makes no network call. Every call that does (``put`` and the lease) is
made from a worker thread, because ``StateSync`` runs them through ``asyncio.to_thread``.
"""

import threading
import time

from azure.core.exceptions import AzureError, HttpResponseError, ResourceExistsError, ResourceNotFoundError
from azure.storage.blob import BlobServiceClient

from ..statesync import SyncError

LEASE_SECONDS = 60
WAIT_SECONDS = 120


class AzureBlobLeaseStore:
    """One private container per instance-and-manager; created on first use."""

    def __init__(self, service, container):
        self.container = service.get_container_client(container)
        self._ready = False
        self._lock = threading.Lock()

    @classmethod
    def factory(cls, account_url, credential):
        """Return ``store_for(container)``, which reuses one store per container."""
        service = BlobServiceClient(account_url=account_url, credential=credential)
        stores, lock = {}, threading.Lock()

        def store_for(container):
            with lock:
                if container not in stores:
                    stores[container] = cls(service, container)
                return stores[container]

        return store_for

    def ensure_container(self):
        with self._lock:
            if self._ready:
                return
            try:
                self.container.create_container()
            except ResourceExistsError:
                pass
            self._ready = True

    def forget_container(self):
        """The container was removed since it was created here; create it again on next use."""
        with self._lock:
            self._ready = False

    def lease(self, name):
        return _Lease(self, self.container.get_blob_client(name))

    def put(self, name, data):
        self.ensure_container()
        self.container.upload_blob(name, data, overwrite=True)


class _Lease:
    def __init__(self, store, blob):
        self.store, self.blob, self.lease = store, blob, None

    def __enter__(self):
        try:
            self._bootstrap()
            deadline = time.monotonic() + WAIT_SECONDS
            while True:
                try:
                    self.lease = self.blob.acquire_lease(lease_duration=LEASE_SECONDS)
                    return self
                except HttpResponseError as exc:
                    if exc.status_code == 409 and time.monotonic() < deadline:
                        time.sleep(1)
                        continue
                    raise
        except AzureError as exc:
            raise SyncError("instance state is busy or unavailable") from exc

    def _bootstrap(self, retry=True):
        """A lease needs an existing blob. While another sandbox holds the lease, Azure can refuse
        this create with 412 (no lease ID) instead of 409 (exists); either way the blob exists."""
        self.store.ensure_container()
        try:
            self.blob.upload_blob(b"", overwrite=False)
        except ResourceExistsError:
            pass
        except ResourceNotFoundError:
            if not retry:
                raise
            self.store.forget_container()
            self._bootstrap(retry=False)
        except HttpResponseError as exc:
            if exc.status_code != 412:
                raise

    def read(self):
        try:
            downloader = self.blob.download_blob(lease=self.lease)
            data = downloader.readall()
        except ResourceNotFoundError:
            return None, None
        except AzureError as exc:
            raise SyncError("could not read instance state") from exc
        return (data or None), downloader.properties.etag

    def write(self, data):
        try:
            return self.blob.upload_blob(data, overwrite=True, lease=self.lease)["etag"]
        except AzureError as exc:
            raise SyncError("could not save instance state; the lease may have been lost") from exc

    def renew(self):
        self.lease.renew()

    def __exit__(self, *exc):
        try:
            if self.lease is not None:
                self.lease.release()
        except AzureError:
            pass  # an expired lease frees itself after LEASE_SECONDS

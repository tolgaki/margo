"""Azure Blob implementation of the leased state store used by ``StateSync``."""

import time

from azure.core.exceptions import HttpResponseError, ResourceExistsError, ResourceNotFoundError
from azure.storage.blob import BlobServiceClient

from ..statesync import SyncError

LEASE_SECONDS = 60
WAIT_SECONDS = 120


class AzureBlobLeaseStore:
    """One private container per instance-and-manager; created on first use."""

    def __init__(self, service, container):
        self.container = service.get_container_client(container)
        try:
            self.container.create_container()
        except ResourceExistsError:
            pass

    @classmethod
    def factory(cls, account_url, credential):
        service = BlobServiceClient(account_url=account_url, credential=credential)
        return lambda container: cls(service, container)

    def lease(self, name):
        return _Lease(self.container.get_blob_client(name))

    def put(self, name, data):
        self.container.upload_blob(name, data, overwrite=True)


class _Lease:
    def __init__(self, blob):
        self.blob, self.lease = blob, None

    def __enter__(self):
        try:
            self.blob.upload_blob(b"", overwrite=False)  # a lease needs an existing blob
        except ResourceExistsError:
            pass
        deadline = time.monotonic() + WAIT_SECONDS
        while True:
            try:
                self.lease = self.blob.acquire_lease(lease_duration=LEASE_SECONDS)
                return self
            except HttpResponseError as exc:
                if exc.status_code == 409 and time.monotonic() < deadline:
                    time.sleep(1)
                    continue
                raise SyncError("instance state is busy or unavailable") from exc

    def read(self):
        try:
            downloader = self.blob.download_blob(lease=self.lease)
        except ResourceNotFoundError:
            return None, None
        data = downloader.readall()
        return (data or None), downloader.properties.etag

    def write(self, data):
        try:
            return self.blob.upload_blob(data, overwrite=True, lease=self.lease)["etag"]
        except HttpResponseError as exc:
            raise SyncError("could not save instance state; the lease may have been lost") from exc

    def renew(self):
        self.lease.renew()

    def __exit__(self, *exc):
        try:
            if self.lease is not None:
                self.lease.release()
        except HttpResponseError:
            pass  # an expired lease frees itself after LEASE_SECONDS

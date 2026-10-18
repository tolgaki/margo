"""The provisioner job: drain the queue, create or retire instances, keep the route table.

Runs as a scheduled Container Apps job with its own identity, which may deploy into one resource
group and assign the four roles an instance needs. It never reads any instance's state.

    python -m margo_autopilot.provision run
    python -m margo_autopilot.provision redeploy-all
    python -m margo_autopilot.provision enqueue ensure|suspend|retire AGENT_IDENTITY_ID AGENT_USER_ID [--redeploy]
"""

import json
import logging
import os
import sys
from pathlib import Path

from .core import Provisioner

APP_API = "2024-03-01"
IDENTITY_API = "2023-01-31"
VISIBILITY_SECONDS = 900

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s | %(message)s")
logger = logging.getLogger("margo.provision")


def _credential():
    from azure.identity import DefaultAzureCredential, ManagedIdentityCredential

    client_id = os.getenv("AZURE_CLIENT_ID")
    return ManagedIdentityCredential(client_id=client_id) if client_id else DefaultAzureCredential()


class AzureCloud:
    def __init__(self, credential, *, subscription_id, resource_group, routes_url):
        from azure.mgmt.authorization import AuthorizationManagementClient
        from azure.mgmt.resource import ResourceManagementClient
        from azure.storage.blob import BlobClient

        self.resources = ResourceManagementClient(credential, subscription_id)
        self.authorization = AuthorizationManagementClient(credential, subscription_id)
        self.routes = BlobClient.from_blob_url(routes_url, credential=credential)
        self.subscription_id, self.resource_group = subscription_id, resource_group

    def _id(self, provider, name):
        return "/subscriptions/%s/resourceGroups/%s/providers/%s/%s" % (
            self.subscription_id, self.resource_group, provider, name)

    def deploy(self, name, template, parameters):
        body = {"properties": {"mode": "Incremental", "template": template, "parameters": parameters}}
        self.resources.deployments.begin_create_or_update(self.resource_group, name, body).result()

    def delete_app(self, name):
        from azure.core.exceptions import ResourceNotFoundError

        try:
            self.resources.resources.begin_delete_by_id(self._id("Microsoft.App/containerApps", name), APP_API).result()
        except ResourceNotFoundError:
            pass

    def delete_identity(self, name):
        """Remove the identity's role assignments first, so none is left pointing at nothing."""
        from azure.core.exceptions import ResourceNotFoundError

        identity_id = self._id("Microsoft.ManagedIdentity/userAssignedIdentities", name)
        try:
            principal = self.resources.resources.get_by_id(identity_id, IDENTITY_API).properties["principalId"]
        except ResourceNotFoundError:
            return
        scope = "/subscriptions/%s/resourceGroups/%s" % (self.subscription_id, self.resource_group)
        for assignment in self.authorization.role_assignments.list_for_scope(
                scope, filter="principalId eq '%s'" % principal):
            self.authorization.role_assignments.delete_by_id(assignment.id)
        self.resources.resources.begin_delete_by_id(identity_id, IDENTITY_API).result()

    def load_routes(self):
        from azure.core.exceptions import ResourceNotFoundError

        try:
            download = self.routes.download_blob()
            return download.readall().decode("utf-8"), download.properties.etag
        except ResourceNotFoundError:
            return "", None

    def save_routes(self, text, etag):
        from azure.core import MatchConditions
        from azure.core.exceptions import ResourceExistsError, ResourceModifiedError

        try:
            if etag is None:
                self.routes.upload_blob(text.encode("utf-8"), overwrite=False)
            else:
                self.routes.upload_blob(text.encode("utf-8"), overwrite=True, etag=etag,
                                        match_condition=MatchConditions.IfNotModified)
            return True
        except (ResourceModifiedError, ResourceExistsError):
            return False


def load_config():
    """Static values from the deployment, plus the ones set after the image is built."""
    config = json.loads(os.environ["MARGO_PROVISIONER_CONFIG"])
    for name, variable in (("image", "MARGO_INSTANCE_IMAGE"), ("storageRoleDefinitionId", "MARGO_STORAGE_ROLE_ID"),
                           ("pullRoleDefinitionId", "MARGO_PULL_ROLE_ID"), ("modelRoleDefinitionId", "MARGO_MODEL_ROLE_ID"),
                           ("speechRoleDefinitionId", "MARGO_SPEECH_ROLE_ID"),
                           ("blueprintClientId", "MARGO_BLUEPRINT_CLIENT_ID")):
        if os.getenv(variable):
            config[name] = os.environ[variable]
    return config


def _queue(credential):
    from azure.storage.queue import QueueClient

    return QueueClient.from_queue_url(os.environ["MARGO_PROVISION_QUEUE_URL"], credential=credential)


def run():
    credential = _credential()
    template = json.loads(Path(os.getenv("MARGO_INSTANCE_TEMPLATE", "/app/autopilot/infra/instance.json"))
                          .read_text(encoding="utf-8"))
    cloud = AzureCloud(credential, subscription_id=os.environ["AZURE_SUBSCRIPTION_ID"],
                       resource_group=os.environ["AZURE_RESOURCE_GROUP"], routes_url=os.environ["MARGO_ROUTES_URL"])
    provisioner = Provisioner(cloud=cloud, config=load_config(), template=template, log=logger.info)
    queue = _queue(credential)
    handled = 0
    for message in queue.receive_messages(messages_per_page=16, visibility_timeout=VISIBILITY_SECONDS):
        outcome = provisioner.handle(message.content, dequeue_count=message.dequeue_count or 1)
        if outcome in ("done", "dropped"):
            queue.delete_message(message)
        handled += 1
    logger.info("processed %d message(s)", handled)
    return 0


def enqueue(action, agent_identity_id, agent_user_id, redeploy=False):
    from ..instances import check_message

    message = {"action": action, "agent_identity_id": agent_identity_id, "agent_user_id": agent_user_id,
               "event": "operator"}
    if redeploy:
        message["redeploy"] = True  # an ensure that redeploys even a ready instance (new image or voice)
    check_message(message)
    _queue(_credential()).send_message(json.dumps(message, sort_keys=True))
    logger.info("queued %s", action)
    return 0


def redeploy_all():
    """Queue a redeploy for every ready instance, after the image or voice settings change."""
    from ..instances import READY, RouteTable

    credential = _credential()
    cloud = AzureCloud(credential, subscription_id=os.environ["AZURE_SUBSCRIPTION_ID"],
                       resource_group=os.environ["AZURE_RESOURCE_GROUP"], routes_url=os.environ["MARGO_ROUTES_URL"])
    queue = _queue(credential)
    count = 0
    for entry in RouteTable.loads(cloud.load_routes()[0]).routes.values():
        if entry.get("state") == READY:
            queue.send_message(json.dumps({"action": "ensure", "agent_identity_id": entry["agent_identity_id"],
                                           "agent_user_id": entry["agent_user_id"], "event": "operator",
                                           "redeploy": True}, sort_keys=True))
            count += 1
    logger.info("queued %d redeploy(s)", count)
    return 0


def main(argv=None):
    args = sys.argv[1:] if argv is None else argv
    if args[:1] == ["run"]:
        return run()
    if args[:1] == ["redeploy-all"]:
        return redeploy_all()
    if len(args) in (4, 5) and args[0] == "enqueue" and args[1] in ("ensure", "suspend", "retire") \
            and args[4:] in ([], ["--redeploy"]):
        return enqueue(*args[1:4], redeploy=bool(args[4:]))
    print("\n".join(line.strip() for line in __doc__.strip().splitlines()[-3:]), file=sys.stderr)
    return 2

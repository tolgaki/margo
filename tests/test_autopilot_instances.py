"""Offline tests for one-container-per-instance hosting: names, routes, router and provisioner."""

import importlib
import json
import re
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "skills" / "chief-of-staff" / "scripts"))
sys.path.insert(0, str(ROOT / "autopilot" / "src"))
instances = importlib.import_module("margo_autopilot.instances")
router = importlib.import_module("margo_autopilot.router.core")
provision = importlib.import_module("margo_autopilot.provision.core")

IDENTITY, USER = "agent-identity-1", "margo-user-1"
CONFIG = {name: "fictional-%s" % name.lower() for name in instances.REQUIRED_CONFIG}
CONFIG.update(environmentDomain="calmforest-1a2b3c.example.com", minReplicas=0)


def activity(**overrides):
    value = {"type": "message", "recipient": {"agenticAppId": IDENTITY, "agenticUserId": USER},
             "from": {"aadObjectId": "principal-1"}, "text": "Brief me"}
    value.update(overrides)
    return value


class NamesAndRoutes(unittest.TestCase):
    def test_keys_are_stable_opaque_and_valid_azure_names(self):
        key = instances.instance_key(IDENTITY, USER)
        self.assertEqual(key, instances.instance_key(IDENTITY, USER))
        self.assertNotEqual(key, instances.instance_key(IDENTITY, "margo-user-2"))
        self.assertRegex(key, r"^[0-9a-f]{20}$")
        names = instances.resource_names(key)
        self.assertRegex(names["app"], r"^[a-z][a-z0-9-]{1,30}[a-z0-9]$")
        self.assertLessEqual(len(names["app"]), 32)
        self.assertRegex(names["container"], r"^[a-z0-9][a-z0-9-]{2,62}$")
        for bad in (("same", "same"), ("", USER), ("a b", USER), (None, USER)):
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                instances.instance_key(*bad)
        with self.assertRaises(instances.RouteError):
            instances.resource_names("../escape")

    def test_route_table_round_trip_and_validation(self):
        table = instances.RouteTable()
        key = instances.instance_key(IDENTITY, USER)
        table.put(key, agent_identity_id=IDENTITY, agent_user_id=USER, state=instances.READY,
                  url=instances.internal_url(key, CONFIG["environmentDomain"]))
        again = instances.RouteTable.loads(table.dumps())
        self.assertEqual(again.resolve(IDENTITY, USER)[1]["state"], instances.READY)
        self.assertEqual(again.get(key)["revision"], 1)
        again.put(key, agent_identity_id=IDENTITY, agent_user_id=USER, state=instances.RETIRED)
        self.assertEqual((again.get(key)["revision"], again.get(key)["url"]), (2, table.get(key)["url"]))
        self.assertIn("retired_at", again.get(key))
        for text in ("{", json.dumps({"schema_version": 9, "routes": {}}),
                     json.dumps({"schema_version": 1, "routes": {"x": {"state": "ready"}}}),
                     json.dumps({"schema_version": 1, "routes": {key: {"state": "dancing"}}})):
            with self.subTest(text=text), self.assertRaises(instances.RouteError):
                instances.RouteTable.loads(text)
        self.assertEqual(instances.RouteTable.loads("").routes, {})

    def test_lifecycle_events_map_to_provisioning_actions(self):
        cases = {"agenticUserIdentityCreated": "ensure", "agenticUserManagerUpdated": "ensure",
                 "agenticUserDisabled": "suspend", "agenticUserDeleted": "retire"}
        for event, action in cases.items():
            message = instances.lifecycle_message(activity(type="event", name="agentLifecycle", valueType=event))
            self.assertEqual((message["action"], message["agent_user_id"]), (action, USER))
        self.assertIsNone(instances.lifecycle_message(activity()))
        self.assertIsNone(instances.lifecycle_message(activity(name="agentLifecycle", valueType="somethingNew")))


class Plans(unittest.TestCase):
    def test_plans_are_idempotent_and_never_delete_storage(self):
        ensure = {"action": "ensure"}
        self.assertEqual(instances.plan(ensure, None), ["route_provisioning", "deploy", "route_ready"])
        self.assertEqual(instances.plan(ensure, {"state": "ready"}), [])
        self.assertEqual(instances.plan(dict(ensure, redeploy=True), {"state": "ready"})[1], "deploy")
        self.assertEqual(instances.plan(ensure, {"state": "failed"})[1], "deploy")
        self.assertEqual(instances.plan({"action": "suspend"}, {"state": "ready"}), ["route_suspended"])
        self.assertEqual(instances.plan({"action": "suspend"}, None), [])
        retire = instances.plan({"action": "retire"}, {"state": "ready"})
        self.assertEqual(retire, ["route_retired", "delete_app", "delete_identity"])
        self.assertFalse(any("storage" in step or "container" in step for step in retire))
        self.assertEqual(instances.plan({"action": "retire"}, {"state": "retired"}), [])

    def test_deployment_parameters_need_full_config_and_one_replica(self):
        key = instances.instance_key(IDENTITY, USER)
        message = {"action": "ensure", "agent_identity_id": IDENTITY, "agent_user_id": USER}
        parameters = instances.deployment_parameters(key, message, CONFIG)
        self.assertEqual(parameters["appName"]["value"], "margo-" + key)
        self.assertEqual(parameters["agentUserId"]["value"], USER)
        self.assertNotIn("environmentDomain", parameters)
        with self.assertRaises(instances.RouteError):
            instances.deployment_parameters(key, message, {})
        with self.assertRaises(instances.RouteError):
            instances.deployment_parameters(key, message, dict(CONFIG, minReplicas=3))

    def test_parameters_match_the_compiled_template_and_its_source(self):
        template = json.loads((ROOT / "autopilot" / "infra" / "instance.json").read_text(encoding="utf-8"))
        source = (ROOT / "autopilot" / "infra" / "instance.bicep").read_text(encoding="utf-8")
        declared = set(re.findall(r"^param (\w+)", source, re.MULTILINE))
        self.assertEqual(set(template["parameters"]), declared, "rebuild instance.json from instance.bicep")
        key = instances.instance_key(IDENTITY, USER)
        sent = set(instances.deployment_parameters(key, {"agent_identity_id": IDENTITY, "agent_user_id": USER},
                                                   CONFIG))
        required = {name for name, spec in template["parameters"].items() if "defaultValue" not in spec}
        self.assertEqual(sorted(required - sent), [])
        self.assertEqual(sorted(sent - set(template["parameters"])), [])
        text = json.dumps(template)
        self.assertIn('"maxReplicas": 1', text)
        self.assertIn('"external": false', text)
        self.assertNotIn("storageAccounts/delete", text)


class RouterDecisions(unittest.TestCase):
    def table(self, state=None):
        table = instances.RouteTable()
        if state:
            key = instances.instance_key(IDENTITY, USER)
            table.put(key, agent_identity_id=IDENTITY, agent_user_id=USER, state=state,
                      url=instances.internal_url(key, CONFIG["environmentDomain"]))
        return table

    def test_activities_go_to_their_own_instance_only(self):
        decision = router.decide(activity(), self.table(instances.READY))
        self.assertEqual(decision["kind"], "forward")
        self.assertIn(".internal.calmforest-1a2b3c.example.com", decision["url"])
        other = router.decide(activity(recipient={"agenticAppId": IDENTITY, "agenticUserId": "margo-user-2"}),
                              self.table(instances.READY))
        self.assertEqual(other["kind"], "provision")
        self.assertNotEqual(other["key"], decision["key"])

    def test_unknown_waiting_stopped_and_malformed(self):
        self.assertEqual(router.decide(activity(), self.table())["message"]["action"], "ensure")
        self.assertEqual(router.decide(activity(), self.table(instances.FAILED))["kind"], "provision")
        self.assertEqual(router.decide(activity(), self.table(instances.PROVISIONING))["status"], 503)
        for state in (instances.SUSPENDED, instances.RETIRED):
            self.assertEqual(router.decide(activity(), self.table(state))["kind"], "ignore")
        self.assertEqual(router.decide(activity(recipient={}), self.table())["status"], 400)
        self.assertEqual(router.decide(activity(recipient={"agenticAppId": "x", "agenticUserId": "x"}),
                                       self.table())["status"], 400)
        lifecycle = router.decide(activity(type="event", name="agentLifecycle", valueType="agenticUserDeleted"),
                                  self.table(instances.READY))
        self.assertEqual((lifecycle["kind"], lifecycle["message"]["action"]), ("lifecycle", "retire"))

    def test_voice_paths_are_confined_to_one_ready_instance(self):
        key = instances.instance_key(IDENTITY, USER)
        target = router.voice_target("/i/%s/voice/ws" % key, self.table(instances.READY))
        self.assertTrue(target["url"].endswith("/voice/ws"))
        self.assertEqual(router.voice_target("/i/%s/voice" % key, self.table(instances.READY))["url"][-7:], "/voice/")
        for path in ("/i/%s/voice/../admin" % key, "/i/%s/voice//x" % key, "/i/zz/voice/", "/voice/ws",
                     "/i/%s/voice/a?b" % key):
            with self.subTest(path=path):
                self.assertEqual(router.voice_target(path, self.table(instances.READY))["status"], 404)
        self.assertEqual(router.voice_target("/i/%s/voice/" % key, self.table(instances.PROVISIONING))["status"], 503)
        self.assertEqual(router.voice_target("/i/%s/voice/" % key, self.table(instances.RETIRED))["status"], 404)


class FakeCloud:
    def __init__(self, fail=None):
        self.text, self.etag, self.calls, self.fail, self.conflicts = "", None, [], fail, 0

    def deploy(self, name, template, parameters):
        self.calls.append(("deploy", name))
        if self.fail == "deploy":
            raise RuntimeError("deployment failed")

    def delete_app(self, name):
        self.calls.append(("delete_app", name))

    def delete_identity(self, name):
        self.calls.append(("delete_identity", name))

    def load_routes(self):
        return self.text, self.etag

    def save_routes(self, text, etag):
        if self.conflicts:
            self.conflicts -= 1
            self.etag = "etag-changed-%d" % self.conflicts
            return False
        if etag != self.etag:
            return False
        self.text, self.etag = text, "etag-%d" % (len(self.calls) + len(text))
        return True


class ProvisionerSteps(unittest.TestCase):
    def provisioner(self, cloud):
        self.log = []
        return provision.Provisioner(cloud=cloud, config=CONFIG, template={"resources": []}, log=self.log.append)

    def message(self, action):
        return json.dumps({"action": action, "agent_identity_id": IDENTITY, "agent_user_id": USER})

    def test_ensure_then_retire_keeps_storage_and_records_each_state(self):
        cloud = FakeCloud()
        provisioner = self.provisioner(cloud)
        self.assertEqual(provisioner.handle(self.message("ensure")), "done")
        key = instances.instance_key(IDENTITY, USER)
        self.assertEqual(instances.RouteTable.loads(cloud.text).get(key)["state"], "ready")
        self.assertEqual(provisioner.handle(self.message("ensure")), "done")
        self.assertEqual([call[0] for call in cloud.calls], ["deploy"])  # already ready: nothing redeployed
        self.assertEqual(provisioner.handle(self.message("retire")), "done")
        self.assertEqual([call[0] for call in cloud.calls], ["deploy", "delete_app", "delete_identity"])
        self.assertEqual(instances.RouteTable.loads(cloud.text).get(key)["state"], "retired")
        self.assertFalse(any("tok" in line.lower() for line in self.log))

    def test_failures_are_recorded_retried_then_dropped(self):
        cloud = FakeCloud(fail="deploy")
        provisioner = self.provisioner(cloud)
        self.assertEqual(provisioner.handle(self.message("ensure"), dequeue_count=1), "retry")
        key = instances.instance_key(IDENTITY, USER)
        entry = instances.RouteTable.loads(cloud.text).get(key)
        self.assertEqual((entry["state"], entry["error"]), ("failed", "RuntimeError"))
        self.assertEqual(provisioner.handle(self.message("ensure"), dequeue_count=provision.MAX_ATTEMPTS), "dropped")
        self.assertEqual(provisioner.handle("not json"), "dropped")
        self.assertEqual(provisioner.handle(json.dumps({"action": "explode"})), "dropped")

    def test_route_writes_survive_concurrent_changes(self):
        cloud = FakeCloud()
        cloud.conflicts = 2
        self.assertEqual(self.provisioner(cloud).handle(self.message("ensure")), "done")
        cloud = FakeCloud()
        cloud.conflicts = 50
        self.assertEqual(self.provisioner(cloud).handle(self.message("ensure")), "retry")


if __name__ == "__main__":
    unittest.main()

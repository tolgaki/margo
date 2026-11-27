"""Repository checks for the Margo Cloud deployment files (cloud/): what the scripts need, the infrastructure
provides; every role runs the C# image; the instance template matches its source; nothing of Foundry remains.

The C# runtime itself is tested by `dotnet test` in cloud/; these checks read only the deployment files, so they run
with the rest of the desktop suite (Python 3.9+, standard library).
"""

import json
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CLOUD = ROOT / "cloud"


def read(*parts):
    return CLOUD.joinpath(*parts).read_text(encoding="utf-8")


class Deployment(unittest.TestCase):
    def test_postprovision_needs_only_what_main_bicep_outputs(self):
        bicep = read("infra", "main.bicep")
        outputs = set(re.findall(r"^output (\w+)", bicep, re.MULTILINE))
        script = read("scripts", "postprovision.sh")
        needed = re.search(r"^need ((?:[^\n]*\\\n)*[^\n]*)", script, re.MULTILINE).group(1)
        names = set(re.findall(r"[A-Z][A-Z0-9_]+", needed))
        self.assertTrue(names)
        self.assertEqual(sorted(names - outputs), [])
        self.assertIn("MESSAGING_ENDPOINT", outputs)
        self.assertRegex(bicep, r"output MESSAGING_ENDPOINT string = '[^']*/api/messages'")

    def test_the_image_carries_a_pinned_copy_of_microsofts_workiq_skill(self):
        vendor = CLOUD / "vendor" / "workiq"
        source = json.loads((vendor / "source.json").read_text(encoding="utf-8"))
        self.assertEqual(source["repository"], "https://github.com/microsoft/work-iq")
        self.assertRegex(source["commit"], r"^[0-9a-f]{40}$")
        self.assertTrue((vendor / "plugins" / "workiq" / "skills" / "workiq" / "SKILL.md").is_file())
        self.assertIn("MIT License", (vendor / "LICENSE").read_text(encoding="utf-8"))
        skill = (vendor / "plugins" / "workiq" / "skills" / "workiq" / "SKILL.md").read_text(encoding="utf-8")
        self.assertTrue(skill.startswith("---\nname: workiq\n"))
        dockerfile = read("Dockerfile")
        self.assertIn("COPY cloud/vendor/workiq/plugins/workiq/skills/ /app/skills/", dockerfile)
        self.assertNotIn("vendor", (ROOT / ".dockerignore").read_text(encoding="utf-8"))

    def test_no_foundry_hosted_agent_remains(self):
        files = [CLOUD / "Dockerfile"]
        files += sorted((CLOUD / "infra").glob("*.bicep"))
        files += sorted((CLOUD / "scripts").glob("*.sh"))
        files += sorted(path for path in (CLOUD / "src").rglob("*.cs") if "bin" not in path.parts and "obj" not in path.parts)
        self.assertGreater(len(files), 20)
        for path in files:
            text = path.read_text(encoding="utf-8")
            for word in ("foundry", "IdentityProxyManager", "azure-ai-projects", "AIProjectClient"):
                self.assertNotIn(word.lower(), text.lower(), "%s mentions %s" % (path.relative_to(ROOT), word))

    def test_instance_template_matches_its_source_and_stays_bounded(self):
        template = json.loads(read("infra", "instance.json"))
        source = read("infra", "instance.bicep")
        declared = set(re.findall(r"^param (\w+)", source, re.MULTILINE))
        self.assertEqual(set(template["parameters"]), declared, "rebuild instance.json from instance.bicep")
        text = json.dumps(template)
        self.assertIn('"maxReplicas": 1', text)
        self.assertIn('"external": false', text)
        self.assertNotIn("storageAccounts/delete", text)
        env = re.findall(r"createObject\('name', '([A-Z0-9_]+)'", text)
        for name in re.findall(r"\{ name: '([A-Z0-9_]+)'", source):
            self.assertIn(name, env, "instance.json lacks %s; rebuild it from instance.bicep" % name)

    def test_every_role_runs_the_csharp_image(self):
        dockerfile = read("Dockerfile")
        main = read("infra", "main.bicep")
        instance = read("infra", "instance.bicep")
        template = json.loads(read("infra", "instance.json"))
        self.assertIn("command: [ 'dotnet', '/app/router/Margo.Cloud.Router.dll' ]", main)
        self.assertIn("command: [ 'dotnet', '/app/provisioner/Margo.Cloud.Provisioner.dll', 'run' ]", main)
        self.assertIn("command: [ 'dotnet', '/app/host/Margo.Cloud.Host.dll' ]", instance)
        containers = [container for resource in template["resources"]
                      for container in resource.get("properties", {}).get("template", {}).get("containers", [])]
        self.assertIn(["dotnet", "/app/host/Margo.Cloud.Host.dll"], [c.get("command") for c in containers])
        for app in ("host/Margo.Cloud.Host", "router/Margo.Cloud.Router", "provisioner/Margo.Cloud.Provisioner"):
            name = app.split("/")[1]
            self.assertRegex(dockerfile, r"dotnet publish src/%s/%s\.csproj \$publish -o /out/%s\b"
                             % (re.escape(name), re.escape(name), app.split("/")[0]))
        self.assertIn('CMD ["dotnet", "/app/host/Margo.Cloud.Host.dll"]', dockerfile)
        self.assertIn("EXPOSE 8088", dockerfile)
        self.assertRegex(dockerfile, r"(?m)^USER \$APP_UID$")
        self.assertRegex(dockerfile, r"apk add --no-cache tzdata")
        self.assertNotIn("MargoStateProbes", dockerfile)
        self.assertNotRegex(dockerfile, r"(?m)^COPY .*state/")

    def test_the_router_placeholder_never_gets_the_margo_command_or_port(self):
        # The public placeholder image has no dotnet and listens on port 80. Given the router's command or port 8088 it
        # never starts, so the minReplicas=1 router never becomes ready and every `azd provision` (the first one, and
        # each one after the blueprint is connected, which reset the router to the placeholder) fails or leaves the
        # messaging endpoint dead. The router runs the Margo image, its command and port only once postprovision.sh has
        # pinned an image (MARGO_PINNED_IMAGE) and the blueprint exists; re-provisioning then keeps that image.
        main = read("infra", "main.bicep")
        router = main[main.index("resource router "):main.index("var provisionerConfig")]
        self.assertIn("param margoImage string = ''", main)
        self.assertIn("var routerOnMargo = !empty(margoImage) && !empty(blueprintClientId)", main)
        self.assertIn("image: routerOnMargo ? margoImage : placeholderImage", router)
        self.assertIn("targetPort: routerOnMargo ? 8088 : 80,", router)
        self.assertIn("routerOnMargo ? { command: [ 'dotnet', '/app/router/Margo.Cloud.Router.dll' ] } : {}", router)
        self.assertNotRegex(router, r"(?m)^\s*command:", "the router's command must depend on routerOnMargo")
        self.assertIn("registries: routerOnMargo ? [ { server: registry.properties.loginServer, identity: routerIdentity.id } ] : []",
                      router)
        parameters = json.loads(read("infra", "main.parameters.json"))["parameters"]
        self.assertEqual(parameters["margoImage"], {"value": "${MARGO_PINNED_IMAGE=}"})
        script = read("scripts", "postprovision.sh")
        self.assertIn('azd env set MARGO_PINNED_IMAGE "$pinned"', script)
        # The image may be switched in place only when this provision already started the router on a Margo image;
        # otherwise the router still has the placeholder's port and no command.
        switch = script.index('az containerapp update -n "$ROUTER_NAME"')
        guard = script.index('if [ -z "$deployed_image" ]')
        self.assertLess(script.index('deployed_image=${MARGO_PINNED_IMAGE:-}'), guard)
        self.assertLess(guard, switch)

    def test_the_image_is_published_for_its_runtime_only(self):
        # The runtime image is Alpine on x64. Publishing for that runtime, framework-dependent and ReadyToRun, starts a
        # container scaled from zero faster and leaves out the SQLite natives of every other platform (runtimes/).
        dockerfile = read("Dockerfile")
        self.assertIn("FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine", dockerfile)
        publish = re.search(r'publish="([^"]+)"', dockerfile).group(1)
        self.assertEqual(publish, "-c Release -r linux-musl-x64 --self-contained false -p:PublishReadyToRun=true --no-restore")
        self.assertEqual(len(re.findall(r"dotnet publish \S+ \$publish -o /out/", dockerfile)), 4)
        self.assertNotRegex(dockerfile, r"dotnet publish (?!\S+ \$publish )")
        # The restore is a layer of its own, from the project files alone, for the same runtime and configuration
        # (Release: the Debug configuration would restore the contract probes).
        restore = dockerfile.index("dotnet restore")
        self.assertLess(dockerfile.index("COPY --from=projects /src/cloud/src/ src/"), restore)
        self.assertLess(restore, dockerfile.index("\nCOPY cloud/src/ src/\nCOPY cloud/infra/instance.json"))
        self.assertIn("for app in Host StateCli Router Provisioner; do", dockerfile)
        self.assertIn("-r linux-musl-x64 -p:Configuration=Release -p:PublishReadyToRun=true", dockerfile)
        self.assertIn("RUN find src -type f ! -name '*.csproj' -delete", dockerfile)
        # The image is refused if another platform's natives, or the contract probes, reached an output.
        self.assertIn("find /out -type d -name runtimes | grep .", dockerfile)
        self.assertIn("find /out -iname '*Probes*' | grep .", dockerfile)
        self.assertIn("test -f /out/host/libe_sqlite3.so", dockerfile)

    def test_the_provisioner_job_keeps_its_image_and_settings_across_provisions(self):
        # The job used to be reset to the public placeholder by every `azd provision`, without its registry, image or
        # role IDs; only the end of postprovision.sh restored them, so a hook that stopped early (an uncommitted change,
        # a failed build) silently stopped hires, suspensions and retirements. It now follows the router.
        main = read("infra", "main.bicep")
        job = main[main.index("resource provisioner "):main.index("output ")]
        config = main[main.index("var provisionerConfig = {"):main.index("resource provisioner ")]
        self.assertIn("image: routerOnMargo ? margoImage : placeholderImage", job)
        self.assertIn("registries: routerOnMargo ? [ { server: registry.properties.loginServer, identity: provisionerIdentity.id } ] : []",
                      job)
        self.assertIn("routerOnMargo ? { command: [ 'dotnet', '/app/provisioner/Margo.Cloud.Provisioner.dll', 'run' ] } : {}", job)
        self.assertNotRegex(job, r"(?m)^\s*command:", "the placeholder image has no dotnet: the command must depend on routerOnMargo")
        self.assertNotRegex(job, r"(?m)^\s*image: placeholderImage")
        # What it deploys: the pinned image and the four role IDs, as deployment values.
        self.assertIn("  image: margoImage\n", config)
        parameters = json.loads(read("infra", "main.parameters.json"))["parameters"]
        script = read("scripts", "postprovision.sh")
        for name, variable in (("storageRoleDefinitionId", "MARGO_STORAGE_ROLE_ID"), ("pullRoleDefinitionId", "MARGO_PULL_ROLE_ID"),
                               ("modelRoleDefinitionId", "MARGO_MODEL_ROLE_ID"), ("speechRoleDefinitionId", "MARGO_SPEECH_ROLE_ID")):
            self.assertIn("  %s: %s\n" % (name, name), config)
            self.assertRegex(main, r"(?m)^param %s string = ''$" % name)
            self.assertEqual(parameters[name], {"value": "${%s=}" % variable})
            # Recorded before the image build, the step that stops on an uncommitted change.
            self.assertLess(script.index("azd env set %s " % variable), script.index('sh "$HERE/build-image.sh"'))
        # No role-definition GUID lives in the repository (tools/check-clean.sh refuses GUIDs): they are looked up by name.
        self.assertNotRegex(main, r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")
        # Started by the queue, one execution at a time, never on a timer: on the placeholder nothing queues work.
        self.assertIn("triggerType: 'Event'", job)
        self.assertNotIn("cronExpression", main)
        self.assertRegex(job, r"minExecutions: 0\s+maxExecutions: 1\s+pollingInterval: 30")
        self.assertIn("type: 'azure-queue'", job)
        self.assertIn("queueName: provisionQueue.name", job)
        self.assertIn("accountName: storage.name", job)
        self.assertIn("identity: provisionerIdentity.id", job[job.index("rules: ["):job.index("replicaTimeout")])
        self.assertNotIn("connection", job.lower())  # no storage key or connection string
        self.assertIn('assign_role "$PROVISIONER_PRINCIPAL_ID" "Storage Queue Data Reader" "$PROVISION_QUEUE_ID"', script)
        # A job created with a schedule by an earlier version is removed before the provision recreates it.
        pre = read("scripts", "preprovision.sh")
        self.assertIn('--query "properties.configuration.triggerType"', pre)
        self.assertRegex(pre, r"Schedule\|Manual\)[^;]*?az containerapp job delete -n \"\$PROVISIONER_NAME\" -g \"\$RESOURCE_GROUP\" --yes")
        self.assertIn('if [ -n "${PROVISIONER_NAME:-}" ] && [ -n "${RESOURCE_GROUP:-}" ]; then', pre)
        # The cap on instances reaches the job, and the sizes are the small ones.
        self.assertIn("{ name: 'MARGO_MAX_INSTANCES', value: string(maxInstances) }", job)
        self.assertEqual(parameters["maxInstances"], {"value": "${MARGO_MAX_INSTANCES=50}"})
        self.assertIn('MaxInstancesVariable = "MARGO_MAX_INSTANCES"', read("src", "Margo.Cloud.Provisioner", "ProvisionerConfig.cs"))
        self.assertIn("DefaultMaxInstances = 50", read("src", "Margo.Cloud.Provisioner", "Provisioner.cs"))
        self.assertEqual(re.findall(r"resources: \{ cpu: json\('([\d.]+)'\), memory: '(\w+(?:\.\w+)?)' \}", main),
                         [("0.25", "0.5Gi"), ("0.25", "0.5Gi")])

    def test_every_azd_variable_maps_to_a_declared_parameter(self):
        main = read("infra", "main.bicep")
        declared = dict(re.findall(r"(?m)^param (\w+) (\w+)", main))
        parameters = json.loads(read("infra", "main.parameters.json"))["parameters"]
        self.assertEqual(sorted(set(parameters) - set(declared)), [])
        values = {name: entry["value"] for name, entry in parameters.items()}
        for name, value in {
            "modelSku": "${MARGO_MODEL_SKU=GlobalStandard}",
            "modelCapacity": "${MARGO_MODEL_CAPACITY=100}",
            "instanceMinReplicas": "${MARGO_INSTANCE_MIN_REPLICAS=0}",
            "stateVersionRetentionDays": "${MARGO_STATE_VERSION_RETENTION_DAYS=30}",
            "logRetentionDays": "${MARGO_LOG_RETENTION_DAYS=30}",
            "logDailyQuotaGb": "${MARGO_LOG_DAILY_QUOTA_GB=1}",
            "voiceMaxSeconds": "${MARGO_VOICE_MAX_SECONDS=}",
            "voiceMaxToolCalls": "${MARGO_VOICE_MAX_TOOL_CALLS=}",
        }.items():
            self.assertEqual(values[name], value)
        # A default in the parameters file is the default in main.bicep (azd turns the text into the parameter's type).
        for name, value in values.items():
            default = re.fullmatch(r"\$\{\w+=(.*)\}", value)
            if default is None:
                continue
            in_bicep = re.search(r"(?m)^param %s \w+ = (.+)$" % name, main).group(1)
            self.assertEqual(in_bicep.strip("'"), default.group(1), name)
        # The model deployment type and capacity are settings, not literals.
        self.assertIn("@allowed(['GlobalStandard', 'DataZoneStandard', 'Standard'])\n@description(", main)
        self.assertIn("sku: { name: modelSku, capacity: modelCapacity }", main)
        # Optional voice limits reach the instances only when set.
        config = main[main.index("var provisionerConfig = {"):main.index("resource provisioner ")]
        for name, parameter in (("MARGO_VOICE_MAX_SECONDS", "voiceMaxSeconds"), ("MARGO_VOICE_MAX_TOOL_CALLS", "voiceMaxToolCalls")):
            self.assertIn("empty(%s) ? [] : [ { name: '%s', value: %s } ]" % (parameter, name, parameter), config)
            self.assertIn('"%s"' % name, read("src", "Margo.Cloud.Voice", "VoiceSession.cs"))
        self.assertIn("  minReplicas: instanceMinReplicas\n", config)

    def test_replaced_state_versions_expire_and_logs_are_capped(self):
        # Blob versioning keeps every replaced state.zip (one per turn) for ever unless a lifecycle rule deletes them.
        main = read("infra", "main.bicep")
        self.assertIn("isVersioningEnabled: true", main)
        policy = main[main.index("resource stateLifecycle "):main.index("// -- one container per instance")]
        self.assertIn("'Microsoft.Storage/storageAccounts/managementPolicies@", policy)
        self.assertIn("name: 'default'", policy)
        self.assertIn("actions: { version: { delete: { daysAfterCreationGreaterThan: stateVersionRetentionDays } } }", policy)
        # Only replaced versions: no rule may delete a current blob (an idle manager's state.zip, the route table).
        self.assertNotIn("baseBlob", policy)
        self.assertNotIn("snapshot", policy)
        self.assertEqual(policy.count("name: 'delete-"), 1)
        self.assertRegex(main, r"(?m)^@minValue\(1\)\n@description\([^\n]+\)\nparam stateVersionRetentionDays int = 30$")
        self.assertIn("retentionInDays: logRetentionDays", main)
        self.assertIn("workspaceCapping: { dailyQuotaGb: logDailyQuotaGb }", main)

    def test_instances_are_probed_calmly_and_given_time_to_finish_a_turn(self):
        source = read("infra", "instance.bicep")
        template = json.loads(read("infra", "instance.json"))
        app = [resource for resource in template["resources"] if resource["type"] == "Microsoft.App/containerApps"][0]
        self.assertIn("{ type: 'Readiness', httpGet: { path: '/readiness', port: 8088 }, periodSeconds: 30 }", source)
        self.assertIn("terminationGracePeriodSeconds: 600", source)
        container = app["properties"]["template"]["containers"][0]
        self.assertEqual([(probe["type"], probe["periodSeconds"]) for probe in container["probes"]],
                         [("Liveness", 30), ("Readiness", 30)])
        self.assertEqual(app["properties"]["template"]["terminationGracePeriodSeconds"], 600)

    def test_the_scripts_fail_loudly_instead_of_reporting_done(self):
        post = read("scripts", "postprovision.sh")
        roles = post[post.index('rbac_admin='):post.index('sh "$HERE/build-image.sh"')]
        # Nothing in the role-administration section swallows a failure, and the old group-wide grant must be gone.
        self.assertNotIn("2>/dev/null", roles)
        self.assertNotIn("|| true", roles)
        self.assertNotRegex(roles, r"az role assignment delete[^\n]*&& say")
        self.assertIn('[ -z "$left" ] || die "the provisioner still has resource-group-wide role administration: $left"', roles)
        self.assertEqual(roles.count("az role assignment delete"), 2)
        self.assertEqual(roles.count("|| die \"could not remove"), 2)
        # A stored condition that is not the wanted one is replaced, not taken for "already granted".
        self.assertIn('assignments_at "$1" "$condition"', roles)
        self.assertIn("sed -n 's/^other //p'", roles)
        self.assertNotIn("RoleAssignmentExists", roles)
        self.assertIn("PrincipalType] ForAnyOfAnyValues:StringEqualsIgnoreCase {'ServicePrincipal'}", roles)
        self.assertNotIn("PrincipalType] StringEqualsIgnoreCase", roles)
        # blueprint.sh: a Graph read that fails is never "not found"; the old inheritance entry survives a failed update.
        blueprint = read("scripts", "blueprint.sh")
        self.assertNotIn("|| true", blueprint)
        for line in blueprint.splitlines():
            if "--method get" in line or "graph_get " in line and "graph_get()" not in line:
                self.assertNotIn("2>/dev/null", line)
        self.assertEqual(blueprint.count("=$(graph_get "), 5)
        self.assertEqual(len(re.findall(r"=\$\(graph_get [^\n]*\\\n\s+\|\| die ", blueprint)), 5)
        self.assertIn('[ "$fic_subject" = "$BLUEPRINT_CREDENTIAL_PRINCIPAL_ID" ]', blueprint)
        self.assertIn('--method patch --url "$BLUEPRINTS/$blueprint_id/federatedIdentityCredentials/$fic_id"', blueprint)
        delete = blueprint.index('--method delete --url "$inheritable_url/$workiq_app"')
        self.assertLess(blueprint.index('--method patch --url "$inheritable_url/$workiq_app"'), delete)
        self.assertLess(delete, blueprint.index('--body "$previous"'))
        self.assertIn("could not be put back", blueprint)
        # The azd environment (subscription, tenant, blueprint identifiers) never reaches git or the registry build.
        self.assertIn("cloud/.azure/", (ROOT / ".gitignore").read_text(encoding="utf-8").split())
        self.assertIn("**/.azure/", (ROOT / ".dockerignore").read_text(encoding="utf-8").split())

    def test_the_infrastructure_sets_what_each_role_requires(self):
        # The provisioner refuses to deploy unless every RequiredConfig key is in MARGO_PROVISIONER_CONFIG (main.bicep
        # provisionerConfig) or set by an override variable postprovision.sh gives the job.
        main = read("infra", "main.bicep")
        instances = read("src", "Margo.Cloud.Core", "Instances.cs")
        required = re.findall(r'"(\w+)"', re.search(r"RequiredConfig \{ get; \} =\s*\[(.*?)\];", instances, re.S).group(1))
        self.assertIn("environmentDomain", required)
        overrides = dict(re.findall(r'new\("(\w+)", "(\w+)"\)', read("src", "Margo.Cloud.Provisioner", "ProvisionerConfig.cs")))
        config = main[main.index("var provisionerConfig = {"):main.index("resource provisioner ")]
        configured = set(re.findall(r"(?m)^  (\w+):", config))
        self.assertEqual(sorted(set(required) - configured - set(overrides)), [])
        job_update = read("scripts", "postprovision.sh")
        job_update = job_update[job_update.index('az containerapp job update'):]
        for name, variable in overrides.items():
            self.assertIn(variable + "=", job_update, "postprovision.sh must set %s (%s)" % (variable, name))
        # Variables each role reads without a default, and where the infrastructure sets them.
        router = main[main.index("resource router "):main.index("var provisionerConfig")]
        job = main[main.index("resource provisioner "):main.index("output ")]
        instance = read("infra", "instance.bicep")
        for block, names in ((router, ["MARGO_ROUTES_URL", "MARGO_PROVISION_QUEUE_URL", "AZURE_CLIENT_ID",
                                       "CONNECTIONS__SERVICE_CONNECTION__SETTINGS__CLIENTID",
                                       "CONNECTIONS__SERVICE_CONNECTION__SETTINGS__TENANTID"]),
                             (job, ["AZURE_SUBSCRIPTION_ID", "AZURE_RESOURCE_GROUP", "MARGO_ROUTES_URL",
                                    "MARGO_PROVISION_QUEUE_URL", "MARGO_PROVISIONER_CONFIG", "AZURE_CLIENT_ID"]),
                             (instance, ["MARGO_HOSTING", "MARGO_INSTANCE_KEY", "MARGO_INSTANCE_AGENT_ID",
                                         "MARGO_INSTANCE_USER_ID", "MARGO_STATE_BLOB_URL", "MARGO_STATE_CONTAINER",
                                         "MARGO_PUBLIC_BASE_URL", "MARGO_TENANT_ID", "AZURE_CLIENT_ID",
                                         "AZURE_OPENAI_ENDPOINT", "MARGO_MODEL_DEPLOYMENT", "VOICE_LIVE_ENDPOINT",
                                         "CONNECTIONS__SERVICE_CONNECTION__SETTINGS__CLIENTID",
                                         "CONNECTIONS__SERVICE_CONNECTION__SETTINGS__TENANTID"])):
            for name in names:
                self.assertIn("{ name: '%s', value: " % name, block, name)
        for name in ("MARGO_PRINCIPAL_ACCESS", "MARGO_TIMEZONE"):
            self.assertIn("{ name: '%s', value: " % name, config)

    def test_every_copy_source_exists_and_the_build_scripts_point_at_cloud(self):
        dockerfile = read("Dockerfile")
        for source in re.findall(r"(?m)^COPY (?!--from)(.+) \S+$", dockerfile):
            for item in source.split():
                self.assertTrue((ROOT / item).exists(), "COPY source %s does not exist" % item)
        self.assertIn('--file "$CLOUD/Dockerfile"', read("scripts", "build-image.sh"))
        for script in sorted((CLOUD / "scripts").glob("*.sh")):
            text = script.read_text(encoding="utf-8")
            self.assertNotIn("margo_autopilot", text, script.name)
            self.assertNotIn("$AUTOPILOT", text, script.name)

    def test_dockerignore_keeps_state_and_build_outputs_out(self):
        ignored = set((ROOT / ".dockerignore").read_text(encoding="utf-8").split())
        for entry in ("**/state/", "**/bin/", "**/obj/", "cloud/tests/", "tests/", "docs/", "**/*.sqlite3", "**/.env"):
            self.assertIn(entry, ignored)

    def test_dockerignore_keeps_what_git_ignores_as_private_out_of_the_registry_upload(self):
        # build-image.sh uploads the repository to the registry build and only refuses *uncommitted* changes, so a file
        # git ignores is uploaded unless .dockerignore drops it too: the Agent 365 CLI's files (tenant identifiers and
        # possibly a client secret, which .gitignore expects to find here by mistake), local agent settings and
        # worktrees, and the publish outputs cloud/README.md writes.
        ignored = set((ROOT / ".dockerignore").read_text(encoding="utf-8").split())
        for entry in ("**/a365.config.json", "**/a365.generated.config.json", ".claude/", "cloud/out/",
                      "cloud/artifacts/"):
            self.assertIn(entry, ignored)
        gitignored = set((ROOT / ".gitignore").read_text(encoding="utf-8").split())
        self.assertIn("cloud/out/", gitignored)
        self.assertIn("-o out/host", read("README.md"))


if __name__ == "__main__":
    unittest.main()

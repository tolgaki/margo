using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Memory;

namespace Margo.Cloud.State.MemoryLearning;

public static partial class MemoryLearning
{
    /// <summary>
    /// <c>capabilities(memory, skills_directory=None, environment=None, tools=None)</c>: observe installed skill files and
    /// host-exported tool manifests only; never install, invoke or grant tools. A host export is evidence of exposure,
    /// not of successful execution. Runs in one store transaction.
    /// </summary>
    public static JsonObject Capabilities(MemoryStore memory, string? skillsDirectory = null, JsonNode? environment = null, string? tools = null)
    {
        ArgumentNullException.ThrowIfNull(memory);
        return memory.Transaction(() => CapabilitiesCore(memory, skillsDirectory, environment, tools));
    }

    private static JsonObject CapabilitiesCore(MemoryStore memory, string? skillsDirectory, JsonNode? environment, string? tools)
    {
        var account = memory.Account;
        var supplied = environment is not null;
        var env = supplied ? Environment(memory, environment) : new JsonObject { ["host"] = "unbound", ["account"] = account };
        var inputs = new List<JsonObject>();
        var origins = new List<(string Origin, JsonObject Environment)>();
        JsonObject? source = null;
        string? stamp = null;
        if (skillsDirectory is not null)
        {
            var root = Resolve(PyPath.ExpandUser(PyPath.Normalize(skillsDirectory)));
            if (!PyPath.IsDir(root))
            {
                throw new StateError("installed skills directory does not exist");
            }

            var paths = GlobSkills(root);
            if (paths.Count > MaxObservations)
            {
                throw new StateError("skill inventory exceeds the bounded observation budget");
            }

            var origin = "skills:" + Digest(root);
            origins.Add((origin, env));
            foreach (var path in paths)
            {
                if (PyPath.IsSymlink(path) || PyPath.IsSymlink(PyPath.Parent(path)))
                {
                    continue;
                }

                (_, source) = File(path);
                var name = PyPath.Name(PyPath.Parent(path));
                inputs.Add(new JsonObject
                {
                    ["name"] = name,
                    ["capability_kind"] = "skill",
                    ["state"] = "installed",
                    ["content_hash"] = PyNode.Copy(source["revision"]),
                    ["version"] = PyNode.Copy(source["revision"]),
                    ["origin"] = origin,
                    ["source_refs"] = new JsonArray(PyNode.Copy(source)),
                    ["base_environment"] = PyNode.Copy(env),
                    ["availability"] = "installed_file_only",
                    ["required_permissions"] = new JsonArray(),
                    ["dependencies"] = new JsonObject(),
                });
            }
        }

        if (tools is not null)
        {
            var (manifest, manifestSource) = JsonFile(tools);
            source = manifestSource;
            var toolEnv = Environment(memory, PyNode.Get(manifest, "environment"));
            if (supplied && !Eq(toolEnv, env))
            {
                throw new StateError("manifest environment does not match requested environment");
            }

            stamp = StateStore.ValidateTimestamp(PyNode.Get(manifest, "observed_at"));
            if (Time(stamp) > PyDateTime.Now())
            {
                throw new StateError("host observation cannot be in the future");
            }

            if (PyNode.Get(manifest, "tools") is not JsonArray entries || entries.Count > MaxObservations)
            {
                throw new StateError("host manifest needs a bounded tools list");
            }

            if (!PyJson.IsBool(PyNode.Get(manifest, "complete")))
            {
                throw new StateError("host manifest must state whether coverage is complete");
            }

            var collection = Text(PyNode.Get(manifest, "scope", JsonValue.Create("host-tools")), "manifest scope");
            var origin = "tools:" + Digest(new JsonArray(PyNode.Copy(Item(toolEnv, "account")), PyNode.Copy(Item(toolEnv, "host")), collection));
            var legacyOrigin = "tools:" + Digest(PyNode.Copy(source["ref"]));
            var origins2 = new HashSet<string>(StringComparer.Ordinal) { origin, legacyOrigin };
            var incomingNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                if (entry is JsonObject value && PyNode.AsString(PyNode.Get(value, "name")) is { } entryName)
                {
                    incomingNames.Add(entryName);
                }
            }

            var clockKey = "learning:inventory-clock:" + origin;
            var clock = memory.Conn.Execute("SELECT value FROM memory_meta WHERE key=?", clockKey).FetchOne();
            var previousStamps = new List<JsonNode?>();
            if (clock is not null)
            {
                previousStamps.Add(PyNode.Copy(Item(PyNode.Loads(clock[0]), "observed_at")));
            }

            var toolHost = Item(toolEnv, "host");
            foreach (var row in memory.List("agent"))
            {
                if (Eq(Item(row, "kind"), "capability")
                    && Eq(Get(Get(row, "metadata", new JsonObject()), "capability_kind"), "tool")
                    && Eq(Get(Get(Item(row, "metadata"), "base_environment", new JsonObject()), "host"), toolHost)
                    && (PyNode.In(Get(Item(row, "metadata"), "origin"), origins2) || PyNode.In(Get(Item(row, "metadata"), "name"), incomingNames))
                    && PyJson.Truthy(Get(Item(row, "metadata"), "observed_at")))
                {
                    previousStamps.Add(PyNode.Copy(Item(Item(row, "metadata"), "observed_at")));
                }
            }

            if (previousStamps.Count > 0)
            {
                var current = Time(stamp);
                PyDateTime? latest = null;
                foreach (var previous in previousStamps)
                {
                    var moment = Time(previous);
                    if (latest is null || moment > latest.Value)
                    {
                        latest = moment;
                    }
                }

                if (current < latest!.Value)
                {
                    throw new StateError("host inventory is older than an already accepted observation");
                }
            }

            memory.Conn.Execute(
                "INSERT INTO memory_meta VALUES(?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                clockKey, StateStore.CanonicalJson(new JsonObject { ["observed_at"] = stamp, ["complete"] = PyNode.Copy(manifest["complete"]) }));
            if (PyJson.Truthy(manifest["complete"]))
            {
                origins.Add((origin, toolEnv));
                origins.Add((legacyOrigin, toolEnv));
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in entries)
            {
                if (item is not JsonObject entry)
                {
                    throw new StateError("tool entry must be an object");
                }

                var name = Text(PyNode.Get(entry, "name"), "tool name");
                if (!names.Add(name))
                {
                    throw new StateError("duplicate tool identity in host manifest");
                }

                Text(PyNode.Get(entry, "version"), "tool version");
                if (PyNode.Get(entry, "input_schema") is not JsonObject)
                {
                    throw new StateError("tool entry requires its exposed input_schema");
                }

                if (!PyJson.IsBool(PyNode.Get(entry, "exposed")))
                {
                    throw new StateError("tool entry requires observed exposed boolean");
                }

                var permissions = Strings(PyNode.Get(entry, "required_permissions", new JsonArray()), "permissions", allowEmpty: true);
                if (PyNode.Get(entry, "dependencies", new JsonObject()) is not JsonObject dependencies)
                {
                    throw new StateError("tool dependencies must be versioned values");
                }

                var fingerprint = Digest(new JsonObject
                {
                    ["version"] = PyNode.Copy(PyNode.Get(entry, "version")),
                    ["input_schema"] = PyNode.Copy(PyNode.Get(entry, "input_schema")),
                    ["required_permissions"] = PyNode.Copy(PyNode.Get(entry, "required_permissions", new JsonArray())),
                    ["dependencies"] = PyNode.Copy(PyNode.Get(entry, "dependencies", new JsonObject())),
                });
                var exposed = PyJson.Truthy(entry["exposed"]);
                inputs.Add(new JsonObject
                {
                    ["name"] = name,
                    ["capability_kind"] = "tool",
                    ["version"] = PyNode.Copy(entry["version"]),
                    ["content_hash"] = fingerprint,
                    ["state"] = exposed ? "installed" : "discovered",
                    ["availability"] = exposed ? "host_exposed" : "documented_not_exposed",
                    ["input_schema"] = PyNode.Copy(entry["input_schema"]),
                    ["source_refs"] = new JsonArray(PyNode.Copy(source)),
                    ["origin"] = origin,
                    ["base_environment"] = PyNode.Copy(toolEnv),
                    ["observed_at"] = stamp,
                    ["inventory_scope"] = collection,
                    ["required_permissions"] = permissions,
                    ["dependencies"] = PyNode.Copy(dependencies),
                });
            }
        }

        if (skillsDirectory is null && tools is null)
        {
            throw new StateError("provide installed skills or a host tool manifest");
        }

        var observed = new List<string>();
        var retired = new List<string>();
        var priorRows = memory.List("agent").OfType<JsonObject>().ToList();
        foreach (var item in inputs)
        {
            var kind = PyNode.AsString(item["capability_kind"])!;
            var name = PyNode.AsString(item["name"])!;
            var baseEnvironment = (JsonObject)item["base_environment"]!;
            var key = kind == "skill" && Eq(Item(baseEnvironment, "host"), "unbound")
                ? "installed-skill:" + name
                : "capability:" + Digest(new JsonArray(PyNode.Copy(Item(baseEnvironment, "account")), PyNode.Copy(Item(baseEnvironment, "host")), kind, name));
            JsonObject? prior = null;
            string? identity = null;
            foreach (var row in priorRows)
            {
                identity ??= memory.RecordId("agent", key);
                if (Eq(Item(row, "id"), identity))
                {
                    prior = row;
                    break;
                }
            }

            if (prior is not null && PyNode.In(Item(prior, "status"), InactiveCapabilityStates))
            {
                continue;
            }

            var requirements = PyNode.CopyObject(baseEnvironment);
            requirements[kind + ":" + name] = new JsonObject
            {
                ["version"] = PyNode.Copy(item["version"]),
                ["content_hash"] = PyNode.Copy(item["content_hash"]),
                ["dependencies"] = PyNode.Copy(item["dependencies"]),
                ["required_permissions"] = PyNode.Copy(item["required_permissions"]),
            };
            var metadata = PyNode.DictWith(item, ("environment_requirements", requirements), ("inventory_source_refs", item["source_refs"]),
                                           ("validation", "not_validated"), ("no_execution_authority", true));
            metadata.Remove("source_refs");
            if (kind == "skill")
            {
                metadata["skill_name"] = name;
            }

            var recordData = new JsonObject
            {
                ["domain"] = "agent",
                ["kind"] = "capability",
                ["title"] = char.ToUpperInvariant(kind[0]) + kind[1..] + " capability: " + name,
                ["text"] = "Observed " + PyNode.AsString(item["availability"]) + " for " + name
                           + ". Discovery or installation does not establish competence or grant permission.",
                ["authority"] = "source_observed",
                ["scope"] = "capabilities:" + PyNode.Str(Item(baseEnvironment, "host")),
                ["sensitivity"] = "private",
                ["allowed_uses"] = new JsonArray("reasoning"),
                ["entities"] = new JsonArray(kind + ":" + name),
                ["routines"] = new JsonArray(),
                ["source_refs"] = PyNode.Copy(item["source_refs"]),
                ["metadata"] = metadata,
            };
            if (prior is not null)
            {
                var old = Get(prior, "metadata", new JsonObject());
                var history = new JsonArray();
                foreach (var entry in PyNode.Iter(Get(old, "validation_history", new JsonArray())))
                {
                    var fallback = ValidationScope(old);
                    history.Add(PyNode.DictWith(entry, ("scope", PyNode.Copy(Get(entry, "scope", fallback)))));
                }

                metadata["validation_history"] = history;
                if (!Eq(Get(old, "state"), "retired")
                    && ComparableFields.All(field => Eq(Get(old, field), PyNode.Get(metadata, field))))
                {
                    // Refreshing the export file must not silently transfer validation to a new source.
                    if (Eq(Get(old, "inventory_source_refs", Item(prior, "source_refs")), recordData["source_refs"])
                        && memory.WorkSourcesCurrent(prior))
                    {
                        observed.Add(PyNode.AsString(prior["id"])!);
                        continue;
                    }
                }
            }

            var record = memory.Put(key, recordData, "active", prior is null ? null : PyNode.Copy(Item(prior, "revision")));
            observed.Add(PyNode.AsString(record["id"])!);
        }

        foreach (var prior in priorRows)
        {
            var meta = Get(prior, "metadata", new JsonObject());
            if (!Eq(Item(prior, "kind"), "capability") || observed.Contains(PyNode.AsString(prior["id"]) ?? "", StringComparer.Ordinal)
                || !Eq(Item(prior, "status"), "active") || Eq(Get(meta, "state"), "retired")
                || !origins.Any(pair => Eq(Get(meta, "origin"), pair.Origin) && Eq(Get(meta, "base_environment"), pair.Environment)))
            {
                continue;
            }

            var changed = Data(prior);
            var changedMetadata = PyNode.DictWith(meta, ("state", "retired"), ("validation", "not_validated"));
            changed["metadata"] = changedMetadata;
            changed["text"] = "This capability is absent from the current complete inventory; no permission was changed.";
            if (tools is not null && Eq(Get(meta, "capability_kind"), "tool"))
            {
                changed["source_refs"] = new JsonArray(PyNode.Copy(source));
                changedMetadata["observed_at"] = stamp;
            }

            var record = memory.Revise(PyNode.Copy(prior["id"]), changed, "active", PyNode.Copy(Item(prior, "revision")));
            retired.Add(PyNode.AsString(record["id"])!);
        }

        return new JsonObject
        {
            ["observed"] = observed.Count,
            ["memory_ids"] = PyNode.StrList(observed),
            ["retired_ids"] = PyNode.StrList(retired),
            ["note"] = "Files and host manifests only; no tools executed and no competence inferred.",
        };
    }

    /// <summary>
    /// <c>sorted(root.glob("*/SKILL.md"))</c> in CPython 3.13: every entry of <paramref name="root"/> (hidden ones too)
    /// that is a directory, following symlinks, with a <c>SKILL.md</c> that <c>lexists</c> (a dangling symlink or a
    /// directory counts); sorted by path components.
    /// </summary>
    internal static List<string> GlobSkills(string root)
    {
        var found = new List<(string Name, string Path)>();
        IEnumerable<string> names;
        try
        {
            names = Directory.EnumerateFileSystemEntries(root, "*", new EnumerationOptions
            {
                AttributesToSkip = 0,
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
                MatchType = MatchType.Simple,
            }).Select(System.IO.Path.GetFileName).OfType<string>().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        foreach (var name in names)
        {
            var directory = PyPath.Join(root, name);
            if (!SafeStat(directory, follow: true, out var info) || !info.IsDirectory)
            {
                continue;
            }

            var candidate = PyPath.Join(directory, "SKILL.md");
            if (SafeStat(candidate, follow: false, out _))
            {
                found.Add((name, candidate));
            }
        }

        return PySort.SortedBy(found, pair => pair.Name, (left, right) => PyRepr.CompareStrings(left, right) < 0)
                     .Select(pair => pair.Path).ToList();
    }

    private static bool SafeStat(string path, bool follow, out PyStat info)
    {
        try
        {
            info = PosixNative.Stat(path, follow);
            return true;
        }
        catch (Exception ex) when (ex is PyOSError or PyValueError)
        {
            info = default;
            return false;
        }
    }
}

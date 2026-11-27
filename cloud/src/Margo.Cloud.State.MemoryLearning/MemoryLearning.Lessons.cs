using System.Numerics;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Memory;

namespace Margo.Cloud.State.MemoryLearning;

public static partial class MemoryLearning
{
    /// <summary>
    /// <c>lesson_plan(memory, key, data, revision=None)</c>: persist a candidate recipe grounded in local observations,
    /// never executable code. Required: title, scope, trigger, goal, preconditions[], procedure[], risk,
    /// capability_refs[{id,revision}], evidence[{capability_id,observation}], counterexamples[] (capability_id,
    /// observation and condition); feedback_refs is optional.
    /// </summary>
    public static JsonObject LessonPlan(MemoryStore memory, JsonNode? key, JsonNode? data, JsonNode? revision = null)
    {
        ArgumentNullException.ThrowIfNull(memory);
        if (data is not JsonObject proposal)
        {
            throw new StateError("lesson proposal must be an object");
        }

        foreach (var field in new[] { "title", "scope", "trigger", "goal" })
        {
            Text(PyNode.Get(proposal, field), field);
        }

        var preconditions = Strings(PyNode.Get(proposal, "preconditions"), "preconditions");
        var procedure = Strings(PyNode.Get(proposal, "procedure"), "procedure");
        if (!PyNode.In(PyNode.Get(proposal, "risk"), Risks))
        {
            throw new StateError("lesson must preserve an explicit action boundary");
        }

        if (PyNode.Get(proposal, "capability_refs") is not JsonArray references || references.Count < 1 || references.Count > 20)
        {
            throw new StateError("lesson needs bounded pinned capability_refs");
        }

        return memory.Transaction(() =>
        {
            var caps = new OrderedDictionary<string, JsonObject>(StringComparer.Ordinal);
            var sources = new List<JsonNode?>();
            var environment = new JsonObject();
            foreach (var reference in references)
            {
                if (reference is not JsonObject exact || !KeysAre(exact, "id", "revision") || !PyJson.IsInt(exact["revision"]))
                {
                    throw new StateError("capability ref requires exact id and revision");
                }

                var cap = memory.Show(exact["id"]);
                if (!Eq(Item(cap, "kind"), "capability") || !Eq(Item(cap, "status"), "active")
                    || !Eq(Item(cap, "revision"), exact["revision"])
                    || !PyNode.In(Get(Item(cap, "metadata"), "state"), ExercisedStates)
                    || !memory.WorkSourcesCurrent(cap))
                {
                    throw new StateError("lesson requires current, exercised capability evidence");
                }

                var capId = PyNode.AsString(cap["id"])!;
                if (caps.ContainsKey(capId))
                {
                    throw new StateError("duplicate capability reference");
                }

                foreach (var (name, value) in Obj(Item(Item(cap, "metadata"), "environment_requirements"), "items"))
                {
                    if (environment.TryGetPropertyValue(name, out var existing) && !Eq(existing, value))
                    {
                        throw new StateError("lesson cannot combine incompatible environments");
                    }

                    environment[name] = PyNode.Copy(value);
                }

                caps[capId] = cap;
                sources.Add(new JsonObject { ["kind"] = "memory_record", ["ref"] = capId, ["revision"] = PyNode.Str(cap["revision"]) });
            }

            var observations = new List<JsonObject>();
            var counterexamples = new List<JsonObject>();
            var observedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in EvidenceFields)
            {
                if (PyNode.Get(proposal, field) is not JsonArray entries || entries.Count > MaxObservations
                    || (field == "evidence" && entries.Count == 0))
                {
                    throw new StateError("lesson requires bounded evidence and explicit counterexamples lists");
                }

                foreach (var candidate in entries)
                {
                    if (candidate is not JsonObject entry || !InKeys(PyNode.Get(entry, "capability_id"), caps))
                    {
                        throw new StateError("lesson evidence must identify a pinned capability");
                    }

                    var result = Observation(memory, caps[PyNode.AsString(entry["capability_id"])!], PyNode.Get(entry, "observation"));
                    var resultId = PyNode.AsString(result["id"])!;
                    if (observedIds.Contains(resultId))
                    {
                        throw new StateError("repeated evidence is not an independent lesson observation");
                    }

                    observedIds.Add(resultId);
                    if (field == "counterexamples")
                    {
                        Text(PyNode.Get(entry, "condition"), "counterexample condition");
                        if (!Eq(result["outcome"], "failed"))
                        {
                            throw new StateError("a counterexample requires observed failure, not an invented assertion");
                        }

                        counterexamples.Add(PyNode.DictWith(entry, ("error_category", PyNode.Copy(result["error_category"]))));
                    }
                    else
                    {
                        observations.Add(PyNode.DictWith(entry, ("outcome", PyNode.Copy(result["outcome"])),
                                                         ("level", PyNode.Copy(result["level"])),
                                                         ("error_category", PyNode.Copy(result["error_category"]))));
                    }

                    sources.AddRange(((JsonArray)result["source_refs"]!).Select(PyNode.Copy));
                }
            }

            var feedbackRefs = PyNode.Get(proposal, "feedback_refs", new JsonArray());
            if (feedbackRefs is not JsonArray feedbackList || feedbackList.Count > 20)
            {
                throw new StateError("feedback_refs exceeds the lesson budget");
            }

            foreach (var reference in feedbackList)
            {
                sources.Add(Feedback(memory, reference));
            }

            var successful = observations.Where(row => Eq(row["outcome"], "succeeded")).ToList();
            var failures = new JsonArray();
            foreach (var cap in caps.Values)
            {
                var capMeta = Item(cap, "metadata");
                foreach (var entry in PyNode.Iter(Get(capMeta, "validation_history", new JsonArray())))
                {
                    if (Eq(Item(entry, "outcome"), "failed")
                        && Eq(Get(entry, "scope", ValidationScope(capMeta)), ValidationScope(capMeta)))
                    {
                        var failure = new JsonObject { ["capability_id"] = PyNode.Copy(cap["id"]) };
                        PyNode.Update(failure, entry);
                        failures.Add(failure);
                    }
                }
            }

            var runtimeValidated = successful.Any(row => Eq(row["level"], "existing_execution_receipt"));
            var metadata = new JsonObject
            {
                ["trigger"] = PyNode.Copy(proposal["trigger"]),
                ["goal"] = PyNode.Copy(proposal["goal"]),
                ["preconditions"] = preconditions,
                ["procedure"] = procedure,
                ["risk"] = PyNode.Copy(proposal["risk"]),
                ["counterexamples"] = new JsonArray(counterexamples.Select(row => (JsonNode?)row).ToArray()),
                ["environment_requirements"] = environment,
                ["capability_refs"] = PyNode.Copy(references),
                ["observations"] = new JsonArray(observations.Select(row => (JsonNode?)row).ToArray()),
                ["feedback_refs"] = PyNode.Copy(feedbackList),
                ["evidence_ids"] = PyNode.StrList(PyText.Sorted(observedIds)),
                ["no_execution_authority"] = true,
                ["confidence_basis"] = successful.Count > 0 ? "bounded_success_evidence" : "failure_or_correction_only",
                ["runtime_validated"] = runtimeValidated,
                ["known_capability_failures"] = failures,
                ["applicability"] = "candidate_requires_exact_review",
            };
            var lines = new List<string>
            {
                "Trigger: " + PyNode.AsString(proposal["trigger"]),
                "Goal: " + PyNode.AsString(proposal["goal"]),
                "Preconditions:",
            };
            lines.AddRange(preconditions.Select(item => "- " + PyNode.AsString(item)));
            lines.Add("Proposed procedure (advisory data):");
            lines.AddRange(procedure.Select(item => "- " + PyNode.AsString(item)));
            lines.Add("Counterexamples:");
            lines.AddRange(counterexamples.Select(item =>
                "- " + PyNode.AsString(item["condition"]) + " [" + StrOperand(item["error_category"]) + "]"));
            if (failures.Count > 0)
            {
                var categories = new HashSet<string>(StringComparer.Ordinal);
                foreach (var failure in failures)
                {
                    var category = Item(failure, "error_category");
                    categories.Add(PyJson.Truthy(category) ? StrOperand(category) : "unclassified");
                }

                lines.Add("Capability failure categories: " + string.Join(", ", PyText.Sorted(categories)));
            }
            else
            {
                lines.Add("No additional capability failures were supplied.");
            }

            lines.Add(successful.Count == 0
                ? "Evidence limit: failure or correction only; the proposed recovery is not validated."
                : runtimeValidated
                    ? "Evidence limit: an existing scoped execution receipt supports a bounded success, not general competence."
                    : "Evidence limit: synthetic input-contract checks only; remote execution is not validated.");
            lines.Add("Action boundary: " + PyNode.AsString(proposal["risk"]) + "; no stored lesson grants permissions or lowers existing approvals.");
            var lessonText = string.Join("\n", lines);
            if (PyRepr.Length(lessonText) > 10000)
            {
                throw new StateError("lesson exceeds the bounded recipe budget; propose a smaller scoped lesson");
            }

            var recordData = new JsonObject
            {
                ["domain"] = "agent",
                ["kind"] = "lesson",
                ["title"] = PyNode.Copy(proposal["title"]),
                ["text"] = lessonText,
                ["authority"] = "inferred",
                ["scope"] = PyNode.Copy(proposal["scope"]),
                ["sensitivity"] = "private",
                ["allowed_uses"] = new JsonArray("reasoning"),
                ["entities"] = PyNode.Copy(PyNode.Get(proposal, "entities", new JsonArray())),
                ["routines"] = PyNode.Copy(PyNode.Get(proposal, "routines", new JsonArray())),
                ["source_refs"] = Refs(sources),
                ["metadata"] = metadata,
            };
            if (PyJson.Truthy(PyNode.Get(proposal, "review_after")))
            {
                recordData["review_after"] = StateStore.ValidateTimestamp(proposal["review_after"]);
            }

            var normal = NormaliseProposal(memory, recordData);
            var prior = Prior(memory, key);
            if (prior is not null && !Eq(Item(prior, "status"), "candidate"))
            {
                throw new StateError("reviewed lessons are immutable through proposal creation; propose a separate replacement");
            }

            if (prior is not null && Eq(Data(prior), normal)
                && (revision is null || (PyJson.IsInt(revision) && Eq(revision, Item(prior, "revision")))))
            {
                return prior;
            }

            Conditional(prior, revision);
            return memory.Put(key, normal, "candidate", PyNode.Copy(revision));
        });
    }

    /// <summary>The <c>str</c> operand of <c>"..." + x</c>: Python raises <c>TypeError</c> for anything else.</summary>
    private static string StrOperand(JsonNode? value) =>
        PyNode.AsString(value) ?? throw new PyTypeError("can only concatenate str (not \"" + PyJson.TypeName(value) + "\") to str");

    /// <summary>
    /// <c>activate_lesson(memory, memory_id, revision, evidence)</c>: exact human review activates a scoped recipe, not
    /// permission to execute it. Its capabilities, observations and feedback are re-read first.
    /// </summary>
    public static JsonObject ActivateLesson(MemoryStore memory, JsonNode? memoryId, JsonNode? revision, JsonNode? evidence)
    {
        ArgumentNullException.ThrowIfNull(memory);
        return memory.Transaction(() =>
        {
            var current = memory.Show(memoryId);
            Conditional(current, revision);
            if (!Eq(Item(current, "kind"), "lesson") || !Eq(Item(current, "status"), "candidate"))
            {
                throw new StateError("only a current lesson candidate can be activated");
            }

            if (PyJson.Truthy(PyNode.Get(current, "review_after")) && Time(current["review_after"]) <= PyDateTime.Now())
            {
                throw new StateError("lesson review deadline passed; revalidate before activation");
            }

            if (!memory.WorkSourcesCurrent(current))
            {
                throw new StateError("lesson evidence changed; create and review a fresh proposal");
            }

            var metadata = Item(current, "metadata");
            if (!Eq(Get(metadata, "confidence_basis"), "bounded_success_evidence"))
            {
                throw new StateError("a failed-only recipe needs a successful bounded exercise before activation");
            }

            foreach (var reference in PyNode.Iter(Item(metadata, "capability_refs")))
            {
                var cap = memory.Show(Item(reference, "id"));
                if (!Eq(Item(cap, "revision"), Item(reference, "revision")) || !Eq(Item(Item(cap, "metadata"), "state"), "validated"))
                {
                    throw new StateError("activation requires currently validated capabilities");
                }
            }

            foreach (var entry in Concat(Item(metadata, "observations"), Item(metadata, "counterexamples")))
            {
                var cap = memory.Show(Item(entry, "capability_id"));
                var observed = Observation(memory, cap, Item(entry, "observation"));
                var stored = (JsonArray)Item(current, "source_refs")!;
                if (((JsonArray)observed["source_refs"]!).Any(reference => !PyNode.InList(reference, stored))
                    || !Eq(observed["outcome"], Get(entry, "outcome", "failed")))
                {
                    throw new StateError("lesson execution evidence changed; review a fresh proposal");
                }
            }

            foreach (var reference in PyNode.Iter(Item(metadata, "feedback_refs")))
            {
                Feedback(memory, reference);
            }

            memory.Human(evidence, memoryId, revision, "confirm");
            var changed = Data(current);
            changed["authority"] = "user_confirmed";
            changed["metadata"] = PyNode.DictWith(metadata, ("applicability", "reviewed_scoped_recipe"));
            return memory.Revise(memoryId, changed, "active", revision, evidence);
        });
    }

    /// <summary><c>_definition(value)</c>: a complete, bounded trend definition.</summary>
    public static JsonObject Definition(JsonNode? value)
    {
        var expected = DefaultTrend;
        if (value is not JsonObject definition || definition.Count != expected.Count || expected.Any(pair => !definition.ContainsKey(pair.Key)))
        {
            throw new StateError("trend definition requires " + PyNode.JoinSorted(expected.Select(pair => pair.Key)));
        }

        foreach (var (field, lower, upper) in new (string, long, long)[]
                 {
                     ("min_independent_events", 3, MaxObservations), ("window_days", 1, 366), ("min_population", 3, 1000000),
                 })
        {
            if (!PyJson.IsInt(definition[field]) || PyJson.AsInt(definition[field]) < lower || PyJson.AsInt(definition[field]) > upper)
            {
                throw new StateError("invalid trend threshold: " + field);
            }
        }

        var coverage = definition["min_coverage"];
        if (!(PyJson.IsInt(coverage) || PyJson.IsFloat(coverage)) || !PyObj.Lt(0, coverage) || !PyObj.Le(coverage, 1)
            || !PyJson.IsBool(definition["require_complete"]))
        {
            throw new StateError("invalid trend coverage threshold");
        }

        if (PyJson.AsInt(definition["min_population"]) < PyJson.AsInt(definition["min_independent_events"]))
        {
            throw new StateError("trend population threshold must include supporting events");
        }

        return PyNode.CopyObject(definition);
    }

    /// <summary>
    /// <c>configure_trend(memory, name, definition, revision=None, evidence=None)</c>: preview without evidence; persist
    /// only an exact reviewed configuration.
    /// </summary>
    public static JsonObject ConfigureTrend(MemoryStore memory, JsonNode? name, JsonNode? definition, JsonNode? revision = null,
                                           JsonNode? evidence = null)
    {
        ArgumentNullException.ThrowIfNull(memory);
        if (!PyNode.In(name, TrendTypes))
        {
            throw new StateError("unsupported aggregate trend type; people scoring is not supported");
        }

        var checkedDefinition = Definition(definition);
        return memory.Transaction(() =>
        {
            var metaKey = "learning:trend-definition:" + PyNode.AsString(name);
            var row = memory.Conn.Execute("SELECT value FROM memory_meta WHERE key=?", metaKey).FetchOne();
            var current = row is not null ? PyNode.Loads(row[0]) : new JsonObject { ["revision"] = 0 };
            if (revision is not null && (!PyJson.IsInt(revision) || !Eq(revision, Item(current, "revision"))))
            {
                throw new StateError("trend definition revision conflict");
            }

            var currentRevision = Item(current, "revision");

            var manifest = new JsonObject
            {
                ["account"] = memory.Account,
                ["name"] = PyNode.Copy(name),
                ["definition"] = checkedDefinition,
                ["expected_revision"] = PyNode.Copy(currentRevision),
            };
            var preview = PyNode.DictWith(manifest, ("subject_id", "trend-definition:" + Digest(manifest)), ("revision", 1));
            if (PyNode.IsNone(evidence))
            {
                return preview;
            }

            memory.Human(evidence, preview["subject_id"], 1, "configure");
            var saved = PyNode.DictWith(manifest, ("revision", PyNumbersAdd(currentRevision, 1)), ("evidence", PyNode.Copy(evidence)));
            memory.Conn.Execute("INSERT INTO memory_meta VALUES (?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                                metaKey, StateStore.CanonicalJson(saved));
            return saved;
        });
    }

    private static JsonValue PyNumbersAdd(JsonNode? value, long amount) => PyNumbers.Add(PyJson.Scalar(value), amount) switch
    {
        BigInteger integer => PyJson.Int(integer),
        double number => JsonValue.Create(number),
        var other => throw new PyTypeError("unsupported operand type(s) for +: '" + PyObj.TypeName(other) + "' and 'int'"),
    };
}

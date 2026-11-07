using System.Numerics;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Memory;

namespace Margo.Cloud.State.MemoryLearning;

public static partial class MemoryLearning
{
    /// <summary>
    /// <c>trend(memory, key, data, revision=None)</c>: aggregate supplied resolved events into a candidate trend
    /// hypothesis; duplicate reports are corroboration, not samples. Events contain id, independence_key, occurred_at,
    /// matched and source_refs. Coverage is <c>complete</c> or {status, observed_population, expected_population};
    /// population describes the observed population only. Legacy <c>event_ids</c> stay caller-resolved, without
    /// verified dates. Runs in one store transaction.
    /// </summary>
    public static JsonObject Trend(MemoryStore memory, JsonNode? key, JsonNode? data, JsonNode? revision = null)
    {
        ArgumentNullException.ThrowIfNull(memory);
        return memory.Transaction(() => TrendCore(memory, key, data, revision));
    }

    private static JsonObject TrendCore(MemoryStore memory, JsonNode? key, JsonNode? data, JsonNode? revision)
    {
        if (data is not JsonObject input)
        {
            throw new StateError("trend input must be an object");
        }

        foreach (var field in new[] { "title", "text", "scope", "window_start", "window_end" })
        {
            Text(PyNode.Get(input, field), field);
        }

        var start = StateStore.ValidateTimestamp(input["window_start"]);
        var end = StateStore.ValidateTimestamp(input["window_end"]);
        var now = Time(StateStore.UtcNow());
        if (Time(start) >= Time(end))
        {
            throw new StateError("trend window must be ordered");
        }

        if (Time(end) > now)
        {
            throw new StateError("trend window must end at or before the observation time");
        }

        var name = PyNode.Get(input, "trend_type", "meeting_preparation");
        if (!PyNode.In(name, TrendTypes))
        {
            throw new StateError("unsupported aggregate trend type; people scoring is not supported");
        }

        var definition = DefaultTrend;
        JsonNode? definitionRevision = 0;
        var configured = memory.Conn.Execute("SELECT value FROM memory_meta WHERE key=?",
                                             "learning:trend-definition:" + PyNode.AsString(name)).FetchOne();
        if (configured is not null)
        {
            var config = PyNode.Loads(configured[0]);
            definition = Definition(Item(config, "definition"));
            definitionRevision = PyNode.Copy(Item(config, "revision"));
        }

        if ((Time(end) - Time(start)).TotalSeconds() > (double)(PyJson.AsInt(definition["window_days"]) * 86400))
        {
            throw new StateError("trend window exceeds its configured duration");
        }

        var entities = PyNode.Get(input, "entities", new JsonArray());
        if (entities is not JsonArray entityList
            || entityList.Any(item => PyNode.AsString(item) is not { } text
                                      || PersonPrefixes.Any(prefix => text.StartsWith(prefix, StringComparison.Ordinal))))
        {
            throw new StateError("trends are aggregate observations, not named-person scoring");
        }

        var events = new OrderedDictionary<string, JsonObject>(StringComparer.Ordinal);
        var groups = new OrderedDictionary<string, bool>(StringComparer.Ordinal);
        JsonNode? sources;
        JsonArray eventIds;
        JsonArray independentIds;
        string independence;
        var supplied = PyNode.Get(input, "events");
        if (supplied is not null)
        {
            if (supplied is not JsonArray reported || reported.Count < 1 || reported.Count > MaxObservations)
            {
                throw new StateError("trend needs a bounded resolved event list");
            }

            var collected = new List<JsonNode?>();
            foreach (var item in reported)
            {
                if (item is not JsonObject original)
                {
                    throw new StateError("resolved event must be an object");
                }

                var identity = Text(PyNode.Get(original, "id"), "event.id");
                var independent = Text(PyNode.Get(original, "independence_key"), "event.independence_key");
                var stamp = StateStore.ValidateTimestamp(PyNode.Get(original, "occurred_at"));
                var moment = Time(stamp);
                if (!(Time(start) <= moment && moment < Time(end)))
                {
                    throw new StateError("supporting event lies outside the declared window");
                }

                if (Time(stamp) > now)
                {
                    throw new StateError("a future event is not an observed trend sample");
                }

                if (!PyJson.IsBool(PyNode.Get(original, "matched")))
                {
                    throw new StateError("event requires a resolved matched boolean");
                }

                if (PyNode.Get(original, "source_refs") is not JsonArray refs || refs.Count == 0)
                {
                    throw new StateError("each resolved event requires citable sources");
                }

                var matched = PyJson.Truthy(original["matched"]);
                var normalized = new JsonObject
                {
                    ["id"] = identity,
                    ["independence_key"] = independent,
                    ["occurred_at"] = stamp,
                    ["matched"] = matched,
                };
                if (events.TryGetValue(identity, out var known) && !Eq(known, normalized))
                {
                    throw new StateError("conflicting reports for the same event");
                }

                if (groups.TryGetValue(independent, out var outcome) && outcome != matched)
                {
                    throw new StateError("contradictory reports for one independent event");
                }

                events[identity] = normalized;
                groups[independent] = matched;
                collected.AddRange(refs);
            }

            sources = new JsonArray(collected.Select(PyNode.Copy).ToArray());
            eventIds = PyNode.StrList(PyText.Sorted(events.Where(pair => PyJson.Truthy(pair.Value["matched"])).Select(pair => pair.Key)));
            independentIds = PyNode.StrList(PyText.Sorted(groups.Where(pair => pair.Value).Select(pair => pair.Key)));
            independence = "caller_resolved_event_groups; cross-source independence_not_independently_verified";
        }
        else
        {
            var legacy = PyNode.Get(input, "event_ids");
            if (legacy is not JsonArray declared || declared.Count > MaxObservations || !declared.All(PyNode.IsText)
                || declared.Select(item => PyNode.AsString(item)!).Distinct(StringComparer.Ordinal).Count() != declared.Count)
            {
                throw new StateError("legacy event_ids must be distinct caller-resolved IDs");
            }

            eventIds = PyNode.StrList(PyText.Sorted(declared.Select(item => PyNode.AsString(item)!)));
            independentIds = eventIds;
            independence = "caller_declared_independence_and_dates_not_independently_verified";
            sources = PyNode.Get(input, "source_refs", new JsonArray());
        }

        var count = independentIds.Count;
        var denominator = PyNode.Get(input, "population");
        if (count < PyJson.AsInt(definition["min_independent_events"]))
        {
            throw new StateError("insufficient independent events for the configured trend threshold");
        }

        if (!PyJson.IsInt(denominator)
            || PyJson.AsInt(denominator) < BigInteger.Max(BigInteger.Max(groups.Count, count), PyJson.AsInt(definition["min_population"])))
        {
            throw new StateError("trend population must include all resolved supporting and nonmatching events");
        }

        var population = PyJson.AsInt(denominator);
        var coverage = PyNode.Get(input, "coverage");
        if (PyNode.AsString(coverage) is { } coverageText)
        {
            if (coverageText != "complete")
            {
                throw new StateError("partial coverage requires observed and expected population counts");
            }

            coverage = new JsonObject
            {
                ["status"] = coverageText,
                ["observed_population"] = PyNode.Copy(denominator),
                ["expected_population"] = PyNode.Copy(denominator),
            };
        }

        if (coverage is not JsonObject covered || !PyNode.In(PyNode.Get(covered, "status"), CoverageStatuses)
            || !Eq(PyNode.Get(covered, "observed_population"), denominator)
            || !PyJson.IsInt(PyNode.Get(covered, "expected_population"))
            || PyJson.AsInt(covered["expected_population"]) < population)
        {
            throw new StateError("coverage must identify observed and expected populations");
        }

        var fraction = PyNumbers.TrueDivide(population, PyJson.AsInt(covered["expected_population"]));
        if (Eq(covered["status"], "complete") && fraction != 1)
        {
            throw new StateError("complete coverage cannot have missing population");
        }

        if ((PyJson.Truthy(definition["require_complete"]) && !Eq(covered["status"], "complete"))
            || PyObj.Lt(fraction, definition["min_coverage"]))
        {
            throw new StateError("incomplete coverage cannot establish this configured trend; retain a gap");
        }

        if (sources is not JsonArray sourceList || sourceList.Count == 0)
        {
            throw new StateError("trend needs citable source references");
        }

        var resolved = PySort.SortedBy(events.Values, row => PyNode.AsString(row["id"])!, (left, right) => PyRepr.CompareStrings(left, right) < 0);
        var record = new JsonObject
        {
            ["domain"] = PyNode.Copy(PyNode.Get(input, "domain", "agent")),
            ["kind"] = "trend",
            ["title"] = PyNode.Copy(input["title"]),
            ["text"] = PyNode.Copy(input["text"]),
            ["authority"] = "inferred",
            ["scope"] = PyNode.Copy(input["scope"]),
            ["source_refs"] = Refs(sourceList),
            ["entities"] = PyNode.Copy(entityList),
            ["routines"] = PyNode.Copy(PyNode.Get(input, "routines", new JsonArray())),
            ["sensitivity"] = "private",
            ["allowed_uses"] = new JsonArray("reasoning"),
            ["metadata"] = new JsonObject
            {
                ["trend_type"] = PyNode.Copy(name),
                ["definition"] = definition,
                ["definition_revision"] = definitionRevision,
                ["event_ids"] = PyNode.Copy(eventIds),
                ["independent_event_ids"] = PyNode.Copy(independentIds),
                ["resolved_events"] = new JsonArray(resolved.Select(row => (JsonNode?)PyNode.Copy(row)).ToArray()),
                ["window_start"] = start,
                ["window_end"] = end,
                ["coverage"] = PyNode.Copy(covered["status"]),
                ["coverage_fraction"] = fraction,
                ["expected_population"] = PyNode.Copy(covered["expected_population"]),
                ["population"] = PyNode.Copy(denominator),
                ["observations"] = count,
                ["rate"] = PyNumbers.TrueDivide(count, population),
                ["independence"] = independence,
                ["status"] = "hypothesis_not_confirmed",
                ["rate_scope"] = "observed_population_only",
                ["no_execution_authority"] = true,
            },
        };
        var normal = NormaliseProposal(memory, record);
        var prior = Prior(memory, key, PyNode.AsString(normal["domain"])!);
        if (prior is not null)
        {
            if (Eq(Data(prior), normal) && (revision is null || (PyJson.IsInt(revision) && Eq(revision, Item(prior, "revision")))))
            {
                return prior;
            }

            Conditional(prior, revision);
            if (!Eq(Item(prior, "status"), "candidate"))
            {
                throw new StateError("reviewed or rejected trends cannot be overwritten by aggregation");
            }

            // {event["id"]: event for event in ...}: a Python dict keyed by any hashable id. Every entry counts below, not
            // only string ids: 1, True and 1.0 are one key (the last event wins), and each surviving event's outcome
            // feeds independent_outcomes even when its id can never equal a reported (string) id.
            var previous = new PyDict<JsonNode?>();
            foreach (var entry in PyNode.Iter(Get(Item(prior, "metadata"), "resolved_events", new JsonArray())))
            {
                previous.Set(Item(entry, "id"), entry);
            }

            if (events.Values.Any(entry => previous.TryGetValue(entry["id"], out var old) && !Eq(entry, old)))
            {
                throw new StateError("resolved event changed; review the contradictory evidence separately");
            }

            var outcomes = new PyDict<JsonNode?>();
            foreach (var (_, entry) in previous.Items())
            {
                var independenceKey = Item(entry, "independence_key");
                outcomes.Set(independenceKey, Item(entry, "matched"));
            }

            if (groups.Any(pair => outcomes.TryGetValue(pair.Key, out var old) && !Eq(JsonValue.Create(pair.Value), old)))
            {
                throw new StateError("independent event outcome changed; review the contradictory evidence separately");
            }
        }

        return memory.Put(key, normal, "candidate", PyNode.Copy(revision));
    }
}

using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Memory;
using Margo.Cloud.State.Proactive;
using Margo.Cloud.State.Tasks;

namespace Margo.Cloud.State.MemoryCommands;

/// <summary>
/// <c>memory_dream.py</c>: opted-in session checkpoints and bounded manual reflection in the memory owner. The host
/// supplies observations and reasoning; nothing here discovers host history, calls a model, or treats a citation as
/// proof that an interpretation is true. Every function takes the open <see cref="MemoryStore"/> first, as in Python;
/// model-supplied values are <see cref="JsonNode"/>s so Python's dynamic errors are reproduced. The source-identity
/// helpers (<c>text</c>, <c>source_keys</c>, <c>validate_source</c>) live in State.Memory's <see cref="DreamSources"/>,
/// which the store itself needs.
/// </summary>
public static class MemoryDream
{
    /// <summary>The module docstring.</summary>
    public const string Doc = "Opted-in session checkpoints and bounded manual reflection in the memory owner.\n\n"
                              + "The host supplies observations and reasoning. This module never discovers host history,\n"
                              + "calls a model, or treats a citation as proof that an interpretation is true.\n";

    /// <summary><c>MAX_INPUT</c>: the character budget of a Dream input (also <c>memory_state.py</c>'s <c>load</c> bound).</summary>
    public const int MaxInput = 100000;

    /// <summary><c>MAX_EVENTS</c>.</summary>
    public const int MaxEvents = 100;

    /// <summary><c>MAX_CLAIMS</c>.</summary>
    public const int MaxClaims = 20;

    /// <summary><c>MAX_PACKET_CHARS</c>.</summary>
    public const int MaxPacketChars = 14000;

    /// <summary><c>INTERPRETATIONS</c>.</summary>
    public static readonly IReadOnlySet<string> Interpretations =
        new HashSet<string>(["hypothesis", "decision", "rationale", "alternative", "direction", "question", "discussion"], StringComparer.Ordinal);

    private static readonly AsyncLocal<int?> MaxScanOverride = new();

    private static readonly string[] SnapshotFields = ["snapshot_hash", "request", "coverage", "policy_revision"];

    /// <summary>
    /// <c>MAX_SCAN</c> (500). Python's tests patch the module constant (<c>patch.object(dream, "MAX_SCAN", 1)</c>);
    /// <see cref="OverrideMaxScan"/> does the same for the current async flow.
    /// </summary>
    public static int MaxScan => MaxScanOverride.Value ?? 500;

    /// <summary>Replaces <see cref="MaxScan"/> for the current async flow (null restores the constant).</summary>
    public static void OverrideMaxScan(int? value) => MaxScanOverride.Value = value;

    // ------------------------------------------------------------------------------ validation

    /// <summary><c>bounded(value, fields)</c>: exactly these keys, at most 100000 characters of canonical JSON.</summary>
    public static JsonObject Bounded(JsonNode? value, string fields)
    {
        var names = fields.Split(' ');
        if (value is not JsonObject obj || !obj.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal).SetEquals(names))
        {
            throw new StateError("Dream input requires exactly: " + fields);
        }

        if (PyRepr.Length(StateStore.CanonicalJson(obj)) > MaxInput)
        {
            throw new StateError("Dream input exceeds 100000 characters");
        }

        return obj;
    }

    /// <summary><c>text(value, field, maximum=1000)</c> (<see cref="DreamSources.Text"/>).</summary>
    public static string Text(JsonNode? value, string field, int maximum = 1000) => DreamSources.Text(value, field, maximum);

    /// <summary><c>scope_for(account, host, workspace)</c>.</summary>
    public static string ScopeFor(JsonNode? account, JsonNode? host, JsonNode? workspace)
    {
        Text(account, "account", 200);
        Text(host, "host", 200);
        Text(workspace, "workspace", 200);
        return "dream:" + MemoryGovernance.Digest(new JsonArray(PyNode.Copy(account), PyNode.Copy(host), PyNode.Copy(workspace)));
    }

    /// <summary><c>capability(account, value)</c>: an authorised current-session Margo checkpoint adapter.</summary>
    public static JsonObject Capability(string account, JsonNode? value)
    {
        var cap = Bounded(value, "account host workspace adapter content_access session_list authorized");
        if (!PyJson.PyEquals(cap["account"], JsonValue.Create(account)))
        {
            throw new StateError("checkpoint account differs from the configured account");
        }

        ScopeFor(account, cap["host"], cap["workspace"]);
        if (!PyJson.PyEquals(cap["adapter"], JsonValue.Create("margo-session-checkpoint-v1")))
        {
            throw new StateError("unsupported history adapter; only explicit Margo session checkpoints are supported");
        }

        if (!PyJson.PyEquals(cap["content_access"], JsonValue.Create("current-session")) || !IsTrue(cap["authorized"]))
        {
            throw new StateError("authorized current-session content access is required; session listing is not content access");
        }

        if (!PyJson.IsBool(cap["session_list"]))
        {
            throw new StateError("session_list must be boolean");
        }

        return cap;
    }

    /// <summary><c>_data(...)</c>: a Dream memory record.</summary>
    public static JsonObject Data(string title, JsonNode? body, JsonNode? scope, JsonNode? refs, JsonNode? metadata, JsonNode? entities = null,
                                  JsonNode? uses = null, JsonNode? sensitivity = null, string authority = "source_observed", string kind = "episode",
                                  bool usesGiven = false, bool sensitivityGiven = false) => new()
    {
        ["domain"] = "user",
        ["kind"] = kind,
        ["title"] = title,
        ["text"] = PyNode.Copy(body),
        ["scope"] = PyNode.Copy(scope),
        ["authority"] = authority,
        ["source_refs"] = PyNode.Copy(refs),
        ["sensitivity"] = sensitivityGiven ? PyNode.Copy(sensitivity) : "private",
        ["allowed_uses"] = usesGiven && uses is not null ? PyNode.Copy(uses) : new JsonArray("reasoning"),
        ["entities"] = PyJson.Truthy(entities) ? PyNode.Copy(entities) : new JsonArray(),
        ["metadata"] = PyNode.Copy(metadata),
    };

    // ------------------------------------------------------------------------------ checkpoint

    /// <summary><c>checkpoint(memory, value)</c>: capture or replay one opted-in session checkpoint.</summary>
    public static JsonObject Checkpoint(MemoryStore memory, JsonNode? input)
    {
        ArgumentNullException.ThrowIfNull(memory);
        var value = Bounded(input, "capability session event revision expected_revision timestamp speaker locator text "
                                   + "origin sensitivity allowed_uses entities work_refs");
        var cap = Capability(memory.Account, value["capability"]);
        if (!PyJson.PyEquals(value["origin"], JsonValue.Create("margo-session")))
        {
            throw new StateError("Dream outputs, Dream runs and imported history are not session evidence");
        }

        foreach (var field in new[] { "session", "event", "revision", "speaker", "locator" })
        {
            Text(value[field], field, field == "locator" ? 1000 : 200);
        }

        if (!PyNode.IsOneOf(value["speaker"], "user", "assistant", "tool"))
        {
            throw new StateError("speaker must be user, assistant or tool");
        }

        var expected = value["expected_revision"];
        if (expected is not null && (!PyJson.IsInt(expected) || PyJson.AsInt(expected) < 1))
        {
            throw new StateError("expected_revision must be null or a positive memory revision");
        }

        var observed = StateStore.ValidateTimestamp(value["timestamp"]);
        if (PyDateTime.FromIsoformat(observed) > PyDateTime.Now())
        {
            throw new StateError("event timestamp is in the future");
        }

        Text(value["text"], "checkpoint text", 6000);
        if (value["work_refs"] is not JsonArray workRefs || workRefs.Count > 20)
        {
            throw new StateError("work_refs must be a bounded list of exact canonical IDs");
        }

        if (workRefs.Count > 0)
        {
            var tasks = TaskStore.FromConnection(memory.Conn, memory.Account);
            foreach (var identity in workRefs)
            {
                tasks.Ledger.Show(Text(identity, "canonical work ID", 200));
            }
        }

        var identityList = new JsonArray(memory.Account, PyNode.Copy(cap["host"]), PyNode.Copy(cap["workspace"]), PyNode.Copy(value["session"]),
                                         PyNode.Copy(value["event"]));
        var identityText = StateStore.CanonicalJson(identityList);
        var sourceList = (JsonArray)StateStore.ParseJson(identityText)!;
        sourceList.Add(PyNode.Copy(value["locator"]));
        var source = StateStore.CanonicalJson(sourceList);
        var key = "dream-event:" + MemoryGovernance.Digest(JsonValue.Create(identityText));
        return memory.Transaction(() =>
        {
            var previous = memory.Conn.Execute("SELECT id FROM memory_records WHERE id=? AND account=?",
                                               memory.RecordId("user", key), memory.Account).FetchOne();
            var old = previous is not null ? memory.Show(previous["id"] as string) : null;
            var meta = new JsonObject
            {
                ["dream"] = "checkpoint",
                ["host"] = PyNode.Copy(cap["host"]),
                ["workspace"] = PyNode.Copy(cap["workspace"]),
                ["session"] = PyNode.Copy(value["session"]),
                ["event"] = PyNode.Copy(value["event"]),
                ["speaker"] = PyNode.Copy(value["speaker"]),
                ["locator"] = PyNode.Copy(value["locator"]),
                ["timestamp"] = observed,
                ["origin"] = PyNode.Copy(value["origin"]),
                ["adapter"] = PyNode.Copy(cap["adapter"]),
                ["work_refs"] = PyNode.Copy(value["work_refs"]),
            };
            var refs = new JsonArray(new JsonObject
            {
                ["kind"] = "session_checkpoint",
                ["ref"] = source,
                ["revision"] = PyNode.Copy(value["revision"]),
            });
            var data = Data("Session checkpoint", value["text"], ScopeFor(memory.Account, cap["host"], cap["workspace"]), refs, meta,
                            value["entities"], value["allowed_uses"], value["sensitivity"], usesGiven: true, sensitivityGiven: true);
            if (old is not null)
            {
                if (!PyJson.PyEquals(PyNode.Index(old, "status"), JsonValue.Create("active")))
                {
                    throw new StateError("checkpoint is not active; inspect/review it rather than reactivating by capture");
                }

                var prior = PyNode.Index(Subscript(PyNode.Index(old, "source_refs"), 0), "revision");
                if (PyJson.PyEquals(prior, value["revision"]))
                {
                    var cleanMeta = PyNode.CopyObject(meta);
                    cleanMeta["capture_policy_revision"] = PyNode.Copy(PyNode.Index(PyNode.Index(old, "metadata"), "capture_policy_revision"));
                    var candidate = PyNode.CopyObject(data);
                    candidate["metadata"] = cleanMeta;
                    var clean = memory.NormaliseData(candidate);
                    foreach (var (field, _) in data)
                    {
                        if (!PyJson.PyEquals(PyNode.Index(clean, field), PyNode.Index(old, field)))
                        {
                            throw new StateError("same source revision has changed checkpoint content");
                        }
                    }

                    memory.ResolveWorkSources(clean);
                    MemoryGovernance.AuthorizeCapture(memory, clean);
                    return old;
                }

                if (!PyJson.PyEquals(value["expected_revision"], PyNode.Index(old, "revision")))
                {
                    throw new StateError("changed checkpoint requires its current expected_revision");
                }

                foreach (var revision in memory.Conn.Execute("SELECT data FROM memory_revisions WHERE memory_id=?", PyNode.Str(old["id"])).FetchAll())
                {
                    var stored = StateStore.ParseJson((string)revision["data"]!);
                    foreach (var reference in PyNode.Iter(PyNode.Get(PyNode.Dict(stored), "source_refs", new JsonArray())))
                    {
                        if (PyJson.PyEquals(PyNode.Index(reference, "kind"), JsonValue.Create("session_checkpoint"))
                            && PyJson.PyEquals(PyNode.Get(PyNode.Dict(reference), "revision"), value["revision"]))
                        {
                            throw new StateError("an older host revision cannot be replayed as a new checkpoint");
                        }
                    }
                }
            }

            return MemoryGovernance.Capture(memory, key, data, PyNode.Copy(value["expected_revision"]), PyNode.Copy(((JsonArray)data["source_refs"]!)[0]));
        });
    }

    // ------------------------------------------------------------------------------ window and currency

    /// <summary>
    /// <c>_window(value)</c>: the UTC start and end of the local daily window ending at <c>cutoff</c> on <c>day</c> in
    /// <c>timezone</c> (an IANA key; <c>UTC</c> needs no time zone database). Ambiguous or nonexistent local cutoffs and
    /// windows that have not ended are refused. <c>OverflowError</c>s at the limits of the datetime range escape, as in
    /// Python.
    /// </summary>
    public static (PyDateTime Start, PyDateTime End) Window(JsonNode? input)
    {
        var value = Bounded(input, "host workspace day timezone cutoff lookback_days after");
        Text(value["host"], "host", 200);
        Text(value["workspace"], "workspace", 200);
        var lookback = value["lookback_days"];
        if (!PyJson.IsInt(lookback) || PyJson.AsInt(lookback) < 0 || PyJson.AsInt(lookback) > 7)
        {
            throw new StateError("lookback_days must be between 0 and 7");
        }

        if (value["after"] is not null)
        {
            Text(value["after"], "scan cursor", 100);
        }

        try
        {
            var zone = PyJson.PyEquals(value["timezone"], JsonValue.Create("UTC")) ? PyZoneInfo.Utc : PyZoneInfo.Load(value["timezone"]);
            var day = PyDate.FromIsoformat(PyNode.AsString(value["day"]) ?? throw new PyTypeError("fromisoformat: argument must be str"));
            var cutoff = PyTimeOfDay.FromIsoformat(PyNode.AsString(value["cutoff"]) ?? throw new PyTypeError("fromisoformat: argument must be str"));
            if (cutoff.UtcOffset is not null || cutoff.Second != 0 || cutoff.Microsecond != 0)
            {
                throw new PyValueError("cutoff must be local HH:MM");
            }

            var start = PyDateTime.Combine(day, cutoff);
            var end = PyDateTime.Combine(day.AddDays(PyTimeDelta.FromDays(1)), cutoff);
            var utc = new List<PyDateTime>(2);
            foreach (var point in new[] { start, end })
            {
                var offset = zone.UtcOffset(point, 0);
                if (offset != zone.UtcOffset(point, 1))
                {
                    throw new PyValueError("ambiguous/nonexistent cutoff; choose another local time");
                }

                // point.astimezone(timezone.utc).astimezone(zone) != point (same tzinfo: the wall times are compared).
                var instant = (point - offset).WithOffset(PyTimeDelta.Zero);
                if (zone.FromUtc(instant) != point)
                {
                    throw new PyValueError("ambiguous/nonexistent cutoff; choose another local time");
                }

                utc.Add(instant);
            }

            if (utc[1] > PyDateTime.Now())
            {
                throw new PyValueError("the daily cutoff has not completed");
            }

            return (utc[0], utc[1]);
        }
        catch (Exception ex) when (ex is PyValueError or PyTypeError or ZoneInfoNotFoundError)
        {
            throw new StateError("invalid Dream daily window: " + ex.Message, ex);
        }
    }

    /// <summary><c>_current(memory, row)</c>: an active, observed, non-sensitive record usable for reasoning now.</summary>
    public static bool Current(MemoryStore memory, JsonNode? row)
    {
        ArgumentNullException.ThrowIfNull(memory);
        var record = PyNode.Dict(row);
        return PyJson.PyEquals(PyNode.Index(row, "status"), JsonValue.Create("active"))
               && PyJson.PyEquals(PyNode.Get(record, "authority"), JsonValue.Create("source_observed"))
               && !PyJson.PyEquals(PyNode.Get(record, "sensitivity"), JsonValue.Create("sensitive"))
               && Contains(PyNode.Get(record, "allowed_uses", new JsonArray()), JsonValue.Create("reasoning"))
               && PyObj.Le(PyNode.Get(record, "valid_from", JsonValue.Create(StateStore.UtcNow())), StateStore.UtcNow())
               && PyObj.Gt(PyNode.Get(record, "valid_to", JsonValue.Create("9999")), StateStore.UtcNow())
               && PyObj.Gt(PyNode.Get(record, "review_after", JsonValue.Create("9999")), StateStore.UtcNow())
               && memory.WorkSourcesCurrent(row);
    }

    /// <summary><c>_episode_current(memory, row, sources)</c>: a current Dream episode of one of the given (ref, revision) sources.</summary>
    public static bool EpisodeCurrent(MemoryStore memory, JsonNode? row, IReadOnlyList<(JsonNode? Ref, JsonNode? Revision)> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (!PyJson.PyEquals(PyNode.Get(PyNode.Dict(PyNode.Get(PyNode.Dict(row), "metadata", new JsonObject())), "dream"), JsonValue.Create("episode")))
        {
            return false;
        }

        var found = false;
        foreach (var reference in PyNode.Iter(PyNode.Index(row, "source_refs")))
        {
            if (PyJson.PyEquals(PyNode.Index(reference, "kind"), JsonValue.Create("memory_record"))
                && TupleIn(PyNode.Index(reference, "ref"), PyNode.Get(PyNode.Dict(reference), "revision"), sources))
            {
                found = true;
                break;
            }
        }

        return found && Current(memory, row);
    }

    // ------------------------------------------------------------------------------ plan, start, finish

    /// <summary><c>plan(memory, value)</c>: the bounded, coverage-labelled checkpoint intake of one daily window.</summary>
    public static JsonObject Plan(MemoryStore memory, JsonNode? input)
    {
        ArgumentNullException.ThrowIfNull(memory);
        var (start, end) = Window(input);
        var value = (JsonObject)input!;
        var scope = ScopeFor(memory.Account, value["host"], value["workspace"]);
        return memory.Transaction(() =>
        {
            var rules = memory.Policy();
            var capture = PyNode.Index(PyNode.Index(rules, "data"), "capture");
            if (!Contains(PyNode.Index(capture, "scopes"), JsonValue.Create(scope)) || !PyJson.Truthy(PyNode.Index(capture, "enabled")))
            {
                throw new StateError("Dream is not opted in for this exact account/host/workspace");
            }

            if (!Contains(PyNode.Index(capture, "domains"), JsonValue.Create("user"))
                || !SubsetOf(["episode", "decision"], PyNode.Index(capture, "kinds"))
                || !SubsetOf(["session_checkpoint", "memory_record"], PyNode.Index(capture, "source_kinds")))
            {
                throw new StateError("Dream requires user episode/decision kinds and session_checkpoint/memory_record capture");
            }

            var maxScan = MaxScan;
            var after = PyJson.Truthy(value["after"]) ? PyNode.Str(value["after"]) : "";
            var scanned = memory.Conn.Execute(
                "SELECT id FROM memory_records WHERE account=? AND domain='user' AND kind='episode' "
                + "AND status<>'forgotten' AND id>? ORDER BY id LIMIT ?",
                memory.Account, after, (long)maxScan + 1).FetchAll();
            var events = new List<JsonObject>();
            var excluded = 0L;
            var oversized = 0L;
            var lookback = PyTimeDelta.FromDays((long)PyJson.AsInt(value["lookback_days"]));
            foreach (var item in scanned.Take(maxScan))
            {
                var row = memory.Show(item["id"] as string);
                if (!PyJson.PyEquals(PyNode.Get(row, "scope"), JsonValue.Create(scope))
                    || !PyJson.PyEquals(PyNode.Get(PyNode.Dict(PyNode.Get(row, "metadata", new JsonObject())), "dream"), JsonValue.Create("checkpoint")))
                {
                    continue;
                }

                JsonNode? source = null;
                foreach (var reference in PyNode.Iter(PyNode.Index(row, "source_refs")))
                {
                    if (PyJson.PyEquals(PyNode.Index(reference, "kind"), JsonValue.Create("session_checkpoint")))
                    {
                        source = reference;
                        break;
                    }
                }

                if (source is null || !PyJson.PyEquals(PyNode.Index(row, "id"), JsonValue.Create(memory.RecordId(
                        "user", "dream-event:" + MemoryGovernance.Digest(JsonValue.Create(StateStore.CanonicalJson(
                            Head(ParseRef(PyNode.Index(source, "ref")), 5))))))))
                {
                    excluded++;
                    continue;
                }

                var timestamp = PyNode.Index(PyNode.Index(row, "metadata"), "timestamp");
                var observed = PyDateTime.FromIsoformat(PyNode.AsString(timestamp) ?? throw new PyTypeError("fromisoformat: argument must be str"));
                if (!((start - lookback) <= observed && observed < end))
                {
                    continue;
                }

                if (!Current(memory, row))
                {
                    excluded++;
                    continue;
                }

                if (PyRepr.Length(StateStore.CanonicalJson(row)) > MaxPacketChars)
                {
                    excluded++;
                    oversized++;
                    continue;
                }

                events.Add(row);
            }

            var over = events.Count > MaxEvents;
            if (over)
            {
                events = events.Take(MaxEvents).ToList();
            }

            var packet = new List<JsonObject>();
            var size = 0L;
            foreach (var row in events)
            {
                var cost = PyRepr.Length(StateStore.CanonicalJson(row));
                if (size + cost > MaxPacketChars)
                {
                    over = true;
                    break;
                }

                size += cost;
                packet.Add(row);
            }

            JsonNode? cursor = over && packet.Count > 0
                ? PyNode.Copy(packet[^1]["id"])
                : scanned.Count > maxScan ? JsonValue.Create((string)scanned[maxScan - 1]["id"]!) : null;
            packet = PySort.SortedBy(packet, row => (object?)new List<object?>
            {
                PyNode.Index(PyNode.Index(row, "metadata"), "timestamp"),
                PyNode.Index(row, "id"),
            });
            var coverage = new JsonObject
            {
                ["status"] = "partial",
                ["source"] = "opted-in-checkpoints-only",
                ["history_access"] = "unsupported",
                ["window"] = new JsonObject { ["start"] = start.Isoformat(), ["end"] = end.Isoformat() },
                ["timezone"] = PyNode.Copy(value["timezone"]),
                ["cutoff"] = PyNode.Copy(value["cutoff"]),
                ["lookback_days"] = PyNode.Copy(value["lookback_days"]),
                ["excluded"] = excluded,
                ["excluded_oversized"] = oversized,
                ["scan_truncated"] = scanned.Count > maxScan,
                ["intake_truncated"] = over,
                ["next_cursor"] = cursor,
                ["pagination"] = "not-enumerated",
                ["late_arrivals"] = "bounded-lookback; rerun with a new key",
                ["observed_events"] = (long)packet.Count,
            };
            var refs = new JsonArray(packet.Select(row => (JsonNode?)new JsonObject
            {
                ["kind"] = "memory_record",
                ["ref"] = PyNode.Copy(row["id"]),
                ["revision"] = PyNode.Str(PyNode.Index(row, "revision")),
            }).ToArray());
            var result = new JsonObject
            {
                ["account"] = memory.Account,
                ["scope"] = scope,
                ["request"] = PyNode.Copy(value),
                ["policy_revision"] = PyNode.Copy(PyNode.Index(rules, "revision")),
                ["coverage"] = coverage,
                ["source_refs"] = refs,
            };
            var hash = MemoryGovernance.Digest(result);
            result["snapshot_hash"] = hash;
            result["events"] = new JsonArray(packet.Select(row => (JsonNode?)row).ToArray());
            return result;
        });
    }

    /// <summary><c>start(memory, key, value)</c>: claim one manual reflection and reserve its host model budget.</summary>
    public static JsonObject Start(MemoryStore memory, JsonNode? key, JsonNode? input)
    {
        ArgumentNullException.ThrowIfNull(memory);
        var value = Bounded(input, "request snapshot_hash request_ref");
        var runKey = Text(key, "Dream run key", 200);
        var tasks = TaskStore.FromConnection(memory.Conn, memory.Account);
        return memory.Transaction(() =>
        {
            var snapshot = Plan(memory, value["request"]);
            if (!PyJson.PyEquals(snapshot["snapshot_hash"], value["snapshot_hash"]))
            {
                throw new StateError("Dream intake or policy changed; inspect a fresh plan");
            }

            var sourceRefs = (JsonArray)snapshot["source_refs"]!;
            if (sourceRefs.Count == 0)
            {
                throw new StateError("no eligible checkpoints; no reflection was completed");
            }

            var snapshotKey = "dream-snapshot:" + runKey;
            var snapshotId = memory.RecordId("user", snapshotKey);
            var previous = memory.Conn.Execute("SELECT id FROM memory_records WHERE id=? AND account=?", snapshotId, memory.Account).FetchOne();
            JsonObject saved;
            JsonObject run;
            if (previous is not null)
            {
                saved = memory.Show(snapshotId);
                var savedHash = PyNode.Get(PyNode.Dict(PyNode.Get(saved, "metadata", new JsonObject())), "snapshot_hash");
                if (!PyJson.PyEquals(savedHash, snapshot["snapshot_hash"]))
                {
                    throw new StateError("run key already exists or was forgotten; inspect it, never overwrite");
                }

                run = tasks.Show(PyNode.Copy(PyNode.Index(PyNode.Index(saved, "metadata"), "task_id")));
            }
            else
            {
                var now = PyDateTime.Now();
                var request = (JsonObject)value["request"]!;
                var environment = new JsonObject
                {
                    ["account"] = memory.Account,
                    ["host"] = PyNode.Copy(request["host"]),
                    ["capabilities"] = new JsonObject { ["local.dream"] = "checkpoint-v1" },
                    ["observed_at"] = now.Isoformat(),
                };
                var cost = TaskRuns.Zero();
                cost["model_calls"] = 1L;
                cost["items"] = (long)sourceRefs.Count;
                cost["output_chars"] = 24000L;
                var limits = PyNode.CopyObject(cost);
                limits["max_steps"] = 1L;
                limits["max_attempts_per_step"] = 2L;
                limits["max_parallel"] = 1L;
                limits["deadline_at"] = (now + PyTimeDelta.FromHours(1)).Isoformat();
                limits["lease_seconds"] = 900L;
                var plan = new JsonObject
                {
                    ["goal"] = "Reflect on opted-in checkpoints",
                    ["routine"] = "dream",
                    ["request_ref"] = PyNode.Copy(value["request_ref"]),
                    ["mode"] = "foreground",
                    ["environment"] = environment,
                    ["window"] = PyNode.Copy(snapshot["coverage"]!["window"]),
                    ["limits"] = limits,
                    ["steps"] = new JsonArray(new JsonObject
                    {
                        ["key"] = "reflect",
                        ["title"] = "Prepare sourced reflection",
                        ["kind"] = "local",
                        ["capability"] = "local.dream",
                        ["depends_on"] = new JsonArray(),
                        ["allow_partial"] = false,
                        ["cost"] = cost,
                    }),
                };
                run = tasks.Create("dream:" + MemoryGovernance.Digest(new JsonArray(memory.Account, runKey)), plan);
                var meta = new JsonObject();
                foreach (var name in SnapshotFields)
                {
                    meta[name] = PyNode.Copy(snapshot[name]);
                }

                meta["dream"] = "snapshot";
                meta["task_id"] = PyNode.Copy(run["id"]);
                saved = memory.Capture(snapshotKey, Data("Dream collection snapshot", "Bounded checkpoint selection.", snapshot["scope"],
                                                         sourceRefs, meta));
            }

            if (PyJson.PyEquals(run["state"], JsonValue.Create("completed")))
            {
                return Inspect(memory, saved["id"]);
            }

            var step = PyNode.Index(Subscript(PyNode.Index(run, "steps"), 0), "revision");
            var binding = PyNode.CopyObject(PyNode.Index(PyNode.Index(run, "plan"), "environment"));
            binding["observed_at"] = StateStore.UtcNow();
            var claim = tasks.Start(PyNode.Copy(run["id"]), "reflect", PyNode.Copy(step), PyNode.Copy(run["plan_hash"]), binding);
            var charge = TaskRuns.Zero();
            charge["model_calls"] = 1L;
            charge["items"] = (long)sourceRefs.Count;
            var grant = tasks.Charge(PyNode.Copy(claim["attempt_id"]), PyNode.Copy(claim["token"]), "host-reflection", charge);
            return new JsonObject
            {
                ["snapshot_id"] = PyNode.Copy(saved["id"]),
                ["claim"] = claim,
                ["model_grant"] = grant,
                ["events"] = PyNode.Copy(snapshot["events"]),
                ["coverage"] = PyNode.Copy(snapshot["coverage"]),
                ["instructions"] = "Reason outside transactions; submit only cited candidate interpretations.",
            };
        });
    }

    /// <summary><c>finish(memory, value)</c>: persist sourced episodes and cited candidate interpretations of a claimed run.</summary>
    public static JsonObject Finish(MemoryStore memory, JsonNode? input)
    {
        ArgumentNullException.ThrowIfNull(memory);
        var value = Bounded(input, "snapshot_id attempt_id token claims");
        if (value["claims"] is not JsonArray claims || claims.Count > MaxClaims || PyRepr.Length(StateStore.CanonicalJson(claims)) > 8000)
        {
            throw new StateError("reflection accepts at most 20 claims and 8000 characters");
        }

        var tasks = TaskStore.FromConnection(memory.Conn, memory.Account);
        return memory.Transaction(() =>
        {
            var snapshot = memory.Show(PyNode.Copy(value["snapshot_id"]));
            var meta = PyNode.Get(snapshot, "metadata", new JsonObject());
            if (!PyJson.PyEquals(PyNode.Get(PyNode.Dict(meta), "dream"), JsonValue.Create("snapshot")) || !Current(memory, snapshot))
            {
                throw new StateError("Dream snapshot is unavailable, stale, suppressed or forgotten");
            }

            if (!PyJson.PyEquals(memory.Policy()["revision"], PyNode.Index(meta, "policy_revision")))
            {
                throw new StateError("capture policy changed during reflection; prepare a new run");
            }

            var run = tasks.Show(PyNode.Copy(PyNode.Index(meta, "task_id")));
            var attempt = PyNode.Get(PyNode.Dict(Subscript(PyNode.Index(run, "steps"), 0)), "latest_attempt");
            if (!PyJson.PyEquals(attempt, value["attempt_id"]))
            {
                throw new StateError("claim belongs to another Dream run");
            }

            var events = PyNode.Iter(PyNode.Index(snapshot, "source_refs")).Select(reference => memory.Show(PyNode.Copy(PyNode.Index(reference, "ref")))).ToList();
            var byId = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var row in events)
            {
                byId[PyNode.Str(row["id"])] = row;
            }

            var prepared = new List<(JsonObject Claim, List<JsonObject> Sources)>();
            foreach (var item in claims)
            {
                var claim = Bounded(item, "key type text citations");
                Text(claim["key"], "claim key", 100);
                Text(claim["text"], "claim text", 2000);
                if (!PyNode.In(claim["type"], Interpretations))
                {
                    throw new StateError("unsupported interpretation; preferences/lessons require separate reviewed workflows");
                }

                if (claim["citations"] is not JsonArray citations || citations.Count < 1 || citations.Count > 10)
                {
                    throw new StateError("every claim requires 1..10 source-locatable citations");
                }

                var sources = new List<JsonObject>();
                foreach (var entry in citations)
                {
                    var citation = Bounded(entry, "id revision quote");
                    PyNode.RequireHashable(citation["id"]);
                    var row = PyNode.AsString(citation["id"]) is { } id && byId.TryGetValue(id, out var found) ? found : null;
                    if (row is null || !PyJson.IsInt(citation["revision"]) || !PyJson.PyEquals(citation["revision"], row["revision"]))
                    {
                        throw new StateError("citation is outside the exact checkpoint snapshot");
                    }

                    var quote = Text(citation["quote"], "citation quote", 2000);
                    if (PyNode.AsString(PyNode.Index(row, "text")) is not { } body
                        ? !PyObj.Contains(PyNode.Index(row, "text"), quote)
                        : !StrContains(body, quote))
                    {
                        throw new StateError("citation quote is not present; this check does not prove entailment");
                    }

                    sources.Add(row);
                }

                prepared.Add((claim, sources));
            }

            if (prepared.Select(pair => PyNode.Str(pair.Claim["key"])).Distinct(StringComparer.Ordinal).Count() != prepared.Count)
            {
                throw new StateError("duplicate claim keys");
            }

            var payloadHash = MemoryGovernance.Digest(claims);
            var snapshotId = PyNode.Str(snapshot["id"]);
            var receipt = new JsonObject { ["kind"] = "local_result", ["reference"] = snapshotId + ":" + payloadHash };
            if (PyJson.PyEquals(run["state"], JsonValue.Create("completed")))
            {
                tasks.Finish(PyNode.Copy(value["attempt_id"]), PyNode.Copy(value["token"]), "succeeded", receipt);
                return Inspect(memory, snapshotId);
            }

            var persist = TaskRuns.Zero();
            persist["output_chars"] = 24000L;
            tasks.Charge(PyNode.Copy(value["attempt_id"]), PyNode.Copy(value["token"]), "persist:" + payloadHash, persist);
            var outputs = new JsonArray();
            var request = PyNode.Index(meta, "request");
            JsonObject Requirements() => new()
            {
                ["account"] = memory.Account,
                ["host"] = PyNode.Copy(PyNode.Index(request, "host")),
                ["workspace"] = PyNode.Copy(PyNode.Index(request, "workspace")),
            };

            // Episodes are exact observations, not model-written summaries or confirmations.
            foreach (var @event in events)
            {
                var eventId = PyNode.Str(@event["id"]);
                var eventRevision = PyNode.Str(PyNode.Index(@event, "revision"));
                var refs = new JsonArray(new JsonObject { ["kind"] = "memory_record", ["ref"] = eventId, ["revision"] = eventRevision });
                var key = "dream-episode:" + eventId + ":" + eventRevision;
                var episodeId = memory.RecordId("user", key);
                var previous = memory.Conn.Execute("SELECT id FROM memory_records WHERE id=? AND account=?", episodeId, memory.Account).FetchOne();
                if (previous is not null)
                {
                    var episode = memory.Show(episodeId);
                    if (EpisodeCurrent(memory, episode, [(JsonValue.Create(eventId), JsonValue.Create(eventRevision))]))
                    {
                        outputs.Add(episodeId);
                    }

                    continue;
                }

                // Restored deletion journals can retain an identity without a record.
                if (memory.Conn.Execute("SELECT 1 FROM memory_tombstones WHERE id=? AND account=?", episodeId, memory.Account).FetchOne() is not null)
                {
                    continue;
                }

                var eventMeta = PyNode.Index(@event, "metadata");
                var title = "Dream sourced episode: " + PyNode.Str(PyNode.Index(eventMeta, "speaker")) + " at "
                            + PyNode.Str(PyNode.Index(eventMeta, "timestamp"));
                var episodeMeta = new JsonObject
                {
                    ["dream"] = "episode",
                    ["chronology"] = PyNode.Copy(eventMeta),
                    ["environment_requirements"] = Requirements(),
                    ["work_refs"] = PyNode.Copy(PyNode.Index(eventMeta, "work_refs")),
                };
                var data = Data(title, PyNode.Index(@event, "text"), PyNode.Index(@event, "scope"), refs, episodeMeta, PyNode.Index(@event, "entities"),
                                PyNode.Index(@event, "allowed_uses"), PyNode.Index(@event, "sensitivity"), usesGiven: true, sensitivityGiven: true);
                outputs.Add(PyNode.Copy(memory.Capture(key, data)["id"]));
            }

            foreach (var (claim, sources) in prepared)
            {
                var unique = new List<JsonObject>();
                foreach (var row in sources)
                {
                    var index = unique.FindIndex(existing => PyJson.PyEquals(existing["id"], row["id"]));
                    if (index < 0)
                    {
                        unique.Add(row);
                    }
                    else
                    {
                        unique[index] = row;
                    }
                }

                var refs = new JsonArray(unique.Select(row => (JsonNode?)new JsonObject
                {
                    ["kind"] = "memory_record",
                    ["ref"] = PyNode.Copy(row["id"]),
                    ["revision"] = PyNode.Str(PyNode.Index(row, "revision")),
                }).ToArray());
                refs.Add(new JsonObject { ["kind"] = "memory_record", ["ref"] = snapshotId, ["revision"] = PyNode.Str(PyNode.Index(snapshot, "revision")) });
                var common = new List<string>();
                var first = true;
                foreach (var row in sources)
                {
                    var uses = PyNode.Iter(PyNode.Index(row, "allowed_uses")).Select(StrItem).ToList();
                    common = first ? uses.Distinct(StringComparer.Ordinal).ToList() : common.Where(uses.Contains).ToList();
                    first = false;
                }

                var sortedUses = PyText.Sorted(common);
                if (sortedUses.Count == 0)
                {
                    throw new StateError("cited sources have no common allowed use");
                }

                var sensitivity = sources.Any(row => PyJson.PyEquals(PyNode.Index(row, "sensitivity"), JsonValue.Create("private"))) ? "private" : "public";
                var workRefs = PyText.Sorted(sources.SelectMany(row => PyNode.Iter(PyNode.Index(PyNode.Index(row, "metadata"), "work_refs")).Select(StrItem))
                                                    .Distinct(StringComparer.Ordinal));
                var entities = PyText.Sorted(sources.SelectMany(row => PyNode.Iter(PyNode.Index(row, "entities")).Select(StrItem))
                                                    .Distinct(StringComparer.Ordinal));
                var type = PyNode.Str(claim["type"]);
                var claimMeta = new JsonObject
                {
                    ["dream"] = "interpretation",
                    ["snapshot_id"] = snapshotId,
                    ["type"] = type,
                    ["citations"] = PyNode.Copy(claim["citations"]),
                    ["environment_requirements"] = Requirements(),
                    ["work_refs"] = PyNode.StrList(workRefs),
                };
                var data = Data("Dream candidate: " + type, claim["text"], snapshot["scope"], refs, claimMeta, PyNode.StrList(entities),
                                PyNode.StrList(sortedUses), sensitivity, "inferred", type == "decision" ? "decision" : "episode",
                                usesGiven: true, sensitivityGiven: true);
                outputs.Add(PyNode.Copy(memory.Capture("dream-claim:" + snapshotId + ":" + PyNode.Str(claim["key"]), data)["id"]));
            }

            // Publication content is a pointer only. Erasable prose stays exclusively in memory.
            ProactiveState.PublicationRecord(memory.Conn, null, new JsonObject
            {
                ["id"] = "dream:" + snapshotId,
                ["content"] = snapshotId,
                ["status"] = "prepared",
            });
            tasks.Finish(PyNode.Copy(value["attempt_id"]), PyNode.Copy(value["token"]), "succeeded", receipt);
            var result = Inspect(memory, snapshotId);
            result["output_ids"] = outputs;
            return result;
        });
    }

    // ------------------------------------------------------------------------------ inspect and status

    /// <summary><c>inspect(memory, snapshot_id)</c>: collection, reflection, outputs, source currency and availability.</summary>
    public static JsonObject Inspect(MemoryStore memory, JsonNode? snapshotId)
    {
        ArgumentNullException.ThrowIfNull(memory);
        var snapshot = memory.Show(snapshotId);
        var meta = PyNode.Get(snapshot, "metadata", new JsonObject());
        if (!PyJson.PyEquals(PyNode.Get(PyNode.Dict(meta), "dream"), JsonValue.Create("snapshot")))
        {
            return new JsonObject
            {
                ["snapshot_id"] = PyNode.Copy(snapshotId),
                ["status"] = PyNode.Copy(PyNode.Index(snapshot, "status")),
                ["available"] = false,
            };
        }

        var tasks = TaskStore.FromConnection(memory.Conn, memory.Account);
        var run = tasks.Show(PyNode.Copy(PyNode.Index(meta, "task_id")));
        var maxScan = MaxScan;
        var rows = memory.Conn.Execute("SELECT id FROM memory_records WHERE account=? AND status<>'forgotten' ORDER BY id LIMIT ?",
                                       memory.Account, (long)maxScan + 1).FetchAll();
        var candidates = rows.Take(maxScan).Select(row => memory.Show(row["id"] as string)).ToList();
        var eventSources = new List<(JsonNode? Ref, JsonNode? Revision)>();
        foreach (var reference in PyNode.Iter(PyNode.Index(snapshot, "source_refs")))
        {
            if (PyJson.PyEquals(PyNode.Index(reference, "kind"), JsonValue.Create("memory_record")))
            {
                var pair = (PyNode.Index(reference, "ref"), PyNode.Get(PyNode.Dict(reference), "revision"));
                PyNode.RequireHashable(pair.Item1);
                PyNode.RequireHashable(pair.Item2);
                if (!TupleIn(pair.Item1, pair.Item2, eventSources))
                {
                    eventSources.Add(pair);
                }
            }
        }

        var outputs = new JsonArray();
        foreach (var row in candidates)
        {
            if (PyJson.PyEquals(PyNode.Get(PyNode.Dict(PyNode.Get(row, "metadata", new JsonObject())), "snapshot_id"), snapshotId)
                || EpisodeCurrent(memory, row, eventSources))
            {
                outputs.Add(row);
            }
        }

        var current = Current(memory, snapshot) && PyJson.PyEquals(memory.Policy()["revision"], PyNode.Index(meta, "policy_revision"));
        var completed = PyJson.PyEquals(run["state"], JsonValue.Create("completed"));
        var id = PyNode.Str(snapshotId);
        var publication = completed ? ProactiveState.PublicationShow(memory.Conn, "dream:" + id) : null;
        return new JsonObject
        {
            ["snapshot_id"] = PyNode.Copy(snapshotId),
            ["task_id"] = PyNode.Copy(run["id"]),
            ["collection"] = PyNode.Copy(PyNode.Index(meta, "coverage")),
            ["reflection"] = PyNode.Copy(run["state"]),
            ["sources_current"] = current,
            ["availability"] = current && publication is not null ? PyNode.Copy(PyNode.Index(publication, "status")) : "unavailable",
            ["human_review"] = publication is not null && PyJson.PyEquals(PyNode.Index(publication, "status"), JsonValue.Create("reviewed"))
                ? "output-reviewed"
                : "not-established",
            ["outputs"] = outputs,
            ["inspection_truncated"] = rows.Count > maxScan,
            ["output_receipt"] = "dream:" + id,
            ["outbound_actions"] = false,
        };
    }

    /// <summary><c>status(memory, host, workspace)</c>: exact scope and opt-in; no setup or history reads.</summary>
    public static JsonObject Status(MemoryStore memory, string host, string workspace)
    {
        ArgumentNullException.ThrowIfNull(memory);
        var scope = ScopeFor(memory.Account, host, workspace);
        var rules = memory.Policy();
        var capture = PyNode.Index(PyNode.Index(rules, "data"), "capture");
        var enabled = PyNode.Index(capture, "enabled");
        // capture["enabled"] and ...: Python's `and` returns the first falsy operand, else the last.
        JsonNode? optedIn = PyJson.Truthy(enabled)
            ? Contains(PyNode.Index(capture, "scopes"), JsonValue.Create(scope))
              && Contains(PyNode.Index(capture, "domains"), JsonValue.Create("user"))
              && SubsetOf(["episode", "decision"], PyNode.Index(capture, "kinds"))
              && SubsetOf(["session_checkpoint", "memory_record"], PyNode.Index(capture, "source_kinds"))
            : PyNode.Copy(enabled);
        return new JsonObject
        {
            ["account"] = memory.Account,
            ["host"] = host,
            ["workspace"] = workspace,
            ["scope"] = scope,
            ["capture_policy"] = rules,
            ["opted_in"] = optedIn,
            ["adapter"] = "margo-session-checkpoint-v1",
            ["history_access"] = "unsupported",
            ["schedule"] = "not-installed",
            ["review"] = "memory_state.py inspect/revise/consolidate",
        };
    }

    // ------------------------------------------------------------------------------ Python helpers

    /// <summary><c>x is True</c>.</summary>
    private static bool IsTrue(JsonNode? value) => PyJson.IsBool(value) && PyJson.Truthy(value);

    /// <summary><c>item in container</c> for a list (Python equality), dict (keys) or string (substring).</summary>
    private static bool Contains(JsonNode? container, JsonNode? item) => container switch
    {
        JsonArray list => PyNode.InList(item, list),
        _ => PyObj.Contains(container, item),
    };

    /// <summary><c>{a, b} &lt;= set(values)</c>: <c>set()</c> hashes every element first.</summary>
    private static bool SubsetOf(string[] required, JsonNode? values)
    {
        var items = PyNode.Iter(values);
        foreach (var item in items)
        {
            PyNode.RequireHashable(item);
        }

        return required.All(name => items.Any(item => PyNode.Is(item, name)));
    }

    /// <summary>
    /// <c>needle in haystack</c> for two Python strings, by code points. An ordinal UTF-16 search would also match a
    /// lone surrogate (a JSON <c>\udXXX</c> escape) against half of an astral character's surrogate pair; Python's
    /// strings hold that character as one code point, so such a match is not a substring there. Belongs in State.Core
    /// (<c>PyObj.Contains</c> on strings has the same UTF-16 behaviour; see PORTING.md).
    /// </summary>
    internal static bool StrContains(string haystack, string needle)
    {
        ArgumentNullException.ThrowIfNull(haystack);
        ArgumentNullException.ThrowIfNull(needle);
        for (var start = haystack.IndexOf(needle, StringComparison.Ordinal); start >= 0;
             start = start + 1 > haystack.Length ? -1 : haystack.IndexOf(needle, start + 1, StringComparison.Ordinal))
        {
            var end = start + needle.Length;
            var splitsStart = start > 0 && char.IsHighSurrogate(haystack[start - 1]) && char.IsLowSurrogate(haystack[start]);
            var splitsEnd = end > start && end < haystack.Length && char.IsHighSurrogate(haystack[end - 1]) && char.IsLowSurrogate(haystack[end]);
            if (!splitsStart && !splitsEnd)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary><c>(ref, revision) in sources</c> for a set of tuples (both parts hashed, compared by Python equality).</summary>
    private static bool TupleIn(JsonNode? reference, JsonNode? revision, IReadOnlyList<(JsonNode? Ref, JsonNode? Revision)> sources)
    {
        PyNode.RequireHashable(reference);
        PyNode.RequireHashable(revision);
        return sources.Any(pair => PyJson.PyEquals(pair.Ref, reference) && PyJson.PyEquals(pair.Revision, revision));
    }

    /// <summary><c>value[index]</c> for a list (a non-list raises Python's error for the type).</summary>
    private static JsonNode? Subscript(JsonNode? value, int index) => value switch
    {
        JsonArray list when index < list.Count => list[index],
        JsonArray => throw new PyIndexError("list index out of range"),
        JsonObject => throw PyKeyError.ForRepr(index.ToString(CultureInfo.InvariantCulture)),
        _ when PyNode.AsString(value) is { } text => PyRepr.Length(text) > index
            ? JsonValue.Create(PyText.CodePointString(PyRepr.CodePoints(text).ElementAt(index)))
            : throw new PyIndexError("string index out of range"),
        _ => throw new PyTypeError("'" + PyJson.TypeName(value) + "' object is not subscriptable"),
    };

    /// <summary><c>parse_json(ref)</c> of a stored source reference.</summary>
    private static JsonNode? ParseRef(JsonNode? reference) =>
        StateStore.ParseJson(PyNode.AsString(reference) ?? throw new StateError(
            "invalid JSON", new PyTypeError("the JSON object must be str, bytes or bytearray, not " + PyJson.TypeName(reference))));

    /// <summary><c>value[:count]</c> of a parsed identity: a list prefix, a string prefix, or Python's error.</summary>
    private static JsonNode? Head(JsonNode? value, int count) => value switch
    {
        JsonArray list => new JsonArray(list.Take(count).Select(PyNode.Copy).ToArray()),
        JsonObject => throw PyKeyError.ForSlice(count),
        _ when PyNode.AsString(value) is { } text => JsonValue.Create(PyText.Prefix(text, count)),
        _ => throw new PyTypeError("'" + PyJson.TypeName(value) + "' object is not subscriptable"),
    };

    /// <summary>A stored string list item (records are normalised; anything else is a hand-edited row).</summary>
    private static string StrItem(JsonNode? value)
    {
        PyNode.RequireHashable(value);
        return PyNode.AsString(value) ?? throw new PyTypeError("'<' not supported between instances of '"
                                                               + PyJson.TypeName(value) + "' and 'str'");
    }

    internal static BigInteger ToInt(JsonNode? value) => PyJson.AsInt(value);
}

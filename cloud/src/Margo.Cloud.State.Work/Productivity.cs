using System.Numerics;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using static Margo.Cloud.State.Work.WorkLedger;

namespace Margo.Cloud.State.Work;

/// <summary>
/// Port of <c>skills/chief-of-staff/scripts/work_productivity.py</c>: typed outcome, meeting, correction/rule and
/// private Markdown artifact records on top of a <see cref="Ledger"/>, including recap retries, agenda
/// carry-forward and sharing receipts.
/// </summary>
public sealed class Productivity
{
    /// <summary><c>STATES</c>.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> States = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
    {
        ["outcome"] = Set("proposed", "agreed", "active", "achieved", "deferred", "cancelled"),
        ["meeting"] = Set("scheduled", "agenda_accumulating", "prepped", "occurred", "recap_pending", "debrief_proposed", "reviewed",
                          "carried_forward", "cancelled"),
        ["feedback"] = Set("recorded", "do_not_learn"),
        ["rule"] = Set("proposed", "active", "rejected", "revoked", "superseded"),
        ["artifact"] = Set("prepared", "approved"),
    };

    /// <summary><c>TRANSITIONS</c>.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlySet<string>>> Transitions =
        new Dictionary<string, IReadOnlyDictionary<string, IReadOnlySet<string>>>(StringComparer.Ordinal)
        {
            ["outcome"] = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
            {
                ["proposed"] = Set("agreed", "deferred", "cancelled"),
                ["agreed"] = Set("active", "achieved", "deferred", "cancelled"),
                ["active"] = Set("achieved", "deferred", "cancelled"),
                ["achieved"] = Set(),
                ["deferred"] = Set("proposed", "agreed", "active", "cancelled"),
                ["cancelled"] = Set(),
            },
            ["meeting"] = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
            {
                ["scheduled"] = Set("agenda_accumulating", "prepped", "occurred", "recap_pending", "cancelled"),
                ["agenda_accumulating"] = Set("prepped", "occurred", "recap_pending", "cancelled"),
                ["prepped"] = Set("agenda_accumulating", "occurred", "recap_pending", "cancelled"),
                ["occurred"] = Set("recap_pending", "debrief_proposed", "cancelled"),
                ["recap_pending"] = Set("debrief_proposed", "cancelled"),
                ["debrief_proposed"] = Set("reviewed", "recap_pending"),
                ["reviewed"] = Set("carried_forward"),
                ["carried_forward"] = Set(),
                ["cancelled"] = Set(),
            },
            ["feedback"] = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
            {
                ["recorded"] = Set(),
                ["do_not_learn"] = Set(),
            },
            ["rule"] = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
            {
                ["proposed"] = Set("active", "rejected"),
                ["active"] = Set("revoked", "superseded"),
                ["rejected"] = Set("proposed"),
                ["revoked"] = Set("active"),
                ["superseded"] = Set("active"),
            },
            ["artifact"] = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
            {
                ["prepared"] = Set("approved"),
                ["approved"] = Set("prepared"),
            },
        };

    /// <summary><c>ARTIFACT_KINDS</c>.</summary>
    public static readonly IReadOnlySet<string> ArtifactKinds = Set("decision_memo", "document_comparison", "status_update", "meeting_agenda", "delegation_brief");

    private static readonly string[] OutcomeRequired = ["week", "title", "owner", "definition_of_done", "due", "effort", "next_step", "allocation", "blocker"];
    private static readonly string[] OutcomeAgreed = ["owner", "definition_of_done", "due", "next_step"];
    private static readonly string[] MeetingTexts = ["series_id", "occurrence_id", "scheduled_start", "scheduled_end"];
    private static readonly string[] TopicTexts = ["id", "title", "created_at", "why"];
    private static readonly string[] DecisionTexts = ["canonical_id", "web_link", "status"];
    private static readonly string[] RetryCounts = ["attempts", "max_attempts"];
    private static readonly string[] RuleTexts = ["wording", "scope"];
    private static readonly string[] ArtifactTexts = ["title", "markdown", "audience", "purpose", "sensitivity", "proposed_next_action"];
    private static readonly string[] AuditKeys = ["kind", "actor", "evidence_ref", "decision", "decided_at"];
    private static readonly IReadOnlySet<string> Attendance = Set("unknown", "present", "absent");
    private static readonly IReadOnlySet<string> TopicStates = Set("open", "resolved");
    private static readonly IReadOnlySet<string> DecisionStatuses = Set("current", "superseded", "unknown");
    private static readonly IReadOnlySet<string> RetryStatuses = Set("pending", "available", "blocked", "exhausted");
    private static readonly IReadOnlySet<string> RecapResults = Set("pending", "blocked", "available", "exhausted");
    private static readonly IReadOnlySet<string> StoppedRetries = Set("blocked", "exhausted", "available");
    private static readonly IReadOnlySet<string> FeedbackKept = Set("subject_id", "subject_revision", "do_not_learn");
    private static readonly IReadOnlySet<string> Agreed = Set("agreed", "active", "achieved");
    private static readonly IReadOnlySet<string> PreparationStates = Set("scheduled", "agenda_accumulating", "prepped");
    private static readonly IReadOnlySet<string> ClosedItems = Set("resolved", "rejected", "cancelled");

    private static readonly Dictionary<string, (string Table, string Column)> VersionTables = new(StringComparer.Ordinal)
    {
        ["action"] = ("work_action_revisions", "action_id"),
        ["item"] = ("work_item_revisions", "item_id"),
        ["record"] = ("work_record_revisions", "record_id"),
    };

    private static readonly Dictionary<string, string> Defaults = new(StringComparer.Ordinal)
    {
        ["outcome"] = "proposed",
        ["meeting"] = "scheduled",
        ["feedback"] = "recorded",
        ["rule"] = "proposed",
        ["artifact"] = "prepared",
    };

    private static readonly Dictionary<string, string> Decisions = new(StringComparer.Ordinal)
    {
        ["rejected"] = "reject",
        ["revoked"] = "revoke",
        ["superseded"] = "supersede",
        ["cancelled"] = "cancel",
        ["deferred"] = "defer",
        ["carried_forward"] = "carry_forward",
    };

    /// <summary><c>Productivity(ledger)</c>.</summary>
    public Productivity(Ledger ledger)
    {
        Ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
    }

    /// <summary>The ledger the records live in.</summary>
    public Ledger Ledger { get; }

    /// <summary><c>record_id(kind, identity)</c>: the typed record ID to quote before collecting a human decision.</summary>
    public string RecordId(string kind, JsonNode? identity)
    {
        if (!States.ContainsKey(kind))
        {
            throw new StateError("unknown record kind");
        }

        Text(identity, "identity");
        return kind + "_" + Digest(new JsonArray(Ledger.Account, kind, PyNode.Copy(identity)))[..32];
    }

    private JsonObject Validate(string kind, JsonNode? input, string state)
    {
        if (input is not JsonObject original)
        {
            throw new StateError("record data must be an object");
        }

        var data = PyNode.CopyObject(original);
        data["source_refs"] = Ledger.SourceRefs(PyNode.Get(original, "source_refs", new JsonArray()));
        switch (kind)
        {
            case "outcome":
                ValidateOutcome(data, state);
                break;
            case "meeting":
                ValidateMeeting(data, state);
                break;
            case "feedback":
                data = ValidateFeedback(data, state);
                break;
            case "rule":
                ValidateRule(data);
                break;
            case "artifact":
                ValidateArtifact(data);
                break;
        }

        return data;
    }

    private void ValidateOutcome(JsonObject data, string state)
    {
        if (!OutcomeRequired.All(data.ContainsKey))
        {
            throw new StateError("outcomes require week,title,owner,definition_of_done,due,effort,next_step,allocation,blocker; unknowns are null");
        }

        Text(data["title"], "title");
        try
        {
            var week = PyDate.FromIsoformat(PyNode.AsString(data["week"]) ?? throw new PyTypeError("fromisoformat: argument must be str"));
            if ((week.ToOrdinal() + 6) % 7 != 0)
            {
                throw new PyValueError("");
            }

            if (data["due"] is not null)
            {
                PyDate.FromIsoformat(PyNode.AsString(data["due"]) ?? throw new PyTypeError("fromisoformat: argument must be str"));
            }
        }
        catch (Exception ex) when (ex is PyValueError or PyTypeError)
        {
            throw new StateError("week must be an ISO Monday; due must be an ISO date or null", ex);
        }

        var effort = data["effort"];
        if (effort is not null)
        {
            if (PyJson.IsBool(effort))
            {
                throw new StateError("effort must be positive minutes or a range");
            }

            if (PyNode.IsNumber(effort))
            {
                if (PyObj.Le(effort, 0L))
                {
                    throw new StateError("effort must be positive");
                }
            }
            else if (effort is JsonObject range && range.Count == 2 && range.ContainsKey("min_minutes") && range.ContainsKey("max_minutes"))
            {
                var low = range["min_minutes"];
                var high = range["max_minutes"];
                if (!PyNode.IsNumber(low) || PyJson.IsBool(low) || !PyNode.IsNumber(high) || PyJson.IsBool(high)
                    || !(PyObj.Lt(0L, low) && PyObj.Le(low, high)))
                {
                    throw new StateError("invalid effort range");
                }
            }
            else
            {
                throw new StateError("effort must be positive minutes, a min_minutes/max_minutes range, or null");
            }
        }

        if (Agreed.Contains(state))
        {
            foreach (var key in OutcomeAgreed)
            {
                Text(data[key], key);
            }

            if (effort is null || !(PyJson.Truthy(data["allocation"]) || PyJson.Truthy(data["blocker"])))
            {
                throw new StateError("agreement requires explicit effort and an allocation or named blocker");
            }
        }

        foreach (var itemId in PyNode.Iter(PyNode.Get(data, "work_item_ids", new JsonArray())))
        {
            Ledger.Row("work_items", itemId);
        }
    }

    private void ValidateMeeting(JsonObject data, string state)
    {
        foreach (var key in MeetingTexts)
        {
            Text(PyNode.Get(data, key), key);
        }

        if (Timestamp(data["scheduled_end"]) <= Timestamp(data["scheduled_start"]))
        {
            throw new StateError("meeting end must be after start");
        }

        if (!PyNode.In(PyNode.Get(data, "attendance", PyNode.Of("unknown")), Attendance))
        {
            throw new StateError("attendance must be unknown, present, or absent");
        }

        if (!data.ContainsKey("attendance"))
        {
            data["attendance"] = "unknown";
        }

        if (!PyNode.Is(data["attendance"], "unknown") && !PyJson.Truthy(PyNode.Get(data, "attendance_evidence")))
        {
            throw new StateError("attendance requires explicit evidence");
        }

        if (!PyJson.Truthy(data["source_refs"]))
        {
            throw new StateError("meeting requires an occurrence source revision");
        }

        foreach (var itemId in PyNode.Iter(PyNode.Get(data, "work_item_ids", new JsonArray())))
        {
            Ledger.Row("work_items", itemId);
        }

        if (PyNode.Get(data, "topics", new JsonArray()) is not JsonArray topics)
        {
            throw new StateError("meeting topics must be a list");
        }

        var topicIds = new List<JsonNode?>();
        foreach (var topic in topics)
        {
            ValidateTopic(topic);
            var id = topic!["id"];
            if (topicIds.Any(seen => PyJson.PyEquals(seen, id)))
            {
                throw new StateError("duplicate agenda topic ID");
            }

            topicIds.Add(id);
        }

        foreach (var reference in PyNode.Iter(PyNode.Get(data, "decision_refs", new JsonArray())))
        {
            if (reference is not JsonObject decision)
            {
                throw new StateError("decision refs require canonical_id,web_link,status");
            }

            foreach (var key in DecisionTexts)
            {
                Text(PyNode.Get(decision, key), "decision." + key);
            }

            if (!DecisionStatuses.Contains(PyNode.AsString(decision["status"])!))
            {
                throw new StateError("decision status must identify current/superseded/unknown");
            }
        }

        if (state == "recap_pending" && PyNode.Get(data, "recap_retry") is not JsonObject)
        {
            throw new StateError("recap_pending requires an explicit recap_retry policy");
        }

        var retryValue = PyNode.Get(data, "recap_retry");
        if (PyJson.Truthy(retryValue))
        {
            var retry = PyNode.Dict(retryValue);
            foreach (var key in RetryCounts)
            {
                if (!PyJson.IsInt(PyNode.Get(retry, key)))
                {
                    throw new StateError("recap_retry requires integer attempts,max_attempts");
                }
            }

            var attempts = PyJson.AsInt(retry["attempts"]);
            var maximum = PyJson.AsInt(retry["max_attempts"]);
            if (!(BigInteger.Zero <= attempts && attempts <= maximum && maximum <= 10))
            {
                throw new StateError("recap retries must have a finite budget no greater than ten");
            }

            if (!PyNode.In(PyNode.Get(retry, "status"), RetryStatuses))
            {
                throw new StateError("invalid recap retry status");
            }

            if (PyNode.Is(retry["status"], "pending"))
            {
                if (attempts >= maximum)
                {
                    throw new StateError("recap retry budget exhausted");
                }

                Timestamp(PyNode.Get(retry, "next_check_at"));
            }
        }

        if (state == "carried_forward")
        {
            var target = Ledger.Show(PyNode.Get(data, "next_occurrence_id"));
            if (!PyNode.Is(PyNode.Get(target, "kind"), "meeting")
                || !PyJson.PyEquals(PyNode.Index(PyNode.Index(target, "data"), "series_id"), data["series_id"]))
            {
                throw new StateError("carry forward target must be another occurrence of the same series");
            }

            if (PyJson.PyEquals(PyNode.Index(target["data"], "occurrence_id"), data["occurrence_id"]))
            {
                throw new StateError("cannot carry topics to the same occurrence");
            }
        }
    }

    private static void ValidateTopic(JsonNode? topic)
    {
        if (topic is not JsonObject obj)
        {
            throw new StateError("agenda topics require id,title,state,owner,created_at,why");
        }

        foreach (var key in TopicTexts)
        {
            Text(PyNode.Get(obj, key), "topic." + key);
        }

        Timestamp(obj["created_at"]);
        if (!obj.ContainsKey("owner") || !PyNode.In(PyNode.Get(obj, "state"), TopicStates))
        {
            throw new StateError("agenda topics require explicit state and owner (null when unknown)");
        }
    }

    private JsonObject ValidateFeedback(JsonObject data, string state)
    {
        if (state == "do_not_learn" || PyJson.Truthy(PyNode.Get(data, "do_not_learn")))
        {
            // Keep only an operational opt-out marker, never the correction/statement as training data.
            data["do_not_learn"] = true;
            var kept = new JsonObject();
            foreach (var (key, value) in data)
            {
                if (FeedbackKept.Contains(key))
                {
                    kept[key] = PyNode.Copy(value);
                }
            }

            data = kept;
        }
        else
        {
            Text(PyNode.Get(data, "correction"), "correction");
            if (data.ContainsKey("reason") && data["reason"] is not null)
            {
                Text(data["reason"], "reason");
            }
        }

        var subject = Ledger.Show(PyNode.Get(data, "subject_id"));
        var subjectType = PyNode.AsString(PyNode.Index(subject, "type"))!;
        if (!VersionTables.TryGetValue(subjectType, out var table) || !PyJson.IsInt(PyNode.Get(data, "subject_revision")))
        {
            throw new StateError("feedback requires an exact stored subject revision");
        }

        if (Ledger.Conn.Execute("SELECT 1 FROM " + table.Table + " WHERE " + table.Column + "=? AND revision=?",
                                subject["id"], data["subject_revision"]).FetchOne() is null)
        {
            throw new StateError("feedback subject revision does not exist");
        }

        return data;
    }

    private void ValidateRule(JsonObject data)
    {
        foreach (var key in RuleTexts)
        {
            Text(PyNode.Get(data, key), key);
        }

        if (PyNode.Get(data, "routines") is not JsonArray routines || routines.Count == 0
            || !routines.All(routine => PyNode.AsString(routine) is { } text && PyRepr.Strip(text).Length > 0))
        {
            throw new StateError("rule requires explicitly affected routines");
        }

        if (!PyNode.Is(PyNode.Get(data, "authority"), "advisory_only"))
        {
            throw new StateError("rules are advisory_only and cannot grant permissions or lower approval requirements");
        }

        Text(PyNode.Get(data, "conflict_check"), "conflict_check");
        if (PyNode.Get(data, "feedback_ids") is not JsonArray feedbackIds || feedbackIds.Count == 0)
        {
            throw new StateError("rule requires human feedback examples");
        }

        foreach (var feedbackId in feedbackIds)
        {
            var feedback = Ledger.Show(feedbackId);
            if (!PyNode.Is(PyNode.Get(feedback, "kind"), "feedback") || !PyNode.Is(PyNode.Index(feedback, "state"), "recorded"))
            {
                throw new StateError("do-not-learn feedback cannot support a rule");
            }
        }

        if (PyJson.Truthy(PyNode.Get(data, "supersedes")))
        {
            var prior = Ledger.Show(data["supersedes"]);
            if (!PyNode.Is(PyNode.Get(prior, "kind"), "rule")
                || !PyJson.PyEquals(PyNode.Index(PyNode.Index(prior, "data"), "scope"), data["scope"]))
            {
                throw new StateError("superseded rule must have the same scope");
            }

            var seen = new List<JsonNode?>();
            while (PyJson.Truthy(PyNode.Get(PyNode.Dict(PyNode.Index(prior, "data")), "supersedes")))
            {
                var priorId = PyNode.Index(prior, "id");
                if (seen.Any(id => PyJson.PyEquals(id, priorId)))
                {
                    throw new StateError("rule supersession cycle");
                }

                seen.Add(priorId);
                prior = Ledger.Show(PyNode.Index(prior["data"], "supersedes"));
            }
        }
    }

    private void ValidateArtifact(JsonObject data)
    {
        if (!PyNode.In(PyNode.Get(data, "artifact_kind"), ArtifactKinds))
        {
            throw new StateError("unsupported Markdown artifact kind");
        }

        foreach (var key in ArtifactTexts)
        {
            Text(PyNode.Get(data, key), key);
        }

        if (PyNode.Get(data, "open_questions") is not JsonArray)
        {
            throw new StateError("artifact requires open_questions, including unknown inputs");
        }

        if (!PyJson.Truthy(data["source_refs"]))
        {
            throw new StateError("artifact requires linked source revisions");
        }

        if (PyJson.Truthy(PyNode.Get(data, "work_item_id")))
        {
            Ledger.Row("work_items", data["work_item_id"]);
        }

        if (data.ContainsKey("sharing"))
        {
            throw new StateError("sharing is receipt-only, not editable artifact content");
        }
    }

    private void Snapshot(JsonNode? recordId, JsonNode? evidence)
    {
        var row = Ledger.Row("work_records", recordId);
        Ledger.Conn.Execute("INSERT INTO work_record_revisions VALUES(?,?,?,?,?)",
                            recordId, row["revision"], StateStore.CanonicalJson(row),
                            PyJson.Truthy(evidence) ? StateStore.CanonicalJson(evidence) : null, StateStore.UtcNow());
        var data = PyNode.Dict(PyNode.Loads(PyNode.AsString(row["data"])));
        foreach (var reference in PyNode.Iter(PyNode.Get(data, "source_refs", new JsonArray())))
        {
            Ledger.Conn.Execute("INSERT INTO work_record_sources VALUES(?,?,?,?,?)",
                                recordId, row["revision"], PyNode.Index(reference, "source_id"), PyNode.Index(reference, "revision"),
                                PyNode.Index(reference, "fingerprint"));
        }
    }

    /// <summary>
    /// <c>put(kind, identity, data, state=None, revision=None, evidence=None)</c>: creates or conditionally updates a
    /// typed record (updates replace all data); human decisions need exact evidence.
    /// </summary>
    public JsonObject Put(string kind, JsonNode? identity, JsonNode? data, JsonNode? state = null, JsonNode? revision = null, JsonNode? evidence = null)
    {
        if (kind == "meeting" && !PyJson.PyEquals(identity, PyNode.Get(PyNode.Dict(data), "occurrence_id")))
        {
            throw new StateError("meeting identity must equal its stable occurrence_id");
        }

        var recordId = RecordId(kind, identity);
        var recordNode = PyNode.Of(recordId);
        return Ledger.Transaction(() =>
        {
            var found = Ledger.Conn.Execute("SELECT * FROM work_records WHERE id=?", recordId).FetchOne();
            var old = found is null ? null : PyNode.FromRow(found);
            if (!PyJson.Truthy(state))
            {
                state = old is not null ? PyNode.Copy(old["state"]!) : PyNode.Of(Defaults[kind]);
            }

            if (kind == "feedback" && PyJson.Truthy(PyNode.Get(PyNode.Dict(data), "do_not_learn")))
            {
                state = PyNode.Of("do_not_learn");
            }

            if (!PyNode.In(state, States[kind]))
            {
                throw new StateError("invalid " + kind + " state");
            }

            var next = PyNode.AsString(state)!;
            var record = Validate(kind, data, next);
            var oldState = old is null ? null : PyNode.AsString(old["state"]);
            if (old is not null)
            {
                if (revision is null)
                {
                    if (PyJson.PyEquals(PyNode.Loads(PyNode.AsString(old["data"])), record) && oldState == next)
                    {
                        return Ledger.Show(recordNode);
                    }

                    throw new StateError("existing record; supply its revision for conditional edit");
                }

                CheckRevision(old, revision);
                if (kind == "feedback")
                {
                    throw new StateError("feedback events are immutable");
                }

                if (oldState != next && !(Transitions[kind].TryGetValue(oldState!, out var allowed) ? allowed : throw new PyKeyError(oldState!)).Contains(next))
                {
                    throw new StateError("invalid " + kind + " transition: " + oldState + " -> " + next);
                }

                var oldData = PyNode.Loads(PyNode.AsString(old["data"]));
                if (kind == "meeting"
                    && !(PyJson.PyEquals(PyNode.Get(record, "series_id"), PyNode.Index(oldData, "series_id"))
                         && PyJson.PyEquals(PyNode.Get(record, "occurrence_id"), PyNode.Index(oldData, "occurrence_id"))))
                {
                    throw new StateError("meeting identity is immutable");
                }

                if (kind == "rule" && oldState is "active" or "revoked" or "superseded" && !PyJson.PyEquals(record, oldData))
                {
                    throw new StateError("accepted rule text is immutable; propose a superseding rule");
                }

                if (kind == "artifact" && oldState == "approved" && !PyJson.PyEquals(record, oldData) && next != "prepared")
                {
                    throw new StateError("artifact edits invalidate content approval; save as prepared");
                }
            }
            else if (revision is not null)
            {
                throw new StateError("cannot conditionally edit a record that does not exist");
            }

            var expected = old is not null ? old["revision"] : PyNode.Of(1);
            var requiresHuman = kind == "feedback"
                                || (kind == "outcome" && (Agreed.Contains(next) || (old is not null && oldState != "proposed")))
                                || (kind == "meeting" && (next is "reviewed" or "carried_forward" || (old is not null && oldState is "reviewed" or "carried_forward")))
                                || (kind == "rule" && (next is "active" or "revoked" or "superseded" || (old is not null && oldState == "active")))
                                || (kind == "artifact" && next == "approved");
            if (requiresHuman)
            {
                Human(evidence, recordNode, expected, Decisions.GetValueOrDefault(next, "confirm"));
            }

            if (old is not null && PyJson.PyEquals(PyNode.Loads(PyNode.AsString(old["data"])), record) && oldState == next)
            {
                return Ledger.Show(recordNode);
            }

            if (kind == "rule")
            {
                var ancestorId = PyNode.Get(record, "supersedes");
                var ancestors = new List<JsonNode?>();
                while (PyJson.Truthy(ancestorId))
                {
                    if (PyNode.Is(ancestorId, recordId) || ancestors.Any(seen => PyJson.PyEquals(seen, ancestorId)))
                    {
                        throw new StateError("rule supersession would create a cycle");
                    }

                    PyNode.RequireHashable(ancestorId);
                    ancestors.Add(ancestorId);
                    ancestorId = PyNode.Get(PyNode.Dict(PyNode.Index(Ledger.Show(ancestorId), "data")), "supersedes");
                }
            }

            if (kind == "outcome" && Agreed.Contains(next))
            {
                var others = Ledger.Conn.Execute(
                    "SELECT data FROM work_records WHERE kind='outcome' AND account=? AND id<>? AND state IN ('agreed','active','achieved')",
                    Ledger.Account, recordId).FetchAll();
                if (others.Count(other => PyJson.PyEquals(PyNode.Index(PyNode.Loads(other["data"]), "week"), record["week"])) >= 3)
                {
                    throw new StateError("at most three agreed outcomes per week");
                }
            }

            if (kind == "rule" && next == "active")
            {
                foreach (var current in Ledger.Conn.Execute("SELECT id,data FROM work_records WHERE kind='rule' AND state='active' AND id<>?", recordId))
                {
                    var currentData = PyNode.Loads(current["data"]);
                    if (PyJson.PyEquals(PyNode.Index(currentData, "scope"), record["scope"])
                        && PyNode.Iter(PyNode.Index(currentData, "routines"))
                                    .Any(routine => PyNode.Iter(record["routines"]).Any(other => PyJson.PyEquals(routine, other))))
                    {
                        throw new StateError("overlapping active rule; explicitly supersede/revoke the old rule first");
                    }
                }
            }

            var auditEvidence = evidence;
            if (kind == "feedback" && next == "do_not_learn")
            {
                var audit = new JsonObject();
                foreach (var key in AuditKeys)
                {
                    audit[key] = PyNode.Copy(PyNode.Index(evidence, key));
                }

                auditEvidence = audit;
            }

            var stamp = StateStore.UtcNow();
            if (old is not null)
            {
                Ledger.Conn.Execute("UPDATE work_records SET revision=revision+1,state=?,data=?,updated_at=? WHERE id=?",
                                    next, StateStore.CanonicalJson(record), stamp, recordId);
            }
            else
            {
                Ledger.Conn.Execute("INSERT INTO work_records VALUES(?,?,?,?,?,?,?,?,?)",
                                    recordId, Ledger.Account, kind, 1L, next, identity, StateStore.CanonicalJson(record), stamp, stamp);
            }

            Snapshot(recordNode, auditEvidence);
            Ledger.Event(recordNode, old is not null ? "record_updated" : "record_created", new JsonObject
            {
                ["kind"] = kind,
                ["state"] = next,
                ["evidence"] = PyNode.Copy(auditEvidence),
            });
            if (kind == "artifact" && old is not null)
            {
                var actions = Ledger.Conn.Execute(
                    "SELECT a.id,r.data FROM work_actions a JOIN work_action_revisions r ON a.id=r.action_id AND a.revision=r.revision WHERE a.state IN ('ready','approved','deferred')").FetchAll();
                foreach (var action in actions)
                {
                    if (PyNode.Is(PyNode.Get(PyNode.Dict(PyNode.Loads(action["data"])), "artifact_id"), recordId))
                    {
                        Ledger.Invalidate(PyNode.FromSql(action["id"]), "artifact_changed", new JsonObject { ["artifact_id"] = recordId });
                    }
                }
            }

            return Ledger.Show(recordNode);
        });
    }

    /// <summary>
    /// <c>recap_retry(meeting_id, revision, result, evidence_ref, next_check_at=None)</c>: persists one bounded recap
    /// check of a pending meeting recap; blocked/available/exhausted stop further retries.
    /// </summary>
    public JsonObject RecapRetry(JsonNode? meetingId, JsonNode? revision, JsonNode? result, JsonNode? evidenceRef, JsonNode? nextCheckAt = null)
    {
        Text(evidenceRef, "evidence_ref");
        if (!PyNode.In(result, RecapResults))
        {
            throw new StateError("invalid recap check result");
        }

        var meeting = Ledger.Show(meetingId);
        CheckRevision(meeting, revision);
        if (!PyNode.Is(PyNode.Get(meeting, "kind"), "meeting") || !PyNode.Is(PyNode.Index(meeting, "state"), "recap_pending"))
        {
            throw new StateError("only pending recaps can be checked");
        }

        var data = PyNode.CopyObject(PyNode.Index(meeting, "data")!);
        var retry = PyNode.CopyObject(PyNode.Index(data, "recap_retry")!);
        if (PyNode.In(PyNode.Index(retry, "status"), StoppedRetries))
        {
            throw new StateError("recap retry is stopped; new evidence requires explicit record review");
        }

        if (Timestamp(PyNode.Index(retry, "next_check_at")) > Timestamp(StateStore.UtcNow()))
        {
            throw new StateError("recap check is not yet eligible");
        }

        retry["attempts"] = PyJson.Int(PyJson.AsInt(retry["attempts"]) + 1);
        retry["status"] = PyNode.Copy(result);
        retry["last_check_at"] = StateStore.UtcNow();
        retry["evidence_ref"] = PyNode.Copy(evidenceRef);
        if (PyNode.Is(result, "pending") && PyObj.Ge(retry["attempts"], PyNode.Index(retry, "max_attempts")))
        {
            retry["status"] = "exhausted";
        }

        if (PyNode.Is(retry["status"], "pending"))
        {
            if (Timestamp(nextCheckAt) <= Timestamp(StateStore.UtcNow()))
            {
                throw new StateError("next recap retry must be in the future");
            }

            retry["next_check_at"] = PyNode.Copy(nextCheckAt);
        }
        else
        {
            retry["next_check_at"] = null;
        }

        data["recap_retry"] = retry;
        return Put("meeting", PyNode.Index(meeting, "identity_key"), data, PyNode.Of("recap_pending"), revision);
    }

    /// <summary>
    /// <c>carry_forward(meeting_id, revision, target_id, target_revision, evidence)</c>: moves open agenda references
    /// (not copied obligations) from a reviewed occurrence to a later occurrence of the same series.
    /// </summary>
    public JsonObject CarryForward(JsonNode? meetingId, JsonNode? revision, JsonNode? targetId, JsonNode? targetRevision, JsonNode? evidence)
    {
        return Ledger.Transaction(() =>
        {
            var source = Ledger.Show(meetingId);
            var target = Ledger.Show(targetId);
            CheckRevision(source, revision);
            CheckRevision(target, targetRevision);
            if (!PyNode.Is(PyNode.Get(source, "kind"), "meeting") || !PyNode.Is(PyNode.Get(target, "kind"), "meeting")
                || PyJson.PyEquals(meetingId, targetId))
            {
                throw new StateError("carry-forward needs two distinct meeting occurrences");
            }

            if (!PyNode.Is(PyNode.Index(source, "state"), "reviewed") || !PyNode.In(PyNode.Index(target, "state"), PreparationStates))
            {
                throw new StateError("carry-forward needs a reviewed source and a future preparation occurrence");
            }

            var sourceData = PyNode.CopyObject(PyNode.Index(source, "data")!);
            var targetData = PyNode.CopyObject(PyNode.Index(target, "data")!);
            if (!PyJson.PyEquals(PyNode.Index(sourceData, "series_id"), PyNode.Index(targetData, "series_id")))
            {
                throw new StateError("cannot carry topics across different series");
            }

            if (Timestamp(PyNode.Index(targetData, "scheduled_start")) <= Timestamp(PyNode.Index(sourceData, "scheduled_start")))
            {
                throw new StateError("carry-forward target must be a later occurrence");
            }

            Human(evidence, meetingId, revision, "carry_forward");
            var itemIds = new JsonArray();
            foreach (var itemId in PyNode.Iter(PyNode.Get(targetData, "work_item_ids", new JsonArray())))
            {
                itemIds.Add(PyNode.Copy(itemId));
            }

            foreach (var itemId in PyNode.Iter(PyNode.Get(sourceData, "work_item_ids", new JsonArray())))
            {
                if (!PyNode.In(Ledger.Row("work_items", itemId)["state"], ClosedItems) && !itemIds.Any(existing => PyJson.PyEquals(existing, itemId)))
                {
                    itemIds.Add(PyNode.Copy(itemId));
                }
            }

            targetData["work_item_ids"] = itemIds;
            var topics = new JsonArray();
            var byId = new List<(JsonNode? Id, JsonNode? Topic)>();
            foreach (var topic in PyNode.Iter(PyNode.Get(targetData, "topics", new JsonArray())))
            {
                topics.Add(PyNode.Copy(topic));
            }

            foreach (var topic in topics)
            {
                var id = PyNode.Index(topic, "id");
                PyNode.RequireHashable(id);
                var index = byId.FindIndex(entry => PyJson.PyEquals(entry.Id, id));
                if (index >= 0)
                {
                    byId[index] = (byId[index].Id, topic);
                }
                else
                {
                    byId.Add((id, topic));
                }
            }

            foreach (var topic in PyNode.Iter(PyNode.Get(sourceData, "topics", new JsonArray())))
            {
                ValidateTopic(topic);
                if (!PyNode.Is(topic!["state"], "open"))
                {
                    continue;
                }

                var id = topic["id"];
                var index = byId.FindIndex(entry => PyJson.PyEquals(entry.Id, id));
                if (index >= 0)
                {
                    if (!PyJson.PyEquals(byId[index].Topic, topic))
                    {
                        throw new StateError("conflicting agenda topic revision; review before carry-forward");
                    }
                }
                else
                {
                    var copy = PyNode.Copy(topic);
                    topics.Add(copy);
                    byId.Add((id, copy));
                }
            }

            targetData["topics"] = topics;
            sourceData["next_occurrence_id"] = PyNode.Copy(targetId);
            foreach (var (record, data, state) in new[] { (source, sourceData, "carried_forward"), (target, targetData, "agenda_accumulating") })
            {
                var validated = Validate("meeting", data, state);
                Ledger.Conn.Execute("UPDATE work_records SET revision=revision+1,state=?,data=?,updated_at=? WHERE id=?",
                                    state, StateStore.CanonicalJson(validated), StateStore.UtcNow(), record["id"]);
                Snapshot(record["id"], evidence);
                Ledger.Event(record["id"], "agenda_carried_forward", new JsonObject
                {
                    ["from_id"] = PyNode.Copy(meetingId),
                    ["to_id"] = PyNode.Copy(targetId),
                    ["evidence"] = PyNode.Copy(evidence),
                });
            }

            return new JsonObject { ["source"] = Ledger.Show(meetingId), ["target"] = Ledger.Show(targetId) };
        });
    }

    /// <summary>
    /// <c>share_receipt(artifact_id, revision, attempt_id)</c>: links a succeeded execution that delivered this exact
    /// artifact revision; never publishes anything itself.
    /// </summary>
    public JsonObject ShareReceipt(JsonNode? artifactId, JsonNode? revision, JsonNode? attemptId)
    {
        return Ledger.Transaction(() =>
        {
            var artifact = Ledger.Show(artifactId);
            CheckRevision(artifact, revision);
            if (!PyNode.Is(PyNode.Get(artifact, "kind"), "artifact"))
            {
                throw new StateError("expected artifact");
            }

            var attempt = Ledger.Conn.Execute("SELECT * FROM work_executions WHERE id=?", attemptId).FetchOne();
            if (attempt is null || !Equals(attempt["state"], "succeeded"))
            {
                throw new StateError("sharing requires a succeeded action execution receipt");
            }

            var action = Ledger.Conn.Execute("SELECT data FROM work_action_revisions WHERE action_id=? AND revision=?",
                                             attempt["action_id"], attempt["revision"]).FetchOne()!;
            var data = PyNode.Dict(PyNode.Loads(action["data"]));
            if (!PyJson.PyEquals(PyNode.Get(data, "artifact_id"), artifactId) || !PyJson.PyEquals(PyNode.Get(data, "artifact_revision"), revision))
            {
                throw new StateError("execution did not deliver this exact artifact revision");
            }

            var receipt = PyNode.Loads(attempt["receipt"]);
            Ledger.Event(artifactId, "shared", new JsonObject
            {
                ["revision"] = PyNode.Copy(revision),
                ["attempt_id"] = PyNode.Copy(attemptId),
                ["receipt"] = PyNode.Copy(receipt),
            });
            return new JsonObject
            {
                ["artifact_id"] = PyNode.Copy(artifactId),
                ["revision"] = PyNode.Copy(revision),
                ["content_state"] = PyNode.Copy(artifact["state"]),
                ["sharing_state"] = "shared",
                ["receipt"] = receipt,
            };
        });
    }
}

"""Typed outcome, meeting, correction/rule and private Markdown records."""

import json
from datetime import date

from work_ledger import StateError, canonical_json, check_revision, digest, human, text, timestamp, utc_now


STATES = {
    "outcome": {"proposed", "agreed", "active", "achieved", "deferred", "cancelled"},
    "meeting": {"scheduled", "agenda_accumulating", "prepped", "occurred", "recap_pending",
                "debrief_proposed", "reviewed", "carried_forward", "cancelled"},
    "feedback": {"recorded", "do_not_learn"},
    "rule": {"proposed", "active", "rejected", "revoked", "superseded"},
    "artifact": {"prepared", "approved"},
}
TRANSITIONS = {
    "outcome": {
        "proposed": {"agreed", "deferred", "cancelled"}, "agreed": {"active", "achieved", "deferred", "cancelled"},
        "active": {"achieved", "deferred", "cancelled"}, "achieved": set(),
        "deferred": {"proposed", "agreed", "active", "cancelled"}, "cancelled": set(),
    },
    "meeting": {
        "scheduled": {"agenda_accumulating", "prepped", "occurred", "recap_pending", "cancelled"},
        "agenda_accumulating": {"prepped", "occurred", "recap_pending", "cancelled"},
        "prepped": {"agenda_accumulating", "occurred", "recap_pending", "cancelled"},
        "occurred": {"recap_pending", "debrief_proposed", "cancelled"},
        "recap_pending": {"debrief_proposed", "cancelled"}, "debrief_proposed": {"reviewed", "recap_pending"},
        "reviewed": {"carried_forward"}, "carried_forward": set(), "cancelled": set(),
    },
    "feedback": {"recorded": set(), "do_not_learn": set()},
    "rule": {
        "proposed": {"active", "rejected"}, "active": {"revoked", "superseded"},
        "rejected": {"proposed"}, "revoked": {"active"}, "superseded": {"active"},
    },
    "artifact": {"prepared": {"approved"}, "approved": {"prepared"}},
}
ARTIFACT_KINDS = {"decision_memo", "document_comparison", "status_update", "meeting_agenda", "delegation_brief"}


class Productivity:
    def __init__(self, ledger):
        self.ledger = ledger

    def record_id(self, kind, identity):
        if kind not in STATES:
            raise StateError("unknown record kind")
        text(identity, "identity")
        return kind + "_" + digest([self.ledger.account, kind, identity])[:32]

    def _validate(self, kind, data, state):
        if not isinstance(data, dict):
            raise StateError("record data must be an object")
        data = dict(data)
        data["source_refs"] = self.ledger.source_refs(data.get("source_refs", []))
        if kind == "outcome":
            required = {"week", "title", "owner", "definition_of_done", "due", "effort", "next_step", "allocation", "blocker"}
            if not required <= set(data):
                raise StateError("outcomes require week,title,owner,definition_of_done,due,effort,next_step,allocation,blocker; unknowns are null")
            text(data["title"], "title")
            try:
                week = date.fromisoformat(data["week"])
                if week.weekday() != 0:
                    raise ValueError()
                if data["due"] is not None:
                    date.fromisoformat(data["due"])
            except (ValueError, TypeError):
                raise StateError("week must be an ISO Monday; due must be an ISO date or null")
            effort = data["effort"]
            if effort is not None:
                if isinstance(effort, bool):
                    raise StateError("effort must be positive minutes or a range")
                if isinstance(effort, (int, float)):
                    if effort <= 0:
                        raise StateError("effort must be positive")
                elif isinstance(effort, dict) and set(effort) == {"min_minutes", "max_minutes"}:
                    low, high = effort["min_minutes"], effort["max_minutes"]
                    if (not isinstance(low, (int, float)) or isinstance(low, bool)
                            or not isinstance(high, (int, float)) or isinstance(high, bool) or not 0 < low <= high):
                        raise StateError("invalid effort range")
                else:
                    raise StateError("effort must be positive minutes, a min_minutes/max_minutes range, or null")
            if state in {"agreed", "active", "achieved"}:
                for key in ("owner", "definition_of_done", "due", "next_step"):
                    text(data[key], key)
                if effort is None or not (data["allocation"] or data["blocker"]):
                    raise StateError("agreement requires explicit effort and an allocation or named blocker")
            for item_id in data.get("work_item_ids", []):
                self.ledger.row("work_items", item_id)
        elif kind == "meeting":
            for key in ("series_id", "occurrence_id", "scheduled_start", "scheduled_end"):
                text(data.get(key), key)
            if timestamp(data["scheduled_end"]) <= timestamp(data["scheduled_start"]):
                raise StateError("meeting end must be after start")
            if data.get("attendance", "unknown") not in {"unknown", "present", "absent"}:
                raise StateError("attendance must be unknown, present, or absent")
            data.setdefault("attendance", "unknown")
            if data["attendance"] != "unknown" and not data.get("attendance_evidence"):
                raise StateError("attendance requires explicit evidence")
            if not data["source_refs"]:
                raise StateError("meeting requires an occurrence source revision")
            for item_id in data.get("work_item_ids", []):
                self.ledger.row("work_items", item_id)
            if not isinstance(data.get("topics", []), list):
                raise StateError("meeting topics must be a list")
            topic_ids = set()
            for topic in data.get("topics", []):
                if not isinstance(topic, dict):
                    raise StateError("agenda topics require id,title,state,owner,created_at,why")
                for key in ("id", "title", "created_at", "why"):
                    text(topic.get(key), "topic." + key)
                timestamp(topic["created_at"])
                if "owner" not in topic or topic.get("state") not in {"open", "resolved"}:
                    raise StateError("agenda topics require explicit state and owner (null when unknown)")
                if topic["id"] in topic_ids:
                    raise StateError("duplicate agenda topic ID")
                topic_ids.add(topic["id"])
            for reference in data.get("decision_refs", []):
                if not isinstance(reference, dict):
                    raise StateError("decision refs require canonical_id,web_link,status")
                for key in ("canonical_id", "web_link", "status"):
                    text(reference.get(key), "decision." + key)
                if reference["status"] not in {"current", "superseded", "unknown"}:
                    raise StateError("decision status must identify current/superseded/unknown")
            if state == "recap_pending" and not isinstance(data.get("recap_retry"), dict):
                raise StateError("recap_pending requires an explicit recap_retry policy")
            retry = data.get("recap_retry")
            if retry:
                for key in ("attempts", "max_attempts"):
                    if type(retry.get(key)) is not int:
                        raise StateError("recap_retry requires integer attempts,max_attempts")
                if not 0 <= retry["attempts"] <= retry["max_attempts"] <= 10:
                    raise StateError("recap retries must have a finite budget no greater than ten")
                if retry.get("status") not in {"pending", "available", "blocked", "exhausted"}:
                    raise StateError("invalid recap retry status")
                if retry["status"] == "pending":
                    if retry["attempts"] >= retry["max_attempts"]:
                        raise StateError("recap retry budget exhausted")
                    timestamp(retry.get("next_check_at"))
            if state == "carried_forward":
                target = self.ledger.show(data.get("next_occurrence_id"))
                if target.get("kind") != "meeting" or target["data"]["series_id"] != data["series_id"]:
                    raise StateError("carry forward target must be another occurrence of the same series")
                if target["data"]["occurrence_id"] == data["occurrence_id"]:
                    raise StateError("cannot carry topics to the same occurrence")
        elif kind == "feedback":
            if state == "do_not_learn" or data.get("do_not_learn"):
                # Keep only an operational opt-out marker, never the correction/statement as training data.
                data["do_not_learn"] = True
                data = {k: v for k, v in data.items() if k in {"subject_id", "subject_revision", "do_not_learn"}}
            else:
                text(data.get("correction"), "correction")
                if "reason" in data and data["reason"] is not None:
                    text(data["reason"], "reason")
            subject = self.ledger.show(data.get("subject_id"))
            version_tables = {"action": ("work_action_revisions", "action_id"),
                              "item": ("work_item_revisions", "item_id"),
                              "record": ("work_record_revisions", "record_id")}
            if subject["type"] not in version_tables or type(data.get("subject_revision")) is not int:
                raise StateError("feedback requires an exact stored subject revision")
            table, column = version_tables[subject["type"]]
            if not self.ledger.conn.execute(
                    "SELECT 1 FROM %s WHERE %s=? AND revision=?" % (table, column),
                    (subject["id"], data["subject_revision"])).fetchone():
                raise StateError("feedback subject revision does not exist")
        elif kind == "rule":
            for key in ("wording", "scope"):
                text(data.get(key), key)
            if not isinstance(data.get("routines"), list) or not data["routines"] or not all(isinstance(r, str) and r.strip() for r in data["routines"]):
                raise StateError("rule requires explicitly affected routines")
            if data.get("authority") != "advisory_only":
                raise StateError("rules are advisory_only and cannot grant permissions or lower approval requirements")
            text(data.get("conflict_check"), "conflict_check")
            if not isinstance(data.get("feedback_ids"), list) or not data["feedback_ids"]:
                raise StateError("rule requires human feedback examples")
            for feedback_id in data["feedback_ids"]:
                feedback = self.ledger.show(feedback_id)
                if feedback.get("kind") != "feedback" or feedback["state"] != "recorded":
                    raise StateError("do-not-learn feedback cannot support a rule")
            if data.get("supersedes"):
                prior = self.ledger.show(data["supersedes"])
                if prior.get("kind") != "rule" or prior["data"]["scope"] != data["scope"]:
                    raise StateError("superseded rule must have the same scope")
                seen = set()
                while prior["data"].get("supersedes"):
                    if prior["id"] in seen:
                        raise StateError("rule supersession cycle")
                    seen.add(prior["id"])
                    prior = self.ledger.show(prior["data"]["supersedes"])
        elif kind == "artifact":
            if data.get("artifact_kind") not in ARTIFACT_KINDS:
                raise StateError("unsupported Markdown artifact kind")
            for key in ("title", "markdown", "audience", "purpose", "sensitivity", "proposed_next_action"):
                text(data.get(key), key)
            if not isinstance(data.get("open_questions"), list):
                raise StateError("artifact requires open_questions, including unknown inputs")
            if not data["source_refs"]:
                raise StateError("artifact requires linked source revisions")
            if data.get("work_item_id"):
                self.ledger.row("work_items", data["work_item_id"])
            if "sharing" in data:
                raise StateError("sharing is receipt-only, not editable artifact content")
        return data

    def _snapshot(self, record_id, evidence):
        row = self.ledger.row("work_records", record_id)
        self.ledger.conn.execute("INSERT INTO work_record_revisions VALUES(?,?,?,?,?)",
                                 (record_id, row["revision"], canonical_json(row),
                                  canonical_json(evidence) if evidence else None, utc_now()))
        for ref in json.loads(row["data"]).get("source_refs", []):
            self.ledger.conn.execute("INSERT INTO work_record_sources VALUES(?,?,?,?,?)",
                                     (record_id, row["revision"], ref["source_id"], ref["revision"], ref["fingerprint"]))

    def put(self, kind, identity, data, state=None, revision=None, evidence=None):
        if kind == "meeting" and identity != data.get("occurrence_id"):
            raise StateError("meeting identity must equal its stable occurrence_id")
        record_id = self.record_id(kind, identity)
        with self.ledger.transaction():
            found = self.ledger.conn.execute("SELECT * FROM work_records WHERE id=?", (record_id,)).fetchone()
            old = dict(found) if found else None
            defaults = {"outcome": "proposed", "meeting": "scheduled", "feedback": "recorded", "rule": "proposed", "artifact": "prepared"}
            state = state or (old["state"] if old else defaults[kind])
            if kind == "feedback" and data.get("do_not_learn"):
                state = "do_not_learn"
            if state not in STATES[kind]:
                raise StateError("invalid %s state" % kind)
            data = self._validate(kind, data, state)
            if old:
                if revision is None:
                    if json.loads(old["data"]) == data and old["state"] == state:
                        return self.ledger.show(record_id)
                    raise StateError("existing record; supply its revision for conditional edit")
                check_revision(old, revision)
                if kind == "feedback":
                    raise StateError("feedback events are immutable")
                if old["state"] != state and state not in TRANSITIONS[kind][old["state"]]:
                    raise StateError("invalid %s transition: %s -> %s" % (kind, old["state"], state))
                old_data = json.loads(old["data"])
                if kind == "meeting" and (data.get("series_id"), data.get("occurrence_id")) != (old_data["series_id"], old_data["occurrence_id"]):
                    raise StateError("meeting identity is immutable")
                if kind == "rule" and old["state"] in {"active", "revoked", "superseded"} and data != old_data:
                    raise StateError("accepted rule text is immutable; propose a superseding rule")
                if kind == "artifact" and old["state"] == "approved" and data != old_data and state != "prepared":
                    raise StateError("artifact edits invalidate content approval; save as prepared")
            elif revision is not None:
                raise StateError("cannot conditionally edit a record that does not exist")
            expected = old["revision"] if old else 1
            requires_human = (
                kind == "feedback"
                or (kind == "outcome" and (state in {"agreed", "active", "achieved"} or (old and old["state"] != "proposed")))
                or (kind == "meeting" and (state in {"reviewed", "carried_forward"} or (old and old["state"] in {"reviewed", "carried_forward"})))
                or (kind == "rule" and (state in {"active", "revoked", "superseded"} or (old and old["state"] == "active")))
                or (kind == "artifact" and state == "approved")
            )
            if requires_human:
                decision = {
                    "rejected": "reject", "revoked": "revoke", "superseded": "supersede",
                    "cancelled": "cancel", "deferred": "defer", "carried_forward": "carry_forward",
                }.get(state, "confirm")
                human(evidence, record_id, expected, decision)
            if old and json.loads(old["data"]) == data and old["state"] == state:
                return self.ledger.show(record_id)
            if kind == "rule":
                ancestor_id = data.get("supersedes")
                ancestors = set()
                while ancestor_id:
                    if ancestor_id == record_id or ancestor_id in ancestors:
                        raise StateError("rule supersession would create a cycle")
                    ancestors.add(ancestor_id)
                    ancestor_id = self.ledger.show(ancestor_id)["data"].get("supersedes")
            if kind == "outcome" and state in {"agreed", "active", "achieved"}:
                others = self.ledger.conn.execute(
                    "SELECT data FROM work_records WHERE kind='outcome' AND account=? AND id<>? AND state IN ('agreed','active','achieved')",
                    (self.ledger.account, record_id),
                ).fetchall()
                if sum(json.loads(r["data"])["week"] == data["week"] for r in others) >= 3:
                    raise StateError("at most three agreed outcomes per week")
            if kind == "rule" and state == "active":
                for current in self.ledger.conn.execute(
                        "SELECT id,data FROM work_records WHERE kind='rule' AND state='active' AND id<>?", (record_id,)):
                    current_data = json.loads(current["data"])
                    if current_data["scope"] == data["scope"] and set(current_data["routines"]) & set(data["routines"]):
                        raise StateError("overlapping active rule; explicitly supersede/revoke the old rule first")
            audit_evidence = evidence
            if kind == "feedback" and state == "do_not_learn":
                audit_evidence = {k: evidence[k] for k in ("kind", "actor", "evidence_ref", "decision", "decided_at")}
            stamp = utc_now()
            if old:
                self.ledger.conn.execute("UPDATE work_records SET revision=revision+1,state=?,data=?,updated_at=? WHERE id=?",
                                         (state, canonical_json(data), stamp, record_id))
            else:
                self.ledger.conn.execute("INSERT INTO work_records VALUES(?,?,?,?,?,?,?,?,?)",
                                         (record_id, self.ledger.account, kind, 1, state, identity, canonical_json(data), stamp, stamp))
            self._snapshot(record_id, audit_evidence)
            self.ledger.event(record_id, "record_updated" if old else "record_created",
                              {"kind": kind, "state": state, "evidence": audit_evidence})
            if kind == "artifact" and old:
                for action in self.ledger.conn.execute(
                        "SELECT a.id,r.data FROM work_actions a JOIN work_action_revisions r ON a.id=r.action_id AND a.revision=r.revision WHERE a.state IN ('ready','approved','deferred')").fetchall():
                    if json.loads(action["data"]).get("artifact_id") == record_id:
                        self.ledger._invalidate(action["id"], "artifact_changed", {"artifact_id": record_id})
            return self.ledger.show(record_id)

    def recap_retry(self, meeting_id, revision, result, evidence_ref, next_check_at=None):
        text(evidence_ref, "evidence_ref")
        if result not in {"pending", "blocked", "available", "exhausted"}:
            raise StateError("invalid recap check result")
        meeting = self.ledger.show(meeting_id)
        check_revision(meeting, revision)
        if meeting.get("kind") != "meeting" or meeting["state"] != "recap_pending":
            raise StateError("only pending recaps can be checked")
        data = dict(meeting["data"])
        retry = dict(data["recap_retry"])
        if retry["status"] in {"blocked", "exhausted", "available"}:
            raise StateError("recap retry is stopped; new evidence requires explicit record review")
        if timestamp(retry["next_check_at"]) > timestamp(utc_now()):
            raise StateError("recap check is not yet eligible")
        retry.update(attempts=retry["attempts"] + 1, status=result, last_check_at=utc_now(), evidence_ref=evidence_ref)
        if result == "pending" and retry["attempts"] >= retry["max_attempts"]:
            retry["status"] = "exhausted"
        if retry["status"] == "pending":
            if timestamp(next_check_at) <= timestamp(utc_now()):
                raise StateError("next recap retry must be in the future")
            retry["next_check_at"] = next_check_at
        else:
            retry["next_check_at"] = None
        data["recap_retry"] = retry
        return self.put("meeting", meeting["identity_key"], data, state="recap_pending", revision=revision)

    def carry_forward(self, meeting_id, revision, target_id, target_revision, evidence):
        """Move open agenda references, not copied obligations, to a known occurrence."""
        with self.ledger.transaction():
            source = self.ledger.show(meeting_id)
            target = self.ledger.show(target_id)
            check_revision(source, revision)
            check_revision(target, target_revision)
            if source.get("kind") != "meeting" or target.get("kind") != "meeting" or meeting_id == target_id:
                raise StateError("carry-forward needs two distinct meeting occurrences")
            if source["state"] != "reviewed" or target["state"] not in {"scheduled", "agenda_accumulating", "prepped"}:
                raise StateError("carry-forward needs a reviewed source and a future preparation occurrence")
            if source["data"]["series_id"] != target["data"]["series_id"]:
                raise StateError("cannot carry topics across different series")
            if timestamp(target["data"]["scheduled_start"]) <= timestamp(source["data"]["scheduled_start"]):
                raise StateError("carry-forward target must be a later occurrence")
            human(evidence, meeting_id, revision, "carry_forward")
            source_data, target_data = dict(source["data"]), dict(target["data"])
            item_ids = list(target_data.get("work_item_ids", []))
            for item_id in source_data.get("work_item_ids", []):
                if self.ledger.row("work_items", item_id)["state"] not in {"resolved", "rejected", "cancelled"} and item_id not in item_ids:
                    item_ids.append(item_id)
            target_data["work_item_ids"] = item_ids
            topics = list(target_data.get("topics", []))
            by_id = {topic["id"]: topic for topic in topics}
            for topic in source_data.get("topics", []):
                if not isinstance(topic, dict):
                    raise StateError("agenda topics require id,title,state,owner,created_at,why")
                for key in ("id", "title", "created_at", "why"):
                    text(topic.get(key), "topic." + key)
                timestamp(topic["created_at"])
                if "owner" not in topic or topic.get("state") not in {"open", "resolved"}:
                    raise StateError("agenda topics require explicit state and owner (null when unknown)")
                if topic["state"] != "open":
                    continue
                if topic["id"] in by_id:
                    if by_id[topic["id"]] != topic:
                        raise StateError("conflicting agenda topic revision; review before carry-forward")
                else:
                    topics.append(topic)
                    by_id[topic["id"]] = topic
            target_data["topics"] = topics
            source_data["next_occurrence_id"] = target_id
            for record, data, state in ((source, source_data, "carried_forward"),
                                        (target, target_data, "agenda_accumulating")):
                data = self._validate("meeting", data, state)
                self.ledger.conn.execute(
                    "UPDATE work_records SET revision=revision+1,state=?,data=?,updated_at=? WHERE id=?",
                    (state, canonical_json(data), utc_now(), record["id"]))
                self._snapshot(record["id"], evidence)
                self.ledger.event(record["id"], "agenda_carried_forward",
                                  {"from_id": meeting_id, "to_id": target_id, "evidence": evidence})
            return {"source": self.ledger.show(meeting_id), "target": self.ledger.show(target_id)}

    def share_receipt(self, artifact_id, revision, attempt_id):
        with self.ledger.transaction():
            artifact = self.ledger.show(artifact_id)
            check_revision(artifact, revision)
            if artifact.get("kind") != "artifact":
                raise StateError("expected artifact")
            attempt = self.ledger.conn.execute("SELECT * FROM work_executions WHERE id=?", (attempt_id,)).fetchone()
            if not attempt or attempt["state"] != "succeeded":
                raise StateError("sharing requires a succeeded action execution receipt")
            action = self.ledger.conn.execute(
                "SELECT data FROM work_action_revisions WHERE action_id=? AND revision=?", (attempt["action_id"], attempt["revision"])
            ).fetchone()
            data = json.loads(action["data"])
            if data.get("artifact_id") != artifact_id or data.get("artifact_revision") != revision:
                raise StateError("execution did not deliver this exact artifact revision")
            receipt = json.loads(attempt["receipt"])
            self.ledger.event(artifact_id, "shared", {"revision": revision, "attempt_id": attempt_id, "receipt": receipt})
            return {"artifact_id": artifact_id, "revision": revision, "content_state": artifact["state"],
                    "sharing_state": "shared", "receipt": receipt}

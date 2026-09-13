import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import vm from "node:vm";
import { dispatchDecision, decisionPrompt } from "./decision-requests.mjs";
import { commandArguments, validateInput } from "./backend.mjs";

const sandbox = { Intl };
vm.runInNewContext(await readFile(new URL("./decision-model.js", import.meta.url), "utf8"), sandbox);
const model = sandbox.MargoDecision;
const now = Date.parse("2026-09-11T16:00:00Z");
const pref = { value: "America/Los_Angeles; Mon-Fri; 09:00-18:00" };
const context = model.dayContext(now, pref);
const work = (id, fields = {}) => ({ id, type: "item", title: id, revision: 1, state: "candidate",
    data: { title: id, next_step: "Choose a design" }, ...fields });
const action = (fields = {}) => work("action-1", { type: "action", kind: "mail.reply", state: "ready", data: {}, ...fields });
const meeting = (start, end, state = "scheduled") => work("meeting-1", { type: "record", kind: "meeting",
    state, data: { title: "Design review", scheduled_start: start, scheduled_end: end } });
const row = item => model.project([item], now, context)[0];

test("decision ranking moves at the exact sixty-minute boundary without mutating records", () => {
    const value = meeting("2026-09-11T17:00:01Z", "2026-09-11T17:30:00Z");
    const before = JSON.stringify(value);
    assert.equal(row(value).lane, "Next");
    assert.equal(model.project([value], now + 1000, context)[0].lane, "Now");
    assert.equal(JSON.stringify(value), before);
    const order = model.project([action(), work("late", { confirmed: true, data: { due: "2026-09-11T15:59:59Z" } }),
        action({ id: "unknown", state: "outcome_unknown" })], now, context);
    assert.deepEqual(Array.from(order, value => value.item.id), ["unknown", "late", "action-1"]);
    assert.match(order[0].why, /Never retry/);
});

test("past, completed and cancelled meetings never return as upcoming prep", () => {
    for (const state of ["occurred", "recap_pending", "debrief_proposed", "reviewed", "carried_forward", "cancelled"]) {
        const value = row(meeting("2026-09-11T16:30:00Z", "2026-09-11T17:00:00Z", state));
        assert.notEqual(value.lane, "Now");
    }
    const past = row(meeting("2026-09-11T15:00:00Z", "2026-09-11T16:00:00Z"));
    assert.equal(past.lane, "History");
    assert.match(past.time.label, /attendance not inferred/);
    assert.equal(row(meeting("2026-09-11T16:00:00Z", "2026-09-11T17:00:00Z")).lane, "Now");
    assert.match(row(meeting("2026-09-11T16:30:00Z", "2026-09-11T17:00:00Z", "cancelled")).time.label, /Cancelled.*not upcoming/);
    assert.doesNotMatch(row(work("closed", { state: "resolved", data: { due: "2026-01-01" } })).time.label, /Overdue/);
});

test("clock respects account timezone, working days and DST; unknown hours are not invented", () => {
    assert.match(context.context, /Within/);
    assert.equal(context.localDate, "2026-09-11");
    assert.match(model.dayContext(Date.parse("2026-09-12T16:00:00Z"), pref).context, /Outside.*working days/);
    assert.match(model.dayContext(Date.parse("2026-09-12T02:00:00Z"), pref).context, /After/);
    const spring = { value: "America/New_York; Sun; 01:00-04:00" };
    assert.match(model.dayContext(Date.parse("2026-03-08T06:59:59Z"), spring).context, /Within/);
    assert.match(model.dayContext(Date.parse("2026-03-08T07:00:00Z"), spring).context, /Within/);
    assert.match(model.dayContext(Date.parse("2026-03-08T08:00:00Z"), spring).context, /After/);
    for (const time of ["2026-11-01T05:30:00Z", "2026-11-01T06:30:00Z"]) {
        assert.match(model.dayContext(Date.parse(time), spring).context, /Within/);
    }
    assert.equal(model.preferences({ value: "09:00-18:00 PT" }).timezone, null);
    assert.equal(model.preferences({ value: "America/Unknown; Mon-Fri; 09:00-18:00" }).timezone, null);
    assert.equal(model.preferences({ value: "UTC; Mon-Fri; 18:00-09:00" }).schedule, null);
    assert.match(model.dayContext(now, {}).label, /device clock/);
});

test("date-only deadlines cross the account day boundary, not the device or UTC day", () => {
    const value = work("dated", { confirmed: true, data: { due: "2026-09-11" } });
    const before = Date.parse("2026-09-12T06:59:59Z"), after = before + 1000;
    assert.match(model.project([value], before, model.dayContext(before, pref))[0].time.label, /Due today/);
    assert.match(model.project([value], after, model.dayContext(after, pref))[0].time.label, /Overdue/);
    assert.match(model.project([value], after, model.dayContext(after, {}))[0].time.label, /timezone unknown/);
});

test("invalid, naive and missing dates never invent urgency", () => {
    for (const date of ["2026-02-30T10:00:00Z", "not-a-date", "2026-09-11T24:00:00Z", "2026-09-11T16:00:00", null]) {
        assert.equal(model.instant(date), null);
    }
    assert.match(row(work("due", { data: { due: "Friday afternoon" } })).time.label, /not interpreted/);
    assert.match(row(meeting(null, null)).time.label, /invalid or missing/);
});

test("snooze expires in presentation only and cannot hide unresolved execution", () => {
    const value = action({ state: "deferred", deferred_until: "2026-09-11T16:00:01Z" });
    assert.equal(row(value).lane, "Snoozed");
    assert.equal(model.project([value], now + 1000, context)[0].lane, "Needs your decision");
    assert.equal(value.state, "deferred");
    assert.match(row(action({ state: "deferred", deferred_until: "invalid" })).why, /invalid/);
    assert.equal(row(action({ state: "outcome_unknown", deferred_until: "2999-01-01T00:00:00Z" })).lane, "Now");
});

test("coverage ages against its recorded cadence independently of the clock and ledger read", () => {
    const coverage = { status: "available", sources: [{ family: "mail", status: "complete",
        last_successful_coverage_at: "2026-09-11T15:59:00Z", cadence_seconds: 60 }] };
    assert.equal(model.coverage(coverage, now).sources[0].healthy, true);
    assert.equal(model.coverage(coverage, now + 1).sources[0].healthy, false);
    assert.match(model.coverage(coverage, now + 1).sources[0].freshness, /Stale/);
    assert.match(model.coverage({ status: "unavailable" }, now).label, /unavailable/);
    assert.match(model.coverage({ status: "available", sources: [] }, now).label, /No active/);
    coverage.sources[0].status = "failed";
    assert.equal(model.coverage(coverage, now).sources[0].healthy, false);
    coverage.sources[0].last_successful_coverage_at = "2999-01-01T00:00:00Z";
    assert.match(model.coverage(coverage, now).sources[0].freshness, /Invalid/);
});

test("key asks, candidate confirmation and successful delivery stay distinct", () => {
    const item = work("work-1", { data: { next_step: "Decide between A and B", blocker: "Waiting for design evidence", due: null } });
    const proposal = action({ work_item_id: "work-1", affected_people: ["Dana"], state: "succeeded" });
    const projected = model.project([item, proposal], now, context);
    assert.match(projected[0].readiness, /Candidate/);
    assert.equal(projected[1].ask, "Decide between A and B");
    assert.match(projected[1].readiness, /linked work may still be open/);
    assert.equal(projected[1].canMutate, false);
});

test("accepted requests never become ready merely from elapsed time or a successful dispatch", () => {
    const request = { phase: "accepted", deadline_at: "2026-09-11T16:01:00Z" };
    assert.equal(model.requestPhase(request, now), "accepted");
    assert.equal(model.requestPhase(request, now + 60_000), "blocked");
    assert.equal(model.requestPhase({ ...request, phase: "ready" }, now + 60_000), "ready");
});

test("decision requests carry only identities and fixed local instructions; dispatch failure is retained", async () => {
    const input = { id: "item-1", expected_revision: 1, intent: "prepare" };
    const response = { account: "fixture", request: { run_id: "task-fixture", phase: "dispatching" },
        subject: { id: "item-1", revision: 1, intent: "prepare" },
        claim: { attempt_id: `task_attempt_${"a".repeat(32)}`, token: "a".repeat(48) } };
    const calls = [], sent = [];
    const backend = { run: async (operation, value) => {
        calls.push([operation, value]);
        return operation === "desk-request" ? response : { request: { ...response.request, phase: value.accepted ? "accepted" : "outcome_unknown" } };
    } };
    const result = await dispatchDecision({ backend, host: "sdk:fixture", sendReview: async options => {
        sent.push(options); return "message-fixture";
    } }, input);
    assert.equal(result.request.phase, "accepted");
    assert.equal(result.approved, false);
    assert.equal(result.claim, undefined);
    assert.equal(sent[0].mode, "enqueue");
    assert.match(sent[0].prompt, /NOT approval/);
    assert.match(sent[0].prompt, /No M365 refresh/);
    assert.doesNotMatch(decisionPrompt("fixture", response.subject, response.request), /Unsent draft/);
    response.replayed = true;
    await dispatchDecision({ backend, host: "sdk:fixture", sendReview: async () => { throw new Error("duplicate"); } }, input);
    assert.equal(sent.length, 1);
    response.replayed = false;
    await assert.rejects(dispatchDecision({ backend, host: "sdk:fixture", sendReview: async () => { throw new Error("timeout"); } }, input),
        { code: "dispatch_unknown" });
    assert.equal(calls.at(-1)[1].accepted, false);
    assert.match(calls.at(-1)[1].reference, /unknown/);
    await assert.rejects(dispatchDecision({ backend, host: "sdk:fixture", sendReview: async () => "ok" }, { ...input, host: "spoofed" }), { code: "invalid_input" });
    response.subject.id = "other-item";
    await assert.rejects(dispatchDecision({ backend, host: "sdk:fixture", sendReview: async () => {
        throw new Error("must not send an unverifiable identity");
    } }, input), { code: "invalid_backend_response" });
});

test("desk command validation never admits arbitrary transport, account, prompt or approval", () => {
    assert.deepEqual(commandArguments("core.py", "desk"), ["-B", "core.py", "desk"]);
    for (const field of ["account", "prompt", "path", "approved", "command"]) {
        assert.throws(() => validateInput("desk-request", {
            id: "item-1", expected_revision: 1, intent: "prepare", host: "sdk:fixture", [field]: "arbitrary",
        }), { code: "invalid_input" });
    }
});

// Optional real-browser interaction rehearsal, using synthetic state and no external services.
import test from "node:test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { pathToFileURL } from "node:url";
import { normalizeItem, BackendError } from "./backend.mjs";
import { startServer } from "./server.mjs";

const modulePath = process.env.MARGO_PLAYWRIGHT_MODULE;
test("browser decision workflow: time, editing, snooze, durable request display, offline and keyboard", {
    skip: !modulePath && "Set MARGO_PLAYWRIGHT_MODULE to an explicitly installed Playwright module.",
}, async t => {
    const { chromium } = await import(pathToFileURL(modulePath).href);
    const browser = await chromium.launch({ headless: true,
        ...(process.env.MARGO_BROWSER_EXECUTABLE ? { executablePath: process.env.MARGO_BROWSER_EXECUTABLE } : {}) });
    t.after(() => browser.close());
    const now = Date.now();
    const hash = revision => createHash("sha256").update(String(revision)).digest("hex");
    const action = {
        id: "act_fixture", type: "action", account: "synthetic-account", revision: 1, state: "ready",
        action_hash: hash(1), title: "Choose the design approach", kind: "mail.reply",
        payload: { subject: "Design review", body: "I recommend approach A for the fictional prototype.", importance: "normal" },
        why: "Dana needs a direction before the design review.", target: { to: ["dana@example.com"] },
        source_refs: [], sources: [], approvals: [], executions: [], stale: false,
        work_item_id: "item_fixture", affected_people: ["Dana"],
    };
    const rows = [
        action,
        { id: "item_fixture", type: "item", account: action.account, revision: 1, state: "candidate", confirmed: false,
            data: { title: "Pick a design direction", next_step: "Choose approach A or B", due: null,
                blocker: "A direction is needed before implementation", source_refs: [] } },
        { id: "meeting_fixture", type: "record", kind: "meeting", account: action.account, revision: 1,
            state: "scheduled", data: { title: "Design review", source_refs: [],
                scheduled_start: new Date(now + 61 * 60_000).toISOString(),
                scheduled_end: new Date(now + 91 * 60_000).toISOString() } },
    ];
    const requests = {};
    let sends = 0, writes = 0, readFailure = false;
    const backend = { async run(operation, input = {}) {
        if (operation === "profile") return { assistant_name: "Rowan", work_root: { path: null, status: "not_configured" }, runtime_state_moved: false };
        if (operation === "desk") {
            if (readFailure) throw new BackendError("offline", "Synthetic offline state.", 503);
            return { account: action.account, read_at: new Date(now).toISOString(), items: rows.map(normalizeItem),
                time_preferences: { value: "UTC; Mon-Fri; 09:00-18:00" },
                coverage: { status: "available", sources: [{ family: "mail", scope: "inbox", status: "failed",
                    error_class: "timeout", last_successful_coverage_at: new Date(now - 3600_000).toISOString(),
                    cadence_seconds: 900 }] },
                request_capability: { available: true }, requests, truncated: [], limit_per_type: 50 };
        }
        if (operation === "show") return { item: normalizeItem(rows.find(row => row.id === input.id)) };
        if (operation === "desk-request") {
            const subject = { id: input.id, revision: input.expected_revision, intent: input.intent, action_hash: input.expected_hash ?? null };
            const existing = requests[input.id]?.[input.intent];
            if (existing) return { account: action.account, subject, request: existing, replayed: true };
            const request = { run_id: "task_fixture", phase: "dispatching", deadline_at: new Date(now + 1800_000).toISOString() };
            requests[input.id] ||= {};
            requests[input.id][input.intent] = request;
            return { account: action.account, request, subject,
                replayed: false, claim: { attempt_id: `task_attempt_${"a".repeat(32)}`, token: "a".repeat(48) } };
        }
        if (operation === "desk-dispatched") {
            const request = Object.values(requests).flatMap(Object.values).find(value => value.phase === "dispatching");
            request.phase = input.accepted ? "accepted" : "outcome_unknown";
            return { request };
        }
        if (input.expected_revision !== action.revision || input.expected_hash !== action.action_hash) {
            throw new BackendError("revision_conflict", "Synthetic revision changed.", 409);
        }
        writes++;
        action.revision++;
        action.action_hash = hash(action.revision);
        if (operation === "revise") action.payload = input.payload;
        if (operation === "defer") { action.state = "deferred"; action.deferred_until = input.until; }
        if (operation === "dismiss") action.state = "dismissed";
        return { item: normalizeItem(action) };
    } };
    const panel = await startServer({ backend, host: "sdk:browser-fixture", sendReview: async () => {
        sends++; return "synthetic-message";
    } });
    t.after(() => panel.close());
    const context = await browser.newContext({ viewport: { width: 1180, height: 900 }, colorScheme: "light" });
    const page = await context.newPage();
    const errors = [], outgoing = [];
    page.on("pageerror", error => errors.push(error.message));
    page.on("request", request => { if (!request.url().startsWith(new URL(panel.url).origin)) outgoing.push(request.url()); });
    await page.clock.install({ time: now });
    await page.goto(panel.url);
    await page.locator("#work-account").filter({ hasText: "synthetic-account" }).waitFor({ state: "attached" });
    await page.getByRole("heading", { name: "Rowan Workspace" }).waitFor();
    assert.equal(await page.locator("html").getAttribute("data-theme"), "light");
    assert.match(await page.locator("#work-focus").innerText(), /Choose approach A or B/);
    assert.match(await page.locator("#work-coverage-label").textContent(), /0\/1/);
    await page.locator('[data-focus-key="item:act_fixture"]').click();
    assert.equal(await page.evaluate(() => document.activeElement.id), "work-detail");
    await page.getByText("Edit local proposal payload", { exact: true }).click();
    const editor = page.locator("#work-payload");
    const draft = { subject: "Design review", body: "Edited synthetic recommendation." };
    await page.locator("#work-field-body").fill("Plain text edit of the same exact payload.");
    assert.equal(JSON.parse(await editor.inputValue()).body, "Plain text edit of the same exact payload.");
    assert.equal(JSON.parse(await editor.inputValue()).importance, "normal", "Plain-text editing preserves other payload fields");
    await page.getByText("Advanced: exact payload JSON", { exact: true }).click();
    await editor.fill(JSON.stringify(draft));
    assert.equal(await page.locator("#work-field-body").inputValue(), draft.body);
    assert.equal(await page.locator('#work-detail [data-request="review"]').isDisabled(), true);
    await page.clock.runFor(61_000);
    assert.equal(await editor.inputValue(), JSON.stringify(draft));
    assert.equal(await page.evaluate(() => document.activeElement.id), "work-payload");
    assert.match(await page.locator("#work-focus").innerText(), /Design review/);
    await page.setViewportSize({ width: 360, height: 900 });
    await page.getByRole("button", { name: "Back to list" }).click();
    await page.locator("#work-work-query").fill("Choose the design");
    assert.equal(await page.locator("#work-items > button").count(), 1);
    await page.locator('[data-focus-key="item:act_fixture"]').click();
    assert.equal(await editor.inputValue(), JSON.stringify(draft), "Returning to a dirty detail never reloads over the draft");
    await page.setViewportSize({ width: 1180, height: 900 });
    await page.locator("#work-work-query").fill("");
    await page.locator("#work-save").click();
    await page.locator("#work-notice").filter({ hasText: "Saved a new local revision" }).waitFor();
    assert.equal(action.revision, 2);
    assert.equal(writes, 1);
    assert.equal(action.approvals.length, 0);

    await page.locator("#work-detail").getByText("Assistance", { exact: true }).click();
    await page.locator('#work-detail [data-request="recommend"]').dblclick();
    await page.locator("#work-request-status").filter({ hasText: "accepted" }).waitFor();
    assert.equal(sends, 1);
    assert.match(await page.locator("#work-request-status").innerText(), /has not started/);
    await page.reload();
    await page.locator('[data-focus-key="item:act_fixture"]').click();
    await page.locator("#work-detail").getByText("Assistance", { exact: true }).click();
    assert.equal(await page.locator('#work-detail [data-request="recommend"]').isDisabled(), true);
    const request = requests.act_fixture.recommend;
    request.phase = "working";
    request.lease_expires = new Date(now + 900_000).toISOString();
    await page.locator("#work-refresh").click();
    await page.locator("#work-request-status").filter({ hasText: "Working" }).waitFor();
    request.phase = "ready";
    request.result = { summary: "Prepared recommendation; nothing sent.", work_ids: ["item_fixture"] };
    await page.locator("#work-refresh").click();
    await page.locator("#work-request-status").filter({ hasText: "Local preparation ready" }).waitFor();
    await page.getByRole("button", { name: "Open Pick a design direction" }).click();
    await page.locator("#work-detail").filter({ hasText: "Candidate ask" }).waitFor();
    assert.equal(rows[1].confirmed, false);

    await page.locator('[data-focus-key="item:act_fixture"]').click();
    await page.getByText("Defer or dismiss locally", { exact: true }).click();
    await page.getByRole("button", { name: "Snooze proposal for 1 hour" }).click();
    await page.locator("#work-notice").filter({ hasText: "Deferred" }).waitFor();
    assert.equal(action.state, "deferred");
    assert.equal(await page.locator('[data-focus-key="item:act_fixture"]').count(), 0);
    await page.clock.runFor(3601_000);
    assert.equal(await page.locator('[data-focus-key="item:act_fixture"]').count(), 1);
    assert.equal(action.state, "deferred");

    readFailure = true;
    await page.locator("#work-refresh").click();
    await page.locator("#work-notice").filter({ hasText: "Synthetic offline" }).waitFor();
    assert.equal(await page.locator('#work-detail [data-mutation="defer"]').first().isDisabled(), true);
    await page.clock.runFor(1000);
    assert.match(await page.locator("#work-freshness").textContent(), /Offline/);
    readFailure = false;
    await page.locator("#work-refresh").click();
    await page.locator("#work-notice").filter({ hasText: "Current persistent" }).waitFor();

    await page.setViewportSize({ width: 360, height: 800 });
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
    await page.locator("#work-refresh").focus();
    await page.keyboard.press("Tab");
    assert.notEqual(await page.evaluate(() => document.activeElement.tagName), "BODY");
    await page.emulateMedia({ colorScheme: "dark" });
    await page.locator('html[data-theme="dark"]').waitFor();
    await page.reload();
    await page.locator("#work-account").filter({ hasText: "synthetic-account" }).waitFor({ state: "attached" });
    assert.equal(await page.locator("html").getAttribute("data-theme"), "dark");
    if (process.env.MARGO_BROWSER_SCREENSHOT) await page.screenshot({ path: process.env.MARGO_BROWSER_SCREENSHOT, fullPage: true });
    assert.deepEqual(errors, []);
    assert.deepEqual(outgoing, []);
    assert.equal(sends, 1);
});

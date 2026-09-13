import test from "node:test";
import assert from "node:assert/strict";
import { readFile, mkdir } from "node:fs/promises";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import vm from "node:vm";
import { request as httpRequest } from "node:http";
import { startServer } from "./server.mjs";
import { BackendError, normalizeItem } from "./backend.mjs";

const modulePath = process.env.MARGO_PLAYWRIGHT_MODULE;
const skip = !modulePath && "Set MARGO_PLAYWRIGHT_MODULE to explicitly installed browser-test tooling.";
const metrics = { tool_calls: 2, pages: 1, items: 5, model_calls: 0, output_chars: 240 };
function fixture() {
    const account = "synthetic-ux-account";
    const memory = {
        id: "mem_review", revision: 2, account, domain: "user", kind: "preference", status: "active",
        title: "Leave room to prepare", text: "Keep a short preparation window before a consequential review.\nDiscuss tradeoffs if the calendar is full.",
        authority: "user_confirmed", scope: "meeting preparation", allowed_uses: ["reasoning"],
        source_refs: [{ kind: "user_statement", ref: "conversation:synthetic-review", excerpt: "Please protect preparation time." }],
    };
    const person = { ...memory, id: "mem_person", kind: "person", title: "Dana", text: "Owns the fictional design review.", authority: "source_observed" };
    const step = { key: "prepare-review", title: "Prepare the design review", kind: "local", capability: "local.prepare",
        cost: metrics, depends_on: [], allow_partial: false };
    const run = {
        id: "task_review", account, revision: 3, state: "active", status: "running", plan_hash: "a".repeat(64),
        plan: { goal: "Prepare a clear design recommendation", routine: "meeting-prep", mode: "foreground",
            limits: { ...metrics, output_chars: 4000, max_steps: 3 }, steps: [step] },
        steps: [{ key: step.key, definition: step, revision: 2, state: "running", attempts: 1, in_current_plan: true,
            blocked_reasons: [], result: null, lease_expires: "2026-09-12T21:55:00Z" }],
        usage: metrics, remaining: { ...metrics, output_chars: 3760 }, ready_steps: [],
        expired_claims: [], unresolved_effects: [], blocked_reasons: [],
        created_at: "2026-09-12T21:00:00Z", updated_at: "2026-09-12T21:20:00Z",
        token_usage: null, model_cost: null, approval_granted: false,
        limits_enforcement: "Tracked claims only; not a sandbox over other host tools.",
    };
    const action = normalizeItem({
        id: "act_reply", type: "action", account, revision: 2, action_hash: "b".repeat(64), state: "ready",
        kind: "mail.reply", title: "Choose the design approach", why: "Dana needs a decision before the review.",
        target: { to: ["dana@example.com"] }, payload: { subject: "Design direction", body: "I recommend approach A for the fictional prototype.\nIt keeps the first iteration focused." },
        source_refs: [], approvals: [], executions: [], affected_people: ["Dana"], work_item_id: "item_review",
        sources: [{ family: "mail", source_id: "src_design", revision: "1", current_revision: "1",
            observed_at: "2026-09-12T21:00:00Z", web_link: "https://example.com/design", title: "Design discussion" }],
    });
    const item = normalizeItem({ id: "item_review", account, type: "item", state: "candidate", revision: 1,
        confirmed: false, data: { title: "Pick a design direction", next_step: "Choose between approach A and B",
            blocker: "Implementation needs a direction", owner: "Dana", due: null, source_refs: [] } });
    return { account, memory, person, run, action, item, reads: [], sent: [], offline: false, notInitialized: false, conflict: false };
}
async function server(t, data) {
    const guard = () => {
        if (data.notInitialized) throw new BackendError("not_initialized", "Synthetic state is not initialized; setup required.", 503);
        if (data.offline) throw new BackendError("source_unavailable", "Synthetic local read is offline.", 503);
    };
    const backend = { async run(operation, input = {}) {
        data.reads.push(operation);
        if (operation === "profile") return { account: data.account, assistant_name: "Rowan", work_root: { status: "not_configured" }, runtime_state_moved: false };
        guard();
        if (operation === "desk") return { account: data.account, items: [data.action, data.item],
            read_at: "2026-09-12T21:30:00Z", time_preferences: { value: "UTC; Mon-Fri; 09:00-18:00" },
            profile: { assistant_name: "Rowan" }, coverage: { status: "available", sources: [] },
            request_capability: { available: false, reason: "Synthetic fixture: preparation requests unavailable." },
            requests: {}, truncated: [], limit_per_type: 50 };
        if (operation === "show") return { item: input.id === data.item.id ? data.item : data.action };
        throw new Error("Unexpected UX test operation: " + operation);
    } };
    const memoryBackend = { async run(operation, input = {}) {
        data.reads.push(`memory:${operation}`);
        if (data.memoryUnavailable) throw new BackendError("not_initialized", "Synthetic memory state is not initialized.", 503);
        guard();
        if (operation === "status") return { account: data.account, memory: {}, index: {}, embedding_runtime: { status: "unavailable" } };
        if (operation === "policy") return { account: data.account, revision: 1, configured: true,
            data: { capture: { enabled: false }, usage_enabled: false, retention_days: { preference: 90 } } };
        if (operation === "list") return { account: data.account, memories: [data.memory, data.person] };
        if (operation === "search") {
            if (input.mode !== "lexical") throw new BackendError("model_unavailable", "Local model unavailable. Choose keyword search explicitly.", 503);
            return { account: data.account, results: [{ memory: data.memory, matched_by: ["lexical"] }], warnings: [] };
        }
        if (operation === "show") return { ...data.memory, revision: data.conflict ? 4 : data.memory.revision };
        if (operation === "inspect") {
            if (data.pendingMemoryRead) await data.pendingMemoryRead;
            const memory = input.id === data.person.id ? data.person : data.memory;
            return { memory, history: [{ revision: 1, status: "candidate", created_at: "2026-09-10T12:00:00Z",
                data: { title: memory.title }, evidence: null }], links: [{ source_id: memory.id, relation: "relates_to", target_id: data.person.id }],
                forget_preview: { subject_id: memory.id, revision: memory.revision,
                    affected: [{ id: memory.id, revision: memory.revision, kind: memory.kind, domain: memory.domain }],
                    retained: ["Source documents and earlier exports remain."] }, usage: [] };
        }
        if (operation === "graph") return { account: data.account, nodes: [{ id: data.person.id, revision: 2,
            kind: "person", title: "Dana", selection_reasons: ["explicit_link"], source_record: { store: "memory" } }], edges: [], gaps: [] };
        throw new Error("Unexpected memory operation: " + operation);
    } };
    const taskBackend = { async run(operation) {
        data.reads.push(`task:${operation}`);
        guard();
        if (operation === "health") return { account: data.account, status: "available", runs_by_state: { active: 1 }, expired_claims: 0, unreconciled_attempts: 0 };
        if (operation === "list") return { account: data.account, runs: [{ ...data.run, goal: data.run.plan.goal,
            routine: "meeting-prep", mode: "foreground", step_counts: { running: 1 } }], next_cursor: null };
        if (operation === "show") return { ...data.run, revision: data.conflict ? 5 : data.run.revision };
        if (operation === "history") return { run_id: data.run.id, events: [{ revision: 3, event: "step_started", created_at: data.run.updated_at, data: { step_key: "prepare-review" } }] };
        throw new Error("Unexpected task operation: " + operation);
    } };
    const panel = await startServer({ backend, memoryBackend, taskBackend, sendReview: async message => { data.sent.push(message); return "synthetic-message"; } });
    t.after(() => panel.close());
    return panel;
}
const panels = [
    { key: "action", path: "/", title: "Rowan Workspace", list: "#work-items", heading: "Choose the design approach" },
    { key: "memory", path: "/memory", title: "Rowan Workspace", list: "#memory-results", heading: "Leave room to prepare" },
    { key: "tasks", path: "/tasks", title: "Rowan Workspace", list: "#tasks-runs", heading: "Prepare a clear design recommendation" },
];
function urlFor(panel, path) { const url = new URL(panel.url); url.pathname = path; return url.href; }
async function launch() {
    const { chromium } = await import(pathToFileURL(modulePath).href);
    return chromium.launch({ headless: true,
        ...(process.env.MARGO_BROWSER_EXECUTABLE ? { executablePath: process.env.MARGO_BROWSER_EXECUTABLE } : {}) });
}
async function checkLayout(page) {
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true, "No horizontal page overflow");
    const unlabeled = await page.locator("input,select,textarea").evaluateAll(nodes => nodes.filter(node =>
        !node.labels?.length && !node.getAttribute("aria-label") && !node.getAttribute("aria-labelledby")).map(node => node.id));
    assert.deepEqual(unlabeled, [], "Every field has an accessible name");
    assert.equal(await page.locator("iframe").count(), 0, "One document; no nested app frames");
    const ids = await page.locator("[id]").evaluateAll(nodes => nodes.map(node => node.id));
    assert.equal(new Set(ids).size, ids.length, "Section IDs and label targets are unique");
    assert.equal(await page.locator(".workspace-section:not([hidden]) .notice").getAttribute("aria-live"), "polite");
}
async function contrast(page) {
    return page.evaluate(() => {
        const rgb = value => value.match(/[\d.]+/g)?.map(Number);
        const lum = color => color.map(n => n / 255).map(n => n <= 0.04045 ? n / 12.92 : ((n + 0.055) / 1.055) ** 2.4)
            .reduce((sum, n, i) => sum + n * [0.2126, 0.7152, 0.0722][i], 0);
        const failures = [];
        for (const selector of ["body", ".subtitle", ".workspace-section:not([hidden]) .list-pane .meta", ".workspace-section:not([hidden]) .detail-pane h2", ".workspace-section:not([hidden]) .detail-pane .meta", ".workspace-section:not([hidden]) .detail-pane .reading", ".workspace-section:not([hidden]) .detail-pane .primary"]) {
            const node = document.querySelector(selector);
            if (!node || !node.getClientRects().length) continue;
            const foreground = rgb(getComputedStyle(node).color)?.slice(0, 3);
            let parent = node, bg = [255, 255, 255];
            const layers = [];
            while (parent) {
                const style = getComputedStyle(parent);
                if (style.backgroundColor !== "transparent") layers.push(rgb(style.backgroundColor));
                parent = parent.parentElement;
            }
            for (const layer of layers.reverse().filter(Boolean)) {
                const alpha = layer[3] ?? 1;
                bg = bg.map((n, i) => layer[i] * alpha + n * (1 - alpha));
            }
            if (!bg || !foreground) continue;
            const a = lum(foreground), b = lum(bg), ratio = (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05);
            if (ratio < 4.5) failures.push({ selector, ratio });
        }
        return failures;
    });
}
test("all three canvases: narrow/wide light/dark, readable contrast and keyboard reading flow", { skip }, async t => {
    const browser = await launch();
    t.after(() => browser.close());
    const data = fixture(), panel = await server(t, data);
    if (process.env.MARGO_UX_SCREENSHOTS) await mkdir(process.env.MARGO_UX_SCREENSHOTS, { recursive: true });
    for (const spec of panels) for (const width of [360, 1280]) for (const colorScheme of ["light", "dark"]) {
        await t.test(`${spec.key} ${width}px ${colorScheme}`, async () => {
            const context = await browser.newContext({ viewport: { width, height: 900 }, colorScheme });
            const page = await context.newPage(), errors = [], remote = [];
            page.on("pageerror", error => errors.push(error.message));
            page.on("request", request => { if (!request.url().startsWith(new URL(panel.url).origin)) remote.push(request.url()); });
            await page.goto(urlFor(panel, spec.path));
            await page.getByRole("heading", { name: spec.title }).waitFor();
            await page.locator(`${spec.list} > button`).first().waitFor();
            await checkLayout(page);
            assert.equal(await page.locator("html").getAttribute("data-theme"), colorScheme);
            if (process.env.MARGO_UX_SCREENSHOTS) await page.screenshot({ path: join(process.env.MARGO_UX_SCREENSHOTS, `${spec.key}-${width}-${colorScheme}-list.png`) });
            const first = page.locator(`${spec.list} > button`).first();
            await first.focus();
            await page.keyboard.press("Enter");
            await page.locator(".workspace-section:not([hidden]) .detail-pane h2").filter({ hasText: spec.heading }).waitFor();
            assert.equal(await page.evaluate(() => document.activeElement.id === "work-detail" || document.activeElement.tagName === "H2"), true);
            assert.equal(await page.locator(".workspace-section:not([hidden]) .detail-pane pre:visible").count(), 0, "Raw records are progressively disclosed");
            if (width === 360) assert.equal(await page.locator(".workspace-section:not([hidden]) .list-pane").isVisible(), false);
            await checkLayout(page);
            assert.deepEqual(await contrast(page), []);
            if (process.env.MARGO_UX_SCREENSHOTS) await page.screenshot({ path: join(process.env.MARGO_UX_SCREENSHOTS, `${spec.key}-${width}-${colorScheme}-detail.png`) });
            await page.keyboard.press("Escape");
            assert.equal(await page.locator(".workspace-section:not([hidden]) .list-pane").isVisible(), true);
            assert.equal(await page.evaluate(() => document.activeElement.getAttribute("aria-current")), "true");
            // Explicit host theme wins over the local fallback without changing data or routes.
            await page.evaluate(() => {
                document.documentElement.style.setProperty("--background-color-default", "#101010");
                document.documentElement.style.setProperty("--text-color-default", "#eeeeee");
                document.documentElement.style.setProperty("--text-color-muted", "#bbbbbb");
                document.documentElement.setAttribute("data-color-mode", "dark");
            });
            await page.locator('html[data-theme="dark"]').waitFor();
            assert.equal(await page.locator("body").evaluate(node => getComputedStyle(node).backgroundColor), "rgb(16, 16, 16)");
            assert.deepEqual(errors, []);
            assert.deepEqual(remote, []);
            await context.close();
        });
    }
    assert.equal(data.sent.length, 0, "Browsing never sends review requests");
});

test("memory UX: explicit search fallback, evidence, review conflict, retained offline detail and recovery", { skip }, async t => {
    const browser = await launch(); t.after(() => browser.close());
    const data = fixture(), panel = await server(t, data);
    const page = await browser.newPage({ viewport: { width: 380, height: 900 } });
    await page.goto(urlFor(panel, "/memory"));
    await page.locator("#memory-results > button").first().waitFor();
    await page.locator("#memory-query").fill("preparation");
    await page.locator("#memory-query").press("Enter");
    await page.locator("#memory-notice").filter({ hasText: "Local model unavailable" }).waitFor();
    assert.equal(await page.locator("#memory-results > button").count(), 2, "Last read survives failed search");
    assert.equal(await page.locator("#memory-mode").inputValue(), "hybrid");
    await page.locator("#memory-mode").selectOption("lexical");
    assert.equal(await page.locator("#memory-search-options").getAttribute("open"), "");
    await page.locator("#memory-search").click();
    await page.locator("#memory-notice").filter({ hasText: "Choose a domain" }).waitFor();
    await page.locator("#memory-domain").selectOption("user");
    await page.locator("#memory-search").click();
    await page.locator("#memory-count").filter({ hasText: "1 of 1" }).waitFor();
    await page.locator("#memory-results > button").first().click();
    await page.locator("#memory-detail h2").waitFor();
    await page.locator("#memory-detail").getByText("Relationships & dependencies (1)", { exact: true }).click();
    await page.getByRole("button", { name: "Inspect current relationship graph (2 hops, at most 20 nodes)" }).click();
    await page.locator("#memory-detail").getByText("Eligible graph nodes", { exact: true }).waitFor();
    await page.locator("#memory-detail").getByText("Assistance", { exact: true }).click();
    await page.getByRole("button", { name: "Request correction review", exact: true }).click();
    await page.locator("#memory-notice").filter({ hasText: "requested" }).waitFor();
    assert.equal(data.sent.length, 1);
    assert.match(data.sent[0].prompt, /NOT approval/);
    assert.doesNotMatch(data.sent[0].prompt, /Keep a short preparation/);
    data.conflict = true;
    await page.getByRole("button", { name: "Request correction review", exact: true }).click();
    await page.locator("#memory-notice").filter({ hasText: "Reload the selected memory" }).waitFor();
    assert.equal(await page.locator('[data-review="correct"]').isDisabled(), true);
    data.conflict = false;
    await page.getByRole("button", { name: "Reload selected memory" }).click();
    await page.locator("#memory-notice").filter({ hasText: "Inspection only" }).waitFor();
    assert.equal(await page.locator('[data-review="correct"]').isDisabled(), false);
    data.offline = true;
    await page.getByRole("button", { name: "Reload selected memory" }).click();
    await page.locator("#memory-notice").filter({ hasText: "offline" }).waitFor();
    assert.match(await page.locator("#memory-detail h2").textContent(), /Leave room to prepare/);
    assert.equal(await page.locator('[data-review="correct"]').isDisabled(), true);
    data.offline = false;
    await page.getByRole("button", { name: "Reload selected memory" }).click();
    await page.locator("#memory-notice").filter({ hasText: "Inspection only" }).waitFor();
    assert.equal(await page.locator('[data-review="correct"]').isDisabled(), false);
    await page.getByRole("button", { name: "Back to list" }).click();
    assert.equal(await page.locator("#memory-query").inputValue(), "preparation");
    data.notInitialized = true;
    await page.reload();
    await page.locator("#memory-notice").filter({ hasText: "not initialized" }).waitFor();
    assert.equal(await page.locator("#memory-results > button").count(), 0);
    assert.match(await page.locator("#memory-results").textContent(), /unavailable/);
});

test("task UX: local search, exact review, stale refresh, honest progress, history and setup recovery", { skip }, async t => {
    const browser = await launch(); t.after(() => browser.close());
    const data = fixture(), panel = await server(t, data);
    const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
    await page.goto(urlFor(panel, "/tasks"));
    await page.locator("#tasks-runs > button").waitFor();
    await page.locator("#tasks-task-query").fill("missing");
    assert.equal(await page.locator("#tasks-runs > button").count(), 0);
    await page.locator("#tasks-task-query").fill("design");
    await page.locator("#tasks-runs > button").click();
    await page.locator("#tasks-detail h2").waitFor();
    assert.match(await page.locator(".step-card h4").textContent(), /Prepare the design review/);
    assert.equal(await page.locator("#tasks-detail progress").getAttribute("value"), "0");
    await page.locator("#tasks-detail").getByText("Assistance", { exact: true }).click();
    await page.getByRole("button", { name: "Request pause", exact: true }).click();
    await page.locator("#tasks-notice").filter({ hasText: "Foreground review" }).waitFor();
    assert.equal(data.sent.length, 1);
    assert.equal(data.run.status, "running");
    data.run.revision++;
    data.run.status = "outcome_unknown";
    data.run.unresolved_effects = ["prepare-review"];
    await page.locator("#tasks-refresh-runs").click();
    await page.locator("#tasks-notice").filter({ hasText: "selected run changed" }).waitFor();
    assert.equal(await page.locator('[data-review="pause"]').isDisabled(), true);
    await page.getByRole("button", { name: "Reload this run" }).click();
    await page.locator("#tasks-detail").getByText("Assistance", { exact: true }).click();
    await page.locator('#tasks-detail [data-review="reconcile"]').waitFor();
    assert.match(await page.locator("#tasks-detail .warn").first().textContent(), /unknown effect/);
    await page.getByText("Activity history", { exact: true }).click();
    await page.getByRole("button", { name: "Load history (most recent 20 events)" }).click();
    await page.getByText("Step started", { exact: true }).waitFor();
    data.offline = true;
    await page.locator("#tasks-refresh-runs").click();
    await page.locator("#tasks-notice").filter({ hasText: "offline" }).waitFor();
    assert.equal(await page.locator("#tasks-runs > button").count(), 1);
    assert.equal(await page.locator('[data-review="reconcile"]').isDisabled(), true);
    data.offline = false; data.notInitialized = true;
    await page.reload();
    await page.locator("#tasks-notice").filter({ hasText: "not initialized" }).waitFor();
    assert.match(await page.locator("#tasks-runs").textContent(), /unavailable/);
    data.notInitialized = false;
    await page.locator("#tasks-refresh-runs").click();
    await page.locator("#tasks-runs > button").waitFor();
    assert.equal(data.sent.length, 1);
});

test("shared design is local, nonce-protected, theme-aware and read-only across all pages", async t => {
    const panel = await server(t, fixture());
    for (const spec of panels) {
        const response = await fetch(urlFor(panel, spec.path));
        const html = await response.text();
        assert.match(html, /--cp-accent:/);
        assert.match(html, /--background-color-default/);
        assert.match(html, /src="\/ui\.js"/);
        assert.doesNotMatch(html, /__STYLES__|__NONCE__|unsafe-inline/);
        assert.match(response.headers.get("content-security-policy"), /default-src 'none'/);
    }
    const ui = await readFile(new URL("./ui.js", import.meta.url), "utf8");
    assert.doesNotMatch(ui, /innerHTML|outerHTML|eval\(|fetch\(|localStorage|sessionStorage/);
});

test("one workspace preserves all section state, draft and focus through tabs and browser history", { skip }, async t => {
    const browser = await launch(); t.after(() => browser.close());
    for (const width of [360, 1280]) for (const colorScheme of ["light", "dark"]) {
        await t.test(`${width}px ${colorScheme}`, async t => {
            const data = fixture(), panel = await server(t, data);
            const context = await browser.newContext({ viewport: { width, height: 900 }, colorScheme });
            t.after(() => context.close());
            const page = await context.newPage(), documents = [], errors = [];
            page.on("request", request => { if (request.resourceType() === "document") documents.push(request.url()); });
            page.on("pageerror", error => errors.push(error.message));
            await page.goto(panel.url);
            await page.locator("#work-items > button").first().waitFor();
            await page.evaluate(() => { window.syntheticWorkspaceMarker = "same-document"; });
            assert.equal(data.reads.some(operation => /^(memory|task):/.test(operation)), false, "Other sections load on first visit only");
            const originalToken = new URL(page.url()).hash;
            await page.locator("#work-work-query").fill("design");
            await page.locator('#work-filters [data-focus-key="filter:Needs decision"]').click();
            await page.locator("#work-items > button").first().click();
            await page.getByText("Edit local proposal payload", { exact: true }).click();
            await page.locator("#work-field-body").fill("A local draft kept while I inspect context and progress.");
            await page.locator("#work-field-body").focus();
            const exactDraft = await page.locator("#work-payload").inputValue();
            await page.getByRole("tab", { name: "Memory", exact: true }).click();
            await page.locator("#memory-results > button").first().waitFor();
            assert.equal(new URL(page.url()).pathname, "/memory");
            await page.locator("#memory-mode").selectOption("lexical");
            await page.locator("#memory-domain").selectOption("user");
            await page.locator("#memory-query").fill("preparation");
            await page.locator("#memory-search").click();
            await page.locator("#memory-count").filter({ hasText: "1 of 1" }).waitFor();
            await page.locator("#memory-views").getByRole("button", { name: "Facts", exact: true }).click();
            await page.locator("#memory-results > button").first().click();
            await page.locator("#memory-detail h2").waitFor();
            await page.getByText("Why this memory", { exact: true }).click();
            const memoryReads = data.reads.filter(operation => operation.startsWith("memory:")).length;
            await page.getByRole("tab", { name: "Tasks", exact: true }).click();
            await page.locator("#tasks-runs > button").waitFor();
            await page.locator("#tasks-task-query").fill("design");
            await page.locator("#tasks-views").getByRole("button", { name: "Running", exact: true }).click();
            await page.locator("#tasks-runs > button").click();
            await page.locator("#tasks-detail h2").waitFor();
            await page.getByText("Activity history", { exact: true }).click();
            await page.getByRole("button", { name: "Load history (most recent 20 events)" }).click();
            await page.getByText("Step started", { exact: true }).waitFor();
            const taskReads = data.reads.filter(operation => operation.startsWith("task:")).length;
            await page.getByRole("tab", { name: "Work", exact: true }).click();
            await page.locator("#work-detail").waitFor();
            await page.locator('#work-workspace[aria-busy="false"]').waitFor();
            assert.equal(await page.locator("#work-payload").inputValue(), exactDraft);
            assert.equal(await page.locator("#work-field-body").inputValue(), "A local draft kept while I inspect context and progress.");
            assert.equal(await page.locator("#work-work-query").inputValue(), "design");
            assert.equal(await page.locator('#work-filters [data-focus-key="filter:Needs decision"]').getAttribute("aria-pressed"), "true");
            assert.equal(await page.evaluate(() => document.activeElement.id), "work-field-body");
            assert.equal(await page.locator("#work-save").isDisabled(), false, "The unsaved exact local draft remains editable");
            await page.goBack();
            await page.locator("#section-tasks:not([hidden])").waitFor();
            assert.equal(await page.locator("#tasks-task-query").inputValue(), "design");
            assert.equal(await page.locator("#tasks-views").getByRole("button", { name: "Running", exact: true, includeHidden: true }).getAttribute("aria-pressed"), "true");
            assert.equal(await page.getByText("Step started", { exact: true }).isVisible(), true);
            assert.equal(data.reads.filter(operation => operation.startsWith("task:")).length, taskReads);
            await page.goBack();
            await page.locator("#section-memory:not([hidden])").waitFor();
            assert.equal(await page.locator("#memory-query").inputValue(), "preparation");
            assert.equal(await page.locator("#memory-mode").inputValue(), "lexical");
            assert.equal(await page.locator("#memory-views").getByRole("button", { name: "Facts", exact: true, includeHidden: true }).getAttribute("aria-pressed"), "true");
            assert.equal(await page.locator("#memory-detail").getByText("Why selected: lexical", { exact: true }).isVisible(), true);
            assert.equal(data.reads.filter(operation => operation.startsWith("memory:")).length, memoryReads);
            await page.goForward();
            await page.locator("#section-tasks:not([hidden])").waitFor();
            await page.getByRole("tab", { name: "Tasks", exact: true }).focus();
            await page.keyboard.press("Home");
            assert.equal(await page.evaluate(() => document.activeElement.id), "tab-work");
            await page.keyboard.press("Enter");
            await page.locator("#section-work:not([hidden])").waitFor();
            assert.equal(await page.locator("#work-payload").inputValue(), exactDraft);
            assert.equal(new URL(page.url()).hash, originalToken);
            assert.equal(new URL(page.url()).origin, new URL(panel.url).origin);
            assert.equal(await page.evaluate(() => window.syntheticWorkspaceMarker), "same-document");
            assert.equal(documents.length, 1, "Navigation never reloads or creates a second document");
            assert.equal(context.pages().length, 1);
            assert.equal(data.sent.length, 0);
            assert.deepEqual(errors, []);
            await checkLayout(page);
        });
    }
});

test("switching during a read never steals focus; unavailable sections do not break other sections", { skip }, async t => {
    const browser = await launch(); t.after(() => browser.close());
    const data = fixture(), panel = await server(t, data);
    const page = await browser.newPage({ viewport: { width: 380, height: 900 } });
    await page.goto(urlFor(panel, "/memory"));
    await page.locator("#memory-results > button").first().waitFor();
    let release;
    data.pendingMemoryRead = new Promise(resolve => { release = resolve; });
    await page.locator("#memory-results > button").first().click();
    await page.locator('#memory-workspace[aria-busy="true"]').waitFor();
    await page.getByRole("tab", { name: "Tasks", exact: true }).click();
    await page.locator("#tasks-runs > button").waitFor();
    await page.locator("#tasks-task-query").fill("design");
    release();
    await page.locator('#memory-workspace[aria-busy="false"]').waitFor({ state: "attached" });
    assert.equal(await page.evaluate(() => document.activeElement.id), "tasks-task-query");
    await page.getByRole("tab", { name: "Memory", exact: true }).click();
    await page.locator("#memory-detail h2").waitFor();
    data.memoryUnavailable = true;
    await page.getByRole("button", { name: "Reload selected memory" }).click();
    await page.locator("#memory-notice").filter({ hasText: "not initialized" }).waitFor();
    await page.getByRole("tab", { name: "Work", exact: true }).click();
    await page.locator("#work-items > button").first().waitFor();
    await page.getByRole("tab", { name: "Memory", exact: true }).click();
    assert.equal(await page.locator('#memory-detail [data-review="correct"]').isDisabled(), true);
    assert.match(await page.locator("#memory-notice").textContent(), /not initialized/);
    assert.equal(data.sent.length, 0);
});

test("an account change hides all cached sections and blocks cross-account operations until explicit reload", { skip }, async t => {
    const browser = await launch(); t.after(() => browser.close());
    const data = fixture(), panel = await server(t, data);
    const page = await browser.newPage();
    await page.goto(panel.url);
    await page.locator("#work-items > button").first().waitFor();
    await page.getByRole("tab", { name: "Memory", exact: true }).click();
    await page.locator("#memory-results > button").first().waitFor();
    await page.getByRole("tab", { name: "Tasks", exact: true }).click();
    await page.locator("#tasks-runs > button").waitFor();
    data.account = "different-fixture-account";
    await page.locator("#tasks-refresh-runs").click();
    await page.locator("#workspace-alert").filter({ hasText: "account changed" }).waitFor();
    assert.equal(await page.locator(".workspace-section:visible").count(), 0);
    assert.equal(await page.locator('#workspace-tabs button:disabled').count(), 5);
    assert.equal(await page.getByRole("button", { name: "Reload workspace for the current account" }).isVisible(), true);
    assert.equal(data.sent.length, 0);
});

test("unified and legacy routes share one static shell with unchanged token Host Origin and input guards", async t => {
    const data = fixture(), panel = await server(t, data);
    const base = new URL(panel.url), token = new URLSearchParams(base.hash.slice(1)).get("token");
    for (const [path, initial] of [["/", "work"], ["/memory", "memory"], ["/tasks", "tasks"]]) {
        const response = await fetch(new URL(path, base));
        const html = await response.text();
        assert.match(html, new RegExp(`data-initial-section="${initial}"`));
        assert.equal((html.match(/role="tabpanel"/g) || []).length, 5);
        assert.equal((html.match(/<h1 /g) || []).length, 1);
        assert.doesNotMatch(html, /<iframe|__WORK_SECTION__|__MEMORY_SECTION__|__TASKS_SECTION__/);
        assert.equal((await fetch(new URL(`${path}?section=arbitrary`, base))).status, 404);
        assert.equal((await fetch(new URL(`${path}?scoutTheme=dark`, base))).status, 200);
        const status = await new Promise((resolve, reject) => {
            const request = httpRequest(new URL(path, base), { headers: { Host: "rebound.example" } }, response => {
                response.resume(); response.on("end", () => resolve(response.statusCode));
            });
            request.on("error", reject); request.end();
        });
        assert.equal(status, 403);
    }
    for (const path of ["/api/desk", "/api/profile", "/api/memory/list", "/api/task/list"]) {
        const post = /\/(memory|task)\//.test(path);
        const options = { method: post ? "POST" : "GET", ...(post ? { body: "{}" } : {}) };
        assert.equal((await fetch(new URL(path, base), { ...options, headers: { "Content-Type": "application/json", Origin: base.origin } })).status, 403);
        assert.equal((await fetch(new URL(path, base), { ...options,
            headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}`, Origin: "https://example.com" } })).status, 403);
    }
    assert.equal(data.sent.length, 0);
    assert.equal(data.reads.length, 0);
});

test("canonical declaration and both legacy IDs open the same unified server without losing read actions", async () => {
    const source = (await readFile(new URL("./extension.mjs", import.meta.url), "utf8")).replace(/^import .*;\r?$/gm, "");
    const reads = [], starts = [], closed = [];
    const backend = { run: async (operation, input) => {
        reads.push(["work", operation, input]);
        return operation === "profile" ? { assistant_name: "Rowan" } : { items: [] };
    } };
    const memory = { run: async (...args) => { reads.push(["memory", ...args]); return {}; } };
    const tasks = { run: async (...args) => { reads.push(["tasks", ...args]); return {}; } };
    const sandbox = {
        URL, Map, CanvasError: Error, createBackend: () => backend, createMemoryBackend: () => memory,
        createProactiveTools: () => ({ tools: [], hooks: {} }), createAutomationBackend: () => ({}), createAutomationTool: () => ({}),
        createTaskBackend: () => tasks, createCanvas: value => value,
        joinSession: async value => { sandbox.declarations = value.canvases; return { sessionId: "fixture", send: async () => "message" }; },
        startServer: async options => { starts.push(options); return {
            url: "http://127.0.0.1:12345/#token=synthetic", refresh() {},
            close: async () => { closed.push(true); },
        }; },
    };
    await vm.runInNewContext(`(async () => { ${source} })()`, sandbox);
    const [canonical, oldMemory, oldTasks] = sandbox.declarations;
    assert.equal(canonical.id, "margo-action-desk");
    assert.equal(canonical.displayName, "Margo Workspace");
    assert.deepEqual(Array.from(canonical.inputSchema.properties.section.enum), ["work", "memory", "tasks", "automations", "config"]);
    assert.deepEqual(Array.from(canonical.actions, action => action.name), ["snapshot", "list", "show", "refresh"]);
    for (const [entry, instanceId, section] of [[canonical, "one", "memory"], [oldMemory, "legacy-memory", "memory"], [oldTasks, "legacy-tasks", "tasks"]]) {
        const result = await entry.open({ instanceId, input: { ...(entry === canonical ? { section } : {}) } });
        assert.equal(result.title, "Rowan Workspace");
        assert.equal(new URL(result.url).pathname, `/${section}`);
        assert.equal(starts.at(-1).backend, backend);
        assert.equal(starts.at(-1).memoryBackend, memory);
        assert.equal(starts.at(-1).taskBackend, tasks);
        assert.equal(starts.at(-1).host, "sdk:fixture");
    }
    await canonical.open({ instanceId: "one", input: {} });
    assert.equal(starts.length, 3, "Reopening an instance reuses its loopback server");
    await oldMemory.actions[0].handler({ input: {} });
    await oldTasks.actions[0].handler({ input: {} });
    assert.ok(reads.some(row => row[0] === "memory"));
    assert.ok(reads.some(row => row[0] === "tasks"));
    await canonical.onClose({ instanceId: "one" });
    assert.equal(closed.length, 1);
});

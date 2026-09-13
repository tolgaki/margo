// Opt-in validation through the installed Chromium browser's protocol; no package or browser download.
import test from "node:test";
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { once } from "node:events";
import { cp, mkdtemp, mkdir, readFile, realpath, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { setTimeout as delay } from "node:timers/promises";
import { startServer } from "./server.mjs";
import { BackendError, createBackend, executeWithInput, normalizeItem } from "./backend.mjs";
import { createAutomationBackend } from "./automation-backend.mjs";

const executable = process.env.MARGO_BROWSER_EXECUTABLE;
const skip = !executable ? "Set MARGO_BROWSER_EXECUTABLE to an existing Chromium/Edge executable; no installation is performed."
    : typeof WebSocket !== "function" ? "The native browser rehearsal requires Node with built-in WebSocket." : false;

async function browser(t) {
    const profile = await mkdtemp(join(tmpdir(), "margo-quiet-browser-"));
    const child = spawn(executable, ["--headless=new", "--no-first-run", "--no-default-browser-check",
        "--disable-background-networking", "--disable-component-update", "--disable-sync",
        "--remote-debugging-address=127.0.0.1", "--remote-debugging-port=0", `--user-data-dir=${profile}`, "about:blank"],
    { stdio: ["ignore", "ignore", "pipe"], windowsHide: true });
    let socket;
    t.after(async () => {
        if (child.exitCode === null) {
            const exited = once(child, "exit");
            if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 1_000_000, method: "Browser.close" }));
            const timer = setTimeout(() => { if (child.exitCode === null) child.kill(); }, 5000);
            await exited;
            clearTimeout(timer);
        }
        socket?.close();
        await rm(profile, { recursive: true, force: true, maxRetries: 3, retryDelay: 100 });
    });
    const endpoint = await new Promise((resolve, reject) => {
        const timer = setTimeout(() => reject(new Error("Browser debugging endpoint did not start.")), 15_000);
        let output = "";
        child.stderr.on("data", chunk => {
            output += chunk;
            const match = /DevTools listening on (ws:\/\/127\.0\.0\.1:\d+\/\S+)/.exec(output);
            if (match) { clearTimeout(timer); resolve(match[1]); }
        });
        child.once("error", error => { clearTimeout(timer); reject(error); });
        child.once("exit", () => { clearTimeout(timer); reject(new Error("Browser exited before becoming responsive.")); });
    });
    socket = new WebSocket(endpoint);
    await new Promise((resolve, reject) => {
        socket.addEventListener("open", resolve, { once: true });
        socket.addEventListener("error", reject, { once: true });
    });
    let sequence = 0;
    const pending = new Map(), listeners = [];
    socket.addEventListener("message", event => {
        const message = JSON.parse(event.data);
        if (message.id) {
            const request = pending.get(message.id);
            if (!request) return;
            pending.delete(message.id);
            clearTimeout(request.timer);
            if (message.error) request.reject(new Error(message.error.message));
            else request.resolve(message.result);
        } else for (const listener of listeners) listener(message);
    });
    function send(method, params = {}, sessionId) {
        const id = ++sequence;
        return new Promise((resolve, reject) => {
            const timer = setTimeout(() => { pending.delete(id); reject(new Error(`Browser protocol timed out: ${method}`)); }, 15_000);
            pending.set(id, { resolve, reject, timer });
            socket.send(JSON.stringify({ id, method, params, ...(sessionId ? { sessionId } : {}) }));
        });
    }
    await send("Browser.getVersion");
    return {
        async page(width, theme) {
            const { browserContextId } = await send("Target.createBrowserContext");
            const { targetId } = await send("Target.createTarget", { url: "about:blank", browserContextId });
            const { sessionId } = await send("Target.attachToTarget", { targetId, flatten: true });
            const errors = [], documents = [];
            const listener = message => {
                if (message.sessionId !== sessionId) return;
                if (message.method === "Runtime.exceptionThrown") errors.push(message.params.exceptionDetails.text);
                if (message.method === "Network.requestWillBeSent" && message.params.type === "Document") documents.push(message.params.request.url);
            };
            listeners.push(listener);
            const call = (method, params) => send(method, params, sessionId);
            await call("Runtime.enable"); await call("Page.enable"); await call("Network.enable");
            await call("Page.bringToFront");
            await call("Emulation.setDeviceMetricsOverride", { width, height: 900, deviceScaleFactor: 1, mobile: false });
            await call("Emulation.setEmulatedMedia", { features: [{ name: "prefers-color-scheme", value: theme }] });
            async function evaluate(expression) {
                const result = await call("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true, userGesture: true });
                if (result.exceptionDetails) throw new Error(result.exceptionDetails.exception?.description || result.exceptionDetails.text);
                return result.result.value;
            }
            async function wait(expression) {
                for (let attempt = 0; attempt < 150; attempt++) {
                    if (await evaluate(expression)) return;
                    await delay(50);
                }
                const state = await evaluate("({focus: document.activeElement?.id, notices: Array.from(document.querySelectorAll('.notice')).map(node => node.textContent)})");
                throw new Error(`Browser condition not reached: ${expression}; ${JSON.stringify({ state, errors })}`);
            }
            return {
                evaluate, wait, errors, documents,
                goto: url => call("Page.navigate", { url }),
                async key(key) {
                    const codes = { Enter: 13, Escape: 27, Tab: 9, ArrowRight: 39 };
                    await call("Input.dispatchKeyEvent", { type: "rawKeyDown", key, code: key, windowsVirtualKeyCode: codes[key] });
                    if (key === "Enter") await call("Input.dispatchKeyEvent", { type: "char", text: "\r", key, code: key, windowsVirtualKeyCode: 13 });
                    await call("Input.dispatchKeyEvent", { type: "keyUp", key, code: key, windowsVirtualKeyCode: codes[key] });
                },
                async click(selector) {
                    await wait(`!!document.querySelector(${JSON.stringify(selector)})`);
                    const point = await evaluate(`(() => {
                        const node = document.querySelector(${JSON.stringify(selector)});
                        if (!node.checkVisibility({ checkVisibilityCSS: true }) || node.disabled) throw new Error("Control is hidden or disabled: " + ${JSON.stringify(selector)});
                        node.scrollIntoView({ block: "center" });
                        const rect = node.getBoundingClientRect();
                        return { x: rect.x + rect.width / 2, y: rect.y + rect.height / 2 };
                    })()`);
                    await call("Input.dispatchMouseEvent", { type: "mousePressed", ...point, button: "left", clickCount: 1 });
                    await call("Input.dispatchMouseEvent", { type: "mouseReleased", ...point, button: "left", clickCount: 1 });
                },
                fill: (selector, value) => evaluate(`(() => {
                    const node = document.querySelector(${JSON.stringify(selector)});
                    if (!node.checkVisibility({ checkVisibilityCSS: true }) || node.disabled || node.readOnly) throw new Error("Field is not editable.");
                    node.focus(); node.value = ${JSON.stringify(value)}; node.dispatchEvent(new Event("input", { bubbles: true }));
                })()`),
                async screenshot(path) {
                    const result = await call("Page.captureScreenshot", { format: "png", captureBeyondViewport: false });
                    await writeFile(path, Buffer.from(result.data, "base64"));
                },
                async close() {
                    listeners.splice(listeners.indexOf(listener), 1);
                    await send("Target.disposeBrowserContext", { browserContextId });
                },
            };
        },
    };
}

async function fixture(t) {
    const hash = revision => String(revision).padStart(64, "a");
    const account = "quiet-fixture";
    const action = { id: "act_quiet", type: "action", account, revision: 1, state: "ready",
        action_hash: hash(1), kind: "mail.reply", title: "Design review response", why: "A direction is needed for the next iteration.",
        target: { to: ["dana@example.com"] }, payload: { subject: "Design review", body: "Approach A keeps the first iteration focused.", importance: "normal" },
        source_refs: [], sources: [], approvals: [], executions: [], affected_people: ["Dana"] };
    const memory = { id: "mem_quiet", account, revision: 1, domain: "user", kind: "preference", status: "active",
        title: "Review preparation", text: "Leave time to read the decision notes before a review.",
        authority: "user_confirmed", scope: "reviews", source_refs: [], allowed_uses: ["reasoning"] };
    const cost = { tool_calls: 1, model_calls: 0, pages: 0, items: 1, output_chars: 200 };
    const run = { id: "task_quiet", account, revision: 1, state: "active", status: "running", plan_hash: "b".repeat(64),
        plan: { goal: "Prepare review notes", routine: "meeting-prep", mode: "foreground", limits: cost,
            steps: [{ key: "notes", title: "Read the design notes" }] },
        steps: [{ key: "notes", revision: 1, state: "running", definition: { title: "Read the design notes", kind: "local" },
            attempts: 1, in_current_plan: true, blocked_reasons: [], result: null }],
        usage: cost, remaining: cost, expired_claims: [], unresolved_effects: [], blocked_reasons: [], ready_steps: [],
        created_at: "2026-09-12T12:00:00Z", updated_at: "2026-09-12T12:00:00Z",
        token_usage: null, model_cost: null, approval_granted: false, limits_enforcement: "Tracked work only." };
    const data = { writes: 0, sent: [], requests: {}, input: null, offline: false, conflict: false, action, memory, run };
    const backend = { async run(operation, input = {}) {
        if (operation === "profile") return { account, assistant_name: "Rowan", work_root: { status: "not_configured" }, runtime_state_moved: false };
        if (data.offline) throw new BackendError("unavailable", "Synthetic ledger is offline.", 503);
        if (operation === "desk") return { account, items: [normalizeItem(action)], read_at: new Date().toISOString(),
            coverage: { status: "available", sources: [] }, time_preferences: { value: "UTC; Mon-Fri; 09:00-18:00" },
            request_capability: { available: true }, requests: data.requests, truncated: [], limit_per_type: 50 };
        if (operation === "show") return { item: normalizeItem(action) };
        if (operation === "desk-request") {
            data.input = input;
            const request = { run_id: "task_request", phase: "dispatching" };
            return { account, request, subject: { id: input.id, revision: input.expected_revision, action_hash: input.expected_hash, intent: input.intent },
                claim: { attempt_id: `task_attempt_${"a".repeat(32)}`, token: "a".repeat(48) }, replayed: false };
        }
        if (operation === "desk-dispatched") {
            const request = { run_id: "task_request", phase: "accepted" };
            data.requests[action.id] = { [data.input.intent]: request };
            return { request };
        }
        if (operation === "revise") {
            if (input.expected_revision !== action.revision || input.expected_hash !== action.action_hash) throw new BackendError("revision_conflict", "Synthetic revision changed.", 409);
            data.writes++;
            action.revision++; action.action_hash = hash(action.revision); action.payload = input.payload;
            return { item: normalizeItem(action) };
        }
        throw new Error("Unexpected synthetic operation: " + operation);
    } };
    const memoryBackend = { async run(operation) {
        if (operation === "list") return { account, memories: [memory] };
        if (operation === "status") return { account, memory: {}, index: {}, embedding_runtime: { status: "unavailable" } };
        if (operation === "policy") return { account, revision: 1, configured: true, data: { capture: { enabled: false }, retention_days: {} } };
        if (operation === "show") return { ...memory, revision: data.conflict ? 2 : 1 };
        if (operation === "inspect") return { memory, links: [], history: [], usage: [],
            forget_preview: { subject_id: memory.id, revision: 1, affected: [], retained: ["Sources remain."] } };
        throw new Error("Unexpected synthetic memory operation: " + operation);
    } };
    const taskBackend = { async run(operation) {
        if (operation === "health") return { account, status: "available", runs_by_state: { active: 1 }, expired_claims: 0, unreconciled_attempts: 0 };
        if (operation === "list") return { account, runs: [{ ...run, goal: run.plan.goal, routine: run.plan.routine, mode: run.plan.mode, step_counts: { running: 1 } }], next_cursor: null };
        if (operation === "show") return run;
        throw new Error("Unexpected synthetic task operation: " + operation);
    } };
    const panel = await startServer({ backend, memoryBackend, taskBackend, host: "sdk:quiet-fixture",
        sendReview: async value => { data.sent.push(value); return "quiet-message"; } });
    t.after(() => panel.close());
    return { ...panel, data };
}

function inspectView() {
    const visible = node => node.checkVisibility({ checkVisibilityCSS: true });
    const section = document.querySelector(".workspace-section:not([hidden])");
    const colors = [];
    const neutral = value => {
        const rgb = value.match(/[\d.]+/g)?.slice(0, 3).map(Number);
        return !rgb || Math.max(...rgb) - Math.min(...rgb) <= 6;
    };
    for (const node of document.querySelectorAll("body, .card, .badge, .item, [role=tab], .filters button, .view-tabs button, .step-card, .assistance button, .detail-pane")) {
        if (!visible(node)) continue;
        const style = getComputedStyle(node);
        for (const property of ["backgroundColor", "borderTopColor", "color"]) {
            if (!neutral(style[property])) colors.push({ tag: node.tagName, class: node.className, property, value: style[property] });
        }
    }
    const rgb = value => value.match(/[\d.]+/g)?.map(Number);
    const luminance = color => color.map(n => n / 255).map(n => n <= .04045 ? n / 12.92 : ((n + .055) / 1.055) ** 2.4)
        .reduce((sum, n, i) => sum + n * [.2126, .7152, .0722][i], 0);
    const contrast = [];
    for (const node of document.querySelectorAll("h1, .subtitle, .item strong, .item .meta, .reading, summary, input, label")) {
        if (!visible(node)) continue;
        const style = getComputedStyle(node);
        const foreground = rgb(style.color)?.slice(0, 3);
        let background = [255, 255, 255], ancestor = node;
        const layers = [];
        while (ancestor) { layers.unshift(rgb(getComputedStyle(ancestor).backgroundColor)); ancestor = ancestor.parentElement; }
        for (const layer of layers.filter(Boolean)) background = background.map((value, i) => layer[i] * (layer[3] ?? 1) + value * (1 - (layer[3] ?? 1)));
        for (const color of [foreground, ...(node.tagName === "INPUT" ? [rgb(getComputedStyle(node, "::placeholder").color)?.slice(0, 3)] : [])]) {
            if (!color) continue;
            const a = luminance(color), b = luminance(background), ratio = (Math.max(a, b) + .05) / (Math.min(a, b) + .05);
            if (ratio < 4.5) contrast.push({ id: node.id, tag: node.tagName, ratio });
        }
    }
    return { section: section.id, colors, contrast, overflow: document.documentElement.scrollWidth > innerWidth,
        visibleAssistanceActions: [...section.querySelectorAll("[data-request],[data-review]")].filter(visible).length,
        detailButtons: [...section.querySelectorAll(".detail-pane button")].filter(visible).map(node => node.textContent),
        assistanceCount: section.querySelectorAll(".assistance").length,
        assistanceOpen: !!section.querySelector(".assistance")?.open,
        visiblePrimary: [...section.querySelectorAll(".detail-pane .primary")].filter(visible).length,
        eyebrowCount: document.querySelectorAll(".eyebrow").length, frames: document.querySelectorAll("iframe").length };
}

test("quiet defaults: neutral palette and all assistance stays collapsed without removing controls", async () => {
    const css = await readFile(new URL("./ui.css", import.meta.url), "utf8");
    assert.doesNotMatch(css, /b11f4b|fd8ea1|177,\s*31,\s*75|253,\s*142,\s*161|gradient|--cp-(?:success|warning|shadow|overlay|sheen)/i);
    const accentUses = css.split("\n").filter(line => /var\(--cp-accent/.test(line));
    assert.ok(accentUses.every(line => /--cp-(link|focus)|:focus-visible/.test(line)), "Functional accent is only for links and focus");
    for (const file of ["app.js", "memory-app.js", "task-app.js"]) {
        const source = await readFile(new URL(file, import.meta.url), "utf8");
        assert.match(source, /ui\.disclosure\("Assistance"/);
        assert.doesNotMatch(source, /primary-actions|Other ways to help|Other review choices|Other review requests/);
    }
});

test("quiet workspace batched native-browser visual and interaction rehearsal", { skip }, async t => {
    const engine = await browser(t);
    const output = process.env.MARGO_QUIET_SCREENSHOTS;
    if (output) await mkdir(output, { recursive: true });
    const findings = [], checks = [];
    for (const width of [360, 1280]) for (const theme of ["light", "dark"]) {
        const panel = await fixture(t), page = await engine.page(width, theme);
        try {
            await page.goto(panel.url);
            await page.wait("!!document.querySelector('#work-items > button')");
            await page.wait("document.querySelector('h1').textContent === 'Rowan Workspace'");
            const origin = new URL(panel.url).origin;
            assert.equal(await page.evaluate("location.origin"), origin);
            for (const [section, list] of [["work", "items"], ["memory", "results"], ["tasks", "runs"]]) {
                if (section !== "work") await page.click(`#tab-${section}`);
                await page.wait(`!!document.querySelector('#${section}-${list} > button')`);
                assert.equal(await page.evaluate(`Array.from(document.querySelectorAll('#section-${section} [data-request], #section-${section} [data-review]')).filter(node => node.checkVisibility({ checkVisibilityCSS: true })).length`), 0);
                if (output) await page.screenshot(join(output, `${section}-${width}-${theme}-list.png`));
                await page.click(`#${section}-${list} > button`);
                await page.wait(`!!document.querySelector('#${section}-detail h2')`);
                const result = await page.evaluate(`(${inspectView})()`);
                checks.push({ width, theme, ...result });
                if (result.colors.length || result.contrast.length || result.overflow || result.visibleAssistanceActions
                    || result.visiblePrimary || result.eyebrowCount || result.frames || result.assistanceOpen
                    || result.assistanceCount !== 1 || result.detailButtons.length > (width === 360 ? 2 : 1)) {
                    findings.push({ width, theme, ...result });
                }
                if (output) await page.screenshot(join(output, `${section}-${width}-${theme}-detail.png`));
                await page.click(`#${section}-detail .assistance > summary`);
                const expected = { work: 3, memory: 4, tasks: 6 }[section];
                assert.equal(await page.evaluate(`(${inspectView})().visibleAssistanceActions`), expected);
                if (section === "work") {
                    await page.click('#work-detail [data-request="recommend"]');
                    await page.wait("document.querySelector('#work-request-status').textContent.includes('Accepted')");
                    assert.equal(panel.data.input.expected_revision, 1);
                    assert.equal(panel.data.input.expected_hash, panel.data.action.action_hash);
                } else if (section === "memory") {
                    await page.click('#memory-detail [data-review="correct"]');
                    await page.wait("document.querySelector('#memory-notice').textContent.includes('requested')");
                } else {
                    await page.click('#tasks-detail [data-review="pause"]');
                    await page.wait("document.querySelector('#tasks-notice').textContent.includes('Foreground review')");
                }
                await page.click(`#${section}-detail .assistance > summary`);
            }
            assert.equal(panel.data.sent.length, 3);
            assert.ok(panel.data.sent.every(message => /NOT approval/i.test(message.prompt)));
            assert.equal(panel.data.action.approvals.length, 0);
            assert.equal(panel.data.run.status, "running");
            await page.click("#tab-work");
            await page.wait("document.querySelector('#work-workspace').getAttribute('aria-busy') === 'false'");
            await page.evaluate("Array.from(document.querySelectorAll('#work-detail summary')).find(node => node.textContent === 'Edit local proposal payload').focus()");
            await page.key("Enter");
            await page.fill("#work-field-body", "A manual edit kept across workspace sections.");
            const draft = await page.evaluate("document.querySelector('#work-payload').value");
            await page.click("#tab-memory"); await page.click("#tab-tasks"); await page.click("#tab-work");
            assert.equal(await page.evaluate("document.querySelector('#work-payload').value"), draft);
            assert.equal(await page.evaluate("document.activeElement.id"), "work-field-body");
            await page.click("#work-save");
            await page.wait("document.querySelector('#work-notice').textContent.includes('Saved a new local revision')");
            assert.equal(panel.data.action.revision, 2);
            assert.equal(panel.data.action.payload.importance, "normal");
            assert.equal(panel.data.writes, 1);
            await page.click("#tab-memory");
            panel.data.conflict = true;
            await page.click("#memory-detail .assistance > summary");
            await page.click('#memory-detail [data-review="correct"]');
            await page.wait("document.querySelector('#memory-notice').textContent.includes('Reload the selected memory')");
            assert.equal(await page.evaluate("document.querySelector('#memory-detail [data-review=correct]').disabled"), true);
            await page.click("#tab-work");
            panel.data.offline = true;
            await page.click("#work-refresh");
            await page.wait("document.querySelector('#work-notice').textContent.includes('offline')");
            assert.equal(await page.evaluate("document.querySelector('#work-save').disabled"), true);
            await page.evaluate("document.documentElement.setAttribute('data-color-mode','dark'); document.documentElement.style.setProperty('--background-color-default','#181818'); document.documentElement.style.setProperty('--text-color-default','#eeeeee'); document.documentElement.style.setProperty('--text-color-muted','#bbbbbb')");
            await page.wait("document.documentElement.getAttribute('data-theme') === 'dark'");
            assert.equal(await page.evaluate("getComputedStyle(document.body).backgroundColor"), "rgb(24, 24, 24)");
            assert.equal(page.documents.length, 1);
            assert.deepEqual(page.errors, []);
        } finally { await page.close(); }
    }
    if (output) await writeFile(join(output, "checks.json"), JSON.stringify({ checks, findings }, null, 2));
    assert.deepEqual(findings, [], "One batched inspection: neutral default hierarchy, contrast and overflow");
});

test("Config and Automations real save flows preserve drafts, CAS and quiet section navigation", {
    skip: skip || (!process.env.MARGO_CANVAS_TEST_PARENT && "Set a private fixture parent for real authoring browser checks."),
}, async t => {
    const root = await mkdtemp(join(await realpath(process.env.MARGO_CANVAS_TEST_PARENT), "authoring-browser-"));
    t.after(() => rm(root, { recursive: true, force: true, maxRetries: 3, retryDelay: 100 }));
    const scripts = join(root, "install", "skills", "chief-of-staff", "scripts"), privateDir = join(root, "private"), workRoot = join(root, "Daily Work");
    await cp(resolve(dirname(fileURLToPath(import.meta.url)), "../../../skills/chief-of-staff/scripts"), scripts, { recursive: true });
    await writeFile(join(root, "install", ".margo-install"), "mode=copy\n", { mode: 0o600 });
    await mkdir(privateDir, { mode: 0o700 }); await mkdir(join(privateDir, "state"), { mode: 0o700 }); await mkdir(workRoot, { mode: 0o700 });
    const config = join(privateDir, "config.json");
    await writeFile(config, JSON.stringify({ account: "browser-fixture" }), { mode: 0o600 });
    const env = Object.fromEntries(Object.entries(process.env).filter(([key]) => !key.startsWith("MARGO_") && key !== "COPILOT_HOME"));
    const execute = (command, args, options) => executeWithInput(command, args, { ...options, env, cwd: workRoot });
    await execute(process.platform === "win32" ? "python" : "python3", ["-B", join(scripts, "margo_store.py"), "locations-bind",
        "--config-path", config, "--state-dir", join(privateDir, "state"), "--account", "browser-fixture", "--expected-revision", "missing"], { encoding: "utf8" });
    const backend = createBackend({ resolveScript: async () => join(scripts, "work_state.py"), execute });
    const automationBackend = createAutomationBackend({ resolveScript: async () => join(scripts, "work_state.py"), execute });
    const panel = await startServer({ backend, automationBackend }); t.after(() => panel.close());
    const engine = await browser(t);
    for (const width of [360, 1280]) for (const theme of ["light", "dark"]) {
        const page = await engine.page(width, theme);
        try {
            const url = new URL(panel.url); url.pathname = "/config";
            await page.goto(url.href);
            await page.wait("!!document.querySelector('#config-name') && !document.querySelector('#config-name').disabled");
            const name = `Rowan ${width} ${theme}`;
            await page.fill("#config-name", name);
            if (await page.evaluate("document.querySelector('#config-clear').checked")) await page.click("#config-clear");
            await page.fill("#config-root", workRoot);
            await page.click("#tab-automations");
            await page.click("#tab-config");
            assert.equal(await page.evaluate("document.querySelector('#config-name').value"), name);
            await page.click("#config-save");
            await page.wait("document.querySelector('#config-notice').textContent.includes('Settings saved')");
            assert.equal(await page.evaluate("document.querySelector('h1').textContent"), name + " Workspace");
            assert.ok((await page.evaluate("document.querySelector('#profile-settings').textContent")).includes(name));
            if (process.env.MARGO_QUIET_SCREENSHOTS) await page.screenshot(join(process.env.MARGO_QUIET_SCREENSHOTS, `config-${width}-${theme}.png`));
            const saved = await backend.run("profile");
            await page.fill("#config-name", "Draft name");
            await backend.run("profile-save", { expected_revision: saved.revision, assistant_name: "External change", work_root: workRoot });
            await page.click("#config-reload");
            await page.wait("!document.querySelector('#config-conflict').hidden");
            assert.equal(await page.evaluate("document.querySelector('#config-name').value"), "Draft name");
            assert.equal(await page.evaluate("document.querySelector('#config-save').disabled"), true);
            await page.click("#config-cancel");
            assert.equal(await page.evaluate("document.querySelector('#config-name').value"), "External change");
            await page.fill("#config-name", "<script>");
            assert.equal(await page.evaluate("document.querySelector('#config-save').disabled"), true);
            await page.click("#config-cancel");
            await page.click("#tab-automations");
            await page.click("#automations-refresh");
            await page.wait("document.querySelector('#automations-master').textContent.includes('unbound') && !document.querySelector('#automations-new').disabled");
            await page.click("#automations-new");
            await page.fill("#automations-id", `synthetic-${width}-${theme}`);
            await page.fill("#automations-title", "Synthetic browser definition");
            await page.fill("#automations-cron", "30 8 * * 1-5");
            await page.fill("#automations-timezone", "Etc/UTC");
            for (let index = 0; index < 9; index++) {
                await page.evaluate(`(() => { const field = document.querySelector('#automations-content-${index}'); const details = field.closest('details'); if (details) details.open = true; })()`);
                await page.fill(`#automations-content-${index}`, `Synthetic content section ${index}.`);
            }
            await page.click("#tab-config"); await page.click("#tab-automations");
            assert.equal(await page.evaluate("document.querySelector('#automations-title').value"), "Synthetic browser definition");
            await page.click('#automations-detail [data-write="preview"]');
            await page.wait("!!document.querySelector('#automations-detail [data-write=commit]')");
            await page.fill("#automations-title", "Reviewed synthetic browser definition");
            assert.equal(await page.evaluate("!!document.querySelector('#automations-detail [data-write=commit]')"), false);
            await page.click('#automations-detail [data-write="preview"]');
            await page.wait("!!document.querySelector('#automations-detail [data-write=commit]')");
            await page.click('#automations-detail [data-write="commit"]');
            await page.wait("document.querySelector('#automations-notice').textContent.includes('Definition saved')");
            assert.equal(await page.evaluate("document.querySelector('#automations-detail [data-write=toggle]').disabled"), true);
            assert.equal((await automationBackend.run("list")).native_controller.workflow_id, null);
            assert.equal(await page.evaluate("document.documentElement.scrollWidth <= innerWidth"), true);
            const appearance = await page.evaluate(`(${inspectView})()`);
            assert.deepEqual(appearance.colors, []);
            assert.deepEqual(appearance.contrast, []);
            if (process.env.MARGO_QUIET_SCREENSHOTS) await page.screenshot(join(process.env.MARGO_QUIET_SCREENSHOTS, `automations-${width}-${theme}.png`));
            await page.fill("#automations-title", "Unsaved definition");
            const profile = await backend.run("profile");
            await backend.run("profile-save", { expected_revision: profile.revision, assistant_name: "Updated elsewhere", work_root: workRoot });
            await page.click("#automations-refresh");
            await page.wait("document.querySelector('#automations-notice').textContent.includes('draft is preserved')");
            assert.equal(await page.evaluate("document.querySelector('#automations-title').value"), "Unsaved definition");
            assert.equal(await page.evaluate("document.querySelector('#automations-detail [data-write=preview]').disabled"), true);
            await page.evaluate("document.querySelector('#tab-automations').focus()");
            await page.key("ArrowRight"); await page.key("Enter");
            await page.wait("!document.querySelector('#section-config').hidden");
            assert.equal(page.documents.length, 1);
            assert.deepEqual(page.errors, []);
        } finally { await page.close(); }
    }
});

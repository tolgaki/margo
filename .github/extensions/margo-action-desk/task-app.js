globalThis.MargoSections.tasks = (section, ui) => {
    "use strict";
    const token = new URLSearchParams(location.hash.slice(1)).get("token");
    const $ = ui.get;
    const METRICS = ["tool_calls", "pages", "items", "model_calls", "output_chars"];
    const REVIEW_INTENTS = ["pause", "cancel", "resume", "replan", "recover", "reconcile"];
    const views = ["All", "Ready", "Running", "Waiting approval", "Blocked", "Attention", "Paused", "Cancelled", "Completed"];
    let runs = [];
    let nextCursor = null;
    let selected = null;
    let selectedStale = false;
    let currentAccount = null;
    let busy = false;
    let view = "All";

    // Stored text is never HTML. Every value below renders through textContent/pretty JSON.
    function node(tag, text, cls) {
        const result = document.createElement(tag);
        if (text !== undefined) result.textContent = String(text ?? "");
        if (cls) result.className = cls;
        return result;
    }
    const pretty = (value) => value === undefined || value === null ? "Not recorded" : JSON.stringify(value, null, 2);
    function button(text, handler) {
        const result = node("button", text);
        result.type = "button";
        result.addEventListener("click", handler);
        return result;
    }
    function notice(message, error = false) {
        $("notice").textContent = message;
        $("notice").className = error ? "notice error" : "notice";
        $("notice").setAttribute("data-busy", String(busy));
    }
    function controls() {
        $("workspace").setAttribute("aria-busy", String(busy));
        for (const control of section.querySelectorAll("button,input,select")) {
            control.disabled = busy || (control.hasAttribute("data-review") && selectedStale);
        }
    }
    async function work(message, action, review = false) {
        if (busy) return;
        busy = true;
        controls();
        notice(message);
        try {
            await action();
        } catch (error) {
            if (error.code === "conflict" || review || !error.code || /unavailable|not_initialized|timeout/.test(error.code)) selectedStale = true;
            const hint = error.code === "conflict" ? " Reload the selected run before requesting another review."
                : error.code === "not_initialized" ? " This panel never creates task state; start or resume a task in the foreground conversation, then refresh."
                : error.code === "setup_needed" ? " Configure the account/CLI in the foreground, then refresh."
                : review ? " Check the conversation before another review request; delivery may be unknown. No approval was granted."
                : " Previously loaded tasks are retained when available. Retry the read; no task was changed.";
            notice((error.name === "TimeoutError" || error.name === "AbortError"
                ? "The request timed out; its delivery is unknown. Check the foreground conversation before retrying."
                : error.message) + hint, true);
            if (!runs.length) $("runs").replaceChildren(ui.empty("Task state is unavailable",
                "Review the setup or read error above, then refresh tasks. Opening this view has not initialized a journal."));
        } finally {
            busy = false;
            controls();
            $("notice").setAttribute("data-busy", "false");
        }
    }
    async function api(operation, input = {}) {
        const response = await fetch("/api/task/" + operation, {
            method: "POST",
            headers: { Authorization: "Bearer " + (token || ""), "Content-Type": "application/json" },
            body: JSON.stringify(input), signal: AbortSignal.timeout(20_000),
            cache: "no-store", credentials: "omit",
        });
        let result;
        try { result = await response.json(); }
        catch { throw new Error("Task journal returned an unreadable response. Check the CLI in the conversation."); }
        if (!response.ok || result.error) {
            const error = new Error(result.error?.message || "Task journal request failed.");
            error.code = result.error?.code;
            throw error;
        }
        return result;
    }
    function account(value) {
        ui.observeAccount(value);
        if (typeof value !== "string" || !value) return;
        if (currentAccount && currentAccount !== value) {
            runs = [];
            nextCursor = null;
            selected = null;
            currentAccount = value;
            $("runs").replaceChildren();
            $("detail").replaceChildren();
            $("count").textContent = "";
            $("health-summary").textContent = "Account changed. Health must be refreshed.";
            $("health-detail").replaceChildren();
            $("account").textContent = `Current account: ${value}`;
            throw new Error("The configured account changed. Refresh health and browse again; previous runs were cleared.");
        }
        currentAccount = value;
        $("account").textContent = `Current account: ${value}`;
    }
    function details(title, value) {
        const result = node("details");
        result.append(node("summary", title), node("pre", pretty(value)));
        return result;
    }
    function table(title, headings, entries) {
        const wrapper = node("div", undefined, "table-scroll");
        const result = node("table");
        result.append(node("caption", title));
        const head = node("thead");
        const header = node("tr");
        for (const heading of headings) {
            const cell = node("th", heading);
            cell.setAttribute("scope", "col");
            header.append(cell);
        }
        head.append(header);
        const body = node("tbody");
        for (const entry of entries) {
            const row = node("tr");
            for (const value of entry) {
                const cell = node("td");
                if (value && typeof value === "object" && value.nodeType) cell.append(value);
                else cell.textContent = String(value ?? "Not recorded");
                row.append(cell);
            }
            body.append(row);
        }
        result.append(head, body);
        wrapper.append(result);
        if (!entries.length) wrapper.append(node("p", "No records returned."));
        return wrapper;
    }
    async function refreshHealth() {
        $("health-summary").textContent = "Task journal health: checking...";
        $("health-detail").replaceChildren();
        try {
            const result = await api("health");
            account(result.account);
            $("health-summary").textContent = `Journal status: ${result.status}. Expired claims: ${result.expired_claims}. Unreconciled attempts: ${result.unreconciled_attempts}. ${result.note || ""}`;
            if (result.action) $("health-summary").textContent += ` Suggested: ${result.action}`;
            $("health-detail").append(details("Runs by state and complete health record", result));
            return true;
        } catch (error) {
            $("health-summary").textContent = `Task journal health unavailable: ${error.message}`;
            return false;
        }
    }
    function lane(run) {
        if (["partial", "failed", "outcome_unknown", "interrupted"].includes(run.status)
            || (run.unresolved_effects || []).length || (run.expired_claims || []).length) return "Attention";
        if (run.status === "cancelled") return "Cancelled";
        if (run.status === "paused") return "Paused";
        if (run.status === "succeeded") return "Completed";
        if (run.status === "waiting_approval") return "Waiting approval";
        if (run.status === "blocked") return "Blocked";
        if (run.status === "running") return "Running";
        if (["ready", "planned"].includes(run.status)) return "Ready";
        return "Other";
    }
    function visible(run) {
        const query = $("task-query").value.trim().toLowerCase();
        if (query && ![run.goal, run.routine, run.status].some(value => String(value || "").toLowerCase().includes(query))) return false;
        if (view === "Cancelled") return run.status === "cancelled";
        if (view === "Paused") return run.status === "paused";
        return view === "All" || lane(run) === view;
    }
    function renderList(focusView = false) {
        $("views").replaceChildren();
        let currentTab;
        for (const name of views) {
            const tab = button(name, () => {
                view = name;
                renderList(true);
                notice(`${name} view; filters apply locally to the loaded runs.`);
            });
            tab.setAttribute("aria-pressed", String(view === name));
            $("views").append(tab);
            if (view === name) currentTab = tab;
        }
        $("runs").replaceChildren();
        const filtered = runs.filter(visible);
        $("count").textContent = `${filtered.length} of ${runs.length} loaded run(s)${nextCursor ? " · more available" : ""}`;
        for (const run of filtered) {
            const item = button("", () => select(run.id));
            item.className = "item";
            item.setAttribute("aria-current", String(selected?.id === run.id));
            const attention = (run.unresolved_effects || []).length || (run.expired_claims || []).length;
            item.append(node("strong", run.goal || run.id),
                node("span", `${ui.label(run.routine)} · ${lane(run)}${attention ? " · needs attention" : ""}`, "meta"));
            const count = run.step_counts || {};
            const total = Object.values(count).reduce((sum, value) => sum + value, 0);
            if (total) item.append(node("span", `${count.succeeded || 0} of ${total} steps complete`, "meta"));
            $("runs").append(item);
        }
        if (!filtered.length) {
            $("runs").append(ui.empty(runs.length ? "No matching tasks" : "No tracked tasks yet",
                runs.length ? "Try another view or search. All records stay in the task journal."
                    : "No task runs were returned for this account. Refresh tasks to read the journal again."));
        }
        $("more").hidden = !nextCursor;
        if (focusView && currentTab) ui.focus(currentTab);
    }
    function readinessNote(run) {
        const active = run.steps.filter(step => step.in_current_plan && step.state === "running");
        if (run.status === "running" && active.length) return `Working on: ${active.map(step => stepTitle(run, step.key)).join(", ")}. The result has not been recorded yet.`;
        if (run.unresolved_effects?.length) return "Check the existing effect before any next step. Do not repeat it while the outcome is unknown.";
        if (run.status === "paused") return "Future steps are paused. Review the current plan before resuming.";
        if (run.status === "succeeded") return "The planned steps are complete. Delivery and obligation closure remain separate.";
        if (run.ready_steps.length) return `Next: ${run.ready_steps.map(key => stepTitle(run, key)).join(", ")}.`;
        return "No step is currently ready to claim. Readiness reflects tracked state only, not a live provider check.";
    }
    function stepTitle(run, key) {
        return run.steps.find(step => step.key === key)?.definition?.title
            || run.plan.steps.find(step => step.key === key)?.title || ui.label(key);
    }
    function reviewActions(run) {
        const actions = node("div", undefined, "actions");
        for (const intent of REVIEW_INTENTS) {
            const request = button(`Request ${intent}`, () => work(`Requesting foreground ${intent} review, not approval...`, async () => {
                if (selectedStale) throw new Error("Reload this run first.");
                const result = await api("review", { id: run.id, revision: run.revision, plan_hash: run.plan_hash, intent });
                notice(result.message);
            }, true));
            request.setAttribute("data-review", intent);
            actions.append(request);
        }
        const assistance = ui.disclosure("Assistance", node("p", "Request a conversation about this exact run. Never approval or the operation itself.", "meta"), actions);
        assistance.className = "assistance";
        return assistance;
    }
    function renderDetail() {
        const run = selected;
        const root = node("article");
        const title = node("h2", run.plan.goal || run.id);
        title.tabIndex = -1;
        const toolbar = node("div", undefined, "detail-toolbar");
        toolbar.append(ui.backButton(), button("Reload this run", () => select(run.id)));
        root.append(toolbar, node("p", ui.label(run.status), "badge status-badge"), title);
        root.append(node("div", `${ui.label(run.plan.routine)} · ${ui.label(run.plan.mode)} · revision ${run.revision}`, "meta"));
        if (run.status === "cancelled" && (run.unresolved_effects || []).length) {
            root.append(node("p", `Cancelled with unresolved effect(s) on step(s): ${run.unresolved_effects.join(", ")}. `
                + "Cancellation stopped future steps; it did not undo, unsend or erase any effect already in flight. "
                + "Reconcile these from real evidence in the foreground conversation before treating this run as fully resolved.", "warn"));
        } else if ((run.unresolved_effects || []).length) {
            root.append(node("p", `Unresolved / unknown effect(s) on step(s): ${run.unresolved_effects.join(", ")}. `
                + "These outcomes are unknown, not failed and not complete.", "warn"));
        }
        if ((run.expired_claims || []).length) {
            root.append(node("p", `Expired claim(s) on step(s): ${run.expired_claims.join(", ")}. `
                + "Their claimed lease passed without a recorded result. Recover in the foreground conversation before retrying.", "warn"));
        }
        if ((run.blocked_reasons || []).length) root.append(node("p", `Run is blocked: ${run.blocked_reasons.join("; ")}`));
        root.append(node("p", readinessNote(run)));
        root.append(reviewActions(run));
        const steps = run.steps.filter(step => step.in_current_plan);
        const done = steps.filter(step => step.state === "succeeded").length;
        const progress = node("progress");
        progress.max = Math.max(1, steps.length);
        progress.value = done;
        progress.setAttribute("aria-label", `${done} of ${steps.length} steps complete; not delivery approval`);
        root.append(node("h3", "The work, step by step"), progress,
            node("p", `${done} of ${steps.length} steps complete. Completed attempts do not automatically close an obligation.`, "meta"));
        const timeline = node("ol", undefined, "step-list");
        for (const step of steps) {
            const entry = node("li");
            const card = node("div", undefined, "step-card");
            const heading = node("div", undefined, "row");
            heading.append(node("h4", stepTitle(run, step.key)), node("span", ui.label(step.state), "badge"));
            card.append(heading);
            if (step.result?.summary) card.append(node("p", step.result.summary));
            if (step.blocked_reasons?.length) card.append(node("p", step.blocked_reasons.join("; "), "warn"));
            if (step.source_evidence_changed) card.append(node("p", "Supporting evidence changed. Revalidate before continuing.", "warn"));
            if (step.lease_expires) card.append(node("p", `Claim expires ${new Date(step.lease_expires).toLocaleString()}`, "meta"));
            card.append(ui.disclosure("Step detail & result", ui.facts({ key: step.key, kind: step.definition.kind, attempts: step.attempts,
                next_retry: step.next_retry_at, result: step.result, action_state: step.action_state, owned_action_state: step.owned_action_state })));
            entry.append(card);
            timeline.append(entry);
        }
        root.append(timeline);
        const budget = ui.disclosure("Budget & limits — tracked work only");
        root.append(budget);
        {
        const root = budget;
        root.append(node("p", run.limits_enforcement));
        root.append(table("Usage vs tracked remaining", ["Metric", "Charged / reserved", "Remaining", "Tracked limit"], METRICS.map((metric) => [
            ui.label(metric), run.usage[metric], run.remaining[metric], run.plan.limits[metric],
        ])));
        if (METRICS.some((metric) => run.usage[metric] > run.plan.limits[metric])) {
            root.append(node("p", "Reported usage exceeded the displayed plan limit. Remaining zero does not mean the limit was increased.", "warn"));
        }
        root.append(node("p", `Token usage: ${run.token_usage === null ? "not measured within this tracked path" : pretty(run.token_usage)}. `
            + `Model cost: ${run.model_cost === null ? "not measured within this tracked path" : pretty(run.model_cost)}. `
            + `Approval granted: ${run.approval_granted} — this panel never grants or holds approval.`));
        }
        const technical = ui.disclosure("Plan, identity & all step records");
        root.append(technical);
        {
        const root = technical;
        root.append(ui.facts({ id: run.id, revision: run.revision, plan_hash: run.plan_hash, account: run.account }));
        root.append(details("Exact plan (raw, read-only)", run.plan));
        root.append(table("Steps in the current and prior plan", ["Key", "State", "Kind", "Attempts", "Next retry", "Blocked reasons", "Notes"],
            run.steps.map((step) => [
                step.key, step.state, step.definition?.kind || "unknown", step.attempts, step.next_retry_at || "None recorded",
                (step.blocked_reasons || []).join("; ") || "None recorded",
                [
                    step.in_current_plan ? null : "not in current plan",
                    step.lease_expires ? `lease expires ${step.lease_expires}` : null,
                    (run.expired_claims || []).includes(step.key) ? "EXPIRED CLAIM" : null,
                    step.action_state ? `linked action state: ${step.action_state}` : null,
                    step.owned_action_state ? `this run's own execution: ${step.owned_action_state}` : null,
                    step.source_evidence_changed ? "source evidence changed" : null,
                ].filter(Boolean).join("; ") || "None recorded",
            ])));
        root.append(details("Complete current run (raw, read-only)", run));
        }
        const history = ui.disclosure("Activity history");
        root.append(history);
        {
        const root = history;
        const historyBox = node("div");
        root.append(button("Load history (most recent 20 events)", () => work("Reading task history...", async () => {
            const result = await api("history", { id: run.id, limit: 20 });
            historyBox.replaceChildren(table("Recorded events (newest first; not a full archive)",
                ["Revision", "Event", "Recorded at", "Data"], result.events.map((event) => [
                    event.revision, ui.label(event.event), event.created_at, details("Event data (untrusted, read-only)", event.data),
                ])));
            if (!result.events.length) historyBox.append(node("p", "No history events recorded yet."));
        })), historyBox);
        }
        root.append(node("p", "Review requests do not execute anything. Resuming or recovering still requires fresh checks and separate action approval. Cancellation never undoes an in-flight effect.", "meta"));
        $("detail").replaceChildren(root);
        ui.focus(title);
    }
    function select(id) {
        ui.openDetail();
        return work("Reading this task run's current state...", async () => {
            const result = await api("show", { id });
            account(result.account);
            selected = result;
            selectedStale = false;
            renderDetail();
            renderList();
            notice("Current tracked state loaded. Buttons below request foreground review; none of them execute anything.");
        });
    }
    async function loadFirstPage() {
        const result = await api("list", { limit: 20 });
        account(result.account);
        runs = result.runs;
        nextCursor = result.next_cursor;
        renderList();
        notice(runs.length ? `${runs.length} task run(s) loaded.${nextCursor ? " More are available." : ""}`
            : "No task runs yet for this account. This panel does not create sample runs.");
        if (selected) {
            const current = runs.find(run => run.id === selected.id);
            if (!current || current.revision !== selected.revision || current.plan_hash !== selected.plan_hash
                || current.status !== selected.status || current.updated_at !== selected.updated_at) {
                selectedStale = true;
                notice("The selected run changed or is outside this page. Reload it before requesting review; the displayed detail was preserved.", true);
            }
        }
    }
    async function loadNextPage() {
        if (!nextCursor) return;
        const result = await api("list", { limit: 20, after: nextCursor });
        account(result.account);
        const seen = new Set(runs.map((run) => run.id));
        runs = [...runs, ...result.runs.filter((run) => !seen.has(run.id))];
        nextCursor = result.next_cursor;
        renderList();
        notice(`${runs.length} task run(s) loaded so far.${nextCursor ? " More are available." : ""}`);
    }
    $("health").addEventListener("click", () => work("Checking current account and task journal health...", async () => {
        const complete = await refreshHealth();
        notice(complete ? "Account and journal health refreshed. All controls here are read-only checks." : "Health check failed. Unknown status is shown above; retry.", !complete);
    }));
    $("more").addEventListener("click", () => work("Reading the next page of task runs...", loadNextPage));
    $("refresh-runs").addEventListener("click", () => work("Refreshing tracked tasks...", loadFirstPage));
    $("task-query").addEventListener("input", () => renderList());
    if (!token) {
        notice("This panel has no access token. Reopen Task Progress from the app.", true);
    } else {
        work("Loading task journal health and runs...", async () => {
            const complete = await refreshHealth();
            await loadFirstPage();
            if (!complete) notice($("notice").textContent + " Journal health checks failed; see unknown status above and retry.", true);
        });
    }
    return {};
};

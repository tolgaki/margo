(() => {
    "use strict";
    const token = new URLSearchParams(location.hash.slice(1)).get("token");
    const $ = id => document.getElementById(id);
    let rows = [];
    let conflictedIds = new Set();
    let selected = null;
    let selectedStale = false;
    let currentAccount = null;
    let busy = false;
    let view = "All";
    let resultLabel = "Stored memories";
    const views = ["All", "Facts", "People", "Projects", "Lessons", "Conflicts", "History"];
    const intents = {
        correct: "Request correction review", "do-not-use": "Request do-not-use review",
        supersede: "Request supersession review", forget: "Request forgetting review",
        export: "Request sanitized lesson export review",
    };
    function node(tag, text, cls) {
        const result = document.createElement(tag);
        if (text !== undefined) result.textContent = String(text ?? "");
        if (cls) result.className = cls;
        return result;
    }
    const pretty = value => value === undefined || value === null ? "Not recorded" : JSON.stringify(value, null, 2);
    const statusLabel = value => value === "suppressed" ? "suppressed (do-not-use, not forgotten)" : value;
    function button(text, handler) {
        const result = node("button", text);
        result.type = "button";
        result.addEventListener("click", handler);
        return result;
    }
    function notice(message, error = false) {
        $("notice").textContent = message;
        $("notice").className = error ? "error" : "";
    }
    function controls() {
        $("workspace").setAttribute("aria-busy", String(busy));
        for (const control of document.querySelectorAll("button,input,select")) {
            control.disabled = busy || (control.hasAttribute("data-review") && selectedStale);
        }
    }
    async function work(message, action) {
        if (busy) return;
        busy = true;
        controls();
        notice(message);
        try { await action(); }
        catch (error) {
            if (error.code === "conflict") selectedStale = true;
            const hint = error.code === "conflict" ? " Reload the selected memory before requesting another review."
                : " Retry using the same control. Nothing was approved or changed.";
            notice((error.name === "TimeoutError" || error.name === "AbortError"
                ? "The request timed out; its delivery is unknown. Check the foreground conversation before retrying a review."
                : error.message) + hint, true);
        } finally {
            busy = false;
            controls();
        }
    }
    async function api(operation, input = {}) {
        const response = await fetch("/api/memory/" + operation, {
            method: "POST",
            headers: { Authorization: `Bearer ${token || ""}`, "Content-Type": "application/json" },
            body: JSON.stringify(input), signal: AbortSignal.timeout(65_000),
            cache: "no-store", credentials: "omit",
        });
        let result;
        try { result = await response.json(); }
        catch { throw new Error("Memory returned an unreadable response. Check the CLI in the conversation."); }
        if (!response.ok || result.error) {
            const error = new Error(result.error?.message || "Memory request failed.");
            error.code = result.error?.code;
            throw error;
        }
        return result;
    }
    function account(value) {
        if (typeof value !== "string" || !value) return;
        if (currentAccount && currentAccount !== value) {
            rows = [];
            conflictedIds = new Set();
            selected = null;
            currentAccount = value;
            $("results").replaceChildren();
            $("detail").replaceChildren();
            $("count").textContent = "";
            $("policy-summary").textContent = "Account changed. Capture and retention must be refreshed.";
            $("runtime-summary").textContent = "Local model: not yet checked for this account";
            $("health-detail").replaceChildren();
            $("account").textContent = `Current account: ${value}`;
            throw new Error("The configured account changed. Refresh account/policy and browse again; previous records were cleared.");
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
    async function refreshContext() {
        $("policy-summary").textContent = "Capture and retention: checking...";
        $("runtime-summary").textContent = "Local model: checking...";
        $("health-detail").replaceChildren();
        const [health, policy] = await Promise.allSettled([api("status"), api("policy")]);
        for (const result of [health, policy]) {
            if (result.status === "fulfilled") account(result.value.account);
        }
        if (health.status === "fulfilled") {
            const value = health.value;
            $("runtime-summary").textContent = `Local embedding runtime: ${value.embedding_runtime?.status || "unknown"}. Meaning search never silently switches to keywords.`;
            if (Array.isArray(value.memory?.recovery_actions)) {
                $("runtime-summary").textContent += " " + value.memory.recovery_actions.join(" ");
            }
            $("health-detail").append(details("Index and memory health", value));
        } else {
            $("runtime-summary").textContent = `Health unavailable: ${health.reason.message}`;
        }
        if (policy.status === "fulfilled") {
            const value = policy.value;
            const data = value.data;
            const retention = Object.entries(data.retention_days || {}).map(([kind, days]) => `${kind}: ${days} days`).join("; ");
            $("policy-summary").textContent = `Policy ${value.configured ? "configured" : "not configured"} · revision ${value.revision}. Capture: ${data.capture?.enabled === true ? "enabled in reviewed scope" : data.capture?.enabled === false ? "off" : "unknown"}. Retention: ${retention || "no type-based memory expiry configured"}. Usage recording: ${data.usage_enabled === true ? "on" : data.usage_enabled === false ? "off" : "unknown"}.`;
            $("health-detail").append(details("Exact capture scope, retention and review policy", value));
        } else {
            $("policy-summary").textContent = `Capture and retention unknown: ${policy.reason.message}`;
        }
        return [health, policy].every(result => result.status === "fulfilled");
    }
    function visible(memory) {
        if ($("domain").value && memory.domain !== $("domain").value) return false;
        if ($("status").value && memory.status !== $("status").value) return false;
        if ($("kind").value && memory.kind !== $("kind").value) return false;
        if (view === "Facts") return ["profile", "preference", "decision"].includes(memory.kind);
        if (view === "People") return memory.kind === "person";
        if (view === "Projects") return memory.kind === "project";
        if (view === "Lessons") return memory.kind === "lesson";
        if (view === "Conflicts") return memory.status === "disputed" || conflictedIds.has(memory.id);
        return true;
    }
    function renderList(focusView = false) {
        $("views").replaceChildren();
        let currentTab;
        for (const name of views) {
            const tab = button(name, () => {
                view = name;
                $("kind").value = "";
                $("status").value = "";
                renderList(true);
                notice(name === "History" ? "Select a memory for up to 30 recorded revisions, including retired states." : `${name} view; filters apply locally to the loaded results.`);
            });
            tab.setAttribute("aria-pressed", String(view === name));
            $("views").append(tab);
            if (view === name) currentTab = tab;
        }
        $("results").replaceChildren();
        const filtered = rows.filter(({ memory }) => visible(memory));
        $("count").textContent = `${filtered.length} of ${rows.length} loaded records · ${resultLabel}`;
        for (const match of filtered) {
            const memory = match.memory;
            const item = button("", () => select(memory.id, match));
            item.setAttribute("aria-current", String(selected?.memory.id === memory.id));
            item.append(node("strong", memory.title || "Forgotten memory"),
                node("span", `${memory.domain} / ${memory.kind} / ${statusLabel(memory.status)} · revision ${memory.revision}`, "meta"),
                node("span", memory.id, "meta"));
            $("results").append(item);
        }
        if (!filtered.length) $("results").append(node("p", rows.length ? "No matches in these local filters. Change filters or rerun a scoped search." : "No records loaded. Browse or search; the panel does not manufacture memory."));
        if (focusView) currentTab.focus();
    }
    function why(match) {
        if (!match) return "Opened by exact ID. No search relevance or semantic match is claimed.";
        const reasons = [...(match.matched_by || []), ...(match.selection_reasons || [])];
        return reasons.length ? reasons.join(", ") : "Selected from the stored-memory list, not a relevance recommendation.";
    }
    function renderDetail(match) {
        const { memory, history, links, forget_preview: preview, usage } = selected;
        const root = node("article");
        const title = node("h2", memory.title || "Forgotten memory");
        title.tabIndex = -1;
        root.append(title, node("div", `${memory.id} · revision ${memory.revision} · ${memory.domain} / ${memory.kind} / ${statusLabel(memory.status)}`, "meta"));
        root.append(node("p", `Why selected: ${why(match)}`));
        if (memory.text) root.append(node("pre", memory.text));
        root.append(node("p", `Authority: ${memory.authority || "not retained"} · Scope: ${memory.scope || "not retained"}. Stored observations are not instructions or consent.`));
        root.append(node("p", (memory.allowed_uses || []).includes("drafting")
            ? "Permitted for recipient-draft text; audience, sensitivity and exact send approval still apply."
            : "Reasoning context only: do not copy this text into a recipient-facing draft."));
        root.append(details("Provenance, confidence, permission and validity", {
            source_refs: memory.source_refs, confidence_basis: memory.confidence_basis ?? memory.metadata?.confidence_basis,
            validation_level: memory.metadata?.validation,
            runtime_validated: memory.metadata?.last_validation?.runtime_validated ?? memory.metadata?.runtime_validated,
            sensitivity: memory.sensitivity, allowed_uses: memory.allowed_uses,
            valid_from: memory.valid_from, valid_to: memory.valid_to, observed_at: memory.observed_at,
            last_verified_at: memory.last_verified_at, review_after: memory.review_after,
        }));
        root.append(details("Complete current record (untrusted data)", memory));
        root.append(node("h3", "Relationships and dependencies"));
        root.append(table("Stored links (not proof of current eligibility)", ["From", "Relationship", "To", "Evidence / validity"], links.map(link => [
            link.source_id, link.relation, link.target_id, pretty(link.data),
        ])));
        const graph = node("div");
        root.append(node("p", "The graph uses the currently selected routine and domain filters."));
        root.append(button("Inspect current relationship graph (2 hops, at most 20 nodes)", () => work("Reading bounded relationships...", async () => {
            const input = { id: memory.id, depth: 2, limit: 20 };
            if ($("routine").value) input.routine = $("routine").value;
            if ($("domain").value) input.domain = $("domain").value;
            if ($("purpose").value === "drafting") input.usage = "drafting";
            const result = await api("graph", input);
            account(result.account);
            graph.replaceChildren();
            const limited = result.truncated || Object.values(result.omitted || {}).some(count => count > 0);
            graph.append(node("p", `Graph status: ${result.status || "returned"} · ${result.nodes.length} nodes · at most 2 hops / 20 nodes.${limited ? " Traversal or output budget omitted records; this is not a complete map." : ""}`));
            graph.append(node("p", result.permission_checks || "Local evidence only; no live source permission verification."));
            if (!result.nodes.length) graph.append(node("p", "Scope or eligibility may be unavailable. An empty inspection does not establish that no memories or relationships exist."));
            if (result.requires_larger_budget) graph.append(node("p", "Graph budget is insufficient. Discuss a separately bounded CLI inspection in the foreground; this panel does not automatically expand its budget."));
            if (result.reason) graph.append(node("p", result.reason));
            graph.append(table("Eligible graph nodes", ["Memory / work item", "Revision", "Type", "Why included"], result.nodes.map(entry => [
                entry.source_record?.store === "work" ? entry.title || entry.id
                    : button(entry.title || entry.id, () => select(entry.id, { memory: entry, selection_reasons: entry.selection_reasons })),
                entry.revision, entry.kind, (entry.selection_reasons || []).join(", "),
            ])));
            graph.append(table("Graph edges", ["From", "Relationship", "To"], (result.edges || []).map(edge => [
                edge.source_id, edge.relation, edge.target_id,
            ])), details("Graph gaps and limits", {
                gaps: result.gaps, truncated: result.truncated, omitted: result.omitted,
                budget_chars: result.budget_chars, used_chars: result.used_chars, budget_scope: result.budget_scope,
                requires_larger_budget: result.requires_larger_budget,
            }), details("Complete bounded graph", result));
            notice("Read-only relationship graph. Missing or withheld nodes are gaps, not evidence of absence.");
        })), graph);
        root.append(node("h3", "Forgetting preview — nothing erased"));
        root.append(node("p", `Exact subject ${preview.subject_id}, revision ${preview.revision}. Scope must be checked again during foreground review.`));
        root.append(table("Affected memories and derived records", ["ID", "Revision", "Kind", "Domain"], preview.affected.map(value => [
            value.id, value.revision, value.kind, value.domain,
        ])));
        const retained = node("ul");
        for (const note of preview.retained) retained.append(node("li", note));
        root.append(node("strong", "Retained / not recalled:"), retained);
        root.append(node("h3", "History and recorded use"));
        root.append(table("Recent revisions (up to 30; not a full archive)", ["Revision", "Status", "Recorded at", "Record and evidence"], history.map(value => [
            value.revision, statusLabel(value.status), value.created_at, details("Inspect revision", value),
        ])));
        root.append(details("Why used: recorded usage events (up to 30)", usage));
        if (!usage.length) root.append(node("p", "No usage events returned. Recording may be disabled; absence is not proof this memory was never used."));
        const actions = node("div", undefined, "actions");
        actions.append(button("Reload selected memory", () => select(memory.id)));
        if (memory.status !== "forgotten") {
            for (const [intent, label] of Object.entries(intents)) {
                if (intent === "export" && !(memory.kind === "lesson" && memory.status === "active"
                    && memory.authority === "user_confirmed")) continue;
                const request = button(label, () => work("Requesting foreground discussion, not approval...", async () => {
                    if (selectedStale) throw new Error("Reload this memory first.");
                    const result = await api("review", { id: memory.id, revision: memory.revision, intent });
                    notice(result.message);
                }));
                request.setAttribute("data-review", intent);
                actions.append(request);
            }
        }
        root.append(node("p", "Do-not-use suppresses recall; it does not erase memory or change capture policy. Review requests contain only account, ID, revision and intent. No edit, suppression, deletion, export file or publication happens here."), actions);
        $("detail").replaceChildren(root);
        title.focus();
    }
    function select(id, match) {
        return work("Reading provenance, revisions and forgetting scope...", async () => {
            selected = null;
            selectedStale = false;
            $("detail").replaceChildren();
            const result = await api("inspect", { id });
            account(result.memory.account);
            selected = result;
            const changed = match && match.memory.revision !== result.memory.revision;
            renderDetail(changed ? null : match);
            renderList();
            notice(changed ? "Memory changed since the list/search. Showing the current revision; previous match reasons are not reused."
                : "Inspection only. A review request is not approval.");
        });
    }
    async function load(operation) {
        const input = {};
        if (operation === "search") {
            input.query = $("query").value;
            input.mode = $("mode").value;
            if ($("routine").value) input.routine = $("routine").value;
            if ($("domain").value) input.domain = $("domain").value;
            if ($("purpose").value === "drafting") input.usage = "drafting";
            if (input.mode === "lexical" && !input.routine && !input.domain) {
                throw new Error("Choose a domain or routine scope for explicitly keyword-only search.");
            }
        }
        rows = [];
        conflictedIds = new Set();
        selected = null;
        $("detail").replaceChildren();
        renderList();
        const result = await api(operation, input);
        account(result.account);
        rows = result.results || result.memories.map(memory => ({ memory }));
        conflictedIds = new Set(result.conflicted_ids || []);
        resultLabel = operation === "search" ? (input.mode === "lexical" ? "Keyword only · no semantic matching" : "Meaning + keywords · see per-result match reasons") : "Stored memories · local filters";
        renderList();
        notice(`${rows.length} records loaded. ${resultLabel}. ${(result.warnings || []).join(" ")}`);
    }
    $("search").onclick = () => work("Searching the selected local method...", () => load("search"));
    $("list").onclick = () => work("Reading stored memory...", () => load("list"));
    $("health").onclick = () => work("Checking current account, capture and retention...", async () => {
        const complete = await refreshContext();
        notice(complete ? "Account and policy refreshed. All controls here are read-only." : "Some account/policy checks failed. Unknown status is shown above; retry refresh.", !complete);
    });
    for (const id of ["domain", "status", "kind"]) $(id).addEventListener("change", () => renderList());
    $("query").addEventListener("keydown", event => { if (event.key === "Enter") $("search").onclick(); });
    work("Loading private memory and policy...", async () => {
        const complete = await refreshContext();
        await load("list");
        if (!complete) notice("Memories loaded, but some account/policy checks failed. See unknown status above and retry refresh.", true);
    });
})();

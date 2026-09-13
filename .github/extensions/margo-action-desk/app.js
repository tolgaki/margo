globalThis.MargoSections.work = (section, ui) => {
    "use strict";
    const $ = ui.get;
    const token = new URLSearchParams(location.hash.slice(1)).get("token");
    const model = globalThis.MargoDecision;
    const lanes = ["Focus", "All", "Needs decision", "Ready", "Waiting", "Errors", "Closed"];
    let items = [];
    let snapshot = {};
    let projections = [];
    let selected = null;
    let filter = "Focus";
    let busy = false;
    let dirty = false;
    let stale = false;
    let offline = true;
    let refreshing = false;
    let listSignature = "";
    let requestSignature = "";
    let coverageSignature = "";

    // Stored text is never HTML. Only sourceLinks creates scheme-validated URLs.
    function element(tag, text, className) {
        const node = document.createElement(tag);
        if (text !== undefined) node.textContent = String(text);
        if (className) node.className = className;
        return node;
    }
    const pretty = (value) => value === undefined || value === null ? "Not recorded" : JSON.stringify(value, null, 2);
    function lane(item) {
        const status = String(item.status || item.state || "").toLowerCase();
        if (item.source_changed || /error|fail|blocked|expired|stale|partial|outcome_unknown|executing/.test(status)) return "Errors";
        if (/dismiss|complet|executed|cancel|supersed|succeeded|resolved|rejected/.test(status)) return "Closed";
        if (/defer|wait|snooz|pending/.test(status)) return "Waiting";
        if (/approved|ready|prepared|active|confirmed/.test(status)) return "Ready";
        return "Needs decision";
    }
    function notice(message, error = false) {
        $("notice").textContent = message;
        $("notice").className = error ? "notice error" : "notice";
        $("notice").setAttribute("data-busy", String(busy || refreshing));
    }
    function errorMessage(error) {
        if (error.code === "not_initialized") {
            return "Local work state needs explicit setup or migration. Review task_state.py init in the foreground conversation before enabling decision requests. Opening this panel has not initialized anything.";
        }
        if (/setup|config|account|missing_core/.test(error.code)) {
            return `Setup needed. ${error.message} Configure Margo's shared account with the portable CLI, then refresh. No sample data has been created.`;
        }
        if (/auth|credential|permission/.test(error.code)) {
            return `Authentication blocked. ${error.message} Restore the account's access in the conversation, then refresh.`;
        }
        if (/conflict|revision|stale/.test(error.code)) {
            stale = true;
            return `${error.message} Another panel or process may have changed this item. Your local edits have not been applied; reload the item before trying again.`;
        }
        return error.message;
    }
    async function api(path, input) {
        const response = await fetch(path, {
            method: input === undefined ? "GET" : "POST",
            headers: {
                Authorization: `Bearer ${token || ""}`,
                ...(input === undefined ? {} : { "Content-Type": "application/json" }),
            },
            ...(input === undefined ? {} : { body: JSON.stringify(input) }),
            cache: "no-store",
            credentials: "omit",
            signal: AbortSignal.timeout(20_000),
        });
        const result = await response.json();
        if (!response.ok || result.error) {
            const error = new Error(result.error?.message || "Could not read work state.");
            error.code = result.error?.code || "request_failed";
            throw error;
        }
        ui.observeAccount(result.account || result.item?.account);
        return result;
    }
    function button(text, handler, className) {
        const node = element("button", text, className);
        node.type = "button";
        node.addEventListener("click", handler);
        return node;
    }
    function renderList() {
        const focused = document.activeElement?.dataset?.focusKey;
        $("filters").replaceChildren();
        for (const name of lanes) {
            const count = projections.filter(row => visible(row, name)).length;
            const tab = button(`${name} (${count})`, () => { filter = name; renderList(); });
            tab.dataset.focusKey = `filter:${name}`;
            tab.setAttribute("aria-pressed", String(filter === name));
            $("filters").append(tab);
        }
        $("items").replaceChildren();
        const query = $("work-query").value.trim().toLowerCase();
        const shown = projections.filter(row => visible(row, filter)
            && [row.item.title, row.ask, row.blocker, ...row.people].some(value => String(value || "").toLowerCase().includes(query)));
        if (!shown.length) $("items").append(ui.empty(items.length ? "No matching work" : "No work recorded",
            items.length ? "Try a different search or view. Nothing has been removed."
                : "This is the local ledger, not your inbox or calendar."));
        let previousLane;
        for (const projection of shown) {
            const item = projection.item;
            if (filter === "Focus" && projection.lane !== previousLane) {
                $("items").append(element("h3", projection.lane, "group-title"));
                previousLane = projection.lane;
            }
            const row = button("", () => select(item.id), "item");
            row.dataset.focusKey = `item:${item.id}`;
            row.setAttribute("aria-current", String(selected?.id === item.id));
            row.append(element("strong", item.title || item.id));
            row.append(element("span", projection.candidate ? "Candidate ask" : ui.label(item.kind || item.type), "meta"));
            if (projection.ask !== item.title) row.append(element("span", projection.ask, "ask preview"));
            if (!["No deadline recorded", "No active deadline"].includes(projection.time.label)) row.append(element("span", projection.time.label, "timing"));
            $("items").append(row);
        }
        if (focused) ui.focus([...$("filters").querySelectorAll("button"), ...$("items").querySelectorAll("button")]
            .find(node => node.dataset.focusKey === focused), { preventScroll: true });
    }
    function visible(row, name) {
        if (name === "Focus") return !["History", "Snoozed"].includes(row.lane);
        if (name === "All") return true;
        if (name === "Waiting" && row.lane === "Snoozed") return true;
        if (name === "Closed") return row.lane === "History";
        if (name === "Needs decision") return row.lane === "Needs your decision" || row.candidate && row.lane === "Now";
        return lane(row.item) === name;
    }
    function tick(force = false) {
        const now = Date.now();
        const context = model.dayContext(now, snapshot.time_preferences);
        $("clock").textContent = context.clock;
        $("day").textContent = context.date;
        $("timezone").textContent = context.label;
        $("working-context").textContent = context.context;
        $("time-settings").textContent = [context.reason, "Supported private preference format: America/Los_Angeles; Mon-Fri; 09:00-18:00. No hours, meetings or availability are inferred. Date-only deadlines have no invented time."].filter(Boolean).join(" ");
        projections = model.project(items, now, context);
        const detailProjection = projections.find(row => row.item.id === selected?.id
            && row.item.revision === selected.revision && row.item.payload_hash === selected.payload_hash);
        if ($("detail-time") && detailProjection) $("detail-time").textContent = detailProjection.time.label;
        if ($("detail-why") && detailProjection) $("detail-why").textContent = detailProjection.why;
        if ($("detail-readiness") && detailProjection) $("detail-readiness").textContent = detailProjection.readiness;
        if ($("approval-time") && selected) {
            const expiry = model.instant(selected.expires_at);
            $("approval-time").textContent = expiry === null ? "No current approval recorded"
                : expiry <= now ? "Recorded approval expired. Obtain a new exact decision after revalidation."
                    : `Recorded approval expires ${model.relative(expiry, now)}. Fresh preflight still required.`;
        }
        const signature = JSON.stringify(projections.map(row => [row.item.id, row.item.revision, row.item.status, row.lane, row.time.label, row.readiness]));
        if (force || signature !== listSignature) {
            listSignature = signature;
            renderList();
            renderFocus();
        }
        const coverage = model.coverage(snapshot.coverage, now);
        $("coverage-label").textContent = coverage.label;
        const readAt = model.instant(snapshot.read_at);
        $("freshness").textContent = `${offline ? "Offline / cached view. " : ""}Local ledger read: ${readAt === null ? "not available" : model.relative(readAt, now)}. ${coverage.warning}`;
        const coverageKey = JSON.stringify(coverage);
        if (coverageKey !== coverageSignature) {
        coverageSignature = coverageKey;
        $("coverage-details").replaceChildren();
        if (!coverage.sources.length) $("coverage-details").append(element("p", "No proof of complete mail, calendar or Teams coverage.", "meta"));
        for (const source of coverage.sources) {
            const row = element("div", undefined, "coverage-row");
            row.append(element("strong", `${source.family} · ${source.status}`),
                element("span", `${source.freshness}. Last successful collection: ${source.lastLabel}.`, "meta"),
                ui.disclosure("Recorded scope & window", ui.facts({ scope: source.scope, window: source.covered_start && source.covered_end
                    ? { start: source.covered_start, end: source.covered_end } : source.requested_window,
                    error: source.error_class || null })));
            $("coverage-details").append(row);
        }
        }
        const limited = snapshot.truncated || [];
        $("scope").textContent = limited.length
            ? `Limited view: newest ${snapshot.limit_per_type} per type; ${limited.join(", ")} truncated. Use the CLI list --view all for the complete ledger.`
            : `${items.length} local records. Snoozed and historical records remain in All; nothing is deleted or auto-completed.`;
        renderRequests();
        updateControls();
    }
    function renderFocus() {
        const root = $("focus");
        const activeKey = document.activeElement?.dataset?.focusKey;
        root.replaceChildren();
        const row = projections.find(value => !["History", "Snoozed"].includes(value.lane));
        if (!row) {
            root.append(element("h2", items.length ? "No active items in this view" : "No active work recorded"),
                element("p", "Historical and snoozed items remain under All. This view does not establish an empty day.", "muted"));
            return;
        }
        root.append(element("h2", row.ask), element("p", row.why),
            element("p", `${row.item.title} · ${row.readiness}`, "meta"));
        const controls = element("div", undefined, "actions");
        const inspect = button("Inspect item", () => select(row.item.id), "quiet");
        inspect.dataset.focusKey = "focus:inspect";
        controls.append(inspect);
        root.append(controls);
        if (activeKey) ui.focus([...root.querySelectorAll("button")].find(node => node.dataset.focusKey === activeKey), { preventScroll: true });
    }
    function requestButton(intent, item) {
        const labels = { prepare: "Prepare for me", recommend: "Recommend a response", review: "Review in conversation" };
        const node = button(labels[intent], () => requestHelp(intent, item));
        node.dataset.request = intent;
        node.dataset.itemId = item.id;
        return node;
    }
    function renderRequests(force = false) {
        const root = $("request-status");
        if (!root || !selected) return;
        const requests = snapshot.requests?.[selected.id] || {};
        const signature = JSON.stringify([selected.id, snapshot.request_capability, requests,
            Object.values(requests).map(request => model.requestPhase(request, Date.now()))]);
        if (!force && signature === requestSignature) return;
        requestSignature = signature;
        root.replaceChildren();
        if (!snapshot.request_capability?.available) {
            root.append(element("p", `Agent requests unavailable: ${snapshot.request_capability?.reason || "Task journal has not been read."} Continue in the conversation; nothing has been initialized.`, "changed"));
        }
        for (const [intent, request] of Object.entries(requests)) {
            const phase = model.requestPhase(request, Date.now());
            const block = element("div", undefined, "request-result");
            block.append(element("strong", `${ui.label(intent)} · ${ui.label(phase)}`),
                element("p", phase === "accepted" ? "Conversation accepted the request. Preparation has not started."
                    : phase === "ready" ? "Local preparation ready. Not approved, sent, or completed work."
                    : phase === "working" ? "A tracked preparation claim is active."
                    : ["outcome_unknown", "interrupted", "dispatching"].includes(phase)
                        ? "Check the conversation and task journal before taking further action. No automatic resend."
                        : "Inspect the recorded limitation in the conversation or task journal.", "meta"),
                ui.disclosure("Request identity", element("p", `Task ${request.run_id}`, "meta")));
            if (request.result?.summary) block.append(element("p", request.result.summary));
            for (const id of request.result?.work_ids || []) {
                const result = items.find(item => item.id === id);
                block.append(button(result?.title ? `Open ${result.title}` : "Inspect prepared result", () => select(id)));
            }
            if (request.blocked_reasons?.length && !["ready", "working", "accepted"].includes(phase)) {
                block.append(element("p", request.blocked_reasons.join("; "), "meta"));
            }
            root.append(block);
        }
    }
    async function requestHelp(intent, item) {
        if (busy || offline || dirty || stale || !snapshot.request_capability?.available
            || snapshot.requests?.[item.id]?.[intent]) return;
        busy = true;
        updateControls();
        try {
            const result = await api("/api/decision-request", {
                id: item.id, expected_revision: item.revision,
                ...(item.type === "action" ? { expected_hash: item.payload_hash } : {}), intent,
            });
            snapshot.requests ||= {};
            snapshot.requests[item.id] ||= {};
            snapshot.requests[item.id][intent] = result.request;
            notice(result.replayed ? "This exact request already exists. Check its recorded progress; it was not sent again."
                : "Conversation accepted your request. Preparation is not yet complete; no approval was granted.");
            renderRequests();
        } catch (error) {
            notice(errorMessage(error), true);
            if (!error.code || /timeout|dispatch|backend_input|backend_error/.test(error.code)) offline = true;
        } finally {
            busy = false;
            updateControls();
        }
    }
    function rawSection(container, title, content) {
        container.append(element("h3", title), element("pre", typeof content === "string" ? content : pretty(content)));
    }
    function sourceLinks(source) {
        const links = element("div", undefined, "actions");
        const seen = new Set();
        const candidates = [source, source?.data, ...(source?.revisions || []).flatMap((revision) => [revision, revision.data])];
        for (const candidate of candidates) {
            for (const raw of [candidate?.web_link, candidate?.url]) {
                if (typeof raw !== "string" || !/^https?:\/\//i.test(raw)) continue;
                let url;
                try { url = new URL(raw); } catch { continue; }
                if (!["https:", "http:"].includes(url.protocol) || url.username || url.password || seen.has(url.href)) continue;
                seen.add(url.href);
                const title = candidate.title || candidate.evidence?.title;
                const link = element("a", title ? `${url.hostname} — ${String(title)}` : url.hostname);
                link.href = url.href;
                link.target = "_blank";
                link.rel = "noopener noreferrer";
                links.append(link);
            }
        }
        return links;
    }
    function version() {
        return { expected_revision: selected.revision, expected_hash: selected.payload_hash };
    }
    function updateControls() {
        $("workspace").setAttribute("aria-busy", String(busy || refreshing));
        $("notice").setAttribute("data-busy", String(busy || refreshing));
        for (const node of $("detail").querySelectorAll("[data-mutation]")) {
            const row = projections.find(value => value.item.id === selected?.id);
            node.disabled = busy || stale || offline || !row?.canMutate || (node.dataset.mutation !== "revise" && dirty);
        }
        const save = $("save");
        if (save) save.disabled ||= !dirty;
        for (const control of $("detail").querySelectorAll("[data-payload-editor]")) control.readOnly = busy || stale || offline;
        for (const node of section.querySelectorAll("[data-request]")) {
            const pending = snapshot.requests?.[node.dataset.itemId]?.[node.dataset.request];
            const row = projections.find(value => value.item.id === node.dataset.itemId);
            const blockedPreparation = node.dataset.request === "prepare" && !row?.canPrepare;
            node.disabled = busy || stale || dirty || offline || !snapshot.request_capability?.available || !!pending || blockedPreparation;
            node.title = pending ? `Already ${model.requestPhase(pending, Date.now())}; inspect the conversation or request result.`
                : blockedPreparation ? "Preparation unavailable for historical/unresolved or changed-source work; request review."
                : !snapshot.request_capability?.available ? snapshot.request_capability?.reason || "Task journal unavailable."
                    : "Bounded local preparation/review only. No external refresh or approval.";
        }
        $("refresh").disabled = busy;
    }
    async function operate(operation, extra = {}) {
        if (busy || stale || offline) return;
        busy = true;
        updateControls();
        try {
            const result = await api(`/api/items/${encodeURIComponent(selected.id)}/${operation}`, { ...version(), ...extra });
            if (operation === "review") {
                notice(result.message);
            } else {
                dirty = false;
                stale = false;
                selected = result.item || (await api(`/api/items/${encodeURIComponent(selected.id)}`)).item;
                renderDetail();
                await refresh();
                ui.focus($("detail"), { preventScroll: true });
                notice(operation === "revise"
                    ? "Saved a new local revision. Previous approval does not authorize changed content. No outbound action was performed."
                    : `${operation === "defer" ? "Deferred" : "Dismissed"} in the persistent local ledger. No outbound action was performed.`);
            }
        } catch (error) {
            notice(errorMessage(error), true);
        } finally {
            busy = false;
            updateControls();
        }
    }
    function action(text, operation, callback, className) {
        const node = button(text, callback, className);
        node.dataset.mutation = operation;
        return node;
    }
    function renderDetail() {
        const root = $("detail");
        root.replaceChildren();
        if (!selected) {
            ui.initialDetail("Select an ask to read its recommendation, evidence and exact prepared response.");
            return;
        }
        const item = selected;
        const toolbar = element("div", undefined, "detail-toolbar");
        toolbar.append(ui.backButton(), button("Reload item / discard edits", () => select(item.id, true), "quiet"));
        root.append(toolbar);
        const row = element("div", undefined, "row");
        row.append(element("span", `${ui.label(item.kind || item.type)} · ${ui.label(item.status || item.state)}`, "badge"));
        root.append(row, element("h2", item.title || item.id));
        root.append(element("div", `Revision ${item.revision}`, "meta"));
        const projection = model.project([...items.filter(row => row.id !== item.id), item], Date.now(),
            model.dayContext(Date.now(), snapshot.time_preferences)).find(row => row.item.id === item.id);
        if (projection) {
            const detailTime = element("p", projection.time.label, "badge");
            detailTime.id = ui.id("detail-time");
            const readiness = element("p", projection.readiness, "meta");
            readiness.id = ui.id("detail-readiness");
            root.append(detailTime, readiness);
            const context = element("div");
            for (const [label, value] of [
                ["Key ask / recorded next step", projection.ask],
                ["Why now", projection.why],
                ["Recorded blocker", projection.blocker || "Not recorded"],
                ["Work blocked by this item", projection.blocks.length ? projection.blocks.join("; ") : "No explicit blocking relationship recorded"],
                ["People affected", projection.people.length ? projection.people.join("; ") : "Not recorded"],
            ]) {
                const line = element("div", undefined, "summary-line");
                const content = element("span", value);
                if (label === "Why now") content.id = ui.id("detail-why");
                line.append(element("strong", label), content);
                (["Key ask / recorded next step", "Why now"].includes(label) ? root : context).append(line);
            }
            root.append(ui.disclosure("People, blockers & impact", context));
        }
        const requests = element("div", undefined, "actions");
        for (const intent of ["prepare", "recommend", "review"]) requests.append(requestButton(intent, item));
        const requestStatus = element("div");
        requestStatus.id = ui.id("request-status");
        const assistance = ui.disclosure("Assistance", element("p", "Optional preparation or conversation review. Nothing is approved or sent here.", "meta"), requests, requestStatus);
        assistance.className = "assistance";
        assistance.hidden = item.kind === "feedback";
        root.append(assistance);
        renderRequests(true);
        const expires = item.expires_at || item.expiry;
        const expired = expires && Date.parse(expires) <= Date.now();
        if (item.type === "action") {
            const approvalTime = element("p", `Current approval expiry: ${expires || "No current approval"}${expired ? " — expired; revalidate before review" : ""}`, expired ? "changed" : "meta");
            approvalTime.id = ui.id("approval-time");
            root.append(approvalTime);
        }
        if (item.source_changed || item.changed_source || item.status === "stale" || item.state === "stale") {
            root.append(element("p", "Source changed. Revalidate evidence and prepare a new revision before seeking approval.", "changed"));
        }
        if (!projection) rawSection(root, "Why now", item.why_now || item.reason);
        if (item.type === "action") {
            root.append(element("h3", "Prepared response"), ui.facts(item.target));
            const content = item.payload || {};
            for (const key of ["subject", "title", "body", "content", "text"]) {
                const value = content[key];
                if (typeof value === "string") root.append(element(key === "subject" || key === "title" ? "h4" : "div", value, "reading"));
                else if (value && typeof value.content === "string") root.append(element("div", value.content, "reading"));
            }
            root.append(ui.disclosure("Exact proposed payload", element("pre", pretty(item.payload))));
        } else {
            if (item.kind === "artifact" && typeof item.payload?.markdown === "string") root.append(element("div", item.payload.markdown, "reading"));
            root.append(ui.disclosure("Stored work details", ui.facts(item.payload)));
        }
        const evidenceRoot = element("div");
        const sources = Array.isArray(item.sources) ? item.sources : [];
        for (const source of sources) {
            evidenceRoot.append(sourceLinks(source));
            const observed = model.instant(source.observed_at);
            evidenceRoot.append(element("p", `Source ${source.family || ""} · observed ${observed === null ? "time unknown" : new Date(observed).toLocaleString()} · ${source.current_revision !== source.revision ? "source revision changed" : "stored revision"}; not a live read.`, "meta"));
        }
        const snapshots = element("details");
        snapshots.append(element("summary", "Evidence and source snapshots"), element("pre", pretty(item.evidence || item.sources)));
        evidenceRoot.append(snapshots);
        for (const ref of Array.isArray(item.evidence) ? item.evidence : []) {
            if (!ref?.source_id) continue;
            const sourceDetail = element("details");
            sourceDetail.append(element("summary", `Source ${ref.source_id} · referenced revision ${ref.revision}`));
            const sourceBody = element("pre", "Open to read the stored source snapshots. This does not fetch external content.");
            const links = element("div");
            links.append(sourceLinks(ref));
            let loaded = false;
            sourceDetail.addEventListener("toggle", async () => {
                if (!sourceDetail.open || loaded) return;
                try {
                    const source = (await api(`/api/items/${encodeURIComponent(ref.source_id)}`)).item;
                    sourceBody.textContent = pretty(source);
                    links.replaceChildren(sourceLinks(source));
                    loaded = true;
                } catch (error) { sourceBody.textContent = errorMessage(error); }
            });
            sourceDetail.append(links, sourceBody);
            evidenceRoot.append(sourceDetail);
        }
        root.append(ui.disclosure(`Evidence & sources (${sources.length || (item.evidence || []).length})`, evidenceRoot));
        const provenance = element("details");
        provenance.append(element("summary", "Identity & provenance"), ui.facts({ id: item.id, revision: item.revision,
            ...(item.type === "action" ? { action_hash: item.payload_hash } : {}), ...item.provenance }));
        root.append(provenance);
        if (item.type !== "action") {
            root.append(element("p", "Work items and typed records are read-only here. Review their lifecycle changes and any required human evidence in the conversation.", "muted"));
            root.append(ui.disclosure("Complete stored record", element("pre", pretty(item))));
            updateControls();
            return;
        }
        root.append(ui.disclosure("Approval and execution history", element("pre", pretty({ approvals: item.approvals, executions: item.executions }))));
        root.append(element("p", "Review is not approval. Confirm the exact target, payload and revision in the conversation before anything is sent.", "meta"));
        const edit = element("details");
        edit.append(element("summary", "Edit local proposal payload"));
        const label = element("label", "Payload JSON");
        label.htmlFor = ui.id("payload");
        const editor = element("textarea");
        editor.id = ui.id("payload");
        editor.dataset.payloadEditor = "json";
        editor.value = pretty(item.payload);
        editor.spellcheck = false;
        const plainFields = element("div");
        const fields = new Map();
        for (const key of ["subject", "title", "body", "text", "content"]) {
            if (typeof item.payload?.[key] !== "string") continue;
            const field = element(key === "subject" || key === "title" ? "input" : "textarea");
            field.id = ui.id(`field-${key}`);
            field.dataset.payloadEditor = key;
            field.value = item.payload[key];
            const name = element("label", ui.label(key));
            name.htmlFor = field.id;
            field.addEventListener("input", () => {
                let payload;
                try { payload = JSON.parse(editor.value); } catch {
                    notice("The exact payload JSON is invalid. Fix it before editing individual fields.", true);
                    return;
                }
                if (!payload || Array.isArray(payload) || typeof payload[key] !== "string") {
                    notice("This field changed in the exact JSON. Continue editing the exact payload instead.", true);
                    return;
                }
                payload[key] = field.value;
                editor.value = pretty(payload);
                dirty = editor.value !== pretty(item.payload);
                $("edit-state").textContent = dirty ? "Unsaved changes · save a new revision or reload to discard." : "No unsaved changes.";
                updateControls();
            });
            fields.set(key, field);
            plainFields.append(name, field);
        }
        editor.addEventListener("input", () => {
            dirty = editor.value !== pretty(item.payload);
            let payload;
            try { payload = JSON.parse(editor.value); } catch { payload = null; }
            for (const [key, field] of fields) {
                field.disabled = typeof payload?.[key] !== "string";
                if (!field.disabled) field.value = payload[key];
            }
            updateControls();
            $("edit-state").textContent = dirty ? "Unsaved changes · save a new revision or reload to discard." : "No unsaved changes.";
        });
        const save = action("Save new revision", "revise", () => {
            let payload;
            try {
                payload = JSON.parse(editor.value);
                if (!payload || typeof payload !== "object" || Array.isArray(payload)) throw new Error();
            } catch {
                notice("Payload must be a JSON object. No changes were saved.", true);
                return;
            }
            operate("revise", { payload });
        });
        save.id = ui.id("save");
        const editState = element("p", "No unsaved changes.", "meta");
        editState.id = ui.id("edit-state");
        edit.append(plainFields, ui.disclosure("Advanced: exact payload JSON", label, editor), editState,
            element("p", "Saving changes the local proposal only and invalidates approval of the old payload.", "meta"), save);
        root.append(edit);
        const defer = element("details");
        defer.append(element("summary", "Defer or dismiss locally"));
        const dateLabel = element("label", `Defer until (device local time: ${Intl.DateTimeFormat().resolvedOptions().timeZone}; not necessarily the account timezone)`);
        dateLabel.htmlFor = ui.id("defer-until");
        const dateInput = element("input");
        dateInput.id = ui.id("defer-until");
        dateInput.type = "datetime-local";
        const deferButton = action("Defer item", "defer", () => {
            const until = new Date(dateInput.value);
            if (!Number.isFinite(until.getTime()) || until <= new Date()) {
                notice("Choose a future date and time.", true);
                return;
            }
            operate("defer", { until: until.toISOString() });
        });
        const dismissLabel = element("label");
        const dismissCheck = element("input");
        dismissCheck.type = "checkbox";
        dismissLabel.append(dismissCheck, document.createTextNode("I want to dismiss this local proposal"));
        const dismissButton = action("Dismiss local item", "dismiss", () => {
            if (!dismissCheck.checked) return notice("Confirm the local dismissal using the checkbox first.", true);
            operate("dismiss");
        }, "danger");
        defer.append(dateLabel, dateInput, deferButton, dismissLabel, dismissButton);
        defer.append(action("Snooze proposal for 1 hour", "defer", () => operate("defer", { until: new Date(Date.now() + 3_600_000).toISOString() })));
        root.append(defer);
        const raw = element("details");
        raw.append(element("summary", "Complete stored record"), element("pre", pretty(item)));
        root.append(raw);
        updateControls();
    }
    async function select(id, discard = false) {
        if (busy) return;
        if (dirty && !discard && id === selected?.id) {
            ui.openDetail();
            ui.focus($("detail"));
            return;
        }
        if (dirty && !discard) return notice("Save your payload edits, or choose Reload item / discard edits before switching items.", true);
        busy = true;
        updateControls();
        try {
            ui.openDetail();
            const result = (await api(`/api/items/${encodeURIComponent(id)}`)).item;
            ui.observeAccount(result.account);
            selected = result;
            dirty = false;
            stale = false;
            renderDetail();
            renderList();
            ui.focus($("detail"), { preventScroll: false });
            notice("Local proposal loaded. Review requests are not approval; no outbound action is performed here.");
        } catch (error) {
            notice(errorMessage(error), true);
        } finally {
            busy = false;
            updateControls();
        }
    }
    async function refresh(quiet = false) {
        if (refreshing) return;
        refreshing = true;
        try {
            const result = await api("/api/desk");
            ui.observeAccount(result.account);
            const accountChanged = snapshot.account && result.account !== snapshot.account;
            if (accountChanged) {
                selected = null;
                dirty = false;
                stale = false;
                renderDetail();
            }
            snapshot = result;
            items = result.items;
            offline = false;
            const account = result.account;
            $("account").textContent = `Configured account: ${typeof account === "object" ? account?.name || account?.id || pretty(account) : account || "CLI default"} · shared across panels`;
            tick(true);
            if (selected) {
                const current = items.find((item) => item.id === selected.id);
                if (!current || current.revision !== selected.revision || current.payload_hash !== selected.payload_hash
                    || current.status !== selected.status || current.source_changed !== selected.source_changed
                    || current.updated_at !== selected.updated_at) {
                    stale = true;
                    notice("This item changed in the shared ledger. Reload it before editing or requesting review; unsaved text has been preserved.", true);
                    updateControls();
                }
            }
            if (accountChanged) notice("Configured account changed. Previous account details and unsaved edits were cleared; select an item under the current account.", true);
            else if (!quiet && !stale) notice(items.length ? "Current persistent work state. No outbound actions run from this panel." : "Your ledger is empty. No sample proposals have been added.");
        } catch (error) {
            offline = true;
            notice(errorMessage(error), true);
            if (!items.length) {
                $("focus").replaceChildren(element("h2", "Local work state unavailable"),
                    element("p", "Restore setup/access in the conversation, then refresh local state. No sample records, account or database have been created.", "muted"));
            }
        } finally {
            refreshing = false;
            updateControls();
        }
    }
    $("refresh").addEventListener("click", () => refresh());
    $("work-query").addEventListener("input", renderList);
    $("detail").tabIndex = -1;
    tick();
    if (!token) {
        notice("This panel has no access token. Reopen the Action Desk from the app.", true);
    } else {
        refresh();
        setInterval(() => { if (ui.active() && !busy) refresh(true); }, 15_000);
        setInterval(() => { if (ui.active()) tick(); }, 1000);
        document.addEventListener("visibilitychange", () => { if (ui.active()) { tick(true); if (!busy) refresh(true); } });
    }
    return { activate() { tick(true); if (token && !busy) refresh(true); } };
};

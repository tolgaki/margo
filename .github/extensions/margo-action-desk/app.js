(() => {
    "use strict";
    const $ = (id) => document.getElementById(id);
    const token = new URLSearchParams(location.hash.slice(1)).get("token");
    const lanes = ["All", "Needs decision", "Ready", "Waiting", "Errors", "Closed"];
    let items = [];
    let selected = null;
    let filter = "All";
    let busy = false;
    let dirty = false;
    let stale = false;

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
    }
    function errorMessage(error) {
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
        return result;
    }
    function button(text, handler, className) {
        const node = element("button", text, className);
        node.type = "button";
        node.addEventListener("click", handler);
        return node;
    }
    function renderList() {
        $("filters").replaceChildren();
        for (const name of lanes) {
            const count = items.filter((item) => name === "All" || lane(item) === name).length;
            const tab = button(`${name} (${count})`, () => { filter = name; renderList(); });
            tab.setAttribute("aria-pressed", String(filter === name));
            $("filters").append(tab);
        }
        $("items").replaceChildren();
        const visible = items.filter((item) => filter === "All" || lane(item) === filter);
        if (!visible.length) $("items").append(element("p", items.length ? "No items in this view." : "No proposals yet. Margo's CLI and sweeps populate this account; this panel does not manufacture work.", "empty"));
        for (const item of visible) {
            const row = button("", () => select(item.id), "item");
            row.setAttribute("aria-current", String(selected?.id === item.id));
            row.append(element("strong", item.title || item.id));
            row.append(element("span", `${item.type || "record"} · ${lane(item)} · ${item.status || item.state || "Unspecified"}`, "meta"));
            row.append(element("span", item.why_now || item.reason || "", "meta"));
            $("items").append(row);
        }
    }
    function section(container, title, content) {
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
        for (const node of $("detail").querySelectorAll("[data-mutation]")) {
            node.disabled = busy || stale || (node.dataset.mutation !== "revise" && dirty);
        }
        const save = $("save");
        if (save) save.disabled = busy || stale || !dirty;
        $("refresh").disabled = busy;
    }
    async function operate(operation, extra = {}) {
        if (busy || stale) return;
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
            root.append(element("p", "Select a proposal to review its evidence and exact payload.", "empty"));
            return;
        }
        const item = selected;
        const row = element("div", undefined, "row");
        row.append(element("span", `${item.type || "record"} · ${lane(item)} · ${item.status || item.state || "Unspecified"}`, "badge"));
        row.append(button("Reload item / discard edits", () => select(item.id, true)));
        root.append(row, element("h2", item.title || item.id));
        root.append(element("div", `ID ${item.id} · revision ${item.revision}`, "meta"));
        if (item.type === "action") root.append(element("div", `Action hash ${item.payload_hash || "Not recorded"}`, "meta"));
        const expires = item.expires_at || item.expiry;
        const expired = expires && Date.parse(expires) <= Date.now();
        if (item.type === "action") root.append(element("p", `Current approval expiry: ${expires || "No current approval"}${expired ? " — expired; revalidate before review" : ""}`, expired ? "changed" : "meta"));
        if (item.source_changed || item.changed_source || item.status === "stale" || item.state === "stale") {
            root.append(element("p", "Source changed. Revalidate evidence and prepare a new revision before seeking approval.", "changed"));
        }
        section(root, "Why now", item.why_now || item.reason);
        section(root, "Evidence and source snapshots", item.evidence || item.sources);
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
            root.append(sourceDetail);
        }
        section(root, "Provenance", item.provenance);
        if (item.type === "action") section(root, "Exact target / recipients", item.target);
        section(root, item.type === "action" ? "Exact proposed payload" : "Stored work data", item.payload);
        if (item.type !== "action") {
            root.append(element("p", "Work items and typed records are read-only here. Review their lifecycle changes and any required human evidence in the conversation.", "muted"));
            section(root, "Complete stored record", item);
            return;
        }
        section(root, "Approval and execution history", { approvals: item.approvals, executions: item.executions });
        root.append(element("p", "Requesting review is not approval. The conversation must display the current action and obtain explicit confirmation for the exact revision and hash. Nothing here sends mail, posts messages or changes your calendar.", "muted"));
        root.append(action("Request conversation review", "review", () => operate("review"), "primary"));
        const edit = element("details");
        edit.append(element("summary", "Edit local proposal payload"));
        const label = element("label", "Payload JSON");
        label.htmlFor = "payload";
        const editor = element("textarea");
        editor.id = "payload";
        editor.value = pretty(item.payload);
        editor.spellcheck = false;
        editor.addEventListener("input", () => {
            dirty = editor.value !== pretty(item.payload);
            updateControls();
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
        save.id = "save";
        edit.append(label, editor, element("p", "Saving changes the local proposal only and invalidates approval of the old payload.", "meta"), save);
        root.append(edit);
        const defer = element("details");
        defer.append(element("summary", "Defer or dismiss locally"));
        const dateLabel = element("label", "Defer until (your local time)");
        dateLabel.htmlFor = "defer-until";
        const dateInput = element("input");
        dateInput.id = "defer-until";
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
        root.append(defer);
        const raw = element("details");
        raw.append(element("summary", "Complete stored record"), element("pre", pretty(item)));
        root.append(raw);
        updateControls();
    }
    async function select(id, discard = false) {
        if (busy) return;
        if (dirty && !discard) return notice("Save your payload edits, or choose Reload item / discard edits before switching items.", true);
        busy = true;
        updateControls();
        try {
            selected = (await api(`/api/items/${encodeURIComponent(id)}`)).item;
            dirty = false;
            stale = false;
            renderDetail();
            renderList();
            notice("Local proposal loaded. Review requests are not approval; no outbound action is performed here.");
        } catch (error) {
            notice(errorMessage(error), true);
        } finally {
            busy = false;
            updateControls();
        }
    }
    async function refresh(quiet = false) {
        try {
            const result = await api("/api/items");
            items = result.items;
            const account = result.account;
            $("account").textContent = `Configured account: ${typeof account === "object" ? account?.name || account?.id || pretty(account) : account || "CLI default"} · shared across panels`;
            renderList();
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
            if (!quiet && !stale) notice(items.length ? "Current persistent work state. No outbound actions run from this panel." : "Your ledger is empty. No sample proposals have been added.");
        } catch (error) {
            notice(errorMessage(error), true);
        }
    }
    $("refresh").addEventListener("click", () => refresh());
    const syncTheme = () => {
        const mode = document.documentElement.getAttribute("data-color-mode");
        if (mode === "dark" || mode === "light") document.documentElement.setAttribute("data-theme", mode);
    };
    new MutationObserver(syncTheme).observe(document.documentElement, { attributes: true, attributeFilter: ["data-color-mode"] });
    syncTheme();
    if (!token) {
        notice("This panel has no access token. Reopen Margo Action Desk from the app.", true);
    } else {
        refresh();
        setInterval(() => { if (!document.hidden && !busy) refresh(true); }, 15_000);
    }
})();

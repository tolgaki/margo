(() => {
    "use strict";
    const root = document.documentElement;
    const media = globalThis.matchMedia?.("(prefers-color-scheme: dark)");
    function syncTheme() {
        const host = root.getAttribute("data-color-mode");
        const parameter = new URLSearchParams(globalThis.location?.search || "").get("scoutTheme");
        root.setAttribute("data-theme", ["dark", "light"].includes(host) ? host
            : ["dark", "light"].includes(parameter) ? parameter : media?.matches ? "dark" : "light");
    }
    syncTheme();
    media?.addEventListener("change", syncTheme);
    new MutationObserver(syncTheme).observe(root, { attributes: true, attributeFilter: ["data-color-mode"] });
    const names = { owe: "You owe", waiting_on: "Waiting on someone", own: "You own",
        outcome_unknown: "Outcome unknown", waiting_approval: "Needs approval", do_not_learn: "Do not learn",
        debrief_proposed: "Debrief ready for review", recap_pending: "Waiting for recap", user_confirmed: "User confirmed",
        source_observed: "Source observed", model_calls: "Model calls", output_chars: "Output characters",
        tool_calls: "Tool calls", prepared: "Prepared", succeeded: "Completed attempt",
        "meeting-prep": "Meeting preparation", "work-products": "Work products", "daily-brief": "Daily brief",
        "mail.reply": "Reply proposal", "mail.send": "Message proposal" };
    function label(value) {
        if (value === undefined || value === null || value === "") return "Not recorded";
        const text = String(value);
        return names[text] || text.replaceAll("_", " ").replace(/^\w/, letter => letter.toUpperCase());
    }
    function node(tag, text, cls) {
        const result = document.createElement(tag);
        if (text !== undefined) result.textContent = String(text);
        if (cls) result.className = cls;
        return result;
    }
    function disclosure(title, ...contents) {
        const result = node("details");
        result.append(node("summary", title), ...contents);
        return result;
    }
    function facts(value) {
        const result = node("dl", undefined, "facts");
        for (const [key, entry] of Object.entries(value || {})) {
            const field = node("div");
            const format = (entry, depth = 0) => entry === null || entry === undefined ? "Not recorded"
                : typeof entry !== "object" ? String(entry)
                    : depth >= 3 ? JSON.stringify(entry)
                        : Array.isArray(entry) ? entry.map(item => format(item, depth + 1)).join("\n") || "None recorded"
                            : Object.entries(entry).map(([name, item]) => `${label(name)}: ${format(item, depth + 1)}`).join("\n") || "None recorded";
            const content = format(entry);
            field.append(node("dt", label(key)), node("dd", content));
            result.append(field);
        }
        return result;
    }
    function empty(title, description) {
        const result = node("div", undefined, "empty");
        result.append(node("strong", title), node("p", description));
        return result;
    }
    function forSection(container, prefix = "") {
        let returnTarget;
        const stateRoot = container === document ? document.documentElement : container;
        const id = value => prefix ? `${prefix}-${value}` : value;
        const get = value => container === document ? document.getElementById(id(value)) : container.querySelector(`#${id(value)}`);
        const active = () => !document.hidden && !stateRoot.hidden && !globalThis.MargoWorkspace?.locked;
        const focus = (target, options) => { if (active()) target?.focus(options); };
        function openDetail(target) {
            if (active()) returnTarget = target || document.activeElement;
            stateRoot.setAttribute("data-detail", "true");
        }
        function closeDetail() {
            stateRoot.setAttribute("data-detail", "false");
            const current = container.querySelector('[aria-current="true"]');
            const fallback = container.querySelector(".list-tools input, input, button");
            focus(returnTarget?.isConnected && returnTarget.closest?.(".list-pane") && container.contains(returnTarget)
                ? returnTarget : current || fallback);
        }
        function backButton() {
            const result = node("button", "Back to list", "back-button quiet");
            result.type = "button";
            result.addEventListener("click", closeDetail);
            return result;
        }
        function initialDetail(text) {
            stateRoot.setAttribute("data-detail", "false");
            get("detail").replaceChildren(empty("Select a record", text));
        }
        container.addEventListener?.("keydown", event => {
            if (event.key === "Escape" && stateRoot.getAttribute("data-detail") === "true"
                && !["INPUT", "TEXTAREA", "SELECT"].includes(event.target?.tagName)) closeDetail();
        });
        const observeAccount = value => globalThis.MargoWorkspace?.observeAccount(value);
        return Object.freeze({ label, node, disclosure, facts, empty, get, id, active, focus, observeAccount,
            openDetail, closeDetail, backButton, initialDetail });
    }
    globalThis.MargoUI = Object.freeze({ label, node, disclosure, facts, empty, forSection });
    globalThis.MargoSections = {};
})();

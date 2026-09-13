(() => {
    "use strict";
    const names = ["work", "memory", "tasks", "automations", "config"];
    const labels = { work: "Work", memory: "Memory", tasks: "Tasks", automations: "Automations", config: "Config" };
    const paths = { work: "/", memory: "/memory", tasks: "/tasks", automations: "/automations", config: "/config" };
    const panels = new Map(names.map(name => [name, { root: document.getElementById(`section-${name}`), mounted: false, scroll: 0 }]));
    const tabs = [...document.querySelectorAll("#workspace-tabs [role=tab]")];
    const alert = document.getElementById("workspace-alert");
    let current = null, currentAccount = null, locked = false;
    function observeAccount(account) {
        if (locked) throw new Error("Account changed. Reload this workspace before continuing.");
        if (typeof account !== "string" || !account) return;
        if (currentAccount && currentAccount !== account) {
            locked = true;
            for (const panel of panels.values()) { panel.root.hidden = true; panel.root.inert = true; }
            for (const tab of tabs) tab.disabled = true;
            alert.textContent = "The configured account changed. Previous section content and drafts are hidden. Reload the workspace to read the current account; no operation was authorized by this change.";
            alert.hidden = false;
            document.getElementById("workspace-reload").hidden = false;
            throw new Error("Account changed. Reload this workspace before continuing.");
        }
        currentAccount = account;
        document.getElementById("workspace-account").textContent = `Local owner: ${account}. Shared across this workspace. Microsoft 365 authentication: not checked.`;
    }
    function navigate(name, { history = true, restore = true } = {}) {
        if (!names.includes(name) || locked) return;
        const previous = panels.get(current);
        if (name === current) return;
        if (previous) {
            previous.scroll = window.scrollY;
            previous.root.hidden = true;
        }
        current = name;
        for (const tab of tabs) {
            const chosen = tab.dataset.section === name;
            tab.setAttribute("aria-selected", String(chosen));
            tab.tabIndex = chosen ? 0 : -1;
        }
        document.getElementById("workspace-section-label").textContent = labels[name];
        const panel = panels.get(name);
        panel.root.hidden = false;
        if (!panel.mounted) {
            try {
                if (typeof globalThis.MargoSections[name] !== "function") throw new Error("Section controller not loaded.");
                panel.controller = globalThis.MargoSections[name](panel.root, globalThis.MargoUI.forSection(panel.root, name));
                panel.mounted = true;
            } catch {
                panel.root.replaceChildren(globalThis.MargoUI.empty("Section unavailable", "Reload the workspace after checking that the extension's renderer files were installed together."));
                alert.textContent = `${labels[name]} could not start. Other sections remain available. No state was initialized.`;
                alert.hidden = false;
            }
        } else panel.controller?.activate?.();
        if (history) {
            const url = new URL(location.href);
            url.pathname = paths[name];
            window.history.pushState({ margoSection: name }, "", url);
        }
        if (restore) {
            const target = panel.focus?.isConnected && panel.root.contains(panel.focus) ? panel.focus : panel.root;
            target.focus({ preventScroll: true });
            window.scrollTo(0, panel.scroll);
        }
    }
    for (const [name, panel] of panels) panel.root.addEventListener("focusin", event => { if (current === name) panel.focus = event.target; });
    for (const tab of tabs) {
        tab.addEventListener("click", () => navigate(tab.dataset.section));
        tab.addEventListener("keydown", event => {
            const index = tabs.indexOf(tab);
            const next = event.key === "ArrowRight" ? (index + 1) % tabs.length : event.key === "ArrowLeft"
                ? (index + tabs.length - 1) % tabs.length : event.key === "Home" ? 0 : event.key === "End" ? tabs.length - 1 : null;
            if (next !== null) {
                event.preventDefault();
                tabs.forEach((button, i) => { button.tabIndex = i === next ? 0 : -1; });
                tabs[next].focus();
            }
        });
    }
    window.addEventListener("popstate", () => {
        const name = names.find(name => paths[name] === location.pathname);
        if (name) navigate(name, { history: false });
        else {
            alert.textContent = "This workspace address does not name a supported section. Use the workspace tabs.";
            alert.hidden = false;
        }
    });
    document.getElementById("workspace-reload").addEventListener("click", () => location.reload());
    globalThis.MargoWorkspace = Object.freeze({ observeAccount, get locked() { return locked; } });
    navigate(document.getElementById("workspace-content").dataset.initialSection, { history: false, restore: false });
})();

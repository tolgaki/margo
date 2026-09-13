(() => {
    "use strict";
    const token = new URLSearchParams(location.hash.slice(1)).get("token");
    let loading = false;
    function apply(profile) {
        globalThis.MargoWorkspace?.observeAccount(profile.account);
        for (const node of document.querySelectorAll("[data-assistant-title]")) {
            node.textContent = `${profile.assistant_name} ${node.dataset.assistantTitle}`;
            document.title = node.textContent;
        }
        const settings = document.getElementById("profile-settings");
        if (settings) settings.textContent = `Assistant: ${profile.assistant_name}. Work root: ${profile.work_root.path || "not configured"} (${profile.work_root.status}). ${profile.work_root.reason || ""} Runtime database stays in private non-synced storage. Use Config for explicit local settings changes; no files are moved.`;
        const error = document.getElementById("profile-error");
        if (error) error.textContent = profile.account ? ""
            : "Local account setup is not configured at the resolved location. Inspect margo_store.py locations and explicitly bind the approved private root. Microsoft 365 authentication has not been checked.";
        const owner = document.getElementById("workspace-account");
        if (owner && profile.account) owner.textContent = `Local owner: ${profile.account}. Microsoft 365 authentication: not checked.`;
        document.dispatchEvent(new CustomEvent("margo-profile", { detail: profile }));
    }
    globalThis.MargoProfile = Object.freeze({ apply });
    async function refresh() {
        if (!token || loading || document.hidden) return;
        loading = true;
        try {
            const response = await fetch("/api/profile", {
                headers: { Authorization: `Bearer ${token}` }, credentials: "omit", cache: "no-store",
                signal: AbortSignal.timeout(15_000),
            });
            const profile = await response.json();
            if (!response.ok || profile.error || typeof profile.assistant_name !== "string") {
                throw new Error(profile.error?.message || "Profile unavailable.");
            }
            apply(profile);
        } catch (error) {
            const node = document.getElementById("profile-error");
            if (node) node.textContent = `Profile could not be refreshed: ${error.message} Shown name may be outdated; no settings were changed.`;
        } finally {
            loading = false;
        }
    }
    refresh();
    setInterval(refresh, 15_000);
})();

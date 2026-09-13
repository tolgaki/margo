globalThis.MargoSections.config = (section, ui) => {
    const $ = ui.get;
    const token = new URLSearchParams(location.hash.slice(1)).get("token");
    let baseline = null, current = null, busy = false, stale = false;
    const draft = () => ({ assistant_name: $("name").value, work_root: $("clear").checked ? null : $("root").value });
    const stored = profile => ({ assistant_name: profile.assistant_name, work_root: profile.work_root.path ?? null });
    const dirty = () => baseline && JSON.stringify(draft()) !== JSON.stringify(stored(baseline));
    function notice(text, error = false) { $("notice").textContent = text; $("notice").className = error ? "notice error" : "notice"; }
    function controls() {
        $("form").setAttribute("aria-busy", String(busy));
        const value = draft();
        const validName = /^[\p{L}\p{M}\p{N} .'-]{1,60}$/u.test(value.assistant_name) && /[\p{L}\p{N}]/u.test(value.assistant_name)
            && value.assistant_name.trim() === value.assistant_name;
        const validRoot = value.work_root === null || /^(?:[A-Za-z]:[\\/]|\/)/.test(value.work_root) && !/[\x00-\x1f\x7f]/.test(value.work_root);
        $("save").disabled = busy || stale || !baseline?.revision || !dirty() || !validName || !validRoot;
        $("cancel").disabled = busy || !baseline;
        $("reload").disabled = busy;
        $("name").disabled = busy || !baseline?.revision;
        $("clear").disabled = busy || !baseline?.revision;
        $("root").disabled = busy || !baseline?.revision || $("clear").checked;
        $("review").hidden = !stale || !current;
    }
    function showAvailability(profile) {
        const path = profile.work_root.path;
        const name = path?.split(/[\\/]/).filter(Boolean).at(-1);
        $("availability").textContent = `${name ? `${name} — ${path}` : "No workspace"}. ${profile.work_root.status}. ${profile.work_root.reason || ""} Local owner: ${profile.account || "not configured"}; M365 authentication not checked.`;
    }
    function accept(profile, overwrite = false) {
        ui.observeAccount(profile.account);
        current = profile;
        showAvailability(profile);
        if (!baseline || overwrite || !dirty()) {
            baseline = profile; stale = false;
            $("name").value = profile.assistant_name;
            $("root").value = profile.work_root.path || "";
            $("clear").checked = !profile.work_root.path;
            $("conflict").hidden = true;
        } else if (baseline.revision !== profile.revision) {
            stale = true;
            $("conflict").hidden = false;
            $("conflict").textContent = `Settings changed. Your edits are preserved. Current name: ${profile.assistant_name}; workspace: ${profile.work_root.path || "not configured"}. Review these values before applying your edits.`;
        }
        controls();
    }
    async function api(input) {
        const response = await fetch("/api/profile", { method: input ? "POST" : "GET",
            headers: { Authorization: `Bearer ${token || ""}`, ...(input ? { "Content-Type": "application/json" } : {}) },
            ...(input ? { body: JSON.stringify(input) } : {}), cache: "no-store", credentials: "omit", signal: AbortSignal.timeout(20000) });
        const value = await response.json();
        if (!response.ok || value.error) throw Object.assign(new Error(value.error?.message || "Settings request failed."), { code: value.error?.code });
        return value;
    }
    async function reload() {
        if (busy) return;
        busy = true; controls();
        try { accept(await api()); notice(stale ? "Current settings loaded; local edits still need review." : "Current local settings loaded. Nothing changed."); }
        catch (error) { notice(error.message, true); }
        finally { busy = false; controls(); }
    }
    $("save").onclick = async () => {
        if ($("save").disabled) return;
        busy = true; controls(); notice("Saving exact local settings...");
        try {
            const value = await api({ expected_revision: baseline.revision, ...draft() });
            accept(value, true);
            globalThis.MargoProfile?.apply(value);
            notice("Settings saved. Existing files, private state and native scheduler bindings were not moved.");
        } catch (error) {
            stale = true; current = null;
            notice(`${error.message} Your edits are preserved. Read current settings before retrying; a timeout may have saved them.`, true);
        } finally { busy = false; controls(); }
    };
    $("cancel").onclick = () => { accept(current || baseline, true); notice("Local edits discarded; nothing saved."); };
    $("reload").onclick = reload;
    $("review").onclick = () => { baseline = current; stale = false; $("conflict").hidden = true; controls(); notice("Current revision acknowledged. Review your draft, then Save changes."); };
    for (const id of ["name", "root", "clear"]) $(id).addEventListener("input", controls);
    document.addEventListener("margo-profile", event => { if (!busy) accept(event.detail); });
    reload();
    return {};
};

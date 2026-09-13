globalThis.MargoSections.automations = (section, ui) => {
    const $ = ui.get, token = new URLSearchParams(location.hash.slice(1)).get("token");
    const headings = ["Description/Purpose", "Conditions", "Inputs", "Steps", "Outputs", "Completion/Idempotency", "Failure/Retry", "Permissions/Review", "Source references"];
    let snapshot = null, selected = null, busy = false, dirty = false, stale = false, draftFields = null, previewArea = null;
    const node = ui.node;
    function notice(text, error = false) { $("notice").textContent = text; $("notice").className = error ? "notice error" : "notice"; }
    async function api(operation, input = {}) {
        const response = await fetch("/api/automations/" + operation, { method: "POST",
            headers: { Authorization: `Bearer ${token || ""}`, "Content-Type": "application/json" },
            body: JSON.stringify(input), credentials: "omit", cache: "no-store", signal: AbortSignal.timeout(20000) });
        const result = await response.json();
        if (!response.ok || result.error) throw Object.assign(new Error(result.error?.message || "Automation request failed."), { code: result.error?.code });
        return result;
    }
    function controls() {
        for (const control of section.querySelectorAll("[data-write]")) control.disabled = busy || stale || control.dataset.blocked === "true";
        if (draftFields) draftFields.save.disabled = busy || stale || !dirty || !draftFields.id.value.trim() || !draftFields.title.value.trim()
            || Object.values(draftFields.sections).some(field => !field.value.trim());
        $("new").disabled = busy || !snapshot || dirty;
        $("refresh").disabled = busy;
        $("workspace").setAttribute("aria-busy", String(busy));
    }
    function button(text, handler) { const result = node("button", text); result.type = "button"; result.addEventListener("click", handler); return result; }
    async function work(operation, saving = false) {
        if (busy) return;
        busy = true; controls();
        try { await operation(); } catch (error) {
            if (error.code === "revision_conflict" || saving) { stale = true; previewArea?.replaceChildren(); }
            notice(`${error.message} Local drafts are preserved; no native workflow was created or executed.${saving ? " A failed response may still have saved the definition. Cancel and reread before another save." : ""}`, true);
        } finally { busy = false; controls(); }
    }
    function list() {
        $("items").replaceChildren();
        for (const entry of snapshot?.scenarios || []) {
            const meta = entry.metadata, search = $("query").value.toLowerCase(), filter = $("filter").value;
            if (![meta.title, meta.id, entry.description].some(value => String(value || "").toLowerCase().includes(search))
                || filter === "enabled" && !meta.enabled || filter === "disabled" && meta.enabled
                || filter === "review_required" && meta.review_status !== "review_required") continue;
            const item = button("", () => select(meta.id));
            item.className = "item";
            item.setAttribute("aria-current", String(selected?.metadata.id === meta.id));
            item.append(node("strong", meta.title), node("span", `${meta.enabled ? "Enabled descriptor" : "Disabled"} · ${ui.label(meta.review_status)}`, "meta"),
                node("span", meta.schedule ? `${meta.schedule.expression} · ${meta.timezone || "timezone unresolved"}` : "Schedule unresolved", "meta"));
            $("items").append(item);
        }
        for (const error of snapshot?.errors || []) $("items").append(node("p", `${error.id}: ${error.error}`, "warn"));
        if (!$("items").children.length) $("items").append(ui.empty("No matching registered scenarios", "Only controller-registered Markdown definitions are listed. No folder scan or native workflow import occurs."));
    }
    function resetEditor() {
        selected = null; draftFields = null; previewArea = null; dirty = false; stale = false;
        ui.initialDetail("Select a registered scenario or Add scenario.");
    }
    async function refresh() {
        const value = await api("list");
        const changed = snapshot && (snapshot.workspace !== value.workspace || snapshot.profile_revision !== value.profile_revision
            || snapshot.controller_revision !== value.controller_revision
            || selected && value.scenarios.find(entry => entry.metadata.id === selected.metadata.id)?.revision !== selected.revision);
        if (changed && dirty) {
            stale = true;
            previewArea?.replaceChildren();
            notice("Workspace or definition changed. Your draft is preserved but cannot be saved. Cancel and reread explicitly.", true);
        } else if (changed) resetEditor();
        snapshot = value;
        $("master").textContent = `Workspace: ${value.workspace}. Native controller: ${value.native_controller.status}; enablement ${value.native_controller.enabled === null ? "unknown" : value.native_controller.enabled}. ${value.native_controller.reason} Margo starter workflows: separate.`;
        list();
        if (!stale && value.same_schedule_groups.length) notice("Coincident schedules: " + value.same_schedule_groups.map(group => group.join(", ")).join("; ") + ". Dependency and firing-time readiness are not verified.");
    }
    function field(container, label, id, value, multiline = false) {
        const name = node("label", label), input = node(multiline ? "textarea" : "input");
        input.id = ui.id(id); name.htmlFor = input.id; input.value = value || "";
        input.addEventListener("input", () => { dirty = true; previewArea?.replaceChildren(); controls(); });
        container.append(name, input); return input;
    }
    async function showSaved(record) {
        resetEditor();
        await refresh();
        if (snapshot.profile_revision !== record.profile_revision || snapshot.controller_revision !== record.controller_revision) {
            throw new Error("Definition saved, but workspace settings changed before reread. Select it again from the current workspace.");
        }
        selected = record; editor(record); list(); ui.openDetail(); ui.focus($("detail"));
    }
    function editor(record) {
        const root = node("article"), toolbar = node("div", undefined, "detail-toolbar");
        toolbar.append(ui.backButton(), button("Cancel / reread", async () => { resetEditor(); controls(); if (record) await select(record.metadata.id); }));
        root.append(toolbar, node("h2", record ? record.metadata.title : "New scenario"),
            node("p", "Save changes edits Markdown only. Content/schedule edits clear descriptor approval and disable the scenario until re-reviewed.", "meta"));
        const id = field(root, "Stable ID", "id", record?.metadata.id); id.disabled = !!record;
        const title = field(root, "Title", "title", record?.metadata.title);
        const expression = field(root, "Schedule — five-field cron (blank = draft)", "cron", record?.metadata.schedule?.expression);
        const timezone = field(root, "IANA timezone (blank = draft)", "timezone", record?.metadata.timezone);
        root.append(node("p", "All five cron fields are ANDed, including day-of-month and weekday. No next firing time or success is inferred.", "meta"));
        const sections = {};
        headings.forEach((heading, index) => {
            const container = index === 0 || heading === "Steps" ? root : ui.disclosure(heading);
            if (container !== root) root.append(container);
            sections[heading] = field(container, heading, `content-${index}`, record?.sections[heading], true);
        });
        const previewBox = node("div");
        previewArea = previewBox;
        const save = button("Review changes", () => work(async () => {
            const patch = { title: title.value, timezone: timezone.value || null,
                schedule: expression.value ? { kind: "cron", expression: expression.value } : null,
                sections: Object.fromEntries(headings.map(heading => [heading, sections[heading].value])) };
            if (record && headings.every(heading => sections[heading].value === record.sections[heading])) delete patch.sections;
            const change = { operation: record ? "update" : "create", id: id.value, expected_revision: record?.revision || "missing",
                controller_revision: record?.controller_revision || snapshot.controller_revision,
                profile_revision: record?.profile_revision || snapshot.profile_revision, patch };
            const preview = await api("preview", change);
            const confirm = button("Save reviewed changes", () => work(async () => {
                    const saved = await api("commit", { change, preview_hash: preview.preview_hash });
                    await showSaved(saved); notice("Definition saved. Native schedules and execution permissions unchanged.");
                }, true));
            confirm.dataset.write = "commit";
            previewBox.replaceChildren(node("p", "Reviewed change will be saved disabled and review-required. No existing native scheduler binding is retargeted.", "meta"), confirm);
        }, true));
        save.dataset.write = "preview";
        root.append(save, previewBox);
        if (record) {
            const toggle = button(record.metadata.enabled ? "Disable descriptor" : "Enable reviewed descriptor", () => work(async () => {
                if (dirty) throw new Error("Save or cancel edits before changing enablement.");
                const change = { operation: record.metadata.enabled ? "disable" : "enable", id: record.metadata.id,
                    expected_revision: record.revision, controller_revision: record.controller_revision, profile_revision: record.profile_revision, patch: {} };
                const preview = await api("preview", change);
                const confirm = button("Confirm descriptor change", () => work(async () => {
                        await showSaved(await api("commit", { change, preview_hash: preview.preview_hash }));
                        notice("Descriptor updated. No native workflow was enabled or executed.");
                    }));
                confirm.dataset.write = "toggle";
                previewBox.replaceChildren(node("p", "This changes only the scenario descriptor. The native master remains unchanged; execution still requires separate private approval.", "meta"), confirm);
            }));
            toggle.dataset.write = "toggle";
            if (!record.metadata.enabled && record.metadata.review_status !== "approved") {
                toggle.disabled = true; toggle.dataset.blocked = "true";
                root.append(node("p", "Enablement unavailable until this definition is reviewed. This manager cannot grant execution approval.", "meta"));
            }
            root.append(toggle, ui.disclosure("Provenance and review state", ui.facts(record.metadata)),
                node("p", "Last result: unavailable. Native binding, dependency readiness and private execution approval: unverified.", "meta"));
        }
        $("detail").replaceChildren(root);
        draftFields = { id, title, expression, timezone, sections, save };
        controls();
        for (const control of section.querySelectorAll("[data-blocked]")) control.disabled = true;
    }
    function select(id) {
        if (dirty) { if (selected?.metadata.id === id) { ui.openDetail(); return; } notice("Save or cancel the current draft before selecting another scenario.", true); return; }
        ui.openDetail();
        return work(async () => { selected = await api("show", { id }); stale = false; editor(selected); list(); ui.focus($("detail")); });
    }
    $("detail").tabIndex = -1;
    $("refresh").onclick = () => work(refresh);
    $("new").onclick = () => { if (!snapshot || dirty) return; selected = null; dirty = true; stale = false; ui.openDetail(); editor(null); };
    $("query").addEventListener("input", list); $("filter").addEventListener("change", list);
    work(refresh);
    return {};
};

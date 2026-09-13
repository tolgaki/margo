/* Presentation-only projections. Time never mutates a record or refreshes a source. */
(() => {
    "use strict";
    const MINUTE = 60_000;
    const NOW_WINDOW = 60 * MINUTE;
    const closed = new Set(["succeeded", "dismissed", "resolved", "rejected", "cancelled",
        "achieved", "carried_forward", "revoked", "superseded"]);
    const pastMeeting = new Set(["occurred", "recap_pending", "debrief_proposed", "reviewed", "carried_forward"]);
    const text = value => typeof value === "string" && value.trim() ? value : null;
    function instant(value) {
        if (typeof value !== "string" || !/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d+)?(?:Z|[+-]\d\d:\d\d)$/.test(value)) return null;
        const day = value.slice(0, 10);
        const normalized = new Date(`${day}T00:00:00Z`);
        if (!Number.isFinite(+normalized) || normalized.toISOString().slice(0, 10) !== day
            || Number(value.slice(11, 13)) > 23 || Number(value.slice(14, 16)) > 59 || Number(value.slice(17, 19)) > 59) return null;
        const stamp = Date.parse(value);
        return Number.isFinite(stamp) ? stamp : null;
    }
    function validZone(value) {
        if (!value) return null;
        try { return new Intl.DateTimeFormat("en-US", { timeZone: value }).resolvedOptions().timeZone; }
        catch { return null; }
    }
    function preferences(record = {}) {
        const value = text(record.value) || "";
        const zone = value.match(/\b(?:[A-Za-z_]+\/[A-Za-z_+-]+(?:\/[A-Za-z_+-]+)?|UTC)\b/)?.[0];
        const timezone = validZone(zone);
        const fields = value.split(";").map(part => part.trim());
        const days = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
        let workdays = null;
        const dayText = fields[1];
        if (dayText === "Mon-Fri") workdays = [1, 2, 3, 4, 5];
        else if (dayText && dayText.split(",").every(day => days.includes(day.trim()))) {
            workdays = [...new Set(dayText.split(",").map(day => days.indexOf(day.trim())))];
        }
        const hours = /^([01]\d|2[0-3]):([0-5]\d)-([01]\d|2[0-3]):([0-5]\d)$/.exec(fields[2] || "");
        const start = hours ? Number(hours[1]) * 60 + Number(hours[2]) : null;
        const end = hours ? Number(hours[3]) * 60 + Number(hours[4]) : null;
        const schedule = timezone && workdays?.length && start !== null && end > start
            ? { days: workdays, start, end } : null;
        return { timezone, schedule, recorded: !!value, raw: value,
            reason: !timezone ? "Account timezone not configured or not recognized; using device time for the clock only."
                : !schedule ? "Working days/hours not parsed; no availability assumed." : null };
    }
    function zoned(now, timezone) {
        const parts = new Intl.DateTimeFormat("en-US", {
            timeZone: timezone, year: "numeric", month: "2-digit", day: "2-digit",
            weekday: "short", hour: "2-digit", minute: "2-digit", hourCycle: "h23",
        }).formatToParts(now);
        const values = Object.fromEntries(parts.map(part => [part.type, part.value]));
        return { date: `${values.year}-${values.month}-${values.day}`, day: values.weekday,
            minutes: Number(values.hour) * 60 + Number(values.minute) };
    }
    function dayContext(now, record, deviceZone = Intl.DateTimeFormat().resolvedOptions().timeZone) {
        const pref = preferences(record);
        const timezone = pref.timezone || validZone(deviceZone) || "UTC";
        const local = zoned(now, timezone);
        const day = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"].indexOf(local.day);
        let context = `${local.day === "Sat" || local.day === "Sun" ? "Weekend. " : ""}Working hours unknown.`;
        if (pref.schedule) {
            context = !pref.schedule.days.includes(day) ? "Outside your configured working days."
                : local.minutes < pref.schedule.start ? "Before your configured working hours."
                : local.minutes >= pref.schedule.end ? "After your configured working hours."
                : "Within your configured working hours; not proof of availability.";
        }
        return { ...pref, displayZone: timezone, localDate: local.date, context,
            label: `${timezone}${pref.timezone ? " · account preference" : " · device clock (not account timezone)"}`,
            clock: new Intl.DateTimeFormat(undefined, { timeZone: timezone, hour: "2-digit", minute: "2-digit", second: "2-digit" }).format(now),
            date: new Intl.DateTimeFormat(undefined, { timeZone: timezone, weekday: "long", month: "long", day: "numeric" }).format(now) };
    }
    function relative(stamp, now) {
        const delta = stamp - now;
        const minutes = Math.max(1, Math.ceil(Math.abs(delta) / MINUTE));
        const value = minutes < 60 ? `${minutes} min` : minutes < 1440 ? `${Math.floor(minutes / 60)}h ${minutes % 60}m`
            : `${Math.floor(minutes / 1440)}d ${Math.floor(minutes % 1440 / 60)}h`;
        return delta === 0 ? "now" : delta > 0 ? `in ${value}` : `${value} ago`;
    }
    function timing(item, now, context, linked) {
        const data = item.data || {};
        const state = item.state || item.status;
        const meeting = item.kind === "meeting";
        const start = instant(data.scheduled_start), end = instant(data.scheduled_end);
        if (meeting) {
            if (state === "cancelled") return { label: "Cancelled occurrence; not upcoming", rank: 99, past: true };
            if (pastMeeting.has(state)) return { label: "Post-meeting record; not upcoming", rank: 5, past: true };
            if (start === null || end === null || end <= start) return { label: "Meeting timing invalid or missing", rank: 6, invalid: true };
            if (now >= end) return { label: `Meeting ended ${relative(end, now)}; attendance not inferred`, rank: 8, past: true };
            if (now >= start) return { label: `Scheduled now · ends ${relative(end, now)}`, rank: 2, at: end, live: true };
            return { label: `Starts ${relative(start, now)}`, rank: start - now <= NOW_WINDOW ? 2 : 6, at: start };
        }
        const due = data.due ?? linked?.data?.due;
        if (closed.has(state)) return { label: due ? `Recorded deadline: ${String(due)} (historical)` : "No active deadline", rank: 99 };
        if (!due) return { label: "No deadline recorded", rank: 7 };
        const at = instant(due);
        if (at !== null) return { label: at < now ? `Overdue · due ${relative(at, now)}` : `Due ${relative(at, now)}`,
            rank: at < now ? 1 : at - now <= NOW_WINDOW ? 3 : 6, at, overdue: at < now };
        if (/^\d{4}-\d\d-\d\d$/.test(due) && instant(`${due}T00:00:00Z`) !== null) {
            if (!context.timezone) return { label: `Due ${due} · date only; account timezone unknown`, rank: 6 };
            return { label: due < context.localDate ? `Overdue · due ${due} (date only)`
                : due === context.localDate ? "Due today · no time specified" : `Due ${due} · date only`,
            rank: due < context.localDate ? 1 : due === context.localDate ? 5 : 6, overdue: due < context.localDate };
        }
        return { label: `Deadline not interpreted: ${String(due)}`, rank: 7, invalid: true };
    }
    function project(items, now, context) {
        const byId = new Map(items.map(item => [item.id, item]));
        return items.map(item => {
            const data = item.data || {}, state = item.state || item.status;
            const linked = byId.get(item.work_item_id);
            const time = timing(item, now, context, linked);
            const until = instant(item.deferred_until || data.deferred_until);
            const unresolved = ["executing", "partial", "outcome_unknown"].includes(state);
            const terminal = closed.has(state);
            const snoozed = !terminal && !unresolved && state === "deferred" && until !== null && until > now;
            const changed = !terminal && (item.source_changed || item.stale);
            const candidate = item.type === "item" && item.confirmed !== true;
            const needsDecision = item.type === "action" || candidate
                || ["proposed", "debrief_proposed", "prepared"].includes(state);
            let rank = unresolved ? 0 : time.rank;
            if (rank > 3 && (changed || needsDecision || state === "blocked")) rank = 4;
            let lane = terminal || item.kind === "feedback" || (time.past && !["recap_pending", "debrief_proposed", "occurred"].includes(state))
                ? "History" : snoozed ? "Snoozed" : rank <= 3 ? "Now" : rank === 4 ? "Needs your decision" : "Next";
            if (lane === "History") rank = 99;
            if (lane === "Snoozed") rank = 90;
            const ask = text(data.next_step) || text(linked?.data?.next_step)
                || (item.type === "action" ? `Review this prepared ${item.kind || "action"} proposal`
                    : text(data.proposed_next_action) || text(data.title) || text(item.title) || "Inspect the recorded context");
            const blocker = text(data.blocker) || text(linked?.data?.blocker);
            const blocks = (item.relationships || []).filter(ref => ref.kind === "blocks" && ref.source_id === item.id)
                .map(ref => byId.get(ref.target_id)?.title || ref.target_id);
            const people = Array.isArray(item.affected_people) ? item.affected_people.filter(person => typeof person === "string") : [];
            const why = unresolved ? "An existing execution needs inspection/reconciliation. Never retry it blindly."
                : changed ? "Stored evidence changed. Revalidate before seeking any approval."
                : snoozed ? `Hidden until ${relative(until, now)}; the obligation remains open.`
                : state === "deferred" ? until === null ? "Deferral time missing or invalid; inspect it, do not silently hide it."
                    : "Snooze elapsed. This is a review reminder, not approval or a state transition."
                : time.rank <= 3 ? time.label
                : text(item.why_now) || text(item.why) || blocker || "Review the recorded next step; no timed urgency established.";
            const readiness = unresolved ? "Needs reconciliation" : terminal ? state === "succeeded"
                ? "Delivery receipt recorded; linked work may still be open" : "Historical record"
                : changed ? "Evidence changed" : state === "failed" ? "Failed; review before any retry"
                : candidate ? "Candidate ask · not a confirmed obligation"
                : state === "approved" && instant(item.expires_at) !== null && instant(item.expires_at) <= now ? "Approval expired · a new exact decision is required"
                : item.type === "action" ? "Prepared proposal · separate approval and fresh preflight required"
                : item.confirmed ? "Confirmed obligation · not completed" : state === "prepped" ? "Meeting preparation recorded" : "Stored context";
            return { item, time, lane, rank, ask, why, readiness, candidate, blocker, blocks, people,
                canMutate: item.type === "action" && !unresolved && state !== "succeeded",
                canPrepare: !terminal && !unresolved && !changed && item.kind !== "feedback"
                    && !(item.kind === "meeting" && time.past && !pastMeeting.has(state)),
                primaryIntent: item.type === "action" || candidate || changed || unresolved ? "review" : "prepare" };
        }).sort((a, b) => a.rank - b.rank || (a.time.at ?? Infinity) - (b.time.at ?? Infinity) || a.item.id.localeCompare(b.item.id));
    }
    function coverage(coverageRecord, now) {
        if (coverageRecord?.status !== "available") return { label: "Source coverage unavailable", sources: [],
            warning: coverageRecord?.reason || "No source collection history available." };
        const sources = (coverageRecord.sources || []).filter(source => !source.retired).map(source => {
            const last = instant(source.last_successful_coverage_at);
            const cadence = source.cadence_seconds;
            const known = Number.isFinite(cadence) && cadence > 0 && last !== null && last <= now;
            const fresh = known && now <= last + cadence * 1000;
            return { ...source, freshness: last !== null && last > now ? "Invalid future observation"
                : !known ? "Freshness unknown" : fresh ? "Within recorded cadence" : "Stale beyond recorded cadence",
            healthy: source.status === "complete" && fresh,
            lastLabel: last === null ? "No successful collection" : relative(last, now) };
        });
        return { sources, label: !sources.length ? "No active source coverage recorded"
            : `${sources.filter(source => source.healthy).length}/${sources.length} recorded source scopes complete and within cadence`,
        warning: "Only recorded scopes are represented. A live clock or ledger poll does not refresh Microsoft 365." };
    }
    function requestPhase(request, now) {
        if (!request) return null;
        if (["accepted", "dispatching", "working"].includes(request.phase)) {
            const deadline = instant(request.deadline_at), lease = instant(request.lease_expires);
            if (deadline !== null && deadline <= now) return "blocked";
            if (["working", "dispatching"].includes(request.phase) && lease !== null && lease <= now) return "interrupted";
        }
        return request.phase;
    }
    globalThis.MargoDecision = Object.freeze({ instant, preferences, dayContext, relative, timing, project, coverage, requestPhase, NOW_WINDOW });
})();

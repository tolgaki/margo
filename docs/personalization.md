# Personalization

Margo works unconfigured. She works *well* configured. This is the difference between a generic
summarizer and something that knows your 09:05 is non-negotiable and that a note to your skip gets
a different voice than a note to your team.

Two files do the work, and they sit next to `SKILL.md` in the installed skill:

| File | Holds | Written by |
|---|---|---|
| **`preferences.md`** | Who you are, who matters, how you work, how you write | You |
| **`commitments.md`** | What you owe, what you're waiting on | Margo, with your approval |

Both ship as templates. Both will contain real names and addresses once filled in — see
[what stays on your machine](safety.md#7-what-stays-on-your-machine).

---

## The four sections that earn their keep

`preferences.md` has ten sections. Four of them change Margo's behaviour immediately; the rest
sharpen it over time.

### 1. About me

Role, working hours, time zone, and — the important one — **focus blocks to protect**.

```markdown
- **Time zone & working hours:** 9:00–18:00, PT
- **Focus-time blocks to protect:** no meetings before 10am; heads-down 2–4pm
```

Protected blocks aren't advisory. Margo won't book over one without calling it out explicitly, and
"the only slot is inside your 2–4pm heads-down" becomes a stated trade-off rather than a silent
one.

### 2. People → VIPs

This is the single highest-leverage section, because it drives the **interrupt bar** for
proactive runs. A VIP with a direct ask breaks silence; everyone else waits for the next brief.

Record the management chain up from you, your directs, and anyone else who should always surface
fast. Names, addresses, titles.

> Org data goes stale. The template carries a **last-verified date** — when it's more than a
> quarter old, Margo offers to refresh the chains via `workiq-fetch` on `/users/{id}/manager` and
> `/users/{id}/directReports` and update the date.

Also worth filling: **auto-lower-priority senders** — newsletters, no-reply addresses, automated
systems.

### 3. Scheduling defaults → the priority ladder

Without this, every reschedule becomes a question. With it, Margo computes the whole cascade and
brings you a costed plan.

```markdown
| **Never move** | Your manager · external customers · anything where you're the named DRI |
| **Move freely** | Your 1:1s with directs · working sessions you organize · your own blocks |
| **Drop, don't move** | Large FYI meetings you're tentative on and not presenting at |
| **Ask before touching** | Anyone in a distant time zone — the daily overlap is an hour |
```

The categories matter more than the specific entries. **"Drop, don't move"** is the one people
forget to fill in and then miss: for a large meeting you're tentative on and not presenting at,
declining is far cheaper than reshuffling three other things around it.

The **"ask before touching"** row exists for a specific failure. If a colleague is twelve time
zones away, your working overlap might be one hour a day — and casually moving the meeting that
sits in it is expensive in a way that free/busy data doesn't show.

Also here: **default meeting times**. A common setting is starting everything at `:05` or `:35`
so there's buffer between back-to-backs, with 25- and 55-minute durations to preserve it. Margo
applies it to every meeting you organize and every slot she proposes.

### 4. Communication & drafting voice

Drafts are written as **you**, so this section is the difference between sending and rewriting.

Tone, length, do/don't — and most importantly the **sign-off, per channel**:

```markdown
- **Email** — initials only. No assistant attribution.
- **Teams** — no sign-off at all.
- **Calendar invites** — keep the assistant block.
```

That three-way split is deliberate, and the reasoning is worth borrowing:

- **Email:** you approved the message, so it's your word. An "AI Chief of Staff" line invites the
  reader to discount it.
- **Teams:** chat is conversational. A signature block on a two-line message reads as machine
  generated.
- **Invites:** logistics sent to people who didn't ask for them. Naming the assistant explains why
  it landed and who to reply to about timing.

---

## The rest

| Section | What it changes |
|---|---|
| **Priorities & projects** | Gives `ask` calls something to rank against |
| **Standing rules for triage** | *Always flag* becomes an extra interrupt criterion; *auto-deprioritize* becomes an absolute bar to interrupting |
| **Daily brief preferences** | When, how deep, always/never include |
| **Executive comms voice** | Only your *deviations* from the shared rubric in `references/exec-followup.md` |
| **Work tracking / communities** | Azure DevOps org and query IDs; Engage GroupId; Teams channel ID |

Note the exec-comms design: the rubric itself is shared and lives in the reference file; your
personal variations live here. That way the rubric stays improvable by everyone and your voice
stays yours.

---

## commitments.md — you don't write this one

It starts empty and fills as you approve sends. Three tables: **I owe**, **waiting on others**,
and a **log** of recently closed items.

Margo reads it on every brief, catch-up and EOD wrap-up, and proposes updates whenever an approved
send creates or resolves something. The brief's "waiting on" section is rendered from this file —
never from memory.

Two rules keep it worth reading:

- **Dates are absolute.** `2026-09-02`, never "next Friday".
- **Every row carries a source** — subject or meeting name, plus the `webLink` — so any item can
  be verified and reopened in one click.

And one that keeps it honest: **a row is added only when the commitment was really made**, in a
real message or meeting, or because you said so. Nothing is inferred.

Closed items move to the log rather than being deleted. Prune it around twenty entries, oldest
first.

---

## Tuning as you go

Margo will offer to capture preferences as she learns them — a correction you make twice is a
preference you haven't written down. Durable ones can also go to memory, but `preferences.md` is
the authoritative copy: it's the thing that survives a reinstall and the thing you can read.

The fastest way to improve output is to fix the file rather than re-explaining in chat. If a
brief surfaces something you never care about, that's a missing *auto-deprioritize* row.

---

## Changing the persona entirely

The voice is **not** in these files. It's in `agents/margo.agent.md`, which is deliberately
separable — the skills carry no personality and inherit whatever agent loads them.

See **[Build your own](build-your-own.md)**.

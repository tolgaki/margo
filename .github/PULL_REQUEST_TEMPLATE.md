## What changed

<!-- One or two sentences. What does this do that wasn't happening before? -->

## Why

<!-- The problem this solves. For a new rule, say what goes wrong without it. -->

## Before / after

<!--
For behavioural changes, show sanitized output from before and after. This is the most useful
thing in the PR.
-->

---

## Checklist

**Data hygiene — required**

- [ ] **No real workplace data.** No colleague names or email addresses, no tenant / org / team /
      channel / group / query GUIDs, no message subjects, senders, quotes or links, no customer or
      partner names, no internal codenames, nothing from `skills/*/state/`.
- [ ] All examples use invented names and `example.com` addresses.
- [ ] Installation-specific values are `{placeholders}`.

**Structure**

- [ ] Skill changes carry **no persona or tone** — voice stays in `agents/`.
- [ ] Any new rule is paired with the failure it prevents.
- [ ] Docs in `docs/` still match the behaviour, and the router table in `SKILL.md` is current.
- [ ] Internal links resolve.

**Safety**

- [ ] Doesn't widen what can happen without explicit approval of that specific action.
- [ ] Doesn't introduce **outbound** actions (send, reply, post, react, RSVP, delete, work-item
      change) into unattended scheduled runs. Local or vault file writes are allowed where a
      skill documents them — see `docs/safety.md` §3.

**Checks**

- [ ] `python3 -m py_compile skills/chief-of-staff/scripts/*.py` passes (if scripts changed).
- [ ] Ran the affected routine against a real account and confirmed the output is grounded and
      cited (if behaviour changed).

## Related issues

<!-- Closes #… -->

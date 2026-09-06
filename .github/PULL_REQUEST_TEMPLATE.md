## What changed

<!-- One or two sentences. What does this do that wasn't happening before? -->

## Why

<!-- The problem this solves. For a new rule, say what goes wrong without it. -->

## Before / after

<!--
For behavioural changes, show sanitized output from before and after. This is the most useful
thing in the PR.
-->

## User workflow and documentation

<!-- Feature IDs, guide links, availability, prerequisites and recovery behavior. -->

## Scope and compatibility

<!-- Owned surfaces, dependencies, non-goals, schema/install effects and integration owner. -->

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
- [ ] Feature catalog and mapped user scenarios include the change and identify their evidence scope.
- [ ] Internal links resolve.

**Safety**

- [ ] Doesn't widen what can happen without explicit approval of that specific action.
- [ ] Doesn't introduce **outbound** actions (send, reply, post, react, RSVP, delete, work-item
      change) into unattended scheduled runs. Only documented private local preparation is
      allowed; this repository does not grant unattended shared-vault writes.

**Checks**

- [ ] `python3 -m py_compile skills/chief-of-staff/scripts/*.py` passes (if scripts changed).
- [ ] Ran the affected synthetic scenarios and relevant existing integration checks.
- [ ] Distinguished procedure contracts from actual model-behavior evidence; missing traces are not passes.
- [ ] Any live read-only exercise was separately authorized, bounded and sanitized. No live write was used as a test.

## Related issues

<!-- Closes #… -->

# Margo — terms of use and privacy

**Publisher:** Tolga Kilicli · **Source:** [github.com/tolgaki/margo](https://github.com/tolgaki/margo)
· **Last updated:** 17 October 2026

These terms cover the Margo autopilot blueprint and the instances an organisation creates from
it. The Margo source code is separately licensed under the [MIT License](../LICENSE); nothing
here narrows that licence.

## What Margo is

Margo is an AI chief-of-staff agent. An instance works for one person, its manager in
Microsoft 365. It reads that person's work through Work IQ, prepares briefs, triage and drafts,
and proposes actions. It is an AI, not a person, and it says so when asked.

## What Margo will and will not do

- **Proposes, never acts unilaterally.** Margo does not send, reply, post, accept or decline
  meetings, delete, or change work items without the manager's explicit approval of that exact
  action. Approving one action does not approve the next.
- **One standing exception.** Margo may message its manager in their one-to-one chat at any
  time, including on a schedule. It messages no one else on its own initiative.
- **Takes instructions only from its manager.** Messages from anyone else are treated as
  information for the manager, not instructions. Whether Margo replies to other people is a
  setting the manager controls; by default it does not.
- **Mail.** With the manager's approval of each message, Margo can send mail on the manager's
  behalf or from its own mailbox.

## Your responsibilities

- Review what Margo proposes before you approve it. You remain responsible for messages sent
  and decisions made with your approval.
- AI output can be wrong, incomplete or out of date. Check facts that matter, especially
  dates, figures, commitments and anything sent to other people.
- Use Margo only in line with your organisation's policies and the terms of the Microsoft
  services it runs on.

## Privacy

- **Where your data goes.** An instance runs in the Azure subscription and Microsoft 365 tenant
  of the organisation that deploys it. Its memory, work records and approvals are stored in
  that organisation's resources. The publisher does not receive your content, prompts or
  results, and the blueprint sends no telemetry to the publisher.
- **What Margo reads.** Only what its manager authorises, through Work IQ, scoped to that
  manager's own access. It cannot reach data its manager cannot see.
- **What Margo remembers.** Facts, preferences and past events relevant to helping its
  manager, each recorded with its source, plus its own working experience. The manager can
  ask what Margo knows, correct it, or have it forget something. Retiring an instance deletes
  its stored state; messages already sent and backups follow the organisation's own retention.
- **Operational logs.** The deploying organisation's monitoring records which operations ran
  and their outcomes. It is configured not to record message content.
- **Microsoft services.** Microsoft processes data under the deploying organisation's agreements
  with Microsoft, not under these terms.

## No warranty

Margo is provided "as is", without warranty of any kind, as set out in the MIT License. To the
extent permitted by law, the publisher is not liable for loss or damage arising from its use,
including actions taken with your approval.

## Changes

These terms may change with new versions of the blueprint. The version in this repository is
the current one; its history is the change log.

## Contact

Questions and problems: [GitHub issues](https://github.com/tolgaki/margo/issues). Security
issues: follow [SECURITY.md](../SECURITY.md), not a public issue.

# Margo — terms of use and privacy

**Publisher:** Tolga Kilicli · **Source:** [github.com/tolgaki/margo](https://github.com/tolgaki/margo)
· **Last updated:** 23 November 2026

An organisation that deploys Margo publishes these terms to its own users: replace the publisher and
contact details with your own, and check each statement against how you deployed it (retention
periods, model region, whether voice is on).

These terms cover the Margo Cloud agent blueprint and the instances an organisation creates from
it. The Margo source code is separately licensed under the [MIT License](../LICENSE); nothing
here narrows that licence.

## What Margo is

Margo is an AI chief-of-staff agent. An instance works for one person, its manager in
Microsoft 365. It reads that person's work through Work IQ, prepares briefs, triage and drafts,
and proposes actions. It is an AI, not a person, and it says so when asked.

## What this version does and does not do

- **Acts only on the manager's tap.** Margo reads, briefs, triages and drafts. It sends email and
  replies (from the manager's mailbox, as Margo on their behalf), sets up, moves or cancels meetings
  on the manager's calendar, and posts Teams messages as itself, only after the manager taps Approve
  on a card in Teams showing that exact action. Each approval covers that one action, once. A "yes"
  in chat or by voice is not an approval. Everything else outward is refused in software.
- **Takes instructions only from its manager**, and only in their one-to-one chat. Messages and
  mail from anyone else are kept as requests for the manager to review, not acted on.
- **Two fixed notices are sent without per-message approval.** In a group chat or channel Margo
  answers with one fixed note that she works only one-to-one. If the manager turns it on
  (`/replies acknowledge`), a colleague who writes to Margo gets one fixed "passed this on" note per
  conversation. Neither contains the manager's data. By default colleagues get no reply.
- **Does not message first.** This version answers; it does not run on a schedule or start
  conversations.

## Your responsibilities

- Read each card before you approve it. You remain responsible for what is sent and decided with
  your approval.
- AI output can be wrong, incomplete or out of date. Check facts that matter, especially
  dates, figures, commitments and anything sent to other people.
- Use Margo only in line with your organisation's policies and the terms of the Microsoft
  services it runs on.

## Privacy

- **Where your data goes.** An instance runs in the Azure subscription and Microsoft 365 tenant
  of the organisation that deploys it. Its memory and work records are stored in that
  organisation's storage account. The publisher of the source code does not receive your content,
  prompts or results, and the blueprint sends no telemetry to the publisher.
- **What Margo reads.** In delegation mode: her manager's mail and calendar, through the access
  the manager or an administrator delegated to her, and whatever is shared with Margo's own
  account (mail sent to her, chats and channels she is in, files and sites shared with her). She
  reads through Work IQ, and only for her manager's requests.
- **The AI model.** What Margo reads for a request, and the conversation so far, is sent to the
  organisation's Azure AI model deployment to produce the answer. Margo asks the service not to
  store it. Microsoft's own abuse monitoring may retain prompts for a limited time unless the
  organisation has an exemption, and with the default deployment type the request may be processed
  outside the organisation's chosen region. The conversation is kept only in the running instance's
  memory, for up to 30 minutes without use, and is lost when the instance restarts.
- **What Margo remembers.** Facts, preferences and past events relevant to helping her manager,
  each recorded with its source, plus her own working notes. The manager can ask what Margo knows,
  correct it, or have her forget something. Forgetting removes it from her current records at
  once; earlier copies of those records (storage versions and daily copies) are deleted after the
  organisation's retention period, 30 days unless it chose otherwise.
- **Messages from other people.** When someone other than the manager writes to Margo, she keeps
  the sender's directory ID and display name, the conversation, and a short excerpt, for the
  manager to review. These records have no automatic expiry. Senders are not told unless the
  manager turned the acknowledgement on.
- **When an instance is retired or the manager changes.** The instance stops and its identity is
  removed. Its stored records are not deleted automatically: the organisation's administrators
  delete them. A new manager never sees a previous manager's records.
- **Who else can read it.** Administrators of the deploying organisation with data access to the
  storage account can read every manager's stored records.
- **Operational logs.** The organisation's monitoring records which operations ran, on what kind
  of resource, and their outcome. It is configured not to record message content; the resources Margo read
  are logged by kind, without names, addresses or search text. Logs are kept 30 days unless the organisation chose otherwise.
- **Voice, if turned on.** The manager's speech is sent to Azure Voice Live for the session.
  Margo stores no audio or transcript.
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

# Margo documentation

## Choose your path

Margo is a Work IQ reference implementation: an AI chief of staff for Microsoft 365, built to show
how an agent reads and writes Microsoft 365 through Work IQ. It comes in two forms that share the
persona and skills. **Margo desktop** runs in GitHub Copilot CLI on your machine; most of this
documentation describes it. **Margo Cloud** runs in your own Azure subscription as an Agent 365 agent
with her own identity ([overview](margo-cloud.md)). Nobody runs either one for you.

You do not need to understand the Python core to use Margo, or connect a real mailbox to contribute.

| Start here | Your destination |
| --- | --- |
| **[User journey](user-guide.md)** | Install, get a useful first brief, prepare a reply, carry work through the week, and add optional capabilities deliberately |
| **[Developer journey](development/README.md)** | Explore safely, follow a feature through the implementation, make a bounded change, and prepare a contribution |
| **[Feature reference](features.md)** | Find every listed capability, its availability, guide, implementation kind, and limitations |
| **[How-to guides](how-to/README.md)** | Complete a specific task without reading the entire manual |
| **[Learn Work IQ](work-iq/README.md)** | Load Work IQ in a harness or a custom agent; identity, capabilities, patterns, errors, and the approval pattern for writes |
| **[Margo Cloud overview](margo-cloud.md)** | Decide whether to deploy Margo into your tenant: architecture, who sees what, costs, maturity |

## Follow the user journey

Read in order the first time, then return to the task guides as needed.

| Stage | Read | What you should be able to do afterward |
| --- | --- | --- |
| Understand the boundaries | [Trust and safety](safety.md) | Distinguish a recommendation, a local draft, an exact approval, and an actual external result |
| Get connected | [Getting started](getting-started.md) | Install a private copy, confirm Work IQ identity, configure local storage, and request a first brief |
| Make it yours | [Personalization](personalization.md) | Set working hours, priorities, important people, and your drafting voice |
| Run a day | [User journey](user-guide.md#2-get-your-first-useful-brief) and [walkthroughs](walkthroughs.md) | Move from a cited brief to meeting preparation and a reviewed reply |
| Carry work forward | [Closed-loop productivity](closed-loop.md) | Keep obligations, action proposals, and results connected without treating one as another |
| Find a particular feature | [How-to index](how-to/README.md) | Use calendar, files, meetings, work tracking, decisions, memory, and recovery guides |
| Add a routine | [Proactive and scheduled](proactive.md) | Choose a schedule path, understand its permission limits, and inspect coverage and delivery |
| Maintain your installation | [Setup and migration](how-to/setup-and-migration.md) | Update code separately from private-state migration, workflow sync, and session reload |

Common questions (does my data leave my machine, what can Margo do without asking, how do I
uninstall) are answered in the [FAQ](faq.md).

If something stopped working, start with [automation and health](how-to/automation-health.md)
or [task progress and recovery](how-to/task-progress-and-recovery.md). A missing source is a gap,
not an empty inbox; an uncertain send is not a reason to press send again.

## Follow the developer journey

The [developer guide](development/README.md) supplies the ordered path. These references answer
the deeper questions along the way.

| Question | Reference |
| --- | --- |
| What owns each part of the system? | [Architecture and state ownership](development/architecture.md) |
| How do I change one feature completely? | [Agent-owned change workflow](development/agent-workflow.md) |
| How do I adapt the persona or procedures? | [Build your own](build-your-own.md) |
| Which procedure handles a user request? | [Chief-of-staff playbook](chief-of-staff.md) and the [live skill router](../skills/chief-of-staff/SKILL.md#core-routines) |
| How do reads, synthesis, discovery, and writes use Work IQ? | [Learn Work IQ](work-iq/README.md), then [patterns](work-iq/patterns.md) and the [capability map](work-iq/capability-map.md) |
| What is deterministic, and what still needs model evidence? | [Journey fixtures](../tests/fixtures/journeys/README.md) and [evaluation guide](../evals/README.md) |
| How does the optional UI use the core? | [Canvas extension](../.github/extensions/margo-action-desk/README.md) |
| What ships in an install or package? | [Contributing](../CONTRIBUTING.md) and [packaging](../packaging/README.md) |
| How are schedules authored? | [Automation manifests](../automations/README.md) |
| How does the local embedding adapter work? | [Embedding model](embedding-model.md) |

Coding agents start at [AGENTS.md](../AGENTS.md). Human contributors should also read the
[contribution rules](../CONTRIBUTING.md): fictional fixtures only, no real account state in the
checkout, and no implicit expansion of outward-action permissions.

## Know what is available

The [full feature index](features.md#full-feature-index) separates four availability labels:

| Label | Meaning for you |
| --- | --- |
| Implemented | The code or procedure is present; its host, account, and source prerequisites still apply |
| Optional | Additional setup, an integration, or an explicit opt-in is needed |
| Limited | The feature is present but has a documented capability or permission limitation |
| Planned | A design, not a usable feature |

An implementation kind of **runtime** means deterministic support exists; it does not mean a
model is no longer needed to author a draft or choose the right evidence. **Procedure** means
the host's agent follows the skill. Neither label is a promise of live tenant access.

This documentation describes the checked-out source. Compare [VERSION](../VERSION) and the
[changelog](../CHANGELOG.md) with your installed version; an unreleased checkout feature may not
be in the package you downloaded.

## Margo Cloud and deployment references

Read these after the current implementation, not as extra setup requirements for desktop Margo.

| Document | How to read it |
| --- | --- |
| [Margo Cloud overview](margo-cloud.md) | Start here: what it is, architecture, who sees what, licenses and costs, maturity, glossary |
| [Deploy Margo Cloud](autopilot-deployment.md) | Step-by-step, self-hosted deployment to a test tenant, operation, troubleshooting and known limits |
| [One container per hired Margo](autopilot-instance-containers.md) | Per-instance isolation, router, provisioning, scoped identity; what is built and what is only the target |
| [Talk to Margo by voice](autopilot-voice.md) | Optional Margo Cloud feature: talk to your instance from a browser page (not yet tested against a live Voice Live resource) |
| [Margo Cloud design and decision record](autopilot.md) | Why Margo Cloud is shaped as it is: identity, guard, memory, threat model, decisions |
| [Margo Cloud code](../cloud/README.md) | Build, test and publish the C# projects |
| [Running in a container](container.md) | Run desktop Margo's scheduled briefs in a container, with separate Copilot and Work IQ sign-ins; not a ready-made security sandbox |

## Glossary

| Term | Meaning |
| --- | --- |
| Action desk | The list of actions Margo has proposed, each with its exact content, waiting for your decision. In the conversation, or as an optional panel |
| Anchor | A scheduled run at a fixed time (morning brief, end-of-day wrap-up) that makes a brief available, as opposed to a frequent sweep ([proactive](proactive.md)) |
| Candidate | Something Margo suspects (a commitment, a memory) but you have not confirmed. Candidates never count as confirmed work |
| Canvas, panels | Optional review screens in the Copilot app, served by the [action-desk extension](../.github/extensions/margo-action-desk/README.md). The CLI does everything without them |
| Copilot app | The desktop app that hosts Copilot agents, extension canvases and scheduled workflows; distinct from the Copilot CLI terminal |
| Coverage | Which sources a run actually read, and over which window. A missing source is a gap, not an empty inbox |
| Dream | Opt-in, manual reflection over selected Margo sessions into sourced episodes and reviewable ideas ([Dream](how-to/dream.md)) |
| Ledger | Margo's private local database of work items, proposed actions, approvals and results ([closed loop](closed-loop.md)) |
| Margo Cloud terms | Agent 365, blueprint, agent user, delegation, on-behalf-of: see the [Margo Cloud glossary](margo-cloud.md#glossary) |

## Help and documentation maintenance

For a normal bug, use the [issue templates](https://github.com/tolgaki/margo/issues/new/choose)
and describe a fictional or sanitized reproduction. Report sensitive issues through the
[security policy](../SECURITY.md), not a public transcript. The
[code of conduct](../CODE_OF_CONDUCT.md) applies to all contributions.

Feature inventory and generated navigation come from [feature-catalog.json](feature-catalog.json).
Schedule tables come from [automation manifests](../automations/README.md). Edit those sources
when the inventory or schedule changes; keep explanations and examples in the authored guides.
The [developer journey](development/README.md) explains the existing documentation checks.

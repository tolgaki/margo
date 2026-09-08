# Margo documentation

Margo is a Work IQ reference implementation that you can run in a supported Copilot host.
It combines an assistant persona, procedural skills, private local state, and optional review
panels. It is not a hosted service: you supply the host, connected accounts, and permissions.

Choose the journey that matches what you want to do. You do not need to understand the Python
core to use Margo, or connect a real mailbox to contribute.

| Start here | Your destination |
| --- | --- |
| **[User journey](user-guide.md)** | Install, get a useful first brief, prepare a reply, carry work through the week, and add optional capabilities deliberately |
| **[Developer journey](development/README.md)** | Explore safely, follow a feature through the implementation, make a bounded change, and prepare a contribution |
| **[Feature reference](features.md)** | Find every catalogued capability, its availability, guide, implementation kind, and limitations |
| **[How-to guides](how-to/README.md)** | Complete a specific task without reading the entire manual |

## Follow the user journey

Read in order the first time, then return to the task guides as needed.

| Stage | Read | What you should be able to do afterwards |
| --- | --- | --- |
| Understand the boundaries | [Trust and safety](safety.md) | Distinguish a recommendation, a local draft, an exact approval, and an actual external result |
| Get connected | [Getting started](getting-started.md) | Install a private copy, confirm Work IQ identity, configure local storage, and request a first brief |
| Make it yours | [Personalization](personalization.md) | Set working hours, priorities, important people, and your drafting voice |
| Run a day | [User journey](user-guide.md#2-get-your-first-useful-brief) and [walkthroughs](walkthroughs.md) | Move from a cited brief to meeting preparation and a reviewed reply |
| Carry work forward | [Closed-loop productivity](closed-loop.md) | Keep obligations, action proposals, and results connected without treating one as another |
| Find a particular feature | [How-to index](how-to/README.md) | Use calendar, files, meetings, work tracking, decisions, memory, and recovery guides |
| Add a routine | [Proactive and scheduled](proactive.md) | Choose a schedule path, understand its permission limits, and inspect coverage and delivery |
| Maintain your installation | [Setup and migration](how-to/setup-and-migration.md) | Update code separately from private-state migration, workflow sync, and session reload |

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
| How do reads, synthesis, discovery, and writes use Work IQ? | [Work IQ integration](work-iq.md) |
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

## Design notes and deployment references

Read these after the current implementation, not as extra setup requirements.

| Document | How to read it |
| --- | --- |
| [Running in a container](container.md) | Optional deployment guidance, including separate Copilot and Work IQ sign-ins; not a ready-made security sandbox |
| [Margo as an autopilot](autopilot.md) | Design exploration of an agent-owned identity, not a shipped sign-in or unattended-send mode |
| [Agentic development plan](agentic-development-plan.md) | Delivery strategy and sequencing; use the current architecture, catalog, and workflow for implementation facts |

## Help and documentation maintenance

For a normal bug, use the [issue templates](https://github.com/tolgaki/margo/issues/new/choose)
and describe a fictional or sanitized reproduction. Report sensitive issues through the
[security policy](../SECURITY.md), not a public transcript. The
[code of conduct](../CODE_OF_CONDUCT.md) applies to all contributions.

Feature inventory and generated navigation come from [feature-catalog.json](feature-catalog.json).
Schedule tables come from [automation manifests](../automations/README.md). Edit those sources
when the inventory or schedule changes; keep explanations and examples in the authored guides.
The [developer journey](development/README.md) explains the existing documentation checks.

---
mode: agent
description: Change Margo Cloud (C#) behavior, such as what she reports to her manager or what the guard allows
---
Make the Margo Cloud change described below. Read `AGENTS.md`, `cloud/README.md` (Conventions) and the `PORTING.md` of
each project you touch first.

- Warnings are errors; package versions are central (`cloud/Directory.Packages.props`).
- Never edit recorded Python answers or frozen fixtures to make a test pass. If one fails, your change altered
  behavior: say so, and change it only as a deliberate, documented deviation in that project's `PORTING.md`.
- The guard (`cloud/src/Margo.Cloud.Guard/`) is the safety boundary: never widen what reaches anyone but the manager
  without the user's explicit decision.
- Where things live: the manager channel and its limits `cloud/src/Margo.Cloud.Orchestrator/ManagerChannel.cs`; the
  observation and listening prompts `ObservationText` and `RoomsText` in `cloud/src/Margo.Cloud.Orchestrator/Orchestrator.cs`;
  what listening reads `cloud/src/Margo.Cloud.Orchestrator/Rooms.cs`; the listening timer
  `cloud/src/Margo.Cloud.Host/RoomListener.cs`; settings `cloud/src/Margo.Cloud.Host/HostSettings.cs` and `cloud/infra/main.bicep`.
- Run `(cd cloud && dotnet build Margo.Cloud.slnx && dotnet test --solution Margo.Cloud.slnx)` and update the
  deployment guide (`docs/autopilot-deployment.md`) for anything an operator sees.

The change:

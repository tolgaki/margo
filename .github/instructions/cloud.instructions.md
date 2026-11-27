---
applyTo: "cloud/**"
---
# Margo Cloud (C#)

Follow `AGENTS.md` and `cloud/README.md` (Conventions). In short:

- .NET 10, warnings are errors, central package versions, one owner per project; record mappings and deliberate
  deviations in that project's `PORTING.md`.
- The C# state commands (`margo-state`) must stay byte-identical to the Python helpers in
  `skills/chief-of-staff/scripts/`; `python3 tools/state_parity.py --check` (CPython 3.13) judges it.
- Recorded Python answers and frozen fixtures are baselines: never edit them to make a test pass.
- Work IQ is loaded with the official MCP C# SDK (`Margo.Cloud.WorkIQ`); keep that client idiomatic, with no
  Python-compatibility code.
- Every Work IQ call goes through `WorkIQGuard`; writes need the manager's approval, except what reaches the manager
  alone (the manager channel).
- Check with `dotnet build Margo.Cloud.slnx` and `dotnet test --solution Margo.Cloud.slnx` from `cloud/`.

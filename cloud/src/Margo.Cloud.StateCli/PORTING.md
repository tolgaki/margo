# Margo.Cloud.StateCli (`margo-state`): porting notes

`margo-state SCRIPT.py ARGS...` is the C# stand-in for `python3 SCRIPT.py ARGS...` from
`skills/chief-of-staff/scripts/`, as `autopilot/src/margo_autopilot/statecmd.py` invokes the scripts
(the hosted engine's port of `statecmd` passes `--account`/`--state-dir` and stdin the same way).

## What maps to what

| Python | C# |
| --- | --- |
| `python3 -s scripts/SCRIPT.py ARGS` | `margo-state SCRIPT.py ARGS` (`StateCliHost.Run`) |
| the script file | an `IStateScript` with `ScriptName == "SCRIPT.py"` in any `Margo.Cloud.State.*` assembly next to the program |
| `sys.stdin` (UTF-8 under `LC_ALL=C.UTF-8`, no BOM stripping) | `PyIO.StdinReader` (strict UTF-8) |
| `sys.stdout`/`sys.stderr` (UTF-8, `\n`) | `PyIO.OutputWriter`, flushed on exit |
| `sys.exit(status)`, argparse exit 2, uncaught exception exit 1 | `StateScripts.RunGuarded` |

Discovery loads every `Margo.Cloud.State.*.dll` in the program folder (except `*.Tests.dll`, which a
test host places there) and instantiates each concrete `IStateScript`. Two implementations of one
script name fail at start-up. A new port adds its class in its own project; this project only needs
the existing project reference.

A path such as `scripts/work_state.py` is accepted and matched by file name.

## Deviations

- `margo-state` itself has no Python counterpart: with no arguments it prints a usage error and exits
  2; an unknown script prints `margo-state: error: unknown script '...' (choose from ...)` and exits 2;
  `-h`/`--help` lists the scripts. Everything after the script name belongs to the script.

## Not ported / open questions

None. Per-script behaviour lives in the State.* projects; see
`cloud/src/Margo.Cloud.State.Core/PORTING.md` for the shared helpers and the judge.

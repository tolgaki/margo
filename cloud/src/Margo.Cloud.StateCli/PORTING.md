# Margo.Cloud.StateCli (`margo-state`): porting notes

`margo-state SCRIPT.py ARGS...` is the C# stand-in for `python3 SCRIPT.py ARGS...` from
`skills/chief-of-staff/scripts/`, as `statecmd.py` (Python hosted runtime, removed at the cutover) invokes the scripts
(the hosted engine's port of `statecmd` passes `--account`/`--state-dir` and stdin the same way).

## What maps to what

| Python | C# |
| --- | --- |
| `python3 -s scripts/SCRIPT.py ARGS` | `margo-state SCRIPT.py ARGS` (`StateCliHost.Run`) |
| the script file | an `IStateScript` with `ScriptName == "SCRIPT.py"` in any `Margo.Cloud.State.*` assembly next to the program |
| `sys.stdin` (UTF-8 under `LC_ALL=C.UTF-8`, no BOM stripping) | `PyIO.StdinReader` (strict UTF-8, `TextIOWrapper` chunking) |
| `sys.stdout`/`sys.stderr` (UTF-8, `\n`; stderr `backslashreplace`) | `PyIO.OutputWriter` / `PyIO.ErrorWriter`, flushed on exit |
| `sys.argv` (UTF-8 with `surrogateescape`) | `PyRuntime.CommandLineArguments(args)`: re-read from `/proc/self/cmdline`, because .NET replaces undecodable bytes with U+FFFD |
| CPython ignoring SIGXFSZ | `PosixNative.IgnoreFileSizeLimitSignal()` at start-up, so writes past `RLIMIT_FSIZE` fail with `EFBIG` |
| `sys.exit(status)`, argparse exit 2, uncaught exception exit 1 | `StateScripts.RunGuarded` |

Discovery loads every `Margo.Cloud.State.*.dll` in the program folder (except `*.Tests.dll`, which a
test host places there) and instantiates each concrete `IStateScript`. Two implementations of one
script name fail at start-up. A new port adds its class in its own project; this project only needs
the existing project reference.

### Contract probes

The probe twins (`*_probe.py`, `IStateProbeScript`) live in `Margo.Cloud.State.Probes`, which this project
references only when the MSBuild property `MargoStateProbes` is `true`: the Debug configuration sets it by default,
Release does not. Even when present, `margo-state` refuses a probe unless the environment sets
`MARGO_STATE_PROBES=1` (`tools/state_parity.py` does): it prints the usage line and `margo-state: error: 'NAME' is
a contract probe, not a Margo command (set MARGO_STATE_PROBES=1 to run it for the parity judge)` and exits 2, and
`--help` and the unknown-script list leave probes out. See `cloud/src/Margo.Cloud.State.Probes/PORTING.md`.

A path such as `scripts/work_state.py` is accepted and matched by file name.

## Deviations

- `margo-state` itself has no Python counterpart: with no arguments it prints a usage error and exits
  2; an unknown script prints `margo-state: error: unknown script '...' (choose from ...)` and exits 2;
  a contract probe without `MARGO_STATE_PROBES=1` exits 2 the same way; `-h`/`--help` lists the scripts.
  Everything after the script name belongs to the script.
- stdin is `PyIO.StdinReader`, a model of CPython's `TextIOWrapper` with a strict UTF-8 incremental decoder:
  `sys.stdin.read()` decodes all remaining bytes at once, so an undecodable byte is reported with CPython's reason
  and absolute position.

## Not ported / open questions

None. Per-script behaviour lives in the State.* projects; see
`cloud/src/Margo.Cloud.State.Core/PORTING.md` for the shared helpers and the judge.

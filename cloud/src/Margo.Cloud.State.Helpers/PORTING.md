# Margo.Cloud.State.Helpers: porting notes

Ports the three stateless helpers in `skills/chief-of-staff/scripts/`:

- `capacity.py` (capacity from explicit, timezone-aware intervals),
- `teams_feedback.py` (Teams channel messages saved from `workiq-fetch` into a triage view),
- `engage_parse.py` (Viva Engage hits from `workiq-retrieve` markdown).

They touch no Margo state: no `--account`/`--state-dir` (the judge's `STATEFUL` set and
`statecmd.STATEFUL` leave them out, and `statecmd.COMMANDS` classifies them as `READ: {None}`, a
script without subcommands). Reference behaviour is CPython 3.13 (measured on 3.13.16), as for
`Margo.Cloud.State.Core`.

No other script imports these modules, so the public API below exists for the hosted engine and
later ports rather than for an importer.

## What maps to what

### capacity.py (`Capacity`, `CapacityScript`)

| Python | C# |
| --- | --- |
| module docstring | `Capacity.Doc` |
| `instant(value)` | `Capacity.Instant(object?)` (aware UTC `PyDateTime`) |
| `intervals(values, label)` | `Capacity.Intervals(object?, string)` |
| `merge`, `intersection`, `subtract` | `Capacity.Merge`, `Intersection`, `Subtract` over `CapacityInterval` |
| `(end - start).total_seconds()` | `Capacity.TotalSeconds` (microseconds / 10**6, correctly rounded) |
| `minutes(values)` | `Capacity.Minutes` (`sum()` semantics, see below) |
| `number(value, label, positive)` | `Capacity.Number` (returns `BigInteger` or `double`) |
| `serialise(values)` | `Capacity.Serialise` |
| `calculate(data)` | `Capacity.Calculate(JsonNode?)` returns the report `JsonObject` |
| `main()` | `CapacityScript` (`margo-state capacity.py --input FILE|-`) |

### teams_feedback.py (`TeamsFeedback`, `TeamsFeedbackScript`)

| Python | C# |
| --- | --- |
| `clean(body)` | `TeamsFeedback.Clean(object?)` |
| `iter_messages(paths, problems)` | `TeamsFeedback.IterMessages` (lazy, like the generator) |
| `collect(paths, problems)` | `TeamsFeedback.Collect` returns a `PyDict<object?>` keyed like a Python dict |
| `build(paths, reply_paths, problems)` | `TeamsFeedback.Build` returns `TeamsFeedbackRow`s (`ToPython()` is the JSON row) |
| `main()` | `TeamsFeedbackScript` (table and `--format json`, exit 1 when any page yielded no data) |

### engage_parse.py (`EngageParse`, `EngageParseScript`)

| Python | C# |
| --- | --- |
| `FOOTNOTE.findall` | `EngageParse.Footnotes` (`EngageFootnote` records) |
| `SOURCE_JSON.findall` | `EngageParse.SourceBlobs` |
| `unescape(raw)` | `EngageParse.Unescape` |
| `salvage(raw)` (`FIELD`, `BODY`) | `EngageParse.Salvage` returns a `JsonObject` with the same keys in the same order |
| `_encoded_key(value)` | `EngageParse.EncodedKey(object?)` |
| the `/threads/` `re.search` in `load_hits` | `EngageParse.ThreadId` |
| `_daygap(created, updated)` | `EngageParse.DayGap` |
| `load_hits(path)` | `EngageParse.LoadHits` returns `EngageRow`s (`ToPython()` is the JSON row) |
| `main()` | `EngageParseScript` |

### Python semantics (`Python/`)

The scripts apply Python's dynamic operations to untrusted JSON (`msg.get(...)`, `x or ''`,
`x[:10]`, `len`, `in`, `int()`, `<` between whatever types arrived, dict keys of any hashable
type). A wrong type does not fail the same way in C#, so these helpers reproduce CPython 3.13 and
raise the same exception class. All of them belong in `Margo.Cloud.State.Core` (see "Requests for
State.Core"); they live here so this port does not edit another project.

| Python | C# | Notes |
| --- | --- | --- |
| `x.get`, truthiness, `x[:n]`, `x['k']`, `len`, `in`, iteration, `==`, `!=`, `<`, `<=`, `>`, `>=`, tuple `<`, `str`, `repr`, `int()` on JSON values | `PyObj` | `AttributeError`, `TypeError`, `KeyError(slice(...))`, `ValueError`, `OverflowError` as CPython raises them; list comparison by first unequal item; exact int/float comparison |
| dict keys (`hash` + `==`) and an insertion-ordered dict | `PyKey`, `PyDict<T>` | `True == 1 == 1.0`, `-0.0 == 0`, unhashable lists/dicts; assigning keeps the first key object and position |
| `list.sort` / `sorted(key=...)` | `PySort` | line-for-line port of CPython 3.13 `listsort` (3.13 `count_run`, binary insertion, powersort, galloping), so the same `<` comparisons happen in the same order |
| `float(int)`, `int / int`, int/float comparison, `sum()` of floats | `PyNumbers` | round half to even with `OverflowError`; correctly rounded division; Neumaier summation of CPython 3.12+ |
| `json.dump(v, sys.stdout, indent=2)` | `PyJsonDump` | the pure-Python encoder, written chunk by chunk, with its recursion limit (below) |
| `json.loads` at a reduced C-stack budget | `PyJsonLoad` | see "Recursion limits" |
| `html.unescape` | `PyHtml` + generated `PyHelpersTables.Html5`/`InvalidCharrefs`/`InvalidCodepoints` | |
| `urllib.parse.unquote` (UTF-8, `errors='replace'`) | `PyUrl` | ASCII runs decoded separately; U+FFFD per CPython error range |
| `re`'s `\d`, `\s`, `\w`, code-point slicing, `%-Ns` | `PyText` + generated `PyHelpersTables.AlnumRanges` | Unicode 15.1 `str.isalnum()`; .NET's `\w` differs (Mn, Pc, numerals) and is Unicode 16 |
| `sys.stdin.read()` | `PyProcess.ReadStdin` | decodes all bytes at once with `PyIO.DecodeUtf8` |
| CPython's deep C stack | `PyProcess.OnLargeStack` | teams_feedback/engage_parse/the probe run on a 64 MiB-stack thread so recursive `repr`/comparison of ~10000-deep lists cannot overflow |
| `AttributeError`, `ZeroDivisionError`, `KeyError` of a non-str key | `PyAttributeError`, `PyZeroDivisionError`, `PyKeyErrorRepr` | |

`Python/generate_helpers_tables.py` regenerates `PyHelpersTables.cs` from the running CPython
(run it with 3.13 and review the diff).

The regular expressions of engage_parse.py and teams_feedback.py are matched by hand rather than
with .NET `Regex`: the patterns are anchored by literals, every quantifier in them is either
deterministic or lazy up to a literal, and the hand-written scanners make the code-point and
Unicode-table semantics explicit (`.NET` regex sees UTF-16 units and Unicode 16 classes).

## Recursion limits (measured, CPython 3.13)

- `json.dump` from a script's `main()` encodes containers nested up to depth 996 (the
  outermost is 1): `<module>`, `main`, `dump` and `_iterencode` hold four of the 1000 frames.
  The 997th container raises `RecursionError` after everything before it has been written, so
  stdout holds the partial document. `PyJsonDump.MaxDepthFromMain` reproduces both.
- `json.load` inside teams_feedback.py's `iter_messages` (a generator resumed by `collect`'s
  loop) accepts 9996 nested containers; `json.loads` from capacity.py's `main` and
  engage_parse.py's `load_hits` accept 9998 (`PyJson.MaxDepth`). `PyJsonLoad` lowers the limit
  and keeps CPython's order of errors: a decode error before the first too-deep container wins.
- Values loaded within those limits print with `repr` (table view) without hitting CPython's C
  recursion limit (checked at depth 9990, `teams_feedback_depth.json`).

## Deliberate deviations (and why)

1. **Adjacent lone surrogates.** A Python string can hold a lone high surrogate followed by a lone
   low surrogate (two code points); in UTF-16 they are one astral character. Only
   `html.unescape` can produce that from valid input (`"\ud800&#1;\udc00"` drops the reference
   between them). JSON output is identical (both escape `𐀀`), but the table view of
   teams_feedback.py prints the character where Python raises `UnicodeEncodeError`, and `[:72]`
   counts it once. State.Core's notes list the same limitation.
2. **NaN identity.** Containment, container equality and dict keys treat every NaN as identical,
   because every NaN the scripts see is `json.decoder.NaN` (one object). A NaN computed at run
   time would not be identical in Python; none is.
3. **Big-int position in `PyJsonLoad`.** When a document is both too deep and holds an int literal
   over 4300 digits, the lexer finds the first such literal by counting digits; a literal with
   leading zeros (invalid JSON) could be counted where the decoder stops earlier. Both outcomes
   are uncaught exceptions with exit status 1; only the class name could differ.
4. **Uncaught exceptions** print the traceback-shaped report of `StateScripts.RunGuarded`, as in
   every port; exit status and class match.
5. **Probe-only shortcuts.** `helpers_probe.py numbers sum` takes float lists only (the scripts
   only sum floats); `values html|unquote|unescape|salvage` mirror the Python type errors by
   class, not by message.

## Not ported

Nothing from the three scripts. The hosted classification (`statecmd.COMMANDS`,
`READ: {None}`) and the refusal of path arguments (`tests/test_autopilot_runtime.py` cases for
`capacity.py --input /etc/passwd`) belong to `Margo.Cloud.Engine`.

## Requests for State.Core

- Move `Python/` (or the parts other ports need: `PyObj`, `PyKey`, `PyDict`, `PySort`,
  `PyNumbers`, `PyText`, `PyJsonDump`, the extra exception classes) into State.Core so later
  ports share one implementation. Every script that sorts with `sorted()` or compares mixed JSON
  values needs the same semantics.
- `PyIO.ReadText` decodes stdin through a `StreamReader`, whose error message always says
  "invalid start byte" and whose position is relative to its buffer. CPython (`PYTHONIOENCODING=utf-8`,
  `LC_ALL=C.UTF-8`) reads all of stdin and reports the exact reason and absolute position
  (verified: "can't decode bytes in position 20000-20001: unexpected end of data").
  `PyProcess.ReadStdin` does that here; State.Core's deviation 7 could be removed the same way.
- `PyJson.MaxDepth` is the limit for a call from `main()`; a depth parameter (as `PyJsonLoad`
  adds) would let other ports match calls made deeper in the Python stack.

## Scenarios (`cloud/contracts/state/helpers/`)

| File | Covers |
| --- | --- |
| `capacity_python_tests.json` | every case of `tests/test_capacity.py`, through the command line |
| `capacity_reports.json` | warnings, merged/clipped intervals across offsets, focus threshold, int versus float effort, big ints, `-0.0`, microsecond intervals and compensated sums, spans past 2**53 microseconds, `fromisoformat` forms, `ensure_ascii` ids, duplicate keys |
| `capacity_errors.json` | every refusal message, JSON errors (BOM, digit limit, nesting), uncaught `OverflowError`s |
| `capacity_cli.json` | help (COLUMNS), argparse errors, `--input=-`, abbreviations, pathlib-normalised file errors, universal newlines, BOM, invalid and truncated UTF-8, empty files |
| `teams_feedback_triage.json` | statuses, replies inline and separate, dedupe by newest copy, dropped messages, `--since`, both views |
| `teams_feedback_problems.json` | unreadable files, HTTP errors per page, missing value arrays, partial output and exit 1, undecodable path bytes |
| `teams_feedback_types.json` | untrusted JSON types throughout, equal keys across types, NaN, lone surrogates |
| `teams_feedback_cli.json` | help, `nargs='+'`/`'*'` interplay, choices, abbreviations, `--` |
| `teams_feedback_depth.json` | `json.dump` partial output at the recursion limit, `json.load` limit 9996 and error order |
| `engage_parse_readout.json` | footnotes, padding-insensitive joins, marks, `--group`, ordering, both views |
| `engage_parse_malformed.json` | salvage, odd footnotes and markers, CRLF/CR, empty file |
| `engage_parse_types.json` | untrusted JSON types, `unquote` of odd ids, `_daygap` rules, NaN/infinities, digit limits |
| `engage_parse_cli.json` | unreadable files, nesting limits, command line |
| `probe_sort.json` | `list.sort` comparison traces (up to 1500 items, galloping, NaN, mixed types, tuples) |
| `probe_ops.json`, `probe_numbers.json`, `probe_capacity.json`, `probe_text.json`, `probe_dump.json` | the Python semantics layer and the library functions on their own |

The probe pair is `cloud/contracts/state/probes/helpers_probe.py` (reference) and
`HelpersProbeScript` (`margo-state helpers_probe.py`). It is not a Margo command: the hosted tool
only runs scripts classified in `statecmd.COMMANDS`. `write`/`write-hex` create the input files the
scripts read (the judge passes only arguments and stdin).

Python tests mined: `tests/test_capacity.py` (all cases, as scenarios and as xUnit tests). No test
in `tests/` exercises teams_feedback.py or engage_parse.py beyond the installer manifest.

## Verification

- `cd cloud && dotnet build Margo.Cloud.slnx` (0 warnings) and `dotnet test --solution Margo.Cloud.slnx`.
- `cloud/tests/Margo.Cloud.State.Tests/Helpers/`: `CapacityTests` (test_capacity.py ported, Python
  number rules, the command line, stdin decode errors), `PythonSemanticsTests` (sort traces,
  stability, equality/identity, dict keys, slicing errors, int/float conversion and division,
  `sum()`, `html.unescape`, `unquote`, Unicode classes, `json.dump` and `json.load` limits),
  `TeamsFeedbackTests`, `EngageParseTests`. Expected values come from CPython 3.13.
- `python3 tools/state_parity.py --check`: all `helpers/*.json` scenarios pass.

## Open questions

- The contracts README (`cloud/contracts/state/README.md`, owned by the foundation) lists only the
  core scenarios; it could link this folder.
- Should desktop Margo pin CPython 3.13? Older Pythons differ in `sum()` (no compensation before
  3.12), `list.sort`'s `count_run` (3.13) and `fromisoformat` forms (3.11), all observable here.

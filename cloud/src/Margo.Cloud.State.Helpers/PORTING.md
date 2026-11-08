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

### Python semantics

The scripts apply Python's dynamic operations to untrusted JSON (`msg.get(...)`, `x or ''`,
`x[:10]`, `len`, `in`, `int()`, `<` between whatever types arrived, dict keys of any hashable
type). A wrong type does not fail the same way in C#, so these helpers reproduce CPython 3.13 and
raise the same exception class. This port wrote most of them; since the state consolidation they live in
`Margo.Cloud.State.Core` and every state port shares them (see its PORTING.md). Only the HTML and URL
helpers stay in `Python/`.

| Python | C# | Notes |
| --- | --- | --- |
| `x.get`, truthiness, `x[:n]`, `x['k']`, `len`, `in`, iteration, `==`, `!=`, `<`, `<=`, `>`, `>=`, tuple `<`, `str`, `repr`, `int()` on JSON values | State.Core `PyObj` | `AttributeError`, `TypeError`, `KeyError(slice(...))`, `ValueError`, `OverflowError` as CPython raises them; list comparison by first unequal item; exact int/float comparison |
| dict keys (`hash` + `==`) and an insertion-ordered dict | State.Core `PyKey`, `PyDict<T>` | `True == 1 == 1.0`, `-0.0 == 0`, unhashable lists/dicts; assigning keeps the first key object and position |
| `list.sort` / `sorted(key=...)` | State.Core `PySort` | line-for-line port of CPython 3.13 `listsort` (3.13 `count_run`, binary insertion, powersort, galloping), so the same `<` comparisons happen in the same order |
| `float(int)`, `int / int`, int/float comparison, `sum()` of floats | State.Core `PyNumbers` | round half to even with `OverflowError`; correctly rounded division; Neumaier summation of CPython 3.12+ |
| `json.dump(v, sys.stdout, indent=2)` | State.Core `PyJsonDump` | the pure-Python encoder, written chunk by chunk, with its recursion limit (below) |
| `json.loads` at a reduced C-stack budget | State.Core `PyJson.Loads` with `PyJsonLoadOptions.MaxDepth` | see "Recursion limits" |
| `html.unescape` | `Python/PyHtml` + generated `PyHelpersTables.Html5`/`InvalidCharrefs`/`InvalidCodepoints` | |
| `urllib.parse.unquote` (UTF-8, `errors='replace'`) | `Python/PyUrl` | ASCII runs decoded separately; U+FFFD per CPython error range |
| `re`'s `\d`, `\s`, `\w`, code-point slicing, `%-Ns` | State.Core `PyText` (`IsDecimal`, `IsSpace`, `IsWord`/`IsAlnum`) on `PyUnicodeData` | Unicode 15.1 `str.isalnum()`; .NET's `\w` differs (Mn, Pc, numerals) and is Unicode 16 |
| `sys.stdin.read()` | State.Core `PyIO.ReadText` on `margo-state`'s `PyIO.StdinReader` | all remaining bytes decoded at once: CPython's reason and absolute position |
| CPython's deep C stack | State.Core `PyRuntime.OnLargeStack` | teams_feedback/engage_parse/the probe run on a 64 MiB-stack thread so recursive `repr`/comparison of ~10000-deep lists cannot overflow |
| `AttributeError`, `ZeroDivisionError`, `KeyError` of a non-str key | State.Core `PyAttributeError`, `PyZeroDivisionError`, `PyKeyError.ForRepr`/`ForSlice` | |

`Python/generate_helpers_tables.py` regenerates `PyHelpersTables.cs` (the `html` tables) from the running CPython
(run it with 3.13 and review the diff); the Unicode classes come from State.Core's `generate_unicode_data.py`.

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
  engage_parse.py's `load_hits` accept 9998 (`PyJson.MaxDepth`). The container limit is not the only
  effect of CPython's C recursion budget; `PyJson.Loads` models the rest for every port (State.Core's
  "JSON recursion budget"). With B the call site's container limit and n the level of the innermost open container
  (root = 1), measured through the scripts:
  - a container opens while n <= B (`RecursionError` "while decoding a JSON array/object ...");
  - `NaN`, `Infinity` and `-Infinity` call `parse_constant` and need n < B;
  - a decode error built in C (`raise_errmsg` calls `json.decoder.JSONDecodeError`, Python code)
    is a `JSONDecodeError` only while n <= B - 4, otherwise `RecursionError` (so capacity.py does
    not print `ERROR: ...` and engage_parse.py does not salvage the blob);
  - "Expecting value" (a `StopIteration` turned into `JSONDecodeError` by `raw_decode` after the C
    stack unwinds) and the 4300-digit `ValueError` fail to be created at n = B: CPython raises
    `RecursionError` with the note `Normalization failed: type=StopIteration|ValueError
    args=<unknown>`, which the traceback prints last, so it is the line the hosted tool reports;
  - errors raised after the scan (`Extra data`, the BOM check) are at level 0 and unaffected.

  The first of these in document order wins (`json_recursion_budget.json`, `JsonRecursionBudgetTests`). This
  port's `PyJsonLoad` modelled these rules by re-walking the valid prefix after `PyJson.Loads`; the state
  consolidation built them into State.Core's decoder, which also gets the `RecursionError` text right where a
  Python frame (not a C call) runs out ("maximum recursion depth exceeded" without "while calling a Python
  object" two and one levels below the limit).
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
3. **The JSON recursion budget is a measured model, not CPython's C stack.** Its rules (above) and the
   offset 4 for `raise_errmsg` were measured with CPython 3.13.16 on Linux x86-64; another build or Python
   version may differ by a level, as State.Core's deviation 5 notes for the container limit.
4. **Uncaught exceptions** print the traceback-shaped report of `StateScripts.RunGuarded`, as in
   every port; exit status and class match.
5. **Probe-only shortcuts.** `helpers_probe.py numbers sum` takes float lists only (the scripts
   only sum floats); `values html|unquote|unescape|salvage` mirror the Python type errors by
   class, not by message.

## Review fixes (second pass)

An independent review compared the port with CPython 3.13 by differential fuzzing (about 12,000
generated CLI cases across the three scripts, 200,000 probe values for `clean`, `html.unescape`,
`unquote`, `salvage`, `_encoded_key`, `_daygap`, comparisons, `int()`, numbers, `capacity.instant`,
400 `list.sort` traces, stdin decode errors at buffer boundaries, odd paths and `OSError`
subclasses, recursion boundaries of `repr`/comparison/`json.dump`). Everything above matched except
the following, each now covered by a test that failed before the fix:

- `PyJsonLoad` counted the fraction and exponent digits of a float (`1e000...`, `0.000...1`) as an
  over-long int literal, so a document with such a float, then a too-deep container, then an
  over-long int raised `ValueError` where CPython raises `RecursionError`.
- Near the nesting limit the port modelled only the container limit (and capacity.py and
  engage_parse.py did not use `PyJsonLoad` at all). CPython also raises `RecursionError` for
  `NaN`/`Infinity` at the limit, for decode errors built in C within four levels of it (capacity.py
  printed `ERROR: ...` and exit 2, engage_parse.py salvaged the blob, teams_feedback.py reported the
  file as unreadable) and, with the `Normalization failed` note as the reported line, for
  "Expecting value" and the 4300-digit `ValueError` at the limit. `PyJsonLoad` now models the C
  recursion budget (see "Recursion limits") and all three scripts load through it.
- The probe twin's `write-hex` stripped all Unicode whitespace and called
  `Convert.FromHexString`, so it accepted input `bytes.fromhex` rejects (`4 1`, `41　42`) and
  reported `FormatException` instead of `ValueError`; `write` raised .NET exceptions
  (`UnauthorizedAccessException`, `DirectoryNotFoundException`, `ArgumentException`) where
  pathlib raises `IsADirectoryError`/`FileNotFoundError`. Both now follow Python.

## Not ported

Nothing from the three scripts. The hosted classification (`statecmd.COMMANDS`,
`READ: {None}`) and the refusal of path arguments (the former Python runtime tests cases for
`capacity.py --input /etc/passwd`) belong to `Margo.Cloud.Engine`.

## Requests for State.Core

All done in the state consolidation: `PyObj`/`PyKey`/`PyDict`, `PySort`, `PyNumbers`, `PyText`, `PyJsonDump` and the
extra exception classes moved to State.Core; `PyIO.ReadText` reads `margo-state`'s stdin as CPython does (this port's
`PyProcess.ReadStdin` is gone); `PyJson.Loads` models the C recursion budget with a depth parameter
(`PyJsonLoadOptions.MaxDepth`), replacing `PyJsonLoad`.

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
| `json_recursion_budget.json` | the C recursion budget rules (`PyJson.Loads`) at full depth through all three scripts, each deep case with a control one level shallower; float fraction/exponent digits are not an over-long int |
| `probe_write.json` | the probe's `write`, `write-hex` (`bytes.fromhex`) and `write-nested` and their failures |
| `probe_sort.json` | `list.sort` comparison traces (up to 1500 items, galloping, NaN, mixed types, tuples) |
| `probe_ops.json`, `probe_numbers.json`, `probe_capacity.json`, `probe_text.json`, `probe_dump.json` | the Python semantics layer and the library functions on their own |

The probe pair is `cloud/contracts/state/probes/helpers_probe.py` (reference) and
`HelpersProbeScript` (in `Margo.Cloud.State.Probes`; `margo-state helpers_probe.py` in Debug builds with
`MARGO_STATE_PROBES=1`). It is not a Margo command: Release builds do not contain it, and the hosted tool
only runs scripts classified in `statecmd.COMMANDS`. `write`/`write-hex`/`write-nested` create the
input files the scripts read (the judge passes only arguments and stdin); `write-nested PATH DEPTH`
reads `[prefix, inner, suffix]` and writes `DEPTH` brackets around `inner`, so deep documents stay
small in a scenario.

Python tests mined: `tests/test_capacity.py` (all cases, as scenarios and as xUnit tests). No test
in `tests/` exercises teams_feedback.py or engage_parse.py beyond the installer manifest.

## Verification

- `cd cloud && dotnet build Margo.Cloud.slnx` (0 warnings) and `dotnet test --solution Margo.Cloud.slnx`.
- `cloud/tests/Margo.Cloud.State.Tests/Helpers/`: `CapacityTests` (test_capacity.py ported, Python
  number rules, the command line, stdin decode errors), `PythonSemanticsTests` (sort traces,
  stability, equality/identity, dict keys, slicing errors, int/float conversion and division,
  `sum()`, `html.unescape`, `unquote`, Unicode classes, `json.dump` and `json.load` limits),
  `TeamsFeedbackTests`, `EngageParseTests`, `JsonRecursionBudgetTests` (the C recursion budget
  rules, the three scripts at full depth, the probe's `write`/`write-hex`). Expected values come from
  CPython 3.13.
- `python3 tools/state_parity.py --check`: all `helpers/*.json` scenarios pass (also after the state
  consolidation, which changed no helper output).

## Open questions

- Should desktop Margo pin CPython 3.13? Older Pythons differ in `sum()` (no compensation before
  3.12), `list.sort`'s `count_run` (3.13) and `fromisoformat` forms (3.11), all observable here.

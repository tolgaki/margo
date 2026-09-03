#!/usr/bin/env bash
#
# Render the automations table into docs/proactive.md from automations/*.md.
#
#   ./tools/gen-automations-docs.sh            # print the table
#   ./tools/gen-automations-docs.sh --write    # update docs/proactive.md in place
#   ./tools/gen-automations-docs.sh --check    # fail if the docs are stale (CI)
#
# The table used to be maintained by hand next to the schedule it described,
# which is how it came to say 07:15 while the live schedule ran at 06:00. Front
# matter is now the only place a time is written down.
#
set -euo pipefail

ROOT=$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]:-$0}")/.." && pwd)
AUTOMATIONS="$ROOT/automations"
DOC="$ROOT/docs/proactive.md"
BEGIN='<!-- BEGIN GENERATED: automations -->'
END='<!-- END GENERATED: automations -->'

die() { printf 'error: %s\n' "$*" >&2; exit 1; }

# Flat 'key: value' front matter only — same contract as the wrapper, which
# deliberately does not depend on a YAML parser.
fm_get() {
  awk -v key="$2" '
    function is_delim(s,   p) { gsub(/^[ \t]+|[ \t]+$/, "", s); p = index(s, "---"); return (p > 0 && p <= 4 && substr(s, p) == "---") }
    { sub(/\r$/, "") }
    NR == 1 { if (!is_delim($0)) exit; next }
    { t = $0; gsub(/^[ \t]+|[ \t]+$/, "", t); if (t == "---") exit }
    {
      eq = index($0, ":")
      if (!eq) next
      k = substr($0, 1, eq - 1); v = substr($0, eq + 1)
      gsub(/^[ \t]+|[ \t]+$/, "", k); gsub(/^[ \t]+|[ \t]+$/, "", v)
      if (k != key) next
      if ((v ~ /^".*"$/) || (v ~ /^'"'"'.*'"'"'$/)) {
        v = substr(v, 2, length(v) - 2)
      } else {
        if (match(v, /[ \t]#/)) v = substr(v, 1, RSTART - 1)
        gsub(/[ \t]+$/, "", v)
      }
      print v; exit
    }
  ' "$1"
}

# cron → English, for the five-field subset these automations actually use.
# Anything outside it is printed verbatim rather than guessed at: a schedule
# described wrongly in the docs is worse than one described as a cron string.
#
# That guarantee needs BOUNDS, not just syntax. Validating shape alone rendered
# "Daily 06:60" and "Weekdays 25:99" into docs/proactive.md — and because
# --check compares the doc against this generator's own output, a wrong schedule
# was self-consistently wrong and CI stayed green.
humanize_cron() {
  awk -v cron="$1" '
    function pad(n) { return sprintf("%02d", n + 0) }
    BEGIN {
      n = split(cron, f, /[ \t]+/)
      if (n != 5 || f[3] != "*" || f[4] != "*" || f[1] !~ /^[0-9]+$/) { print cron; exit }
      if (f[1] + 0 > 59) { print cron; exit }

      if (f[2] ~ /^[0-9]+$/) {
        if (f[2] + 0 > 23) { print cron; exit }
        when = pad(f[2]) ":" pad(f[1])
      }
      else if (f[2] ~ /^[0-9]+-[0-9]+$/) {
        split(f[2], h, "-")
        if (h[1] + 0 > 23 || h[2] + 0 > 23 || h[2] + 0 <= h[1] + 0) { print cron; exit }
        when = "hourly " pad(h[1]) ":" pad(f[1]) "–" pad(h[2]) ":" pad(f[1])
      } else { print cron; exit }

      dow = f[5]
      if (dow == "*")        day = "Daily"
      else if (dow == "1-5") day = "Weekdays"
      else if (dow == "0-4") day = "Sun–Thu"
      else {
        split("Sunday Monday Tuesday Wednesday Thursday Friday Saturday", names, " ")
        if (dow ~ /^[0-6]$/) day = names[dow + 1]
        else { print cron; exit }
      }
      print day " " when
    }
  '
}

[ -d "$AUTOMATIONS" ] || die "no automations/ directory at $AUTOMATIONS"

render() {
  printf '| Automation | Tier | When | Routine | Wrapper |\n'
  printf '|---|---|---|---|---|\n'
  find "$AUTOMATIONS" -maxdepth 1 -type f -name '*.md' ! -name 'README.md' \
    | sort \
    | while IFS= read -r f; do
        case "$(fm_get "$f" tier)" in
          anchor) rank=1 ;; sweep) rank=2 ;; ambient) rank=3 ;; *) rank=4 ;;
        esac
        printf '%s\t%s\n' "$rank" "$f"
      done \
    | sort -k1,1n -k2,2 | cut -f2- \
    | while IFS= read -r f; do
        name=$(fm_get "$f" name)
        # 'CoS — ' distinguishes the workflows in the app's list; in a table
        # that is entirely chief-of-staff automations it is six wasted columns.
        name="${name#CoS — }"
        [ -n "$name" ] || die "$f has no 'name' in its front matter"
        verb=$(fm_get "$f" verb)
        [ -n "$verb" ] || die "$f has no 'verb' in its front matter"
        cron=$(fm_get "$f" cron)
        [ -n "$cron" ] || die "$f has no 'cron' in its front matter"
        printf '| %s | %s | %s | `%s` | `%s` |\n' \
          "$name" "$(fm_get "$f" tier)" "$(humanize_cron "$cron")" \
          "$(fm_get "$f" routine)" "$verb"
      done
}

TABLE=$(render)

case "${1:-}" in
  --write|--check)
    [ -f "$DOC" ] || die "no $DOC"
    grep -qF "$BEGIN" "$DOC" || die "$DOC has no '$BEGIN' marker"
    grep -qF "$END" "$DOC"   || die "$DOC has no '$END' marker"

    tmp=$(mktemp)
    tbl=$(mktemp)
    trap 'rm -f "$tmp" "$tbl"' EXIT
    printf '%s\n' "$TABLE" > "$tbl"
    # The table is read from a file rather than passed with -v: BSD awk rejects
    # a newline inside a -v assignment, so the obvious version works on Linux
    # and fails on every macOS machine.
    awk -v begin="$BEGIN" -v end="$END" -v tf="$tbl" '
      $0 == begin {
        print; print ""
        while ((getline line < tf) > 0) print line
        print ""
        skip = 1; next
      }
      $0 == end { skip = 0 }
      !skip     { print }
    ' "$DOC" > "$tmp"

    if [ "${1}" = "--check" ]; then
      if cmp -s "$tmp" "$DOC"; then
        printf 'automations table is current\n'
      else
        printf 'error: docs/proactive.md is stale.\n' >&2
        diff -u "$DOC" "$tmp" | sed -n '1,40p' >&2 || true
        printf '\nrun: ./tools/gen-automations-docs.sh --write\n' >&2
        exit 1
      fi
    else
      cp "$tmp" "$DOC"
      printf 'wrote %s\n' "${DOC#"$ROOT"/}"
    fi
    ;;
  ''|--print) printf '%s\n' "$TABLE" ;;
  *) die "unknown option: $1 (use --write, --check or no argument)" ;;
esac

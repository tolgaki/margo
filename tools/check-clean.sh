#!/usr/bin/env bash
#
# Checks that no real workplace data has crept into the repo.
#
#   ./tools/check-clean.sh              check the repo
#   ./tools/check-clean-selftest.sh     prove the checker still detects things
#
# This is the rule CONTRIBUTING.md makes non-negotiable, enforced. It runs in CI
# on every pull request; run it locally before you open one.
#
# Design notes, because the obvious implementation is wrong in ways that matter:
#
#   * Filters apply to the MATCH, never to the line. Filtering whole lines lets
#     a real address hide beside an allowed one on the same line.
#   * Every text file is scanned, not an extension allow-list. Extension-less
#     files such as packaging/macos/scripts/postinstall are exactly where a
#     stray identifier survives review.
#   * Tracked and untracked files are both scanned, so a pre-commit run sees
#     what you are about to add.
#   * Not every tenant identifier is a GUID. OneDrive drive ids and SharePoint
#     item ids have their own shapes and need their own patterns.
#
set -uo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.." || exit 1

if [ -t 1 ] && [ -z "${NO_COLOR:-}" ]; then
  R=$'\033[31m'; G=$'\033[32m'; B=$'\033[1m'; N=$'\033[0m'
else
  R=""; G=""; B=""; N=""
fi

FAILED=0
ROOT="${MARGO_SCAN_ROOT:-.}"

report() {
  # $1 = description, $2 = matches
  if [ -n "$2" ]; then
    printf '%s✗ %s%s\n' "$R$B" "$1" "$N"
    printf '%s\n' "$2" | sed 's/^/    /'
    FAILED=1
  else
    printf '%s✓%s %s\n' "$G" "$N" "$1"
  fi
}

# Every text file under $ROOT: tracked plus untracked, minus ignored and build
# output. No extension allow-list.
scan_files() {
  {
    if [ "$ROOT" = "." ] && git rev-parse --git-dir >/dev/null 2>&1; then
      git ls-files -z --cached --others --exclude-standard
    else
      find "$ROOT" -type f -not -path '*/.git/*' -print0
    fi
  } | while IFS= read -r -d '' f; do
      case "$f" in
        build/*|dist/*|*/build/*|*/dist/*) continue ;;
        tools/check-clean-selftest.sh) continue ;;   # planted fixtures by design
        *.png|*.jpg|*.jpeg|*.gif|*.ico|*.pdf|*.zip|*.pkg|*.exe) continue ;;
      esac
      [ -f "$f" ] || continue
      if LC_ALL=C grep -qI . "$f" 2>/dev/null; then printf '%s\n' "$f"; fi
    done
}

# Emit file:line:match so filters apply to the MATCH, not the whole line.
scan() {
  # $1 = extended regex
  scan_files | tr '\n' '\0' | xargs -0 grep -HnoE "$1" 2>/dev/null || true
}

join_hits() { printf '%s\n' "$@" | grep -v '^$' || true; }

printf '%sChecking for real workplace data%s\n\n' "$B" "$N"

# 1. Email addresses. Only example.com / example.org are permitted. Filtering is
#    on the matched address, so a real one beside an allowed one is still caught.
hits=$(scan '[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}' \
  | grep -viE ':[A-Za-z0-9._%+-]+@([A-Za-z0-9.-]*\.example|example\.(com|org)|users\.noreply\.github\.com|thread\.tacv[0-9]*)$' \
  | grep -viE ':[A-Za-z0-9._%+-]+@(graph\.microsoft\.com|login\.microsoftonline\.com|msrc\.microsoft\.com|timestamp\.digicert\.com)$' || true)
report "no real email addresses" "$hits"

# 2. GUIDs. Two are allowed and documented: the fixed public Azure DevOps
#    resource id, and the Windows installer's own AppId.
hits=$(scan '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}' \
  | grep -vi '499b84ac-1321-427f-aa17-267ca6975798' \
  | grep -vi 'b7e4b0a1-3c5d-4e2a-9f17-6d8c2a5e9b34' || true)
report "no tenant/org/channel GUIDs" "$hits"

# 3. Corporate mail domains that would identify an employer or colleague.
hits=$(scan '@(microsoft|google|amazon|apple|meta|contoso|fabrikam)\.(com|net|org|co\.[a-z]{2})\b' \
  | grep -viE ':@(graph|msrc|login|api|docs|learn)\.' || true)
tenant_hits=$(scan '[A-Za-z0-9-]+\.onmicrosoft\.com' | grep -vE ':\{' || true)
report "no corporate mail domains" "$(join_hits "$hits" "$tenant_hits")"

# 4. Absolute home paths leak a username — Unix and Windows, any case, with or
#    without a trailing slash.
unix_hits=$(scan '/(Users|home)/[A-Za-z][A-Za-z0-9_.-]*' \
  | grep -vE '/(Users|home)/\{' \
  | grep -viE '/(Users|home)/(you|user|username|name|someone|me|runner|margo)$' || true)
win_hits=$(scan '[A-Za-z]:\\+Users\\+[A-Za-z][A-Za-z0-9_.-]*' \
  | grep -vE '\\Users\\+\{' \
  | grep -viE '\\Users\\+(you|user|username|name|someone|me)$' || true)
report "no absolute home paths" "$(join_hits "$unix_hits" "$win_hits")"

# 5. Tenant-specific identifiers that are not GUID-shaped: OneDrive drive ids
#    (b!...), SharePoint/Graph item ids, Teams thread ids.
drive_hits=$(scan 'b![A-Za-z0-9_-]{30,}' || true)
item_hits=$(scan '\b0[0-9A-Z]{24,}\b' || true)
thread_hits=$(scan '19:[A-Za-z0-9_+/=-]{8,}@thread\.(tacv2|v2)' | grep -vE ':19:\{' || true)
sp_hits=$(scan 'https://[A-Za-z0-9-]+\.sharepoint\.com/[A-Za-z0-9/_%.-]*' | grep -vE '://\{' || true)
teams_hits=$(scan 'https://teams\.microsoft\.com/l/meetup-join/[A-Za-z0-9%_./-]+' || true)
report "no tenant resource identifiers" \
  "$(join_hits "$drive_hits" "$item_hits" "$thread_hits" "$sp_hits" "$teams_hits")"

# 6. Paths leak as readily as contents. A file named after a colleague, or a
#    directory named after a person, defeats every content check above.
path_hits=$(scan_files | grep -Ei '[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}|/(Users|home)/[A-Za-z][A-Za-z0-9_.-]*|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}' 2>/dev/null \
  | grep -viE '(example\.(com|org)|users\.noreply\.github\.com)' \
  | grep -vE '/(Users|home)/\{' || true)
report "no workplace data in file or directory names" "$path_hits"

# 7. Runtime state is real mailbox content. The WHOLE subtree, not just *.json —
#    the skills deliberately store Markdown state here because it is not JSON.
#    Repo-scoped: meaningless against a fixture tree, so skipped when scanning one.
if [ "$ROOT" = "." ]; then
  hits=$(git ls-files 'skills/*/state/*' 2>/dev/null | grep -v '/state/\.gitignore$' || true)
  report "no runtime state committed" "$hits"
fi

# 8. Personalization files must still be templates, not somebody's real data.
#    commitments.md has no {placeholder} by design — it is an empty ledger — so
#    it needs its own emptiness test rather than being left unchecked.
if [ "$ROOT" = "." ]; then
  tmpl_ok=1
  for f in skills/chief-of-staff/preferences.md skills/decision-log/config.md; do
    [ -f "$f" ] || continue
    if ! grep -q '{[a-z_]*}' "$f"; then
      printf '%s✗%s %s has no {placeholder} — filled in with real data?\n' "$R$B" "$N" "$f"
      FAILED=1
      tmpl_ok=0
    fi
  done
  # commitments.md is a ledger, not a template: it ships with empty table rows
  # and no placeholders, so the check above cannot see it at all. Count real
  # data rows instead — a filled ledger is a list of colleagues and deadlines.
  cm="skills/chief-of-staff/commitments.md"
  if [ -f "$cm" ]; then
    rows=$(awk '
      /^\|[-: |]*$/ { hdr[NR-1] = 1; next }
      /^\|/         { line[NR] = $0 }
      END { for (n in line) if (!(n in hdr)) {
              s = line[n]; gsub(/[| \t]/, "", s); if (length(s)) c++
            }
            print c + 0 }' "$cm")
    if [ "$rows" -gt 0 ]; then
      printf '%s✗%s %s has %s filled row(s) — real commitments must not be committed\n' \
        "$R$B" "$N" "$cm" "$rows"
      FAILED=1
      tmpl_ok=0
    fi
  fi
  if [ "$tmpl_ok" -eq 1 ]; then
    printf '%s✓%s personalization files are still templates\n' "$G" "$N"
  fi
fi

# 9. Names outside the fictional cast. Regexes cannot recognise that "Soumya" is
#    a real colleague and "Dana" is invented, so the repo standardises on one
#    small cast and flags anything else that appears in a person-shaped context.
#    This is a heuristic, deliberately narrow: it looks only where examples name
#    people, not at prose generally.
CAST='Dana|Rafa|Ines|Marco|Priya|Jane|Smith|Alice|Bob|Eve|Margo|Asker|Helper'
# "from X" is deliberately NOT a trigger: "from Work IQ", "from People" and
# "from Teams" are all legitimate, and the noise would train people to ignore
# this check. Only unambiguously person-shaped phrasings.
person_hits=$(scan_files | tr '\n' '\0' \
  | xargs -0 grep -HnoE "(1:1 with|reply to|respond to|Confirm with|chase|nudge) [A-Z][a-z]{2,}" 2>/dev/null \
  | grep -vE ":(1:1 with|reply to|respond to|Confirm with|chase|nudge) ($CAST)\$" || true)
report "no personal names outside the fictional cast" "$person_hits"

# 10. Optional private denylist. Some leaks are only recognisable to you: your own
#    initials in a sign-off, a team codename, a customer. Put one string per line
#    in tools/forbidden.local.txt (gitignored, never published) and they become
#    hard failures. Matching is case-insensitive and word-bounded, so "Tolga"
#    matches "Tolga's" but "/ac" does not match "action". Blank lines and
#    #-comments are ignored.
DENY="tools/forbidden.local.txt"
if [ -f "$DENY" ]; then
  deny_hits=""
  while IFS= read -r term; do
    case "$term" in ''|'#'*) continue ;; esac
    found=$(scan_files | tr '\n' '\0' | xargs -0 grep -Finw -- "$term" 2>/dev/null \
      | grep -v "^$DENY:" || true)
    [ -n "$found" ] && deny_hits=$(printf '%s\n%s' "$deny_hits" "$found")
  done < "$DENY"
  report "no strings from your private denylist" "$(printf '%s' "$deny_hits" | grep -v '^$' || true)"
else
  printf '  %s(no tools/forbidden.local.txt — see CONTRIBUTING.md to add your own terms)%s\n' "$B" "$N"
fi

printf '\n'
if [ "$FAILED" -ne 0 ]; then
  printf '%sFound content that looks like real workplace data.%s\n' "$R$B" "$N"
  printf 'Replace it with {placeholders} or invented names before committing.\n'
  printf 'See CONTRIBUTING.md.\n'
  exit 1
fi

printf '%sClean.%s\n' "$G$B" "$N"
exit 0

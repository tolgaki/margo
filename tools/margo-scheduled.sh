#!/usr/bin/env bash
#
# Run Margo on a schedule, with writes disabled at the CLI level.
#
#   ./tools/margo-scheduled.sh list           # what is defined
#   ./tools/margo-scheduled.sh brief          # morning brief
#   ./tools/margo-scheduled.sh eod            # end-of-day wrap-up
#   ./tools/margo-scheduled.sh week           # week ahead
#   ./tools/margo-scheduled.sh commitments    # commitment ageing
#   ./tools/margo-scheduled.sh sweep          # hourly sweep
#   ./tools/margo-scheduled.sh ambient        # ambient scan
#   ./tools/margo-scheduled.sh "any prompt"   # anything else (must be a phrase)
#   ./tools/margo-scheduled.sh word --prompt  # force a single word as a prompt
#
#   ./tools/margo-scheduled.sh crontab        # crontab lines for every automation
#   ./tools/margo-scheduled.sh brief --print  # show the command, run nothing
#   ./tools/margo-scheduled.sh brief --show-prompt   # show just the prompt
#
# Prompts come from automations/ — one Markdown file per automation, front
# matter for the schedule, body for the prompt. That directory is the source of
# truth for BOTH schedule paths (this wrapper and the app's workflows), so a
# prompt cannot drift between them. See automations/README.md.
#
# Why this exists rather than a documented command line:
#
# `docs/proactive.md` describes an unattended run as `--allow-all-tools` plus
# four `--deny-tool` flags. That is correct, and it is also four lines someone
# copies into cron and trims. Drop one and the read-only guarantee silently
# becomes an instruction the model is merely asked to follow — at 06:00, while
# you are asleep, with your mailbox connected.
#
# Here the deny list is not a parameter. Extra arguments are passed through, but
# they cannot re-enable writes: Copilot CLI resolves denial ahead of any allow
# rule, including --allow-all-tools, so even an explicit
# `--allow-tool 'workiq(do_action)'` loses to the entries below.
#
# What this does not do: --allow-all-tools is still passed, so shell, gh and
# curl remain available. The four Work IQ write tools are unreachable; a
# determined outbound action is not. That is the intended trade — this closes
# the path Margo would actually take, against the realistic failure of someone
# trimming a crontab. It is not a sandbox. If you want one, see
# docs/container.md. See docs/safety.md §3 for the full statement.
#
set -euo pipefail

# The four Work IQ tools that change the outside world. Everything else — fetch,
# retrieve, ask, call_function, get_schema, search_paths, fetch_blob — is a read
# and stays available.
#
# call_function looks like it belongs here and does not. OData functions are
# side-effect-free by definition, and it is how reminderView and
# calendarView/delta are reached — denying it would break the brief's own reads
# to prevent nothing.
DENY_TOOLS="do_action create_entity update_entity delete_entity"

AGENT="${MARGO_AGENT:-margo}"
LOG="${MARGO_SCHEDULED_LOG:-}"

# A free-form prompt gets the contract too. Everything this wrapper runs is
# unattended by definition, and the skill gates its Unattended Mode Contract on
# "when invoked by a scheduled workflow" — a condition the model has to infer.
# Left to infer it, a run can legally end in `ask_user` and hang until timeout.
UNATTENDED_PREAMBLE="Unattended scheduled run — apply the chief-of-staff Unattended Mode Contract: never call ask_user, no trailing offers, nothing is sent or RSVP'd or changed, drafts may be prepared but never delivered. Silence is a successful run."

if [ -t 1 ] && [ -z "${NO_COLOR:-}" ]; then
  B=$'\033[1m'; DIM=$'\033[2m'; R=$'\033[31m'; N=$'\033[0m'
else
  B=""; DIM=""; R=""; N=""
fi
die() { printf '%serror:%s %s\n' "$R" "$N" "$*" >&2; exit 1; }

usage() {
  sed -n '3,22p' "$0" | sed 's/^# \{0,1\}//'
  exit 0
}

# ------------------------------------------------------------ automations ----

SELF_DIR=$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]:-$0}")" && pwd)

# Repo checkout first, then an install. $MARGO_AUTOMATIONS overrides both, which
# is what you want when testing a change without reinstalling.
#
# `pwd -P`, not `pwd`. A --link install makes automations/ a symlink, and the
# logical path is what `cd … && pwd` returns — which `find` will not traverse
# without -H. That combination silently produced ZERO automations on every
# linked install, and every verb then degraded to the free-form fallback: the
# 06:00 brief ran with the literal word "brief" as its entire prompt.
resolve_automations() {
  if [ -n "${MARGO_AUTOMATIONS:-}" ]; then
    [ -d "$MARGO_AUTOMATIONS" ] \
      || die "MARGO_AUTOMATIONS is set to '$MARGO_AUTOMATIONS', which is not a directory"
    (CDPATH='' cd -- "$MARGO_AUTOMATIONS" && pwd -P); return
  fi
  for d in "$SELF_DIR/../automations" "${MARGO_DEST:-$HOME/.copilot}/automations"; do
    [ -d "$d" ] || continue
    # A directory with no automations in it is not the one we are looking for.
    # README.md does not count: matching *.md alone certified a README-only
    # directory as valid, and every verb then fell through to the fallback.
    found=0
    for c in "$d"/*.md; do
      [ -e "$c" ] || continue
      case "${c##*/}" in README.md) continue ;; esac
      found=1; break
    done
    [ "$found" -eq 1 ] || continue
    (CDPATH='' cd -- "$d" && pwd -P)
    return
  done
  die "no automations/ directory found.
       Looked in: $SELF_DIR/../automations
                  ${MARGO_DEST:-$HOME/.copilot}/automations
       Install with ./install.sh, or set MARGO_AUTOMATIONS to a checkout's automations/."
}

# Front matter is deliberately flat 'key: value' — no nesting, no lists. A YAML
# parser is a dependency this script cannot assume, and every field here is a
# scalar. Surrounding quotes are stripped so cron can be quoted (it must be, or
# the '*' makes it a YAML alias in anything that does parse it properly).
#
# Three normalizations, each of which caused a real divergence between this
# parser and the PowerShell one:
#   - a UTF-8 BOM on line 1 (what Windows editors write) made the opening '---'
#     unrecognisable here while PowerShell's ReadAllLines stripped it;
#   - trailing whitespace after '---' did the same, because PowerShell trims;
#   - an unquoted trailing '# comment' — which the published template used —
#     stayed part of the value, so `verb: brief  # …` never matched `brief`.
fm_get() {
  # $1 = file, $2 = key
  awk -v key="$2" '
    NR == 1 && substr($0, 1, 3) == "\357\273\277" { $0 = substr($0, 4) }
    { sub(/\r$/, "") }
    NR == 1 { t = $0; gsub(/^[ \t]+|[ \t]+$/, "", t); if (t != "---") exit; next }
    { t = $0; gsub(/^[ \t]+|[ \t]+$/, "", t); if (t == "---") exit }
    {
      eq = index($0, ":")
      if (!eq) next
      k = substr($0, 1, eq - 1)
      v = substr($0, eq + 1)
      gsub(/^[ \t]+|[ \t]+$/, "", k)
      gsub(/^[ \t]+|[ \t]+$/, "", v)
      if (k != key) next
      if ((v ~ /^".*"$/) || (v ~ /^'"'"'.*'"'"'$/)) {
        v = substr(v, 2, length(v) - 2)
      } else {
        # Unquoted scalar: an inline comment ends the value. Only when preceded
        # by whitespace, so a legitimate "#" inside a value survives.
        if (match(v, /[ \t]#/)) v = substr(v, 1, RSTART - 1)
        gsub(/[ \t]+$/, "", v)
      }
      print v
      exit
    }
  ' "$1"
}

# Body boundary must match the PowerShell wrapper's .Trim() exactly, or the two
# wrappers send different bytes for a prompt with leading or trailing spaces.
fm_body() {
  awk '
    NR == 1 && substr($0, 1, 3) == "\357\273\277" { $0 = substr($0, 4) }
    { sub(/\r$/, "") }
    NR == 1 { t = $0; gsub(/^[ \t]+|[ \t]+$/, "", t); if (t != "---") body = 1; next }
    !body { t = $0; gsub(/^[ \t]+|[ \t]+$/, "", t); if (t == "---") { body = 1; next } }
    body { print }
  ' "$1" | awk '
    { line[NR] = $0 }
    END {
      first = 0; last = 0
      for (i = 1; i <= NR; i++) if (line[i] ~ /[^ \t]/) { if (!first) first = i; last = i }
      if (!first) exit
      for (i = first; i <= last; i++) {
        s = line[i]
        if (i == first) sub(/^[ \t]+/, "", s)
        if (i == last)  sub(/[ \t]+$/, "", s)
        print s
      }
    }
  '
}

# Every automation file, ordered anchors → sweeps → ambient, then by filename so
# output is stable. Filename order alone puts the morning brief fifth, which
# reads as arbitrary in `list` and scatters the tiers in `crontab`.
automation_files() {
  find "$AUTOMATIONS" -maxdepth 1 -type f -name '*.md' ! -name 'README.md' \
    | sort \
    | while IFS= read -r f; do
        case "$(fm_get "$f" tier)" in
          anchor) rank=1 ;; sweep) rank=2 ;; ambient) rank=3 ;; *) rank=4 ;;
        esac
        printf '%s\t%s\n' "$rank" "$f"
      done \
    | sort -k1,1n -k2,2 | cut -f2-
}

# Subcommand names cannot also be verbs: `case "$WHAT" in list|crontab)` matches
# first, so such an automation is listed as available and is unreachable — and
# the generated crontab line for it re-invokes the subcommand, giving a cron job
# that prints crontab lines at 06:00 forever. Rejected at validation instead.
RESERVED_VERBS="list crontab help"

# Validate the whole set before anything uses it. A file that exists but does
# not parse MUST be an error: the failure mode this replaces was a broken
# manifest silently selecting the free-form fallback, which is indistinguishable
# from a successful run and sends a one-word prompt to an unattended session.
validate_automations() {
  va_n=0
  va_verbs=""
  while IFS= read -r f; do
    [ -n "$f" ] || continue
    va_n=$((va_n + 1))
    for key in name verb cron tier; do
      [ -n "$(fm_get "$f" "$key")" ] || die "automation ${f##*/} has no '$key' in its front matter.
       Every automation needs name, verb, cron and tier. See automations/README.md."
    done
    v=$(fm_get "$f" verb)
    case " $RESERVED_VERBS " in
      *" $v "*) die "automation ${f##*/} uses the reserved verb '$v'.
       $RESERVED_VERBS are subcommands of this script and cannot be automation verbs." ;;
    esac
    case " $va_verbs " in
      *" $v "*) die "verb '$v' is defined by more than one automation in $AUTOMATIONS.
       Verbs must be unique — otherwise which prompt runs depends on filename order." ;;
    esac
    va_verbs="${va_verbs:+$va_verbs }$v"
    [ -n "$(fm_body "$f")" ] || die "automation ${f##*/} has an empty prompt body."
  done <<EOF
$(automation_files)
EOF
  [ "$va_n" -gt 0 ] || die "no automations found in $AUTOMATIONS.
       Expected one or more *.md files with front matter. See automations/README.md."
  AVAILABLE_VERBS="$va_verbs"
}

find_by_verb() {
  # $1 = verb. Prints the file path, or nothing.
  while IFS= read -r f; do
    [ -n "$f" ] || continue
    [ "$(fm_get "$f" verb)" = "$1" ] && { printf '%s' "$f"; return 0; }
  done <<EOF
$(automation_files)
EOF
  return 1
}

cmd_list() {
  printf '%s%-13s %-8s %-16s %s%s\n' "$B" "VERB" "TIER" "CRON" "NAME" "$N"
  while IFS= read -r f; do
    [ -n "$f" ] || continue
    printf '%-13s %-8s %-16s %s\n' \
      "$(fm_get "$f" verb)" "$(fm_get "$f" tier)" "$(fm_get "$f" cron)" "$(fm_get "$f" name)"
  done <<EOF
$(automation_files)
EOF
  # Footer to stderr: `list` is parsed by CI and by anyone scripting against it,
  # and a provenance line in the data stream reads as a seventh automation.
  printf '\n%ssource: %s%s\n' "$DIM" "$AUTOMATIONS" "$N" >&2
}

# Crontab lines calling this wrapper by absolute path. cron does not inherit
# your shell's PATH, so the wrapper is invoked absolutely and PATH is set once
# at the top — the two things that otherwise make a correct crontab do nothing.
#
# Paths are shell-quoted: cron runs the line through a shell, so an unquoted
# install path containing a space truncates the command at the space and the
# job silently never runs.
shq() {
  # Single-quote for /bin/sh, escaping any embedded single quotes.
  printf "'%s'" "$(printf '%s' "$1" | sed "s/'/'\\\\''/g")"
}

cmd_crontab() {
  printf '# Margo — generated by %s crontab\n' "${0##*/}"
  printf '# Review, then: %s crontab | crontab -\n' "$0"
  printf 'PATH=%s\n' "${PATH}"
  while IFS= read -r f; do
    [ -n "$f" ] || continue
    printf '\n# %s (%s)\n%s %s %s\n' \
      "$(fm_get "$f" name)" "$(fm_get "$f" tier)" \
      "$(fm_get "$f" cron)" "$(shq "$SELF_DIR/${0##*/}")" "$(shq "$(fm_get "$f" verb)")"
  done <<EOF
$(automation_files)
EOF
}

# ------------------------------------------------------------------ main ----

[ $# -ge 1 ] || usage
case "${1:-}" in -h|--help|help) usage ;; esac

AUTOMATIONS=$(resolve_automations)
validate_automations

WHAT="$1"; shift

case "$WHAT" in
  list)    cmd_list; exit 0 ;;
  crontab) cmd_crontab; exit 0 ;;
esac

# An explicit escape hatch, so free-form does not have to be inferred.
FORCE_PROMPT=0
for a in "$@"; do
  [ "$a" = "--prompt" ] && FORCE_PROMPT=1
done

if [ "$FORCE_PROMPT" -eq 0 ] && FILE=$(find_by_verb "$WHAT"); then
  PROMPT=$(fm_body "$FILE")
  [ -n "$PROMPT" ] || die "automation '$WHAT' ($FILE) has an empty prompt body"
else
  # Not a known verb. A bare word is almost certainly a verb whose manifest did
  # not resolve, and sending it as the literal prompt is the failure this whole
  # mechanism exists to prevent — an unattended 06:00 run whose entire prompt is
  # the word "brief". Free-form prompts are phrases; require one, or --prompt.
  if [ "$FORCE_PROMPT" -eq 0 ]; then
    case "$WHAT" in
      *[[:space:]]*) ;;
      *) die "unknown verb '$WHAT'.
       Available: $AVAILABLE_VERBS
       Source:    $AUTOMATIONS
       To send '$WHAT' as a literal prompt anyway, add --prompt." ;;
    esac
  fi
  FILE=""
  PROMPT="$UNATTENDED_PREAMBLE

$WHAT"
fi

PRINT_ONLY=0
SHOW_PROMPT=0
ARGS=""
for a in "$@"; do
  case "$a" in
    --print|--dry-run) PRINT_ONLY=1 ;;
    --show-prompt)     SHOW_PROMPT=1 ;;
    *) ARGS="${ARGS:+$ARGS }$a" ;;
  esac
done

if [ "$SHOW_PROMPT" -eq 1 ]; then
  printf '%s\n' "$PROMPT"
  exit 0
fi

set -- copilot --agent "$AGENT" -p "$PROMPT" --allow-all-tools
for t in $DENY_TOOLS; do
  set -- "$@" --deny-tool "workiq($t)"
done
# shellcheck disable=SC2086  # deliberate word-splitting of pass-through args
[ -n "$ARGS" ] && set -- "$@" $ARGS

# --print before the PATH check: showing the command is a dry run, and is exactly
# what you want when inspecting it before installing anything, or composing a
# crontab line for a different machine.
if [ "$PRINT_ONLY" -eq 1 ]; then
  printf '%s\n' "$*"
  exit 0
fi

command -v copilot >/dev/null 2>&1 \
  || die "copilot not found on PATH.
       cron and launchd do not inherit your shell's PATH — use an absolute path
       or set PATH at the top of the crontab. '$0 crontab' emits both."


printf '%s[%s]%s margo scheduled: %s%s\n' \
  "$B" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$N" "$WHAT" \
  "${FILE:+ ${DIM}(${FILE##*/})${N}}"

if [ -n "$LOG" ]; then
  mkdir -p "$(dirname "$LOG")"
  "$@" 2>&1 | tee -a "$LOG"
  exit "${PIPESTATUS[0]}"
fi

exec "$@"

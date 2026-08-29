#!/usr/bin/env bash
#
# Run Margo on a schedule, with writes disabled at the CLI level.
#
#   ./tools/margo-scheduled.sh brief          # morning brief
#   ./tools/margo-scheduled.sh eod            # end-of-day wrap-up
#   ./tools/margo-scheduled.sh week           # week ahead
#   ./tools/margo-scheduled.sh commitments    # commitment ageing
#   ./tools/margo-scheduled.sh sweep          # hourly sweep
#   ./tools/margo-scheduled.sh "any prompt"   # anything else
#
#   ./tools/margo-scheduled.sh brief --print  # show the command, run nothing
#
# Why this exists rather than a documented command line:
#
# `docs/proactive.md` describes an unattended run as `--allow-all-tools` plus
# four `--deny-tool` flags. That is correct, and it is also four lines someone
# copies into cron and trims. Drop one and the read-only guarantee silently
# becomes an instruction the model is merely asked to follow — at 07:15, while
# you are asleep, with your mailbox connected.
#
# Here the deny list is not a parameter. Extra arguments are passed through, but
# they cannot re-enable writes: Copilot CLI resolves denial ahead of any allow
# rule, including --allow-all-tools, so even an explicit
# `--allow-tool 'workiq(do_action)'` loses to the entries below.
#
set -euo pipefail

# The four Work IQ tools that change the outside world. Everything else — fetch,
# retrieve, ask, call_function, get_schema, search_paths, fetch_blob — is a read
# and stays available.
DENY_TOOLS="do_action create_entity update_entity delete_entity"

AGENT="${MARGO_AGENT:-margo}"
LOG="${MARGO_SCHEDULED_LOG:-}"

if [ -t 1 ] && [ -z "${NO_COLOR:-}" ]; then
  B=$'\033[1m'; R=$'\033[31m'; N=$'\033[0m'
else
  B=""; R=""; N=""
fi
die() { printf '%serror:%s %s\n' "$R" "$N" "$*" >&2; exit 1; }

usage() {
  sed -n '3,20p' "$0" | sed 's/^# \{0,1\}//'
  exit 0
}

[ $# -ge 1 ] || usage
case "${1:-}" in -h|--help|help) usage ;; esac

WHAT="$1"; shift

# Named anchors match the tiers in skills/chief-of-staff/references/proactive.md.
case "$WHAT" in
  brief)       PROMPT="Run my morning brief." ;;
  eod)         PROMPT="Run my end-of-day wrap-up." ;;
  week)        PROMPT="Run my week ahead." ;;
  commitments) PROMPT="Run commitment ageing: what have I promised that is slipping?" ;;
  sweep)       PROMPT="Run my hourly sweep. Stay silent unless something clears the interrupt bar." ;;
  *)           PROMPT="$WHAT" ;;
esac

PRINT_ONLY=0
ARGS=""
for a in "$@"; do
  case "$a" in
    --print|--dry-run) PRINT_ONLY=1 ;;
    *) ARGS="${ARGS:+$ARGS }$a" ;;
  esac
done

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
       or set PATH at the top of the crontab."


printf '%s[%s]%s margo scheduled: %s\n' "$B" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$N" "$WHAT"

if [ -n "$LOG" ]; then
  mkdir -p "$(dirname "$LOG")"
  "$@" 2>&1 | tee -a "$LOG"
  exit "${PIPESTATUS[0]}"
fi

exec "$@"

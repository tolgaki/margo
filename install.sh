#!/usr/bin/env bash
#
# Margo installer — macOS and Linux
#
#   ./install.sh                      install the chief-of-staff skill + agent
#   ./install.sh --all                include the decision-log skill too
#   ./install.sh --link               symlink instead of copy (for contributors)
#   ./install.sh status               show what's installed
#   ./install.sh uninstall            remove it, keeping your personal files
#
# Also works without a clone:
#   curl -fsSL https://raw.githubusercontent.com/tolgaki/margo/main/install.sh | bash
#
set -euo pipefail

REPO_SLUG="tolgaki/margo"
BRANCH="${MARGO_BRANCH:-main}"
# Where the installed copy records what it is, so `update` and `status` can tell
# you whether you are behind. Plain key=value: parsing JSON would add a runtime
# dependency to a script whose whole job is to work before anything is set up.
MANIFEST_NAME=".margo-install"
AGENT_FILE="margo.agent.md"
ALL_SKILLS="chief-of-staff decision-log"
DEFAULT_SKILLS="chief-of-staff"

# Files that hold your data, not ours. Never overwritten, never deleted.
USER_DATA="chief-of-staff/preferences.md
chief-of-staff/commitments.md
decision-log/config.md"

COMMAND="install"
CHECK_ONLY=0
DEST="${MARGO_DEST:-$HOME/.copilot}"
SKILLS=""
MODE="copy"
DRY_RUN=0
FORCE=0
ASSUME_YES=0
SRC=""
TMP_DIR=""

# ---------------------------------------------------------------- output ----

if [ -t 1 ] && [ -z "${NO_COLOR:-}" ]; then
  B=$'\033[1m'; DIM=$'\033[2m'; R=$'\033[31m'; G=$'\033[32m'; Y=$'\033[33m'; C=$'\033[36m'; N=$'\033[0m'
else
  B=""; DIM=""; R=""; G=""; Y=""; C=""; N=""
fi

info() { printf '%s\n' "$*"; }
step() { printf '%s==>%s %s%s%s\n' "$C" "$N" "$B" "$*" "$N"; }
ok()   { printf '  %s✓%s %s\n' "$G" "$N" "$*"; }
skip() { printf '  %s·%s %s\n' "$DIM" "$N" "$*"; }
warn() { printf '  %s!%s %s\n' "$Y" "$N" "$*" >&2; }
die()  { printf '%serror:%s %s\n' "$R" "$N" "$*" >&2; exit 1; }

cleanup() {
  if [ -n "$TMP_DIR" ] && [ -d "$TMP_DIR" ]; then rm -rf "$TMP_DIR"; fi
  return 0
}
trap cleanup EXIT

# Version of the source tree we are installing FROM.
source_version() {
  if [ -f "$SRC/VERSION" ]; then
    tr -d ' \t\n\r' < "$SRC/VERSION"
  else
    printf 'unknown'
  fi
}

# Version recorded by a previous install into $DEST, if any.
installed_version() {
  [ -f "$DEST/$MANIFEST_NAME" ] || { printf ''; return; }
  sed -n 's/^version=//p' "$DEST/$MANIFEST_NAME" | head -n 1
}

installed_field() {
  # $1 = key
  [ -f "$DEST/$MANIFEST_NAME" ] || { printf ''; return; }
  sed -n "s/^$1=//p" "$DEST/$MANIFEST_NAME" | head -n 1
}

# Is $1 a newer version than $2? Numeric dotted compare; any pre-release suffix
# is ignored, so 1.2.0-rc1 and 1.2.0 compare equal rather than unordered.
version_gt() {
  vg_a=$(printf '%s' "$1" | sed 's/[^0-9.].*$//')
  vg_b=$(printf '%s' "$2" | sed 's/[^0-9.].*$//')
  [ -n "$vg_a" ] || return 1
  [ -n "$vg_b" ] || return 0
  [ "$vg_a" = "$vg_b" ] && return 1
  vg_top=$(printf '%s\n%s\n' "$vg_a" "$vg_b" | sort -t. -k1,1n -k2,2n -k3,3n | tail -n 1)
  [ "$vg_top" = "$vg_a" ]
}

write_manifest() {
  # $1 = space-separated skill list actually installed
  [ "$DRY_RUN" -eq 1 ] && return 0
  mkdir -p "$DEST"
  {
    printf 'version=%s\n' "$(source_version)"
    printf 'installed_at=%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    printf 'mode=%s\n' "$MODE"
    printf 'skills=%s\n' "$1"
    printf 'source=%s\n' "$SRC"
  } > "$DEST/$MANIFEST_NAME"
}

# The newest version published upstream. Empty if offline or unavailable.
remote_version() {
  command -v curl >/dev/null 2>&1 || return 1
  curl -fsSL --max-time 10 \
    "https://raw.githubusercontent.com/$REPO_SLUG/$BRANCH/VERSION" 2>/dev/null \
    | tr -d ' \t\n\r'
}

usage() {
  cat <<EOF
${B}Margo installer${N}

${B}USAGE${N}
  ./install.sh [command] [options]

${B}COMMANDS${N}
  install            Install the agent and skills  ${DIM}(default)${N}
  update             Re-install the skills you already have, at the latest version
  uninstall          Remove them, preserving your personal files
  status             Show what is currently installed

${B}OPTIONS${N}
  --all              Install all skills: $ALL_SKILLS
  --skills a,b       Install only the named skills
  --link             Symlink to this clone instead of copying
  --dest DIR         Install root ${DIM}(default: ~/.copilot)${N}
  --check            With 'update': report whether you are behind, change nothing
  --force            Overwrite personal files, backing them up first
  --dry-run          Print what would happen, change nothing
  -y, --yes          Don't prompt
  -h, --help         This

${B}NOTES${N}
  preferences.md, commitments.md, config.md and state/ are never
  overwritten or deleted. Reinstalling to upgrade is safe.
EOF
}

# ------------------------------------------------------------------ args ----

while [ $# -gt 0 ]; do
  case "$1" in
    install|uninstall|status|update) COMMAND="$1" ;;
    --check)      CHECK_ONLY=1 ;;
    --all)        SKILLS="$ALL_SKILLS" ;;
    --skills)     [ $# -ge 2 ] || die "--skills needs a value"
                  SKILLS=$(printf '%s' "$2" | tr ',' ' '); shift ;;
    --skills=*)   SKILLS=$(printf '%s' "${1#*=}" | tr ',' ' ') ;;
    --link)       MODE="link" ;;
    --dest)       [ $# -ge 2 ] || die "--dest needs a value"; DEST="$2"; shift ;;
    --dest=*)     DEST="${1#*=}" ;;
    --force)      FORCE=1 ;;
    --dry-run)    DRY_RUN=1 ;;
    -y|--yes)     ASSUME_YES=1 ;;
    -h|--help)    usage; exit 0 ;;
    *)            die "unknown argument: $1  (try --help)" ;;
  esac
  shift
done

[ -n "$SKILLS" ] || SKILLS="$DEFAULT_SKILLS"

for s in $SKILLS; do
  case " $ALL_SKILLS " in
    *" $s "*) ;;
    *) die "unknown skill: $s  (available: $ALL_SKILLS)" ;;
  esac
done

# --------------------------------------------------------------- helpers ----

is_user_data() {
  # $1 = path relative to skills/, e.g. chief-of-staff/preferences.md
  printf '%s\n' "$USER_DATA" | grep -qxF "$1"
}

# Locate the repo: the directory this script lives in, or a fresh download.
resolve_source() {
  script_dir=$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]:-$0}")" && pwd)
  if [ -f "$script_dir/agents/$AGENT_FILE" ]; then
    SRC="$script_dir"
    return
  fi

  # Piped from curl, or run from outside a checkout.
  command -v curl >/dev/null 2>&1 || die "curl is required to download the repo"
  step "Downloading $REPO_SLUG@$BRANCH"
  TMP_DIR=$(mktemp -d 2>/dev/null || mktemp -d -t margo)
  curl -fsSL "https://codeload.github.com/$REPO_SLUG/tar.gz/refs/heads/$BRANCH" \
    | tar -xzf - -C "$TMP_DIR" \
    || die "download failed — check your network, or clone the repo and run ./install.sh"
  SRC=$(find "$TMP_DIR" -maxdepth 1 -type d -name 'margo-*' | head -n 1)
  [ -n "$SRC" ] && [ -f "$SRC/agents/$AGENT_FILE" ] || die "downloaded archive looks wrong"
  ok "fetched to a temporary directory"
}

# One archive directory per invocation, created lazily and atomically the first
# time something needs saving, and never reused. A name derived only from
# `date +%S` collides whenever two commands run in the same second — a --force
# install followed by an uninstall then shared a directory, and the uninstall
# archived the freshly-installed template over the --force backup of the user's
# original. mkdir (without -p) is the atomic test: it fails if the name is taken.
STASH_DIR=""
ensure_stash() {
  [ -n "$STASH_DIR" ] && return 0
  mkdir -p "$DEST" 2>/dev/null || true
  es_base="$DEST/margo-personal-backup.$(date +%Y%m%d%H%M%S)"
  es_cand="$es_base"; es_n=2
  while ! mkdir "$es_cand" 2>/dev/null; do
    [ "$es_n" -gt 999 ] && die "cannot create a unique backup directory under $DEST"
    es_cand="$es_base-$es_n"; es_n=$((es_n + 1))
  done
  STASH_DIR="$es_cand"
}

# Everything in an installed skill that belongs to the user rather than to us:
# the personalization files, the ENTIRE state/ subtree at any depth and any file
# type, and any *.bak.* left by an older --force. Enumerated by sweeping what is
# actually present — never from a hard-coded list, which is how Markdown state
# and --force backups were previously destroyed while being reported as kept.
user_files_in() {
  # $1 = installed skill dir, $2 = skill name. Prints paths relative to $1.
  d="$1"; uname="$2"
  [ -d "$d" ] || return 0

  printf '%s\n' "$USER_DATA" | while IFS= read -r rel; do
    case "$rel" in "$uname/"*) ;; *) continue ;; esac
    sub="${rel#"$uname"/}"
    [ -f "$d/$sub" ] && printf '%s\n' "$sub"
  done

  if [ -d "$d/state" ]; then
    find "$d/state" -type f 2>/dev/null | while IFS= read -r f; do
      sub="${f#"$d"/}"
      [ "$sub" = "state/.gitignore" ] || printf '%s\n' "$sub"
    done
  fi

  find "$d" -type f -name '*.bak.*' 2>/dev/null | while IFS= read -r f; do
    printf '%s\n' "${f#"$d"/}"
  done
}

# Archive every user file out of an installed skill before it is deleted.
# Returns non-zero if ANY file could not be archived; the caller must not
# delete the directory in that case. Sets ARCHIVED to the count.
ARCHIVED=0
archive_user_data() {
  # $1 = skill name, $2 = installed dir. Archives into this invocation's stash,
  # created on first use so a run that saves nothing leaves no empty directory.
  aname="$1"; adir="$2"
  ARCHIVED=0
  while IFS= read -r sub; do
    [ -n "$sub" ] || continue
    if [ "$DRY_RUN" -eq 0 ]; then
      ensure_stash
      adst="$STASH_DIR/$aname/$sub"
      mkdir -p "$(dirname "$adst")" 2>/dev/null || return 1
      # Never clobber an existing archive entry.
      [ -e "$adst" ] && return 1
      cp -p "$adir/$sub" "$adst" 2>/dev/null || return 1
    fi
    ARCHIVED=$((ARCHIVED + 1))
    printf '  %s·%s kept %s/%s\n' "$DIM" "$N" "$aname" "$sub"
  done <<ARCHEOF
$(user_files_in "$adir" "$aname")
ARCHEOF
  return 0
}

confirm() {
  [ "$ASSUME_YES" -eq 1 ] && return 0
  [ "$DRY_RUN" -eq 1 ] && return 0
  # No terminal: take the [y/N] default, which is N. A destructive prompt that
  # answers itself "yes" when piped is how --link used to destroy personal data
  # from a Makefile, an ssh command or a CI step, with no prompt ever shown.
  [ -t 0 ] || { printf '%s\n' "$1"; warn "not a terminal — assuming no"; return 1; }
  printf '%s [y/N] ' "$1"
  read -r reply
  case "$reply" in [yY]|[yY][eE][sS]) return 0 ;; *) return 1 ;; esac
}

python_bin() {
  for p in python3 python; do
    if command -v "$p" >/dev/null 2>&1; then
      if "$p" -c 'import sys; sys.exit(0 if sys.version_info >= (3,9) else 1)' 2>/dev/null; then
        printf '%s' "$p"; return 0
      fi
    fi
  done
  return 1
}

# --------------------------------------------------------------- install ----

install_agent() {
  target="$DEST/agents/$AGENT_FILE"
  [ "$DRY_RUN" -eq 1 ] || mkdir -p "$DEST/agents"

  if [ -e "$target" ] || [ -L "$target" ]; then
    [ "$DRY_RUN" -eq 1 ] || rm -f "$target"
  fi

  if [ "$MODE" = "link" ]; then
    [ "$DRY_RUN" -eq 1 ] || ln -s "$SRC/agents/$AGENT_FILE" "$target"
    ok "agents/$AGENT_FILE ${DIM}→ linked${N}"
  else
    [ "$DRY_RUN" -eq 1 ] || cp "$SRC/agents/$AGENT_FILE" "$target"
    ok "agents/$AGENT_FILE"
  fi
}

install_skill() {
  name="$1"
  src="$SRC/skills/$name"
  target="$DEST/skills/$name"
  [ -d "$src" ] || die "skill not found in source: $name"
  [ "$DRY_RUN" -eq 1 ] || mkdir -p "$DEST/skills"

  if [ "$MODE" = "link" ]; then
    if [ -e "$target" ] && [ ! -L "$target" ]; then
      # Count first so the prompt can state the stakes, then archive, then
      # delete. This used to delete outright with no backup of any kind.
      n=$(user_files_in "$target" "$name" | grep -c . || true)
      confirm "  $target is a real directory holding $n personal file(s).
     Replace it with a link? They will be archived to a backup directory first." \
        || { skip "$name ${DIM}(kept existing directory)${N}"; return; }

      ARCHIVED=0
      if ! archive_user_data "$name" "$target"; then
        die "could not archive personal files from skills/$name — nothing was changed.
       Check that $DEST is writable, then try again."
      fi
      [ "$ARCHIVED" -gt 0 ] && warn "$ARCHIVED file(s) archived to ${STASH_DIR##*/}/$name"
      [ "$DRY_RUN" -eq 1 ] || rm -rf "$target"
    fi
    [ -L "$target" ] && { [ "$DRY_RUN" -eq 1 ] || rm -f "$target"; }
    [ "$DRY_RUN" -eq 1 ] || ln -s "$src" "$target"
    ok "skills/$name ${DIM}→ linked${N}"
    return
  fi

  if [ -L "$target" ]; then
    [ "$DRY_RUN" -eq 1 ] || rm -f "$target"
    warn "skills/$name was a symlink — replaced with a copy"
  fi

  preserved=0
  copied=0

  # Walk the source tree, skipping generated and runtime files.
  while IFS= read -r file; do
    rel="${file#"$src"/}"
    case "$rel" in
      state/.gitignore) ;;                                  # ship the tracked ignore file
      state/*) continue ;;                                  # never copy runtime state, any depth
      */__pycache__/*|__pycache__/*|*.pyc|.DS_Store|*/.DS_Store) continue ;;
    esac

    dst="$target/$rel"

    if is_user_data "$name/$rel" && [ -f "$dst" ]; then
      if [ "$FORCE" -eq 1 ]; then
        # Outside $target: a backup written inside the skill directory is
        # destroyed by a later uninstall, which then hands back the template.
        [ "$DRY_RUN" -eq 1 ] || ensure_stash
        bak="$STASH_DIR/$name/$rel"
        [ "$DRY_RUN" -eq 1 ] || { mkdir -p "$(dirname "$bak")" || die "cannot write backup to $bak"; \
                                  cp -p "$dst" "$bak" || die "cannot back up $name/$rel — nothing overwritten"; }
        warn "$name/$rel overwritten — original saved to ${STASH_DIR##*/}/$name/$rel"
      else
        preserved=$((preserved + 1))
        continue
      fi
    fi

    [ "$DRY_RUN" -eq 1 ] || { mkdir -p "$(dirname "$dst")"; cp "$file" "$dst"; }
    copied=$((copied + 1))
  done <<EOF
$(find "$src" -type f)
EOF

  [ "$DRY_RUN" -eq 1 ] || [ ! -d "$src/state" ] || mkdir -p "$target/state"
  [ "$DRY_RUN" -eq 1 ] || chmod +x "$target"/scripts/*.py 2>/dev/null || true

  if [ "$preserved" -gt 0 ]; then
    ok "skills/$name ${DIM}($copied files, $preserved personal file(s) kept)${N}"
  else
    ok "skills/$name ${DIM}($copied files)${N}"
  fi
}

# Refuse to install a checkout onto itself. Without this, `--dest <the clone>`
# makes $target and $src the same path: link mode rm -rf's each source skill and
# then creates a self-referential symlink, and copy mode deletes the agent file
# it is about to copy. Both report success while destroying the source.
# Absolute, symlink-resolved path for something that may not exist yet: walk up
# to the nearest existing ancestor, resolve that, then re-append the remainder.
# A destination is usually created by this script, so it is normally absent, and
# a guard that gives up on a missing path is a guard that never fires.
resolve_path() {
  rp_p="$1"; rp_suffix=""
  while [ ! -e "$rp_p" ] && [ "$rp_p" != "/" ] && [ "$rp_p" != "." ]; do
    rp_suffix="/$(basename "$rp_p")$rp_suffix"
    rp_p=$(dirname "$rp_p")
  done
  rp_base=$(CDPATH='' cd -- "$rp_p" 2>/dev/null && pwd -P) || return 1
  printf '%s%s' "$rp_base" "$rp_suffix"
}

assert_not_overlapping() {
  # $1 = source root, $2 = destination root
  a=$(resolve_path "$1") || return 0
  b=$(resolve_path "$2") || return 0
  case "$b" in
    "$a") die "--dest is the source checkout itself ($b).
       Installing a clone onto itself would destroy it. Use --dest ~/.copilot, or
       run --link from the clone with the default destination." ;;
    "$a"/*) die "--dest ($b) is inside the source checkout ($a).
       That would install the clone into itself. Choose a destination outside it." ;;
  esac
  case "$a" in
    "$b"/*) die "the source checkout ($a) is inside --dest ($b).
       Uninstall or reinstall would delete your clone. Choose a different destination." ;;
  esac
}

cmd_install() {
  resolve_source
  assert_not_overlapping "$SRC" "$DEST"

  step "Installing to $DEST"
  info "  ${DIM}source: $SRC${N}"
  info "  ${DIM}mode:   $MODE${N}"
  [ "$DRY_RUN" -eq 1 ] && warn "dry run — nothing will be written"

  if [ "$MODE" = "link" ] && [ -n "$TMP_DIR" ]; then
    die "--link needs a real clone; it can't link to a temporary download"
  fi

  step "Agent"
  install_agent

  step "Skills"
  selected=""
  for s in $SKILLS; do install_skill "$s"; selected="${selected:+$selected }$s"; done

  step "Checks"
  if py=$(python_bin); then
    ok "$py $("$py" -c 'import sys;print(".".join(map(str,sys.version_info[:3])))')"
  else
    warn "no python3 ≥ 3.9 on PATH — the bundled scripts won't run"
  fi

  if [ "$MODE" = "link" ]; then
    warn "linked install: preferences.md and state/ now live in your clone"
    info "     ${DIM}uncomment the personal-data lines in .gitignore before filling them in${N}"
  fi

  write_manifest "$selected"

  printf '\n%sInstalled%s %s\n\n' "$G$B" "$N" "$(source_version)"
  info "Next:"
  info "  1. ${B}\$EDITOR $DEST/skills/chief-of-staff/preferences.md${N}"
  info "     ${DIM}fill in About me, VIPs, the priority ladder, and your drafting voice${N}"
  info "  2. In Copilot CLI: ${B}Margo, brief me.${N}"
  info ""
  info "  ${DIM}Docs: https://github.com/$REPO_SLUG/blob/$BRANCH/docs/getting-started.md${N}"
}

# ---------------------------------------------------------------- update ----

cmd_update() {
  have=$(installed_version)
  if [ -z "$have" ]; then
    die "no Margo installation found in $DEST.
       If you installed before versions were tracked, run a normal install:
         ./install.sh --all --dest \"$DEST\""
  fi

  prev_skills=$(installed_field skills)
  prev_mode=$(installed_field mode)
  [ -n "$prev_skills" ] || prev_skills="$DEFAULT_SKILLS"

  step "Checking for updates"
  info "  ${DIM}installed: $have  ($prev_mode, $prev_skills)${N}"

  # A linked install tracks the clone directly — updating means pulling there,
  # not copying over the top of it, which would replace the links with files.
  if [ "$prev_mode" = "link" ]; then
    printf '\n%sThis is a linked install.%s Its files are your clone, so update it with:\n\n' "$B" "$N"
    printf '  git -C %s pull\n\n' "$(installed_field source)"
    return 0
  fi

  # Prefer the published version; fall back to whatever source we have locally,
  # which is what a clone or the staged copy under /usr/local/share/margo gives.
  latest=$(remote_version || true)
  if [ -n "$latest" ]; then
    ok "available: $latest ${DIM}(published)${N}"
  else
    resolve_source
    latest=$(source_version)
    warn "could not fetch the published version — using the local source ($latest)"
  fi

  if version_gt "$latest" "$have"; then
    printf '\n%sUpdate available:%s %s → %s\n' "$Y$B" "$N" "$have" "$latest"
    if [ "$CHECK_ONLY" -eq 1 ]; then
      info "  ${DIM}run: ./install.sh update${N}"
      return 0
    fi
  else
    printf '\n%sUp to date.%s  (%s)\n' "$G$B" "$N" "$have"
    [ "$CHECK_ONLY" -eq 1 ] && return 0
    if [ "$FORCE" -eq 1 ]; then
      warn "--force given: reinstalling anyway"
    else
      info "  ${DIM}re-run with --force to reinstall the same version${N}"
      return 0
    fi
  fi

  # Reinstall exactly what is already there. Adding skills the user never chose
  # would be a surprise, and updating is not the moment to spring one.
  SKILLS="$prev_skills"
  MODE="copy"
  cmd_install

  new=$(installed_version)
  if [ "$new" != "$have" ]; then
    printf '%sUpdated%s %s → %s\n' "$G$B" "$N" "$have" "$new"
  fi
}

# ------------------------------------------------------------- uninstall ----

cmd_uninstall() {
  step "Removing from $DEST"
  [ "$DRY_RUN" -eq 1 ] && warn "dry run — nothing will be removed"

  found=0
  stashed=0

  # Two passes. Archiving every skill before deleting any of them is what makes
  # "nothing was removed" true: interleaving them means a failure on the third
  # skill reports that message after the first two are already gone.
  for s in $ALL_SKILLS; do
    target="$DEST/skills/$s"
    [ -e "$target" ] || [ -L "$target" ] || continue
    found=1
    [ -L "$target" ] && continue          # a link owns no data of its own

    if ! archive_user_data "$s" "$target"; then
      die "could not archive personal files from skills/$s — nothing was removed.
       Check that $DEST is writable, then run uninstall again."
    fi
    [ "$ARCHIVED" -gt 0 ] && stashed=1
  done

  # Everything is safely archived; only now delete.
  for s in $ALL_SKILLS; do
    target="$DEST/skills/$s"
    [ -e "$target" ] || [ -L "$target" ] || continue

    if [ -L "$target" ]; then
      [ "$DRY_RUN" -eq 1 ] || rm -f "$target"
      ok "skills/$s ${DIM}(symlink removed, clone untouched)${N}"
    else
      [ "$DRY_RUN" -eq 1 ] || rm -rf "$target"
      ok "skills/$s"
    fi
  done

  agent="$DEST/agents/$AGENT_FILE"
  if [ -e "$agent" ] || [ -L "$agent" ]; then
    found=1
    [ "$DRY_RUN" -eq 1 ] || rm -f "$agent"
    ok "agents/$AGENT_FILE"
  fi

  # Install metadata, not user data: remove it rather than archiving it.
  [ "$DRY_RUN" -eq 1 ] || rm -f "$DEST/$MANIFEST_NAME"

  [ "$found" -eq 1 ] || { info "  ${DIM}nothing installed${N}"; return; }

  if [ "$stashed" -eq 1 ] && [ -n "$STASH_DIR" ]; then
    printf '\n%sPersonal files saved to:%s %s\n' "$B" "$N" "$STASH_DIR"
  fi
  printf '\n%sRemoved.%s\n' "$G$B" "$N"
}

# ---------------------------------------------------------------- status ----

describe() {
  # $1 = path
  if [ -L "$1" ]; then printf 'linked → %s' "$(readlink "$1")"
  elif [ -e "$1" ]; then printf 'installed'
  else printf 'not installed'
  fi
}

# Is a personalization file still a blank template? Placeholders mean "unfilled";
# for ledgers like commitments.md there are none, so count real table rows instead.
describe_user_file() {
  f="$1"
  if [ ! -f "$f" ]; then printf 'missing'; return; fi
  if grep -q '{[a-z_]*}' "$f" 2>/dev/null; then printf 'TEMPLATE'; return; fi
  if grep -q '^|' "$f" 2>/dev/null; then
    rows=$(awk '
      /^\|[-: |]*$/ { hdr[NR-1] = 1; next }
      /^\|/         { line[NR] = $0 }
      END { for (n in line) if (!(n in hdr)) {
              s = line[n]; gsub(/[| \t]/, "", s); if (length(s)) c++
            }
            print c + 0 }
    ' "$f")
    if [ "$rows" -eq 0 ]; then printf 'empty'; else printf '%s entr(y|ies)' "$rows"; fi
    return
  fi
  printf 'personalized'
}

cmd_status() {
  step "Margo — $DEST"

  have=$(installed_version)
  if [ -n "$have" ]; then
    ok "version    $have ${DIM}(installed $(installed_field installed_at), $(installed_field mode))${N}"
  else
    skip "version    not recorded ${DIM}(installed before version tracking, or by hand)${N}"
  fi
  info "             ${DIM}run 'update --check' to see if a newer version exists${N}"


  agent="$DEST/agents/$AGENT_FILE"
  if [ -e "$agent" ] || [ -L "$agent" ]; then
    ok "agent      $(describe "$agent")"
  else
    skip "agent      not installed"
  fi

  for s in $ALL_SKILLS; do
    target="$DEST/skills/$s"
    label=$(printf '%-16s' "$s")
    if [ -e "$target" ] || [ -L "$target" ]; then
      ok "$label $(describe "$target")"
    else
      skip "$label not installed"
      continue
    fi

    printf '%s\n' "$USER_DATA" | while IFS= read -r rel; do
      case "$rel" in "$s/"*) ;; *) continue ;; esac
      state=$(describe_user_file "$DEST/skills/$rel")
      base="${rel##*/}"
      case "$state" in
        TEMPLATE) printf '     %s· %-18s%s %snot filled in%s\n' "$DIM" "$base" "$N" "$Y" "$N" ;;
        missing)  printf '     %s· %-18s missing%s\n' "$DIM" "$base" "$N" ;;
        empty)    printf '     %s· %-18s%s %sempty%s\n' "$DIM" "$base" "$N" "$DIM" "$N" ;;
        *)        printf '     %s· %-18s%s %s%s%s\n' "$DIM" "$base" "$N" "$G" "$state" "$N" ;;
      esac
    done

    if [ -d "$target/state" ]; then
      n=$(find "$target/state" -type f ! -name '.gitignore' 2>/dev/null | wc -l | tr -d ' ')
      if [ "$n" != "0" ]; then
        printf '     %s· %-18s%s %s state file(s)\n' "$DIM" "state/" "$N" "$n"
      fi
    fi
  done

  step "Environment"
  if py=$(python_bin); then
    ok "python     $py $("$py" -c 'import sys;print(".".join(map(str,sys.version_info[:3])))')"
  else
    warn "python     not found (≥3.9 needed for bundled scripts)"
  fi
  if command -v gh >/dev/null 2>&1; then ok "gh         present"
  else skip "gh         not found ${DIM}(GitHub routine)${N}"; fi
  if command -v az >/dev/null 2>&1; then ok "az         present"
  else skip "az         not found ${DIM}(work-items routine)${N}"; fi
}

# ------------------------------------------------------------------ main ----

case "$COMMAND" in
  install)   cmd_install ;;
  update)    cmd_update ;;
  uninstall) cmd_uninstall ;;
  status)    cmd_status ;;
esac

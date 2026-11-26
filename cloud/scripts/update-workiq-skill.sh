#!/usr/bin/env bash
# Refresh Microsoft's Work IQ skill bundled into Margo Cloud's image (cloud/vendor/workiq).
#
# Desktop Margo gets this skill from the Work IQ plugin for Copilot CLI (/plugin install workiq@work-iq). Margo Cloud
# has no plugin host, so the image carries a pinned copy: the skill's Markdown, its license, and the commit it came
# from. Review the diff before committing; nothing here runs Microsoft's code.
#
# Usage: bash scripts/update-workiq-skill.sh [ref]    (from cloud/; ref defaults to main)
set -euo pipefail

REF="${1:-main}"
REPO="https://github.com/microsoft/work-iq.git"
HERE="$(cd "$(dirname "$0")/.." && pwd)"
DEST="$HERE/vendor/workiq"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

git clone --quiet --depth 1 --branch "$REF" "$REPO" "$WORK/work-iq" 2>/dev/null \
  || { git clone --quiet "$REPO" "$WORK/work-iq" && git -C "$WORK/work-iq" checkout --quiet "$REF"; }
SRC="$WORK/work-iq"
[ -f "$SRC/plugins/workiq/skills/workiq/SKILL.md" ] || { echo "error: no plugins/workiq/skills/workiq/SKILL.md at $REF" >&2; exit 1; }

COMMIT="$(git -C "$SRC" rev-parse HEAD)"
VERSION="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["version"])' "$SRC/plugins/workiq/.claude-plugin/plugin.json")"

rm -rf "$DEST/skills"
mkdir -p "$DEST/skills"
cp -R "$SRC/plugins/workiq/skills/workiq" "$DEST/skills/workiq"
cp "$SRC/LICENSE" "$DEST/LICENSE"
python3 - "$DEST/source.json" "$COMMIT" "$VERSION" "$REF" <<'PY'
import json, sys, datetime
path, commit, version, ref = sys.argv[1:5]
json.dump({
    "repository": "https://github.com/microsoft/work-iq",
    "path": "plugins/workiq/skills/workiq",
    "plugin": "workiq",
    "version": version,
    "ref": ref,
    "commit": commit,
    "updated": datetime.date.today().isoformat(),
}, open(path, "w"), indent=2)
open(path, "a").write("\n")
PY
echo "Bundled workiq $VERSION from $COMMIT into cloud/vendor/workiq. Review: git diff --stat -- cloud/vendor/workiq"

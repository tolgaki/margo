#!/usr/bin/env bash
#
# Builds Margo.pkg — a native macOS installer.
#
#   ./packaging/macos/build-pkg.sh [version]
#
# Signing and notarization are opt-in via the environment. Without them you get
# a working but unsigned package (Gatekeeper will warn on double-click):
#
#   MARGO_INSTALLER_IDENTITY   "Developer ID Installer: Your Name (TEAMID)"
#   MARGO_NOTARY_PROFILE       notarytool keychain profile name
#     ...or...
#   MARGO_NOTARY_APPLE_ID      Apple ID email
#   MARGO_NOTARY_TEAM_ID       Team ID
#   MARGO_NOTARY_PASSWORD      app-specific password
#
# Create the keychain profile once with:
#   xcrun notarytool store-credentials margo-notary \
#     --apple-id you@example.com --team-id TEAMID --password xxxx-xxxx-xxxx-xxxx
#
set -euo pipefail

# VERSION at the repo root is the single source of truth — the installers stamp
# it into the install manifest, and `update` compares against it.
HERE_EARLY=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
VERSION="${1:-$(tr -d ' \t\n\r' < "$HERE_EARLY/../../VERSION" 2>/dev/null || echo 0.1.0)}"
ID_PREFIX="com.github.tolgaki.margo"
INSTALL_LOCATION="/usr/local/share/margo"

# Skills the wizard offers as deselectable extras. chief-of-staff is not here:
# it ships in the core component and cannot be turned off.
OPTIONAL_SKILLS="decision-log"

HERE=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
REPO=$(cd "$HERE/../.." && pwd)
BUILD="$REPO/build/macos"
DIST="$REPO/dist"

B=$'\033[1m'; G=$'\033[32m'; Y=$'\033[33m'; N=$'\033[0m'
step() { printf '%s==>%s %s%s%s\n' $'\033[36m' "$N" "$B" "$*" "$N"; }
ok()   { printf '  %s✓%s %s\n' "$G" "$N" "$*"; }
warn() { printf '  %s!%s %s\n' "$Y" "$N" "$*"; }
die()  { printf '\033[31merror:\033[0m %s\n' "$*" >&2; exit 1; }

[ "$(uname -s)" = "Darwin" ] || die "this builds a macOS package; run it on macOS"

step "Margo $VERSION"
rm -rf "$BUILD"
mkdir -p "$BUILD/payload" "$DIST"

# ------------------------------------------------------------- 1. payload ---
# Everything a user needs, minus git metadata, caches and runtime state.

# Drive the payload from git, not from the filesystem. Walking the working tree
# ships whatever happens to be lying in it — .venv/, .idea/, scratch *.local.md,
# and tools/forbidden.local.txt, which is the maintainer's private denylist and
# by construction the densest collection of identifying strings they own. An
# exclusion list can only ever exclude what someone thought of; an allow-list of
# tracked files cannot ship an untracked secret.
step "Staging payload"
[ -d "$REPO/.git" ] || die "not a git checkout — refusing to build a payload from an unknown file set"

LIST="$BUILD/payload-files.txt"
ALL="$BUILD/tracked-files.txt"
# Tracked plus untracked-but-not-ignored, so this works before the first commit
# while still honouring .gitignore — which is what keeps .venv/, scratch
# *.local.md and tools/forbidden.local.txt out of the payload.
git -C "$REPO" ls-files --cached --others --exclude-standard > "$ALL"
[ -s "$ALL" ] || die "git listed no files to package"

# Tracked, minus repo infrastructure, minus all runtime state...
grep -vE '^(\.github|packaging)/' "$ALL" \
  | grep -vE '^skills/[^/]+/state/' > "$LIST"
# ...but keep the tracked state/.gitignore so the directory ships with its rule.
grep -E '^skills/[^/]+/state/\.gitignore$' "$ALL" >> "$LIST" || true

rsync -a --files-from="$LIST" "$REPO/" "$BUILD/payload/"

# Belt and braces: nothing untracked may exist in the payload.
if git -C "$REPO" status --porcelain --ignored=matching -- . \
     | grep -E '^!! ' | sed 's/^...//' \
     | while IFS= read -r f; do [ -e "$BUILD/payload/$f" ] && printf '%s\n' "$f"; done \
     | grep -q .; then
  die "payload contains gitignored files — refusing to build"
fi

chmod +x "$BUILD/payload/install.sh"
find "$BUILD/payload/skills" -name '*.py' -exec chmod +x {} \;

# Strip what extended attributes we can. com.apple.provenance is applied by the
# OS and cannot be removed; it rides in the archive as an AppleDouble "._" entry
# and is converted back to an xattr on extract, so no "._" files land on disk.
xattr -cr "$BUILD/payload" 2>/dev/null || true
find "$BUILD/payload" -name '._*' -delete
ok "$(find "$BUILD/payload" -type f | wc -l | tr -d ' ') files"

# ------------------------------------------------------------ 2. scripts ---
# One postinstall per component, each installing its own skill via install.sh.

make_scripts() {
  # $1 = component name, $2 = comma-free skill list
  d="$BUILD/scripts-$1"
  mkdir -p "$d"
  sed "s|__SKILLS__|$2|g" "$HERE/scripts/postinstall" > "$d/postinstall"
  chmod +x "$d/postinstall"
}

step "Generating component scripts"
make_scripts core "chief-of-staff"
for c in $OPTIONAL_SKILLS; do make_scripts "$c" "$c"; done
ok "$(( 1 + $(printf '%s\n' $OPTIONAL_SKILLS | wc -w | tr -d ' ') )) components"

# ---------------------------------------------------------- 3. components ---

step "Building component packages"

# pkgbuild prints a harmless "write: Permission denied" on recent macOS while
# probing for bundle components. Filter it; keep every other diagnostic.
quiet_pkgbuild() {
  pkgbuild "$@" 2>&1 >/dev/null | grep -v '^write: Permission denied$' || true
}

quiet_pkgbuild \
  --root "$BUILD/payload" \
  --install-location "$INSTALL_LOCATION" \
  --scripts "$BUILD/scripts-core" \
  --identifier "$ID_PREFIX.core" \
  --version "$VERSION" \
  "$BUILD/margo-core.pkg"
[ -f "$BUILD/margo-core.pkg" ] || die "pkgbuild produced no core package"
ok "margo-core.pkg"

for c in $OPTIONAL_SKILLS; do
  quiet_pkgbuild \
    --nopayload \
    --scripts "$BUILD/scripts-$c" \
    --identifier "$ID_PREFIX.$c" \
    --version "$VERSION" \
    "$BUILD/margo-$c.pkg"
  [ -f "$BUILD/margo-$c.pkg" ] || die "pkgbuild produced no $c package"
  ok "margo-$c.pkg"
done

# ----------------------------------------------------------- 4. resources ---

step "Preparing resources"
RES="$BUILD/resources"
mkdir -p "$RES"
cp "$HERE/resources/welcome.html"    "$RES/"
cp "$HERE/resources/conclusion.html" "$RES/"
cp "$REPO/LICENSE"                   "$RES/LICENSE.txt"
sed "s|__VERSION__|$VERSION|g" "$HERE/distribution.xml" > "$BUILD/distribution.xml"
ok "welcome, license, conclusion"

# ------------------------------------------------------------- 5. product ---

step "Building product archive"
UNSIGNED="$BUILD/Margo-unsigned.pkg"
productbuild \
  --distribution "$BUILD/distribution.xml" \
  --resources "$RES" \
  --package-path "$BUILD" \
  "$UNSIGNED" >/dev/null
ok "$(basename "$UNSIGNED")"

# ------------------------------------------------------------- 6. signing ---

FINAL="$DIST/Margo-$VERSION.pkg"

if [ -n "${MARGO_INSTALLER_IDENTITY:-}" ]; then
  step "Signing"
  productsign --sign "$MARGO_INSTALLER_IDENTITY" "$UNSIGNED" "$FINAL"
  pkgutil --check-signature "$FINAL" | sed 's/^/  /'
  ok "signed"
else
  cp "$UNSIGNED" "$FINAL"
  warn "unsigned — set MARGO_INSTALLER_IDENTITY to sign"
  warn "users will need to right-click → Open, or run: sudo installer -pkg Margo-$VERSION.pkg -target /"
fi

# -------------------------------------------------------- 7. notarization ---

notarize() {
  if [ -n "${MARGO_NOTARY_PROFILE:-}" ]; then
    xcrun notarytool submit "$FINAL" --keychain-profile "$MARGO_NOTARY_PROFILE" --wait
  elif [ -n "${MARGO_NOTARY_APPLE_ID:-}" ]; then
    xcrun notarytool submit "$FINAL" \
      --apple-id "$MARGO_NOTARY_APPLE_ID" \
      --team-id "${MARGO_NOTARY_TEAM_ID:?MARGO_NOTARY_TEAM_ID required}" \
      --password "${MARGO_NOTARY_PASSWORD:?MARGO_NOTARY_PASSWORD required}" \
      --wait
  else
    return 1
  fi
}

if [ -n "${MARGO_INSTALLER_IDENTITY:-}" ]; then
  if notarize; then
    step "Stapling"
    xcrun stapler staple "$FINAL"
    xcrun stapler validate "$FINAL" && ok "notarized and stapled"
  else
    warn "not notarized — set MARGO_NOTARY_PROFILE (or MARGO_NOTARY_APPLE_ID/TEAM_ID/PASSWORD)"
  fi
fi

# --------------------------------------------------------------- 8. done ---

printf '\n%sBuilt.%s  %s  (%s)\n\n' "$G$B" "$N" "$FINAL" "$(du -h "$FINAL" | cut -f1 | tr -d ' ')"
echo "Verify without installing:"
echo "  installer -pkgexpr /dev/null -pkg \"$FINAL\" -target / 2>/dev/null; pkgutil --payload-files \"$FINAL\" | head"
echo
echo "Install from the command line:"
echo "  sudo installer -pkg \"$FINAL\" -target /"

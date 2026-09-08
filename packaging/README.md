# Packaging

Native installers for macOS and Windows.

[Developer journey](../docs/development/README.md) · [Contribution rules](../CONTRIBUTING.md) ·
[User setup and recovery](../docs/how-to/setup-and-migration.md)

Both are thin wrappers. They stage the repo, then delegate the real work to
`install.sh` / `install.ps1`, so there is **one implementation** of the
merge-and-preserve logic shared by the command line, the `.pkg` and the `.exe`.
Fix a bug in the engine and all three surfaces get it.

```
packaging/
  macos/
    build-pkg.sh          Builds dist/Margo-<version>.pkg
    distribution.xml      Wizard layout and the skill checkboxes
    scripts/postinstall    Template; __SKILLS__ is substituted per component
    resources/            welcome.html, conclusion.html
  windows/
    build-exe.ps1         Builds dist/Margo-<version>-setup.exe
    margo.iss             Inno Setup script
    welcome.txt           Shown before the license page
```

---

## What the installers actually do

| | macOS | Windows |
|---|---|---|
| Staged copy | `/usr/local/share/margo` | `%LOCALAPPDATA%\Margo` |
| Installed to | `~/.copilot` | `%USERPROFILE%\.copilot` |
| Privileges | admin (writes to `/usr/local`), then drops to the console user | none — per-user, no UAC |
| Optional skills | separate component packages | Inno components |
| Uninstall | `/usr/local/share/margo/install.sh uninstall` | Settings → Apps |

The staged copy is deliberate: it means `install.sh status`, `uninstall` and the
docs are available later without a clone.

Both installers preserve `preferences.md`, `commitments.md`, `config.md` and legacy state by
default. `--force`/`-Force` explicitly replaces personal files after backup; do not use it for
ordinary upgrades. The private account-scoped `margo/` directory is outside the payload and is
retained on uninstall.

The source payload includes the SQLite helpers, doctor, capacity calculator and optional action-desk
renderer source. The native skill checkboxes do not automatically enable the renderer; run the
staged installer with `--action-desk`/`-ActionDesk` to opt in, then reload extensions.
Copying automation files does not synchronise the app's saved workflow prompts.

The current source also includes private memory and task-run helpers. The generic skill payload
copies these modules and their references; no separate background service is installed. The
task-progress renderer is part of the same optional extension. Its views do not initialize
account state, and copying the code does not enable task collection, memory capture or schedules.

Before a release, the feature catalog, generated guide navigation and synthetic journey contracts
must agree, and installed-copy scenarios must preserve existing memory and customized preferences.
Native packagers currently enumerate `git ls-files --cached --others --exclude-standard`:
**tracked files and non-ignored untracked files are candidates**, using their working-tree
contents. They then apply explicit infrastructure/runtime exclusions. This is not a
tracked-only or committed-only payload guarantee. Build from a clean, sanitized clone; inspect
both the source list and resulting payload, including filenames and modified templates.

---

## Versioning

`VERSION` at the repo root is the single source of truth. Both packagers default
to it, and both installers stamp it into `~/.copilot/.margo-install` so
`install.sh update` can tell whether the machine is behind. Bump that file in the
same commit as the tag.

Copy updates compare the remote commit as well as `VERSION` and download that pinned commit,
not the staged native package's older source. `update --reinstall` / `update -Reinstall` safely
refreshes the same revision. Remote failures never fall back to a stale staged payload.

The install also records managed-file hashes in `.margo-files.json` and source revision when
available. These describe the copied files, not a signed attestation. Customised automation
prompts may intentionally differ from upstream; reconcile them explicitly.

## Building

### macOS

Needs macOS's `pkgbuild` and `productbuild`, plus Git and the shell tools used by the script
(`rsync`, `xattr`, etc.). Use a conventional clone: the current macOS script requires a
`.git` **directory** and rejects linked worktrees with a `.git` file. It recreates
`build/macos/`; do not put unrelated work there.

```bash
./packaging/macos/build-pkg.sh  # Defaults to VERSION.
```

Signing and notarization are opt-in. An unsigned package may be blocked by Gatekeeper; do not
present it as verified or tell users to ignore their organization's security policy.

```bash
export MARGO_INSTALLER_IDENTITY="Developer ID Installer: Your Name (TEAMID)"
export MARGO_NOTARY_PROFILE="margo-notary"
./packaging/macos/build-pkg.sh
```

Create the notary profile once:

```bash
xcrun notarytool store-credentials margo-notary \
  --apple-id you@example.com --team-id TEAMID --password xxxx-xxxx-xxxx-xxxx
```

`MARGO_NOTARY_APPLE_ID` / `_TEAM_ID` / `_PASSWORD` work instead of a profile,
which is what CI uses.

> Note: `pkgbuild` prints `write: Permission denied` on recent macOS while
> probing for bundle components. It is harmless and filtered out; the build
> asserts the package exists rather than trusting the exit code.

### Windows

Needs [Inno Setup 6](https://jrsoftware.org/isinfo.php):

```powershell
# Optional packaging dependency, not needed for normal documentation/core work.
winget install --id JRSoftware.InnoSetup -e
.\packaging\windows\build-exe.ps1  # Defaults to VERSION.
```

Signing is opt-in via `MARGO_SIGN_THUMBPRINT`, or `MARGO_SIGN_PFX` +
`MARGO_SIGN_PASSWORD`. The script recreates `build/windows/`. Keep signing credentials outside
the checkout; do not put them in examples, logs or artifacts.

Windows may show SmartScreen warnings for unsigned or low-reputation downloads. Signing is
not a guarantee that every device policy permits installation.

---

## Verifying a build

```bash
# What lands on disk, and where
version="$(tr -d ' \t\r\n' < VERSION)"
pkg="dist/Margo-$version.pkg"
pkgutil --payload-files "$pkg"
# Choose a new inspection destination; pkgutil refuses an existing destination.
mkdir -p build/package-inspection
pkgutil --expand "$pkg" build/package-inspection/expanded
cat build/package-inspection/expanded/Distribution

# Signature and notarization
pkgutil --check-signature "$pkg"
xcrun stapler validate "$pkg"
```

```powershell
$version = (Get-Content VERSION -Raw).Trim()
Get-AuthenticodeSignature "dist\Margo-$version-setup.exe"
```

**Installation is a separate test.** Running a native `.pkg` or `.exe`, even silently, changes
its real destination in the table above. Use a disposable test machine/CI runner and explicit
authorization for that step, not your personal Copilot profile. For ordinary contribution
testing, use `install.sh --dest` / `install.ps1 -Dest` and the existing synthetic installer
tests described in the [developer journey](../docs/development/README.md).

The payload should contain **nothing** under `skills/*/state/` except the tracked
`.gitignore` — that directory holds real mailbox content, relationship notes and
1:1 agendas, and not all of it is JSON. CI fails the build if anything else appears.

> Keep exclusion semantics explicit. The macOS packager filters a Git-generated file list
> before `rsync --files-from`; Windows filters the list before copying. The shell/PowerShell
> installers have their own source walkers. Test nested state and personalization content
> on each surface rather than assuming one tool's pattern has the same meaning in another.

`._`-prefixed entries in `pkgutil --payload-files` are not files: they are how
extended attributes ride inside a cpio archive, and `installer` converts them
back to xattrs. Nothing extra lands on disk.

---

## Releasing

**Maintainer operation, not ordinary contribution setup.** `.github/workflows/release.yml`
builds both installers, signs them
if the secrets are configured, smoke-tests the Windows one on a clean runner,
and attaches everything plus `SHA256SUMS.txt` to the GitHub release.

After review and applicable CI gates, update `VERSION` and release notes in the intended
release commit. A separately authorized `v<version>` tag must match `VERSION`; the workflow
refuses a mismatch. Inspect the selected revision and tags before any explicitly authorized
push. Do not use a blanket commit or push-all-tags command that can include unrelated work.

`workflow_dispatch` builds artifacts using the supplied version and enforces the same
`VERSION` check. For a build-only rehearsal, dispatch from a branch: the publish job runs when
the selected ref begins `refs/tags/v`, so a dispatch against a release tag is not necessarily
publication-free. The workflow is not a substitute for the separate PR quality gates.

| Secret | For |
|---|---|
| `MACOS_CERT_P12_BASE64`, `MACOS_CERT_PASSWORD` | Importing the Developer ID Installer certificate |
| `MACOS_INSTALLER_IDENTITY` | The identity string to sign with |
| `MACOS_NOTARY_APPLE_ID`, `MACOS_NOTARY_TEAM_ID`, `MACOS_NOTARY_PASSWORD` | Notarization |
| `WINDOWS_CERT_PFX_BASE64`, `WINDOWS_CERT_PASSWORD` | Authenticode signing |

All signing secrets are optional. Missing secrets produce unsigned artifacts, not signed
ones; they do not waive the build, payload or platform checks. CI also builds native packages
without installing into a contributor's profile. Neither a local build nor a signature check
proves an upgrade preserved user data; use the installer preservation scenarios as well.

# Packaging

Native installers for macOS and Windows.

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

Neither installer ever overwrites `preferences.md`, `commitments.md`,
`config.md` or `state/`.

---

## Versioning

`VERSION` at the repo root is the single source of truth. Both packagers default
to it, and both installers stamp it into `~/.copilot/.margo-install` so
`install.sh update` can tell whether the machine is behind. Bump that file in the
same commit as the tag.

## Building

### macOS

Needs only what ships with macOS — `pkgbuild`, `productbuild`.

```bash
./packaging/macos/build-pkg.sh 1.0.0
```

Signing and notarization are opt-in. Unsigned still works; Gatekeeper just warns.

```bash
export MARGO_INSTALLER_IDENTITY="Developer ID Installer: Your Name (TEAMID)"
export MARGO_NOTARY_PROFILE="margo-notary"
./packaging/macos/build-pkg.sh 1.0.0
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
winget install --id JRSoftware.InnoSetup -e
.\packaging\windows\build-exe.ps1 1.0.0
```

Signing is opt-in via `MARGO_SIGN_THUMBPRINT`, or `MARGO_SIGN_PFX` +
`MARGO_SIGN_PASSWORD`.

Without a certificate SmartScreen will warn until the download builds
reputation. An EV certificate skips that; an OV one does not.

---

## Verifying a build

```bash
# What lands on disk, and where
pkgutil --payload-files dist/Margo-1.0.0.pkg
pkgutil --expand dist/Margo-1.0.0.pkg /tmp/x && cat /tmp/x/Distribution

# Signature and notarization
pkgutil --check-signature dist/Margo-1.0.0.pkg
xcrun stapler validate dist/Margo-1.0.0.pkg
```

```powershell
Get-AuthenticodeSignature dist\Margo-1.0.0-setup.exe
.\dist\Margo-1.0.0-setup.exe /VERYSILENT /SUPPRESSMSGBOXES
```

The payload should contain **nothing** under `skills/*/state/` except the tracked
`.gitignore` — that directory holds real mailbox content, relationship notes and
1:1 agendas, and not all of it is JSON. CI fails the build if anything else appears.

> The exclusion patterns are not interchangeable across tools: rsync's `*` does
> not cross `/`, a shell `case` `*` does, and a regex `.` does. `build-pkg.sh`
> therefore uses `**`. A pattern that looks right in one and wrong in another is
> how nested state slipped past the packager while the installer excluded it.

`._`-prefixed entries in `pkgutil --payload-files` are not files: they are how
extended attributes ride inside a cpio archive, and `installer` converts them
back to xattrs. Nothing extra lands on disk.

---

## Releasing

Tag and push. `.github/workflows/release.yml` builds both installers, signs them
if the secrets are configured, smoke-tests the Windows one on a clean runner,
and attaches everything plus `SHA256SUMS.txt` to the GitHub release.

```bash
# 1. Bump VERSION in the same commit you tag. The workflow refuses to build if
#    the tag and VERSION disagree — otherwise you would ship Margo-1.1.0.pkg
#    whose install manifest claims 1.0.0, and `update --check` would never fire.
echo 1.1.0 > VERSION
git commit -am "Release 1.1.0"

# 2. Tag and push.
git tag v1.1.0 && git push origin main --tags
```

`workflow_dispatch` builds the same artifacts on demand for a dry run; it applies
the same tag/VERSION check against the input you give it.

| Secret | For |
|---|---|
| `MACOS_CERT_P12_BASE64`, `MACOS_CERT_PASSWORD` | Importing the Developer ID Installer certificate |
| `MACOS_INSTALLER_IDENTITY` | The identity string to sign with |
| `MACOS_NOTARY_APPLE_ID`, `MACOS_NOTARY_TEAM_ID`, `MACOS_NOTARY_PASSWORD` | Notarization |
| `WINDOWS_CERT_PFX_BASE64`, `WINDOWS_CERT_PASSWORD` | Authenticode signing |

All are optional — without them the workflow still produces working unsigned
installers, so forks and pull requests are never blocked.

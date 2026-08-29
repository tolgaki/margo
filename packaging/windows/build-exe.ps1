<#
.SYNOPSIS
  Builds Margo-<version>-setup.exe — a native Windows installer.

.DESCRIPTION
  Stages the repo, then compiles packaging\windows\margo.iss with Inno Setup.

  Signing is opt-in via the environment. Without it you get a working but
  unsigned installer, and SmartScreen will warn until the download builds
  reputation:

    MARGO_SIGN_THUMBPRINT   certificate thumbprint in the current user store
    MARGO_SIGN_PFX          path to a .pfx file  (alternative to thumbprint)
    MARGO_SIGN_PASSWORD     password for the .pfx
    MARGO_SIGN_TIMESTAMP    timestamp URL (default: http://timestamp.digicert.com)

.EXAMPLE
  .\packaging\windows\build-exe.ps1 1.0.0
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$Version,
    [string]$InnoPath
)

$ErrorActionPreference = 'Stop'

$Here = Split-Path -Parent $PSCommandPath
$Repo = (Resolve-Path (Join-Path $Here '..\..')).Path
$Build = Join-Path $Repo 'build\windows'
$Payload = Join-Path $Build 'payload'
$Dist = Join-Path $Repo 'dist'

function Step { param($m) Write-Host "==> " -NoNewline -ForegroundColor Cyan; Write-Host $m -ForegroundColor White }
function Ok   { param($m) Write-Host "  OK " -NoNewline -ForegroundColor Green; Write-Host $m }
function Warn { param($m) Write-Host "  !  " -NoNewline -ForegroundColor Yellow; Write-Host $m }
function Die  { param($m) Write-Host "error: $m" -ForegroundColor Red; exit 1 }

if (-not $Version) {
    # VERSION at the repo root is the single source of truth — the installers
    # stamp it into the install manifest, and `update` compares against it.
    $vf = Join-Path $Repo 'VERSION'
    $Version = if (Test-Path $vf) { (Get-Content $vf -Raw).Trim() } else { '0.1.0' }
}
if ($Version -notmatch '^\d+\.\d+(\.\d+)?(\.\d+)?$') {
    Die "version must be numeric (e.g. 1.0.0), got '$Version'"
}

Step "Margo $Version"

# ------------------------------------------------------------ 1. locate ISCC ---

if (-not $InnoPath) {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )
    $InnoPath = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $InnoPath) {
        $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        if ($cmd) { $InnoPath = $cmd.Source }
    }
}
if (-not $InnoPath -or -not (Test-Path $InnoPath)) {
    Die @"
Inno Setup 6 not found. Install it with one of:
    winget install --id JRSoftware.InnoSetup -e
    choco install innosetup
...or pass -InnoPath 'C:\Path\To\ISCC.exe'.
"@
}
Ok "ISCC: $InnoPath"

# --------------------------------------------------------------- 2. payload ---

Step "Staging payload"
if (Test-Path $Build) { Remove-Item -Recurse -Force $Build }
New-Item -ItemType Directory -Path $Payload -Force | Out-Null
New-Item -ItemType Directory -Path $Dist -Force | Out-Null

# Drive the payload from git, not from the filesystem. Walking the working tree
# ships whatever happens to be lying in it — .venv, .idea, scratch *.local.md,
# and tools/forbidden.local.txt, which is the maintainer's private denylist and
# by construction the densest collection of identifying strings they own. An
# exclusion list can only exclude what someone thought of; an allow-list of
# tracked files cannot ship an untracked secret.
if (-not (Test-Path (Join-Path $Repo '.git'))) {
    Die "not a git checkout - refusing to build a payload from an unknown file set"
}

# Tracked plus untracked-but-not-ignored, so this works before the first commit
# while still honouring .gitignore.
$tracked = & git -C $Repo ls-files --cached --others --exclude-standard
if ($LASTEXITCODE -ne 0 -or -not $tracked) { Die "git listed no files to package" }

$excludeDirs = @('.github', 'packaging')
$count = 0

foreach ($rel in $tracked) {
    $parts = $rel -split '[\\/]'
    if ($parts | Where-Object { $excludeDirs -contains $_ }) { continue }
    # Runtime state holds real mailbox content; never ship it. The whole
    # subtree, any depth and any file type — but keep the tracked .gitignore.
    if ($rel -match '(^|[\\/])state[\\/]' -and $rel -notmatch '[\\/]state[\\/]\.gitignore$') { continue }

    $src = Join-Path $Repo $rel
    if (-not (Test-Path $src)) { continue }
    $dst = Join-Path $Payload $rel
    $parent = Split-Path -Parent $dst
    if (-not (Test-Path $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    Copy-Item -Force $src $dst
    $count++
}

# Belt and braces: nothing untracked or ignored may exist in the payload.
$dirty = & git -C $Repo status --porcelain --ignored=matching -- .
foreach ($line in $dirty) {
    if ($line -notmatch '^!! ') { continue }
    $f = $line.Substring(3)
    if (Test-Path (Join-Path $Payload $f)) {
        Die "payload contains gitignored file: $f"
    }
}

foreach ($required in @('install.ps1', 'agents\margo.agent.md', 'skills\chief-of-staff\SKILL.md', 'LICENSE')) {
    if (-not (Test-Path (Join-Path $Payload $required))) { Die "payload is missing $required" }
}
Ok "$count files"

# --------------------------------------------------------------- 3. compile ---

Step "Compiling installer"
$issArgs = @(
    "/DMyAppVersion=$Version",
    "/DPayloadDir=$Payload",
    (Join-Path $Here 'margo.iss')
)

# Signing, if configured.
$signTool = $null
if ($env:MARGO_SIGN_THUMBPRINT) {
    $signTool = 'signtool.exe sign /fd sha256 /sha1 ' + $env:MARGO_SIGN_THUMBPRINT
} elseif ($env:MARGO_SIGN_PFX) {
    $signTool = 'signtool.exe sign /fd sha256 /f "' + $env:MARGO_SIGN_PFX + '"'
    if ($env:MARGO_SIGN_PASSWORD) { $signTool += ' /p "' + $env:MARGO_SIGN_PASSWORD + '"' }
}
if ($signTool) {
    $ts = if ($env:MARGO_SIGN_TIMESTAMP) { $env:MARGO_SIGN_TIMESTAMP } else { 'http://timestamp.digicert.com' }
    $signTool += ' /tr ' + $ts + ' /td sha256 $f'
    $issArgs = @("/Ssigntool=$signTool", '/DSignOutput=1') + $issArgs
    Ok "signing enabled"
} else {
    Warn "unsigned - set MARGO_SIGN_THUMBPRINT or MARGO_SIGN_PFX to sign"
    Warn "SmartScreen will warn users until the download builds reputation"
}

& $InnoPath @issArgs
if ($LASTEXITCODE -ne 0) { Die "ISCC failed with exit code $LASTEXITCODE" }

$out = Join-Path $Dist "Margo-$Version-setup.exe"
if (-not (Test-Path $out)) { Die "expected output not found: $out" }

$size = '{0:N1} MB' -f ((Get-Item $out).Length / 1MB)
Write-Host ""
Write-Host "Built." -ForegroundColor Green -NoNewline
Write-Host "  $out  ($size)"
Write-Host ""
Write-Host "Test it with:  `"$out`" /SILENT"
Write-Host "Uninstall:     via Settings > Apps, or the Start menu shortcut"

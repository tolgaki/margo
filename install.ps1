<#
.SYNOPSIS
  Margo installer — Windows.

.DESCRIPTION
  Installs the Margo agent and chief-of-staff skills into your Copilot CLI
  directory. Your personal files (preferences.md, commitments.md, config.md,
  state/) are never overwritten or deleted, so reinstalling to upgrade is safe.

.EXAMPLE
  .\install.ps1
  Install the agent and the chief-of-staff skill.

.EXAMPLE
  .\install.ps1 -All
  Include decision-log and partner-updates.

.EXAMPLE
  .\install.ps1 status
  Show what is currently installed.

.EXAMPLE
  irm https://raw.githubusercontent.com/tolgaki/margo/main/install.ps1 | iex
  Install without cloning.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('install', 'update', 'uninstall', 'status')]
    [string]$Command = 'install',

    [switch]$All,
    [string[]]$Skills,
    [switch]$Link,
    [string]$Dest,
    [switch]$Force,
    [switch]$DryRun,
    [switch]$Check,
    [Alias('y')][switch]$Yes,
    [string]$Branch = 'main'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$RepoSlug     = 'tolgaki/margo'
$AgentFile    = 'margo.agent.md'
# Where the installed copy records what it is, so `update` and `status` can tell
# you whether you are behind. Plain key=value: parsing JSON would add a runtime
# dependency to a script whose whole job is to work before anything is set up.
$ManifestName = '.margo-install'
$AllSkills    = @('chief-of-staff', 'decision-log', 'partner-updates')
$DefaultSkill = @('chief-of-staff')

# Files that hold your data, not ours. Never overwritten, never deleted.
$UserData = @(
    'chief-of-staff/preferences.md',
    'chief-of-staff/commitments.md',
    'decision-log/config.md',
    'partner-updates/config.md'
)

if (-not $Dest) {
    $Dest = if ($env:MARGO_DEST) { $env:MARGO_DEST } else { Join-Path $HOME '.copilot' }
}

$script:TmpDir = $null

# ---------------------------------------------------------------- output ----

function Write-Step { param($m) Write-Host "==> " -NoNewline -ForegroundColor Cyan; Write-Host $m -ForegroundColor White }
function Write-Ok   { param($m) Write-Host "  " -NoNewline; Write-Host "OK " -NoNewline -ForegroundColor Green; Write-Host $m }
function Write-Skip { param($m) Write-Host "  .  $m" -ForegroundColor DarkGray }
function Write-Warn { param($m) Write-Host "  " -NoNewline; Write-Host "!  " -NoNewline -ForegroundColor Yellow; Write-Host $m }
function Write-Dim  { param($m) Write-Host "     $m" -ForegroundColor DarkGray }
function Fail       { param($m) Write-Host "error: $m" -ForegroundColor Red; exit 1 }

function Confirm-Action {
    param([string]$Message)
    if ($Yes -or $DryRun) { return $true }
    # No usable console: take the [y/N] default, which is No. Read-Host throws
    # on a genuinely non-interactive host, and with $ErrorActionPreference='Stop'
    # that would abort the whole install mid-way instead of skipping one skill.
    if (-not [Environment]::UserInteractive) {
        Write-Host $Message
        Write-Warn "not an interactive host - assuming no"
        return $false
    }
    try {
        $reply = Read-Host "  $Message [y/N]"
    } catch {
        Write-Warn "cannot prompt - assuming no"
        return $false
    }
    return $reply -match '^(y|yes)$'
}

# --------------------------------------------------------------- helpers ----

function Resolve-Skills {
    if ($All) { return $AllSkills }
    if ($Skills) {
        $wanted = @()
        foreach ($s in $Skills) { $wanted += ($s -split ',') }
        $wanted = $wanted | ForEach-Object { $_.Trim() } | Where-Object { $_ }
        foreach ($s in $wanted) {
            if ($AllSkills -notcontains $s) {
                Fail "unknown skill: $s  (available: $($AllSkills -join ', '))"
            }
        }
        return $wanted
    }
    return $DefaultSkill
}

function Get-Source {
    # The directory this script lives in, or a fresh download.
    $scriptDir = $null
    if ($PSCommandPath) { $scriptDir = Split-Path -Parent $PSCommandPath }
    if ($scriptDir -and (Test-Path (Join-Path $scriptDir "agents/$AgentFile"))) {
        return $scriptDir
    }

    # Piped from irm, or run from outside a checkout.
    Write-Step "Downloading $RepoSlug@$Branch"
    $script:TmpDir = Join-Path ([IO.Path]::GetTempPath()) ("margo-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $script:TmpDir -Force | Out-Null
    $zip = Join-Path $script:TmpDir 'margo.zip'

    try {
        $progress = $ProgressPreference
        $ProgressPreference = 'SilentlyContinue'
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri "https://codeload.github.com/$RepoSlug/zip/refs/heads/$Branch" -OutFile $zip -UseBasicParsing
        $ProgressPreference = $progress
        Expand-Archive -Path $zip -DestinationPath $script:TmpDir -Force
    } catch {
        Fail "download failed: $($_.Exception.Message)`n       Clone the repo and run .\install.ps1 instead."
    }

    $root = Get-ChildItem -Path $script:TmpDir -Directory | Where-Object { $_.Name -like 'margo-*' } | Select-Object -First 1
    if (-not $root -or -not (Test-Path (Join-Path $root.FullName "agents/$AgentFile"))) {
        Fail "downloaded archive looks wrong"
    }
    Write-Ok "fetched to a temporary directory"
    return $root.FullName
}

function Remove-TempDir {
    if ($script:TmpDir -and (Test-Path $script:TmpDir)) {
        Remove-Item -Recurse -Force $script:TmpDir -ErrorAction SilentlyContinue
    }
}

function Test-IsLink {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return $false }
    $item = Get-Item $Path -Force
    return [bool]($item.Attributes -band [IO.FileAttributes]::ReparsePoint)
}

function Get-LinkTarget {
    param([string]$Path)
    $item = Get-Item $Path -Force
    if ($item.PSObject.Properties.Name -contains 'Target' -and $item.Target) {
        return ($item.Target | Select-Object -First 1)
    }
    return '?'
}

function Remove-Entry {
    # Removes a file, directory or reparse point without following links.
    param([string]$Path)
    if ($DryRun) { return }
    if (-not (Test-Path $Path)) { return }
    $item = Get-Item $Path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -and $item.PSIsContainer) {
        # Directory reparse point: delete the link, not the contents.
        [IO.Directory]::Delete($Path)
    } elseif ($item.PSIsContainer) {
        Remove-Item -Recurse -Force $Path
    } else {
        Remove-Item -Force $Path
    }
}

function New-Link {
    <#
      Windows symlinks need Developer Mode or elevation. Fall back to a junction
      for directories and a hard link for files — both work unprivileged on NTFS
      and both still reflect edits made in the clone.
    #>
    param([string]$Target, [string]$Source, [bool]$IsDirectory)

    if ($DryRun) { return 'symlink' }
    try {
        New-Item -ItemType SymbolicLink -Path $Target -Value $Source -ErrorAction Stop | Out-Null
        return 'symlink'
    } catch {
        try {
            $kind = if ($IsDirectory) { 'Junction' } else { 'HardLink' }
            New-Item -ItemType $kind -Path $Target -Value $Source -ErrorAction Stop | Out-Null
            return $kind.ToLower()
        } catch {
            Copy-Item -Recurse -Force $Source $Target
            return 'copy'
        }
    }
}

# Version of the source tree we are installing FROM.
function Get-SourceVersion {
    param([string]$Src)
    $f = Join-Path $Src 'VERSION'
    if (Test-Path $f) { return (Get-Content $f -Raw).Trim() }
    return 'unknown'
}

function Get-ManifestField {
    param([string]$Key)
    $f = Join-Path $Dest $ManifestName
    if (-not (Test-Path $f)) { return '' }
    foreach ($line in Get-Content $f) {
        if ($line -match "^$([regex]::Escape($Key))=(.*)$") { return $Matches[1] }
    }
    return ''
}

# Is $A newer than $B? Numeric dotted compare; any pre-release suffix is ignored,
# so 1.2.0-rc1 and 1.2.0 compare equal rather than unordered.
function Test-VersionGreater {
    param([string]$A, [string]$B)
    $na = ($A -replace '[^0-9.].*$', '')
    $nb = ($B -replace '[^0-9.].*$', '')
    if (-not $na) { return $false }
    if (-not $nb) { return $true }
    $pa = @($na -split '\.' | ForEach-Object { [int]($_ -as [int]) })
    $pb = @($nb -split '\.' | ForEach-Object { [int]($_ -as [int]) })
    for ($i = 0; $i -lt [Math]::Max($pa.Count, $pb.Count); $i++) {
        $x = if ($i -lt $pa.Count) { $pa[$i] } else { 0 }
        $y = if ($i -lt $pb.Count) { $pb[$i] } else { 0 }
        if ($x -gt $y) { return $true }
        if ($x -lt $y) { return $false }
    }
    return $false
}

function Write-Manifest {
    param([string]$Src, [string[]]$Skills)
    if ($DryRun) { return }
    New-Item -ItemType Directory -Path $Dest -Force | Out-Null
    $lines = @(
        "version=$(Get-SourceVersion -Src $Src)"
        "installed_at=$([DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ'))"
        "mode=$(if ($Link) { 'link' } else { 'copy' })"
        "skills=$($Skills -join ' ')"
        "source=$Src"
    )
    Set-Content -Path (Join-Path $Dest $ManifestName) -Value $lines
}

# The newest version published upstream. Empty if offline or unavailable.
function Get-RemoteVersion {
    try {
        $progress = $ProgressPreference
        $ProgressPreference = 'SilentlyContinue'
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $r = Invoke-WebRequest -UseBasicParsing -TimeoutSec 10 `
             -Uri "https://raw.githubusercontent.com/$RepoSlug/$Branch/VERSION"
        $ProgressPreference = $progress
        return ($r.Content).Trim()
    } catch { return '' }
}

function Join-Rel {
    # Join a repo-relative 'a/b/c' path onto a base using the native separator.
    param([string]$Base, [string]$Rel)
    $parts = $Rel -split '/'
    $p = $Base
    foreach ($part in $parts) { $p = Join-Path $p $part }
    return $p
}

# One archive directory per invocation, created lazily and atomically the first
# time something needs saving, and never reused. A name derived only from the
# second collides whenever two commands run in the same second — a -Force
# install followed by an uninstall then shared a directory, and the uninstall
# archived the freshly-installed template over the -Force backup of the user's
# original. CreateDirectory is the atomic test: it throws if the name is taken.
$script:StashDir = $null
function Get-StashDir {
    if ($script:StashDir) { return $script:StashDir }
    New-Item -ItemType Directory -Path $Dest -Force -ErrorAction SilentlyContinue | Out-Null
    $base = Join-Path $Dest "margo-personal-backup.$(Get-Date -Format 'yyyyMMddHHmmss')"
    $cand = $base
    $n = 2
    while ($true) {
        try {
            [IO.Directory]::CreateDirectory($cand) | Out-Null
            if (@(Get-ChildItem -LiteralPath $cand -Force -ErrorAction SilentlyContinue).Count -eq 0) { break }
        } catch { }
        if ($n -gt 999) { Fail "cannot create a unique backup directory under $Dest" }
        $cand = "$base-$n"
        $n++
    }
    $script:StashDir = $cand
    return $script:StashDir
}

# Everything in an installed skill that belongs to the user rather than to us:
# the personalization files, the ENTIRE state/ subtree at any depth and any file
# type, and any *.bak.* left by an older -Force. Swept from what is actually
# present — never from a hard-coded list, which is how Markdown state and -Force
# backups were previously destroyed while being reported as kept.
function Get-UserFiles {
    param([string]$Dir, [string]$Name)
    $out = New-Object System.Collections.Generic.List[string]
    if (-not (Test-Path $Dir)) { return $out.ToArray() }
    $prefix = (Resolve-Path $Dir).Path.TrimEnd('\', '/')

    foreach ($rel in $UserData | Where-Object { $_ -like "$Name/*" }) {
        $sub = $rel.Substring($Name.Length + 1)
        if (Test-Path (Join-Rel $Dir $sub)) { $out.Add($sub) }
    }

    $stateDir = Join-Path $Dir 'state'
    if (Test-Path $stateDir) {
        foreach ($f in Get-ChildItem -Path $stateDir -Recurse -File -Force -ErrorAction SilentlyContinue) {
            $sub = $f.FullName.Substring($prefix.Length + 1) -replace '\\', '/'
            if ($sub -ne 'state/.gitignore') { $out.Add($sub) }
        }
    }

    foreach ($f in Get-ChildItem -Path $Dir -Recurse -File -Force -Filter '*.bak.*' -ErrorAction SilentlyContinue) {
        $out.Add(($f.FullName.Substring($prefix.Length + 1) -replace '\\', '/'))
    }
    return $out.ToArray()
}

# Archive every user file out of an installed skill before it is deleted.
# Throws if any file cannot be archived; the caller must not delete in that case.
function Save-UserData {
    param([string]$Name, [string]$Dir)
    $n = 0
    foreach ($sub in @(Get-UserFiles -Dir $Dir -Name $Name)) {
        if (-not $DryRun) {
            $dst = Join-Rel (Get-StashDir) "$Name/$sub"
            New-Item -ItemType Directory -Path (Split-Path -Parent $dst) -Force -ErrorAction Stop | Out-Null
            # Never clobber an existing archive entry.
            if (Test-Path $dst) { throw "archive entry already exists: $dst" }
            Copy-Item -Force (Join-Rel $Dir $sub) $dst -ErrorAction Stop
        }
        $n++
        Write-Skip "kept $Name/$sub"
    }
    return $n
}

# Absolute, resolved path for something that may not exist yet: walk up to the
# nearest existing ancestor, resolve that, then re-append the remainder. The
# destination is normally created by this script, and a guard that gives up on a
# missing path is a guard that never fires.
function Resolve-PathAlways {
    param([string]$Path)

    # Split into the nearest existing ancestor plus the not-yet-created tail.
    # The destination is normally created by this script, so it is usually
    # absent, and a guard that gives up on a missing path never fires.
    $suffix = ''
    $p = [IO.Path]::GetFullPath($Path)
    while ($p -and -not (Test-Path -LiteralPath $p)) {
        $leaf = Split-Path -Leaf $p
        if (-not $leaf) { break }
        $suffix = [IO.Path]::DirectorySeparatorChar + $leaf + $suffix
        $parent = Split-Path -Parent $p
        if (-not $parent -or $parent -eq $p) { break }
        $p = $parent
    }
    if (-not (Test-Path -LiteralPath $p)) { return $null }

    # Canonicalize the existing part. .NET GetFullPath normalizes "." and ".."
    # but does NOT follow symlinked ancestors, so on Unix /tmp/x and
    # /private/tmp/x compare unequal and an equality-based guard never fires.
    # Delegate to the platform resolver there; on Windows GetFullPath is enough.
    $canon = $null
    $onWindows = $true
    if (Test-Path Variable:IsWindows) { $onWindows = $IsWindows }
    if (-not $onWindows) {
        try {
            $canon = (& /usr/bin/env realpath -- "$p" 2>$null | Select-Object -First 1)
        } catch { $canon = $null }
    }
    if (-not $canon) { $canon = [IO.Path]::GetFullPath($p) }

    return ($canon.TrimEnd('\', '/') + $suffix)
}

# Refuse to install a checkout onto itself. Without this, -Dest <the clone> makes
# target and source the same path: link mode removes each source skill and then
# creates a self-referential link, and copy mode deletes the agent file it is
# about to copy. Both report success while destroying the source.
function Assert-NotOverlapping {
    param([string]$Source, [string]$Destination)
    $a = Resolve-PathAlways $Source
    $b = Resolve-PathAlways $Destination
    if (-not $a -or -not $b) { return }
    $sep = [IO.Path]::DirectorySeparatorChar
    if ($a -eq $b) {
        Fail ("-Dest is the source checkout itself ($b).`n" +
              "       Installing a clone onto itself would destroy it. Use the default`n" +
              "       destination, or -Dest pointing outside the clone.")
    }
    if ($b.StartsWith($a + $sep)) {
        Fail ("-Dest ($b) is inside the source checkout ($a).`n" +
              "       That would install the clone into itself. Choose a destination outside it.")
    }
    if ($a.StartsWith($b + $sep)) {
        Fail ("the source checkout ($a) is inside -Dest ($b).`n" +
              "       Uninstall or reinstall would delete your clone. Choose a different destination.")
    }
}

function Get-PythonInfo {
    foreach ($exe in @('python3', 'python', 'py')) {
        $cmd = Get-Command $exe -ErrorAction SilentlyContinue
        if (-not $cmd) { continue }
        try {
            $v = & $exe -c "import sys;print('.'.join(map(str,sys.version_info[:3])) if sys.version_info>=(3,9) else '')" 2>$null
            if ($v) { return "$exe $v" }
        } catch { }
    }
    return $null
}

# --------------------------------------------------------------- install ----

function Install-Agent {
    param([string]$Src)
    $agentDir = Join-Path $Dest 'agents'
    $target   = Join-Path $agentDir $AgentFile
    $source   = Join-Path $Src "agents/$AgentFile"

    if (-not $DryRun) { New-Item -ItemType Directory -Path $agentDir -Force | Out-Null }
    Remove-Entry $target

    if ($Link) {
        $kind = New-Link -Target $target -Source $source -IsDirectory $false
        if ($kind -eq 'copy') {
            Write-Warn "agents/$AgentFile copied - could not link"
            Write-Dim "enable Developer Mode (Settings > System > For developers) for real symlinks"
        } else {
            Write-Ok "agents/$AgentFile  ($kind)"
        }
    } else {
        if (-not $DryRun) { Copy-Item -Force $source $target }
        Write-Ok "agents/$AgentFile"
    }
}

function Install-Skill {
    param([string]$Src, [string]$Name)

    $source = Join-Path $Src "skills/$Name"
    $target = Join-Path $Dest "skills/$Name"
    if (-not (Test-Path $source)) { Fail "skill not found in source: $Name" }
    if (-not $DryRun) { New-Item -ItemType Directory -Path (Join-Path $Dest 'skills') -Force | Out-Null }

    if ($Link) {
        if ((Test-Path $target) -and -not (Test-IsLink $target)) {
            # Count first so the prompt states the stakes, then archive, then
            # delete. This used to delete outright with no backup of any kind.
            $n = @(Get-UserFiles -Dir $target -Name $Name).Count
            if (-not (Confirm-Action "$target is a real directory holding $n personal file(s).`n     Replace it with a link? They will be archived to a backup directory first.")) {
                Write-Skip "$Name (kept existing directory)"
                return
            }
            try {
                $saved = Save-UserData -Name $Name -Dir $target
            } catch {
                Fail ("could not archive personal files from skills/$Name - nothing was changed.`n" +
                      "       Check that $Dest is writable, then try again.`n" +
                      "       ($($_.Exception.Message))")
            }
            if ($saved -gt 0) { Write-Warn "$saved file(s) archived to $(Split-Path -Leaf $script:StashDir)/$Name" }
        }
        Remove-Entry $target
        $kind = New-Link -Target $target -Source $source -IsDirectory $true
        Write-Ok "skills/$Name  ($kind)"
        return
    }

    if (Test-IsLink $target) {
        Remove-Entry $target
        Write-Warn "skills/$Name was a link - replaced with a copy"
    }

    $preserved = 0
    $copied    = 0
    $prefix    = (Resolve-Path $source).Path.TrimEnd('\', '/')

    foreach ($file in Get-ChildItem -Path $source -Recurse -File -Force) {
        $rel = $file.FullName.Substring($prefix.Length + 1) -replace '\\', '/'

        if (($rel -match '^state/' -and $rel -ne 'state/.gitignore') -or
            $rel -match '(^|/)__pycache__/' -or $rel -match '\.pyc$' -or
            $rel -match '(^|/)\.DS_Store$') { continue }

        $dst = Join-Rel $target $rel

        if (($UserData -contains "$Name/$rel") -and (Test-Path $dst)) {
            if ($Force) {
                # Outside $target: a backup written inside the skill directory is
                # destroyed by a later uninstall, which then returns the template.
                if (-not $DryRun) {
                    $stashRoot = Get-StashDir
                    $bak = Join-Rel $stashRoot "$Name/$rel"
                    New-Item -ItemType Directory -Path (Split-Path -Parent $bak) -Force | Out-Null
                    if (Test-Path $bak) { Fail "refusing to overwrite an existing archive entry: $bak" }
                    Copy-Item -Force $dst $bak
                    Write-Warn "$Name/$rel overwritten - original saved to $(Split-Path -Leaf $stashRoot)/$Name/$rel"
                } else {
                    Write-Warn "$Name/$rel would be overwritten - original would be archived"
                }
            } else {
                $preserved++
                continue
            }
        }

        if (-not $DryRun) {
            $parent = Split-Path -Parent $dst
            if (-not (Test-Path $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
            Copy-Item -Force $file.FullName $dst
        }
        $copied++
    }

    if ((Test-Path (Join-Path $source 'state')) -and -not $DryRun) {
        New-Item -ItemType Directory -Path (Join-Path $target 'state') -Force | Out-Null
    }

    if ($preserved -gt 0) {
        Write-Ok "skills/$Name  ($copied files, $preserved personal file(s) kept)"
    } else {
        Write-Ok "skills/$Name  ($copied files)"
    }
}

function Invoke-Install {
    $src      = Get-Source
    Assert-NotOverlapping -Source $src -Destination $Dest
    $selected = Resolve-Skills

    Write-Step "Installing to $Dest"
    Write-Dim "source: $src"
    Write-Dim "mode:   $(if ($Link) { 'link' } else { 'copy' })"
    if ($DryRun) { Write-Warn "dry run - nothing will be written" }

    if ($Link -and $script:TmpDir) {
        Fail "-Link needs a real clone; it can't link to a temporary download"
    }

    Write-Step "Agent"
    Install-Agent -Src $src

    Write-Step "Skills"
    foreach ($s in $selected) { Install-Skill -Src $src -Name $s }

    Write-Step "Checks"
    $py = Get-PythonInfo
    if ($py) { Write-Ok $py } else { Write-Warn "no Python 3.9+ on PATH - the bundled scripts won't run" }

    if ($Link) {
        Write-Warn "linked install: preferences.md and state/ now live in your clone"
        Write-Dim "uncomment the personal-data lines in .gitignore before filling them in"
    }

    Write-Manifest -Src $src -Skills $selected

    Write-Host ""
    Write-Host "Installed $(Get-SourceVersion -Src $src)" -ForegroundColor Green
    Write-Host ""
    Write-Host "Next:"
    Write-Host "  1. notepad $(Join-Rel $Dest 'skills/chief-of-staff/preferences.md')"
    Write-Dim "fill in About me, VIPs, the priority ladder, and your drafting voice"
    Write-Host "  2. In Copilot CLI: Margo, brief me."
    Write-Host ""
    Write-Dim "Docs: https://github.com/$RepoSlug/blob/$Branch/docs/getting-started.md"
}

# ------------------------------------------------------------- uninstall ----

function Invoke-Update {
    $have = Get-ManifestField 'version'
    if (-not $have) {
        Fail ("no Margo installation found in $Dest.`n" +
              "       If you installed before versions were tracked, run a normal install:`n" +
              "         .\install.ps1 install -All -Dest `"$Dest`"")
    }

    $prevSkills = Get-ManifestField 'skills'
    $prevMode   = Get-ManifestField 'mode'
    if (-not $prevSkills) { $prevSkills = ($DefaultSkill -join ' ') }

    Write-Step "Checking for updates"
    Write-Dim "installed: $have  ($prevMode, $prevSkills)"

    # A linked install tracks the clone directly — updating means pulling there,
    # not copying over the top of it, which would replace the links with files.
    if ($prevMode -eq 'link') {
        Write-Host ""
        Write-Host "This is a linked install." -NoNewline -ForegroundColor White
        Write-Host " Its files are your clone, so update it with:"
        Write-Host ""
        Write-Host "  git -C $(Get-ManifestField 'source') pull"
        Write-Host ""
        return
    }

    # Prefer the published version; fall back to whatever source we have locally.
    $latest = Get-RemoteVersion
    if ($latest) {
        Write-Ok "available: $latest (published)"
    } else {
        $src = Get-Source
        $latest = Get-SourceVersion -Src $src
        Write-Warn "could not fetch the published version - using the local source ($latest)"
    }

    if (Test-VersionGreater -A $latest -B $have) {
        Write-Host ""
        Write-Host "Update available: $have -> $latest" -ForegroundColor Yellow
        if ($Check) { Write-Dim "run: .\install.ps1 update"; return }
    } else {
        Write-Host ""
        Write-Host "Up to date.  ($have)" -ForegroundColor Green
        if ($Check) { return }
        if ($Force) {
            Write-Warn "-Force given: reinstalling anyway"
        } else {
            Write-Dim "re-run with -Force to reinstall the same version"
            return
        }
    }

    # Reinstall exactly what is already there. Adding skills the user never chose
    # would be a surprise, and updating is not the moment to spring one.
    $script:Skills = @($prevSkills -split '\s+' | Where-Object { $_ })
    $script:All = $false
    $script:Link = $false
    Invoke-Install

    $new = Get-ManifestField 'version'
    if ($new -ne $have) {
        Write-Host "Updated $have -> $new" -ForegroundColor Green
    }
}

function Invoke-Uninstall {
    Write-Step "Removing from $Dest"
    if ($DryRun) { Write-Warn "dry run - nothing will be removed" }

    $found   = $false
    $stashed = $false

    # Two passes. Archiving every skill before deleting any of them is what makes
    # "nothing was removed" true: interleaving them means a failure on the third
    # skill reports that message after the first two are already gone.
    foreach ($s in $AllSkills) {
        $target = Join-Path $Dest "skills/$s"
        if (-not (Test-Path $target)) { continue }
        $found = $true
        if (Test-IsLink $target) { continue }   # a link owns no data of its own

        # Archive first. If anything cannot be saved, stop before deleting —
        # a partial archive followed by a recursive delete is unrecoverable.
        try {
            $n = Save-UserData -Name $s -Dir $target
        } catch {
            Fail ("could not archive personal files from skills/$s - nothing was removed.`n" +
                  "       Check that $Dest is writable, then run uninstall again.`n" +
                  "       ($($_.Exception.Message))")
        }
        if ($n -gt 0) { $stashed = $true }
    }

    # Everything is safely archived; only now delete.
    foreach ($s in $AllSkills) {
        $target = Join-Path $Dest "skills/$s"
        if (-not (Test-Path $target)) { continue }
        $isLink = Test-IsLink $target
        Remove-Entry $target
        if ($isLink) { Write-Ok "skills/$s  (link removed, clone untouched)" }
        else         { Write-Ok "skills/$s" }
    }

    $agent = Join-Path $Dest "agents/$AgentFile"
    if (Test-Path $agent) {
        $found = $true
        Remove-Entry $agent
        Write-Ok "agents/$AgentFile"
    }

    # Install metadata, not user data: remove it rather than archiving it.
    if (-not $DryRun) { Remove-Item -Force (Join-Path $Dest $ManifestName) -ErrorAction SilentlyContinue }

    if (-not $found) { Write-Dim "nothing installed"; return }

    if ($stashed -and $script:StashDir) {
        Write-Host ""
        Write-Host "Personal files saved to: $($script:StashDir)"
    }
    Write-Host ""
    Write-Host "Removed." -ForegroundColor Green
}

# ---------------------------------------------------------------- status ----

function Get-UserFileState {
    # Placeholders mean "unfilled". Ledgers like commitments.md have none, so
    # count real table rows instead.
    param([string]$Path)
    if (-not (Test-Path $Path)) { return 'missing' }
    $lines = Get-Content $Path -ErrorAction SilentlyContinue
    if (-not $lines) { return 'empty' }
    if ($lines -match '\{[a-z_]+\}') { return 'TEMPLATE' }

    $tableLines = @($lines | Where-Object { $_ -match '^\|' })
    if ($tableLines.Count -gt 0) {
        $rows = 0
        for ($i = 0; $i -lt $tableLines.Count; $i++) {
            if ($tableLines[$i] -match '^\|[-: |]*$') { continue }
            if (($i + 1) -lt $tableLines.Count -and $tableLines[$i + 1] -match '^\|[-: |]*$') { continue }
            if (($tableLines[$i] -replace '[|\s]', '').Length -gt 0) { $rows++ }
        }
        if ($rows -eq 0) { return 'empty' }
        return "$rows entries"
    }
    return 'personalized'
}

function Invoke-Status {
    Write-Step "Margo - $Dest"

    $have = Get-ManifestField 'version'
    if ($have) {
        Write-Ok "version    $have (installed $(Get-ManifestField 'installed_at'), $(Get-ManifestField 'mode'))"
    } else {
        Write-Skip "version    not recorded (installed before version tracking, or by hand)"
    }

    $agent = Join-Path $Dest "agents/$AgentFile"
    if (Test-Path $agent) {
        if (Test-IsLink $agent) { Write-Ok ("agent      linked -> " + (Get-LinkTarget $agent)) }
        else { Write-Ok "agent      installed" }
    } else {
        Write-Skip "agent      not installed"
    }

    foreach ($s in $AllSkills) {
        $target = Join-Path $Dest "skills/$s"
        $label  = $s.PadRight(16)
        if (-not (Test-Path $target)) { Write-Skip "$label not installed"; continue }

        if (Test-IsLink $target) { Write-Ok ("$label linked -> " + (Get-LinkTarget $target)) }
        else { Write-Ok "$label installed" }

        foreach ($rel in $UserData | Where-Object { $_ -like "$s/*" }) {
            $state = Get-UserFileState (Join-Path $Dest "skills/$rel")
            $base  = (Split-Path -Leaf $rel).PadRight(18)
            switch ($state) {
                'TEMPLATE' { Write-Host "     .  $base " -NoNewline -ForegroundColor DarkGray
                             Write-Host "not filled in" -ForegroundColor Yellow }
                'missing'  { Write-Host "     .  $base missing" -ForegroundColor DarkGray }
                'empty'    { Write-Host "     .  $base empty" -ForegroundColor DarkGray }
                default    { Write-Host "     .  $base " -NoNewline -ForegroundColor DarkGray
                             Write-Host $state -ForegroundColor Green }
            }
        }

        $stateDir = Join-Path $target 'state'
        if (Test-Path $stateDir) {
            $n = @(Get-ChildItem -Path $stateDir -File -Recurse -Force -ErrorAction SilentlyContinue |
                   Where-Object { $_.Name -ne '.gitignore' }).Count
            if ($n -gt 0) { Write-Host ("     .  " + 'state/'.PadRight(18) + " $n state file(s)") -ForegroundColor DarkGray }
        }
    }

    Write-Step "Environment"
    $py = Get-PythonInfo
    if ($py) { Write-Ok "python     $py" } else { Write-Warn "python     not found (3.9+ needed for bundled scripts)" }
    foreach ($t in @(@('gh', 'GitHub routine'), @('az', 'work-items routine'))) {
        if (Get-Command $t[0] -ErrorAction SilentlyContinue) { Write-Ok "$($t[0].PadRight(10)) present" }
        else { Write-Skip "$($t[0].PadRight(10)) not found ($($t[1]))" }
    }
}

# ------------------------------------------------------------------ main ----

try {
    switch ($Command) {
        'install'   { Invoke-Install }
        'update'    { Invoke-Update }
        'uninstall' { Invoke-Uninstall }
        'status'    { Invoke-Status }
    }
} finally {
    Remove-TempDir
}

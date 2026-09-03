<#
.SYNOPSIS
  Run Margo on a schedule, with writes disabled at the CLI level.

.DESCRIPTION
  Prompts come from automations/ — one Markdown file per automation, front
  matter for the schedule, body for the prompt. That directory is the source of
  truth for BOTH schedule paths (this wrapper and the app's workflows), so a
  prompt cannot drift between them. See automations/README.md.

  Why this exists rather than a documented command line:

  docs/proactive.md describes an unattended run as -AllowAllTools plus four
  --deny-tool flags. That is correct, and it is also four lines someone copies
  into Task Scheduler and trims. Drop one and the read-only guarantee silently
  becomes an instruction the model is merely asked to follow — at 06:00, while
  you are asleep, with your mailbox connected.

  Here the deny list is not a parameter. Extra arguments are passed through, but
  they cannot re-enable writes: Copilot CLI resolves denial ahead of any allow
  rule, including --allow-all-tools, so even an explicit
  --allow-tool 'workiq(do_action)' loses to the entries below.

  What this does not do: -AllowAllTools is still passed, so shell, gh and curl
  remain available. The four Work IQ write tools are unreachable; a determined
  outbound action is not. That is the intended trade — this closes the path
  Margo would actually take, against the realistic failure of someone trimming
  a scheduled task. It is not a sandbox. If you want one, see docs/container.md.
  See docs/safety.md §3 for the full statement.

.EXAMPLE
  .\margo-scheduled.ps1 list              # what is defined

.EXAMPLE
  .\margo-scheduled.ps1 brief

.EXAMPLE
  .\margo-scheduled.ps1 brief -Print      # show the command, run nothing

.EXAMPLE
  .\margo-scheduled.ps1 brief -ShowPrompt # show just the prompt

.EXAMPLE
  .\margo-scheduled.ps1 schtasks          # register-task commands for every automation

.EXAMPLE
  .\margo-scheduled.ps1 "What did I miss?"
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0, Mandatory = $true)]
    [string]$What,

    [switch]$Print,

    [switch]$ShowPrompt,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Extra
)

$ErrorActionPreference = 'Stop'

# The four Work IQ tools that change the outside world. Everything else — fetch,
# retrieve, ask, call_function, get_schema, search_paths, fetch_blob — is a read
# and stays available.
#
# call_function looks like it belongs here and does not. OData functions are
# side-effect-free by definition, and it is how reminderView and
# calendarView/delta are reached — denying it would break the brief's own reads
# to prevent nothing.
$DenyTools = @('do_action', 'create_entity', 'update_entity', 'delete_entity')

$Agent = if ($env:MARGO_AGENT) { $env:MARGO_AGENT } else { 'margo' }
$Log   = $env:MARGO_SCHEDULED_LOG

# A free-form prompt gets the contract too. Everything this wrapper runs is
# unattended by definition, and the skill gates its Unattended Mode Contract on
# "when invoked by a scheduled workflow" — a condition the model has to infer.
# Left to infer it, a run can legally end in ask_user and hang until timeout.
$UnattendedPreamble = "Unattended scheduled run — apply the chief-of-staff Unattended Mode Contract: never call ask_user, no trailing offers, nothing is sent or RSVP'd or changed, drafts may be prepared but never delivered. Silence is a successful run."

$SelfDir = Split-Path -Parent $PSCommandPath

# ------------------------------------------------------------ automations ----

# Repo checkout first, then an install. $env:MARGO_AUTOMATIONS overrides both,
# which is what you want when testing a change without reinstalling.
function Resolve-Automations {
    if ($env:MARGO_AUTOMATIONS) {
        if (-not (Test-Path -PathType Container $env:MARGO_AUTOMATIONS)) {
            throw "MARGO_AUTOMATIONS is set to '$($env:MARGO_AUTOMATIONS)', which is not a directory"
        }
        return (Resolve-Path $env:MARGO_AUTOMATIONS).Path
    }
    $dest = if ($env:MARGO_DEST) { $env:MARGO_DEST } else { Join-Path $HOME '.copilot' }
    foreach ($d in @((Join-Path $SelfDir '..' | Join-Path -ChildPath 'automations'),
                     (Join-Path $dest 'automations'))) {
        if (-not (Test-Path -PathType Container $d)) { continue }
        # A directory with no automations in it is not the one we are looking for.
        if (-not (Get-ChildItem -Path $d -Filter '*.md' -File -ErrorAction SilentlyContinue)) { continue }
        return (Resolve-Path $d).Path
    }
    throw @"
no automations/ directory found.
       Looked in: $SelfDir\..\automations
                  $dest\automations
       Install with .\install.ps1, or set MARGO_AUTOMATIONS to a checkout's automations\.
"@
}

# Front matter is deliberately flat 'key: value' — no nesting, no lists. A YAML
# parser is a dependency this script cannot assume, and every field here is a
# scalar. Surrounding quotes are stripped so cron can be quoted (it must be, or
# the '*' makes it a YAML alias in anything that does parse it properly).
function Read-Automation {
    param([string]$Path)

    $lines = [System.IO.File]::ReadAllLines($Path)
    $meta  = @{}
    $body  = New-Object System.Collections.Generic.List[string]

    if ($lines.Count -eq 0 -or $lines[0].Trim() -ne '---') {
        return [pscustomobject]@{ Path = $Path; Meta = $meta; Prompt = ($lines -join "`n").Trim() }
    }

    $inBody = $false
    for ($i = 1; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if (-not $inBody) {
            if ($line.Trim() -eq '---') { $inBody = $true; continue }
            $eq = $line.IndexOf(':')
            if ($eq -lt 1) { continue }
            $k = $line.Substring(0, $eq).Trim()
            $v = $line.Substring($eq + 1).Trim()
            if ($v.Length -ge 2 -and
                (($v.StartsWith('"') -and $v.EndsWith('"')) -or
                 ($v.StartsWith("'") -and $v.EndsWith("'")))) {
                $v = $v.Substring(1, $v.Length - 2)
            }
            $meta[$k] = $v
        } else {
            $body.Add($line)
        }
    }

    [pscustomobject]@{
        Path   = $Path
        Meta   = $meta
        Prompt = ($body -join "`n").Trim()
    }
}

# Ordered anchors → sweeps → ambient, then by filename so output is stable.
# Filename order alone puts the morning brief fifth, which reads as arbitrary.
function Get-Automations {
    param([string]$Dir)
    $rank = @{ anchor = 1; sweep = 2; ambient = 3 }
    Get-ChildItem -Path $Dir -Filter '*.md' -File |
        Where-Object { $_.Name -ne 'README.md' } |
        Sort-Object Name |
        ForEach-Object { Read-Automation $_.FullName } |
        Sort-Object @{ Expression = {
            $t = $_.Meta['tier']
            if ($t -and $rank.ContainsKey($t)) { $rank[$t] } else { 4 }
        } }, @{ Expression = { $_.Path } }
}

# Subcommand names cannot also be verbs. The shell wrapper matches `list` and
# `crontab` before verb lookup, so such an automation is unreachable there — and
# because this wrapper has no `crontab` subcommand it would RUN the automation,
# giving the same manifest opposite behaviour on the two platforms. Rejected in
# validation instead, on both, using one shared list.
$ReservedVerbs = @('list', 'crontab', 'schtasks', 'help')

# Validate the whole set before anything uses it. A file that exists but does
# not parse MUST be an error: the failure mode this replaces was a broken
# manifest silently selecting the free-form fallback, which is indistinguishable
# from a successful run and sends a one-word prompt to an unattended session.
function Assert-Automations {
    param([object[]]$Automations, [string]$Dir)
    if (-not $Automations -or $Automations.Count -eq 0) {
        throw "no automations found in $Dir.`n       Expected one or more *.md files with front matter. See automations/README.md."
    }
    $seen = @{}
    foreach ($a in $Automations) {
        $leaf = Split-Path -Leaf $a.Path
        foreach ($key in @('name', 'verb', 'cron', 'tier')) {
            if (-not $a.Meta[$key]) {
                throw "automation $leaf has no '$key' in its front matter.`n       Every automation needs name, verb, cron and tier. See automations/README.md."
            }
        }
        $v = $a.Meta['verb']
        if ($ReservedVerbs -contains $v) {
            throw "automation $leaf uses the reserved verb '$v'.`n       $($ReservedVerbs -join ' ') are subcommands of this script and cannot be automation verbs."
        }
        if ($seen.ContainsKey($v)) {
            throw "verb '$v' is defined by more than one automation in $Dir.`n       Verbs must be unique — otherwise which prompt runs depends on filename order."
        }
        $seen[$v] = $true
        if (-not $a.Prompt) { throw "automation $leaf has an empty prompt body." }
    }
}

$Automations = Resolve-Automations
$AllAutomations = @(Get-Automations $Automations)
Assert-Automations -Automations $AllAutomations -Dir $Automations
$AvailableVerbs = ($AllAutomations | ForEach-Object { $_.Meta['verb'] }) -join ' '

if ($What -eq 'list') {
    $AllAutomations |
        ForEach-Object {
            [pscustomobject]@{
                Verb = $_.Meta['verb']
                Tier = $_.Meta['tier']
                Cron = $_.Meta['cron']
                Name = $_.Meta['name']
            }
        } | Format-Table -AutoSize | Out-String | Write-Host
    # Footer to stderr: `list` is parsed by CI and by anyone scripting against
    # it, and a provenance line in the data stream reads as another automation.
    [Console]::Error.WriteLine("source: $Automations")
    exit 0
}

# Task Scheduler has no cron parser, so translate the fields we actually use:
# minute, hour and day-of-week. Anything more exotic in a cron expression is
# reported rather than silently mistranslated into a task that runs at the
# wrong time — a schedule that is quietly wrong is worse than one that is absent.
if ($What -eq 'schtasks') {
    Write-Host "# Margo - generated by margo-scheduled.ps1 schtasks"
    Write-Host "# Review, then run these in an elevated prompt."
    $days = @{ '0' = 'SUN'; '1' = 'MON'; '2' = 'TUE'; '3' = 'WED'; '4' = 'THU'; '5' = 'FRI'; '6' = 'SAT' }
    foreach ($a in Get-Automations $Automations) {
        $cron = $a.Meta['cron']
        $verb = $a.Meta['verb']
        Write-Host ""
        Write-Host ("# {0} ({1}) - cron: {2}" -f $a.Meta['name'], $a.Meta['tier'], $cron)
        $f = $cron -split '\s+'
        if ($f.Count -ne 5 -or $f[2] -ne '*' -or $f[3] -ne '*' -or $f[0] -notmatch '^\d+$') {
            Write-Host ("#   cannot translate '{0}' to schtasks - create this one by hand." -f $cron)
            continue
        }

        # Bounds, not just syntax. '99 25 * * 1-5' previously emitted /ST 25:99
        # and '0 17-9 * * 1-5' emitted a negative /DU — commands that look
        # actionable and are not. A schedule quietly wrong is worse than absent.
        if ([int]$f[0] -gt 59) {
            Write-Host ("#   minute '{0}' is out of range (0-59) - fix the cron field." -f $f[0])
            continue
        }

        # An hour range ('0 9-17 * * 1-5') is the hourly sweep, and schtasks does
        # express it: start at the first hour and repeat every 60 minutes for the
        # width of the range. Reporting it as untranslatable would leave the one
        # tier that runs 40 times a week to be hand-built, which is where a
        # wrong-by-an-hour schedule comes from.
        $repeat = ''
        if ($f[1] -match '^(\d+)-(\d+)$') {
            $startHour = [int]$Matches[1]
            $endHour   = [int]$Matches[2]
            if ($startHour -gt 23 -or $endHour -gt 23) {
                Write-Host ("#   hour range '{0}' is out of range (0-23) - fix the cron field." -f $f[1])
                continue
            }
            if ($endHour -le $startHour) {
                Write-Host ("#   hour range '{0}' does not increase - schtasks cannot express it." -f $f[1])
                continue
            }
            $repeat = ' /RI 60 /DU {0:00}:00' -f ($endHour - $startHour)
        } elseif ($f[1] -match '^\d+$') {
            $startHour = [int]$f[1]
            if ($startHour -gt 23) {
                Write-Host ("#   hour '{0}' is out of range (0-23) - fix the cron field." -f $f[1])
                continue
            }
        } else {
            Write-Host ("#   cannot translate hour field '{0}' - create this one by hand." -f $f[1])
            continue
        }

        $time = '{0:00}:{1:00}' -f $startHour, [int]$f[0]
        $dow  = $f[4]
        $cmd  = "powershell -NoProfile -File `"$PSCommandPath`" $verb"
        if ($dow -eq '*') {
            Write-Host ("schtasks /Create /TN `"Margo\{0}`" /TR '{1}' /SC DAILY /ST {2}{3}" -f $verb, $cmd, $time, $repeat)
        } elseif ($dow -eq '1-5') {
            Write-Host ("schtasks /Create /TN `"Margo\{0}`" /TR '{1}' /SC WEEKLY /D MON,TUE,WED,THU,FRI /ST {2}{3}" -f $verb, $cmd, $time, $repeat)
        } elseif ($days.ContainsKey($dow)) {
            Write-Host ("schtasks /Create /TN `"Margo\{0}`" /TR '{1}' /SC WEEKLY /D {2} /ST {3}{4}" -f $verb, $cmd, $days[$dow], $time, $repeat)
        } else {
            Write-Host ("#   cannot translate day-of-week '{0}' - create this one by hand." -f $dow)
        }
    }
    exit 0
}

# ------------------------------------------------------------------ main ----

# An explicit escape hatch, so free-form does not have to be inferred.
$forcePrompt = $false
if ($Extra) { $forcePrompt = $Extra -contains '--prompt' }
if ($forcePrompt) { $Extra = @($Extra | Where-Object { $_ -ne '--prompt' }) }

$match = $null
if (-not $forcePrompt) {
    $match = $AllAutomations | Where-Object { $_.Meta['verb'] -eq $What } | Select-Object -First 1
}

if ($match) {
    if (-not $match.Prompt) { throw "automation '$What' ($($match.Path)) has an empty prompt body" }
    $Prompt = $match.Prompt
} else {
    # Not a known verb. A bare word is almost certainly a verb whose manifest did
    # not resolve, and sending it as the literal prompt is the failure this whole
    # mechanism exists to prevent — an unattended 06:00 run whose entire prompt is
    # the word "brief". Free-form prompts are phrases; require one, or -Prompt.
    if (-not $forcePrompt -and $What -notmatch '\s') {
        Write-Host "error: unknown verb '$What'." -ForegroundColor Red
        Write-Host "       Available: $AvailableVerbs"
        Write-Host "       Source:    $Automations"
        Write-Host "       To send '$What' as a literal prompt anyway, add --prompt."
        exit 1
    }
    $Prompt = "$UnattendedPreamble`n`n$What"
}

if ($ShowPrompt) {
    Write-Output $Prompt
    exit 0
}

$cmdArgs = @('--agent', $Agent, '-p', $Prompt, '--allow-all-tools')
foreach ($t in $DenyTools) { $cmdArgs += @('--deny-tool', "workiq($t)") }
if ($Extra) { $cmdArgs += $Extra }

# -Print before the PATH check: showing the command is a dry run, and is exactly
# what you want when inspecting it before installing anything, or composing a
# scheduled task for a different machine.
if ($Print) {
    "copilot " + (($cmdArgs | ForEach-Object {
        if ($_ -match '[\s()]') { "'$_'" } else { $_ }
    }) -join ' ')
    exit 0
}

if (-not (Get-Command copilot -ErrorAction SilentlyContinue)) {
    Write-Host "error: copilot not found on PATH." -ForegroundColor Red
    Write-Host "       Scheduled tasks do not inherit your interactive PATH - use an"
    Write-Host "       absolute path, or set it in the task definition."
    exit 1
}


Write-Host ("[{0}] margo scheduled: {1}{2}" -f
    ([DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')), $What,
    $(if ($match) { " ($(Split-Path -Leaf $match.Path))" } else { '' }))

if ($Log) {
    $dir = Split-Path -Parent $Log
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    & copilot @cmdArgs 2>&1 | Tee-Object -FilePath $Log -Append
    exit $LASTEXITCODE
}

& copilot @cmdArgs
exit $LASTEXITCODE

<#
.SYNOPSIS
  Run Margo on a schedule, with writes disabled at the CLI level.

.DESCRIPTION
  Why this exists rather than a documented command line:

  docs/proactive.md describes an unattended run as -AllowAllTools plus four
  --deny-tool flags. That is correct, and it is also four lines someone copies
  into Task Scheduler and trims. Drop one and the read-only guarantee silently
  becomes an instruction the model is merely asked to follow — at 07:15, while
  you are asleep, with your mailbox connected.

  Here the deny list is not a parameter. Extra arguments are passed through, but
  they cannot re-enable writes: Copilot CLI resolves denial ahead of any allow
  rule, including --allow-all-tools, so even an explicit
  --allow-tool 'workiq(do_action)' loses to the entries below.

.EXAMPLE
  .\margo-scheduled.ps1 brief

.EXAMPLE
  .\margo-scheduled.ps1 brief -Print      # show the command, run nothing

.EXAMPLE
  .\margo-scheduled.ps1 "What did I miss?"
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0, Mandatory = $true)]
    [string]$What,

    [switch]$Print,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Extra
)

$ErrorActionPreference = 'Stop'

# The four Work IQ tools that change the outside world. Everything else — fetch,
# retrieve, ask, call_function, get_schema, search_paths, fetch_blob — is a read
# and stays available.
$DenyTools = @('do_action', 'create_entity', 'update_entity', 'delete_entity')

$Agent = if ($env:MARGO_AGENT) { $env:MARGO_AGENT } else { 'margo' }
$Log   = $env:MARGO_SCHEDULED_LOG

# Named anchors match the tiers in skills/chief-of-staff/references/proactive.md.
$Prompt = switch ($What) {
    'brief'       { 'Run my morning brief.' }
    'eod'         { 'Run my end-of-day wrap-up.' }
    'week'        { 'Run my week ahead.' }
    'commitments' { 'Run commitment ageing: what have I promised that is slipping?' }
    'sweep'       { 'Run my hourly sweep. Stay silent unless something clears the interrupt bar.' }
    default       { $What }
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


Write-Host ("[{0}] margo scheduled: {1}" -f
    ([DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')), $What)

if ($Log) {
    $dir = Split-Path -Parent $Log
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    & copilot @cmdArgs 2>&1 | Tee-Object -FilePath $Log -Append
    exit $LASTEXITCODE
}

& copilot @cmdArgs
exit $LASTEXITCODE

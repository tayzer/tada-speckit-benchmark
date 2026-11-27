[CmdletBinding(SupportsShouldProcess)]
param([string]$PacketRoot)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$harness = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($PacketRoot)) {
    $PacketRoot = Join-Path $harness '.local\codex-sdd-handoff\owner-review'
}
$packets = (Resolve-Path -LiteralPath $PacketRoot).Path
[object[]]$drafts = Get-Content -LiteralPath (Join-Path $packets 'drafts.json') -Raw | ConvertFrom-Json
if ($drafts.Count -ne 2 -or ($drafts.ticket -join ',') -cne 'ticket-1,ticket-2') {
    throw 'Choose a packet directory containing both handoff reviews.'
}

function Get-RecordedApproval($Draft) {
    $directory = Join-Path $packets "$($Draft.ticket)\.tada\approvals"
    if (!(Test-Path -LiteralPath $directory -PathType Container)) { return }
    $records = @(Get-ChildItem -LiteralPath $directory -File -Filter '*.json' | ForEach-Object {
        $record = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
        if ($record.review -ceq $Draft.review -and $record.baseline -ceq $Draft.baseline) {
            [pscustomobject]@{ File = $_; Record = $record }
        }
    })
    if ($records.Count -gt 1) { throw "Multiple approval records for $($Draft.ticket). Review the existing records." }
    if ($records.Count -eq 1) {
        if ([string]::IsNullOrWhiteSpace($records[0].Record.by) -or $records[0].Record.by -match '(?i)automated') {
            throw "Expected a named human approval for $($Draft.ticket)."
        }
        return $records[0]
    }
}

function Record-Approval($Draft, [string]$Owner, [string]$Replaces) {
    $arguments = @('baseline', 'approve', '--review', $Draft.review, '--by', $Owner)
    if ($Replaces) { $arguments += @('--replaces', $Replaces) }
    Push-Location -LiteralPath (Join-Path $packets $Draft.ticket)
    try {
        & dotnet tool run tada @arguments
        if ($LASTEXITCODE -ne 0) { throw "TADA did not record the $($Draft.ticket) approval." }
    } finally { Pop-Location }
}

foreach ($draft in $drafts) {
    if ($draft.review -cnotmatch '^[a-f0-9]{32}$' -or $draft.baseline -cnotmatch '^sha256:[a-f0-9]{64}$') {
        throw "Invalid review identity for $($draft.ticket)."
    }
    $manifest = Join-Path $packets "$($draft.ticket)\.config\dotnet-tools.json"
    if (!(Test-Path -LiteralPath $manifest -PathType Leaf)) { throw "Missing TADA tool manifest at $manifest." }
    $review = Get-Content -LiteralPath (Join-Path $packets "$($draft.ticket)\.tada\reviews\$($draft.review).json") -Raw | ConvertFrom-Json
    if ($review.id -cne $draft.review -or $review.baseline -cne $draft.baseline) { throw "Review drift for $($draft.ticket)." }
    if ($draft.ticket -eq 'ticket-2' -and $review.comparedTo -cne $drafts[0].baseline) {
        throw 'Ticket 2 must identify the Ticket 1 baseline it amends.'
    }
    Write-Host "$($draft.ticket) review $($draft.review) baseline $($draft.baseline)"
}

if (!$PSCmdlet.ShouldProcess($packets, 'Record and register both handoff fixture approvals')) { return }
$first = Get-RecordedApproval $drafts[0]
$second = Get-RecordedApproval $drafts[1]
$owner = ''
if (!$first -or !$second) {
    $owner = Read-Host 'After reviewing both packets, enter your name to record approval'
    if ([string]::IsNullOrWhiteSpace($owner) -or $owner -match '(?i)automated') { throw 'Enter your name as the approving owner.' }
}

if (!$first) {
    Record-Approval $drafts[0] $owner ''
    $first = Get-RecordedApproval $drafts[0]
    if (!$first) { throw 'The Ticket 1 approval record was not found.' }
}

$amendmentApprovals = Join-Path $packets 'ticket-2\.tada\approvals'
New-Item -ItemType Directory -Path $amendmentApprovals -Force | Out-Null
$prior = Join-Path $amendmentApprovals $first.File.Name
if (!(Test-Path -LiteralPath $prior)) {
    Copy-Item -LiteralPath $first.File.FullName -Destination $prior
} elseif ((Get-FileHash -LiteralPath $prior).Hash -ne (Get-FileHash -LiteralPath $first.File.FullName).Hash) {
    throw 'Ticket 2 contains a different copy of the Ticket 1 approval.'
}
if (!$second) { Record-Approval $drafts[1] $owner $drafts[0].baseline }

& (Join-Path $harness 'bench.ps1') batch-fixtures --suite codex-sdd-handoff --out $packets --register
if ($LASTEXITCODE -ne 0) { throw 'The approvals could not be registered.' }

[CmdletBinding(DefaultParameterSetName='Account')]
param(
    [Parameter(Mandatory, ParameterSetName='Account')][ValidatePattern('^[A-Za-z0-9_]{1,20}$')][string]$Username,
    [Parameter(Mandatory, ParameterSetName='Workspace')][switch]$Workspace,
    [string]$LegacyRoot = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ZenithSessions\Personal')
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($LegacyRoot)
$workspaceRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../../..')).Path
$helper = Join-Path $PSScriptRoot 'LegacyProbeBundle/bin/Release/net10.0-windows10.0.19041.0/LegacyProbeBundle.exe'
if (-not (Test-Path -LiteralPath $helper -PathType Leaf)) { throw 'Build LegacyProbeBundle in Release first.' }

$paths = @('instance.lock', 'accounts.db', 'accounts.vault')
foreach ($relative in $paths) {
    $item = Get-Item -LiteralPath (Join-Path $root $relative) -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The legacy store contains a filesystem link.' }
}

$lock = [IO.File]::Open((Join-Path $root 'instance.lock'), 'Open', 'Read', 'None')
try {
    $vault = Join-Path $root 'accounts.vault'
    if (Get-ChildItem -LiteralPath $vault -Force -Recurse | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } | Select-Object -First 1) {
        throw 'The legacy vault contains a filesystem link.'
    }
    $snapshot = Join-Path $workspaceRoot ('artifacts/rust-launch-probe/' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $snapshot | Out-Null
    $files = @('accounts.db', 'accounts.db-wal', 'accounts.db-journal')
    if ($Workspace) {
        $files += @('favorite-destinations.json', 'favorite-destinations.json.bak', 'recipient-bundles.json',
            'recipient-bundles.json.bak', 'macro-assignments.db', 'macro-assignments.db-wal', 'macro-assignments.db-journal',
            'settings.json', 'settings.json.bak')
    }
    foreach ($relative in $files) {
        $source = Join-Path $root $relative
        if (Test-Path -LiteralPath $source -PathType Leaf) {
            if ((Get-Item -LiteralPath $source -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'A legacy source file is a filesystem link.' }
            Copy-Item -LiteralPath $source -Destination $snapshot
        }
    }
    Copy-Item -LiteralPath $vault -Destination $snapshot -Recurse -Force
    New-Item -ItemType File -Path (Join-Path $snapshot 'zenith-rust-probe-snapshot') | Out-Null
}
finally { $lock.Dispose() }

if ($Workspace) {
    & $helper --workspace $snapshot | Out-Host
    $output = 'legacy-workspace.zenith-migration'
}
else {
    & $helper $snapshot $Username | Out-Host
    $output = 'probe.dpapi'
}
if ($LASTEXITCODE -ne 0) { throw "The helper could not stage the migration. Snapshot is in $snapshot." }
Join-Path $snapshot $output

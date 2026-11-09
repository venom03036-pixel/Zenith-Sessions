param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_]{1,20}$')][string]$Username,
    [string]$LegacyRoot = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ZenithSessions\Personal')
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($LegacyRoot)
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../../..')).Path
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
    $snapshot = Join-Path $workspace ('artifacts/rust-launch-probe/' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $snapshot | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'accounts.db') -Destination $snapshot
    $wal = Join-Path $root 'accounts.db-wal'
    if (Test-Path -LiteralPath $wal -PathType Leaf) {
        if ((Get-Item -LiteralPath $wal -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The legacy database WAL is a filesystem link.' }
        Copy-Item -LiteralPath $wal -Destination $snapshot
    }
    Copy-Item -LiteralPath $vault -Destination $snapshot -Recurse -Force
    New-Item -ItemType File -Path (Join-Path $snapshot 'zenith-rust-probe-snapshot') | Out-Null
}
finally { $lock.Dispose() }

& $helper $snapshot $Username | Out-Host
if ($LASTEXITCODE -ne 0) { throw "The helper could not stage this account. Snapshot is in $snapshot." }
Join-Path $snapshot 'probe.dpapi'

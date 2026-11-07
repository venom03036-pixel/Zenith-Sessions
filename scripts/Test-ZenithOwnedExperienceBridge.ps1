[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$LuauDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root 'integrations/roblox/ZenithSessionsBridge.server.luau'
$fixture = Join-Path $root 'tests/Zenith.Sessions.Tests/Fixtures/OwnedExperienceBridgeTests.luau'
$luau = Join-Path $LuauDirectory 'luau.exe'
$compiler = Join-Path $LuauDirectory 'luau-compile.exe'

& $compiler --null $source
if ($LASTEXITCODE -ne 0) { throw 'The production server script did not compile.' }

$production = Get-Content -LiteralPath $source -Raw
$configured = $production.Replace('local universeId = 0', 'local universeId = 8000000001').
    Replace('local allowedPlaces = {}', 'local allowedPlaces = { [9000000001] = true, [9000000002] = true }').
    Replace('local allowedUsers = {}', 'local allowedUsers = { [8000000002] = true }')
$program = @(
    'local function installDisabled(game, Instance, os)', $production, 'end'
    'local function install(game, Instance, os)', $configured, 'end'
    (Get-Content -LiteralPath $fixture -Raw)
) -join [Environment]::NewLine

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$programPath = Join-Path $OutputDirectory 'server-tests.luau'
Set-Content -LiteralPath $programPath -Value $program -Encoding utf8
& $luau $programPath | Tee-Object -FilePath (Join-Path $OutputDirectory 'server-tests.log')
if ($LASTEXITCODE -ne 0) { throw 'The owned-experience server checks failed.' }

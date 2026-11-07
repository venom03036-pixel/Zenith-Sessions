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

# Windows PowerShell 5.1 and PowerShell 7 must produce the same bytes: BOM-free UTF-8, because luau.exe rejects U+FEFF.
$utf8 = New-Object System.Text.UTF8Encoding $false
$production = Get-Content -LiteralPath $source -Raw -Encoding UTF8
$configured = $production.Replace('local universeId = 0', 'local universeId = 8000000001').
    Replace('local allowedPlaces = {}', 'local allowedPlaces = { [9000000001] = true, [9000000002] = true }').
    Replace('local allowedUsers = {}', 'local allowedUsers = { [8000000002] = true }')
# The standalone luau.exe has no Roblox task scheduler or warn, so the fixture supplies both.
$program = @(
    'local function installDisabled(game, Instance, os, task, warn)', $production, 'end'
    'local function install(game, Instance, os, task, warn)', $configured, 'end'
    (Get-Content -LiteralPath $fixture -Raw -Encoding UTF8)
) -join [Environment]::NewLine

# [IO.File] resolves relative paths against the process directory, not the PowerShell location, so resolve the folder first.
$OutputDirectory = (New-Item -ItemType Directory -Path $OutputDirectory -Force).FullName
$programPath = Join-Path $OutputDirectory 'server-tests.luau'
[IO.File]::WriteAllText($programPath, $program, $utf8)
$lines = @(& $luau $programPath)
$exit = $LASTEXITCODE
$lines
[IO.File]::WriteAllLines((Join-Path $OutputDirectory 'server-tests.log'), [string[]]$lines, $utf8)
if ($exit -ne 0) { throw 'The owned-experience server checks failed.' }

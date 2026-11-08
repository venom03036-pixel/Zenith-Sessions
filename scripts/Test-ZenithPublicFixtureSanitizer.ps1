#Requires -Version 7
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Capture-ZenithPublicFixtures.ps1')

function Assert-True([bool]$condition,[string]$message) { if (-not $condition) { throw $message } }
$sentinel='DO-NOT-KEEP-SYNTHETIC-PRIVATE-VALUE'
$json = @{ data=@(@{ id='aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'; playing=2; maxPlayers=12; ping=$null; fps=59.5; playerTokens=@($sentinel); accessCode=$sentinel; serverType='Private' }); nextPageCursor=$sentinel; unknown=@{ secret=$sentinel } } | ConvertTo-Json -Depth 8
$safe = ConvertTo-SanitizedPublicFixture -Kind Servers -Json $json
Assert-True (-not $safe.Contains($sentinel)) 'Sanitizer leaked an unapproved field or raw text.'
$row = (ConvertFrom-Json $safe).data[0]
Assert-True ($row.playing -eq 2 -and $row.maxPlayers -eq 12 -and $row.fps -eq 59.5 -and $null -eq $row.ping) 'Numeric/null response shape changed.'
Assert-True ($row.accessCode.Length -gt 0 -and $row.serverType -ne 'Public') 'Nonpublic rejection markers were erased.'
Assert-True ($row.id -eq '11111111-1111-1111-1111-000000000001') 'Real join target survived sanitization.'
$safe=ConvertTo-SanitizedPublicFixture -Kind Avatar -Json ('{"data":[{"targetId":987,"state":"Completed","imageUrl":"https://untrusted.example/'+$sentinel+'"}]}')
Assert-True (-not $safe.Contains($sentinel) -and $safe.Contains('invalid.example')) 'Unsafe image URI became trusted or retained its contents.'
$safe=ConvertTo-SanitizedPublicFixture -Kind Mapping -Json '{"universeId":null}'
Assert-True ($safe.Contains('null')) 'Missing universe became a successful mapping.'
$refused=$false
try { ConvertTo-SanitizedPublicFixture -Kind Mapping -Json ('{"universeId":42,"unexpected":"'+('x'*65536)+'"}') | Out-Null } catch { $refused=$true }
Assert-True $refused 'Oversized body was accepted.'
Write-Host 'Sanitizer: 7 assertions passed; synthetic input only; no requests made.'

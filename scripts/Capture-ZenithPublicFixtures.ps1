#Requires -Version 7
param([switch]$Capture)

# Explicit research tool, never invoked by tests or the application. Dot-source without -Capture to test the sanitizer.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function ConvertTo-SanitizedPublicFixture {
    param([Parameter(Mandatory)][string]$Kind, [Parameter(Mandatory)][string]$Json)
    if ([Text.Encoding]::UTF8.GetByteCount($Json) -gt 65536) { throw 'Body exceeds capture limit.' }
    $source = ConvertFrom-Json -InputObject $Json -AsHashtable -Depth 16
    if ($source -isnot [System.Collections.IDictionary]) { throw 'Expected an object response.' }
    # Only these fields can reach disk. String identifiers/text/URLs are replaced, not copied or regex-redacted.
    function Project-Fields($inputObject, [string[]]$fields) {
        if ($inputObject -isnot [System.Collections.IDictionary]) { throw 'Expected an object row.' }
        $result = [ordered]@{}
        foreach ($field in $fields) {
            if (-not $inputObject.Contains($field)) { continue }
            $value = $inputObject[$field]
            if ($null -eq $value) { $result[$field] = $null; continue }
            switch ($field) {
                { $_ -in 'universeId','rootPlaceId','targetId','id' } {
                    if ($value -is [string]) {
                        $parsed = [Guid]::Empty
                        $result[$field] = if ([Guid]::TryParseExact($value,'D',[ref]$parsed)) { '11111111-1111-1111-1111-111111111111' } else { 'synthetic-invalid-id' }
                    } elseif ($value -is [long] -or $value -is [int]) {
                        $result[$field] = if ($value -le 0) { $value } elseif ($field -eq 'universeId') { 84 } elseif ($field -eq 'rootPlaceId') { 42 } else { 123 }
                    } else { throw 'Unexpected identifier type.' }
                }
                { $_ -in 'name','description','displayName','message','nextPageCursor','previousPageCursor','accessCode','linkCode','reservedServerAccessCode','privateServerId' } {
                    if ($value -isnot [string]) { throw 'Unexpected text type.' }
                    $result[$field] = if ($value.Length -eq 0) { '' } else { 'Synthetic ' + $field }
                }
                'imageUrl' {
                    if ($value -isnot [string]) { throw 'Unexpected image address type.' }
                    $uri = $null
                    $safe = [Uri]::TryCreate($value,[UriKind]::Absolute,[ref]$uri) -and $uri.Scheme -eq 'https' -and $uri.Host.EndsWith('.rbxcdn.com') -and $uri.UserInfo.Length -eq 0
                    $result[$field] = if ($safe) { 'https://tr.rbxcdn.com/00000000000000000000000000000000/150/150/AvatarHeadshot/Png/noFilter' } else { 'https://invalid.example/synthetic-image' }
                }
                { $_ -in 'state','type','serverType' } {
                    if ($value -isnot [string]) { throw 'Unexpected enum type.' }
                    $result[$field] = if ($value -in 'Completed','Pending','InReview','Blocked','Error','User','Group','Public') { $value } else { 'SyntheticUnknown' }
                }
                { $_ -in 'version','clientVersionUpload' } {
                    if ($value -isnot [string]) { throw 'Unexpected release type.' }
                    if ($value -notmatch '^(?:[0-9]{1,10}\.){3}[0-9]{1,10}$|^version-[0-9a-f]{16}$') { throw 'Unexpected public release shape.' }
                    $result[$field] = $value
                }
                default {
                    if ($value -isnot [ValueType] -or $value -is [datetime]) { throw 'Unexpected scalar type.' }
                    $result[$field] = $value
                }
            }
        }
        return $result
    }
    if ($source.Contains('errors')) {
        $clean = [ordered]@{ errors = @($source.errors | ForEach-Object { Project-Fields $_ @('code','message') }) }
    } else {
        switch ($Kind) {
            'Mapping' { $clean = Project-Fields $source @('universeId') }
            'Release' { $clean = Project-Fields $source @('version','clientVersionUpload') }
            { $_ -in 'Avatar','Icon' } {
                $clean = [ordered]@{ data = @($source.data | ForEach-Object {
                    $row = Project-Fields $_ @('targetId','state','imageUrl')
                    if ($Kind -eq 'Icon' -and $row.Contains('targetId') -and $row.targetId -eq 123) { $row.targetId = 84 }
                    $row
                }) }
            }
            'Game' {
                $clean = [ordered]@{ data = @($source.data | ForEach-Object {
                    $row = Project-Fields $_ @('id','rootPlaceId','name','playing','maxPlayers','visits','createVipServersAllowed','isContentRestricted')
                    if ($row.Contains('id') -and $row.id -eq 123) { $row.id = 84 }
                    if ($_.Contains('creator')) { $row.creator = Project-Fields $_.creator @('id','name','type') }
                    $row
                }) }
            }
            'Servers' {
                $clean = Project-Fields $source @('nextPageCursor','previousPageCursor')
                $ordinal = 0
                $clean.data = @($source.data | ForEach-Object {
                    $row = Project-Fields $_ @('id','playing','maxPlayers','fps','ping','vipServerId','privateServerId','accessCode','linkCode','reservedServerAccessCode','serverType')
                    # Preserve distinct valid job IDs without keeping a real join target.
                    if ($row.Contains('id') -and $row.id -eq '11111111-1111-1111-1111-111111111111') {
                        $ordinal++; $row.id = '11111111-1111-1111-1111-' + $ordinal.ToString('D12')
                    }
                    $row
                })
            }
            default { throw 'Unknown fixture kind.' }
        }
    }
    return ($clean | ConvertTo-Json -Depth 16 -Compress) + "`n"
}

if (-not $Capture) { return }
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Run this research tool with PowerShell 7.' }
$fixtureRoot = Join-Path $PSScriptRoot '../tests/Zenith.Sessions.Tests/fixtures'
$scriptHash = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
$requests = @(
    @{ Kind='Mapping'; Url='https://apis.roblox.com/universes/v1/places/85896571713843/universe' },
    @{ Kind='Game'; Url='https://games.roblox.com/v1/games?universeIds=6504986360' },
    @{ Kind='Avatar'; Url='https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds=1&size=150x150&format=Png&isCircular=false' },
    @{ Kind='Icon'; Url='https://thumbnails.roblox.com/v1/games/icons?universeIds=6504986360&returnPolicy=PlaceHolder&size=150x150&format=Png&isCircular=false' },
    @{ Kind='Servers'; Url='https://games.roblox.com/v1/games/85896571713843/servers/Public?sortOrder=Asc&excludeFullGames=false&limit=10' },
    @{ Kind='Release'; Url='https://clientsettingscdn.roblox.com/v2/client-version/WindowsPlayer' },
    @{ Kind='NotFound'; Url='https://users.roblox.com/v1/users/9223372036854775807' }
)
$handler = [Net.Http.HttpClientHandler]::new()
$handler.AllowAutoRedirect=$false; $handler.UseCookies=$false; $handler.UseDefaultCredentials=$false; $handler.Credentials=$null
$http = [Net.Http.HttpClient]::new($handler)
$http.Timeout=[TimeSpan]::FromSeconds(15)
$records = [Collections.Generic.List[object]]::new()
try {
    foreach ($spec in $requests) {
        $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get,$spec.Url)
        $request.Headers.UserAgent.ParseAdd('ZenithSessions/0.50')
        $request.Headers.Accept.ParseAdd('application/json')
        $deadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(15))
        $record = [ordered]@{ kind=$spec.Kind; url=$spec.Url; utc=[DateTimeOffset]::UtcNow.ToString('O'); method='GET'; anonymous=$true; bodyLimit=65536; sanitizer='Capture-ZenithPublicFixtures.ps1'; sanitizerSha256=$scriptHash }
        $response=$null; $stream=$null; $buffer=$null
        try {
            $response=$http.SendAsync($request,[Net.Http.HttpCompletionOption]::ResponseHeadersRead,$deadline.Token).GetAwaiter().GetResult()
            $record.status=[int]$response.StatusCode
            if ($response.Content.Headers.ContentLength -gt 65536) { throw 'Body exceeds capture limit.' }
            $stream=$response.Content.ReadAsStreamAsync($deadline.Token).GetAwaiter().GetResult()
            $buffer=[IO.MemoryStream]::new(); $bytes=[byte[]]::new(4096)
            while (($read=$stream.ReadAsync($bytes,0,$bytes.Length,$deadline.Token).GetAwaiter().GetResult()) -gt 0) {
                if ($buffer.Length+$read -gt 65536) { throw 'Body exceeds capture limit.' }
                $buffer.Write($bytes,0,$read)
            }
            $record.rawBodyBytes=$buffer.Length
            $json=[Text.Encoding]::UTF8.GetString($buffer.ToArray())
            $sanitized=ConvertTo-SanitizedPublicFixture -Kind $spec.Kind -Json $json
            $name='PublicCaptured'+$spec.Kind+'.sanitized.json'
            $path=Join-Path $fixtureRoot $name
            [IO.File]::WriteAllText($path,$sanitized,[Text.UTF8Encoding]::new($false))
            $record.file=$name; $record.sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant(); $record.outcome='Captured'
        } catch {
            # No remote body, response header or exception message is persisted on a failed capture.
            $record.outcome='Unavailable'; $record.errorType=$_.Exception.GetType().Name
        } finally {
            if ($stream) { $stream.Dispose() }; if ($buffer) { $buffer.Dispose() }; if ($response) { $response.Dispose() }
            $request.Dispose(); $deadline.Dispose()
        }
        $records.Add($record)
        Write-Host ($record.kind + ': ' + $record.outcome + $(if ($record.Contains('status')) { ' HTTP ' + $record.status }))
    }
} finally { $http.Dispose() }
$manifest=[ordered]@{ schema=1; note='Anonymous public shape captures; identifiers/text/URLs are synthetic. No 429 was induced. Tests make no network requests.'; captures=$records.ToArray() }
[IO.File]::WriteAllText((Join-Path $fixtureRoot 'PublicCaptured.provenance.json'),($manifest | ConvertTo-Json -Depth 16)+"`n",[Text.UTF8Encoding]::new($false))

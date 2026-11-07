# One explicitly approved historical file only. Never enumerate logs or print source contents.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$expectedUserRoot = 'C:\Users\noelm'
$sourceRoot = 'C:\Users\noelm\AppData\Local\Roblox\logs'
$sourcePath = Join-Path $sourceRoot '0.745.0.7451137_20261105T134938Z_Player_0E573_last.log'
$expectedHash = '1E65C248525B94CB5BD7626DDD8D7D653938A9B3F23EF88E09E7B095B58F9CDC'
$expectedLength = 968792L
$maximumBytes = 2 * 1024 * 1024
$maximumLineChars = 8192
$maximumEvents = 512
$version = '0.745.0.7451137'
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$fixturePath = Join-Path $repoRoot 'tests\Zenith.Sessions.Tests\fixtures\RobloxPlayer0745.sanitized.log'
$provenancePath = Join-Path $repoRoot 'artifacts\sessions-sanitized-log-fixture.json'
$temporaryFixture = $fixturePath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
$temporaryProvenance = $provenancePath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
$stream = $null
$reader = $null
$sha = $null
$buffer = [char[]]::new(4096)
$line = [Text.StringBuilder]::new($maximumLineChars)
$safeOutput = [Text.StringBuilder]::new()
$clock = [Diagnostics.Stopwatch]::StartNew()
$regexOptions = [Text.RegularExpressions.RegexOptions]::CultureInvariant
$regexTimeout = [TimeSpan]::FromMilliseconds(100)
# The metadata before the FIRST tag is discarded. A second embedded tag cannot become the first.
$prefix = [regex]::new('\A(?<time>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z),[^\[\]\r\n]{1,160}\[(?<tag>FLog::(?:Output|GameJoinLoadTime|Network|UgcExperienceController))\]\s*(?<body>.*)\z', $regexOptions, $regexTimeout)
$joining = [regex]::new("\A! Joining game '(?<job>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})' place (?<place>[0-9]+)(?=\s|[,;]|\z)", $regexOptions, $regexTimeout)
$identity = [regex]::new('\AReport game_join_loadtime:(?:.*?[\s,])?userid:(?<user>[0-9]+)(?=\s|[,;]|\z)', $regexOptions, $regexTimeout)
$connected = [regex]::new('\AReplicator created(?=\s|:|\z)', $regexOptions, $regexTimeout)
$disconnected = [regex]::new('\ASending disconnect with reason(?=\s|:|\z)', $regexOptions, $regexTimeout)
$teleporting = [regex]::new('\AUgcExperienceController: doTeleport(?=\s|:|\z)', $regexOptions, $regexTimeout)
$counts = [ordered]@{ Joining = 0; UserIdentityObserved = 0; Connected = 0; Disconnected = 0; Teleporting = 0 }
$sourceLines = 0
$oversizedLines = 0
$eventCount = 0
$failureStage = 'ApprovedSourceContext'

function ConvertTo-SafeEvent([string]$text, [int]$ordinal) {
    $match = $prefix.Match($text)
    if (-not $match.Success) { return $null }
    $timestamp = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse($match.Groups['time'].Value, [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::AssumeUniversal, [ref]$timestamp)) { return $null }
    $tag = $match.Groups['tag'].Value
    $body = $match.Groups['body'].Value
    $kind = $null
    $safeBody = $null
    switch ($tag) {
        'FLog::Output' {
            $minimal = $joining.Match($body)
            $job = [Guid]::Empty
            $place = 0L
            if ($minimal.Success -and [Guid]::TryParseExact($minimal.Groups['job'].Value, 'D', [ref]$job) -and $job -ne [Guid]::Empty -and
                [long]::TryParse($minimal.Groups['place'].Value, [Globalization.NumberStyles]::None, [Globalization.CultureInfo]::InvariantCulture, [ref]$place) -and $place -gt 0) {
                $kind = 'Joining'
                $syntheticJob = '11111111-2222-4333-8444-' + ($ordinal + 1).ToString('D12', [Globalization.CultureInfo]::InvariantCulture)
                $safeBody = "! Joining game '$syntheticJob' place 900000000000000001"
            }
        }
        'FLog::GameJoinLoadTime' {
            $minimal = $identity.Match($body)
            $user = 0L
            if ($minimal.Success -and [long]::TryParse($minimal.Groups['user'].Value, [Globalization.NumberStyles]::None,
                [Globalization.CultureInfo]::InvariantCulture, [ref]$user) -and $user -gt 0) {
                $kind = 'UserIdentityObserved'
                $safeBody = 'Report game_join_loadtime: userid:900000000000000002'
            }
        }
        'FLog::Network' {
            if ($connected.IsMatch($body)) { $kind = 'Connected'; $safeBody = 'Replicator created' }
            elseif ($disconnected.IsMatch($body)) { $kind = 'Disconnected'; $safeBody = 'Sending disconnect with reason' }
        }
        'FLog::UgcExperienceController' {
            if ($teleporting.IsMatch($body)) { $kind = 'Teleporting'; $safeBody = 'UgcExperienceController: doTeleport' }
        }
    }
    if ($null -eq $kind) { return $null }
    $syntheticTime = [DateTimeOffset]::new(2026, 1, 1, 0, 0, 0, [TimeSpan]::Zero).AddMilliseconds($ordinal)
    $safeLine = $syntheticTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", [Globalization.CultureInfo]::InvariantCulture) + ',0.000000,0000,6,Info [' + $tag + '] ' + $safeBody
    # Only constant templates, synthetic values and the allowlisted tag can reach the output.
    return [pscustomobject]@{ Kind = $kind; Text = $safeLine }
}

try {
    if (-not [string]::Equals([IO.Path]::GetFullPath($env:USERPROFILE), $expectedUserRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The approved current-user context does not match. No fixture was published.'
    }
    $checkedPath = $sourcePath
    while (-not [string]::IsNullOrEmpty($checkedPath)) {
        if (([IO.File]::GetAttributes($checkedPath) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'A redirected source path is not permitted. No fixture was published.'
        }
        $parent = [IO.Path]::GetDirectoryName($checkedPath)
        if ($parent -eq $checkedPath) { break }
        $checkedPath = $parent
    }
    # FileShare.Read refuses concurrent write/delete access; retain this same handle for both hashes and parsing.
    $stream = [IO.FileStream]::new($sourcePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read, 4096, [IO.FileOptions]::SequentialScan)
    if ($stream.Length -ne $expectedLength -or $stream.Length -gt $maximumBytes) {
        throw 'The approved source size changed or exceeded its bound. No fixture was published.'
    }
    $failureStage = 'ApprovedSourceFingerprint'
    $sha = [Security.Cryptography.SHA256]::Create()
    $sourceHash = [Convert]::ToHexString($sha.ComputeHash($stream))
    if ($sourceHash -ne $expectedHash) { throw 'The approved source fingerprint changed. No fixture was published.' }
    $stream.Position = 0
    $failureStage = 'BoundedAllowlistRead'
    $reader = [IO.StreamReader]::new($stream, [Text.UTF8Encoding]::new($false, $true), $false, 4096, $true)
    $discardLine = $false
    while (($read = $reader.Read($buffer, 0, $buffer.Length)) -gt 0) {
        if ($clock.Elapsed -gt [TimeSpan]::FromSeconds(30)) { throw 'The bounded sanitizer deadline expired. No fixture was published.' }
        $cursor = 0
        while ($cursor -lt $read) {
            $newline = [Array]::IndexOf($buffer, [char]10, $cursor, $read - $cursor)
            $end = if ($newline -lt 0) { $read } else { $newline }
            $length = $end - $cursor
            if (-not $discardLine) {
                if ($line.Length + $length -gt $maximumLineChars) {
                    [void]$line.Clear(); $discardLine = $true; $oversizedLines++
                } else { [void]$line.Append($buffer, $cursor, $length) }
            }
            if ($newline -lt 0) { break }
            $sourceLines++
            if (-not $discardLine) {
                $safe = ConvertTo-SafeEvent $line.ToString().TrimEnd([char]13) $eventCount
                if ($null -ne $safe) {
                    if ($eventCount -ge $maximumEvents) { throw 'The event output bound was exceeded. No fixture was published.' }
                    $counts[$safe.Kind]++; $eventCount++
                    [void]$safeOutput.Append($safe.Text).Append([char]10)
                }
            }
            [void]$line.Clear(); $discardLine = $false; $cursor = $newline + 1
        }
    }
    # Match reader complete-line boundaries: never promote an unterminated final source line.
    if ($line.Length -gt 0 -or $discardLine) { $sourceLines++ }
    $reader.Dispose(); $reader = $null
    $stream.Position = 0
    $failureStage = 'FinalSourceFingerprint'
    if ([Convert]::ToHexString($sha.ComputeHash($stream)) -ne $expectedHash -or $stream.Length -ne $expectedLength) {
        throw 'The approved source changed during reading. No fixture was published.'
    }
    $failureStage = 'ApprovedCountComparison'
    if ($sourceLines -ne 5078 -or $eventCount -ne 134 -or $counts.Joining -ne 14 -or $counts.UserIdentityObserved -ne 14 -or
        $counts.Connected -ne 28 -or $counts.Disconnected -ne 42 -or $counts.Teleporting -ne 36 -or $oversizedLines -ne 0) {
        throw 'Allowlisted event counts differ from the approved scan. No fixture was published; inspect only bounded count diagnostics.'
    }
    if ($clock.Elapsed -gt [TimeSpan]::FromSeconds(30)) { throw 'The bounded sanitizer deadline expired. No fixture was published.' }
    $failureStage = 'SanitizedPublication'
    $utf8 = [Text.UTF8Encoding]::new($false, $true)
    $fixtureBytes = $utf8.GetBytes($safeOutput.ToString())
    $fixtureHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($fixtureBytes))
    $provenance = [ordered]@{
        SourceVersion = $version
        SourceSha256 = $sourceHash
        SourceBytes = $expectedLength
        SourceLines = $sourceLines
        EventCounts = $counts
        FixtureLines = $eventCount
        FixtureBytes = $fixtureBytes.Length
        FixtureSha256 = $fixtureHash
        Limitations = @(
            'SANITIZED historical syntax fixture; not a live client, writer association or authentication authority.'
            'Only one exact historical Win32 log version/fingerprint was inspected; no global or other-variant support is established.'
            'First native tag only; five allowlisted event shapes. All other lines and body attributes are dropped.'
            'All timestamps, CSV metadata, UserId, PlaceId and jobs are deterministic synthetic values; real relationships and timing are not preserved.'
            'Input <=2 MiB, line <=8192 characters, output <=512 events, regex timeout 100 ms, elapsed read deadline 30 seconds.'
            'The same read-only handle is fingerprinted before and after parsing; no paths, raw lines, URLs, addresses or credentials are exported.'
            'An unterminated final source line is discarded. Managed transient source strings are not claimed to be perfectly erasable.'
            'Parser and correlator FormatSupport remain Unverified; fixture replay tests must be run separately.'
        )
    }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($fixturePath)) | Out-Null
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($provenancePath)) | Out-Null
    [IO.File]::WriteAllBytes($temporaryFixture, $fixtureBytes)
    [IO.File]::WriteAllText($temporaryProvenance, ($provenance | ConvertTo-Json -Depth 5) + [Environment]::NewLine, $utf8)
    [IO.File]::Move($temporaryFixture, $fixturePath, $true)
    [IO.File]::Move($temporaryProvenance, $provenancePath, $true)
    $provenance | ConvertTo-Json -Depth 5
} catch {
    # Never include the caught exception, source text, source path or matching values in tool output.
    if ($failureStage -eq 'ApprovedCountComparison') {
        [ordered]@{ SourceLines = $sourceLines; EventCounts = $counts; FixtureLines = $eventCount; OversizedLines = $oversizedLines } | ConvertTo-Json -Depth 3
    }
    throw "The approved bounded log sanitizer stopped at $failureStage; publication did not complete. No raw diagnostic output is permitted."
} finally {
    if ($null -ne $reader) { $reader.Dispose() }
    if ($null -ne $stream) { $stream.Dispose() }
    if ($null -ne $sha) { $sha.Dispose() }
    [Array]::Clear($buffer); [void]$line.Clear(); [void]$safeOutput.Clear()
    if ([IO.File]::Exists($temporaryFixture)) { [IO.File]::Delete($temporaryFixture) }
    if ([IO.File]::Exists($temporaryProvenance)) { [IO.File]::Delete($temporaryProvenance) }
}

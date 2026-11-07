# Exercises the production preflight without starting a VM or changing Windows.
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$validator=Join-Path $root 'scripts\Test-ZenithSandboxProfile.ps1'
$profile=Join-Path $PSScriptRoot 'Offline-Inspection.wsb'
$approved=[IO.File]::ReadAllText($profile)
[void](& $validator -ProfilePath $profile)
$onlineProfile=Join-Path $PSScriptRoot 'Online-Inspection.wsb'
[void](& $validator -ProfilePath $onlineProfile -NetworkMode Online)
$implicitOnlineRejected=$false
try { [void](& $validator -ProfilePath $onlineProfile) } catch { $implicitOnlineRejected=$true }
if(-not $implicitOnlineRejected) { throw 'An offline preflight accepted internet access.' }
$temporary=Join-Path ([IO.Path]::GetTempPath()) ('zenith-profile-test-'+[Guid]::NewGuid().ToString('N')+'.wsb')
try {
    $cases=@(
        $approved.Replace('<Networking>Disable</Networking>','<Networking>Enable</Networking>'),
        $approved.Replace('<ClipboardRedirection>Disable</ClipboardRedirection>',''),
        $approved.Replace('<MappedFolders />','<MappedFolders><MappedFolder><HostFolder>C:\</HostFolder></MappedFolder></MappedFolders>'),
        $approved.Replace('</Configuration>','<LogonCommand><Command>unapproved.exe</Command></LogonCommand></Configuration>'),
        $approved.Replace('</Configuration>','<Networking>Disable</Networking></Configuration>'),
        $approved.Replace('<MemoryInMB>4096</MemoryInMB>','<MemoryInMB>32768</MemoryInMB>'),
        ('<!DOCTYPE Configuration [<!ENTITY test SYSTEM "file:///C:/Windows/win.ini">]>'+$approved.Replace('<MappedFolders />','<MappedFolders>&test;</MappedFolders>')),
        $approved.Replace('<Configuration>','<Configuration xmlns="unexpected">'),
        $approved.Replace('<vGPU>Disable</vGPU>','<vGPU>Enable</vGPU>'),
        $approved.Replace('<Networking>Disable</Networking>','<networking>Disable</networking>')
    )
    $number=0
    foreach($candidate in $cases) {
        $number++
        [IO.File]::WriteAllText($temporary,$candidate)
        $rejected=$false
        try { [void](& $validator -ProfilePath $temporary) } catch { $rejected=$true }
        if(-not $rejected) { throw ('Invalid sandbox profile was accepted: case '+$number) }
    }
    $onlineUnsafe=[IO.File]::ReadAllText($onlineProfile).Replace('<MappedFolders />','<MappedFolders><MappedFolder><HostFolder>C:\</HostFolder></MappedFolder></MappedFolders>')
    [IO.File]::WriteAllText($temporary,$onlineUnsafe)
    $onlineUnsafeRejected=$false
    try { [void](& $validator -ProfilePath $temporary -NetworkMode Online) } catch { $onlineUnsafeRejected=$true }
    if(-not $onlineUnsafeRejected) { throw 'The online preflight accepted a host mapping.' }
    [pscustomobject]@{ ValidProfilesAccepted=2; InvalidProfilesRejected=($number+2); WindowsChanged=$false; VirtualMachinesStarted=0 }
} finally {
    if(Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
}

[CmdletBinding()]
param([switch]$AllowDownload)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$folder=Join-Path $root 'artifacts\sessions-owned-bridge-0.57\luau'
$archive=Join-Path $folder 'luau-windows.zip'
$archiveHash='5AFE2FD5DB34A045A2E4261EC0EA3144DA6346FF38808726D5AC6E06115081C6'
$expected=@{'luau.exe'='EE2463CD5F1E9D980FD2600AF9FD58EAAB5E69ABF1C9F318D91D8BDE877D992E';'luau-compile.exe'='C6221974A0D8C9567445188095A16FB734C64481B09381BD7A6DAE537F2758EB'}
$valid=$true
foreach($name in $expected.Keys){
    $file=Join-Path $folder $name
    if(-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $expected[$name]){$valid=$false}
}
if($valid){Write-Output $folder;return}
New-Item -ItemType Directory -Path $folder -Force | Out-Null
if(-not (Test-Path -LiteralPath $archive -PathType Leaf)){
    if(-not $AllowDownload){throw 'Pinned Luau 0.745 tools are missing. Provide the verified archive or explicitly use -AllowDownload.'}
    $temporary=$archive+'.'+[Guid]::NewGuid().ToString('N')+'.download'
    try{
        Invoke-WebRequest -UseBasicParsing -Uri 'https://github.com/luau-lang/luau/releases/download/0.745/luau-windows.zip' -OutFile $temporary -TimeoutSec 120
        if((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $archiveHash){throw 'Downloaded Luau archive did not match the pinned SHA-256.'}
        Move-Item -LiteralPath $temporary -Destination $archive
    }finally{if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary -Force}}
}
if((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $archiveHash){throw 'Cached Luau archive did not match the pinned SHA-256. It was preserved.'}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::OpenRead($archive)
try{
    foreach($name in $expected.Keys){
        $entry=$zip.GetEntry($name)
        if($null -eq $entry -or $entry.Length -gt 8MB){throw 'Pinned Luau archive entry is missing or exceeds the size limit.'}
        $stream=$entry.Open();$bytes=New-Object IO.MemoryStream
        try{$stream.CopyTo($bytes);$bytes.Position=0;$hash=(Get-FileHash -InputStream $bytes -Algorithm SHA256).Hash
            if($hash -ne $expected[$name]){throw 'Luau executable did not match the pinned SHA-256.'}
            [IO.File]::WriteAllBytes((Join-Path $folder $name),$bytes.ToArray())
        }finally{$bytes.Dispose();$stream.Dispose()}
    }
}finally{$zip.Dispose()}
Write-Output $folder

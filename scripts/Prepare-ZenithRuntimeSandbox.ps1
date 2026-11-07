[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$cache=Join-Path $root 'artifacts\sandbox-runtime-share'
$runtimeSource='C:\Program Files (x86)\Microsoft\EdgeWebView\Application'
$runtime=Get-ChildItem -LiteralPath $runtimeSource -Directory | Where-Object {$_.Name -match '^\d+\.\d+\.\d+\.\d+$'} | Sort-Object {[version]$_.Name} -Descending | Select-Object -First 1
if(-not $runtime) { throw 'A host WebView2 engine was not found.' }
$signature=Get-AuthenticodeSignature -LiteralPath (Join-Path $runtime.FullName 'msedgewebview2.exe')
if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation(?:,|$)') { throw 'The host runtime is not verified Microsoft code.' }
$entries=@(Get-ChildItem -LiteralPath $runtime.FullName -Recurse -Force)
if(@($entries | Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count -gt 0) { throw 'The source runtime contains redirected paths.' }
$files=@($entries | Where-Object {-not $_.PSIsContainer})
if($files.Count -gt 1490 -or ($files | Measure-Object Length -Sum).Sum -gt 2GB) { throw 'The runtime exceeds the test-cache bound.' }
if(Test-Path -LiteralPath $cache) {
    if((Get-Item -LiteralPath $cache).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The runtime cache is redirected.' }
    if(@(Get-ChildItem -LiteralPath $cache -Recurse -Force | Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count -gt 0) { throw 'The runtime cache contains a redirected path.' }
    if(Test-Path -LiteralPath (Join-Path $cache 'manifest.json')) {
        $old=Get-Content -LiteralPath (Join-Path $cache 'manifest.json') -Raw | ConvertFrom-Json
        if($old.RuntimeVersion -ne $runtime.Name) { throw 'The existing runtime cache uses another version. Preserve active guests before refreshing it.' }
    }
}
[IO.Directory]::CreateDirectory((Join-Path $cache 'Runtime')) | Out-Null
[IO.Directory]::CreateDirectory((Join-Path $cache 'Check')) | Out-Null
foreach($file in $files) {
    $relative=$file.FullName.Substring($runtime.FullName.Length+1)
    $destination=Join-Path (Join-Path $cache 'Runtime') $relative
    [IO.Directory]::CreateDirectory((Split-Path -Parent $destination)) | Out-Null
    if(-not (Test-Path -LiteralPath $destination) -or (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash) { [IO.File]::Copy($file.FullName,$destination,$true) }
}
$package=Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget\packages\microsoft.web.webview2\1.0.3800.47'
foreach($name in @('Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll')) { Copy-Item -LiteralPath (Join-Path $package ('lib\net462\'+$name)) -Destination (Join-Path $cache ('Check\'+$name)) -Force }
Copy-Item -LiteralPath (Join-Path $package 'runtimes\win-x64\native\WebView2Loader.dll') -Destination (Join-Path $cache 'Check\WebView2Loader.dll') -Force
$source=Join-Path $root 'tools\Zenith.Sessions.SandboxLab\RuntimeCheck.cs'
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:winexe /platform:x64 ('/out:'+(Join-Path $cache 'Check\RuntimeCheck.exe')) /reference:System.Windows.Forms.dll /reference:System.Drawing.dll ('/reference:'+(Join-Path $cache 'Check\Microsoft.Web.WebView2.Core.dll')) ('/reference:'+(Join-Path $cache 'Check\Microsoft.Web.WebView2.WinForms.dll')) $source
if($LASTEXITCODE -ne 0) { throw 'The runtime rendering check did not compile.' }
Copy-Item -LiteralPath (Join-Path $root 'tools\Zenith.Sessions.SandboxLab\InitializeRuntimeGuest.ps1') -Destination (Join-Path $cache 'InitializeRuntimeGuest.ps1') -Force
$manifestFiles=@(foreach($directory in @('Runtime','Check')) { Get-ChildItem -LiteralPath (Join-Path $cache $directory) -Recurse -File | ForEach-Object { [pscustomobject]@{Path=$_.FullName.Substring($cache.Length+1);Length=$_.Length;Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash} } })
[ordered]@{FormatVersion=1;RuntimeVersion=$runtime.Name;Files=$manifestFiles} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $cache 'manifest.json') -Encoding utf8
$config=[Xml.XmlDocument]::new()
$configuration=$config.CreateElement('Configuration'); [void]$config.AppendChild($configuration)
$values=[ordered]@{vGPU='Disable';Networking='Enable';AudioInput='Disable';VideoInput='Disable';PrinterRedirection='Disable';ClipboardRedirection='Disable';ProtectedClient='Enable';MemoryInMB='10240'}
foreach($name in $values.Keys) {$node=$config.CreateElement($name);$node.InnerText=$values[$name];[void]$configuration.AppendChild($node)}
$mapped=$config.CreateElement('MappedFolders');[void]$configuration.AppendChild($mapped)
$folder=$config.CreateElement('MappedFolder');[void]$mapped.AppendChild($folder)
foreach($pair in @(@('HostFolder',$cache),@('SandboxFolder','C:\ZenithSource'),@('ReadOnly','true'))) {$node=$config.CreateElement($pair[0]);$node.InnerText=$pair[1];[void]$folder.AppendChild($node)}
$logon=$config.CreateElement('LogonCommand');[void]$configuration.AppendChild($logon)
$setupBody=[IO.File]::ReadAllText((Join-Path $cache 'InitializeRuntimeGuest.ps1'))
$setupEncoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($setupBody))
$startupCommand='C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -NonInteractive -EncodedCommand '+$setupEncoded
if($startupCommand.Length -gt 28000){throw 'The guest setup exceeds the bounded startup-command size.'}
$command=$config.CreateElement('Command');$command.InnerText=$startupCommand;[void]$logon.AppendChild($command)
$profile=Join-Path $root 'tools\Zenith.Sessions.SandboxLab\Zenith-10GB-WebView2.wsb'
$config.Save($profile)
[pscustomobject]@{Profile=$profile;RuntimeVersion=$runtime.Name;CacheFiles=$manifestFiles.Count;CacheBytes=($manifestFiles|Measure-Object Length -Sum).Sum;MemoryInMB=10240;MappedFolders=1;ReadOnly=$true;BrowserProfilesShared=$false;ThirdPartyFilesShared=$false}

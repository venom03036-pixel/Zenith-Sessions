[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)] [string]$ProfilePath,
    [ValidateSet('Offline','Online')] [string]$NetworkMode='Offline'
)
# Read-only preflight. Online access must be selected explicitly; it cannot prove
# hypervisor isolation or authorize an online malware test.
$ErrorActionPreference='Stop'
$resolved=[IO.Path]::GetFullPath($ProfilePath)
$file=Get-Item -LiteralPath $resolved
if($file.PSIsContainer -or $file.Length -le 0 -or $file.Length -gt 65536) {
    throw 'Sandbox profile must be a nonempty XML file of at most 64 KiB.'
}
$settings=[Xml.XmlReaderSettings]::new()
$settings.DtdProcessing=[Xml.DtdProcessing]::Prohibit
$settings.XmlResolver=$null
$settings.MaxCharactersInDocument=65536
$reader=[Xml.XmlReader]::Create($resolved,$settings)
try {
    $document=[Xml.XmlDocument]::new()
    $document.XmlResolver=$null
    $document.Load($reader)
} finally { $reader.Dispose() }
$root=$document.DocumentElement
if($root.Name -cne 'Configuration' -or $root.NamespaceURI -ne '' -or $root.Attributes.Count -ne 0) {
    throw 'Sandbox profile needs a plain Configuration root without attributes.'
}
$expected=@{
    vGPU='Disable'; Networking='Disable'; AudioInput='Disable'; VideoInput='Disable'
    PrinterRedirection='Disable'; ClipboardRedirection='Disable'; ProtectedClient='Enable'
    MemoryInMB='4096'; MappedFolders=''
}
if($NetworkMode -eq 'Online') { $expected.Networking='Enable' }
$allowed=[Collections.Generic.HashSet[string]]::new([string[]]@($expected.Keys),[StringComparer]::Ordinal)
$seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach($child in $root.ChildNodes) {
    if($child.NodeType -in @([Xml.XmlNodeType]::Whitespace,[Xml.XmlNodeType]::SignificantWhitespace,[Xml.XmlNodeType]::Comment)) { continue }
    if($child.NodeType -ne [Xml.XmlNodeType]::Element -or $child.NamespaceURI -ne '' -or
        -not $allowed.Contains($child.Name) -or $child.Attributes.Count -ne 0 -or -not $seen.Add($child.Name)) {
        throw 'Sandbox profile has an unexpected, duplicate, namespaced, or attributed setting.'
    }
    if(@($child.ChildNodes | Where-Object { $_.NodeType -eq [Xml.XmlNodeType]::Element }).Count -ne 0 -or
        $child.InnerText.Trim() -cne $expected[$child.Name]) {
        throw ('Sandbox isolation setting is not the selected profile value: '+$child.Name)
    }
}
if($seen.Count -ne $expected.Count) { throw 'Sandbox profile is missing an explicit isolation setting.' }
[pscustomobject]@{
    Profile=$resolved; Sha256=(Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash
    ProfileValid=$true; OfflineProfileValid=($NetworkMode -eq 'Offline')
    NetworkMode=$NetworkMode; InternetEnabled=($NetworkMode -eq 'Online')
    HostFoldersShared=$false; ClipboardShared=$false
    ThirdPartyProgramsStarted=$false; OnlineRuntimeTestApproved=$false
}

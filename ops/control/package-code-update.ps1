param(
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9-]{1,40}$')][string]$Label,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{64}$')][string]$BaseSha256
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$artifactDirectory = Join-Path $PSScriptRoot '.artifacts'
New-Item -ItemType Directory -Force $artifactDirectory | Out-Null
$bundlePath = Join-Path $artifactDirectory ($Label + '-update.zip')
$jarPath = Join-Path $PSScriptRoot '../../openviya-backend/target/openviya-backend-0.1.0-SNAPSHOT.jar'
$inputZip = [IO.Compression.ZipFile]::OpenRead($jarPath)
$outputFile = [IO.File]::Open($bundlePath, [IO.FileMode]::Create)
$outputZip = [IO.Compression.ZipArchive]::new($outputFile, [IO.Compression.ZipArchiveMode]::Create)
$libraryHashes = [ordered]@{}
try {
    foreach ($entry in $inputZip.Entries) {
        if ($entry.FullName.StartsWith('BOOT-INF/lib/') -and $entry.Length -gt 0) {
            $stream = $entry.Open()
            $hasher = [Security.Cryptography.SHA256]::Create()
            try {
                $libraryHashes[$entry.FullName] = [BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-', '').ToLowerInvariant()
            } finally {
                $stream.Dispose()
                $hasher.Dispose()
            }
        }
        if ($entry.FullName.StartsWith('BOOT-INF/classes/') -and -not $entry.FullName.EndsWith('/')) {
            $copy = $outputZip.CreateEntry($entry.FullName, [IO.Compression.CompressionLevel]::Optimal)
            $source = $entry.Open()
            $destination = $copy.Open()
            try { $source.CopyTo($destination) } finally { $source.Dispose(); $destination.Dispose() }
        }
    }
    $metadata = @{base_sha256 = $BaseSha256; libraries = $libraryHashes}
    $manifest = $outputZip.CreateEntry('bundle-manifest.json')
    $writer = [IO.StreamWriter]::new($manifest.Open())
    try { $writer.Write(($metadata | ConvertTo-Json -Depth 5 -Compress)) } finally { $writer.Dispose() }
} finally {
    $outputZip.Dispose()
    $outputFile.Dispose()
    $inputZip.Dispose()
}
Get-Item $bundlePath | Select-Object Name, Length

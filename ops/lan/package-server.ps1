[CmdletBinding()]
param([string]$OutputPath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$artifacts = Join-Path $workspace 'openviya-ops/.artifacts'
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
if (-not $OutputPath) { $OutputPath = Join-Path $artifacts 'openviya-ops-lan-server.zip' }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$archive = [IO.Compression.ZipArchive]::new([IO.File]::Open($OutputPath, [IO.FileMode]::Create), [IO.Compression.ZipArchiveMode]::Create)
try {
    $paths = @('.dockerignore', 'openviya-ops/Directory.Build.props', 'ops/vps/bootstrap-vps.sh', 'ops/vps/maintenance-helper.py')
    foreach ($project in @('Core', 'Dns', 'Network', 'Provisioning', 'Server')) {
        $directory = Join-Path $workspace "openviya-ops/src/OpenViya.Ops.$project"
        foreach ($file in Get-ChildItem -LiteralPath $directory -File -Recurse) {
            $relative = [IO.Path]::GetRelativePath($workspace, $file.FullName).Replace('\', '/')
            if ($relative -match '/(bin|obj)/') { continue }
            if ($file.Extension -in @('.cs', '.csproj')) { $paths += $relative }
        }
    }
    foreach ($file in Get-ChildItem -LiteralPath $PSScriptRoot -File) {
        if ($file.Name -in @('Dockerfile', 'compose.yaml', 'nginx.conf', 'bootstrap.ps1', 'package-server.ps1', 'README.md', 'QUICKSTART.ru.md')) {
            $paths += "ops/lan/$($file.Name)"
        }
    }
    foreach ($relative in $paths | Sort-Object -Unique) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $workspace $relative), $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $archive.Dispose() }
Get-Item -LiteralPath $OutputPath | Select-Object FullName, Length

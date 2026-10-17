param([string] $OutputPath)
$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$bundledSdk = Join-Path $repository '.tools\dotnet\dotnet.exe'
$dotnetCommand = if (Test-Path -LiteralPath $bundledSdk) { $bundledSdk } else { (Get-Command dotnet.exe -ErrorAction Stop).Source }
$releaseApp = Join-Path $repository 'src\OpenViya.Ops.Desktop\bin\Release\net10.0-windows\OpenViya.Ops.Desktop.dll'
$debugApp = Join-Path $repository 'src\OpenViya.Ops.Desktop\bin\Debug\net10.0-windows\OpenViya.Ops.Desktop.dll'
$app = if (Test-Path -LiteralPath $releaseApp) { $releaseApp } else { $debugApp }
$tempData = Join-Path ([System.IO.Path]::GetTempPath()) ('OpenViya-Ops-QA-' + [guid]::NewGuid().ToString('N'))
if ([string]::IsNullOrWhiteSpace($OutputPath)) { $output = Join-Path $repository '.artifacts\ui-smoke.png' }
else { $output = [System.IO.Path]::GetFullPath($OutputPath) }
$outputDirectory = Split-Path -Parent $output
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
if (-not (Test-Path -LiteralPath $app)) { throw 'Build OpenViya.Ops.Desktop before running this smoke check.' }
$env:DOTNET_ROOT = Split-Path -Parent $dotnetCommand
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_NOLOGO = '1'
try {
    & $dotnetCommand $app --data-dir $tempData --smoke-test $output
    if ($LASTEXITCODE -ne 0) { throw "WPF smoke process failed with exit code $LASTEXITCODE." }
    if (-not (Test-Path -LiteralPath $output)) { throw 'WPF smoke image was not created.' }
    $file = Get-Item -LiteralPath $output
    if ($file.Length -lt 1000) { throw 'WPF smoke image is unexpectedly small.' }
    Write-Output "WPF smoke passed: $output ($($file.Length) bytes)"
}
finally {
    if (Test-Path -LiteralPath $tempData) {
        $resolvedTemp = (Resolve-Path -LiteralPath $tempData).Path
        $resolvedBase = [System.IO.Path]::GetTempPath().TrimEnd('\')
        if ($resolvedTemp.StartsWith($resolvedBase, [System.StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $resolvedTemp -Recurse -Force
        }
    }
}

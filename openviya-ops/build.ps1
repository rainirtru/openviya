param(
    [ValidateSet('Build', 'Check', 'Publish', 'Run')]
    [string]$Action = 'Build',
    [string]$OutputDirectory = '.artifacts/app'
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_NOLOGO = '1'
$opsLocalSdk = Join-Path $PSScriptRoot '.tools/dotnet/dotnet.exe'
if (Test-Path -LiteralPath $opsLocalSdk) {
    $opsDotnet = $opsLocalSdk
    $env:DOTNET_ROOT = Split-Path $opsLocalSdk
} else {
    $opsDotnet = (Get-Command dotnet -ErrorAction Stop).Source
}
function Invoke-OpsDotnet {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$CommandArguments)
    & $opsDotnet @CommandArguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}
Push-Location $PSScriptRoot
try {
    switch ($Action) {
        'Build' { Invoke-OpsDotnet @('build', 'OpenViya.Ops.slnx', '-c', 'Release') }
        'Check' {
            Invoke-OpsDotnet @('build', 'OpenViya.Ops.slnx', '-c', 'Release')
            foreach ($opsCheck in @('StorageChecks', 'SshChecks', 'NetworkChecks', 'DnsChecks', 'PublicDnsChecks', 'PanelAllowedIpsChecks', 'ProvisionStorageChecks', 'ProvisioningChecks', 'ProvisionTransportChecks', 'PanelOpeningChecks', 'WizardChecks', 'MaintenanceServiceChecks', 'MaintenanceChecks')) {
                Invoke-OpsDotnet @('run', '--project', "tests/$opsCheck/$opsCheck.csproj", '-c', 'Release')
            }
        }
        'Publish' {
            $opsOutput = if ([System.IO.Path]::IsPathFullyQualified($OutputDirectory)) { $OutputDirectory } else { Join-Path $PSScriptRoot $OutputDirectory }
            Invoke-OpsDotnet @('publish', 'src/OpenViya.Ops.Desktop/OpenViya.Ops.Desktop.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $opsOutput)
            Invoke-OpsDotnet @('publish', 'src/OpenViya.Ops.HostsHelper/OpenViya.Ops.HostsHelper.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', (Join-Path $opsOutput 'helper'))
        }
        'Run' {
            $opsHelperOutput = Join-Path $PSScriptRoot 'src/OpenViya.Ops.Desktop/bin/Release/net10.0-windows/helper'
            Invoke-OpsDotnet @('build', 'src/OpenViya.Ops.HostsHelper/OpenViya.Ops.HostsHelper.csproj', '-c', 'Release', '-o', $opsHelperOutput)
            Invoke-OpsDotnet @('run', '--project', 'src/OpenViya.Ops.Desktop/OpenViya.Ops.Desktop.csproj', '-c', 'Release')
        }
    }
} finally {
    Pop-Location
}

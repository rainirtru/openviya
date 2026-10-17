[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$LanBindIp,

    [switch]$ShowToken,

    [switch]$RenewCertificate
)

$ErrorActionPreference = 'Stop'

try {
    $parsedIp = [System.Net.IPAddress]::Parse($LanBindIp)
} catch {
    throw 'LanBindIp must be the server PC fixed/reserved IPv4 address on the home LAN.'
}
if ($parsedIp.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) {
    throw 'Use an explicit private LAN IPv4 address; wildcard and IPv6 binds are not supported.'
}
$octets = $parsedIp.GetAddressBytes()
$isPrivateIpv4 = $octets[0] -eq 10 -or
    ($octets[0] -eq 172 -and $octets[1] -ge 16 -and $octets[1] -le 31) -or
    ($octets[0] -eq 192 -and $octets[1] -eq 168)
if (-not $isPrivateIpv4) {
    throw 'LanBindIp must be an RFC1918 private IPv4 address (10/8, 172.16/12, or 192.168/16) on the home LAN.'
}

$lanDir = $PSScriptRoot
$secretDir = Join-Path $lanDir '.secrets'
$certDir = Join-Path $lanDir 'certs'
New-Item -ItemType Directory -Force -Path $secretDir, $certDir | Out-Null

# Keep the operator-readable credentials local to the Windows account running bootstrap.
foreach ($directory in @($secretDir, $certDir)) {
    & icacls.exe $directory /inheritance:r /grant:r "${env:USERNAME}:(OI)(CI)F" '*S-1-5-32-544:(OI)(CI)F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not restrict ACLs on $directory" }
}

function Write-RandomSecret([string]$Path, [int]$Bytes, [switch]$Hex) {
    $random = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes($Bytes)
    if ($Hex) {
        [System.IO.File]::WriteAllText($Path, [Convert]::ToHexString($random), [System.Text.Encoding]::ASCII)
    } else {
        [System.IO.File]::WriteAllBytes($Path, $random)
    }
}

$tokenPath = Join-Path $secretDir 'ops_token'
$encryptionKeyPath = Join-Path $secretDir 'encryption_key'
$postgresPasswordPath = Join-Path $secretDir 'postgres_password'
$connectionPath = Join-Path $secretDir 'db_connection_string'
if (-not (Test-Path -LiteralPath $tokenPath)) { Write-RandomSecret $tokenPath 32 -Hex }
if (-not (Test-Path -LiteralPath $encryptionKeyPath)) { Write-RandomSecret $encryptionKeyPath 32 }
if (-not (Test-Path -LiteralPath $postgresPasswordPath)) {
    $dbPassword = [Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(36))
    [System.IO.File]::WriteAllText($postgresPasswordPath, $dbPassword, [System.Text.Encoding]::ASCII)
}
if (-not (Test-Path -LiteralPath $connectionPath)) {
    $dbPassword = [System.IO.File]::ReadAllText($postgresPasswordPath).Trim()
    $connection = "Host=postgres;Port=5432;Database=openviya_ops;Username=openviya_ops;Password=$dbPassword"
    [System.IO.File]::WriteAllText($connectionPath, $connection, [System.Text.Encoding]::UTF8)
}

$certificatePath = Join-Path $certDir 'server.crt'
$privateKeyPath = Join-Path $certDir 'server.key'
$existingCert = Test-Path -LiteralPath $certificatePath
$existingKey = Test-Path -LiteralPath $privateKeyPath
if (-not $RenewCertificate -and ($existingCert -xor $existingKey)) {
    throw 'Only one TLS certificate file exists. Restore the matching cert/key pair or rerun with -RenewCertificate.'
}

if (-not $RenewCertificate -and $existingCert -and $existingKey) {
    $existingEnvPath = Join-Path $lanDir '.env'
    if (Test-Path -LiteralPath $existingEnvPath) {
        $previousIp = (Get-Content -LiteralPath $existingEnvPath | Where-Object { $_ -match '^LAN_BIND_IP=' } | Select-Object -First 1) -replace '^LAN_BIND_IP=', ''
        if ($previousIp -and $previousIp -ne $LanBindIp) {
            throw "The saved certificate is for $previousIp. Rerun with -RenewCertificate after verifying the new reserved LAN address and update every client pin."
        }
    }
    $loadedCert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::CreateFromPemFile($certificatePath, $privateKeyPath)
    try {
        if (-not $loadedCert.HasPrivateKey) { throw 'The saved TLS certificate has no matching private key. Rerun with -RenewCertificate.' }
        if ($loadedCert.NotAfter -le (Get-Date)) { throw 'The saved TLS certificate has expired. Rerun with -RenewCertificate and update every client pin.' }
        $san = $loadedCert.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.17' } | Select-Object -First 1
        $sanText = if ($san) { $san.Format($false) } else { '' }
        $expectedSan = '(?im)^\s*IP Address\s*=\s*' + [regex]::Escape($LanBindIp) + '\s*$'
        if ($sanText -notmatch $expectedSan) {
            throw "The saved TLS certificate does not contain $LanBindIp. Rerun with -RenewCertificate and update every client pin."
        }
        $fingerprint = $loadedCert.GetCertHashString([System.Security.Cryptography.HashAlgorithmName]::SHA256)
    } finally { $loadedCert.Dispose() }
} else {
    $cert = New-SelfSignedCertificate `
        -Subject "CN=$LanBindIp" `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -KeyAlgorithm RSA `
        -KeyLength 3072 `
        -KeyExportPolicy Exportable `
        -NotAfter (Get-Date).AddYears(5) `
        -TextExtension @("2.5.29.17={text}IPAddress=$LanBindIp")
    try {
        [System.IO.File]::WriteAllText($certificatePath, $cert.ExportCertificatePem(), [System.Text.Encoding]::ASCII)
        $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
        try {
            [System.IO.File]::WriteAllText($privateKeyPath, $rsa.ExportPkcs8PrivateKeyPem(), [System.Text.Encoding]::ASCII)
        } finally { $rsa.Dispose() }
        $fingerprint = $cert.GetCertHashString([System.Security.Cryptography.HashAlgorithmName]::SHA256)
    } finally {
        Remove-Item -LiteralPath "Cert:\CurrentUser\My\$($cert.Thumbprint)" -ErrorAction SilentlyContinue
        $cert.Dispose()
    }
}

# Preserve an explicit binding in Compose; do not expose a wildcard listener.
[System.IO.File]::WriteAllText((Join-Path $lanDir '.env'), "LAN_BIND_IP=$LanBindIp`n", [System.Text.Encoding]::ASCII)

Write-Host "LAN HTTPS URL: https://${LanBindIp}:8444"
Write-Host "Certificate SHA-256 pin (enter this in each desktop client): $fingerprint"
Write-Host "Admin token: ******** (stored in ops/lan/.secrets/ops_token)"
Write-Host 'Secrets and certificate are ready. Keep this folder and the PostgreSQL volume backed up together.'
if ($ShowToken) {
    Write-Host 'Admin token (visible on request):'
    Get-Content -Raw -LiteralPath $tokenPath
}

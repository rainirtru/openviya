# OpenViya Ops on a home LAN

This deployment hosts the shared Ops registry and durable VPS setup jobs on a separate Windows 11 PC. Windows clients connect to that PC over HTTPS. Docker Desktop runs Linux containers; PostgreSQL and the Ops API stay on a private Compose network, and only the Nginx HTTPS listener is published.

## Requirements and first setup

- Windows 11 PC on the home LAN, Docker Desktop configured for Linux containers, and PowerShell 7.
- Reserve a stable IPv4 address for the PC in the router's DHCP settings. Do not configure router port forwarding or expose this service to the internet.
- From this folder, create secrets and a self-signed certificate for the reserved address:

  ```powershell
  .\bootstrap.ps1 -LanBindIp 192.168.1.50
  ```

  Replace the example address with the PC's reserved RFC1918 address (`10/8`, `172.16/12`, or `192.168/16`). The script rejects public/wildcard addresses, creates an API bearer token and separate AES key, creates PostgreSQL credentials, writes the Compose LAN binding and a self-signed certificate, and prints the certificate SHA-256 fingerprint. The certificate is preserved on later runs so revealing the token does not change client trust. The token is masked. Reveal it only when you are ready to enter it on a trusted desktop:

  ```powershell
  .\bootstrap.ps1 -LanBindIp 192.168.1.50 -ShowToken
  ```

  Save the fingerprint and token securely. Enter the URL, token, and fingerprint in each desktop client's remote Ops settings. The client pins the fingerprint. If the server's LAN IP changes, explicitly issue a new certificate and pin:

  ```powershell
  .\bootstrap.ps1 -LanBindIp 192.168.1.51 -RenewCertificate
  ```

  A certificate renewal changes the pin; update every client after verifying the fingerprint printed by the host.

- Start the deployment explicitly when ready:

  ```powershell
  docker compose up -d --build
  docker compose ps
  ```

  To stop it, use `docker compose down`. The database volume remains unless separately removed. Do not use `down -v` unless intentionally destroying the stored registry and job state.

- On each desktop or laptop, open OpenViya Ops storage settings and choose **Общий сервер в локальной сети**. Enter `https://<reserved-IP>:8444`, the SHA-256 fingerprint printed by bootstrap, and the bearer token. Use **Проверить защищённое соединение**, then save and restart the app. On the first device, use the optional profile migration button if its existing local profiles should be copied to the shared server. Migration leaves the local database and SSH identities on that device; configure the other clients separately with the same URL, pin, and token.

## Network and availability

The service listens on the one IPv4 address in `.env`, port `8444`. Allow that port only from trusted home-LAN devices in Windows Defender Firewall/router policy. Bootstrap does not change firewall rules. Do not bind `0.0.0.0`, add a router port-forward, or publish the database/API container ports. The server container has outbound network access for VPS SSH/DNS tasks; PostgreSQL has no outbound network access.

The host PC must be awake and Docker Desktop running for clients to sync and for queued VPS jobs to execute. Windows sleep, shutdown, Docker Desktop exit, or loss of LAN/power pauses jobs until service availability returns. Set Windows power policy to keep this PC awake when it is intended to be the always-on LAN host.

## Secrets and backups

`.secrets/`, `certs/`, and `.env` are local-only and ignored by Git. Keep the token, encryption key, database password/connection string, private TLS key, and PostgreSQL volume restricted to the host administrator. The encryption key is independent of the database; losing it makes encrypted stored secrets unreadable. Bootstrap preserves existing API/database/encryption secrets, including when `-RenewCertificate` replaces the TLS certificate. Rotating the encryption key requires an application-supported re-encryption procedure, not merely replacing the file.

Generated VPS SSH keys are encrypted in PostgreSQL and materialized only in the server's ephemeral tmpfs while needed. For a recoverable backup, stop writes or use a PostgreSQL-consistent dump, and preserve the dump together with `.secrets/encryption_key`, `.secrets/ops_token`, `.secrets/postgres_password`, `.secrets/db_connection_string`, and `certs/`. For example:

```powershell
$dumpName = "openviya-ops-$(Get-Date -Format yyyyMMdd-HHmmss).dump"
docker compose exec -T postgres pg_dump -U openviya_ops -d openviya_ops -Fc -f /tmp/openviya-ops.dump
docker compose cp postgres:/tmp/openviya-ops.dump ".\$dumpName"
docker compose exec -T postgres rm -f /tmp/openviya-ops.dump
```

Store the dump and key/certificate backup encrypted and off the host. Restore PostgreSQL data and the matching encryption key before starting the API. Desktop clients retain their own protected remote settings and certificate pin; retain the Windows user profile or re-enter the token and pin after a client reinstall. Keep the desktop's SSH host-key trust data backed up as appropriate; this LAN service does not replace SSH fingerprint verification.

To rotate/reissue the host certificate, preserve the backup first and rerun bootstrap with `-RenewCertificate`, then update the displayed fingerprint on every client. Existing secrets remain in place during this operation.

## Troubleshooting

- `docker compose ps` shows health/restart status for all services; `docker compose logs --tail 100 server proxy postgres` shows service logs.
- A client connection failure is commonly a changed LAN address, sleeping host, stopped Docker Desktop, or Windows Firewall rule. Confirm the URL uses the reserved IP and port `8444`.
- Certificate pin errors mean the saved fingerprint differs from the certificate currently served. Verify the new fingerprint directly on the host before changing client trust.
- No firewall or router configuration is performed by these scripts.

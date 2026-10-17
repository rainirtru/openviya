# OpenViya Ops LAN server

This ASP.NET Core 10 service is the shared profile store and LAN-side VPS executor. The WPF clients call it over the home network; provisioning/maintenance SSH sessions, DNS provider calls, and durable jobs run in this service. Interactive SSH terminals, panel tunnels, and hosts changes remain local to each desktop. The server image listens on `http://+:8080`; terminate HTTPS at the LAN reverse proxy and expose the proxy only on the chosen LAN address.

## Required configuration

- `OPENVIYA_OPS_TOKEN_FILE`: read-only mounted bearer token file, at least 32 bytes. No built-in token exists.
- `OPENVIYA_OPS_ENCRYPTION_KEY_FILE`: read-only mounted 32-byte AES key (raw bytes, 64 hex characters, or base64).
- `OPENVIYA_OPS_DB_CONNECTION_STRING_FILE`: read-only mounted Npgsql connection string. `OPENVIYA_OPS_DB_CONNECTION_STRING` and `ConnectionStrings:Ops` are supported alternatives.
- `OPENVIYA_OPS_RUNTIME_DIR`: private ephemeral runtime directory, default `/tmp/openviya-worker`. Mount it as tmpfs. Managed SSH key files are rehydrated there from the encrypted PostgreSQL vault.
- `ASPNETCORE_URLS`: defaults to `http://0.0.0.0:8080`.

PostgreSQL stores application records as AES-GCM ciphertext. The key, bearer token, and database credentials are supplied outside the image. Do not reuse the encryption key for any other environment. Back up it alongside the database; encrypted records cannot be recovered without it.

## API

All routes except `GET /api/health` require `Authorization: Bearer <token>`.

| Area | Routes |
| --- | --- |
| Profiles | `GET /api/profiles`, `PUT /api/profiles/{id}`, `DELETE /api/profiles/{id}`, `POST /api/profiles/import` (multipart `file`), `GET /api/profiles/export` |
| Shared state | `GET/PUT /api/servers/{id}/observations`, `GET/PUT /api/servers/{id}/dns-records` |
| Protected secrets | `GET/POST/PUT /api/servers/{id}/credentials`, `GET/PUT /api/servers/{id}/host-key`, explicit opt-in `PUT /api/servers/{id}/admin-key`, `GET/PUT/DELETE /api/dns/token` |
| Provisioning | `POST /api/provisioning/runs`, `GET /api/provisioning/runs/{runId}`, `GET /api/provisioning/runs/{runId}/history`, `GET /api/provisioning/history?serverId=…`, explicit `POST /api/provisioning/runs/{runId}/resume` |
| Maintenance jobs | `POST /api/maintenance/jobs`, `GET /api/maintenance/jobs/{runId}` |
| Maintenance reads | `POST /api/servers/{id}/maintenance/{inspect,plan-access,check-certificate,updates}` |
| Backup download | `POST /api/servers/{id}/maintenance/backups/{name}` with `RemoteMaintenanceRequest` JSON body |

Provisioning requests contain explicit key bytes instead of client-local paths. The queue stores the full request encrypted and continues if a client disconnects. Run IDs are idempotency keys. Interrupted provisioning resumes under the same ID; it never silently starts a second run. Mutating maintenance jobs (`apply-access`, `renew-certificate`, `create-backup`) are queued durably. An interrupted maintenance mutation is marked `needs-attention`; inspect the VPS before retrying it.

`RemoteSshCredential`, `RemoteProvisionRequest`, `RemoteMaintenanceJobRequest`, and related request records are declared in `OpenViya.Ops.Core/RemoteContracts.cs`. Key files are written with owner-only permissions beneath the runtime tmpfs and deleted after the SSH action. The server never reads or trusts a path supplied by a client.

`PanelBasePath`, SSH and Timeweb credentials, selected private keys, panel credentials, and queued secret-bearing requests are encrypted before PostgreSQL writes. Profile exports omit `IdentityFile` and `PanelBasePath`. API errors and server logs avoid request bodies, secret values, provider responses, and SSH output.

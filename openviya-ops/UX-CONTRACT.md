# UX Contract

## Product context

- Audience: single Windows 11 infrastructure operator managing existing VPS.
- Primary jobs: keep local profiles, verify SSH identity/authentication, control owned tunnels, diagnose endpoints, manage DNS in automatic or manual mode, inspect/change owned hosts entry, open a TLS-verified panel.
- Target market(s): local Windows operator environment; no customer-facing interface.
- Active locales: `ru-RU` UI; system locale dates/times.
- Language/content register: Russian plain operational language; technical identifiers preserve canonical spelling.
- Timezone/calendar policy: Windows user timezone and Gregorian calendar.
- Accessibility target: native WPF keyboard and accessibility semantics; runtime Windows UI Automation verification required.

## Business-context sources

| Domain / scope | Authoritative source | Source type | Reviewed date |
|---|---|---|---|
| Product and infrastructure boundaries | `ops/LOCAL-OPS-APP-ARCHITECTURE.md` | Architecture | 2026-10-16 |
| Server domains, ports, and network roles | `ops/NETWORK-PLAN.md` | Network plan | 2026-10-16 |
| Shared UI-to-adapter contracts | `openviya-ops/IMPLEMENTATION.md`, `src/OpenViya.Ops.Core/Contracts.cs` | Implementation contract | 2026-10-16 |
| Profile validation and domain defaults | `src/OpenViya.Ops.Core/ProfileValidation.cs` | Shared implementation | 2026-10-16 |

## Visual contract

- Project `DESIGN.md`: `openviya-ops/DESIGN.md`.
- Token ownership model: DESIGN.md owns palette intent; runtime WPF adapter is canonical.
- Runtime token source: `src/OpenViya.Ops.Desktop/Theme.xaml`.
- Mapping/export/adapters: explicit named WPF colors/brushes in Theme.xaml, consumed by all desktop windows and controls.
- Token drift gate: compare every normative palette value in DESIGN.md with Theme.xaml during desktop review; no independent inline palette except status-specific selected accents.
- Supported themes: dark operational theme.
- Design-context owner: desktop UI implementation.

## Canonical UI Map

| Capability | Canonical owner | Source of truth | Allowed variants | Verification |
|---|---|---|---|---|
| Table Selection | WPF DataGrid selection | UX contract + native controls | single row | Windows UI Automation; keyboard |
| Select/Listbox | WPF ComboBox | UX contract | native popup accepted | keyboard and Windows UI Automation |
| Date | Not applicable | — | — | — |
| Form | MainWindow profile editor + Core.ProfileValidator | shared contract and validator | create / edit | CRUD smoke + UI Automation |
| Scrollbar | WPF ScrollViewer/DataGrid native scrollbars | DESIGN.md | form scroll, table own scroll | Windows UI Automation |
| Toast | Persistent footer status line | UX contract | success / warning / info / error | inspect visible state and screen reader announcement |
| CRUD | MainWindow + IProfileStore | local registry contract | save returns to selected details | --smoke-test and manual CRUD |
| DNS settings | DnsSettingsWindow + IDnsTokenStore / IDnsProviderService | Timeweb DNS contract | mask and DPAPI-protect token; read-only zone check | isolated smoke, no token or provider calls |
| DNS plan | MainWindow details card + IDnsProviderService / IDnsRecordStore | DnsContracts | auto mode creates only missing records on save; manual mode is local-only | isolated smoke, provider API not contacted |
| Public DNS | MainWindow details card + IPublicDnsService | Network.PublicDnsService | explicit Cloudflare DNS-over-HTTPS read-only check | isolated smoke, public resolver not contacted |
| Fresh VPS provisioning | VpsProvisionWizardWindow + IVpsProvisioner | ProvisioningContracts and VPS-provisioning contract | new profile only; auto/manual DNS; explicit host fingerprint and freshness/firewall/ACME confirmations | isolated smoke with fake provisioner; no SSH/DNS/VPS calls |
| Public panel allowed IPs | VpsProvisionWizardWindow + MainWindow profile editor + Core.PanelAllowedIps | optional public-panel source restriction in provisioning request and saved profile settings | one IPv4/IPv6 per line, maximum 100; blank lines ignored, duplicates removed; tunnel-only ignores the setting; edits preserve existing access and allowed-IP values | focused strict-input checks + multiline Both-mode native smoke |
| Provisioning status | MainWindow selected-profile card + IVpsProvisionStore | persisted latest run for selected profile | resume incomplete run; credentials visible only for completed run | 900×620 isolated smoke snapshots |
| Provisioning journal | ProvisionProgressLog + VpsProgressWindow + IVpsProvisionStore | allow-listed remote steps and safe provisioner messages | fixed current-step area; expandable live journal; saved history viewer; up to 200 deduplicated events per run | fake remote failure/legacy protocol checks, SQLite reopen/bounds checks, running/error/680px smoke |
| Panel credentials | VpsCredentialsWindow + IVpsProvisionStore | local encrypted credentials record | masked by default, per-field explicit reveal/copy | isolated preview credentials only |
| Panel opening | `PanelOpeningService` + `ITunnelManager`, `IDiagnosticsService`, `IHostsService` | profile opening mode and strict system TLS validation | tunnel mode starts its owned SSH tunnel and offers an explicit hosts/UAC action; public HTTPS mode never starts a tunnel, removes only an explicitly confirmed owned loopback hosts entry, and uses the configured remote port | fake orchestration checks; strict TLS required before browser launch |
| Server observations | `IServerStateStore` + `PanelOpeningViewModel` | persisted `ServerObservedState` | desired opening mode and current tunnel snapshot are separate from timestamped SSH/panel/certificate observations | isolated storage checks and selected-profile UI smoke |

Native controls are accepted because this is a Windows-only native desktop app. The premium web audit's browser-specific invariants (HTML `noValidate`, browser ARIA live regions, CSS global scrollbar declarations) do not map to WPF; equivalent behavior must be evaluated with Windows UI Automation.

## Component behavior

- Buttons have stable labels and sizes; pending work is stated in the footer.
- Form errors remain inline in the editor and preserve entered values.
- Search filters locally and clears synchronously; results page at 20 profiles.
- DataGrid sorting and keyboard row selection are native WPF behavior.
- Tunnel state changes update the selected profile detail from the manager event.
- The selected-profile card leads with Open panel, SSH check, and diagnostics. Host, trust, TLS, and DNS tools live in action tabs. Stop is labeled “Остановить туннель”.
- Tunnel opening starts the owned tunnel when needed, then checks the exact panel HTTPS endpoint and hosts mapping. If the mapping is absent, the operator must confirm the OpenViya Ops entry before HostsHelper requests UAC. Conflicts block opening and do not change hosts.
- Public HTTPS opening uses the profile domain, remote port, and complete protected path. It does not start a tunnel. A conflicting hosts entry blocks; an owned OpenViya Ops loopback entry can be removed only after explicit confirmation. HTTPS uses the normal system proxy and operating-system certificate/name validation; redirects and non-success responses block browser launch.
- Opening checks require successful HTTPS before the browser starts. The complete protected panel path is passed only to the browser process and is omitted from ordinary status text.
- Desired opening mode, owned tunnel state, and persisted SSH/panel/certificate observations are shown as distinct values. A check timestamp labels saved observations; rendering never describes an unperformed probe as current.
- Missing registry data, empty registry, and no search matches have distinct copy.
- Confirmation dialog starts on the least destructive action and restores the owner on close.
- Automatic subdomains regenerate from role, region, index, and zone; save persists the profile before attempting Timeweb creation. If token/API setup fails, the saved profile remains selected with a recovery status.
- Manual subdomain mode lets the operator edit custom domains and never writes to provider APIs. DNS is configured separately on the provider website.
- Fresh VPS setup is a separate owned wizard and creates a new local profile only after explicit operator action. It requires an initial root password or private key, independent ssh-ed25519 fingerprint verification, fresh-server confirmation, provider-firewall confirmation, and acceptance of Let’s Encrypt terms. Credentials for the initial root access are memory-only; close/stop cancels local RunAsync observation while an already-started remote systemd stage may continue. The 20-minute rollback applies only after remote access preparation starts.
- Resume uses the persisted run ID and the saved administrative identity; initial root access is optional if that identity is available. No provisioning probe or mutation runs at application launch or import.
- The latest provisioning stage appears in the selected profile card. Panel credentials load only after the operator opens the dedicated window, stay masked by default, and expose separate explicit reveal/copy controls. They never appear in ordinary status text.
- Current server substeps arrive through a fixed-code status protocol; arbitrary remote text is discarded. A failed job shows its recognized step and exit code. The wizard retains full failure text and timestamped events; a local history window remains available from the selected profile. History is stored per run with a 200-event limit, consecutive identical polls are deduplicated, and an older approved installer honestly falls back to coarse stages. No percentage is invented. Opening history is local-only and does not resume or contact the VPS.
- DNS plan conflicts block all writes. Existing records are never overwritten; successful record IDs are stored locally for ownership tracking. Provider failures show per-name outcomes without raw exception dumps.
- Timeweb token input remains masked and is stored with DPAPI for the current Windows user. Zone verification is read-only and starts only from its button.
- Public DNS verification is explicitly labeled as Cloudflare, read-only, and never runs on launch/save/import. Manual mode checks the operator-entered names locally without reading the Timeweb token; it reports that expected authoritative NS cannot be compared when no expected set is known.

## Installed-server maintenance

- Canonical owner: `ServerMaintenanceWindow` + `ServerMaintenanceViewModel` + `IVpsServerMaintenanceService`; profile settings persist through `IProfileStore`, and the latest non-secret panel observation persists through `IServerStateStore`.
- Opening the window performs no network work. Every VPS request follows an explicit operator action and uses only the profile's previously accepted application `known_hosts` Ed25519 key. Missing, damaged, or ambiguous saved trust blocks maintenance; this workflow never scans candidates or accepts a key automatically.
- SSH uses the profile's existing administrative username. When the profile has a private key, the masked field accepts its optional passphrase; otherwise it accepts the administrative SSH password. No initial root password is requested or saved. The field clears after each operation and credentials are not kept in the profile or status text.
- “Проверить текущее состояние” reads the actual panel port, access mode, source restrictions, health, UFW alignment, certificate, backups, and 3x-ui version. It does not change remote state. Checked values and desired edits are labeled separately.
- Draft changes are not sent until a current plan is prepared. The plan shows port, access, source restrictions, and firewall-rule repair. Edits invalidate that plan. Applying changes requires an app-owned confirmation; a VPS-side five-minute rollback remains active until the service verifies a new SSH connection and panel health. If a port change would affect an owned tunnel, the operator confirms its stop before the change begins. The tool never edits a hosting provider firewall.
- Public HTTPS with an empty source list means any source. Confirmation states that consequence and the provider firewall check. When an IP list is present, it accepts up to 100 plain IPv4/IPv6 literals, one per line.
- Certificate renewal is an explicit second confirmation that names the external Let’s Encrypt ACME request and installation. Certificate checks are read-only. The 3x-ui action checks the official version only; it does not install updates.
- Backups are created explicitly, verified on the VPS, and selected from the returned archive list before download. The native Save dialog asks for the local destination. No archive is downloaded or saved on window open.
- Long operations use one pending footer status and can be canceled by the operator or by closing the window. A cancellation or timeout does not claim that a remote mutation rolled back; the next action is to inspect VPS state before retrying. No automatic retries run.
- At 900×680 the native form scrolls inside the window. Controls retain visible keyboard focus, standard Enter/newline and Tab behavior for the IP field, native accessible labels, and a stable footer. Verification: isolated fake-service ViewModel checks and temporary-data WPF smoke; actual Windows UI Automation remains the manual accessibility check.

## Dataset navigation

- Page size: 20 profiles.
- Search scope: current local registry by name, host, role, region, panel domain, and VPN domain; transient and local-only.
- Empty/no-results/error/loading: separate localized states; status line reports pending actions; registry read failure has an unavailable state.
- Selection: one profile; delete is disabled by explanation while its tunnel is active or starting/stopping.

## Flow ledger

| Operation | Trigger | Pending | Success destination | Success feedback | Failure recovery | Focus outcome | Source ref |
|---|---|---|---|---|---|---|---|
| Create | Add server | Save pending in footer | Selected detail | Saved locally | Inline validator/store error; values preserved | Name field on open |
| Edit | Edit selected | Save pending in footer | Selected detail | Saved locally | Inline validator/store error | Name field on open |
| Delete | Delete selected | Confirmation; unavailable with active tunnel | Registry list | Profile removed locally | Status error; profile remains | Dialog returns to owner/list |
| Search | Type | Immediate local filter | Current page 1 | Result count | Clear query | Search retains focus |
| SSH trust | Scan host keys then select candidate and explicit confirm | `ssh-keyscan` in progress | Selected detail | Fingerprint accepted for profile | Error in footer; no automatic accept | Modal selection and focus restore |
| Tunnel | Start/stop | Operation state in footer and snapshot | Selected detail | Owned tunnel state | Clear recovery message | Invoking control retains focus |
| Open panel | Open panel | Start owned tunnel when tunnel mode needs one; inspect hosts; perform strict HTTPS check | External browser | Mode, checked endpoint, and result in footer without the protected path | Do not open; report tunnel, hosts, TLS, or HTTP recovery step; explicit confirmation precedes hosts changes | Browser intentionally takes focus only on success |
| Hosts | Inspect/apply/remove | Explicit action and UAC as needed | Selected detail | Helper result/status | Conflict or canceled UAC reported; retry after resolving | Confirmation restores owner |
| Automatic DNS | Save automatic profile, or explicit DNS retry | Timeweb plan and creation pending | Selected detail | Per-name create/already/conflict results; IDs persisted locally | Missing token or API error keeps profile and shows recovery status; conflicts are not written | Invoking control retains focus |
| Manual DNS | Save manual profile | Local profile save only | Selected detail | Custom names saved locally | DNS changes remain the operator's provider-site task | Invoking control retains focus |
| DNS settings | Open Timeweb settings; save/clear token or check zone | Local encryption or bounded read-only API check | Settings window | Token state or zone/NS result | Safe localized error, no token display | Owned window returns focus to owner |
| Fresh VPS setup | Select “Настроить новый VPS” | Owned wizard: fingerprint scan, independent verification, then persisted run stages | Selected new profile | Current safe stage in wizard and main profile card | Local workflow cancellation; saved run remains resumable; remote job may continue | Wizard close restores owner; closing while running asks to stop observation |
| Provisioning resume | Select incomplete profile run and “Продолжить настройку” | Resume by persisted run ID; use saved admin identity first | Same selected profile | Safe stage/status and refreshed profile | Clear error and retry; never silently starts a second run | Invoking control retains focus |
| Panel credentials | Open “Данные панели…” on completed run | Local encrypted record load | Owned credentials window | Masked fields with explicit reveal/copy | Local storage error in status; no secret in ordinary status | Owned window returns focus to owner |
| Import/export | File picker | Adapter operation in footer | Registry selection | Count/export completion | Status error | Owner restored after OS dialog |

## Navigation and responsive behavior

One main window, no route changes. Minimum 900×620. DataGrid and editor each own their scroll behavior. All operational actions are available to keyboard users through native controls. Dialogs are centered on and owned by the main window.

## Overlays and feedback

- Dialog primitive: application-owned WPF window for trust, confirmation, and diagnostics.
- Destructive confirmation: name object and local consequence; least destructive button focused first.
- Toast equivalent: persistent status line until next operation.
- Unsaved changes: editor save is explicit; closing editor discards uncommitted local edits.

## Async and resilience

- Mutations: pessimistic; local persistence completes before UI success.
- Duplicate submit: avoid via UI single-thread interaction; no background retry.
- Offline: local registry works offline. Network results only follow explicit actions.
- Timeout: adapter-owned bounded checks.
- Stale UI: cancellation tied to window lifetime; profile selection is rechecked when presenting the operation result.
- Shutdown: stop only tunnels owned by this instance, then dispose adapters.

## Validation

- Schema: `Core.ProfileValidator.Validate`.
- Trigger: save click; preserve values and display every correction message.
- Secret handling: no credential fields; private key path is local metadata; base path is masked and DPAPI-protected by storage; exports omit base path and identity path.
- Provisioning secrets: initial root password/key passphrase and existing admin-key passphrase remain transient in the wizard and are cleared before remote work. Panel credentials are read only on explicit request and shown in the dedicated masked window.
- Public `.pub` files selected for initial or administrative authentication are rejected inline with a private-file correction hint before provisioning can start. The initial provider key and future administrative identity are distinct roles.
- Optional allowed IPs are entered as plain IPv4/IPv6 addresses, one per line, with no commas, CIDR suffixes, hostnames or `any`. The shared Core helper normalizes exact hosts to /32 or /128 for the remote firewall. Errors name the row without echoing its contents; preserve the list and focus it on invalid submit. At most 100 nonblank entries and 16000 input characters are accepted. The field is enabled only for public-panel mode; tunnel-only mode neither validates nor forwards it. This creation-time setting does not reconfigure existing VPS profiles.

## VPS provisioning progress

- Show named provisioning stages and their pending, running, complete, or attention-needed state. Do not infer a percentage from elapsed time or the current remote step.
- Show elapsed time for the current observed operation, the time of the last successful VPS response, and the next scheduled status/DNS check when one is known. A missing remote status is explicitly unknown; continue observing the same saved run ID and never launch a replacement job automatically.
- Show DNS checks per validated hostname with its expected record/address and observed result. Do not present local progress events as VPS responses, and do not echo arbitrary remote/API messages into the progress UI or saved journal.
- Resume screens distinguish persisted journal snapshots from live observations. Stopping observation does not claim that remote work stopped; failed or ambiguous runs require an explicit operator check before any retry.

## Verification

- Static audit: `audit_project.py` strict; native WPF limitations and resulting findings documented.
- Build: bundled .NET 10 SDK.
- Smoke: `--data-dir <temp>` and `--smoke-test <png>` exercise temp-only registry save/load, fake provisioning status/credentials and native rendering without network/hosts/VPS changes.
- Runtime matrix: Windows UI Automation must exercise empty, no-results, create/edit/delete, SSH trust, tunnel, hosts confirmation, keyboard navigation, and async error states.

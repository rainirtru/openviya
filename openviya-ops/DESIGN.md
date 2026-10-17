---
version: alpha
name: OpenViya Ops
description: Native Windows operations notebook for local VPS, SSH, tunnel, panel, and DNS workflows.
colors:
  primary: "#73A9D1"
  background: "#101820"
  surface: "#17232D"
  raised: "#1E2D38"
  border: "#344653"
  text: "#E8EEF0"
  muted: "#9AAEB8"
  teal: "#70B8AD"
  warning: "#D8B46B"
  danger: "#D98478"
typography:
  sans:
    fontFamily: "Segoe UI Variable, Segoe UI, sans-serif"
  mono:
    fontFamily: "Cascadia Mono, Consolas, monospace"
rounded:
  DEFAULT: "0.25rem"
  sm: "0.125rem"
  md: "0.25rem"
  lg: "0.375rem"
spacing:
  rail-width: "218px"
  page-gutter: "26px"
  control-height: "38px"
components:
  button:
    minHeight: "38px"
  card:
    background: "#17232D"
    border: "#344653"
  dialog:
    width: "520px"
---

# OpenViya Ops Design System

> Internal Windows 11 operations tool for one infrastructure operator. The visual identity is derived from the brief and the existing OpenViya infrastructure plan; runtime WPF resources are canonical.

## Overview

### Creative North Star

A field engineer’s dark blue instrument notebook: calm, legible, dense enough to keep endpoint facts and operator actions in one view. The memorable signature is the narrow teal register marker and mono endpoint line that make the current infrastructure record feel like an inspected instrument entry.

### Product context and register

- **Audience and primary job:** An infrastructure owner managing existing VPS profiles, SSH connections, tunnels, panel access, and opt-in DNS provisioning.
- **Target market(s) and evidence:** Windows 11 operator environment, from `ops/LOCAL-OPS-APP-ARCHITECTURE.md`.
- **Locale(s) and language policy:** Russian UI and content; technical identifiers, hostnames, enum codes, SSH fingerprints, and paths preserve their canonical forms. Date and time use the Windows user locale.
- **Usage scene:** Native desktop at 900×620 minimum, often alongside terminal and browser during operational checks. Dense endpoint data must stay visible; longer forms scroll inside their panel.
- **Register:** Product / admin.
- **Memorable signature:** Teal operational register marker with a mono endpoint line for the selected server.
- **Restraint:** Keep confirmations, warnings, and action labels explicit. Avoid decorative motion and marketing language.
- **Anti-references:** Neon hacker dashboards, promotional gradients, web-admin tables transplanted into a window, and hidden/ambiguous operation state.
- **Token ownership/runtime mapping:** This file owns visual intent and normative palette. `src/OpenViya.Ops.Desktop/Theme.xaml` is the canonical runtime adapter, mapping named WPF brushes and colors to controls. There is no generated token pipeline; compare this file to Theme.xaml during review.

## Colors

Palette: canvas `#101820`, panel `#17232D`, raised control `#1E2D38`, divider `#344653`, warm text `#E8EEF0`, subdued text `#9AAEB8`, operational blue `#73A9D1`, completion teal `#70B8AD`, warning brass `#D8B46B`, and danger `#D98478`. Blue marks navigation and focus, teal marks the active operational register, brass carries caution copy, and muted red marks destructive actions. Text labels accompany every status color. Focus must remain visible against raised surfaces.

## Typography

Use Segoe UI Variable with Segoe UI fallback for Russian prose and controls. Use Cascadia Mono with Consolas fallback for IP addresses, SSH endpoints, and selected endpoint data. Headings use semibold weight with restrained scale; uppercase is reserved for small section labels. Avoid truncating hostnames when a detail line can wrap.

## Layout

A fixed 218-pixel left rail identifies the local registry; the work area places page title and add action above search, a locally paged table, and selected-server detail/actions. The window is at least 900×620. At constrained heights, the form scrolls internally while the table remains an independent bounded work surface. The form uses three columns where width permits and keeps field error text visible before save controls.

## Elevation & Depth

Hierarchy comes from tonal surfaces and thin blue-gray borders. Use no blur and no shadows for static cards. The confirmation window is owned by the app, centered on its owner, and does not move surrounding layout.

DNS is presented as an explicit operator action with a per-name result list. Automatic mode labels save as a provider operation and reports a missing token without losing the profile. Manual mode keeps DNS changes on the provider website and never invokes the write API. The Timeweb settings window masks token input, describes local DPAPI protection in Local mode or encrypted server storage in LAN mode, and labels zone verification as read-only. A server-held token is represented only by configured/not-configured state; its value is never returned to the client.

Fresh VPS setup is a separate owned wizard in the same operational notebook. Keep host identity review, first-login method, DNS mode, ACME email, panel exposure, and the three required confirmations visible as a deliberate sequence. Its progress and recovery status stay in a fixed area while the form scrolls. The main profile card exposes resumable run status and a separate credentials window; credentials stay masked until explicitly revealed. In LAN mode, closing the wizard stops polling while the accepted server job continues, and the selected profile card can show the latest server-owned status.

Provisioning progress uses `ProvisionProgressLog`, a shared native read-only TextBox with local timestamps, wrapping, selection and bounded history. The wizard keeps the current remote step and expandable journal outside the form scroller; errors retain their full text with scroll access. The main profile card opens the same journal in an owned dark window. Preserve the existing Ink/Muted/Surface palette and native keyboard copy behavior. Never render raw SSH, systemd journal or credential output as a progress event.

The Local/LAN storage selector lives in connection settings, not in an implicit network fallback. LAN configuration shows HTTPS URL, masked bearer token with explicit reveal, and SHA-256 certificate pin. Test status applies only to the current tuple; edits invalidate it. Migration uses separate opt-in choices with a concrete destination and category review. Local profile paths, SSH tunnels and hosts edits remain device-specific. In remote maintenance, a journal action opens a read-only summary of recent durable server jobs using localized state labels and timestamps; arbitrary server messages are omitted.

## Shapes

Prefer square-edged notebook panels and modestly padded native controls. Keep standard Windows focus and keyboard affordances. Thin divider strokes replace decorative separators. Do not rely on rounded status pills.

## Components

### Foundational visual states

Default controls use raised surfaces and divider borders. Hover uses a brighter border/surface. Focus uses the operational blue border and system focus behavior. Disabled controls are dimmed but remain readable. Busy operations keep their button geometry and show a concise pending status in the footer. Empty, no-results, unavailable-registry, success, warning, and error states use text.

### Buttons and actions

Primary save/add actions use blue; opening a panel uses teal; destructive profile and hosts actions use danger or brass wording and an app-owned confirmation. Every action is text-labeled. Tunnel start and stop reflect the current known snapshot.

The selected-server details lead with the intended panel opening mode, the owned tunnel snapshot, and last saved SSH/HTTPS observations. Keep desired mode and observed results as separate labeled lines; include the observation timestamp and call persisted outcomes “last saved” so they are not mistaken for live checks. Put panel opening, SSH authentication, and diagnostics in the first action tab. Place host edits, trust scans, TLS probes, and DNS operations behind their named tabs.

### Navigation and data display

The left rail has one local registry destination. The table has one selected profile, native keyboard sorting, local search, and pages of 20. Technical columns use Cascadia Mono. Selected details expose the endpoint and actions.

### Forms and overlays

WPF labels and input controls are native and keyboard operable. Validation is in the form and focuses the first correction surface where possible; no native validation bubble is used. Base path is masked with PasswordBox. Native Open/Save dialogs are allowed only for key/profile file selection. Trust, confirmation, and diagnostic results use app-owned windows.

The fresh-VPS wizard uses a native 90-pixel multiline field for optional public-panel allowed IPs, with standard selection, Enter for a new line, Tab for navigation, and visible native scrollbars. Keep its label, format hint and per-line validation adjacent. It is enabled only for tunnel plus public access; an empty field explicitly means public access without an IP restriction.

### Iconography

No icon-only actions in the MVP. Status marks are supplementary to localized text and status messages.

### Motion

No decorative motion. Async operations update a stable footer line. Dialogs open centered on their owner. Reduce movement to zero.

### Content and data visualization

Use concise Russian action verbs: «Сохранить профиль», «Проверить SSH», «Остановить», «Применить hosts». Never overstate what a probe proves. Present endpoint, observed result, and next recovery step together. Do not display raw exception dumps or private key material.

## Do's and Don'ts

- **Do:** Keep the endpoint line and operation result readable at a glance.
- **Do:** Name consequences in destructive and trust confirmations.
- **Don't:** Suggest that a key scan authenticates server identity; require independent fingerprint verification.
- **Don't:** open a panel before tunnel TLS and the exact expected hosts record both pass.

## Installed-server maintenance

Maintenance is a separate, explicitly opened operator work surface. It uses the existing blue-gray instrument notebook palette, a mono endpoint line, labeled state text, and a stable footer status. At 900×680 the form scrolls inside the window; field labels, controls, and footer actions remain reachable with keyboard navigation.

The observed VPS configuration is a read-only snapshot, visually separate from the requested port, opening mode, and source IP draft. A prepared plan lists each concrete remote change before the apply action appears. Confirmations use an app-owned dialog and name public exposure, the five-minute VPS rollback, and provider firewall work that remains manual. A blank source list in public mode is described as access from any source.

Administrative SSH credentials use a masked field for the current action and are cleared when it finishes. The selected profile key path is shown by filename only. Backup archives are sensitive data: the operator chooses the local destination through a Save dialog only after selecting a verified remote archive. This is the maintenance workflow's deliberate exception to the usual profile/key-only file picker rule.

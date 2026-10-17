---
version: alpha
name: OpenViya Web
description: Russian public entry point and account interface for OpenViya internet access.
colors:
  primary: "#193f3d"
  secondary: "#72c8b3"
  accent: "#ed9a6e"
  background: "#eff4f1"
  surface: "#f9fbf9"
  mutedSurface: "#edf2ee"
  text: "#193f3d"
  mutedText: "#607578"
  border: "#c8d4ce"
  success: "#216b51"
  danger: "#a33d33"
  focus: "#df7049"
typography:
  body:
    fontFamily: '"Segoe UI", Arial, sans-serif'
  display:
    fontFamily: 'Georgia, "Times New Roman", serif'
  technical:
    fontFamily: '"Cascadia Code", Consolas, monospace'
rounded:
  DEFAULT: "16px"
  panel: "16px"
spacing:
  publicContentMax: "1120px"
  accountContentMax: "1120px"
  desktopSectionVertical: "112px"
components:
  button:
    height: "50px"
    radius: "40px"
  panel:
    radius: "16px"
    border: "1px solid #d2ded7"
    background: "#f9fbf9"
  focusRing:
    color: "#df7049"
    width: "3px"
---

# OpenViya Web Design System

## Overview

### Creative North Star

The public home borrows from a route-planning instrument: a light gray-green page, a dark green route card, and a warm peach route line. Its diagram illustrates a personal route without claiming a current connection, a location, or an available server. The account view moves that same line language onto quiet paper surfaces so status and account facts remain easy to scan.

### Product context and register

- **Audience and primary job:** Russian-speaking OpenViya visitors learn what the service is and enter the Telegram sign-in flow; signed-in users inspect their profile and server-reported subscription.
- **Target market(s) and evidence:** Russian-language product copy is explicit in this brief and the repository. It does not establish a particular geographic market, legal regime, or supported payment region.
- **Locale(s) and language policy:** Russian (`ru-RU`) throughout the public site and account. Format dates and times with `Intl.DateTimeFormat('ru-RU')`; system fonts keep Cyrillic readable without remote font requests.
- **Usage scene:** Responsive desktop and mobile browser, including direct arrival from Telegram OIDC. The cabinet prioritizes status, identity, expiry, and grant history.
- **Register:** Hybrid. `/` is the public brand surface; `/account/` is the functional product surface.
- **Memorable signature:** One illustrative routed line through OpenViya to the internet, used only on the public hero.
- **Restraint:** The account never shows fabricated offers, prices, payment states, VPN locations, device configurations, or live connection state. Purchase information is limited to the backend's `paidPurchaseConfirmed` fact.
- **Anti-references:** Generic neon hacker motifs and map pins for unverified VPN locations. The service's data model treats nodes and commercial subscription state distinctly.
- **Token ownership/runtime mapping:** This file records the accepted values; `public/assets/site.css` is their runtime owner. The CSS `:root` variables map primary, secondary, accent, paper, surface, muted, border, success, danger, and focus roles to `--ink`, `--mint`, `--copper`, `--paper`, `--paper-bright`, `--muted`, `--line`, `--success`, `--danger`, and `--focus-ring`. Typography maps to `--font-body`, `--font-display`, and the technical code stack. Components consume those variables. When changing a durable value, update this file and the CSS in the same change and compare the documented hex values with `:root`.

## Colors

`primary` (`#193f3d`) grounds the public hero and primary actions. `secondary` (`#72c8b3`) marks the route and positive access state. `accent` (`#ed9a6e`) is reserved for visible focus and small directional detail. `background` (`#eff4f1`) and `surface` (`#f9fbf9`) distinguish the page from account cards. `mutedSurface` (`#edf2ee`) supports the account background. Text and muted text use `#193f3d` and `#607578`; borders use `#c8d4ce`. Success and danger are explicit semantic states. Focus uses `#df7049` with a 3px outline and offset. Forced-colors mode returns scrollbar rendering to the platform and keeps control borders visible.

## Typography

Body UI uses Segoe UI with Arial fallback. Georgia is used only for display headlines and short account headings, giving the brand a human editorial note without making data harder to scan. Telegram IDs and technical identifiers use Cascadia Code/Consolas. Russian copy uses sentence case; short eyebrow labels may use uppercase with tracking. User and grant values wrap instead of being silently truncated.

## Layout

The public hero uses a light two-column layout: the slogan and entry action on the left, rounded dark route illustration on the right. The public content width is 1120px with 24px side padding. A three-part everyday-use strip precedes the capabilities section; a full-width dark principle band and centered closing slogan follow it. At 850px the illustration moves below the copy; at 620px the page becomes a single column. The public content caps at 1240px. The account caps at 1120px with profile and subscription panels side by side, stacking below 620px. The document owns vertical scrolling; panels do not trap it. Buttons and navigation retain stable space while requests resolve. The global scrollbar baseline uses standards declarations and WebKit fallbacks, remains visible, and honors forced-colors.

## Elevation & Depth

Depth comes from the dark hero against warm paper and from thin panel borders. Static cards have no shadow. Avoid blur, glass, and floating overlays.

## Shapes

Use 16px corners for account panels, 24px for the route card, and pill-shaped action buttons. Circles are reserved for avatar/status marks and the route instrument. Dividers are hairline green-gray strokes.

## Components

### Foundational visual states

Buttons have solid brand and light brand variants, visible hover and pressed movement, focus outline, and a distinct disabled state. API loading uses a small reserved inline spinner beside its status text; reduced motion collapses animation. Success is indicated by text plus mint/green color, and errors remain text in a stable inline banner.

### Buttons and actions

Public actions use filled buttons. Secondary page navigation is a text link. Account refresh and logout are compact neutral controls. Pending operations disable the triggering action while preserving its dimensions. No destructive action or payment action exists in this frontend.

### Navigation and data display

The public site uses anchors to sections and the API-hosted account route. The account is one document view, not a fake tabbed workspace. Grant history is an ordered semantic list and renders only backend-provided grants; an empty history is hidden rather than populated with sample data.

### Forms and overlays

There are no user-entered forms, custom selects, dialogs, or overlays in this frontend. Login is a full-page navigation to the backend's Telegram OIDC route.

### Iconography

Small route and action marks are CSS geometry or simple Unicode arrows paired with visible text. They are supplementary and never carry an action name alone.

### Motion

Only brief button hover and the inline loading mark move. Honor `prefers-reduced-motion`; no scroll-reveal or ambient loop is used.

### Content and data visualization

The hero route card is imported from the user's Sites project `appgprj_6ab3b0e2b3888191b955356b8caf6d0e`, source commit `d66ae295204580fa1cdc948f0d8a6f58c90cb189`, `dist/index.html`. Preserve its SVG contour/route/node geometry and the shield caption. Its canonical local markup is `.ov-atlas` in `public/index.html`; the corresponding namespaced CSS lives in `public/assets/site.css`. Local component tokens `--atlas-sea: #183c3b`, `--atlas-coral: #ef946b`, `--atlas-mist: #dcebe4`, and `--atlas-utility` preserve the original component palette and typography without replacing account tokens. Radius is 28px on desktop and 21px on mobile; minimum heights are 525px and 360px respectively. Reduced motion disables the dashed-line animation. This is illustrative artwork, not measured connection status.

The public diagram is conceptual, not a telemetry display. Account data is plain text; it does not infer connection quality, active VPN sessions, server selection, or payment from subscription state.

User-facing language describes OpenViya and internet access without naming VPN directly, including page metadata, diagram accessibility labels, and account messages. The user-selected reference for the next visual and slogan revision is https://openviya.rainirtru.chatgpt.site/; the user supplied screenshots on 17 October 2026, which are the visual evidence for this revision. The approved slogan is «Ваше личное пространство — где угодно». The principle line is «Ваше пространство — с вами. Где бы вы ни были». Light gray-green surfaces, dark green ink, muted green italic display type, peach route accents, rounded cards, and spacious sections follow those screenshots. No unsupported security guarantees are imported.

## Do's and Don'ts

- Keep the public hero route diagram as the one expressive visual device.
- Keep commercial state and live VPN connection state separate in copy.
- Use Russian labels and dates everywhere.
- Do not add prices, plan names, device lists, support promises, uptime claims, or location names without an authoritative product/API source.
- Do not present trial access as an action until its activation worker is enabled and integrated.

# Changelog

**English** | [Português](docs/pt-BR/CHANGELOG.md)

All notable changes to Severino are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- HTTP ports in services: a port marked **HTTP** goes through the proxy and gets HTTPS from the local CA (`https://name` answers for the main HTTP port, and each HTTP port takes TLS on its own number), the route rules, and its requests with details in Requests. The app's Host still reaches the destination. Docker and Kubernetes refreshes keep the marks. In WSL the ports still go straight to the destination.
- Clicking a service's name opens it in the browser, like a route; with several web ports, a menu lists them, and the card's ⋯ menu has **Open in browser**. Database and queue ports are left out.
- Security for routes, in the route editor: open, Severino's password page (a session cookie until the browser closes, 12 hours at most), Basic Auth, or a required header like `X-Api-Key`. Only hashes of the password and of the key are kept. The route's card shows the mode, and folder routes are covered too.

### Changed

- Address, host and port fields take out what comes with a copied address, when it is pasted or when the field is left: `http://` in front, a path, a `:` left after an IP, a port with its colon. A port that came with the host goes to the port field, and `https://` picks HTTPS.

## [0.6.0] - 2026-10-13

### Added

- Request details: clicking a line in Requests opens its headers, sizes, where it went and, with **Capture bodies** on, the first 64 KB of each text body. **Copy as curl** (PowerShell or bash), **Repeat** and **Export HAR**.
- Health notices: routes and services with **Tell me when it stops answering** get a tray notice after 15 seconds without an answer, and another when they are back. Clicking it opens the tab. Settings › General turns them all off.
- Route rules, in the route editor's **Rules**: allow CORS (Severino answers the preflight), set or remove headers both ways, wait before forwarding, and fail on purpose with 429, 500, 502, 503 or 504 in part of the requests. Each rule on shows as a badge on the route's card.
- A folder as a route's target: Severino serves its files itself, with index.html, no cache and, for single-page apps, unknown pages sent to index.html. Path routes, HTTPS and the route rules work with folders too.
- Environments: routes, services and DNS entries can belong to one, like `producao` or `staging`, and the same name can live in several. Only the active environment counts (with the items of none); the status bar and the tray switch it, and hosts, proxy, services and WSL follow at once. `Get-SeverinoEnvironment` and `Set-SeverinoEnvironment` in PowerShell.
- Project files: a `severino.json` in a repository declares a project's routes, services and DNS entries. Drop it on the window, open it in Settings › Projects or run `Import-SeverinoProject`; a preview shows what comes in, changes and leaves before anything is applied, and applying again touches only the project's items. **Save as project…** on a route group, or `Export-SeverinoProject`, writes one.
- The local CA trusted in WSL: Settings › WSL installs it in the chosen distros (update-ca-certificates or update-ca-trust, and NODE_EXTRA_CA_CERTS for Node), follows reissues, and takes it out when turned off, when the CA goes, and with Clean up everything and the uninstaller.

### Changed

- Clicking the new version notice in the tray opens the window.
- Backup files are now version 3; older versions of Severino do not read them.

### Fixed

- The editors (route, service, DNS entry) and the first-run wizard sometimes opened blank, with only the frame and the text cursor.

## [0.5.1] - 2026-10-11

### Fixed

- "Exit" in the tray left the menu frozen on screen and the process running.
- The DNS tab has less text: shorter section hints, a compact empty state, the reload by its section title and no repeated "outside Severino" badge on each line.
- The scroll bar of the lists no longer covers the cards' buttons.

## [0.5.0] - 2026-10-10

First published version.

### Added

- Routes: a domain, like `meuapp.sev`, leads to a local app through a reverse proxy that only listens on `127.0.0.1` and `::1`, with the name written in a block of its own in the hosts file.
- Path routes: `meuapp.sev/api` goes to a different target than the rest of the domain, optionally stripping the path before forwarding.
- Wildcard routes: `*.meuapp.sev` answers any name below it, resolved by a DNS server of the helper service on `127.53.0.1` and Windows NRPT rules.
- Route groups, with a switch that turns the whole group on or off and a menu to rename or ungroup it.
- HTTPS from a local ECDSA P-256 certificate authority, with Name Constraints limited to the routes' domains, its key protected by DPAPI and installed for the current user only.
- HTTP to HTTPS redirect with 307, never 301 nor HSTS.
- Services: names that lead straight to a port, over any protocol (Postgres, Redis, gRPC), each on a loopback address of its own.
- Service import from Kubernetes (NodePort, ClusterIP through a managed `kubectl port-forward`, Ingress hosts as routes) and from Docker containers, on Windows or in a WSL distro.
- Imported services are followed: Docker events and a Kubernetes check every 30 seconds fix ports on their own.
- Apps in WSL call the services by name, in the chosen distros.
- DNS tab: entries that lead a name straight to an IP and stay with the app closed. Private IPs go in right away; a public IP or a wildcard asks for a Windows administrator confirmation.
- Hosts lines that are not Severino's show up marked; editing or removing one asks for confirmation and leaves a comment in the hosts file.
- DNS names as targets of routes and services, with the IP in sight and links between the tabs.
- The route form suggests a service when the port belongs to a database or queue, and a DNS entry when the target is a network IP.
- Requests tab, with an in-memory log of what went through the proxy and the services.
- Tray icon with the route list and a pause that hands the domains back to the internet.
- PowerShell module that talks to the running app: `New-SeverinoRoute`, `Set-SeverinoDns`, `Disable-SeverinoRoute -Group` and more.
- Backup of routes, services and DNS entries to a JSON file.
- First-run wizard that checks port 80, the helper service and the system proxy.
- New version notice: the app asks GitHub for the latest release at start and once a day, and tells you at the top of the window and in the tray. **Update now** downloads the installer, checks the SHA-256 GitHub published, installs silently and opens the app again, keeping routes, services and settings. The check can be turned off in Settings › About.
- English and Portuguese: the app, the installer and the proxy's error pages follow the Windows language, and Settings › General lets you pick.
- "Clean up everything" and the uninstaller remove what Severino put into Windows.
- Installer for Windows 10 and 11, 64-bit.
- Website at <https://lucassm02.github.io/severino/>, in English and Portuguese, with an intro video and social media images.

[Unreleased]: https://github.com/lucassm02/severino/compare/v0.6.0...HEAD
[0.6.0]: https://github.com/lucassm02/severino/compare/v0.5.1...v0.6.0
[0.5.1]: https://github.com/lucassm02/severino/compare/v0.5.0...v0.5.1
[0.5.0]: https://github.com/lucassm02/severino/releases/tag/v0.5.0

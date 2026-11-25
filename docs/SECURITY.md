# Security model and audit

## Security model

ThisIsATrap is an **internal development tool**:

- **No sign-in.** The web UI and API have no authentication, on purpose. Anyone who can reach the HTTP port can read, change and delete trapped mail.
- **SMTP accepts anyone.** Any client can send mail, with any credentials or none.
- **Run it on a trusted network.** Never expose the HTTP or SMTP ports to the internet. The deploy script's firewall rules only open them on the Domain and Private network profiles.

What it does protect against:

| Threat | Protection |
| --- | --- |
| Mail being delivered | No relay code. `NeverSendTests` fails the build if the app references an SMTP client. |
| Script in trapped HTML mail | Rendered in a sandboxed iframe whose CSP blocks scripts. |
| Script in attachments | Served with a strict CSP and `X-Content-Type-Options: nosniff`. |
| XSS through mail content (subject, addresses, inbox names, links, analysis output) | Everything is escaped before it's added to the page. Link `href`s are limited to `http(s)`. |
| Path traversal | Message files are addressed only by 32-character hex IDs. |
| SQL injection | All queries use parameters. |
| SSRF from the link checker and webhooks | Every connection is checked against the IPs DNS returned, including redirects. Cloud metadata addresses are always refused. Private networks are refused for link checks (unless `LinkCheck:AllowPrivateNetworks`), but allowed for webhooks. |
| Injection into SMTP replies from error-simulation rules | Rule messages have CR/LF stripped and are length-limited. The reply code is limited to 200–599. Enhanced codes must match a strict pattern. |
| Slow error-simulation patterns | Matched with a linear-time regex engine with a 100 ms timeout. |
| Webhook secrets leaking | Write-only: the API only says whether a secret is set. |
| TLS private key leaking | Only the public certificate can be downloaded. The generated PFX lives in the data folder, which isn't served. |

## Audit: 2026-11-25 (branch `feature/mailtrap-parity`)

This was an automated security review of every change since the first release, plus a manual trace of untrusted data (message content, headers, the SMTP username, the `X-Trap-Inbox` header, API request bodies) to every place it's used.

**Result: no high-confidence vulnerabilities.**

### Accepted risks / follow-ups

| Item | Notes |
| --- | --- |
| No authentication | This is the intended design for an internal tool (see above). |
| Cross-site requests from a web page someone visits | Endpoints with JSON bodies need a CORS preflight, so a cross-site page can't call them. But simple `POST`/`DELETE` calls, such as delete-all or mark-read, can still be triggered cross-site. **Planned:** reject state-changing requests whose `Origin` or `Sec-Fetch-Site` header shows they came from another site. |
| DNS rebinding against the UI (`AllowedHosts: "*"`) | Set `AllowedHosts` to the real host name(s) in production to close this. |
| IPv6 transition addresses (NAT64 `64:ff9b::/96`, 6to4 `2002::/16`, IPv4-compatible) | These aren't unwrapped before the private-network check. Only relevant on a host behind NAT64 or a 6to4 relay. **Planned:** unwrap them before classifying. |
| Security response headers on the app shell | **Planned:** CSP, `frame-ancestors 'none'`, `Referrer-Policy`, `Permissions-Policy`. |

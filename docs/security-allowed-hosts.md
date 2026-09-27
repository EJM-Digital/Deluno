# Security: Allowed Hosts (#939)

## What it is

Without Allowed Hosts, Deluno answers a request no matter which hostname or
IP address it was sent to. That is how a page open in your own browser can
be tricked into talking to it (a "DNS-rebinding" attack): the page's script
resolves to your machine's loopback address, and Deluno answers because it
never checked what it was asked for.

System &rarr; Security &rarr; Allowed Hosts lets you tick which addresses
Deluno should answer on: the one you're using right now, this computer's
name, its LAN IPs, `localhost`, and any reverse-proxy or Docker host Deluno
has actually seen in a request. You can also add a custom address (a
reverse-proxy domain) and wildcard subdomains (`*.example.com`). There is
never a raw comma-separated text box.

## Nothing changes by itself (#936)

- A brand-new install is asked once, in the setup guide: "Only answer on the
  addresses you use?" Answering No (or leaving the guide before that step)
  behaves exactly as every earlier release did.
- An existing install is never switched on by an update. It keeps answering
  on any address until someone opens Security and turns it on.
- While it is off, System &rarr; Health shows one plain item with a one-click
  Fix that opens the Security page.

## You can't lock yourself out

- Saving is refused, with a plain explanation, if the address you are using
  right now is not on the list you are about to save.
- `localhost`, `127.0.0.1` and `::1` always answer on the machine Deluno runs
  on, whatever is saved or left unticked.
- A refused request gets a plain-English HTML page explaining why, not a
  blank 400.

## Recovery path that needs no working web UI

If every address you can reach Deluno on somehow ends up refused, two
routes get you back in without a browser session:

1. **Config file or environment variable.** Set the environment variable
   `DELUNO_ALLOWED_HOSTS_DISABLE=true` (or `Security__AllowedHosts__ForceDisabled=true`
   in Docker/Compose, or `Security:AllowedHosts:ForceDisabled: true` in
   `appsettings.json` next to the Deluno executable) and restart Deluno.
   Allowed Hosts stops being enforced immediately -- Deluno answers on any
   address again, exactly as it did before this feature -- and nothing
   already saved in the database is touched. Remove the override and
   restart again once you've fixed the list from the Security page, and
   your saved choices come back exactly as they were.
2. **The tray, on Windows.** The tray needs no web page to reach: after
   setting the override above, use the tray icon's **Restart** menu item to
   apply it. The tray keeps working even while the web UI itself is
   refusing every address you can reach it on.

## Enforcement

A single middleware (`Deluno.Security.Hardening.DelunoAllowedHostsMiddleware`)
runs ahead of routing in both `Deluno.Host/Program.cs` and the Windows tray's
embedded server (`apps/windows-tray/DelunoServer.cs`), so it covers the web
UI, the JSON API, SignalR/websocket upgrades, and the external integration
API (`docs/external-integration-api.md`) alike. `X-Forwarded-Host` is honoured
via `ForwardedHeadersMiddleware`, and a refused host is logged once (not once
per request) until Deluno restarts or that host is refused again after a
gap.

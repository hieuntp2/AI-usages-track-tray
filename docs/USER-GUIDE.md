# User Guide

## The tray icon

AI Usage Tray runs entirely from the Windows notification area — there's no taskbar button and no
window on startup.

- **Left-click** or **double-click**: open the flyout above the tray.
- **Right-click**: context menu — Open, Refresh now, one status row per added AI service, **+ Add AI
  service...**, Settings, Start with Windows, About, Exit.
- **Tooltip**: hover to see a one-line summary of the highest usage per provider, e.g.
  `AI Usage — Codex 72%, Claude 48%`.
- The icon can optionally change appearance once any provider's usage crosses a configurable
  threshold (Settings → General → icon alert).
- Clicking anywhere outside the flyout closes it.

### Provider status rows in the right-click menu

Between "Refresh now" and "Settings", the menu lists every AI service you've enabled, each prefixed
with its live status:

```
✓ OpenAI Codex — Connected
✗ Claude Code — Not authenticated: Claude Code is not signed in.
⚠ GitHub Copilot — Setup required
+ Add AI service...
```

- **✓** connected and reporting data normally.
- **⚠** needs attention but isn't necessarily broken (setup required, stale data, unsupported version).
- **✗** an authentication or hard error - the message after the em dash is the actual error, and it's
  also shown as the row's tooltip.
- Clicking any row (or **+ Add AI service...**) opens **Settings → Providers**, where you can fix the
  issue or enable a new service.

## Adding and authenticating a provider

- **Codex** and **Claude Code** don't need credentials from this app - just enable them and make sure
  you're signed in to their own CLI/app (see the README's Codex/Claude setup sections). Enabling
  Claude also requires running its one-time bridge Setup.
- **GitHub Copilot** is the one provider that needs a credential entered directly here: in
  **Settings → Providers → GitHub Copilot**, enter your GitHub username and a fine-grained personal
  access token (with `Plan: read` permission), then click **Add & Authenticate**. The app immediately
  validates the token against GitHub before enabling the provider - if GitHub rejects it, the row shows
  exactly why (e.g. "GitHub rejected the token (HTTP 401)...") instead of silently failing.

## The flyout

- **Header**: "AI Usage", last global refresh time, a refresh button.
- **One card per enabled provider**, each showing:
  - Provider name, plan (e.g. "OpenAI Codex — Plus"), and a connection-status dot.
  - Account label, when available.
  - One row per quota window: used %, remaining %, and reset time (relative/exact/both, per your
    Settings → General → Time display choice).
  - Provider-specific metrics (e.g. Codex lifetime tokens, Claude session cost) below the windows.
  - A stale-data or setup warning message when relevant.
  - "Last updated N minutes ago".
- **Settings** button at the bottom.

Percentages are never combined across providers — Codex's 72% and Claude's 48% are unrelated numbers
and are always shown as separate cards, never averaged or summed.

## Provider states

| State | Meaning |
|---|---|
| Available | Connected and showing current data |
| Refreshing | A refresh is in progress |
| Not installed | The provider's CLI isn't found on `PATH` |
| Not authenticated | The CLI is installed but not signed in |
| Setup required | Integration needs a one-time setup step (Claude) |
| Stale | Data was read successfully before, but is older than expected |
| Error | The last refresh failed; the previous good data (if any) is still shown |
| Unsupported version | The installed CLI version doesn't expose the needed data |

## Settings

### General

- **Start with Windows** — adds/removes a per-user `HKCU` Run entry (no admin rights needed).
- **Start minimized** — if unchecked, the flyout opens automatically on launch.
- **Theme** — System / Light / Dark. System follows the Windows 10/11 light/dark setting live.
- **Refresh interval** — how often actively-polled providers (Codex) refresh in the background.
- **Notification thresholds** — percentages (default 70/90/100) at which a Windows notification fires,
  per provider. Each threshold fires at most once per quota window — it resets automatically once the
  window's reset time passes and a fresh value is observed.
- **Time display** — relative ("in 2h 14m"), exact ("today at 09:30"), or both.
- **Icon alert threshold** — the tray icon changes appearance once any enabled provider's highest
  window crosses this percentage.

### Providers

Per provider: enable/disable, detected executable path and version, data source, and (for Claude)
Setup / Repair integration / Remove integration buttons.

### Diagnostics

App version, Windows version, per-provider version/status, last successful refresh, last error, a
button to open the log folder, and **Export sanitized diagnostics** — a JSON file with all secrets,
tokens, prompt content, and (where avoidable) your full user profile path stripped out, suitable for
sharing when reporting an issue.

## Notifications

Example: *"Codex weekly usage reached 90%. It resets Tuesday at 09:30."*

- Configurable per provider in Settings.
- Fires once per threshold per quota window — not on every background refresh.
- Claude notifications only fire when a genuinely new cached snapshot arrives (i.e. after you've used
  Claude Code), never from re-reading the same cache file repeatedly.

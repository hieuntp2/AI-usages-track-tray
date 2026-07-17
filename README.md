# AI Usage Tray

A lightweight Windows system-tray application that monitors coding-AI usage and quota — starting
with **OpenAI Codex** and **Claude Code** — without ever consuming your quota, reading your
credentials, or sending anything off your machine.

> Screenshot: a compact flyout above the system tray showing one card per enabled provider
> (see [docs/USER-GUIDE.md](docs/USER-GUIDE.md) for a full description of the layout).

## Supported providers

| Provider | Status | Integration |
|---|---|---|
| OpenAI Codex | Supported | `codex app-server` JSON-RPC (read-only) |
| Claude Code | Supported | Status-line bridge (event-driven, no polling) |
| GitHub Copilot | Beta, disabled by default | GitHub REST billing API |
| Gemini CLI, Cursor, Windsurf/Devin | Not yet supported | See [docs/PROVIDER-INTEGRATION.md](docs/PROVIDER-INTEGRATION.md) |

## Installation

### One-step install (recommended)

Requires Windows 10/11 and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
From a clone of this repository, run:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install.ps1
```

The script (no admin rights needed — everything is per-user):

1. builds and publishes a Release build,
2. stops any running AiUsageTray instance,
3. installs the app to `%LOCALAPPDATA%\Programs\AiUsageTray`,
4. registers a Scheduled Task named `AiUsageTray` that starts the app automatically at Windows
   logon (and removes the older Run-key startup entry so the app isn't started twice),
5. starts the app immediately.

Re-running the script upgrades an existing install in place. Options:

| Option | Effect |
|---|---|
| `-SelfContained` | Bundle the .NET runtime (no Desktop Runtime needed afterwards; larger install) |
| `-InstallDir <path>` | Install somewhere other than `%LOCALAPPDATA%\Programs\AiUsageTray` |
| `-NoScheduledTask` | Install files only; skip auto-start registration |
| `-NoStart` | Don't launch the app after installing |

To uninstall (keeps settings/logs unless you add `-PurgeData`):

```powershell
powershell -ExecutionPolicy Bypass -File scripts\uninstall.ps1
```

### Manual install

Requires the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
(unless you use the self-contained publish below).

1. Download or build the app (see **Publishing**).
2. Run `AiUsageTray.exe`. It starts minimized in the system tray — no window, no taskbar button.
3. Left-click the tray icon to open the usage flyout. Right-click for the menu (Open, Refresh now,
   Settings, Start with Windows, About, Exit).

## Running locally

```powershell
dotnet restore
dotnet build -c Release
dotnet run --project src/AiUsageTray/AiUsageTray.csproj
```

## Publishing

Framework-dependent (requires the .NET 8 Desktop Runtime on the target machine):

```powershell
dotnet publish src/AiUsageTray/AiUsageTray.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false
```

Self-contained (no runtime install needed, larger output):

```powershell
dotnet publish src/AiUsageTray/AiUsageTray.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true
```

The published folder (`src/AiUsageTray/bin/Release/net8.0-windows/win-x64/publish/`) is a portable
app — copy it anywhere and run `AiUsageTray.exe`. No installer is required for this milestone.

## Codex setup

Nothing to configure. On startup the app locates `codex` on your `PATH` (via `where.exe`), reads its
version, and starts `codex app-server` as a hidden background process. Quota is read through the
read-only `account/read` and `account/rateLimits/read` JSON-RPC calls — **no Codex prompt or agent
turn is ever started**, so refreshing usage never touches your quota. If Codex isn't installed or is
too old to expose these endpoints, the provider card explains that directly ("Codex CLI not
installed." / "Update Codex CLI to enable usage monitoring.").

## Claude bridge setup

Claude Code doesn't expose a usage API — the only source of rate-limit data is the JSON Claude Code
sends to your configured **status line** command. This app installs a small bridge that sits between
Claude Code and your existing status line:

1. Open **Settings → Providers → Claude Code → Setup**.
2. The app writes a PowerShell script to `%LOCALAPPDATA%\AiUsageTray\bridge\claude-statusline-bridge.ps1`
   and points Claude Code's `statusLine.command` at it, **preserving your existing status line** — the
   bridge forwards the same input to your original command and relays its output back, so your status
   line looks exactly the same as before.
3. Open Claude Code and send one normal prompt. That first response is what populates usage data —
   **the app never launches Claude Code or sends a prompt itself**, since that would spend your quota.
4. Reopen the flyout — Claude's card now shows your 5-hour and weekly usage.

Setup only ever edits the `statusLine` property of `%USERPROFILE%\.claude\settings.json`; every other
property (and a timestamped backup of the whole file) is preserved. Setup is safe to re-run — it's
idempotent and won't wrap the bridge around itself.

### Removing Claude integration

**Settings → Providers → Claude Code → Remove integration** restores your original `statusLine`
configuration exactly (or removes the property entirely if you had none), from the same metadata the
installer recorded. **Repair integration** re-applies the bridge if something else has overwritten
your status line.

## Troubleshooting

- **"Codex CLI not installed"** — install the Codex CLI so `codex` resolves on `PATH`.
- **"Update Codex CLI to enable usage monitoring"** — your Codex CLI version doesn't support
  `app-server` quota endpoints; update it.
- **"Integration not configured" (Claude)** — run Setup in Settings.
- **"Waiting for first Claude response"** — the bridge is installed but hasn't seen a status-line
  update yet; send one prompt in Claude Code.
- **"Cached data stale" / "Limit may have reset"** — the cached snapshot is old, or its reset time has
  already passed; send another prompt in Claude Code to refresh it.
- **"Integration damaged"** — another tool (or a manual edit) overwrote the `statusLine` entry; use
  **Repair integration**.
- Logs live at `%LOCALAPPDATA%\AiUsageTray\logs\` (7-day rolling retention, secrets redacted).
- **Settings → Diagnostics → Export sanitized diagnostics** produces a shareable file with tokens,
  credentials, prompt content, and full user paths stripped out.

## Privacy and security

- Never reads Codex or Claude OAuth tokens or credentials.
- Never starts a Codex turn or a Claude prompt to "refresh" usage — Codex reads are read-only
  JSON-RPC calls; Claude data is purely event-driven from your own normal usage.
- Never reads prompt content or conversation transcripts.
- The GitHub Copilot token (when configured) is encrypted at rest via Windows DPAPI, never stored as
  plaintext JSON.
- No telemetry, no analytics, no network calls except the ones you explicitly configure (GitHub
  Copilot's billing API, if enabled).
- All state lives under `%LOCALAPPDATA%\AiUsageTray\` on your machine only.

## Documentation

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — components, data flow, lifecycle, security boundaries.
- [docs/PROVIDER-INTEGRATION.md](docs/PROVIDER-INTEGRATION.md) — per-provider integration contracts
  and research notes on Gemini CLI / Cursor / Windsurf.
- [docs/USER-GUIDE.md](docs/USER-GUIDE.md) — day-to-day usage, settings reference, notifications.
- [docs/IMPLEMENTATION-REPORT.md](docs/IMPLEMENTATION-REPORT.md) — what was built, tested, and what's left.

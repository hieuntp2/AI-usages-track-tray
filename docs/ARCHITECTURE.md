# Architecture

## Overview

AI Usage Tray is a single WPF application (`src/AiUsageTray`) built around one abstraction:
`IUsageProvider`. Every AI coding tool the app monitors implements that interface and produces a
provider-agnostic `UsageSnapshot`. Nothing above the provider layer — orchestration, notifications,
the flyout UI — knows anything about Codex, Claude, or GitHub specifically; it only knows the
normalized model. Adding a new provider (Gemini CLI, Cursor, an API-key service) means writing a new
class under `Providers/<Name>/` and registering it in `App.xaml.cs` — no other layer changes.

```
src/AiUsageTray/
  Models/          Normalized data model + IUsageProvider contract + settings model
  Infrastructure/   Cross-cutting: paths, atomic file I/O, logging, single instance, DPAPI, CLI locator
  Services/        Orchestration, settings persistence, notifications, diagnostics, tray icon
  Providers/
    Codex/          JSON-RPC client + process supervisor + Codex-specific parsing
    Claude/         Status-line cache reader + settings/bridge installer + Claude-specific parsing
    GitHubCopilot/  REST billing client scaffold (beta, disabled by default)
  ViewModels/       MVVM view models (CommunityToolkit.Mvvm)
  Views/            Flyout, Settings, About windows (XAML)
```

## Normalized usage model

`Models/UsageModels.cs` defines the shapes every provider maps into:

- **`UsageWindow`** — one quota window ("5-hour limit", "Weekly limit", "Monthly credits", ...).
  Carries `UsedPercent`/`RemainingPercent` *and* raw `UsedValue`/`LimitValue`/`Unit`, so a
  percentage-based provider (Codex, Claude) and a value-based provider (GitHub Copilot credits) both
  fit without forcing a meaningless conversion between them.
- **`UsageMetric`** — a scalar that doesn't fit the window shape (lifetime tokens, session cost,
  streak counters).
- **`UsageSnapshot`** — the full picture for one provider at one point in time: identity, account/plan
  label, `ProviderConnectionStatus`, a list of windows, a list of metrics, and a free-text message for
  anything that needs explaining (stale data, setup required, etc.).

Two rules are enforced everywhere percentages are parsed:

1. **Missing is not zero.** A field the provider didn't report becomes `null`, never `0`. The UI shows
   "Usage unknown" rather than a false "0% used".
2. **Malformed is clamped for display, not silently trusted.** `UsageWindow.ClampPercent` clamps into
   `[0, 100]` for anything on screen; the original value is what got logged before clamping.

The flyout never computes a cross-provider aggregate percentage — each provider's card is fully
self-contained, because "Codex 72% + Claude 48%" has no meaningful combined value.

## Provider abstraction (`IUsageProvider`)

```csharp
public interface IUsageProvider
{
    string Id { get; }
    string DisplayName { get; }
    ProviderCapabilities Capabilities { get; }
    Task<ProviderDetectionResult> DetectAsync(CancellationToken ct);
    Task<ProviderSetupResult> SetupAsync(CancellationToken ct);
    Task<UsageSnapshot> RefreshAsync(CancellationToken ct);
}
```

`ProviderCapabilities` (in `Models/UsageModels.cs`) declares what a provider can do — supports active
refresh vs. event-driven only, percentage windows, monetary cost, request counts, token counts,
whether setup is required, whether it needs network access. The settings UI and orchestrator use this
instead of special-casing providers by name.

## Data flow

```
                       ┌─────────────────────┐
   codex app-server ──►│ CodexUsageProvider   │──┐
   (JSON-RPC, stdio)   └─────────────────────┘  │
                                                  │
   claude-latest.json ─►│ ClaudeUsageProvider   │──┼──► ProviderOrchestrator ──► FlyoutViewModel ──► FlyoutWindow
   (bridge cache file)  └─────────────────────┘  │         │        │
                                                  │         │        └──► TrayIconService (tooltip, icon alert)
   GitHub billing API ─►│ GitHubCopilotProvider │──┘         └──► NotificationService ──► Windows balloon
                        └─────────────────────┘
```

`ProviderOrchestrator` (`Services/ProviderOrchestrator.cs`) holds the registered providers, runs
`DetectAsync`/`RefreshAsync` concurrently (bounded to 4 at a time), and keeps the **last known-good
snapshot** per provider — a failing refresh degrades that provider's card to a `Stale`/`Error` state
without touching any other provider's data or blocking the orchestrator itself. Every provider call is
wrapped in a try/catch that logs and records `LastError` rather than propagating.

`NotificationService` evaluates each provider/window independently and emits only two edge-triggered
events: usage reaching 100%, or a genuinely observed usage reset. Delivery state is stored in
`%LOCALAPPDATA%\AiUsageTray\config\notification-state.json` by `NotificationStateStore` using
`AtomicFile`. The delivered marker is written before the Windows notification is requested, so later
refreshes and application restarts cannot replay the same event. If persistence fails, the event stays
pending and silent until a later write succeeds; notification failure never blocks provider refreshes.
The state file contains only stable provider/window IDs, reset timestamps, last percentages, and
delivery flags — never credentials, provider payloads, prompts, or transcripts.

## Refresh lifecycle

- **Codex** — actively polled: on flyout open if data is older than 60 seconds, every 5 minutes in the
  background, plus a live update whenever the app-server sends an unsolicited
  `account/rateLimits/updated` notification (merged field-by-field into the last complete state, since
  the notification payload can be sparse).
- **Claude** — purely event-driven. `ClaudeCacheReader` watches
  `%LOCALAPPDATA%\AiUsageTray\data\claude-latest.json` with a `FileSystemWatcher` (plus a slow poll
  fallback in case the watcher misses an event). Manual refresh re-reads that file; it never launches
  Claude Code or sends a prompt.
- **GitHub Copilot** — actively polled via HTTPS when enabled (disabled by default).

## Process lifecycle (Codex)

`CodexProcessSupervisor` owns a single `codex app-server` child process for the whole application
session (not per-refresh): hidden window, redirected stdio, UTF-8. `CodexJsonRpcClient` implements
newline-delimited JSON-RPC 2.0 over those streams — incrementing request IDs, a pending-request
dictionary with per-request timeouts, notification dispatch, and a bounded line reader that drops
oversized/malformed lines instead of taking down the read loop. If the process exits unexpectedly, the
next request triggers a restart with exponential backoff (2s → capped at 2m) and a fresh
`initialize`/`initialized` handshake before any provider call resumes.

## Cache lifecycle (Claude)

The bridge script (`Providers/Claude/Bridge/claude-statusline-bridge.ps1`, embedded as a resource and
written to `%LOCALAPPDATA%\AiUsageTray\bridge\` at setup time) receives Claude Code's status-line JSON
on stdin, wraps it with a local capture timestamp, and writes it via a temp-file-then-atomic-replace
sequence (`AtomicFile.WriteAllText`) so a reader never observes a partial write. It then forwards the
same stdin to the user's original status-line command (recorded in bridge metadata at setup time) and
relays that command's stdout back to Claude Code, so the visible status line is unchanged.

`ClaudeBridgeInstaller` never blindly overwrites `~/.claude/settings.json` — it parses the file as a
`JsonObject` tree (preserving every property it doesn't understand), records the original `statusLine`
block (or its absence) in `%LOCALAPPDATA%\AiUsageTray\config\claude-bridge.json`, and only Setup /
Repair / Remove ever touch the `statusLine` property. A timestamped backup is written before every
modification.

## Process/instance lifecycle (app itself)

- **Single instance**: a named mutex (`SingleInstance.cs`) gates startup; a second launch signals the
  first over a named pipe and exits instead of starting a redundant process.
- **Startup**: `StartupRegistration` writes/removes a per-user `HKCU\...\Run` value — no admin rights,
  no scheduled task, no Windows Service.
- **Shutdown**: `App.OnExit` cancels the shared lifetime `CancellationToken`, disposes the tray icon,
  file watchers, and the orchestrator (which disposes every `IDisposable` provider, tearing down the
  Codex child process and Claude file watcher).

## Security boundaries

- Codex: only calls `account/read`, `account/rateLimits/read`, `account/usage/read` — never a method
  that starts a thread/turn. Never reads `%USERPROFILE%\.codex\auth.json`.
- Claude: never reads Claude's OAuth credentials; never calls an undocumented Claude web API; never
  stores transcript or prompt content — only the status-line metadata fields listed in
  [PROVIDER-INTEGRATION.md](PROVIDER-INTEGRATION.md).
- GitHub Copilot token is stored via Windows DPAPI (`Infrastructure/SecretStore.cs`), scoped to the
  current user, never as plaintext JSON.
- Logging (`Infrastructure/AppLog.cs`) redacts token-shaped substrings before every write and never
  logs raw provider payloads unless debug logging is explicitly enabled.
- Diagnostics export (`Services/DiagnosticsService.cs`) strips secrets and the user profile path before
  writing the sanitized file.

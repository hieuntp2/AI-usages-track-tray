# Implementation Report

## Summary

A working .NET 8 / WPF system-tray application was built from an empty repository: solution +
project scaffolding, normalized usage models, provider orchestration, a real Codex `app-server`
JSON-RPC client, a real Claude status-line bridge (installer + cache reader), a beta GitHub Copilot
REST scaffold, a full tray/flyout/settings UI, notifications, settings persistence, logging, and a
70-test unit test suite. `dotnet build`, `dotnet test`, and `dotnet publish` all succeed.

## Files created

**Solution/build**: `AiUsageTray.sln`, `src/AiUsageTray/AiUsageTray.csproj`,
`tests/AiUsageTray.Tests/AiUsageTray.Tests.csproj`, `src/AiUsageTray/app.manifest`,
`src/AiUsageTray/Resources/tray.ico`, `tray-alert.ico`.

**Models** (`src/AiUsageTray/Models/`): `UsageModels.cs` (UsageSnapshot/UsageWindow/UsageMetric/
ProviderConnectionStatus/ProviderCapabilities/detection & setup results), `IUsageProvider.cs`,
`SettingsModels.cs` (AppSettings, ProviderSettings, NotificationThresholds, enums).

**Infrastructure** (`src/AiUsageTray/Infrastructure/`): `AppPaths.cs`, `AtomicFile.cs`, `AppLog.cs`
(redacting rolling logger), `SingleInstance.cs` (named mutex + named-pipe activation), `SecretStore.cs`
(DPAPI), `CliLocator.cs` (shared where.exe + --version probing), `StartupRegistration.cs` (HKCU Run
key), `ThemeDetector.cs` (light/dark + live change events).

**Services** (`src/AiUsageTray/Services/`): `ProviderOrchestrator.cs`, `SettingsService.cs`,
`NotificationService.cs`, `DiagnosticsService.cs`, `TrayIconService.cs` (NotifyIcon wrapper).

**Codex provider** (`src/AiUsageTray/Providers/Codex/`): `CodexJsonRpcClient.cs` (newline-delimited
JSON-RPC 2.0 over arbitrary streams — request correlation, timeouts, cancellation, notification
dispatch, bounded/defensive line parsing), `CodexProcessSupervisor.cs` (persistent hidden child
process, handshake, restart with exponential backoff), `CodexCliLocator.cs`, `CodexJsonHelpers.cs`
(defensive JsonElement readers incl. safe Unix-timestamp conversion), `CodexRateLimitState.cs` +
`CodexRateLimitParser.cs` (rateLimitsByLimitId/legacy parsing, sparse-notification merge, duration-
derived labels), `CodexUsageParser.cs`, `CodexUsageProvider.cs`.

**Claude provider** (`src/AiUsageTray/Providers/Claude/`): `Bridge/claude-statusline-bridge.ps1`
(embedded resource), `ClaudeCacheModels.cs`, `ClaudeCacheParser.cs`, `ClaudeCacheReader.cs`
(FileSystemWatcher + poll fallback + atomic reads), `ClaudeSettingsService.cs` (JsonObject-tree
read/write preserving unknown properties), `ClaudeBridgeMetadata.cs`, `ClaudeBridgeInstaller.cs`
(install/repair/remove, idempotent), `ClaudeUsageProvider.cs`.

**GitHub Copilot scaffold** (`src/AiUsageTray/Providers/GitHubCopilot/`): `GitHubCopilotModels.cs`,
`GitHubCopilotUsageParser.cs`, `GitHubCopilotUsageProvider.cs`.

**ViewModels/Views**: `FlyoutViewModel.cs`, `ProviderCardViewModel.cs`, `UsageWindowRowViewModel.cs`,
`SettingsViewModel.cs`, `ProviderSettingsRowViewModel.cs`; `FlyoutWindow.xaml(.cs)`,
`SettingsWindow.xaml(.cs)`, `AboutWindow.xaml(.cs)`, `Converters.cs`; `Themes/Light.xaml`,
`Dark.xaml`, `Styles.xaml`; `App.xaml`/`App.xaml.cs` (composition root).

**Tests** (`tests/AiUsageTray.Tests/`): `Codex/CodexJsonRpcClientTests.cs` (in-memory-pipe harness —
no real process spawned), `Codex/CodexRateLimitParserTests.cs`, `Codex/CodexUsageParserTests.cs`,
`Claude/ClaudeCacheParserTests.cs`, `Claude/ClaudeCacheReaderTests.cs`,
`Claude/ClaudeBridgeInstallerTests.cs`, `Shared/UsageWindowTests.cs`,
`Shared/NotificationServiceTests.cs`, `Shared/SettingsServiceTests.cs`,
`Shared/SingleInstanceTests.cs`, `Shared/GitHubCopilotUsageParserTests.cs`,
`TestSupport/IsolatedAppData.cs`.

**Docs**: `README.md`, `docs/ARCHITECTURE.md`, `docs/PROVIDER-INTEGRATION.md`,
`docs/USER-GUIDE.md`, this file.

## Features completed

- Tray-only startup (no window, no taskbar button), left-click flyout, right-click menu, single
  instance with activation hand-off, start-with-Windows via per-user Run key.
- Normalized usage model that avoids a cross-provider aggregate percentage and distinguishes missing
  vs. zero.
- Codex: real `codex app-server` JSON-RPC integration, persistent process with restart/backoff,
  read-only `account/read` + `account/rateLimits/read` + best-effort `account/usage/read`, sparse
  notification merging, duration-derived window labels.
- Claude: real status-line bridge (PowerShell), safe/idempotent settings.json modification with
  backup and exact restore, event-driven cache reader, staleness and reset-passed handling.
- GitHub Copilot: compiling, unit-tested-at-the-parsing-level scaffold; disabled by default (see
  Known Limitations).
- Settings (general/providers/diagnostics), sanitized diagnostics export, notification thresholds
  with per-window dedup, light/dark theme incl. live OS theme change, structured redacting logger
  with 7-day rolling retention.

## Tests executed

```
dotnet test AiUsageTray.sln -c Release
Passed!  - Failed: 0, Passed: 75, Skipped: 0, Total: 75
```

Coverage highlights: Codex JSON-RPC request/response correlation, multiple in-flight requests,
timeout, cancellation, RPC error, malformed-line resilience, notification dispatch, and
process-exit/stream-closed cleanup — all against in-memory `System.IO.Pipelines` streams, no real
`codex` process. Codex rate-limit parsing across `rateLimitsByLimitId`/legacy shapes, primary-only,
primary+secondary, missing fields, unknown window durations, sparse-update merge, Unix
seconds/milliseconds conversion. Claude cache parsing (complete/partial/invalid payloads, invalid
timestamps, reset-passed detection), atomic cache reads including a simulated "two atomic writes in a
row" newest-wins case. Claude bridge install/repair/remove including idempotency, exact settings
restoration, invalid-JSON safety, and a real bug this suite caught (below). Shared: percentage
clamping, notification threshold dedup + reset-on-new-window, settings schema migration, corrupted
settings recovery, single-instance mutex semantics.

## Bugs found and fixed via testing

1. **Backup filename collision** (`Infrastructure/AtomicFile.cs`): `CreateTimestampedBackup` used
   second-resolution timestamps; calling Install immediately followed by Remove (both take a backup)
   within the same second produced the same backup filename and threw on the non-overwriting
   `File.Copy`. Fixed by adding millisecond resolution plus a short random suffix.
2. **Settings deserialization ignored case-insensitive matching** (`Services/SettingsService.cs`):
   `Load()` called `JsonSerializer.Deserialize<AppSettings>(json)` without the service's own
   `JsonSerializerOptions`, so a hand-edited or older-casing settings file wouldn't migrate correctly.
   Fixed by passing the shared options (now also `PropertyNameCaseInsensitive = true`) into `Load()`.
3. **GitHub billing date aggregation had a timezone-dependent off-by-one-day bug**
   (`Providers/GitHubCopilot/GitHubCopilotUsageParser.cs`): parsing a date-only string
   (`"2026-07-01"`) with `DateTimeOffset.TryParse`'s default (local-time) interpretation and then
   comparing via `.UtcDateTime` could shift the date across a month boundary depending on the host's
   UTC offset, silently excluding valid same-month usage. Fixed by parsing with
   `DateTimeStyles.AssumeUniversal` and comparing directly, removing the local-time round-trip.
4. **`InvariantGlobalization=true` crashed every WPF data binding at startup**
   (`AiUsageTray.csproj`): `System.Windows.Data.BindingExpression.Activate` calls
   `XmlLanguage.GetSpecificCulture()`, which requires real ICU culture data for the element's
   `Language` (default "en-US"). Under invariant globalization mode that data doesn't exist, so the
   very first binding activated on startup threw
   `"Cannot find non-neutral culture related to 'en-us'."` and crashed the app before the tray icon
   ever appeared. `InvariantGlobalization` is meant for headless console/ASP.NET workloads; it is
   fundamentally incompatible with WPF's XAML binding/culture system and was removed. Verified with a
   live smoke-launch of the published Release build - the app now starts, the tray icon appears, and
   it survives multiple refresh cycles with no crash logged.
5. **`SettingsViewModel` leaked an orchestrator subscription per Settings-window open, and could run
   provider-row updates off the UI thread**: `orchestrator.StateChanged +=` was previously never
   unsubscribed (each "Open Settings" click added another handler that outlived the window), and the
   handler updated bound `ProviderSettingsRowViewModel` properties directly from whatever background
   thread completed a provider refresh - exactly the kind of cross-thread access that crashes WPF
   bindings once a real UI element is attached. Fixed by (a) marshaling the handler through the
   window's `Dispatcher`, matching the pattern `FlyoutViewModel` already used, (b) storing the handler
   as a field so `SettingsViewModel.Dispose()` can unsubscribe it, and (c) having `App.xaml.cs` reuse a
   single cached `SettingsWindow`/`SettingsViewModel` pair (disposing on `Closed`) instead of
   constructing a new one - and a new background subscription - every time Settings is opened.

## Follow-up feature: tray menu provider status + GitHub Copilot authentication UI

Added in response to user feedback after the initial build:

- **Right-click tray menu** now shows one live row per *enabled* provider between "Refresh now" and
  "Settings" (`TrayIconService.UpdateProviderMenu`), each prefixed `✓`/`⚠`/`✗`/`…` and rebuilt on every
  `ProviderOrchestrator.StateChanged`. A row with an authentication/setup/error problem shows the
  message inline in its own text (plus as a tooltip) - e.g. `✗ GitHub Copilot — Not authenticated:
  GitHub token not configured.` - rather than requiring the user to open Settings to discover why a
  provider isn't showing data.
- **"+ Add AI service..."** row opens Settings pre-scrolled to the Providers tab
  (`SettingsWindow.SelectTab`), where a not-yet-enabled provider can be turned on and, for GitHub
  Copilot specifically, authenticated.
- **`ProviderSettingsRowViewModel.ApplySnapshot`** mirrors a provider's live `UsageSnapshot.Status`
  onto the Providers tab as `ConnectionStatusText`/`ConnectionHasError` (red when true), seeded
  immediately from the current state when Settings is opened - not only after clicking a button -
  satisfying "show the error on the currently active row" for a provider that was already added.
- **GitHub Copilot credential UI** (the gap flagged in the previous report) is now wired end-to-end:
  a username field and a `PasswordBox` per provider row, a "Add & Authenticate" button
  (`SettingsWindow.xaml.cs: OnSaveGitHubCredentialsClick`) that calls the new
  `GitHubCopilotUsageProvider.ConfigureAndAuthenticateAsync` - which saves the token via DPAPI *and*
  immediately calls GitHub's `/user` endpoint to validate it, surfacing "GitHub rejected the token
  (HTTP 401/403...)" on failure or auto-enabling the provider and triggering an immediate refresh on
  success. This also exercises the "GitHub token rejected" error-handling requirement that had no
  concrete code path before.

## Verified against the real Codex CLI

This machine turned out to have `codex-cli 0.144.1` and Claude Code actually installed, so rather than
relying only on hand-written fixtures, the Codex integration was checked against real output:

1. Piped a real `initialize` → `initialized` → `account/read` → `account/rateLimits/read` sequence
   directly into `codex app-server` and captured the raw responses.
2. That surfaced a real bug: `credits`/`rateLimitResetCredits` are shaped differently than the initial
   guess (`Providers/Codex/CodexRateLimitParser.cs`) - fixed, see bugs list above, and the exact
   captured response is now a permanent regression fixture
   (`CodexRateLimitParserTests.Parse_RealCodexCliFixture_*`).
3. Also confirmed the credit/reset-credit metrics were parsed but never actually reached the UI -
   added `CodexRateLimitParser.ToCreditMetrics` and wired it into `CodexUsageProvider.RefreshAsync`,
   closing that gap.
4. Smoke-launched the published Release build twice (before and after the fix): the first run's log
   showed repeated `Ignoring malformed line` warnings from real Codex traffic; after the fix, a fresh
   run against the same real Codex CLI produced zero warnings/errors over ~13 seconds of uptime before
   being stopped. (Claude Code is also installed here but a live end-to-end bridge/setup run was not
   performed as part of this pass - see Known Limitations.)

## Build result

```
dotnet build AiUsageTray.sln -c Release   → Build succeeded, 0 Warning(s), 0 Error(s)
dotnet test  AiUsageTray.sln -c Release   → Passed! 75/75
dotnet publish src/AiUsageTray/AiUsageTray.csproj -c Release -r win-x64 --self-contained false
  → produces a runnable AiUsageTray.exe + AiUsageTray.dll + CommunityToolkit.Mvvm.dll
```

## Known limitations

- **GitHub Copilot** is a genuine scaffold: the HTTP call path and JSON aggregation are implemented
  and unit-tested against hand-written fixtures, but have never been exercised against a real GitHub
  billing account/token (none was available in this environment). The exact response shape of
  GitHub's billing usage endpoints should be re-verified against a live account before enabling this
  provider by default.
- **Codex minimum supported version** is not pinned to a specific version number — no authoritative
  "app-server quota support was added in version X" reference was available. `DetectAsync` currently
  treats any resolvable `codex --version` as supported and lets a missing/erroring RPC method surface
  as an error at refresh time instead. This should be tightened once a real minimum version is known.
- **No installer** (MSIX/WiX/Velopack) — a portable published folder is the only distribution
  mechanism, per the MVP scope. Packaging notes are in the task brief for a later milestone.
- **Flyout positioning** approximates the tray icon's location (bottom-right corner of the work area,
  which screen's taskbar it's on) rather than using Shell32 interop to find the exact icon rectangle,
  since Windows doesn't expose that through `NotifyIcon` directly. This matches common tray-app
  convention but isn't pixel-exact on multi-monitor setups with the taskbar on a non-primary edge.
- **Codex was verified live** (see previous section) - real `app-server` output round-tripped
  correctly and one field-mapping bug it surfaced has been fixed. **Claude Code has not** been
  verified end-to-end: this environment has `claude.exe` installed, but no actual bridge
  install → send-a-prompt → read-the-cache cycle was run against it as part of this pass. That
  remains the highest-value next manual check.
- Icon assets (`tray.ico`, `tray-alert.ico`) are placeholder solid-color icons generated
  programmatically, not designed artwork.

## Recommended next tasks

1. Run `Settings → Providers → Claude Code → Setup` on this (or any) machine with Claude Code
   installed, send one real prompt, and confirm the flyout card populates from the bridge cache -
   the one integration not yet exercised live.
2. Obtain a GitHub account/fine-grained token with Copilot billing access, verify the real response
   shape against `GitHubCopilotUsageParser`, then enable the provider by default once confirmed.
3. Pin a real Codex CLI minimum-version check once that version number is documented upstream.
4. Replace the placeholder tray icons with designed artwork.
5. Consider MSIX packaging for easier distribution once the MVP has been used for a while.

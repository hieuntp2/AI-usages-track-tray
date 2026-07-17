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
5. **npm shim layout broke Codex detection entirely** (`Infrastructure/CliLocator.cs`): `where.exe
   codex` lists the npm global install's *extensionless POSIX shell shim* (for Git Bash) before the
   runnable `codex.cmd`, and the locator blindly took the first line. Windows can't execute the POSIX
   shim, so the `--version` probe always failed → "Could not determine Codex CLI version" → the
   provider showed "Update Codex CLI to enable usage monitoring" forever, regardless of the installed
   version. Fixed by ranking `where.exe` matches by executable preference (.exe > .com > .cmd > .bat >
   extensionless last) and probing candidates in order until one answers; the path that actually
   answered is the one later used to spawn `app-server`. Probe timeout also raised 5s → 15s for
   node-based .cmd shims on cold start. Verified live: detection now resolves `codex.cmd` and reads
   `codex-cli 0.144.2`, and the running app spawns a working app-server through it with zero log
   warnings.
6. **Detection only ran once at startup** (`Services/ProviderOrchestrator.cs`): installing or
   updating a CLI while the tray app was running changed nothing until an app restart - which is
   exactly why "I updated Codex CLI but it still shows the error" happened. `RefreshOneAsync` now
   re-runs `DetectAsync` before refreshing whenever the previous detection was incomplete (not
   installed, or version unreadable), so a manual "Refresh now" - or the next background poll - picks
   up a newly installed/updated CLI.
7. **Force-killing the tray app leaked the `codex app-server` child tree**
   (`Infrastructure/ChildProcessJob.cs`): graceful exit killed children via `Dispose`, but taskkill
   /F, a crash, or logoff skips all managed cleanup - and an npm-based launch leaves a whole
   `cmd.exe → node.exe → codex.exe` chain running headless. Fixed with a Windows Job Object
   (JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE): every spawned app-server is assigned to the job, and the OS
   reaps the entire tree the instant the tray process dies, no matter how. Verified live: force-killed
   the running app and confirmed zero orphaned app-server processes remained.
8. **Claude `resets_at` would have crashed the whole payload parse**
   (`Providers/Claude/ClaudeCacheModels.cs`): the official statusline schema (verified against
   code.claude.com/docs/en/statusline *and* a live bridge capture from Claude Code 2.1.195) sends
   `rate_limits.*.resets_at` as **Unix epoch seconds - a JSON number** - while the model declared it
   `string?`. The moment a Pro/Max account's first response populated `rate_limits`, deserialization
   would throw and the entire cached payload would read as "Invalid JSON". Fixed with
   `FlexibleTimestampConverter` (accepts epoch seconds, epoch milliseconds, and ISO-8601; unparsable
   → null, never an exception).
9. **Claude `context_window` model didn't match reality** (`ClaudeCacheModels.cs`/`ClaudeCacheParser.cs`):
   the parser expected `used_tokens`/`total_tokens`, but the real fields are `total_input_tokens`,
   `total_output_tokens`, `context_window_size`, precomputed nullable `used_percentage`/
   `remaining_percentage`, and a nullable `current_usage` breakdown - so the "Context used" metric
   never appeared. Rewritten to prefer Claude's own `used_percentage`, falling back to the documented
   formula `(input + cache_creation + cache_read) / context_window_size` (output tokens excluded).
   The live-captured session-start payload (all context percentages null, no rate_limits) is now a
   permanent regression fixture proving nulls stay "unknown" rather than becoming 0%.
10. **Empty Claude card was unexplained**: a valid payload without `rate_limits` (normal before the
   session's first API response, and always for non-Pro/Max accounts) rendered a card with no quota
   bars and no explanation. The card now says "Claude hasn't reported rate limits yet. They appear
   for Pro/Max accounts after the first response in a session."
11. **`SettingsViewModel` leaked an orchestrator subscription per Settings-window open, and could run
   provider-row updates off the UI thread**: `orchestrator.StateChanged +=` was previously never
   unsubscribed (each "Open Settings" click added another handler that outlived the window), and the
   handler updated bound `ProviderSettingsRowViewModel` properties directly from whatever background
   thread completed a provider refresh - exactly the kind of cross-thread access that crashes WPF
   bindings once a real UI element is attached. Fixed by (a) marshaling the handler through the
   window's `Dispatcher`, matching the pattern `FlyoutViewModel` already used, (b) storing the handler
   as a field so `SettingsViewModel.Dispose()` can unsubscribe it, and (c) having `App.xaml.cs` reuse a
   single cached `SettingsWindow`/`SettingsViewModel` pair (disposing on `Closed`) instead of
   constructing a new one - and a new background subscription - every time Settings is opened.

## Follow-up features: usage-reset notifications, designed icons

- **Usage-reset notifications** (`Services/NotificationService.cs`): a second notification kind -
  "*{Provider} {window} has been reset. Usage is now 2%.*" - fires when a new quota period starts,
  detected either by the window's reset time changing while usage drops, or (for providers that never
  report `resets_at`) by usage falling ≥30 points after having been ≥10%. First sightings of a window
  and trivially-used periods never announce, and threshold notifications re-arm for the new period.
  Six new unit tests cover fire/no-fire cases.
- **Designed icons** replace the placeholder solid squares: a usage-gauge glyph on a rounded blue
  badge (red, near-full gauge for the alert variant), rendered per-size from a 16x supersampled
  master into multi-resolution .ico files (16-256 px), with a simplified thicker-stroke variant for
  16/20/24 px so the glyph stays legible in the tray. Generated by a reproducible Pillow script;
  previews in `docs/icon-preview-*.png`.

## Follow-up feature: headless Claude usage probe (no more "open Claude Code to update")

The original Claude integration was purely passive: quota only updated when the user's own Claude
Code session pushed a status-line payload through the bridge, so the card went stale whenever the
user simply didn't use Claude Code. Now `ClaudeUsageProvider` falls back to actively probing the CLI
(`Providers/Claude/ClaudeUsageProbe.cs`) whenever the bridge cache is missing, stale (>30 min),
quota-bar-less, or past a window's reset time:

- `claude auth status --json` (parsed by `ClaudeAuthStatusParser`) gates on sign-in, then
  `claude --safe-mode --ax-screen-reader /usage` - run with stdin closed and stdout piped - prints
  the usage panel as flat one-line-per-window text and exits by itself. No TTY/ConPTY needed, no
  prompt ever reaches the model, so a probe costs zero quota.
- `ClaudeUsageOutputParser` gained the single-line format ("Current week (all models): 26% used ·
  resets Jul 20, 11pm (Zone)") alongside the interactive multi-line/ANSI format; both are covered by
  fixtures captured from the real CLI (2.1.195). Window ids stay identical to the bridge's
  (`five_hour`, `seven_day`, `weekly_model:<name>`), so notifications and reset detection work
  unchanged across both sources.
- Probes are serialized, rate-limited (90s floor), time-limited (20s/90s with process-tree kill),
  assigned to a kill-on-close Job Object, and run from a neutral working directory with
  `NO_COLOR=1` / `DISABLE_AUTOUPDATER=1`. Old CLI builds fail fast on the unknown
  `--ax-screen-reader` flag and surface "update Claude Code" instead of misbehaving.
- Fresh bridge snapshots (with actual quota bars) still short-circuit the probe entirely - the
  passive path remains the cheap, metric-rich preferred source; bridge setup is now an enhancement
  (context/cost metrics) rather than a prerequisite.

Verified live: with the bridge cache removed and Claude Code closed, the tray app logged
"CLI usage probe returned 3 quota window(s)" and rendered current 5-hour/weekly/per-model usage on
its own. 111/111 unit tests pass.

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

# Provider Integration Notes

## Codex integration contract

**Transport**: `codex app-server`, spawned as a hidden child process. Communication is
newline-delimited JSON-RPC 2.0 over stdin/stdout (`Providers/Codex/CodexJsonRpcClient.cs`).

**Handshake** (once per process lifetime):

1. `initialize` request with `clientInfo: { name: "ai_usage_tray", title: "AI Usage Tray", version }`.
2. `initialized` notification.
3. No thread/turn is ever started.

**Calls made, every refresh**:

- `account/read` (`{ refreshToken: false }`) → account label + plan.
- `account/rateLimits/read` → quota windows. Parsed in `CodexRateLimitParser`:
  - Prefers `rateLimitsByLimitId["codex"]`; falls back to the legacy top-level `rateLimits`.
  - Reads `limitId`, `limitName`, `planType`, `primary`/`secondary` (`usedPercent`,
    `windowDurationMins`, `resetsAt`), `rateLimitReachedType`.
  - Credit info is nested as `credits: { hasCredits, unlimited, balance }` on the entry itself (a
    numeric *string* balance) - only surfaced when `hasCredits` is `true`, since a `false` plan's
    `"0"` balance isn't a meaningful value. The free-reset count lives at the *top level* of the
    result as a sibling of `rateLimits`/`rateLimitsByLimitId`: `rateLimitResetCredits.availableCount`
    - not nested per-entry. Both shapes were confirmed against a live `codex-cli 0.144.1` session
    (see `CodexRateLimitParserTests.Parse_RealCodexCliFixture_*` for the exact captured fixture) after
    an initial guess at the field names turned out wrong.
  - Window labels are derived from `windowDurationMins`, not hardcoded: 300 → "5-hour limit", 10080 →
    "Weekly limit", anything else → formatted from the actual duration (e.g. "2-hour limit",
    "90-minute limit"). Codex changing its window sizes doesn't require a code change. Confirmed live:
    one real account's *only* populated window was `primary` at `windowDurationMins: 10080` with
    `secondary: null` - i.e. "primary" here was the weekly window, not a 5-hour one, exactly the case
    this design was built to not assume away.
- `account/usage/read` (best-effort — older servers may not support it; a JSON-RPC error here is
  swallowed and just omits the extra metrics) → lifetime tokens, peak daily tokens, current/longest
  streak, surfaced as compact `UsageMetric`s. Daily token buckets are intentionally not surfaced in the
  compact flyout.

**Live updates**: `account/rateLimits/updated` notifications may arrive with a sparse payload (e.g.
just an updated `primary.usedPercent`). These are merged field-by-field onto the last complete state
(`CodexRateLimitState.Merge`) rather than replacing it, so an update that omits `secondary` doesn't
null out the weekly window.

**Version compatibility**: detection (`CodexCliLocator`) resolves `codex` via `where.exe` and reads
`codex --version`. If the version can't be determined, or a required RPC method isn't supported by the
running server, the card shows "Update Codex CLI to enable usage monitoring." rather than a raw error.

## Claude status-line payload contract

**Transport**: Claude Code's `statusLine` mechanism — Claude sends session JSON to the configured
command's stdin on every status-line refresh. This app never calls Claude Code itself; it only reads
what the bridge already cached from Claude's own, normal traffic.

**Fields read** (`Providers/Claude/ClaudeCacheModels.cs`):

```
rate_limits.five_hour.used_percentage
rate_limits.five_hour.resets_at
rate_limits.seven_day.used_percentage
rate_limits.seven_day.resets_at
model.id
model.display_name
context_window.used_tokens / total_tokens
cost.total_cost_usd
session_id
version
```

`rate_limits` may be entirely absent (before the first API response, for unsupported account types,
non-Pro/Max plans, or older CLI versions) — the provider then reports `SetupRequired` /
"Waiting for first Claude response" rather than 0%.

**Bridge** (`Providers/Claude/Bridge/claude-statusline-bridge.ps1`):

1. Reads complete stdin JSON.
2. Validates it parses as JSON (if not, still attempts to forward the raw input, but skips caching).
3. Wraps it as `{ "capturedAt": <ISO-8601>, "payload": <original JSON> }`.
4. Writes to a temp file, then atomically replaces `%LOCALAPPDATA%\AiUsageTray\data\claude-latest.json`.
5. Reads `%LOCALAPPDATA%\AiUsageTray\config\claude-bridge.json` for the original status-line command
   (if one existed before this app was set up).
6. If present, forwards the same stdin to that command and relays its stdout back to Claude Code
   unmodified — so the visible status line is unchanged.
7. Any bridge-internal error is logged to `%LOCALAPPDATA%\AiUsageTray\logs\claude-bridge.log`, never
   printed to stdout (which would corrupt the status line).

**Settings modification** (`Providers/Claude/ClaudeSettingsService.cs` +
`ClaudeBridgeInstaller.cs`): `~/.claude/settings.json` is parsed as a `System.Text.Json.Nodes.JsonObject`
tree, so every property this app doesn't know about (`padding`, `refreshInterval`,
`hideVimModeIndicator`, arbitrary future fields) round-trips untouched. Only the `statusLine` property
is ever written. A timestamped backup is taken before every write. Setup is idempotent — running it
twice detects the bridge is already installed and does nothing further; "Repair integration"
re-applies the bridge entry if something else overwrote it; "Remove integration" restores the exact
original `statusLine` block (or removes the property if none existed) from the recorded metadata.

**Staleness**: if the cached snapshot is older than 30 minutes, the card is marked `Stale`. If a
window's `resets_at` has already passed with no fresher snapshot since, the UI shows "Limit may have
reset. Open Claude Code to confirm current usage." instead of inferring 0%.

## GitHub Copilot research

Uses GitHub's official REST billing endpoints — never Copilot CLI/TUI scraping:

```
GET /users/{username}/settings/billing/ai_credit/usage
GET /users/{username}/settings/billing/premium_request/usage
```

Requires a fine-grained personal access token with `Plan: read` permission, stored via Windows DPAPI
(never plaintext). The parser (`GitHubCopilotUsageParser`) aggregates `usageItems` for the current
calendar month where `product` contains "copilot", summing `quantity`. Because GitHub's billing model
and exact response shape can change, plan allowance and remaining balance are **not** hardcoded —
the card explicitly states when that information isn't available from the API.

**Status: Beta, disabled by default.** The HTTP call path and JSON aggregation are implemented and
unit-tested against fixtures, but have not been exercised against a live GitHub billing account (no
such account/token was available in this environment). Enable only after verifying the response shape
against a real account; do not assume the fixture shape in this repo is exactly what production
returns.

## Gemini CLI — not yet supported

The documented interactive command is `/stats model`, which shows session token usage and quota in a
human-readable TUI. There is no documented headless JSON output, local API, or hook/event payload as
of this writing. Screen-scraping an interactive TUI is explicitly out of scope (fragile, and Gemini
CLI's output format is not a stable contract). **Decision: provider placeholder only**, to be
implemented once one of the following exists:

- A stable headless JSON command (e.g. `gemini stats --json`).
- An official local API or hook/event payload equivalent to Codex's `app-server` or Claude's
  status-line mechanism.
- An account usage API usable by personal OAuth users (API-key / Google Cloud users may be supportable
  separately later through official Cloud quota/billing APIs, which is a distinct integration path from
  personal CLI usage).

## Cursor — not yet supported

Cursor's official APIs currently focus on team/admin analytics, not personal quota. Reading Cursor's
local SQLite database, browser cookies, or undocumented private endpoints is explicitly out of scope
(fragile, unauthorized surface, and liable to break silently on every Cursor update). **Decision:
personal quota monitoring is unsupported** until Cursor documents a personal-usage API.

## Windsurf / Devin Desktop — not yet supported

Enterprise APIs can expose team credit balance/analytics, but there is no documented personal
daily/weekly quota endpoint, and undocumented endpoints are out of scope for the same reasons as
Cursor. **Decision**: a future "Enterprise provider" capability (team-level credit balance, for admins
who have that API access) is a plausible separate feature, but it is explicitly not part of this MVP.

## Version compatibility strategy

Every provider's `DetectAsync` records the resolved executable path and version string, and
`ProviderDetectionResult.IsSupportedVersion` lets a provider say "installed, but too old" distinctly
from "not installed". The UI surfaces that distinction directly (`UnsupportedVersion` state) instead
of a generic error. New RPC methods/fields are always read defensively — an absent field is `null`,
never assumed to be `0` or a default — so a provider CLI update that adds fields is forward-compatible
by construction, and one that removes a field degrades to "unknown" rather than crashing the refresh.

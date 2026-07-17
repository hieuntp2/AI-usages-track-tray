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

**Transport**: two complementary sources, preferred in this order:

1. **Status-line bridge (passive, free)** — Claude Code's `statusLine` mechanism sends session JSON
   to the configured command's stdin on every status-line refresh; the bridge caches it. Richest
   data (quota + context window + session cost), but only updates while the user is actually using
   Claude Code.
2. **Headless CLI probe (active fallback)** — whenever the cache is missing, stale (>30 min), has
   no quota windows yet, or a window's reset time has passed, the provider runs the CLI itself
   (`ClaudeUsageProbe`), so the card keeps updating even if Claude Code is never opened. See
   "Headless CLI probe" below.

**Fields read** (`Providers/Claude/ClaudeCacheModels.cs`), verified against the official schema at
code.claude.com/docs/en/statusline *and* a live capture from Claude Code 2.1.195 through the bridge:

```
rate_limits.five_hour.used_percentage      number 0-100
rate_limits.five_hour.resets_at            Unix epoch SECONDS (a JSON number, not an ISO string)
rate_limits.seven_day.used_percentage
rate_limits.seven_day.resets_at
model.id / model.display_name
context_window.total_input_tokens          current context (v2.1.132+; cumulative before that)
context_window.total_output_tokens
context_window.context_window_size
context_window.used_percentage             nullable - precomputed by Claude Code, input-side only
context_window.remaining_percentage        nullable
context_window.current_usage               nullable object: input_tokens, output_tokens,
                                           cache_creation_input_tokens, cache_read_input_tokens
cost.total_cost_usd
session_id / version
```

Two traps this integration hit and now guards against:

- `resets_at` is a **Unix-seconds number**. Deserializing it into a string property throws and takes
  the *entire payload* down with it. `FlexibleTimestampConverter` accepts epoch seconds, epoch
  milliseconds, and ISO-8601 strings, and turns anything unparsable into null instead of an exception.
- `context_window` percentages are **null early in a session** (and `current_usage` is null before
  the first API call / right after `/compact`). Context usage prefers Claude's precomputed
  `used_percentage` and falls back to `(input + cache_creation + cache_read) / context_window_size`
  per the documented formula (output tokens excluded); when both are unavailable no metric is shown.

`rate_limits` is only present **for Claude.ai Pro/Max subscribers, and only after the session's first
API response** — its absence in an otherwise-valid payload is normal, and the card explains that
("Claude hasn't reported rate limits yet...") instead of rendering an unexplained empty card. Each
window (`five_hour`, `seven_day`) may also be independently absent.

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

**Staleness**: if the cached snapshot is older than 30 minutes, the card is marked `Stale` and the
CLI probe takes over. If a window's `resets_at` has already passed, the cache view says "Limit may
have reset. Refreshing from the Claude CLI…" instead of inferring 0%, and the probe fetches the
post-reset truth.

## Headless CLI probe (`Providers/Claude/ClaudeUsageProbe.cs`)

Verified live against Claude Code 2.1.195 (WinGet native build): with stdin redirected-and-closed
and stdout piped (no TTY), the CLI renders `/usage` as flat text — one line per window — and exits
on its own:

```
claude auth status --json                        → { "loggedIn": true, "subscriptionType": ... }
claude --safe-mode --ax-screen-reader /usage     → Current session: 16% used · resets Jul 17, 11:30am (Asia/Saigon)
                                                   Current week (all models): 26% used · resets Jul 20, 11pm (Asia/Saigon)
                                                   Current week (Fable): 10% used · resets Jul 20, 11pm (Asia/Saigon)
```

Design constraints, all deliberate:

- **Zero quota cost**: `/usage` is a built-in panel, not a prompt — nothing reaches the model.
  `auth status` gates the probe so a signed-out CLI surfaces `NotAuthenticated` instead of noise.
- **`--safe-mode`, not `--bare`**: safe-mode disables hooks/plugins/MCP/CLAUDE.md discovery (the
  probe must never trigger user customizations) while OAuth subscription auth still works; `--bare`
  would restrict auth to `ANTHROPIC_API_KEY` and break subscription accounts.
- **`--ax-screen-reader` doubles as a version gate**: builds too old to support it reject the
  unknown flag and fail fast (→ `Unsupported`, "update Claude Code" message) *before* anything
  could be interpreted as a prompt.
- **Process hygiene**: neutral working directory (`%LOCALAPPDATA%\AiUsageTray`), `NO_COLOR=1`,
  `DISABLE_AUTOUPDATER=1`, hard timeouts (20s auth / 90s usage) with kill-on-timeout, and the same
  kill-on-job-close Job Object the Codex supervisor uses, so a hung probe can never outlive the app.
- **Throttled**: probes are serialized and rate-limited (90s floor between CLI spawns); rapid
  manual refreshes reuse the last result. The default 300s background poll is unaffected.
- **Parsing** (`ClaudeUsageOutputParser`) handles both the one-line-per-window headless render and
  the multi-line interactive panel (ANSI repaints, doubled percentages), maps windows to the same
  stable ids the bridge uses (`five_hour`, `seven_day`, `weekly_model:<name>`), and never coerces
  an unreadable percentage to 0. Probe snapshots carry quota windows only (`Source: "claude-cli"`) —
  context/cost metrics stay bridge-only, because the probe session's own numbers are meaningless.

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

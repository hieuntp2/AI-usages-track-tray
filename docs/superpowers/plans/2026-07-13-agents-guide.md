# Repository Agent Guide Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a concise root `AGENTS.md` that gives coding agents accurate, project-specific implementation and verification guidance.

**Architecture:** Use one root instruction file because this repository contains one WPF application and one closely related xUnit test project. Keep the guide operational and stable: document architectural boundaries, safety invariants, change routing, and commands while linking to existing docs for volatile provider details.

**Tech Stack:** Markdown, .NET 8, C# with nullable reference types, WPF, CommunityToolkit.Mvvm, xUnit, Windows APIs.

---

## File Structure

- Create: `AGENTS.md` — repository-wide instructions for coding agents.
- Reference only: `README.md`, `docs/ARCHITECTURE.md`, `docs/PROVIDER-INTEGRATION.md`, `docs/USER-GUIDE.md` — authoritative detailed documentation; do not modify.

### Task 1: Create the Repository Agent Guide

**Files:**
- Create: `AGENTS.md`

- [ ] **Step 1: Create `AGENTS.md` with the approved project-specific guidance**

Use this exact content:

````markdown
# AGENTS.md

## Project Overview

AI Usage Tray is a Windows-only .NET 8 WPF system-tray application that displays coding-AI usage
and quota without consuming quota itself. It currently integrates with OpenAI Codex, Claude Code,
and an opt-in GitHub Copilot beta provider.

Read these before substantial changes:

- `README.md` — setup, operation, publishing, privacy promises, and troubleshooting.
- `docs/ARCHITECTURE.md` — component boundaries, data flow, and lifecycles.
- `docs/PROVIDER-INTEGRATION.md` — provider contracts and verified payload shapes.
- `docs/USER-GUIDE.md` — user-visible behavior and settings.

## Non-Negotiable Product Invariants

- Monitoring must never start an AI prompt, thread, or turn, or otherwise consume user quota.
- Never read, persist, log, or transmit prompt content or conversation transcripts.
- Never read Codex or Claude OAuth credentials. Store configured GitHub tokens only through
  `SecretStore` (Windows DPAPI), never in settings JSON or logs.
- Do not add telemetry or analytics. Network access must remain provider-specific and explicit.
- Treat missing provider data as unknown, never as zero. Clamp malformed percentages only for
  display and preserve diagnostic context safely.
- A failure in one provider must not blank or block other providers. Preserve last-known-good data.

## Repository Map

- `src/AiUsageTray/Models/` — normalized usage models, settings, and `IUsageProvider`.
- `src/AiUsageTray/Infrastructure/` — paths, atomic I/O, logging, secrets, process and Windows helpers.
- `src/AiUsageTray/Services/` — orchestration, settings, notifications, diagnostics, and tray behavior.
- `src/AiUsageTray/Providers/<Name>/` — provider transport, parsing, detection, and setup logic.
- `src/AiUsageTray/ViewModels/` — UI state and commands using CommunityToolkit.Mvvm.
- `src/AiUsageTray/Views/` — WPF XAML and minimal code-behind for window-specific behavior.
- `src/AiUsageTray/Themes/` — shared styles and light/dark resources.
- `tests/AiUsageTray.Tests/` — xUnit tests grouped by provider and shared subsystem.
- `docs/` — architecture, integration contracts, user guidance, and implementation notes.

## Architecture Rules

- All providers implement `IUsageProvider` and return the normalized `UsageSnapshot` model.
- Provider-specific payload types must stay inside `Providers/<Name>/`; do not leak them into the
  orchestrator, view models, or views.
- Express provider behavior through `ProviderCapabilities`; avoid provider-name conditionals in
  shared UI and orchestration code unless the workflow is inherently provider-specific.
- Register new providers in `App.xaml.cs`, add default settings in `SettingsModels.cs`, and add tests
  under `tests/AiUsageTray.Tests/<Provider>/`.
- Keep parsing separate from transport and process/file lifecycle code. Parsers should be testable
  with captured or hand-written fixtures and without live services.
- Keep shared orchestration resilient: bound concurrency, honor cancellation, catch provider-local
  failures, log safely, and retain last-known-good snapshots.

## Provider Constraints

### Codex

- Use only read-only `codex app-server` account/rate-limit/usage RPC methods. Never start a thread or
  turn and never inspect `%USERPROFILE%\.codex\auth.json`.
- Maintain one supervised app-server process per application session, not one process per refresh.
- Preserve JSON-RPC request correlation, bounded input handling, timeouts, restart/backoff behavior,
  handshake ordering, sparse notification merging, and cleanup of the child process tree.
- Derive quota labels from server-provided window duration. Do not assume `primary` means five-hour
  or `secondary` means weekly.

### Claude

- Claude usage is event-driven through the status-line bridge. Refresh reads the local cache only;
  it must never launch Claude or send a prompt.
- Preserve the user's existing status-line command and every unrelated property in
  `~/.claude/settings.json`.
- Setup, repair, and removal must remain idempotent and must create a timestamped backup before
  changing user configuration.
- Keep bridge/cache writes atomic and tolerate stale, incomplete, or malformed cache data.

### GitHub Copilot

- Keep the provider disabled by default until its live billing response contract is verified.
- Store tokens with `SecretStore`, validate authentication failures explicitly, and do not log
  authorization headers, token values, or unredacted response bodies.
- Keep HTTP calls cancellable and provider-local; other providers must continue when GitHub fails.

## C# and WPF Conventions

- Preserve nullable reference type correctness; do not suppress warnings instead of modeling nulls.
- Use `async`/`await` for I/O, accept and propagate `CancellationToken`, and use
  `ConfigureAwait(false)` below the UI layer where no dispatcher affinity is needed.
- Marshal collection and UI-bound property changes to the WPF dispatcher.
- Keep view code-behind limited to window mechanics that are awkward to express in bindings.
  Put state and commands in view models using CommunityToolkit.Mvvm patterns already in the repo.
- Unsubscribe events and dispose timers, watchers, streams, semaphores, HTTP/process resources, and
  providers that own lifecycle state.
- Follow existing naming and formatting. Prefer focused types and small methods over new frameworks
  or broad refactors.

## Persistence, Processes, and Security

- Use `AppPaths` for application-owned paths. Do not scatter hard-coded local-app-data paths.
- Use `AtomicFile` for settings, cache, and metadata writes that must not be observed partially.
- Back up user-owned configuration before modifying it, and preserve unknown JSON properties.
- Use `AppLog`; do not bypass its redaction or log raw secrets, prompt content, full provider
  payloads, or unnecessary full user paths.
- Start CLI processes hidden with redirected streams and bounded waits. Ensure cancellation and app
  shutdown terminate owned child processes and do not leave background readers hanging.
- Avoid live integration actions during routine verification. Do not install/remove the Claude
  bridge, authenticate GitHub, send prompts, or alter startup registration unless explicitly asked.

## Testing

- Add or update focused xUnit tests for every behavior change, especially parser edge cases,
  malformed/missing fields, sparse updates, cancellation, cleanup, and error isolation.
- Prefer deterministic fixtures over real provider calls. If a real payload reveals a bug, retain a
  sanitized regression fixture and document what shape it proves.
- Tests that call `AppPaths.SetRootForTests` or otherwise mutate process-wide app-data state must use
  `[Collection("IsolatedAppData")]` and `IsolatedAppData`; these tests cannot safely run in parallel.
- Never let tests touch real `%LOCALAPPDATA%\AiUsageTray`, user CLI settings, credentials, registry
  startup entries, or live quota endpoints.
- For view-model or async changes, test the non-visual behavior directly where practical; manually
  smoke-test tray positioning, theme resources, and window interaction when UI behavior changes.

## Commands

Run from the repository root in PowerShell:

```powershell
dotnet restore AiUsageTray.sln
dotnet build AiUsageTray.sln -c Release --no-restore
dotnet test AiUsageTray.sln -c Release --no-build --no-restore
dotnet run --project src/AiUsageTray/AiUsageTray.csproj
```

Framework-dependent publish:

```powershell
dotnet publish src/AiUsageTray/AiUsageTray.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false
```

The project targets `net8.0-windows`; build and runtime verification require Windows and the .NET 8
SDK/runtime even when a newer SDK is also installed.

## Change Checklist

- Inspect `git status` first and preserve unrelated user changes in a dirty worktree.
- Make the smallest change that respects the architecture and privacy invariants.
- Add regression tests before or alongside behavior changes; run the narrowest relevant test first.
- Run the full Release build and test suite before claiming completion.
- Review logs, diagnostics, fixtures, and diffs for secrets or user-specific paths.
- Update `docs/ARCHITECTURE.md` for lifecycle or boundary changes,
  `docs/PROVIDER-INTEGRATION.md` for provider contracts/payloads, and `docs/USER-GUIDE.md` for
  user-visible behavior.
- Report manual verification gaps clearly, especially for real provider integration or WPF visuals.
````

- [ ] **Step 2: Inspect the rendered file for accidental encoding or formatting damage**

Run:

```powershell
Get-Content -Raw AGENTS.md
```

Expected: the complete guide is readable, headings and fenced PowerShell blocks are balanced, and
Windows paths contain the intended backslashes.

- [ ] **Step 3: Check the guide for incomplete markers and volatile test counts**

Run:

```powershell
$patterns = @('T' + 'BD', 'TO' + 'DO', 'FIX' + 'ME', 'PLACE' + 'HOLDER')
$hits = Select-String -Path AGENTS.md -Pattern ($patterns -join '|')
if ($hits) { $hits; throw 'Incomplete marker found in AGENTS.md.' }
if (Select-String -Path AGENTS.md -Pattern '[0-9]+/[0-9]+ test') {
  throw 'Volatile test count found in AGENTS.md.'
}
```

Expected: exit code 0 with no output.

### Task 2: Verify Repository Accuracy

**Files:**
- Verify: `AGENTS.md`
- Reference: `AiUsageTray.sln`
- Reference: `src/AiUsageTray/AiUsageTray.csproj`
- Reference: `tests/AiUsageTray.Tests/AiUsageTray.Tests.csproj`

- [ ] **Step 1: Confirm every path named by the guide exists**

Run:

```powershell
$paths = @(
  'README.md',
  'docs/ARCHITECTURE.md',
  'docs/PROVIDER-INTEGRATION.md',
  'docs/USER-GUIDE.md',
  'src/AiUsageTray/Models',
  'src/AiUsageTray/Infrastructure',
  'src/AiUsageTray/Services',
  'src/AiUsageTray/Providers',
  'src/AiUsageTray/ViewModels',
  'src/AiUsageTray/Views',
  'src/AiUsageTray/Themes',
  'tests/AiUsageTray.Tests'
)
$missing = $paths | Where-Object { -not (Test-Path -LiteralPath $_) }
if ($missing) { throw "Missing referenced paths: $($missing -join ', ')" }
```

Expected: exit code 0 with no output.

- [ ] **Step 2: Build the solution using the documented sequence**

Run:

```powershell
dotnet restore AiUsageTray.sln
dotnet build AiUsageTray.sln -c Release --no-restore
```

Expected: restore succeeds and the Release build completes with 0 errors.

- [ ] **Step 3: Run the full test suite using the documented command**

Run:

```powershell
dotnet test AiUsageTray.sln -c Release --no-build --no-restore
```

Expected: all tests pass with 0 failures.

- [ ] **Step 4: Compare the guide against the approved design**

Run:

```powershell
Get-Content -Raw AGENTS.md
Get-Content -Raw docs/superpowers/specs/2026-07-13-agents-guide-design.md
```

Expected: the guide covers all ten content areas in the design and does not introduce any listed
non-goal.

### Task 3: Review and Commit the Guide

**Files:**
- Create: `AGENTS.md`

- [ ] **Step 1: Confirm only the intended new guide is included in this implementation**

Run:

```powershell
git status --short
git diff -- AGENTS.md
```

Expected: `AGENTS.md` is the only new implementation file from this plan. Existing unrelated dirty
worktree entries may remain and must not be staged.

- [ ] **Step 2: Check whitespace and Markdown diff quality**

Run:

```powershell
git diff --check -- AGENTS.md
```

Expected: exit code 0 with no whitespace errors.

- [ ] **Step 3: Stage and commit only `AGENTS.md`**

Run:

```powershell
git add -- AGENTS.md
git diff --cached --name-only
git commit -m "docs: add repository agent guide" -- AGENTS.md
```

Expected: the staged file list contains only `AGENTS.md`, and the commit succeeds without including
the user's unrelated changes.

# AGENTS.md Design

## Goal

Create a concise, repository-specific root `AGENTS.md` that helps coding agents make safe,
consistent changes to AI Usage Tray without duplicating the existing user and architecture
documentation.

## Audience and Scope

The guide is written in English for coding agents working anywhere in this repository. A single
root file is sufficient because the solution is small, uses one application project and one test
project, and already has clear module boundaries. Nested instruction files are out of scope.

## Content Structure

The guide will contain:

1. A short project overview covering Windows-only WPF, .NET 8, MVVM, and the supported providers.
2. The non-negotiable privacy and product invariants: usage monitoring must not consume AI quota,
   access prompt or transcript content, expose credentials, or add telemetry.
3. A compact repository map and change-routing guide for models, infrastructure, services,
   providers, view models, views, themes, tests, and documentation.
4. Architecture rules centered on `IUsageProvider`, `UsageSnapshot`, provider capabilities, and
   registration in `App.xaml.cs`.
5. Provider-specific constraints for Codex JSON-RPC, Claude's event-driven status-line bridge, and
   GitHub Copilot's network and DPAPI behavior.
6. C# and WPF implementation conventions for nullable code, asynchronous I/O, cancellation,
   dispatcher affinity, MVVM, event unsubscription, and disposal.
7. Persistence, process, security, and logging rules, including atomic file writes, backups before
   user configuration changes, secret storage, redaction, and child-process cleanup.
8. Testing guidance, especially regression fixtures and the serialized `IsolatedAppData` xUnit
   collection required by the process-wide mutable `AppPaths` test override.
9. Exact restore, build, test, run, and publish commands plus a proportional definition of done.
10. Documentation expectations and a reminder to preserve unrelated user changes in a dirty
    worktree.

## Detail Level

Target roughly 100–150 lines. Prefer actionable rules and file paths over prose. Link to
`README.md`, `docs/ARCHITECTURE.md`, `docs/PROVIDER-INTEGRATION.md`, and `docs/USER-GUIDE.md` for
details instead of copying them. Avoid volatile claims such as an exact test count or a hard-coded
Codex CLI minimum version.

## Verification

Before delivery:

- Confirm every referenced path and command exists or succeeds in the current repository.
- Check that the guide matches the current architecture and does not contradict existing docs.
- Scan for placeholders, ambiguous requirements, duplicated documentation, and stale test counts.
- Run `dotnet test AiUsageTray.sln -c Release --no-restore` as the repository baseline.
- Review `git diff` to ensure only the intended documentation files were added or changed.

## Non-Goals

- Changing application or test code.
- Adding nested `AGENTS.md` files.
- Rewriting existing documentation.
- Running live provider setup, authentication, prompts, or quota-affecting integration checks.
- Staging or modifying the user's unrelated worktree changes.

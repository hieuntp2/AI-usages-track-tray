# Durable Notification Deduplication Design

## Goal

Show a Windows notification only when an AI provider's quota window reaches 100% usage or when that
window has genuinely reset. Never repeat the same event during later refreshes or after the
application restarts.

## User-visible behavior

- The former 70% and 90% notifications are removed. A limit notification is eligible only when a
  quota window reports `UsedPercent >= 100`.
- A limit event fires once for each provider, quota window, and quota period. Re-reading the same
  snapshot, receiving another snapshot in the same period, or restarting the application must not
  fire it again.
- A reset event fires once when the service confirms a new quota period: the reported reset time
  changes and usage drops, or a provider without reset times reports the existing meaningful
  cliff-drop signal.
- First sighting of a low-usage window remains a baseline, not a reset. Reset notifications retain
  the existing safeguards: prior usage must be at least 10%, and a reset-time-free drop must be at
  least 30 percentage points.
- A confirmed reset re-arms the 100% event for the new quota period. Separate quota windows remain
  separate events because they can have different limits and reset schedules.
- Per-provider notification enablement remains supported. Configurable percentage lists are no
  longer part of notification behavior.

## Architecture

`NotificationService` remains the policy owner. It compares each normalized `UsageWindow` with the
last recorded state, decides whether a 100% or reset event is new, and formats the existing balloon
message. Its deduplication key remains the stable provider ID plus quota-window ID; the stored reset
time identifies the quota period when the provider supplies one.

A focused `NotificationStateStore` persists the policy state in
`%LOCALAPPDATA%\AiUsageTray\config\notification-state.json`. `AppPaths` exposes that location and the
store uses `AtomicFile` so a process interruption cannot expose a partial file. The JSON contains
only provider/window identifiers, reset time, last observed percentage, and event flags. It contains
no credentials, provider payloads, prompt content, or conversation data.

The service loads state when it is created and updates in-memory state under a lock. When an event is
eligible, it builds a candidate state with the event marked as delivered and successfully persists
that candidate before committing the marker in memory and raising `NotificationRequested`. This
ordering prioritizes the explicit no-repeat requirement: if state persistence fails, the service
logs a safe error, keeps the event pending, and suppresses the balloon. A later evaluation may retry
the durable write and emit the still-new event only after that write succeeds.

## Data flow

1. `ProviderOrchestrator` supplies a normalized snapshot as it does today.
2. `NotificationService` ignores unavailable percentages and disabled providers.
3. For each window, it detects a period change or cliff-drop reset and updates the durable period
   state.
4. It then checks the fixed 100% limit condition against the persisted delivered flag.
5. Any new event flag and the latest observation are atomically persisted before the corresponding
   event is raised.
6. Later evaluations and newly constructed service instances load the delivered flag and remain
   silent for the same event.

## Recovery and error handling

- A missing state file is treated as first run and created from observed data.
- Malformed state is backed up through the existing backup infrastructure, logged without raw
  contents, and replaced with a safe baseline. The first observation after recovery establishes
  state without emitting a notification, preventing a possibly duplicated event.
- Unknown JSON fields are tolerated so the state format can evolve.
- Persistence failure never breaks provider refresh or other providers. The latest observation
  remains in memory for the current process, while a newly eligible balloon remains pending and is
  suppressed until its deduplication marker can be written durably.
- Removing or disabling a provider does not erase its durable event history automatically, because
  doing so would allow an old 100% event to repeat when the provider is re-enabled. A genuine reset
  still re-arms the next period.

## Tests

Focused xUnit tests under `tests/AiUsageTray.Tests/Shared/` will cover:

- 70% and 90% never emitting notifications;
- first arrival at 100% emitting exactly once;
- repeated refreshes at or above 100% remaining silent;
- reconstructing `NotificationService` from the same isolated app-data root remaining silent;
- a reset emitting once and repeated reset-period reads remaining silent;
- a reset re-arming the next period's 100% event;
- providers without reset times using the cliff-drop reset signal without repeats;
- disabled provider notifications remaining silent;
- malformed state recovering with a no-notification baseline;
- a failed durable write suppressing an otherwise eligible event.

All state-file tests use the repository's `IsolatedAppData` collection so they never touch the real
user profile. Verification includes the focused notification tests followed by the full Release
build and test suite.

## Documentation impact

`docs/ARCHITECTURE.md` will describe the durable notification-state boundary and event ordering.
`docs/USER-GUIDE.md` will replace configurable 70/90/100 threshold language with the fixed 100% and
reset-only behavior, including deduplication across restarts. Provider integration contracts do not
change.

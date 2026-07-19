# Durable Notification Deduplication Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Notify only when a provider quota window reaches 100% or genuinely resets, with at-most-once delivery preserved across refreshes and application restarts.

**Architecture:** Keep event policy in `NotificationService` and add a focused `NotificationStateStore` backed by an atomic JSON file under app config. The service records event markers durably before raising balloons; persistence failure leaves events pending and silent until a later successful write.

**Tech Stack:** .NET 8, C#, WPF, `System.Text.Json`, existing `AppPaths`/`AtomicFile`/`AppLog`, xUnit with `IsolatedAppData`.

---

## File map

- Modify `src/AiUsageTray/Infrastructure/AppPaths.cs`: expose the notification-state path.
- Create `src/AiUsageTray/Services/NotificationStateStore.cs`: JSON state models, recovery, cloning, and atomic persistence.
- Modify `src/AiUsageTray/Services/NotificationService.cs`: fixed-100 policy, reset detection, locking, pending delivery, and durable deduplication.
- Modify `src/AiUsageTray/Models/SettingsModels.cs`: keep notification enablement and remove configurable percentages.
- Create `tests/AiUsageTray.Tests/Shared/NotificationStateStoreTests.cs`: persistence and corruption tests.
- Modify `tests/AiUsageTray.Tests/Shared/NotificationServiceTests.cs`: policy and restart regression tests.
- Modify `docs/ARCHITECTURE.md` and `docs/USER-GUIDE.md`: persistence boundary and visible behavior.

### Task 1: Add the durable notification state store

**Files:**
- Modify: `src/AiUsageTray/Infrastructure/AppPaths.cs:25`
- Create: `src/AiUsageTray/Services/NotificationStateStore.cs`
- Create: `tests/AiUsageTray.Tests/Shared/NotificationStateStoreTests.cs`

- [ ] **Step 1: Write the failing store tests**

Create three `[Collection("IsolatedAppData")]` tests with these exact assertions:

```csharp
[Fact]
public void Load_MissingFile_ReturnsEmptyNormalState()
{
    using var isolated = new IsolatedAppData();
    var result = new NotificationStateStore().Load();
    Assert.Empty(result.State.Windows);
    Assert.False(result.State.SuppressNotificationsForUnseenWindows);
}

[Fact]
public void TrySave_ThenLoad_RoundTripsWindowState()
{
    using var isolated = new IsolatedAppData();
    var resetsAt = DateTimeOffset.UtcNow.AddHours(3);
    var state = new NotificationStateDocument();
    state.Windows["codex:primary"] = new NotificationWindowState
    {
        ResetsAt = resetsAt,
        LastUsedPercent = 100m,
        LimitReachedNotified = true,
    };
    var store = new NotificationStateStore();
    Assert.True(store.TrySave(state));
    var loaded = store.Load().State.Windows["codex:primary"];
    Assert.Equal(resetsAt, loaded.ResetsAt);
    Assert.Equal(100m, loaded.LastUsedPercent);
    Assert.True(loaded.LimitReachedNotified);
}

[Fact]
public void Load_MalformedFile_RecoversWithConservativeBaselineMode()
{
    using var isolated = new IsolatedAppData();
    File.WriteAllText(AppPaths.NotificationStateFile, "{not-json");
    var result = new NotificationStateStore().Load();
    Assert.Empty(result.State.Windows);
    Assert.True(result.State.SuppressNotificationsForUnseenWindows);
    Assert.NotEmpty(Directory.GetFiles(AppPaths.BackupsDir, "notification-state*.bak"));
}
```

Use `System.IO`, `AiUsageTray.Infrastructure`, `AiUsageTray.Services`, and the existing `IsolatedAppData` test support.

- [ ] **Step 2: Run the store tests and verify RED**

```powershell
dotnet test tests/AiUsageTray.Tests/AiUsageTray.Tests.csproj -c Release --filter FullyQualifiedName~NotificationStateStoreTests
```

Expected: compilation fails because the state store types and `AppPaths.NotificationStateFile` do not exist.

- [ ] **Step 3: Add the centralized path**

Add beside `SettingsFile` in `AppPaths.cs`:

```csharp
public static string NotificationStateFile => Path.Combine(ConfigDir, "notification-state.json");
```

- [ ] **Step 4: Implement the state types and store**

Create these internal types in `NotificationStateStore.cs`:

```csharp
internal interface INotificationStateStore
{
    NotificationStateLoadResult Load();
    bool TrySave(NotificationStateDocument state);
}

internal sealed record NotificationStateLoadResult(NotificationStateDocument State);

internal sealed class NotificationStateDocument
{
    public int SchemaVersion { get; set; } = 1;
    public bool SuppressNotificationsForUnseenWindows { get; set; }
    public Dictionary<string, NotificationWindowState> Windows { get; set; } = new();

    public NotificationStateDocument Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        SuppressNotificationsForUnseenWindows = SuppressNotificationsForUnseenWindows,
        Windows = Windows.ToDictionary(
            pair => pair.Key,
            pair => new NotificationWindowState
            {
                ResetsAt = pair.Value.ResetsAt,
                LastUsedPercent = pair.Value.LastUsedPercent,
                LimitReachedNotified = pair.Value.LimitReachedNotified,
            },
            StringComparer.Ordinal),
    };
}

internal sealed class NotificationWindowState
{
    public DateTimeOffset? ResetsAt { get; set; }
    public decimal? LastUsedPercent { get; set; }
    public bool LimitReachedNotified { get; set; }
}
```

Implement `NotificationStateStore.Load()` as follows: missing/blank file returns a normal empty document; valid JSON is deserialized with case-insensitive property matching; malformed JSON is backed up with `AtomicFile.CreateTimestampedBackup`, logged without raw content, replaced by an empty document whose `SuppressNotificationsForUnseenWindows` is `true`, and returned. Implement `TrySave` by serializing indented JSON and calling `AtomicFile.WriteAllText`; catch/log any exception and return `false`. Normalize a deserialized null `Windows` collection without suppressing nullable warnings.

- [ ] **Step 5: Run the focused store tests and verify GREEN**

Run the Step 2 command again. Expected: 3 passing tests and zero failures.

- [ ] **Step 6: Commit the store**

```powershell
git add src/AiUsageTray/Infrastructure/AppPaths.cs src/AiUsageTray/Services/NotificationStateStore.cs tests/AiUsageTray.Tests/Shared/NotificationStateStoreTests.cs
git commit -m "feat: persist notification delivery state"
```

### Task 2: Enforce 100%-only and restart-safe event delivery

**Files:**
- Modify: `src/AiUsageTray/Models/SettingsModels.cs:17-22`
- Modify: `src/AiUsageTray/Services/NotificationService.cs:1-124`
- Modify: `tests/AiUsageTray.Tests/Shared/NotificationServiceTests.cs:1-164`

- [ ] **Step 1: Write failing fixed-limit tests**

Replace the current 70/90 threshold tests with:

```csharp
[Theory]
[InlineData(70)]
[InlineData(90)]
[InlineData(99.9)]
public void Evaluate_BelowOneHundred_NeverFiresLimitNotification(decimal usage)
{
    using var isolated = new IsolatedAppData();
    var service = new NotificationService(new SettingsService());
    var fired = new List<NotificationEvent>();
    service.NotificationRequested += fired.Add;
    service.Evaluate(MakeSnapshot(usage, DateTimeOffset.UtcNow.AddHours(3)));
    Assert.Empty(fired);
}

[Fact]
public void Evaluate_ReachesOneHundred_FiresLimitOnce()
{
    using var isolated = new IsolatedAppData();
    var service = new NotificationService(new SettingsService());
    var fired = new List<NotificationEvent>();
    service.NotificationRequested += fired.Add;
    var resetsAt = DateTimeOffset.UtcNow.AddHours(3);
    service.Evaluate(MakeSnapshot(99, resetsAt));
    service.Evaluate(MakeSnapshot(100, resetsAt));
    service.Evaluate(MakeSnapshot(100, resetsAt));
    Assert.Contains("reached 100%", Assert.Single(fired).Message);
}
```

- [ ] **Step 2: Write failing restart/reset tests**

Add separate tests that: construct a service, emit at 100%, reconstruct a service against the same isolated root, and assert no repeat; emit a reset after reconstructing, reconstruct again and assert the reset does not repeat; then raise the new period to 100% and assert one re-armed limit event. Tighten the no-reset-time cliff test by evaluating the low reading twice and asserting exactly one reset event.

Use this restart skeleton so the persisted boundary is exercised instead of a mock:

```csharp
var first = new NotificationService(new SettingsService());
first.Evaluate(MakeSnapshot(100, resetsAt));

var restarted = new NotificationService(new SettingsService());
var restartedEvents = new List<NotificationEvent>();
restarted.NotificationRequested += restartedEvents.Add;
restarted.Evaluate(MakeSnapshot(100, resetsAt));
Assert.Empty(restartedEvents);
```

- [ ] **Step 3: Write failing recovery/write-order tests**

Add `Evaluate_CorruptRecoveredState_BaselinesFullWindowWithoutNotification` by writing `"{broken"` to `AppPaths.NotificationStateFile`, constructing the real service, evaluating 100%, and asserting no event.

Add an internal fake store:

```csharp
private sealed class ControllableStateStore : INotificationStateStore
{
    public bool AllowSave { get; set; }
    public NotificationStateDocument State { get; private set; } = new();
    public NotificationStateLoadResult Load() => new(State.Clone());
    public bool TrySave(NotificationStateDocument state)
    {
        if (!AllowSave) return false;
        State = state.Clone();
        return true;
    }
}
```

In `Evaluate_DurableWriteFails_DelaysEventUntilWriteSucceeds`, evaluate 100% while `AllowSave` is false and assert silence; set it true, evaluate the same snapshot, and assert exactly one event.

- [ ] **Step 4: Run notification tests and verify RED**

```powershell
dotnet test tests/AiUsageTray.Tests/AiUsageTray.Tests.csproj -c Release --filter FullyQualifiedName~NotificationServiceTests
```

Expected: lower percentages still emit, restart repeats, and the injectable store constructor is absent.

- [ ] **Step 5: Remove the percentage list from settings**

Keep compatibility with the existing `Notifications` JSON property while reducing its type to:

```csharp
public sealed class NotificationThresholds
{
    public bool Enabled { get; set; } = true;
}
```

Legacy `Percentages` fields are ignored on deserialization and disappear on the next settings save.

- [ ] **Step 6: Implement locked, durable event evaluation**

Give `NotificationService` a fixed `LimitReachedPercent = 100m`, a synchronization object, `INotificationStateStore`, loaded `NotificationStateDocument`, `_hasUnpersistedChanges`, and `_pendingEvents`. Keep the public constructor and add an internal injectable constructor for tests:

```csharp
public NotificationService(SettingsService settingsService)
    : this(settingsService, new NotificationStateStore()) { }

internal NotificationService(SettingsService settingsService, INotificationStateStore stateStore)
{
    _settingsService = settingsService;
    _stateStore = stateStore;
    _state = stateStore.Load().State;
}
```

Under the lock, evaluate every known percentage window using key `providerId:windowId`. For an unseen window, record the baseline; if conservative recovery is active and usage is already 100%, set `LimitReachedNotified` without queuing an event. Otherwise queue a fixed-100 event and mark it delivered in the candidate state.

For existing windows, retain these exact reset predicates:

```csharp
var resetTimeChanged = state.ResetsAt != window.ResetsAt;
var meaningfulTimedReset = resetTimeChanged &&
    state.ResetsAt is not null &&
    state.LastUsedPercent is { } priorTimedUsage &&
    priorTimedUsage >= ResetNotifyMinimumPriorUsedPercent &&
    used < priorTimedUsage;
var meaningfulCliffReset = !resetTimeChanged &&
    state.LastUsedPercent is { } priorUsage &&
    priorUsage >= ResetNotifyMinimumPriorUsedPercent &&
    priorUsage - used >= ResetNotifyMinimumDropPercent;
```

On either reset signal, replace that window's state so the new period is re-armed; queue a reset event only when the corresponding meaningful predicate is true. Then queue a limit event only when `used >= 100m && !state.LimitReachedNotified`.

Persist `_state.Clone()` before taking pending events. On failure, return no events while retaining pending markers in memory for retry. On success, clear `_hasUnpersistedChanges`, copy and clear `_pendingEvents`, leave the lock, and only then invoke subscribers. This ensures callbacks cannot deadlock state evaluation and a crash after persistence cannot replay the event.

Delete the threshold loop, `NotifiedThresholds`, the old reset-notify helpers, and unused `ResetProvider`. Preserve `FormatReset` and existing message wording, substituting fixed `100%`.

- [ ] **Step 7: Run notification tests and verify GREEN**

Run the Step 4 command again. Expected: every `NotificationServiceTests` case passes.

- [ ] **Step 8: Run all shared subsystem tests**

```powershell
dotnet test tests/AiUsageTray.Tests/AiUsageTray.Tests.csproj -c Release --filter FullyQualifiedName~AiUsageTray.Tests.Shared
```

Expected: all shared tests pass with zero failures.

- [ ] **Step 9: Commit the policy**

```powershell
git add src/AiUsageTray/Models/SettingsModels.cs src/AiUsageTray/Services/NotificationService.cs tests/AiUsageTray.Tests/Shared/NotificationServiceTests.cs
git commit -m "feat: notify only on full usage or reset"
```

### Task 3: Update documentation and verify the complete change

**Files:**
- Modify: `docs/ARCHITECTURE.md`
- Modify: `docs/USER-GUIDE.md`

- [ ] **Step 1: Update architecture documentation**

Document that `NotificationService` persists provider/window period markers to `config/notification-state.json` with `AtomicFile`, writes markers before delivery, and fails closed when persistence is unavailable. State that the file contains no secrets, provider payloads, prompt content, or transcripts.

- [ ] **Step 2: Update user-visible documentation**

Replace the configurable “default 70/90/100” wording with:

```markdown
- **Notifications** — a Windows notification appears only when an AI quota window reaches 100% or
  when that window genuinely resets. Each event appears once, including across app restarts.
```

Update the Notifications section to use a 100% example, retain meaningful-reset safeguards, and explicitly state that refreshes, cached reads, and restarts do not replay delivered events.

- [ ] **Step 3: Inspect formatting, scope, and privacy**

```powershell
git diff --check
git diff -- src/AiUsageTray tests/AiUsageTray.Tests docs/ARCHITECTURE.md docs/USER-GUIDE.md
```

Expected: no whitespace errors, no credentials or user-specific paths, and no unrelated changes.

- [ ] **Step 4: Run fresh Release verification**

```powershell
dotnet restore AiUsageTray.sln
dotnet build AiUsageTray.sln -c Release --no-restore
dotnet test AiUsageTray.sln -c Release --no-build --no-restore
```

Expected: every command exits 0; build has no errors; all tests pass with zero failures. Do not run live providers, alter CLI settings, authenticate GitHub, or send prompts.

- [ ] **Step 5: Check every approved requirement against evidence**

Confirm from the fresh output and final diff: values below 100% are silent; 100% emits once; reset emits once; restart/repeat evaluations replay neither; reset re-arms the next period; disabled providers remain silent; persistence failures stay provider-local; state contains no sensitive data.

- [ ] **Step 6: Commit documentation**

```powershell
git add docs/ARCHITECTURE.md docs/USER-GUIDE.md
git commit -m "docs: describe durable notification policy"
```

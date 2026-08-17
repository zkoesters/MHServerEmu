# PostgreSQL PR 1C Leaderboards and Activation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add atomic SQLite/PostgreSQL leaderboard persistence, remove leaderboard persistence singletons, and safely activate the complete PostgreSQL runtime profile.

**Architecture:** A provider-neutral `ILeaderboardStore` exposes schedule, lifecycle, score, visibility, and reward transactions. SQLite implements it over the unchanged schema-v1 file; PostgreSQL implements it over migration `0003` and the existing bounded fenced executor. `LeaderboardService` owns non-static runtime objects, while an async `PersistenceRuntime` owns all provider capabilities and provider lifetime through controlled shutdown.

**Tech Stack:** .NET 8, C# 12, xUnit 2.4.2, Dapper, System.Data.SQLite, Npgsql 10.0.3, PostgreSQL 16/17, GitHub Actions service containers

---

## Preconditions

Execute in the isolated worktree on `phase1c-postgresql-activation` at commit `1fec7049e` or later. Read:

- `docs/superpowers/specs/2026-08-16-postgresql-pr1c-leaderboards-activation-design.md`

Establish a clean baseline:

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet restore MHServerEmu.sln
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet build MHServerEmu.sln --configuration Release --no-restore
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test MHServerEmu.sln --configuration Release --no-build
```

Expected: zero build warnings/errors and 622 passed tests, with 47 PostgreSQL integration tests skipped when no administrative connection string is configured.

Do not modify PostgreSQL migrations `0001` or `0002`, SQLite leaderboard initialization SQL, SQLite `user_version = 1`, existing JSON/SQLite file names, wire protocol messages, null/zero sentinel behavior, or the documented at-least-once reward-grant contract. Do not globally enable SQLite foreign keys, rewrite existing SQLite rows, add an importer/downgrade path, add production containers, or include the forward-port rehearsal. PostgreSQL support remains fresh-install-only and single-writer.

Every task commit is a buildable checkpoint. Immediately before each commit, run:

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet build MHServerEmu.sln --configuration Release -p:Platform=x64
```

Expected: zero warnings and zero errors. Do not remove a compatibility entry point until the task also migrates its final production caller.

## File Responsibility Map

### Shared Database Access

- `src/MHServerEmu.DatabaseAccess/Capabilities/ILeaderboardStore.cs`: compound provider-neutral leaderboard contract.
- `src/MHServerEmu.DatabaseAccess/Capabilities/*Result.cs`: stable operation outcomes.
- `src/MHServerEmu.DatabaseAccess/Models/Leaderboards/*`: immutable request/snapshot DTOs and existing database rows.
- `src/MHServerEmu.DatabaseAccess/Persistence/PersistenceServices.cs`: four capability bundle.
- `src/MHServerEmu.DatabaseAccess/Persistence/PersistenceRuntime.cs`: capability and async-lifetime owner.

### Provider Implementations

- `src/MHServerEmu.DatabaseAccess/SQLite/SQLiteLeaderboardDBManager.cs`: instance-owned SQLite compound store over unchanged schema.
- `src/MHServerEmu.DatabaseAccess.PostgreSQL/Migrations/0003_LeaderboardPersistence.sql`: complete PostgreSQL leaderboard schema.
- `src/MHServerEmu.DatabaseAccess.PostgreSQL/Stores/PostgreSQLLeaderboardStore.cs`: bounded fenced PostgreSQL implementation.
- `src/MHServerEmu.DatabaseAccess.PostgreSQL/PostgreSQLPersistenceFacade.cs`: public four-capability bootstrap/lifetime facade.

### Runtime

- `src/MHServerEmu.Leaderboards/LeaderboardService.cs`: owns runtime database, reward manager, and mailbox.
- `src/MHServerEmu.Leaderboards/LeaderboardDatabase.cs`: schedule reconciliation and runtime collection.
- `src/MHServerEmu.Leaderboards/Leaderboard.cs`: definition lifecycle coordinator.
- `src/MHServerEmu.Leaderboards/LeaderboardInstance.cs`: score, ranking, lifecycle, and reward computation.
- `src/MHServerEmu.Leaderboards/LeaderboardRewardManager.cs`: pending delivery, conditional finalization, and retry.
- `src/MHServerEmu.Leaderboards/Administration/*`: mailbox-backed command facade.

### Application Lifetime

- `src/MHServerEmu/Persistence/PersistenceComposition.cs`: async provider selection.
- `src/MHServerEmu/ServerApp.cs`: async bootstrap, service ownership, shutdown signal, and disposal.
- `src/MHServerEmu.Core/Network/ServerManager.cs`: service startup fault detection and partial shutdown.
- `src/MHServerEmu/Program.cs`: async entry point.

## Task 1: Add Leaderboard Contracts and Immutable DTOs

**Files:**
- Create: `src/MHServerEmu.DatabaseAccess/Capabilities/ILeaderboardStore.cs`
- Create: `src/MHServerEmu.DatabaseAccess/Capabilities/LeaderboardStoreResult.cs`
- Create: `src/MHServerEmu.DatabaseAccess/Capabilities/RewardFinalizationResult.cs`
- Create: `src/MHServerEmu.DatabaseAccess/Persistence/PersistenceFatalFailure.cs`
- Create: `src/MHServerEmu.DatabaseAccess/Models/Leaderboards/LeaderboardReconciliation.cs`
- Create: `src/MHServerEmu.DatabaseAccess/Models/Leaderboards/LeaderboardSnapshot.cs`
- Create: `src/MHServerEmu.DatabaseAccess/Models/Leaderboards/LeaderboardActivation.cs`
- Create: `src/MHServerEmu.DatabaseAccess/Models/Leaderboards/LeaderboardScoreBatch.cs`
- Create: `src/MHServerEmu.DatabaseAccess/Models/Leaderboards/LeaderboardExpiration.cs`
- Create: `src/MHServerEmu.DatabaseAccess/Models/Leaderboards/LeaderboardRotation.cs`
- Create: `src/MHServerEmu.DatabaseAccess/Models/Leaderboards/LeaderboardVisibilityRequest.cs`
- Create: `src/MHServerEmu.DatabaseAccess/Models/Leaderboards/LeaderboardVisibilitySnapshot.cs`
- Create: `src/MHServerEmu.DatabaseAccess/Models/Leaderboards/LeaderboardRewardGeneration.cs`
- Create: `src/MHServerEmu.DatabaseAccess/Models/Leaderboards/LeaderboardRewardKey.cs`
- Create: `src/MHServerEmu.DatabaseAccess/Models/Leaderboards/LeaderboardStoreRecords.cs`
- Create: `src/MHServerEmu.DatabaseAccess/Models/Leaderboards/LeaderboardInstanceIdGenerator.cs`
- Test: `src/MHServerEmu.DatabaseAccess.Tests/Capabilities/LeaderboardStoreContractTests.cs`
- Test: `src/MHServerEmu.DatabaseAccess.Tests/Models/LeaderboardStoreDtoTests.cs`

- [ ] **Step 1: Write failing contract and DTO validation tests**

```csharp
[Fact]
public void LeaderboardScoreBatch_DefensivelyCopiesEntries()
{
    DBLeaderboardEntry entry = TestEntry(1, 2, 3, new byte[] { 4, 5 });
    List<DBLeaderboardEntry> entries = new() { entry };
    LeaderboardScoreBatch batch = new(10, 11, LeaderboardState.eLBS_Active, entries);
    entries.Clear();
    entry.Score = 99;
    entry.RuleStates[0] = 99;
    Assert.Equal(3, batch.Entries.Single().Score);
    Assert.Equal(new byte[] { 4, 5 }, batch.Entries.Single().RuleStates);
}

[Fact]
public void VisibilityRequest_RetentionAbovePaginationLimit_IsAllowed()
{
    LeaderboardVisibilityRequest request = new(1, archiveLimit: 1000, currentTime: 100);
    Assert.Equal(1000, request.ArchiveLimit);
}

[Fact]
public void NextInstanceId_LowerCounterOverflow_ReturnsFalse()
{
    long leaderboardId = unchecked((long)0xABCDEF1200000042UL);
    long existing = unchecked((long)0xABCDEF12FFFFFFFFUL);
    Assert.False(LeaderboardInstanceIdGenerator.TryGetNext(leaderboardId, new[] { existing }, out _));
}
```

- [ ] **Step 2: Run focused tests and verify missing-type failures**

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Release -p:Platform=x64 --filter "FullyQualifiedName~LeaderboardStoreContractTests|FullyQualifiedName~LeaderboardStoreDtoTests"
```

Expected: FAIL because the leaderboard capability and DTOs do not exist.

- [ ] **Step 3: Add exact enums and interface**

```csharp
public enum LeaderboardStoreResult
{
    Success, NotFound, Conflict, StaleState, InvalidData, Failed, OutcomeUncertain
}

public enum RewardFinalizationResult
{
    Finalized, AlreadyFinalized, NotFound, Failed, OutcomeUncertain
}

public interface ILeaderboardStore
{
    LeaderboardStoreResult Initialize();
    LeaderboardStoreResult ReconcileSchedule(LeaderboardReconciliation request, out LeaderboardSnapshot snapshot);
    LeaderboardStoreResult LoadEntries(long instanceId, out IReadOnlyList<DBLeaderboardEntry> entries);
    LeaderboardStoreResult LoadInstance(long leaderboardId, long instanceId, out DBLeaderboardInstance instance);
    LeaderboardStoreResult LoadVisibleInstances(long leaderboardId, long beforeInstanceId, int limit, out IReadOnlyList<DBLeaderboardInstance> instances);
    LeaderboardStoreResult ActivateInstance(LeaderboardActivation request);
    LeaderboardStoreResult SaveScoreBatch(LeaderboardScoreBatch request);
    LeaderboardStoreResult ExpireInstance(LeaderboardExpiration request);
    LeaderboardStoreResult RotateActiveInstance(LeaderboardRotation request, out DBLeaderboardInstance committedInstance);
    LeaderboardStoreResult MaintainVisibility(LeaderboardVisibilityRequest request, out LeaderboardVisibilitySnapshot snapshot);
    LeaderboardStoreResult GenerateRewards(LeaderboardRewardGeneration request);
    LeaderboardStoreResult GetPendingRewards(long participantId, out IReadOnlyList<DBRewardEntry> rewards);
    RewardFinalizationResult FinalizeReward(LeaderboardRewardKey key, long rewardedDate);
}
```

`PersistenceFatalFailure` contains only `Code` and `Operation` strings and rejects null/blank values.

- [ ] **Step 4: Add immutable request and output DTOs**

Use constructor-only scalar properties. Request and composite snapshot DTOs deep-clone mutable `DB*` rows, including `RuleStates`, on input and never expose their owned copies directly; any returned row view is detached. Define `LeaderboardRewardKey` exactly once:

```csharp
public sealed record LeaderboardActivation(long LeaderboardId, long ExpectedActiveInstanceId, long InstanceId, LeaderboardState ExpectedState);
public readonly record struct LeaderboardRewardKey(long LeaderboardId, long InstanceId, long ParticipantId);
```

Use this definitive field table:

| DTO | Fields |
| --- | --- |
| `LeaderboardDefinitionSpec` | `LeaderboardId`, `PrototypeName`, `IsEnabled`, `StartTime`, `MaxResetCount` |
| `LeaderboardInstanceSpec` | `InstanceId`, `LeaderboardId`, `State`, `ActivationDate`, `Visible` |
| `LeaderboardEntryWrite` | `InstanceId`, `ParticipantId`, `Score`, `HighScore`, owned `RuleStates` bytes |
| `LeaderboardMetaMapping` | `LeaderboardId`, `InstanceId`, `SubLeaderboardId`, `SubInstanceId` |
| `LeaderboardRewardWrite` | `LeaderboardId`, `InstanceId`, `RewardId`, `ParticipantId`, `Rank`, `CreationDate`; the store always inserts it pending |
| `LeaderboardReconciliation` | desired definitions, initial instances, complete meta topology, `CurrentTime`, `NormalArchiveLimit` |
| `LeaderboardSnapshot` | committed definitions, active/nonterminal instances, bounded normal archive instances, required meta mappings |
| `LeaderboardActivation` | `LeaderboardId`, `ExpectedActiveInstanceId`, `InstanceId`, `ExpectedState` |
| `LeaderboardScoreBatch` | `LeaderboardId`, `InstanceId`, `ExpectedState`, complete dirty entries |
| `LeaderboardExpiration` | `LeaderboardId`, `ExpectedActiveInstanceId`, `InstanceId`, `ExpectedState`, complete final dirty entries |
| `LeaderboardRotation` | `LeaderboardId`, expected active pointer/state, requested previous state, complete next instance, requested next state, new meta mappings |
| `LeaderboardVisibilityRequest` | `LeaderboardId`, configured non-negative `ArchiveLimit`, `CurrentTime` |
| `LeaderboardVisibilitySnapshot` | bounded normal archive instances and required meta mappings |
| `LeaderboardRewardGeneration` | `LeaderboardId`, `ExpectedActiveInstanceId`, `InstanceId`, expected `Expired` state, complete rewards; the store hard-codes `Rewarded` and pending rewarded dates |

All `ILeaderboardStore` output collections are non-null. Every failure path assigns an empty collection/snapshot rather than a partial result. `LoadEntries`/`LoadInstance` return `NotFound` for a missing instance; an existing empty instance and no pending rewards return `Success` with empty collections. `LoadVisibleInstances` validates limit `1..100`; cursor zero starts newest and later cursors are exclusive.

`LeaderboardInstanceIdGenerator` uses unsigned bit patterns: initial `(id & 0xFFFFFFFF00000000UL) | 1`; next preserves upper bits and increments greatest lower counter. Reject upper-bit mismatch, collision, or `0xFFFFFFFF` overflow.

- [ ] **Step 5: Run focused contract tests**

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Release -p:Platform=x64 --filter "FullyQualifiedName~LeaderboardStoreContractTests|FullyQualifiedName~LeaderboardStoreDtoTests"
```

Expected: PASS for deep defensive copies, empty failure outputs, IDs, pagination, duplicate reward participants, and high-bit values.

- [ ] **Step 6: Commit shared contracts**

```bash
git add src/MHServerEmu.DatabaseAccess src/MHServerEmu.DatabaseAccess.Tests
git commit -m "feat(persistence): add leaderboard store contracts"
```

## Task 2: Add Account Reconciliation and PR 1B Uncertainty Recovery

**Files:**
- Modify: `src/MHServerEmu.DatabaseAccess/Capabilities/IAccountStore.cs`
- Modify: `src/MHServerEmu.DatabaseAccess.PostgreSQL/PostgreSQLAccountStore.cs`
- Modify: `src/MHServerEmu.DatabaseAccess/SQLite/SQLiteDBManager.cs`
- Modify: `src/MHServerEmu.DatabaseAccess/Json/JsonDBManager.cs`
- Modify: `src/MHServerEmu.PlayerManagement/Players/AccountManager.cs`
- Modify: `src/MHServerEmu.PlayerManagement.Tests/StubDBManager.cs`
- Modify: `src/MHServerEmu.DatabaseAccess.Tests/Persistence/PersistenceServicesTests.cs`
- Test: `src/MHServerEmu.DatabaseAccess.Tests/Conformance/AccountStoreConformanceTests.cs`
- Test: `src/MHServerEmu.DatabaseAccess.Tests/PostgreSQL/Stores/PostgreSQLAccountStoreTests.cs`
- Test: `src/MHServerEmu.DatabaseAccess.Tests/SQLite/SQLiteDBManagerTests.cs`
- Test: `src/MHServerEmu.PlayerManagement.Tests/AccountManagerTests.cs`

- [ ] **Step 1: Write failing reconciliation tests**

```csharp
[Fact]
public void ReconcileAccount_UncertainObject_ReplacesScalarsButPreservesPlayerAggregate()
{
    DBAccount account = InsertAndCloneAccount();
    DBPlayer player = account.Player;
    account.PersistenceState = PersistenceState.OutcomeUncertain;
    UpdatePersistedFlags(account.Id, AccountFlags.IsBanned);

    Assert.Equal(AccountStoreResult.Success, _store.ReconcileAccount(account));
    Assert.Same(player, account.Player);
    Assert.Equal(AccountFlags.IsBanned, account.Flags);
    Assert.Equal(PersistenceState.Clean, account.PersistenceState);
}
```

- [ ] **Step 2: Run account tests and verify interface failure**

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Release -p:Platform=x64 --filter FullyQualifiedName~AccountStore
```

Expected: FAIL because `ReconcileAccount` is absent.

- [ ] **Step 3: Switch all account implementations and fakes together**

Add exactly:

```csharp
AccountStoreResult ReconcileAccount(DBAccount account);
```

PostgreSQL uses its bounded read executor by immutable ID. SQLite queries by ID. JSON accepts only its configured default account. Copy email/name, credentials, user level, flags, credential metadata, security versions, revision, verification/audit timestamps, then clear uncertain state. Never replace `Player`, entity collections, transfer/migration data, or locks.

- [ ] **Step 4: Reconcile before a later explicit mutation**

In `AccountManager`, when a supplied account is uncertain, call reconciliation before checking current flags/level. Failed reconciliation returns `DatabaseError`. Do not replay the operation that originally became uncertain and do not notify on reconciliation.

- [ ] **Step 5: Run account conformance and caller tests**

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Release -p:Platform=x64 --filter FullyQualifiedName~AccountStore
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.PlayerManagement.Tests/MHServerEmu.PlayerManagement.Tests.csproj --configuration Release -p:Platform=x64 --filter FullyQualifiedName~AccountManagerTests
```

Expected: PASS for clean no-op, missing ID, scalar replacement, aggregate preservation, fail-closed behavior, and no automatic replay/notification.

- [ ] **Step 6: Commit account recovery**

```bash
git add src/MHServerEmu.DatabaseAccess src/MHServerEmu.DatabaseAccess.PostgreSQL src/MHServerEmu.PlayerManagement src/MHServerEmu.DatabaseAccess.Tests src/MHServerEmu.PlayerManagement.Tests
git commit -m "feat(persistence): reconcile uncertain accounts"
```

## Task 3: Implement the Instance-Owned SQLite Leaderboard Store

**Files:**
- Modify: `src/MHServerEmu.DatabaseAccess/SQLite/SQLiteLeaderboardDBManager.cs`
- Verify unchanged: `src/MHServerEmu.DatabaseAccess/SQLite/Scripts/InitializeLeaderboardsDatabase.sql`
- Create: `src/MHServerEmu.DatabaseAccess.Tests/Conformance/LeaderboardStoreTestData.cs`
- Create: `src/MHServerEmu.DatabaseAccess.Tests/Conformance/LeaderboardStoreConformanceTests.cs`
- Create: `src/MHServerEmu.DatabaseAccess.Tests/SQLite/SQLiteLeaderboardStoreTestFixture.cs`
- Create: `src/MHServerEmu.DatabaseAccess.Tests/SQLite/SQLiteLeaderboardStoreTests.cs`
- Create: `src/MHServerEmu.DatabaseAccess.Tests/SQLite/SQLiteLeaderboardSchemaGoldenTests.cs`

- [ ] **Step 1: Write failing initialization and load tests**

```csharp
[Fact]
public void Constructor_DoesNotCreateDatabaseFile()
{
    using TemporaryDirectory directory = new();
    _ = new SQLiteLeaderboardDBManager(Path.Combine(directory.Path, "Leaderboards.db"));
    Assert.Empty(directory.GetFiles());
}

[Fact]
public void LoadVisibleInstances_UsesExclusiveBoundedCursor()
{
    using SQLiteLeaderboardStoreTestFixture fixture = CreateFixtureWithVisibleInstances(150);
    Assert.Equal(LeaderboardStoreResult.Success, fixture.Store.LoadVisibleInstances(1, 0, 100, out var first));
    Assert.Equal(100, first.Count);
    Assert.Equal(LeaderboardStoreResult.Success, fixture.Store.LoadVisibleInstances(1, first[^1].InstanceId, 100, out var second));
    Assert.Equal(50, second.Count);
}
```

- [ ] **Step 2: Run SQLite leaderboard tests and verify missing-interface failures**

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Release -p:Platform=x64 --filter "FullyQualifiedName~SQLiteLeaderboard|FullyQualifiedName~LeaderboardStoreConformance"
```

Expected: FAIL because the CRUD manager does not implement the compound interface or lazy constructor.

- [ ] **Step 3: Implement initialization and bounded loads**

Add a public constructor that accepts the configured file path but performs no file I/O. Keep the temporary `Instance` compatibility entry point until Task 10 so intermediate commits compile. `Initialize` creates the parent/file/schema only when called, validates an existing file's `user_version == 1`, and returns `Failed` without rewriting incompatible files. Implement exact/entry/visible loads with detached rows, empty failure outputs, and bounded exclusive pagination.

- [ ] **Step 4: Run initialization/load tests, then write failing reconciliation tests**

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Release -p:Platform=x64 --filter FullyQualifiedName~SQLiteLeaderboardStoreTests
```

Add fresh insert, no-op replay, update, disable/re-enable, zero activation-date, deterministic initial ID, overflow, collision, and atomic rollback tests.

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Release -p:Platform=x64 --filter FullyQualifiedName~SQLiteLeaderboardStoreTests
```

Expected: existing initialization/load tests pass and new reconciliation tests fail.

- [ ] **Step 5: Implement reconciliation and return a bounded committed snapshot**

Use one transaction over the unchanged schema. Validate the complete request before writes. Process definitions by unsigned ID order, deterministically create required initial instances/meta mappings, and atomically update active pointers. Return active/nonterminal instances plus no more than the requested normal archive limit while retaining reward-bearing visibility.

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Release -p:Platform=x64 --filter FullyQualifiedName~SQLiteLeaderboardStoreTests
```

Expected: reconciliation tests pass.

- [ ] **Step 6: Run reconciliation tests, then write failing score/lifecycle tests**

```csharp
[Fact]
public void ExpireInstance_StalePointer_RollsBackFinalEntries()
{
    using SQLiteLeaderboardStoreTestFixture fixture = CreateStartedFixture();
    LeaderboardExpiration request = fixture.CreateExpiration(expectedPointer: 999);
    Assert.Equal(LeaderboardStoreResult.StaleState, fixture.Store.ExpireInstance(request));
    Assert.Empty(fixture.ReadEntries(request.InstanceId));
}
```

Cover activation replay, score replay/blob mismatch, high-bit score/participant bit patterns, expiration replay before/after rotation, rotation exact/mismatched replay, and lower-counter overflow.

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Release -p:Platform=x64 --filter FullyQualifiedName~SQLiteLeaderboardStoreTests
```

Expected: earlier groups pass and new score/lifecycle tests fail.

- [ ] **Step 7: Implement score and lifecycle transactions**

Use expected pointer/state predicates. Activation, each score batch, expiration, and rotation are each one transaction. Rotation verifies ownership, creates the next instance/meta mappings, and changes pointer/states atomically. Compare expiration replay against every submitted final row including rule-state bytes.

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Release -p:Platform=x64 --filter FullyQualifiedName~SQLiteLeaderboardStoreTests
```

Expected: score/lifecycle tests pass.

- [ ] **Step 8: Run lifecycle tests, then write failing visibility/reward tests**

```csharp
[Fact]
public void FinalizeReward_DuplicateConfirmation_PreservesTimestamp()
{
    LeaderboardRewardKey key = SeedPendingReward();
    Assert.Equal(RewardFinalizationResult.Finalized, _store.FinalizeReward(key, 100));
    Assert.Equal(RewardFinalizationResult.AlreadyFinalized, _store.FinalizeReward(key, 200));
    Assert.Equal(100, ReadReward(key).RewardedDate);
}
```

Cover normal-window visibility, permanent reward-bearing visibility, exact bidirectional reward replay, duplicate participant rejection, empty pending results, full-key conditional finalization, and no timestamp overwrite.

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Release -p:Platform=x64 --filter FullyQualifiedName~SQLiteLeaderboardStoreTests
```

Expected: earlier groups pass and new visibility/reward tests fail.

- [ ] **Step 9: Implement visibility and reward transactions**

Use existing tables/columns only. `MaintainVisibility` changes flags and returns the bounded normal window from one transaction snapshot. Reward generation compares exact participant/reward/rank sets in both directions. Finalization updates only a pending row matching the complete key.

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Release -p:Platform=x64 --filter FullyQualifiedName~SQLiteLeaderboardStoreTests
```

Expected: visibility/reward tests pass.

- [ ] **Step 10: Lock the SQLite schema and run complete conformance**

Assert exact `sqlite_master` table/index SQL, column lists, and `PRAGMA user_version = 1` before and after operations. Run fresh reconciliation, replay, lifecycle, unsigned score, reward, and pagination scenarios.

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Release -p:Platform=x64 --filter "FullyQualifiedName~SQLiteLeaderboard|FullyQualifiedName~LeaderboardStoreConformance"
```

Expected: PASS with no initialization-script diff.

- [ ] **Step 11: Build and commit SQLite store**

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet build MHServerEmu.sln --configuration Release -p:Platform=x64
git add src/MHServerEmu.DatabaseAccess/SQLite/SQLiteLeaderboardDBManager.cs src/MHServerEmu.DatabaseAccess.Tests
git commit -m "feat(leaderboards): add compound SQLite store"
```

## Task 4: Add PostgreSQL Leaderboard Migration 0003

**Files:**
- Create: `src/MHServerEmu.DatabaseAccess.PostgreSQL/Migrations/0003_LeaderboardPersistence.sql`
- Modify: `src/MHServerEmu.DatabaseAccess.PostgreSQL/MHServerEmu.DatabaseAccess.PostgreSQL.csproj`
- Modify: `src/MHServerEmu.DatabaseAccess.Tests/PostgreSQL/Migrations/PostgreSQLMigrationCatalogTests.cs`
- Modify: `src/MHServerEmu.DatabaseAccess.Tests/PostgreSQL/Migrations/PostgreSQLMigrationRunnerTests.cs`
- Create: `src/MHServerEmu.DatabaseAccess.Tests/PostgreSQL/Migrations/PostgreSQLLeaderboardMigrationTests.cs`

- [ ] **Step 1: Write failing migration catalog/schema tests**

```csharp
[Fact]
public void LoadEmbedded_IncludesLeaderboardMigration()
{
    Assert.Equal(new[] { 1, 2, 3 }, PostgreSQLMigrationCatalog.LoadEmbedded().Migrations.Select(m => m.Version));
}

[PostgreSQLIntegrationFact]
public async Task Migration003_RejectsActiveInstanceOwnedByAnotherLeaderboard()
{
    await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
    await AssertSqlStateAsync("23503", () => fixture.ExecuteAsync(InvalidActivePointerSql));
}
```

- [ ] **Step 2: Run catalog tests and verify migration absence**

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Release -p:Platform=x64 --filter "FullyQualifiedName~PostgreSQLMigrationCatalogTests|FullyQualifiedName~PostgreSQLLeaderboardMigrationTests"
```

Expected: FAIL because version 3 and leaderboard tables are absent.

- [ ] **Step 3: Add exact schema-qualified DDL**

Create `leaderboard`, `leaderboard_instance`, `leaderboard_entry`, `leaderboard_meta_entry`, and `leaderboard_reward` with the approved types/nullability/checks. Add composite ownership keys/FKs, cascades, and deferrable initially-deferred active-pointer FK. Create unqualified index identifiers targeting qualified tables for lifecycle, score, meta, and pending rewards. Include no transaction control or functions.

- [ ] **Step 4: Embed 0003 and renumber test migrations**

```xml
<EmbeddedResource Include="Migrations\0003_LeaderboardPersistence.sql" LogicalName="Migrations.0003_LeaderboardPersistence.sql" />
```

Rename synthetic `0003_Test` resources in migration tests to `0004_Test` and update expected applied counts.

- [ ] **Step 5: Run migration tests live on PostgreSQL 16**

Use the final-validation container recipe from Task 13, export its connection string, then run:

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Release -p:Platform=x64 --filter "FullyQualifiedName~PostgreSQLMigration"
```

Expected: all migrations apply/replay; state, rank, FK, cascade, deferrability, and index tests pass.

- [ ] **Step 6: Commit migration**

```bash
git add src/MHServerEmu.DatabaseAccess.PostgreSQL src/MHServerEmu.DatabaseAccess.Tests/PostgreSQL/Migrations
git commit -m "feat(postgresql): add leaderboard migration"
```

## Task 5: Implement PostgreSQL Leaderboard Store

**Files:**
- Create: `src/MHServerEmu.DatabaseAccess.PostgreSQL/Stores/PostgreSQLLeaderboardStore.cs`
- Modify: `src/MHServerEmu.DatabaseAccess.PostgreSQL/PostgreSQLProvider.cs`
- Modify: `src/MHServerEmu.DatabaseAccess.Tests/PostgreSQL/Stores/PostgreSQLStoreTestFixture.cs`
- Create: `src/MHServerEmu.DatabaseAccess.Tests/PostgreSQL/Stores/PostgreSQLLeaderboardStoreTests.cs`
- Modify: `src/MHServerEmu.DatabaseAccess.Tests/Conformance/LeaderboardStoreConformanceTests.cs`

- [ ] **Step 1: Start a PostgreSQL 16 fixture and write failing initialization/load tests**

```csharp
[PostgreSQLIntegrationFact]
public async Task LoadVisibleInstances_UsesBoundedExclusivePagination()
{
    await using PostgreSQLStoreTestFixture fixture = await StartWithVisibleInstances(150);
    Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.LoadVisibleInstances(1, 0, 100, out var first));
    Assert.Equal(100, first.Count);
    Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.LoadVisibleInstances(1, first[^1].InstanceId, 100, out var second));
    Assert.Equal(50, second.Count);
}
```

- [ ] **Step 2: Run PostgreSQL store tests and verify missing store failure**

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Release -p:Platform=x64 --filter FullyQualifiedName~PostgreSQLLeaderboardStoreTests
```

Expected: FAIL because `PostgreSQLLeaderboardStore` is absent.

- [ ] **Step 3: Implement initialization and bounded reads**

Use `PostgreSQLStoreExecutor` for every operation. Reads have one absolute deadline but no fence. Return detached rows, empty failure outputs, and bounded exclusive pagination. Classify named constraints and SQLSTATE without exception messages or values.

- [ ] **Step 4: Run load tests, then write failing reconciliation tests**

Cover fresh reconciliation, no-op replay, update/disable/re-enable, zero activation date, deterministic initial ID, topology mismatch, collision/overflow, and snapshot bounds.

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Release -p:Platform=x64 --filter FullyQualifiedName~PostgreSQLLeaderboardStoreTests
```

Expected: load tests pass and new reconciliation tests fail.

- [ ] **Step 5: Implement fenced reconciliation**

Use `ReadCommitted`, writer-fence validation, row locks, and expected predicates. Lock definitions in unsigned ID order before computing IDs. Validate before writes and return a bounded committed snapshot.

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Release -p:Platform=x64 --filter FullyQualifiedName~PostgreSQLLeaderboardStoreTests
```

Expected: reconciliation tests pass.

- [ ] **Step 6: Run reconciliation tests, then write failing score/lifecycle tests**

```csharp
[PostgreSQLIntegrationFact]
public async Task SaveScoreBatch_ThousandRows_UsesOneAtomicBatch()
{
    await using PostgreSQLStoreTestFixture fixture = await StartWithActiveInstance();
    LeaderboardScoreBatch batch = fixture.CreateScoreBatch(1000);
    Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.SaveScoreBatch(batch));
    Assert.Equal(1000, fixture.ReadEntries(batch.InstanceId).Count);
}
```

Cover activation replay, set-based 1/1000-row batches, blob mismatch, high-bit values, stale pointer/state rollback, exact and mismatched expiration/rotation replay, and ID overflow.

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Release -p:Platform=x64 --filter FullyQualifiedName~PostgreSQLLeaderboardStoreTests
```

Expected: earlier groups pass and new score/lifecycle tests fail.

- [ ] **Step 7: Implement set-based score and lifecycle operations**

Use typed arrays plus `UNNEST` for score batches. Compare replay including rule-state bytes. Rotation locks definitions before computing IDs. Expiration replay succeeds after rotation only when persisted final rows match. Never delegate unsigned ranking to SQL.

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Release -p:Platform=x64 --filter FullyQualifiedName~PostgreSQLLeaderboardStoreTests
```

Expected: score/lifecycle tests pass.

- [ ] **Step 8: Run lifecycle tests, then write failing visibility/reward tests**

```csharp
[PostgreSQLIntegrationFact]
public async Task GenerateRewards_ReplayRequiresExactBidirectionalSet()
{
    await using PostgreSQLStoreTestFixture fixture = await StartWithExpiredInstance();
    LeaderboardRewardGeneration request = fixture.CreateRewards(participants: new long[] { 1, 2 });
    Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.GenerateRewards(request));
    Assert.Equal(LeaderboardStoreResult.Conflict, fixture.Leaderboards.GenerateRewards(fixture.CreateRewards(participants: new long[] { 1 })));
}
```

Cover normal/reward-bearing visibility, exact reward replay in both directions, duplicate participants, set-based 1/1000-row inserts, pending lookup, and full-key finalization replay.

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Release -p:Platform=x64 --filter FullyQualifiedName~PostgreSQLLeaderboardStoreTests
```

Expected: earlier groups pass and new visibility/reward tests fail.

- [ ] **Step 9: Implement visibility and reward operations**

Use one fenced transaction per write. Use typed arrays plus `UNNEST` for reward batches. Validate duplicate participants before SQL and require exact set equality on replay.

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Release -p:Platform=x64 --filter FullyQualifiedName~PostgreSQLLeaderboardStoreTests
```

Expected: visibility/reward tests pass.

- [ ] **Step 10: Write separate pre-commit and commit-stage connection-loss tests**

For both score and reward writes, terminate the backend before commit begins and assert `Failed`, no fatal callback, and a subsequent operation succeeds through the recovered pool. Separately use a deferred trigger to terminate its own backend during commit and assert `OutcomeUncertain`, exactly one fatal callback, and no stale-runtime retry.

- [ ] **Step 11: Implement failure classification and run all conformance tests**

Classify strictly at the commit-start boundary: a disconnect before commit begins is `Failed`; a commit-stage disconnect is `OutcomeUncertain`. Attempt rollback and connection invalidation only as best-effort cleanup and verify pool recovery; cleanup failure does not change a known pre-commit outcome to uncertain. Run every shared conformance scenario.

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Release -p:Platform=x64 --filter "FullyQualifiedName~PostgreSQLLeaderboardStoreTests|FullyQualifiedName~LeaderboardStoreConformance"
```

Expected: PASS against a live PostgreSQL fixture.

- [ ] **Step 12: Build and commit PostgreSQL store**

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet build MHServerEmu.sln --configuration Release -p:Platform=x64
git add src/MHServerEmu.DatabaseAccess.PostgreSQL src/MHServerEmu.DatabaseAccess.Tests
git commit -m "feat(postgresql): persist leaderboards"
```

## Task 6: Add Owned Persistence Runtime and PostgreSQL Composition

**Files:**
- Create: `src/MHServerEmu.DatabaseAccess/Persistence/PersistenceRuntime.cs`
- Create: `src/MHServerEmu.DatabaseAccess.PostgreSQL/PostgreSQLPersistenceFacade.cs`
- Modify: `src/MHServerEmu.DatabaseAccess/Persistence/PersistenceServices.cs`
- Modify: `src/MHServerEmu.DatabaseAccess.PostgreSQL/PostgreSQLProvider.cs`
- Modify: `src/MHServerEmu/Persistence/PersistenceComposition.cs`
- Modify: `src/MHServerEmu/MHServerEmu.csproj`
- Test: `src/MHServerEmu.DatabaseAccess.Tests/Persistence/PersistenceServicesTests.cs`
- Test: `src/MHServerEmu.DatabaseAccess.Tests/PostgreSQL/PostgreSQLPersistenceFacadeTests.cs`
- Test: `src/MHServerEmu.Tests/Persistence/PersistenceCompositionTests.cs`
- Test: `src/MHServerEmu.Tests/Persistence/PersistenceRuntimeTests.cs`

- [ ] **Step 1: Write failing four-capability/lifetime tests**

```csharp
[Fact]
public async Task PostgreSQLComposition_UsesInjectedFacadeAndNoSQLitePath()
{
    RecordingPostgreSQLFacade facade = new();
    await using PersistenceRuntime runtime = await CreatePostgreSQLRuntime(facade);
    Assert.NotNull(runtime.Services.Leaderboards);
    Assert.Null(facade.ReceivedSQLitePath);
}

[Fact]
public async Task DisposeAsync_IsIdempotent()
{
    PersistenceRuntime runtime = CreateRuntime();
    await runtime.DisposeAsync();
    await runtime.DisposeAsync();
    Assert.Equal(1, DisposalCount);
}

[PostgreSQLIntegrationFact]
public async Task StartAsync_ExposesFourCapabilities()
{
    await using PersistenceRuntime runtime = await _fixture.StartFacadeAsync();
    Assert.NotNull(runtime.Services.Leaderboards);
}
```

Keep the injected composition unit test in `MHServerEmu.Tests`. Put the tagged live bootstrap test in `MHServerEmu.DatabaseAccess.Tests/PostgreSQL/PostgreSQLPersistenceFacadeTests.cs`, using its administrative database fixture and cleanup rather than introducing PostgreSQL fixture dependencies into `MHServerEmu.Tests`.

- [ ] **Step 2: Run composition tests and verify PostgreSQL gate failure**

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.Tests/MHServerEmu.Tests.csproj --configuration Release -p:Platform=x64 --filter FullyQualifiedName~Persistence
```

Expected: FAIL because `PersistenceServices` has three capabilities and PostgreSQL remains rejected.

- [ ] **Step 3: Add four-capability runtime and public facade**

`PersistenceRuntime` owns `PersistenceServices` plus one idempotent async dispose delegate. Public `PostgreSQLPersistenceFacade.StartAsync` accepts validated config/override connection string and `Action<PersistenceFatalFailure>`, starts internal provider, constructs account/player/guild/leaderboard stores, and returns a runtime. Expose no Npgsql/internal types.

- [ ] **Step 4: Add async composition without breaking the current caller**

Add `Leaderboards` to every `PersistenceServices` constructor call/test fake. Add `CreateAsync` while retaining `TryCreate` until `ServerApp` is migrated in Task 11. JSON/SQLite construct lazy instance-owned SQLite leaderboard adapters. PostgreSQL uses the facade and never receives the SQLite path. Add the PostgreSQL project reference to the executable.

- [ ] **Step 5: Test disabled mode, failed startup, second runtime, and disposal**

PostgreSQL with leaderboards disabled still migrates 0003 and opens no SQLite file. Failed provider start disposes partial writer owner/monitor/data source. Second runtime gets stable writer-lock failure. JSON/SQLite do not open leaderboard file until service initialization.

- [ ] **Step 6: Run composition tests and commit composition**

Start PostgreSQL 16 as in Task 13 and set `ADMIN_CONNECTION_STRING`; the tagged facade run must execute without skips.

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.Tests/MHServerEmu.Tests.csproj --configuration Release -p:Platform=x64 --filter FullyQualifiedName~Persistence
MHSERVEREMU_POSTGRESQL_TEST_ADMIN_CONNECTION_STRING="${ADMIN_CONNECTION_STRING}" DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Release -p:Platform=x64 --filter FullyQualifiedName~PostgreSQLPersistenceFacade
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet build MHServerEmu.sln --configuration Release -p:Platform=x64
git add src/MHServerEmu.DatabaseAccess src/MHServerEmu.DatabaseAccess.PostgreSQL src/MHServerEmu src/MHServerEmu.DatabaseAccess.Tests src/MHServerEmu.Tests
git commit -m "feat(postgresql): compose complete persistence"
```

## Task 7: Make Runtime Ownership Explicit and Reconcile Deterministically

**Files:**
- Create: `src/MHServerEmu.Leaderboards/ILeaderboardPlayerNameResolver.cs`
- Create: `src/MHServerEmu.Leaderboards/PlayerStoreLeaderboardNameResolver.cs`
- Create: `src/MHServerEmu.Leaderboards/ILeaderboardPrototypeCatalog.cs`
- Create: `src/MHServerEmu.Leaderboards/GameDatabaseLeaderboardPrototypeCatalog.cs`
- Create: `src/MHServerEmu.Leaderboards/LeaderboardRuntimeOptions.cs`
- Create: `src/MHServerEmu.Leaderboards/LeaderboardScheduleLoader.cs`
- Create: `src/MHServerEmu.Leaderboards/ILeaderboardPublisher.cs`
- Modify: `src/MHServerEmu.Leaderboards/LeaderboardService.cs`
- Modify: `src/MHServerEmu.Leaderboards/LeaderboardDatabase.cs`
- Modify: `src/MHServerEmu/ServerApp.cs`
- Modify: `src/MHServerEmu.Leaderboards/LeaderboardScheduler.cs`
- Modify: `src/MHServerEmu.Leaderboards/Leaderboard.cs`
- Modify: `src/MHServerEmu.Leaderboards/LeaderboardInstance.cs`
- Modify: `src/MHServerEmu.Leaderboards/LeaderboardEntry.cs`
- Modify: `src/MHServerEmu.Leaderboards/MetaLeaderboardEntry.cs`
- Test: `src/MHServerEmu.Tests/Leaderboards/LeaderboardScheduleReconciliationTests.cs`
- Test: `src/MHServerEmu.Tests/Leaderboards/LeaderboardRestartTests.cs`
- Test: `src/MHServerEmu.Tests/Leaderboards/LeaderboardArchiveCacheTests.cs`
- Test helpers: `src/MHServerEmu.Tests/Leaderboards/Fixtures/*.cs`

- [ ] **Step 1: Write failing ownership and schedule tests**

```csharp
[Fact]
public void Initialize_MissingSchedule_GeneratesBeforeStoreContact()
{
    using TemporaryLeaderboardSchedule schedule = new(missing: true);
    RecordingLeaderboardStore store = new();
    LeaderboardDatabase database = CreateDatabase(store, schedule, SyntheticCatalog.OneLeaderboard());
    Assert.True(database.Initialize());
    Assert.True(File.Exists(schedule.Path));
    Assert.Equal("ReconcileSchedule", store.Calls.Single());
}

[Fact]
public void SecondDatabase_HasNoStateFromFirstInstance()
{
    Assert.NotSame(CreateDatabase().GetLeaderboards(), CreateDatabase().GetLeaderboards());
}
```

- [ ] **Step 2: Run runtime tests and verify singleton/constructor failures**

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.Tests/MHServerEmu.Tests.csproj --configuration Release -p:Platform=x64 --filter "FullyQualifiedName~LeaderboardSchedule|FullyQualifiedName~LeaderboardRestart"
```

Expected: FAIL because runtime objects still depend on singletons and hidden writes.

- [ ] **Step 3: Add narrow runtime dependencies and schedule loader**

Create explicit resolver/catalog/publisher/options abstractions. Loader generates a canonical file from live public prototypes only when missing; validates duplicate IDs/names, topology, dates, reset count, and prototype existence before store calls. Schedule JSON owns enabled/start/reset fields; prototypes own identity/topology.

- [ ] **Step 4: Switch leaderboard-service runtime paths away from singletons**

Update `ServerApp` to pass `_persistence.Leaderboards` into `LeaderboardService`. Construct `LeaderboardDatabase` and reward manager inside the service. Pass store/resolver/publisher to definitions, instances, entries, and meta entries. Constructors perform no database writes. Keep the existing static compatibility entry points temporarily because command callers are migrated and the entry points are deleted together in Task 10; no new runtime path may use them.

- [ ] **Step 5: Build runtime from committed bounded snapshot**

Reconciliation creates missing definitions, disables absent definitions, and handles re-enable/zero activation dates through the store. Instantiate only active/nonterminal and normal-window metadata. Load entries on demand for other visible instances through a bounded LRU cache.

Before implementing the cache, add red tests proving startup materializes no more than the configured archive limit, capacity is clamped to at least one, exact lookup is supported, least-recently-used entries are evicted, a miss requests bounded pagination, and finalized-reward history remains visible in persistence without eager runtime materialization.

- [ ] **Step 6: Run schedule/restart/cache tests and commit**

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.Tests/MHServerEmu.Tests.csproj --configuration Release -p:Platform=x64 --filter "FullyQualifiedName~LeaderboardSchedule|FullyQualifiedName~LeaderboardRestart|FullyQualifiedName~LeaderboardArchiveCache"
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet build MHServerEmu.sln --configuration Release -p:Platform=x64
git add src/MHServerEmu.Leaderboards src/MHServerEmu/ServerApp.cs src/MHServerEmu.Tests
git commit -m "refactor(leaderboards): own runtime persistence"
```

Expected: schedule and in-process restart tests pass with no singleton access from leaderboard-service runtime paths; temporary command compatibility remains until Task 10.

## Task 8: Commit Scores and Lifecycle Before Publication

**Files:**
- Modify: `src/MHServerEmu.Leaderboards/LeaderboardService.cs`
- Modify: `src/MHServerEmu.Leaderboards/LeaderboardDatabase.cs`
- Modify: `src/MHServerEmu.Leaderboards/Leaderboard.cs`
- Modify: `src/MHServerEmu.Leaderboards/LeaderboardInstance.cs`
- Modify: `src/MHServerEmu.Leaderboards/LeaderboardEntry.cs`
- Test: `src/MHServerEmu.Tests/Leaderboards/LeaderboardScoreCommitTests.cs`
- Test: `src/MHServerEmu.Tests/Leaderboards/LeaderboardLifecycleCommitTests.cs`
- Test: `src/MHServerEmu.Tests/Leaderboards/LeaderboardRankingTests.cs`
- Test: `src/MHServerEmu.Tests/Leaderboards/LeaderboardVisibilityTests.cs`

- [ ] **Step 1: Write failing commit-order and ranking tests**

```csharp
[Fact]
public void SaveScores_SafeFailure_KeepsDirtyFlags()
{
    RecordingLeaderboardStore store = CreateActiveStore(LeaderboardStoreResult.Failed);
    LeaderboardInstance instance = CreateDirtyInstance(store);
    instance.SaveEntries();
    Assert.All(instance.Entries, entry => Assert.True(entry.SaveRequired));
}

[Fact]
public void EqualHighBitScores_UseUnsignedTieOrderAndCompetitionRank()
{
    var entries = CreateEntries((3UL, ulong.MaxValue), (1UL, ulong.MaxValue), (2UL, 5UL));
    LeaderboardRanking.Sort(entries, descending: true);
    Assert.Equal(new ulong[] { 1, 3, 2 }, entries.Select(entry => entry.ParticipantId));
    Assert.Equal(new[] { 1, 1, 3 }, LeaderboardRanking.GetCompetitionRanks(entries));
}
```

- [ ] **Step 2: Run lifecycle tests and verify current publish-before-commit failures**

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.Tests/MHServerEmu.Tests.csproj --configuration Release -p:Platform=x64 --filter "FullyQualifiedName~LeaderboardScoreCommit|FullyQualifiedName~LeaderboardLifecycleCommit|FullyQualifiedName~LeaderboardRanking"
```

Expected: FAIL because dirty flags clear early and lifecycle uses split writes.

- [ ] **Step 3: Reorder ticks and use compound operations**

Each tick drains score messages, persists due batches, then evaluates activation/expiration/rotation. Use `ActivateInstance`, `ExpireInstance`, and `RotateActiveInstance`; update runtime state/cache/game messages only after `Success`. Safe failures retain state for retry. Uncertain results invoke fatal callback.

- [ ] **Step 4: Make unsigned `Score` authoritative**

Load entries unordered. Sort by `unchecked((ulong)Score)` according to ranking rule, then unsigned participant ID ascending. Use competition rank `1,1,3`; `HighScore` is persisted but not the ordering field. Clear dirty flags after store success only.

- [ ] **Step 5: Flush dirty scores before schedule reload**

Administration reload drains the accepted queue and force-saves every dirty entry affected by disable/replacement. Abort safe-failed reload without replacing runtime schedule. Test no score loss.

- [ ] **Step 6: Run lifecycle/ranking/visibility tests and commit**

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.Tests/MHServerEmu.Tests.csproj --configuration Release -p:Platform=x64 --filter "FullyQualifiedName~LeaderboardScoreCommit|FullyQualifiedName~LeaderboardLifecycleCommit|FullyQualifiedName~LeaderboardRanking|FullyQualifiedName~LeaderboardVisibility"
git add src/MHServerEmu.Leaderboards src/MHServerEmu.Tests
git commit -m "fix(leaderboards): commit lifecycle before publication"
```

## Task 9: Make Reward Generation and Finalization Idempotent

**Files:**
- Modify: `src/MHServerEmu.Leaderboards/LeaderboardRewardManager.cs`
- Modify: `src/MHServerEmu.Leaderboards/LeaderboardInstance.cs`
- Modify: `src/MHServerEmu.Leaderboards/LeaderboardService.cs`
- Test: `src/MHServerEmu.Tests/Leaderboards/LeaderboardRewardGenerationTests.cs`
- Test: `src/MHServerEmu.Tests/Leaderboards/LeaderboardRewardDeliveryTests.cs`
- Test: `src/MHServerEmu.Tests/Leaderboards/LeaderboardRewardRetryTests.cs`

- [ ] **Step 1: Write failing generation/finalization tests**

```csharp
[Fact]
public void FinalizationFailure_RetriesAtBoundedSchedule()
{
    ManualLeaderboardClock clock = new();
    RecordingLeaderboardStore store = RecordingLeaderboardStore.FinalizeFailures(6);
    LeaderboardRewardManager manager = CreateRewardManager(store, clock);
    manager.Confirm(TestRewardKey);
    Assert.Equal(new[] { 1, 2, 4, 8, 16, 30 }, AdvanceThroughRetries(manager, clock));
}

[Fact]
public void RewardGeneration_DuplicateParticipant_IsInvalidBeforeStore()
{
    Assert.False(LeaderboardRewardBuilder.TryBuild(new[] { Entry(1), Entry(1) }, out _));
}
```

- [ ] **Step 2: Run reward tests and verify current non-idempotent behavior**

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.Tests/MHServerEmu.Tests.csproj --configuration Release -p:Platform=x64 --filter FullyQualifiedName~LeaderboardReward
```

Expected: FAIL because current code inserts directly and removes pending rows before confirmed finalization.

- [ ] **Step 3: Generate complete rewards in one operation**

Sort by unsigned score, calculate shared competition ranks, reject duplicate participants, and call `GenerateRewards` with the full set. On success publish Reward, RewardsPending, Rewarded in order. On safe failure remain Expired. Preserve original creation dates on exact replay.

- [ ] **Step 4: Implement full-key conditional finalization and retry**

Cache by `LeaderboardRewardKey`. First/duplicate/late confirmations call the store. Remove on Finalized/AlreadyFinalized/NotFound only. Safe failure retries 1/2/4/8/16/30 seconds, then every 30 seconds until success/shutdown. Use a copied due-work list before dictionary removals. OutcomeUncertain invokes fatal shutdown.

- [ ] **Step 5: Preserve at-least-once delivery semantics**

Send failure changes no durable state. Shutdown stops retry timers without finalizing. A restart retrieves pending rows by participant. Add explicit grant-before-confirm and confirm-before-response crash-window tests.

- [ ] **Step 6: Run reward tests and commit**

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.Tests/MHServerEmu.Tests.csproj --configuration Release -p:Platform=x64 --filter FullyQualifiedName~LeaderboardReward
git add src/MHServerEmu.Leaderboards src/MHServerEmu.Tests
git commit -m "fix(leaderboards): make reward persistence idempotent"
```

## Task 10: Add Mailbox-Backed Administration and Inject Commands

**Files:**
- Create: `src/MHServerEmu.Leaderboards/Administration/ILeaderboardAdministration.cs`
- Create: `src/MHServerEmu.Leaderboards/Administration/LeaderboardAdminResult.cs`
- Create: `src/MHServerEmu.Leaderboards/Administration/LeaderboardSummary.cs`
- Create: `src/MHServerEmu.Leaderboards/Administration/LeaderboardInstanceSummary.cs`
- Create: `src/MHServerEmu.Leaderboards/LeaderboardServiceMailbox.cs`
- Modify: `src/MHServerEmu.Leaderboards/LeaderboardService.cs`
- Modify: `src/MHServerEmu.Leaderboards/LeaderboardDatabase.cs`
- Modify: `src/MHServerEmu.DatabaseAccess/SQLite/SQLiteLeaderboardDBManager.cs`
- Modify: `src/MHServerEmu/Commands/Implementations/LeaderboardsCommands.cs`
- Modify: `src/MHServerEmu/Commands/CommandManager.cs`
- Modify: `src/MHServerEmu/ServerApp.cs`
- Test: `src/MHServerEmu.Tests/Leaderboards/LeaderboardAdministrationTests.cs`
- Test: `src/MHServerEmu.Tests/Commands/LeaderboardsCommandsTests.cs`
- Test: `src/MHServerEmu.Tests/CommandManagerInitializationTests.cs`

- [ ] **Step 1: Write failing mailbox and command tests**

```csharp
[Fact]
public void GetLeaderboards_ServiceUnavailable_ReturnsUnavailable()
{
    ILeaderboardAdministration administration = CreateAdministration(serviceRunning: false);
    Assert.Equal(LeaderboardAdminResult.Unavailable, administration.GetLeaderboards(out _));
}

[Fact]
public void LeaderboardsCommands_UsesInjectedFacade()
{
    var facade = new StubLeaderboardAdministration();
    string output = new LeaderboardsCommands(facade).All(Array.Empty<string>(), null);
    Assert.Contains(facade.Summary.Name, output);
}
```

- [ ] **Step 2: Run command/admin tests and verify singleton failures**

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.Tests/MHServerEmu.Tests.csproj --configuration Release -p:Platform=x64 --filter "FullyQualifiedName~LeaderboardAdministration|FullyQualifiedName~LeaderboardsCommands|FullyQualifiedName~CommandManagerInitialization"
```

Expected: FAIL because commands directly access `LeaderboardDatabase.Instance`.

- [ ] **Step 3: Implement immutable summaries and mailbox requests**

Facade methods are exact spec signatures. Post request objects to the leaderboard service mailbox and wait no longer than five seconds. Return immutable scalar snapshots; no runtime object/store leaves the service thread.

- [ ] **Step 4: Inject command facade and remove compatibility singletons**

Give `LeaderboardsCommands` a required facade constructor. Update `ServerApp` to register a preconstructed instance beside `AccountCommands`; reflection skips it. Preserve existing output content using DTOs. After the final command caller is migrated, remove `LeaderboardDatabase.Instance`, `SQLiteLeaderboardDBManager.Instance`, and every remaining concrete `DBManager` access from production leaderboard code in this same step.

- [ ] **Step 5: Test thread affinity, timeout, reload drain, and commit**

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.Tests/MHServerEmu.Tests.csproj --configuration Release -p:Platform=x64 --filter "FullyQualifiedName~LeaderboardAdministration|FullyQualifiedName~LeaderboardsCommands|FullyQualifiedName~CommandManagerInitialization"
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet build MHServerEmu.sln --configuration Release -p:Platform=x64
git add src/MHServerEmu.Leaderboards src/MHServerEmu.DatabaseAccess/SQLite/SQLiteLeaderboardDBManager.cs src/MHServerEmu/Commands src/MHServerEmu/ServerApp.cs src/MHServerEmu.Tests
git commit -m "refactor(commands): inject leaderboard administration"
```

## Task 11: Wire Fatal Outcomes, Player Disconnect, and Async Application Lifetime

**Files:**
- Modify: `src/MHServerEmu.DatabaseAccess.PostgreSQL/Stores/PostgreSQLGuildStore.cs`
- Modify: `src/MHServerEmu.DatabaseAccess.PostgreSQL/Stores/PostgreSQLLeaderboardStore.cs`
- Modify: `src/MHServerEmu.PlayerManagement/Players/PlayerHandle.cs`
- Modify: `src/MHServerEmu.PlayerManagement/Network/PlayerManagerServiceMailbox.cs`
- Modify: `src/MHServerEmu.PlayerManagement/Players/ClientManager.cs`
- Modify: `src/MHServerEmu.Core/Network/ServerManager.cs`
- Modify: `src/MHServerEmu/Persistence/PersistenceComposition.cs`
- Create: `src/MHServerEmu/ServerStartupDependencies.cs`
- Modify: `src/MHServerEmu/Program.cs`
- Modify: `src/MHServerEmu/ServerApp.cs`
- Modify: `src/MHServerEmu.Leaderboards/LeaderboardService.cs`
- Modify: `src/MHServerEmu.Tests/MHServerEmu.Tests.csproj`
- Test: `src/MHServerEmu.Core.Tests/Network/ServerManagerLifecycleTests.cs`
- Test: `src/MHServerEmu.PlayerManagement.Tests/PlayerHandleTests.cs`
- Test: `src/MHServerEmu.Tests/ServerAppLifecycleTests.cs`
- Test: `src/MHServerEmu.Tests/PostgreSQLActivationTests.cs`
- Test: `src/MHServerEmu.Tests/Leaderboards/LeaderboardShutdownDrainTests.cs`

- [ ] **Step 1: Write failing fatal, service-fault, and asset-free activation tests**

```csharp
[Fact]
public void RunServices_ServiceThrows_ReturnsFalseAndStopsStartedServices()
{
    ServerManager manager = CreateManager(new RunningService(), new ThrowingService());
    Assert.False(manager.RunServices());
    Assert.Equal(GameServiceState.Shutdown, manager.GetGameService(GameServiceType.GameInstance).State);
}

[Fact]
public async Task FatalCallback_WhileConsoleReadPending_StopsAndDisposesOnce()
{
    ServerAppHarness app = CreateAppWithPendingConsoleRead();
    Task run = app.RunAsync();
    app.Persistence.RaiseFatal("WriterLockLost");
    await run.WaitAsync(TimeSpan.FromSeconds(5));
    Assert.Equal(1, app.Persistence.DisposeCount);
}
```

The tagged activation test uses injected startup-system and game-service factories, a synthetic public leaderboard prototype catalog/schedule, and no proprietary game-data files. It asserts persistence migration and leaderboard initialization happen before the synthetic game systems are invoked.

- [ ] **Step 2: Run lifecycle tests and verify hangs/current result collapse**

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.Core.Tests/MHServerEmu.Core.Tests.csproj --configuration Release -p:Platform=x64 --filter FullyQualifiedName~ServerManagerLifecycle
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.Tests/MHServerEmu.Tests.csproj --configuration Release -p:Platform=x64 --filter FullyQualifiedName~ServerAppLifecycle
MHSERVEREMU_POSTGRESQL_TEST_ADMIN_CONNECTION_STRING="${ADMIN_CONNECTION_STRING}" DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.Tests/MHServerEmu.Tests.csproj --configuration Release -p:Platform=x64 --filter "Category=PostgreSQLIntegration&FullyQualifiedName~PostgreSQLActivation"
```

Expected: FAIL because service startup waits indefinitely, `ServerApp.Run` is synchronous, and no injectable asset-free startup seam exists. Start PostgreSQL 16 as in Task 13 and set `ADMIN_CONNECTION_STRING` before running the tagged test; do not accept a skip.

- [ ] **Step 3: Propagate uncertain domain outcomes**

Guild/leaderboard stores invoke provider fatal callback before returning uncertain. `PlayerHandle` propagates `PlayerStoreResult`; mailbox disconnects on uncertain and never retries. Client removal discards aggregate. Account reconciliation from Task 2 remains fail-closed.

- [ ] **Step 4: Make service startup/shutdown fault-aware**

Wrap service thread entry, capture exception/early exit, and make `RunServices` return false. Track only successfully started services and shut them down in reverse order. Shutdown is legal and idempotent during partial startup, running, and already stopping states.

- [ ] **Step 5: Make application entry/startup asynchronous**

Change `Program.Main` to `async Task Main` and `ServerApp.RunAsync`. Add `ServerStartupDependencies` with production defaults and injectable startup-system/service factories for tests; the seam controls sequencing but does not bypass production initialization. Replace the final `PersistenceComposition.TryCreate` caller with `await CreateAsync`, then delete `TryCreate` in the same step. States are Starting/Running/Stopping/Stopped. Await persistence before game-data systems. Race console `ReadLineAsync` against a `TaskCompletionSource` shutdown signal. Register commands/services after complete initialization. Put service shutdown and persistence disposal in `finally`; remove blocking failure `ReadLine` calls.

Add tagged activation integration tests using the existing administrative connection-string environment variable and a self-contained fixture in `MHServerEmu.Tests`. The fixture creates an isolated database/role, writes its generated application connection string to a temporary `ConfigOverride.ini`, points an injected `ServerApp` configuration root at that directory, and deletes both file and database during cleanup. Add a test-only Npgsql package reference if direct administrative setup is required; do not reference the `MHServerEmu.DatabaseAccess.Tests` assembly. Unit lifecycle tests continue to use fake persistence and no live database.

- [ ] **Step 6: Drain leaderboard shutdown and run lifecycle tests**

Leaderboard service rejects new work, drains queued scores, performs final safe saves, then enters Shutdown. Provider disposal occurs afterward.

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.Core.Tests/MHServerEmu.Core.Tests.csproj --configuration Release -p:Platform=x64 --filter FullyQualifiedName~ServerManagerLifecycle
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.PlayerManagement.Tests/MHServerEmu.PlayerManagement.Tests.csproj --configuration Release -p:Platform=x64 --filter FullyQualifiedName~PlayerHandle
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.Tests/MHServerEmu.Tests.csproj --configuration Release -p:Platform=x64 --filter "FullyQualifiedName~ServerAppLifecycle|FullyQualifiedName~LeaderboardShutdownDrain"
MHSERVEREMU_POSTGRESQL_TEST_ADMIN_CONNECTION_STRING="${ADMIN_CONNECTION_STRING}" DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.Tests/MHServerEmu.Tests.csproj --configuration Release -p:Platform=x64 --filter "Category=PostgreSQLIntegration&FullyQualifiedName~PostgreSQLActivation"
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet build MHServerEmu.sln --configuration Release -p:Platform=x64
git add src
git commit -m "fix(server): own persistence through shutdown"
```

## Task 12: Add Guards, Configuration, and PostgreSQL Operations Documentation

**Files:**
- Modify: `src/MHServerEmu.Tests/Architecture/PersistenceSingletonGuardTests.cs`
- Modify: `src/MHServerEmu/Config.ini`
- Create: `docs/Setup/PostgreSQL.md`
- Modify: `docs/Setup/InitialSetup.md`
- Modify: `docs/Setup/AdvancedSetup.md`
- Modify: `docs/Index.md`
- Modify: `.github/workflows/verify.yml`

- [ ] **Step 1: Write failing syntax-aware source guards**

```csharp
[Theory]
[InlineData("LeaderboardDatabase.Instance")]
[InlineData("SQLiteLeaderboardDBManager /* comment */ . Instance")]
public void ProductionSource_LeaderboardPersistenceSingleton_IsRejected(string source)
{
    Assert.True(PersistenceSingletonGuard.ContainsForbiddenAccess(source));
}
```

- [ ] **Step 2: Run guards and verify singleton detections**

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.Tests/MHServerEmu.Tests.csproj --configuration Release -p:Platform=x64 --filter FullyQualifiedName~PersistenceSingletonGuardTests
```

Expected: FAIL until new syntax patterns and all production references are removed.

- [ ] **Step 3: Extend guards and remove residual accesses**

Reject qualified/aliased/commented `LeaderboardDatabase.Instance`, `SQLiteLeaderboardDBManager.Instance`, and concrete leaderboard stores outside provider/composition paths. Do not reject test fixtures.

- [ ] **Step 4: Update configuration and setup docs**

Mark PostgreSQL supported, fresh-install-only, single-writer, connection string override-only, and leaderboard database file JSON/SQLite-only. Explicitly state that no in-place conversion/import from JSON/SQLite and no downgrade path are supported; PostgreSQL application/schema upgrades remain supported through ordered startup migrations. `PostgreSQL.md` must include exact examples for database/role ownership, `SSL Mode=VerifyFull`, root certificate, `chmod 600`, Windows ACL, `pg_dump --format=custom --no-owner`, clean target creation, `pg_restore --no-owner`, ownership/history verification SQL, and stop-backup-deploy-start-verify upgrade order.

- [ ] **Step 5: Enforce nonempty, zero-skip PostgreSQL CI results**

Keep the PostgreSQL 16/17 service matrix, add a TRX logger to the category-filtered integration command, and add a `pwsh` step that parses `TestRun/ResultSummary/Counters`. Fail when `total == 0`, `executed == 0`, or `notExecuted != 0`; test failures already fail `dotnet test`. Upload the TRX file on failure for diagnosis.

```powershell
[xml]$trx = Get-Content artifacts/postgresql/TestResults.trx
$counters = $trx.TestRun.ResultSummary.Counters
if ([int]$counters.total -eq 0 -or [int]$counters.executed -eq 0 -or [int]$counters.notExecuted -ne 0) {
    throw "PostgreSQL integration matrix matched no tests or skipped tests"
}
```

- [ ] **Step 6: Run guards, inspect links/secrets, and commit**

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.Tests/MHServerEmu.Tests.csproj --configuration Release -p:Platform=x64 --filter FullyQualifiedName~PersistenceSingletonGuardTests
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet build MHServerEmu.sln --configuration Release -p:Platform=x64
git diff --check
if git diff --unified=0 origin/release/1.0.1 -- . ':!docs/superpowers' | grep -E '^\+.*(Password=|ConnectionString=.+[^=]$)' | grep -vE '(POSTGRES_PASSWORD=postgres|Password=postgres)'; then exit 1; fi
git add .github/workflows/verify.yml src/MHServerEmu.Tests src/MHServerEmu/Config.ini docs
git commit -m "docs(postgresql): document activated persistence"
```

Expected: guards pass, the workflow rejects skipped/empty PostgreSQL runs, all changed relative documentation links resolve, and no unexpected credential literal is present.

## Task 13: Run Complete Regression and PostgreSQL 16/17 Activation Validation

**Files:**
- Modify only if a failing test proves a focused defect.
- Verify: `.github/workflows/verify.yml`
- Verify: `src/MHServerEmu/Persistence/PersistenceComposition.cs`
- Verify: `src/MHServerEmu/ServerApp.cs`

- [ ] **Step 1: Run complete Release build and provider-independent suite**

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet restore MHServerEmu.sln
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet build MHServerEmu.sln --configuration Release -p:Platform=x64 --no-restore
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test MHServerEmu.sln --configuration Release -p:Platform=x64 --no-build
```

Expected: zero warnings/errors and all non-credential-gated tests pass.

- [ ] **Step 2: Run live PostgreSQL 16 matrix**

```bash
docker rm -f "mhserveremu-pg16-pr1c" 2>/dev/null || true
docker run -d --name "mhserveremu-pg16-pr1c" -e POSTGRES_USER=postgres -e POSTGRES_PASSWORD=postgres -e POSTGRES_DB=postgres -p "127.0.0.1::5432" --health-cmd "pg_isready -U postgres -d postgres" --health-interval 2s --health-timeout 5s --health-retries 30 postgres:16
until [ "$(docker inspect --format '{{.State.Health.Status}}' "mhserveremu-pg16-pr1c")" = "healthy" ]; do sleep 1; done
PORT="$(docker port "mhserveremu-pg16-pr1c" 5432/tcp | cut -d: -f2)"
MHSERVEREMU_POSTGRESQL_TEST_ADMIN_CONNECTION_STRING="Host=127.0.0.1;Port=${PORT};Database=postgres;Username=postgres;Password=postgres" DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Release -p:Platform=x64 --filter "Category=PostgreSQLIntegration"
docker rm -f "mhserveremu-pg16-pr1c"
```

Expected: every tagged test passes with zero skips.

- [ ] **Step 3: Run live PostgreSQL 17 matrix**

```bash
docker rm -f "mhserveremu-pg17-pr1c" 2>/dev/null || true
docker run -d --name "mhserveremu-pg17-pr1c" -e POSTGRES_USER=postgres -e POSTGRES_PASSWORD=postgres -e POSTGRES_DB=postgres -p "127.0.0.1::5432" --health-cmd "pg_isready -U postgres -d postgres" --health-interval 2s --health-timeout 5s --health-retries 30 postgres:17
until [ "$(docker inspect --format '{{.State.Health.Status}}' "mhserveremu-pg17-pr1c")" = "healthy" ]; do sleep 1; done
PORT="$(docker port "mhserveremu-pg17-pr1c" 5432/tcp | cut -d: -f2)"
MHSERVEREMU_POSTGRESQL_TEST_ADMIN_CONNECTION_STRING="Host=127.0.0.1;Port=${PORT};Database=postgres;Username=postgres;Password=postgres" DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Release -p:Platform=x64 --filter "Category=PostgreSQLIntegration"
docker rm -f "mhserveremu-pg17-pr1c"
```

Expected: every tagged test passes with zero skips.

- [ ] **Step 4: Run full activation lifecycle tests**

Start a fresh PostgreSQL 16 container as in Step 2, assign its administrative connection string to `ADMIN_CONNECTION_STRING`, and execute:

```bash
MHSERVEREMU_POSTGRESQL_TEST_ADMIN_CONNECTION_STRING="${ADMIN_CONNECTION_STRING}" DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.Tests/MHServerEmu.Tests.csproj --configuration Release -p:Platform=x64 --filter "Category=PostgreSQLIntegration&FullyQualifiedName~PostgreSQLActivation"
DOTNET_ROLL_FORWARD=Major ~/.dotnet/dotnet test src/MHServerEmu.Tests/MHServerEmu.Tests.csproj --configuration Release -p:Platform=x64 --filter "FullyQualifiedName~ServerAppLifecycle|FullyQualifiedName~LeaderboardRestart|FullyQualifiedName~LeaderboardShutdown"
```

Expected: fresh startup, no-op restart, disabled leaderboard startup, second-runtime rejection, writer-loss shutdown, graceful drain/disposal, and zero SQLite leaderboard file access all pass without proprietary assets.

- [ ] **Step 5: Inspect final branch**

```bash
git status --short --branch
git diff --check origin/release/1.0.1...HEAD
git log --oneline origin/release/1.0.1..HEAD
git diff --name-only origin/release/1.0.1...HEAD | grep -E "ConfigOverride|\.env|credentials" && exit 1 || true
```

Expected: clean worktree, no whitespace errors, no secrets/override files, only PR 1C scope, and no PostgreSQL activation gate remains.

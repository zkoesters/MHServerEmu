# PostgreSQL PR 1B Core Data Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add revision-safe PostgreSQL account, player/entity, and guild persistence while retaining JSON/SQLite compatibility and keeping PostgreSQL runtime activation gated for PR 1C.

**Architecture:** Focused PostgreSQL account, player, and guild stores share one bounded operation executor over the PR 1A data source and writer fence. Shared capability contracts expose intent-specific writes and stable result enums; SQLite and JSON adapt without file-schema changes. A single `player_entity` table represents the current four entity collections, and guild callers persist each compound transition before publishing in-memory or game state.

**Tech Stack:** .NET 8, C# 12, xUnit 2.4.2, Dapper, System.Data.SQLite, Npgsql 10.0.3, PostgreSQL 16/17, GitHub Actions service containers

---

## Preconditions

Execute in `phase1b-postgresql-core-data` at commit `07a467db5` or later. Read the approved design before editing:

- `docs/superpowers/specs/2026-08-16-postgresql-pr1b-core-data-design.md`

Use the .NET 8 host:

```bash
~/.dotnet/dotnet restore MHServerEmu.sln
~/.dotnet/dotnet build MHServerEmu.sln --configuration Release --no-restore
~/.dotnet/dotnet test MHServerEmu.sln --configuration Release --no-build
```

Expected: zero build errors and 511 passed tests, with 13 PostgreSQL integration tests skipped unless `MHSERVEREMU_POSTGRESQL_TEST_ADMIN_CONNECTION_STRING` is set.

Before executing any live PostgreSQL task, start the specified local service and export its exact administrative connection string:

```bash
docker rm -f "mhserveremu-pg16-pr1b" 2>/dev/null || true
docker run -d --name "mhserveremu-pg16-pr1b" -e POSTGRES_USER=postgres -e POSTGRES_PASSWORD=postgres -e POSTGRES_DB=postgres -p "127.0.0.1::5432" --health-cmd "pg_isready -U postgres -d postgres" --health-interval 2s --health-timeout 5s --health-retries 30 postgres:16
until [ "$(docker inspect --format '{{.State.Health.Status}}' "mhserveremu-pg16-pr1b")" = "healthy" ]; do sleep 1; done
PORT="$(docker port "mhserveremu-pg16-pr1b" 5432/tcp | cut -d: -f2)"
export MHSERVEREMU_POSTGRESQL_TEST_ADMIN_CONNECTION_STRING="Host=127.0.0.1;Port=${PORT};Database=postgres;Username=postgres;Password=postgres"
```

Do not add the PostgreSQL project reference to `src/MHServerEmu/MHServerEmu.csproj`, change `PersistenceComposition` activation behavior, alter SQLite schema version 6 or scripts, alter JSON file shape, modify migration `0001`, or add leaderboard persistence.

## File Responsibility Map

### Shared Contracts and Models

- `src/MHServerEmu.DatabaseAccess/Capabilities/*Store*.cs`: result enums and intent-specific account/player/guild contracts.
- `src/MHServerEmu.DatabaseAccess/Models/DBAccount.cs`, `DBPlayer.cs`, `DBGuild.cs`: `[JsonIgnore]` persistence metadata and uncertain state.
- `src/MHServerEmu.DatabaseAccess/Models/GuildMemberChange.cs`, `GuildMemberTransition.cs`, `PersistenceState.cs`: checked compound guild transition value types.
- `src/MHServerEmu.DatabaseAccess/Validation/IdentityNormalizer.cs`: normalization-v1 keys.
- `src/MHServerEmu.DatabaseAccess/Validation/PlayerAggregateValidator.cs`: entity graph and slot validation before any store write.
- `src/MHServerEmu.Core/Helpers/CryptographyHelper.cs`: expose existing PBKDF2 constants without changing legacy hashing.

### Existing Providers and Callers

- `src/MHServerEmu.DatabaseAccess/SQLite/SQLiteDBManager.cs`: map legacy operations to contracts; make compound guild writes transactional without schema changes.
- `src/MHServerEmu.DatabaseAccess/Json/JsonDBManager.cs`: preserve JSON behavior and map unsupported writes to stable results.
- `src/MHServerEmu.PlayerManagement/Players/AccountManager.cs`: call intent-specific account operations and notify only after commit.
- `src/MHServerEmu.PlayerManagement/Players/PlayerHandle.cs`: map `PlayerStoreResult` to current load/save behavior.
- `src/MHServerEmu.Games/Network/PlayerConnection.cs`: record archive diagnostics immediately after successful database serialization.
- `src/MHServerEmu.PlayerManagement/Social/MasterGuild.cs`: persist initial creation, name/MOTD, joins, rank transitions, and deletions before publishing state.
- `src/MHServerEmu.PlayerManagement/Social/MasterGuildManager.cs`: delay registry and guild-map updates until persisted success.

### PostgreSQL

- `src/MHServerEmu.DatabaseAccess.PostgreSQL/Migrations/0002_CorePersistence.sql`: immutable core relational schema.
- `src/MHServerEmu.DatabaseAccess.PostgreSQL/MHServerEmu.DatabaseAccess.PostgreSQL.csproj`: embed migration 0002.
- `src/MHServerEmu.DatabaseAccess.PostgreSQL/PostgreSQLStoreExecutor.cs`: deadline, fence, transaction, command timeout, rollback, and uncertain-commit handling.
- `src/MHServerEmu.DatabaseAccess.PostgreSQL/Stores/PostgreSQLAccountStore.cs`: normalized account CRUD/mutations.
- `src/MHServerEmu.DatabaseAccess.PostgreSQL/Stores/PostgreSQLPlayerStore.cs`: atomic profile/entity aggregate load/save.
- `src/MHServerEmu.DatabaseAccess.PostgreSQL/Stores/PostgreSQLGuildStore.cs`: checked guild and membership transitions.
- `src/MHServerEmu.DatabaseAccess.PostgreSQL/PostgreSQLProvider.cs` and `Locking/PostgreSQLWriterOwner.cs`: expose stores and apply an absolute deadline to fence validation.

### Tests

- `src/MHServerEmu.DatabaseAccess.Tests/Capabilities/IdentityNormalizerTests.cs` and `Models/PlayerAggregateValidatorTests.cs`: deterministic provider-neutral rules.
- `src/MHServerEmu.DatabaseAccess.Tests/Conformance/*StoreConformanceTests.cs`: shared SQLite/PostgreSQL behavior.
- `src/MHServerEmu.DatabaseAccess.Tests/PostgreSQL/Stores/*StoreTests.cs`: PostgreSQL-only constraints, revisions, locks, and uncertain commits.
- `src/MHServerEmu.DatabaseAccess.Tests/PostgreSQL/PostgreSQLStoreExecutorTests.cs`: deadline/fence/commit classification seam.
- `src/MHServerEmu.PlayerManagement.Tests/*`: contract fakes, account result mapping, and guild persist-before-publish behavior.

## Task 1: Add Stable Result Types, Persistence Metadata, and Contracts

**Files:**
- Create: `src/MHServerEmu.DatabaseAccess/Capabilities/AccountStoreResult.cs`
- Create: `src/MHServerEmu.DatabaseAccess/Capabilities/PlayerStoreResult.cs`
- Create: `src/MHServerEmu.DatabaseAccess/Capabilities/GuildStoreResult.cs`
- Create: `src/MHServerEmu.DatabaseAccess/Models/PersistenceState.cs`
- Create: `src/MHServerEmu.DatabaseAccess/Models/GuildMemberChange.cs`
- Create: `src/MHServerEmu.DatabaseAccess/Models/GuildMemberTransition.cs`
- Modify: `src/MHServerEmu.DatabaseAccess/Capabilities/IAccountStore.cs`
- Modify: `src/MHServerEmu.DatabaseAccess/Capabilities/IPlayerStore.cs`
- Modify: `src/MHServerEmu.DatabaseAccess/Capabilities/IGuildStore.cs`
- Modify: `src/MHServerEmu.DatabaseAccess/Models/DBAccount.cs`
- Modify: `src/MHServerEmu.DatabaseAccess/Models/DBPlayer.cs`
- Modify: `src/MHServerEmu.DatabaseAccess/Models/DBGuild.cs`
- Modify: `src/MHServerEmu.DatabaseAccess/Json/JsonDBManager.cs`
- Modify: `src/MHServerEmu.DatabaseAccess/SQLite/SQLiteDBManager.cs`
- Modify: `src/MHServerEmu.PlayerManagement.Tests/StubDBManager.cs`
- Modify: `src/MHServerEmu.DatabaseAccess.Tests/Persistence/PersistenceServicesTests.cs`
- Modify: `src/MHServerEmu.Tests/Leaderboards/LeaderboardDatabasePlayerStoreTests.cs`
- Modify: `src/MHServerEmu.PlayerManagement/Players/AccountManager.cs`
- Modify: `src/MHServerEmu.PlayerManagement/Players/PlayerHandle.cs`
- Modify: `src/MHServerEmu.PlayerManagement/Social/MasterGuild.cs`
- Modify: `src/MHServerEmu.PlayerManagement/Social/MasterGuildManager.cs`
- Modify: `src/MHServerEmu.PlayerManagement.Tests/AccountManagerTests.cs`
- Modify: `src/MHServerEmu.PlayerManagement.Tests/PlayerHandleTests.cs`
- Modify: `src/MHServerEmu.PlayerManagement.Tests/GuildPersistenceInjectionTests.cs`
- Test: `src/MHServerEmu.DatabaseAccess.Tests/Persistence/PersistenceServicesTests.cs`
- Test: `src/MHServerEmu.DatabaseAccess.Tests/Json/DBAccountJsonSerializerTests.cs`

- [ ] **Step 1: Write failing contract and JSON-shape tests**

```csharp
[Fact]
public void ProviderMetadata_IsNotSerialized()
{
    AssertProviderMetadataIsIgnored(new DBAccount("player"));
    AssertProviderMetadataIsIgnored(new DBPlayer(1));
    AssertProviderMetadataIsIgnored(new DBGuild(1, "Guild", "", 1, 1));
}

[Fact]
public void GuildMemberTransition_RejectsDuplicatePlayerChanges()
{
    Assert.Throws<ArgumentException>(() => new GuildMemberTransition(10, 2,
        new GuildMemberChange(1, null, 1), new GuildMemberChange(1, 1, 2)));
}

private static void AssertProviderMetadataIsIgnored<T>(T value)
{
    string json = JsonSerializer.Serialize(value);
    Assert.DoesNotContain("PersistenceRevision", json);
    Assert.DoesNotContain("PersistenceState", json);
    Assert.DoesNotContain("CreatedAtUtc", json);
    Assert.DoesNotContain("UpdatedAtUtc", json);
}
```

- [ ] **Step 2: Run the focused tests and verify compilation fails**

```bash
~/.dotnet/dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Release --filter "FullyQualifiedName~PersistenceServicesTests|FullyQualifiedName~DBAccountJsonSerializerTests"
```

Expected: FAIL because the new result, transition, and persistence-state types do not exist.

- [ ] **Step 3: Add exact result enums and transition value types**

```csharp
public enum AccountStoreResult { Success, AccountNotFound, EmailConflict, PlayerNameConflict, StaleRevision, InvalidData, Failed, OutcomeUncertain }
public enum PlayerStoreResult { Success, AccountNotFound, StaleRevision, InvalidAggregate, Failed, OutcomeUncertain }
public enum GuildStoreResult { Success, GuildNotFound, NameConflict, MembershipConflict, StaleRevision, InvalidData, Failed, OutcomeUncertain }
public enum PersistenceState { Clean, OutcomeUncertain }

public readonly record struct GuildMemberChange(long PlayerDbGuid, long? ExpectedMembership, long? NewMembership);

public sealed class GuildMemberTransition
{
    public GuildMemberTransition(long guildId, long expectedRevision, params GuildMemberChange[] changes)
    {
        if (changes is null || changes.Length is < 1 or > 2 || changes.Select(change => change.PlayerDbGuid).Distinct().Count() != changes.Length || changes.Any(change => change.ExpectedMembership is 0 || change.NewMembership is 0 || change.ExpectedMembership is < 0 or > 3 || change.NewMembership is < 0 or > 3))
            throw new ArgumentException("A guild transition must contain one or two unique player changes.", nameof(changes));
        GuildId = guildId;
        ExpectedRevision = expectedRevision;
        Changes = changes;
    }
    public long GuildId { get; }
    public long ExpectedRevision { get; }
    public IReadOnlyList<GuildMemberChange> Changes { get; }
}
```

- [ ] **Step 4: Add metadata and replace CRUD contracts**

Add `[JsonIgnore]` properties named `PersistenceRevision`, `PersistenceState`, `CreatedAtUtc`, and `UpdatedAtUtc` to all three aggregate models. Add account credential metadata (`PasswordAlgorithm`, `PasswordFormatVersion`, `PasswordIterations`, `PasswordKeySize`, `CredentialVersion`, `GameSecurityVersion`, `EmailVerifiedAtUtc`) to `DBAccount`; add nullable `ArchiveVersion` and `GameBuildNumber` to `DBPlayer`. Extend `AssertProviderMetadataIsIgnored` to serialize each model and assert every listed property name is absent.

Use these exact interface methods:

```csharp
public interface IAccountStore
{
    bool TryQueryAccountByEmail(string email, out DBAccount account);
    AccountStoreResult InsertAccount(DBAccount account);
    AccountStoreResult ChangePlayerName(DBAccount account, string playerName);
    AccountStoreResult ChangePassword(DBAccount account, byte[] passwordHash, byte[] salt);
    AccountStoreResult ChangeUserLevel(DBAccount account, AccountUserLevel userLevel);
    AccountStoreResult ChangeFlags(DBAccount account, AccountFlags flags);
}

public interface IPlayerStore
{
    bool TryGetPlayerDbIdByName(string playerName, out ulong playerDbId, out string playerNameOut);
    bool TryGetPlayerName(ulong playerDbId, out string playerName);
    bool GetPlayerNames(Dictionary<ulong, string> playerNames);
    bool TryGetLastLogoutTime(ulong playerDbId, out long lastLogoutTime);
    PlayerStoreResult LoadPlayerData(DBAccount account);
    PlayerStoreResult SavePlayerData(DBAccount account);
}

public interface IGuildStore
{
    bool LoadGuilds(List<DBGuild> guilds);
    GuildStoreResult CreateGuild(DBGuild guild, DBGuildMember leader);
    GuildStoreResult ChangeGuildName(DBGuild guild, string name);
    GuildStoreResult ChangeGuildMotd(DBGuild guild, string motd);
    GuildStoreResult ApplyMembershipTransition(DBGuild guild, GuildMemberTransition transition);
    GuildStoreResult DeleteGuild(DBGuild guild);
}
```

- [ ] **Step 5: Adapt every implementation, caller, and test in the same contract-switch commit**

Change `JsonDBManager`, `SQLiteDBManager`, `StubDBManager`, the `PersistenceServicesTests` test stores, and the leaderboard player-store fake to implement the exact new interface signatures before running any test. Change `AccountManager`, `PlayerHandle`, `MasterGuild`, and `MasterGuildManager` so no production source calls removed `UpdateAccount`, `SaveGuild`, `SaveGuildMember`, or `DeleteGuildMember` methods. Update every affected existing test in the same commit. JSON returns `Failed` for unsupported account writes and `Success` for its existing guild no-ops. SQLite may initially map new intent methods to its existing writes; Task 3 strengthens conformance and transactional semantics. This keeps the entire solution buildable after the contract change.

- [ ] **Step 6: Run focused shared tests**

```bash
~/.dotnet/dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Release --filter "FullyQualifiedName~PersistenceServicesTests|FullyQualifiedName~DBAccountJsonSerializerTests"
```

Expected: PASS after updating the test-only capability fakes to implement the changed interfaces.

- [ ] **Step 7: Commit the shared contract slice**

```bash
git add src/MHServerEmu.DatabaseAccess src/MHServerEmu.DatabaseAccess.Tests src/MHServerEmu.PlayerManagement src/MHServerEmu.PlayerManagement.Tests src/MHServerEmu.Tests
git commit -m "feat(persistence): add checked store contracts"
```

## Task 2: Implement Shared Normalization and Aggregate Validation

**Files:**
- Create: `src/MHServerEmu.DatabaseAccess/Validation/IdentityNormalizer.cs`
- Create: `src/MHServerEmu.DatabaseAccess/Validation/PlayerAggregateValidator.cs`
- Modify: `src/MHServerEmu.DatabaseAccess/Models/DBEntityCollection.cs`
- Test: `src/MHServerEmu.DatabaseAccess.Tests/Capabilities/IdentityNormalizerTests.cs`
- Test: `src/MHServerEmu.DatabaseAccess.Tests/Models/PlayerAggregateValidatorTests.cs`

- [ ] **Step 1: Write failing normalization-vector tests**

```csharp
[Theory]
[InlineData("  Te\u0301ST@Example.COM  ", "tést@example.com")]
public void NormalizeEmail_UsesTrimNfcAndInvariantLowercase(string value, string expected)
{
    Assert.Equal(expected, IdentityNormalizer.NormalizeEmail(value));
}

[Theory]
[InlineData("Player12", "PLAYER12")]
public void NormalizePlayerName_UsesInvariantUppercase(string value, string expected)
{
    Assert.Equal(expected, IdentityNormalizer.NormalizePlayerName(value));
}

[Theory]
[InlineData("")]
[InlineData("too-long-player-name")]
[InlineData("has space")]
[InlineData("é")]
public void NormalizePlayerName_InvalidInput_ThrowsArgumentException(string value)
{
    Assert.Throws<ArgumentException>(() => IdentityNormalizer.NormalizePlayerName(value));
}

[Theory]
[InlineData("  Guild Name  ", "GUILD NAME")]
public void NormalizeGuildName_UsesTrimNfcAndInvariantUppercase(string value, string expected)
{
    Assert.Equal(expected, IdentityNormalizer.NormalizeGuildName(value));
}

[Fact]
public void Validate_RejectsItemParentedByItem()
{
    DBAccount account = CreateAccountWithItemParentedByItem();
    Assert.False(PlayerAggregateValidator.TryValidate(account, out _));
}
```

- [ ] **Step 2: Run validation tests and verify they fail**

```bash
~/.dotnet/dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Release --filter "FullyQualifiedName~IdentityNormalizerTests|FullyQualifiedName~PlayerAggregateValidatorTests"
```

Expected: FAIL because the normalizer and validator do not exist.

- [ ] **Step 3: Implement exact normalization-v1 methods**

```csharp
public static string NormalizeEmail(string value) => NormalizeText(value, "email").ToLowerInvariant();
public static string NormalizePlayerName(string value)
{
    if (string.IsNullOrEmpty(value) || value.Length > 16 || value.Any(character => char.IsAsciiLetterOrDigit(character) == false))
        throw new ArgumentException("Player names must be 1-16 ASCII alphanumeric characters.", nameof(value));
    return value.ToUpperInvariant();
}
public static string NormalizeGuildName(string value) => NormalizeText(value, "guild name").ToUpperInvariant();
```

`NormalizeText` must trim, normalize to `NormalizationForm.FormC`, reject any `char.IsControl` character, and reject a post-normalization length over 320. Do not use database collation or `citext`. `NormalizePlayerName` must independently require length 1-16 and every character between `A-Z`, `a-z`, or `0-9`, then return invariant uppercase; it cannot depend on PlayerManagement validation.

- [ ] **Step 4: Implement the complete entity graph validator**

Flatten `Avatars`, `TeamUps`, `Items`, and `ControlledEntities` into one ID map. Reject duplicate IDs across collections, null archives, slot outside `0..uint.MaxValue`, a missing parent, an account mismatch, cycles, or depth over one. Enforce this matrix: avatars/team-ups are roots; items are roots or children of avatar/team-up; controlled entities are avatar children only. For every entity with nonzero inventory prototype, reject a duplicate `(parent-or-account-root, inventory prototype, slot)` tuple.

- [ ] **Step 5: Run shared validation tests**

```bash
~/.dotnet/dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Release --filter "FullyQualifiedName~IdentityNormalizerTests|FullyQualifiedName~PlayerAggregateValidatorTests"
```

Expected: PASS, including NFC, controls, duplicate IDs, roots, parent-kind matrix, cycles, slots, and empty aggregate cases.

- [ ] **Step 6: Commit normalization and validation**

```bash
git add src/MHServerEmu.DatabaseAccess src/MHServerEmu.DatabaseAccess.Tests
git commit -m "feat(persistence): validate normalized aggregates"
```

## Task 3: Adapt JSON, SQLite, and Fakes to the Stronger Contracts

**Files:**
- Modify: `src/MHServerEmu.DatabaseAccess/Json/JsonDBManager.cs`
- Modify: `src/MHServerEmu.DatabaseAccess/SQLite/SQLiteDBManager.cs`
- Modify: `src/MHServerEmu.DatabaseAccess.Tests/SQLite/SQLiteDBManagerTests.cs`
- Modify: `src/MHServerEmu.PlayerManagement.Tests/StubDBManager.cs`
- Modify: `src/MHServerEmu.DatabaseAccess.Tests/Persistence/PersistenceServicesTests.cs`
- Modify: `src/MHServerEmu.Tests/Leaderboards/LeaderboardDatabasePlayerStoreTests.cs`
- Test: `src/MHServerEmu.DatabaseAccess.Tests/SQLite/SQLiteDBManagerTests.cs`
- Test: `src/MHServerEmu.DatabaseAccess.Tests/Json/DBAccountJsonSerializerTests.cs`

- [ ] **Step 1: Write provider-parameterized conformance cases**

```csharp
public void ChangePlayerName_ExistingAccount_UpdatesOnlyAfterSuccess()
{
    SQLiteDBManager store = CreateInitializedDatabase();
    DBAccount account = new("a@example.test", "Original", "password");
    Assert.Equal(AccountStoreResult.Success, store.InsertAccount(account));
    Assert.Equal(AccountStoreResult.Success, store.ChangePlayerName(account, "Renamed"));
    Assert.True(store.TryQueryAccountByEmail("a@example.test", out DBAccount loaded));
    Assert.Equal("Renamed", loaded.PlayerName);
}

public void CreateGuild_WritesGuildAndLeaderTogether()
{
    SQLiteDBManager store = CreateInitializedDatabase();
    var guild = new DBGuild(10, "Founders", "", 1, 1);
    var leader = new DBGuildMember(1, 10, 3);
    Assert.Equal(GuildStoreResult.Success, store.CreateGuild(guild, leader));
}
```

Use the existing SQLite temporary database factory in `SQLiteDBManagerTests`; unsupported JSON account writes must assert `Failed`, and JSON guild operations must retain no-op success. Tasks 7, 8, and 9 add PostgreSQL conformance cases alongside their respective stores.

- [ ] **Step 2: Run SQLite/JSON focused tests and verify behavioral failures**

```bash
~/.dotnet/dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Release --filter "FullyQualifiedName~SQLiteDBManagerTests|FullyQualifiedName~DBAccountJsonSerializerTests"
```

Expected: FAIL because SQLite does not yet classify final uniqueness races, make compound guild writes transactional, or preserve append-only `GetPlayerNames` behavior.

- [ ] **Step 3: Adapt JSON without changing serialized files**

Return `AccountStoreResult.Failed` for every unsupported account mutation. Return `PlayerStoreResult.Success` for its current default-account load/save behavior and `Failed` when saving a non-default account. Keep guild `CreateGuild`, change, transition, and delete methods as no-op `Success`, retaining current JSON behavior. Do not serialize the new metadata.

- [ ] **Step 4: Adapt SQLite while retaining schema version 6**

Map unique-account insert failures to `EmailConflict` or `PlayerNameConflict`: after catching a uniqueness exception, re-query normalized email and player name and return the matching conflict result; never collapse a recognized final race into `Failed`. Implement each account intent with an update limited to the affected columns; assign synthesized metadata only after success. Keep revisions at zero and `PersistenceState.Clean`.

Implement `CreateGuild` and `ApplyMembershipTransition` in one `SQLiteTransaction`: verify expected guild/member state, write all changes, and commit before mutating passed-in revision. Implement checked name/MOTD/delete operations with affected-row checks. `GetPlayerNames` must append and report whether the resulting dictionary is nonempty.

- [ ] **Step 5: Update all fakes and run conformance tests**

```bash
~/.dotnet/dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Release --filter "FullyQualifiedName~SQLiteDBManagerTests|FullyQualifiedName~DBAccountJsonSerializerTests"
~/.dotnet/dotnet test src/MHServerEmu.PlayerManagement.Tests/MHServerEmu.PlayerManagement.Tests.csproj --configuration Release --filter "FullyQualifiedName~StubDBManager"
```

Expected: PASS. The fake must record exactly one `GuildMemberTransition`, preserve dictionary contents in `GetPlayerNames`, and allow each result enum to be forced by tests.

- [ ] **Step 6: Commit compatibility adapters**

```bash
git add src/MHServerEmu.DatabaseAccess src/MHServerEmu.DatabaseAccess.Tests src/MHServerEmu.PlayerManagement.Tests src/MHServerEmu.Tests
git commit -m "refactor(persistence): adapt legacy stores to checked results"
```

## Task 4: Update Account and Player Callers for Intent-Specific Results

**Files:**
- Modify: `src/MHServerEmu.Core/Helpers/CryptographyHelper.cs`
- Modify: `src/MHServerEmu.PlayerManagement/Players/AccountManager.cs`
- Modify: `src/MHServerEmu.PlayerManagement/Players/PlayerHandle.cs`
- Modify: `src/MHServerEmu.Games/Network/PlayerConnection.cs`
- Test: `src/MHServerEmu.PlayerManagement.Tests/AccountManagerTests.cs`
- Test: `src/MHServerEmu.PlayerManagement.Tests/PlayerHandleTests.cs`

- [ ] **Step 1: Write failing account result-mapping tests**

```csharp
[Theory]
[InlineData(AccountStoreResult.PlayerNameConflict, AccountOperationResult.PlayerNameAlreadyUsed)]
[InlineData(AccountStoreResult.StaleRevision, AccountOperationResult.DatabaseError)]
[InlineData(AccountStoreResult.OutcomeUncertain, AccountOperationResult.DatabaseError)]
public void ChangeAccountPlayerName_StoreFailure_DoesNotMutateOrNotify(AccountStoreResult storeResult, AccountOperationResult expected)
{
    _database.ChangePlayerNameResult = storeResult;
    Assert.Equal(expected, _manager.ChangeAccountPlayerName("user@test", "Renamed"));
    Assert.Equal("Original", _database.Account.PlayerName);
    Assert.Empty(_notifier.Changes);
}
```

- [ ] **Step 2: Run caller tests and verify they fail**

```bash
~/.dotnet/dotnet test src/MHServerEmu.PlayerManagement.Tests/MHServerEmu.PlayerManagement.Tests.csproj --configuration Release --filter "FullyQualifiedName~AccountManagerTests|FullyQualifiedName~PlayerHandleTests"
```

Expected: FAIL because `AccountManager` still calls `UpdateAccount`.

- [ ] **Step 3: Expose existing PBKDF2 constants and migrate AccountManager**

Expose read-only `CryptographyHelper.PasswordIterationCount`, `PasswordKeySize`, and a `PasswordAlgorithm` metadata value while preserving `HashPassword` and `VerifyPassword` output.

Remove `TryUpdateAccount`. Call `ChangePlayerName`, `ChangePassword`, `ChangeUserLevel`, or `ChangeFlags` using the loaded account plus desired value. Map conflict results to existing user-facing `AccountOperationResult`; map stale, failed, invalid, and uncertain to `DatabaseError`. Publish player-name changes and security notifications only on `Success`.

- [ ] **Step 4: Update player result handling and archive diagnostics**

Treat `PlayerStoreResult.Success` as existing success and every other result as current failure. Preserve the loaded aggregate marker on `OutcomeUncertain`; do not attempt another save. Immediately after a successful database `Serializer.Transfer` in `PlayerConnection`, set `DBPlayer.ArchiveVersion = (int)ArchiveVersion.Current` and `DBPlayer.GameBuildNumber = (int)GameBuildNumber.Current` before invoking the store save.

- [ ] **Step 5: Run focused caller tests**

```bash
~/.dotnet/dotnet test src/MHServerEmu.PlayerManagement.Tests/MHServerEmu.PlayerManagement.Tests.csproj --configuration Release --filter "FullyQualifiedName~AccountManagerTests|FullyQualifiedName~PlayerHandleTests"
```

Expected: PASS, including rollback/no-notification behavior and current player save/load behavior.

- [ ] **Step 6: Commit caller migration**

```bash
git add src/MHServerEmu.Core src/MHServerEmu.Games src/MHServerEmu.PlayerManagement src/MHServerEmu.PlayerManagement.Tests
git commit -m "refactor(players): use checked persistence operations"
```

## Task 5: Add Immutable PostgreSQL Core Migration

**Files:**
- Create: `src/MHServerEmu.DatabaseAccess.PostgreSQL/Migrations/0002_CorePersistence.sql`
- Modify: `src/MHServerEmu.DatabaseAccess.PostgreSQL/MHServerEmu.DatabaseAccess.PostgreSQL.csproj`
- Modify: `src/MHServerEmu.DatabaseAccess.Tests/PostgreSQL/Migrations/PostgreSQLMigrationCatalogTests.cs`
- Modify: `src/MHServerEmu.DatabaseAccess.Tests/PostgreSQL/Migrations/PostgreSQLMigrationRunnerTests.cs`

- [ ] **Step 1: Write failing migration catalog/bootstrap assertions**

```csharp
[Fact]
public void LoadEmbedded_ReturnsFoundationAndCoreMigrations()
{
    PostgreSQLMigrationCatalog catalog = PostgreSQLMigrationCatalog.LoadEmbedded();
    Assert.Collection(catalog.Migrations,
        migration => Assert.Equal((1, "InitializePersistence"), (migration.Version, migration.Name)),
        migration => Assert.Equal((2, "CorePersistence"), (migration.Version, migration.Name)));
}

[PostgreSQLIntegrationFact]
public async Task RunAsync_FreshDatabase_CreatesCoreTables()
{
    await using NpgsqlDataSource dataSource = await _database.CreateDataSourceAsync();
    PostgreSQLMigrationResult result = await new PostgreSQLMigrationRunner(dataSource, PostgreSQLMigrationCatalog.LoadEmbedded(), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5)).RunAsync();
    await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync();
    string[] tables = await connection.QueryAsync<string>("SELECT tablename FROM pg_catalog.pg_tables WHERE schemaname = 'mhserveremu' ORDER BY tablename");
    Assert.True(result.Succeeded);
    Assert.Equal(new[] { "account", "application_metadata", "guild", "guild_member", "player_entity", "player_profile", "schema_migrations", "writer_fence" }, tables);
}
```

- [ ] **Step 2: Run migration tests and verify missing migration failure**

```bash
~/.dotnet/dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Release --filter "FullyQualifiedName~PostgreSQLMigrationCatalogTests|FullyQualifiedName~PostgreSQLMigrationRunnerTests"
```

Expected: FAIL because migration 0002 is absent.

- [ ] **Step 3: Add 0002 schema-qualified DDL**

Create named PostgreSQL constraints/indexes for normalized account email/player name; `player_profile` account cascade; single `player_entity` with owner-scoped self FK, kinds `0..3`, slot `0..4294967295`, and the partial `UNIQUE NULLS NOT DISTINCT` inventory index; guild normalized name; guild-member rank `1..3`, one membership per player, and one leader per guild. Use exact fresh credential values: algorithm `1`, format `1`, iterations `210000`, key size `64`, and 64-byte hash/salt checks. All audit timestamps default to `CURRENT_TIMESTAMP`; revisions default to zero. Do not include `BEGIN`, `COMMIT`, triggers, or test functions.

- [ ] **Step 4: Embed the resource**

```xml
<EmbeddedResource Include="Migrations\0002_CorePersistence.sql" LogicalName="Migrations.0002_CorePersistence.sql" />
```

- [ ] **Step 5: Run catalog tests and live migration tests**

```bash
~/.dotnet/dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Release --filter "FullyQualifiedName~PostgreSQLMigrationCatalogTests"
~/.dotnet/dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Release -p:Platform=x64 --filter "FullyQualifiedName~PostgreSQLMigrationRunnerTests"
```

Expected: catalog tests pass; live tests apply versions 1 and 2 idempotently. The test command uses the already-exported administrative connection string from the PostgreSQL integration precondition.

- [ ] **Step 6: Commit the migration**

```bash
git add src/MHServerEmu.DatabaseAccess.PostgreSQL src/MHServerEmu.DatabaseAccess.Tests/PostgreSQL/Migrations
git commit -m "feat(postgresql): add core persistence migration"
```

## Task 6: Build the Deadline-Bounded PostgreSQL Store Executor

**Files:**
- Create: `src/MHServerEmu.DatabaseAccess.PostgreSQL/IPostgreSQLTransactionCommitter.cs`
- Create: `src/MHServerEmu.DatabaseAccess.PostgreSQL/PostgreSQLStoreExecutor.cs`
- Create: `src/MHServerEmu.DatabaseAccess.PostgreSQL/PostgreSQLWriteOutcome.cs`
- Create: `src/MHServerEmu.DatabaseAccess.PostgreSQL/PostgreSQLWriteResult.cs`
- Create: `src/MHServerEmu.DatabaseAccess.Tests/PostgreSQL/Stores/PostgreSQLStoreTestFixture.cs`
- Modify: `src/MHServerEmu.DatabaseAccess.PostgreSQL/PostgreSQLProvider.cs`
- Modify: `src/MHServerEmu.DatabaseAccess.PostgreSQL/Locking/PostgreSQLWriterOwner.cs`
- Test: `src/MHServerEmu.DatabaseAccess.Tests/PostgreSQL/PostgreSQLStoreExecutorTests.cs`

- [ ] **Step 1: Write failing executor unit tests**

```csharp
[Fact]
public void ClassifyCommitFailure_CommitStarted_ReturnsOutcomeUncertain()
{
    PostgreSQLWriteResult result = PostgreSQLStoreExecutor.ClassifyFailure("AccountChange", commitStarted: true, new NpgsqlException());
    Assert.Equal(PostgreSQLWriteOutcome.OutcomeUncertain, result.Outcome);
}

[PostgreSQLIntegrationFact]
public async Task ExecuteWriteAsync_FencedWriter_DoesNotInvokeCallback()
{
    bool called = false;
    PostgreSQLWriteResult result = await CreateFencedExecutor().ExecuteWriteAsync("AccountChange", 30, (_, _, _) => { called = true; return Task.CompletedTask; });
    Assert.False(called);
    Assert.Equal(PostgreSQLWriteOutcome.Failed, result.Outcome);
}
```

- [ ] **Step 2: Run executor tests and verify missing type failure**

```bash
~/.dotnet/dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Release --filter FullyQualifiedName~PostgreSQLStoreExecutorTests
```

Expected: FAIL because the executor and committer seam do not exist.

- [ ] **Step 3: Implement the executor and commit-stage seam**

`IPostgreSQLTransactionCommitter.CommitAsync` accepts an `NpgsqlTransaction` and cancellation token; its production implementation calls `CommitAsync`. `PostgreSQLStoreExecutor` creates one `PostgreSQLOperationDeadline`, sets local `statement_timeout` and `lock_timeout` to remaining milliseconds, creates linked cancellation tokens for every phase, and distinguishes pre-commit exceptions from exceptions thrown by the committer. It always rolls back only when commit was not started. It returns a safe outcome/failure object and never logs parameters or exception messages. Extract its stage-only `ClassifyFailure(string operation, bool commitStarted, Exception exception)` as an internal pure method, allowing the uncertain classification test to run without a database.

Define the executor's exact public-internal result API:

```csharp
internal enum PostgreSQLWriteOutcome { Success, Failed, OutcomeUncertain }
internal readonly record struct PostgreSQLWriteResult(PostgreSQLWriteOutcome Outcome, PostgreSQLPersistenceFailure Failure)
{
    internal static PostgreSQLWriteResult Success() => new(PostgreSQLWriteOutcome.Success, null);
}
```

Define `CreateFencedExecutor` as a private nested integration-test helper in `PostgreSQLStoreExecutorTests`; it starts a fixture-backed provider then explicitly fences it before returning its executor. No unit test depends on an administrative connection string.

Pass the deadline into `PostgreSQLWriterOwner.ValidateTransactionAsync`; replace unbounded `pg_advisory_xact_lock_shared` with bounded polling or a transaction-local lock timeout derived from the remaining deadline.

- [ ] **Step 4: Expose executor/store construction only for integration tests**

Have `PostgreSQLProvider` construct one executor from its data source, writer owner, settings, and default committer. Add internal factory methods or `InternalsVisibleTo` only where existing PostgreSQL tests already use internals. Do not construct a persistence graph in `PersistenceComposition`.

`PostgreSQLStoreTestFixture` wraps `PostgreSQLTestDatabase`: `StartAsync` creates settings from the fixture, starts `PostgreSQLProvider` with `PostgreSQLMigrationCatalog.LoadEmbedded()`, and returns a disposable object exposing its started provider. It also supplies `CreateAccount(long id, string email, string playerName)` using 64-byte deterministic hash/salt arrays and `CreateEmptyAccount(long id)` for load tests. Task 7 extends the fixture with the account store; Tasks 8 and 9 add player and guild store properties respectively. Every later store test uses this fixture instead of undeclared helper types.

- [ ] **Step 5: Run executor and existing fencing tests**

```bash
~/.dotnet/dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Release --filter "FullyQualifiedName~PostgreSQLStoreExecutorTests|FullyQualifiedName~PostgreSQLWriterBootstrapTests"
```

Expected: PASS; existing writer-fence behavior remains unchanged.

- [ ] **Step 6: Commit executor foundation**

```bash
git add src/MHServerEmu.DatabaseAccess.PostgreSQL src/MHServerEmu.DatabaseAccess.Tests/PostgreSQL
git commit -m "feat(postgresql): bound store operations"
```

## Task 7: Implement and Verify PostgreSQL Account Store

**Files:**
- Create: `src/MHServerEmu.DatabaseAccess.PostgreSQL/Stores/PostgreSQLAccountStore.cs`
- Modify: `src/MHServerEmu.DatabaseAccess.PostgreSQL/PostgreSQLProvider.cs`
- Modify: `src/MHServerEmu.DatabaseAccess.Tests/PostgreSQL/Stores/PostgreSQLStoreTestFixture.cs`
- Test: `src/MHServerEmu.DatabaseAccess.Tests/PostgreSQL/Stores/PostgreSQLAccountStoreTests.cs`
- Test: `src/MHServerEmu.DatabaseAccess.Tests/Conformance/AccountStoreConformanceTests.cs`

- [ ] **Step 1: Write failing PostgreSQL account integration tests**

```csharp
[PostgreSQLIntegrationFact]
public async Task ChangePassword_MatchingRevision_IncrementsCredentialAndSecurityVersions()
{
    await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
    DBAccount account = fixture.CreateAccount(1, "account@test", "Account");
    Assert.Equal(AccountStoreResult.Success, fixture.Accounts.InsertAccount(account));

    Assert.Equal(AccountStoreResult.Success, fixture.Accounts.ChangePassword(account, new byte[64], new byte[64]));
    Assert.Equal(2, account.CredentialVersion);
    Assert.Equal(2, account.GameSecurityVersion);
    Assert.Equal(1, account.PersistenceRevision);
}

[PostgreSQLIntegrationFact]
public async Task InsertAccount_NormalizedDuplicateRace_ReturnsOneSuccessAndOneConflict()
{
    await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
    DBAccount left = fixture.CreateAccount(1, "Te\u0301st@Example.Test", "Left");
    DBAccount right = fixture.CreateAccount(2, "tést@example.test", "Right");
    AccountStoreResult[] results = await Task.WhenAll(Task.Run(() => fixture.Accounts.InsertAccount(left)), Task.Run(() => fixture.Accounts.InsertAccount(right)));
    Assert.Contains(AccountStoreResult.Success, results);
    Assert.Contains(AccountStoreResult.EmailConflict, results);
}
```

- [ ] **Step 2: Run account-store tests and verify they fail**

```bash
~/.dotnet/dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Release -p:Platform=x64 --filter "FullyQualifiedName~PostgreSQLAccountStoreTests|FullyQualifiedName~AccountStoreConformanceTests"
```

Expected: FAIL because `PostgreSQLAccountStore` does not exist.

- [ ] **Step 3: Implement normalized reads and insert**

Use schema-qualified SQL and `IdentityNormalizer` keys. Read all account columns and map metadata. Insert display/key columns and initial credential/security version `1`, revision `0`; classify named normalized constraints as `EmailConflict` or `PlayerNameConflict`. Preserve `bytea` exactly and support signed high-bit IDs.

Extend `PostgreSQLStoreTestFixture` with an `Accounts` property initialized from the started provider's executor and data source. Add `AccountStoreConformanceTests` in this task using the fixture for the same insert, lookup, and committed name-change cases exercised by SQLite in Task 3.

- [ ] **Step 4: Implement checked account intents**

For each mutation, use the executor and `UPDATE ... WHERE id = @id AND revision = @expectedRevision RETURNING revision, updated_at_utc, credential_version, game_security_version`. Change password increments both versions and clears password-expired; level and flag changes increment game-security only; player-name updates display/key. If zero rows return, issue a bounded read by ID: missing row is `AccountNotFound`; present row is `StaleRevision`. Apply returned values to the supplied object only after a successful commit. Mark it `OutcomeUncertain` only when the executor reports commit ambiguity, and make every later account intent return `OutcomeUncertain` without SQL while the account state is uncertain.

- [ ] **Step 5: Run account integration and shared conformance tests**

```bash
~/.dotnet/dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Release -p:Platform=x64 --filter "FullyQualifiedName~PostgreSQLAccountStoreTests|FullyQualifiedName~AccountStoreConformanceTests"
```

Expected: PASS for normalization, constraints, binary round trips, missing-after-load classification, stale revisions, duplicate races, version increments, and forced-uncertain retry rejection.

- [ ] **Step 6: Commit the account store**

```bash
git add src/MHServerEmu.DatabaseAccess.PostgreSQL src/MHServerEmu.DatabaseAccess.Tests
git commit -m "feat(postgresql): persist checked accounts"
```

## Task 8: Implement and Verify PostgreSQL Player Aggregate Store

**Files:**
- Create: `src/MHServerEmu.DatabaseAccess.PostgreSQL/Stores/PostgreSQLPlayerStore.cs`
- Modify: `src/MHServerEmu.DatabaseAccess.PostgreSQL/PostgreSQLProvider.cs`
- Modify: `src/MHServerEmu.DatabaseAccess.Tests/PostgreSQL/Stores/PostgreSQLStoreTestFixture.cs`
- Test: `src/MHServerEmu.DatabaseAccess.Tests/PostgreSQL/Stores/PostgreSQLPlayerStoreTests.cs`
- Test: `src/MHServerEmu.DatabaseAccess.Tests/Conformance/PlayerStoreConformanceTests.cs`

- [ ] **Step 1: Write failing aggregate round-trip and rejection tests**

```csharp
[PostgreSQLIntegrationFact]
public async Task SaveAndLoad_NestedAggregate_ReconstructsRootContainersAndDeletesStaleRows()
{
    await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
    DBAccount account = fixture.CreateNestedAggregate(1);
    Assert.Equal(AccountStoreResult.Success, fixture.Accounts.InsertAccount(account));
    Assert.Equal(PlayerStoreResult.Success, fixture.Players.SavePlayerData(account));
    account.Items.Clear();
    Assert.Equal(PlayerStoreResult.Success, fixture.Players.SavePlayerData(account));

    DBAccount loaded = fixture.CreateEmptyAccount(account.Id);
    Assert.Equal(PlayerStoreResult.Success, fixture.Players.LoadPlayerData(loaded));
    Assert.Empty(loaded.Items);
}

[PostgreSQLIntegrationFact]
public async Task Save_InvalidCrossCategoryDuplicate_ReturnsInvalidAggregate()
{
    await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
    DBAccount account = fixture.CreateCrossCategoryDuplicateAggregate(2);
    Assert.Equal(AccountStoreResult.Success, fixture.Accounts.InsertAccount(account));
    Assert.Equal(PlayerStoreResult.InvalidAggregate, fixture.Players.SavePlayerData(account));
}
```

- [ ] **Step 2: Run player-store tests and verify they fail**

```bash
~/.dotnet/dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Release -p:Platform=x64 --filter "FullyQualifiedName~PostgreSQLPlayerStoreTests|FullyQualifiedName~PlayerStoreConformanceTests"
```

Expected: FAIL because `PostgreSQLPlayerStore` does not exist.

- [ ] **Step 3: Implement player reads and atomic temporary aggregate mapping**

Extend `PostgreSQLStoreTestFixture` with a `Players` property and `CreateNestedAggregate`, `CreateEmptyAccount`, and `CreateCrossCategoryDuplicateAggregate`; these methods construct the exact entities used in the tests above. Read the account existence first. Return `AccountNotFound` when absent; create `new DBPlayer(account.Id)` on a missing profile. Load every owner entity in one query, map null parent to account root `ContainerDbGuid`, partition by kind, validate the temporary aggregate, and replace the target account only after all mapping succeeds. Implement name and logout reads with normalized player names.

- [ ] **Step 4: Implement one locked aggregate write**

Validate before opening the write transaction. Return `InvalidAggregate` when validation fails. Return `OutcomeUncertain` without opening SQL when the supplied player state is already uncertain. In one executor transaction, acquire a signed-account advisory transaction lock, insert or compare-and-swap the profile revision, bulk-upsert parent rows then child rows with owner/kind checks, and delete owner rows absent from the submitted ID array. Use empty arrays safely. Commit before assigning the new profile revision and clean persistence state to the model.

- [ ] **Step 5: Add scale, race, and uncertain-commit coverage**

Add a thousand-entity round trip under the configured command timeout; two competing save attempts where only one revision succeeds; and an integration-created deferred constraint trigger that calls `pg_terminate_backend(pg_backend_pid())` during commit. The latter must return `OutcomeUncertain` and reject another save on that object. A unit test of the injected committer covers classification without a live database.

- [ ] **Step 6: Run player tests and commit**

```bash
~/.dotnet/dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Release -p:Platform=x64 --filter "FullyQualifiedName~PostgreSQLPlayerStoreTests|FullyQualifiedName~PlayerStoreConformanceTests|FullyQualifiedName~PlayerAggregateValidatorTests"
git add src/MHServerEmu.DatabaseAccess.PostgreSQL src/MHServerEmu.DatabaseAccess.Tests
git commit -m "feat(postgresql): persist player aggregates"
```

Expected: all specified player tests pass.

## Task 9: Implement and Verify PostgreSQL Guild Store

**Files:**
- Create: `src/MHServerEmu.DatabaseAccess.PostgreSQL/Stores/PostgreSQLGuildStore.cs`
- Modify: `src/MHServerEmu.DatabaseAccess.PostgreSQL/PostgreSQLProvider.cs`
- Modify: `src/MHServerEmu.DatabaseAccess.Tests/PostgreSQL/Stores/PostgreSQLStoreTestFixture.cs`
- Test: `src/MHServerEmu.DatabaseAccess.Tests/PostgreSQL/Stores/PostgreSQLGuildStoreTests.cs`
- Test: `src/MHServerEmu.DatabaseAccess.Tests/Conformance/GuildStoreConformanceTests.cs`

- [ ] **Step 1: Write failing guild integration tests**

```csharp
[PostgreSQLIntegrationFact]
public async Task ApplyMembershipTransition_LeadershipTransfer_UpdatesBothMembersAndRevisionAtomically()
{
    await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
    DBAccount leaderAccount = fixture.CreateAccount(1, "leader@test", "Leader");
    DBAccount successorAccount = fixture.CreateAccount(2, "officer@test", "Officer");
    Assert.Equal(AccountStoreResult.Success, fixture.Accounts.InsertAccount(leaderAccount));
    Assert.Equal(AccountStoreResult.Success, fixture.Accounts.InsertAccount(successorAccount));
    DBGuild guild = new(10, "Founders", "", 1, 1);
    Assert.Equal(GuildStoreResult.Success, fixture.Guilds.CreateGuild(guild, new DBGuildMember(1, 10, 3)));
    Assert.Equal(GuildStoreResult.Success, fixture.Guilds.ApplyMembershipTransition(guild, new GuildMemberTransition(10, 0, new GuildMemberChange(2, null, 2))));
    GuildMemberTransition transition = new(guild.Id, guild.PersistenceRevision,
        new GuildMemberChange(1, 3, 2), new GuildMemberChange(2, 2, 3));

    Assert.Equal(GuildStoreResult.Success, fixture.Guilds.ApplyMembershipTransition(guild, transition));
    Assert.Equal(2, guild.PersistenceRevision);
}

[PostgreSQLIntegrationFact]
public async Task CreateGuild_DuplicateNormalizedName_ReturnsNameConflictWithoutLeaderRow()
{
    await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
    foreach (DBAccount account in new[] { fixture.CreateAccount(1, "one@test", "One"), fixture.CreateAccount(2, "two@test", "Two") })
        Assert.Equal(AccountStoreResult.Success, fixture.Accounts.InsertAccount(account));
    Assert.Equal(GuildStoreResult.Success, fixture.Guilds.CreateGuild(new DBGuild(10, "Founders", "", 1, 1), new DBGuildMember(1, 10, 3)));
    Assert.Equal(GuildStoreResult.NameConflict, fixture.Guilds.CreateGuild(new DBGuild(11, " founders ", "", 2, 1), new DBGuildMember(2, 11, 3)));
    List<DBGuild> guilds = new();
    Assert.True(fixture.Guilds.LoadGuilds(guilds));
    Assert.Single(guilds);
    Assert.DoesNotContain(guilds.Single().Members, member => member.PlayerDbGuid == 2);
}
```

- [ ] **Step 2: Run guild-store tests and verify they fail**

```bash
~/.dotnet/dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Release -p:Platform=x64 --filter "FullyQualifiedName~PostgreSQLGuildStoreTests|FullyQualifiedName~GuildStoreConformanceTests"
```

Expected: FAIL because `PostgreSQLGuildStore` does not exist.

- [ ] **Step 3: Implement guild load and atomic creation**

Load guilds and members with schema-qualified queries; schema constraints mean no orphan is silently ignored. `CreateGuild` validates normalized name and leader rank `3`, inserts guild and leader in one executor transaction, maps the named unique constraint to `NameConflict`, and updates the supplied guild revision only after commit.

Extend `PostgreSQLStoreTestFixture` with a `Guilds` property and add `GuildStoreConformanceTests` with the SQLite-compatible create/load/name/MOTD/member/delete cases after this store exists.

- [ ] **Step 4: Implement checked mutations and transitions**

Name/MOTD/deletion use `WHERE id = @id AND revision = @expectedRevision`. On zero rows, issue a bounded read: an absent guild returns `GuildNotFound`; a present guild returns `StaleRevision`. `ApplyMembershipTransition` rejects an uncertain guild before SQL, locks the guild row, checks revision, verifies every expected member presence/rank, calculates that the committed membership set has exactly one leader, rejects another-guild membership, applies deletion/demotion before insertion/promotion to satisfy the one-leader index, increments revision once, and commits atomically. `DeleteGuild` relies on foreign-key cascade and uses the same missing-versus-stale classification. Any commit ambiguity marks the supplied guild uncertain and every later guild write returns `OutcomeUncertain` without SQL.

- [ ] **Step 5: Run guild integration and conformance tests**

```bash
~/.dotnet/dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Release -p:Platform=x64 --filter "FullyQualifiedName~PostgreSQLGuildStoreTests|FullyQualifiedName~GuildStoreConformanceTests"
```

Expected: PASS for initial atomic creation, normalized conflict, join/leave, transfer, missing-after-load and stale revision classification, membership conflict, cascade deletion, and an uncertain guild write followed by no-SQL retry rejection.

- [ ] **Step 6: Commit the guild store**

```bash
git add src/MHServerEmu.DatabaseAccess.PostgreSQL src/MHServerEmu.DatabaseAccess.Tests
git commit -m "feat(postgresql): persist checked guilds"
```

## Task 10: Persist Guilds Before Runtime Publication

**Files:**
- Modify: `src/MHServerEmu.PlayerManagement/Social/MasterGuild.cs`
- Modify: `src/MHServerEmu.PlayerManagement/Social/MasterGuildManager.cs`
- Modify: `src/MHServerEmu.PlayerManagement.Tests/StubDBManager.cs`
- Modify: `src/MHServerEmu.PlayerManagement.Tests/GuildPersistenceInjectionTests.cs`
- Create: `src/MHServerEmu.PlayerManagement.Tests/GuildPersistBeforePublishTests.cs`

- [ ] **Step 1: Write failing persist-before-publish tests**

```csharp
[Fact]
public void ChangeMotd_StoreFailure_DoesNotMutateOrBroadcast()
{
    _database.ChangeGuildMotdResult = GuildStoreResult.StaleRevision;

    GuildChangeMotdResultCode result = _guild.ChangeMotd(_leader, "New MOTD");

    Assert.Equal(GuildChangeMotdResultCode.eGCMotdRCGuildInErrorState, result);
    Assert.Equal("Old MOTD", _guild.Motd);
    Assert.Empty(_gameMessages);
}

[Fact]
public void TransferLeadership_StoreFailure_LeavesBothRanksAndLeaderUnchanged()
{
    _database.ApplyMembershipTransitionResult = GuildStoreResult.Failed;
    Assert.Equal(GuildChangeMemberResultCode.eGCMRCGuildInErrorState, _guild.ChangeMember(_leader, _successor.PlayerDbId, GuildMembership.eGMLeader));
    Assert.Equal((long)GuildMembership.eGMLeader, _leaderData.Membership);
    Assert.Equal((long)GuildMembership.eGMOfficer, _successorData.Membership);
    Assert.Empty(_gameMessages);
}
```

- [ ] **Step 2: Run guild caller tests and verify they fail**

```bash
~/.dotnet/dotnet test src/MHServerEmu.PlayerManagement.Tests/MHServerEmu.PlayerManagement.Tests.csproj --configuration Release --filter "FullyQualifiedName~GuildPersistenceInjectionTests|FullyQualifiedName~GuildPersistBeforePublishTests"
```

Expected: FAIL because `MasterGuild` mutates and broadcasts before persistence.

- [ ] **Step 3: Move each mutation behind a successful store result**

For initial guild formation call `CreateGuild(guild, leader)` before adding the guild to manager/registry maps. For name/MOTD, call the checked store method before changing `_data`, invalidating cache, or sending messages. For join and member changes, construct exact `GuildMemberTransition` values from existing ranks and call the store before changing `_leader`, `_members`, player guild/chat state, cache, manager maps, or messages. For sole-leader departure call checked `DeleteGuild` before removing the guild.

- [ ] **Step 4: Apply the approved protocol mappings**

Map every non-success result exactly: name `NameConflict` to `eGCNRCDuplicateName`; name `GuildNotFound` or `InvalidData` to `eGCNRCInvalidGuild`; MOTD `GuildNotFound` or `InvalidData` to `eGCMotdRCInvalidGuild`; membership `GuildNotFound` to `eGCMRCGuildInErrorState`; membership `InvalidData` to `eGCMRCInternalError`; membership `MembershipConflict` to `eGCMRCGuildInErrorState`; join `MembershipConflict` to `eGRIRAlreadyInOtherGuild`; and all stale, failed, or uncertain name/MOTD/member results to their respective `GuildInErrorState` codes. Other failed joins map to `eGRIRCInternalError`. Never publish on any non-success result.

- [ ] **Step 5: Run focused PlayerManagement tests**

```bash
~/.dotnet/dotnet test src/MHServerEmu.PlayerManagement.Tests/MHServerEmu.PlayerManagement.Tests.csproj --configuration Release --filter "FullyQualifiedName~GuildPersistenceInjectionTests|FullyQualifiedName~GuildPersistBeforePublishTests|FullyQualifiedName~AccountManagerTests"
```

Expected: PASS, including creation, join, transfer, deletion, stale result, and no-publication failure cases.

- [ ] **Step 6: Commit runtime guild ordering**

```bash
git add src/MHServerEmu.PlayerManagement src/MHServerEmu.PlayerManagement.Tests
git commit -m "fix(guilds): persist before publishing changes"
```

## Task 11: Run Complete Regression and Live PostgreSQL Matrix

**Files:**
- Modify only if a test proves a focused defect: files named by the failing test.
- Verify: `src/MHServerEmu/Persistence/PersistenceComposition.cs`
- Verify: `src/MHServerEmu.Tests/Persistence/PersistenceCompositionTests.cs`
- Verify: `.github/workflows/verify.yml`

- [ ] **Step 1: Add/adjust the PostgreSQL 16/17 integration fixture only as required**

Ensure test helper startup applies the full embedded catalog, exposes a started provider plus focused stores, disposes stores/data source/lock connections before database drop, and creates the deferred trigger only inside the uncertain-commit test. The trigger must be dropped automatically with its ephemeral database.

- [ ] **Step 2: Verify the PostgreSQL activation gate remains intact**

```bash
~/.dotnet/dotnet test src/MHServerEmu.Tests/MHServerEmu.Tests.csproj --configuration Release --filter FullyQualifiedName~PersistenceCompositionTests
```

Expected: PASS; `Provider=PostgreSQL` still reports that the provider is incomplete without opening a connection.

- [ ] **Step 3: Run complete Release build and unit suite**

```bash
~/.dotnet/dotnet build MHServerEmu.sln --configuration Release --no-restore
~/.dotnet/dotnet test MHServerEmu.sln --configuration Release --no-build
```

Expected: zero warnings/errors and all non-credential-gated tests pass.

- [ ] **Step 4: Run PostgreSQL 16 live integration suite**

```bash
docker rm -f "mhserveremu-pg16-pr1b" 2>/dev/null || true
docker run -d --name "mhserveremu-pg16-pr1b" -e POSTGRES_USER=postgres -e POSTGRES_PASSWORD=postgres -e POSTGRES_DB=postgres -p "127.0.0.1::5432" --health-cmd "pg_isready -U postgres -d postgres" --health-interval 2s --health-timeout 5s --health-retries 30 postgres:16
until [ "$(docker inspect --format '{{.State.Health.Status}}' "mhserveremu-pg16-pr1b")" = "healthy" ]; do sleep 1; done
PORT="$(docker port "mhserveremu-pg16-pr1b" 5432/tcp | cut -d: -f2)"
MHSERVEREMU_POSTGRESQL_TEST_ADMIN_CONNECTION_STRING="Host=127.0.0.1;Port=${PORT};Database=postgres;Username=postgres;Password=postgres" ~/.dotnet/dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Release -p:Platform=x64 --filter "Category=PostgreSQLIntegration"
docker rm -f "mhserveremu-pg16-pr1b" 2>/dev/null || true
```

Expected: all tagged PostgreSQL integration tests pass with zero skips.

- [ ] **Step 5: Repeat on PostgreSQL 17**

```bash
docker rm -f "mhserveremu-pg17-pr1b" 2>/dev/null || true
docker run -d --name "mhserveremu-pg17-pr1b" -e POSTGRES_USER=postgres -e POSTGRES_PASSWORD=postgres -e POSTGRES_DB=postgres -p "127.0.0.1::5432" --health-cmd "pg_isready -U postgres -d postgres" --health-interval 2s --health-timeout 5s --health-retries 30 postgres:17
until [ "$(docker inspect --format '{{.State.Health.Status}}' "mhserveremu-pg17-pr1b")" = "healthy" ]; do sleep 1; done
PORT="$(docker port "mhserveremu-pg17-pr1b" 5432/tcp | cut -d: -f2)"
MHSERVEREMU_POSTGRESQL_TEST_ADMIN_CONNECTION_STRING="Host=127.0.0.1;Port=${PORT};Database=postgres;Username=postgres;Password=postgres" ~/.dotnet/dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj --configuration Release -p:Platform=x64 --filter "Category=PostgreSQLIntegration"
docker rm -f "mhserveremu-pg17-pr1b"
```

Expected: all tagged PostgreSQL integration tests pass with zero skips.

- [ ] **Step 6: Inspect the PR diff and create the final commit only when needed**

```bash
git diff --check origin/release/1.0.1...HEAD
```

Expected: no whitespace errors, no runtime override/config secrets, no activation-gate change, and only planned PR 1B files.

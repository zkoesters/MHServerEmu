using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.DatabaseAccess.PostgreSQL.Configuration;
using MHServerEmu.DatabaseAccess.Tests.Conformance;
using MHServerEmu.DatabaseAccess.Tests.PostgreSQL.Migrations;
using Npgsql;

namespace MHServerEmu.DatabaseAccess.Tests.PostgreSQL.Stores
{
    [Trait("Category", "PostgreSQLIntegration")]
    [Collection("PostgreSQL migration integration")]
    public class PostgreSQLAccountStoreTests
    {
        private readonly PostgreSQLTestDatabase _database;

        public PostgreSQLAccountStoreTests(PostgreSQLTestDatabase database)
        {
            _database = database;
        }

        [PostgreSQLIntegrationFact]
        public async Task InsertAndLookup_NormalizedEmail_PreservesAllAccountData()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            DBAccount account = fixture.CreateAccount(unchecked((long)0xE000000000000001), "  User@Example.Test  ", "PlayerOne");
            account.PasswordHash = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
            account.Salt = Enumerable.Range(64, 64).Select(value => (byte)value).ToArray();
            account.UserLevel = AccountUserLevel.Admin;
            account.Flags = AccountFlags.IsBanned | AccountFlags.BypassLoginQueue;
            account.EmailVerifiedAtUtc = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            account.CredentialVersion = 0;
            account.GameSecurityVersion = 43;

            Assert.Equal(AccountStoreResult.Success, fixture.AccountStore.InsertAccount(account));
            Assert.True(fixture.AccountStore.TryQueryAccountByEmail("user@example.test", out DBAccount stored));

            Assert.Equal(account.Id, stored.Id);
            Assert.Equal(account.Email, stored.Email);
            Assert.Equal(account.PlayerName, stored.PlayerName);
            Assert.Equal(account.PasswordHash, stored.PasswordHash);
            Assert.Equal(account.Salt, stored.Salt);
            Assert.Equal(account.UserLevel, stored.UserLevel);
            Assert.Equal(account.Flags, stored.Flags);
            Assert.Equal(account.PasswordAlgorithm, stored.PasswordAlgorithm);
            Assert.Equal(account.PasswordFormatVersion, stored.PasswordFormatVersion);
            Assert.Equal(account.PasswordIterations, stored.PasswordIterations);
            Assert.Equal(account.PasswordKeySize, stored.PasswordKeySize);
            Assert.Equal(1, account.CredentialVersion);
            Assert.Equal(1, account.GameSecurityVersion);
            Assert.Equal(1, stored.CredentialVersion);
            Assert.Equal(1, stored.GameSecurityVersion);
            Assert.Equal(0, stored.PersistenceRevision);
            Assert.Equal(account.EmailVerifiedAtUtc, stored.EmailVerifiedAtUtc);
            Assert.NotNull(stored.CreatedAtUtc);
            Assert.NotNull(stored.UpdatedAtUtc);
        }

        [PostgreSQLIntegrationFact]
        public async Task InsertAccount_NormalizedIdentityRace_MapsNamedUniqueConstraints()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            DBAccount existing = fixture.CreateAccount(1, "existing@example.test", "PlayerOne");
            DBAccount first = fixture.CreateAccount(2, "race@example.test", "PlayerTwo");
            DBAccount duplicateEmail = fixture.CreateAccount(3, "RACE@example.test", "PlayerThree");
            DBAccount duplicatePlayerName = fixture.CreateAccount(4, "other@example.test", "playerone");
            Assert.Equal(AccountStoreResult.Success, fixture.AccountStore.InsertAccount(existing));

            AccountStoreResult[] results = await Task.WhenAll(
                Task.Run(() => fixture.AccountStore.InsertAccount(first)),
                Task.Run(() => fixture.AccountStore.InsertAccount(duplicateEmail)));

            Assert.Contains(AccountStoreResult.Success, results);
            Assert.Contains(AccountStoreResult.EmailConflict, results);
            Assert.Equal(AccountStoreResult.PlayerNameConflict, fixture.AccountStore.InsertAccount(duplicatePlayerName));
        }

        [PostgreSQLIntegrationFact]
        public async Task AccountStoreConformance_IdentityRoundTripAndConflicts()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);

            AccountStoreConformanceTests.AssertIdentityRoundTripAndConflicts(
                fixture.AccountStore,
                fixture.CreateAccount(1, "account@example.test", "PlayerOne"),
                fixture.CreateAccount(2, "ACCOUNT@example.test", "PlayerTwo"),
                fixture.CreateAccount(3, "other@example.test", "playerone"),
                "PlayerRenamed");
        }

        [PostgreSQLIntegrationFact]
        public async Task AccountIntentWrites_ApplyCommittedValuesAndVersionIncrements()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            DBAccount account = fixture.CreateAccount(1, "account@example.test", "PlayerOne");
            account.Flags = AccountFlags.IsPasswordExpired | AccountFlags.BypassLoginQueue;
            Assert.Equal(AccountStoreResult.Success, fixture.AccountStore.InsertAccount(account));

            Assert.Equal(AccountStoreResult.Success, fixture.AccountStore.ChangePassword(account, Enumerable.Repeat((byte)0xA1, 64).ToArray(), Enumerable.Repeat((byte)0xB2, 64).ToArray()));
            Assert.Equal(1, account.PersistenceRevision);
            Assert.Equal(2, account.CredentialVersion);
            Assert.Equal(2, account.GameSecurityVersion);
            Assert.Equal(AccountFlags.BypassLoginQueue, account.Flags);
            Assert.Equal(AccountStoreResult.Success, fixture.AccountStore.ChangeUserLevel(account, AccountUserLevel.Admin));
            Assert.Equal(2, account.PersistenceRevision);
            Assert.Equal(3, account.GameSecurityVersion);
            Assert.Equal(AccountStoreResult.Success, fixture.AccountStore.ChangeFlags(account, AccountFlags.IsBanned));
            Assert.Equal(3, account.PersistenceRevision);
            Assert.Equal(4, account.GameSecurityVersion);
            Assert.Equal(AccountStoreResult.Success, fixture.AccountStore.ChangePlayerName(account, "PlayerTwo"));
            Assert.Equal(4, account.PersistenceRevision);
            Assert.Equal("PlayerTwo", account.PlayerName);

            Assert.True(fixture.AccountStore.TryQueryAccountByEmail(account.Email, out DBAccount stored));
            Assert.Equal(account.PasswordHash, stored.PasswordHash);
            Assert.Equal(account.Salt, stored.Salt);
            Assert.Equal(AccountUserLevel.Admin, stored.UserLevel);
            Assert.Equal(AccountFlags.IsBanned, stored.Flags);
            Assert.Equal(2, stored.CredentialVersion);
            Assert.Equal(4, stored.GameSecurityVersion);
            Assert.Equal(4, stored.PersistenceRevision);
        }

        [PostgreSQLIntegrationFact]
        public async Task AccountIntentWrites_StaleAndMissingAccounts_DoNotMutateModels()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            DBAccount account = fixture.CreateAccount(1, "account@example.test", "PlayerOne");
            Assert.Equal(AccountStoreResult.Success, fixture.AccountStore.InsertAccount(account));
            Assert.True(fixture.AccountStore.TryQueryAccountByEmail(account.Email, out DBAccount stale));
            Assert.Equal(AccountStoreResult.Success, fixture.AccountStore.ChangeFlags(account, AccountFlags.IsBanned));

            Assert.Equal(AccountStoreResult.StaleRevision, fixture.AccountStore.ChangeUserLevel(stale, AccountUserLevel.Admin));
            Assert.Equal(AccountUserLevel.User, stale.UserLevel);
            Assert.Equal(0, stale.PersistenceRevision);

            DBAccount missing = fixture.CreateAccount(99, "missing@example.test", "Missing");
            Assert.Equal(AccountStoreResult.AccountNotFound, fixture.AccountStore.ChangePlayerName(missing, "Changed"));
            Assert.Equal("Missing", missing.PlayerName);
        }

        [PostgreSQLIntegrationFact]
        public async Task AccountIntentWrites_OutcomeUncertain_ReturnWithoutExecutingSql()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            DBAccount account = fixture.CreateAccount(1, "account@example.test", "PlayerOne");
            Assert.Equal(AccountStoreResult.Success, fixture.AccountStore.InsertAccount(account));
            DateTime? updatedAtUtc = account.UpdatedAtUtc;

            await CreateDeferredBackendTerminationAsync(fixture.Provider.DataSource);
            try
            {
                Assert.Equal(AccountStoreResult.OutcomeUncertain, fixture.AccountStore.ChangeFlags(account, AccountFlags.IsBanned));
                Assert.Equal(PersistenceState.OutcomeUncertain, account.PersistenceState);
                Assert.Equal(AccountFlags.None, account.Flags);
                Assert.Equal(0, account.PersistenceRevision);
                Assert.Equal(1, account.GameSecurityVersion);
                Assert.Equal(updatedAtUtc, account.UpdatedAtUtc);
            }
            finally
            {
                await DropDeferredBackendTerminationAsync(fixture.Provider.DataSource);
            }

            Assert.True(fixture.AccountStore.TryQueryAccountByEmail(account.Email, out DBAccount persistedAfterAmbiguity));
            fixture.Provider.FenceForTest();
            Assert.Equal(AccountStoreResult.OutcomeUncertain, fixture.AccountStore.ChangeUserLevel(account, AccountUserLevel.Admin));
            Assert.True(fixture.AccountStore.TryQueryAccountByEmail(account.Email, out DBAccount persistedAfterRetry));
            Assert.Equal(persistedAfterAmbiguity.UserLevel, persistedAfterRetry.UserLevel);
            Assert.Equal(persistedAfterAmbiguity.Flags, persistedAfterRetry.Flags);
            Assert.Equal(persistedAfterAmbiguity.PersistenceRevision, persistedAfterRetry.PersistenceRevision);
        }

        [PostgreSQLIntegrationFact]
        public async Task ReconcileAccount_UncertainObject_ReplacesScalarsButPreservesPlayerAggregate()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            DBAccount persisted = fixture.CreateAccount(1, "persisted@example.test", "PersistedPlayer");
            persisted.PasswordHash = [0x01, 0x02];
            persisted.Salt = [0x03, 0x04];
            persisted.UserLevel = AccountUserLevel.Admin;
            persisted.Flags = AccountFlags.IsBanned;
            persisted.EmailVerifiedAtUtc = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            Assert.Equal(AccountStoreResult.Success, fixture.AccountStore.InsertAccount(persisted));

            DBAccount uncertain = fixture.CreateAccount(persisted.Id, "stale@example.test", "StalePlayer");
            uncertain.PasswordHash = [0x10];
            uncertain.Salt = [0x11];
            uncertain.UserLevel = AccountUserLevel.User;
            uncertain.Flags = AccountFlags.IsArchived;
            uncertain.PasswordAlgorithm = "stale";
            uncertain.PasswordFormatVersion = 99;
            uncertain.PasswordIterations = 98;
            uncertain.PasswordKeySize = 97;
            uncertain.CredentialVersion = 96;
            uncertain.GameSecurityVersion = 95;
            uncertain.PersistenceRevision = 94;
            uncertain.EmailVerifiedAtUtc = null;
            uncertain.CreatedAtUtc = null;
            uncertain.UpdatedAtUtc = null;
            uncertain.PersistenceState = PersistenceState.OutcomeUncertain;
            DBPlayer player = new(uncertain.Id);
            uncertain.Player = player;
            Assert.True(uncertain.Avatars.Add(new() { DbGuid = 2, ContainerDbGuid = uncertain.Id }));

            Assert.Equal(AccountStoreResult.Success, fixture.AccountStore.ReconcileAccount(uncertain));

            Assert.Equal(persisted.Email, uncertain.Email);
            Assert.Equal(persisted.PlayerName, uncertain.PlayerName);
            Assert.Equal(persisted.PasswordHash, uncertain.PasswordHash);
            Assert.Equal(persisted.Salt, uncertain.Salt);
            Assert.Equal(persisted.UserLevel, uncertain.UserLevel);
            Assert.Equal(persisted.Flags, uncertain.Flags);
            Assert.Equal(persisted.PasswordAlgorithm, uncertain.PasswordAlgorithm);
            Assert.Equal(persisted.PasswordFormatVersion, uncertain.PasswordFormatVersion);
            Assert.Equal(persisted.PasswordIterations, uncertain.PasswordIterations);
            Assert.Equal(persisted.PasswordKeySize, uncertain.PasswordKeySize);
            Assert.Equal(persisted.CredentialVersion, uncertain.CredentialVersion);
            Assert.Equal(persisted.GameSecurityVersion, uncertain.GameSecurityVersion);
            Assert.Equal(persisted.PersistenceRevision, uncertain.PersistenceRevision);
            Assert.Equal(persisted.EmailVerifiedAtUtc, uncertain.EmailVerifiedAtUtc);
            Assert.Equal(persisted.CreatedAtUtc, uncertain.CreatedAtUtc);
            Assert.Equal(persisted.UpdatedAtUtc, uncertain.UpdatedAtUtc);
            Assert.Same(player, uncertain.Player);
            Assert.Single(uncertain.Avatars.Entries);
            Assert.Equal(PersistenceState.Clean, uncertain.PersistenceState);
        }

        [PostgreSQLIntegrationFact]
        public async Task AccountStoreConformance_ReconciliationNoOpAndMissingAccount()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            DBAccount clean = fixture.CreateAccount(1, "clean@example.test", "CleanPlayer");
            DBAccount missing = fixture.CreateAccount(2, "missing@example.test", "MissingPlayer");

            AccountStoreConformanceTests.AssertReconciliationNoOpAndMissingAccount(fixture.AccountStore, clean, missing);
        }

        [PostgreSQLIntegrationFact]
        public async Task Lookup_ExhaustedPool_ReturnsAtOperationDeadline()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database, new PostgreSQLConfig
            {
                MaxPoolSize = 4,
                ConnectTimeoutSeconds = 5,
                OperationTimeoutSeconds = 1,
            });
            await using NpgsqlConnection first = await fixture.Provider.DataSource.OpenConnectionAsync();
            await using NpgsqlConnection second = await fixture.Provider.DataSource.OpenConnectionAsync();
            await using NpgsqlConnection third = await fixture.Provider.DataSource.OpenConnectionAsync();
            await using NpgsqlConnection fourth = await fixture.Provider.DataSource.OpenConnectionAsync();

            Task<bool> lookup = Task.Run(() => fixture.AccountStore.TryQueryAccountByEmail("missing@example.test", out _));

            Assert.False(await lookup.WaitAsync(TimeSpan.FromSeconds(2)));
        }

        private static async Task CreateDeferredBackendTerminationAsync(NpgsqlDataSource dataSource)
        {
            await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync();
            await using NpgsqlCommand command = new("CREATE FUNCTION mhserveremu.account_store_test_backend_termination() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN PERFORM pg_terminate_backend(pg_backend_pid()); RETURN NULL; END; $$; CREATE CONSTRAINT TRIGGER account_store_test_backend_termination AFTER UPDATE ON mhserveremu.account DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION mhserveremu.account_store_test_backend_termination();", connection);
            await command.ExecuteNonQueryAsync();
        }

        private static async Task DropDeferredBackendTerminationAsync(NpgsqlDataSource dataSource)
        {
            await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync();
            await using NpgsqlCommand command = new("DROP TRIGGER IF EXISTS account_store_test_backend_termination ON mhserveremu.account; DROP FUNCTION IF EXISTS mhserveremu.account_store_test_backend_termination();", connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}

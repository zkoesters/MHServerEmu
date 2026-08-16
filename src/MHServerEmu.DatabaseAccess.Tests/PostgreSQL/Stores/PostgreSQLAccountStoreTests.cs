using MHServerEmu.DatabaseAccess.Models;
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
                fixture.CreateAccount(3, "other@example.test", "playerone"));
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

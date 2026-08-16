using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.DatabaseAccess.Tests.Conformance;
using MHServerEmu.DatabaseAccess.Tests.PostgreSQL.Migrations;

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
            DBAccount uninserted = fixture.CreateAccount(2, "uninserted@example.test", "Uninserted");
            uninserted.PersistenceState = PersistenceState.OutcomeUncertain;

            Assert.Equal(AccountStoreResult.OutcomeUncertain, fixture.AccountStore.InsertAccount(uninserted));
            Assert.False(fixture.AccountStore.TryQueryAccountByEmail(uninserted.Email, out _));
            AccountStoreConformanceCases.AssertOutcomeUncertain(fixture.AccountStore, account);

            Assert.True(fixture.AccountStore.TryQueryAccountByEmail(account.Email, out DBAccount stored));
            Assert.Equal("PlayerOne", stored.PlayerName);
            Assert.Equal(AccountUserLevel.User, stored.UserLevel);
            Assert.Equal(AccountFlags.None, stored.Flags);
            Assert.Equal(0, stored.PersistenceRevision);
        }
    }
}

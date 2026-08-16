using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.DatabaseAccess.Tests.Conformance;
using MHServerEmu.DatabaseAccess.Tests.PostgreSQL.Migrations;
using Npgsql;

namespace MHServerEmu.DatabaseAccess.Tests.PostgreSQL.Stores
{
    [Trait("Category", "PostgreSQLIntegration")]
    [Collection("PostgreSQL migration integration")]
    public class PostgreSQLPlayerStoreTests
    {
        private readonly PostgreSQLTestDatabase _database;

        public PostgreSQLPlayerStoreTests(PostgreSQLTestDatabase database)
        {
            _database = database;
        }

        [PostgreSQLIntegrationFact]
        public async Task SaveAndLoad_NestedAggregate_ReconstructsRootContainersAndDeletesStaleRows()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            DBAccount account = fixture.CreateNestedAggregate(1);
            Assert.Equal(AccountStoreResult.Success, fixture.AccountStore.InsertAccount(account));

            PlayerStoreConformanceTests.AssertNestedRoundTrip(fixture.Players, account, fixture.CreateEmptyAccount(account.Id));
            account.Items.Clear();
            Assert.Equal(PlayerStoreResult.Success, fixture.Players.SavePlayerData(account));

            DBAccount loaded = fixture.CreateEmptyAccount(account.Id);
            Assert.Equal(PlayerStoreResult.Success, fixture.Players.LoadPlayerData(loaded));
            Assert.Empty(loaded.Items.Entries);
            Assert.Single(loaded.Avatars.Entries);
            Assert.Single(loaded.TeamUps.Entries);
            Assert.Single(loaded.ControlledEntities.Entries);
        }

        [PostgreSQLIntegrationFact]
        public async Task Load_MissingProfile_CreatesDefaultPlayerAfterConfirmingAccount()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            DBAccount account = fixture.CreateEmptyAccount(1);
            Assert.Equal(AccountStoreResult.Success, fixture.AccountStore.InsertAccount(account));

            Assert.Equal(PlayerStoreResult.Success, fixture.Players.LoadPlayerData(account));

            Assert.NotNull(account.Player);
            Assert.Equal(account.Id, account.Player.DbGuid);
            Assert.Empty(account.Avatars.Entries);
            Assert.Equal(0, account.Player.PersistenceRevision);
        }

        [PostgreSQLIntegrationFact]
        public async Task LoadAndSave_MissingAccount_ReturnAccountNotFound()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            DBAccount account = fixture.CreateNestedAggregate(1);

            Assert.Equal(PlayerStoreResult.AccountNotFound, fixture.Players.LoadPlayerData(account));
            Assert.Equal(PlayerStoreResult.AccountNotFound, fixture.Players.SavePlayerData(account));
        }

        [PostgreSQLIntegrationFact]
        public async Task Save_InvalidCrossCategoryDuplicate_ReturnsInvalidAggregate()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            DBAccount account = fixture.CreateCrossCategoryDuplicateAggregate(2);
            Assert.Equal(AccountStoreResult.Success, fixture.AccountStore.InsertAccount(account));

            Assert.Equal(PlayerStoreResult.InvalidAggregate, fixture.Players.SavePlayerData(account));
        }

        [PostgreSQLIntegrationFact]
        public async Task Save_EmptyAggregate_RemovesAllPersistedEntities()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            DBAccount account = fixture.CreateNestedAggregate(1);
            Assert.Equal(AccountStoreResult.Success, fixture.AccountStore.InsertAccount(account));
            Assert.Equal(PlayerStoreResult.Success, fixture.Players.SavePlayerData(account));
            account.ClearEntities();

            Assert.Equal(PlayerStoreResult.Success, fixture.Players.SavePlayerData(account));
            DBAccount loaded = fixture.CreateEmptyAccount(account.Id);
            Assert.Equal(PlayerStoreResult.Success, fixture.Players.LoadPlayerData(loaded));
            Assert.Empty(loaded.Avatars.Entries);
            Assert.Empty(loaded.TeamUps.Entries);
            Assert.Empty(loaded.Items.Entries);
            Assert.Empty(loaded.ControlledEntities.Entries);
        }

        [PostgreSQLIntegrationFact]
        public async Task Save_EntityOwnerConflict_RollsBackAggregate()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            DBAccount first = fixture.CreateEmptyAccount(1);
            first.Player = new DBPlayer(first.Id);
            first.Avatars.Add(PostgreSQLStoreTestFixture.Entity(10, first.Id));
            DBAccount second = fixture.CreateEmptyAccount(2);
            second.Player = new DBPlayer(second.Id);
            second.Avatars.Add(PostgreSQLStoreTestFixture.Entity(10, second.Id));
            Assert.Equal(AccountStoreResult.Success, fixture.AccountStore.InsertAccount(first));
            Assert.Equal(AccountStoreResult.Success, fixture.AccountStore.InsertAccount(second));
            Assert.Equal(PlayerStoreResult.Success, fixture.Players.SavePlayerData(first));

            Assert.Equal(PlayerStoreResult.Failed, fixture.Players.SavePlayerData(second));
            Assert.Equal(PlayerStoreResult.Success, fixture.Players.LoadPlayerData(second));
            Assert.Empty(second.Avatars.Entries);
        }

        [PostgreSQLIntegrationFact]
        public async Task Save_EntityKindConflict_RollsBackAggregate()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            DBAccount account = fixture.CreateEmptyAccount(1);
            account.Player = new DBPlayer(account.Id);
            account.Avatars.Add(PostgreSQLStoreTestFixture.Entity(10, account.Id));
            Assert.Equal(AccountStoreResult.Success, fixture.AccountStore.InsertAccount(account));
            Assert.Equal(PlayerStoreResult.Success, fixture.Players.SavePlayerData(account));

            DBAccount conflicting = fixture.CreateEmptyAccount(account.Id);
            conflicting.Player = new DBPlayer(account.Id);
            conflicting.Items.Add(PostgreSQLStoreTestFixture.Entity(10, account.Id));
            Assert.Equal(PlayerStoreResult.Failed, fixture.Players.SavePlayerData(conflicting));
            Assert.Equal(PlayerStoreResult.Success, fixture.Players.LoadPlayerData(conflicting));
            Assert.Single(conflicting.Avatars.Entries);
            Assert.Empty(conflicting.Items.Entries);
        }

        [PostgreSQLIntegrationFact]
        public async Task Reads_NormalizeNameAndReturnLastLogout()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            DBAccount account = fixture.CreateNestedAggregate(1);
            account.PlayerName = "PlayerOne";
            Assert.Equal(AccountStoreResult.Success, fixture.AccountStore.InsertAccount(account));
            Assert.Equal(PlayerStoreResult.Success, fixture.Players.SavePlayerData(account));

            Assert.True(fixture.Players.TryGetPlayerDbIdByName("playerone", out ulong id, out string playerName));
            Assert.Equal(unchecked((ulong)account.Id), id);
            Assert.Equal(account.PlayerName, playerName);
            Assert.True(fixture.Players.TryGetPlayerName(unchecked((ulong)account.Id), out string byId));
            Assert.Equal(account.PlayerName, byId);
            Assert.True(fixture.Players.TryGetLastLogoutTime(unchecked((ulong)account.Id), out long lastLogout));
            Assert.Equal(account.Player.LastLogoutTime, lastLogout);
        }

        [PostgreSQLIntegrationFact]
        public async Task Save_ThousandEntities_RoundTripsWithinOperationTimeout()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            DBAccount account = fixture.CreateEmptyAccount(1);
            account.Player = new DBPlayer(account.Id);
            for (int index = 0; index < 1_000; index++)
                account.Items.Add(PostgreSQLStoreTestFixture.Entity(10_000 + index, account.Id, 100 + index, (uint)index));
            Assert.Equal(AccountStoreResult.Success, fixture.AccountStore.InsertAccount(account));

            Assert.Equal(PlayerStoreResult.Success, fixture.Players.SavePlayerData(account));
            DBAccount loaded = fixture.CreateEmptyAccount(account.Id);
            Assert.Equal(PlayerStoreResult.Success, fixture.Players.LoadPlayerData(loaded));
            Assert.Equal(1_000, loaded.Items.Count);
        }

        [PostgreSQLIntegrationFact]
        public async Task Save_CompetingRevisions_AllowsOnlyOneWriter()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            DBAccount account = fixture.CreateNestedAggregate(1);
            Assert.Equal(AccountStoreResult.Success, fixture.AccountStore.InsertAccount(account));
            Assert.Equal(PlayerStoreResult.Success, fixture.Players.SavePlayerData(account));
            DBAccount first = fixture.CreateEmptyAccount(account.Id);
            DBAccount second = fixture.CreateEmptyAccount(account.Id);
            Assert.Equal(PlayerStoreResult.Success, fixture.Players.LoadPlayerData(first));
            Assert.Equal(PlayerStoreResult.Success, fixture.Players.LoadPlayerData(second));
            first.Player.LastLogoutTime = 1;
            second.Player.LastLogoutTime = 2;

            PlayerStoreResult[] results = await Task.WhenAll(
                Task.Run(() => fixture.Players.SavePlayerData(first)),
                Task.Run(() => fixture.Players.SavePlayerData(second)));

            Assert.Contains(PlayerStoreResult.Success, results);
            Assert.Contains(PlayerStoreResult.StaleRevision, results);
        }

        [PostgreSQLIntegrationFact]
        public async Task Save_CommitTermination_MarksAggregateUncertainAndRejectsRetry()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            DBAccount account = fixture.CreateNestedAggregate(1);
            Assert.Equal(AccountStoreResult.Success, fixture.AccountStore.InsertAccount(account));
            Assert.Equal(PlayerStoreResult.Success, fixture.Players.SavePlayerData(account));
            account.Player.LastLogoutTime++;
            await CreateDeferredBackendTerminationAsync(fixture.Provider.DataSource);
            try
            {
                Assert.Equal(PlayerStoreResult.OutcomeUncertain, fixture.Players.SavePlayerData(account));
                Assert.Equal(PersistenceState.OutcomeUncertain, account.Player.PersistenceState);
            }
            finally
            {
                await DropDeferredBackendTerminationAsync(fixture.Provider.DataSource);
            }

            fixture.Provider.FenceForTest();
            Assert.Equal(PlayerStoreResult.OutcomeUncertain, fixture.Players.SavePlayerData(account));
        }

        private static async Task CreateDeferredBackendTerminationAsync(NpgsqlDataSource dataSource)
        {
            await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync();
            await using NpgsqlCommand command = new("CREATE FUNCTION mhserveremu.player_store_test_backend_termination() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN PERFORM pg_terminate_backend(pg_backend_pid()); RETURN NULL; END; $$; CREATE CONSTRAINT TRIGGER player_store_test_backend_termination AFTER UPDATE ON mhserveremu.player_profile DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION mhserveremu.player_store_test_backend_termination();", connection);
            await command.ExecuteNonQueryAsync();
        }

        private static async Task DropDeferredBackendTerminationAsync(NpgsqlDataSource dataSource)
        {
            await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync();
            await using NpgsqlCommand command = new("DROP TRIGGER IF EXISTS player_store_test_backend_termination ON mhserveremu.player_profile; DROP FUNCTION IF EXISTS mhserveremu.player_store_test_backend_termination();", connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}

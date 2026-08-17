using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.DatabaseAccess.PostgreSQL;
using MHServerEmu.DatabaseAccess.PostgreSQL.Configuration;
using MHServerEmu.DatabaseAccess.PostgreSQL.Migrations;
using MHServerEmu.DatabaseAccess.Tests.PostgreSQL.Migrations;

namespace MHServerEmu.DatabaseAccess.Tests.PostgreSQL.Stores
{
    internal sealed class PostgreSQLStoreTestFixture : IAsyncDisposable
    {
        private PostgreSQLStoreTestFixture(PostgreSQLProvider provider)
        {
            Provider = provider;
            AccountStore = new PostgreSQLAccountStore(provider.DataSource, provider.StoreExecutor);
            Players = new PostgreSQLPlayerStore(provider.DataSource, provider.StoreExecutor);
            Guilds = new PostgreSQLGuildStore(provider.DataSource, provider.StoreExecutor);
            Leaderboards = new PostgreSQLLeaderboardStore(provider.DataSource, provider.StoreExecutor);
        }

        internal PostgreSQLProvider Provider { get; }
        internal PostgreSQLAccountStore AccountStore { get; }
        internal PostgreSQLPlayerStore Players { get; }
        internal PostgreSQLGuildStore Guilds { get; }
        internal PostgreSQLLeaderboardStore Leaderboards { get; }

        internal static async Task<PostgreSQLStoreTestFixture> StartAsync(PostgreSQLTestDatabase database, PostgreSQLConfig config = null)
        {
            ArgumentNullException.ThrowIfNull(database);
            string connectionString = await database.CreateSettingsConnectionStringAsync();
            if (PostgreSQLSettings.TryCreate(connectionString, config ?? new PostgreSQLConfig(), false, out PostgreSQLSettings settings, out _) == false)
                throw new InvalidOperationException("Unable to create PostgreSQL test settings.");

            PostgreSQLProvider provider = new(settings, PostgreSQLMigrationCatalog.LoadEmbedded());
            PostgreSQLProviderStartResult result = await provider.StartAsync();
            if (result.Succeeded == false)
            {
                await provider.DisposeAsync();
                throw new InvalidOperationException("Unable to start PostgreSQL test provider.");
            }

            return new PostgreSQLStoreTestFixture(provider);
        }

        internal DBAccount CreateAccount(long id, string email, string playerName)
        {
            return new DBAccount
            {
                Id = id,
                Email = email,
                PlayerName = playerName,
                PasswordHash = Enumerable.Repeat((byte)0x11, 64).ToArray(),
                Salt = Enumerable.Repeat((byte)0x22, 64).ToArray(),
            };
        }

        internal DBAccount CreateEmptyAccount(long id)
        {
            return CreateAccount(id, $"account-{id}@example.test", $"Player{id}");
        }

        internal DBAccount CreateNestedAggregate(long id)
        {
            DBAccount account = CreateEmptyAccount(id);
            account.Player = new DBPlayer(id)
            {
                ArchiveData = [1, 2, 3],
                ArchiveVersion = 1,
                GameBuildNumber = 1,
                LastLogoutTime = 1234,
            };
            account.Avatars.Add(Entity(10_000 + id, id));
            account.TeamUps.Add(Entity(20_000 + id, id));
            account.Items.Add(Entity(30_000 + id, id, 100, 1));
            account.Items.Add(Entity(30_100 + id, 10_000 + id, 101, 2));
            account.ControlledEntities.Add(Entity(40_000 + id, 10_000 + id));
            return account;
        }

        internal DBAccount CreateCrossCategoryDuplicateAggregate(long id)
        {
            DBAccount account = CreateEmptyAccount(id);
            account.Player = new DBPlayer(id);
            account.Avatars.Add(Entity(10_000 + id, id));
            account.Items.Add(Entity(10_000 + id, id));
            return account;
        }

        internal static DBEntity Entity(long id, long parentId, long inventoryPrototypeId = 0, uint slot = 0)
        {
            return new DBEntity
            {
                DbGuid = id,
                ContainerDbGuid = parentId,
                InventoryProtoGuid = inventoryPrototypeId,
                Slot = slot,
                EntityProtoGuid = id + 500_000,
                ArchiveData = [(byte)id],
            };
        }

        public ValueTask DisposeAsync()
        {
            return Provider.DisposeAsync();
        }
    }
}

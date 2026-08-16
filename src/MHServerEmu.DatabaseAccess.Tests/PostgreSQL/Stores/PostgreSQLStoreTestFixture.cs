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
        }

        internal PostgreSQLProvider Provider { get; }

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

        public ValueTask DisposeAsync()
        {
            return Provider.DisposeAsync();
        }
    }
}

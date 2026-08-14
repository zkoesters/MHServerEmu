using System.Data.SQLite;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.DatabaseAccess.SQLite;

namespace MHServerEmu.DatabaseAccess.Tests.SQLite
{
    public class SQLiteDBManagerTests
    {
        [Fact]
        public void Initialize_FreshDatabase_CreatesSchemaWithoutAccounts()
        {
            using TemporaryDirectory temporaryDirectory = new();
            string databasePath = Path.Combine(temporaryDirectory.Path, "Account.db");
            SQLiteDBManager manager = new();

            Assert.True(manager.Initialize(databasePath, 0, TimeSpan.FromHours(1)));

            using SQLiteConnection connection = new($"Data Source={databasePath}");
            connection.Open();
            using SQLiteCommand schemaVersionCommand = new("PRAGMA user_version", connection);
            using SQLiteCommand accountCountCommand = new("SELECT COUNT(*) FROM Account", connection);
            Assert.Equal(6L, Convert.ToInt64(schemaVersionCommand.ExecuteScalar()));
            Assert.Equal(0L, Convert.ToInt64(accountCountCommand.ExecuteScalar()));
        }

        [Fact]
        public void AccountAndPlayer_RoundTrip_PreservesBaselineSemantics()
        {
            using TemporaryDirectory temporaryDirectory = new();
            string databasePath = Path.Combine(temporaryDirectory.Path, "Account.db");
            SQLiteDBManager manager = new();
            DBAccount account = new("Case@Test.com", "PlayerOne", "twelve-chars");

            Assert.True(manager.Initialize(databasePath, 0, TimeSpan.FromHours(1)));
            Assert.True(manager.InsertAccount(account));
            Assert.True(manager.TryQueryAccountByEmail("case@test.com", out DBAccount emailAccount));
            Assert.Equal(account.Id, emailAccount.Id);
            Assert.True(manager.TryGetPlayerDbIdByName("playerone", out ulong playerDbId, out string playerName));
            Assert.Equal((ulong)account.Id, playerDbId);
            Assert.Equal("PlayerOne", playerName);

            account.Player = new(account.Id) { ArchiveData = [0x10, 0x20, 0x30] };
            Assert.True(account.Avatars.Add(new()
            {
                DbGuid = account.Id + 1,
                ContainerDbGuid = account.Id,
                InventoryProtoGuid = 100,
                Slot = 0,
                EntityProtoGuid = 200,
                ArchiveData = [0x40, 0x50, 0x60]
            }));
            Assert.True(manager.SavePlayerData(account));

            account.Player = null;
            account.ClearEntities();
            Assert.True(manager.TryQueryAccountByEmail("case@test.com", out DBAccount loadedAccount));
            Assert.True(manager.LoadPlayerData(loadedAccount));
            Assert.Equal(new byte[] { 0x10, 0x20, 0x30 }, loadedAccount.Player.ArchiveData);
            DBEntity avatar = Assert.Single(loadedAccount.Avatars.Entries);
            Assert.Equal(new byte[] { 0x40, 0x50, 0x60 }, avatar.ArchiveData);
        }
    }
}

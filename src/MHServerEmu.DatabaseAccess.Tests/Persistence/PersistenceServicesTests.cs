using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.DatabaseAccess.Persistence;

namespace MHServerEmu.DatabaseAccess.Tests.Persistence
{
    public class PersistenceServicesTests
    {
        [Fact]
        public void Constructor_NullAccounts_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() => new PersistenceServices(null, new PlayerStore(), new GuildStore(), PersistenceCapabilities.Json));
        }

        [Fact]
        public void Constructor_NullPlayers_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() => new PersistenceServices(new AccountStore(), null, new GuildStore(), PersistenceCapabilities.Json));
        }

        [Fact]
        public void Constructor_NullGuilds_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() => new PersistenceServices(new AccountStore(), new PlayerStore(), null, PersistenceCapabilities.Json));
        }

        [Fact]
        public void Constructor_NullCapabilities_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() => new PersistenceServices(new AccountStore(), new PlayerStore(), new GuildStore(), null));
        }

        [Fact]
        public void Constructor_ValidDependencies_ReturnsSuppliedServices()
        {
            AccountStore accounts = new();
            PlayerStore players = new();
            GuildStore guilds = new();
            PersistenceCapabilities capabilities = PersistenceCapabilities.SQLite;

            PersistenceServices services = new(accounts, players, guilds, capabilities);

            Assert.Same(accounts, services.Accounts);
            Assert.Same(players, services.Players);
            Assert.Same(guilds, services.Guilds);
            Assert.Same(capabilities, services.Capabilities);
        }

        private class AccountStore : IAccountStore
        {
            public bool TryQueryAccountByEmail(string email, out DBAccount account)
            {
                account = null;
                return false;
            }

            public bool InsertAccount(DBAccount account) => false;
            public bool UpdateAccount(DBAccount account) => false;
        }

        private class PlayerStore : IPlayerStore
        {
            public bool TryGetPlayerDbIdByName(string playerName, out ulong playerDbId, out string playerNameOut)
            {
                playerDbId = 0;
                playerNameOut = null;
                return false;
            }

            public bool TryGetPlayerName(ulong playerDbId, out string playerName)
            {
                playerName = null;
                return false;
            }

            public bool GetPlayerNames(Dictionary<ulong, string> playerNames) => false;

            public bool TryGetLastLogoutTime(ulong playerDbId, out long lastLogoutTime)
            {
                lastLogoutTime = 0;
                return false;
            }

            public bool LoadPlayerData(DBAccount account) => false;
            public bool SavePlayerData(DBAccount account) => false;
        }

        private class GuildStore : IGuildStore
        {
            public bool LoadGuilds(List<DBGuild> guilds) => false;
            public bool SaveGuild(DBGuild guild) => false;
            public bool DeleteGuild(DBGuild guild) => false;
            public bool SaveGuildMember(DBGuildMember guildMember) => false;
            public bool DeleteGuildMember(DBGuildMember guildMember) => false;
        }
    }
}

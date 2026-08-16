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

            public AccountStoreResult InsertAccount(DBAccount account) => AccountStoreResult.Failed;
            public AccountStoreResult ChangePlayerName(DBAccount account, string playerName) => AccountStoreResult.Failed;
            public AccountStoreResult ChangePassword(DBAccount account, byte[] passwordHash, byte[] salt) => AccountStoreResult.Failed;
            public AccountStoreResult ChangeUserLevel(DBAccount account, AccountUserLevel userLevel) => AccountStoreResult.Failed;
            public AccountStoreResult ChangeFlags(DBAccount account, AccountFlags flags) => AccountStoreResult.Failed;
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

            public PlayerStoreResult LoadPlayerData(DBAccount account) => PlayerStoreResult.Failed;
            public PlayerStoreResult SavePlayerData(DBAccount account) => PlayerStoreResult.Failed;
        }

        private class GuildStore : IGuildStore
        {
            public bool LoadGuilds(List<DBGuild> guilds) => false;
            public GuildStoreResult CreateGuild(DBGuild guild, DBGuildMember creator) => GuildStoreResult.Failed;
            public GuildStoreResult ChangeGuildName(DBGuild guild, string name) => GuildStoreResult.Failed;
            public GuildStoreResult ChangeGuildMotd(DBGuild guild, string motd) => GuildStoreResult.Failed;
            public GuildStoreResult ApplyMembershipTransition(DBGuild guild, GuildMemberTransition transition) => GuildStoreResult.Failed;
            public GuildStoreResult DeleteGuild(DBGuild guild) => GuildStoreResult.Failed;
        }
    }
}

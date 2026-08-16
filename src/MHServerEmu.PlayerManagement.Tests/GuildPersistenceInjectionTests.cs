using Gazillion;
using Google.ProtocolBuffers;
using MHServerEmu.Core.Network;
using MHServerEmu.DatabaseAccess;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.DatabaseAccess.Persistence;
using MHServerEmu.PlayerManagement.Auth;
using MHServerEmu.Games.GameData;
using MHServerEmu.PlayerManagement.Players;
using MHServerEmu.PlayerManagement.Social;

namespace MHServerEmu.PlayerManagement.Tests
{
    public class GuildPersistenceInjectionTests
    {
        [Fact]
        public void Initialize_LoadsGuildsFromSuppliedStore()
        {
            StubDBManager store = new();
            MasterGuildManager guildManager = CreateGuildManager(store);

            guildManager.Initialize();

            Assert.Equal(1, store.LoadGuildsCallCount);
        }

        [Fact]
        public void Initialize_EmptyLoadedGuild_DeletesThroughSuppliedStore()
        {
            StubDBManager store = new();
            store.GuildsToLoad.Add(new DBGuild(1, "Empty", string.Empty, 1, 0));
            MasterGuildManager guildManager = CreateGuildManager(store);

            guildManager.Initialize();

            Assert.Equal(1, store.DeleteGuildCallCount);
        }

        [Fact]
        public void Initialize_PopulatedLoadedGuild_LoadsGuildFromSuppliedStore()
        {
            StubDBManager store = new();
            DBGuild guildData = new(1, "Guild", string.Empty, 1, 0);
            guildData.Members.Add(new DBGuildMember(1, 1, (long)GuildMembership.eGMLeader));
            store.GuildsToLoad.Add(guildData);
            MasterGuildManager guildManager = CreateGuildManager(store);

            guildManager.Initialize();

            Assert.Equal(1, store.LoadGuildsCallCount);
            Assert.NotNull(guildManager.GetGuild(1));
        }

        [Fact]
        public void ChangeMember_AppliesTransitionThroughSuppliedStore()
        {
            StubDBManager store = new();
            DBGuild guildData = new(1, "Guild", string.Empty, 1, 0);
            guildData.Members.Add(new DBGuildMember(1, 1, (long)GuildMembership.eGMLeader));
            guildData.Members.Add(new DBGuildMember(2, 1, (long)GuildMembership.eGMMember));
            MasterGuildManager guildManager = CreateGuildManager(store);
            MasterGuild guild = new(guildData, true, store, new PlayerNameCache(store), guildManager, true);
            PlayerHandle leader = new(new FakeFrontendClient(new DBAccount("leader@example.com", "Leader", "password") { Id = 1 }), store, () => true, PrototypeId.Invalid);

            GuildChangeMemberResultCode result = guild.ChangeMember(leader, 2, GuildMembership.eGMOfficer);

            Assert.Equal(GuildChangeMemberResultCode.eGCMRCSuccess, result);
            Assert.Equal(1, store.SaveGuildCallCount);
            Assert.Equal(1, store.SaveGuildMemberCallCount);
        }

        private static MasterGuildManager CreateGuildManager(StubDBManager store)
        {
            AccountManager accountManager = new(store, store, PersistenceCapabilities.SQLite, new TestAccountSecurityNotifier());
            PlayerManagerService playerManager = new(accountManager, store, store, PersistenceCapabilities.SQLite);
            return MasterGuildManager.CreateForTesting(playerManager, store);
        }

        private sealed class TestAccountSecurityNotifier : IAccountSecurityNotifier
        {
            public void Notify(ulong accountId, AccountSecurityChangeType changeType) { }
        }

        private sealed class FakeFrontendClient(DBAccount account) : IFrontendClient, IDBAccountOwner
        {
            public DBAccount Account { get; } = account;
            public bool IsConnected => true;
            public IFrontendSession Session => null;
            public ulong DbId => (ulong)Account.Id;

            public void Disconnect() { }
            public void SuspendReceiveTimeout() { }
            public bool AssignSession(IFrontendSession session) => true;
            public bool HandleIncomingMessageBuffer(ushort muxId, in MessageBuffer messageBuffer) => true;
            public void SendMuxCommand(ushort muxId, MuxCommand command) { }
            public void SendMessage(ushort muxId, IMessage message) { }
            public void SendMessageList(ushort muxId, List<IMessage> messageList) { }
        }
    }
}

using Gazillion;
using Google.ProtocolBuffers;
using MHServerEmu.Core.Network;
using MHServerEmu.DatabaseAccess;
using MHServerEmu.DatabaseAccess.Models;
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
            MasterGuildManager guildManager = new(store);

            guildManager.Initialize();

            Assert.Equal(1, store.LoadGuildsCallCount);
        }

        [Fact]
        public void Initialize_EmptyLoadedGuild_DeletesThroughSuppliedStore()
        {
            StubDBManager store = new();
            store.GuildsToLoad.Add(new DBGuild(1, "Empty", string.Empty, 1, 0));
            MasterGuildManager guildManager = new(store);

            guildManager.Initialize();

            Assert.Equal(1, store.DeleteGuildCallCount);
        }

        [Fact]
        public void ChangeMember_SavesMemberThroughSuppliedStore()
        {
            StubDBManager store = new();
            DBGuild guildData = new(1, "Guild", string.Empty, 1, 0);
            guildData.Members.Add(new DBGuildMember(1, 1, (long)GuildMembership.eGMLeader));
            guildData.Members.Add(new DBGuildMember(2, 1, (long)GuildMembership.eGMMember));
            MasterGuildManager guildManager = new(store);
            MasterGuild guild = new(guildData, true, store, new PlayerNameCache(store), guildManager, true);
            PlayerHandle leader = new(new FakeFrontendClient(new DBAccount("leader@example.com", "Leader", "password") { Id = 1 }), store, () => true, PrototypeId.Invalid);

            GuildChangeMemberResultCode result = guild.ChangeMember(leader, 2, GuildMembership.eGMOfficer);

            Assert.Equal(GuildChangeMemberResultCode.eGCMRCSuccess, result);
            Assert.Equal(1, store.SaveGuildCallCount);
            Assert.Equal(3, store.SaveGuildMemberCallCount);
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

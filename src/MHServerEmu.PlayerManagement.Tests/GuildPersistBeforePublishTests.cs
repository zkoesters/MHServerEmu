using System.Reflection;
using Gazillion;
using Google.ProtocolBuffers;
using MHServerEmu.Core.Network;
using MHServerEmu.DatabaseAccess;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.DatabaseAccess.Persistence;
using MHServerEmu.Games.GameData;
using MHServerEmu.PlayerManagement.Auth;
using MHServerEmu.PlayerManagement.Games;
using MHServerEmu.PlayerManagement.Players;
using MHServerEmu.PlayerManagement.Social;

namespace MHServerEmu.PlayerManagement.Tests
{
    public class GuildPersistBeforePublishTests : IClassFixture<GuildMessageFixture>
    {
        private readonly GuildMessageFixture _messages;

        public GuildPersistBeforePublishTests(GuildMessageFixture messages)
        {
            _messages = messages;
        }

        [Fact]
        public void CreateGuild_StoreFailure_DoesNotPublishManagerEntries()
        {
            GuildFixture fixture = new(_messages);
            fixture.Store.CreateGuildStoreResult = GuildStoreResult.Failed;
            DBGuildMember leader = new(10, 10, (long)GuildMembership.eGMLeader);
            DBGuild guildData = new(10, "Unpublished", string.Empty, 10, 0);
            guildData.Members.Add(leader);

            MasterGuild guild = CreateGuild(fixture.Manager, guildData, out GuildStoreResult storeResult);

            Assert.Null(guild);
            Assert.Equal(GuildStoreResult.Failed, storeResult);
            Assert.Single(fixture.Store.CreatedGuilds);
            Assert.Null(fixture.Manager.GetGuild(10));
            Assert.Null(fixture.Manager.GetGuildForPlayer(10));
        }

        [Fact]
        public void CreateGuild_StoreSuccess_PublishesManagerEntries()
        {
            GuildFixture fixture = new(_messages);
            DBGuildMember leader = new(10, 10, (long)GuildMembership.eGMLeader);
            DBGuild guildData = new(10, "Published", string.Empty, 10, 0);
            guildData.Members.Add(leader);

            MasterGuild guild = CreateGuild(fixture.Manager, guildData, out GuildStoreResult storeResult);

            Assert.Equal(GuildStoreResult.Success, storeResult);
            Assert.Same(guild, fixture.Manager.GetGuild(10));
            Assert.Same(guild, fixture.Manager.GetGuildForPlayer(10));
            Assert.Same(guildData, Assert.Single(fixture.Store.CreatedGuilds));
            Assert.Same(leader, Assert.Single(fixture.Store.GuildCreators));
        }

        [Theory]
        [InlineData(GuildStoreResult.NameConflict, GuildChangeNameResultCode.eGCNRCDuplicateName)]
        [InlineData(GuildStoreResult.GuildNotFound, GuildChangeNameResultCode.eGCNRCInvalidGuild)]
        [InlineData(GuildStoreResult.InvalidData, GuildChangeNameResultCode.eGCNRCInvalidGuild)]
        [InlineData(GuildStoreResult.StaleRevision, GuildChangeNameResultCode.eGCNRCGuildInErrorState)]
        [InlineData(GuildStoreResult.Failed, GuildChangeNameResultCode.eGCNRCGuildInErrorState)]
        [InlineData(GuildStoreResult.OutcomeUncertain, GuildChangeNameResultCode.eGCNRCGuildInErrorState)]
        public void ChangeName_StoreFailure_DoesNotPublish(GuildStoreResult storeResult, GuildChangeNameResultCode expectedResult)
        {
            GuildFixture fixture = new(_messages);
            fixture.Store.ChangeGuildNameStoreResult = storeResult;

            Assert.Equal(expectedResult, fixture.Guild.ChangeName(fixture.Leader, "Renamed"));
            Assert.Equal("Guild", fixture.Guild.Name);
            Assert.Same(fixture.Guild, fixture.Manager.GetGuildForPlayer(fixture.Leader.PlayerDbId));
            Assert.Empty(_messages.Messages);
        }

        [Fact]
        public void ChangeName_StoreSuccess_PublishesName()
        {
            GuildFixture fixture = new(_messages);

            Assert.Equal(GuildChangeNameResultCode.eGCNRCSuccess, fixture.Guild.ChangeName(fixture.Leader, "Renamed"));
            Assert.Equal("Renamed", fixture.Guild.Name);
            Assert.NotEmpty(_messages.Messages);
        }

        [Theory]
        [InlineData(GuildStoreResult.GuildNotFound, GuildChangeMotdResultCode.eGCMotdRCInvalidGuild)]
        [InlineData(GuildStoreResult.InvalidData, GuildChangeMotdResultCode.eGCMotdRCInvalidGuild)]
        [InlineData(GuildStoreResult.StaleRevision, GuildChangeMotdResultCode.eGCMotdRCGuildInErrorState)]
        [InlineData(GuildStoreResult.Failed, GuildChangeMotdResultCode.eGCMotdRCGuildInErrorState)]
        [InlineData(GuildStoreResult.OutcomeUncertain, GuildChangeMotdResultCode.eGCMotdRCGuildInErrorState)]
        public void ChangeMotd_StoreFailure_DoesNotPublish(GuildStoreResult storeResult, GuildChangeMotdResultCode expectedResult)
        {
            GuildFixture fixture = new(_messages);
            fixture.Store.ChangeGuildMotdStoreResult = storeResult;

            Assert.Equal(expectedResult, fixture.Guild.ChangeMotd(fixture.Leader, "New MOTD"));
            Assert.Equal("Old MOTD", fixture.Guild.Motd);
            Assert.Empty(_messages.Messages);
        }

        [Fact]
        public void ChangeMotd_StoreSuccess_PublishesMotd()
        {
            GuildFixture fixture = new(_messages);

            Assert.Equal(GuildChangeMotdResultCode.eGCMotdRCSuccess, fixture.Guild.ChangeMotd(fixture.Leader, "New MOTD"));
            Assert.Equal("New MOTD", fixture.Guild.Motd);
            Assert.NotEmpty(_messages.Messages);
        }

        [Theory]
        [InlineData(GuildStoreResult.GuildNotFound, GuildChangeMemberResultCode.eGCMRCGuildInErrorState)]
        [InlineData(GuildStoreResult.InvalidData, GuildChangeMemberResultCode.eGCMRCInternalError)]
        [InlineData(GuildStoreResult.MembershipConflict, GuildChangeMemberResultCode.eGCMRCGuildInErrorState)]
        [InlineData(GuildStoreResult.StaleRevision, GuildChangeMemberResultCode.eGCMRCGuildInErrorState)]
        [InlineData(GuildStoreResult.Failed, GuildChangeMemberResultCode.eGCMRCGuildInErrorState)]
        [InlineData(GuildStoreResult.OutcomeUncertain, GuildChangeMemberResultCode.eGCMRCGuildInErrorState)]
        public void TransferLeadership_StoreFailure_LeavesRanksAndLeaderUnchanged(GuildStoreResult storeResult, GuildChangeMemberResultCode expectedResult)
        {
            GuildFixture fixture = new(_messages);
            fixture.Store.ApplyMembershipTransitionStoreResult = storeResult;

            Assert.Equal(expectedResult, fixture.Guild.ChangeMember(fixture.Leader, fixture.Successor.PlayerDbId, GuildMembership.eGMLeader));
            Assert.Equal((long)GuildMembership.eGMLeader, fixture.LeaderData.Membership);
            Assert.Equal((long)GuildMembership.eGMOfficer, fixture.SuccessorData.Membership);
            Assert.Same(fixture.Guild, fixture.Manager.GetGuildForPlayer(fixture.Successor.PlayerDbId));
            Assert.Empty(_messages.Messages);
        }

        [Fact]
        public void TransferLeadership_StoreSuccess_PublishesExactTransition()
        {
            GuildFixture fixture = new(_messages);

            Assert.Equal(GuildChangeMemberResultCode.eGCMRCSuccess, fixture.Guild.ChangeMember(fixture.Leader, fixture.Successor.PlayerDbId, GuildMembership.eGMLeader));
            Assert.Equal((long)GuildMembership.eGMOfficer, fixture.LeaderData.Membership);
            Assert.Equal((long)GuildMembership.eGMLeader, fixture.SuccessorData.Membership);

            GuildMemberTransition transition = Assert.Single(fixture.Store.GuildMemberTransitions);
            Assert.Equal(new GuildMemberChange(fixture.SuccessorData.PlayerDbGuid, (long)GuildMembership.eGMOfficer, (long)GuildMembership.eGMLeader), transition.Changes[0]);
            Assert.Equal(new GuildMemberChange(fixture.LeaderData.PlayerDbGuid, (long)GuildMembership.eGMLeader, (long)GuildMembership.eGMOfficer), transition.Changes[1]);
            Assert.NotEmpty(_messages.Messages);
        }

        [Theory]
        [InlineData(GuildStoreResult.MembershipConflict, GuildRespondToInviteResultCode.eGRIRAlreadyInOtherGuild)]
        [InlineData(GuildStoreResult.StaleRevision, GuildRespondToInviteResultCode.eGRIRCGuildInErrorState)]
        [InlineData(GuildStoreResult.Failed, GuildRespondToInviteResultCode.eGRIRCGuildInErrorState)]
        [InlineData(GuildStoreResult.OutcomeUncertain, GuildRespondToInviteResultCode.eGRIRCGuildInErrorState)]
        public void Join_StoreFailure_DoesNotPublishMembership(GuildStoreResult storeResult, GuildRespondToInviteResultCode expectedResult)
        {
            GuildFixture fixture = new(_messages);
            fixture.Store.ApplyMembershipTransitionStoreResult = storeResult;

            Assert.Equal(expectedResult, CreateNewMember(fixture.Guild, fixture.Invitee));
            Assert.False(fixture.Guild.HasMember(fixture.Invitee.PlayerDbId));
            Assert.Null(fixture.Invitee.Guild);
            Assert.Null(fixture.Manager.GetGuildForPlayer(fixture.Invitee.PlayerDbId));
            Assert.Empty(_messages.Messages);
        }

        [Fact]
        public void Join_StoreSuccess_PublishesMembership()
        {
            GuildFixture fixture = new(_messages);

            Assert.Equal(GuildRespondToInviteResultCode.eGRIRCJoined, CreateNewMember(fixture.Guild, fixture.Invitee));
            Assert.True(fixture.Guild.HasMember(fixture.Invitee.PlayerDbId));
            Assert.Same(fixture.Guild, fixture.Invitee.Guild);
            Assert.Same(fixture.Guild, fixture.Manager.GetGuildForPlayer(fixture.Invitee.PlayerDbId));
            Assert.NotEmpty(_messages.Messages);
        }

        [Fact]
        public void SoleLeaderDeparture_DeleteFailure_DoesNotPublishRemoval()
        {
            GuildFixture fixture = new(_messages, includeSuccessor: false);
            fixture.Store.DeleteGuildStoreResult = GuildStoreResult.StaleRevision;

            Assert.Equal(GuildChangeMemberResultCode.eGCMRCGuildInErrorState, fixture.Guild.ChangeMember(fixture.Leader, fixture.Leader.PlayerDbId, GuildMembership.eGMNone));
            Assert.True(fixture.Guild.HasMember(fixture.Leader.PlayerDbId));
            Assert.Equal((long)GuildMembership.eGMLeader, fixture.LeaderData.Membership);
            Assert.Same(fixture.Guild, fixture.Manager.GetGuildForPlayer(fixture.Leader.PlayerDbId));
            Assert.Empty(_messages.Messages);
        }

        private sealed class GuildFixture
        {
            public StubDBManager Store { get; } = new();
            public MasterGuildManager Manager { get; }
            public MasterGuild Guild { get; }
            public PlayerHandle Leader { get; }
            public PlayerHandle Successor { get; }
            public PlayerHandle Invitee { get; }
            public DBGuildMember LeaderData { get; }
            public DBGuildMember SuccessorData { get; }

            public GuildFixture(GuildMessageFixture messages, bool includeSuccessor = true)
            {
                Manager = CreateGuildManager(Store);
                LeaderData = new DBGuildMember(1, 1, (long)GuildMembership.eGMLeader);
                SuccessorData = new DBGuildMember(2, 1, (long)GuildMembership.eGMOfficer);
                DBGuild guildData = new(1, "Guild", "Old MOTD", 1, 0);
                guildData.Members.Add(LeaderData);
                if (includeSuccessor)
                    guildData.Members.Add(SuccessorData);

                Guild = new MasterGuild(guildData, false, Store, new PlayerNameCache(Store), Manager, true);
                Leader = CreatePlayer(Store, 1, "Leader");
                Successor = CreatePlayer(Store, 2, "Successor");
                Invitee = CreatePlayer(Store, 3, "Invitee");
                GameHandle game = new(1);
                typeof(GameHandle).GetField("<State>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(game, GameHandleState.Running);
                SetCurrentGame(Leader, game);
                SetCurrentGame(Successor, game);
                SetCurrentGame(Invitee, game);
                Guild.OnMemberOnline(Leader);
                messages.Messages.Clear();
            }

            private static MasterGuildManager CreateGuildManager(StubDBManager store)
            {
                AccountManager accountManager = new(store, store, PersistenceCapabilities.SQLite, new TestAccountSecurityNotifier());
                PlayerManagerService playerManager = new(accountManager, store, store, PersistenceCapabilities.SQLite);
                return MasterGuildManager.CreateForTesting(playerManager, store);
            }

            private static PlayerHandle CreatePlayer(StubDBManager store, long id, string name)
            {
                PlayerHandle player = new(new FakeFrontendClient(new DBAccount($"{name}@example.com", name, "password") { Id = id }), store, () => true, PrototypeId.Invalid);
                typeof(PlayerHandle).GetField("<State>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(player, PlayerHandleState.InGame);
                return player;
            }

            private static void SetCurrentGame(PlayerHandle player, GameHandle game)
            {
                typeof(PlayerHandle).GetField("<CurrentGame>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(player, game);
            }
        }

        private static GuildRespondToInviteResultCode CreateNewMember(MasterGuild guild, PlayerHandle player)
        {
            MethodInfo createNewMember = typeof(MasterGuild).GetMethod("CreateNewMember", BindingFlags.Instance | BindingFlags.NonPublic);
            return (GuildRespondToInviteResultCode)createNewMember.Invoke(guild, [player, "Leader"]);
        }

        private static MasterGuild CreateGuild(MasterGuildManager manager, DBGuild guildData, out GuildStoreResult storeResult)
        {
            MethodInfo createGuild = typeof(MasterGuildManager).GetMethod("CreateGuild", BindingFlags.Instance | BindingFlags.NonPublic);
            object[] arguments = [guildData, true, null];
            MasterGuild guild = (MasterGuild)createGuild.Invoke(manager, arguments);
            storeResult = (GuildStoreResult)arguments[2];
            return guild;
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

    public sealed class GuildMessageFixture : IDisposable
    {
        public List<object> Messages { get; } = new();

        public GuildMessageFixture()
        {
            ServerManager.Instance.RegisterGameService(new RecordingGameService(Messages), GameServiceType.GameInstance);
            ServerManager.Instance.RegisterGameService(new RecordingGameService(Messages), GameServiceType.GroupingManager);
        }

        public void Dispose()
        {
            ServerManager.Instance.UnregisterGameService(GameServiceType.GameInstance);
            ServerManager.Instance.UnregisterGameService(GameServiceType.GroupingManager);
        }

        private sealed class RecordingGameService(List<object> messages) : IGameService
        {
            public GameServiceState State => GameServiceState.Running;

            public void Run() { }
            public void Shutdown() { }
            public void GetStatus(Dictionary<string, long> statusDict) { }

            public void ReceiveServiceMessage<T>(in T message) where T : struct, IGameServiceMessage
            {
                messages.Add(message);
            }
        }
    }
}

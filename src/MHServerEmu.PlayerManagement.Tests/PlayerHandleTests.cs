using Google.ProtocolBuffers;
using System.Reflection;
using MHServerEmu.Core.Network;
using MHServerEmu.Core.Serialization;
using MHServerEmu.DatabaseAccess;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.Network;
using MHServerEmu.PlayerManagement.Players;

namespace MHServerEmu.PlayerManagement.Tests
{
    public class PlayerHandleTests
    {
        [Fact]
        public void LoadAndSavePlayerData_UsesSuppliedPlayerStore()
        {
            StubDBManager store = new();
            DBAccount account = new("player@example.com", "Player", "password") { Id = 1 };
            PlayerHandle player = new(new FakeFrontendClient(account), store, () => true, PrototypeId.Invalid);

            Assert.True(player.LoadPlayerData());
            Assert.True(player.SavePlayerData());

            Assert.Equal(1, store.LoadPlayerDataCallCount);
            Assert.Equal(1, store.SavePlayerDataCallCount);
        }

        [Fact]
        public void SavePlayerData_OutcomeUncertain_DoesNotRetry()
        {
            StubDBManager store = new() { SavePlayerDataStoreResult = PlayerStoreResult.OutcomeUncertain };
            DBAccount account = new("player@example.com", "Player", "password") { Id = 1 };
            PlayerHandle player = new(new FakeFrontendClient(account), store, () => true, PrototypeId.Invalid);

            Assert.True(player.LoadPlayerData());
            Assert.False(player.SavePlayerData());

            Assert.Equal(1, store.SavePlayerDataCallCount);
        }

        [Fact]
        public void SetArchiveMetadata_UsesCurrentArchiveAndBuildVersions()
        {
            MethodInfo setArchiveMetadata = typeof(PlayerConnection).GetMethod("SetArchiveMetadata", BindingFlags.NonPublic | BindingFlags.Static);
            DBPlayer player = new();

            Assert.NotNull(setArchiveMetadata);
            setArchiveMetadata.Invoke(null, [player]);

            Assert.Equal((int)ArchiveVersion.Current, player.ArchiveVersion);
            Assert.Equal((int)GameBuildNumber.Current, player.GameBuildNumber);
        }

        private sealed class FakeFrontendClient : IFrontendClient, IDBAccountOwner
        {
            public FakeFrontendClient(DBAccount account)
            {
                Account = account;
            }

            public DBAccount Account { get; }
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

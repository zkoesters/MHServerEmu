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
        public void TryStoreSerializedPlayerData_TransferSucceeds_StoresArchiveAndMetadata()
        {
            MethodInfo tryStoreSerializedPlayerData = typeof(PlayerConnection).GetMethod("TryStoreSerializedPlayerData", BindingFlags.NonPublic | BindingFlags.Static);
            DBPlayer player = new() { ArchiveData = [0x01] };
            using Archive archive = new(ArchiveSerializeType.Database);

            Assert.NotNull(tryStoreSerializedPlayerData);
            Assert.True((bool)tryStoreSerializedPlayerData.Invoke(null, [player, archive, new TestSerialize(true)]));

            Assert.Equal(archive.AsSpan().ToArray(), player.ArchiveData);
            Assert.Equal((int)ArchiveVersion.Current, player.ArchiveVersion);
            Assert.Equal((int)GameBuildNumber.Current, player.GameBuildNumber);
        }

        [Fact]
        public void TryStoreSerializedPlayerData_TransferFails_PreservesArchiveAndMetadata()
        {
            MethodInfo tryStoreSerializedPlayerData = typeof(PlayerConnection).GetMethod("TryStoreSerializedPlayerData", BindingFlags.NonPublic | BindingFlags.Static);
            DBPlayer player = new()
            {
                ArchiveData = [0x01, 0x02],
                ArchiveVersion = 7,
                GameBuildNumber = 8
            };
            using Archive archive = new(ArchiveSerializeType.Database);

            Assert.NotNull(tryStoreSerializedPlayerData);
            Assert.False((bool)tryStoreSerializedPlayerData.Invoke(null, [player, archive, new TestSerialize(false)]));

            Assert.Equal([0x01, 0x02], player.ArchiveData);
            Assert.Equal(7, player.ArchiveVersion);
            Assert.Equal(8, player.GameBuildNumber);
        }

        private sealed class TestSerialize(bool success) : ISerialize
        {
            public bool Serialize(Archive archive)
            {
                int value = 42;
                return archive.Transfer(ref value) && success;
            }
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

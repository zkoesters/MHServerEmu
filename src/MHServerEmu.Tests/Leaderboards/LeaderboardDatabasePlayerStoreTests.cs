using System.Reflection;
using MHServerEmu.DatabaseAccess;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.Leaderboards;

namespace MHServerEmu.Tests.Leaderboards
{
    public class LeaderboardDatabasePlayerStoreTests
    {
        [Fact]
        public void InitializePlayerNames_UsesInjectedStoreForPreloadAndCacheMiss()
        {
            TestPlayerStore players = new();
            players.PlayerNames[1] = "CachedPlayer";
            players.PlayerNames[2] = "FetchedPlayer";
            LeaderboardDatabase database = LeaderboardDatabase.Instance;
            MethodInfo initializePlayerNames = typeof(LeaderboardDatabase).GetMethod("InitializePlayerNames", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(initializePlayerNames);
            initializePlayerNames.Invoke(database, new object[] { players });

            Assert.Equal("CachedPlayer", database.GetPlayerNameById(1));
            Assert.Equal("FetchedPlayer", database.GetPlayerNameById(2));
            Assert.Equal(1, players.GetPlayerNamesCallCount);
            Assert.Equal(1, players.TryGetPlayerNameCallCount);
        }

        private sealed class TestPlayerStore : IPlayerStore
        {
            public Dictionary<ulong, string> PlayerNames { get; } = new();
            public int GetPlayerNamesCallCount { get; private set; }
            public int TryGetPlayerNameCallCount { get; private set; }

            public bool TryGetPlayerDbIdByName(string playerName, out ulong playerDbId, out string playerNameOut)
            {
                playerDbId = 0;
                playerNameOut = null;
                return false;
            }

            public bool TryGetPlayerName(ulong playerDbId, out string playerName)
            {
                TryGetPlayerNameCallCount++;
                return PlayerNames.TryGetValue(playerDbId, out playerName);
            }

            public bool GetPlayerNames(Dictionary<ulong, string> playerNames)
            {
                GetPlayerNamesCallCount++;
                playerNames[1] = PlayerNames[1];
                return true;
            }

            public bool TryGetLastLogoutTime(ulong playerDbId, out long lastLogoutTime)
            {
                lastLogoutTime = 0;
                return false;
            }

            public PlayerStoreResult LoadPlayerData(DBAccount account) => PlayerStoreResult.Failed;

            public PlayerStoreResult SavePlayerData(DBAccount account) => PlayerStoreResult.Failed;
        }
    }
}

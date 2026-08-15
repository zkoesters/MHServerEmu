using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.PlayerManagement.Players;

namespace MHServerEmu.PlayerManagement.Tests
{
    public class PlayerNameCacheTests
    {
        [Fact]
        public void TryGetPlayerName_SeparateStoresWithSameId_ReturnsEachStoresName()
        {
            StubDBManager firstStore = new();
            StubDBManager secondStore = new();
            firstStore.Accounts.Add("first@example.com", new DBAccount("first@example.com", "First", "password") { Id = 1 });
            secondStore.Accounts.Add("second@example.com", new DBAccount("second@example.com", "Second", "password") { Id = 1 });
            PlayerNameCache firstCache = new(firstStore);
            PlayerNameCache secondCache = new(secondStore);

            Assert.True(firstCache.TryGetPlayerName(1, out string firstName));
            Assert.True(secondCache.TryGetPlayerName(1, out string secondName));

            Assert.Equal("First", firstName);
            Assert.Equal("Second", secondName);
        }

        [Fact]
        public void TryGetPlayerName_CacheHit_DoesNotQueryStoreAgain()
        {
            StubDBManager store = new();
            store.Accounts.Add("player@example.com", new DBAccount("player@example.com", "Player", "password") { Id = 1 });
            PlayerNameCache cache = new(store);

            Assert.True(cache.TryGetPlayerName(1, out _));
            Assert.True(cache.TryGetPlayerName(1, out _));

            Assert.Equal(1, store.TryGetPlayerNameCallCount);
        }

        [Fact]
        public void OnPlayerNameChanged_EvictsCachedNameAndQueriesStoreAgain()
        {
            StubDBManager store = new();
            DBAccount account = new("player@example.com", "Before", "password") { Id = 1 };
            store.Accounts.Add(account.Email, account);
            PlayerNameCache cache = new(store);
            Assert.True(cache.TryGetPlayerName(1, out _));
            account.PlayerName = "After";

            cache.OnPlayerNameChanged(1);

            Assert.True(cache.TryGetPlayerName(1, out string playerName));
            Assert.Equal("After", playerName);
            Assert.Equal(2, store.TryGetPlayerNameCallCount);
        }
    }
}

using MHServerEmu.DatabaseAccess.Models;

namespace MHServerEmu.DatabaseAccess.Tests.Conformance
{
    internal static class PlayerStoreConformanceTests
    {
        internal static void AssertNestedRoundTrip(IPlayerStore store, DBAccount account, DBAccount loaded)
        {
            Assert.Equal(PlayerStoreResult.Success, store.SavePlayerData(account));
            Assert.Equal(PlayerStoreResult.Success, store.LoadPlayerData(loaded));
            Assert.NotNull(loaded.Player);
            Assert.Equal(account.Player.ArchiveData, loaded.Player.ArchiveData);
            Assert.Equal(account.Player.LastLogoutTime, loaded.Player.LastLogoutTime);
            Assert.Equal(account.Avatars.Count, loaded.Avatars.Count);
            Assert.Equal(account.TeamUps.Count, loaded.TeamUps.Count);
            Assert.Equal(account.Items.Count, loaded.Items.Count);
            Assert.Equal(account.ControlledEntities.Count, loaded.ControlledEntities.Count);
            Assert.All(loaded.Avatars.Entries.Concat(loaded.TeamUps.Entries)
                .Concat(loaded.Items.Entries.Where(entity => entity.ContainerDbGuid == loaded.Id)),
                entity => Assert.Equal(loaded.Id, entity.ContainerDbGuid));
        }
    }
}

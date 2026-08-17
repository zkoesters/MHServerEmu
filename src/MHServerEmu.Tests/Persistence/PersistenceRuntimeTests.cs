using MHServerEmu.DatabaseAccess.Persistence;
using MHServerEmu.DatabaseAccess.Json;
using MHServerEmu.DatabaseAccess.SQLite;

namespace MHServerEmu.Tests.Persistence
{
    public class PersistenceRuntimeTests
    {
        [Fact]
        public async Task DisposeAsync_DisposesOwnedLifetimeOnce()
        {
            int disposalCount = 0;
            JsonDBManager manager = JsonDBManager.Instance;
            PersistenceServices services = new(manager, manager, manager, new SQLiteLeaderboardDBManager("unused.db"), PersistenceCapabilities.Json);
            PersistenceRuntime runtime = new(services, () =>
            {
                disposalCount++;
                return ValueTask.CompletedTask;
            });

            await runtime.DisposeAsync();
            await runtime.DisposeAsync();

            Assert.Equal(1, disposalCount);
        }
    }
}

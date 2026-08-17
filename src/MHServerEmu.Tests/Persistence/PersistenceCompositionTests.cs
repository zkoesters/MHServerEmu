using MHServerEmu.DatabaseAccess.Persistence;
using MHServerEmu.DatabaseAccess.Json;
using MHServerEmu.DatabaseAccess.SQLite;
using MHServerEmu.Persistence;

namespace MHServerEmu.Tests.Persistence
{
    public class PersistenceCompositionTests
    {
        [Fact]
        public async Task CreateAsync_PostgreSQL_UsesInjectedFactoryAndDoesNotCreateSQLiteRuntime()
        {
            int jsonFactoryCalls = 0;
            int sqliteFactoryCalls = 0;
            int postgreSQLFactoryCalls = 0;
            PersistenceRuntime expected = CreateRuntime();

            PersistenceRuntime runtime = await PersistenceComposition.CreateAsync(
                PersistenceProvider.PostgreSQL,
                () => { jsonFactoryCalls++; return Task.FromResult<PersistenceRuntime>(null); },
                () => { sqliteFactoryCalls++; return Task.FromResult<PersistenceRuntime>(null); },
                () => { postgreSQLFactoryCalls++; return Task.FromResult(expected); });

            Assert.Same(expected, runtime);
            Assert.Equal(0, jsonFactoryCalls);
            Assert.Equal(0, sqliteFactoryCalls);
            Assert.Equal(1, postgreSQLFactoryCalls);
        }

        private static PersistenceRuntime CreateRuntime()
        {
            JsonDBManager manager = JsonDBManager.Instance;
            return new PersistenceRuntime(new PersistenceServices(manager, manager, manager, new SQLiteLeaderboardDBManager("unused.db"), PersistenceCapabilities.Json), () => ValueTask.CompletedTask);
        }
    }
}

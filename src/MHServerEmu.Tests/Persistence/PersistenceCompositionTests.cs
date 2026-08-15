using MHServerEmu.DatabaseAccess.Persistence;
using MHServerEmu.Persistence;

namespace MHServerEmu.Tests.Persistence
{
    public class PersistenceCompositionTests
    {
        [Fact]
        public void TryCreate_PostgreSQL_DoesNotInvokeProviderFactories()
        {
            int jsonFactoryCalls = 0;
            int sqliteFactoryCalls = 0;
            Func<(bool Success, PersistenceServices Services)> jsonFactory = () =>
            {
                jsonFactoryCalls++;
                return (true, null);
            };
            Func<(bool Success, PersistenceServices Services)> sqliteFactory = () =>
            {
                sqliteFactoryCalls++;
                return (true, null);
            };

            bool result = PersistenceComposition.TryCreate(PersistenceProvider.PostgreSQL, jsonFactory, sqliteFactory, out PersistenceServices services);

            Assert.False(result);
            Assert.Null(services);
            Assert.Equal(0, jsonFactoryCalls);
            Assert.Equal(0, sqliteFactoryCalls);
        }
    }
}

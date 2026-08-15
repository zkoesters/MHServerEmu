using MHServerEmu.Core.Config;
using MHServerEmu.DatabaseAccess.Persistence;

namespace MHServerEmu.DatabaseAccess.Tests.Persistence
{
    public class PersistenceConfigTests
    {
        [Fact]
        public void Initialize_PersistenceProviderInIni_MapsToProvider()
        {
            using TemporaryDirectory temporaryDirectory = new();
            string configPath = Path.Combine(temporaryDirectory.Path, "Config.ini");
            File.WriteAllText(configPath, "[Persistence]\nProvider=PostgreSQL");
            PersistenceConfig config = new();

            config.Initialize(new IniFile(configPath), null);

            Assert.Equal("PostgreSQL", config.Provider);
        }
    }
}

using MHServerEmu.Core.Config;

namespace MHServerEmu.Core.Tests.Config
{
    public class ConfigManagerTests
    {
        [Fact]
        public void Constructor_NewConfiguredDirectory_CreatesOverrideFile()
        {
            string configDirectory = Path.Combine(Path.GetTempPath(), $"mhserveremu-config-{Guid.NewGuid()}");

            try
            {
                Assert.False(Directory.Exists(configDirectory));

                new ConfigManager(configDirectory);

                Assert.True(Directory.Exists(configDirectory));
                Assert.True(File.Exists(Path.Combine(configDirectory, "ConfigOverride.ini")));
            }
            finally
            {
                if (Directory.Exists(configDirectory))
                    Directory.Delete(configDirectory, true);
            }
        }
    }
}

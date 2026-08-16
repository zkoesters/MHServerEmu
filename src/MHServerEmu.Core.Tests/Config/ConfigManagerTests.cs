using MHServerEmu.Core.Config;

namespace MHServerEmu.Core.Tests.Config
{
    public class ConfigManagerTests
    {
        [Fact]
        public void GetOverrideString_OverrideContainsConnectionString_ReturnsOverrideValue()
        {
            using TemporaryConfigFiles files = new();
            files.WriteOverride("[PostgreSQL]\nConnectionString=override-secret");
            ConfigManager manager = new(files.ConfigPath, files.OverridePath);

            string value = manager.GetOverrideString("PostgreSQL", "ConnectionString");

            Assert.Equal("override-secret", value);
            Assert.Equal(files.OverridePath, manager.OverrideFilePath);
        }

        [Fact]
        public void GetOverrideString_OverrideHasNoConnectionString_DoesNotReturnBaseValue()
        {
            using TemporaryConfigFiles files = new();
            files.WriteConfig("[PostgreSQL]\nConnectionString=base-secret");
            files.WriteOverride("[PostgreSQL]\nPort=5432");
            ConfigManager manager = new(files.ConfigPath, files.OverridePath);

            string value = manager.GetOverrideString("PostgreSQL", "ConnectionString");

            Assert.True(string.IsNullOrEmpty(value));
            Assert.NotEqual("base-secret", value);
        }

        [Fact]
        public void Initialize_OverrideContainsValue_UsesOverrideOverBase()
        {
            using TemporaryConfigFiles files = new();
            files.WriteConfig("[Test]\nConnectionString=base-value");
            files.WriteOverride("[Test]\nConnectionString=override-value");
            TestConfig config = new();

            config.Initialize(new IniFile(files.ConfigPath), new IniFile(files.OverridePath));

            Assert.Equal("override-value", config.ConnectionString);
        }

        [Fact]
        public void Constructor_MissingOverrideOnUnix_CreatesOwnerReadWriteOnlyFile()
        {
            if (OperatingSystem.IsWindows())
                return;

            using TemporaryConfigFiles files = new();

            _ = new ConfigManager(files.ConfigPath, files.OverridePath);

            Assert.True(File.Exists(files.OverridePath));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(files.OverridePath));
        }

        [Theory]
        [InlineData((int)UnixFileMode.GroupRead)]
        [InlineData((int)UnixFileMode.OtherWrite)]
        public void HasUnsafeOverrideFilePermissions_ExistingOverrideAllowsGroupOrOtherReadWrite_ReturnsTrue(int unsafeMode)
        {
            if (OperatingSystem.IsWindows())
                return;

            using TemporaryConfigFiles files = new();
            files.WriteOverride(string.Empty);
            File.SetUnixFileMode(files.OverridePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | (UnixFileMode)unsafeMode);
            ConfigManager manager = new(files.ConfigPath, files.OverridePath);

            Assert.True(manager.HasUnsafeOverrideFilePermissions());
        }

        [Fact]
        public void HasUnsafeOverrideFilePermissions_OnWindows_ReturnsFalse()
        {
            if (OperatingSystem.IsWindows() == false)
                return;

            using TemporaryConfigFiles files = new();
            files.WriteOverride(string.Empty);
            ConfigManager manager = new(files.ConfigPath, files.OverridePath);

            Assert.False(manager.HasUnsafeOverrideFilePermissions());
        }

        private sealed class TestConfig : ConfigContainer
        {
            public string ConnectionString { get; set; }
        }
    }

    internal sealed class TemporaryConfigFiles : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        public string ConfigPath { get; }
        public string OverridePath { get; }

        public TemporaryConfigFiles()
        {
            Directory.CreateDirectory(DirectoryPath);
            ConfigPath = Path.Combine(DirectoryPath, "Config.ini");
            OverridePath = Path.Combine(DirectoryPath, "ConfigOverride.ini");
        }

        public void WriteConfig(string contents) => File.WriteAllText(ConfigPath, contents);

        public void WriteOverride(string contents) => File.WriteAllText(OverridePath, contents);

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
                Directory.Delete(DirectoryPath, true);
        }
    }
}

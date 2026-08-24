using MHServerEmu.DatabaseAccess;

namespace MHServerEmu.DatabaseAccess.Tests
{
    public class DBManagerFactoryTests
    {
        [Theory]
        [InlineData(null, false, DBManagerType.SQLite, false)]
        [InlineData("", false, DBManagerType.SQLite, false)]
        [InlineData(null, true, DBManagerType.Json, true)]
        [InlineData("   ", true, DBManagerType.Json, true)]
        [InlineData("SQLite", false, DBManagerType.SQLite, false)]
        [InlineData("sqlite", true, DBManagerType.Json, true)]
        [InlineData("Json", false, DBManagerType.Json, false)]
        [InlineData("PostgreSQL", true, DBManagerType.PostgreSQL, false)]
        [InlineData(" PostgreSQL ", true, DBManagerType.PostgreSQL, false)]
        public void TryResolveType_ValidConfiguration_ReturnsExpectedType(
            string configuredType,
            bool useJsonDBManager,
            DBManagerType expectedType,
            bool expectedLegacyJsonSetting)
        {
            bool resolved = DBManagerFactory.TryResolveType(
                configuredType,
                useJsonDBManager,
                out DBManagerType type,
                out bool usedLegacyJsonSetting);

            Assert.True(resolved);
            Assert.Equal(expectedType, type);
            Assert.Equal(expectedLegacyJsonSetting, usedLegacyJsonSetting);
        }

        [Theory]
        [InlineData("Oracle")]
        [InlineData("1")]
        public void TryResolveType_InvalidConfiguredType_ReturnsFalse(string configuredType)
        {
            bool resolved = DBManagerFactory.TryResolveType(
                configuredType,
                false,
                out _,
                out _);

            Assert.False(resolved);
        }
    }
}

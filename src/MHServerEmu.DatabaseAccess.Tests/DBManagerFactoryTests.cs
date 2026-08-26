using MHServerEmu.DatabaseAccess;
using MHServerEmu.DatabaseAccess.Json;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.DatabaseAccess.MySQL;
using MHServerEmu.DatabaseAccess.PostgreSQL;
using MHServerEmu.DatabaseAccess.SQLite;

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
        [InlineData("MySQL", true, DBManagerType.MySQL, false)]
        [InlineData("mysql", true, DBManagerType.MySQL, false)]
        [InlineData(" MySQL ", true, DBManagerType.MySQL, false)]
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

        [Theory]
        [InlineData(null, false, "SQLite")]
        [InlineData(null, true, "Json")]
        [InlineData("Json", false, "Json")]
        [InlineData("SQLite", false, "SQLite")]
        [InlineData("SQLite", true, "Json")]
        [InlineData("MySQL", true, "MySQL")]
        [InlineData("PostgreSQL", true, "PostgreSQL")]
        public void TryCreate_ValidConfiguration_ReturnsExpectedSingleton(
            string configuredType,
            bool useJsonDBManager,
            string expectedType)
        {
            bool created = DBManagerFactory.TryCreate(
                configuredType,
                useJsonDBManager,
                out IDBManager manager,
                out _);

            Assert.True(created);
            Assert.Same(GetExpectedManager(expectedType), manager);
        }

        [Theory]
        [InlineData("Oracle")]
        [InlineData("1")]
        public void TryCreate_InvalidConfiguredType_ReturnsFalseAndNullManager(string configuredType)
        {
            bool created = DBManagerFactory.TryCreate(
                configuredType,
                false,
                out IDBManager manager,
                out _);

            Assert.False(created);
            Assert.Null(manager);
        }

        [Fact]
        public void TryCreate_MySQLGuildLoad_DoesNotThrow()
        {
            Assert.True(DBManagerFactory.TryCreate("MySQL", false, out IDBManager manager, out _));
            Assert.IsType<MySQLDBManager>(manager);

            Assert.Null(Record.Exception(() => manager.LoadGuilds(new List<DBGuild>())));
        }

        private static IDBManager GetExpectedManager(string configuredType)
        {
            return configuredType switch
            {
                "Json" => JsonDBManager.Instance,
                "SQLite" => SQLiteDBManager.Instance,
                "MySQL" => MySQLDBManager.Instance,
                "PostgreSQL" => PostgreSQLDBManager.Instance,
                _ => null
            };
        }
    }
}

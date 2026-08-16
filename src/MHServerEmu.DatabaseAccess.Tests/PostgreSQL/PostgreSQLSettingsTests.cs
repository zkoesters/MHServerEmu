using MHServerEmu.Core.Config;
using MHServerEmu.DatabaseAccess.PostgreSQL;
using MHServerEmu.DatabaseAccess.PostgreSQL.Configuration;

namespace MHServerEmu.DatabaseAccess.Tests.PostgreSQL
{
    public class PostgreSQLSettingsTests
    {
        [Fact]
        public void TryCreate_ValidConnection_EnforcesTypedConnectionSettings()
        {
            PostgreSQLConfig config = new();

            bool result = PostgreSQLSettings.TryCreate("Host=localhost;Database=mh;Username=server;Password=secret", config, false, out PostgreSQLSettings settings, out PostgreSQLPersistenceFailure failure);

            Assert.True(result);
            Assert.Null(failure);
            Assert.Equal(20, settings.MaxPoolSize);
            Assert.Equal(5, settings.ConnectTimeoutSeconds);
            Assert.Equal(30, settings.CommandTimeoutSeconds);
            Assert.Equal(2000, settings.CancellationTimeoutMilliseconds);
            Assert.Contains("Pooling=True", settings.ConnectionString);
            Assert.Contains("Minimum Pool Size=0", settings.ConnectionString);
            Assert.Contains("Maximum Pool Size=20", settings.ConnectionString);
            Assert.Contains("Include Error Detail=False", settings.ConnectionString);
            Assert.Contains("Persist Security Info=False", settings.ConnectionString);
        }

        [Theory]
        [InlineData("Host=localhost;Pooling=true")]
        [InlineData("Host=localhost;Pooling=false")]
        [InlineData("Host=localhost;Minimum Pool Size=0")]
        [InlineData("Host=localhost;Minimum Pool Size=1")]
        [InlineData("Host=localhost;Maximum Pool Size=21")]
        [InlineData("Host=localhost;Timeout=8")]
        [InlineData("Host=localhost;Command Timeout=8")]
        [InlineData("Host=localhost;Cancellation Timeout=8")]
        [InlineData("Host=localhost;Include Error Detail=true")]
        [InlineData("Host=localhost;Persist Security Info=true")]
        public void TryCreate_ConnectionOverridesOwnedOrUnsafeSettings_ReturnsSanitizedFailure(string connectionString)
        {
            bool result = PostgreSQLSettings.TryCreate(connectionString, new PostgreSQLConfig(), false, out PostgreSQLSettings settings, out PostgreSQLPersistenceFailure failure);

            Assert.False(result);
            Assert.Null(settings);
            Assert.NotNull(failure);
            Assert.Equal("InvalidSettings", failure.Code);
            Assert.DoesNotContain(connectionString, failure.ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void TryCreate_InvalidTypedSettings_ReturnsSanitizedFailure()
        {
            PostgreSQLConfig config = new() { MaxPoolSize = 3 };

            bool result = PostgreSQLSettings.TryCreate("Host=localhost", config, false, out PostgreSQLSettings settings, out PostgreSQLPersistenceFailure failure);

            Assert.False(result);
            Assert.Null(settings);
            Assert.Equal("InvalidSettings", failure.Code);
        }

        [Fact]
        public void CreateWriterConnectionString_DisablesPooling()
        {
            Assert.True(PostgreSQLSettings.TryCreate("Host=localhost", new PostgreSQLConfig(), false, out PostgreSQLSettings settings, out _));

            string writerConnectionString = settings.CreateWriterConnectionString();

            Assert.Contains("Pooling=False", writerConnectionString);
        }

        [Fact]
        public void Build_CreatesPooledDataSourceWithEffectiveSettings()
        {
            Assert.True(PostgreSQLSettings.TryCreate("Host=localhost", new PostgreSQLConfig(), false, out PostgreSQLSettings settings, out _));

            using var dataSource = PostgreSQLDataSourceFactory.Build(settings);

            Assert.Contains("Pooling=True", dataSource.ConnectionString);
            Assert.Contains("Maximum Pool Size=20", dataSource.ConnectionString);
            Assert.Contains("Include Error Detail=False", dataSource.ConnectionString);
        }

        [Fact]
        public void TryLoad_ConnectionStringOutsideOverrideFile_IsNotUsed()
        {
            using TemporaryDirectory temporaryDirectory = new();
            string configPath = Path.Combine(temporaryDirectory.Path, "Config.ini");
            string overridePath = Path.Combine(temporaryDirectory.Path, "ConfigOverride.ini");
            File.WriteAllText(configPath, "[PostgreSQL]\nConnectionString=Host=base");
            ConfigManager manager = new(configPath, overridePath);

            bool result = PostgreSQLSettings.TryLoad(manager, new PostgreSQLConfig(), out PostgreSQLSettings settings, out PostgreSQLPersistenceFailure failure);

            Assert.False(result);
            Assert.Null(settings);
            Assert.Equal("InvalidSettings", failure.Code);
        }
    }
}

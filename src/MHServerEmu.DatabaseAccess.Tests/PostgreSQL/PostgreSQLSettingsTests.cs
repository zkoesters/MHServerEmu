using MHServerEmu.Core.Config;
using MHServerEmu.DatabaseAccess.PostgreSQL;
using MHServerEmu.DatabaseAccess.PostgreSQL.Configuration;
using Npgsql;

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
            Assert.Equal(5000, settings.MigrationLockTimeoutMilliseconds);
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

        [Theory]
        [InlineData("Include Error Detail", "false")]
        [InlineData("Include Error Detail", "true")]
        [InlineData("Persist Security Info", "false")]
        [InlineData("Persist Security Info", "true")]
        public void TryCreate_ExplicitErrorDetailFlags_ReturnsSanitizedFailure(string key, string value)
        {
            bool result = PostgreSQLSettings.TryCreate($"Host=localhost;Password=secret;{key}={value}", new PostgreSQLConfig(), false, out PostgreSQLSettings settings, out PostgreSQLPersistenceFailure failure);

            Assert.False(result);
            Assert.Null(settings);
            Assert.Equal("InvalidSettings", failure.Code);
            Assert.DoesNotContain("secret", failure.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("Log Parameters", "false")]
        [InlineData("Log Parameters", "true")]
        [InlineData("Include Failed Batched Command", "false")]
        [InlineData("Include Failed Batched Command", "true")]
        public void TryCreate_ExplicitDiagnosticFlags_ReturnsSanitizedFailure(string key, string value)
        {
            bool result = PostgreSQLSettings.TryCreate($"Host=localhost;Password=secret;{key}={value}", new PostgreSQLConfig(), false, out PostgreSQLSettings settings, out PostgreSQLPersistenceFailure failure);

            Assert.False(result);
            Assert.Null(settings);
            Assert.Equal("InvalidSettings", failure.Code);
            Assert.DoesNotContain("secret", failure.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TryCreate_ValidConnection_DisablesDiagnosticFlags()
        {
            Assert.True(PostgreSQLSettings.TryCreate("Host=localhost", new PostgreSQLConfig(), false, out PostgreSQLSettings settings, out _));

            NpgsqlConnectionStringBuilder builder = new(settings.ConnectionString);

            Assert.False(builder.LogParameters);
            Assert.False(builder.IncludeFailedBatchedCommand);
        }

        [Fact]
        public void TryCreate_OverflowingStartupBackoff_ReturnsSanitizedFailure()
        {
            PostgreSQLConfig config = new()
            {
                StartupRetryCount = 3,
                StartupRetryDelayMilliseconds = 1073741824,
            };

            Exception exception = Record.Exception(() => PostgreSQLSettings.TryCreate("Host=localhost;Password=secret", config, false, out _, out PostgreSQLPersistenceFailure failure));

            Assert.Null(exception);
            Assert.True(PostgreSQLSettings.TryCreate("Host=localhost;Password=secret", config, false, out PostgreSQLSettings settings, out PostgreSQLPersistenceFailure failure) == false);
            Assert.Null(settings);
            Assert.Equal("InvalidSettings", failure.Code);
            Assert.DoesNotContain("secret", failure.ToString(), StringComparison.OrdinalIgnoreCase);
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

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void TryCreate_NonPositiveMigrationLockTimeout_ReturnsSanitizedFailure(int timeoutMilliseconds)
        {
            PostgreSQLConfig config = new() { MigrationLockTimeoutMilliseconds = timeoutMilliseconds };

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

using MHServerEmu.Core.Config;
using Npgsql;

namespace MHServerEmu.DatabaseAccess.PostgreSQL.Configuration
{
    internal sealed class PostgreSQLSettings
    {
        private PostgreSQLSettings(NpgsqlConnectionStringBuilder connectionStringBuilder, PostgreSQLConfig config, bool hasUnsafeUnixOverrideFilePermissions)
        {
            ConnectionString = connectionStringBuilder.ConnectionString;
            MaxPoolSize = config.MaxPoolSize;
            ConnectTimeoutSeconds = config.ConnectTimeoutSeconds;
            CommandTimeoutSeconds = config.CommandTimeoutSeconds;
            OperationTimeoutSeconds = config.OperationTimeoutSeconds;
            CancellationTimeoutMilliseconds = config.CancellationTimeoutMilliseconds;
            MigrationTimeoutSeconds = config.MigrationTimeoutSeconds;
            StartupRetryCount = config.StartupRetryCount;
            StartupRetryDelayMilliseconds = config.StartupRetryDelayMilliseconds;
            HasUnsafeUnixOverrideFilePermissions = hasUnsafeUnixOverrideFilePermissions;
        }

        internal string ConnectionString { get; }
        internal int MaxPoolSize { get; }
        internal int ConnectTimeoutSeconds { get; }
        internal int CommandTimeoutSeconds { get; }
        internal int OperationTimeoutSeconds { get; }
        internal int CancellationTimeoutMilliseconds { get; }
        internal int MigrationTimeoutSeconds { get; }
        internal int StartupRetryCount { get; }
        internal int StartupRetryDelayMilliseconds { get; }
        internal bool HasUnsafeUnixOverrideFilePermissions { get; }

        internal static bool TryLoad(ConfigManager manager, PostgreSQLConfig config, out PostgreSQLSettings settings, out PostgreSQLPersistenceFailure failure)
        {
            bool hasUnsafeUnixOverrideFilePermissions = manager.HasUnsafeUnixOverrideFilePermissions();
            return TryCreate(manager.GetOverrideString("PostgreSQL", "ConnectionString"), config, hasUnsafeUnixOverrideFilePermissions, out settings, out failure);
        }

        internal static bool TryCreate(string connectionString, PostgreSQLConfig config, bool hasUnsafeUnixOverrideFilePermissions, out PostgreSQLSettings settings, out PostgreSQLPersistenceFailure failure)
        {
            settings = null;
            failure = null;

            if (hasUnsafeUnixOverrideFilePermissions)
            {
                failure = new("UnsafeOverrideFilePermissions", "SettingsLoad");
                return false;
            }

            if (config == null || AreTypedSettingsValid(config) == false || string.IsNullOrWhiteSpace(connectionString))
            {
                failure = new("InvalidSettings", "SettingsLoad");
                return false;
            }

            try
            {
                NpgsqlConnectionStringBuilder builder = new(connectionString);
                if (ContainsRejectedSetting(builder))
                {
                    failure = new("InvalidSettings", "SettingsLoad");
                    return false;
                }

                builder.Pooling = true;
                builder.MinPoolSize = 0;
                builder.MaxPoolSize = config.MaxPoolSize;
                builder.Timeout = config.ConnectTimeoutSeconds;
                builder.CommandTimeout = config.CommandTimeoutSeconds;
                builder.CancellationTimeout = config.CancellationTimeoutMilliseconds;
                builder.IncludeErrorDetail = false;
                builder.PersistSecurityInfo = false;
                settings = new(builder, config, false);
                return true;
            }
            catch (ArgumentException)
            {
                failure = new("InvalidSettings", "SettingsLoad");
                return false;
            }
        }

        internal string CreateWriterConnectionString()
        {
            NpgsqlConnectionStringBuilder builder = new(ConnectionString)
            {
                Pooling = false,
            };
            return builder.ConnectionString;
        }

        private static bool AreTypedSettingsValid(PostgreSQLConfig config)
        {
            return config.MaxPoolSize >= 4
                && config.ConnectTimeoutSeconds > 0
                && config.CommandTimeoutSeconds > 0
                && config.OperationTimeoutSeconds > 0
                && config.CancellationTimeoutMilliseconds > 0
                && config.MigrationTimeoutSeconds > 0
                && config.StartupRetryCount > 0
                && config.StartupRetryDelayMilliseconds > 0;
        }

        private static bool ContainsRejectedSetting(NpgsqlConnectionStringBuilder builder)
        {
            return builder.ShouldSerialize("Pooling")
                || builder.ShouldSerialize("Minimum Pool Size")
                || builder.ShouldSerialize("Maximum Pool Size")
                || builder.ShouldSerialize("Timeout")
                || builder.ShouldSerialize("Command Timeout")
                || builder.ShouldSerialize("Cancellation Timeout")
                || builder.ShouldSerialize("Include Error Detail")
                || builder.ShouldSerialize("Persist Security Info");
        }
    }
}

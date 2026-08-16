using MHServerEmu.Core.Config;

namespace MHServerEmu.DatabaseAccess.PostgreSQL.Configuration
{
    public sealed class PostgreSQLConfig : ConfigContainer
    {
        public int MaxPoolSize { get; internal set; } = 20;
        public int ConnectTimeoutSeconds { get; internal set; } = 5;
        public int CommandTimeoutSeconds { get; internal set; } = 30;
        public int OperationTimeoutSeconds { get; internal set; } = 30;
        public int CancellationTimeoutMilliseconds { get; internal set; } = 2000;
        public int MigrationTimeoutSeconds { get; internal set; } = 120;
        public int MigrationLockTimeoutMilliseconds { get; internal set; } = 5000;
        public int StartupRetryCount { get; internal set; } = 3;
        public int StartupRetryDelayMilliseconds { get; internal set; } = 250;
    }
}

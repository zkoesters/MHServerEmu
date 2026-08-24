using MHServerEmu.Core.Config;

namespace MHServerEmu.DatabaseAccess.PostgreSQL
{
    public class PostgreSQLDBManagerConfig : ConfigContainer
    {
        public string ConnectionString { get; private set; } = string.Empty;
    }
}

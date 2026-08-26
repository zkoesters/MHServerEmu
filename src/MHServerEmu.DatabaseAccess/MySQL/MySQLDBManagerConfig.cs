using MHServerEmu.Core.Config;

namespace MHServerEmu.DatabaseAccess.MySQL
{
    public class MySQLDBManagerConfig : ConfigContainer
    {
        public string ConnectionString { get; private set; } = string.Empty;
    }
}

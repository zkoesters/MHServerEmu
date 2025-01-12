using MHServerEmu.Core.Config;

namespace MHServerEmu.DatabaseAccess.PostgreSQL;

public class PostgreSQLDBManagerConfig : ConfigContainer
{
    public string Host { get; private set; } = "localhost";
    public string Port { get; private set; } = "5432";
    public string Username { get; private set; } = "mhserveremu";
    public string Password { get; private set; } = null;
    public string Database { get; private set; } = "mhserveremu";
}
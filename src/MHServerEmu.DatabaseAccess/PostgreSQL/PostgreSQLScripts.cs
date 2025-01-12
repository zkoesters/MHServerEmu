using MHServerEmu.Core.Helpers;
using MHServerEmu.Core.Logging;

namespace MHServerEmu.DatabaseAccess.PostgreSQL;

public class PostgreSQLScripts
{
    private static readonly Logger _logger = LogManager.CreateLogger();
    
    public static string GetInitializationScript()
    {
        string filePath = Path.Combine(FileHelper.DataDirectory, "PostgreSQL", "InitializeDatabase.sql");
        if (File.Exists(filePath) == false)
            return _logger.WarnReturn(string.Empty, $"GetDatabaseInitializationScript(): Initialization script file not found at {FileHelper.GetRelativePath(filePath)}");

        return File.ReadAllText(filePath);
    }

    public static string GetMigrationScript(int currentVersion)
    {
        string filePath = Path.Combine(FileHelper.DataDirectory, "PostgreSQL", "Migrations", $"{currentVersion}.sql");
        if (File.Exists(filePath) == false)
            return _logger.WarnReturn(string.Empty, $"GetMigrationScript(): Migration script for version {currentVersion} not found at {FileHelper.GetRelativePath(filePath)}");

        return File.ReadAllText(filePath);
    }
}
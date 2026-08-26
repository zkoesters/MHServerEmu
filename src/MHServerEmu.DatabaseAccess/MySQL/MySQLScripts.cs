using System.Reflection;

namespace MHServerEmu.DatabaseAccess.MySQL
{
    public static class MySQLScripts
    {
        public static string GetInitializationScript()
        {
            return LoadScript("InitializeDatabase");
        }

        public static string GetMigrationScript(int currentVersion)
        {
            return LoadScript($"Migrations.{currentVersion}");
        }

        public static string GetLeaderboardInitializationScript()
        {
            return LoadScript("InitializeLeaderboardsDatabase");
        }

        private static string LoadScript(string name)
        {
            string resourceName = $"MHServerEmu.DatabaseAccess.MySQL.Scripts.{name}.sql";

            using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (stream == null)
                throw new InvalidOperationException($"Script resource '{resourceName}' was not found.");

            using StreamReader reader = new(stream);
            return reader.ReadToEnd();
        }
    }
}

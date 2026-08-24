using System.Reflection;

namespace MHServerEmu.DatabaseAccess.PostgreSQL
{
    public static class PostgreSQLScripts
    {
        public static string GetInitializationScript()
        {
            return LoadScript("InitializeDatabase");
        }

        public static string GetMigrationScript(int currentVersion)
        {
            return LoadScript($"Migrations.{currentVersion}");
        }

        private static string LoadScript(string name)
        {
            string resourceName = $"MHServerEmu.DatabaseAccess.PostgreSQL.Scripts.{name}.sql";

            using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (stream == null)
                throw new InvalidOperationException($"Script resource '{resourceName}' was not found.");

            using StreamReader reader = new(stream);
            return reader.ReadToEnd();
        }
    }
}

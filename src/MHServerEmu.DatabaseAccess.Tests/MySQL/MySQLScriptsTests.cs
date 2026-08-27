using System.Reflection;
using MHServerEmu.DatabaseAccess.MySQL;

namespace MHServerEmu.DatabaseAccess.Tests.MySQL
{
    public class MySQLScriptsTests
    {
        [Fact]
        public void GetInitializationScript_ContainsRequiredSchemaElements()
        {
            string script = MySQLScripts.GetInitializationScript();

            Assert.Contains("CREATE TABLE mhserveremu_schema", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("CREATE TABLE account", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("CREATE TABLE player", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("CREATE TABLE avatar", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("CREATE TABLE team_up", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("CREATE TABLE item", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("CREATE TABLE controlled_entity", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("CREATE TABLE guild", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("CREATE TABLE guild_member", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("email VARCHAR(320) COLLATE utf8mb4_unicode_ci", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("player_name VARCHAR(16) COLLATE utf8mb4_unicode_ci", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("archive_data LONGBLOB", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("slot BIGINT CHECK (slot BETWEEN 0 AND 4294967295)", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("flags BIGINT", script, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData(0, "CREATE TABLE player")]
        [InlineData(1, "CHANGE COLUMN is_banned flags INT NOT NULL")]
        [InlineData(2, "gazillionite_balance BIGINT")]
        [InlineData(3, "DROP COLUMN start_target_region_override")]
        [InlineData(4, "CREATE TABLE guild")]
        [InlineData(5, "compatibility boundary")]
        public void GetMigrationScript_ContainsRequiredTransition(int version, string requiredText)
        {
            string script = MySQLScripts.GetMigrationScript(version);

            Assert.False(string.IsNullOrWhiteSpace(script));
            Assert.Contains(requiredText, script, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void SchemaVersion0Resource_IsEmbedded()
        {
            const string resourceName = "MHServerEmu.DatabaseAccess.Tests.MySQL.Scripts.SchemaVersion0.sql";
            using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);

            Assert.NotNull(stream);
        }

        [Fact]
        public void GetMigrationScript_VersionSixIsNotEmbedded()
        {
            Assert.Throws<InvalidOperationException>(() => MySQLScripts.GetMigrationScript(6));
        }
    }
}

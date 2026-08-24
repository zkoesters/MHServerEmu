using MHServerEmu.DatabaseAccess.PostgreSQL;

namespace MHServerEmu.DatabaseAccess.Tests.PostgreSQL
{
    public class PostgreSQLScriptsTests
    {
        [Fact]
        public void GetInitializationScript_ContainsRequiredSchemaElements()
        {
            string script = PostgreSQLScripts.GetInitializationScript();

            Assert.Contains("INSERT INTO mhserveremu_schema", script);
            Assert.Contains("VALUES (1, 6)", script);
            Assert.Contains("id smallint PRIMARY KEY CHECK (id = 1)", script);
            Assert.Contains("CREATE TABLE account", script);
            Assert.Contains("password_hash bytea NOT NULL", script);
            Assert.Contains("CREATE TABLE guild_member", script);
            Assert.Contains("lower(email)", script);
            Assert.Contains("ux_account_email_ci", script);
            Assert.Contains("ux_account_player_name_ci", script);
            Assert.Contains("ix_avatar_container_db_guid", script);
            Assert.Contains("ix_team_up_container_db_guid", script);
            Assert.Contains("ix_item_container_db_guid", script);
            Assert.Contains("ix_controlled_entity_container_db_guid", script);
            Assert.Contains("ux_guild_name_ci", script);
        }

        [Theory]
        [InlineData(0, "CREATE TABLE player")]
        [InlineData(1, "RENAME COLUMN is_banned TO flags")]
        [InlineData(2, "gazillionite_balance bigint")]
        [InlineData(3, "DROP COLUMN start_target_region_override")]
        [InlineData(4, "CREATE TABLE guild")]
        [InlineData(5, "compatibility boundary")]
        public void GetMigrationScript_ContainsRequiredTransition(int version, string requiredText)
        {
            string script = PostgreSQLScripts.GetMigrationScript(version);

            Assert.False(string.IsNullOrWhiteSpace(script));
            Assert.Contains(requiredText, script, StringComparison.OrdinalIgnoreCase);
        }
    }
}

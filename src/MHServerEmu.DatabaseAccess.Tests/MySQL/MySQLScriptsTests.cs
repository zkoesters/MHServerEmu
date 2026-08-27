using System.Reflection;
using MHServerEmu.DatabaseAccess.MySQL;
using MySqlConnector;

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
        }

        [Fact]
        public void GetLeaderboardInitializationScript_ContainsRequiredSchemaElements()
        {
            string script = MySQLScripts.GetLeaderboardInitializationScript();

            Assert.Contains("CREATE TABLE mhserveremu_leaderboards_schema", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("id SMALLINT PRIMARY KEY CHECK (id = 1)", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("version INT NOT NULL", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("INSERT INTO mhserveremu_leaderboards_schema", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("CREATE TABLE leaderboard (", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("CREATE TABLE leaderboard_instance (", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("CREATE TABLE leaderboard_entry (", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("CREATE TABLE leaderboard_meta_entry (", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("CREATE TABLE leaderboard_reward (", script, StringComparison.OrdinalIgnoreCase);

            string metadataTable = GetTableDefinition(script, "mhserveremu_leaderboards_schema");
            string leaderboardTable = GetTableDefinition(script, "leaderboard");
            string instanceTable = GetTableDefinition(script, "leaderboard_instance");
            string entryTable = GetTableDefinition(script, "leaderboard_entry");
            string metaEntryTable = GetTableDefinition(script, "leaderboard_meta_entry");
            string rewardTable = GetTableDefinition(script, "leaderboard_reward");

            AssertTableContains(metadataTable, "id SMALLINT PRIMARY KEY CHECK (id = 1)", "version INT NOT NULL");
            AssertTableContains(leaderboardTable, "leaderboard_id BIGINT PRIMARY KEY", "prototype_name VARCHAR(255)", "active_instance_id BIGINT", "is_enabled BOOLEAN", "start_time BIGINT", "max_reset_count INT");
            AssertTableDoesNotContain(leaderboardTable, "prototype_name VARCHAR(255) NOT NULL", "active_instance_id BIGINT NOT NULL", "is_enabled BOOLEAN NOT NULL", "start_time BIGINT NOT NULL", "max_reset_count INT NOT NULL");
            AssertTableContains(instanceTable, "instance_id BIGINT PRIMARY KEY", "leaderboard_id BIGINT NOT NULL", "state INT", "activation_date BIGINT", "visible BOOLEAN");
            AssertTableDoesNotContain(instanceTable, "state INT NOT NULL", "activation_date BIGINT NOT NULL", "visible BOOLEAN NOT NULL");
            AssertTableContains(entryTable, "instance_id BIGINT NOT NULL", "participant_id BIGINT NOT NULL", "score BIGINT", "high_score BIGINT", "rule_states LONGBLOB", "PRIMARY KEY (instance_id, participant_id)");
            AssertTableDoesNotContain(entryTable, "score BIGINT NOT NULL", "high_score BIGINT NOT NULL", "rule_states LONGBLOB NOT NULL");
            AssertTableContains(metaEntryTable, "leaderboard_id BIGINT NOT NULL", "instance_id BIGINT NOT NULL", "sub_leaderboard_id BIGINT NOT NULL", "sub_instance_id BIGINT NOT NULL", "PRIMARY KEY (leaderboard_id, instance_id, sub_leaderboard_id)");
            AssertTableContains(rewardTable, "leaderboard_id BIGINT NOT NULL", "instance_id BIGINT NOT NULL", "participant_id BIGINT NOT NULL", "`rank` INT NOT NULL", "reward_id BIGINT NOT NULL", "creation_date BIGINT", "rewarded_date BIGINT", "PRIMARY KEY (leaderboard_id, instance_id, participant_id)");
            AssertTableDoesNotContain(rewardTable, "creation_date BIGINT NOT NULL", "rewarded_date BIGINT NOT NULL");

            string[] requiredPrimaryKeys =
            [
                "leaderboard_id BIGINT PRIMARY KEY",
                "instance_id BIGINT PRIMARY KEY",
                "PRIMARY KEY (instance_id, participant_id)",
                "PRIMARY KEY (leaderboard_id, instance_id, sub_leaderboard_id)",
                "PRIMARY KEY (leaderboard_id, instance_id, participant_id)"
            ];
            foreach (string primaryKey in requiredPrimaryKeys)
                Assert.Contains(primaryKey, script, StringComparison.OrdinalIgnoreCase);

            const string instanceForeignKey = "CONSTRAINT fk_leaderboard_instance_leaderboard FOREIGN KEY (leaderboard_id) REFERENCES leaderboard (leaderboard_id) ON DELETE CASCADE";
            const string entryForeignKey = "CONSTRAINT fk_leaderboard_entry_instance FOREIGN KEY (instance_id) REFERENCES leaderboard_instance (instance_id) ON DELETE CASCADE";
            const string metaEntryForeignKey = "CONSTRAINT fk_leaderboard_meta_entry_leaderboard FOREIGN KEY (leaderboard_id) REFERENCES leaderboard (leaderboard_id) ON DELETE CASCADE";
            const string rewardForeignKey = "CONSTRAINT fk_leaderboard_reward_instance FOREIGN KEY (instance_id) REFERENCES leaderboard_instance (instance_id) ON DELETE CASCADE";
            string[] requiredForeignKeys = [instanceForeignKey, entryForeignKey, metaEntryForeignKey, rewardForeignKey];
            foreach (string foreignKey in requiredForeignKeys)
                Assert.Contains(foreignKey, script, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(4, script.Split("FOREIGN KEY", StringSplitOptions.None).Length - 1);
            Assert.DoesNotContain("CONSTRAINT fk_leaderboard_meta_entry_instance", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("CONSTRAINT fk_leaderboard_reward_leaderboard", script, StringComparison.OrdinalIgnoreCase);

            const string instancesIndex = "KEY idx_instances_leaderboardid (leaderboard_id)";
            const string entriesIndex = "KEY idx_entries_instanceid (instance_id)";
            const string metaEntriesIndex = "KEY idx_meta_entries_leaderboardid (leaderboard_id)";
            const string rewardsInstanceIndex = "KEY idx_rewards_instanceid (instance_id)";
            const string rewardsParticipantIndex = "KEY idx_rewards_participantid (participant_id)";
            string[] requiredIndexes = [instancesIndex, entriesIndex, metaEntriesIndex, rewardsInstanceIndex, rewardsParticipantIndex];
            foreach (string index in requiredIndexes)
                Assert.Contains(index, script, StringComparison.OrdinalIgnoreCase);
            AssertTableContains(instanceTable, instancesIndex, instanceForeignKey);
            AssertTableContains(entryTable, entriesIndex, entryForeignKey);
            AssertTableContains(metaEntryTable, metaEntriesIndex, metaEntryForeignKey);
            AssertTableContains(rewardTable, rewardsInstanceIndex, rewardsParticipantIndex, rewardForeignKey);
            Assert.True(instanceTable.IndexOf(instancesIndex, StringComparison.OrdinalIgnoreCase) < instanceTable.IndexOf(instanceForeignKey, StringComparison.OrdinalIgnoreCase));
            Assert.True(entryTable.IndexOf(entriesIndex, StringComparison.OrdinalIgnoreCase) < entryTable.IndexOf(entryForeignKey, StringComparison.OrdinalIgnoreCase));
            Assert.True(metaEntryTable.IndexOf(metaEntriesIndex, StringComparison.OrdinalIgnoreCase) < metaEntryTable.IndexOf(metaEntryForeignKey, StringComparison.OrdinalIgnoreCase));
            Assert.True(rewardTable.IndexOf(rewardsInstanceIndex, StringComparison.OrdinalIgnoreCase) < rewardTable.IndexOf(rewardForeignKey, StringComparison.OrdinalIgnoreCase));

            string[] signedBigIntColumns =
            [
                "leaderboard_id BIGINT", "active_instance_id BIGINT", "start_time BIGINT", "instance_id BIGINT",
                "activation_date BIGINT", "participant_id BIGINT", "score BIGINT", "high_score BIGINT",
                "sub_leaderboard_id BIGINT", "sub_instance_id BIGINT", "reward_id BIGINT", "creation_date BIGINT", "rewarded_date BIGINT"
            ];
            foreach (string column in signedBigIntColumns)
                Assert.Contains(column, script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("BIGINT UNSIGNED", script, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(20, script.Split("BIGINT", StringSplitOptions.None).Length - 1);
            Assert.Contains("max_reset_count INT", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("state INT", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("is_enabled BOOLEAN", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("visible BOOLEAN", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("`rank` INT NOT NULL", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("rewarded_date BIGINT NOT NULL", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("rule_states LONGBLOB", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ENGINE=InnoDB DEFAULT CHARACTER SET=utf8mb4 COLLATE=utf8mb4_unicode_ci", script, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(6, script.Split("ENGINE=InnoDB DEFAULT CHARACTER SET=utf8mb4 COLLATE=utf8mb4_unicode_ci", StringSplitOptions.None).Length - 1);
        }

        [MySQLFact]
        public void GetLeaderboardInitializationScript_ExecutesAndCreatesExpectedTables()
        {
            using MySQLTestDatabase database = new();
            using MySqlConnection connection = database.OpenConnection();
            using (MySqlCommand command = new(MySQLScripts.GetLeaderboardInitializationScript(), connection))
                command.ExecuteNonQuery();

            using MySqlCommand tableCommand = new(@"
                SELECT table_name
                FROM information_schema.tables
                WHERE table_schema = DATABASE()
                ORDER BY table_name", connection);
            using MySqlDataReader reader = tableCommand.ExecuteReader();

            List<string> tableNames = [];
            while (reader.Read())
                tableNames.Add(reader.GetString(0));

            Assert.Equal(
            [
                "leaderboard",
                "leaderboard_entry",
                "leaderboard_instance",
                "leaderboard_meta_entry",
                "leaderboard_reward",
                "mhserveremu_leaderboards_schema"
            ], tableNames);
        }

        private static string GetTableDefinition(string script, string tableName)
        {
            const string tableTerminator = ") ENGINE=InnoDB DEFAULT CHARACTER SET=utf8mb4 COLLATE=utf8mb4_unicode_ci;";
            int startIndex = script.IndexOf($"CREATE TABLE {tableName} (", StringComparison.OrdinalIgnoreCase);
            Assert.True(startIndex >= 0);

            int endIndex = script.IndexOf(tableTerminator, startIndex, StringComparison.OrdinalIgnoreCase);
            Assert.True(endIndex >= 0);

            return script[startIndex..(endIndex + tableTerminator.Length)];
        }

        private static void AssertTableContains(string table, params string[] values)
        {
            foreach (string value in values)
                Assert.Contains(value, table, StringComparison.OrdinalIgnoreCase);
        }

        private static void AssertTableDoesNotContain(string table, params string[] values)
        {
            foreach (string value in values)
                Assert.DoesNotContain(value, table, StringComparison.OrdinalIgnoreCase);
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
        public void GetMigrationScript_VersionSixIsNotEmbedded()
        {
            Assert.Throws<InvalidOperationException>(() => MySQLScripts.GetMigrationScript(6));
        }

        [Fact]
        public void SchemaVersion0Resource_IsEmbedded()
        {
            const string resourceName = "MHServerEmu.DatabaseAccess.Tests.MySQL.Scripts.SchemaVersion0.sql";
            using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);

            Assert.NotNull(stream);
        }
    }
}

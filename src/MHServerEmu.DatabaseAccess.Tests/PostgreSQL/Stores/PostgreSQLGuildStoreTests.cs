using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.DatabaseAccess.Tests.Conformance;
using MHServerEmu.DatabaseAccess.Tests.PostgreSQL.Migrations;
using Npgsql;

namespace MHServerEmu.DatabaseAccess.Tests.PostgreSQL.Stores
{
    [Trait("Category", "PostgreSQLIntegration")]
    [Collection("PostgreSQL migration integration")]
    public class PostgreSQLGuildStoreTests
    {
        private readonly PostgreSQLTestDatabase _database;

        public PostgreSQLGuildStoreTests(PostgreSQLTestDatabase database)
        {
            _database = database;
        }

        [PostgreSQLIntegrationFact]
        public async Task CreateAndLoad_PersistGuildAndLeaderAtomically()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            await CreateMemberAsync(fixture, 1);
            DBGuild guild = new(10, "Founders", "", 1, 1);

            GuildStoreConformanceTests.AssertCreateLoadAndMutate(fixture.Guilds, guild, new DBGuildMember(1, guild.Id, 3));

            Assert.Equal(2, guild.PersistenceRevision);
            Assert.Equal(PersistenceState.Clean, guild.PersistenceState);
        }

        [PostgreSQLIntegrationFact]
        public async Task CreateGuild_DuplicateNormalizedName_ReturnsNameConflictWithoutLeaderRow()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            await CreateMemberAsync(fixture, 1);
            await CreateMemberAsync(fixture, 2);
            Assert.Equal(GuildStoreResult.Success, fixture.Guilds.CreateGuild(new DBGuild(10, "Founders", "", 1, 1), new DBGuildMember(1, 10, 3)));

            Assert.Equal(GuildStoreResult.NameConflict, fixture.Guilds.CreateGuild(new DBGuild(11, " founders ", "", 2, 1), new DBGuildMember(2, 11, 3)));

            Assert.Equal(0, await CountAsync(fixture.Provider.DataSource, "SELECT COUNT(*) FROM mhserveremu.guild_member WHERE player_account_id = 2"));
        }

        [PostgreSQLIntegrationFact]
        public async Task CreateGuild_ReturningReaderIsDisposedBeforeInsertingLeader()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            await CreateMemberAsync(fixture, 1);
            DBGuild guild = new(10, "Founders", "", 1, 1);

            Assert.Equal(GuildStoreResult.Success, fixture.Guilds.CreateGuild(guild, new DBGuildMember(1, guild.Id, 3)));
            Assert.Equal(1, await CountAsync(fixture.Provider.DataSource, "SELECT COUNT(*) FROM mhserveremu.guild_member WHERE guild_id = 10"));
        }

        [PostgreSQLIntegrationFact]
        public async Task ApplyMembershipTransition_JoinLeaveAndLeadershipTransfer_CommitAtomically()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            await CreateMemberAsync(fixture, 1);
            await CreateMemberAsync(fixture, 2);
            DBGuild guild = new(10, "Founders", "", 1, 1);
            Assert.Equal(GuildStoreResult.Success, fixture.Guilds.CreateGuild(guild, new DBGuildMember(1, guild.Id, 3)));

            Assert.Equal(GuildStoreResult.Success, fixture.Guilds.ApplyMembershipTransition(guild, new GuildMemberTransition(guild.Id, guild.PersistenceRevision, new GuildMemberChange(2, null, 2))));
            Assert.Equal(GuildStoreResult.Success, fixture.Guilds.ApplyMembershipTransition(guild, new GuildMemberTransition(guild.Id, guild.PersistenceRevision, new GuildMemberChange(1, 3, 2), new GuildMemberChange(2, 2, 3))));
            Assert.Equal(GuildStoreResult.Success, fixture.Guilds.ApplyMembershipTransition(guild, new GuildMemberTransition(guild.Id, guild.PersistenceRevision, new GuildMemberChange(1, 2, null))));

            List<DBGuild> loaded = new();
            Assert.True(fixture.Guilds.LoadGuilds(loaded));
            DBGuild persisted = Assert.Single(loaded);
            DBGuildMember leader = Assert.Single(persisted.Members);
            Assert.Equal(2, leader.PlayerDbGuid);
            Assert.Equal(3, leader.Membership);
            Assert.Equal(3, guild.PersistenceRevision);
        }

        [PostgreSQLIntegrationFact]
        public async Task ApplyMembershipTransition_ExistingOtherGuildMember_ReturnsMembershipConflict()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            await CreateMemberAsync(fixture, 1);
            await CreateMemberAsync(fixture, 2);
            await CreateMemberAsync(fixture, 3);
            DBGuild first = new(10, "First", "", 1, 1);
            DBGuild second = new(11, "Second", "", 2, 1);
            Assert.Equal(GuildStoreResult.Success, fixture.Guilds.CreateGuild(first, new DBGuildMember(1, first.Id, 3)));
            Assert.Equal(GuildStoreResult.Success, fixture.Guilds.CreateGuild(second, new DBGuildMember(2, second.Id, 3)));
            Assert.Equal(GuildStoreResult.Success, fixture.Guilds.ApplyMembershipTransition(first, new GuildMemberTransition(first.Id, first.PersistenceRevision, new GuildMemberChange(3, null, 2))));

            Assert.Equal(GuildStoreResult.MembershipConflict, fixture.Guilds.ApplyMembershipTransition(second, new GuildMemberTransition(second.Id, second.PersistenceRevision, new GuildMemberChange(3, null, 2))));
            Assert.Equal(1, first.PersistenceRevision);
            Assert.Equal(0, second.PersistenceRevision);
        }

        [PostgreSQLIntegrationFact]
        public async Task CheckedMutations_StaleAndMissingGuilds_DoNotMutateModels()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            await CreateMemberAsync(fixture, 1);
            DBGuild guild = new(10, "Founders", "Original", 1, 1);
            Assert.Equal(GuildStoreResult.Success, fixture.Guilds.CreateGuild(guild, new DBGuildMember(1, guild.Id, 3)));
            List<DBGuild> loaded = new();
            Assert.True(fixture.Guilds.LoadGuilds(loaded));
            DBGuild stale = Assert.Single(loaded);

            Assert.Equal(GuildStoreResult.Success, fixture.Guilds.ChangeGuildMotd(guild, "Current"));
            Assert.Equal(GuildStoreResult.StaleRevision, fixture.Guilds.ChangeGuildName(stale, "Stale"));
            Assert.Equal("Founders", stale.Name);
            Assert.Equal(GuildStoreResult.GuildNotFound, fixture.Guilds.ChangeGuildMotd(new DBGuild(99, "Missing", "Original", 1, 1), "Changed"));
        }

        [PostgreSQLIntegrationFact]
        public async Task CheckedNameAndMotd_ZeroRowProbe_ReturnsStaleOrMissing()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            await CreateMemberAsync(fixture, 1);
            DBGuild guild = new(10, "Founders", "Original", 1, 1);
            Assert.Equal(GuildStoreResult.Success, fixture.Guilds.CreateGuild(guild, new DBGuildMember(1, guild.Id, 3)));
            List<DBGuild> loaded = new();
            Assert.True(fixture.Guilds.LoadGuilds(loaded));
            DBGuild stale = Assert.Single(loaded);

            Assert.Equal(GuildStoreResult.Success, fixture.Guilds.ChangeGuildMotd(guild, "Current"));
            Assert.Equal(GuildStoreResult.StaleRevision, fixture.Guilds.ChangeGuildName(stale, "Stale"));
            Assert.Equal(GuildStoreResult.GuildNotFound, fixture.Guilds.ChangeGuildMotd(new DBGuild(99, "Missing", "Original", 1, 1), "Changed"));
        }

        [PostgreSQLIntegrationFact]
        public async Task DeleteGuild_CascadesMembers()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            await CreateMemberAsync(fixture, 1);
            await CreateMemberAsync(fixture, 2);
            DBGuild guild = new(10, "Founders", "", 1, 1);
            Assert.Equal(GuildStoreResult.Success, fixture.Guilds.CreateGuild(guild, new DBGuildMember(1, guild.Id, 3)));
            Assert.Equal(GuildStoreResult.Success, fixture.Guilds.ApplyMembershipTransition(guild, new GuildMemberTransition(guild.Id, guild.PersistenceRevision, new GuildMemberChange(2, null, 2))));

            Assert.Equal(GuildStoreResult.Success, fixture.Guilds.DeleteGuild(guild));
            Assert.Equal(0, await CountAsync(fixture.Provider.DataSource, "SELECT COUNT(*) FROM mhserveremu.guild_member"));
            List<DBGuild> loaded = new();
            Assert.True(fixture.Guilds.LoadGuilds(loaded));
            Assert.Empty(loaded);
        }

        [PostgreSQLIntegrationFact]
        public async Task ChangeGuildMotd_CommitTermination_MarksGuildUncertainAndRejectsRetryWithoutSql()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            await CreateMemberAsync(fixture, 1);
            DBGuild guild = new(10, "Founders", "Original", 1, 1);
            Assert.Equal(GuildStoreResult.Success, fixture.Guilds.CreateGuild(guild, new DBGuildMember(1, guild.Id, 3)));
            await CreateDeferredBackendTerminationAsync(fixture.Provider.DataSource);
            try
            {
                Assert.Equal(GuildStoreResult.OutcomeUncertain, fixture.Guilds.ChangeGuildMotd(guild, "Ambiguous"));
                Assert.Equal(PersistenceState.OutcomeUncertain, guild.PersistenceState);
            }
            finally
            {
                await DropDeferredBackendTerminationAsync(fixture.Provider.DataSource);
            }

            fixture.Provider.FenceForTest();
            Assert.Equal(GuildStoreResult.OutcomeUncertain, fixture.Guilds.ChangeGuildName(guild, "MustNotExecute"));
        }

        private static async Task CreateMemberAsync(PostgreSQLStoreTestFixture fixture, long id)
        {
            DBAccount account = fixture.CreateAccount(id, $"account-{id}@example.test", $"Player{id}");
            Assert.Equal(AccountStoreResult.Success, fixture.AccountStore.InsertAccount(account));
            Assert.Equal(PlayerStoreResult.Success, fixture.Players.SavePlayerData(account));
        }

        private static async Task<long> CountAsync(NpgsqlDataSource dataSource, string sql)
        {
            await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync();
            await using NpgsqlCommand command = new(sql, connection);
            return (long)await command.ExecuteScalarAsync();
        }

        private static async Task CreateDeferredBackendTerminationAsync(NpgsqlDataSource dataSource)
        {
            await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync();
            await using NpgsqlCommand command = new("CREATE FUNCTION mhserveremu.guild_store_test_backend_termination() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN PERFORM pg_terminate_backend(pg_backend_pid()); RETURN NULL; END; $$; CREATE CONSTRAINT TRIGGER guild_store_test_backend_termination AFTER UPDATE ON mhserveremu.guild DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION mhserveremu.guild_store_test_backend_termination();", connection);
            await command.ExecuteNonQueryAsync();
        }

        private static async Task DropDeferredBackendTerminationAsync(NpgsqlDataSource dataSource)
        {
            await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync();
            await using NpgsqlCommand command = new("DROP TRIGGER IF EXISTS guild_store_test_backend_termination ON mhserveremu.guild; DROP FUNCTION IF EXISTS mhserveremu.guild_store_test_backend_termination();", connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}

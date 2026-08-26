using MHServerEmu.Core.Helpers;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.DatabaseAccess.PostgreSQL;
using Npgsql;

namespace MHServerEmu.DatabaseAccess.Tests.PostgreSQL
{
    public class PostgreSQLDBManagerTests
    {
        [Fact]
        public void CreateErrorLogMessage_ContainsOnlyApprovedValues()
        {
            PostgreSQLDBManager manager = new("Host=database.example;Port=5432;Database=mhserveremu;Username=test-user;Password=super-secret");
            string message = manager.CreateErrorLogMessage("InsertAccount", 42);

            Assert.Equal("InsertAccount(): PostgreSQL error for database database.example:5432/mhserveremu accountId=42", message);
            Assert.DoesNotContain("email", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("test-user", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("super-secret", message, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("08006", "connection failure")]
        [InlineData("28P01", "authentication failure")]
        [InlineData(PostgresErrorCodes.UniqueViolation, "schema uniqueness failure")]
        [InlineData("XX000", "schema or migration failure")]
        public void CreateInitializationErrorLogMessage_ClassifiesPostgreSQLFailuresWithoutExceptionDetails(string sqlState, string category)
        {
            PostgreSQLDBManager manager = new("Host=database.example;Port=5432;Database=mhserveremu;Username=test-user;Password=super-secret");
            PostgresException exception = new("failure containing super-secret", "ERROR", "ERROR", sqlState);

            string message = manager.CreateInitializationErrorLogMessage(exception);

            Assert.Equal($"Initialize(): PostgreSQL error for database database.example:5432/mhserveremu: {category} (SQLSTATE {sqlState})", message);
            Assert.DoesNotContain("test-user", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("super-secret", message, StringComparison.OrdinalIgnoreCase);
        }

        [PostgreSQLFact]
        public void Initialize_FreshDatabase_SeedsFiveAccountsWithoutReseeding()
        {
            using PostgreSQLTestDatabase database = new();
            PostgreSQLDBManager manager = new(database.ConnectionString);

            Assert.True(manager.Initialize());
            Assert.True(manager.GetPlayerNames(new Dictionary<ulong, string>()));
            Assert.True(manager.TryQueryAccountByEmail("test1@test.com", out DBAccount account));
            Assert.Equal("Player1", account.PlayerName);
            Assert.True(CryptographyHelper.VerifyPassword("123", account.PasswordHash, account.Salt));

            Assert.True(manager.Initialize());
            using NpgsqlConnection connection = database.OpenConnection();
            Assert.Equal(5, GetAccountCount(connection));
        }

        [PostgreSQLFact]
        public void AccountQueries_MatchEmailAndNameCaseInsensitivelyWithStoredCasing()
        {
            using PostgreSQLTestDatabase database = new();
            PostgreSQLDBManager manager = Initialize(database);
            DBAccount account = new("Mixed.Email@example.com", "MixedPlayer", "password");

            Assert.True(manager.InsertAccount(account));
            Assert.True(manager.TryQueryAccountByEmail("mixed.email@EXAMPLE.COM", out DBAccount queriedAccount));
            Assert.Equal(account.Email, queriedAccount.Email);
            Assert.Equal(account.PlayerName, queriedAccount.PlayerName);
            Assert.True(manager.TryGetPlayerDbIdByName("mixedplayer", out ulong playerDbId, out string playerName));
            Assert.Equal((ulong)account.Id, playerDbId);
            Assert.Equal(account.PlayerName, playerName);
            Assert.True(manager.TryGetPlayerName(playerDbId, out string nameById));
            Assert.Equal(account.PlayerName, nameById);
        }

        [PostgreSQLFact]
        public void InsertAccount_DuplicateEmailOrPlayerNameIgnoringCase_ReturnsFalse()
        {
            using PostgreSQLTestDatabase database = new();
            PostgreSQLDBManager manager = Initialize(database);

            Assert.True(manager.InsertAccount(new DBAccount("duplicate@example.com", "DuplicatePlayer", "password")));
            Assert.False(manager.InsertAccount(new DBAccount("DUPLICATE@example.com", "OtherPlayer", "password")));
            Assert.False(manager.InsertAccount(new DBAccount("other@example.com", "duplicateplayer", "password")));
        }

        [PostgreSQLFact]
        public void UpdateAccount_PersistsAllAccountFields()
        {
            using PostgreSQLTestDatabase database = new();
            PostgreSQLDBManager manager = Initialize(database);
            DBAccount account = new("original@example.com", "OriginalPlayer", "password");
            Assert.True(manager.InsertAccount(account));

            account.Email = "updated@example.com";
            account.PlayerName = "UpdatedPlayer";
            account.PasswordHash = CryptographyHelper.HashPassword("updated-password", out byte[] salt);
            account.Salt = salt;
            account.UserLevel = AccountUserLevel.Admin;
            account.Flags = AccountFlags.IsBanned | AccountFlags.BypassLoginQueue;

            Assert.True(manager.UpdateAccount(account));
            Assert.True(manager.TryQueryAccountByEmail("UPDATED@example.com", out DBAccount updated));
            Assert.Equal(account.Id, updated.Id);
            Assert.Equal(account.PlayerName, updated.PlayerName);
            Assert.Equal(account.UserLevel, updated.UserLevel);
            Assert.Equal(account.Flags, updated.Flags);
            Assert.True(CryptographyHelper.VerifyPassword("updated-password", updated.PasswordHash, updated.Salt));
        }

        [PostgreSQLFact]
        public void GetPlayerNames_EnumeratesStoredAccounts()
        {
            using PostgreSQLTestDatabase database = new();
            PostgreSQLDBManager manager = Initialize(database);
            DBAccount first = new("first@example.com", "FirstPlayer", "password");
            DBAccount second = new("second@example.com", "SecondPlayer", "password");
            Assert.True(manager.InsertAccount(first));
            Assert.True(manager.InsertAccount(second));
            Dictionary<ulong, string> playerNames = new();

            Assert.True(manager.GetPlayerNames(playerNames));
            Assert.Equal("FirstPlayer", playerNames[(ulong)first.Id]);
            Assert.Equal("SecondPlayer", playerNames[(ulong)second.Id]);
        }

        [PostgreSQLFact]
        public void TryGetLastLogoutTime_ReturnsFalseForMissingOrNonPositiveValues()
        {
            using PostgreSQLTestDatabase database = new();
            PostgreSQLDBManager manager = Initialize(database);
            const long playerId = 1234;

            Assert.False(manager.TryGetLastLogoutTime((ulong)playerId, out long missingLogoutTime));
            Assert.Equal(0, missingLogoutTime);

            using (NpgsqlConnection connection = database.OpenConnection())
            {
                ExecuteNonQuery(connection, "INSERT INTO player (db_guid, last_logout_time) VALUES (@PlayerId, 0)", playerId);
            }

            Assert.False(manager.TryGetLastLogoutTime((ulong)playerId, out long zeroLogoutTime));
            Assert.Equal(0, zeroLogoutTime);

            using (NpgsqlConnection connection = database.OpenConnection())
            {
                ExecuteNonQuery(connection, "UPDATE player SET last_logout_time = 42 WHERE db_guid = @PlayerId", playerId);
            }

            Assert.True(manager.TryGetLastLogoutTime((ulong)playerId, out long logoutTime));
            Assert.Equal(42, logoutTime);
        }

        [PostgreSQLFact]
        public void AccountLookups_WithMaxPoolSizeOne_ReuseShortLivedConnections()
        {
            using PostgreSQLTestDatabase database = new();
            NpgsqlConnectionStringBuilder connectionStringBuilder = new(database.ConnectionString) { MaxPoolSize = 1, Timeout = 2 };
            PostgreSQLDBManager manager = new(connectionStringBuilder.ConnectionString);
            Assert.True(manager.Initialize());

            for (int i = 0; i < 20; i++)
            {
                Assert.True(manager.TryQueryAccountByEmail("test1@test.com", out DBAccount account));
                Assert.Equal("Player1", account.PlayerName);
            }
        }

        [PostgreSQLFact]
        public void LoadPlayerData_LoadsHierarchyAndReplacesExistingData()
        {
            using PostgreSQLTestDatabase database = new();
            PostgreSQLDBManager manager = Initialize(database);
            const long accountId = long.MinValue + 10;
            DBAccount account = new() { Id = accountId };
            DBEntity avatar = CreateEntity(long.MinValue + 11, accountId, uint.MaxValue);
            DBEntity teamUp = CreateEntity(long.MinValue + 12, accountId, 2);
            DBEntity accountItem = CreateEntity(long.MinValue + 13, accountId, 3);
            DBEntity avatarItem = CreateEntity(long.MinValue + 14, avatar.DbGuid, 4);
            DBEntity controlledEntity = CreateEntity(long.MinValue + 15, avatar.DbGuid, 5);
            DBEntity teamUpItem = CreateEntity(long.MinValue + 16, teamUp.DbGuid, 6);

            using (NpgsqlConnection connection = database.OpenConnection())
            {
                InsertPlayer(connection, accountId);
                InsertEntity(connection, "avatar", avatar);
                InsertEntity(connection, "team_up", teamUp);
                InsertEntity(connection, "item", accountItem);
                InsertEntity(connection, "item", avatarItem);
                InsertEntity(connection, "controlled_entity", controlledEntity);
                InsertEntity(connection, "item", teamUpItem);
            }

            Assert.True(manager.LoadPlayerData(account));
            Assert.Equal(accountId, account.Player.DbGuid);
            Assert.Equal(new byte[] { 1, 2, 3 }, account.Player.ArchiveData);
            Assert.Equal(long.MinValue + 20, account.Player.StartTarget);
            Assert.Equal(42, account.Player.AOIVolume);
            Assert.Equal(long.MinValue + 21, account.Player.GazillioniteBalance);
            Assert.Equal(long.MinValue + 22, account.Player.LastLogoutTime);
            Assert.Equal(long.MinValue + 23, account.Player.Flags);
            AssertEntity(avatar, account.Avatars.Entries);
            AssertEntity(teamUp, account.TeamUps.Entries);
            Assert.Equal(3, account.Items.Count);
            AssertEntity(accountItem, account.Items.Entries);
            AssertEntity(avatarItem, account.Items.Entries);
            AssertEntity(teamUpItem, account.Items.Entries);
            AssertEntity(controlledEntity, account.ControlledEntities.Entries);

            using (NpgsqlConnection connection = database.OpenConnection())
            {
                ExecuteNonQuery(connection, "DELETE FROM avatar");
                ExecuteNonQuery(connection, "DELETE FROM team_up");
                ExecuteNonQuery(connection, "DELETE FROM item");
                ExecuteNonQuery(connection, "DELETE FROM controlled_entity");
                ExecuteNonQuery(connection, "DELETE FROM player");
            }

            Assert.True(manager.LoadPlayerData(account));
            Assert.Equal(accountId, account.Player.DbGuid);
            Assert.Empty(account.Player.ArchiveData);
            Assert.Empty(account.Avatars.Entries);
            Assert.Empty(account.TeamUps.Entries);
            Assert.Empty(account.Items.Entries);
            Assert.Empty(account.ControlledEntities.Entries);
        }

        [PostgreSQLFact]
        public void LoadPlayerData_MissingPlayer_UsesDefaults()
        {
            using PostgreSQLTestDatabase database = new();
            PostgreSQLDBManager manager = Initialize(database);
            const long accountId = 6000;
            DBAccount account = new() { Id = accountId };
            DBPlayer expectedPlayer = new(accountId);

            Assert.True(manager.LoadPlayerData(account));
            Assert.Equal(expectedPlayer.DbGuid, account.Player.DbGuid);
            Assert.Equal(expectedPlayer.ArchiveData, account.Player.ArchiveData);
            Assert.Equal(expectedPlayer.StartTarget, account.Player.StartTarget);
            Assert.Equal(expectedPlayer.AOIVolume, account.Player.AOIVolume);
            Assert.Equal(expectedPlayer.GazillioniteBalance, account.Player.GazillioniteBalance);
            Assert.Equal(expectedPlayer.LastLogoutTime, account.Player.LastLogoutTime);
            Assert.Equal(expectedPlayer.Flags, account.Player.Flags);
            Assert.Empty(account.Avatars.Entries);
            Assert.Empty(account.TeamUps.Entries);
            Assert.Empty(account.Items.Entries);
            Assert.Empty(account.ControlledEntities.Entries);
        }

        [PostgreSQLFact]
        public void LoadPlayerData_RepeatableReadTransaction_LoadsEntities()
        {
            using PostgreSQLTestDatabase database = new();
            PostgreSQLDBManager manager = Initialize(database);
            const long accountId = 6500;
            DBAccount account = new() { Id = accountId };
            DBEntity item = CreateEntity(6501, accountId, 1);

            using (NpgsqlConnection connection = database.OpenConnection())
            {
                InsertPlayer(connection, accountId);
                InsertEntity(connection, "item", item);
            }

            bool functionCreated = false;
            bool itemTableRenamed = false;
            bool itemViewCreated = false;
            try
            {
                using (NpgsqlConnection connection = database.OpenConnection())
                {
                    ExecuteNonQuery(connection, @"CREATE FUNCTION require_repeatable_read() RETURNS boolean LANGUAGE plpgsql AS $$
                        BEGIN
                            IF current_setting('transaction_isolation') <> 'repeatable read' THEN
                                RAISE EXCEPTION 'LoadPlayerData must use a repeatable read transaction';
                            END IF;
                            RETURN true;
                        END;
                    $$");
                    functionCreated = true;
                    ExecuteNonQuery(connection, "ALTER TABLE item RENAME TO item_transaction_test");
                    itemTableRenamed = true;
                    ExecuteNonQuery(connection, "CREATE VIEW item AS SELECT * FROM item_transaction_test WHERE require_repeatable_read()");
                    itemViewCreated = true;
                }

                Assert.True(manager.LoadPlayerData(account));
                AssertEntity(item, account.Items.Entries);
            }
            finally
            {
                using NpgsqlConnection connection = database.OpenConnection();
                if (itemViewCreated)
                    ExecuteNonQuery(connection, "DROP VIEW item");
                if (itemTableRenamed)
                    ExecuteNonQuery(connection, "ALTER TABLE item_transaction_test RENAME TO item");
                if (functionCreated)
                    ExecuteNonQuery(connection, "DROP FUNCTION require_repeatable_read()");
            }
        }

        [PostgreSQLFact]
        public void LoadPlayerData_LaterEntityLoadFailure_ClearsPartialAggregate()
        {
            using PostgreSQLTestDatabase database = new();
            PostgreSQLDBManager manager = Initialize(database);
            const long accountId = 7000;
            DBAccount account = new() { Id = accountId };

            using (NpgsqlConnection connection = database.OpenConnection())
            {
                InsertPlayer(connection, accountId);
                InsertEntity(connection, "avatar", CreateEntity(7001, accountId, 1));
                InsertEntity(connection, "team_up", CreateEntity(7002, accountId, 2));
            }

            Assert.True(manager.LoadPlayerData(account));
            Assert.NotNull(account.Player);
            Assert.NotEmpty(account.Avatars.Entries);
            Assert.NotEmpty(account.TeamUps.Entries);

            bool itemTableRenamed = false;
            try
            {
                using (NpgsqlConnection connection = database.OpenConnection())
                {
                    ExecuteNonQuery(connection, "ALTER TABLE item RENAME TO item_load_failure");
                    itemTableRenamed = true;
                }

                Assert.False(manager.LoadPlayerData(account));
                Assert.Null(account.Player);
                Assert.Empty(account.Avatars.Entries);
                Assert.Empty(account.TeamUps.Entries);
                Assert.Empty(account.Items.Entries);
                Assert.Empty(account.ControlledEntities.Entries);
            }
            finally
            {
                if (itemTableRenamed)
                {
                    using NpgsqlConnection connection = database.OpenConnection();
                    ExecuteNonQuery(connection, "ALTER TABLE item_load_failure RENAME TO item");
                }
            }
        }

        [PostgreSQLFact]
        public void SavePlayerData_RoundTripsInitialAndUpdatedAggregate()
        {
            using PostgreSQLTestDatabase database = new();
            PostgreSQLDBManager manager = Initialize(database);
            const long accountId = long.MinValue + 100;
            DBAccount account = CreateAggregate(accountId);

            Assert.True(manager.SavePlayerData(account));
            AssertAggregate(account, LoadAggregate(manager, accountId));

            account.Player.ArchiveData = new byte[] { 9, 8, 7 };
            account.Player.StartTarget++;
            account.Player.AOIVolume++;
            account.Player.GazillioniteBalance++;
            account.Player.LastLogoutTime++;
            account.Player.Flags++;
            DBEntity avatar = Assert.Single(account.Avatars.Entries);
            avatar.InventoryProtoGuid++;
            avatar.Slot = uint.MaxValue;
            avatar.EntityProtoGuid++;
            avatar.ArchiveData = new byte[] { 6, 5, 4 };

            Assert.True(manager.SavePlayerData(account));
            AssertAggregate(account, LoadAggregate(manager, accountId));
        }

        [PostgreSQLFact]
        public void SavePlayerData_DeletesStaleEntitiesAndReparentsEntity()
        {
            using PostgreSQLTestDatabase database = new();
            PostgreSQLDBManager manager = Initialize(database);
            const long accountId = long.MinValue + 200;
            DBAccount original = CreateAggregate(accountId);
            DBEntity avatarItem = original.Items.Entries.Single(entity => entity.ContainerDbGuid == accountId + 1);
            long staleItemId = original.Items.Entries.Single(entity => entity.ContainerDbGuid == accountId).DbGuid;
            Assert.True(manager.SavePlayerData(original));

            DBAccount updated = CreateAggregate(accountId);
            updated.Items.Clear();
            DBEntity reparentedItem = CreateEntity(avatarItem.DbGuid, accountId + 2, uint.MaxValue);
            updated.Items.Add(reparentedItem);

            Assert.True(manager.SavePlayerData(updated));
            DBAccount loaded = LoadAggregate(manager, accountId);
            Assert.DoesNotContain(loaded.Items.Entries, entity => entity.DbGuid == staleItemId);
            AssertEntity(reparentedItem, loaded.Items.Entries);
            Assert.DoesNotContain(loaded.Items.Entries, entity => entity.DbGuid == avatarItem.DbGuid && entity.ContainerDbGuid == accountId + 1);
        }

        [PostgreSQLFact]
        public void SavePlayerData_RemovedParentsDeleteDescendantsAndRetainCurrentParentChildren()
        {
            using PostgreSQLTestDatabase database = new();
            PostgreSQLDBManager manager = Initialize(database);
            const long accountId = long.MinValue + 250;
            DBAccount original = CreateAggregate(accountId);
            DBEntity removedAvatar = CreateEntity(accountId + 7, accountId, 7);
            DBEntity removedTeamUp = CreateEntity(accountId + 8, accountId, 8);
            original.Avatars.Add(removedAvatar);
            original.TeamUps.Add(removedTeamUp);
            original.Items.Add(CreateEntity(accountId + 9, removedAvatar.DbGuid, 9));
            original.Items.Add(CreateEntity(accountId + 10, removedTeamUp.DbGuid, 10));
            original.ControlledEntities.Add(CreateEntity(accountId + 11, removedAvatar.DbGuid, 11));
            Assert.True(manager.SavePlayerData(original));

            DBAccount updated = CreateAggregate(accountId);
            Assert.True(manager.SavePlayerData(updated));

            using NpgsqlConnection connection = database.OpenConnection();
            Assert.Equal(0, GetEntityCount(connection, "avatar", removedAvatar.DbGuid));
            Assert.Equal(0, GetEntityCount(connection, "team_up", removedTeamUp.DbGuid));
            Assert.Equal(0, GetContainerEntityCount(connection, "item", removedAvatar.DbGuid));
            Assert.Equal(0, GetContainerEntityCount(connection, "controlled_entity", removedAvatar.DbGuid));
            Assert.Equal(0, GetContainerEntityCount(connection, "item", removedTeamUp.DbGuid));
            Assert.Equal(1, GetContainerEntityCount(connection, "item", accountId + 1));
            Assert.Equal(1, GetContainerEntityCount(connection, "controlled_entity", accountId + 1));
            Assert.Equal(1, GetContainerEntityCount(connection, "item", accountId + 2));
        }

        [PostgreSQLFact]
        public void SavePlayerData_ParentDeleteFailureRollsBackDescendantCleanup()
        {
            using PostgreSQLTestDatabase database = new();
            PostgreSQLDBManager manager = Initialize(database);
            const long accountId = long.MinValue + 275;
            DBAccount original = CreateAggregate(accountId);
            DBEntity removedAvatar = CreateEntity(accountId + 7, accountId, 7);
            DBEntity removedTeamUp = CreateEntity(accountId + 8, accountId, 8);
            original.Avatars.Add(removedAvatar);
            original.TeamUps.Add(removedTeamUp);
            original.Items.Add(CreateEntity(accountId + 9, removedAvatar.DbGuid, 9));
            original.Items.Add(CreateEntity(accountId + 10, removedTeamUp.DbGuid, 10));
            original.ControlledEntities.Add(CreateEntity(accountId + 11, removedAvatar.DbGuid, 11));
            Assert.True(manager.SavePlayerData(original));

            bool functionCreated = false;
            bool triggerCreated = false;
            try
            {
                using (NpgsqlConnection connection = database.OpenConnection())
                {
                    ExecuteNonQuery(connection, @"CREATE FUNCTION fail_avatar_delete() RETURNS trigger LANGUAGE plpgsql AS $$
                        BEGIN
                            RAISE EXCEPTION 'induced parent delete failure';
                        END;
                    $$");
                    functionCreated = true;
                    ExecuteNonQuery(connection, "CREATE TRIGGER fail_avatar_delete_trigger BEFORE DELETE ON avatar FOR EACH ROW EXECUTE FUNCTION fail_avatar_delete()");
                    triggerCreated = true;
                }

                Assert.False(manager.SavePlayerData(CreateAggregate(accountId)));

                using NpgsqlConnection assertionConnection = database.OpenConnection();
                Assert.Equal(1, GetEntityCount(assertionConnection, "avatar", removedAvatar.DbGuid));
                Assert.Equal(1, GetEntityCount(assertionConnection, "team_up", removedTeamUp.DbGuid));
                Assert.Equal(1, GetContainerEntityCount(assertionConnection, "item", removedAvatar.DbGuid));
                Assert.Equal(1, GetContainerEntityCount(assertionConnection, "controlled_entity", removedAvatar.DbGuid));
                Assert.Equal(1, GetContainerEntityCount(assertionConnection, "item", removedTeamUp.DbGuid));
            }
            finally
            {
                using NpgsqlConnection connection = database.OpenConnection();
                if (triggerCreated)
                    ExecuteNonQuery(connection, "DROP TRIGGER fail_avatar_delete_trigger ON avatar");
                if (functionCreated)
                    ExecuteNonQuery(connection, "DROP FUNCTION fail_avatar_delete()");
            }
        }

        [PostgreSQLFact]
        public void SavePlayerData_ReparentedChildrenOfRemovedParentsRemainUnderCurrentParents()
        {
            using PostgreSQLTestDatabase database = new();
            PostgreSQLDBManager manager = Initialize(database);
            const long accountId = long.MinValue + 290;
            DBAccount original = CreateAggregate(accountId);
            DBEntity removedAvatar = CreateEntity(accountId + 7, accountId, 7);
            DBEntity removedTeamUp = CreateEntity(accountId + 8, accountId, 8);
            DBEntity avatarItem = CreateEntity(accountId + 9, removedAvatar.DbGuid, 9);
            DBEntity teamUpItem = CreateEntity(accountId + 10, removedTeamUp.DbGuid, 10);
            DBEntity controlledEntity = CreateEntity(accountId + 11, removedAvatar.DbGuid, 11);
            original.Avatars.Add(removedAvatar);
            original.TeamUps.Add(removedTeamUp);
            original.Items.Add(avatarItem);
            original.Items.Add(teamUpItem);
            original.ControlledEntities.Add(controlledEntity);
            Assert.True(manager.SavePlayerData(original));

            DBAccount updated = CreateAggregate(accountId);
            DBEntity reparentedAvatarItem = CreateEntity(avatarItem.DbGuid, accountId + 1, avatarItem.Slot);
            DBEntity reparentedTeamUpItem = CreateEntity(teamUpItem.DbGuid, accountId + 2, teamUpItem.Slot);
            DBEntity reparentedControlledEntity = CreateEntity(controlledEntity.DbGuid, accountId + 1, controlledEntity.Slot);
            updated.Items.Add(reparentedAvatarItem);
            updated.Items.Add(reparentedTeamUpItem);
            updated.ControlledEntities.Add(reparentedControlledEntity);

            Assert.True(manager.SavePlayerData(updated));

            using NpgsqlConnection connection = database.OpenConnection();
            Assert.Equal(0, GetContainerEntityCount(connection, "item", removedAvatar.DbGuid));
            Assert.Equal(0, GetContainerEntityCount(connection, "controlled_entity", removedAvatar.DbGuid));
            Assert.Equal(0, GetContainerEntityCount(connection, "item", removedTeamUp.DbGuid));
            Assert.Equal(1, GetEntityCount(connection, "item", reparentedAvatarItem.DbGuid));
            Assert.Equal(1, GetEntityCount(connection, "item", reparentedTeamUpItem.DbGuid));
            Assert.Equal(1, GetEntityCount(connection, "controlled_entity", reparentedControlledEntity.DbGuid));
            Assert.Equal(2, GetContainerEntityCount(connection, "item", accountId + 1));
            Assert.Equal(2, GetContainerEntityCount(connection, "item", accountId + 2));
            Assert.Equal(2, GetContainerEntityCount(connection, "controlled_entity", accountId + 1));
        }

        [PostgreSQLFact]
        public void SavePlayerData_NonTransientFailureRollsBackAggregateAndCleansUpTrigger()
        {
            using PostgreSQLTestDatabase database = new();
            PostgreSQLDBManager manager = Initialize(database);
            const long accountId = long.MinValue + 300;
            DBAccount original = CreateAggregate(accountId);
            Assert.True(manager.SavePlayerData(original));

            DBAccount changed = CreateAggregate(accountId);
            changed.Player.ArchiveData = new byte[] { 7, 7, 7 };
            changed.Player.Flags = 99;
            Assert.Single(changed.Avatars.Entries).ArchiveData = new byte[] { 8, 8, 8 };

            bool functionCreated = false;
            bool triggerCreated = false;
            try
            {
                using (NpgsqlConnection connection = database.OpenConnection())
                {
                    ExecuteNonQuery(connection, @"CREATE FUNCTION fail_controlled_entity_save() RETURNS trigger LANGUAGE plpgsql AS $$
                        BEGIN
                            RAISE EXCEPTION 'induced non-transient save failure';
                        END;
                    $$");
                    functionCreated = true;
                    ExecuteNonQuery(connection, "CREATE TRIGGER fail_controlled_entity_save_trigger BEFORE INSERT OR UPDATE ON controlled_entity FOR EACH ROW EXECUTE FUNCTION fail_controlled_entity_save()");
                    triggerCreated = true;
                }

                Assert.False(manager.SavePlayerData(changed));
                AssertAggregate(original, LoadAggregate(manager, accountId));
            }
            finally
            {
                using NpgsqlConnection connection = database.OpenConnection();
                if (triggerCreated)
                    ExecuteNonQuery(connection, "DROP TRIGGER fail_controlled_entity_save_trigger ON controlled_entity");
                if (functionCreated)
                    ExecuteNonQuery(connection, "DROP FUNCTION fail_controlled_entity_save()");
            }
        }

        [Fact]
        public void GetRetryDelay_RetriesOnlyTransientFailuresBeforeTheThirdAttempt()
        {
            NpgsqlException transient = new PostgresException("serialization failure", "ERROR", "ERROR", "40001");

            Assert.Equal(TimeSpan.FromMilliseconds(50), PostgreSQLDBManager.GetRetryDelay(transient, 0));
            Assert.Equal(TimeSpan.FromMilliseconds(150), PostgreSQLDBManager.GetRetryDelay(transient, 1));
            Assert.Null(PostgreSQLDBManager.GetRetryDelay(transient, 2));
            Assert.Null(PostgreSQLDBManager.GetRetryDelay(new NpgsqlException("non-transient"), 0));
        }

        [PostgreSQLFact]
        public void SaveGuild_CreatesAndUpdatesOnlyMutableFields()
        {
            using PostgreSQLTestDatabase database = new();
            PostgreSQLDBManager manager = Initialize(database);
            DBGuild guild = new(8001, "Original Guild", "Original MOTD", 8002, 8003);

            Assert.True(manager.SaveGuild(guild));
            guild.Name = "Updated Guild";
            guild.Motd = "Updated MOTD";
            guild.CreatorDbGuid = 8004;
            guild.CreationTime = 8005;
            Assert.True(manager.SaveGuild(guild));

            using NpgsqlConnection connection = database.OpenConnection();
            using NpgsqlCommand command = new("SELECT name, motd, creator_db_guid, creation_time FROM guild WHERE id = @Id", connection);
            command.Parameters.AddWithValue("Id", guild.Id);
            using NpgsqlDataReader reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("Updated Guild", reader.GetString(0));
            Assert.Equal("Updated MOTD", reader.GetString(1));
            Assert.Equal(8002, reader.GetInt64(2));
            Assert.Equal(8003, reader.GetInt64(3));
        }

        [PostgreSQLFact]
        public void SaveGuild_DuplicateNameIgnoringCase_ReturnsFalse()
        {
            using PostgreSQLTestDatabase database = new();
            PostgreSQLDBManager manager = Initialize(database);

            Assert.True(manager.SaveGuild(new DBGuild(8101, "Duplicate Guild", "First", 8102, 8103)));
            Assert.False(manager.SaveGuild(new DBGuild(8104, "duplicate guild", "Second", 8105, 8106)));
        }

        [PostgreSQLFact]
        public void SaveGuildMember_CreatesAndUpdatesOnlyMembership()
        {
            using PostgreSQLTestDatabase database = new();
            PostgreSQLDBManager manager = Initialize(database);
            DBGuildMember member = new(8201, 8202, 1);

            Assert.True(manager.SaveGuildMember(member));
            member.GuildId = 8203;
            member.Membership = 2;
            Assert.True(manager.SaveGuildMember(member));

            using NpgsqlConnection connection = database.OpenConnection();
            using NpgsqlCommand command = new("SELECT guild_id, membership FROM guild_member WHERE player_db_guid = @PlayerDbGuid", connection);
            command.Parameters.AddWithValue("PlayerDbGuid", member.PlayerDbGuid);
            using NpgsqlDataReader reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(8202, reader.GetInt64(0));
            Assert.Equal(2, reader.GetInt64(1));
        }

        [PostgreSQLFact]
        public void LoadGuilds_AttachesKnownMembersAndIgnoresOrphans()
        {
            using PostgreSQLTestDatabase database = new();
            PostgreSQLDBManager manager = Initialize(database);
            DBGuild guild = new(8301, "Loaded Guild", "MOTD", 8302, 8303);
            DBGuildMember member = new(8304, guild.Id, 1);
            Assert.True(manager.SaveGuild(guild));
            Assert.True(manager.SaveGuildMember(member));

            using (NpgsqlConnection connection = database.OpenConnection())
            {
                using NpgsqlCommand command = new("INSERT INTO guild_member (player_db_guid, guild_id, membership) VALUES (@PlayerDbGuid, @GuildId, @Membership)", connection);
                command.Parameters.AddWithValue("PlayerDbGuid", 8305L);
                command.Parameters.AddWithValue("GuildId", 8399L);
                command.Parameters.AddWithValue("Membership", 2L);
                command.ExecuteNonQuery();
            }

            List<DBGuild> guilds = new();
            Assert.True(manager.LoadGuilds(guilds));
            DBGuild loadedGuild = Assert.Single(guilds);
            Assert.Equal(guild.Id, loadedGuild.Id);
            Assert.Equal(guild.Name, loadedGuild.Name);
            Assert.Equal(guild.Motd, loadedGuild.Motd);
            Assert.Equal(guild.CreatorDbGuid, loadedGuild.CreatorDbGuid);
            Assert.Equal(guild.CreationTime, loadedGuild.CreationTime);
            DBGuildMember loadedMember = Assert.Single(loadedGuild.Members);
            Assert.Equal(member.PlayerDbGuid, loadedMember.PlayerDbGuid);
            Assert.Equal(member.GuildId, loadedMember.GuildId);
            Assert.Equal(member.Membership, loadedMember.Membership);
        }

        [PostgreSQLFact]
        public void DeleteGuildMember_RemovesMember()
        {
            using PostgreSQLTestDatabase database = new();
            PostgreSQLDBManager manager = Initialize(database);
            DBGuildMember member = new(8401, 8402, 1);
            Assert.True(manager.SaveGuildMember(member));

            Assert.True(manager.DeleteGuildMember(member));

            using NpgsqlConnection connection = database.OpenConnection();
            Assert.Equal(0, GetGuildMemberCount(connection, member.PlayerDbGuid));
        }

        [PostgreSQLFact]
        public void DeleteGuild_DeletesMembersAndRollsBackBothDeletesWhenGuildDeleteFails()
        {
            using PostgreSQLTestDatabase database = new();
            PostgreSQLDBManager manager = Initialize(database);
            DBGuild guild = new(8501, "Deleted Guild", "MOTD", 8502, 8503);
            DBGuildMember member = new(8504, guild.Id, 1);
            Assert.True(manager.SaveGuild(guild));
            Assert.True(manager.SaveGuildMember(member));

            bool functionCreated = false;
            bool triggerCreated = false;
            try
            {
                using (NpgsqlConnection connection = database.OpenConnection())
                {
                    ExecuteNonQuery(connection, @"CREATE FUNCTION fail_guild_delete() RETURNS trigger LANGUAGE plpgsql AS $$
                        BEGIN
                            RAISE EXCEPTION 'induced guild delete failure';
                        END;
                    $$");
                    functionCreated = true;
                    ExecuteNonQuery(connection, "CREATE TRIGGER fail_guild_delete_trigger BEFORE DELETE ON guild FOR EACH ROW EXECUTE FUNCTION fail_guild_delete()");
                    triggerCreated = true;
                }

                Assert.False(manager.DeleteGuild(guild));

                using (NpgsqlConnection connection = database.OpenConnection())
                {
                    Assert.Equal(1, GetGuildCount(connection, guild.Id));
                    Assert.Equal(1, GetGuildMemberCount(connection, member.PlayerDbGuid));
                }
            }
            finally
            {
                using NpgsqlConnection connection = database.OpenConnection();
                if (triggerCreated)
                    ExecuteNonQuery(connection, "DROP TRIGGER fail_guild_delete_trigger ON guild");
                if (functionCreated)
                    ExecuteNonQuery(connection, "DROP FUNCTION fail_guild_delete()");
            }

            Assert.True(manager.DeleteGuild(guild));
            using NpgsqlConnection assertionConnection = database.OpenConnection();
            Assert.Equal(0, GetGuildCount(assertionConnection, guild.Id));
            Assert.Equal(0, GetGuildMemberCount(assertionConnection, member.PlayerDbGuid));
        }

        private static PostgreSQLDBManager Initialize(PostgreSQLTestDatabase database)
        {
            PostgreSQLDBManager manager = new(database.ConnectionString);
            Assert.True(manager.Initialize());
            return manager;
        }

        private static int GetAccountCount(NpgsqlConnection connection)
        {
            using NpgsqlCommand command = new("SELECT COUNT(*) FROM account", connection);
            return Convert.ToInt32(command.ExecuteScalar());
        }

        private static int GetGuildCount(NpgsqlConnection connection, long guildId)
        {
            using NpgsqlCommand command = new("SELECT COUNT(*) FROM guild WHERE id = @Id", connection);
            command.Parameters.AddWithValue("Id", guildId);
            return Convert.ToInt32(command.ExecuteScalar());
        }

        private static int GetGuildMemberCount(NpgsqlConnection connection, long playerDbGuid)
        {
            using NpgsqlCommand command = new("SELECT COUNT(*) FROM guild_member WHERE player_db_guid = @PlayerDbGuid", connection);
            command.Parameters.AddWithValue("PlayerDbGuid", playerDbGuid);
            return Convert.ToInt32(command.ExecuteScalar());
        }

        private static int GetEntityCount(NpgsqlConnection connection, string tableName, long dbGuid)
        {
            using NpgsqlCommand command = new($"SELECT COUNT(*) FROM {tableName} WHERE db_guid = @DbGuid", connection);
            command.Parameters.AddWithValue("DbGuid", dbGuid);
            return Convert.ToInt32(command.ExecuteScalar());
        }

        private static int GetContainerEntityCount(NpgsqlConnection connection, string tableName, long containerDbGuid)
        {
            using NpgsqlCommand command = new($"SELECT COUNT(*) FROM {tableName} WHERE container_db_guid = @ContainerDbGuid", connection);
            command.Parameters.AddWithValue("ContainerDbGuid", containerDbGuid);
            return Convert.ToInt32(command.ExecuteScalar());
        }

        private static void ExecuteNonQuery(NpgsqlConnection connection, string commandText, long playerId)
        {
            using NpgsqlCommand command = new(commandText, connection);
            command.Parameters.AddWithValue("PlayerId", playerId);
            command.ExecuteNonQuery();
        }

        private static void ExecuteNonQuery(NpgsqlConnection connection, string commandText)
        {
            using NpgsqlCommand command = new(commandText, connection);
            command.ExecuteNonQuery();
        }

        private static DBEntity CreateEntity(long dbGuid, long containerDbGuid, uint slot)
        {
            return new()
            {
                DbGuid = dbGuid,
                ContainerDbGuid = containerDbGuid,
                InventoryProtoGuid = dbGuid + 100,
                Slot = slot,
                EntityProtoGuid = dbGuid + 200,
                ArchiveData = BitConverter.GetBytes(dbGuid)
            };
        }

        private static DBAccount CreateAggregate(long accountId)
        {
            DBAccount account = new()
            {
                Id = accountId,
                Player = new()
                {
                    DbGuid = accountId,
                    ArchiveData = new byte[] { 1, 2, 3 },
                    StartTarget = long.MinValue + 20,
                    AOIVolume = 42,
                    GazillioniteBalance = long.MinValue + 21,
                    LastLogoutTime = long.MinValue + 22,
                    Flags = long.MinValue + 23
                }
            };
            DBEntity avatar = CreateEntity(accountId + 1, accountId, uint.MaxValue);
            DBEntity teamUp = CreateEntity(accountId + 2, accountId, 2);
            account.Avatars.Add(avatar);
            account.TeamUps.Add(teamUp);
            account.Items.Add(CreateEntity(accountId + 3, accountId, 3));
            account.Items.Add(CreateEntity(accountId + 4, avatar.DbGuid, 4));
            account.Items.Add(CreateEntity(accountId + 5, teamUp.DbGuid, 5));
            account.ControlledEntities.Add(CreateEntity(accountId + 6, avatar.DbGuid, 6));
            return account;
        }

        private static DBAccount LoadAggregate(PostgreSQLDBManager manager, long accountId)
        {
            DBAccount account = new() { Id = accountId };
            Assert.True(manager.LoadPlayerData(account));
            return account;
        }

        private static void AssertAggregate(DBAccount expected, DBAccount actual)
        {
            Assert.Equal(expected.Player.DbGuid, actual.Player.DbGuid);
            Assert.Equal(expected.Player.ArchiveData, actual.Player.ArchiveData);
            Assert.Equal(expected.Player.StartTarget, actual.Player.StartTarget);
            Assert.Equal(expected.Player.AOIVolume, actual.Player.AOIVolume);
            Assert.Equal(expected.Player.GazillioniteBalance, actual.Player.GazillioniteBalance);
            Assert.Equal(expected.Player.LastLogoutTime, actual.Player.LastLogoutTime);
            Assert.Equal(expected.Player.Flags, actual.Player.Flags);
            AssertEntities(expected.Avatars.Entries, actual.Avatars.Entries);
            AssertEntities(expected.TeamUps.Entries, actual.TeamUps.Entries);
            AssertEntities(expected.Items.Entries, actual.Items.Entries);
            AssertEntities(expected.ControlledEntities.Entries, actual.ControlledEntities.Entries);
        }

        private static void AssertEntities(IEnumerable<DBEntity> expected, IEnumerable<DBEntity> actual)
        {
            DBEntity[] expectedEntities = expected.OrderBy(entity => entity.DbGuid).ToArray();
            DBEntity[] actualEntities = actual.OrderBy(entity => entity.DbGuid).ToArray();
            Assert.Equal(expectedEntities.Length, actualEntities.Length);
            foreach (DBEntity entity in expectedEntities)
                AssertEntity(entity, actualEntities);
        }

        private static void AssertEntity(DBEntity expected, IEnumerable<DBEntity> entities)
        {
            DBEntity actual = Assert.Single(entities.Where(entity => entity.DbGuid == expected.DbGuid));
            Assert.Equal(expected.DbGuid, actual.DbGuid);
            Assert.Equal(expected.ContainerDbGuid, actual.ContainerDbGuid);
            Assert.Equal(expected.InventoryProtoGuid, actual.InventoryProtoGuid);
            Assert.Equal(expected.Slot, actual.Slot);
            Assert.Equal(expected.EntityProtoGuid, actual.EntityProtoGuid);
            Assert.Equal(expected.ArchiveData, actual.ArchiveData);
        }

        private static void InsertPlayer(NpgsqlConnection connection, long accountId)
        {
            using NpgsqlCommand command = new(@"INSERT INTO player (db_guid, archive_data, start_target, aoi_volume, gazillionite_balance, last_logout_time, flags)
                VALUES (@DbGuid, @ArchiveData, @StartTarget, @AOIVolume, @GazillioniteBalance, @LastLogoutTime, @Flags)", connection);
            command.Parameters.AddWithValue("DbGuid", accountId);
            command.Parameters.AddWithValue("ArchiveData", new byte[] { 1, 2, 3 });
            command.Parameters.AddWithValue("StartTarget", long.MinValue + 20);
            command.Parameters.AddWithValue("AOIVolume", 42);
            command.Parameters.AddWithValue("GazillioniteBalance", long.MinValue + 21);
            command.Parameters.AddWithValue("LastLogoutTime", long.MinValue + 22);
            command.Parameters.AddWithValue("Flags", long.MinValue + 23);
            command.ExecuteNonQuery();
        }

        private static void InsertEntity(NpgsqlConnection connection, string tableName, DBEntity entity)
        {
            using NpgsqlCommand command = new($@"INSERT INTO {tableName} (db_guid, container_db_guid, inventory_proto_guid, slot, entity_proto_guid, archive_data)
                VALUES (@DbGuid, @ContainerDbGuid, @InventoryProtoGuid, @Slot, @EntityProtoGuid, @ArchiveData)", connection);
            command.Parameters.AddWithValue("DbGuid", entity.DbGuid);
            command.Parameters.AddWithValue("ContainerDbGuid", entity.ContainerDbGuid);
            command.Parameters.AddWithValue("InventoryProtoGuid", entity.InventoryProtoGuid);
            command.Parameters.AddWithValue("Slot", (long)entity.Slot);
            command.Parameters.AddWithValue("EntityProtoGuid", entity.EntityProtoGuid);
            command.Parameters.AddWithValue("ArchiveData", entity.ArchiveData);
            command.ExecuteNonQuery();
        }
    }
}

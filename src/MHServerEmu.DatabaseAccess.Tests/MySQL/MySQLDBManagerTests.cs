using MHServerEmu.Core.Helpers;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.DatabaseAccess.MySQL;
using MySqlConnector;

namespace MHServerEmu.DatabaseAccess.Tests.MySQL
{
    public class MySQLDBManagerTests
    {
        [Fact]
        public void CreateErrorLogMessage_ContainsOnlyApprovedValues()
        {
            MySQLDBManager manager = new("Server=database.example;Port=3307;Database=mhserveremu;User ID=test-user;Password=super-secret");

            string message = manager.CreateErrorLogMessage("InsertAccount", 42);

            Assert.Equal("InsertAccount(): MySQL error for database database.example:3307/mhserveremu accountId=42", message);
            Assert.DoesNotContain("email", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("test-user", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("super-secret", message, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData(1042, "connection failure")]
        [InlineData(1045, "authentication failure")]
        [InlineData(1062, "schema uniqueness failure")]
        [InlineData(1105, "schema or migration failure")]
        public void CreateInitializationErrorLogMessage_ClassifiesMySQLFailuresWithoutExceptionDetails(int errorNumber, string category)
        {
            MySQLDBManager manager = new("Server=database.example;Port=3307;Database=mhserveremu;User ID=test-user;Password=super-secret");

            string message = manager.CreateInitializationErrorLogMessage(errorNumber);

            Assert.Equal($"Initialize(): MySQL error for database database.example:3307/mhserveremu: {category} (error {errorNumber})", message);
            Assert.DoesNotContain("test-user", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("super-secret", message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void CreateInitializationErrorLogMessage_UnknownFailureDoesNotExposeExceptionDetails()
        {
            MySQLDBManager manager = new("Server=database.example;Database=mhserveremu;Password=super-secret");

            string message = manager.CreateInitializationErrorLogMessage((int?)null);

            Assert.Equal("Initialize(): MySQL error for database database.example:3306/mhserveremu: unexpected failure", message);
            Assert.DoesNotContain("super-secret", message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void LoadPlayerData_NullAccount_ReturnsFalseWithoutConnecting()
        {
            MySQLDBManager manager = new("Server=database.example;Database=mhserveremu;Password=super-secret");

            Assert.False(manager.LoadPlayerData(null));
        }

        [Fact]
        public void GetRetryDelay_UsesTransientClassificationBeforeTheThirdAttempt()
        {
            Assert.Equal(TimeSpan.FromMilliseconds(50), MySQLDBManager.GetRetryDelay(true, 0));
            Assert.Equal(TimeSpan.FromMilliseconds(150), MySQLDBManager.GetRetryDelay(true, 1));
            Assert.Null(MySQLDBManager.GetRetryDelay(true, 2));
            Assert.Null(MySQLDBManager.GetRetryDelay(false, 0));
        }

        [MySQLFact]
        public void Initialize_FreshDatabase_SeedsFiveAccountsWithoutReseeding()
        {
            using MySQLTestDatabase database = new();
            MySQLDBManager manager = new(database.ConnectionString);

            Assert.True(manager.Initialize());
            Assert.True(manager.GetPlayerNames(new Dictionary<ulong, string>()));
            Assert.True(manager.TryQueryAccountByEmail("test1@test.com", out DBAccount account));
            Assert.Equal("Player1", account.PlayerName);
            Assert.True(CryptographyHelper.VerifyPassword("123", account.PasswordHash, account.Salt));

            Assert.True(manager.Initialize());
            using MySqlConnection connection = database.OpenConnection();
            Assert.Equal(5, GetAccountCount(connection));
        }

        [MySQLFact]
        public void AccountQueries_MatchEmailAndNameCaseInsensitivelyWithStoredCasing()
        {
            using MySQLTestDatabase database = new();
            MySQLDBManager manager = Initialize(database);
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

        [MySQLFact]
        public void InsertAccount_DuplicateEmailOrPlayerNameIgnoringCase_ReturnsFalse()
        {
            using MySQLTestDatabase database = new();
            MySQLDBManager manager = Initialize(database);

            Assert.True(manager.InsertAccount(new DBAccount("duplicate@example.com", "DuplicatePlayer", "password")));
            Assert.False(manager.InsertAccount(new DBAccount("DUPLICATE@example.com", "OtherPlayer", "password")));
            Assert.False(manager.InsertAccount(new DBAccount("other@example.com", "duplicateplayer", "password")));
        }

        [MySQLFact]
        public void UpdateAccount_PersistsAllAccountFields()
        {
            using MySQLTestDatabase database = new();
            MySQLDBManager manager = Initialize(database);
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

        [MySQLFact]
        public void GetPlayerNames_EnumeratesStoredAccounts()
        {
            using MySQLTestDatabase database = new();
            MySQLDBManager manager = Initialize(database);
            DBAccount first = new("first@example.com", "FirstPlayer", "password");
            DBAccount second = new("second@example.com", "SecondPlayer", "password");
            Assert.True(manager.InsertAccount(first));
            Assert.True(manager.InsertAccount(second));
            Dictionary<ulong, string> playerNames = new();

            Assert.True(manager.GetPlayerNames(playerNames));
            Assert.Equal("FirstPlayer", playerNames[(ulong)first.Id]);
            Assert.Equal("SecondPlayer", playerNames[(ulong)second.Id]);
        }

        [MySQLFact]
        public void TryGetLastLogoutTime_ReturnsFalseForMissingOrNonPositiveValues()
        {
            using MySQLTestDatabase database = new();
            MySQLDBManager manager = Initialize(database);
            const long playerId = 1234;

            Assert.False(manager.TryGetLastLogoutTime((ulong)playerId, out long missingLogoutTime));
            Assert.Equal(0, missingLogoutTime);

            using (MySqlConnection connection = database.OpenConnection())
                ExecuteNonQuery(connection, "INSERT INTO player (db_guid, last_logout_time) VALUES (@PlayerId, 0)", playerId);

            Assert.False(manager.TryGetLastLogoutTime((ulong)playerId, out long zeroLogoutTime));
            Assert.Equal(0, zeroLogoutTime);

            using (MySqlConnection connection = database.OpenConnection())
                ExecuteNonQuery(connection, "UPDATE player SET last_logout_time = 42 WHERE db_guid = @PlayerId", playerId);

            Assert.True(manager.TryGetLastLogoutTime((ulong)playerId, out long logoutTime));
            Assert.Equal(42, logoutTime);
        }

        [MySQLFact]
        public void AccountLookups_WithMaxPoolSizeOne_ReuseShortLivedConnections()
        {
            using MySQLTestDatabase database = new();
            MySqlConnectionStringBuilder connectionStringBuilder = new(database.ConnectionString) { MaximumPoolSize = 1, ConnectionTimeout = 2 };
            MySQLDBManager manager = new(connectionStringBuilder.ConnectionString);
            Assert.True(manager.Initialize());

            for (int i = 0; i < 20; i++)
            {
                Assert.True(manager.TryQueryAccountByEmail("test1@test.com", out DBAccount account));
                Assert.Equal("Player1", account.PlayerName);
            }
        }

        [MySQLFact]
        public void LoadPlayerData_LoadsHierarchyAndReplacesExistingData()
        {
            using MySQLTestDatabase database = new();
            MySQLDBManager manager = Initialize(database);
            const long accountId = long.MinValue + 10;
            DBAccount account = new() { Id = accountId };
            DBEntity avatar = CreateEntity(long.MinValue + 11, accountId, uint.MaxValue);
            DBEntity teamUp = CreateEntity(long.MinValue + 12, accountId, 2);
            DBEntity accountItem = CreateEntity(long.MinValue + 13, accountId, 3);
            DBEntity avatarItem = CreateEntity(long.MinValue + 14, avatar.DbGuid, 4);
            DBEntity controlledEntity = CreateEntity(long.MinValue + 15, avatar.DbGuid, 5);
            DBEntity teamUpItem = CreateEntity(long.MinValue + 16, teamUp.DbGuid, 6);

            using (MySqlConnection connection = database.OpenConnection())
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

            using (MySqlConnection connection = database.OpenConnection())
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

        [MySQLFact]
        public void LoadPlayerData_MissingPlayer_UsesDefaults()
        {
            using MySQLTestDatabase database = new();
            MySQLDBManager manager = Initialize(database);
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

        [MySQLFact]
        public void LoadPlayerData_RepeatableReadTransaction_LoadsEntities()
        {
            using MySQLTestDatabase database = new();
            Initialize(database);
            MySqlConnectionStringBuilder managerConnectionStringBuilder = new(database.ConnectionString)
            {
                MaximumPoolSize = 1,
                ConnectionReset = false
            };
            string managerConnectionString = managerConnectionStringBuilder.ConnectionString;
            MySQLDBManager manager = new(managerConnectionString);
            const long accountId = 6500;
            DBAccount account = new() { Id = accountId };
            DBEntity initialItem = CreateEntity(6501, accountId, 1);
            DBEntity concurrentItem = CreateEntity(6502, accountId, 2);
            DBEntity synchronizationTeamUp = CreateEntity(6503, accountId, 3);
            string lockPrefix = database.DatabaseName.Substring("mhserveremu_test_".Length);
            string startedLockName = $"{lockPrefix}_item_load_started";
            string releaseLockName = $"{lockPrefix}_item_load_release";
            string isolationVariable;

            using (MySqlConnection connection = database.OpenConnection())
            {
                string version = Convert.ToString(ExecuteScalar(connection, "SELECT VERSION()"));
                isolationVariable = version.Contains("MariaDB", StringComparison.OrdinalIgnoreCase) ? "@@tx_isolation" : "@@transaction_isolation";
                InsertPlayer(connection, accountId);
                InsertEntity(connection, "item", initialItem);
                InsertEntity(connection, "team_up", synchronizationTeamUp);
            }
            using (MySqlConnection connection = new(managerConnectionString))
            {
                connection.Open();
                ExecuteNonQuery(connection, "SET SESSION TRANSACTION ISOLATION LEVEL READ COMMITTED");
            }

            bool teamUpTableRenamed = false;
            bool teamUpViewCreated = false;
            bool releaseLockAcquired = false;
            Task<int> nonTransactionalLoadTask = null;
            Task<bool> loadTask = null;
            using MySqlConnection synchronizationConnection = database.OpenConnection();
            try
            {
                Assert.Equal(1, GetNamedLock(synchronizationConnection, releaseLockName, 0));
                releaseLockAcquired = true;

                using (MySqlConnection connection = database.OpenConnection())
                {
                    ExecuteNonQuery(connection, "ALTER TABLE team_up RENAME TO team_up_transaction_test");
                    teamUpTableRenamed = true;
                    ExecuteNonQuery(connection, $@"CREATE VIEW team_up AS
                        SELECT * FROM team_up_transaction_test
                        WHERE CASE
                            WHEN GET_LOCK('{startedLockName}', 0) <> 1 THEN FALSE
                            WHEN GET_LOCK('{releaseLockName}', 30) <> 1 THEN FALSE
                            WHEN RELEASE_LOCK('{releaseLockName}') <> 1 THEN FALSE
                            WHEN RELEASE_LOCK('{startedLockName}') <> 1 THEN FALSE
                            ELSE TRUE
                        END");
                    teamUpViewCreated = true;
                }

                nonTransactionalLoadTask = Task.Run(() =>
                {
                    using MySqlConnection connection = new(managerConnectionString);
                    connection.Open();
                    Assert.Equal("READ-COMMITTED", Convert.ToString(ExecuteScalar(connection, $"SELECT {isolationVariable}")));
                    ExecuteScalar(connection, "SELECT db_guid FROM player WHERE db_guid = @DbGuid", "@DbGuid", accountId);
                    GetContainerEntityCount(connection, "team_up", accountId);
                    return GetContainerEntityCount(connection, "item", accountId);
                });
                WaitForNamedLock(synchronizationConnection, startedLockName);

                using (MySqlConnection connection = database.OpenConnection())
                    InsertEntity(connection, "item", concurrentItem);

                Assert.Equal(1, ReleaseNamedLock(synchronizationConnection, releaseLockName));
                releaseLockAcquired = false;
                Assert.Equal(2, nonTransactionalLoadTask.GetAwaiter().GetResult());
                using (MySqlConnection connection = database.OpenConnection())
                    ExecuteNonQuery(connection, "DELETE FROM item WHERE db_guid = 6502");

                Assert.Equal(1, GetNamedLock(synchronizationConnection, releaseLockName, 0));
                releaseLockAcquired = true;
                loadTask = Task.Run(() => manager.LoadPlayerData(account));
                WaitForNamedLock(synchronizationConnection, startedLockName);

                using (MySqlConnection connection = database.OpenConnection())
                    InsertEntity(connection, "item", concurrentItem);

                Assert.Equal(1, ReleaseNamedLock(synchronizationConnection, releaseLockName));
                releaseLockAcquired = false;
                Assert.True(loadTask.GetAwaiter().GetResult());
                AssertEntity(initialItem, account.Items.Entries);
                Assert.DoesNotContain(account.Items.Entries, entity => entity.DbGuid == concurrentItem.DbGuid);

                using (MySqlConnection connection = new(managerConnectionString))
                {
                    connection.Open();
                    ExecuteNonQuery(connection, "SET SESSION TRANSACTION ISOLATION LEVEL READ COMMITTED");
                }
            }
            finally
            {
                Exception cleanupException = null;
                try
                {
                    try
                    {
                        if (releaseLockAcquired)
                            ReleaseNamedLock(synchronizationConnection, releaseLockName);
                    }
                    finally
                    {
                        try
                        {
                            if (nonTransactionalLoadTask != null)
                                nonTransactionalLoadTask.GetAwaiter().GetResult();
                            if (loadTask != null)
                                loadTask.GetAwaiter().GetResult();
                        }
                        finally
                        {
                            try
                            {
                                if (teamUpViewCreated)
                                    ExecuteNonQuery(synchronizationConnection, "DROP VIEW team_up");
                            }
                            finally
                            {
                                if (teamUpTableRenamed)
                                    ExecuteNonQuery(synchronizationConnection, "ALTER TABLE team_up_transaction_test RENAME TO team_up");
                            }
                        }
                    }
                }
                catch (Exception exception)
                {
                    cleanupException = exception;
                    throw;
                }
                finally
                {
                    try
                    {
                        MySqlConnection.ClearPool(new MySqlConnection(managerConnectionString));
                    }
                    catch when (cleanupException != null)
                    {
                    }
                }
            }

            using (MySqlConnection connection = new(managerConnectionString))
            {
                connection.Open();
                Assert.Equal("REPEATABLE-READ", Convert.ToString(ExecuteScalar(connection, $"SELECT {isolationVariable}")));
            }
        }

        [MySQLFact]
        public void LoadPlayerData_LaterEntityLoadFailure_ClearsPartialAggregate()
        {
            using MySQLTestDatabase database = new();
            MySQLDBManager manager = Initialize(database);
            const long accountId = 7000;
            DBAccount account = new() { Id = accountId };

            using (MySqlConnection connection = database.OpenConnection())
            {
                InsertPlayer(connection, accountId);
                InsertEntity(connection, "avatar", CreateEntity(7001, accountId, 1));
                InsertEntity(connection, "team_up", CreateEntity(7002, accountId, 2));
            }

            Assert.True(manager.LoadPlayerData(account));
            Assert.NotNull(account.Player);
            Assert.NotEmpty(account.Avatars.Entries);
            Assert.NotEmpty(account.TeamUps.Entries);

            try
            {
                using (MySqlConnection connection = database.OpenConnection())
                    ExecuteNonQuery(connection, "ALTER TABLE item RENAME TO item_load_failure");

                Assert.False(manager.LoadPlayerData(account));
                Assert.Null(account.Player);
                Assert.Empty(account.Avatars.Entries);
                Assert.Empty(account.TeamUps.Entries);
                Assert.Empty(account.Items.Entries);
                Assert.Empty(account.ControlledEntities.Entries);
            }
            finally
            {
                using MySqlConnection connection = database.OpenConnection();
                ExecuteNonQuery(connection, "ALTER TABLE item_load_failure RENAME TO item");
            }
        }

        [MySQLFact]
        public void SavePlayerData_RoundTripsInitialAndUpdatedAggregate()
        {
            using MySQLTestDatabase database = new();
            MySQLDBManager manager = Initialize(database);
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

        [MySQLFact]
        public void SavePlayerData_DeletesStaleEntitiesAndReparentsEntity()
        {
            using MySQLTestDatabase database = new();
            MySQLDBManager manager = Initialize(database);
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

        [MySQLFact]
        public void SavePlayerData_RemovedParentsDeleteDescendantsAndRetainCurrentParentChildren()
        {
            using MySQLTestDatabase database = new();
            MySQLDBManager manager = Initialize(database);
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

            using MySqlConnection connection = database.OpenConnection();
            Assert.Equal(0, GetEntityCount(connection, "avatar", removedAvatar.DbGuid));
            Assert.Equal(0, GetEntityCount(connection, "team_up", removedTeamUp.DbGuid));
            Assert.Equal(0, GetContainerEntityCount(connection, "item", removedAvatar.DbGuid));
            Assert.Equal(0, GetContainerEntityCount(connection, "controlled_entity", removedAvatar.DbGuid));
            Assert.Equal(0, GetContainerEntityCount(connection, "item", removedTeamUp.DbGuid));
            Assert.Equal(1, GetContainerEntityCount(connection, "item", accountId + 1));
            Assert.Equal(1, GetContainerEntityCount(connection, "controlled_entity", accountId + 1));
            Assert.Equal(1, GetContainerEntityCount(connection, "item", accountId + 2));
        }

        [MySQLFact]
        public void SavePlayerData_ParentDeleteFailureRollsBackDescendantCleanup()
        {
            using MySQLTestDatabase database = new();
            MySQLDBManager manager = Initialize(database);
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

            try
            {
                using (MySqlConnection connection = database.OpenConnection())
                    ExecuteNonQuery(connection, @"CREATE TRIGGER fail_avatar_delete_trigger BEFORE DELETE ON avatar FOR EACH ROW
                        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'induced parent delete failure'");

                Assert.False(manager.SavePlayerData(CreateAggregate(accountId)));

                using MySqlConnection assertionConnection = database.OpenConnection();
                Assert.Equal(1, GetEntityCount(assertionConnection, "avatar", removedAvatar.DbGuid));
                Assert.Equal(1, GetEntityCount(assertionConnection, "team_up", removedTeamUp.DbGuid));
                Assert.Equal(1, GetContainerEntityCount(assertionConnection, "item", removedAvatar.DbGuid));
                Assert.Equal(1, GetContainerEntityCount(assertionConnection, "controlled_entity", removedAvatar.DbGuid));
                Assert.Equal(1, GetContainerEntityCount(assertionConnection, "item", removedTeamUp.DbGuid));
            }
            finally
            {
                using MySqlConnection connection = database.OpenConnection();
                ExecuteNonQuery(connection, "DROP TRIGGER IF EXISTS fail_avatar_delete_trigger");
            }
        }

        [MySQLFact]
        public void SavePlayerData_ReparentedChildrenOfRemovedParentsRemainUnderCurrentParents()
        {
            using MySQLTestDatabase database = new();
            MySQLDBManager manager = Initialize(database);
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

            using MySqlConnection connection = database.OpenConnection();
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

        [MySQLFact]
        public void SavePlayerData_NonTransientFailureRollsBackAggregateAndCleansUpTrigger()
        {
            using MySQLTestDatabase database = new();
            MySQLDBManager manager = Initialize(database);
            const long accountId = long.MinValue + 300;
            DBAccount original = CreateAggregate(accountId);
            Assert.True(manager.SavePlayerData(original));

            DBAccount changed = CreateAggregate(accountId);
            changed.Player.ArchiveData = new byte[] { 7, 7, 7 };
            changed.Player.Flags = 99;
            Assert.Single(changed.Avatars.Entries).ArchiveData = new byte[] { 8, 8, 8 };

            try
            {
                using (MySqlConnection connection = database.OpenConnection())
                    ExecuteNonQuery(connection, @"CREATE TRIGGER fail_controlled_entity_save_trigger BEFORE INSERT ON controlled_entity FOR EACH ROW
                        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'induced non-transient save failure'");

                Assert.False(manager.SavePlayerData(changed));
                AssertAggregate(original, LoadAggregate(manager, accountId));
            }
            finally
            {
                using MySqlConnection connection = database.OpenConnection();
                ExecuteNonQuery(connection, "DROP TRIGGER IF EXISTS fail_controlled_entity_save_trigger");
            }
        }

        [MySQLFact]
        public void SaveGuild_CreatesAndUpdatesOnlyMutableFields()
        {
            using MySQLTestDatabase database = new();
            MySQLDBManager manager = Initialize(database);
            DBGuild guild = new(8001, "Original Guild", "Original MOTD", 8002, 8003);

            Assert.True(manager.SaveGuild(guild));
            guild.Name = "Updated Guild";
            guild.Motd = "Updated MOTD";
            guild.CreatorDbGuid = 8004;
            guild.CreationTime = 8005;
            Assert.True(manager.SaveGuild(guild));

            using MySqlConnection connection = database.OpenConnection();
            using MySqlCommand command = new("SELECT name, motd, creator_db_guid, creation_time FROM guild WHERE id = @Id", connection);
            command.Parameters.AddWithValue("@Id", guild.Id);
            using MySqlDataReader reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("Updated Guild", reader.GetString(0));
            Assert.Equal("Updated MOTD", reader.GetString(1));
            Assert.Equal(8002, reader.GetInt64(2));
            Assert.Equal(8003, reader.GetInt64(3));
        }

        [MySQLFact]
        public void SaveGuild_DuplicateNameIgnoringCase_ReturnsFalse()
        {
            using MySQLTestDatabase database = new();
            MySQLDBManager manager = Initialize(database);

            Assert.True(manager.SaveGuild(new DBGuild(8101, "Duplicate Guild", "First", 8102, 8103)));
            Assert.False(manager.SaveGuild(new DBGuild(8104, "duplicate guild", "Second", 8105, 8106)));
        }

        [MySQLFact]
        public void SaveGuild_DuplicateNameInNonStrictMode_ReturnsFalseWithoutChangingExistingGuild()
        {
            using MySQLTestDatabase database = new();
            MySqlConnectionStringBuilder connectionStringBuilder = new(database.ConnectionString)
            {
                MaximumPoolSize = 1,
                ConnectionReset = false
            };
            string connectionString = connectionStringBuilder.ConnectionString;
            try
            {
                using (MySqlConnection connection = new(connectionString))
                {
                    connection.Open();
                    ExecuteNonQuery(connection, "SET SESSION sql_mode = ''");
                    Assert.Equal(string.Empty, Convert.ToString(ExecuteScalar(connection, "SELECT @@SESSION.sql_mode")));
                }

                MySQLDBManager manager = new(connectionString);
                Assert.True(manager.Initialize());
                using (MySqlConnection connection = new(connectionString))
                {
                    connection.Open();
                    ExecuteNonQuery(connection, "ALTER TABLE guild MODIFY name VARCHAR(320) COLLATE utf8mb4_unicode_ci NULL");
                }
                DBGuild existing = new(8111, "Non-Strict Guild", "Original MOTD", 8112, 8113);
                Assert.True(manager.SaveGuild(existing));

                Assert.False(manager.SaveGuild(new DBGuild(8114, "non-strict guild", "Changed MOTD", 8115, 8116)));

                using MySqlConnection assertionConnection = new(connectionString);
                assertionConnection.Open();
                using MySqlCommand command = new("SELECT name, motd, creator_db_guid, creation_time FROM guild WHERE id = @Id", assertionConnection);
                command.Parameters.AddWithValue("@Id", existing.Id);
                using MySqlDataReader reader = command.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal(existing.Name, reader.GetString(0));
                Assert.Equal(existing.Motd, reader.GetString(1));
                Assert.Equal(existing.CreatorDbGuid, reader.GetInt64(2));
                Assert.Equal(existing.CreationTime, reader.GetInt64(3));
            }
            finally
            {
                MySqlConnection.ClearPool(new MySqlConnection(connectionString));
            }
        }

        [MySQLFact]
        public void SaveGuild_RenameToAnotherGuildName_ReturnsFalseAndRollsBack()
        {
            using MySQLTestDatabase database = new();
            MySQLDBManager manager = Initialize(database);
            DBGuild first = new(8121, "First Guild", "First MOTD", 8122, 8123);
            DBGuild second = new(8124, "Second Guild", "Second MOTD", 8125, 8126);
            Assert.True(manager.SaveGuild(first));
            Assert.True(manager.SaveGuild(second));

            first.Name = second.Name;
            first.Motd = "Changed MOTD";
            Assert.False(manager.SaveGuild(first));

            using MySqlConnection connection = database.OpenConnection();
            using MySqlCommand command = new("SELECT id, name, motd, creator_db_guid, creation_time FROM guild ORDER BY id", connection);
            using MySqlDataReader reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(8121, reader.GetInt64(0));
            Assert.Equal("First Guild", reader.GetString(1));
            Assert.Equal("First MOTD", reader.GetString(2));
            Assert.Equal(8122, reader.GetInt64(3));
            Assert.Equal(8123, reader.GetInt64(4));
            Assert.True(reader.Read());
            Assert.Equal(second.Id, reader.GetInt64(0));
            Assert.Equal(second.Name, reader.GetString(1));
            Assert.Equal(second.Motd, reader.GetString(2));
            Assert.Equal(second.CreatorDbGuid, reader.GetInt64(3));
            Assert.Equal(second.CreationTime, reader.GetInt64(4));
            Assert.False(reader.Read());
        }

        [MySQLFact]
        public void SaveGuild_ConcurrentNewIdSerializesVerificationAndPreservesOriginalImmutableFields()
        {
            using MySQLTestDatabase database = new();
            MySQLDBManager firstManager = Initialize(database);
            MySQLDBManager secondManager = new(database.ConnectionString);
            DBGuild original = new(8151, "Concurrent Original", "Original MOTD", 8152, 8153);
            DBGuild updated = new(original.Id, "Concurrent Updated", "Updated MOTD", 8154, 8155);
            string lockPrefix = database.DatabaseName.Substring("mhserveremu_test_".Length);
            string firstStartedLockName = $"{lockPrefix}_guild_save_first_started";
            string secondStartedLockName = $"{lockPrefix}_guild_save_second_started";
            string firstReleaseLockName = $"{lockPrefix}_guild_save_first_release";
            string secondReleaseLockName = $"{lockPrefix}_guild_save_second_release";
            bool firstReleaseLockAcquired = false;
            bool secondReleaseLockAcquired = false;
            bool triggerCreated = false;
            Task<bool> firstSave = null;
            Task<bool> secondSave = null;

            MySqlConnection synchronizationConnection = database.OpenConnection();
            Exception primaryException = null;
            try
            {
                Assert.Equal(1, GetNamedLock(synchronizationConnection, firstReleaseLockName, 0));
                firstReleaseLockAcquired = true;
                Assert.Equal(1, GetNamedLock(synchronizationConnection, secondReleaseLockName, 0));
                secondReleaseLockAcquired = true;
                using (MySqlConnection connection = database.OpenConnection())
                {
                    ExecuteNonQuery(connection, $@"CREATE TRIGGER wait_for_concurrent_guild_save AFTER INSERT ON guild FOR EACH ROW
                        BEGIN
                            DECLARE guild_save_wait INT;
                            SET guild_save_wait = GET_LOCK('{firstStartedLockName}', 0);
                            SET guild_save_wait = GET_LOCK('{firstReleaseLockName}', 30);
                            SET guild_save_wait = RELEASE_LOCK('{firstReleaseLockName}');
                            SET guild_save_wait = RELEASE_LOCK('{firstStartedLockName}');
                        END");
                    triggerCreated = true;
                }

                firstSave = Task.Run(() => firstManager.SaveGuild(original));
                WaitForNamedLock(synchronizationConnection, firstStartedLockName);
                secondSave = Task.Run(() => secondManager.SaveGuild(updated));

                Assert.Equal(1, ReleaseNamedLock(synchronizationConnection, secondReleaseLockName));
                secondReleaseLockAcquired = false;
                Assert.True(SpinWait.SpinUntil(() => secondSave.Status != TaskStatus.WaitingToRun, TimeSpan.FromSeconds(30)));
                Assert.False(secondSave.IsCompleted);
                Assert.Equal(1, ReleaseNamedLock(synchronizationConnection, firstReleaseLockName));
                firstReleaseLockAcquired = false;
                Assert.True(firstSave.GetAwaiter().GetResult());
                Assert.True(secondSave.GetAwaiter().GetResult());

                using MySqlConnection assertionConnection = database.OpenConnection();
                using MySqlCommand command = new("SELECT name, motd, creator_db_guid, creation_time FROM guild WHERE id = @Id", assertionConnection);
                command.Parameters.AddWithValue("@Id", original.Id);
                using MySqlDataReader reader = command.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal(updated.Name, reader.GetString(0));
                Assert.Equal(updated.Motd, reader.GetString(1));
                Assert.Equal(original.CreatorDbGuid, reader.GetInt64(2));
                Assert.Equal(original.CreationTime, reader.GetInt64(3));
            }
            catch (Exception exception)
            {
                primaryException = exception;
                throw;
            }
            finally
            {
                Exception cleanupException = null;
                try
                {
                    try
                    {
                        if (firstReleaseLockAcquired)
                            ReleaseNamedLock(synchronizationConnection, firstReleaseLockName);
                    }
                    catch (Exception exception)
                    {
                        cleanupException ??= exception;
                    }
                    finally
                    {
                        try
                        {
                            if (secondReleaseLockAcquired)
                                ReleaseNamedLock(synchronizationConnection, secondReleaseLockName);
                        }
                        catch (Exception exception)
                        {
                            cleanupException ??= exception;
                        }
                        finally
                        {
                            try
                            {
                                if (firstSave != null)
                                    firstSave.GetAwaiter().GetResult();
                            }
                            catch (Exception exception)
                            {
                                cleanupException ??= exception;
                            }
                            finally
                            {
                                try
                                {
                                    if (secondSave != null)
                                        secondSave.GetAwaiter().GetResult();
                                }
                                catch (Exception exception)
                                {
                                    cleanupException ??= exception;
                                }
                                finally
                                {
                                    try
                                    {
                                        if (triggerCreated)
                                        {
                                            using MySqlConnection connection = database.OpenConnection();
                                            ExecuteNonQuery(connection, "DROP TRIGGER IF EXISTS wait_for_concurrent_guild_save");
                                        }
                                    }
                                    catch (Exception exception)
                                    {
                                        cleanupException ??= exception;
                                    }
                                    finally
                                    {
                                        try
                                        {
                                            synchronizationConnection.Dispose();
                                        }
                                        catch (Exception exception)
                                        {
                                            cleanupException ??= exception;
                                        }
                                        finally
                                        {
                                            try
                                            {
                                                MySqlConnection.ClearPool(new MySqlConnection(database.ConnectionString));
                                            }
                                            catch (Exception exception)
                                            {
                                                cleanupException ??= exception;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                finally
                {
                    if (primaryException == null && cleanupException != null)
                        throw cleanupException;
                }
            }
        }

        [MySQLFact]
        public void SaveGuildMember_CreatesAndUpdatesOnlyMembership()
        {
            using MySQLTestDatabase database = new();
            MySQLDBManager manager = Initialize(database);
            DBGuildMember member = new(8201, 8202, 1);

            Assert.True(manager.SaveGuildMember(member));
            member.GuildId = 8203;
            member.Membership = 2;
            Assert.True(manager.SaveGuildMember(member));

            using MySqlConnection connection = database.OpenConnection();
            using MySqlCommand command = new("SELECT guild_id, membership FROM guild_member WHERE player_db_guid = @PlayerDbGuid", connection);
            command.Parameters.AddWithValue("@PlayerDbGuid", member.PlayerDbGuid);
            using MySqlDataReader reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(8202, reader.GetInt64(0));
            Assert.Equal(2, reader.GetInt64(1));
        }

        [MySQLFact]
        public void LoadGuilds_AttachesKnownMembersAndIgnoresOrphans()
        {
            using MySQLTestDatabase database = new();
            MySQLDBManager manager = Initialize(database);
            DBGuild guild = new(8301, "Loaded Guild", "MOTD", 8302, 8303);
            DBGuildMember member = new(8304, guild.Id, 1);
            Assert.True(manager.SaveGuild(guild));
            Assert.True(manager.SaveGuildMember(member));

            using (MySqlConnection connection = database.OpenConnection())
            {
                using MySqlCommand command = new("INSERT INTO guild_member (player_db_guid, guild_id, membership) VALUES (@PlayerDbGuid, @GuildId, @Membership)", connection);
                command.Parameters.AddWithValue("@PlayerDbGuid", 8305L);
                command.Parameters.AddWithValue("@GuildId", 8399L);
                command.Parameters.AddWithValue("@Membership", 2L);
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

        [MySQLFact]
        public void LoadGuilds_PreservesUnsignedIdsStoredAsSignedBigInts()
        {
            using MySQLTestDatabase database = new();
            MySQLDBManager manager = Initialize(database);
            const ulong guildId = ulong.MaxValue - 100;
            const ulong creatorDbGuid = ulong.MaxValue - 101;
            const ulong playerDbGuid = ulong.MaxValue - 102;
            DBGuild guild = new(unchecked((long)guildId), "Unsigned Guild", "MOTD", unchecked((long)creatorDbGuid), 8303);
            DBGuildMember member = new(unchecked((long)playerDbGuid), guild.Id, 1);
            Assert.True(manager.SaveGuild(guild));
            Assert.True(manager.SaveGuildMember(member));

            List<DBGuild> guilds = new();
            Assert.True(manager.LoadGuilds(guilds));
            DBGuild loadedGuild = Assert.Single(guilds);
            DBGuildMember loadedMember = Assert.Single(loadedGuild.Members);
            Assert.Equal(guildId, unchecked((ulong)loadedGuild.Id));
            Assert.Equal(creatorDbGuid, unchecked((ulong)loadedGuild.CreatorDbGuid));
            Assert.Equal(playerDbGuid, unchecked((ulong)loadedMember.PlayerDbGuid));
        }

        [MySQLFact]
        public void DeleteGuildMember_RemovesMember()
        {
            using MySQLTestDatabase database = new();
            MySQLDBManager manager = Initialize(database);
            DBGuildMember member = new(8401, 8402, 1);
            Assert.True(manager.SaveGuildMember(member));

            Assert.True(manager.DeleteGuildMember(member));

            using MySqlConnection connection = database.OpenConnection();
            Assert.Equal(0, GetGuildMemberCount(connection, member.PlayerDbGuid));
        }

        [MySQLFact]
        public void DeleteGuild_DeletesMembersAndRollsBackBothDeletesWhenGuildDeleteFails()
        {
            using MySQLTestDatabase database = new();
            MySQLDBManager manager = Initialize(database);
            DBGuild guild = new(8501, "Deleted Guild", "MOTD", 8502, 8503);
            DBGuildMember member = new(8504, guild.Id, 1);
            Assert.True(manager.SaveGuild(guild));
            Assert.True(manager.SaveGuildMember(member));

            try
            {
                using (MySqlConnection connection = database.OpenConnection())
                    ExecuteNonQuery(connection, @"CREATE TRIGGER fail_guild_delete_trigger BEFORE DELETE ON guild FOR EACH ROW
                        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'induced guild delete failure'");

                Assert.False(manager.DeleteGuild(guild));

                using (MySqlConnection connection = database.OpenConnection())
                {
                    Assert.Equal(1, GetGuildCount(connection, guild.Id));
                    Assert.Equal(1, GetGuildMemberCount(connection, member.PlayerDbGuid));
                }
            }
            finally
            {
                using MySqlConnection connection = database.OpenConnection();
                ExecuteNonQuery(connection, "DROP TRIGGER IF EXISTS fail_guild_delete_trigger");
            }

            Assert.True(manager.DeleteGuild(guild));
            using MySqlConnection assertionConnection = database.OpenConnection();
            Assert.Equal(0, GetGuildCount(assertionConnection, guild.Id));
            Assert.Equal(0, GetGuildMemberCount(assertionConnection, member.PlayerDbGuid));
        }

        private static MySQLDBManager Initialize(MySQLTestDatabase database)
        {
            MySQLDBManager manager = new(database.ConnectionString);
            Assert.True(manager.Initialize());
            return manager;
        }

        private static int GetNamedLock(MySqlConnection connection, string lockName, int timeout)
        {
            return Convert.ToInt32(ExecuteScalar(connection, $"SELECT GET_LOCK('{lockName}', {timeout})"));
        }

        private static int ReleaseNamedLock(MySqlConnection connection, string lockName) => Convert.ToInt32(ExecuteScalar(connection, $"SELECT RELEASE_LOCK('{lockName}')"));

        private static void WaitForNamedLock(MySqlConnection connection, string lockName)
        {
            System.Diagnostics.Stopwatch timeout = System.Diagnostics.Stopwatch.StartNew();
            while (timeout.Elapsed < TimeSpan.FromSeconds(30))
            {
                object owner = ExecuteScalar(connection, $"SELECT IS_USED_LOCK('{lockName}')");
                if (owner != null && owner != DBNull.Value)
                    return;

                Thread.Yield();
            }

            throw new TimeoutException($"Timed out waiting for named lock {lockName}.");
        }

        private static int GetAccountCount(MySqlConnection connection) => Convert.ToInt32(ExecuteScalar(connection, "SELECT COUNT(*) FROM account"));
        private static int GetGuildCount(MySqlConnection connection, long guildId) => Convert.ToInt32(ExecuteScalar(connection, "SELECT COUNT(*) FROM guild WHERE id = @Id", "@Id", guildId));
        private static int GetGuildMemberCount(MySqlConnection connection, long playerDbGuid) => Convert.ToInt32(ExecuteScalar(connection, "SELECT COUNT(*) FROM guild_member WHERE player_db_guid = @PlayerDbGuid", "@PlayerDbGuid", playerDbGuid));
        private static int GetEntityCount(MySqlConnection connection, string tableName, long dbGuid) => Convert.ToInt32(ExecuteScalar(connection, $"SELECT COUNT(*) FROM {tableName} WHERE db_guid = @DbGuid", "@DbGuid", dbGuid));
        private static int GetContainerEntityCount(MySqlConnection connection, string tableName, long containerDbGuid) => Convert.ToInt32(ExecuteScalar(connection, $"SELECT COUNT(*) FROM {tableName} WHERE container_db_guid = @ContainerDbGuid", "@ContainerDbGuid", containerDbGuid));

        private static object ExecuteScalar(MySqlConnection connection, string commandText, string parameterName = null, long parameterValue = 0)
        {
            using MySqlCommand command = new(commandText, connection);
            if (parameterName != null)
                command.Parameters.AddWithValue(parameterName, parameterValue);
            return command.ExecuteScalar();
        }

        private static void ExecuteNonQuery(MySqlConnection connection, string commandText, long playerId)
        {
            using MySqlCommand command = new(commandText, connection);
            command.Parameters.AddWithValue("@PlayerId", playerId);
            command.ExecuteNonQuery();
        }

        private static void ExecuteNonQuery(MySqlConnection connection, string commandText)
        {
            using MySqlCommand command = new(commandText, connection);
            command.ExecuteNonQuery();
        }

        private static DBEntity CreateEntity(long dbGuid, long containerDbGuid, uint slot) => new()
        {
            DbGuid = dbGuid,
            ContainerDbGuid = containerDbGuid,
            InventoryProtoGuid = dbGuid + 100,
            Slot = slot,
            EntityProtoGuid = dbGuid + 200,
            ArchiveData = BitConverter.GetBytes(dbGuid)
        };

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

        private static DBAccount LoadAggregate(MySQLDBManager manager, long accountId)
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

        private static void InsertPlayer(MySqlConnection connection, long accountId)
        {
            using MySqlCommand command = new(@"INSERT INTO player (db_guid, archive_data, start_target, aoi_volume, gazillionite_balance, last_logout_time, flags)
                VALUES (@DbGuid, @ArchiveData, @StartTarget, @AOIVolume, @GazillioniteBalance, @LastLogoutTime, @Flags)", connection);
            command.Parameters.AddWithValue("@DbGuid", accountId);
            command.Parameters.AddWithValue("@ArchiveData", new byte[] { 1, 2, 3 });
            command.Parameters.AddWithValue("@StartTarget", long.MinValue + 20);
            command.Parameters.AddWithValue("@AOIVolume", 42);
            command.Parameters.AddWithValue("@GazillioniteBalance", long.MinValue + 21);
            command.Parameters.AddWithValue("@LastLogoutTime", long.MinValue + 22);
            command.Parameters.AddWithValue("@Flags", long.MinValue + 23);
            command.ExecuteNonQuery();
        }

        private static void InsertEntity(MySqlConnection connection, string tableName, DBEntity entity)
        {
            using MySqlCommand command = new($@"INSERT INTO {tableName} (db_guid, container_db_guid, inventory_proto_guid, slot, entity_proto_guid, archive_data)
                VALUES (@DbGuid, @ContainerDbGuid, @InventoryProtoGuid, @Slot, @EntityProtoGuid, @ArchiveData)", connection);
            command.Parameters.AddWithValue("@DbGuid", entity.DbGuid);
            command.Parameters.AddWithValue("@ContainerDbGuid", entity.ContainerDbGuid);
            command.Parameters.AddWithValue("@InventoryProtoGuid", entity.InventoryProtoGuid);
            command.Parameters.AddWithValue("@Slot", checked((long)entity.Slot));
            command.Parameters.AddWithValue("@EntityProtoGuid", entity.EntityProtoGuid);
            command.Parameters.AddWithValue("@ArchiveData", entity.ArchiveData);
            command.ExecuteNonQuery();
        }
    }
}

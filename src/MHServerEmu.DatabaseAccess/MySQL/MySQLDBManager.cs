using System.Data;
using Dapper;
using MHServerEmu.Core.Config;
using MHServerEmu.Core.Logging;
using MHServerEmu.DatabaseAccess.Models;
using MySqlConnector;

namespace MHServerEmu.DatabaseAccess.MySQL
{
    public class MySQLDBManager : IDBManager
    {
        private const int NumTestAccounts = 5;
        private const int NumPlayerDataWriteAttempts = 3;
        private const int ServerGoneError = 2006;
        private const int ServerLostError = 2013;

        private static readonly TimeSpan[] RetryDelays =
        {
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(150)
        };

        private static readonly Logger Logger = LogManager.CreateLogger();

        private MySQLConnectionInfo _connectionInfo;

        public static MySQLDBManager Instance { get; } = new();

        public bool VerifyAccounts { get => true; }

        private MySQLDBManager() { }

        internal MySQLDBManager(string connectionString)
        {
            if (MySQLConnectionInfo.TryParse(connectionString, out _connectionInfo, out string error) == false)
                throw new ArgumentException(error, nameof(connectionString));
        }

        public bool Initialize()
        {
            if (TryLoadConnectionInfo() == false)
                return false;

            try
            {
                using MySqlConnection connection = GetConnection();
                MySQLSchemaManager.EnsureCurrentSchema(connection, SeedTestAccounts);
                Logger.Info($"Using MySQL database {_connectionInfo.Description}");
                return true;
            }
            catch (Exception exception)
            {
                Logger.Error(CreateInitializationErrorLogMessage(exception));
                return false;
            }
        }

        public bool TryQueryAccountByEmail(string email, out DBAccount account)
        {
            account = null;
            try
            {
                using MySqlConnection connection = GetConnection();
                account = connection.QueryFirstOrDefault<DBAccount>($"{AccountProjection} WHERE email = @Email", new { Email = email });
                return account != null;
            }
            catch
            {
                Logger.Error(CreateErrorLogMessage(nameof(TryQueryAccountByEmail)));
                return false;
            }
        }

        public bool TryGetPlayerDbIdByName(string playerName, out ulong playerDbId, out string playerNameOut)
        {
            playerDbId = 0;
            playerNameOut = null;
            try
            {
                using MySqlConnection connection = GetConnection();
                DBAccount account = connection.QueryFirstOrDefault<DBAccount>(
                    "SELECT id AS Id, player_name AS PlayerName FROM account WHERE player_name = @PlayerName", new { PlayerName = playerName });
                if (account == null)
                    return false;

                playerDbId = unchecked((ulong)account.Id);
                playerNameOut = account.PlayerName;
                return true;
            }
            catch
            {
                Logger.Error(CreateErrorLogMessage(nameof(TryGetPlayerDbIdByName)));
                return false;
            }
        }

        public bool TryGetPlayerName(ulong playerDbId, out string playerName)
        {
            playerName = null;
            try
            {
                using MySqlConnection connection = GetConnection();
                playerName = connection.QueryFirstOrDefault<string>("SELECT player_name FROM account WHERE id = @Id", new { Id = unchecked((long)playerDbId) });
                return string.IsNullOrWhiteSpace(playerName) == false;
            }
            catch
            {
                Logger.Error(CreateErrorLogMessage(nameof(TryGetPlayerName), unchecked((long)playerDbId)));
                return false;
            }
        }

        public bool GetPlayerNames(Dictionary<ulong, string> playerNames)
        {
            try
            {
                using MySqlConnection connection = GetConnection();
                foreach (DBAccount account in connection.Query<DBAccount>("SELECT id AS Id, player_name AS PlayerName FROM account"))
                    playerNames[unchecked((ulong)account.Id)] = account.PlayerName;
                return playerNames.Count > 0;
            }
            catch
            {
                Logger.Error(CreateErrorLogMessage(nameof(GetPlayerNames)));
                return false;
            }
        }

        public bool TryGetLastLogoutTime(ulong playerDbId, out long lastLogoutTime)
        {
            lastLogoutTime = 0;
            try
            {
                using MySqlConnection connection = GetConnection();
                lastLogoutTime = connection.QueryFirstOrDefault<long>("SELECT last_logout_time FROM player WHERE db_guid = @DbGuid", new { DbGuid = unchecked((long)playerDbId) });
                return lastLogoutTime > 0;
            }
            catch
            {
                Logger.Error(CreateErrorLogMessage(nameof(TryGetLastLogoutTime), unchecked((long)playerDbId)));
                return false;
            }
        }

        public bool InsertAccount(DBAccount account)
        {
            try
            {
                using MySqlConnection connection = GetConnection();
                using MySqlTransaction transaction = connection.BeginTransaction();
                InsertAccountCore(connection, transaction, account);
                transaction.Commit();
                return true;
            }
            catch
            {
                Logger.Error(CreateErrorLogMessage(nameof(InsertAccount), account?.Id));
                return false;
            }
        }

        public bool UpdateAccount(DBAccount account)
        {
            try
            {
                using MySqlConnection connection = GetConnection();
                connection.Execute(@"UPDATE account SET email = @Email, player_name = @PlayerName, password_hash = @PasswordHash,
                    salt = @Salt, user_level = @UserLevel, flags = @Flags WHERE id = @Id", ToAccountParameters(account));
                return true;
            }
            catch
            {
                Logger.Error(CreateErrorLogMessage(nameof(UpdateAccount), account?.Id));
                return false;
            }
        }

        public bool LoadPlayerData(DBAccount account)
        {
            if (account == null)
            {
                Logger.Error(CreateErrorLogMessage(nameof(LoadPlayerData)));
                return false;
            }

            account.Player = null;
            account.ClearEntities();

            MySqlConnection connection = null;
            MySqlTransaction transaction = null;
            try
            {
                connection = GetConnection();
                transaction = connection.BeginTransaction(IsolationLevel.RepeatableRead);
                account.Player = connection.QueryFirstOrDefault<DBPlayer>(PlayerProjection, new { DbGuid = account.Id }, transaction);
                if (account.Player == null)
                {
                    account.Player = new(account.Id);
                    Logger.Info($"Initialized player data for account 0x{account.Id:X}");
                }

                MySQLEntityTable avatarTable = MySQLEntityTable.GetTable(DBEntityCategory.Avatar);
                MySQLEntityTable teamUpTable = MySQLEntityTable.GetTable(DBEntityCategory.TeamUp);
                MySQLEntityTable itemTable = MySQLEntityTable.GetTable(DBEntityCategory.Item);
                MySQLEntityTable controlledEntityTable = MySQLEntityTable.GetTable(DBEntityCategory.ControlledEntity);

                avatarTable.LoadEntities(connection, account.Id, account.Avatars, transaction);
                teamUpTable.LoadEntities(connection, account.Id, account.TeamUps, transaction);
                itemTable.LoadEntities(connection, account.Id, account.Items, transaction);
                foreach (DBEntity avatar in account.Avatars)
                {
                    itemTable.LoadEntities(connection, avatar.DbGuid, account.Items, transaction);
                    controlledEntityTable.LoadEntities(connection, avatar.DbGuid, account.ControlledEntities, transaction);
                }
                foreach (DBEntity teamUp in account.TeamUps)
                    itemTable.LoadEntities(connection, teamUp.DbGuid, account.Items, transaction);

                transaction.Commit();
                return true;
            }
            catch
            {
                TryRollback(transaction);
                account.Player = null;
                account.ClearEntities();
                Logger.Error(CreateErrorLogMessage(nameof(LoadPlayerData), account?.Id));
                return false;
            }
            finally
            {
                transaction?.Dispose();
                connection?.Dispose();
            }
        }

        public bool SavePlayerData(DBAccount account)
        {
            for (int attempt = 0; attempt < NumPlayerDataWriteAttempts; attempt++)
            {
                try
                {
                    SavePlayerDataAttempt(account);
                    return true;
                }
                catch (MySqlException exception)
                {
                    TimeSpan? retryDelay = GetRetryDelay(exception.IsTransient, attempt);
                    if (retryDelay.HasValue == false)
                    {
                        Logger.Error(CreateErrorLogMessage(nameof(SavePlayerData), account?.Id));
                        return false;
                    }

                    Logger.Error($"{CreateErrorLogMessage(nameof(SavePlayerData), account?.Id)} transient attempt {attempt + 1} of {NumPlayerDataWriteAttempts}");
                    Thread.Sleep(retryDelay.Value);
                }
                catch
                {
                    Logger.Error(CreateErrorLogMessage(nameof(SavePlayerData), account?.Id));
                    return false;
                }
            }

            return false;
        }

        public bool LoadGuilds(List<DBGuild> outGuilds)
        {
            try
            {
                using MySqlConnection connection = GetConnection();
                IEnumerable<DBGuild> guilds = connection.Query<DBGuild>(GuildProjection);
                IEnumerable<DBGuildMember> members = connection.Query<DBGuildMember>(GuildMemberProjection);

                outGuilds.AddRange(guilds);

                Dictionary<long, DBGuild> guildLookup = new(outGuilds.Count);
                foreach (DBGuild guild in outGuilds)
                    guildLookup.Add(guild.Id, guild);

                foreach (DBGuildMember member in members)
                {
                    if (guildLookup.TryGetValue(member.GuildId, out DBGuild guild) == false)
                    {
                        Logger.Warn($"LoadGuilds(): Found orphan member guildId={member.GuildId} playerDbGuid={member.PlayerDbGuid}");
                        continue;
                    }

                    guild.Members.Add(member);
                }

                return true;
            }
            catch
            {
                outGuilds.Clear();
                Logger.Error(CreateErrorLogMessage(nameof(LoadGuilds)));
                return false;
            }
        }

        public bool SaveGuild(DBGuild guild)
        {
            MySqlConnection connection = null;
            MySqlTransaction transaction = null;
            try
            {
                connection = GetConnection();
                transaction = connection.BeginTransaction();
                connection.Execute(@"INSERT INTO guild (id, name, motd, creator_db_guid, creation_time)
                    VALUES (@Id, @Name, @Motd, @CreatorDbGuid, @CreationTime)
                    ON DUPLICATE KEY UPDATE
                        name = IF(id = @Id, @Name, name),
                        motd = IF(id = @Id, @Motd, motd)", guild, transaction);

                long? savedGuildId = connection.QueryFirstOrDefault<long?>("SELECT id FROM guild WHERE id = @Id AND name = @Name AND motd = @Motd", guild, transaction);
                if (savedGuildId.HasValue == false)
                {
                    TryRollback(transaction);
                    return false;
                }

                transaction.Commit();
                Logger.Trace($"SaveGuild(): {guild}");
                return true;
            }
            catch
            {
                TryRollback(transaction);
                Logger.Error(CreateErrorLogMessage(nameof(SaveGuild)));
                return false;
            }
            finally
            {
                transaction?.Dispose();
                connection?.Dispose();
            }
        }

        public bool DeleteGuild(DBGuild guild)
        {
            MySqlConnection connection = null;
            MySqlTransaction transaction = null;
            try
            {
                connection = GetConnection();
                transaction = connection.BeginTransaction();
                connection.Execute("DELETE FROM guild_member WHERE guild_id = @Id", guild, transaction);
                connection.Execute("DELETE FROM guild WHERE id = @Id", guild, transaction);
                transaction.Commit();
                Logger.Trace($"DeleteGuild(): {guild}");
                return true;
            }
            catch
            {
                TryRollback(transaction);
                Logger.Error(CreateErrorLogMessage(nameof(DeleteGuild)));
                return false;
            }
            finally
            {
                transaction?.Dispose();
                connection?.Dispose();
            }
        }

        public bool SaveGuildMember(DBGuildMember guildMember)
        {
            try
            {
                using MySqlConnection connection = GetConnection();
                connection.Execute(@"INSERT INTO guild_member (player_db_guid, guild_id, membership)
                    VALUES (@PlayerDbGuid, @GuildId, @Membership)
                    ON DUPLICATE KEY UPDATE membership = @Membership", guildMember);
                Logger.Trace($"SaveGuildMember(): {guildMember}");
                return true;
            }
            catch
            {
                Logger.Error(CreateErrorLogMessage(nameof(SaveGuildMember)));
                return false;
            }
        }

        public bool DeleteGuildMember(DBGuildMember guildMember)
        {
            try
            {
                using MySqlConnection connection = GetConnection();
                connection.Execute("DELETE FROM guild_member WHERE player_db_guid = @PlayerDbGuid", guildMember);
                Logger.Trace($"DeleteGuildMember(): {guildMember}");
                return true;
            }
            catch
            {
                Logger.Error(CreateErrorLogMessage(nameof(DeleteGuildMember)));
                return false;
            }
        }

        private const string AccountProjection = @"SELECT id AS Id, email AS Email, player_name AS PlayerName,
            password_hash AS PasswordHash, salt AS Salt, user_level AS UserLevel, flags AS Flags FROM account";
        private const string PlayerProjection = @"SELECT db_guid AS DbGuid, archive_data AS ArchiveData,
            start_target AS StartTarget, aoi_volume AS AOIVolume, gazillionite_balance AS GazillioniteBalance,
            last_logout_time AS LastLogoutTime, flags AS Flags FROM player WHERE db_guid = @DbGuid";
        private const string GuildProjection = @"SELECT id AS Id, name AS Name, motd AS Motd,
            creator_db_guid AS CreatorDbGuid, creation_time AS CreationTime FROM guild";
        private const string GuildMemberProjection = @"SELECT player_db_guid AS PlayerDbGuid, guild_id AS GuildId,
            membership AS Membership FROM guild_member";

        private bool TryLoadConnectionInfo()
        {
            if (_connectionInfo != null)
                return true;

            string connectionString = ConfigManager.Instance.GetConfig<MySQLDBManagerConfig>().ConnectionString;
            if (MySQLConnectionInfo.TryParse(connectionString, out _connectionInfo, out _))
                return true;

            Logger.Error("Initialize(): MySQL configuration is invalid");
            return false;
        }

        internal string CreateErrorLogMessage(string operation, long? accountId = null)
        {
            string message = $"{operation}(): MySQL error for database {_connectionInfo?.Description ?? "unconfigured"}";
            return accountId.HasValue ? $"{message} accountId={accountId.Value}" : message;
        }

        internal string CreateInitializationErrorLogMessage(Exception exception)
        {
            return CreateInitializationErrorLogMessage(exception is MySqlException mySqlException ? mySqlException.Number : null);
        }

        internal string CreateInitializationErrorLogMessage(int? errorNumber)
        {
            string category = errorNumber switch
            {
                1042 or 2002 or 2003 or ServerGoneError or ServerLostError => "connection failure",
                1045 => "authentication failure",
                1062 => "schema uniqueness failure",
                null => "unexpected failure",
                _ => "schema or migration failure"
            };
            string message = $"{CreateErrorLogMessage(nameof(Initialize))}: {category}";
            return errorNumber.HasValue ? $"{message} (error {errorNumber.Value})" : message;
        }

        internal static TimeSpan? GetRetryDelay(bool isTransient, int attempt)
        {
            return isTransient && attempt >= 0 && attempt < RetryDelays.Length ? RetryDelays[attempt] : null;
        }

        private MySqlConnection GetConnection()
        {
            MySqlConnection connection = new(_connectionInfo.ConnectionString);
            connection.Open();
            return connection;
        }

        private void SavePlayerDataAttempt(DBAccount account)
        {
            if (account?.Player == null)
                throw new ArgumentNullException(nameof(account));

            MySqlConnection connection = null;
            MySqlTransaction transaction = null;
            try
            {
                connection = GetConnection();
                transaction = connection.BeginTransaction();
                connection.Execute(@"INSERT INTO player (db_guid, archive_data, start_target, aoi_volume, gazillionite_balance, last_logout_time, flags)
                    VALUES (@DbGuid, @ArchiveData, @StartTarget, @AOIVolume, @GazillioniteBalance, @LastLogoutTime, @Flags)
                    ON DUPLICATE KEY UPDATE archive_data = VALUES(archive_data), start_target = VALUES(start_target),
                    aoi_volume = VALUES(aoi_volume), gazillionite_balance = VALUES(gazillionite_balance),
                    last_logout_time = VALUES(last_logout_time), flags = VALUES(flags)", account.Player, transaction);

                MySQLEntityTable avatarTable = MySQLEntityTable.GetTable(DBEntityCategory.Avatar);
                MySQLEntityTable teamUpTable = MySQLEntityTable.GetTable(DBEntityCategory.TeamUp);
                MySQLEntityTable itemTable = MySQLEntityTable.GetTable(DBEntityCategory.Item);
                MySQLEntityTable controlledEntityTable = MySQLEntityTable.GetTable(DBEntityCategory.ControlledEntity);

                long[] removedAvatarIds = avatarTable.GetEntitiesToDelete(connection, transaction, account.Id, account.Avatars);
                long[] removedTeamUpIds = teamUpTable.GetEntitiesToDelete(connection, transaction, account.Id, account.TeamUps);
                itemTable.DeleteEntitiesForContainers(connection, transaction, removedAvatarIds);
                controlledEntityTable.DeleteEntitiesForContainers(connection, transaction, removedAvatarIds);
                itemTable.DeleteEntitiesForContainers(connection, transaction, removedTeamUpIds);
                avatarTable.DeleteEntities(connection, transaction, removedAvatarIds);
                teamUpTable.DeleteEntities(connection, transaction, removedTeamUpIds);

                avatarTable.UpdateEntities(connection, transaction, account.Id, account.Avatars);
                teamUpTable.UpdateEntities(connection, transaction, account.Id, account.TeamUps);
                itemTable.UpdateEntities(connection, transaction, account.Id, account.Items);
                foreach (DBEntity avatar in account.Avatars)
                {
                    itemTable.UpdateEntities(connection, transaction, avatar.DbGuid, account.Items);
                    controlledEntityTable.UpdateEntities(connection, transaction, avatar.DbGuid, account.ControlledEntities);
                }
                foreach (DBEntity teamUp in account.TeamUps)
                    itemTable.UpdateEntities(connection, transaction, teamUp.DbGuid, account.Items);

                transaction.Commit();
            }
            catch
            {
                TryRollback(transaction);
                throw;
            }
            finally
            {
                transaction?.Dispose();
                connection?.Dispose();
            }
        }

        private static void SeedTestAccounts(MySqlConnection connection, MySqlTransaction transaction)
        {
            for (int i = 0; i < NumTestAccounts; i++)
                InsertAccountCore(connection, transaction, new DBAccount($"test{i + 1}@test.com", $"Player{i + 1}", "123"));
        }

        private static void InsertAccountCore(MySqlConnection connection, MySqlTransaction transaction, DBAccount account)
        {
            connection.Execute(@"INSERT INTO account (id, email, player_name, password_hash, salt, user_level, flags)
                VALUES (@Id, @Email, @PlayerName, @PasswordHash, @Salt, @UserLevel, @Flags)", ToAccountParameters(account), transaction);
        }

        private static object ToAccountParameters(DBAccount account) => new
        {
            account.Id,
            account.Email,
            account.PlayerName,
            account.PasswordHash,
            account.Salt,
            UserLevel = (int)account.UserLevel,
            Flags = (int)account.Flags
        };

        private static void TryRollback(MySqlTransaction transaction)
        {
            try
            {
                transaction?.Rollback();
            }
            catch
            {
            }
        }
    }
}

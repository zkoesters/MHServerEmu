using Dapper;
using MHServerEmu.Core.Config;
using MHServerEmu.Core.Helpers;
using MHServerEmu.Core.Logging;
using MHServerEmu.DatabaseAccess.Models;
using Npgsql;

namespace MHServerEmu.DatabaseAccess.PostgreSQL
{
    public class PostgreSQLDBManager : IDBManager
    {
        private const int NumTestAccounts = 5;
        private const int NumPlayerDataWriteAttempts = 3;

        private static readonly TimeSpan[] RetryDelays =
        {
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(150)
        };

        private static readonly Logger Logger = LogManager.CreateLogger();

        private PostgreSQLConnectionInfo _connectionInfo;

        public static PostgreSQLDBManager Instance { get; } = new();

        public bool VerifyAccounts { get => true; }

        private PostgreSQLDBManager() { }

        internal PostgreSQLDBManager(string connectionString)
        {
            if (PostgreSQLConnectionInfo.TryParse(connectionString, out _connectionInfo, out string error) == false)
                throw new ArgumentException(error, nameof(connectionString));
        }

        public bool Initialize()
        {
            if (TryLoadConnectionInfo() == false)
                return false;

            try
            {
                using NpgsqlConnection connection = GetConnection();
                PostgreSQLSchemaManager.EnsureCurrentSchema(connection, SeedTestAccounts);
                Logger.Info($"Using PostgreSQL database {_connectionInfo.Description}");
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
                using NpgsqlConnection connection = GetConnection();
                account = connection.QueryFirstOrDefault<DBAccount>($"{AccountProjection} WHERE lower(email) = lower(@Email)", new { Email = email });
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
                using NpgsqlConnection connection = GetConnection();
                DBAccount account = connection.QueryFirstOrDefault<DBAccount>(
                    "SELECT id AS Id, player_name AS PlayerName FROM account WHERE lower(player_name) = lower(@PlayerName)",
                    new { PlayerName = playerName });
                if (account == null)
                    return false;

                playerDbId = (ulong)account.Id;
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
                using NpgsqlConnection connection = GetConnection();
                playerName = connection.QueryFirstOrDefault<string>("SELECT player_name FROM account WHERE id = @Id", new { Id = (long)playerDbId });
                return string.IsNullOrWhiteSpace(playerName) == false;
            }
            catch
            {
                Logger.Error(CreateErrorLogMessage(nameof(TryGetPlayerName), (long)playerDbId));
                return false;
            }
        }

        public bool GetPlayerNames(Dictionary<ulong, string> playerNames)
        {
            try
            {
                using NpgsqlConnection connection = GetConnection();
                IEnumerable<DBAccount> accounts = connection.Query<DBAccount>("SELECT id AS Id, player_name AS PlayerName FROM account");

                foreach (DBAccount account in accounts)
                    playerNames[(ulong)account.Id] = account.PlayerName;

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
                using NpgsqlConnection connection = GetConnection();
                lastLogoutTime = connection.QueryFirstOrDefault<long>("SELECT last_logout_time FROM player WHERE db_guid = @DbGuid", new { DbGuid = (long)playerDbId });
                return lastLogoutTime > 0;
            }
            catch
            {
                Logger.Error(CreateErrorLogMessage(nameof(TryGetLastLogoutTime), (long)playerDbId));
                return false;
            }
        }

        public bool LoadPlayerData(DBAccount account)
        {
            account.Player = null;
            account.ClearEntities();

            NpgsqlConnection connection = null;
            NpgsqlTransaction transaction = null;
            try
            {
                connection = GetConnection();
                transaction = connection.BeginTransaction(System.Data.IsolationLevel.RepeatableRead);
                account.Player = connection.QueryFirstOrDefault<DBPlayer>(@"SELECT db_guid AS DbGuid, archive_data AS ArchiveData,
                    start_target AS StartTarget, aoi_volume AS AOIVolume, gazillionite_balance AS GazillioniteBalance,
                    last_logout_time AS LastLogoutTime, flags AS Flags FROM player WHERE db_guid = @DbGuid", new { DbGuid = account.Id }, transaction);
                if (account.Player == null)
                {
                    account.Player = new(account.Id);
                    Logger.Info($"Initialized player data for account 0x{account.Id:X}");
                }

                PostgreSQLEntityTable avatarTable = PostgreSQLEntityTable.GetTable(DBEntityCategory.Avatar);
                PostgreSQLEntityTable teamUpTable = PostgreSQLEntityTable.GetTable(DBEntityCategory.TeamUp);
                PostgreSQLEntityTable itemTable = PostgreSQLEntityTable.GetTable(DBEntityCategory.Item);
                PostgreSQLEntityTable controlledEntityTable = PostgreSQLEntityTable.GetTable(DBEntityCategory.ControlledEntity);

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
                try
                {
                    transaction?.Rollback();
                }
                catch { }

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
                catch (NpgsqlException exception)
                {
                    if (exception.IsTransient == false)
                    {
                        Logger.Error(CreateErrorLogMessage(nameof(SavePlayerData), account?.Id));
                        return false;
                    }

                    TimeSpan? retryDelay = GetRetryDelay(exception, attempt);
                    if (retryDelay.HasValue == false)
                        break;

                    Logger.Error($"{CreateErrorLogMessage(nameof(SavePlayerData), account?.Id)} transient attempt {attempt + 1} of {NumPlayerDataWriteAttempts}");
                    Thread.Sleep(retryDelay.Value);
                }
                catch
                {
                    Logger.Error(CreateErrorLogMessage(nameof(SavePlayerData), account?.Id));
                    return false;
                }
            }

            Verify.IsTrue(false, $"Failed to write player data for account [{account}]");
            return false;
        }

        public bool LoadGuilds(List<DBGuild> outGuilds)
        {
            try
            {
                using NpgsqlConnection connection = GetConnection();
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
            try
            {
                using NpgsqlConnection connection = GetConnection();
                connection.Execute(@"INSERT INTO guild (id, name, motd, creator_db_guid, creation_time)
                    VALUES (@Id, @Name, @Motd, @CreatorDbGuid, @CreationTime)
                    ON CONFLICT (id) DO UPDATE SET name = EXCLUDED.name, motd = EXCLUDED.motd", guild);
                Logger.Trace($"SaveGuild(): {guild}");
                return true;
            }
            catch
            {
                Logger.Error(CreateErrorLogMessage(nameof(SaveGuild)));
                return false;
            }
        }

        public bool DeleteGuild(DBGuild guild)
        {
            NpgsqlConnection connection = null;
            NpgsqlTransaction transaction = null;
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
                try
                {
                    transaction?.Rollback();
                }
                catch { }

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
                using NpgsqlConnection connection = GetConnection();
                connection.Execute(@"INSERT INTO guild_member (player_db_guid, guild_id, membership)
                    VALUES (@PlayerDbGuid, @GuildId, @Membership)
                    ON CONFLICT (player_db_guid) DO UPDATE SET membership = EXCLUDED.membership", guildMember);
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
                using NpgsqlConnection connection = GetConnection();
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

        public bool InsertAccount(DBAccount account)
        {
            try
            {
                using NpgsqlConnection connection = GetConnection();
                using NpgsqlTransaction transaction = connection.BeginTransaction();
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
                using NpgsqlConnection connection = GetConnection();
                connection.Execute(@"UPDATE account SET email = @Email, player_name = @PlayerName, password_hash = @PasswordHash,
                    salt = @Salt, user_level = @UserLevel, flags = @Flags WHERE id = @Id",
                    new
                    {
                        account.Id,
                        account.Email,
                        account.PlayerName,
                        account.PasswordHash,
                        account.Salt,
                        UserLevel = (int)account.UserLevel,
                        Flags = (int)account.Flags
                    });
                return true;
            }
            catch
            {
                Logger.Error(CreateErrorLogMessage(nameof(UpdateAccount), account?.Id));
                return false;
            }
        }

        private const string AccountProjection = @"SELECT id AS Id, email AS Email, player_name AS PlayerName,
            password_hash AS PasswordHash, salt AS Salt, user_level AS UserLevel, flags AS Flags FROM account";

        private const string GuildProjection = @"SELECT id AS Id, name AS Name, motd AS Motd,
            creator_db_guid AS CreatorDbGuid, creation_time AS CreationTime FROM guild";

        private const string GuildMemberProjection = @"SELECT player_db_guid AS PlayerDbGuid, guild_id AS GuildId,
            membership AS Membership FROM guild_member";

        private bool TryLoadConnectionInfo()
        {
            if (_connectionInfo != null)
                return true;

            string connectionString = ConfigManager.Instance.GetConfig<PostgreSQLDBManagerConfig>().ConnectionString;
            if (PostgreSQLConnectionInfo.TryParse(connectionString, out _connectionInfo, out _))
                return true;

            Logger.Error("Initialize(): PostgreSQL configuration is invalid");
            return false;
        }

        internal string CreateErrorLogMessage(string operation, long? accountId = null)
        {
            string message = $"{operation}(): PostgreSQL error for database {_connectionInfo?.Description ?? "unconfigured"}";
            return accountId.HasValue ? $"{message} accountId={accountId.Value}" : message;
        }

        internal string CreateInitializationErrorLogMessage(Exception exception)
        {
            string category;
            string sqlState = null;

            if (exception is PostgresException postgresException)
            {
                sqlState = postgresException.SqlState;
                if (sqlState.StartsWith("08", StringComparison.Ordinal))
                    category = "connection failure";
                else if (sqlState.StartsWith("28", StringComparison.Ordinal))
                    category = "authentication failure";
                else if (sqlState == PostgresErrorCodes.UniqueViolation)
                    category = "schema uniqueness failure";
                else
                    category = "schema or migration failure";
            }
            else if (exception is NpgsqlException)
            {
                category = "connection failure";
            }
            else if (exception is InvalidOperationException)
            {
                category = "schema-version failure";
            }
            else if (exception is ArgumentException)
            {
                category = "configuration failure";
            }
            else
            {
                category = "unexpected failure";
            }

            string message = $"{CreateErrorLogMessage(nameof(Initialize))}: {category}";
            return sqlState == null ? message : $"{message} (SQLSTATE {sqlState})";
        }

        internal static TimeSpan? GetRetryDelay(NpgsqlException exception, int attempt)
        {
            return exception.IsTransient && attempt >= 0 && attempt < RetryDelays.Length ? RetryDelays[attempt] : null;
        }

        private NpgsqlConnection GetConnection()
        {
            NpgsqlConnection connection = new(_connectionInfo.ConnectionString);
            connection.Open();
            return connection;
        }

        private void SavePlayerDataAttempt(DBAccount account)
        {
            if (account?.Player == null)
                throw new ArgumentNullException(nameof(account));

            NpgsqlConnection connection = null;
            NpgsqlTransaction transaction = null;
            try
            {
                connection = GetConnection();
                transaction = connection.BeginTransaction();
                connection.Execute(@"INSERT INTO player (db_guid, archive_data, start_target, aoi_volume, gazillionite_balance, last_logout_time, flags)
                    VALUES (@DbGuid, @ArchiveData, @StartTarget, @AOIVolume, @GazillioniteBalance, @LastLogoutTime, @Flags)
                    ON CONFLICT (db_guid) DO UPDATE SET archive_data = EXCLUDED.archive_data, start_target = EXCLUDED.start_target,
                    aoi_volume = EXCLUDED.aoi_volume, gazillionite_balance = EXCLUDED.gazillionite_balance,
                    last_logout_time = EXCLUDED.last_logout_time, flags = EXCLUDED.flags", account.Player, transaction);

                PostgreSQLEntityTable avatarTable = PostgreSQLEntityTable.GetTable(DBEntityCategory.Avatar);
                PostgreSQLEntityTable teamUpTable = PostgreSQLEntityTable.GetTable(DBEntityCategory.TeamUp);
                PostgreSQLEntityTable itemTable = PostgreSQLEntityTable.GetTable(DBEntityCategory.Item);
                PostgreSQLEntityTable controlledEntityTable = PostgreSQLEntityTable.GetTable(DBEntityCategory.ControlledEntity);

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
                try
                {
                    transaction?.Rollback();
                }
                catch { }

                throw;
            }
            finally
            {
                transaction?.Dispose();
                connection?.Dispose();
            }
        }

        private static void SeedTestAccounts(NpgsqlConnection connection, NpgsqlTransaction transaction)
        {
            for (int i = 0; i < NumTestAccounts; i++)
            {
                DBAccount account = new($"test{i + 1}@test.com", $"Player{i + 1}", "123");
                InsertAccountCore(connection, transaction, account);
            }
        }

        private static void InsertAccountCore(NpgsqlConnection connection, NpgsqlTransaction transaction, DBAccount account)
        {
            connection.Execute(@"INSERT INTO account (id, email, player_name, password_hash, salt, user_level, flags)
                VALUES (@Id, @Email, @PlayerName, @PasswordHash, @Salt, @UserLevel, @Flags)",
                new
                {
                    account.Id,
                    account.Email,
                    account.PlayerName,
                    account.PasswordHash,
                    account.Salt,
                    UserLevel = (int)account.UserLevel,
                    Flags = (int)account.Flags
                },
                transaction);
        }
    }
}

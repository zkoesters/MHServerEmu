using Dapper;
using System.Data.SQLite;
using MHServerEmu.Core.Config;
using MHServerEmu.Core.Helpers;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.System.Time;
using MHServerEmu.DatabaseAccess.Models;

namespace MHServerEmu.DatabaseAccess.SQLite
{
    /// <summary>
    /// Provides functionality for storing <see cref="DBAccount"/> instances in a SQLite database.
    /// </summary>
    public class SQLiteDBManager : IAccountStore, IPlayerStore, IGuildStore
    {
        private const int CurrentSchemaVersion = 6;         // Increment this when making changes to the database schema
        private const int MinimumSchemaVersion = 6;         // Used to ignore legacy 0.x database files.
        private const int NumPlayerDataWriteAttempts = 3;   // Number of write attempts to do when saving player data

        private static readonly Logger Logger = LogManager.CreateLogger();

        private readonly object _writeLock = new();

        private string _dbFilePath;
        private string _connectionString;

        private int _maxBackupNumber;
        private CooldownTimer _backupTimer;
        private volatile bool _backupInProgress;

        public static SQLiteDBManager Instance { get; } = new();

        internal SQLiteDBManager() { }

        public bool Initialize()
        {
            var config = ConfigManager.Instance.GetConfig<SQLiteDBManagerConfig>();

            string dbFilePath = Path.Combine(FileHelper.DataDirectory, config.FileName);
            return Initialize(dbFilePath, config.MaxBackupNumber, TimeSpan.FromMinutes(config.BackupIntervalMinutes));
        }

        internal bool Initialize(string dbFilePath, int maxBackupNumber, TimeSpan backupInterval)
        {
            _dbFilePath = dbFilePath;
            _connectionString = $"Data Source={_dbFilePath};Synchronous=NORMAL;foreign_keys=OFF;";

            // TODO: Foreign key constraints are explicitly disabled for now because our Item table references
            // multiple parent tables (Player / Avatar / TeamUp) at the same time. Need to find an elegant way to fix that.

            if (File.Exists(_dbFilePath) == false)
            {
                // Create a new database file if it does not exist
                if (InitializeDatabaseFile() == false)
                    return false;
            }
            else
            {
                // Migrate existing database if needed
                if (MigrateDatabaseFileToCurrentSchema() == false)
                    return false;
            }

            _maxBackupNumber = maxBackupNumber;
            _backupTimer = new(backupInterval);
            
            Logger.Info($"Using database file {FileHelper.GetRelativePath(_dbFilePath)}");
            return true;
        }

        public bool TryQueryAccountByEmail(string email, out DBAccount account)
        {
            using SQLiteConnection connection = GetConnection();

            // This is just the base account entry, associated player data is loaded separately
            account = connection.QueryFirstOrDefault<DBAccount>("SELECT * FROM Account WHERE Email = @Email COLLATE NOCASE", new { Email = email });

            return account != null;
        }

        public bool TryGetPlayerDbIdByName(string playerName, out ulong playerDbId, out string playerNameOut)
        {
            using SQLiteConnection connection = GetConnection();

            // This check is case insensitive (COLLATE NOCASE)
            var account = connection.QueryFirstOrDefault<DBAccount>(
                "SELECT Id, PlayerName FROM Account WHERE PlayerName = @PlayerName COLLATE NOCASE",
                new { PlayerName = playerName });

            if (account == null)
            {
                playerDbId = 0;
                playerNameOut = null;
                return false;
            }

            playerDbId = (ulong)account.Id;
            playerNameOut = account.PlayerName;
            return true;
        }

        public bool TryGetPlayerName(ulong playerDbId, out string playerName)
        {
            using SQLiteConnection connection = GetConnection();
            
            playerName = connection.QueryFirstOrDefault<string>("SELECT PlayerName FROM Account WHERE Id = @Id", new { Id = (long)playerDbId });

            return string.IsNullOrWhiteSpace(playerName) == false;
        }

        public bool GetPlayerNames(Dictionary<ulong, string> playerNames)
        {
            using SQLiteConnection connection = GetConnection();
            
            var accounts = connection.Query<DBAccount>("SELECT Id, PlayerName FROM Account");

            foreach (DBAccount account in accounts)
                playerNames[(ulong)account.Id] = account.PlayerName;

            return playerNames.Count > 0;
        }

        public bool TryGetLastLogoutTime(ulong playerDbId, out long lastLogoutTime)
        {
            using SQLiteConnection connection = GetConnection();

            lastLogoutTime = connection.QueryFirstOrDefault<long>("SELECT LastLogoutTime FROM Player WHERE DbGuid = @DbGuid", new { DbGuid = (long)playerDbId });

            return lastLogoutTime > 0;
        }

        public AccountStoreResult InsertAccount(DBAccount account)
        {
            lock (_writeLock)
            {
                using SQLiteConnection connection = GetConnection();

                try
                {
                    connection.Execute(@"INSERT INTO Account (Id, Email, PlayerName, PasswordHash, Salt, UserLevel, Flags)
                        VALUES (@Id, @Email, @PlayerName, @PasswordHash, @Salt, @UserLevel, @Flags)", account);
                    SetPersistenceMetadata(account);
                    return AccountStoreResult.Success;
                }
                catch (Exception e)
                {
                    Logger.ErrorException(e, nameof(InsertAccount));

                    if (connection.QueryFirstOrDefault<long?>("SELECT Id FROM Account WHERE Email = @Email COLLATE NOCASE", new { account.Email }).HasValue)
                        return AccountStoreResult.EmailConflict;

                    if (connection.QueryFirstOrDefault<long?>("SELECT Id FROM Account WHERE PlayerName = @PlayerName COLLATE NOCASE", new { account.PlayerName }).HasValue)
                        return AccountStoreResult.PlayerNameConflict;

                    return AccountStoreResult.Failed;
                }
            }
        }

        public AccountStoreResult ChangePlayerName(DBAccount account, string playerName)
        {
            lock (_writeLock)
            {
                try
                {
                    using SQLiteConnection connection = GetConnection();
                    int updated = connection.Execute("UPDATE Account SET PlayerName=@PlayerName WHERE Id=@Id", new { PlayerName = playerName, account.Id });
                    if (updated != 1)
                        return AccountStoreResult.AccountNotFound;

                    SetPersistenceMetadata(account);
                    return AccountStoreResult.Success;
                }
                catch (Exception e)
                {
                    Logger.ErrorException(e, nameof(ChangePlayerName));

                    using SQLiteConnection connection = GetConnection();
                    if (connection.QueryFirstOrDefault<long?>("SELECT Id FROM Account WHERE PlayerName = @PlayerName COLLATE NOCASE", new { PlayerName = playerName }).HasValue)
                        return AccountStoreResult.PlayerNameConflict;

                    return AccountStoreResult.Failed;
                }
            }
        }

        public AccountStoreResult ChangePassword(DBAccount account, byte[] passwordHash, byte[] salt)
        {
            lock (_writeLock)
            {
                try
                {
                    using SQLiteConnection connection = GetConnection();
                    AccountFlags flags = account.Flags & ~AccountFlags.IsPasswordExpired;
                    int updated = connection.Execute("UPDATE Account SET PasswordHash=@PasswordHash, Salt=@Salt, Flags=@Flags WHERE Id=@Id",
                        new { PasswordHash = passwordHash, Salt = salt, Flags = flags, account.Id });
                    if (updated != 1)
                        return AccountStoreResult.AccountNotFound;

                    account.Flags = flags;
                    SetPersistenceMetadata(account);
                    return AccountStoreResult.Success;
                }
                catch (Exception e)
                {
                    Logger.ErrorException(e, nameof(ChangePassword));
                    return AccountStoreResult.Failed;
                }
            }
        }

        public AccountStoreResult ChangeUserLevel(DBAccount account, AccountUserLevel userLevel)
        {
            lock (_writeLock)
            {
                try
                {
                    using SQLiteConnection connection = GetConnection();
                    int updated = connection.Execute("UPDATE Account SET UserLevel=@UserLevel WHERE Id=@Id", new { UserLevel = userLevel, account.Id });
                    if (updated != 1)
                        return AccountStoreResult.AccountNotFound;

                    SetPersistenceMetadata(account);
                    return AccountStoreResult.Success;
                }
                catch (Exception e)
                {
                    Logger.ErrorException(e, nameof(ChangeUserLevel));
                    return AccountStoreResult.Failed;
                }
            }
        }

        public AccountStoreResult ChangeFlags(DBAccount account, AccountFlags flags)
        {
            lock (_writeLock)
            {
                try
                {
                    using SQLiteConnection connection = GetConnection();
                    int updated = connection.Execute("UPDATE Account SET Flags=@Flags WHERE Id=@Id", new { Flags = flags, account.Id });
                    if (updated != 1)
                        return AccountStoreResult.AccountNotFound;

                    SetPersistenceMetadata(account);
                    return AccountStoreResult.Success;
                }
                catch (Exception e)
                {
                    Logger.ErrorException(e, nameof(ChangeFlags));
                    return AccountStoreResult.Failed;
                }
            }
        }

        public PlayerStoreResult LoadPlayerData(DBAccount account)
        {
            // Clear existing data
            account.Player = null;
            account.ClearEntities();

            // Load fresh data
            using SQLiteConnection connection = GetConnection();

            account.Player = connection.QueryFirstOrDefault<DBPlayer>("SELECT * FROM Player WHERE DbGuid = @DbGuid", new { DbGuid = account.Id });
            if (account.Player == null)
            {
                account.Player = new(account.Id);
                Logger.Info($"Initialized player data for account 0x{account.Id:X}");
            }

            // Load inventory entities
            SQLiteEntityTable avatarTable = SQLiteEntityTable.GetTable(DBEntityCategory.Avatar);
            SQLiteEntityTable teamUpTable = SQLiteEntityTable.GetTable(DBEntityCategory.TeamUp);
            SQLiteEntityTable itemTable = SQLiteEntityTable.GetTable(DBEntityCategory.Item);
            SQLiteEntityTable controlledEntityTable = SQLiteEntityTable.GetTable(DBEntityCategory.ControlledEntity);

            avatarTable.LoadEntities(connection, account.Id, account.Avatars);
            teamUpTable.LoadEntities(connection, account.Id, account.TeamUps);
            itemTable.LoadEntities(connection, account.Id, account.Items);

            foreach (DBEntity avatar in account.Avatars)
            {
                itemTable.LoadEntities(connection, avatar.DbGuid, account.Items);
                controlledEntityTable.LoadEntities(connection, avatar.DbGuid, account.ControlledEntities);
            }

            foreach (DBEntity teamUp in account.TeamUps)
            {
                itemTable.LoadEntities(connection, teamUp.DbGuid, account.Items);
            }

            return PlayerStoreResult.Success;
        }

        public PlayerStoreResult SavePlayerData(DBAccount account)
        {
            for (int i = 0; i < NumPlayerDataWriteAttempts; i++)
            {
                if (DoSavePlayerData(account))
                    return PlayerStoreResult.Success;

                // Maybe we should add a delay here
            }

            return Logger.WarnReturn(PlayerStoreResult.Failed, $"SavePlayerData(): Failed to write player data for account [{account}]");
        }

        public bool LoadGuilds(List<DBGuild> outGuilds)
        {
            try
            {
                using SQLiteConnection connection = GetConnection();

                IEnumerable<DBGuild> guildQueryResult = connection.Query<DBGuild>("SELECT * FROM Guild");
                IEnumerable<DBGuildMember> memberQueryResult = connection.Query<DBGuildMember>("SELECT * FROM GuildMember");

                outGuilds.AddRange(guildQueryResult);

                // This is going to be called only on server startup, so it's fine not to pool this.
                Dictionary<long, DBGuild> guildLookup = new(outGuilds.Count);
                foreach (DBGuild guild in outGuilds)
                    guildLookup.Add(guild.Id, guild);

                foreach (DBGuildMember member in memberQueryResult)
                {
                    if (guildLookup.TryGetValue(member.GuildId, out DBGuild guild) == false)
                    {
                        Logger.Warn($"LoadGuilds(): Found orphan member [{member}]");
                        continue;
                    }

                    guild.Members.Add(member);
                }

                return true;
            }
            catch (Exception e)
            {
                outGuilds.Clear();
                Logger.ErrorException(e, nameof(LoadGuilds));
                return false;
            }
        }

        public GuildStoreResult CreateGuild(DBGuild guild, DBGuildMember creator)
        {
            lock (_writeLock)
            {
                if (creator.GuildId != guild.Id || creator.Membership != 3)
                    return GuildStoreResult.InvalidData;

                using SQLiteConnection connection = GetConnection();
                try
                {
                    using SQLiteTransaction transaction = connection.BeginTransaction();

                    if (connection.QueryFirstOrDefault<long?>("SELECT GuildId FROM GuildMember WHERE PlayerDbGuid=@PlayerDbGuid", new { creator.PlayerDbGuid }, transaction).HasValue)
                    {
                        transaction.Rollback();
                        return GuildStoreResult.MembershipConflict;
                    }

                    connection.Execute("INSERT INTO Guild (Id, Name, Motd, CreatorDbGuid, CreationTime) VALUES (@Id, @Name, @Motd, @CreatorDbGuid, @CreationTime)", guild, transaction);
                    connection.Execute("INSERT INTO GuildMember (PlayerDbGuid, GuildId, Membership) VALUES (@PlayerDbGuid, @GuildId, @Membership)", creator, transaction);
                    transaction.Commit();
                    SetPersistenceMetadata(guild);
                    Logger.Trace($"CreateGuild(): {guild}");
                    return GuildStoreResult.Success;
                }
                catch (Exception e)
                {
                    Logger.ErrorException(e, nameof(CreateGuild));

                    if (connection.QueryFirstOrDefault<long?>("SELECT Id FROM Guild WHERE Name=@Name COLLATE NOCASE", new { guild.Name }).HasValue)
                        return GuildStoreResult.NameConflict;

                    return GuildStoreResult.Failed;
                }
            }
        }

        public GuildStoreResult ChangeGuildName(DBGuild guild, string name)
        {
            try
            {
                using SQLiteConnection connection = GetConnection();
                int updated = connection.Execute("UPDATE Guild SET Name=@Name WHERE Id=@Id", new { Name = name, guild.Id });
                if (updated != 1)
                    return GuildStoreResult.GuildNotFound;

                SetPersistenceMetadata(guild);
                return GuildStoreResult.Success;
            }
            catch (Exception e)
            {
                Logger.ErrorException(e, nameof(ChangeGuildName));

                using SQLiteConnection connection = GetConnection();
                if (connection.QueryFirstOrDefault<long?>("SELECT Id FROM Guild WHERE Name=@Name COLLATE NOCASE", new { Name = name }).HasValue)
                    return GuildStoreResult.NameConflict;

                return GuildStoreResult.Failed;
            }
        }

        public GuildStoreResult ChangeGuildMotd(DBGuild guild, string motd)
        {
            try
            {
                using SQLiteConnection connection = GetConnection();
                int updated = connection.Execute("UPDATE Guild SET Motd=@Motd WHERE Id=@Id", new { Motd = motd, guild.Id });
                if (updated != 1)
                    return GuildStoreResult.GuildNotFound;

                SetPersistenceMetadata(guild);
                return GuildStoreResult.Success;
            }
            catch (Exception e)
            {
                Logger.ErrorException(e, nameof(ChangeGuildMotd));
                return GuildStoreResult.Failed;
            }
        }

        public GuildStoreResult ApplyMembershipTransition(DBGuild guild, GuildMemberTransition transition)
        {
            lock (_writeLock)
            {
                if (transition.GuildId != guild.Id)
                    return GuildStoreResult.InvalidData;

                try
                {
                    using SQLiteConnection connection = GetConnection();
                    using SQLiteTransaction transaction = connection.BeginTransaction();

                    if (connection.QueryFirstOrDefault<long?>("SELECT Id FROM Guild WHERE Id=@Id", new { guild.Id }, transaction).HasValue == false)
                    {
                        transaction.Rollback();
                        return GuildStoreResult.GuildNotFound;
                    }

                    Dictionary<long, DBGuildMember> members = connection.Query<DBGuildMember>("SELECT * FROM GuildMember WHERE GuildId=@Id", new { guild.Id }, transaction)
                        .ToDictionary(member => member.PlayerDbGuid);

                    foreach (GuildMemberChange change in transition.Changes)
                    {
                        DBGuildMember currentMember = connection.QueryFirstOrDefault<DBGuildMember>("SELECT * FROM GuildMember WHERE PlayerDbGuid=@PlayerDbGuid", new { change.PlayerDbGuid }, transaction);
                        if (change.ExpectedMembership == null)
                        {
                            if (currentMember != null)
                            {
                                transaction.Rollback();
                                return GuildStoreResult.MembershipConflict;
                            }
                        }
                        else if (currentMember == null || currentMember.GuildId != guild.Id || currentMember.Membership != change.ExpectedMembership.Value)
                        {
                            transaction.Rollback();
                            return GuildStoreResult.MembershipConflict;
                        }

                        if (change.NewMembership == null)
                            members.Remove(change.PlayerDbGuid);
                        else
                            members[change.PlayerDbGuid] = new(change.PlayerDbGuid, guild.Id, change.NewMembership.Value);
                    }

                    if (members.Values.Count(member => member.Membership == 3) != 1)
                    {
                        transaction.Rollback();
                        return GuildStoreResult.InvalidData;
                    }

                    foreach (GuildMemberChange change in transition.Changes.Where(change => change.NewMembership == null || (change.ExpectedMembership == 3 && change.NewMembership != 3)))
                    {
                        if (ApplyMembershipChange(connection, transaction, guild.Id, change) != 1)
                        {
                            transaction.Rollback();
                            return GuildStoreResult.MembershipConflict;
                        }
                    }

                    foreach (GuildMemberChange change in transition.Changes.Where(change => change.NewMembership != null && (change.ExpectedMembership != 3 || change.NewMembership == 3)))
                    {
                        if (ApplyMembershipChange(connection, transaction, guild.Id, change) != 1)
                        {
                            transaction.Rollback();
                            return GuildStoreResult.MembershipConflict;
                        }
                    }

                    transaction.Commit();
                    SetPersistenceMetadata(guild);
                    return GuildStoreResult.Success;
                }
                catch (Exception e)
                {
                    Logger.ErrorException(e, nameof(ApplyMembershipTransition));
                    return GuildStoreResult.Failed;
                }
            }
        }

        public GuildStoreResult DeleteGuild(DBGuild guild)
        {
            lock (_writeLock)
            {
                using SQLiteConnection connection = GetConnection();
                using SQLiteTransaction transaction = connection.BeginTransaction();

                try
                {
                    // TODO: Enable foreign key constraints in the connection string and just delete the row from the parent table when we fix the Item table.
                    int deleted = connection.Execute("DELETE FROM Guild WHERE Id = @Id", guild, transaction);
                    if (deleted != 1)
                    {
                        transaction.Rollback();
                        return GuildStoreResult.GuildNotFound;
                    }

                    connection.Execute("DELETE FROM GuildMember WHERE GuildId = @Id", guild, transaction);
                    transaction.Commit();

                    SetPersistenceMetadata(guild);
                    Logger.Trace($"DeleteGuild(): {guild}");
                    return GuildStoreResult.Success;
                }
                catch (Exception e)
                {
                    transaction.Rollback();
                    Logger.ErrorException(e, nameof(DeleteGuild));
                    return GuildStoreResult.Failed;
                }
            }
        }

        private static int ApplyMembershipChange(SQLiteConnection connection, SQLiteTransaction transaction, long guildId, GuildMemberChange change)
        {
            if (change.NewMembership == null)
                return connection.Execute("DELETE FROM GuildMember WHERE PlayerDbGuid=@PlayerDbGuid AND GuildId=@GuildId AND Membership=@ExpectedMembership",
                    new { change.PlayerDbGuid, GuildId = guildId, change.ExpectedMembership }, transaction);

            if (change.ExpectedMembership == null)
                return connection.Execute("INSERT INTO GuildMember (PlayerDbGuid, GuildId, Membership) VALUES (@PlayerDbGuid, @GuildId, @Membership)",
                    new { change.PlayerDbGuid, GuildId = guildId, Membership = change.NewMembership.Value }, transaction);

            return connection.Execute("UPDATE GuildMember SET Membership=@Membership WHERE PlayerDbGuid=@PlayerDbGuid AND GuildId=@GuildId AND Membership=@ExpectedMembership",
                new { change.PlayerDbGuid, GuildId = guildId, Membership = change.NewMembership.Value, change.ExpectedMembership }, transaction);
        }

        private static void SetPersistenceMetadata(DBAccount account)
        {
            account.PersistenceRevision = 0;
            account.PersistenceState = PersistenceState.Clean;
        }

        private static void SetPersistenceMetadata(DBGuild guild)
        {
            guild.PersistenceRevision = 0;
            guild.PersistenceState = PersistenceState.Clean;
        }

        /// <summary>
        /// Creates and opens a new <see cref="SQLiteConnection"/>.
        /// </summary>
        private SQLiteConnection GetConnection()
        {
            SQLiteConnection connection = new(_connectionString);
            connection.Open();
            return connection;
        }

        /// <summary>
        /// Initializes a new empty database file using the current schema.
        /// </summary>
        private bool InitializeDatabaseFile()
        {
            string initializationScript = SQLiteScripts.GetInitializationScript();
            if (initializationScript == string.Empty)
                return Logger.ErrorReturn(false, "InitializeDatabaseFile(): Failed to get database initialization script");

            SQLiteConnection.CreateFile(_dbFilePath);
            using SQLiteConnection connection = GetConnection();
            connection.Execute(initializationScript);

            Logger.Info($"Initialized a new database file at {Path.GetRelativePath(FileHelper.ServerRoot, _dbFilePath)} using schema version {CurrentSchemaVersion}");

            return true;
        }

        /// <summary>
        /// Migrates an existing database file to the current schema if needed.
        /// </summary>
        private bool MigrateDatabaseFileToCurrentSchema()
        {
            using SQLiteConnection connection = GetConnection();

            int schemaVersion = GetSchemaVersion(connection);
            if (schemaVersion > CurrentSchemaVersion)
                return Logger.ErrorReturn(false, $"Initialize(): Existing database file uses unsupported schema version {schemaVersion} (current = {CurrentSchemaVersion})");

            if (schemaVersion < MinimumSchemaVersion)
            {
                Logger.Warn($"Found existing database file with legacy schema version {schemaVersion}, which is not supported by this version of MHServerEmu");
                
                // Need to dispose the connection before moving the database file so that the file is not in use.
                connection.Dispose();
                File.Move(_dbFilePath, $"{_dbFilePath}.old");
                
                InitializeDatabaseFile();
                return true;
            }

            Logger.Info($"Found existing database file with schema version {schemaVersion} (current = {CurrentSchemaVersion})");

            if (schemaVersion == CurrentSchemaVersion)
                return true;

            // Create a backup to fall back to if something goes wrong
            string backupDbPath = $"{_dbFilePath}.v{schemaVersion}";
            File.Copy(_dbFilePath, backupDbPath);

            bool success = true;

            while (schemaVersion < CurrentSchemaVersion)
            {
                Logger.Info($"Migrating version {schemaVersion} => {schemaVersion + 1}...");

                string migrationScript = SQLiteScripts.GetMigrationScript(schemaVersion);
                if (migrationScript == string.Empty)
                {
                    Logger.Error($"MigrateDatabaseFileToCurrentSchema(): Failed to get database migration script for version {schemaVersion}");
                    success = false;
                    break;
                }

                connection.Execute(migrationScript);
                SetSchemaVersion(connection, ++schemaVersion);
            }

            success &= GetSchemaVersion(connection) == CurrentSchemaVersion;

            if (success == false)
            {
                // Restore backup
                File.Delete(_dbFilePath);
                File.Move(backupDbPath, _dbFilePath);
                return Logger.ErrorReturn(false, "MigrateDatabaseFileToCurrentSchema(): Migration failed, backup restored");
            }
            else
            {
                // Clean up backup
                File.Delete(backupDbPath);
            }

            Logger.Info($"Successfully migrated to schema version {CurrentSchemaVersion}");
            return true;
        }

        private bool DoSavePlayerData(DBAccount account)
        {
            // Lock to prevent corruption if we are doing a backup (TODO: Make this better)
            lock (_writeLock)
            {
                using SQLiteConnection connection = GetConnection();

                // Use a transaction to make sure all data is saved
                using SQLiteTransaction transaction = connection.BeginTransaction();

                try
                {
                    // Update player entity
                    if (account.Player != null)
                    {
                        connection.Execute(@"INSERT OR IGNORE INTO Player (DbGuid) VALUES (@DbGuid)", account.Player, transaction);
                        connection.Execute(@"UPDATE Player SET ArchiveData=@ArchiveData, StartTarget=@StartTarget, AOIVolume=@AOIVolume,
                                            GazillioniteBalance=@GazillioniteBalance, LastLogoutTime=@LastLogoutTime WHERE DbGuid = @DbGuid",
                                            account.Player, transaction);
                    }
                    else
                    {
                        Logger.Warn($"DoSavePlayerData(): Attempted to save null player entity data for account {account}");
                    }

                    // Update inventory entities
                    SQLiteEntityTable avatarTable = SQLiteEntityTable.GetTable(DBEntityCategory.Avatar);
                    SQLiteEntityTable teamUpTable = SQLiteEntityTable.GetTable(DBEntityCategory.TeamUp);
                    SQLiteEntityTable itemTable = SQLiteEntityTable.GetTable(DBEntityCategory.Item);
                    SQLiteEntityTable controlledEntityTable = SQLiteEntityTable.GetTable(DBEntityCategory.ControlledEntity);

                    avatarTable.UpdateEntities(connection, transaction, account.Id, account.Avatars);
                    teamUpTable.UpdateEntities(connection, transaction, account.Id, account.TeamUps);
                    itemTable.UpdateEntities(connection, transaction, account.Id, account.Items);

                    foreach (DBEntity avatar in account.Avatars)
                    {
                        itemTable.UpdateEntities(connection, transaction, avatar.DbGuid, account.Items);
                        controlledEntityTable.UpdateEntities(connection, transaction, avatar.DbGuid, account.ControlledEntities);
                    }

                    foreach (DBEntity teamUp in account.TeamUps)
                    {
                        itemTable.UpdateEntities(connection, transaction, teamUp.DbGuid, account.Items);
                    }

                    transaction.Commit();
                }
                catch (Exception e)
                {
                    Logger.Warn($"DoSavePlayerData(): SQLite error for account [{account}]: {e.Message}");
                    transaction.Rollback();
                    return false;
                }

                Logger.Info($"Successfully written player data for account [{account}]");

                if (_backupInProgress == false && _backupTimer.Check())
                {
                    _backupInProgress = true;
                    Task.Run(CreateBackup);
                }

                return true;
            }
        }

        /// <summary>
        /// Creates a backup of the database file using the SQLite backup API.
        /// </summary>
        private void CreateBackup()
        {
            try
            {
                Logger.Info("Starting database backup...");
                TimeSpan startTime = Clock.UnixTime;

                if (FileHelper.PrepareFileBackup(_dbFilePath, _maxBackupNumber, out string backupFilePath) == false)
                    return;

                using SQLiteConnection sourceConnection = GetConnection();
                using SQLiteConnection backupConnection = new($"Data Source={backupFilePath}");
                backupConnection.Open();
                sourceConnection.BackupDatabase(backupConnection, "main", "main", -1, null, -1);

                TimeSpan elapsed = Clock.UnixTime - startTime;
                Logger.Info($"Created database backup in {elapsed.TotalMilliseconds} ms");
            }
            catch (Exception e)
            {
                Logger.Warn($"CreateBackup(): SQLite error creating database backup: {e.Message}");
            }
            finally
            {
                _backupInProgress = false;
            }
        }

        /// <summary>
        /// Returns the user_version value of the current database file.
        /// </summary>
        private static int GetSchemaVersion(SQLiteConnection connection)
        {
            var queryResult = connection.Query<int>("PRAGMA user_version");
            if (queryResult.Any())
                return queryResult.First();

            return Logger.WarnReturn(-1, "GetSchemaVersion(): Failed to query user_version from the DB");
        }

        /// <summary>
        /// Sets the user_version value of the current database file.
        /// </summary>
        private static void SetSchemaVersion(SQLiteConnection connection, int version)
        {
            connection.Execute($"PRAGMA user_version = {version}");
        }
    }
}

using System.Text.Json;
using MHServerEmu.Core.Config;
using MHServerEmu.Core.Helpers;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.System.Time;
using MHServerEmu.DatabaseAccess.Models;

namespace MHServerEmu.DatabaseAccess.Json
{
    /// <summary>
    /// Provides functionality for storing a single <see cref="DBAccount"/> instance in a JSON file.
    /// </summary>
    public class JsonDBManager : IAccountStore, IPlayerStore, IGuildStore
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        private string _accountFilePath;
        private DBAccount _account;
        private JsonSerializerOptions _jsonOptions;

        private int _maxBackupNumber;
        private CooldownTimer _backupTimer;

        public static JsonDBManager Instance { get; } = new();

        private JsonDBManager() { }

        public bool Initialize()
        {
            var config = ConfigManager.Instance.GetConfig<JsonDBManagerConfig>();
            _accountFilePath = Path.Combine(FileHelper.DataDirectory, config.FileName);

            _jsonOptions = new();
            _jsonOptions.Converters.Add(new DBEntityCollectionJsonConverter());

            if (File.Exists(_accountFilePath))
            {
                Logger.Info($"Found existing account file {FileHelper.GetRelativePath(_accountFilePath)}");

                _account = FileHelper.DeserializeJson<DBAccount>(_accountFilePath, _jsonOptions);
                if (_account == null)
                    Logger.Warn($"Initialize(): Failed to load existing account data, resetting");
            }

            if (_account == null)
            {
                // Initialize a new default account from config
                _account = new(config.PlayerName);
                _account.Player = new(_account.Id);

                Logger.Info($"Initialized default account {_account}");
            }
            else
            {
                _account.PlayerName = config.PlayerName;
                Logger.Info($"Loaded default account {_account}");
            }

            _maxBackupNumber = config.MaxBackupNumber;
            _backupTimer = new(TimeSpan.FromMinutes(config.BackupIntervalMinutes));

            return _account != null;
        }

        public bool TryQueryAccountByEmail(string email, out DBAccount account)
        {
            account = _account;
            account.MigrationData.Reset();
            return true;
        }

        public bool TryGetPlayerDbIdByName(string playerName, out ulong playerDbId, out string playerNameOut)
        {
            playerDbId = 0;
            playerNameOut = playerName;
            return Logger.WarnReturn(true, "TryGetPlayerDbIdByName(): Operation not supported");
        }

        public bool TryGetPlayerName(ulong id, out string playerName)
        {
            playerName = $"Player{id}";
            return true;
        }

        public bool GetPlayerNames(Dictionary<ulong, string> playerNames)
        {
            return false;
        }

        public bool TryGetLastLogoutTime(ulong playerDbId, out long lastLogoutTime)
        {
            lastLogoutTime = 0;
            return false;
        }

        public AccountStoreResult InsertAccount(DBAccount account)
        {
            return Logger.WarnReturn(AccountStoreResult.Failed, "InsertAccount(): Operation not supported");
        }

        public AccountStoreResult ChangePlayerName(DBAccount account, string playerName)
        {
            return Logger.WarnReturn(AccountStoreResult.Failed, "ChangePlayerName(): Operation not supported");
        }

        public AccountStoreResult ChangePassword(DBAccount account, byte[] passwordHash, byte[] salt)
        {
            return Logger.WarnReturn(AccountStoreResult.Failed, "ChangePassword(): Operation not supported");
        }

        public AccountStoreResult ChangeUserLevel(DBAccount account, AccountUserLevel userLevel)
        {
            return Logger.WarnReturn(AccountStoreResult.Failed, "ChangeUserLevel(): Operation not supported");
        }

        public AccountStoreResult ChangeFlags(DBAccount account, AccountFlags flags)
        {
            return Logger.WarnReturn(AccountStoreResult.Failed, "ChangeFlags(): Operation not supported");
        }

        public AccountStoreResult ReconcileAccount(DBAccount account)
        {
            if (account == null)
                return AccountStoreResult.InvalidData;
            if (account.PersistenceState == PersistenceState.Clean)
                return AccountStoreResult.Success;
            if (_account == null || account.Id != _account.Id)
                return AccountStoreResult.AccountNotFound;

            ApplyReconciledScalars(account, _account);
            account.PersistenceState = PersistenceState.Clean;
            return AccountStoreResult.Success;
        }

        public PlayerStoreResult LoadPlayerData(DBAccount account)
        {
            // All JSON data is loaded at once (FIXME)
            return PlayerStoreResult.Success;
        }

        public PlayerStoreResult SavePlayerData(DBAccount account)
        {
            if (account != _account)
                return Logger.WarnReturn(PlayerStoreResult.Failed, "SavePlayerData(): Attempting to update non-default account when bypass auth is enabled");

            Logger.Info($"Updated account file {FileHelper.GetRelativePath(_accountFilePath)}");
            FileHelper.SerializeJson(_accountFilePath, _account, _jsonOptions);

            TryCreateBackup();

            return PlayerStoreResult.Success;
        }

        #region Guilds

        // TODO: Guilds are currently not supported by the JSON backend.

        public bool LoadGuilds(List<DBGuild> guilds)
        {
            return true;
        }

        public GuildStoreResult CreateGuild(DBGuild guild, DBGuildMember creator)
        {
            return GuildStoreResult.Success;
        }

        public GuildStoreResult ChangeGuildName(DBGuild guild, string name)
        {
            return GuildStoreResult.Success;
        }

        public GuildStoreResult ChangeGuildMotd(DBGuild guild, string motd)
        {
            return GuildStoreResult.Success;
        }

        public GuildStoreResult ApplyMembershipTransition(DBGuild guild, GuildMemberTransition transition)
        {
            return GuildStoreResult.Success;
        }

        public GuildStoreResult DeleteGuild(DBGuild guild)
        {
            return GuildStoreResult.Success;
        }

        #endregion

        private static void ApplyReconciledScalars(DBAccount account, DBAccount persisted)
        {
            account.Email = persisted.Email;
            account.PlayerName = persisted.PlayerName;
            account.PasswordHash = persisted.PasswordHash;
            account.Salt = persisted.Salt;
            account.UserLevel = persisted.UserLevel;
            account.Flags = persisted.Flags;
            account.PasswordAlgorithm = persisted.PasswordAlgorithm;
            account.PasswordFormatVersion = persisted.PasswordFormatVersion;
            account.PasswordIterations = persisted.PasswordIterations;
            account.PasswordKeySize = persisted.PasswordKeySize;
            account.CredentialVersion = persisted.CredentialVersion;
            account.GameSecurityVersion = persisted.GameSecurityVersion;
            account.PersistenceRevision = persisted.PersistenceRevision;
            account.EmailVerifiedAtUtc = persisted.EmailVerifiedAtUtc;
            account.CreatedAtUtc = persisted.CreatedAtUtc;
            account.UpdatedAtUtc = persisted.UpdatedAtUtc;
        }

        /// <summary>
        /// Creates a backup of the account file if enough time has passed since the last one.
        /// </summary>
        private void TryCreateBackup()
        {
            if (_backupTimer.Check() == false)
                return;

            if (FileHelper.CreateFileBackup(_accountFilePath, _maxBackupNumber))
                Logger.Info("Created account file backup");
        }
    }
}

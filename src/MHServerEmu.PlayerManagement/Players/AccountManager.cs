using Gazillion;
using MHServerEmu.Core.Extensions;
using MHServerEmu.Core.Helpers;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Network;
using MHServerEmu.DatabaseAccess;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.PlayerManagement.Auth;

namespace MHServerEmu.PlayerManagement.Players
{
    public enum AccountOperationResult
    {
        Success,
        GenericFailure,
        DatabaseError,
        EmailInvalid,
        EmailAlreadyUsed,
        EmailNotFound,
        PlayerNameInvalid,
        PlayerNameAlreadyUsed,
        PasswordInvalid,
        FlagAlreadySet,
        FlagNotSet,
    }

    /// <summary>
    /// Provides <see cref="DBAccount"/> management functions.
    /// </summary>
    public static class AccountManager
    {
        private const int EmailMaxLength = 320;
        private const int PasswordMinLength = 3;
        private const int PasswordMaxLength = 64;

        private static readonly Logger Logger = LogManager.CreateLogger();
        private static readonly object AccountCreationLock = new();
        private static readonly object AccountPasswordLock = new();

        /// <summary>
        /// Queries a <see cref="DBAccount"/> using the provided <see cref="LoginDataPB"/> instance.
        /// <see cref="AuthStatusCode"/> indicates the outcome of the query.
        /// </summary>
        public static AuthStatusCode TryGetAccountByLoginDataPB(LoginDataPB loginDataPB, bool useWhitelist, out DBAccount account)
        {
            account = null;

            IDBManager dbManager = IDBManager.Instance;

            // Try to query an account to check
            if (dbManager.TryQueryAccountByEmail(loginDataPB.EmailAddress, out DBAccount accountToCheck) == false)
                return AuthStatusCode.IncorrectUsernameOrPassword403;

            // Check the account we queried if our DB manager requires it
            if (dbManager.VerifyAccounts)
            {
                if (CryptographyHelper.VerifyPassword(loginDataPB.Password, accountToCheck.PasswordHash, accountToCheck.Salt) == false)
                    return AuthStatusCode.IncorrectUsernameOrPassword403;

                if (accountToCheck.Flags.HasFlag(AccountFlags.IsBanned))
                    return AuthStatusCode.AccountBanned;
                
                if (accountToCheck.Flags.HasFlag(AccountFlags.IsArchived))
                    return AuthStatusCode.AccountArchived;
                
                if (accountToCheck.Flags.HasFlag(AccountFlags.IsPasswordExpired))
                    return AuthStatusCode.PasswordExpired;

                if (useWhitelist && accountToCheck.Flags.HasFlag(AccountFlags.IsWhitelisted) == false)
                    return AuthStatusCode.EmailNotVerified;
            }

            // Output the account and return success if everything is okay
            account = accountToCheck;
            return AuthStatusCode.Success;
        }

        /// <summary>
        /// Queries a <see cref="DBAccount"/> using the provided email. Returns <see langword="true"/> if successful.
        /// </summary>
        public static bool TryGetAccountByEmail(string email, out DBAccount account)
        {
            return IDBManager.Instance.TryQueryAccountByEmail(email, out account);
        }

        /// <summary>
        /// Queries a <see cref="DBAccount"/> using the provided player name. Returns <see langword="true"/> if successful.
        /// </summary>
        public static bool TryGetAccountByPlayerName(string playerName, out DBAccount account)
        {
            return IDBManager.Instance.TryQueryAccountByPlayerName(playerName, out account);
        }

        public static bool SupportsCredentialVerification()
        {
            return IDBManager.Instance.VerifyAccounts;
        }

        public static bool TryVerifyAccount(string identifier, string password, out DBAccount account)
        {
            account = null;
            if (string.IsNullOrWhiteSpace(identifier) || string.IsNullOrEmpty(password))
                return false;

            IDBManager dbManager = IDBManager.Instance;
            bool found = identifier.Contains('@')
                ? dbManager.TryQueryAccountByEmail(identifier, out account)
                : dbManager.TryQueryAccountByPlayerName(identifier, out account);
            if (found == false)
                return false;

            if (dbManager.VerifyAccounts && CryptographyHelper.VerifyPassword(password, account.PasswordHash, account.Salt) == false)
            {
                account = null;
                return false;
            }

            return true;
        }

        public static bool LoadPlayerDataForAccount(DBAccount account)
        {
            return IDBManager.Instance.LoadPlayerData(account);
        }

        /// <summary>
        /// Creates a new <see cref="DBAccount"/> and inserts it into the database. Returns <see langword="true"/> if successful.
        /// </summary>
        public static AccountOperationResult CreateAccount(string email, string playerName, string password)
        {
            return CreateAccount(email, playerName, password, out _);
        }

        public static AccountOperationResult CreateAccount(string email, string playerName, string password, out DBAccount account)
        {
            account = null;
            IDBManager dbManager = IDBManager.Instance;

            email = email.ToLowerInvariant();

            // Validate input before doing database queries
            if (ValidateEmail(email) == false)
                return AccountOperationResult.EmailInvalid;

            AccountOperationResult playerNameResult = ValidatePlayerName(playerName);
            if (playerNameResult != AccountOperationResult.Success)
                return playerNameResult;

            if (ValidatePassword(password) == false)
                return AccountOperationResult.PasswordInvalid;

            lock (AccountCreationLock)
            {
                if (dbManager.TryQueryAccountByEmail(email, out _))
                    return AccountOperationResult.EmailAlreadyUsed;

                if (dbManager.TryGetPlayerDbIdByName(playerName, out _, out _))
                    return AccountOperationResult.PlayerNameAlreadyUsed;

                DBAccount createdAccount = new(email, playerName, password);
                if (dbManager.InsertAccount(createdAccount) == false)
                    return AccountOperationResult.DatabaseError;

                account = createdAccount;
                Logger.Info($"CreateAccount(): account=[{account}]");
                return AccountOperationResult.Success;
            }
        }

        // TODO AccountOperationResult ChangeAccountEmail(string oldEmail, string newEmail)

        /// <summary>
        /// Changes the player name of the <see cref="DBAccount"/> with the specified email. Returns <see langword="true"/> if successful.
        /// </summary>
        public static AccountOperationResult ChangeAccountPlayerName(string email, string newPlayerName)
        {
            IDBManager dbManager = IDBManager.Instance;

            AccountOperationResult playerNameResult = ValidatePlayerName(newPlayerName);
            if (playerNameResult != AccountOperationResult.Success)
                return playerNameResult;

            if (dbManager.TryQueryAccountByEmail(email, out DBAccount account) == false)
                return AccountOperationResult.EmailNotFound;

            if (dbManager.TryGetPlayerDbIdByName(newPlayerName, out _, out _))
                return AccountOperationResult.PlayerNameAlreadyUsed;

            // Write the new name to the database
            string oldPlayerName = account.PlayerName;
            account.PlayerName = newPlayerName;
            dbManager.UpdateAccount(account);

            ServiceMessage.PlayerNameChanged playerNameChanged = new((ulong)account.Id, oldPlayerName, newPlayerName);
            ServerManager.Instance.SendMessageToService(GameServiceType.PlayerManager, playerNameChanged);
            ServerManager.Instance.SendMessageToService(GameServiceType.GroupingManager, playerNameChanged);

            Logger.Info($"ChangeAccountPlayerName(): account=[{account}], oldPlayerName={oldPlayerName}");
            return AccountOperationResult.Success;
        }

        /// <summary>
        /// Changes the password of the <see cref="DBAccount"/> with the specified email. Returns <see langword="true"/> if successful.
        /// </summary>
        public static AccountOperationResult ChangeAccountPassword(string email, string newPassword)
        {
            lock (AccountPasswordLock)
            {
                IDBManager dbManager = IDBManager.Instance;

                // Validate input before doing database queries
                if (ValidatePassword(newPassword) == false)
                    return AccountOperationResult.PasswordInvalid;

                if (dbManager.TryQueryAccountByEmail(email, out DBAccount account) == false)
                    return AccountOperationResult.EmailNotFound;

                account.PasswordHash = CryptographyHelper.HashPassword(newPassword, out byte[] salt);
                account.Salt = salt;
                account.Flags &= ~AccountFlags.IsPasswordExpired;
                dbManager.UpdateAccount(account);

                Logger.Info($"ChangeAccountPassword(): account=[{account}]");
                return AccountOperationResult.Success;
            }
        }

        public static AccountOperationResult ChangeAccountPassword(string identifier, string currentPassword, string newPassword)
        {
            lock (AccountPasswordLock)
            {
                if (string.IsNullOrWhiteSpace(identifier) || string.IsNullOrEmpty(currentPassword))
                    return AccountOperationResult.EmailNotFound;

                if (ValidatePassword(newPassword) == false)
                    return AccountOperationResult.PasswordInvalid;

                IDBManager dbManager = IDBManager.Instance;
                bool found = identifier.Contains('@')
                    ? dbManager.TryQueryAccountByEmail(identifier, out DBAccount account)
                    : dbManager.TryQueryAccountByPlayerName(identifier, out account);
                if (found == false)
                    return AccountOperationResult.EmailNotFound;

                if (CryptographyHelper.VerifyPassword(currentPassword, account.PasswordHash, account.Salt) == false)
                    return AccountOperationResult.EmailNotFound;

                byte[] passwordHash = account.PasswordHash;
                byte[] salt = account.Salt;
                AccountFlags flags = account.Flags;
                account.PasswordHash = CryptographyHelper.HashPassword(newPassword, out byte[] newSalt);
                account.Salt = newSalt;
                account.Flags &= ~AccountFlags.IsPasswordExpired;
                if (dbManager.UpdateAccount(account))
                    return AccountOperationResult.Success;

                account.PasswordHash = passwordHash;
                account.Salt = salt;
                account.Flags = flags;
                return AccountOperationResult.DatabaseError;
            }
        }

        public static PortalPasswordChangeOperationOutcome ChangePortalPassword(string identifier, Guid operationId,
            string currentPassword, string newPassword)
        {
            lock (AccountPasswordLock)
            {
                if (SupportsCredentialVerification() == false)
                    return PortalPasswordChangeOperationOutcome.Unavailable;

                if (TryGetAccountByIdentifier(identifier, out DBAccount account) == false)
                    return PortalPasswordChangeOperationOutcome.Rejected;

                return IDBManager.Instance.ResolvePortalPasswordChange(account, operationId, currentPassword, newPassword,
                    ValidatePassword(newPassword));
            }
        }

        public static PortalPasswordChangeOperationOutcome GetPortalPasswordChangeStatus(string identifier, Guid operationId)
        {
            if (SupportsCredentialVerification() == false)
                return PortalPasswordChangeOperationOutcome.Unavailable;

            if (TryGetAccountByIdentifier(identifier, out DBAccount account) == false)
                return PortalPasswordChangeOperationOutcome.Rejected;

            return IDBManager.Instance.GetPortalPasswordChangeStatus(account, operationId);
        }

        /// <summary>
        /// Changes the <see cref="AccountUserLevel"/> of the <see cref="DBAccount"/> with the specified email. Returns <see langword="true"/> if successful.
        /// </summary>
        public static AccountOperationResult SetAccountUserLevel(string email, AccountUserLevel userLevel)
        {
            IDBManager dbManager = IDBManager.Instance;

            // Make sure the specified account exists
            if (dbManager.TryQueryAccountByEmail(email, out DBAccount account) == false)
                return AccountOperationResult.EmailNotFound;

            account.UserLevel = userLevel;
            dbManager.UpdateAccount(account);

            Logger.Info($"SetAccountUserLevel(): account=[{account}], userLevel=[{userLevel}]");
            return AccountOperationResult.Success;
        }

        /// <summary>
        /// Sets the specified <see cref="AccountFlags"/> for the <see cref="DBAccount"/> with the provided email.
        /// </summary>
        public static AccountOperationResult SetFlag(string email, AccountFlags flag)
        {
            if (IDBManager.Instance.TryQueryAccountByEmail(email, out DBAccount account) == false)
                return AccountOperationResult.EmailNotFound;

            return SetFlag(account, flag);
        }

        /// <summary>
        /// Sets the specified <see cref="AccountFlags"/> for the provided <see cref="DBAccount"/>.
        /// </summary>
        public static AccountOperationResult SetFlag(DBAccount account, AccountFlags flag)
        {
            if (account.Flags.HasFlag(flag))
                return AccountOperationResult.FlagAlreadySet;

            account.Flags |= flag;
            IDBManager.Instance.UpdateAccount(account);

            Logger.Info($"SetFlag(): account=[{account}], flag=[{flag}]");
            return AccountOperationResult.Success;
        }

        /// <summary>
        /// Clears the specified <see cref="AccountFlags"/> for the <see cref="DBAccount"/> with the provided email.
        /// </summary>
        public static AccountOperationResult ClearFlag(string email, AccountFlags flag)
        {
            if (IDBManager.Instance.TryQueryAccountByEmail(email, out DBAccount account) == false)
                return AccountOperationResult.EmailNotFound;

            return ClearFlag(account, flag);
        }

        /// <summary>
        /// Clears the specified <see cref="AccountFlags"/> for the provided <see cref="DBAccount"/>.
        /// </summary>
        public static AccountOperationResult ClearFlag(DBAccount account, AccountFlags flag)
        {
            if (account.Flags.HasFlag(flag) == false)
                return AccountOperationResult.FlagNotSet;

            account.Flags &= ~flag;
            IDBManager.Instance.UpdateAccount(account);

            Logger.Info($"ClearFlag(): account=[{account}], flag=[{flag}]");
            return AccountOperationResult.Success;
        }

        public static string GetOperationResultString(AccountOperationResult result, string email = null, string playerName = null)
        {
            switch (result)
            {
                case AccountOperationResult.Success:
                    return $"Created account {email} ({playerName}).";

                case AccountOperationResult.EmailInvalid:
                    return $"'{email}' is not a valid email address.";

                case AccountOperationResult.EmailAlreadyUsed:
                    return $"Email {email} is already used by another account.";

                case AccountOperationResult.EmailNotFound:
                    return $"Account with email {email} not found.";

                case AccountOperationResult.PlayerNameInvalid:
                    return "Names may contain only up to 16 alphanumeric characters.";

                case AccountOperationResult.PlayerNameAlreadyUsed:
                    return $"Name {playerName} is already used by another account.";

                case AccountOperationResult.PasswordInvalid:
                    return "Password must between 3 and 64 characters long.";

                default:
                    return result.ToString();
            }
        }

        /// <summary>
        /// Returns <see langword="true"/> if the provided email <see cref="string"/> is valid.
        /// </summary>
        private static bool ValidateEmail(string email)
        {
            if (email.Length > EmailMaxLength)
                return false;

            // Validate like the client does on the login screen.
            int atIndex = email.IndexOf('@');
            if (atIndex == -1)
                return false;

            int dotIndex = email.LastIndexOf('.');
            if (dotIndex < atIndex)
                return false;

            int topDomainLength = email.Length - (dotIndex + 1);
            if (topDomainLength < 2)
                return false;

            return true;
        }

        private static bool TryGetAccountByIdentifier(string identifier, out DBAccount account)
        {
            account = null;
            if (string.IsNullOrWhiteSpace(identifier))
                return false;

            return identifier.Contains('@')
                ? IDBManager.Instance.TryQueryAccountByEmail(identifier, out account)
                : IDBManager.Instance.TryQueryAccountByPlayerName(identifier, out account);
        }

        /// <summary>
        /// Returns <see langword="true"/> if the provided player name <see cref="string"/> is valid.
        /// </summary>
        private static AccountOperationResult ValidatePlayerName(string playerName)
        {
            return PlayerNameValidator.Instance.ValidatePlayerName(playerName);
        }
        
        /// <summary>
        /// Returns <see langword="true"/> if the provided password <see cref="string"/> is valid.
        /// </summary>
        private static bool ValidatePassword(string password)
        {
            return password.Length.IsWithin(PasswordMinLength, PasswordMaxLength);
        }
    }
}

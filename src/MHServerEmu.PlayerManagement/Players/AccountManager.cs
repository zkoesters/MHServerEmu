using Gazillion;
using MHServerEmu.Core.Helpers;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Network;
using MHServerEmu.DatabaseAccess;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.DatabaseAccess.Persistence;
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
    public sealed class AccountManager
    {
        private const int EmailMaxLength = 320;
        private const int PasswordMinLength = 12;
        private const int PasswordMaxLength = 64;

        private static readonly Logger Logger = LogManager.CreateLogger();

        private readonly IAccountStore _accounts;
        private readonly IPlayerStore _players;
        private readonly PersistenceCapabilities _capabilities;
        private readonly IAccountSecurityNotifier _securityNotifier;

        public AccountManager(IAccountStore accounts, IPlayerStore players, PersistenceCapabilities capabilities, IAccountSecurityNotifier securityNotifier)
        {
            _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
            _players = players ?? throw new ArgumentNullException(nameof(players));
            _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
            _securityNotifier = securityNotifier ?? throw new ArgumentNullException(nameof(securityNotifier));
        }

        /// <summary>
        /// Queries a <see cref="DBAccount"/> using the provided <see cref="LoginDataPB"/> instance.
        /// <see cref="AuthStatusCode"/> indicates the outcome of the query.
        /// </summary>
        public AuthStatusCode TryGetAccountByLoginDataPB(LoginDataPB loginDataPB, bool useWhitelist, out DBAccount account)
        {
            account = null;

            // Try to query an account to check
            if (_accounts.TryQueryAccountByEmail(loginDataPB.EmailAddress, out DBAccount accountToCheck) == false)
                return AuthStatusCode.IncorrectUsernameOrPassword403;

            // Check the account we queried if our DB manager requires it
            if (_capabilities.VerifyAccountCredentials)
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
        public bool TryGetAccountByEmail(string email, out DBAccount account)
        {
            return _accounts.TryQueryAccountByEmail(email, out account);
        }

        /// <summary>
        /// Creates a new <see cref="DBAccount"/> and inserts it into the database. Returns <see langword="true"/> if successful.
        /// </summary>
        public AccountOperationResult CreateAccount(string email, string playerName, string password)
        {
            email = email.ToLowerInvariant();

            // Validate input before doing database queries
            if (ValidateEmail(email) == false)
                return AccountOperationResult.EmailInvalid;

            AccountOperationResult playerNameResult = ValidatePlayerName(playerName);
            if (playerNameResult != AccountOperationResult.Success)
                return playerNameResult;

            if (ValidatePassword(password) == false)
                return AccountOperationResult.PasswordInvalid;

            if (_accounts.TryQueryAccountByEmail(email, out _))
                return AccountOperationResult.EmailAlreadyUsed;

            if (_players.TryGetPlayerDbIdByName(playerName, out _, out _))
                return AccountOperationResult.PlayerNameAlreadyUsed;

            // Create a new account and insert it into the database
            DBAccount account = new(email, playerName, password);

            if (_accounts.InsertAccount(account) != AccountStoreResult.Success)
                return AccountOperationResult.DatabaseError;

            Logger.Info($"CreateAccount(): account=[{account}]");
            return AccountOperationResult.Success;
        }

        // TODO AccountOperationResult ChangeAccountEmail(string oldEmail, string newEmail)

        /// <summary>
        /// Changes the player name of the <see cref="DBAccount"/> with the specified email. Returns <see langword="true"/> if successful.
        /// </summary>
        public AccountOperationResult ChangeAccountPlayerName(string email, string newPlayerName)
        {
            AccountOperationResult playerNameResult = ValidatePlayerName(newPlayerName);
            if (playerNameResult != AccountOperationResult.Success)
                return playerNameResult;

            if (_accounts.TryQueryAccountByEmail(email, out DBAccount account) == false)
                return AccountOperationResult.EmailNotFound;

            if (_players.TryGetPlayerDbIdByName(newPlayerName, out _, out _))
                return AccountOperationResult.PlayerNameAlreadyUsed;

            // Write the new name to the database
            string oldPlayerName = account.PlayerName;
            account.PlayerName = newPlayerName;
            if (TryStoreAccountChange(() => _accounts.ChangePlayerName(account, newPlayerName)) == false)
            {
                account.PlayerName = oldPlayerName;
                return AccountOperationResult.DatabaseError;
            }

            ServiceMessage.PlayerNameChanged playerNameChanged = new((ulong)account.Id, oldPlayerName, newPlayerName);
            ServerManager.Instance.SendMessageToService(GameServiceType.PlayerManager, playerNameChanged);
            ServerManager.Instance.SendMessageToService(GameServiceType.GroupingManager, playerNameChanged);

            Logger.Info($"ChangeAccountPlayerName(): account=[{account}], oldPlayerName={oldPlayerName}");
            return AccountOperationResult.Success;
        }

        /// <summary>
        /// Changes the password of the <see cref="DBAccount"/> with the specified email. Returns <see langword="true"/> if successful.
        /// </summary>
        public AccountOperationResult ChangeAccountPassword(string email, string newPassword)
        {
            // Validate input before doing database queries
            if (ValidatePassword(newPassword) == false)
                return AccountOperationResult.PasswordInvalid;

            if (_accounts.TryQueryAccountByEmail(email, out DBAccount account) == false)
                return AccountOperationResult.EmailNotFound;

            byte[] oldPasswordHash = account.PasswordHash;
            byte[] oldSalt = account.Salt;
            AccountFlags oldFlags = account.Flags;

            account.PasswordHash = CryptographyHelper.HashPassword(newPassword, out byte[] salt);
            account.Salt = salt;
            account.Flags &= ~AccountFlags.IsPasswordExpired;
            if (TryStoreAccountChange(() => _accounts.ChangePassword(account, account.PasswordHash, account.Salt)) == false)
            {
                account.PasswordHash = oldPasswordHash;
                account.Salt = oldSalt;
                account.Flags = oldFlags;
                return AccountOperationResult.DatabaseError;
            }

            _securityNotifier.Notify((ulong)account.Id, AccountSecurityChangeType.CredentialChanged);
            Logger.Info($"ChangeAccountPassword(): account=[{account}]");
            return AccountOperationResult.Success;
        }

        /// <summary>
        /// Changes the <see cref="AccountUserLevel"/> of the <see cref="DBAccount"/> with the specified email. Returns <see langword="true"/> if successful.
        /// </summary>
        public AccountOperationResult SetAccountUserLevel(string email, AccountUserLevel userLevel)
        {
            // Make sure the specified account exists
            if (_accounts.TryQueryAccountByEmail(email, out DBAccount account) == false)
                return AccountOperationResult.EmailNotFound;

            AccountUserLevel oldUserLevel = account.UserLevel;
            account.UserLevel = userLevel;
            if (TryStoreAccountChange(() => _accounts.ChangeUserLevel(account, userLevel)) == false)
            {
                account.UserLevel = oldUserLevel;
                return AccountOperationResult.DatabaseError;
            }

            _securityNotifier.Notify((ulong)account.Id, AccountSecurityChangeType.AuthorizationChanged);
            Logger.Info($"SetAccountUserLevel(): account=[{account}], userLevel=[{userLevel}]");
            return AccountOperationResult.Success;
        }

        /// <summary>
        /// Sets the specified <see cref="AccountFlags"/> for the <see cref="DBAccount"/> with the provided email.
        /// </summary>
        public AccountOperationResult SetFlag(string email, AccountFlags flag)
        {
            if (_accounts.TryQueryAccountByEmail(email, out DBAccount account) == false)
                return AccountOperationResult.EmailNotFound;

            return SetFlag(account, flag);
        }

        /// <summary>
        /// Sets the specified <see cref="AccountFlags"/> for the provided <see cref="DBAccount"/>.
        /// </summary>
        public AccountOperationResult SetFlag(DBAccount account, AccountFlags flag)
        {
            if (account.Flags.HasFlag(flag))
                return AccountOperationResult.FlagAlreadySet;

            AccountFlags oldFlags = account.Flags;
            account.Flags |= flag;
            if (TryStoreAccountChange(() => _accounts.ChangeFlags(account, account.Flags)) == false)
            {
                account.Flags = oldFlags;
                return AccountOperationResult.DatabaseError;
            }

            _securityNotifier.Notify((ulong)account.Id, AccountSecurityChangeType.AccountStatusChanged);
            Logger.Info($"SetFlag(): account=[{account}], flag=[{flag}]");
            return AccountOperationResult.Success;
        }

        /// <summary>
        /// Clears the specified <see cref="AccountFlags"/> for the <see cref="DBAccount"/> with the provided email.
        /// </summary>
        public AccountOperationResult ClearFlag(string email, AccountFlags flag)
        {
            if (_accounts.TryQueryAccountByEmail(email, out DBAccount account) == false)
                return AccountOperationResult.EmailNotFound;

            return ClearFlag(account, flag);
        }

        /// <summary>
        /// Clears the specified <see cref="AccountFlags"/> for the provided <see cref="DBAccount"/>.
        /// </summary>
        public AccountOperationResult ClearFlag(DBAccount account, AccountFlags flag)
        {
            if (account.Flags.HasFlag(flag) == false)
                return AccountOperationResult.FlagNotSet;

            AccountFlags oldFlags = account.Flags;
            account.Flags &= ~flag;
            if (TryStoreAccountChange(() => _accounts.ChangeFlags(account, account.Flags)) == false)
            {
                account.Flags = oldFlags;
                return AccountOperationResult.DatabaseError;
            }

            _securityNotifier.Notify((ulong)account.Id, AccountSecurityChangeType.AccountStatusChanged);
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
                    return "Password must be between 12 and 64 characters long.";

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
            return password != null && password.Length >= PasswordMinLength && password.Length <= PasswordMaxLength;
        }

        private static bool IsSuccess(AccountStoreResult result)
        {
            return result == AccountStoreResult.Success;
        }

        private bool TryStoreAccountChange(Func<AccountStoreResult> change)
        {
            try
            {
                return IsSuccess(change());
            }
            catch (Exception e)
            {
                Logger.ErrorException(e, nameof(TryStoreAccountChange));
                return false;
            }
        }
    }
}

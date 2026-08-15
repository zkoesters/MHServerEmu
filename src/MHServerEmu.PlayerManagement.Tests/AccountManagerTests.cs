using Gazillion;
using MHServerEmu.Core.Network;
using MHServerEmu.DatabaseAccess;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.DatabaseAccess.Persistence;
using MHServerEmu.PlayerManagement.Auth;
using MHServerEmu.PlayerManagement.Players;

namespace MHServerEmu.PlayerManagement.Tests
{
    public class AccountManagerTests
    {
        private readonly StubDBManager _dbManager = new();
        private readonly RecordingAccountSecurityNotifier _notifier = new();
        private readonly AccountManager _accountManager;

        public AccountManagerTests()
        {
            _accountManager = new(_dbManager, _dbManager, PersistenceCapabilities.SQLite, _notifier);
        }

        [Theory]
        [InlineData(11, false)]
        [InlineData(12, true)]
        [InlineData(64, true)]
        [InlineData(65, false)]
        public void CreateAndChangeAccountPassword_RequirePasswordsBetween12And64Characters(int passwordLength, bool valid)
        {
            string password = new('a', passwordLength);
            DBAccount existingAccount = new("existing@example.com", "Existing", "legacy");
            _dbManager.Accounts.Add(existingAccount.Email, existingAccount);

            AccountOperationResult createResult = _accountManager.CreateAccount("new@example.com", "NewPlayer", password);
            AccountOperationResult changeResult = _accountManager.ChangeAccountPassword(existingAccount.Email, password);

            AccountOperationResult expected = valid ? AccountOperationResult.Success : AccountOperationResult.PasswordInvalid;
            Assert.Equal(expected, createResult);
            Assert.Equal(expected, changeResult);
        }

        [Fact]
        public void ChangeAccountPassword_NullPassword_IsInvalid()
        {
            Assert.Equal(AccountOperationResult.PasswordInvalid, _accountManager.ChangeAccountPassword("missing@example.com", null));
        }

        [Fact]
        public void TryGetAccountByLoginDataPB_ExistingShortPassword_Authenticates()
        {
            const string password = "old";
            DBAccount account = new("legacy@example.com", "Legacy", password);
            _dbManager.Accounts.Add(account.Email, account);
            LoginDataPB loginData = LoginDataPB.CreateBuilder()
                .SetEmailAddress(account.Email)
                .SetPassword(password)
                .Build();

            AuthStatusCode result = _accountManager.TryGetAccountByLoginDataPB(loginData, false, out DBAccount authenticatedAccount);

            Assert.Equal(AuthStatusCode.Success, result);
            Assert.Same(account, authenticatedAccount);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ChangeAccountPassword_UpdateFailure_RestoresPasswordData(bool throwOnUpdate)
        {
            DBAccount account = new("account@example.com", "PlayerOne", "old-password")
            {
                Flags = AccountFlags.IsPasswordExpired | AccountFlags.IsBanned
            };
            byte[] oldHash = account.PasswordHash.ToArray();
            byte[] oldSalt = account.Salt.ToArray();
            AccountFlags oldFlags = account.Flags;
            _dbManager.Accounts.Add(account.Email, account);
            _dbManager.UpdateAccountResult = false;
            _dbManager.ThrowOnUpdateAccount = throwOnUpdate;

            AccountOperationResult result = _accountManager.ChangeAccountPassword(account.Email, "new-password1");

            Assert.Equal(AccountOperationResult.DatabaseError, result);
            Assert.Equal(oldHash, account.PasswordHash);
            Assert.Equal(oldSalt, account.Salt);
            Assert.Equal(oldFlags, account.Flags);
        }

        [Theory]
        [InlineData(AccountMutation.PlayerName, false)]
        [InlineData(AccountMutation.PlayerName, true)]
        [InlineData(AccountMutation.UserLevel, false)]
        [InlineData(AccountMutation.UserLevel, true)]
        [InlineData(AccountMutation.SetFlag, false)]
        [InlineData(AccountMutation.SetFlag, true)]
        [InlineData(AccountMutation.ClearFlag, false)]
        [InlineData(AccountMutation.ClearFlag, true)]
        public void AccountMutation_UpdateFailure_RestoresInMemoryState(AccountMutation mutation, bool throwOnUpdate)
        {
            DBAccount account = new("account@example.com", "PlayerOne", "old-password")
            {
                UserLevel = AccountUserLevel.User,
                Flags = AccountFlags.IsPasswordExpired
            };
            _dbManager.Accounts.Add(account.Email, account);
            _dbManager.UpdateAccountResult = false;
            _dbManager.ThrowOnUpdateAccount = throwOnUpdate;

            AccountOperationResult result = mutation switch
            {
                AccountMutation.PlayerName => _accountManager.ChangeAccountPlayerName(account.Email, "PlayerTwo"),
                AccountMutation.UserLevel => _accountManager.SetAccountUserLevel(account.Email, AccountUserLevel.Admin),
                AccountMutation.SetFlag => _accountManager.SetFlag(account.Email, AccountFlags.IsBanned),
                AccountMutation.ClearFlag => _accountManager.ClearFlag(account.Email, AccountFlags.IsPasswordExpired),
                _ => throw new ArgumentOutOfRangeException(nameof(mutation))
            };

            Assert.Equal(AccountOperationResult.DatabaseError, result);
            Assert.Equal("PlayerOne", account.PlayerName);
            Assert.Equal(AccountUserLevel.User, account.UserLevel);
            Assert.Equal(AccountFlags.IsPasswordExpired, account.Flags);
        }

        [Fact]
        public void GetOperationResultString_InvalidPassword_UsesCurrentPasswordPolicyMessage()
        {
            Assert.Equal("Password must be between 12 and 64 characters long.", AccountManager.GetOperationResultString(AccountOperationResult.PasswordInvalid));
        }

        [Theory]
        [InlineData(AccountMutation.Password, AccountSecurityChangeType.CredentialChanged)]
        [InlineData(AccountMutation.UserLevel, AccountSecurityChangeType.AuthorizationChanged)]
        [InlineData(AccountMutation.SetFlag, AccountSecurityChangeType.AccountStatusChanged)]
        [InlineData(AccountMutation.ClearFlag, AccountSecurityChangeType.AccountStatusChanged)]
        public void AccountSecurityMutation_UpdateSucceeds_NotifiesAfterCommit(AccountMutation mutation, AccountSecurityChangeType expectedChangeType)
        {
            DBAccount account = new("account@example.com", "PlayerOne", "old-password")
            {
                Flags = AccountFlags.IsPasswordExpired
            };
            _dbManager.Accounts.Add(account.Email, account);
            RecordingAccountSecurityNotifier notifier = new(() => _dbManager.UpdateAccountCallCount > 0);
            AccountManager accountManager = CreateAccountManager(notifier);

            AccountOperationResult result = mutation switch
            {
                AccountMutation.Password => accountManager.ChangeAccountPassword(account.Email, "new-password1"),
                AccountMutation.UserLevel => accountManager.SetAccountUserLevel(account.Email, AccountUserLevel.Admin),
                AccountMutation.SetFlag => accountManager.SetFlag(account.Email, AccountFlags.IsBanned),
                AccountMutation.ClearFlag => accountManager.ClearFlag(account.Email, AccountFlags.IsPasswordExpired),
                _ => throw new ArgumentOutOfRangeException(nameof(mutation))
            };

            Assert.Equal(AccountOperationResult.Success, result);
            Assert.Equal((ulong)account.Id, notifier.AccountId);
            Assert.Equal(expectedChangeType, notifier.ChangeType);
            Assert.True(notifier.NotifiedAfterCommit);
        }

        [Theory]
        [InlineData(AccountMutation.Password, false)]
        [InlineData(AccountMutation.Password, true)]
        [InlineData(AccountMutation.UserLevel, false)]
        [InlineData(AccountMutation.UserLevel, true)]
        [InlineData(AccountMutation.SetFlag, false)]
        [InlineData(AccountMutation.SetFlag, true)]
        [InlineData(AccountMutation.ClearFlag, false)]
        [InlineData(AccountMutation.ClearFlag, true)]
        public void AccountSecurityMutation_UpdateFails_DoesNotNotify(AccountMutation mutation, bool throwOnUpdate)
        {
            DBAccount account = new("account@example.com", "PlayerOne", "old-password")
            {
                Flags = AccountFlags.IsPasswordExpired
            };
            _dbManager.Accounts.Add(account.Email, account);
            _dbManager.UpdateAccountResult = false;
            _dbManager.ThrowOnUpdateAccount = throwOnUpdate;
            RecordingAccountSecurityNotifier notifier = new();
            AccountManager accountManager = CreateAccountManager(notifier);

            _ = mutation switch
            {
                AccountMutation.Password => accountManager.ChangeAccountPassword(account.Email, "new-password1"),
                AccountMutation.UserLevel => accountManager.SetAccountUserLevel(account.Email, AccountUserLevel.Admin),
                AccountMutation.SetFlag => accountManager.SetFlag(account.Email, AccountFlags.IsBanned),
                AccountMutation.ClearFlag => accountManager.ClearFlag(account.Email, AccountFlags.IsPasswordExpired),
                _ => throw new ArgumentOutOfRangeException(nameof(mutation))
            };

            Assert.False(notifier.Notified);
        }

        [Fact]
        public void TryGetAccountByLoginDataPB_JsonCapabilities_ReturnsAccountWithWrongPassword()
        {
            DBAccount account = new("account@example.com", "PlayerOne", "correct-password");
            _dbManager.Accounts.Add(account.Email, account);
            LoginDataPB loginData = LoginDataPB.CreateBuilder().SetEmailAddress(account.Email).SetPassword("wrong-password").Build();
            AccountManager accountManager = new(_dbManager, _dbManager, PersistenceCapabilities.Json, _notifier);

            AuthStatusCode result = accountManager.TryGetAccountByLoginDataPB(loginData, false, out DBAccount authenticatedAccount);

            Assert.Equal(AuthStatusCode.Success, result);
            Assert.Same(account, authenticatedAccount);
        }

        [Fact]
        public void TryGetAccountByLoginDataPB_SQLiteCapabilities_RejectsWrongPassword()
        {
            DBAccount account = new("account@example.com", "PlayerOne", "correct-password");
            _dbManager.Accounts.Add(account.Email, account);
            LoginDataPB loginData = LoginDataPB.CreateBuilder().SetEmailAddress(account.Email).SetPassword("wrong-password").Build();

            AuthStatusCode result = _accountManager.TryGetAccountByLoginDataPB(loginData, false, out DBAccount authenticatedAccount);

            Assert.Equal(AuthStatusCode.IncorrectUsernameOrPassword403, result);
            Assert.Null(authenticatedAccount);
        }

        public enum AccountMutation
        {
            PlayerName,
            Password,
            UserLevel,
            SetFlag,
            ClearFlag
        }

        private AccountManager CreateAccountManager(IAccountSecurityNotifier notifier)
        {
            return new(_dbManager, _dbManager, PersistenceCapabilities.SQLite, notifier);
        }

        private sealed class RecordingAccountSecurityNotifier(Func<bool> wasCommitted = null) : IAccountSecurityNotifier
        {
            public bool Notified { get; private set; }
            public ulong AccountId { get; private set; }
            public AccountSecurityChangeType ChangeType { get; private set; }
            public bool NotifiedAfterCommit { get; private set; }

            public void Notify(ulong accountId, AccountSecurityChangeType changeType)
            {
                Notified = true;
                AccountId = accountId;
                ChangeType = changeType;
                NotifiedAfterCommit = wasCommitted?.Invoke() ?? true;
            }
        }
    }
}

using MHServerEmu.DatabaseAccess.Models;

namespace MHServerEmu.DatabaseAccess.Tests.Conformance
{
    internal static class AccountStoreConformanceTests
    {
        internal static void AssertIdentityRoundTripAndConflicts(IAccountStore store, DBAccount account, DBAccount duplicateEmail, DBAccount duplicatePlayerName, string changedPlayerName)
        {
            Assert.Equal(AccountStoreResult.Success, store.InsertAccount(account));
            Assert.True(store.TryQueryAccountByEmail(account.Email.ToUpperInvariant(), out DBAccount stored));
            Assert.Equal(account.Id, stored.Id);
            Assert.Equal(account.Email, stored.Email);
            Assert.Equal(account.PlayerName, stored.PlayerName);
            Assert.Equal(account.PasswordHash, stored.PasswordHash);
            Assert.Equal(account.Salt, stored.Salt);
            Assert.Equal(AccountStoreResult.EmailConflict, store.InsertAccount(duplicateEmail));
            Assert.Equal(AccountStoreResult.PlayerNameConflict, store.InsertAccount(duplicatePlayerName));
            Assert.Equal(AccountStoreResult.Success, store.ChangePlayerName(account, changedPlayerName));
            Assert.True(store.TryQueryAccountByEmail(account.Email.ToUpperInvariant(), out stored));
            Assert.Equal(changedPlayerName, stored.PlayerName);
        }
    }
}

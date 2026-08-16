using System.Data.SQLite;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.DatabaseAccess.SQLite;
using MHServerEmu.DatabaseAccess.Tests.Conformance;

namespace MHServerEmu.DatabaseAccess.Tests.SQLite
{
    public class SQLiteDBManagerTests
    {
        [Fact]
        public void Initialize_FreshDatabase_CreatesSchemaWithoutAccounts()
        {
            using TemporaryDirectory temporaryDirectory = new();
            string databasePath = Path.Combine(temporaryDirectory.Path, "Account.db");
            SQLiteDBManager manager = new();

            Assert.True(manager.Initialize(databasePath, 0, TimeSpan.FromHours(1)));

            using SQLiteConnection connection = new($"Data Source={databasePath}");
            connection.Open();
            using SQLiteCommand schemaVersionCommand = new("PRAGMA user_version", connection);
            using SQLiteCommand accountCountCommand = new("SELECT COUNT(*) FROM Account", connection);
            Assert.Equal(6L, Convert.ToInt64(schemaVersionCommand.ExecuteScalar()));
            Assert.Equal(0L, Convert.ToInt64(accountCountCommand.ExecuteScalar()));
        }

        [Fact]
        public void AccountAndPlayer_RoundTrip_PreservesBaselineSemantics()
        {
            using TemporaryDirectory temporaryDirectory = new();
            string databasePath = Path.Combine(temporaryDirectory.Path, "Account.db");
            SQLiteDBManager manager = new();
            DBAccount account = new("Case@Test.com", "PlayerOne", "twelve-chars");

            Assert.True(manager.Initialize(databasePath, 0, TimeSpan.FromHours(1)));
            Assert.Equal(AccountStoreResult.Success, manager.InsertAccount(account));
            Assert.True(manager.TryQueryAccountByEmail("case@test.com", out DBAccount emailAccount));
            Assert.Equal(account.Id, emailAccount.Id);
            Assert.True(manager.TryGetPlayerDbIdByName("playerone", out ulong playerDbId, out string playerName));
            Assert.Equal((ulong)account.Id, playerDbId);
            Assert.Equal("PlayerOne", playerName);

            account.Player = new(account.Id) { ArchiveData = [0x10, 0x20, 0x30] };
            Assert.True(account.Avatars.Add(new()
            {
                DbGuid = account.Id + 1,
                ContainerDbGuid = account.Id,
                InventoryProtoGuid = 100,
                Slot = 0,
                EntityProtoGuid = 200,
                ArchiveData = [0x40, 0x50, 0x60]
            }));
            Assert.Equal(PlayerStoreResult.Success, manager.SavePlayerData(account));

            account.Player = null;
            account.ClearEntities();
            Assert.True(manager.TryQueryAccountByEmail("case@test.com", out DBAccount loadedAccount));
            Assert.Equal(PlayerStoreResult.Success, manager.LoadPlayerData(loadedAccount));
            Assert.Equal(new byte[] { 0x10, 0x20, 0x30 }, loadedAccount.Player.ArchiveData);
            DBEntity avatar = Assert.Single(loadedAccount.Avatars.Entries);
            Assert.Equal(new byte[] { 0x40, 0x50, 0x60 }, avatar.ArchiveData);
        }

        [Fact]
        public void ChangePlayerName_UninsertedAccount_ReturnsAccountNotFound()
        {
            using TemporaryDirectory temporaryDirectory = new();
            string databasePath = Path.Combine(temporaryDirectory.Path, "Account.db");
            SQLiteDBManager manager = new();
            DBAccount account = new("account@example.com", "PlayerOne", "twelve-chars");

            Assert.True(manager.Initialize(databasePath, 0, TimeSpan.FromHours(1)));
            Assert.Equal(AccountStoreResult.AccountNotFound, manager.ChangePlayerName(account, "PlayerTwo"));
        }

        [Fact]
        public void AccountStoreConformance_IdentityRoundTripAndConflicts()
        {
            using TemporaryDirectory temporaryDirectory = new();
            SQLiteDBManager manager = InitializeManager(temporaryDirectory);

            AccountStoreConformanceTests.AssertIdentityRoundTripAndConflicts(
                manager,
                CreateAccount(1, "account@example.com", "PlayerOne"),
                CreateAccount(2, "ACCOUNT@example.com", "PlayerTwo"),
                CreateAccount(3, "other@example.com", "playerone"),
                "PlayerRenamed");
        }

        [Fact]
        public void LoadPlayerData_AbsentAccount_ReturnsAccountNotFoundWithoutMutatingAggregate()
        {
            using TemporaryDirectory temporaryDirectory = new();
            SQLiteDBManager manager = InitializeManager(temporaryDirectory);
            DBAccount account = CreateAccount(1, "account@example.com", "PlayerOne");
            DBPlayer player = new(account.Id) { ArchiveData = [0x01] };
            account.Player = player;
            Assert.True(account.Avatars.Add(new() { DbGuid = 2, ContainerDbGuid = account.Id }));

            Assert.Equal(PlayerStoreResult.AccountNotFound, manager.LoadPlayerData(account));
            Assert.Same(player, account.Player);
            Assert.Single(account.Avatars.Entries);
        }

        [Fact]
        public void SavePlayerData_AbsentAccount_ReturnsAccountNotFoundWithoutCreatingPlayer()
        {
            using TemporaryDirectory temporaryDirectory = new();
            string databasePath = Path.Combine(temporaryDirectory.Path, "Account.db");
            SQLiteDBManager manager = new();
            Assert.True(manager.Initialize(databasePath, 0, TimeSpan.FromHours(1)));
            DBAccount account = CreateAccount(1, "account@example.com", "PlayerOne");
            DBPlayer player = new(account.Id) { ArchiveData = [0x01] };
            account.Player = player;

            Assert.Equal(PlayerStoreResult.AccountNotFound, manager.SavePlayerData(account));
            Assert.Same(player, account.Player);
            Assert.False(PlayerExists(databasePath, account.Id));
        }

        [Fact]
        public void InsertAccount_FinalEmailUniqueConstraint_ReturnsEmailConflict()
        {
            using TemporaryDirectory temporaryDirectory = new();
            SQLiteDBManager manager = InitializeManager(temporaryDirectory);

            Assert.Equal(AccountStoreResult.Success, manager.InsertAccount(CreateAccount(1, "account@example.com", "PlayerOne")));

            Assert.Equal(AccountStoreResult.EmailConflict, manager.InsertAccount(CreateAccount(2, "ACCOUNT@example.com", "PlayerTwo")));
        }

        [Fact]
        public void InsertAccount_FinalPlayerNameUniqueConstraint_ReturnsPlayerNameConflict()
        {
            using TemporaryDirectory temporaryDirectory = new();
            SQLiteDBManager manager = InitializeManager(temporaryDirectory);

            Assert.Equal(AccountStoreResult.Success, manager.InsertAccount(CreateAccount(1, "account@example.com", "PlayerOne")));

            Assert.Equal(AccountStoreResult.PlayerNameConflict, manager.InsertAccount(CreateAccount(2, "other@example.com", "PLAYERONE")));
        }

        [Fact]
        public void ChangePassword_PersistsCredentialsAndClearsPasswordExpiredFlag()
        {
            using TemporaryDirectory temporaryDirectory = new();
            SQLiteDBManager manager = InitializeManager(temporaryDirectory);
            DBAccount account = CreateAccount(1, "account@example.com", "PlayerOne");
            account.Flags = AccountFlags.BypassLoginQueue | AccountFlags.IsPasswordExpired;
            Assert.Equal(AccountStoreResult.Success, manager.InsertAccount(account));

            account.Flags &= ~AccountFlags.IsPasswordExpired;
            account.PersistenceRevision = 42;
            account.PersistenceState = PersistenceState.OutcomeUncertain;

            Assert.Equal(AccountStoreResult.Success, manager.ChangePassword(account, [0x01], [0x02]));
            Assert.Equal(0, account.PersistenceRevision);
            Assert.Equal(PersistenceState.Clean, account.PersistenceState);
            Assert.Equal(new byte[] { 0x01 }, account.PasswordHash);
            Assert.Equal(new byte[] { 0x02 }, account.Salt);
            Assert.Equal(AccountFlags.BypassLoginQueue, account.Flags);
            Assert.True(manager.TryQueryAccountByEmail(account.Email, out DBAccount stored));
            Assert.Equal(AccountFlags.BypassLoginQueue, stored.Flags);
            Assert.Equal(new byte[] { 0x01 }, stored.PasswordHash);
            Assert.Equal(new byte[] { 0x02 }, stored.Salt);
        }

        [Fact]
        public void ChangePassword_StaleAccountFlags_PreservesConcurrentUnrelatedFlags()
        {
            using TemporaryDirectory temporaryDirectory = new();
            string databasePath = Path.Combine(temporaryDirectory.Path, "Account.db");
            SQLiteDBManager manager = new();
            Assert.True(manager.Initialize(databasePath, 0, TimeSpan.FromHours(1)));
            DBAccount account = CreateAccount(1, "account@example.com", "PlayerOne");
            account.Flags = AccountFlags.IsPasswordExpired;
            Assert.Equal(AccountStoreResult.Success, manager.InsertAccount(account));
            SetAccountFlags(databasePath, account.Id, AccountFlags.IsPasswordExpired | AccountFlags.IsBanned);

            Assert.Equal(AccountStoreResult.Success, manager.ChangePassword(account, [0x01], [0x02]));
            Assert.Equal(AccountFlags.IsBanned, account.Flags);
            Assert.True(manager.TryQueryAccountByEmail(account.Email, out DBAccount stored));
            Assert.Equal(AccountFlags.IsBanned, stored.Flags);
        }

        [Fact]
        public void ChangePlayerName_FinalPlayerNameUniqueConstraint_ReturnsPlayerNameConflict()
        {
            using TemporaryDirectory temporaryDirectory = new();
            SQLiteDBManager manager = InitializeManager(temporaryDirectory);
            DBAccount first = CreateAccount(1, "first@example.com", "PlayerOne");
            DBAccount second = CreateAccount(2, "second@example.com", "PlayerTwo");
            Assert.Equal(AccountStoreResult.Success, manager.InsertAccount(first));
            Assert.Equal(AccountStoreResult.Success, manager.InsertAccount(second));

            Assert.Equal(AccountStoreResult.PlayerNameConflict, manager.ChangePlayerName(first, second.PlayerName));
            Assert.Equal("PlayerOne", first.PlayerName);
        }

        [Fact]
        public void ChangePlayerName_AsciiCaseVariant_ReturnsPlayerNameConflict()
        {
            using TemporaryDirectory temporaryDirectory = new();
            SQLiteDBManager manager = InitializeManager(temporaryDirectory);
            DBAccount first = CreateAccount(1, "first@example.com", "PlayerOne");
            DBAccount second = CreateAccount(2, "second@example.com", "PlayerTwo");
            Assert.Equal(AccountStoreResult.Success, manager.InsertAccount(first));
            Assert.Equal(AccountStoreResult.Success, manager.InsertAccount(second));

            Assert.Equal(AccountStoreResult.PlayerNameConflict, manager.ChangePlayerName(first, "PLAYERTWO"));
            Assert.Equal("PlayerOne", first.PlayerName);
        }

        [Fact]
        public void AccountIntentWrites_SuccessApplyCommittedValuesToModel()
        {
            using TemporaryDirectory temporaryDirectory = new();
            SQLiteDBManager manager = InitializeManager(temporaryDirectory);
            DBAccount account = CreateAccount(1, "account@example.com", "PlayerOne");
            Assert.Equal(AccountStoreResult.Success, manager.InsertAccount(account));

            Assert.Equal(AccountStoreResult.Success, manager.ChangePlayerName(account, "PlayerTwo"));
            Assert.Equal("PlayerTwo", account.PlayerName);
            Assert.Equal(AccountStoreResult.Success, manager.ChangeUserLevel(account, AccountUserLevel.Admin));
            Assert.Equal(AccountUserLevel.Admin, account.UserLevel);
            Assert.Equal(AccountStoreResult.Success, manager.ChangeFlags(account, AccountFlags.IsBanned));
            Assert.Equal(AccountFlags.IsBanned, account.Flags);
            Assert.Equal(0, account.PersistenceRevision);
            Assert.Equal(PersistenceState.Clean, account.PersistenceState);
        }

        [Fact]
        public void AccountIntentWrites_FailureLeavesModelUnchanged()
        {
            using TemporaryDirectory temporaryDirectory = new();
            SQLiteDBManager manager = InitializeManager(temporaryDirectory);
            DBAccount account = CreateAccount(1, "account@example.com", "PlayerOne");
            account.PersistenceRevision = 7;
            account.PersistenceState = PersistenceState.OutcomeUncertain;
            byte[] passwordHash = account.PasswordHash;
            byte[] salt = account.Salt;

            Assert.Equal(AccountStoreResult.AccountNotFound, manager.ChangePlayerName(account, "PlayerTwo"));
            Assert.Equal(AccountStoreResult.AccountNotFound, manager.ChangePassword(account, [0x01], [0x02]));
            Assert.Equal(AccountStoreResult.AccountNotFound, manager.ChangeUserLevel(account, AccountUserLevel.Admin));
            Assert.Equal(AccountStoreResult.AccountNotFound, manager.ChangeFlags(account, AccountFlags.IsBanned));
            Assert.Equal("PlayerOne", account.PlayerName);
            Assert.Equal(passwordHash, account.PasswordHash);
            Assert.Equal(salt, account.Salt);
            Assert.Equal(AccountUserLevel.User, account.UserLevel);
            Assert.Equal(AccountFlags.None, account.Flags);
            Assert.Equal(7, account.PersistenceRevision);
            Assert.Equal(PersistenceState.OutcomeUncertain, account.PersistenceState);
        }

        [Fact]
        public void GetPlayerNames_AppendsToSuppliedDictionary()
        {
            using TemporaryDirectory temporaryDirectory = new();
            SQLiteDBManager manager = InitializeManager(temporaryDirectory);
            Assert.Equal(AccountStoreResult.Success, manager.InsertAccount(CreateAccount(1, "account@example.com", "PlayerOne")));
            Dictionary<ulong, string> names = new() { [99] = "Existing" };

            Assert.True(manager.GetPlayerNames(names));

            Assert.Equal("Existing", names[99]);
            Assert.Equal("PlayerOne", names[1]);
        }

        [Fact]
        public void CreateGuild_CreatorAlreadyInAnotherGuild_RollsBackAndReturnsMembershipConflict()
        {
            using TemporaryDirectory temporaryDirectory = new();
            string databasePath = Path.Combine(temporaryDirectory.Path, "Account.db");
            SQLiteDBManager manager = new();
            Assert.True(manager.Initialize(databasePath, 0, TimeSpan.FromHours(1)));
            InsertGuild(databasePath, 1, "Existing");
            InsertGuildMember(databasePath, 1, 1, 3);
            DBGuild guild = new(2, "New", string.Empty, 1, 0);

            Assert.Equal(GuildStoreResult.MembershipConflict, manager.CreateGuild(guild, new DBGuildMember(1, 2, 3)));
            Assert.False(GuildExists(databasePath, 2));
        }

        [Fact]
        public void CreateGuild_FinalNameUniqueConstraint_ReturnsNameConflict()
        {
            using TemporaryDirectory temporaryDirectory = new();
            string databasePath = Path.Combine(temporaryDirectory.Path, "Account.db");
            SQLiteDBManager manager = new();
            Assert.True(manager.Initialize(databasePath, 0, TimeSpan.FromHours(1)));
            InsertGuild(databasePath, 1, "Existing");

            Assert.Equal(GuildStoreResult.NameConflict, manager.CreateGuild(new DBGuild(2, "EXISTING", string.Empty, 2, 0), new DBGuildMember(2, 2, 3)));
        }

        [Fact]
        public void ChangeGuildName_FinalNameUniqueConstraint_ReturnsNameConflict()
        {
            using TemporaryDirectory temporaryDirectory = new();
            string databasePath = Path.Combine(temporaryDirectory.Path, "Account.db");
            SQLiteDBManager manager = new();
            Assert.True(manager.Initialize(databasePath, 0, TimeSpan.FromHours(1)));
            InsertGuild(databasePath, 1, "First");
            InsertGuild(databasePath, 2, "Second");

            Assert.Equal(GuildStoreResult.NameConflict, manager.ChangeGuildName(new DBGuild(1, "First", string.Empty, 1, 0), "SECOND"));
        }

        [Fact]
        public void GuildIntentWrites_SuccessApplyCommittedValuesToModel()
        {
            using TemporaryDirectory temporaryDirectory = new();
            string databasePath = Path.Combine(temporaryDirectory.Path, "Account.db");
            SQLiteDBManager manager = new();
            Assert.True(manager.Initialize(databasePath, 0, TimeSpan.FromHours(1)));
            InsertGuild(databasePath, 1, "First");
            DBGuild guild = new(1, "First", "Original", 1, 0)
            {
                PersistenceRevision = 7,
                PersistenceState = PersistenceState.OutcomeUncertain
            };

            Assert.Equal(GuildStoreResult.Success, manager.ChangeGuildName(guild, "Renamed"));
            Assert.Equal("Renamed", guild.Name);
            Assert.Equal(GuildStoreResult.Success, manager.ChangeGuildMotd(guild, "Updated"));
            Assert.Equal("Updated", guild.Motd);
            Assert.Equal(0, guild.PersistenceRevision);
            Assert.Equal(PersistenceState.Clean, guild.PersistenceState);
        }

        [Fact]
        public void GuildIntentWrites_FailureLeavesModelUnchanged()
        {
            using TemporaryDirectory temporaryDirectory = new();
            SQLiteDBManager manager = InitializeManager(temporaryDirectory);
            DBGuild guild = new(1, "Missing", "Original", 1, 0)
            {
                PersistenceRevision = 7,
                PersistenceState = PersistenceState.OutcomeUncertain
            };

            Assert.Equal(GuildStoreResult.GuildNotFound, manager.ChangeGuildName(guild, "Renamed"));
            Assert.Equal(GuildStoreResult.GuildNotFound, manager.ChangeGuildMotd(guild, "Updated"));
            Assert.Equal("Missing", guild.Name);
            Assert.Equal("Original", guild.Motd);
            Assert.Equal(7, guild.PersistenceRevision);
            Assert.Equal(PersistenceState.OutcomeUncertain, guild.PersistenceState);
        }

        [Fact]
        public void DeleteGuild_MissingGuild_ReturnsGuildNotFound()
        {
            using TemporaryDirectory temporaryDirectory = new();
            SQLiteDBManager manager = InitializeManager(temporaryDirectory);

            Assert.Equal(GuildStoreResult.GuildNotFound, manager.DeleteGuild(new DBGuild(1, "Missing", string.Empty, 1, 0)));
        }

        [Fact]
        public void ApplyMembershipTransition_IgnoresSuppliedOptimisticRevisions()
        {
            using TemporaryDirectory temporaryDirectory = new();
            string databasePath = Path.Combine(temporaryDirectory.Path, "Account.db");
            SQLiteDBManager manager = new();
            Assert.True(manager.Initialize(databasePath, 0, TimeSpan.FromHours(1)));
            InsertGuild(databasePath, 1, "Guild");
            InsertGuildMember(databasePath, 1, 1, 3);
            InsertGuildMember(databasePath, 2, 1, 1);
            DBGuild guild = new(1, "Guild", string.Empty, 1, 0) { PersistenceRevision = 1 };
            GuildMemberTransition transition = new(1, 99,
                new GuildMemberChange(1, 3, 2),
                new GuildMemberChange(2, 1, 3));

            Assert.Equal(GuildStoreResult.Success, manager.ApplyMembershipTransition(guild, transition));
            Assert.Equal(0, guild.PersistenceRevision);
            Assert.Equal(PersistenceState.Clean, guild.PersistenceState);
        }

        [Fact]
        public void ApplyMembershipTransition_MemberInAnotherGuild_ReturnsMembershipConflict()
        {
            using TemporaryDirectory temporaryDirectory = new();
            string databasePath = Path.Combine(temporaryDirectory.Path, "Account.db");
            SQLiteDBManager manager = new();
            Assert.True(manager.Initialize(databasePath, 0, TimeSpan.FromHours(1)));
            InsertGuild(databasePath, 1, "Guild");
            InsertGuild(databasePath, 2, "Other");
            InsertGuildMember(databasePath, 1, 1, 3);
            InsertGuildMember(databasePath, 2, 2, 1);
            DBGuild guild = new(1, "Guild", string.Empty, 1, 0);
            GuildMemberTransition transition = new(1, 0, new GuildMemberChange(2, null, 1));

            Assert.Equal(GuildStoreResult.MembershipConflict, manager.ApplyMembershipTransition(guild, transition));
        }

        [Fact]
        public void ApplyMembershipTransition_LeavingGuildWithoutLeader_ReturnsInvalidData()
        {
            using TemporaryDirectory temporaryDirectory = new();
            string databasePath = Path.Combine(temporaryDirectory.Path, "Account.db");
            SQLiteDBManager manager = new();
            Assert.True(manager.Initialize(databasePath, 0, TimeSpan.FromHours(1)));
            InsertGuild(databasePath, 1, "Guild");
            InsertGuildMember(databasePath, 1, 1, 3);
            DBGuild guild = new(1, "Guild", string.Empty, 1, 0);
            GuildMemberTransition transition = new(1, 0, new GuildMemberChange(1, 3, 2));

            Assert.Equal(GuildStoreResult.InvalidData, manager.ApplyMembershipTransition(guild, transition));
            Assert.Equal(3L, GetGuildMembership(databasePath, 1));
        }

        [Fact]
        public void ApplyMembershipTransition_LeaderTransfer_PersistsExactlyOneLeader()
        {
            using TemporaryDirectory temporaryDirectory = new();
            string databasePath = Path.Combine(temporaryDirectory.Path, "Account.db");
            SQLiteDBManager manager = new();
            Assert.True(manager.Initialize(databasePath, 0, TimeSpan.FromHours(1)));
            InsertGuild(databasePath, 1, "Guild");
            InsertGuildMember(databasePath, 1, 1, 3);
            InsertGuildMember(databasePath, 2, 1, 1);
            DBGuild guild = new(1, "Guild", string.Empty, 1, 0);
            GuildMemberTransition transition = new(1, 0,
                new GuildMemberChange(1, 3, 2),
                new GuildMemberChange(2, 1, 3));

            Assert.Equal(GuildStoreResult.Success, manager.ApplyMembershipTransition(guild, transition));
            Assert.Equal(2L, GetGuildMembership(databasePath, 1));
            Assert.Equal(3L, GetGuildMembership(databasePath, 2));
        }

        private static SQLiteDBManager InitializeManager(TemporaryDirectory temporaryDirectory)
        {
            SQLiteDBManager manager = new();
            Assert.True(manager.Initialize(Path.Combine(temporaryDirectory.Path, "Account.db"), 0, TimeSpan.FromHours(1)));
            return manager;
        }

        private static DBAccount CreateAccount(long id, string email, string playerName)
        {
            return new DBAccount(email, playerName, "twelve-chars") { Id = id };
        }

        private static void InsertGuild(string databasePath, long id, string name)
        {
            using SQLiteConnection connection = OpenConnection(databasePath);
            using SQLiteCommand command = new("INSERT INTO Guild (Id, Name, Motd, CreatorDbGuid, CreationTime) VALUES (@id, @name, '', 0, 0)", connection);
            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@name", name);
            command.ExecuteNonQuery();
        }

        private static void InsertGuildMember(string databasePath, long playerDbGuid, long guildId, long membership)
        {
            using SQLiteConnection connection = OpenConnection(databasePath);
            using SQLiteCommand command = new("INSERT INTO GuildMember (PlayerDbGuid, GuildId, Membership) VALUES (@playerDbGuid, @guildId, @membership)", connection);
            command.Parameters.AddWithValue("@playerDbGuid", playerDbGuid);
            command.Parameters.AddWithValue("@guildId", guildId);
            command.Parameters.AddWithValue("@membership", membership);
            command.ExecuteNonQuery();
        }

        private static bool GuildExists(string databasePath, long id)
        {
            using SQLiteConnection connection = OpenConnection(databasePath);
            using SQLiteCommand command = new("SELECT COUNT(*) FROM Guild WHERE Id = @id", connection);
            command.Parameters.AddWithValue("@id", id);
            return Convert.ToInt64(command.ExecuteScalar()) == 1;
        }

        private static bool PlayerExists(string databasePath, long id)
        {
            using SQLiteConnection connection = OpenConnection(databasePath);
            using SQLiteCommand command = new("SELECT COUNT(*) FROM Player WHERE DbGuid = @id", connection);
            command.Parameters.AddWithValue("@id", id);
            return Convert.ToInt64(command.ExecuteScalar()) == 1;
        }

        private static void SetAccountFlags(string databasePath, long id, AccountFlags flags)
        {
            using SQLiteConnection connection = OpenConnection(databasePath);
            using SQLiteCommand command = new("UPDATE Account SET Flags = @flags WHERE Id = @id", connection);
            command.Parameters.AddWithValue("@flags", flags);
            command.Parameters.AddWithValue("@id", id);
            command.ExecuteNonQuery();
        }

        private static long GetGuildMembership(string databasePath, long playerDbGuid)
        {
            using SQLiteConnection connection = OpenConnection(databasePath);
            using SQLiteCommand command = new("SELECT Membership FROM GuildMember WHERE PlayerDbGuid = @playerDbGuid", connection);
            command.Parameters.AddWithValue("@playerDbGuid", playerDbGuid);
            return Convert.ToInt64(command.ExecuteScalar());
        }

        private static SQLiteConnection OpenConnection(string databasePath)
        {
            SQLiteConnection connection = new($"Data Source={databasePath}");
            connection.Open();
            return connection;
        }
    }
}

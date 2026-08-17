using System.Text.Json;
using System.Reflection;
using MHServerEmu.DatabaseAccess.Json;
using MHServerEmu.DatabaseAccess.Models;

namespace MHServerEmu.DatabaseAccess.Tests.Json
{
    public class DBAccountJsonSerializerTests
    {
        [Fact]
        public void TrySerializeAccount_ExcludesCredentialsAndRetainsPlayer()
        {
            DBAccount account = new("account@example.com", "PlayerOne", "password")
            {
                PasswordHash = [0x10, 0x20, 0x30, 0x40],
                Salt = [0x50, 0x60, 0x70, 0x80]
            };
            account.Player = new(account.Id);

            Assert.True(DBAccountJsonSerializer.Instance.TrySerializeAccount(account, false, out string json));

            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            Assert.False(root.TryGetProperty("PasswordHash", out _));
            Assert.False(root.TryGetProperty("Salt", out _));
            Assert.DoesNotContain(Convert.ToBase64String(account.PasswordHash), json);
            Assert.DoesNotContain(Convert.ToBase64String(account.Salt), json);
            Assert.True(root.TryGetProperty("Player", out JsonElement player));
            Assert.Equal(account.Id, player.GetProperty("DbGuid").GetInt64());
        }

        [Fact]
        public void PersistenceMetadata_IsExcludedFromJson()
        {
            DBAccount account = new("account@example.com", "PlayerOne", "password");
            DBPlayer player = new(1);
            DBGuild guild = new(1, "Guild", "Motd", 1, 1);

            AssertMetadataIsExcluded(JsonSerializer.Serialize(account),
                "PersistenceRevision", "PersistenceState", "CreatedAtUtc", "UpdatedAtUtc",
                "PasswordAlgorithm", "PasswordFormatVersion", "PasswordIterations", "PasswordKeySize",
                "CredentialVersion", "GameSecurityVersion", "EmailVerifiedAtUtc");
            AssertMetadataIsExcluded(JsonSerializer.Serialize(player),
                "PersistenceRevision", "PersistenceState", "CreatedAtUtc", "UpdatedAtUtc",
                "ArchiveVersion", "GameBuildNumber");
            AssertMetadataIsExcluded(JsonSerializer.Serialize(guild),
                "PersistenceRevision", "PersistenceState", "CreatedAtUtc", "UpdatedAtUtc");
        }

        [Fact]
        public void Constructor_InitializesCurrentCredentialMetadata()
        {
            DBAccount account = new("account@example.com", "PlayerOne", "password");

            Assert.Equal("PBKDF2-HMAC-SHA512", account.PasswordAlgorithm);
            Assert.Equal(1, account.PasswordFormatVersion);
            Assert.Equal(210000, account.PasswordIterations);
            Assert.Equal(64, account.PasswordKeySize);
        }

        [Fact]
        public void JsonStore_UsesCheckedNoOpResultsForUnsupportedAccountAndGuildIntents()
        {
            JsonDBManager store = JsonDBManager.Instance;
            DBAccount account = new("account@example.com", "PlayerOne", "password");
            DBGuild guild = new(1, "Guild", string.Empty, account.Id, 0);
            DBGuildMember creator = new(account.Id, guild.Id, 3);
            GuildMemberTransition transition = new(guild.Id, 0, new GuildMemberChange(account.Id, null, 3));

            Assert.Equal(AccountStoreResult.Failed, store.InsertAccount(account));
            Assert.Equal(AccountStoreResult.Failed, store.ChangePlayerName(account, "PlayerTwo"));
            Assert.Equal(AccountStoreResult.Failed, store.ChangePassword(account, [0x01], [0x02]));
            Assert.Equal(AccountStoreResult.Failed, store.ChangeUserLevel(account, AccountUserLevel.Admin));
            Assert.Equal(AccountStoreResult.Failed, store.ChangeFlags(account, AccountFlags.IsBanned));
            Assert.Equal(PlayerStoreResult.Success, store.LoadPlayerData(account));
            Assert.Equal(PlayerStoreResult.Failed, store.SavePlayerData(new DBAccount("other@example.com", "PlayerTwo", "password")));
            Assert.Equal(GuildStoreResult.Success, store.CreateGuild(guild, creator));
            Assert.Equal(GuildStoreResult.Success, store.ChangeGuildName(guild, "Renamed"));
            Assert.Equal(GuildStoreResult.Success, store.ChangeGuildMotd(guild, "Motd"));
            Assert.Equal(GuildStoreResult.Success, store.ApplyMembershipTransition(guild, transition));
            Assert.Equal(GuildStoreResult.Success, store.DeleteGuild(guild));
        }

        [Fact]
        public void ReconcileAccount_UncertainDefaultIdClone_RefreshesScalarsAndPreservesAggregate()
        {
            DBAccount configured = CreateConfiguredAccount();
            JsonDBManager store = CreateStore(configured);
            DBAccount clone = new("Clone")
            {
                Id = configured.Id,
                Email = "stale@example.com",
                PlayerName = "StalePlayer",
                PasswordHash = [0x10],
                Salt = [0x11],
                UserLevel = AccountUserLevel.User,
                Flags = AccountFlags.IsArchived,
                PasswordAlgorithm = "stale",
                PasswordFormatVersion = 99,
                PasswordIterations = 98,
                PasswordKeySize = 97,
                CredentialVersion = 96,
                GameSecurityVersion = 95,
                PersistenceRevision = 94,
                EmailVerifiedAtUtc = null,
                CreatedAtUtc = null,
                UpdatedAtUtc = null,
                PersistenceState = PersistenceState.OutcomeUncertain,
            };
            DBPlayer player = new(clone.Id);
            DBEntityCollection avatars = clone.Avatars;
            DBEntityCollection teamUps = clone.TeamUps;
            DBEntityCollection items = clone.Items;
            DBEntityCollection controlledEntities = clone.ControlledEntities;
            DBEntityCollection transferredEntities = clone.TransferredEntities;
            MigrationData migrationData = clone.MigrationData;
            clone.Player = player;

            Assert.Equal(AccountStoreResult.Success, store.ReconcileAccount(clone));

            Assert.Equal(configured.Email, clone.Email);
            Assert.Equal(configured.PlayerName, clone.PlayerName);
            Assert.Equal(configured.PasswordHash, clone.PasswordHash);
            Assert.Equal(configured.Salt, clone.Salt);
            Assert.Equal(configured.UserLevel, clone.UserLevel);
            Assert.Equal(configured.Flags, clone.Flags);
            Assert.Equal(configured.PasswordAlgorithm, clone.PasswordAlgorithm);
            Assert.Equal(configured.PasswordFormatVersion, clone.PasswordFormatVersion);
            Assert.Equal(configured.PasswordIterations, clone.PasswordIterations);
            Assert.Equal(configured.PasswordKeySize, clone.PasswordKeySize);
            Assert.Equal(configured.CredentialVersion, clone.CredentialVersion);
            Assert.Equal(configured.GameSecurityVersion, clone.GameSecurityVersion);
            Assert.Equal(configured.PersistenceRevision, clone.PersistenceRevision);
            Assert.Equal(configured.EmailVerifiedAtUtc, clone.EmailVerifiedAtUtc);
            Assert.Equal(configured.CreatedAtUtc, clone.CreatedAtUtc);
            Assert.Equal(configured.UpdatedAtUtc, clone.UpdatedAtUtc);
            Assert.Same(player, clone.Player);
            Assert.Same(avatars, clone.Avatars);
            Assert.Same(teamUps, clone.TeamUps);
            Assert.Same(items, clone.Items);
            Assert.Same(controlledEntities, clone.ControlledEntities);
            Assert.Same(transferredEntities, clone.TransferredEntities);
            Assert.Same(migrationData, clone.MigrationData);
            Assert.Equal(PersistenceState.Clean, clone.PersistenceState);
        }

        [Fact]
        public void ReconcileAccount_NonDefaultIdClone_ReturnsAccountNotFoundAndRetainsUncertainty()
        {
            DBAccount configured = CreateConfiguredAccount();
            JsonDBManager store = CreateStore(configured);
            DBAccount clone = new("Clone")
            {
                Id = configured.Id + 1,
                Email = "stale@example.com",
                PersistenceState = PersistenceState.OutcomeUncertain,
            };
            DBPlayer player = new(clone.Id);
            clone.Player = player;

            Assert.Equal(AccountStoreResult.AccountNotFound, store.ReconcileAccount(clone));
            Assert.Equal("stale@example.com", clone.Email);
            Assert.Same(player, clone.Player);
            Assert.Equal(PersistenceState.OutcomeUncertain, clone.PersistenceState);
        }

        [Fact]
        public void ReconcileAccount_CleanDefaultIdClone_IsNoOpSuccess()
        {
            DBAccount configured = CreateConfiguredAccount();
            JsonDBManager store = CreateStore(configured);
            DBAccount clone = new("Clone")
            {
                Id = configured.Id,
                Email = "clean@example.com",
            };

            Assert.Equal(AccountStoreResult.Success, store.ReconcileAccount(clone));
            Assert.Equal("clean@example.com", clone.Email);
            Assert.Equal(PersistenceState.Clean, clone.PersistenceState);
        }

        private static JsonDBManager CreateStore(DBAccount account)
        {
            JsonDBManager store = (JsonDBManager)Activator.CreateInstance(typeof(JsonDBManager), nonPublic: true);
            typeof(JsonDBManager).GetField("_account", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(store, account);
            return store;
        }

        private static DBAccount CreateConfiguredAccount()
        {
            return new DBAccount("Configured")
            {
                Email = "configured@example.com",
                PlayerName = "ConfiguredPlayer",
                PasswordHash = [0x01, 0x02],
                Salt = [0x03, 0x04],
                UserLevel = AccountUserLevel.Admin,
                Flags = AccountFlags.IsBanned,
                PasswordAlgorithm = "configured",
                PasswordFormatVersion = 2,
                PasswordIterations = 3,
                PasswordKeySize = 4,
                CredentialVersion = 5,
                GameSecurityVersion = 6,
                PersistenceRevision = 7,
                EmailVerifiedAtUtc = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                CreatedAtUtc = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                UpdatedAtUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            };
        }

        private static void AssertMetadataIsExcluded(string json, params string[] propertyNames)
        {
            using JsonDocument document = JsonDocument.Parse(json);

            foreach (string propertyName in propertyNames)
                Assert.False(document.RootElement.TryGetProperty(propertyName, out _));
        }
    }
}

using System.Text.Json;
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

        private static void AssertMetadataIsExcluded(string json, params string[] propertyNames)
        {
            using JsonDocument document = JsonDocument.Parse(json);

            foreach (string propertyName in propertyNames)
                Assert.False(document.RootElement.TryGetProperty(propertyName, out _));
        }
    }
}

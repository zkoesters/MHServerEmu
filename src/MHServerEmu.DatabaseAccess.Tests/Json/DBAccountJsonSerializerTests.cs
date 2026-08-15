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
    }
}

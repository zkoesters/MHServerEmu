using System.Text.Json;
using System.Text.Json.Nodes;
using MHServerEmu.DatabaseAccess.Json;
using MHServerEmu.DatabaseAccess.Models;

namespace MHServerEmu.PortalBridge.Tests.Json
{
    public sealed class DBAccountJsonSerializerTests
    {
        [Fact]
        public void SerializeDeserialize_RoundTripsPortalAccountIdWithFormatVersion()
        {
            DBAccount account = new("player@example.test", "StarLord", "correct horse battery staple");

            Assert.True(DBAccountJsonSerializer.Instance.TrySerializeAccount(account, false, out string json));
            Assert.True(DBAccountJsonSerializer.Instance.TryDeserializeAccount(json, out DBAccount roundTripped));
            using JsonDocument document = JsonDocument.Parse(json);

            Assert.Equal(1, document.RootElement.GetProperty("formatVersion").GetInt32());
            Assert.Equal(account.PortalAccountId, roundTripped.PortalAccountId);
            Assert.Equal(account.Email, roundTripped.Email);
            Assert.Equal(account.PlayerName, roundTripped.PlayerName);
        }

        [Fact]
        public void Deserialize_LegacyUnversionedAccount_AssignsPortalAccountId()
        {
            DBAccount accountToSerialize = new("player@example.test", "StarLord", "correct horse battery staple");
            Assert.True(DBAccountJsonSerializer.Instance.TrySerializeAccount(accountToSerialize, false, out string versionedJson));
            JsonObject legacyAccount = JsonNode.Parse(versionedJson)!["account"]!.AsObject();
            legacyAccount.Remove("PortalAccountId");

            Assert.True(DBAccountJsonSerializer.Instance.TryDeserializeAccount(legacyAccount.ToJsonString(), out DBAccount account));

            Assert.StartsWith("acct_", account.PortalAccountId, StringComparison.Ordinal);
        }
    }
}

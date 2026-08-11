using System.Text.Json;
using System.Text.Json.Serialization;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.RateLimiting;
using MHServerEmu.DatabaseAccess.Models;

namespace MHServerEmu.DatabaseAccess.Json
{
    /// <summary>
    /// Serializes <see cref="DBAccount"/> instances to JSON.
    /// </summary>
    public class DBAccountJsonSerializer
    {
        private const int CurrentFormatVersion = 1;
        private static readonly Logger Logger = LogManager.CreateLogger();

        private readonly JsonSerializerOptions _options = new();
        private readonly TimeLeakyBucketCollection<ulong> _rateLimiter = new(TimeSpan.FromMinutes(30), 5);

        public static DBAccountJsonSerializer Instance { get; } = new();

        private DBAccountJsonSerializer()
        {
            _options.Converters.Add(new DBEntityCollectionJsonConverter());
        }

        public bool TrySerializeAccount(DBAccount account, bool checkRateLimit, out string json)
        {
            json = string.Empty;

            if (account == null) return Logger.WarnReturn(false, "TrySerializeAccount(): account == null");

            if (checkRateLimit && _rateLimiter.AddTime((ulong)account.Id) == false)
                return false;

            try
            {
                json = JsonSerializer.Serialize(new AccountEnvelope(CurrentFormatVersion, account), _options);
            }
            catch (Exception e)
            {
                Logger.Error($"Failed to serialize account {account}: {e.Message}");
                return false;
            }

            return true;
        }

        public bool TryDeserializeAccount(string json, out DBAccount account)
        {
            account = null;
            if (string.IsNullOrWhiteSpace(json))
                return false;

            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                JsonElement accountElement = root;
                if (root.TryGetProperty("formatVersion", out JsonElement versionElement))
                {
                    if (versionElement.TryGetInt32(out int version) == false || version != CurrentFormatVersion ||
                        root.TryGetProperty("account", out accountElement) == false)
                        return false;
                }

                account = JsonSerializer.Deserialize<DBAccount>(accountElement.GetRawText(), _options);
                if (account == null)
                    return false;

                account.EnsurePortalAccountId();
                return true;
            }
            catch (JsonException)
            {
                account = null;
                return false;
            }
        }

        private sealed class AccountEnvelope
        {
            [JsonPropertyName("formatVersion")]
            public int FormatVersion { get; }

            [JsonPropertyName("account")]
            public DBAccount Account { get; }

            public AccountEnvelope(int formatVersion, DBAccount account)
            {
                FormatVersion = formatVersion;
                Account = account;
            }
        }
    }
}

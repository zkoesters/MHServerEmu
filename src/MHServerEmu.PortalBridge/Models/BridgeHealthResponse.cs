using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace MHServerEmu.PortalBridge.Models
{
    public sealed class BridgeHealthResponse
    {
        [JsonPropertyName("status")]
        public string Status { get; }

        [JsonPropertyName("checkedAtUtc")]
        public DateTimeOffset CheckedAtUtc { get; }

        [JsonPropertyName("services")]
        public IReadOnlyDictionary<string, string> Services { get; }

        public BridgeHealthResponse(string status, DateTimeOffset checkedAtUtc,
            IReadOnlyDictionary<string, string> services)
        {
            Status = status;
            CheckedAtUtc = checkedAtUtc;
            Services = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(services));
        }
    }
}

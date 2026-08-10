using System.Text.Json.Serialization;

namespace MHServerEmu.PortalBridge.Models
{
    public sealed class BridgeProblemResponse
    {
        [JsonPropertyName("code")]
        public string Code { get; }

        [JsonPropertyName("correlationId")]
        public Guid CorrelationId { get; }

        public BridgeProblemResponse(string code, Guid correlationId)
        {
            Code = code;
            CorrelationId = correlationId;
        }
    }
}

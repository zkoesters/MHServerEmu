using System.Text.Json.Serialization;

namespace MHServerEmu.PortalBridge.Models
{
    public sealed class PortalRegisterRequest
    {
        [JsonPropertyName("email")]
        public string Email { get; set; }

        [JsonPropertyName("playerName")]
        public string PlayerName { get; set; }

        [JsonPropertyName("password")]
        public string Password { get; set; }
    }

    public sealed class PortalVerifyRequest
    {
        [JsonPropertyName("identifier")]
        public string Identifier { get; set; }

        [JsonPropertyName("password")]
        public string Password { get; set; }
    }

    public sealed class PortalChangePasswordRequest
    {
        [JsonPropertyName("identifier")]
        public string Identifier { get; set; }

        [JsonPropertyName("currentPassword")]
        public string CurrentPassword { get; set; }

        [JsonPropertyName("newPassword")]
        public string NewPassword { get; set; }

        [JsonPropertyName("operationId")]
        public Guid OperationId { get; set; }
    }

    public sealed class PortalPasswordChangeStatusRequest
    {
        [JsonPropertyName("identifier")]
        public string Identifier { get; set; }

        [JsonPropertyName("operationId")]
        public Guid OperationId { get; set; }
    }

    public sealed class PortalPasswordChangeOutcomeResponse
    {
        [JsonPropertyName("outcome")]
        public string Outcome { get; }

        public PortalPasswordChangeOutcomeResponse(string outcome)
        {
            Outcome = outcome;
        }
    }

    public sealed class EmulatorAccountResponse
    {
        [JsonPropertyName("emulatorAccountId")]
        public string EmulatorAccountId { get; }

        public EmulatorAccountResponse(string emulatorAccountId)
        {
            EmulatorAccountId = emulatorAccountId;
        }
    }
}

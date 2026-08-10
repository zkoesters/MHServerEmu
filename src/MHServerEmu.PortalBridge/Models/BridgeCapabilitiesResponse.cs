using System.Text.Json.Serialization;

namespace MHServerEmu.PortalBridge.Models
{
    public sealed class BridgeCapabilitiesResponse
    {
        [JsonPropertyName("contractVersion")]
        public string ContractVersion { get; }

        [JsonPropertyName("emulatorVersion")]
        public string EmulatorVersion { get; }

        [JsonPropertyName("upstreamCommit")]
        public string UpstreamCommit { get; }

        [JsonPropertyName("gameBuild")]
        public string GameBuild { get; }

        [JsonPropertyName("snapshotSchemaVersion")]
        public int SnapshotSchemaVersion { get; }

        [JsonPropertyName("serverInstanceId")]
        public Guid ServerInstanceId { get; }

        [JsonPropertyName("capabilities")]
        public IReadOnlyList<string> Capabilities { get; }

        public BridgeCapabilitiesResponse(string contractVersion, string emulatorVersion, string upstreamCommit,
            string gameBuild, int snapshotSchemaVersion, Guid serverInstanceId, IEnumerable<string> capabilities)
        {
            ContractVersion = contractVersion;
            EmulatorVersion = emulatorVersion;
            UpstreamCommit = upstreamCommit;
            GameBuild = gameBuild;
            SnapshotSchemaVersion = snapshotSchemaVersion;
            ServerInstanceId = serverInstanceId;
            Capabilities = Array.AsReadOnly(capabilities.ToArray());
        }
    }
}

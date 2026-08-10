using MHServerEmu.Core.Config;

namespace MHServerEmu.PortalBridge
{
    public class PortalBridgeConfig : ConfigContainer
    {
        public bool Enabled { get; internal set; } = false;
        public string Address { get; internal set; } = "localhost";
        public int Port { get; internal set; } = 8090;
        public string KeyId { get; internal set; } = "portal-primary";
        public string SecretFile { get; internal set; } = string.Empty;
        public string ServerInstanceId { get; internal set; } = string.Empty;

        public bool TryCreateSettings(out PortalBridgeSettings settings, out string error)
        {
            return PortalBridgeSettings.TryCreate(
                Address,
                Port,
                KeyId,
                SecretFile,
                ServerInstanceId,
                out settings,
                out error);
        }
    }
}

using System.Net;
using MHServerEmu.Core.Network.Web;
using MHServerEmu.PortalBridge.Models;

namespace MHServerEmu.PortalBridge.Handlers
{
    public sealed class CapabilitiesWebHandler : WebHandler
    {
        public const string Path = "/portal-bridge/v1/capabilities";

        private static readonly string[] Capabilities = { "bridge.health" };

        private readonly BridgeCapabilitiesResponse _response;

        public CapabilitiesWebHandler(PortalBridgeMetadata metadata, Guid serverInstanceId)
        {
            ArgumentNullException.ThrowIfNull(metadata);
            _response = new BridgeCapabilitiesResponse("1.0", metadata.EmulatorVersion, metadata.UpstreamCommit,
                metadata.GameBuild, 1, serverInstanceId, Capabilities);
        }

        protected override async Task Get(WebRequestContext context)
        {
            if (string.Equals(context.RawUrl, Path, StringComparison.Ordinal) == false)
            {
                context.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            await context.SendJsonAsync(_response);
        }
    }
}

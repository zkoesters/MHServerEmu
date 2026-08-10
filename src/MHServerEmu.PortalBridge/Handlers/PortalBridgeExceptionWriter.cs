using System.Net;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Network.Web;
using MHServerEmu.PortalBridge.Models;

namespace MHServerEmu.PortalBridge.Handlers
{
    public sealed class PortalBridgeExceptionWriter : IWebExceptionWriter
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        public async Task WriteAsync(WebRequestContext context, Exception exception)
        {
            Guid correlationId = Guid.NewGuid();
            Logger.ErrorException(exception, $"PortalBridge request failed with correlationId={correlationId}");

            context.StatusCode = (int)HttpStatusCode.InternalServerError;
            await context.SendJsonAsync(new BridgeProblemResponse("bridge_internal_error", correlationId),
                "application/problem+json");
        }
    }
}

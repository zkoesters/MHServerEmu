using System.Net;
using MHServerEmu.Core.Network.Web;

namespace MHServerEmu.PortalBridge.Handlers
{
    public sealed class PortalBridgeNotFoundWebHandler : WebHandler
    {
        protected override Task Get(WebRequestContext context)
        {
            context.StatusCode = (int)HttpStatusCode.NotFound;
            return Task.CompletedTask;
        }

        protected override Task Post(WebRequestContext context)
        {
            context.StatusCode = (int)HttpStatusCode.NotFound;
            return Task.CompletedTask;
        }

        protected override Task Delete(WebRequestContext context)
        {
            context.StatusCode = (int)HttpStatusCode.NotFound;
            return Task.CompletedTask;
        }
    }
}

using System.Net;
using MHServerEmu.Core.Extensions;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Network.Web;
using MHServerEmu.PortalBridge.Models;

namespace MHServerEmu.PortalBridge.Authentication
{
    public sealed class PortalBridgeRequestAuthorizer : IWebRequestAuthorizer
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        private readonly HmacRequestValidator _validator;

        public PortalBridgeRequestAuthorizer(HmacRequestValidator validator)
        {
            _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        }

        public async Task<bool> AuthorizeAsync(WebRequestContext context)
        {
            bool authorized = _validator.TryValidate(PortalBridgeRequest.FromContext(context), out Guid correlationId);
            string client = context.GetIPAddressHandle();
            Logger.Info($"PortalBridge authorization route={context.LocalPath}, result={authorized}, correlationId={correlationId}, client={client}");

            if (authorized)
                return true;

            context.StatusCode = (int)HttpStatusCode.Unauthorized;
            await context.SendJsonAsync(new BridgeProblemResponse("bridge_authentication_failed", correlationId),
                "application/problem+json");
            return false;
        }
    }
}

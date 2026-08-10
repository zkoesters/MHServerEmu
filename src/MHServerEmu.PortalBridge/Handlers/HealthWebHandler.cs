using System.Net;
using MHServerEmu.Core.Network;
using MHServerEmu.Core.Network.Web;
using MHServerEmu.PortalBridge.Models;

namespace MHServerEmu.PortalBridge.Handlers
{
    public sealed class HealthWebHandler : WebHandler
    {
        public const string Path = "/portal-bridge/v1/health";

        private readonly Func<GameServiceState?> _playerManagerStateProvider;
        private readonly TimeProvider _timeProvider;

        public HealthWebHandler(Func<GameServiceState?> playerManagerStateProvider, TimeProvider timeProvider)
        {
            _playerManagerStateProvider = playerManagerStateProvider ?? throw new ArgumentNullException(nameof(playerManagerStateProvider));
            _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        }

        protected override async Task Get(WebRequestContext context)
        {
            if (string.Equals(context.RawUrl, Path, StringComparison.Ordinal) == false)
            {
                context.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            await context.SendJsonAsync(CreateResponse(_playerManagerStateProvider(), _timeProvider.GetUtcNow()));
        }

        internal static BridgeHealthResponse CreateResponse(GameServiceState? playerManagerState,
            DateTimeOffset checkedAtUtc)
        {
            string playerManagerStatus = playerManagerState switch
            {
                GameServiceState.Running => "healthy",
                GameServiceState.Starting or GameServiceState.ShuttingDown => "degraded",
                _ => "unhealthy",
            };
            Dictionary<string, string> services = new()
            {
                ["bridge"] = "healthy",
                ["playerManager"] = playerManagerStatus,
            };
            string status = playerManagerStatus == "healthy" ? "healthy" : playerManagerStatus == "degraded"
                ? "degraded"
                : "unhealthy";

            return new BridgeHealthResponse(status, checkedAtUtc, services);
        }
    }
}

using System.Net;
using Google.ProtocolBuffers;
using Gazillion;
using MHServerEmu.Core.Extensions;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Network;
using MHServerEmu.Core.Network.Web;
using MHServerEmu.WebFrontend.Network;
using MHServerEmu.WebFrontend.RateLimiting;

namespace MHServerEmu.WebFrontend.Handlers
{
    public class ProtobufWebHandler : WebHandler
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        private readonly DualKeyRateLimiter _loginRateLimiter;

        public ProtobufWebHandler(bool enableLoginRateLimit, TimeSpan loginRateLimitCost, int loginRateLimitBurst)
            : this(enableLoginRateLimit, loginRateLimitCost, loginRateLimitBurst, int.MaxValue)
        {
        }

        public ProtobufWebHandler(bool enableLoginRateLimit, TimeSpan loginRateLimitCost, int loginRateLimitBurst, int rateLimitMaxKeys)
        {
            if (enableLoginRateLimit)
                _loginRateLimiter = new(loginRateLimitCost, loginRateLimitBurst, rateLimitMaxKeys);
        }

        protected override async Task Post(WebRequestContext context)
        {
            if (context.IsGameClientRequest == false)
            {
                context.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            if (_loginRateLimiter != null && _loginRateLimiter.TryAddSource(context.GetIPAddress()) == false)
            {
                context.StatusCode = (int)HttpStatusCode.TooManyRequests;
                return;
            }

            IMessage message = await context.ReadProtobufAsync<FrontendProtocolMessage>();

            switch (message)
            {
                case LoginDataPB loginDataPB:
                    await OnLoginDataPB(context, loginDataPB);
                    break;

                case PrecacheHeaders precacheHeaders:
                    await OnPrecacheHeaders(context, precacheHeaders);
                    break;

                default:
                    Logger.Warn($"Post(): Unhandled protobuf {message?.DescriptorForType.Name}");
                    context.StatusCode = (int)HttpStatusCode.BadRequest;
                    break;
            }
        }

        private async Task OnLoginDataPB(WebRequestContext context, LoginDataPB loginDataPB)
        {
            if (loginDataPB == null)
            {
                Logger.Warn($"OnLoginDataPB(): Failed to retrieve message");
                context.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }

            string email = loginDataPB.EmailAddress?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(email))
            {
                context.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }

            if (_loginRateLimiter != null && _loginRateLimiter.TryAddAccount(email) == false)
            {
                context.StatusCode = (int)HttpStatusCode.TooManyRequests;
                return;
            }

            string ipAddressHandle = context.GetIPAddressHandle();

            ServiceMessage.AuthResponse authResponse = await GameServiceTaskManager.Instance.AuthenticateAsync(loginDataPB);

            int statusCode = authResponse.StatusCode;
            AuthTicket authTicket = authResponse.AuthTicket;

            // Respond with an error if session creation didn't succeed
            if (statusCode != (int)HttpStatusCode.OK)
            {
                context.StatusCode = statusCode;
                Logger.Info($"Authentication for the game client on {ipAddressHandle} failed ({statusCode})");
                return;
            }

            // Send an AuthTicket if we were able to create a session
            string machineId = loginDataPB.HasMachineId ? loginDataPB.MachineId : string.Empty;
            Logger.Info($"Sending AuthTicket for SessionId 0x{authTicket.SessionId:X} to the game client on {ipAddressHandle}, machineId={machineId}");
            await context.SendAsync(authTicket);
        }

        private static async Task OnPrecacheHeaders(WebRequestContext context, PrecacheHeaders precacheHeaders)
        {
            Logger.Trace("Received PrecacheHeaders message");
            await context.SendAsync(PrecacheHeadersMessageResponse.DefaultInstance);
        }
    }
}

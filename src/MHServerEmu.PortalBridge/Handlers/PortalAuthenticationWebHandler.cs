using System.Net;
using MHServerEmu.Core.Network.Web;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.PlayerManagement.Players;
using MHServerEmu.PortalBridge.Authentication;
using MHServerEmu.PortalBridge.Models;

namespace MHServerEmu.PortalBridge.Handlers
{
    public sealed class PortalAuthenticationWebHandler : WebHandler
    {
        public const string RegisterPath = "/portal-bridge/v1/auth/register";
        public const string VerifyPath = "/portal-bridge/v1/auth/verify";
        public const string ChangePasswordPath = "/portal-bridge/v1/auth/password";

        private readonly OpaqueAccountIdGenerator _accountIdGenerator;

        internal PortalAuthenticationWebHandler(OpaqueAccountIdGenerator accountIdGenerator)
        {
            _accountIdGenerator = accountIdGenerator ?? throw new ArgumentNullException(nameof(accountIdGenerator));
        }

        protected override async Task Post(WebRequestContext context)
        {
            if (string.Equals(context.RawUrl, RegisterPath, StringComparison.Ordinal))
            {
                await RegisterAsync(context);
                return;
            }

            if (string.Equals(context.RawUrl, VerifyPath, StringComparison.Ordinal))
            {
                await VerifyAsync(context);
                return;
            }

            if (string.Equals(context.RawUrl, ChangePasswordPath, StringComparison.Ordinal))
            {
                await ChangePasswordAsync(context);
                return;
            }

            context.StatusCode = (int)HttpStatusCode.NotFound;
        }

        private async Task RegisterAsync(WebRequestContext context)
        {
            if (AccountManager.SupportsCredentialVerification() == false)
            {
                await WriteProblemAsync(context, HttpStatusCode.ServiceUnavailable, "account_service_unavailable");
                return;
            }

            PortalRegisterRequest request = await context.ReadJsonAsync<PortalRegisterRequest>();
            if (request == null || string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.PlayerName) ||
                string.IsNullOrEmpty(request.Password))
            {
                await WriteProblemAsync(context, HttpStatusCode.Unauthorized, "invalid_credentials");
                return;
            }

            AccountOperationResult result = AccountManager.CreateAccount(request.Email, request.PlayerName, request.Password,
                out DBAccount account);
            if (result == AccountOperationResult.Success)
            {
                await context.SendJsonAsync(new EmulatorAccountResponse(_accountIdGenerator.GetAccountId(account.Id)));
                return;
            }

            if (result is AccountOperationResult.EmailAlreadyUsed or AccountOperationResult.PlayerNameAlreadyUsed)
            {
                await WriteProblemAsync(context, HttpStatusCode.Conflict, "account_conflict");
                return;
            }

            await WriteProblemAsync(context, result == AccountOperationResult.DatabaseError
                ? HttpStatusCode.ServiceUnavailable
                : HttpStatusCode.Unauthorized, result == AccountOperationResult.DatabaseError
                    ? "account_service_unavailable"
                    : "invalid_credentials");
        }

        private async Task VerifyAsync(WebRequestContext context)
        {
            if (AccountManager.SupportsCredentialVerification() == false)
            {
                await WriteProblemAsync(context, HttpStatusCode.ServiceUnavailable, "account_service_unavailable");
                return;
            }

            PortalVerifyRequest request = await context.ReadJsonAsync<PortalVerifyRequest>();
            if (request == null || AccountManager.TryVerifyAccount(request.Identifier, request.Password, out DBAccount account) == false)
            {
                await WriteProblemAsync(context, HttpStatusCode.Unauthorized, "invalid_credentials");
                return;
            }

            await context.SendJsonAsync(new EmulatorAccountResponse(_accountIdGenerator.GetAccountId(account.Id)));
        }

        private static async Task ChangePasswordAsync(WebRequestContext context)
        {
            if (AccountManager.SupportsCredentialVerification() == false)
            {
                await WriteProblemAsync(context, HttpStatusCode.ServiceUnavailable, "account_service_unavailable");
                return;
            }

            PortalChangePasswordRequest request = await context.ReadJsonAsync<PortalChangePasswordRequest>();
            if (request == null || string.IsNullOrWhiteSpace(request.Identifier) || string.IsNullOrEmpty(request.CurrentPassword) ||
                string.IsNullOrEmpty(request.NewPassword))
            {
                await WriteProblemAsync(context, HttpStatusCode.Unauthorized, "invalid_credentials");
                return;
            }

            AccountOperationResult result = AccountManager.ChangeAccountPassword(request.Identifier, request.CurrentPassword,
                request.NewPassword);
            if (result == AccountOperationResult.Success)
            {
                context.StatusCode = (int)HttpStatusCode.NoContent;
                return;
            }

            await WriteProblemAsync(context, result == AccountOperationResult.DatabaseError
                ? HttpStatusCode.ServiceUnavailable
                : HttpStatusCode.Unauthorized, result == AccountOperationResult.DatabaseError
                    ? "account_service_unavailable"
                    : "invalid_credentials");
        }

        private static Task WriteProblemAsync(WebRequestContext context, HttpStatusCode statusCode, string code)
        {
            context.StatusCode = (int)statusCode;
            return context.SendJsonAsync(new BridgeProblemResponse(code, Guid.NewGuid()), "application/problem+json");
        }

    }
}

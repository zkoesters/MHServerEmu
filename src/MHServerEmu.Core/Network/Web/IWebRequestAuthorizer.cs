namespace MHServerEmu.Core.Network.Web
{
    public interface IWebRequestAuthorizer
    {
        Task<bool> AuthorizeAsync(WebRequestContext context);
    }
}

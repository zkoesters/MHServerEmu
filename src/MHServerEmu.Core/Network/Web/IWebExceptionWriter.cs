namespace MHServerEmu.Core.Network.Web
{
    public interface IWebExceptionWriter
    {
        Task WriteAsync(WebRequestContext context, Exception exception);
    }
}

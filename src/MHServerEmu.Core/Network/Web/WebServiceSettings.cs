namespace MHServerEmu.Core.Network.Web
{
    public class WebServiceSettings
    {
        public string Name { get; init; }
        public string ListenUrl { get; init; }
        public WebHandler FallbackHandler { get; init; }
        public int MaxRequestBodyBytes { get; init; } = 16 * 1024;
        public TimeSpan RequestBodyReadTimeout { get; init; } = TimeSpan.FromSeconds(10);
        public int JsonMaxDepth { get; init; } = 32;
    }
}

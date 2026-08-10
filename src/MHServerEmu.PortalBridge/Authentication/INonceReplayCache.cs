namespace MHServerEmu.PortalBridge.Authentication
{
    public interface INonceReplayCache
    {
        bool TryReserve(string nonce, DateTimeOffset nowUtc, TimeSpan retention);
    }
}

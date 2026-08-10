using System.Reflection;

namespace MHServerEmu.PortalBridge
{
    public static class PortalBridgeBuildMetadata
    {
        public static string UpstreamCommit { get; } = typeof(PortalBridgeBuildMetadata).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "PortalBridgeUpstreamCommit")
            .Value;
    }
}

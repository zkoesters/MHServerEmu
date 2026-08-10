using System.Reflection;

namespace MHServerEmu.PortalBridge
{
    public static class PortalBridgeBuildMetadata
    {
        public static string UpstreamCommit { get => ReadUpstreamCommit(typeof(PortalBridgeBuildMetadata).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()); }

        internal static string ReadUpstreamCommit(IEnumerable<AssemblyMetadataAttribute> attributes)
        {
            string[] values = attributes
                .Where(attribute => attribute.Key == "PortalBridgeUpstreamCommit")
                .Select(attribute => attribute.Value)
                .ToArray();

            if (values.Length != 1 || IsLowercaseCommit(values[0]) == false)
                throw new InvalidOperationException("PortalBridge upstream commit metadata is invalid.");

            return values[0];
        }

        private static bool IsLowercaseCommit(string value)
        {
            return value?.Length == 40 && value.All(character =>
                (character is >= '0' and <= '9') || (character is >= 'a' and <= 'f'));
        }
    }
}

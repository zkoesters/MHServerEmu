using MHServerEmu.Core.Config;
using MHServerEmu.Core.Network.Web;

namespace MHServerEmu.WebFrontend
{
    public class WebFrontendConfig : ConfigContainer
    {
        public string DeploymentProfile { get; private set; } = "Legacy";
        public string Address { get; private set; } = "localhost";
        public string Port { get; private set; } = "8080";
        public bool EnableLoginRateLimit { get; private set; } = true;
        public int LoginRateLimitCostMS { get; private set; } = 30000;
        public int LoginRateLimitBurst { get; private set; } = 10;
        public bool EnableAccountCreationRateLimit { get; private set; } = true;
        public int AccountCreationRateLimitCostMS { get; private set; } = 300000;
        public int AccountCreationRateLimitBurst { get; private set; } = 3;
        public int RateLimitMaxKeys { get; private set; } = 10000;
        public bool EnableWebApi { get; private set; } = true;
        public bool EnableDashboard { get; private set; } = true;
        public string DashboardFileDirectory { get; private set; } = "Dashboard";
        public string DashboardUrlPath { get; private set; } = "/";
        public int MaxRequestBodyBytes { get; private set; } = 16 * 1024;
        public int RequestBodyReadTimeoutMS { get; private set; } = 10000;
        public int JsonMaxDepth { get; private set; } = 32;
        public string TrustedProxyNetworks { get; private set; } = string.Empty;

        public IReadOnlyList<IpNetwork> GetTrustedProxyNetworks()
        {
            if (TrustedProxyNetworks.Length == 0)
                return [];

            return TrustedProxyNetworks.Split(',', StringSplitOptions.TrimEntries).Select(IpNetwork.Parse).ToList();
        }

        public WebDeploymentProfile GetDeploymentProfile()
        {
            if (Enum.TryParse(DeploymentProfile, ignoreCase: true, out WebDeploymentProfile profile) == false
                || Enum.IsDefined(profile) == false)
            {
                throw new InvalidOperationException($"Invalid web deployment profile: {DeploymentProfile}");
            }

            return profile;
        }
    }
}

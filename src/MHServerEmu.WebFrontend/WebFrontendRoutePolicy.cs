namespace MHServerEmu.WebFrontend
{
    public enum WebDeploymentProfile
    {
        Legacy,
        Portal,
    }

    internal static class WebFrontendRoutePolicy
    {
        private static readonly string[] GameRoutes =
        [
            "/Login/IndexPB",
            "/AuthServer/Login/IndexPB",
            "/MTXStore/AddG",
            "/MTXStore/AddG/Submit",
        ];

        private static readonly string[] LegacyRoutes =
        [
            "/AccountManagement/Create",
            "/AccountManagement/SetPlayerName",
            "/AccountManagement/SetPassword",
            "/AccountManagement/SetUserLevel",
            "/AccountManagement/SetFlag",
            "/AccountManagement/ClearFlag",
            "/ServerStatus",
            "/RegionReport",
            "/Metrics/Performance",
        ];

        internal static HashSet<string> GetRoutes(WebDeploymentProfile profile, bool enableWebApi, bool enableDashboard)
        {
            HashSet<string> routes = new(GameRoutes, StringComparer.OrdinalIgnoreCase);

            if (profile == WebDeploymentProfile.Legacy && enableWebApi)
            {
                routes.UnionWith(LegacyRoutes);

                if (enableDashboard)
                    routes.Add("/");
            }

            return routes;
        }
    }
}

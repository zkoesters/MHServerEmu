namespace MHServerEmu.WebFrontend.Tests
{
    public class WebFrontendRoutePolicyTests
    {
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

        [Theory]
        [InlineData(WebDeploymentProfile.Legacy)]
        [InlineData(WebDeploymentProfile.Portal)]
        public void GetRoutes_AlwaysIncludesGameLoginAliasesAndMtxRoutes(WebDeploymentProfile profile)
        {
            HashSet<string> routes = WebFrontendRoutePolicy.GetRoutes(profile, enableWebApi: true, enableDashboard: true);

            Assert.Contains("/Login/IndexPB", routes);
            Assert.Contains("/AuthServer/Login/IndexPB", routes);
            Assert.Contains("/MTXStore/AddG", routes);
            Assert.Contains("/MTXStore/AddG/Submit", routes);
            Assert.Contains("/authserver/login/indexpb", routes);
        }

        [Fact]
        public void GetRoutes_LegacyWithWebApiIncludesLegacyRoutesAndDashboard()
        {
            HashSet<string> routes = WebFrontendRoutePolicy.GetRoutes(WebDeploymentProfile.Legacy, enableWebApi: true, enableDashboard: true);

            Assert.All(LegacyRoutes, route => Assert.Contains(route, routes));
            Assert.Contains("/", routes);
        }

        [Fact]
        public void GetRoutes_LegacyWithoutWebApiExcludesLegacyRoutesAndDashboard()
        {
            HashSet<string> routes = WebFrontendRoutePolicy.GetRoutes(WebDeploymentProfile.Legacy, enableWebApi: false, enableDashboard: true);

            Assert.All(LegacyRoutes, route => Assert.DoesNotContain(route, routes));
            Assert.DoesNotContain("/", routes);
        }

        [Fact]
        public void GetRoutes_LegacyWithoutDashboardExcludesDashboard()
        {
            HashSet<string> routes = WebFrontendRoutePolicy.GetRoutes(WebDeploymentProfile.Legacy, enableWebApi: true, enableDashboard: false);

            Assert.DoesNotContain("/", routes);
        }

        [Fact]
        public void GetRoutes_PortalExcludesLegacyRoutesAndDashboard()
        {
            HashSet<string> routes = WebFrontendRoutePolicy.GetRoutes(WebDeploymentProfile.Portal, enableWebApi: true, enableDashboard: true);

            Assert.All(LegacyRoutes, route => Assert.DoesNotContain(route, routes));
            Assert.DoesNotContain("/", routes);
        }
    }
}

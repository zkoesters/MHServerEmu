using YamlDotNet.RepresentationModel;

namespace MHServerEmu.PortalBridge.Tests.Contract
{
    public class PortalBridgeContractTests
    {
        [Fact]
        public void VendoredContract_DefinesOnlyFoundationOperationsAndResponses()
        {
            YamlMappingNode contract = ParseContract();

            Assert.Equal(new[] { "openapi", "info", "servers", "security", "paths", "components" }, Keys(contract));
            AssertScalar(contract, "openapi", "3.1.0");
            AssertScalar(Map(contract, "info"), "title", "MHServerEmu Portal Bridge");
            AssertScalar(Map(contract, "info"), "version", "1.0.0");
            YamlMappingNode server = Assert.IsType<YamlMappingNode>(Assert.Single(Sequence(contract, "servers")));
            AssertScalar(server, "url", "/portal-bridge/v1");
            YamlMappingNode security = Assert.IsType<YamlMappingNode>(Assert.Single(Sequence(contract, "security")));
            Assert.Equal("portalHmac", Scalar(Assert.Single(security.Children.Keys)));

            YamlMappingNode paths = Map(contract, "paths");
            Assert.Equal(new[] { "/capabilities", "/health" }, Keys(paths));
            AssertOperation(Map(paths, "/capabilities"), "getCapabilities", "BridgeCapabilities");
            AssertOperation(Map(paths, "/health"), "getHealth", "BridgeHealth");
        }

        [Fact]
        public void VendoredContract_DefinesExactHmacSecurityScheme()
        {
            YamlMappingNode components = Map(ParseContract(), "components");
            YamlMappingNode securitySchemes = Map(components, "securitySchemes");
            Assert.Equal(new[] { "portalHmac" }, Keys(securitySchemes));

            YamlMappingNode scheme = Map(securitySchemes, "portalHmac");
            AssertScalar(scheme, "type", "apiKey");
            AssertScalar(scheme, "in", "header");
            AssertScalar(scheme, "name", "X-Portal-Signature");
            Assert.Equal(new[]
                {
                    "X-Portal-Contract-Version",
                    "X-Portal-Timestamp",
                    "X-Portal-Nonce",
                    "X-Portal-Body-SHA256",
                    "X-Portal-Operation-Id",
                    "X-Portal-Key-Id",
                    "X-Portal-Signature",
                }, Values(Sequence(scheme, "x-required-headers")));
        }

        [Fact]
        public void VendoredContract_DefinesExactResponseSchemas()
        {
            YamlMappingNode components = Map(ParseContract(), "components");
            YamlMappingNode responses = Map(components, "responses");
            Assert.Equal(new[] { "Unauthorized" }, Keys(responses));
            AssertSchemaReference(Map(Map(Map(Map(responses, "Unauthorized"), "content"), "application/problem+json"), "schema"), "Problem");

            YamlMappingNode schemas = Map(components, "schemas");
            Assert.Equal(new[] { "BridgeCapabilities", "BridgeHealth", "HealthStatus", "Problem" }, Keys(schemas));
            AssertCapabilitiesSchema(Map(schemas, "BridgeCapabilities"));
            AssertHealthSchema(Map(schemas, "BridgeHealth"));
            AssertHealthStatusSchema(Map(schemas, "HealthStatus"));
            AssertProblemSchema(Map(schemas, "Problem"));
        }

        [Fact]
        public void BuildMetadata_MatchesReleaseUpstreamBase()
        {
            Assert.True(IsLowercaseCommit(PortalBridgeBuildMetadata.UpstreamCommit));
            Assert.Equal("405d278054abfed20fc470800056df72d42f2b82", PortalBridgeBuildMetadata.UpstreamCommit);
        }

        private static void AssertOperation(YamlMappingNode path, string operationId, string responseSchema)
        {
            Assert.Equal(new[] { "get" }, Keys(path));
            YamlMappingNode operation = Map(path, "get");
            Assert.Equal(new[] { "operationId", "responses" }, Keys(operation));
            AssertScalar(operation, "operationId", operationId);

            YamlMappingNode responses = Map(operation, "responses");
            Assert.Equal(new[] { "200", "401" }, Keys(responses));
            AssertSchemaReference(Map(Map(Map(Map(responses, "200"), "content"), "application/json"), "schema"), responseSchema);
            AssertScalar(Map(responses, "401"), "$ref", "#/components/responses/Unauthorized");
        }

        private static void AssertCapabilitiesSchema(YamlMappingNode schema)
        {
            AssertObjectSchema(schema, new[]
                {
                    "contractVersion",
                    "emulatorVersion",
                    "upstreamCommit",
                    "gameBuild",
                    "snapshotSchemaVersion",
                    "serverInstanceId",
                    "capabilities",
                });
            YamlMappingNode properties = Map(schema, "properties");
            Assert.Equal(new[]
                {
                    "contractVersion",
                    "emulatorVersion",
                    "upstreamCommit",
                    "gameBuild",
                    "snapshotSchemaVersion",
                    "serverInstanceId",
                    "capabilities",
                }, Keys(properties));
            AssertScalar(Map(properties, "contractVersion"), "type", "string");
            AssertScalar(Map(properties, "contractVersion"), "const", "1.0");
            AssertStringWithMinimumLength(Map(properties, "emulatorVersion"));
            AssertScalar(Map(properties, "upstreamCommit"), "type", "string");
            AssertScalar(Map(properties, "upstreamCommit"), "pattern", "^[0-9a-f]{40}$");
            AssertStringWithMinimumLength(Map(properties, "gameBuild"));
            AssertScalar(Map(properties, "snapshotSchemaVersion"), "type", "integer");
            AssertScalar(Map(properties, "snapshotSchemaVersion"), "minimum", "1");
            AssertScalar(Map(properties, "serverInstanceId"), "type", "string");
            AssertScalar(Map(properties, "serverInstanceId"), "format", "uuid");

            YamlMappingNode capabilities = Map(properties, "capabilities");
            AssertScalar(capabilities, "type", "array");
            AssertScalar(capabilities, "uniqueItems", "true");
            AssertStringWithMinimumLength(Map(capabilities, "items"));
        }

        private static void AssertHealthSchema(YamlMappingNode schema)
        {
            AssertObjectSchema(schema, new[] { "status", "checkedAtUtc", "services" });
            YamlMappingNode properties = Map(schema, "properties");
            Assert.Equal(new[] { "status", "checkedAtUtc", "services" }, Keys(properties));
            AssertSchemaReference(Map(properties, "status"), "HealthStatus");
            AssertScalar(Map(properties, "checkedAtUtc"), "type", "string");
            AssertScalar(Map(properties, "checkedAtUtc"), "format", "date-time");
            YamlMappingNode services = Map(properties, "services");
            AssertScalar(services, "type", "object");
            AssertSchemaReference(Map(services, "additionalProperties"), "HealthStatus");
        }

        private static void AssertHealthStatusSchema(YamlMappingNode schema)
        {
            AssertScalar(schema, "type", "string");
            Assert.Equal(new[] { "healthy", "degraded", "unhealthy" }, Values(Sequence(schema, "enum")));
        }

        private static void AssertProblemSchema(YamlMappingNode schema)
        {
            AssertObjectSchema(schema, new[] { "code", "correlationId" });
            YamlMappingNode properties = Map(schema, "properties");
            Assert.Equal(new[] { "code", "correlationId" }, Keys(properties));
            AssertScalar(Map(properties, "code"), "type", "string");
            AssertScalar(Map(properties, "correlationId"), "type", "string");
            AssertScalar(Map(properties, "correlationId"), "format", "uuid");
        }

        private static void AssertObjectSchema(YamlMappingNode schema, string[] required)
        {
            AssertScalar(schema, "type", "object");
            AssertScalar(schema, "additionalProperties", "false");
            Assert.Equal(required, Values(Sequence(schema, "required")));
        }

        private static void AssertStringWithMinimumLength(YamlMappingNode schema)
        {
            AssertScalar(schema, "type", "string");
            AssertScalar(schema, "minLength", "1");
        }

        private static void AssertSchemaReference(YamlMappingNode schema, string name)
        {
            AssertScalar(schema, "$ref", $"#/components/schemas/{name}");
        }

        private static YamlMappingNode ParseContract()
        {
            string contractPath = Path.Combine(FindRepositoryRoot(), "contracts", "portal-bridge-v1.openapi.yaml");
            Assert.True(File.Exists(contractPath), $"Missing vendored contract: {contractPath}");

            YamlStream stream = new();
            using StringReader reader = new(File.ReadAllText(contractPath));
            stream.Load(reader);
            return Assert.IsType<YamlMappingNode>(Assert.Single(stream.Documents).RootNode);
        }

        private static YamlMappingNode Map(YamlMappingNode mapping, string key)
        {
            return Assert.IsType<YamlMappingNode>(Node(mapping, key));
        }

        private static YamlSequenceNode Sequence(YamlMappingNode mapping, string key)
        {
            return Assert.IsType<YamlSequenceNode>(Node(mapping, key));
        }

        private static YamlNode Node(YamlMappingNode mapping, string key)
        {
            return mapping.Children.Single(pair => Scalar(pair.Key) == key).Value;
        }

        private static string[] Keys(YamlMappingNode mapping)
        {
            return mapping.Children.Keys.Select(Scalar).ToArray();
        }

        private static string[] Values(YamlSequenceNode sequence)
        {
            return sequence.Children.Select(Scalar).ToArray();
        }

        private static string Scalar(YamlNode node)
        {
            return Assert.IsType<YamlScalarNode>(node).Value;
        }

        private static void AssertScalar(YamlMappingNode mapping, string key, string expected)
        {
            Assert.Equal(expected, Scalar(Node(mapping, key)));
        }

        private static bool IsLowercaseCommit(string value)
        {
            return value?.Length == 40 && value.All(character =>
                (character is >= '0' and <= '9') || (character is >= 'a' and <= 'f'));
        }

        private static string FindRepositoryRoot()
        {
            DirectoryInfo directory = new(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                    File.Exists(Path.Combine(directory.FullName, ".git")))
                    return directory.FullName;

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("Repository root was not found.");
        }
    }
}

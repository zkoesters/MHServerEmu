using YamlDotNet.RepresentationModel;

namespace MHServerEmu.PortalBridge.Tests.Contract
{
    public class PortalBridgeContractTests
    {
        [Fact]
        public void VendoredContract_DefinesFoundationAndAuthenticationOperationsAndResponses()
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
            Assert.Equal(new[] { "/capabilities", "/health", "/auth/register", "/auth/verify", "/auth/password" }, Keys(paths));
            AssertOperation(Map(paths, "/capabilities"), "getCapabilities", "BridgeCapabilities");
            AssertOperation(Map(paths, "/health"), "getHealth", "BridgeHealth");
            AssertAuthenticationOperation(Map(paths, "/auth/register"), "register", "RegisterRequest", true);
            AssertAuthenticationOperation(Map(paths, "/auth/verify"), "verify", "VerifyRequest", false);
            AssertChangePasswordOperation(Map(paths, "/auth/password"));
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
            Assert.Equal(new[] { "Unauthorized", "Conflict", "InvalidCredentials", "Unavailable" }, Keys(responses));
            AssertSchemaReference(Map(Map(Map(Map(responses, "Unauthorized"), "content"), "application/problem+json"), "schema"), "Problem");

            YamlMappingNode schemas = Map(components, "schemas");
            Assert.Equal(new[] { "RegisterRequest", "VerifyRequest", "PasswordChangeRequest", "EmulatorAccount", "BridgeCapabilities", "BridgeHealth", "HealthStatus", "Problem" }, Keys(schemas));
            AssertRegisterRequestSchema(Map(schemas, "RegisterRequest"));
            AssertVerifyRequestSchema(Map(schemas, "VerifyRequest"));
            AssertChangePasswordRequestSchema(Map(schemas, "PasswordChangeRequest"));
            AssertEmulatorAccountSchema(Map(schemas, "EmulatorAccount"));
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

        private static void AssertAuthenticationOperation(YamlMappingNode path, string operationId, string requestSchema, bool conflict)
        {
            Assert.Equal(new[] { "post" }, Keys(path));
            YamlMappingNode operation = Map(path, "post");
            Assert.Equal(new[] { "operationId", "requestBody", "responses" }, Keys(operation));
            AssertScalar(operation, "operationId", operationId);
            AssertSchemaReference(Map(Map(Map(Map(operation, "requestBody"), "content"), "application/json"), "schema"), requestSchema);

            YamlMappingNode responses = Map(operation, "responses");
            Assert.Equal(conflict ? new[] { "200", "401", "409", "503" } : new[] { "200", "401", "503" }, Keys(responses));
            AssertSchemaReference(Map(Map(Map(Map(responses, "200"), "content"), "application/json"), "schema"), "EmulatorAccount");
            AssertScalar(Map(responses, "401"), "$ref", "#/components/responses/InvalidCredentials");
            if (conflict)
                AssertScalar(Map(responses, "409"), "$ref", "#/components/responses/Conflict");
            AssertScalar(Map(responses, "503"), "$ref", "#/components/responses/Unavailable");
        }

        private static void AssertChangePasswordOperation(YamlMappingNode path)
        {
            Assert.Equal(new[] { "post" }, Keys(path));
            YamlMappingNode operation = Map(path, "post");
            Assert.Equal(new[] { "operationId", "requestBody", "responses" }, Keys(operation));
            AssertScalar(operation, "operationId", "changePassword");
            AssertSchemaReference(Map(Map(Map(Map(operation, "requestBody"), "content"), "application/json"), "schema"),
                "PasswordChangeRequest");

            YamlMappingNode responses = Map(operation, "responses");
            Assert.Equal(new[] { "204", "401", "503" }, Keys(responses));
            AssertScalar(Map(responses, "204"), "description", "Emulator password changed.");
            AssertScalar(Map(responses, "401"), "$ref", "#/components/responses/InvalidCredentials");
            AssertScalar(Map(responses, "503"), "$ref", "#/components/responses/Unavailable");
        }

        private static void AssertRegisterRequestSchema(YamlMappingNode schema)
        {
            AssertObjectSchema(schema, new[] { "email", "playerName", "password" });
            YamlMappingNode properties = Map(schema, "properties");
            AssertScalar(Map(properties, "email"), "format", "email");
            AssertScalar(Map(properties, "playerName"), "minLength", "1");
            AssertScalar(Map(properties, "playerName"), "maxLength", "16");
            AssertScalar(Map(properties, "password"), "writeOnly", "true");
        }

        private static void AssertVerifyRequestSchema(YamlMappingNode schema)
        {
            AssertObjectSchema(schema, new[] { "identifier", "password" });
            AssertScalar(Map(Map(schema, "properties"), "password"), "writeOnly", "true");
        }

        private static void AssertChangePasswordRequestSchema(YamlMappingNode schema)
        {
            AssertObjectSchema(schema, new[] { "identifier", "currentPassword", "newPassword" });
            YamlMappingNode properties = Map(schema, "properties");
            AssertScalar(Map(properties, "identifier"), "maxLength", "320");
            AssertScalar(Map(properties, "currentPassword"), "writeOnly", "true");
            AssertScalar(Map(properties, "newPassword"), "writeOnly", "true");
        }

        private static void AssertEmulatorAccountSchema(YamlMappingNode schema)
        {
            AssertObjectSchema(schema, new[] { "emulatorAccountId" });
            AssertStringWithMinimumLength(Map(Map(schema, "properties"), "emulatorAccountId"));
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

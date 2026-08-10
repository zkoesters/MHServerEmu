using System.Text.RegularExpressions;

namespace MHServerEmu.PortalBridge.Tests.Contract
{
    public class PortalBridgeContractTests
    {
        [Fact]
        public void VendoredContract_DefinesExactFoundationApi()
        {
            string contractPath = Path.Combine(FindRepositoryRoot(), "contracts", "portal-bridge-v1.openapi.yaml");

            Assert.True(File.Exists(contractPath), $"Missing vendored contract: {contractPath}");

            string contract = File.ReadAllText(contractPath);
            Assert.Equal(new[] { "getCapabilities", "getHealth" },
                Regex.Matches(contract, @"(?m)^ {6}operationId: (\S+)$").Select(match => match.Groups[1].Value));
            Assert.Equal(new[]
                {
                    "X-Portal-Contract-Version",
                    "X-Portal-Timestamp",
                    "X-Portal-Nonce",
                    "X-Portal-Body-SHA256",
                    "X-Portal-Operation-Id",
                    "X-Portal-Key-Id",
                    "X-Portal-Signature",
                },
                Regex.Matches(contract, @"(?m)^ {8}- (X-Portal-[^\r\n]+)$").Select(match => match.Groups[1].Value));
            string schemas = contract[(contract.IndexOf("\n  schemas:\n", StringComparison.Ordinal) + "\n  schemas:\n".Length)..];
            Assert.Equal(new[] { "BridgeCapabilities", "BridgeHealth", "HealthStatus", "Problem" },
                Regex.Matches(schemas, @"(?m)^ {4}([A-Za-z]+):$").Select(match => match.Groups[1].Value));
            Assert.Contains("const: '1.0'", contract, StringComparison.Ordinal);
        }

        [Fact]
        public void BuildMetadata_MatchesMasterUpstreamBase()
        {
            Assert.Matches("^[0-9a-f]{40}$", PortalBridgeBuildMetadata.UpstreamCommit);
            Assert.Equal("7ae81f6ba8816ad86c156c44b3284a5f271aa61f", PortalBridgeBuildMetadata.UpstreamCommit);
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

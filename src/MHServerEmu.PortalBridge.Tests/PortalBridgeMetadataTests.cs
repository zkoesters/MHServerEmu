namespace MHServerEmu.PortalBridge.Tests
{
    public class PortalBridgeMetadataTests
    {
        [Fact]
        public void UpstreamCommit_ReleaseBranch_MatchesApprovedBase()
        {
            Assert.Equal("405d278054abfed20fc470800056df72d42f2b82", PortalBridgeBuildMetadata.UpstreamCommit);
        }

        [Theory]
        [MemberData(nameof(InvalidUpstreamCommitMetadata))]
        public void ReadUpstreamCommit_InvalidMetadata_ThrowsOnlyWhenRead(System.Reflection.AssemblyMetadataAttribute[] attributes)
        {
            Assert.Throws<InvalidOperationException>(() => PortalBridgeBuildMetadata.ReadUpstreamCommit(attributes));
        }

        [Fact]
        public void Constructor_ValidValues_ExposesImmutableMetadata()
        {
            PortalBridgeMetadata metadata = new("1.0.2", new string('a', 40), "1.52.0.1700");

            Assert.Equal("1.0.2", metadata.EmulatorVersion);
            Assert.Equal(new string('a', 40), metadata.UpstreamCommit);
            Assert.Equal("1.52.0.1700", metadata.GameBuild);
        }

        [Theory]
        [InlineData("", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "1.52.0.1700")]
        [InlineData("1.0.2", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "1.52.0.1700")]
        [InlineData("1.0.2", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "1.52.0.1700")]
        [InlineData("1.0.2", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "")]
        public void Constructor_InvalidValues_ThrowsArgumentException(string emulatorVersion, string upstreamCommit, string gameBuild)
        {
            Assert.Throws<ArgumentException>(() => new PortalBridgeMetadata(emulatorVersion, upstreamCommit, gameBuild));
        }

        public static IEnumerable<object[]> InvalidUpstreamCommitMetadata()
        {
            yield return new object[] { Array.Empty<System.Reflection.AssemblyMetadataAttribute>() };
            yield return new object[]
            {
                new[]
                {
                    new System.Reflection.AssemblyMetadataAttribute("PortalBridgeUpstreamCommit", string.Empty),
                },
            };
            yield return new object[]
            {
                new[]
                {
                    new System.Reflection.AssemblyMetadataAttribute("PortalBridgeUpstreamCommit", new string('A', 40)),
                },
            };
            yield return new object[]
            {
                new[]
                {
                    new System.Reflection.AssemblyMetadataAttribute("PortalBridgeUpstreamCommit", new string('a', 40)),
                    new System.Reflection.AssemblyMetadataAttribute("PortalBridgeUpstreamCommit", new string('b', 40)),
                },
            };
        }
    }
}

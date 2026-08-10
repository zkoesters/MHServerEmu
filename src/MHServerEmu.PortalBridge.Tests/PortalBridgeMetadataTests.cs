namespace MHServerEmu.PortalBridge.Tests
{
    public class PortalBridgeMetadataTests
    {
        [Fact]
        public void UpstreamCommit_MasterBranch_MatchesApprovedBase()
        {
            Assert.Equal("7ae81f6ba8816ad86c156c44b3284a5f271aa61f", PortalBridgeBuildMetadata.UpstreamCommit);
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
    }
}

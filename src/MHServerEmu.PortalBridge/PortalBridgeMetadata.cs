namespace MHServerEmu.PortalBridge
{
    public sealed class PortalBridgeMetadata
    {
        public string EmulatorVersion { get; }
        public string UpstreamCommit { get; }
        public string GameBuild { get; }

        public PortalBridgeMetadata(string emulatorVersion, string upstreamCommit, string gameBuild)
        {
            if (string.IsNullOrWhiteSpace(emulatorVersion))
                throw new ArgumentException("Emulator version is required.", nameof(emulatorVersion));

            if (upstreamCommit?.Length != 40 || upstreamCommit.Any(character =>
                (character is < '0' or > '9') && (character is < 'a' or > 'f')))
                throw new ArgumentException("Upstream commit must be 40 lowercase hexadecimal characters.", nameof(upstreamCommit));

            if (string.IsNullOrWhiteSpace(gameBuild))
                throw new ArgumentException("Game build is required.", nameof(gameBuild));

            EmulatorVersion = emulatorVersion;
            UpstreamCommit = upstreamCommit;
            GameBuild = gameBuild;
        }
    }
}

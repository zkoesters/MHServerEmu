using MHServerEmu.Core.Helpers;

namespace MHServerEmu.Core.Tests.Helpers
{
    public class FileHelperTests
    {
        [Fact]
        public void ResolveDirectory_EmptyConfiguration_ReturnsFullFallbackPath()
        {
            string fallback = Path.Combine(Path.GetTempPath(), "mhserveremu-fallback");

            string result = FileHelper.ResolveDirectory(" ", fallback);

            Assert.Equal(Path.GetFullPath(fallback), result);
        }

        [Fact]
        public void ResolveDirectory_ConfiguredPath_ReturnsFullConfiguredPath()
        {
            string configured = Path.Combine(Path.GetTempPath(), "mhserveremu-runtime");

            string result = FileHelper.ResolveDirectory(configured, "/ignored");

            Assert.Equal(Path.GetFullPath(configured), result);
        }
    }
}

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

        [Fact]
        public void SaveTextFileToDirectory_NewRuntimeDirectory_CreatesFileWithContents()
        {
            string runtimeDirectory = Path.Combine(Path.GetTempPath(), $"mhserveremu-runtime-{Guid.NewGuid()}");
            const string fileName = "output.txt";
            const string text = "runtime output";

            try
            {
                Assert.False(Directory.Exists(runtimeDirectory));

                FileHelper.SaveTextFileToDirectory(runtimeDirectory, fileName, text);

                string filePath = Path.Combine(runtimeDirectory, fileName);
                Assert.True(Directory.Exists(runtimeDirectory));
                Assert.Equal(text, File.ReadAllText(filePath));
            }
            finally
            {
                if (Directory.Exists(runtimeDirectory))
                    Directory.Delete(runtimeDirectory, true);
            }
        }
    }
}

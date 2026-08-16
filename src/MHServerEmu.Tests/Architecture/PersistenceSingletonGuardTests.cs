using System.Text.RegularExpressions;

namespace MHServerEmu.Tests.Architecture
{
    public class PersistenceSingletonGuardTests
    {
        private static readonly Regex[] SingletonReferencePatterns =
        {
            new(@"IDBManager\s*\.Instance"),
            new(@"PlayerNameCache\s*\.Instance")
        };

        [Theory]
        [InlineData("IDBManager .Instance")]
        [InlineData("IDBManager\t.Instance")]
        [InlineData("IDBManager\n.Instance")]
        [InlineData("PlayerNameCache .Instance")]
        [InlineData("PlayerNameCache\t.Instance")]
        [InlineData("PlayerNameCache\n.Instance")]
        public void ContainsPersistenceSingleton_DetectsWhitespaceSeparatedSingletonReferences(string source)
        {
            Assert.True(ContainsPersistenceSingleton(source));
        }

        [Fact]
        public void ProductionCode_DoesNotUsePersistenceSingletons()
        {
            string repositoryRoot = RepositoryRoot.Find();
            string sourceDirectory = Path.Combine(repositoryRoot, "src");
            string[] matches = Directory.EnumerateFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories)
                .Where(path => path.Contains(".Tests", StringComparison.Ordinal) == false)
                .Where(path => ContainsPersistenceSingleton(File.ReadAllText(path)))
                .Select(path => Path.GetRelativePath(repositoryRoot, path))
                .ToArray();

            Assert.Empty(matches);
        }

        private static bool ContainsPersistenceSingleton(string source)
        {
            return SingletonReferencePatterns.Any(pattern => pattern.IsMatch(source));
        }
    }

    internal static class RepositoryRoot
    {
        public static string Find()
        {
            DirectoryInfo directory = new(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "MHServerEmu.sln")))
                    return directory.FullName;

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("Could not find repository root.");
        }
    }
}

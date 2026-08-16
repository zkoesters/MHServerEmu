namespace MHServerEmu.Tests.Architecture
{
    public class PersistenceSingletonGuardTests
    {
        [Fact]
        public void ProductionCode_DoesNotUsePersistenceSingletons()
        {
            string repositoryRoot = RepositoryRoot.Find();
            string sourceDirectory = Path.Combine(repositoryRoot, "src");
            string[] singletonTokens =
            {
                "IDBManager" + ".Instance",
                "PlayerNameCache" + ".Instance"
            };

            string[] matches = Directory.EnumerateFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories)
                .Where(path => path.Contains(".Tests", StringComparison.Ordinal) == false)
                .Where(path => singletonTokens.Any(token => File.ReadAllText(path).Contains(token, StringComparison.Ordinal)))
                .Select(path => Path.GetRelativePath(repositoryRoot, path))
                .ToArray();

            Assert.Empty(matches);
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

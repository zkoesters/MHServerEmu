using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MHServerEmu.Tests.Architecture
{
    public class PersistenceSingletonGuardTests
    {
        [Theory]
        [InlineData("IDBManager .Instance")]
        [InlineData("IDBManager\t.Instance")]
        [InlineData("IDBManager\n.Instance")]
        [InlineData("IDBManager/* legacy */.Instance")]
        [InlineData("IDBManager// legacy\n.Instance")]
        [InlineData("MHServerEmu.DatabaseAccess.IDBManager.Instance")]
        [InlineData("PlayerNameCache .Instance")]
        [InlineData("PlayerNameCache\t.Instance")]
        [InlineData("PlayerNameCache\n.Instance")]
        [InlineData("PlayerNameCache/* legacy */.Instance")]
        [InlineData("PlayerNameCache// legacy\n.Instance")]
        [InlineData("MHServerEmu.PlayerManagement.PlayerNameCache.Instance")]
        public void ContainsPersistenceSingleton_DetectsWhitespaceSeparatedSingletonReferences(string source)
        {
            string fixture = $"class Test {{ void Method() {{ {source}.Initialize(); }} }}";

            Assert.True(PersistenceSingletonGuard.ContainsForbiddenAccess(fixture));
        }

        [Theory]
        [InlineData("LeaderboardDatabase.Instance")]
        [InlineData("SQLiteLeaderboardDBManager /* comment */ . Instance")]
        [InlineData("using Leaderboards = MHServerEmu.Leaderboards.LeaderboardDatabase; class Test { void Method() { Leaderboards.Instance.Initialize(); } }")]
        [InlineData("using Store = MHServerEmu.DatabaseAccess.SQLite.SQLiteLeaderboardDBManager; class Test { void Method() { Store.Instance.Initialize(); } }")]
        public void ContainsForbiddenAccess_DetectsLeaderboardSingletons(string source)
        {
            string fixture = source.StartsWith("using ", StringComparison.Ordinal)
                ? source
                : $"class Test {{ void Method() {{ {source}.Initialize(); }} }}";

            Assert.True(PersistenceSingletonGuard.ContainsForbiddenAccess(fixture));
        }

        [Theory]
        [InlineData("new SQLiteLeaderboardDBManager(\"leaderboards.db\")")]
        [InlineData("SQLiteLeaderboardDBManager store")]
        [InlineData("new SQLiteLeaderboardDBManager /* concrete */ (\"leaderboards.db\")")]
        [InlineData("using Store = MHServerEmu.DatabaseAccess.SQLite.SQLiteLeaderboardDBManager; class Test { void Method() { new Store(\"leaderboards.db\"); } }")]
        public void ContainsForbiddenConcreteStore_DetectsConcreteLeaderboardStores(string source)
        {
            string fixture = source.StartsWith("using ", StringComparison.Ordinal)
                ? source
                : $"class Test {{ void Method() {{ {source}; }} }}";

            Assert.True(PersistenceSingletonGuard.ContainsForbiddenConcreteStore(fixture));
        }

        [Fact]
        public void ContainsPersistenceSingleton_DetectsAccessAfterUrlString()
        {
            const string Source = "class Test { void Method() { var url = \"https://x\"; IDBManager.Instance.Initialize(); } }";

            Assert.True(PersistenceSingletonGuard.ContainsForbiddenAccess(Source));
        }

        [Fact]
        public void ContainsPersistenceSingleton_IgnoresStringLiteral()
        {
            const string Source = "class Test { void Method() { var singleton = \"IDBManager.Instance\"; } }";

            Assert.False(PersistenceSingletonGuard.ContainsForbiddenAccess(Source));
        }

        [Fact]
        public void ContainsPersistenceSingleton_DetectsAliasedSingletonAccess()
        {
            const string Source = "using Db = MHServerEmu.DatabaseAccess.IDBManager; class Test { void Method() { Db.Instance.Initialize(); } }";

            Assert.True(PersistenceSingletonGuard.ContainsForbiddenAccess(Source));
        }

        [Fact]
        public void ContainsPersistenceSingleton_IgnoresUnrelatedAlias()
        {
            const string Source = "using Db = OtherDatabase; class Test { void Method() { Db.Instance.Initialize(); } }";

            Assert.False(PersistenceSingletonGuard.ContainsForbiddenAccess(Source));
        }

        [Fact]
        public void ContainsPersistenceSingleton_DetectsNamespaceScopedAlias()
        {
            const string Source = "namespace Test { using Db = MHServerEmu.DatabaseAccess.IDBManager; class Test { void Method() { Db.Instance.Initialize(); } } }";

            Assert.True(PersistenceSingletonGuard.ContainsForbiddenAccess(Source));
        }

        [Fact]
        public void ContainsPersistenceSingleton_IgnoresNamespaceScopedUnrelatedAlias()
        {
            const string Source = "namespace Test { using Db = OtherDatabase; class Test { void Method() { Db.Instance.Initialize(); } } }";

            Assert.False(PersistenceSingletonGuard.ContainsForbiddenAccess(Source));
        }

        [Fact]
        public void ProductionCode_DoesNotUsePersistenceSingletons()
        {
            string repositoryRoot = RepositoryRoot.Find();
            string sourceDirectory = Path.Combine(repositoryRoot, "src");
            string[] matches = Directory.EnumerateFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories)
                .Where(path => path.Contains(".Tests", StringComparison.Ordinal) == false)
                .Where(path => PersistenceSingletonGuard.ContainsForbiddenAccess(File.ReadAllText(path)))
                .Select(path => Path.GetRelativePath(repositoryRoot, path))
                .ToArray();

            Assert.Empty(matches);
        }

        [Fact]
        public void ProductionCode_DoesNotConstructConcreteLeaderboardStoresOutsideComposition()
        {
            string repositoryRoot = RepositoryRoot.Find();
            string sourceDirectory = Path.Combine(repositoryRoot, "src");
            string[] matches = Directory.EnumerateFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories)
                .Where(path => path.Contains(".Tests", StringComparison.Ordinal) == false)
                .Where(path => IsConcreteStoreProviderOrCompositionPath(repositoryRoot, path) == false)
                .Where(path => PersistenceSingletonGuard.ContainsForbiddenConcreteStore(File.ReadAllText(path)))
                .Select(path => Path.GetRelativePath(repositoryRoot, path))
                .ToArray();

            Assert.Empty(matches);
        }

        private static bool IsConcreteStoreProviderOrCompositionPath(string repositoryRoot, string path)
        {
            string relativePath = Path.GetRelativePath(repositoryRoot, path);
            return relativePath is "src/MHServerEmu/Persistence/PersistenceComposition.cs"
                or "src/MHServerEmu.DatabaseAccess.PostgreSQL/PostgreSQLPersistenceFacade.cs"
                or "src/MHServerEmu.DatabaseAccess.PostgreSQL/PostgreSQLLeaderboardStore.cs"
                or "src/MHServerEmu.DatabaseAccess/SQLite/SQLiteLeaderboardDBManager.cs";
        }
    }

    internal static class PersistenceSingletonGuard
    {
        private static readonly HashSet<string> SingletonTypes = new(StringComparer.Ordinal)
        {
            "IDBManager", "PlayerNameCache", "LeaderboardDatabase", "SQLiteLeaderboardDBManager",
        };

        private static readonly HashSet<string> ConcreteStoreTypes = new(StringComparer.Ordinal)
        {
            "SQLiteLeaderboardDBManager", "PostgreSQLLeaderboardStore",
        };

        public static bool ContainsForbiddenAccess(string source)
        {
            CompilationUnitSyntax compilationUnit = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
            HashSet<string> singletonAliases = GetAliases(compilationUnit, SingletonTypes);

            return compilationUnit.DescendantNodes()
                .OfType<MemberAccessExpressionSyntax>()
                .Any(memberAccess => memberAccess.Name.Identifier.ValueText == "Instance"
                    && (SingletonTypes.Contains(GetTerminalReceiverIdentifier(memberAccess.Expression))
                        || singletonAliases.Contains(GetTerminalReceiverIdentifier(memberAccess.Expression))));
        }

        public static bool ContainsForbiddenConcreteStore(string source)
        {
            CompilationUnitSyntax compilationUnit = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
            HashSet<string> concreteStoreAliases = GetAliases(compilationUnit, ConcreteStoreTypes);

            return compilationUnit.DescendantNodes()
                .OfType<IdentifierNameSyntax>()
                .Any(identifier => ConcreteStoreTypes.Contains(identifier.Identifier.ValueText)
                    || concreteStoreAliases.Contains(identifier.Identifier.ValueText));
        }

        private static HashSet<string> GetAliases(CompilationUnitSyntax compilationUnit, HashSet<string> targetTypes)
        {
            return compilationUnit.DescendantNodes().OfType<UsingDirectiveSyntax>()
                .Where(usingDirective => usingDirective.Alias != null
                    && targetTypes.Contains(GetTerminalReceiverIdentifier(usingDirective.Name)))
                .Select(usingDirective => usingDirective.Alias.Name.Identifier.ValueText)
                .ToHashSet(StringComparer.Ordinal);
        }

        private static string GetTerminalReceiverIdentifier(SyntaxNode receiver)
        {
            return receiver switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                AliasQualifiedNameSyntax aliasQualifiedName => aliasQualifiedName.Name.Identifier.ValueText,
                QualifiedNameSyntax qualifiedName => qualifiedName.Right.Identifier.ValueText,
                _ => null
            };
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

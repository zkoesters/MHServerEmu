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

            Assert.True(ContainsPersistenceSingleton(fixture));
        }

        [Fact]
        public void ContainsPersistenceSingleton_DetectsAccessAfterUrlString()
        {
            const string Source = "class Test { void Method() { var url = \"https://x\"; IDBManager.Instance.Initialize(); } }";

            Assert.True(ContainsPersistenceSingleton(Source));
        }

        [Fact]
        public void ContainsPersistenceSingleton_IgnoresStringLiteral()
        {
            const string Source = "class Test { void Method() { var singleton = \"IDBManager.Instance\"; } }";

            Assert.False(ContainsPersistenceSingleton(Source));
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
            return CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes()
                .OfType<MemberAccessExpressionSyntax>()
                .Any(memberAccess => memberAccess.Name.Identifier.ValueText == "Instance"
                    && GetTerminalReceiverIdentifier(memberAccess.Expression) is "IDBManager" or "PlayerNameCache");
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

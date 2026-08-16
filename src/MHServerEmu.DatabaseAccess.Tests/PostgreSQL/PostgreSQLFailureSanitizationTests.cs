using MHServerEmu.DatabaseAccess.PostgreSQL;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MHServerEmu.DatabaseAccess.Tests.PostgreSQL
{
    public class PostgreSQLFailureSanitizationTests
    {
        [Fact]
        public void Failure_ContainsOnlySafeMetadata()
        {
            PostgreSQLPersistenceFailure failure = new("DatabaseUnavailable", "ConnectivityCheck", "08001", "12", "account-7");

            Assert.Equal("DatabaseUnavailable", failure.Code);
            Assert.Equal("ConnectivityCheck", failure.Operation);
            Assert.Equal("08001", failure.SqlState);
            Assert.Equal("12", failure.MigrationIdentity);
            Assert.Equal("account-7", failure.EntityId);
            Assert.DoesNotContain("Exception", failure.ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void ProviderSources_DoNotUseLogger()
        {
            DirectoryInfo directory = new(AppContext.BaseDirectory);
            while (directory != null && Directory.Exists(Path.Combine(directory.FullName, "MHServerEmu.DatabaseAccess.PostgreSQL")) == false)
                directory = directory.Parent;

            Assert.NotNull(directory);
            string providerDirectory = Path.Combine(directory.FullName, "MHServerEmu.DatabaseAccess.PostgreSQL");
            string[] unsafeSources = Directory.GetFiles(providerDirectory, "*.cs", SearchOption.AllDirectories)
                .Where(path => ContainsProviderLoggerReference(File.ReadAllText(path)))
                .ToArray();

            Assert.Empty(unsafeSources);
        }

        [Theory]
        [InlineData("class Test { void M(System.Exception e) { Logger.Error(e.ToString()); } }")]
        [InlineData("class Test { void M(System.Exception e) { Logger.Warn(e.Message); } }")]
        [InlineData("class Test { void M(System.Exception e) { Logger.Error($\"{e}\"); } }")]
        [InlineData("class Test { void M(System.Exception exception) { Logger.ErrorException(exception, \"failure\"); } }")]
        [InlineData("class Test { void M(System.Exception dbError) { Logger.Error(dbError.Message); } }")]
        [InlineData("class Test { void M(object lastFailure) { Logger.Warn(lastFailure.ToString()); } }")]
        [InlineData("class Test { void M() { Logger.Info(\"safe\"); } }")]
        public void SourceGuard_DetectsLoggerReferences(string source)
        {
            Assert.True(ContainsProviderLoggerReference(source));
        }

        [Fact]
        public void SourceGuard_DetectsLoggerIdentifierReferences()
        {
            const string Source = "class Test { void M() { var logger = Logger; } }";

            Assert.True(ContainsProviderLoggerReference(Source));
        }

        [Fact]
        public void SourceGuard_AllowsNonLoggerMethods()
        {
            const string Source = "class Test { void M() { Diagnostics.Info(\"safe\"); } }";

            Assert.False(ContainsProviderLoggerReference(Source));
        }

        private static bool ContainsProviderLoggerReference(string source)
        {
            var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
            return root.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(IsLoggerInvocation)
                || root.DescendantNodes().OfType<IdentifierNameSyntax>().Any(identifier => identifier.Identifier.ValueText == "Logger");
        }

        private static bool IsLoggerInvocation(InvocationExpressionSyntax invocation)
        {
            return invocation.Expression is MemberAccessExpressionSyntax memberAccess
                && GetTerminalIdentifier(memberAccess.Expression) == "Logger";
        }

        private static string GetTerminalIdentifier(ExpressionSyntax expression)
        {
            return expression switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
                _ => null,
            };
        }
    }
}

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
        public void ProviderSources_DoNotLogRawExceptions()
        {
            DirectoryInfo directory = new(AppContext.BaseDirectory);
            while (directory != null && Directory.Exists(Path.Combine(directory.FullName, "MHServerEmu.DatabaseAccess.PostgreSQL")) == false)
                directory = directory.Parent;

            Assert.NotNull(directory);
            string providerDirectory = Path.Combine(directory.FullName, "MHServerEmu.DatabaseAccess.PostgreSQL");
            string[] unsafeSources = Directory.GetFiles(providerDirectory, "*.cs", SearchOption.AllDirectories)
                .Where(path => ContainsUnsafeLoggerInvocation(File.ReadAllText(path)))
                .ToArray();

            Assert.Empty(unsafeSources);
        }

        [Theory]
        [InlineData("class Test { void M(System.Exception e) { Logger.Error(e.ToString()); } }")]
        [InlineData("class Test { void M(System.Exception e) { Logger.Warn(e.Message); } }")]
        [InlineData("class Test { void M(System.Exception e) { Logger.Error($\"{e}\"); } }")]
        [InlineData("class Test { void M(System.Exception exception) { Logger.ErrorException(exception, \"failure\"); } }")]
        public void SourceGuard_DetectsUnsafeLoggerExpressions(string source)
        {
            Assert.True(ContainsUnsafeLoggerInvocation(source));
        }

        [Fact]
        public void SourceGuard_AllowsPersistenceFailureLogging()
        {
            const string Source = "class Test { void M(PostgreSQLPersistenceFailure failure) { Logger.Error(failure); } }";

            Assert.False(ContainsUnsafeLoggerInvocation(Source));
        }

        private static bool ContainsUnsafeLoggerInvocation(string source)
        {
            return CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot().DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Any(IsUnsafeLoggerInvocation);
        }

        private static bool IsUnsafeLoggerInvocation(InvocationExpressionSyntax invocation)
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess
                || memberAccess.Expression is not IdentifierNameSyntax receiver
                || receiver.Identifier.ValueText != "Logger")
                return false;

            if (memberAccess.Name.Identifier.ValueText.EndsWith("Exception", StringComparison.Ordinal))
                return true;

            return invocation.ArgumentList.Arguments.Any(argument => argument.Expression.DescendantNodesAndSelf()
                .OfType<IdentifierNameSyntax>()
                .Any(identifier => identifier.Identifier.ValueText is "e" or "exception"));
        }
    }
}

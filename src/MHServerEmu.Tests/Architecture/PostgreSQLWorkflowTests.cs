namespace MHServerEmu.Tests.Architecture
{
    public class PostgreSQLWorkflowTests
    {
        [Fact]
        public void PostgreSQLIntegrationWorkflow_UsesUniqueProjectTrxFilesAndValidatesBoth()
        {
            string workflow = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), ".github", "workflows", "verify.yml"));

            Assert.Contains("dotnet test src/MHServerEmu.DatabaseAccess.Tests/MHServerEmu.DatabaseAccess.Tests.csproj", workflow, StringComparison.Ordinal);
            Assert.Contains("dotnet test src/MHServerEmu.Tests/MHServerEmu.Tests.csproj", workflow, StringComparison.Ordinal);
            Assert.DoesNotContain("dotnet test MHServerEmu.sln --configuration Release --no-build -p:Platform=x64", workflow, StringComparison.Ordinal);
            Assert.Contains("artifacts/postgresql/DatabaseAccess.trx", workflow, StringComparison.Ordinal);
            Assert.Contains("artifacts/postgresql/Activation.trx", workflow, StringComparison.Ordinal);
            Assert.Contains("foreach ($resultFile in $resultFiles)", workflow, StringComparison.Ordinal);
            Assert.Contains("$counters.total -eq 0", workflow, StringComparison.Ordinal);
            Assert.Contains("$counters.executed -eq 0", workflow, StringComparison.Ordinal);
            Assert.Contains("$counters.notExecuted -ne 0", workflow, StringComparison.Ordinal);
        }
    }
}

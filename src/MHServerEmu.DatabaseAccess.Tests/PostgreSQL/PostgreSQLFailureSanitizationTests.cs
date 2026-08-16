using MHServerEmu.DatabaseAccess.PostgreSQL;

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
        public void ProviderSources_DoNotUseExceptionLoggingOrInnerExceptions()
        {
            DirectoryInfo directory = new(AppContext.BaseDirectory);
            while (directory != null && Directory.Exists(Path.Combine(directory.FullName, "MHServerEmu.DatabaseAccess.PostgreSQL")) == false)
                directory = directory.Parent;

            Assert.NotNull(directory);
            string providerDirectory = Path.Combine(directory.FullName, "MHServerEmu.DatabaseAccess.PostgreSQL");
            string source = string.Join(Environment.NewLine, Directory.GetFiles(providerDirectory, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText));

            Assert.DoesNotContain("Logger.ErrorException(", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Logger.WarnException(", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Logger.FatalException(", source, StringComparison.Ordinal);
            Assert.DoesNotContain("InnerException", source, StringComparison.Ordinal);
        }
    }
}

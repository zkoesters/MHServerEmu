using FluentMigrator.Runner;
using FluentMigrator.Runner.Processors.Postgres;

namespace MHServerEmu.DatabaseAccess.Extensions;

public static class MigrationRunnerExtensions
{
    /// <summary>
    /// Checks if any migrations have been applied by determining whether the VersionInfo table
    /// exists and contains data.
    /// </summary>
    /// <param name="runner">The migration runner instance used to check the migration status.</param>
    /// <returns><c>true</c> if at least one migration has been applied; otherwise, <c>false</c>.</returns>
    public static bool HasMigrationsApplied(this IMigrationRunner runner)
    {
        // Check if the VersionInfo table exists and has any data
        var processor = runner.Processor;

        var exists = processor.TableExists(null, "VersionInfo");

        if (!exists)
        {
            return false;
        }

        // Check if any rows are present in the VersionInfo table
        var count = processor.ReadTableData(null, "VersionInfo").Tables[0].Rows.Count;
        return count > 0;
    }
}
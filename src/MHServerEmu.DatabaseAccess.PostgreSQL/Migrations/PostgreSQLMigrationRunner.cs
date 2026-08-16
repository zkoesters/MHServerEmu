using System.Diagnostics;
using Npgsql;

namespace MHServerEmu.DatabaseAccess.PostgreSQL.Migrations
{
    internal sealed class PostgreSQLMigrationRunner
    {
        private const string MigrationTimeoutCode = "migration_timeout";
        private const string MigrationLockTimeoutCode = "migration_lock_timeout";
        private readonly NpgsqlDataSource _dataSource;
        private readonly PostgreSQLMigrationCatalog _catalog;
        private readonly TimeSpan _migrationTimeout;
        private readonly TimeSpan _migrationLockTimeout;

        internal PostgreSQLMigrationRunner(NpgsqlDataSource dataSource, PostgreSQLMigrationCatalog catalog, TimeSpan migrationTimeout, TimeSpan migrationLockTimeout)
        {
            _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            if (migrationTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(migrationTimeout));
            if (migrationLockTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(migrationLockTimeout));

            _migrationTimeout = migrationTimeout;
            _migrationLockTimeout = migrationLockTimeout;
        }

        internal async Task<PostgreSQLMigrationResult> RunAsync(CancellationToken cancellationToken = default)
        {
            PostgreSQLOperationDeadline deadline = new(_migrationTimeout);
            PostgreSQLMigration currentMigration = null;
            try
            {
                await using NpgsqlConnection connection = await OpenConnectionAsync(deadline, cancellationToken);
                return await RunAsync(connection, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return PostgreSQLMigrationResult.Failed(new PostgreSQLPersistenceFailure("MigrationFailed", "MigrationRun", migrationIdentity: MigrationIdentity(currentMigration)));
            }
        }

        internal async Task<PostgreSQLMigrationResult> RunAsync(NpgsqlConnection connection, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(connection);
            PostgreSQLOperationDeadline deadline = new(_migrationTimeout);
            PostgreSQLMigration currentMigration = null;
            try
            {
                await using NpgsqlTransaction transaction = await BeginTransactionAsync(connection, deadline, cancellationToken);
                await ConfigureTimeoutsAsync(connection, transaction, deadline, cancellationToken);
                await WaitForAdvisoryLockAsync(connection, transaction, deadline, cancellationToken);

                IReadOnlyList<AppliedMigration> history = await ReadHistoryAsync(connection, transaction, deadline, cancellationToken);
                PostgreSQLPersistenceFailure historyFailure = ValidateHistory(history);
                if (historyFailure != null)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    return PostgreSQLMigrationResult.Failed(historyFailure);
                }

                int appliedMigrationCount = 0;
                foreach (PostgreSQLMigration migration in _catalog.Migrations.Skip(history.Count))
                {
                    currentMigration = migration;
                    Stopwatch stopwatch = Stopwatch.StartNew();
                    await ExecuteNonQueryAsync(connection, transaction, migration.Sql, deadline, cancellationToken);
                    stopwatch.Stop();
                    await RecordMigrationAsync(connection, transaction, migration, stopwatch.ElapsedMilliseconds, deadline, cancellationToken);
                    appliedMigrationCount++;
                }

                await transaction.CommitAsync(cancellationToken);
                return PostgreSQLMigrationResult.Success(appliedMigrationCount);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (MigrationRunnerException exception)
            {
                return PostgreSQLMigrationResult.Failed(new PostgreSQLPersistenceFailure(exception.Code, "MigrationRun", migrationIdentity: MigrationIdentity(currentMigration)));
            }
            catch (PostgresException exception)
            {
                return PostgreSQLMigrationResult.Failed(new PostgreSQLPersistenceFailure("MigrationFailed", "MigrationRun", exception.SqlState, MigrationIdentity(currentMigration)));
            }
            catch (Exception)
            {
                return PostgreSQLMigrationResult.Failed(new PostgreSQLPersistenceFailure("MigrationFailed", "MigrationRun", migrationIdentity: MigrationIdentity(currentMigration)));
            }
        }

        private async Task<NpgsqlConnection> OpenConnectionAsync(PostgreSQLOperationDeadline deadline, CancellationToken cancellationToken)
        {
            using CancellationTokenSource source = deadline.CreateCancellationSource(cancellationToken);
            return await _dataSource.OpenConnectionAsync(source.Token);
        }

        private static async Task<NpgsqlTransaction> BeginTransactionAsync(NpgsqlConnection connection, PostgreSQLOperationDeadline deadline, CancellationToken cancellationToken)
        {
            using CancellationTokenSource source = deadline.CreateCancellationSource(cancellationToken);
            return await connection.BeginTransactionAsync(source.Token);
        }

        private async Task ConfigureTimeoutsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, PostgreSQLOperationDeadline deadline, CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = new("SELECT set_config('statement_timeout', @statementTimeout, true); SELECT set_config('lock_timeout', @lockTimeout, true)", connection, transaction)
            {
                CommandTimeout = deadline.RemainingCommandTimeoutSeconds,
            };
            command.Parameters.AddWithValue("statementTimeout", ToMilliseconds(deadline.Remaining));
            command.Parameters.AddWithValue("lockTimeout", ToMilliseconds(deadline.Remaining));
            using CancellationTokenSource source = deadline.CreateCancellationSource(cancellationToken);
            await command.ExecuteNonQueryAsync(source.Token);
        }

        private async Task WaitForAdvisoryLockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, PostgreSQLOperationDeadline migrationDeadline, CancellationToken cancellationToken)
        {
            PostgreSQLOperationDeadline lockDeadline = new(_migrationLockTimeout);
            while (true)
            {
                PostgreSQLOperationDeadline activeDeadline = GetEarlierAdvisoryLockDeadline(migrationDeadline, lockDeadline, out string timeoutCode);
                try
                {
                    await using NpgsqlCommand command = new("SELECT pg_try_advisory_xact_lock(@namespace, @id)", connection, transaction)
                    {
                        CommandTimeout = activeDeadline.RemainingCommandTimeoutSeconds,
                    };
                    command.Parameters.AddWithValue("namespace", Locking.PostgreSQLAdvisoryKeys.Namespace);
                    command.Parameters.AddWithValue("id", Locking.PostgreSQLAdvisoryKeys.MigrationResource);
                    using CancellationTokenSource source = activeDeadline.CreateCancellationSource(cancellationToken);
                    if ((bool)await command.ExecuteScalarAsync(source.Token))
                        return;
                }
                catch (TimeoutException)
                {
                    throw new MigrationRunnerException(GetAdvisoryLockTimeoutCode(migrationDeadline.Remaining, lockDeadline.Remaining));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested == false)
                {
                    throw new MigrationRunnerException(GetAdvisoryLockTimeoutCode(migrationDeadline.Remaining, lockDeadline.Remaining));
                }

                if (activeDeadline.Remaining == TimeSpan.Zero)
                    throw new MigrationRunnerException(timeoutCode);

                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(50, activeDeadline.Remaining.TotalMilliseconds)), cancellationToken);
            }
        }

        internal static string GetAdvisoryLockTimeoutCode(TimeSpan migrationRemaining, TimeSpan migrationLockRemaining)
        {
            return migrationRemaining <= migrationLockRemaining ? MigrationTimeoutCode : MigrationLockTimeoutCode;
        }

        private static PostgreSQLOperationDeadline GetEarlierAdvisoryLockDeadline(PostgreSQLOperationDeadline migrationDeadline, PostgreSQLOperationDeadline lockDeadline, out string timeoutCode)
        {
            timeoutCode = GetAdvisoryLockTimeoutCode(migrationDeadline.Remaining, lockDeadline.Remaining);
            return timeoutCode == MigrationTimeoutCode ? migrationDeadline : lockDeadline;
        }

        private async Task<IReadOnlyList<AppliedMigration>> ReadHistoryAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, PostgreSQLOperationDeadline deadline, CancellationToken cancellationToken)
        {
            object relation = await ExecuteScalarAsync(connection, transaction, "SELECT to_regclass('mhserveremu.schema_migrations')", deadline, cancellationToken);
            if (relation == null || relation is DBNull)
                return Array.Empty<AppliedMigration>();

            await using NpgsqlCommand command = new("SELECT version, name, checksum FROM mhserveremu.schema_migrations ORDER BY version", connection, transaction)
            {
                CommandTimeout = deadline.RemainingCommandTimeoutSeconds,
            };
            using CancellationTokenSource source = deadline.CreateCancellationSource(cancellationToken);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(source.Token);
            List<AppliedMigration> history = new();
            while (await reader.ReadAsync(source.Token))
                history.Add(new AppliedMigration(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
            return history;
        }

        private PostgreSQLPersistenceFailure ValidateHistory(IReadOnlyList<AppliedMigration> history)
        {
            if (history.Count > _catalog.Migrations.Count)
                return new PostgreSQLPersistenceFailure("FutureMigration", "MigrationHistory");

            for (int index = 0; index < history.Count; index++)
            {
                AppliedMigration applied = history[index];
                PostgreSQLMigration expected = _catalog.Migrations[index];
                if (applied.Version > expected.Version)
                    return new PostgreSQLPersistenceFailure("FutureMigration", "MigrationHistory");
                if (applied.Version != expected.Version || applied.Name != expected.Name || applied.Checksum != expected.Checksum)
                    return new PostgreSQLPersistenceFailure("MigrationHistoryMismatch", "MigrationHistory", migrationIdentity: $"{applied.Version}_{applied.Name}");
            }

            return null;
        }

        private async Task RecordMigrationAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, PostgreSQLMigration migration, long durationMilliseconds, PostgreSQLOperationDeadline deadline, CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = new("INSERT INTO mhserveremu.schema_migrations (version, name, checksum, application_version, applied_at_utc, duration_ms) VALUES (@version, @name, @checksum, @applicationVersion, now(), @durationMilliseconds)", connection, transaction)
            {
                CommandTimeout = deadline.RemainingCommandTimeoutSeconds,
            };
            command.Parameters.AddWithValue("version", migration.Version);
            command.Parameters.AddWithValue("name", migration.Name);
            command.Parameters.AddWithValue("checksum", migration.Checksum);
            command.Parameters.AddWithValue("applicationVersion", GetType().Assembly.GetName().Version?.ToString() ?? "unknown");
            command.Parameters.AddWithValue("durationMilliseconds", checked((int)durationMilliseconds));
            using CancellationTokenSource source = deadline.CreateCancellationSource(cancellationToken);
            await command.ExecuteNonQueryAsync(source.Token);
        }

        private static async Task ExecuteNonQueryAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, PostgreSQLOperationDeadline deadline, CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = new(sql, connection, transaction)
            {
                CommandTimeout = deadline.RemainingCommandTimeoutSeconds,
            };
            using CancellationTokenSource source = deadline.CreateCancellationSource(cancellationToken);
            await command.ExecuteNonQueryAsync(source.Token);
        }

        private static async Task<object> ExecuteScalarAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, PostgreSQLOperationDeadline deadline, CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = new(sql, connection, transaction)
            {
                CommandTimeout = deadline.RemainingCommandTimeoutSeconds,
            };
            using CancellationTokenSource source = deadline.CreateCancellationSource(cancellationToken);
            return await command.ExecuteScalarAsync(source.Token);
        }

        private static string ToMilliseconds(TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero)
                throw new MigrationRunnerException("MigrationTimeout");
            return $"{Math.Max(1, (int)Math.Ceiling(timeout.TotalMilliseconds))}ms";
        }

        private static string MigrationIdentity(PostgreSQLMigration migration)
        {
            return migration == null ? null : $"{migration.Version}_{migration.Name}";
        }

        private sealed class AppliedMigration
        {
            public AppliedMigration(int version, string name, string checksum)
            {
                Version = version;
                Name = name;
                Checksum = checksum;
            }

            public int Version { get; }
            public string Name { get; }
            public string Checksum { get; }
        }

        private sealed class MigrationRunnerException : Exception
        {
            public MigrationRunnerException(string code)
            {
                Code = code;
            }

            public string Code { get; }
        }
    }
}

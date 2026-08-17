using MHServerEmu.Core.Config;
using MHServerEmu.Core.Network;
using MHServerEmu.DatabaseAccess;
using MHServerEmu.DatabaseAccess.Persistence;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Leaderboards;
using MHServerEmu.Persistence;
using Npgsql;

namespace MHServerEmu.Tests
{
    public sealed class PostgreSQLActivationFactAttribute : FactAttribute
    {
        public PostgreSQLActivationFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MHSERVEREMU_POSTGRESQL_TEST_ADMIN_CONNECTION_STRING")))
                Skip = "MHSERVEREMU_POSTGRESQL_TEST_ADMIN_CONNECTION_STRING is not configured.";
        }
    }

    public class PostgreSQLActivationTests
    {
        [PostgreSQLActivationFact]
        [Trait("Category", "PostgreSQLIntegration")]
        public async Task RunAsync_TemporaryOverride_InitializesPersistenceAndLeaderboardsBeforeSyntheticSystems()
        {
            await using ActivationDatabase database = await ActivationDatabase.CreateAsync();
            string root = Path.Combine(Path.GetTempPath(), $"mhserveremu-activation-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                string overridePath = Path.Combine(root, "ConfigOverride.ini");
                await File.WriteAllTextAsync(overridePath, $"[Persistence]\nProvider=PostgreSQL\n\n[PostgreSQL]\nConnectionString={database.ConnectionString}\n");
                if (OperatingSystem.IsLinux())
                    SetOwnerOnlyPermissions(overridePath);
                ConfigManager configuration = new(Path.Combine(root, "Config.ini"), overridePath);
                string schedulePath = Path.Combine(root, "leaderboards.json");
                await File.WriteAllTextAsync(schedulePath, "[{ \"LeaderboardId\": 101, \"IsEnabled\": true, \"StartTime\": \"2026-01-01T00:00:00Z\", \"MaxResetCount\": 0 }]");
                TaskCompletionSource<string> consoleRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
                PersistenceRuntime runtime = null;
                bool leaderboardInitialized = false;
                bool syntheticGameSystemsInvoked = false;
                ServerStartupDependencies dependencies = new(
                    async (fatalCallback, cancellationToken) => runtime = await PersistenceComposition.CreateAsync(configuration, fatalCallback, cancellationToken),
                    () =>
                    {
                        LeaderboardDatabase leaderboard = new(runtime.Services.Leaderboards, new NameResolver(), new Catalog(), new Publisher(), new LeaderboardRuntimeOptions(schedulePath, 1));
                        leaderboardInitialized = leaderboard.Initialize();
                        syntheticGameSystemsInvoked = leaderboardInitialized;
                        return syntheticGameSystemsInvoked;
                    },
                    (_, _, _) => { },
                    () => consoleRead.Task,
                    configManager: configuration);
                ServerApp app = new(dependencies, new ServerManager());

                Task run = app.RunAsync();
                await WaitUntilAsync(() => syntheticGameSystemsInvoked);
                app.Shutdown();
                await run.WaitAsync(TimeSpan.FromSeconds(10));

                Assert.True(leaderboardInitialized);
                Assert.True(syntheticGameSystemsInvoked);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (condition() == false)
            {
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException();
                await Task.Delay(10);
            }
        }

        [System.Runtime.Versioning.SupportedOSPlatform("linux")]
        private static void SetOwnerOnlyPermissions(string path)
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        private sealed class ActivationDatabase : IAsyncDisposable
        {
            private readonly string _adminConnectionString;
            private readonly string _databaseName;

            private ActivationDatabase(string adminConnectionString, string databaseName, string connectionString)
            {
                _adminConnectionString = adminConnectionString;
                _databaseName = databaseName;
                ConnectionString = connectionString;
            }

            public string ConnectionString { get; }

            public static async Task<ActivationDatabase> CreateAsync()
            {
                string adminConnectionString = Environment.GetEnvironmentVariable("MHSERVEREMU_POSTGRESQL_TEST_ADMIN_CONNECTION_STRING");
                string databaseName = $"mhserveremu_test_{Guid.NewGuid():N}";
                await using (NpgsqlConnection admin = new(adminConnectionString))
                {
                    await admin.OpenAsync();
                    await using NpgsqlCommand create = new($"CREATE DATABASE \"{databaseName}\"", admin);
                    await create.ExecuteNonQueryAsync();
                }

                NpgsqlConnectionStringBuilder builder = new(adminConnectionString) { Database = databaseName };
                return new ActivationDatabase(adminConnectionString, databaseName, builder.ConnectionString);
            }

            public async ValueTask DisposeAsync()
            {
                await using NpgsqlConnection admin = new(_adminConnectionString);
                await admin.OpenAsync();
                await using NpgsqlCommand terminate = new("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @databaseName AND pid <> pg_backend_pid()", admin);
                terminate.Parameters.AddWithValue("databaseName", _databaseName);
                await terminate.ExecuteNonQueryAsync();
                await using NpgsqlCommand drop = new($"DROP DATABASE IF EXISTS \"{_databaseName}\"", admin);
                await drop.ExecuteNonQueryAsync();
            }
        }

        private sealed class NameResolver : ILeaderboardPlayerNameResolver
        {
            public string GetPlayerName(ulong participantId) => participantId.ToString();
        }

        private sealed class Catalog : ILeaderboardPrototypeCatalog
        {
            public IReadOnlyList<LeaderboardPrototypeDefinition> GetPublicPrototypes() =>
                [new(101, "SyntheticLeaderboard", true, Array.Empty<long>())];

            public bool TryGetPrototype(long leaderboardId, out LeaderboardPrototype prototype)
            {
                prototype = null;
                return false;
            }
        }

        private sealed class Publisher : ILeaderboardPublisher
        {
            public void Publish(ServiceMessage.LeaderboardStateChange change) { }
            public void Publish(IReadOnlyList<ServiceMessage.LeaderboardStateChange> changes) { }
            public void Publish(ServiceMessage.LeaderboardRewardRequestResponse response) { }
        }
    }
}

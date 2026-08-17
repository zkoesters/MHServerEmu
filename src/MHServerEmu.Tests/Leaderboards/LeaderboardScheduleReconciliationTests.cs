using MHServerEmu.DatabaseAccess.Models.Leaderboards;
using MHServerEmu.Leaderboards;
using Gazillion;
using System.Text.Json;

namespace MHServerEmu.Tests.Leaderboards
{
    public class LeaderboardScheduleReconciliationTests
    {
        [Fact]
        public void LoadOrCreate_MissingSchedule_GeneratesCanonicalPublicDefinitions()
        {
            string path = Path.Combine(Path.GetTempPath(), $"leaderboard-schedule-{Guid.NewGuid():N}.json");
            try
            {
                ILeaderboardPrototypeCatalog catalog = new TestCatalog(
                    new LeaderboardPrototypeDefinition(101, "LeaderboardOne", IsEnabledByDefault: true, Array.Empty<long>()));
                LeaderboardScheduleLoader loader = new(catalog, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

                Assert.True(loader.TryLoadOrCreate(path, normalArchiveLimit: 2, out LeaderboardReconciliation reconciliation));

                Assert.True(File.Exists(path));
                LeaderboardDefinitionSpec definition = Assert.Single(reconciliation.DesiredDefinitions);
                Assert.Equal(101, definition.LeaderboardId);
                Assert.Equal("LeaderboardOne", definition.PrototypeName);
                Assert.Equal(LeaderboardState.eLBS_Created, Assert.Single(reconciliation.InitialInstances).State);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void LoadOrCreate_DuplicatePrototypeNames_FailsBeforeProducingReconciliation()
        {
            string path = Path.Combine(Path.GetTempPath(), $"leaderboard-schedule-{Guid.NewGuid():N}.json");
            try
            {
                ILeaderboardPrototypeCatalog catalog = new TestCatalog(
                    new LeaderboardPrototypeDefinition(101, "Duplicate", IsEnabledByDefault: true, Array.Empty<long>()),
                    new LeaderboardPrototypeDefinition(102, "Duplicate", IsEnabledByDefault: true, Array.Empty<long>()));
                LeaderboardScheduleLoader loader = new(catalog, DateTime.UtcNow);

                Assert.False(loader.TryLoadOrCreate(path, normalArchiveLimit: 0, out _));
                Assert.False(File.Exists(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void LoadOrCreate_LegacyPrototypeName_MigratesScheduleToCanonicalLeaderboardId()
        {
            string path = Path.Combine(Path.GetTempPath(), $"leaderboard-schedule-{Guid.NewGuid():N}.json");
            try
            {
                File.WriteAllText(path, """
                    [{ "PrototypeName": "LeaderboardOne", "IsEnabled": true, "StartTime": "2026-01-01T00:00:00Z", "MaxResetCount": 0 }]
                    """);
                LeaderboardScheduleLoader loader = new(new TestCatalog(
                    new LeaderboardPrototypeDefinition(101, "LeaderboardOne", true, Array.Empty<long>())), DateTime.UtcNow);

                Assert.True(loader.TryLoadOrCreate(path, normalArchiveLimit: 1, out LeaderboardReconciliation reconciliation));
                Assert.Equal(101, Assert.Single(reconciliation.DesiredDefinitions).LeaderboardId);
                using JsonDocument schedule = JsonDocument.Parse(File.ReadAllText(path));
                JsonElement entry = schedule.RootElement[0];
                Assert.Equal(101, entry.GetProperty("LeaderboardId").GetInt64());
                Assert.False(entry.TryGetProperty("PrototypeName", out _));
            }
            finally
            {
                File.Delete(path);
            }
        }

        private sealed class TestCatalog(params LeaderboardPrototypeDefinition[] definitions) : ILeaderboardPrototypeCatalog
        {
            public IReadOnlyList<LeaderboardPrototypeDefinition> GetPublicPrototypes() => definitions;
            public bool TryGetPrototype(long leaderboardId, out MHServerEmu.Games.GameData.Prototypes.LeaderboardPrototype prototype) { prototype = null; return false; }
        }
    }
}

using System.Text.Json;
using Gazillion;
using MHServerEmu.Core.System.Time;
using MHServerEmu.DatabaseAccess.Models.Leaderboards;

namespace MHServerEmu.Leaderboards
{
    public sealed class LeaderboardScheduleLoader
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private readonly ILeaderboardPrototypeCatalog _catalog;
        private readonly DateTime _currentTime;

        public LeaderboardScheduleLoader(ILeaderboardPrototypeCatalog catalog, DateTime currentTime)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _currentTime = currentTime.Kind == DateTimeKind.Utc ? currentTime : currentTime.ToUniversalTime();
        }

        public bool TryLoadOrCreate(string path, int normalArchiveLimit, out LeaderboardReconciliation reconciliation)
        {
            reconciliation = null;
            if (string.IsNullOrWhiteSpace(path) || normalArchiveLimit < 0)
                return false;

            try
            {
                IReadOnlyList<LeaderboardPrototypeDefinition> prototypes = _catalog.GetPublicPrototypes();
                if (ValidatePrototypes(prototypes) == false)
                    return false;

                ScheduleEntry[] entries;
                if (File.Exists(path))
                {
                    entries = JsonSerializer.Deserialize<ScheduleEntry[]>(File.ReadAllText(path), JsonOptions);
                    if (entries == null)
                        return false;
                }
                else
                {
                    entries = prototypes
                        .OrderBy(prototype => unchecked((ulong)prototype.LeaderboardId))
                        .Select(prototype => new ScheduleEntry
                        {
                            PrototypeName = prototype.PrototypeName,
                            IsEnabled = prototype.IsEnabledByDefault,
                            StartTime = _currentTime,
                            MaxResetCount = 0,
                        })
                        .ToArray();

                    string directory = Path.GetDirectoryName(path);
                    if (string.IsNullOrEmpty(directory) == false)
                        Directory.CreateDirectory(directory);
                    File.WriteAllText(path, JsonSerializer.Serialize(entries, JsonOptions));
                }

                Dictionary<string, LeaderboardPrototypeDefinition> definitionsByName = prototypes.ToDictionary(prototype => prototype.PrototypeName, StringComparer.Ordinal);
                if (entries.Any(entry => IsValidScheduleEntry(entry) == false)
                    || entries.GroupBy(entry => entry.PrototypeName, StringComparer.Ordinal).Any(group => group.Skip(1).Any())
                    || entries.Any(entry => definitionsByName.ContainsKey(entry.PrototypeName) == false))
                    return false;

                Dictionary<long, long> initialInstanceIds = new();
                List<LeaderboardDefinitionSpec> definitions = new();
                List<LeaderboardInstanceSpec> initialInstances = new();
                foreach (ScheduleEntry entry in entries)
                {
                    LeaderboardPrototypeDefinition prototype = definitionsByName[entry.PrototypeName];
                    if (LeaderboardInstanceIdGenerator.TryGetNext(prototype.LeaderboardId, Array.Empty<long>(), out long instanceId) == false)
                        return false;

                    initialInstanceIds.Add(prototype.LeaderboardId, instanceId);
                    definitions.Add(new LeaderboardDefinitionSpec(prototype.LeaderboardId, prototype.PrototypeName, entry.IsEnabled,
                        Clock.DateTimeToTimestamp(entry.StartTime), entry.MaxResetCount));
                    initialInstances.Add(new LeaderboardInstanceSpec(instanceId, prototype.LeaderboardId,
                        entry.IsEnabled ? LeaderboardState.eLBS_Created : LeaderboardState.eLBS_Rewarded, 0, entry.IsEnabled));
                }

                List<LeaderboardMetaMapping> mappings = new();
                foreach (LeaderboardPrototypeDefinition prototype in prototypes.Where(prototype => initialInstanceIds.ContainsKey(prototype.LeaderboardId)))
                {
                    foreach (long subLeaderboardId in prototype.SubLeaderboardIds)
                    {
                        if (initialInstanceIds.TryGetValue(subLeaderboardId, out long subInstanceId) == false)
                            return false;
                        mappings.Add(new LeaderboardMetaMapping(prototype.LeaderboardId, initialInstanceIds[prototype.LeaderboardId], subLeaderboardId, subInstanceId));
                    }
                }

                reconciliation = new LeaderboardReconciliation(definitions, initialInstances, mappings,
                    Clock.DateTimeToTimestamp(_currentTime), normalArchiveLimit);
                return true;
            }
            catch (Exception)
            {
                reconciliation = null;
                return false;
            }
        }

        private static bool ValidatePrototypes(IReadOnlyList<LeaderboardPrototypeDefinition> prototypes)
        {
            return prototypes != null
                && prototypes.All(prototype => prototype != null
                    && string.IsNullOrWhiteSpace(prototype.PrototypeName) == false
                    && prototype.SubLeaderboardIds != null)
                && prototypes.GroupBy(prototype => prototype.LeaderboardId).All(group => group.Skip(1).Any() == false)
                && prototypes.GroupBy(prototype => prototype.PrototypeName, StringComparer.Ordinal).All(group => group.Skip(1).Any() == false)
                && prototypes.All(prototype => prototype.SubLeaderboardIds.Distinct().Count() == prototype.SubLeaderboardIds.Count
                    && prototype.SubLeaderboardIds.All(id => prototypes.Any(candidate => candidate.LeaderboardId == id)));
        }

        private static bool IsValidScheduleEntry(ScheduleEntry entry)
        {
            return entry != null
                && string.IsNullOrWhiteSpace(entry.PrototypeName) == false
                && entry.StartTime.Kind == DateTimeKind.Utc
                && entry.StartTime != DateTime.MinValue
                && entry.MaxResetCount >= 0;
        }

        private sealed class ScheduleEntry
        {
            public string PrototypeName { get; set; }
            public bool IsEnabled { get; set; }
            public DateTime StartTime { get; set; }
            public int MaxResetCount { get; set; }
        }
    }
}

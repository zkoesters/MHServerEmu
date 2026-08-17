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

                Dictionary<long, LeaderboardPrototypeDefinition> definitionsById = prototypes.ToDictionary(prototype => prototype.LeaderboardId);
                Dictionary<string, LeaderboardPrototypeDefinition> definitionsByName = prototypes.ToDictionary(prototype => prototype.PrototypeName, StringComparer.Ordinal);
                ScheduleEntry[] entries;
                if (File.Exists(path))
                {
                    entries = JsonSerializer.Deserialize<ScheduleEntry[]>(File.ReadAllText(path), JsonOptions);
                    if (entries == null)
                        return false;

                    if (entries.Any(entry => entry.LeaderboardId == 0))
                    {
                        LegacyScheduleEntry[] legacyEntries = JsonSerializer.Deserialize<LegacyScheduleEntry[]>(File.ReadAllText(path), JsonOptions);
                        if (legacyEntries == null || legacyEntries.Any(entry => entry.LeaderboardId != 0 || string.IsNullOrWhiteSpace(entry.PrototypeName)
                            || definitionsByName.TryGetValue(entry.PrototypeName, out _) == false))
                            return false;

                        entries = legacyEntries.Select(entry => new ScheduleEntry
                        {
                            LeaderboardId = definitionsByName[entry.PrototypeName].LeaderboardId,
                            IsEnabled = entry.IsEnabled,
                            StartTime = entry.StartTime,
                            MaxResetCount = entry.MaxResetCount,
                        }).ToArray();
                        File.WriteAllText(path, JsonSerializer.Serialize(entries, JsonOptions));
                    }
                }
                else
                {
                    entries = prototypes
                        .OrderBy(prototype => unchecked((ulong)prototype.LeaderboardId))
                        .Select(prototype => new ScheduleEntry
                        {
                            LeaderboardId = prototype.LeaderboardId,
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

                if (entries.Any(entry => IsValidScheduleEntry(entry) == false)
                    || entries.GroupBy(entry => entry.LeaderboardId).Any(group => group.Skip(1).Any())
                    || entries.Any(entry => definitionsById.ContainsKey(entry.LeaderboardId) == false))
                    return false;

                Dictionary<long, long> initialInstanceIds = new();
                List<LeaderboardDefinitionSpec> definitions = new();
                List<LeaderboardInstanceSpec> initialInstances = new();
                foreach (ScheduleEntry entry in entries)
                {
                    LeaderboardPrototypeDefinition prototype = definitionsById[entry.LeaderboardId];
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
                && entry.StartTime.Kind == DateTimeKind.Utc
                && entry.StartTime != DateTime.MinValue
                && entry.MaxResetCount >= 0;
        }

        private sealed class ScheduleEntry
        {
            public long LeaderboardId { get; set; }
            public bool IsEnabled { get; set; }
            public DateTime StartTime { get; set; }
            public int MaxResetCount { get; set; }
        }

        private sealed class LegacyScheduleEntry
        {
            public long LeaderboardId { get; set; }
            public string PrototypeName { get; set; }
            public bool IsEnabled { get; set; }
            public DateTime StartTime { get; set; }
            public int MaxResetCount { get; set; }
        }
    }
}

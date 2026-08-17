using System.Text;
using Gazillion;
using MHServerEmu.Commands.Attributes;
using MHServerEmu.Core.Network;
using MHServerEmu.Core.System.Time;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.Leaderboards.Administration;

namespace MHServerEmu.Commands.Implementations
{
    [CommandGroup("leaderboards")]
    [CommandGroupDescription("Commands related to the leaderboard system")]
    [CommandGroupUserLevel(AccountUserLevel.Admin)]
    public class LeaderboardsCommands : CommandGroup
    {
        private readonly ILeaderboardAdministration _administration;

        public LeaderboardsCommands(ILeaderboardAdministration administration)
        {
            _administration = administration ?? throw new ArgumentNullException(nameof(administration));
        }

        [Command("reloadschedule")]
        [CommandDescription("Reloads leaderboard schedule from JSON.")]
        [CommandUsage("leaderboards reloadschedule")]
        public string ReloadSchedule(string[] @params, NetClient client)
        {
            if (_administration.ReloadSchedule() != LeaderboardAdminResult.Success)
                return "Leaderboard database is not available.";
            return "Leaderboard schedule reloaded.";
        }

        [Command("instance")]
        [CommandDescription("Shows details for the specified leaderboard instance.")]
        [CommandUsage("leaderboards instance [instanceId]")]
        public string Instance(string[] @params, NetClient client)
        {
            if (@params.Length == 0) return "Invalid arguments. Type 'help leaderboards instance' to get help.";
            if (long.TryParse(@params[0], out long instanceId) == false)
                return $"Failed to parse InstanceId {@params[0]}";

            if (_administration.TryGetInstance(instanceId, out LeaderboardInstanceSummary instance) != LeaderboardAdminResult.Success)
                return $"InstanceId {instanceId} not found";
            return instance.Details;
        }

        [Command("leaderboard")]
        [CommandDescription("Shows details for the specified leaderboard.")]
        [CommandUsage("leaderboards leaderboard [prototypeGuid]")]
        public string Leaderboard(string[] @params, NetClient client)
        {
            if (@params.Length == 0) return "Invalid arguments. Type 'help leaderboards leaderboard' to get help.";
            if (long.TryParse(@params[0], out long leaderboardId) == false)
                return $"Failed to parse LeaderboardId {@params[0]}";

            if (_administration.TryGetLeaderboard(leaderboardId, out LeaderboardSummary leaderboard) != LeaderboardAdminResult.Success)
                return $"LeaderboardId {leaderboardId} not found";
            return leaderboard.Details;
        }

        [Command("now")]
        [CommandDescription("Shows all active instances.")]
        [CommandUsage("leaderboards now")]
        public string Now(string[] @params, NetClient client)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Current Time: [{Clock.UtcNowTimestamp}] {Clock.UtcNowPrecise}");

            if (_administration.GetLeaderboards(out IReadOnlyList<LeaderboardSummary> leaderboards) != LeaderboardAdminResult.Success)
                return "Leaderboard database is not available.";

            foreach (LeaderboardSummary leaderboard in leaderboards)
                if (leaderboard.ActiveInstance?.State == LeaderboardState.eLBS_Active)
                    sb.AppendLine(
                        $"{leaderboard.Name}" +
                        $"[{leaderboard.ActiveInstance.Id}] = " +
                        $"{leaderboard.ActiveInstance.ActivationTime} - " +
                        $"{leaderboard.ActiveInstance.ExpirationTime}");

            return sb.ToString();
        }

        [Command("enabled")]
        [CommandDescription("Shows enabled leaderboards.")]
        [CommandUsage("leaderboards enabled")]
        public string Enabled(string[] @params, NetClient client)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Current Time: [{Clock.UtcNowTimestamp}] {Clock.UtcNowPrecise}");

            if (_administration.GetLeaderboards(out IReadOnlyList<LeaderboardSummary> leaderboards) != LeaderboardAdminResult.Success)
                return "Leaderboard database is not available.";

            foreach (LeaderboardSummary leaderboard in leaderboards)
                if (leaderboard.IsEnabled && leaderboard.ActiveInstance != null)
                    sb.AppendLine(
                        $"{leaderboard.Name}" +
                        $"[{leaderboard.ActiveInstance.Id}][{leaderboard.ActiveInstance.State}] = " +
                        $"{leaderboard.ActiveInstance.ActivationTime} - " +
                        $"{leaderboard.ActiveInstance.ExpirationTime}");

            return sb.ToString();
        }

        [Command("all")]
        [CommandDescription("Shows all leaderboards.")]
        [CommandUsage("leaderboards all")]
        public string All(string[] @params, NetClient client)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Current Time: [{Clock.UtcNowTimestamp}] {Clock.UtcNowPrecise}");

            if (_administration.GetLeaderboards(out IReadOnlyList<LeaderboardSummary> leaderboards) != LeaderboardAdminResult.Success)
                return "Leaderboard database is not available.";

            foreach (LeaderboardSummary leaderboard in leaderboards)
                sb.AppendLine(
                    $"[{(leaderboard.IsEnabled ? "+" : "-")}]" +
                    $"[{leaderboard.Id}] " +
                    $"{leaderboard.Name} = " +
                    $"{leaderboard.StartTime}");

            return sb.ToString();
        }
    }
}

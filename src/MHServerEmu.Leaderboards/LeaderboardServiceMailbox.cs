using MHServerEmu.Core.Network;
using MHServerEmu.Leaderboards.Administration;
using Gazillion;

namespace MHServerEmu.Leaderboards
{
    internal sealed class LeaderboardServiceMailbox : ServiceMailbox, ILeaderboardAdministration
    {
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

        private readonly LeaderboardService _service;

        public LeaderboardServiceMailbox(LeaderboardService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
        }

        public LeaderboardAdminResult ReloadSchedule()
        {
            TaskCompletionSource<LeaderboardAdminResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (TryPost(new ReloadScheduleRequest(completion)) == false)
                return LeaderboardAdminResult.Unavailable;
            return Wait(completion.Task, LeaderboardAdminResult.Unavailable);
        }

        public void PostLeaderboardRequest(IFrontendClient client, NetMessageLeaderboardRequest request)
        {
            PostMessage(new LeaderboardRequest(client, request));
        }

        public LeaderboardAdminResult TryGetInstance(long instanceId, out LeaderboardInstanceSummary summary)
        {
            TaskCompletionSource<LeaderboardInstanceResponse> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (TryPost(new GetInstanceRequest(instanceId, completion)) == false)
            {
                summary = null;
                return LeaderboardAdminResult.Unavailable;
            }

            LeaderboardInstanceResponse response = Wait(completion.Task, new(LeaderboardAdminResult.Unavailable, null));
            summary = response.Summary;
            return response.Result;
        }

        public LeaderboardAdminResult TryGetLeaderboard(long leaderboardId, out LeaderboardSummary summary)
        {
            TaskCompletionSource<LeaderboardResponse> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (TryPost(new GetLeaderboardRequest(leaderboardId, completion)) == false)
            {
                summary = null;
                return LeaderboardAdminResult.Unavailable;
            }

            LeaderboardResponse response = Wait(completion.Task, new(LeaderboardAdminResult.Unavailable, null));
            summary = response.Summary;
            return response.Result;
        }

        public LeaderboardAdminResult GetLeaderboards(out IReadOnlyList<LeaderboardSummary> summaries)
        {
            TaskCompletionSource<LeaderboardsResponse> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (TryPost(new GetLeaderboardsRequest(completion)) == false)
            {
                summaries = Array.Empty<LeaderboardSummary>();
                return LeaderboardAdminResult.Unavailable;
            }

            LeaderboardsResponse response = Wait(completion.Task, new(LeaderboardAdminResult.Unavailable, Array.Empty<LeaderboardSummary>()));
            summaries = response.Summaries;
            return response.Result;
        }

        private bool TryPost<T>(in T request) where T : struct, IGameServiceMessage
        {
            if (_service.CanAdminister == false)
                return false;

            PostMessage(request);
            return true;
        }

        private static T Wait<T>(Task<T> task, T unavailable)
        {
            try
            {
                return task.Wait(RequestTimeout) ? task.GetAwaiter().GetResult() : unavailable;
            }
            catch
            {
                return unavailable;
            }
        }

        protected override void HandleServiceMessage(IGameServiceMessage message)
        {
            switch (message)
            {
                case ReloadScheduleRequest request:
                    request.Completion.TrySetResult(_service.ReloadSchedule());
                    break;
                case GetInstanceRequest request:
                    request.Completion.TrySetResult(_service.GetInstance(request.InstanceId));
                    break;
                case GetLeaderboardRequest request:
                    request.Completion.TrySetResult(_service.GetLeaderboard(request.LeaderboardId));
                    break;
                case GetLeaderboardsRequest request:
                    request.Completion.TrySetResult(_service.GetLeaderboards());
                    break;
                case LeaderboardRequest request:
                    _service.SendLeaderboardReport(request.Client, request.Request);
                    break;
            }
        }

        private readonly struct ReloadScheduleRequest(TaskCompletionSource<LeaderboardAdminResult> completion) : IGameServiceMessage
        {
            public readonly TaskCompletionSource<LeaderboardAdminResult> Completion = completion;
        }

        private readonly struct GetInstanceRequest(long instanceId, TaskCompletionSource<LeaderboardInstanceResponse> completion) : IGameServiceMessage
        {
            public readonly long InstanceId = instanceId;
            public readonly TaskCompletionSource<LeaderboardInstanceResponse> Completion = completion;
        }

        private readonly struct GetLeaderboardRequest(long leaderboardId, TaskCompletionSource<LeaderboardResponse> completion) : IGameServiceMessage
        {
            public readonly long LeaderboardId = leaderboardId;
            public readonly TaskCompletionSource<LeaderboardResponse> Completion = completion;
        }

        private readonly struct GetLeaderboardsRequest(TaskCompletionSource<LeaderboardsResponse> completion) : IGameServiceMessage
        {
            public readonly TaskCompletionSource<LeaderboardsResponse> Completion = completion;
        }

        private readonly struct LeaderboardRequest(IFrontendClient client, NetMessageLeaderboardRequest request) : IGameServiceMessage
        {
            public readonly IFrontendClient Client = client;
            public readonly NetMessageLeaderboardRequest Request = request;
        }

    }

    internal readonly record struct LeaderboardInstanceResponse(LeaderboardAdminResult Result, LeaderboardInstanceSummary Summary);
    internal readonly record struct LeaderboardResponse(LeaderboardAdminResult Result, LeaderboardSummary Summary);
    internal readonly record struct LeaderboardsResponse(LeaderboardAdminResult Result, IReadOnlyList<LeaderboardSummary> Summaries);
}
